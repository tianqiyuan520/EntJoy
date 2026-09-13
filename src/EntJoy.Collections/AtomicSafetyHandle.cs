using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EntJoy.Collections
{
    /// <summary>
    /// 原子安全句柄。带 generation（代际）防 ABA：句柄释放后 index 复用，旧句柄的 version 不再匹配，
    /// 无法通过安全检查绕过 use-after-free。
    /// 布局保持 8 字节（int index + int version），与 C++ NativeContainers.h 的 intptr_t 一致。
    /// isReadOnly 编码进 version 符号位（负 = 只读）。
    /// </summary>
    public struct AtomicSafetyHandle : IEquatable<AtomicSafetyHandle>
    {
        private readonly int _index;
        private readonly int _version;   // >0 可写；<0 只读（|version| 为代际）；0 = 无效句柄

        public bool IsReadOnly => _version < 0;
        public int Index => _index;
        public int Version => _version < 0 ? -_version : _version;

        internal AtomicSafetyHandle(int index, int version, bool isReadOnly)
        {
            _index = index;
            _version = isReadOnly ? -version : version;
        }

        public bool Equals(AtomicSafetyHandle other) => _index == other._index && _version == other._version;
        public override bool Equals(object obj) => obj is AtomicSafetyHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(_index, _version);
        public static bool operator ==(AtomicSafetyHandle left, AtomicSafetyHandle right) => left.Equals(right);
        public static bool operator !=(AtomicSafetyHandle left, AtomicSafetyHandle right) => !left.Equals(right);
    }

    internal static class SafetyHandleManager
    {
        private const int MaxHandles = 1024 * 1024;
        private const int StateFree = 0;
        private const int StateActive = 1;
        private const int StateReleased = 2;

        // 每个槽位的状态（空闲/活跃/已释放）与最后发放的 version（单调递增，不复用，防 ABA）
        private static int[] _state = new int[MaxHandles];
        private static int[] _version = new int[MaxHandles];
        private static ConcurrentQueue<int> _freeIndices = new ConcurrentQueue<int>();
        private static int _nextIndex = 0;

        // 并行读写冲突检测：槽位当前持写的 job 执行上下文（ctx，JobIdentity.CurrentContext）。
        // 0 = 无 job 写者。仅对「每容器独立句柄」的容器生效；共享句柄（如 ECS chunk view）单独豁免。
        private static nint[] _writerCtx = new nint[MaxHandles];
        // 并行读持有检测：槽位被多少「不同 job」同时读（0 = 无 job 读者）。
        // 多 job 可并行读同一容器（读-读不冲突），主线程访问时只要有一个 job 在读即拦截。
        private static int[] _readerCount = new int[MaxHandles];
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
        private static int[] _writeTxExempt = new int[MaxHandles];   // 1 = 豁免并行读写持有跟踪（共享/框架内部句柄）

        /// <summary>
        /// 读登记快路径标记（P1-10）：high 32 = epoch，low 32 = ctx。
        ///
        /// 为什么要它：原实现只有**单槽** TLS 快路径（<c>_fastReadCtx/_fastReadIndex/_fastReadVersion</c>），
        /// job 内**交替访问多个容器**时 100% miss ⇒ 每次索引访问都要
        /// `ConcurrentDictionary.GetOrAdd` + `lock(HashSet)` + `HashSet.Add` + `Interlocked.Increment`，
        /// 实测 **~167 ns/访问**（15 列 × 1M = 2.3 s/步，见 CPU 百万同屏文档 §5.6）。
        /// 有了它，同一 job 内每容器只在首次访问时走慢路径，之后每次访问只多"一次 long 读 + 比较"。
        ///
        /// 正确性：epoch 在 <see cref="ReleaseReadsForContext"/> 递增 ⇒ 任何 job 结束都会让全部标记失效
        /// （ctx 值来自上下文池会被复用，若不失效，新 job 会误判"已登记"而不计数 → 主线程保护失效）。
        /// 失效只会让另一线程正在跑的 job 多做一次幂等登记，不影响语义。
        /// </summary>
        private static long[] _readMark = new long[MaxHandles];
        private static int _readMarkEpoch;
        // ctx → 该 job 写过的容器 index，用于 job 结束时一次性释放其写声明
        private static readonly ConcurrentDictionary<nint, System.Collections.Generic.List<int>> _ctxWrites = new();
        // ctx → 该 job 读过的容器 index 集合（幂等：同一 job 对同一容器至多 +1）。
        // 必须按 ctx 分集合：多个 job 可并发读同一容器，单个槽位的「归属 ctx」无法表达多读者。
        private static readonly ConcurrentDictionary<nint, System.Collections.Generic.HashSet<int>> _ctxReads = new();
#endif

        /// <summary>
        /// 运行时安全检查开关。默认开启（Debug + Release 均检查）。
        /// 设为 false 可跳过所有 CheckReadAndThrow/CheckWriteAndThrow，
        /// 消除每次 NativeArray 索引的原子读 + 分支开销（~1-2ns/次）。
        /// </summary>
        public static volatile bool SafetyChecksEnabled = true;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static AtomicSafetyHandle Allocate()
        {
            int index;
            int version;
            if (_freeIndices.TryDequeue(out index))
            {
                // 复用 index：version 递增（旧句柄 version 失效，防 ABA）
                version = Interlocked.Increment(ref _version[index]);
                Interlocked.Exchange(ref _state[index], StateActive);
            }
            else
            {
                index = Interlocked.Increment(ref _nextIndex) - 1;
                if (index >= MaxHandles)
                    throw new InvalidOperationException("Out of safety handles");
                version = Interlocked.Increment(ref _version[index]); // 0 → 1
                Interlocked.Exchange(ref _state[index], StateActive);
            }
            return new AtomicSafetyHandle(index, version, isReadOnly: false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void Release(ref AtomicSafetyHandle handle)
        {
            int index = handle.Index;
            if (index < 0 || index >= MaxHandles)
                throw new InvalidOperationException("Invalid handle index.");

            int old = Interlocked.Exchange(ref _state[index], StateReleased);
            if (old == StateReleased)
                return;

            Volatile.Write(ref _writerCtx[index], 0);   // 句柄释放：清写者声明，防 index 复用残留
            Volatile.Write(ref _readerCount[index], 0); // 清读者计数
            _freeIndices.Enqueue(index);
            // 设置为无效索引(-1)，避免 default 被回收后 use-after-free
            handle = new AtomicSafetyHandle(-1, 1, isReadOnly: false);
        }

        /// <summary>强制标记指定索引的句柄为已释放（用于 TempAllocator 紧急清理）</summary>
        /// 注意：不归还索引到空闲队列，因为旧句柄仍可能被访问；
        /// 标记为已释放后 CheckReadAndThrow 会捕获并抛出异常。
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static void MarkReleased(int index)
        {
            if (index < 0 || index >= MaxHandles)
                return;
            Interlocked.Exchange(ref _state[index], StateReleased);
            Volatile.Write(ref _writerCtx[index], 0);
            Volatile.Write(ref _readerCount[index], 0);
            // 不加入空闲队列 — 该句柄可能仍被引用，标记释放后任何访问都会抛异常
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void CheckReadAndThrow(AtomicSafetyHandle handle)
        {
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
            if (!SafetyChecksEnabled) return;
            int index = handle.Index;
            if (index < 0 || index >= MaxHandles)
                throw new InvalidOperationException("Invalid handle index.");
            // 双条件：状态须活跃 且 version 须匹配（防 index 复用后的 ABA）
            if (Volatile.Read(ref _state[index]) != StateActive ||
                Volatile.Read(ref _version[index]) != handle.Version)
            {
                // 区分"从未创建/binding 前访问"与"已释放"：默认句柄是 (Index=0, Version=0)
                // （Allocate 会把 version 置为 ≥1），报错信息不同能省一次排查
                // —— 实测踩过：对未绑定的 default NativeArray 写索引得到 "has been disposed"，
                //    而真实原因是"还没创建/还没绑定"。
                if (index == 0 && handle.Version == 0)
                    throw new InvalidOperationException(
                        "NativeArray 未创建（default 句柄）：请先分配或用 CreateView 绑定后再访问。");
                throw new ObjectDisposedException("NativeContainer has been disposed.");
            }

            nint ctx = JobIdentity.CurrentContext;
            if (_writeTxExempt[index] != 0)
                return;   // 共享/框架内部句柄：豁免并行读写持有跟踪（job 与主线程访问均放行）
            if (ctx != 0)
            {
                // job 内读：先查"本 epoch 内本 ctx 是否已登记过该容器"（一次 long 读 + 比较）
                long mark = ((long)Volatile.Read(ref _readMarkEpoch) << 32) | (uint)(int)ctx;
                if (Volatile.Read(ref _readMark[index]) == mark) return;
                // 登记本 job 为读者（幂等，job 结束 ReleaseReadsForContext 释放）
                RegisterRead(index, handle.Version);
                Volatile.Write(ref _readMark[index], mark);
                return;
            }
            // 主线程读：任何 job 在写或读该容器 → 拦截
            if (Volatile.Read(ref _writerCtx[index]) != 0)
                throw new InvalidOperationException(
                    "NativeContainer is being written by an active job; Complete() before accessing it from the main thread.");
            if (Volatile.Read(ref _readerCount[index]) != 0)
                throw new InvalidOperationException(
                    "NativeContainer is being read by an active job; Complete() before accessing it from the main thread.");
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void CheckReadAndAllowInvalid(AtomicSafetyHandle handle)
        {
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
            if (!SafetyChecksEnabled) return;
            int index = handle.Index;
            if (index < 0) return; // 已释放的容器，允许不抛异常
            if (index >= MaxHandles)
                throw new InvalidOperationException("Invalid handle index.");
            if (Volatile.Read(ref _state[index]) != StateActive ||
                Volatile.Read(ref _version[index]) != handle.Version)
                throw new ObjectDisposedException("NativeContainer has been disposed.");
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void CheckWriteAndThrow(AtomicSafetyHandle handle)
        {
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
            if (!SafetyChecksEnabled) return;
            if (handle.IsReadOnly)
                throw new InvalidOperationException("Cannot write to a read-only NativeContainer.");
            CheckReadAndThrow(handle);
            TryAcquireWriteContext(handle.Index);
#endif
        }

        /// <summary>
        /// 并行写冲突检测：登记当前 job（JobIdentity.CurrentContext）为容器写者。
        /// 同一 job 的并行 tile（同 ctx）多次写放行；不同 job 交叉写同一容器冲突。
        /// 主线程（ctx=0）/共享句柄走旁路。若当前线程不在任何 job 内（ctx=0）且容器已被他处 job 持写 → 冲突。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static void TryAcquireWriteContext(int index)
        {
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
            if (_writeTxExempt[index] != 0) return;
            nint ctx = JobIdentity.CurrentContext;
            if (ctx == 0)
            {
                // 主线程（非 job）写：若容器正被某 job 持写则冲突（保护 job 写区不被主线程破坏）
                if (Volatile.Read(ref _writerCtx[index]) != 0)
                    throw new InvalidOperationException(
                        "NativeContainer is being written by an active job; Complete() before writing from the main thread.");
                if (Volatile.Read(ref _readerCount[index]) != 0)
                    throw new InvalidOperationException(
                        "NativeContainer is being read by an active job; Complete() before writing from the main thread.");
                return;
            }
            nint existing = Volatile.Read(ref _writerCtx[index]);
            if (existing == 0)
            {
                Volatile.Write(ref _writerCtx[index], ctx);
                // 登记本 job 写过的容器，便于 job 结束时释放。
                // 必须用单次 GetOrAdd 取得「当前字典里那份 list」并在锁内 Add —— 不能用
                // TryGetValue + GetOrAdd 组合：两者之间条目可能被并发 ReleaseWritesForContext 移除，
                // 于是 index 被写进一份即将被丢弃的 list，释放时扫不到它，
                // _writerCtx[index] 永久残留该 ctx → Complete() 后主线程访问被永久误拦。
                var list = _ctxWrites.GetOrAdd(ctx, _ => new System.Collections.Generic.List<int>());
                lock (list) { if (!list.Contains(index)) list.Add(index); }
            }
            else if (existing != ctx)
            {
                throw new InvalidOperationException(
                    $"NativeContainer already being written by another parallel job (ctx={ctx} vs {existing}); schedule it after that job with a dependency, or use separate containers.");
            }
#endif
        }

        /// <summary>
        /// 释放某 job 执行上下文声明的全部写者。Native 侧在 job 完成回调（所有 tile 之后）调用一次；
        /// Managed 侧目前在 tile 结束时调用（同一 ctx 会被多次调用）。
        /// 不删 _ctxWrites 条目：条目一旦被删，并发 tile 的 TryAcquireWriteContext 会 GetOrAdd 出一份新 list，
        /// 而调用方随后删掉的那份新 list 里的 index 永远不会被释放（_writerCtx 残留 → 永久误拦）。
        /// 保留条目只做清空，使「同一 ctx 只有一份 list」恒成立；条目随 ctx 存活，ctx 有限，不会无界增长。
        /// </summary>
        public static void ReleaseWritesForContext(nint ctx)
        {
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
            if (ctx == 0) return;
            if (_ctxWrites.TryRemove(ctx, out var list))
                lock (list) { foreach (int i in list) Volatile.Write(ref _writerCtx[i], 0); }
#endif
        }

        /// <summary>
        /// 快路径缓存：本线程最近一次「确实完成登记」的 (ctx, 容器 index, 句柄代际)。
        /// 命中即代表该 (ctx, index) 已登记且其登记尚未被释放，可直接返回，
        /// 免掉每次索引的 ConcurrentDictionary.GetOrAdd + Monitor 开销。
        ///
        /// 命中即安全的依据：
        ///  - 首次登记成功（+1）才写入缓存，而 +1 与释放侧的 -1 成对；
        ///  - ctx 只会在本线程的 job 内等于缓存值（JobIdentity 是 [ThreadStatic]，跨 job 会还原/换值），
        ///    故命中时必然仍处于「写入缓存的那个 ctx」的 job 生命周期内，无需回查槽位归属；
        ///  - 句柄代际（version）参与比较，覆盖 index 释放后复用的情形：复用必然递增代际 → 缓存失效并重新登记；
        ///  - 豁免句柄（_writeTxExempt）在首个访问就提前 return，永不写缓存，故缓存条目恒为非豁免容器。
        /// 这些数组都是内部状态：一旦发生泄漏/失配，RepeatedParallelReadJobs_NoReaderCountLeak 会持续失败。
        /// </summary>
        [ThreadStatic] private static nint _fastReadCtx;
        [ThreadStatic] private static int _fastReadIndex;
        [ThreadStatic] private static int _fastReadVersion;

        /// <summary>
        /// 并行读持有标记：登记当前 job（JobIdentity.CurrentContext）为容器读者。
        /// 仅当该 (ctx, 容器) 首次读时才把读者计数 +1（幂等，同一 job 对同一容器至多 +1，
        /// 避免并行 tile 重复计数撑爆）。主线程（ctx=0）不登记，只查。
        /// 幂等集合与并发释放（ReleaseReadsForContext）之间的竞态：同 ctx 多 tile 并发时，
        /// 一个 tile 的 RegisterRead 可能在另一 tile 已把该 ctx 条目从字典移除后仍持有旧 set。
        /// 计数只允许在「条目仍现役」时进行，且校验必须放在 Add 之后（见方法内注释），
        /// 否则 +1 会落在已丢弃的 set 上而永不配对（读者计数泄漏 → 容器被永久误锁、主线程被永久误拦）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static void RegisterRead(int index, int version)
        {
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
            nint ctx = JobIdentity.CurrentContext;
            // 快路径：同线程在同一 job 内对同一容器反复访问（绝大多数热循环形态）
            if (ctx == _fastReadCtx && index == _fastReadIndex && version == _fastReadVersion)
                return;
            if (_writeTxExempt[index] != 0) return;
            if (ctx == 0) return;
            while (true)
            {
                var set = _ctxReads.GetOrAdd(ctx, _ => new System.Collections.Generic.HashSet<int>());
                lock (set)
                {
                    // 顺序要紧：必须「先 Add 再校验条目仍是现役」。
                    // 若先校验再 Add，则在本行与锁定之间条目可能被并发 ReleaseReadsForContext 移除，
                    // 此时 Add 会在已被丢弃的 set 上返回 true 并 +1，而 ReleaseReadsForContext 只递减
                    // 它移除时看到的那份 set 的成员 → +1 永不配对 → _readerCount 永久泄漏 → 主线程被永久误拦。
                    // Add 返回 true 即代表本次是本 set 内该 index 的首次登记，故撤销自己的登记是安全的。
                    if (!set.Add(index)) { _fastReadCtx = ctx; _fastReadIndex = index; _fastReadVersion = version; return; }
                    if (_ctxReads.TryGetValue(ctx, out var cur) && ReferenceEquals(cur, set))
                    {
                        Interlocked.Increment(ref _readerCount[index]);   // 仅在条目仍现役时计数，保证与释放侧配平
                        _fastReadCtx = ctx; _fastReadIndex = index; _fastReadVersion = version;
                        return;
                    }
                    set.Remove(index);   // 落进了已被移除的条目：撤销后重新登记到现役条目
                }
            }
#endif
        }

        /// <summary>
        /// 释放某 job 执行上下文声明的全部读者（job 完整结束回调调用）。
        /// 幂等：仅第一次调用对该 ctx 生效。读者计数递减做下限钳制（>=0），
        /// 防止并发释放对已归零计数误减成负而污染后续判定。
        /// </summary>
        public static void ReleaseReadsForContext(nint ctx)
        {
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
            if (ctx == 0) return;
            // 令全部 _readMark 失效（ctx 会被复用；不失效会让后续 job 误判"已登记"而不计数，削弱主线程保护）
            Volatile.Write(ref _readMarkEpoch, Volatile.Read(ref _readMarkEpoch) + 1);
            if (_ctxReads.TryRemove(ctx, out var set))
            {
                lock (set)
                {
                    foreach (int i in set)
                    {
                        int c;
                        while ((c = Volatile.Read(ref _readerCount[i])) > 0)
                        {
                            if (Interlocked.CompareExchange(ref _readerCount[i], c - 1, c) == c)
                                break;
                        }
                    }
                }
            }
#endif
        }

        /// <summary>
        /// 标记某句柄豁免并行写冲突检测（框架内部/多容器共享句柄，如 ECS chunk view）。
        /// </summary>
        public static void ExemptWriteTracking(int index)
        {
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
            if (index >= 0 && index < MaxHandles)
                Volatile.Write(ref _writeTxExempt[index], 1);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void CheckExistsAndThrow(AtomicSafetyHandle handle)
        {
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
            if (!SafetyChecksEnabled) return;
            CheckReadAndThrow(handle);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static AtomicSafetyHandle ToReadOnly(AtomicSafetyHandle handle)
        {
            CheckExistsAndThrow(handle);
            return new AtomicSafetyHandle(handle.Index, handle.Version, isReadOnly: true);
        }
    }
}
