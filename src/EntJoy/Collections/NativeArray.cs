#define DEBUG

using System;
using System.Runtime.CompilerServices;

namespace EntJoy.Collections
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public unsafe partial struct NativeArray<T> : IDisposable where T : unmanaged
    {
        private void* _buffer;
        private int _length;
        private Allocator _allocator;
        private AtomicSafetyHandle _safety;
        private bool _isOwner;

#if DEBUG
        //private DisposeSentinel _sentinel;
#endif

        public int Length => _length;
        public bool IsCreated => _buffer != null;
        public bool IsOwner => _isOwner;

        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            get
            {
                unchecked
                {
                    SafetyHandleManager.CheckReadAndThrow(_safety);
                    if (index < 0 || index >= _length)
                        throw new IndexOutOfRangeException();
                    return UnsafeUtility.ReadArrayElement<T>(_buffer, index);
                    //return Unsafe.Read<T>((byte*)_buffer + index * sizeof(T));
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            set
            {
                unchecked
                {
                    SafetyHandleManager.CheckWriteAndThrow(_safety);
                    if (index < 0 || index >= _length)
                        throw new IndexOutOfRangeException();
                    UnsafeUtility.WriteArrayElement(_buffer, index, value);
                    //Unsafe.Write((byte*)_buffer + index * sizeof(T), value);
                }
            }
        }

        // ========== 构造函数（拥有者） ==========
        public NativeArray(int length, Allocator allocator = Allocator.Persistent)
        : this(length, allocator, NativeArrayOptions.ClearMemory) { }

        public NativeArray(int length, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            _length = length;
            _allocator = allocator;
            _safety = SafetyHandleManager.Allocate(); // 先分配安全句柄

            int size = length * sizeof(T);
            _buffer = UnsafeUtility.Malloc(size, allocator, _safety.Index); // 分配内存

            if ((options & NativeArrayOptions.ClearMemory) == NativeArrayOptions.ClearMemory)
            {
                UnsafeUtility.MemClear(_buffer, size); // 如果需要，清零内存
            }

            _isOwner = true;
#if DEBUG
            //_sentinel = new DisposeSentinel();
#endif
        }

        // 从数组构造的构造函数保持不变（已委托给 this(length, allocator)）
        public NativeArray(T[] array, Allocator allocator = Allocator.Persistent)
            : this(array.Length, allocator)
        {
            fixed (void* src = array)
                UnsafeUtility.MemCpy(_buffer, src, array.Length * sizeof(T));
        }

        // 内部构造函数（视图，不拥有内存）
        private NativeArray(void* buffer, int length, Allocator allocator, AtomicSafetyHandle safety, bool isOwner)
        {
            _buffer = buffer;
            _length = length;
            _allocator = allocator;
            _safety = safety;
            _isOwner = isOwner;
#if DEBUG
            //_sentinel = null;
#endif
        }

        // ========== 释放 ==========
        public void Dispose()
        {
            if (_buffer == null) return;
            if (_isOwner)
            {
                SafetyHandleManager.Release(ref _safety);
                UnsafeUtility.Free(_buffer, _allocator);
            }
            _buffer = null;
            _length = 0;
            _safety = default;
#if DEBUG
            //_sentinel?.Dispose();
            //_sentinel = null;
#endif
        }

        //        public JobHandle Dispose(JobHandle deps)
        //        {
        //            if (_buffer == null) return deps;
        //            if (_isOwner)
        //            {
        //                // 直接释放内存，而不是使用Job
        //                SafetyHandleManager.Release(ref _safety);
        //                UnsafeUtility.Free(_buffer, _allocator);
        //#if DEBUG
        //                _sentinel?.Dispose();
        //#endif
        //                _buffer = null;
        //                _length = 0;
        //                _safety = default;
        //#if DEBUG
        //                _sentinel = null;
        //#endif
        //            }
        //            else
        //            {
        //                _buffer = null;
        //                _length = 0;
        //                _safety = default;
        //            }
        //            return deps;
        //        }

        // ========== 复制方法 ==========
        public void CopyTo(T[] array)
        {
            SafetyHandleManager.CheckReadAndThrow(_safety);
            if (array.Length < _length) throw new ArgumentException("目标数组太小");
            fixed (void* dst = array)
                UnsafeUtility.MemCpy(dst, _buffer, _length * sizeof(T));
        }

        public void CopyFrom(T[] array)
        {
            SafetyHandleManager.CheckWriteAndThrow(_safety);
            if (array.Length < _length) throw new ArgumentException("源数组太小");
            fixed (void* src = array)
                UnsafeUtility.MemCpy(_buffer, src, _length * sizeof(T));
        }

        public T[] ToArray()
        {
            SafetyHandleManager.CheckReadAndThrow(_safety);
            var arr = new T[_length];
            fixed (void* dst = arr)
                UnsafeUtility.MemCpy(dst, _buffer, _length * sizeof(T));
            return arr;
        }

        public void* GetUnsafePtr() => _buffer;

        // ========== 子数组（视图） ==========
        public NativeArray<T> GetSubArray(int start, int length)
        {
            if (start < 0 || length < 0 || start + length > _length)
                throw new ArgumentOutOfRangeException();
            void* subBuffer = (byte*)_buffer + start * sizeof(T);
            return new NativeArray<T>(subBuffer, length, _allocator, _safety, isOwner: false);
        }

        // ========== 类型重新解释 ==========
        public NativeArray<U> Reinterpret<U>() where U : unmanaged
        {
            if (sizeof(T) != sizeof(U))
                throw new InvalidOperationException("类型大小不匹配");
            return new NativeArray<U>(_buffer, _length, _allocator, _safety, isOwner: false);
        }

        // ========== Span 互操作 ==========
        public Span<T> AsSpan() => new Span<T>(_buffer, _length);
        public static implicit operator Span<T>(NativeArray<T> arr) => arr.AsSpan();
        public static implicit operator ReadOnlySpan<T>(NativeArray<T> arr) => new ReadOnlySpan<T>(arr._buffer, arr._length);

        // ========== 只读视图 ==========
        public ReadOnly AsReadOnly()
        {
            return new ReadOnly(_buffer, _length, _safety);
        }

        public readonly struct ReadOnly
        {
        internal readonly unsafe void* _bufferRO;
            internal readonly int _length;
            internal readonly AtomicSafetyHandle _safety;

            internal unsafe ReadOnly(void* buffer, int length, AtomicSafetyHandle safety)
            {
                _bufferRO = buffer;
                _length = length;
                _safety = SafetyHandleManager.ToReadOnly(safety);
            }

            public int Length => _length;

            public T this[int index]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    SafetyHandleManager.CheckReadAndThrow(_safety);
                    if ((uint)index >= (uint)_length)
                        throw new IndexOutOfRangeException();
                    return ((T*)_bufferRO)[index];
                }
            }

            public void CopyTo(T[] array) => NativeArray<T>.Copy(this, array);
            public void CopyTo(NativeArray<T> array) => NativeArray<T>.Copy(this, array);
            public T[] ToArray()
            {
                var arr = new T[_length];
                fixed (void* dst = arr)
                    UnsafeUtility.MemCpy(dst, _bufferRO, _length * sizeof(T));
                return arr;
            }
        }

        // ========== 释放作业 ==========
        //        internal unsafe struct NativeArrayDisposeJob : IJob
        //        {
        //            public Allocator Allocator;
        //            public void* Buffer;
        //            public AtomicSafetyHandle Safety;
        //#if DEBUG
        //            public DisposeSentinel Sentinel;
        //#endif
        //            public void Execute()
        //            {
        //                SafetyHandleManager.Release(ref Safety);
        //                UnsafeUtility.Free(Buffer, Allocator);
        //#if DEBUG
        //                Sentinel?.Dispose();
        //#endif
        //            }
        //        }
    }

    // ========== 静态 ==========
    public unsafe partial struct NativeArray<T>
    {
        // 从 NativeArray 到 NativeArray
        public static void Copy(NativeArray<T> src, NativeArray<T> dst)
        {
            SafetyHandleManager.CheckReadAndThrow(src._safety);
            SafetyHandleManager.CheckWriteAndThrow(dst._safety);
            if (src.Length != dst.Length)
                throw new ArgumentException("Source and destination lengths must be equal.");
            UnsafeUtility.MemCpy(dst._buffer, src._buffer, src.Length * sizeof(T));
        }

        public static void Copy(NativeArray<T> src, int srcIndex, NativeArray<T> dst, int dstIndex, int length)
        {
            SafetyHandleManager.CheckReadAndThrow(src._safety);
            SafetyHandleManager.CheckWriteAndThrow(dst._safety);
            if (srcIndex < 0 || dstIndex < 0 || length < 0 ||
                srcIndex + length > src.Length || dstIndex + length > dst.Length)
                throw new ArgumentOutOfRangeException();
            byte* srcPtr = (byte*)src._buffer + srcIndex * sizeof(T);
            byte* dstPtr = (byte*)dst._buffer + dstIndex * sizeof(T);
            UnsafeUtility.MemCpy(dstPtr, srcPtr, length * sizeof(T));
        }

        // 从 NativeArray 到 T[]
        public static void Copy(NativeArray<T> src, T[] dst)
        {
            SafetyHandleManager.CheckReadAndThrow(src._safety);
            if (src.Length != dst.Length)
                throw new ArgumentException("Source and destination lengths must be equal.");
            fixed (void* dstPtr = dst)
                UnsafeUtility.MemCpy(dstPtr, src._buffer, src.Length * sizeof(T));
        }

        public static void Copy(NativeArray<T> src, int srcIndex, T[] dst, int dstIndex, int length)
        {
            SafetyHandleManager.CheckReadAndThrow(src._safety);
            if (srcIndex < 0 || dstIndex < 0 || length < 0 ||
                srcIndex + length > src.Length || dstIndex + length > dst.Length)
                throw new ArgumentOutOfRangeException();
            fixed (void* dstPtr = dst)
            {
                byte* srcPtr = (byte*)src._buffer + srcIndex * sizeof(T);
                byte* dstPtr2 = (byte*)dstPtr + dstIndex * sizeof(T);
                UnsafeUtility.MemCpy(dstPtr2, srcPtr, length * sizeof(T));
            }
        }

        // 从 T[] 到 NativeArray
        public static void Copy(T[] src, NativeArray<T> dst)
        {
            SafetyHandleManager.CheckWriteAndThrow(dst._safety);
            if (src.Length != dst.Length)
                throw new ArgumentException("Source and destination lengths must be equal.");
            fixed (void* srcPtr = src)
                UnsafeUtility.MemCpy(dst._buffer, srcPtr, src.Length * sizeof(T));
        }

        public static void Copy(T[] src, int srcIndex, NativeArray<T> dst, int dstIndex, int length)
        {
            SafetyHandleManager.CheckWriteAndThrow(dst._safety);
            if (srcIndex < 0 || dstIndex < 0 || length < 0 ||
                srcIndex + length > src.Length || dstIndex + length > dst.Length)
                throw new ArgumentOutOfRangeException();
            fixed (void* srcPtr = src)
            {
                byte* srcPtr2 = (byte*)srcPtr + srcIndex * sizeof(T);
                byte* dstPtr = (byte*)dst._buffer + dstIndex * sizeof(T);
                UnsafeUtility.MemCpy(dstPtr, srcPtr2, length * sizeof(T));
            }
        }

        // 从 ReadOnly 到 NativeArray
        public static void Copy(ReadOnly src, NativeArray<T> dst)
        {
            SafetyHandleManager.CheckReadAndThrow(src._safety);
            SafetyHandleManager.CheckWriteAndThrow(dst._safety);
            if (src.Length != dst.Length)
                throw new ArgumentException("Source and destination lengths must be equal.");
            UnsafeUtility.MemCpy(dst._buffer, src._bufferRO, src.Length * sizeof(T));
        }

        public static void Copy(ReadOnly src, int srcIndex, NativeArray<T> dst, int dstIndex, int length)
        {
            SafetyHandleManager.CheckReadAndThrow(src._safety);
            SafetyHandleManager.CheckWriteAndThrow(dst._safety);
            if (srcIndex < 0 || dstIndex < 0 || length < 0 ||
                srcIndex + length > src.Length || dstIndex + length > dst.Length)
                throw new ArgumentOutOfRangeException();
            byte* srcPtr = (byte*)src._bufferRO + srcIndex * sizeof(T);
            byte* dstPtr = (byte*)dst._buffer + dstIndex * sizeof(T);
            UnsafeUtility.MemCpy(dstPtr, srcPtr, length * sizeof(T));
        }

        // 从 ReadOnly 到 T[]
        public static void Copy(ReadOnly src, T[] dst)
        {
            SafetyHandleManager.CheckReadAndThrow(src._safety);
            if (src.Length != dst.Length)
                throw new ArgumentException("Source and destination lengths must be equal.");
            fixed (void* dstPtr = dst)
                UnsafeUtility.MemCpy(dstPtr, src._bufferRO, src.Length * sizeof(T));
        }

        public static void Copy(ReadOnly src, int srcIndex, T[] dst, int dstIndex, int length)
        {
            SafetyHandleManager.CheckReadAndThrow(src._safety);
            if (srcIndex < 0 || dstIndex < 0 || length < 0 ||
                srcIndex + length > src.Length || dstIndex + length > dst.Length)
                throw new ArgumentOutOfRangeException();
            fixed (void* dstPtr = dst)
            {
                byte* srcPtr = (byte*)src._bufferRO + srcIndex * sizeof(T);
                byte* dstPtr2 = (byte*)dstPtr + dstIndex * sizeof(T);
                UnsafeUtility.MemCpy(dstPtr2, srcPtr, length * sizeof(T));
            }
        }

        internal static NativeArray<T> CreateView(void* buffer, int length, AtomicSafetyHandle safety, Allocator allocator)
        {
            return new NativeArray<T>(buffer, length, allocator, safety, isOwner: false);
        }
    }

    // ========== IAtomicHandleProvider 接口实现 ==========
    public unsafe partial struct NativeArray<T> : IAtomicHandleProvider
    {
        AtomicSafetyHandle IAtomicHandleProvider.GetSafetyHandle() => _safety;

        void IAtomicHandleProvider.MakeReadOnly()
        {
            if (_safety.IsReadOnly) return;
            var readOnlyHandle = SafetyHandleManager.ToReadOnly(_safety);
            _safety = readOnlyHandle;
        }

        void IAtomicHandleProvider.CheckExists()
        {
            SafetyHandleManager.CheckExistsAndThrow(_safety);
        }
    }


    public unsafe partial struct NativeArray<T>
    {
        /// <summary>复制此 NativeArray 的内容到目标 NativeArray。</summary>
        /// <param name="destination">目标 NativeArray，其长度必须至少与此数组相同。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void CopyTo(NativeArray<T> destination)
        {
            SafetyHandleManager.CheckReadAndThrow(_safety);
            SafetyHandleManager.CheckWriteAndThrow(destination._safety);
            if (destination.Length < _length)
                throw new ArgumentException("Destination array is too small.");
            UnsafeUtility.MemCpy(destination._buffer, _buffer, _length * sizeof(T));
        }

        public AtomicSafetyHandle GetAtomicSafetyHandle() => _safety;
    }

    internal interface IAtomicHandleProvider
    {
        AtomicSafetyHandle GetSafetyHandle();
        void MakeReadOnly();
        void CheckExists();
    }
}
