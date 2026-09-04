using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EntJoy.JobSystem.Managed
{
    /// <summary>
    /// 托管无锁 Job 调度器。独立于原生 NativeJobScheduler，可脱离 NativeDll 单独使用。
    ///
    /// 关键架构：
    ///   - Chase-Lev 工作窃取（标准 crossbeam 模型）：per-worker 持久 deque + 全局 MPMC Injector。
    ///   - worker parking：空闲 worker 有界自旋后阻塞在 SemaphoreSlim，任务发布按需唤醒。
    ///   - 混合执行并行 for：预切分为 ManagedTileTask 推入 Injector，worker 从 Injector
    ///     拉取推入自己 deque（owner-only PushBottom），空闲时从其他 worker steal。
    ///   - 同步内联覆盖小任务（零调度开销）。
    ///   - completion 槽位池（预分配数组）+ 单槽 per-类型 job 盒池 → 热路径零分配。
    ///   - 依赖链中间 completion 完成后自动归还（防泄漏）；槽位带 generation 代际，防旧 handle 复用误操作（防 ABA）。
    ///   - 依赖回调注册与完成/回收同步，避免并发丢失续体。
    ///
    /// 【约束】依赖图必须是无环 DAG：循环依赖（A→B→A）会导致依赖链
    /// 回调永远无法触发、Complete() 永久阻塞。运行时不做环检测，调用方负责保证。
    /// </summary>
    public static class ManagedJobScheduler
    {
        private static Thread[]? _workers;
        private static int _workerCount;
        private static volatile bool _shutdown;
        private static bool _isInitialized;
        private static readonly object _stateLock = new object();
        private static int _epoch;
        internal static int Epoch => Volatile.Read(ref _epoch);

        private const int QueueCapacity = 1 << 16;
        // 主线程 id：Initialize 时记录，Shutdown 校验——worker 线程调用 Shutdown 会
        // Join 自身（w.Join()）死锁，非主线程调用直接拒绝（警告并返回）。
        private static int _mainThreadId;

        // ───── Chase-Lev 路径 ─────
        private static ManagedMPMCQueue<ManagedTileTask>? _injector;   // 全局 Injector（跨线程提交入口）
        private static ManagedWorkStealingDeque[]? _deques;            // per-worker 持久 deque
        private static ManagedTileTaskPool? _tileTaskPool;             // 全局任务池
        private static SemaphoreSlim? _wakeSignal;                     // 唤醒信号量（计数语义，对齐 Native epoch）
        private static readonly TimeSpan ParkTimeout = TimeSpan.FromMilliseconds(1); // park 超时兜底

        /// <summary>
        /// Complete 的周期协助间隔（对齐 Native Complete 的 wait_for(1ms) + 依赖链回访）：
        /// 等待期间以此间隔周期苏醒并协助认领 Injector 任务推进链；completion 完成时 _done 立即唤醒，
        /// 该间隔只是"空闲复查"频率。无墙钟超时 —— 合法长任务（worker 活跃/队列有活）绝不误报死锁。
        /// </summary>
        private static readonly TimeSpan CompleteAssistInterval = TimeSpan.FromMilliseconds(8);

        // ──────────────────── 生命周期 ────────────────────

        public static void Initialize(int workerCount = 0)
        {
            lock (_stateLock)
            {
                if (_isInitialized) return;
                Interlocked.Increment(ref _epoch);
                _mainThreadId = Environment.CurrentManagedThreadId;
                _workerCount = workerCount <= 0 ? Math.Max(1, Environment.ProcessorCount - 1) : workerCount;

                _shutdown = false;
                _isInitialized = true;

                // 预分配 completion 槽位池
                _completionSlots = new ManagedCompletion[CompletionPoolCap];
                for (int i = 0; i < CompletionPoolCap; i++)
                {
                    _completionSlots[i] = new ManagedCompletion { SlotIndex = i };
                    _completionSlots[i].NextFree = i + 1 < CompletionPoolCap ? i + 1 : -1;
                }
                _completionFreeHead = 0;

                // Chase-Lev 路径
                _injector = new ManagedMPMCQueue<ManagedTileTask>(QueueCapacity);
                _deques = new ManagedWorkStealingDeque[_workerCount];
                for (int i = 0; i < _workerCount; i++)
                    _deques[i] = new ManagedWorkStealingDeque();
                _tileTaskPool = new ManagedTileTaskPool();
                _wakeSignal = new SemaphoreSlim(0); // 无上限（对齐 Native wakeStamp + notify_all）

                _workers = new Thread[_workerCount];
                for (int i = 0; i < _workerCount; i++)
                {
                    int wi = i;
                    var t = new Thread(() => WorkerLoopChaseLev(wi)) { IsBackground = true, Name = $"ManagedJobWorker-{i}" };
                    _workers[i] = t;
                    t.Start();
                }
            }
        }

        public static void Shutdown()
        {
            // 线程防护：worker 线程调用 Shutdown 会 Join 自身（下方 w.Join()）永不返回死锁。
            // 非主线程调用拒绝（警告并返回），对齐 Native 后端 C++ 侧行为。
            if (_mainThreadId != 0 && Environment.CurrentManagedThreadId != _mainThreadId)
            {
                Console.Error.WriteLine(
                    "[ManagedJobScheduler] Shutdown() called from non-main thread — rejected (would self-join deadlock).");
                return;
            }
            lock (_stateLock)
            {
                if (!_isInitialized) return;
                Interlocked.Increment(ref _epoch);
                _shutdown = true;
                _isInitialized = false;

                // 释放所有 SemaphoreSlim 信号让 worker 退出
                _wakeSignal?.Release(_workerCount);
            }
            if (_workers == null) return;
            foreach (var w in _workers)
                if (w != null && w.IsAlive) w.Join();
            _workers = null;
        }

        // ──────────────────── completion 对象池（预分配数组，无 ConcurrentStack Node 分配） ────────────────────

        private const int CompletionPoolCap = 4096;
        private static ManagedCompletion[] _completionSlots = null!;
        private static long _completionFreeHead; // 低 32 位 = 栈顶索引（-1 为空），高 32 位 = ABA 计数器

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ManagedCompletion CompletionAt(int slotIndex) => _completionSlots[slotIndex];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ManagedCompletion RentCompletion()
        {
            while (true)
            {
                long head = Volatile.Read(ref _completionFreeHead);
                int index = (int)head;
                if (index < 0) return new ManagedCompletion(); // 池耗尽兜底
                int next = _completionSlots[index].NextFree;
                long newHead = ((long)(unchecked((int)(head >> 32)) + 1) << 32) | (uint)next;
                if (Interlocked.CompareExchange(ref _completionFreeHead, newHead, head) == head)
                {
                    var c = _completionSlots[index];
                    lock (c.SyncRoot)
                    {
                        // 旧代际可能有回调刚完成注册；在重置前完成派发，避免回调被静默丢弃。
                        c.DispatchComplete();
                        c.Reset();
                        Interlocked.Increment(ref c.Generation);
                        Volatile.Write(ref c._returned, 0);
                    }
                    return c;
                }
            }
        }

        /// <summary>调用方完成归还（幂等：重复归还/与自动归还并发安全）。</summary>
        internal static void ReturnCompletion(ManagedCompletion c, int expectedGeneration)
        {
            if (c == null) return;
            lock (c.SyncRoot)
            {
                if (c.Generation != expectedGeneration || c._returned != 0) return;
                c._returned = 1;
                ReturnCompletionCore(c);
            }
        }

        /// <summary>依赖链中间 completion 完成后的自动归还（幂等防 double）。</summary>
        internal static void AutoReturnCompletion(ManagedCompletion c, int expectedGeneration)
        {
            if (c == null) return;
            lock (c.SyncRoot)
            {
                if (c.Generation != expectedGeneration || c._returned != 0) return;
                c._returned = 1;
                ReturnCompletionCore(c);
            }
        }

        private static void ReturnCompletionCore(ManagedCompletion c)
        {
            // 归还时**不** Reset：保留完成态（Remaining=0、_done=Set），完整 Reset 推迟到 RentCompletion 分配时执行。
            // 这使"已完成但要被并发注册依赖"的 completion 始终被读为已完成的正确状态，
            // 避免 Signal 的自动归还把 Remaining 改回 1 而让 ChainAfter/OnCompleted 误判未完成、把回调永久挂死在已回收槽位。
            // 归还即代际 +1：任何仍持有本 completion 的旧 handle 立即可判"过期"。
            // 这是防止"已自动归还/已重置的槽位被误等待"的关键——否则还原后的 Remaining=1 会让
            // 释放方之外的用户 Complete() 误以为未完成而永久等待（曾导致零长度并行 for 挂死）。
            Interlocked.Increment(ref c.Generation);
            int idx = c.SlotIndex;
            if (idx < 0) return; // 兜底分配的，不在预分配池内
            while (true)
            {
                long head = Volatile.Read(ref _completionFreeHead);
                int headIndex = (int)head;
                _completionSlots[idx].NextFree = headIndex;
                long newHead = ((long)(unchecked((int)(head >> 32)) + 1) << 32) | (uint)idx;
                if (Interlocked.CompareExchange(ref _completionFreeHead, newHead, head) == head)
                    return;
            }
        }

        // ──────────────────── 调度 API ────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException(
                    "ManagedJobScheduler 尚未初始化：请先调用 ManagedJobScheduler.Initialize().");
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static ManagedJobHandle Schedule<T>(ref T job) where T : struct, IJob
        {
            EnsureInitialized();
            return ScheduleChaseLevSingle<T>(ref job);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static ManagedJobHandle Schedule<T>(ref T job, ManagedJobHandle dependsOn) where T : struct, IJob
        {
            EnsureInitialized();
            return ScheduleChaseLevSingle<T>(ref job, dependsOn);
        }

        /// <summary>
        /// 调度 IJobParallelFor：Chase-Lev 工作窃取（Schedule 一律异步提交）。
        /// 依赖通过 completion 计数保证顺序。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static ManagedJobHandle Schedule<T>(ref T job, int arrayLength, int innerBatchCount, ManagedJobHandle dependsOn = default)
            where T : struct, IJobParallelFor
        {
            EnsureInitialized();
            if (arrayLength <= 0)
            {
                // 零长度并行 for：无任何分片会执行，立即完成并自动归还。必须在 Signal（触发自动归还，代际+1）
                // **之前**构造 handle，以抓到归还前的代际快照 —— 否则旧 handle 的 IsExpired 判定不出"已归还"
                // 已被复用的槽位，用户 Complete() 会误等一个 Remaining 已被重置=1 的槽位而永久挂死。
                var zero = ManagedJobScheduler.RentCompletion();
                Interlocked.Exchange(ref zero.Remaining, 1);
                var h = new ManagedJobHandle(zero);
                Volatile.Write(ref zero._autoReturn, 1);
                zero.Signal();
                return h;
            }

            // Chase-Lev 路径：预切分为 ManagedTileTask 推入 Injector
            // （移除 ≤SyncInlineThreshold 的调用线程同步内联——小并行 for 同样可能
            //  承载大工作量阻塞调用线程，Schedule 一律异步，对齐 IJob 族）
            return ScheduleChaseLevParallelFor<T>(ref job, arrayLength, innerBatchCount, dependsOn);
        }

        /// <summary>
        /// 统一挂接依赖启动。带**代际守卫**杜绝跨链 ABA：dep 槽位已被归还/重租（Generation 已前进，
        /// 与调用方看到的 handle 代际不一致）时，原依赖已完成或已过期 → 直接把 dependent 视为就绪立即启动，
        /// **绝不把续体回调注册到已被复用的 completion 对象上**（否则该回调可能被后续重租的 Reset 清空而永远丢失，
        /// 正是并发依赖链丢回调死锁的根因）。
        /// 代际仍匹配时才走"已完成直派 / 未完成挂回调"两条正常路径，且把依赖标记为完成后自动归还
        /// （防中间 handle 泄漏）并把依赖异常传播到新 job 的 completion。
        /// </summary>
        private static void ChainAfter(ManagedCompletion dep, int depGen, ManagedCompletion dependent, Action enqueue)
        {
            if (dep == null || Volatile.Read(ref dep.Generation) != depGen)
            {
                // 依赖槽位已换代：原依赖完成/过期，立即启动 dependent（不等待、不注册、不触碰新槽位属主）。
                enqueue();
                return;
            }
            if (!dep.TryMarkAutoReturn(depGen))
            {
                enqueue();
                return;
            }
            void Propagate()
            {
                var ex = dep.ReadException();
                if (ex != null && dependent != null) dependent.RecordException(ex);
                enqueue();
            }
            if (dep.IsCompleted)
            {
                // 依赖在调度前就已就绪：立即传播 + 启动，并当场归还（Signal 已跑过，不会自动归还，否则泄漏槽位）。
                Propagate();
                ManagedJobScheduler.AutoReturnCompletion(dep, depGen);
            }
            else
            {
                // 依赖尚未完成：挂回调；完成后 Signal 内部会因 _autoReturn==1 自动归还。
                dep.OnCompleted(Propagate, depGen);
            }
        }

        // ──────────────────── 主线程协作完成 ────────────────────

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void CompleteSchedule(ManagedCompletion completion)
        {
            if (completion == null || completion.IsCompleted) return;
            const int CooperativeIdleBudget = 2048;
            int idle = 0;

            // Chase-Lev 路径：主线程 assist（对齐 C++ TryAssistOne）
            while (!completion.IsCompleted)
            {
                if (TryAssistOne()) { idle = 0; continue; }
                if (++idle >= CooperativeIdleBudget) break;
                Thread.Yield();
            }
            while (!completion.IsCompleted)
            {
                if (completion.Wait(CompleteAssistInterval)) break;
                TryAssistOne();
            }
        }

        // ──────────────────── Chase-Lev Worker 循环 ────────────────────

        /// <summary>
        /// 标准 Chase-Lev Worker 循环（crossbeam 模型）：
        ///   1. PopBottom(myDeque)              — LIFO, owner-only, 零竞争
        ///   2. injector_.TryDequeue → PushBottom — 从 Injector 拉取推入 deque
        ///   3. StealTop(otherDeque)             — 从其他 worker 窃取
        ///   4. Park                             — 有界自旋 + SemaphoreSlim wait
        ///
        /// 保证：
        ///   - 所有任务经 deque 执行（LIFO 局部性）
        ///   - 空闲 worker 可 steal 其他 worker 的任务（负载均衡）
        ///   - 不规则负载（GridSearch）下尾部可控
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void WorkerLoopChaseLev(int workerIndex)
        {
            while (true)
            {
                // 1. 本地 PopBottom（LIFO, owner-only, 零竞争）
                if (_deques![workerIndex].PopBottom(out var task))
                {
                    ExecuteTileTask(task);
                    continue;
                }

                // 2. 从 Injector 拉取 → PushBottom 推入 deque（保持可窃取性）
                if (_injector!.TryDequeue(out var tileTask))
                {
                    _deques[workerIndex].PushBottom(tileTask);
                    continue; // 下一轮 PopBottom 取出执行
                }

                // 3. 从其他 worker deque 窃取（FIFO, 1 CAS per victim）
                if (TryStealFromOtherWorkers(workerIndex, out task))
                {
                    ExecuteTileTask(task);
                    continue;
                }

                // 4. Shutdown：协作排空 Injector + deque 后退出
                if (_shutdown)
                {
                    DrainOnQuit(workerIndex);
                    break;
                }

                // 5. 无工作 → park
                if (!ParkChaseLev(workerIndex)) break;
            }
        }

        /// <summary>
        /// Shutdown 时协作排空 Injector + 自己 deque，保证遗留任务仍被执行
        /// （completion 正确触发，依赖它们的 handle 不悬挂）。
        /// </summary>
        private static void DrainOnQuit(int workerIndex)
        {
            bool anyWork = true;
            while (anyWork)
            {
                anyWork = false;

                // 从 Injector 拉取（协作排空，TryDequeue 原子）
                if (_injector!.TryDequeue(out var tileTask))
                {
                    anyWork = true;
                    ExecuteTileTask(tileTask);
                    continue;
                }

                // 从自己 deque 弹出（owner-only）
                if (_deques![workerIndex].PopBottom(out var task))
                {
                    anyWork = true;
                    ExecuteTileTask(task);
                }
            }
        }

        /// <summary>从其他 worker deque 窃取一个任务。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryStealFromOtherWorkers(int workerIndex, out ManagedTileTask task)
        {
            int wc = _workerCount;
            for (int offset = 1; offset < wc; offset++)
            {
                int victimIdx = (workerIndex + offset) % wc;
                if (_deques![victimIdx].StealTop(out task))
                    return true;
            }
            task = default;
            return false;
        }

        /// <summary>Chase-Lev Park：短自旋 + SemaphoreSlim wait。
        /// 自旋/等待期间检查 injector、deque 与 _shutdown。</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static bool ParkChaseLev(int workerIndex)
        {
            // 短自旋（覆盖新批到达的窗口）
            for (int i = 0; i < 256; i++)
            {
                if (_shutdown) return false;
                if (!_injector!.IsEmpty || !_deques![workerIndex].IsEmpty)
                    return true; // 有活，回主循环
                Thread.SpinWait(16);
            }
            // 最后一次检查（自旋期间可能有新任务到达）
            if (!_injector!.IsEmpty)
                return true;
            // Park：SemaphoreSlim.Wait 超时兜底；唤醒后再查 _shutdown（防延迟退出）
            _wakeSignal!.Wait(ParkTimeout);
            if (_shutdown) return false;
            return true;
        }

        /// <summary>
        /// 执行一个 ManagedTileTask 并完成收尾（Signal + Release + 回池）。
        /// 对齐 C++ ChaseLevScheduler::ExecuteAndRelease。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ExecuteTileTask(ManagedTileTask task)
        {
            // 空 Runner 也回池（防池槽位泄漏）
            if (task.Runner == null) { _tileTaskPool?.Release(task); return; }
            // 对齐 Native 回调：设置执行深度，使 EntityManager 的 IsExecutingJob 检测
            // 在 Managed fallback 下同样生效——否则 job 内结构变更会自等待自 → 死锁。
            NativeJobCore.EnterJobExecution();
            try
            {
                task.Runner(task.Job, task.Start, task.Count);
            }
            catch (Exception ex)
            {
                task.Completion?.RecordException(ex);
            }
            finally
            {
                NativeJobCore.ExitJobExecution();
                // completion 信号
                task.Completion?.Signal();
                // 释放 job 盒
                task.Release?.Invoke(task.Job);
                // 释放 tile task 回池（对齐 C++ ChaseLevScheduler::ExecuteAndRelease）
                _tileTaskPool?.Release(task);
            }
        }

        /// <summary>主线程协助执行：从 Injector 或其他 worker deque 窃取并执行。
        /// 对齐 C++ ChaseLevScheduler::TryAssistOne。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryAssistOne()
        {
            if (_injector == null || _deques == null) return false;

            // 1. 从 Injector 窃取
            if (_injector.TryDequeue(out var task))
            {
                ExecuteTileTask(task);
                return true;
            }

            // 2. 从其他 worker deque 窃取
            if (TryStealFromOtherWorkers(0, out var stolen))
            {
                ExecuteTileTask(stolen);
                return true;
            }

            return false;
        }

        /// <summary>Chase-Lev 唤醒：释放足够信号唤醒所有 worker。
        /// 全唤醒更可靠（对齐 Native 的全广播），未找到工作的 worker 立即重新 park。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PublishChaseLev(int taskCount)
        {
            // 全唤醒：释放 workerCount 个信号，确保所有 worker 都有机会认领
            _wakeSignal?.Release(_workerCount);
        }

        // ── Chase-Lev Schedule 入口 ──

        /// <summary>Chase-Lev 单任务调度。</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static ManagedJobHandle ScheduleChaseLevSingle<T>(ref T job, ManagedJobHandle dependsOn = default)
            where T : struct, IJob
        {
            var completion = RentCompletion();
            Interlocked.Exchange(ref completion.Remaining, 1);

            // 先 box job（避免 ref 参数在 lambda 中捕获）
            var boxedJob = SingleCache<T>.Box(job);

            void EnqueueSingle()
            {
                // 池耗尽兜底：池外分配（PoolIndex=-1，Release 跳过），不阻塞不丢任务。
                var acquired = _tileTaskPool!.Acquire();
                var t = acquired.HasValue ? acquired.Value : new ManagedTileTask { PoolIndex = -1 };

                t.Job = boxedJob;
                t.Runner = SingleCache<T>.Runner;
                t.Release = SingleCache<T>.ReleaseBox;
                t.Completion = completion;
                t.Start = 0;
                t.Count = 1;

                while (!_injector!.TryEnqueue(t))
                    Thread.Yield();

                PublishChaseLev(1);
            }

            if (dependsOn.Completion == null)
                EnqueueSingle();
            else
                ChainAfter(dependsOn.Completion, dependsOn.Gen, completion, EnqueueSingle);

            return new ManagedJobHandle(completion);
        }

        /// <summary>
        /// 标准 Chase-Lev 并行 for 调度：预切分为 ManagedTileTask 推入 Injector。
        /// Worker 从 Injector 拉取推入 deque（owner-only PushBottom），标准 Chase-Lev 循环执行。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static ManagedJobHandle ScheduleChaseLevParallelFor<T>(
            ref T job, int arrayLength, int innerBatchCount, ManagedJobHandle dependsOn)
            where T : struct, IJobParallelFor
        {
            var completion = RentCompletion();
            int workers = Math.Max(1, _workerCount);

            // batch 粒度：目标 taskCount ≈ workers*16（平衡预切分开销与 steal 均衡）
            int batch;
            if (innerBatchCount > 0)
                batch = Math.Max(1, innerBatchCount);
            else
                batch = Math.Max(16, (arrayLength + workers * 16 - 1) / (workers * 16));

            int taskCount = (arrayLength + batch - 1) / batch;

            // 池容量保险：taskCount ≤ 池容量/2，防止游标回绕复用未释放任务
            if (taskCount >= ManagedTileTaskPool.PoolSize / 2)
            {
                while (batch < arrayLength &&
                       (arrayLength + batch - 1) / batch >= ManagedTileTaskPool.PoolSize / 2)
                    batch *= 2;
                taskCount = (arrayLength + batch - 1) / batch;
            }

            Interlocked.Exchange(ref completion.Remaining, taskCount);

            var box = ParallelCache<T>.Box(job);
            completion.OnCompleted(() => ParallelCache<T>.ReleaseBox(box));

            void EnqueueTiles()
            {
                for (int i = 0; i < taskCount; i++)
                {
                    // 池耗尽兜底：池外分配（PoolIndex=-1，Release 跳过），
                    // 不跳过任务 —— Remaining 预设为 taskCount，跳过会导致永不到 0。
                    var acquired = _tileTaskPool!.Acquire();
                    var t = acquired.HasValue ? acquired.Value : new ManagedTileTask { PoolIndex = -1 };

                    t.Job = box;
                    t.Runner = ParallelCache<T>.Runner;
                    t.Release = null;
                    t.Completion = completion;
                    t.Start = i * batch;
                    t.Count = Math.Min(batch, arrayLength - i * batch);

                    while (!_injector!.TryEnqueue(t))
                        Thread.Yield();
                }

                PublishChaseLev(taskCount);
            }

            ChainAfter(dependsOn.Completion, dependsOn.Gen, completion, EnqueueTiles);
            return new ManagedJobHandle(completion);
        }

        // ──────────────────── 泛型委托缓存 ────────────────────

        internal delegate void JobRunner(object boxed, int start, int count);

        private static class SingleCache<T> where T : struct, IJob
        {
            private static object? _freeBox;
            public static readonly JobRunner Runner = Run;
            public static readonly Action<object> ReleaseBox = static box => { Interlocked.Exchange(ref _freeBox, box); };
            public static object Box(in T job)
            {
                var box = Interlocked.Exchange(ref _freeBox, null);
                if (box != null) Unsafe.Unbox<T>(box) = job;
                else box = job;
                return box;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void Run(object boxed, int start, int count) => ((T)boxed).Execute();
        }

        private static class ParallelCache<T> where T : struct, IJobParallelFor
        {
            private static object? _freeBox;
            public static readonly JobRunner Runner = Run;
            public static readonly Action<object> ReleaseBox = static box => { Interlocked.Exchange(ref _freeBox, box); };
            public static object Box(in T job)
            {
                var box = Interlocked.Exchange(ref _freeBox, null);
                if (box != null) Unsafe.Unbox<T>(box) = job;
                else box = job;
                return box;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void Run(object boxed, int start, int count)
            {
                var job = (T)boxed;
                for (int i = start; i < start + count; i++) job.Execute(i);
            }
        }

    }
}
