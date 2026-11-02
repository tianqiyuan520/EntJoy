using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public struct SimdMaskedIncrementJob : IJobParallelFor
    {
        public NativeArray<int> Output;
        public void Execute(int index)
        {
            int value = 0;
            if (index % 2 == 0) value++;
            else --value;
            Output[index] = value;
        }
    }

    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public struct SimdMaximumBoundJob : IJobParallelFor
    {
        public NativeArray<int> Output;
        public void Execute(int index)
        {
            int visits = index;
            for (int k = 2147483646; k <= 2147483647; k++)
            {
                visits++;
                if (k == 2147483647 - index % 2) break;
            }
            Output[index] = visits;
        }
    }

    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public struct SimdMutableMinBoundJob : IJobParallelFor
    {
        public NativeArray<int> Output;
        public void Execute(int index)
        {
            int end = 4, best = index - 100;
            for (int k = 0; k < end; k++)
            {
                int value = index + k;
                if (value > best) best = value;
                end--;
            }
            Output[index] = best;
        }
    }

    [NativeTranspile]
    public struct IndexedAdapterJob : IJobFor
    {
        public NativeArray<int> Output;
        public bool Enabled;
        public int Bias;
        public void Execute(int index)
        {
            if (Enabled) Output[index] = index + Bias;
            else Output[index] = Bias - index;
        }
    }

    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public struct SimdNestedContinueJob : IJobParallelFor
    {
        public NativeArray<int> Output;
        public void Execute(int index)
        {
            int sum = index;
            for (int k = -1; k < 2; k++)
            for (int j = -1; j < 2; j++)
            {
                if (j + 1 == index % 3) { sum += 100; continue; }
                sum += j + k;
            }
            Output[index] = sum;
        }
    }

    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public struct SimdFixedBreakJob : IJobParallelFor
    {
        public NativeArray<int> Output;
        public void Execute(int index)
        {
            int sum = index;
            for (int k = 0; k < 4; k++)
            {
                if (k == index % 4) { sum += 100; break; }
                sum += k + 1;
            }
            Output[index] = sum;
        }
    }

    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public struct SimdFixedContinueJob : IJobParallelFor
    {
        public NativeArray<int> Output;
        public void Execute(int index)
        {
            int sum = index;
            for (int k = 0; k < 4; k++)
            {
                if (k == index % 4) { sum += 100; continue; }
                sum += k + 1;
            }
            Output[index] = sum;
        }
    }

    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public struct SimdFixedReturnJob : IJobParallelFor
    {
        public NativeArray<int> Output;
        public void Execute(int index)
        {
            int sum = index;
            for (int k = 0; k < 4; k++)
            {
                if (k == index % 4) { Output[index] = sum; return; }
                sum += k + 1;
            }
            Output[index] = -1;
        }
    }
}
