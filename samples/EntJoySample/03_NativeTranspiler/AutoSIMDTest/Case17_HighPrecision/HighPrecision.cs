using EntJoy.Collections;
using EntJoy.JobSystem;
using System;

namespace EntJoySample.AutoSIMDTest
{
    /// <summary>High 精度档基准：C# 托管执行（对照值）。</summary>
    public struct HighPrecision_CSharp_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;

        public void Execute(int i)
        {
            float x = Input[i];
            Result[i] = MathF.Sin(x) + MathF.Cos(x) * 0.5f + MathF.Log(MathF.Abs(x) + 1.0f)
                      + MathF.Atan(x) + MathF.Tanh(x) + MathF.Pow(1.5f, x);
        }
    }

    /// <summary>同一算式走 High 档：double 中间精度实现，目标 ≤1 ULP。</summary>
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled,
                                      MathPrecision = NativeTranspiler.SimdMathPrecision.High)]
    public struct HighPrecision_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;

        public void Execute(int i)
        {
            float x = Input[i];
            Result[i] = MathF.Sin(x) + MathF.Cos(x) * 0.5f + MathF.Log(MathF.Abs(x) + 1.0f)
                      + MathF.Atan(x) + MathF.Tanh(x) + MathF.Pow(1.5f, x);
        }
    }
}
