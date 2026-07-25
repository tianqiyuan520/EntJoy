using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    
    public struct MathFuncs_CSharp_Job : IJob
    {
        public NativeArray<float> A, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) { float x = A[idx]; Result[idx] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
        }
    }

    
    public struct MathFuncs_CSharp_For : IJobFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
        }
    }

    
    public struct MathFuncs_CSharp_PF : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct MathFuncs_Cpp_Job : IJob
    {
        public NativeArray<float> A, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) { float x = A[idx]; Result[idx] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct MathFuncs_Cpp_For : IJobFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct MathFuncs_Cpp_PF : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct MathFuncs_SIMD_Job : IJob
    {
        public NativeArray<float> A, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) { float x = A[idx]; Result[idx] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct MathFuncs_SIMD_For : IJobFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct MathFuncs_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct MathFuncs_ISPC_Job : IJob
    {
        public NativeArray<float> A, Result;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) { float x = A[idx]; Result[idx] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct MathFuncs_ISPC_For : IJobFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct MathFuncs_ISPC_PF : IJobParallelFor
    {
        public NativeArray<float> A, Result;
        public void Execute(int i)
        {
            float x = A[i]; Result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
        }
    }

    // ── Static function variants ──
    public static class MathFuncs_StaticFuncs
    {
        public static void MathFuncs_Stc_CSharp(
            NativeArray<float> a, NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) { float x = a[i]; result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
        }

        [NativeTranspiler.NativeTranspile]
        public static void MathFuncs_Stc_Cpp(
            NativeArray<float> a, NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) { float x = a[i]; result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
        }

        [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
        public static void MathFuncs_Stc_SIMD(
            NativeArray<float> a, NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) { float x = a[i]; result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
        }

        [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
        public static void MathFuncs_Stc_ISPC(
            NativeArray<float> a, NativeArray<float> result, int count)
        {
            for (int i = 0; i < count; i++) { float x = a[i]; result[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1); }
        }
    }
}
