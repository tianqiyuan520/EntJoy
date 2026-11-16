using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

/// <summary>
/// uint2 进入 AutoSIMD：元素读走 int2 寄存器类型、算术走 int2 重载、
/// / % &gt;&gt; 走无符号 helper（C++ 的 int 运算是有符号的）。
/// </summary>
public class Uint2SimdTests
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
        public static uint2 operator /(uint2 a, uint b) => new uint2(a.x / b, a.y / b);
        public static uint2 operator %(uint2 a, uint b) => new uint2(a.x % b, a.y % b);
        public static uint2 operator >>(uint2 a, int s) => new uint2(a.x >> s, a.y >> s);
    }
}
";

    private static EmitResult Emit(string body)
        => GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.ECS; using EntJoy.Mathematics;
public struct PairIn { public uint2 V; }
public struct ScalarOut { public int V; }
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct Uint2ChunkJob : IJobChunk {
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        var items = chunk.GetComponentDataNativeArray<PairIn>();
        var outs = chunk.GetComponentDataNativeArray<ScalarOut>();
        for (int i = 0; i < items.Length; i++) {
            " + body + @"
        }
    }
}", extraSource: MathStubs);

    [Fact]
    public void PairArithmeticStaysInInt2Registers()
    {
        var result = Emit("uint2 a = items[i].V; uint2 b = a * 2u; uint2 c = b + a; outs[i].V = (int)c.x;");

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", result.Cpp);
        // uint2 没有自己的 SIMD 类型，用 int2 寄存器承载。
        Assert.Contains("simd_value<EntJoy::Mathematics::int2>", result.Cpp);
        Assert.DoesNotContain("simd_value<EntJoy::Mathematics::uint2>", result.Cpp);
    }

    [Fact]
    public void UnsignedDivisionModuloShiftUseHelpers()
    {
        var result = Emit("uint2 a = items[i].V; uint2 d = a / 3u; uint2 m = a % 3u; uint2 s = a >> 2; outs[i].V = (int)(d.x + m.x + s.x);");

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", result.Cpp);
        // 无符号语义必须显式转换；直接 (a / 3) 是有符号除法，大值会错。
        Assert.Contains("simd_div_u32", result.Cpp);
        Assert.Contains("simd_mod_u32", result.Cpp);
        Assert.Contains("simd_shr_u32", result.Cpp);
    }

    [Fact]
    public void ParallelForPairArrayGathersInsteadOfLane0()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections; using EntJoy.Mathematics;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct Uint2ParallelJob : IJobParallelFor {
    public NativeArray<uint2> Input;
    public NativeArray<int> Output;
    public void Execute(int index) {
        uint2 a = Input[index];
        uint2 b = a * 2u;
        Output[index] = (int)(b.x + (a.y / 3u));
    }
}", extraSource: MathStubs);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", result.Cpp);
        // 并行作业的 pair 数组必须按整元素 gather；退化成 lane0 会让所有 lane 读同一元素。
        Assert.Contains("simd_value<EntJoy::Mathematics::int2>::gather(", result.Cpp);
        Assert.DoesNotContain("n_extract_lane_epi32(__gi_", result.Cpp);
        Assert.Contains("simd_div_u32", result.Cpp);
    }
}
