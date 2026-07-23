using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    // ─── C# 基线 ───
    public struct SimpleArith_CSharp : IJobParallelFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i) { Result[i] = A[i] * B[i] + C[i]; }
    }
    // ─── C++ 标量 (并行) ───
    [NativeTranspiler.NativeTranspile]
    public struct SimpleArith_Cpp : IJobParallelFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i) { Result[i] = A[i] * B[i] + C[i]; }
    }
    // ─── C++ Auto-SIMD (并行) ───
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleArith_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i) { Result[i] = A[i] * B[i] + C[i]; }
    }
    // ─── ISPC (并行) ───
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct SimpleArith_ISPC : IJobParallelFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i) { Result[i] = A[i] * B[i] + C[i]; }
    }
    // --- C++ Auto-SIMD (IJobFor) ---
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleArith_SIMD_For : IJobFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i) { Result[i] = A[i] * B[i] + C[i]; }
    }

    // --- C++ Auto-SIMD (IJob) ---
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleArith_SIMD_IJob : IJob
    {
        public NativeArray<float> A, B, C, Result;
        public int Count;
        public void Execute()
        {
            for (int i = 0; i < Count; i++) { Result[i] = A[i] * B[i] + C[i]; }
        }
    }
}
