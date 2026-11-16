using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

/// <summary>
/// uint2 在 chunk 作业里的落点：不进 SIMD 路径（标量翻译），产出的分量访问必须是
/// 数学向量类型的访问器形式，且不能出现 C++ 里不存在的 simd_value&lt;uint2&gt;。
/// </summary>
public class Uint2ArithmeticTests
{
    private const string MathStubs = @"
namespace EntJoy.Mathematics
{
    public struct uint2
    {
        public uint x, y;
        public uint2(uint x, uint y) { this.x = x; this.y = y; }
        public static uint2 zero => new uint2(0u, 0u);
        public static uint2 operator +(uint2 a, uint2 b) => new uint2(a.x + b.x, a.y + b.y);
        public static uint2 operator *(uint2 a, uint b) => new uint2(a.x * b, a.y * b);
    }
    public struct int2 { public int x, y; public static int2 zero => new int2(0, 0); }
}
";

    [Fact]
    public void Uint2ArithmeticStaysScalarAndUsesVectorAccessors()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.ECS; using EntJoy.Mathematics;
public struct PairValue { public uint2 Pair; }
public struct PairTarget { public int Tag; }
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct Uint2ChunkJob : IJobChunk {
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        var pairs = chunk.GetComponentDataNativeArray<PairValue>();
        var targets = chunk.GetComponentDataNativeArray<PairTarget>();
        for (int i = 0; i < pairs.Length; i++) {
            uint2 scaled = pairs[i].Pair * 2u;
            uint2 sum = scaled + pairs[i].Pair;
            targets[i].Tag = (int)sum.x;
        }
    }
}", extraSource: MathStubs);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", result.Cpp);
        // 数学向量在 C++ 侧只有访问器，没有 .x/.y 成员；分量读取必须是调用形式。
        Assert.Contains("EntJoy::Mathematics::uint2", result.Cpp);
        Assert.Contains("sum.xr()", result.Cpp);
        Assert.DoesNotContain("sum.x;", result.Cpp);
        Assert.DoesNotContain("sum.x() =", result.Cpp);
        // uint2 目前不进 SIMD：不能出现不存在的 simd_value<uint2>。
        Assert.DoesNotContain("simd_value<EntJoy::Mathematics::uint2>", result.Cpp);
    }
}
