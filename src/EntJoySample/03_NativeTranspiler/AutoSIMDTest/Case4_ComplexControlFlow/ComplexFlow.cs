using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    public struct ComplexFlow_CSharp : IJobParallelFor
    {
        public NativeArray<float> A, B, Result; public float Threshold;
        public void Execute(int i) { float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0; }
    }
    [NativeTranspiler.NativeTranspile]
    public struct ComplexFlow_Cpp : IJobParallelFor
    {
        public NativeArray<float> A, B, Result; public float Threshold;
        public void Execute(int i) { float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0; }
    }
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct ComplexFlow_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> A, B, Result; public float Threshold;
        public void Execute(int i) { float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0; }
    }
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct ComplexFlow_ISPC : IJobParallelFor
    {
        public NativeArray<float> A, B, Result; public float Threshold;
        public void Execute(int i) { float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0; }
    }
    // --- C++ Auto-SIMD (IJobFor) ---
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct ComplexFlow_SIMD_For : IJobFor
    {
        public NativeArray<float> A, B, Result; public float Threshold;
        public void Execute(int i) { float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0; }
    }

    // --- C++ Auto-SIMD (IJob) ---
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct ComplexFlow_SIMD_IJob : IJob
    {
        public NativeArray<float> A, B, Result; public float Threshold;
        public int Count;
        public void Execute()
        {
            for (int i = 0; i < Count; i++) { float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0; }
        }
    }
}
