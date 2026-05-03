using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.Collections
{
    public static unsafe class UnsafeUtility
    {
        /// <summary>分配内存，对于 Temp/TempJob 需要提供安全句柄索引。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* Malloc(int size, Allocator allocator, int safetyHandleIndex = -1)
        {
            switch (allocator)
            {
                case Allocator.Persistent:
                    return (void*)Marshal.AllocHGlobal(size);
                case Allocator.Temp:
                case Allocator.TempJob:
                    if (safetyHandleIndex < 0)
                        throw new ArgumentException("Temp allocator requires a valid safety handle index.");
                    return (void*)TempAllocator.Alloc(size, safetyHandleIndex);
                default:
                    throw new ArgumentException($"Invalid allocator: {allocator}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Free(void* buffer, Allocator allocator)
        {
            if (buffer == null) return;
            switch (allocator)
            {
                case Allocator.Persistent:
                    Marshal.FreeHGlobal((IntPtr)buffer);
                    break;
                case Allocator.Temp:
                case Allocator.TempJob:
                    TempAllocator.Free((IntPtr)buffer);
                    break;
                default:
                    throw new ArgumentException($"Invalid allocator: {allocator}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void MemCpy(void* dest, void* src, long size) =>
            Buffer.MemoryCopy(src, dest, size, size);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void MemSet(void* dest, byte value, long size) =>
            Unsafe.InitBlockUnaligned(dest, value, (uint)size);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void MemClear(void* dest, long size) => MemSet(dest, 0, size);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static T ReadArrayElement<T>(void* source, int index) where T : unmanaged =>
            Unsafe.Read<T>((byte*)source + index * sizeof(T));

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void WriteArrayElement<T>(void* dest, int index, T value) where T : unmanaged =>
            Unsafe.Write((byte*)dest + index * sizeof(T), value);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void* AddressOf<T>(ref T value) where T : unmanaged =>
            Unsafe.AsPointer(ref value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T AsRef<T>(void* ptr) where T : unmanaged =>
            ref Unsafe.AsRef<T>(ptr);

        public static bool IsUnmanaged<T>() => RuntimeHelpers.IsReferenceOrContainsReferences<T>() == false;

        /// <summary>返回指向数组元素指定索引的引用，不进行边界检查。</summary>
        /// <param name="source">指向数组起始位置的指针。</param>
        /// <param name="index">元素索引。</param>
        /// <typeparam name="T">元素类型，必须是 unmanaged。</typeparam>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T ArrayElementAsRef<T>(void* source, int index) where T : unmanaged
        {
            return ref Unsafe.AsRef<T>((byte*)source + index * sizeof(T));
        }
    }
}