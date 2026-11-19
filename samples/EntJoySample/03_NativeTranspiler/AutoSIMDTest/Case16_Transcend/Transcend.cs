using EntJoy.Collections;
using EntJoy.JobSystem;
using System;

namespace EntJoySample.AutoSIMDTest
{
    /// <summary>组合超越函数基准：C# 托管执行（对照值）。</summary>
    public struct Transcend_CSharp_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;

        public void Execute(int i)
        {
            float x = Input[i];
            float h = x * 0.5f;
            Result[i] = MathF.Tan(x) + MathF.Atan(x) + MathF.Asin(h) + MathF.Acos(h)
                      + MathF.Atan2(x, 2.0f) + MathF.Sinh(x) + MathF.Cosh(x)
                      + MathF.Tanh(x) + MathF.Pow(2.0f, x);
        }
    }

    /// <summary>同一算式走 AutoSIMD：九个函数都命中内联实现，不再逐 lane 调 libm。</summary>
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct Transcend_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;

        public void Execute(int i)
        {
            float x = Input[i];
            float h = x * 0.5f;
            Result[i] = MathF.Tan(x) + MathF.Atan(x) + MathF.Asin(h) + MathF.Acos(h)
                      + MathF.Atan2(x, 2.0f) + MathF.Sinh(x) + MathF.Cosh(x)
                      + MathF.Tanh(x) + MathF.Pow(2.0f, x);
        }
    }
}
