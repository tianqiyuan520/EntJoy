using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    [NativeTranspile]
    public struct SmallFixedLoopJob : IJobParallelFor
    {
        public NativeArray<int> Input;
        public NativeArray<int> Output;
        public NativeArray<int> Visits;

        public void Execute(int index)
        {
            int sum = 0, visits = 0;
            for (int k = -1; k <= 1; k++)
            {
                if (k == 0) continue;
                if (Input[index] == k) break;
                sum += Input[index] + k;
                visits++;
            }
            Output[index] = sum;
            Visits[index] = visits;
        }
    }
}
