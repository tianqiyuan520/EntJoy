using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 组件值的字节搬运。长度在运行时才确定，<see cref="Unsafe.CopyBlock"/> 对任意长度都落到 memmove 调用；
    /// 结构变更逐实体搬组件列时，几字节的组件付这个调用开销不划算，小尺寸改走定长字搬运。
    /// 调用方保证源与目标不重叠（组件列之间、列与栈上临时值之间都不重叠）。
    /// </summary>
    internal static unsafe class BufferCopy
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Copy(void* dst, void* src, int size)
        {
            switch (size)
            {
                case 4: Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<uint>(src)); return;
                case 8: Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<ulong>(src)); return;
                case 1: *(byte*)dst = *(byte*)src; return;
                case 2: Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<ushort>(src)); return;
                case 12:
                    Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<ulong>(src));
                    Unsafe.WriteUnaligned((byte*)dst + 8, Unsafe.ReadUnaligned<uint>((byte*)src + 8));
                    return;
                case 16:
                    Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<ulong>(src));
                    Unsafe.WriteUnaligned((byte*)dst + 8, Unsafe.ReadUnaligned<ulong>((byte*)src + 8));
                    return;
                default: Unsafe.CopyBlock(dst, src, (uint)size); return;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Zero(void* dst, int size)
        {
            switch (size)
            {
                case 4: Unsafe.WriteUnaligned(dst, 0u); return;
                case 8: Unsafe.WriteUnaligned(dst, 0ul); return;
                case 1: *(byte*)dst = 0; return;
                case 2: Unsafe.WriteUnaligned(dst, (ushort)0); return;
                case 12:
                    Unsafe.WriteUnaligned(dst, 0ul);
                    Unsafe.WriteUnaligned((byte*)dst + 8, 0u);
                    return;
                case 16:
                    Unsafe.WriteUnaligned(dst, 0ul);
                    Unsafe.WriteUnaligned((byte*)dst + 8, 0ul);
                    return;
                default: Unsafe.InitBlock(dst, 0, (uint)size); return;
            }
        }
    }
}
