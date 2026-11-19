using EntJoy.Collections;
using EntJoy.JobSystem;
using EntJoy.Mathematics;

namespace EntJoySample.AutoSIMDTest
{
    /// <summary>pair 元素基准：C# 托管执行（对照值）。</summary>
    public struct PairArith_CSharp_PF : IJobParallelFor
    {
        public NativeArray<uint2> Input;
        public NativeArray<uint2> Result;
        public int Mode;

        public void Execute(int i)
        {
            uint2 a = Input[i];
            uint r = a.x * 3u + (a.y >> 1);
            if (Mode == 1) r = r / 7u;
            else if (Mode == 2 && a.x > 0x80000000u) r = a.x % 5u + 1u;
            Result[i] = new uint2(r, a.y ^ a.x);
        }
    }

    /// <summary>同一算式走 AutoSIMD：pair 用 int2 寄存器，分量除法/取模/移位走无符号 helper。</summary>
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct PairArith_SIMD_PF : IJobParallelFor
    {
        public NativeArray<uint2> Input;
        public NativeArray<uint2> Result;
        public int Mode;

        public void Execute(int i)
        {
            uint2 a = Input[i];
            uint r = a.x * 3u + (a.y >> 1);
            if (Mode == 1) r = r / 7u;
            else if (Mode == 2 && a.x > 0x80000000u) r = a.x % 5u + 1u;
            Result[i] = new uint2(r, a.y ^ a.x);
        }
    }
}
