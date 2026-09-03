using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EntJoy.Collections
{
    internal struct BlobHeader
    {
        public int RefCount;
    }

    /// <summary>
    /// 不可变共享数据块 + 引用计数（跨实体共享大块只读数据，如配置/导航网格/动画曲线）。
    /// 内存布局：[BlobHeader(RefCount)] [数据 T]。复制引用需显式 Clone()（ECS 位拷贝不会自动递增计数）。
    /// </summary>
    public unsafe struct SharedBlob<T> : IDisposable where T : unmanaged
    {
        private BlobHeader* _header;

        public bool IsCreated => _header != null;

        /// <summary>当前引用计数（诊断用）。</summary>
        public int RefCount => _header != null ? Volatile.Read(ref _header->RefCount) : 0;

        /// <summary>只读访问 blob 数据。</summary>
        public ref T Value => ref Unsafe.AsRef<T>(_header + 1);

        /// <summary>递增引用计数。</summary>
        public void AddRef()
        {
            if (_header != null) Interlocked.Increment(ref _header->RefCount);
        }

        /// <summary>返回引用计数递增后的副本（复制 SharedBlob 时用，等价 shared_ptr 拷贝）。</summary>
        public SharedBlob<T> Clone()
        {
            AddRef();
            return this;
        }

        /// <summary>递减引用计数，归零释放 blob 内存。</summary>
        public void Dispose()
        {
            if (_header == null) return;
            if (Interlocked.Decrement(ref _header->RefCount) == 0)
            {
                UnsafeUtility.Free(_header, Allocator.Persistent);
                _header = null;
            }
        }

        internal static unsafe SharedBlob<T> Create(in T value)
        {
            int totalSize = sizeof(BlobHeader) + sizeof(T);
            var ptr = (BlobHeader*)UnsafeUtility.Malloc(totalSize, Allocator.Persistent);
            ptr->RefCount = 1;
            Unsafe.CopyBlock(ptr + 1, Unsafe.AsPointer(ref Unsafe.AsRef(in value)), (uint)sizeof(T));
            return new SharedBlob<T> { _header = ptr };
        }
    }

    /// <summary>创建 SharedBlob（不可变共享数据块）。</summary>
    public static class SharedBlobBuilder
    {
        public static SharedBlob<T> Create<T>(in T value) where T : unmanaged
            => SharedBlob<T>.Create(value);
    }
}
