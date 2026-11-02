using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.Collections
{
    /// <summary>
    /// 非拥有的列视图，支持连续数组及固定容量的多块存储。块表和数据由调用方持有；
    /// 使用期间地址、块顺序和容量必须稳定，结构变化后重新创建视图。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NativeColumn<T> : IDisposable where T : unmanaged
    {
        private T* _flat;
        private T** _chunks;
        private int _capacity;
        private int _shift;
        public int Length;

        public bool IsCreated => _flat != null || _chunks != null;

        public static NativeColumn<T> CreateView(void** chunks, int capacity, int length)
        {
            if (chunks == null || capacity <= 0 || length < 0) throw new ArgumentOutOfRangeException();
            int shift = -1;
            if ((capacity & (capacity - 1)) == 0)
            {
                shift = 0;
                while ((1 << shift) < capacity) shift++;
            }
            return new NativeColumn<T> { _chunks = (T**)chunks, _capacity = capacity, _shift = shift, Length = length };
        }

        public static implicit operator NativeColumn<T>(NativeArray<T> array)
            => new NativeColumn<T> { _flat = (T*)array.GetUnsafePtr(), Length = array.Length };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T* GetElementPtr(int index)
        {
            if (_chunks == null) return _flat + index;
            if (_shift >= 0) return _chunks[index >> _shift] + (index & (_capacity - 1));
            int chunk = index / _capacity;
            return _chunks[chunk] + index - chunk * _capacity;
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENTJOY_SAFETY || ENTJOY_SAFETY_BOUNDS
                if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();
#endif
                return ref *GetElementPtr(index);
            }
        }

        /// <summary>仅连续视图提供整列指针；多块视图应使用 GetElementPtr 或 CopyTo。</summary>
        public T* GetUnsafePtr()
        {
            if (_chunks != null) throw new InvalidOperationException("The column is not contiguous.");
            return _flat;
        }

        public void CopyTo(T* destination, int count)
        {
            if ((uint)count > (uint)Length) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count;)
            {
                int n = _chunks == null ? count - i : Math.Min(count - i, _capacity - i % _capacity);
                long bytes = (long)n * sizeof(T);
                Buffer.MemoryCopy(GetElementPtr(i), destination + i, bytes, bytes);
                i += n;
            }
        }

        public void CopyTo(NativeArray<T> destination, int count)
        {
            if ((uint)count > (uint)destination.Length) throw new ArgumentOutOfRangeException(nameof(count));
            CopyTo((T*)destination.GetUnsafePtr(), count);
        }

        public void CopyFrom(T* source, int count)
        {
            if ((uint)count > (uint)Length) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count;)
            {
                int n = _chunks == null ? count - i : Math.Min(count - i, _capacity - i % _capacity);
                long bytes = (long)n * sizeof(T);
                Buffer.MemoryCopy(source + i, GetElementPtr(i), bytes, bytes);
                i += n;
            }
        }

        public T[] ToArray()
        {
            var values = new T[Length];
            fixed (T* destination = values) CopyTo(destination, Length);
            return values;
        }

        /// <summary>仅清除此视图，数据与块表由原拥有者释放。</summary>
        public void Dispose() => this = default;
    }
}
