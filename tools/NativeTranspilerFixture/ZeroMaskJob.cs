using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    [NativeTranspile]
    public struct ZeroMaskJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public int Flags;
        public int B0, B1, B2, B3, B4, B5, B6, B7;

        public void Execute(int index)
        {
            Values[index] = -index;
            if ((index & 15) == 0) return;
            int mode = Flags;
            bool a = (mode & 1) == 0;
            bool b = (mode & 2) == 0;
            bool c = (mode & 4) == 0;
            int sum = B0 + B1 + B2 + B3 + B4 + B5 + B6 + B7;
            for (int k = 0; k < 8; k++)
            {
                if (a) sum += k;
                if (b) sum += 2;
                if (c) sum += 3;
            }
            Values[index] = sum;
        }
    }
}
