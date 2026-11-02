using System.Runtime.CompilerServices;
using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    [NativeTranspile]
    public struct HelperInliningJob : IJobParallelFor
    {
        public NativeArray<int> Values;

        public void Execute(int index)
        {
            Values[index] = HelperInlining.Chain(index);
        }
    }

    public static class HelperInlining
    {
        public static int Chain(int value) { return Ordinary(Forced(Outlined(value))); }

        public static int Ordinary(int value) { return value * 3 + 1; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Forced(int value) { return value + 7; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Outlined(int value) { return value * 2; }
    }
}
