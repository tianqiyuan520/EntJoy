using EntJoy.Collections;
using EntJoy.JobSystem;
using System;

namespace EntJoySample.AutoSIMDTest
{
    /// <summary>性能分解用基准：托管执行。</summary>
    public struct PerfSplit_CSharp_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;

        public void Execute(int i)
        {
            float x = Input[i];
            Result[i] = MathF.Sin(x) + MathF.Cos(x) * 0.5f + MathF.Log(MathF.Abs(x) + 1.0f) + x * 0.25f;
        }
    }

    /// <summary>同一算式生成原生标量内核（不向量化），用于把「托管开销」与「SIMD」分开。</summary>
    [NativeTranspiler.NativeTranspile]
    public struct PerfSplit_Scalar_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;

        public void Execute(int i)
        {
            float x = Input[i];
            Result[i] = MathF.Sin(x) + MathF.Cos(x) * 0.5f + MathF.Log(MathF.Abs(x) + 1.0f) + x * 0.25f;
        }
    }

    /// <summary>同一算式走 AutoSIMD 向量化。</summary>
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct PerfSplit_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> Input;
        public NativeArray<float> Result;

        public void Execute(int i)
        {
            float x = Input[i];
            Result[i] = MathF.Sin(x) + MathF.Cos(x) * 0.5f + MathF.Log(MathF.Abs(x) + 1.0f) + x * 0.25f;
        }
    }
}
