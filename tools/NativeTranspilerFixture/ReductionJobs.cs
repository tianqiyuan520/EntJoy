using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    [NativeTranspile]
    public struct SeededReductionJob : IJobParallelFor
    {
        public NativeArray<int> Input, Output;
        public int Count, Seed;

        public void Execute(int index)
        {
            int sum = Seed + index;
            for (int k = 0; k < Count; k++) sum += Input[k];
            Output[index] = sum;
        }
    }

    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public struct SimdSeededReductionJob : IJobParallelFor
    {
        public NativeArray<int> Input, Output;
        public int Count, Seed;

        public void Execute(int index)
        {
            int sum = Seed + index;
            for (int k = 0; k < Count; k++) sum += Input[k];
            Output[index] = sum;
        }
    }

    public static class ReductionHelpers
    {
        public static int Subtract(int left, int right) => left - right;
    }

    [NativeTranspile]
    public struct OrderedReductionJob : IJobParallelFor
    {
        public NativeArray<int> Input, Output;
        public int Count, Seed;

        public void Execute(int index)
        {
            int sum = Seed + index;
            for (int k = 0; k < Count; k++) sum = ReductionHelpers.Subtract(sum, Input[k]);
            Output[index] = sum;
        }
    }

    [NativeTranspile]
    public struct MinMaxReductionJob : IJobParallelFor
    {
        public NativeArray<int> Input, Minimum, Maximum;
        public int Count, Seed;

        public void Execute(int index)
        {
            int minimum = Seed + index, maximum = Seed + index;
            for (int k = 0; k < Count; k++)
            {
                minimum = System.Math.Min(minimum, Input[k]);
                maximum = System.Math.Max(maximum, Input[k]);
            }
            Minimum[index] = minimum;
            Maximum[index] = maximum;
        }
    }

    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public struct SimdMutableReductionBoundJob : IJobParallelFor
    {
        public NativeArray<int> Output;
        public int Count, Seed;

        public void Execute(int index)
        {
            int end = Count, sum = Seed + index;
            for (int k = 0; k < end; k++)
            {
                sum += k;
                end--;
            }
            Output[index] = sum;
        }
    }
}
