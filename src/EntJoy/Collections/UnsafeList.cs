using System;
using System.Runtime.CompilerServices;

namespace EntJoy.Collections
{
    /// <summary>
    /// 堆上分配的底层列表结构，包含实际数据。
    /// 该结构不包含安全检查，由外层的 NativeList 负责。
    /// </summary>
    public unsafe struct UnsafeList<T> where T : unmanaged
    {
        public T* Ptr;          // 数据缓冲区指针
        public int Length;       // 当前元素个数
        public int Capacity;     // 容量
        public Allocator Allocator; // 分配器类型

        public UnsafeList(int initialCapacity, Allocator allocator)
        {
            if (initialCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));

            int size = initialCapacity * sizeof(T);
            Ptr = (T*)UnsafeUtility.Malloc(size, allocator, -1); // 底层内存不需要安全句柄索引（-1）
            Length = 0;
            Capacity = initialCapacity;
            Allocator = allocator;
        }

        public void Dispose()
        {
            if (Ptr != null)
            {
                UnsafeUtility.Free(Ptr, Allocator);
                Ptr = null;
                Length = 0;
                Capacity = 0;
            }
        }

        public void EnsureCapacity(int min)
        {
            if (Capacity >= min) return;

            int newCapacity = Math.Max(min, Capacity * 2);
            if (newCapacity < 4) newCapacity = 4;

            int newSize = newCapacity * sizeof(T);
            T* newPtr = (T*)UnsafeUtility.Malloc(newSize, Allocator, -1);
            if (Ptr != null)
            {
                UnsafeUtility.MemCpy(newPtr, Ptr, Length * sizeof(T));
                UnsafeUtility.Free(Ptr, Allocator);
            }
            Ptr = newPtr;
            Capacity = newCapacity;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T value)
        {
            EnsureCapacity(Length + 1);
            Ptr[Length++] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Insert(int index, T value)
        {
            if ((uint)index > (uint)Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            EnsureCapacity(Length + 1);
            if (index < Length)
            {
                // 将 index 开始的元素向后移动一位
                void* src = (byte*)Ptr + index * sizeof(T);
                void* dst = (byte*)src + sizeof(T);
                int elementsToMove = Length - index;
                UnsafeUtility.MemCpy(dst, src, elementsToMove * sizeof(T));
            }
            Ptr[index] = value;
            Length++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAt(int index)
        {
            if ((uint)index >= (uint)Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            Length--;
            if (index < Length)
            {
                void* src = (byte*)Ptr + (index + 1) * sizeof(T);
                void* dst = (byte*)Ptr + index * sizeof(T);
                int elementsToMove = Length - index;
                UnsafeUtility.MemCpy(dst, src, elementsToMove * sizeof(T));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAtSwapBack(int index)
        {
            if ((uint)index >= (uint)Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            Length--;
            if (index < Length)
            {
                Ptr[index] = Ptr[Length];
            }
        }

        public void Resize(int newLength, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            if (newLength < 0)
                throw new ArgumentOutOfRangeException(nameof(newLength));

            if (newLength > Capacity)
            {
                EnsureCapacity(newLength);
            }

            if (newLength > Length && options == NativeArrayOptions.ClearMemory)
            {
                int newElements = newLength - Length;
                byte* start = (byte*)Ptr + Length * sizeof(T);
                UnsafeUtility.MemClear(start, newElements * sizeof(T));
            }

            Length = newLength;
        }
    }
}