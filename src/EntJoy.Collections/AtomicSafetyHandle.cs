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
            // 不加入空闲队列 — 该句柄可能仍被引用，标记释放后任何访问都会抛异常
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void CheckReadAndThrow(AtomicSafetyHandle handle)
        {
#if ENTJOY_SAFETY
            if (!SafetyChecksEnabled) return;
            int index = handle.Index;
            if (index < 0 || index >= MaxHandles)
                throw new InvalidOperationException("Invalid handle index.");
            // 双条件：状态须活跃 且 version 须匹配（防 index 复用后的 ABA）
            if (Volatile.Read(ref _state[index]) != StateActive ||
                Volatile.Read(ref _version[index]) != handle.Version)
                throw new ObjectDisposedException("NativeContainer has been disposed.");
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void CheckReadAndAllowInvalid(AtomicSafetyHandle handle)
        {
#if ENTJOY_SAFETY
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
#if ENTJOY_SAFETY
            if (!SafetyChecksEnabled) return;
            if (handle.IsReadOnly)
                throw new InvalidOperationException("Cannot write to a read-only NativeContainer.");
            CheckReadAndThrow(handle);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void CheckExistsAndThrow(AtomicSafetyHandle handle)
        {
#if ENTJOY_SAFETY
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
