//using EntJoy.Collections;
//using EntJoy.JobSystem;

//namespace EntJoySample.AutoSIMDTest
//{
    
//    public struct ComplexFlow_CSharp_Job : IJob
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public int Count;
//        public void Execute()
//        {
//            for (int idx = 0; idx < Count; idx++) { float v = A[idx]; if (v > Threshold) Result[idx] = v * B[idx]; else if (v < -Threshold) Result[idx] = v + B[idx]; else Result[idx] = 0; }
//        }
//    }

    
//    public struct ComplexFlow_CSharp_For : IJobFor
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public void Execute(int i)
//        {
//            float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0;
//        }
//    }

    
//    public struct ComplexFlow_CSharp_PF : IJobParallelFor
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public void Execute(int i)
//        {
//            float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0;
//        }
//    }

//    [NativeTranspiler.NativeTranspile]
//    public struct ComplexFlow_Cpp_Job : IJob
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public int Count;
//        public void Execute()
//        {
//            for (int idx = 0; idx < Count; idx++) { float v = A[idx]; if (v > Threshold) Result[idx] = v * B[idx]; else if (v < -Threshold) Result[idx] = v + B[idx]; else Result[idx] = 0; }
//        }
//    }

//    [NativeTranspiler.NativeTranspile]
//    public struct ComplexFlow_Cpp_For : IJobFor
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public void Execute(int i)
//        {
//            float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0;
//        }
//    }

//    [NativeTranspiler.NativeTranspile]
//    public struct ComplexFlow_Cpp_PF : IJobParallelFor
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public void Execute(int i)
//        {
//            float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0;
//        }
//    }

//    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
//    public struct ComplexFlow_SIMD_Job : IJob
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public int Count;
//        public void Execute()
//        {
//            for (int idx = 0; idx < Count; idx++) { float v = A[idx]; if (v > Threshold) Result[idx] = v * B[idx]; else if (v < -Threshold) Result[idx] = v + B[idx]; else Result[idx] = 0; }
//        }
//    }

//    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
//    public struct ComplexFlow_SIMD_For : IJobFor
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public void Execute(int i)
//        {
//            float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0;
//        }
//    }

//    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
//    public struct ComplexFlow_SIMD_PF : IJobParallelFor
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public void Execute(int i)
//        {
//            float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0;
//        }
//    }

//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
//    public struct ComplexFlow_ISPC_Job : IJob
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public int Count;
//        public void Execute()
//        {
//            for (int idx = 0; idx < Count; idx++) { float v = A[idx]; if (v > Threshold) Result[idx] = v * B[idx]; else if (v < -Threshold) Result[idx] = v + B[idx]; else Result[idx] = 0; }
//        }
//    }

//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
//    public struct ComplexFlow_ISPC_For : IJobFor
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public void Execute(int i)
//        {
//            float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0;
//        }
//    }

//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
//    public struct ComplexFlow_ISPC_PF : IJobParallelFor
//    {
//        public NativeArray<float> A, B, Result; public float Threshold;
//        public void Execute(int i)
//        {
//            float v = A[i]; if (v > Threshold) Result[i] = v * B[i]; else if (v < -Threshold) Result[i] = v + B[i]; else Result[i] = 0;
//        }
//    }

//    // 鈹€鈹€ Static function variants 鈹€鈹€
//    public static class ComplexFlow_StaticFuncs
//    {
//        public static void ComplexFlow_Stc_CSharp(
//            NativeArray<float> a, NativeArray<float> b, NativeArray<float> result, float threshold, int count)
//        {
//            for (int i = 0; i < count; i++) { float v = a[i]; if (v > threshold) result[i] = v * b[i]; else if (v < -threshold) result[i] = v + b[i]; else result[i] = 0; }
//        }

//        [NativeTranspiler.NativeTranspile]
//        public static void ComplexFlow_Stc_Cpp(
//            NativeArray<float> a, NativeArray<float> b, NativeArray<float> result, float threshold, int count)
//        {
//            for (int i = 0; i < count; i++) { float v = a[i]; if (v > threshold) result[i] = v * b[i]; else if (v < -threshold) result[i] = v + b[i]; else result[i] = 0; }
//        }

//        [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
//        public static void ComplexFlow_Stc_SIMD(
//            NativeArray<float> a, NativeArray<float> b, NativeArray<float> result, float threshold, int count)
//        {
//            for (int i = 0; i < count; i++) { float v = a[i]; if (v > threshold) result[i] = v * b[i]; else if (v < -threshold) result[i] = v + b[i]; else result[i] = 0; }
//        }

//        [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
//        public static void ComplexFlow_Stc_ISPC(
//            NativeArray<float> a, NativeArray<float> b, NativeArray<float> result, float threshold, int count)
//        {
//            for (int i = 0; i < count; i++) { float v = a[i]; if (v > threshold) result[i] = v * b[i]; else if (v < -threshold) result[i] = v + b[i]; else result[i] = 0; }
//        }
//    }
//}
