using EntJoy.Collections;
using EntJoy.JobSystem;
using System;

namespace EntJoySample.AutoSIMDTest
{
    // 按 body 拆解 SIMD 收益：同一输入、同一调度，只改计算量。
    // 每组都有原生标量（[NativeTranspile]）与原生 SIMD（AutoSIMD.Enabled）两版。

    [NativeTranspiler.NativeTranspile]
    public struct BodyArith_Scalar_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            Result[i] = Input[i] * 2.5f + 3.0f;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct BodyArith_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            Result[i] = Input[i] * 2.5f + 3.0f;
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct BodySin_Scalar_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            Result[i] = MathF.Sin(Input[i]);
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct BodySin_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            Result[i] = MathF.Sin(Input[i]);
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct BodySinCos_Scalar_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            Result[i] = MathF.Sin(Input[i]) + MathF.Cos(Input[i]);
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct BodySinCos_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            Result[i] = MathF.Sin(Input[i]) + MathF.Cos(Input[i]);
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct BodySinCosLog_Scalar_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            float x = Input[i];
            Result[i] = MathF.Sin(x) + MathF.Cos(x) + MathF.Log(MathF.Abs(x) + 1.0f);
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct BodySinCosLog_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            float x = Input[i];
            Result[i] = MathF.Sin(x) + MathF.Cos(x) + MathF.Log(MathF.Abs(x) + 1.0f);
        }
    }
}
