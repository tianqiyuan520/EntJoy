using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EntJoy.Collections
{
    public struct AtomicSafetyHandle : IEquatable<AtomicSafetyHandle>
    {
        private readonly int _index;
        private readonly bool _isReadOnly;

        public bool IsReadOnly => _isReadOnly;
        public int Index => _index;

        internal AtomicSafetyHandle(int index, bool isReadOnly)
        {
            _index = index;
            _isReadOnly = isReadOnly;
        }

        public bool Equals(AtomicSafetyHandle other) => _index == other._index && _isReadOnly == other._isReadOnly;
        public override bool Equals(object obj) => obj is AtomicSafetyHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(_index, _isReadOnly);
        public static bool operator ==(AtomicSafetyHandle left, AtomicSafetyHandle right) => left.Equals(right);
        public static bool operator !=(AtomicSafetyHandle left, AtomicSafetyHandle right) => !left.Equals(right);
    }

    internal static class SafetyHandleManager
    {
        private const int MaxHandles = 1024 * 1024;
        private const int ReleasedFlag = 1;

        private static int[] _states = new int[MaxHandles];
        private static ConcurrentQueue<int> _freeIndices = new ConcurrentQueue<int>();
        private static int _nextIndex = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static AtomicSafetyHandle Allocate()
        {
            int index;
            if (_freeIndices.TryDequeue(out index))
            {
                Interlocked.Exchange(ref _states[index], 0);
                return new AtomicSafetyHandle(index, isReadOnly: false);
            }
            else
            {
                index = Interlocked.Increment(ref _nextIndex) - 1;
                if (index >= MaxHandles)
                    throw new InvalidOperationException("Out of safety handles");
                return new AtomicSafetyHandle(index, isReadOnly: false);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void Release(ref AtomicSafetyHandle handle)
        {
            int index = handle.Index;
            if (index < 0 || index >= _states.Length)
                throw new InvalidOperationException("Invalid handle index.");

            int old = Interlocked.Exchange(ref _states[index], ReleasedFlag);
            if (old == ReleasedFlag)
                return;

            _freeIndices.Enqueue(index);
            handle = default;
        }

        /// <summary>强制标记指定索引的句柄为已释放（用于 TempAllocator 紧急清理）</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static void MarkReleased(int index)
        {
            if (index < 0 || index >= _states.Length)
                return;
            Interlocked.Exchange(ref _states[index], ReleasedFlag);
            // 注意：不加入空闲队列，因为该句柄可能仍被引用，但已标记释放后任何访问都会抛异常。
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void CheckReadAndThrow(AtomicSafetyHandle handle)
        {
            int index = handle.Index;
            if (index < 0 || index >= _states.Length)
                throw new InvalidOperationException("Invalid handle index.");
            if (Volatile.Read(ref _states[index]) == ReleasedFlag)
                throw new ObjectDisposedException("NativeContainer has been disposed.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void CheckWriteAndThrow(AtomicSafetyHandle handle)
        {
            if (handle.IsReadOnly)
                throw new InvalidOperationException("Cannot write to a read-only NativeContainer.");
            CheckReadAndThrow(handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void CheckExistsAndThrow(AtomicSafetyHandle handle)
        {
            CheckReadAndThrow(handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static AtomicSafetyHandle ToReadOnly(AtomicSafetyHandle handle)
        {
            CheckExistsAndThrow(handle);
            return new AtomicSafetyHandle(handle.Index, isReadOnly: true);
        }
    }
}
