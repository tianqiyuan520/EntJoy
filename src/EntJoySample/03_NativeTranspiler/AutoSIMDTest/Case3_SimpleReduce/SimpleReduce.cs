// Case 3: 简单归约 — if (a[i] < best) best = a[i];
// 测试 SIMD blend/归约模式

using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    public struct SimpleReduce_CSharp : IJobParallelFor
    {
        public NativeArray<float> A;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            float best = float.MaxValue;
            for (int j = 0; j < 100; j++)
            {
                float v = A[i * 100 + j];
                if (v < best) best = v;
            }
            Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct SimpleReduce_Cpp : IJobParallelFor
    {
        public NativeArray<float> A;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            float best = float.MaxValue;
            for (int j = 0; j < 100; j++)
            {
                float v = A[i * 100 + j];
                if (v < best) best = v;
            }
            Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleReduce_SIMD : IJobFor
    {
        public NativeArray<float> A;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            float best = float.MaxValue;
            for (int j = 0; j < 100; j++)
            {
                float v = A[i * 100 + j];
                if (v < best) best = v;
            }
            Result[i] = best;
        }
    }

    // ─── IJob 版本：1500 次迭代的内循环 ───
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleReduce_SIMD_IJob : IJob
    {
        public NativeArray<float> A;
        public NativeArray<float> Result;
        public int Count;
        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                float best = float.MaxValue;
                for (int j = 0; j < 100; j++)
                {
                    float v = A[i * 100 + j];
                    if (v < best) best = v;
                }
                Result[i] = best;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct SimpleReduce_ISPC : IJobParallelFor
    {
        public NativeArray<float> A;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            float best = float.MaxValue;
            for (int j = 0; j < 100; j++)
            {
                float v = A[i * 100 + j];
                if (v < best) best = v;
            }
            Result[i] = best;
        }
    }
}
