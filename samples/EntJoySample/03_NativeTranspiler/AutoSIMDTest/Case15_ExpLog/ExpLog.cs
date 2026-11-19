using EntJoy.Collections;
using EntJoy.JobSystem;
using System;

namespace EntJoySample.AutoSIMDTest
{
    /// <summary>超越函数基准：C# 托管执行（对照值）。</summary>
    public struct ExpLog_CSharp_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;

        public void Execute(int i)
        {
            float x = Input[i];
            Result[i] = MathF.Exp(x) + MathF.Log10(MathF.Abs(x) + 1.0f);
        }
    }

    /// <summary>同一算式走 AutoSIMD：exp/log10 命中内联多项式，不再逐 lane 调 libm。</summary>
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct ExpLog_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;

        public void Execute(int i)
        {
            float x = Input[i];
            Result[i] = MathF.Exp(x) + MathF.Log10(MathF.Abs(x) + 1.0f);
        }
    }
}
