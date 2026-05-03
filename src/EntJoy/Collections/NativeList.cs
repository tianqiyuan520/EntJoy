using System;
using System.Runtime.CompilerServices;

namespace EntJoy.Collections
{
    public enum NativeArrayOptions
    {
        ClearMemory,
        UninitializedMemory
    }



    public unsafe partial struct NativeList<T> : IDisposable where T : unmanaged
    {
        private UnsafeList<T>* _listData;   // 指向堆上数据的指针
        private Allocator _allocator;        // 保存分配器，用于释放自身
        private AtomicSafetyHandle _safety;

#if DEBUG
        private DisposeSentinel _sentinel;
#endif

        public int Length => _listData != null ? _listData->Length : 0;
        public int Capacity => _listData != null ? _listData->Capacity : 0;
        public bool IsCreated => _listData != null && _listData->Ptr != null;

        public NativeList(Allocator allocator = Allocator.Persistent) : this(1, allocator) { }

        public NativeList(int initialCapacity, Allocator allocator = Allocator.Persistent)
        {
            if (initialCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));

            _allocator = allocator;
            _safety = SafetyHandleManager.Allocate();

            // 分配 UnsafeList 结构体本身（在非托管堆上）
            _listData = (UnsafeList<T>*)UnsafeUtility.Malloc(sizeof(UnsafeList<T>), allocator, _safety.Index);
            // 使用 placement new 构造 UnsafeList
            *_listData = new UnsafeList<T>(initialCapacity, allocator);

#if DEBUG
            _sentinel = new DisposeSentinel();
#endif
        }

        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            get
            {
                SafetyHandleManager.CheckReadAndThrow(_safety);
                if (index < 0 || (uint)index >= (uint)_listData->Length)
                    throw new IndexOutOfRangeException();
                //return _listData->Ptr[index];
                return UnsafeUtility.ReadArrayElement<T>(_listData->Ptr, index);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            set
            {
                SafetyHandleManager.CheckWriteAndThrow(_safety);
                if (index < 0 || (uint)index >= (uint)_listData->Length)
                    throw new IndexOutOfRangeException();
                //_listData->Ptr[index] = value;
                UnsafeUtility.WriteArrayElement(_listData->Ptr, index, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public UnsafeList<T>* GetListData()
        {
            return _listData;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T item)
        {
            SafetyHandleManager.CheckWriteAndThrow(_safety);
            _listData->Add(item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Insert(int index, T item)
        {
            SafetyHandleManager.CheckWriteAndThrow(_safety);
            _listData->Insert(index, item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAt(int index)
        {
            SafetyHandleManager.CheckWriteAndThrow(_safety);
            _listData->RemoveAt(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAtSwapBack(int index)
        {
            SafetyHandleManager.CheckWriteAndThrow(_safety);
            _listData->RemoveAtSwapBack(index);
        }

        public void Clear()
        {
            SafetyHandleManager.CheckWriteAndThrow(_safety);
            _listData->Length = 0;
        }

        public void EnsureCapacity(int min)
        {
            SafetyHandleManager.CheckWriteAndThrow(_safety);
            _listData->EnsureCapacity(min);
        }

        public void Resize(int newLength, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            SafetyHandleManager.CheckWriteAndThrow(_safety);
            _listData->Resize(newLength, options);
        }

        public void TrimExcess()
        {
            SafetyHandleManager.CheckWriteAndThrow(_safety);
            if (_listData->Length == 0)
            {
                Dispose();
                return;
            }

            if (_listData->Length < _listData->Capacity)
            {
                var newList = new UnsafeList<T>(_listData->Length, _allocator);
                UnsafeUtility.MemCpy(newList.Ptr, _listData->Ptr, _listData->Length * sizeof(T));
                _listData->Dispose();
                *_listData = newList;
            }
        }

        public NativeArray<T> AsArray()
        {
            if (_listData == null || _listData->Length == 0)
                return default;
            // 返回一个不拥有所有权的视图
            return NativeArray<T>.CreateView(_listData->Ptr, _listData->Length, _safety, _allocator);
        }

        public void* GetUnsafePtr() => _listData->Ptr;

        public Span<T> AsSpan() => new Span<T>(_listData->Ptr, _listData->Length);

        public static implicit operator Span<T>(NativeList<T> list) => list.AsSpan();
        public static implicit operator ReadOnlySpan<T>(NativeList<T> list) => new ReadOnlySpan<T>(list._listData->Ptr, list._listData->Length);

        public void Dispose()
        {
            if (_listData == null) return;

            SafetyHandleManager.Release(ref _safety);
            _listData->Dispose();               // 释放内部缓冲区
            UnsafeUtility.Free(_listData, _allocator); // 释放 UnsafeList 结构体本身
            _listData = null;

#if DEBUG
            _sentinel?.Dispose();
            _sentinel = null;
#endif
        }

        //        public JobHandle Dispose(JobHandle deps)
        //        {
        //            if (_listData == null) return deps;

        //            // 直接释放内存，而不是使用Job
        //            SafetyHandleManager.Release(ref _safety);
        //            _listData->Dispose();               // 释放内部缓冲区
        //            UnsafeUtility.Free(_listData, _allocator); // 释放 UnsafeList 结构体本身
        //#if DEBUG
        //            _sentinel?.Dispose();
        //#endif
        //            _listData = null;
        //            _safety = default;
        //#if DEBUG
        //            _sentinel = null;
        //#endif
        //            return deps;
        //        }


        // 释放作业的内部结构
        //        internal unsafe struct NativeListDisposeJob : IJob
        //        {
        //            public UnsafeList<T>* ListData;   // 注意：这里无法使用泛型，因为 IJob 需要具体类型，可能需要改为 UntypedUnsafeList
        //            public Allocator Allocator;
        //            public AtomicSafetyHandle Safety;
        //#if DEBUG
        //            public DisposeSentinel Sentinel;
        //#endif

        //            public void Execute()
        //            {
        //                SafetyHandleManager.Release(ref Safety);
        //                ListData->Dispose();
        //                UnsafeUtility.Free(ListData, Allocator);
        //#if DEBUG
        //                Sentinel?.Dispose();
        //#endif
        //            }
        //        }
    }





}
