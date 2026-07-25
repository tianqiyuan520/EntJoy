using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    
    public struct SimpleReduce_CSharp_Job : IJob
    {
        public NativeArray<float> A, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) { float b = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[idx * 100 + j]; if (v < b) b = v; } Result[idx] = b; }
        }
    }

    
    public struct SimpleReduce_CSharp_For : IJobFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best;
        }
    }

    
    public struct SimpleReduce_CSharp_PF : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct SimpleReduce_Cpp_Job : IJob
    {
        public NativeArray<float> A, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) { float b = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[idx * 100 + j]; if (v < b) b = v; } Result[idx] = b; }
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct SimpleReduce_Cpp_For : IJobFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct SimpleReduce_Cpp_PF : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleReduce_SIMD_Job : IJob
    {
        public NativeArray<float> A, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) { float b = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[idx * 100 + j]; if (v < b) b = v; } Result[idx] = b; }
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleReduce_SIMD_For : IJobFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleReduce_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct SimpleReduce_ISPC_Job : IJob
    {
        public NativeArray<float> A, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) { float b = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[idx * 100 + j]; if (v < b) b = v; } Result[idx] = b; }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct SimpleReduce_ISPC_For : IJobFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct SimpleReduce_ISPC_PF : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = A[i * 100 + j]; if (v < best) best = v; } Result[i] = best;
        }
    }

    // ── Static function variants ──
    public static class SimpleReduce_StaticFuncs
    {
        public static void SimpleReduce_Stc_CSharp(
            NativeArray<float> a, NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) { float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = a[i * 100 + j]; if (v < best) best = v; } result[i] = best; }
        }

        [NativeTranspiler.NativeTranspile]
        public static void SimpleReduce_Stc_Cpp(
            NativeArray<float> a, NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) { float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = a[i * 100 + j]; if (v < best) best = v; } result[i] = best; }
        }

        [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
        public static void SimpleReduce_Stc_SIMD(
            NativeArray<float> a, NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) { float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = a[i * 100 + j]; if (v < best) best = v; } result[i] = best; }
        }

        [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
        public static void SimpleReduce_Stc_ISPC(
            NativeArray<float> a, NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) { float best = float.MaxValue; for (int j = 0; j < 100; j++) { float v = a[i * 100 + j]; if (v < best) best = v; } result[i] = best; }
        }
    }
}
