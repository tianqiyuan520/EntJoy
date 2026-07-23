using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    public struct SimpleReduce_CSharp : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i) { float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best; }
    }
    [NativeTranspiler.NativeTranspile]
    public struct SimpleReduce_Cpp : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i) { float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best; }
    }
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleReduce_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i) { float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best; }
    }
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct SimpleReduce_ISPC : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i) { float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best; }
    }
}
