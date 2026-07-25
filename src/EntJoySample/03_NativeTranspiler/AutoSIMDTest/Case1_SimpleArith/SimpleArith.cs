using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    
    public struct SimpleArith_CSharp_Job : IJob
    {
        public NativeArray<float> A, B, C, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) Result[idx] = A[idx] * B[idx] + C[idx];
        }
    }

    
    public struct SimpleArith_CSharp_For : IJobFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i)
        {
            Result[i] = A[i] * B[i] + C[i];
        }
    }

    
    public struct SimpleArith_CSharp_PF : IJobParallelFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i)
        {
            Result[i] = A[i] * B[i] + C[i];
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct SimpleArith_Cpp_Job : IJob
    {
        public NativeArray<float> A, B, C, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) Result[idx] = A[idx] * B[idx] + C[idx];
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct SimpleArith_Cpp_For : IJobFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i)
        {
            Result[i] = A[i] * B[i] + C[i];
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct SimpleArith_Cpp_PF : IJobParallelFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i)
        {
            Result[i] = A[i] * B[i] + C[i];
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleArith_SIMD_Job : IJob
    {
        public NativeArray<float> A, B, C, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) Result[idx] = A[idx] * B[idx] + C[idx];
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleArith_SIMD_For : IJobFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i)
        {
            Result[i] = A[i] * B[i] + C[i];
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct SimpleArith_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i)
        {
            Result[i] = A[i] * B[i] + C[i];
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct SimpleArith_ISPC_Job : IJob
    {
        public NativeArray<float> A, B, C, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) Result[idx] = A[idx] * B[idx] + C[idx];
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct SimpleArith_ISPC_For : IJobFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i)
        {
            Result[i] = A[i] * B[i] + C[i];
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct SimpleArith_ISPC_PF : IJobParallelFor
    {
        public NativeArray<float> A, B, C, Result;
        public void Execute(int i)
        {
            Result[i] = A[i] * B[i] + C[i];
        }
    }

    // ── Static function variants ──
    public static class SimpleArith_StaticFuncs
    {
        public static void SimpleArith_Stc_CSharp(
            NativeArray<float> a, NativeArray<float> b, NativeArray<float> c,
            NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) result[i] = a[i] * b[i] + c[i];
        }

        [NativeTranspiler.NativeTranspile]
        public static void SimpleArith_Stc_Cpp(
            NativeArray<float> a, NativeArray<float> b, NativeArray<float> c,
            NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) result[i] = a[i] * b[i] + c[i];
        }

        [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
        public static void SimpleArith_Stc_SIMD(
            NativeArray<float> a, NativeArray<float> b, NativeArray<float> c,
            NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) result[i] = a[i] * b[i] + c[i];
        }

        [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
        public static void SimpleArith_Stc_ISPC(
            NativeArray<float> a, NativeArray<float> b, NativeArray<float> c,
            NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) result[i] = a[i] * b[i] + c[i];
        }
    }
}
