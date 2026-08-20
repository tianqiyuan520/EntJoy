using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EntJoy.JobSystem.Managed
{
    /// <summary>
    /// 托管无锁 Job 调度器。独立于原生 NativeJobScheduler，可脱离 NativeDll 单独使用。
    ///
    /// 关键架构：
    ///   - worker parking：空闲 worker 短自旋后阻塞在 Monitor，任务发布 = PulseAll 唤醒，
    ///     消除空转 yield 导致的 worker 串行化。
    ///   - worker 从全局 MPMC 队列认领任务（快 worker 多拿，天然自适应负载均衡）。
    ///   - **混合执行并行 for**：innerBatchCount &gt; 0 走共享游标细粒度认领（重计算，动态负载均衡）；
    ///     == 0 走静态大分片（轻任务，少 dequeue/Signal 开销）。同步内联覆盖小任务（零调度开销）。
    ///   - completion 槽位池（预分配数组）+ 单槽 per-类型 job 盒池 → 热路径零分配。
    ///   - 依赖链中间 completion 完成后自动归还（防泄漏）；槽位带 generation 代际，防旧 handle 复用误操作（防 ABA）。
    ///   - 依赖回调原子累积（OnCompleted 用 CAS 循环）。
    /// </summary>
    public static class ManagedJobScheduler
    {
        private static ManagedMPMCQueue<ManagedTask> _globalQueue;
        private static Thread[] _workers;
        private static int _workerCount;
        private static volatile bool _shutdown;
        private static bool _isInitialized;
        private static readonly object _stateLock = new object();
        private static readonly object _workMonitor = new object();

        private const int QueueCapacity = 1 << 16;
        private const int SyncInlineThreshold = 1024;

        // ──────────────────── 生命周期 ────────────────────

        public static void Initialize(int workerCount = 0)
        {
            lock (_stateLock)
            {
                if (_isInitialized) return;
                _workerCount = workerCount <= 0 ? Math.Max(1, Environment.ProcessorCount - 1) : workerCount;
                _globalQueue = new ManagedMPMCQueue<ManagedTask>(QueueCapacity);
                _shutdown = false;
                _isInitialized = true;

                // 预分配 completion 槽位池：所有 ManagedCompletion 对象一次性分配，
                // 用 NextFree 链成无锁自由栈，消除 ConcurrentStack.Push 的 Node 分配。
                _completionSlots = new ManagedCompletion[CompletionPoolCap];
                for (int i = 0; i < CompletionPoolCap; i++)
                {
                    _completionSlots[i] = new ManagedCompletion { SlotIndex = i };
                    _completionSlots[i].NextFree = i + 1 < CompletionPoolCap ? i + 1 : -1;
                }
                _completionFreeHead = 0; // 栈顶指向 slot 0，next = 1

                _workers = new Thread[_workerCount];
                for (int i = 0; i < _workerCount; i++)
                {
                    int wi = i;
                    var t = new Thread(() => WorkerLoop(wi)) { IsBackground = true, Name = $"ManagedJobWorker-{i}" };
                    _workers[i] = t;
                    t.Start();
                }
            }
        }

        public static void Shutdown()
        {
            lock (_stateLock)
            {
                if (!_isInitialized) return;
                _shutdown = true;
                _isInitialized = false;
                lock (_workMonitor) Monitor.PulseAll(_workMonitor);
            }
            if (_workers == null) return;
            foreach (var w in _workers)
                if (w != null && w.IsAlive) w.Join();
            _workers = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Publish()
        {
            lock (_workMonitor) Monitor.PulseAll(_workMonitor);
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
                    // 新 job 干净状态：清归还/自动归还标记；代际+1 → 旧 handle 判过期（防 ABA）
                    Interlocked.Exchange(ref c._returned, 0);
                    Volatile.Write(ref c._autoReturn, 0);
                    Interlocked.Increment(ref c.Generation);
                    return c;
                }
            }
        }

        /// <summary>调用方完成归还（幂等：重复归还/与自动归还并发安全）。</summary>
        internal static void ReturnCompletion(ManagedCompletion c)
        {
            if (c == null) return;
            if (Interlocked.Exchange(ref c._returned, 1) == 1) return; // 已归还过
            ReturnCompletionCore(c);
        }

        /// <summary>依赖链中间 completion 完成后的自动归还（幂等防 double）。</summary>
        internal static void AutoReturnCompletion(ManagedCompletion c)
        {
            if (c == null) return;
            if (Interlocked.Exchange(ref c._returned, 1) == 1) return;
            ReturnCompletionCore(c);
        }

        private static void ReturnCompletionCore(ManagedCompletion c)
        {
            c.Reset();
            // 归还即代际 +1：任何仍持有本 completion 的旧 handle 立即可判“过期”。
            // 这是防止“已自动归还/已重置的槽位被误等待”的关键——否则还原后的 Remaining=1 会让
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
            // 未 Initialize() 时 _globalQueue/_completionSlots 为 null，后续会 NRE。
            // 生产环境宁可抛清晰异常，也不隐式建线程池（避免默认 worker 数/生命周期意外）。
            if (_globalQueue == null)
                throw new InvalidOperationException(
                    "ManagedJobScheduler 尚未初始化：请先调用 ManagedJobScheduler.Initialize().");
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static ManagedJobHandle Schedule<T>(ref T job) where T : struct, IJob
        {
            EnsureInitialized();
            var completion = ManagedJobScheduler.RentCompletion();
            completion.Remaining = 1;
            var task = new ManagedTask { Job = SingleCache<T>.Box(job), Runner = SingleCache<T>.Runner, Release = SingleCache<T>.ReleaseBox, Start = 0, Count = 1, Completion = completion };
            EnqueueGlobal(task);
            return new ManagedJobHandle(completion);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static ManagedJobHandle Schedule<T>(ref T job, ManagedJobHandle dependsOn) where T : struct, IJob
        {
            EnsureInitialized();
            var completion = ManagedJobScheduler.RentCompletion();
            completion.Remaining = 1;
            var task = new ManagedTask { Job = SingleCache<T>.Box(job), Runner = SingleCache<T>.Runner, Release = SingleCache<T>.ReleaseBox, Start = 0, Count = 1, Completion = completion };
            EnqueueAfterGlobal(task, dependsOn);
            return new ManagedJobHandle(completion);
        }

        /// <summary>
        /// 调度 IJobParallelFor：混合执行。
        /// - innerBatchCount &gt; 0 → 共享游标细粒度认领（重计算，动态负载均衡）。
        /// - innerBatchCount == 0 → 静态大分片（轻任务，少 dequeue/Signal 开销，无共享游标竞争）。
        /// 同步内联（小任务）两者通用。依赖通过 completion 计数保证顺序。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static ManagedJobHandle Schedule<T>(ref T job, int arrayLength, int innerBatchCount, ManagedJobHandle dependsOn = default)
            where T : struct, IJobParallelFor
        {
            EnsureInitialized();
            if (arrayLength <= 0)
            {
                // 零长度并行 for：无任何分片会执行，立即完成并自动归还。必须在 Signal（触发自动归还，代际+1）
                // **之前**构造 handle，以抓到归还前的代际快照 —— 否则旧 handle 的 IsExpired 判定不出“已归还”
                // 已被复用的槽位，用户 Complete() 会误等一个 Remaining 已被重置=1 的槽位而永久挂死。
                var zero = ManagedJobScheduler.RentCompletion();
                zero.Remaining = 1;
                var h = new ManagedJobHandle(zero);
                Volatile.Write(ref zero._autoReturn, 1);
                zero.Signal();
                return h;
            }

            bool depOk = dependsOn.Completion == null || dependsOn.IsCompleted;

            // [同步内联] 小并行 for 且依赖满足 → 调用线程同步执行，零调度开销（S4 调度延迟命门）
            if (depOk && arrayLength <= SyncInlineThreshold)
            {
                var c = RentCompletion(); c.Remaining = 1;
                ExecuteTask(new ManagedTask { Job = ParallelCache<T>.Box(job), Runner = SelectRunner<T>(), Release = ParallelCache<T>.ReleaseBox, Start = 0, Count = arrayLength, Completion = c });
                return new ManagedJobHandle(c);
            }

            if (innerBatchCount > 0)
                return ScheduleSharedRange<T>(ref job, arrayLength, innerBatchCount, dependsOn);
            return ScheduleStaticSlices<T>(ref job, arrayLength, dependsOn);
        }

        /// <summary>静态粗分片执行：并行 for 拆 ~worker 个固定大 chunk 入全局队列，一次性唤醒；无共享游标竞争。
        /// 每 worker 一块连续执行（极轻任务 S2 少 dequeue/Signal 开销，对齐 TPool/静态分片）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static ManagedJobHandle ScheduleStaticSlices<T>(ref T job, int arrayLength, ManagedJobHandle dependsOn)
            where T : struct, IJobParallelFor
        {
            var completion = RentCompletion();
            int workers = Math.Max(1, _workerCount);
            int targetSlice = Math.Max(1, (arrayLength + workers - 1) / workers);
            int n = (arrayLength + targetSlice - 1) / targetSlice;
            completion.Remaining = n;

            var box = ParallelCache<T>.Box(job);
            completion.OnCompleted(() => ParallelCache<T>.ReleaseBox(box));

            void EnqueueSlices()
            {
                for (int i = 0; i < n; i++)
                {
                    int s = i * targetSlice;
                    int e = Math.Min(s + targetSlice, arrayLength);
                    var t = new ManagedTask
                    {
                        Job = box,
                        Runner = ParallelCache<T>.Runner,
                        Start = s,
                        Count = e - s,
                        Completion = completion,
                        Release = null,
                    };
                    while (!_globalQueue.TryEnqueue(in t))
                    {
                        if (_globalQueue.TryDequeue(out var other)) ExecuteTask(other);
                        Thread.Yield();
                    }
                }
                Publish();
            }

            ChainAfter(dependsOn.Completion, completion, EnqueueSlices);
            return new ManagedJobHandle(completion);
        }

        /// <summary>共享游标细粒度认领执行：建共享 range，向全局队列投入 participants 个参与名额，一次性唤醒；
        /// worker 拿名额后紧循环 Interlocked.Add 认领分片（重计算 S5 动态负载均衡）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static ManagedJobHandle ScheduleSharedRange<T>(ref T job, int arrayLength, int batchSize, ManagedJobHandle dependsOn)
            where T : struct, IJobParallelFor
        {
            var completion = RentCompletion();
            int workers = Math.Max(1, _workerCount);
            int batch = Math.Max(1, batchSize);
            int totalBatches = (arrayLength + batch - 1) / batch;
            int participants = Math.Max(1, Math.Min(totalBatches, workers));
            completion.Remaining = participants;

            var box = ParallelCache<T>.Box(job);
            completion.OnCompleted(() => ParallelCache<T>.ReleaseBox(box));

            var range = new ManagedRange
            {
                Job = box,
                Length = arrayLength,
                Batch = batch,
                Current = 0,
            };
            var claimer = new ManagedTask
            {
                Job = range,
                Runner = SharedRangeClaimRunner<T>.Runner,
                Start = 0,
                Count = 1,
                Completion = completion,
                Release = null,
            };

            void EnqueueClaimers()
            {
                for (int i = 0; i < participants; i++)
                {
                    while (!_globalQueue.TryEnqueue(in claimer))
                    {
                        if (_globalQueue.TryDequeue(out var other)) ExecuteTask(other);
                        Thread.Yield();
                    }
                }
                Publish();
            }

            ChainAfter(dependsOn.Completion, completion, EnqueueClaimers);
            return new ManagedJobHandle(completion);
        }

        /// <summary>
        /// 统一挂接依赖启动：不管依赖此刻是否已完成，都把它标记为“完成后自动归还”（防中间 handle 泄漏），
        /// 并把依赖的 job 异常传播到新 job 的 completion（供末端 Complete() 抛出），然后执行 enqueue。
        /// OnCompleted 在“依赖已就绪”时立即回调，天然覆盖已完成/未完成两种情况 —— 消除了原先 depOk
        /// 分支导致的“是否自动归还取决于时序”的不确定泄漏。
        /// </summary>
        private static void ChainAfter(ManagedCompletion dep, ManagedCompletion dependent, Action enqueue)
        {
            if (dep == null) { enqueue(); return; }
            Volatile.Write(ref dep._autoReturn, 1);
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
                ManagedJobScheduler.AutoReturnCompletion(dep);
            }
            else
            {
                // 依赖尚未完成：挂回调；完成后 Signal 内部会因 _autoReturn==1 自动归还。
                dep.OnCompleted(Propagate);
            }
        }

        // ──────────────────── 主线程协作完成 ────────────────────

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void CompleteSchedule(ManagedCompletion completion)
        {
            if (completion == null || completion.IsCompleted) return;
            const int CooperativeIdleBudget = 2048;
            int idle = 0;
            while (!completion.IsCompleted)
            {
                if (TryExecuteAnyTask()) { idle = 0; continue; }
                if (++idle >= CooperativeIdleBudget) break;
                Thread.Yield();
            }
            if (!completion.IsCompleted) completion.Wait();
        }

        /// <summary>主线程协作：从全局队列取任务执行。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryExecuteAnyTask()
        {
            if (_globalQueue.TryDequeue(out var g)) { ExecuteTask(g); return true; }
            return false;
        }

        // ──────────────────── 内部队列 ────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnqueueGlobal(ManagedTask task)
        {
            while (!_globalQueue.TryEnqueue(in task))
            {
                if (_globalQueue.TryDequeue(out var other)) ExecuteTask(other);
                Thread.Yield();
            }
            Publish();
        }

        private static void EnqueueAfterGlobal(ManagedTask task, ManagedJobHandle dependsOn)
        {
            if (dependsOn.Completion == null) { EnqueueGlobal(task); return; }
            var dep = dependsOn.Completion;
            // 依赖 completion：完成后自动归还（中间 handle 调用方不主动 Complete，防 completion 槽位/闭包泄漏），
            // 并把依赖异常传播到本 job 的 completion（供末端 Complete() 抛出）。
            Volatile.Write(ref dep._autoReturn, 1);
            void Propagate()
            {
                var ex = dep.ReadException();
                if (ex != null) task.Completion?.RecordException(ex);
                EnqueueGlobal(task);
            }
            if (dep.IsCompleted)
            {
                Propagate();
                ManagedJobScheduler.AutoReturnCompletion(dep); // 已在调度前完成：当场归还，防泄漏
            }
            else
            {
                dep.OnCompleted(Propagate);
            }
        }

        // ──────────────────── Worker 循环 ────────────────────

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void WorkerLoop(int workerIndex)
        {
            while (!_shutdown)
            {
                if (_globalQueue.TryDequeue(out var g)) { ExecuteTask(g); continue; }
                if (!ParkIdle()) break;
            }
        }

        private static bool ParkIdle()
        {
            var spin = new SpinWait();
            for (int i = 0; i < 5000; i++)
            {
                if (_shutdown) return false;
                if (_globalQueue.TryDequeue(out var g)) { ExecuteTask(g); return true; }
                spin.SpinOnce();
            }
            lock (_workMonitor)
            {
                while (!_shutdown)
                {
                    if (_globalQueue.TryDequeue(out var g)) { ExecuteTask(g); return true; }
                    Monitor.Wait(_workMonitor);
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ExecuteTask(ManagedTask task)
        {
            // 异常边界：任何 job 抛异常都不得打死 worker 线程（否则依赖它的 completion 永远到不了 0
            // → Complete() 永久死锁）。捕获并记录到 completion（first-wins，供末端 Complete() 抛出），
            // 且无论如何都要 Signal 完成并释放 job 盒，保证调度器在不抛异常的路径上继续推进。
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
                task.Completion?.Signal();
                task.Release?.Invoke(task.Job);
            }
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

        private static class BatchRunner<T> where T : struct, IJobParallelForBatch
        {
            public static readonly JobRunner Runner = Run;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void Run(object boxed, int start, int count) => ((T)boxed).Execute(start, count);
        }

        /// <summary>
        /// 共享游标认领执行器（对齐 Misaki ExecuteParallelFor / C++ WorkerAtomicRangeLoop）：worker 拿到一个参与名额后，
        /// 在共享 Current 上游标紧循环认领分片执行直到耗尽。执行期只碰 shared Current 一个原子。用于重计算（S5）。
        /// </summary>
        private static class SharedRangeClaimRunner<T> where T : struct, IJobParallelFor
        {
            public static readonly JobRunner Runner = Run;

            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            private static void Run(object boxed, int start, int count)
            {
                var range = (ManagedRange)boxed;
                var job = (T)range.Job;
                while (true)
                {
                    int claimed = Interlocked.Add(ref range.Current, range.Batch) - range.Batch;
                    if (claimed >= range.Length) break;
                    int end = Math.Min(claimed + range.Batch, range.Length);
                    for (int i = claimed; i < end; i++) job.Execute(i);
                }
                // 注意：这里**不**再自行 Signal —— 完成信号统一由 ExecuteTask 的 finally 发出，
                // 否则与 ExecuteTask 的 Signal 重复，会提前触发 completion（导致提前完成 + job 盒提前归还）。
            }
        }

        private static readonly ConcurrentDictionary<Type, JobRunner> _batchRunnerCache = new ConcurrentDictionary<Type, JobRunner>();

        private static JobRunner SelectRunner<T>() where T : struct, IJobParallelFor
        {
            if (typeof(IJobParallelForBatch).IsAssignableFrom(typeof(T)))
                return _batchRunnerCache.GetOrAdd(typeof(T), t =>
                {
                    var f = typeof(BatchRunner<>).MakeGenericType(t).GetField("Runner");
                    return (JobRunner)f.GetValue(null);
                });
            return ParallelCache<T>.Runner;
        }

        // ──────────────────── 任务/区间结构 ────────────────────

        private struct ManagedTask
        {
            public object Job;
            public JobRunner Runner;
            public Action<object>? Release;
            public int Start;
            public int Count;
            public ManagedCompletion Completion;
        }

        /// <summary>并行 for 共享游标状态（对齐 C++ BatchState.nextTile / Misaki JobRanges）。</summary>
        private sealed class ManagedRange
        {
            public object Job;           // 盒装 job（整段一份）
            public int Length;
            public int Batch;            // 认领粒度
            public int Current;          // 共享游标（Interlocked.Add 认领）
        }
    }
}
