using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    public struct SimpleArith_CSharp : IJobParallelFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i) { Result[i] = A[i] * B[i] + C[i]; }
    }

    [NativeTranspiler.NativeTranspile]
    public struct SimpleArith_Cpp : IJobParallelFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i) { Result[i] = A[i] * B[i] + C[i]; }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleArith_SIMD : IJobFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i) { Result[i] = A[i] * B[i] + C[i]; }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleArith_SIMD_IJob : IJob
    {
        public NativeArray<float> A, B, C, Result;
        public int Count;
        public void Execute()
        {
            for (int i = 0; i < Count; i++)
                Result[i] = A[i] * B[i] + C[i];
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct SimpleArith_ISPC : IJobParallelFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i) { Result[i] = A[i] * B[i] + C[i]; }
    }
}
