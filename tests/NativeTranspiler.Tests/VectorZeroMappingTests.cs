using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

/// <summary>2 分量向量的 `.zero` 常量映射：三种类型各自产出自己的构造，不能互相串。</summary>
public class VectorZeroMappingTests
{
    private const string MathStubs = @"
namespace EntJoy.Mathematics
{
    public struct uint2 { public uint X, Y; public static uint2 zero => default; }
    public struct int2 { public int X, Y; public static int2 zero => default; }
    public struct float2 { public float X, Y; public static float2 zero => default; }
}
";

    private static EmitResult Emit(string body)
        => GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.ECS; using EntJoy.Mathematics;
public struct ZeroTarget { public int Tag; }
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct ZeroChunkJob : IJobChunk {
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        var targets = chunk.GetComponentDataNativeArray<ZeroTarget>();
        for (int i = 0; i < targets.Length; i++) {
            " + body + @"
        }
    }
}", extraSource: MathStubs);

    [Theory]
    [InlineData("uint2 z = uint2.zero; targets[i].Tag = (int)z.X;", "EntJoy::Mathematics::uint2(0u, 0u)")]
    [InlineData("int2 z = int2.zero; targets[i].Tag = z.X;", "EntJoy::Mathematics::int2(0, 0)")]
    [InlineData("float2 z = float2.zero; targets[i].Tag = (int)z.X;", "EntJoy::Mathematics::float2(0.0f, 0.0f)")]
    public void ZeroConstantMapsToOwnType(string body, string expected)
    {
        var result = Emit(body);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(expected, result.Cpp);
        // 不能退化成 `类型名.zero` 这种非法 C++。
        Assert.DoesNotContain(".zero", result.Cpp);
    }
}
