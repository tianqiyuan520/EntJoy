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
        // ctx → 该 job 写过的容器 index，用于 job 结束时一次性释放其写声明
        private static readonly ConcurrentDictionary<nint, System.Collections.Generic.List<int>> _ctxWrites = new();
        // ctx → 该 job 读过的容器 index 集合（幂等：同一 job 对同一容器至多 +1）
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
                throw new ObjectDisposedException("NativeContainer has been disposed.");

            nint ctx = JobIdentity.CurrentContext;
            if (_writeTxExempt[index] != 0)
                return;   // 共享/框架内部句柄：豁免并行读写持有跟踪（job 与主线程访问均放行）
            if (ctx != 0)
            {
                // job 内读：登记本 job 为读者（幂等，job 结束 ReleaseReadsForContext 释放）
                RegisterRead(index);
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
                // 记录本 job 写过的容器，便于结束时释放
                if (!_ctxWrites.TryGetValue(ctx, out var list))
                    list = _ctxWrites.GetOrAdd(ctx, _ => new System.Collections.Generic.List<int>());
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
        /// 释放某 job 执行上下文声明的全部写者（job 执行结束回调调用）。
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
        /// 并行读持有标记：登记当前 job（JobIdentity.CurrentContext）为容器读者。
        /// 仅当该 (ctx, 容器) 首次读时才把读者计数 +1（幂等，同一 job 对同一容器至多 +1，
        /// 避免并行 tile 重复计数撑爆）。主线程（ctx=0）不登记，只查。
        /// 幂等集合与并发释放（ReleaseReadsForContext）之间的竞态：同 ctx 多 tile 并发时，
        /// 一个 tile 的 RegisterRead 可能在另一 tile 已把该 ctx 条目从字典移除后仍持有旧 set，
        /// 若不管会让 +1 永不配对（读者计数泄漏 → 容器被永久误锁）。故加锁内先校验
        /// 「当前字典仍持有本 set」，否则丢弃并重新登记到现役（或新建）条目。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static void RegisterRead(int index)
        {
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
            if (_writeTxExempt[index] != 0) return;
            nint ctx = JobIdentity.CurrentContext;
            if (ctx == 0) return;
            while (true)
            {
                var set = _ctxReads.GetOrAdd(ctx, _ => new System.Collections.Generic.HashSet<int>());
                lock (set)
                {
                    // 条目可能已被并发 ReleaseReadsForContext 移除或替换：落进旧 set 的 +1 永不配对，
                    // 必须丢弃并重新登记到现役条目（GetOrAdd 会在无条目时新建）。
                    if (!_ctxReads.TryGetValue(ctx, out var cur) || !ReferenceEquals(cur, set))
                        continue;
                    if (set.Add(index)) Interlocked.Increment(ref _readerCount[index]);
                    return;
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
