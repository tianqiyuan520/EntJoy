using EntJoy.Collections;
using EntJoy.JobSystem;
using EntJoy.Mathematics;
using NativeTranspiler;

namespace NativeTranspilerFixture;

/// <summary>uint2 的 SIMD 算术：pair 走 int2 寄存器，分量上的 / % &gt;&gt; 走无符号 helper。</summary>
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct Uint2SimdJob : IJobParallelFor
{
    public NativeArray<uint2> Input;
    public NativeArray<int> Output;

    public void Execute(int index)
    {
        uint2 a = Input[index];
        uint2 b = a * 2u;
        uint dx = a.x / 3u;
        uint mx = a.x % 3u;
        uint dy = a.y / 3u;
        uint my = a.y % 3u;
        uint sx = a.x >> 2;
        uint sy = a.y >> 2;
        Output[index] = (int)(dx + mx + b.x + sx) + (int)(dy + my + b.y + sy);
    }
}
