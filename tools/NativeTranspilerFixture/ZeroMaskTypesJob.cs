using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    public enum SmallMask : byte { A = 1, B = 2, C = 128 }
    public enum WideMask : ulong { A = 1, B = 2, C = 1UL << 63 }

    [NativeTranspile]
    public struct SmallZeroMaskJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public byte Padding;
        public short Flags;
        public uint Sentinel;

        public void Execute(int index)
        {
            int sum = index;
            for (int k = 0; k < 3; k++)
            {
                if ((Flags & 1) == 0) sum += 1;
                if ((Flags & 2) == 0) sum += 2;
                if ((Flags & -32768) == 0) sum += 4;
            }
            if (Flags != 0) sum += 8;
            Values[index] = sum;
        }
    }

    [NativeTranspile]
    public struct UnsignedZeroMaskJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public byte Padding;
        public uint Flags;
        public uint Sentinel;

        public void Execute(int index)
        {
            int sum = index;
            for (int k = 0; k < 3; k++)
            {
                if ((Flags & 1u) == 0) sum += 1;
                if ((Flags & 2u) == 0) sum += 2;
                if ((Flags & 0x80000000u) == 0) sum += 4;
            }
            if (Flags != 0) sum += 8;
            Values[index] = sum;
        }
    }

    [NativeTranspile]
    public struct SignedWideZeroMaskJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public byte Padding;
        public long Flags;
        public uint Sentinel;

        public void Execute(int index)
        {
            int sum = index;
            for (int k = 0; k < 3; k++)
            {
                if ((Flags & 1L) == 0) sum += 1;
                if ((Flags & 2L) == 0) sum += 2;
                if ((Flags & (-9223372036854775807L - 1L)) == 0) sum += 4;
            }
            if (Flags != 0) sum += 8;
            Values[index] = sum;
        }
    }

    [NativeTranspile]
    public struct WideZeroMaskJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public byte Padding;
        public ulong Flags;
        public uint Sentinel;

        public void Execute(int index)
        {
            int sum = index;
            for (int k = 0; k < 3; k++)
            {
                if ((Flags & 1UL) == 0) sum += 1;
                if ((Flags & 2UL) == 0) sum += 2;
                if ((Flags & 0x8000000000000000UL) == 0) sum += 4;
            }
            if (Flags != 0) sum += 8;
            Values[index] = sum;
        }
    }

    [NativeTranspile]
    public struct SmallEnumZeroMaskJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public byte Padding;
        public SmallMask Flags;
        public uint Sentinel;

        public void Execute(int index)
        {
            int sum = index;
            for (int k = 0; k < 3; k++)
            {
                if ((Flags & SmallMask.A) == 0) sum += 1;
                if ((Flags & SmallMask.B) == 0) sum += 2;
                if ((Flags & SmallMask.C) == 0) sum += 4;
            }
            if (((~Flags) == (SmallMask)255) != (Flags == 0)) sum = int.MinValue;
            if (Flags != 0) sum += 8;
            Values[index] = sum;
        }
    }

    [NativeTranspile]
    public struct WideEnumZeroMaskJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public byte Padding;
        public WideMask Flags;
        public uint Sentinel;

        public void Execute(int index)
        {
            int sum = index;
            for (int k = 0; k < 3; k++)
            {
                if ((Flags & WideMask.A) == 0) sum += 1;
                if ((Flags & WideMask.B) == 0) sum += 2;
                if ((Flags & WideMask.C) == 0) sum += 4;
            }
            if (Flags != 0) sum += 8;
            Values[index] = sum;
        }
    }

    [NativeTranspile]
    public struct NativeZeroMaskJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public byte Padding;
        public nuint Flags;
        public uint Sentinel;

        public void Execute(int index)
        {
            int sum = index;
            for (int k = 0; k < 3; k++)
            {
                if ((Flags & (nuint)1) == 0) sum += 1;
                if ((Flags & (nuint)2) == 0) sum += 2;
                if ((Flags & (nuint)4) == 0) sum += 4;
            }
            if (Flags != 0) sum += 8;
            Values[index] = sum;
        }
    }
}
