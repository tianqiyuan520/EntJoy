using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    public struct MathFuncs_CSharp : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i) { float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
    }
    [NativeTranspiler.NativeTranspile]
    public struct MathFuncs_Cpp : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i) { float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
    }
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct MathFuncs_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i) { float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
    }
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct MathFuncs_ISPC : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i) { float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
    }
}
