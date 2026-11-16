using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

/// <summary>
/// 生成器遇到不支持的写法时必须给标记/诊断，而不是抛异常（NT026 崩溃通道）。
/// </summary>
public class UnsupportedStoreContractTests
{
    [Fact]
    public void PairElementCompoundAssignEmitsMarkerInsteadOfCrash()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections; using EntJoy.Mathematics;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct PairCompoundJob : IJobParallelFor {
    public NativeArray<uint2> Values;
    public void Execute(int index) { Values[index] *= 2u; }
}", extraSource: @"namespace EntJoy.Mathematics { public struct uint2 {
    public uint x, y;
    public static uint2 operator *(uint2 a, uint b) => new uint2(a.x * b, a.y * b);
} }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "NT026");
        Assert.Contains("SIMD_StructCompoundAssign", result.Cpp);
    }
}
