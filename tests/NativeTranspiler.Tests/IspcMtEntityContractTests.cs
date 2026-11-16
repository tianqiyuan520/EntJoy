using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

/// <summary>
/// IJobEntity 的 ISPC 路径按 chunk 级调度只发一次实体循环，没有多任务变体。
/// 组合 UseISPC_MT 必须在校验期报 NT037，而不是等到加载期找不到符号。
/// </summary>
public class IspcMtEntityContractTests
{
    [Fact]
    public void EntityJobWithIspcMtReportsNt037()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.ECS;
public struct Cmp { public int V; }
[NativeTranspile(Target = BackendTarget.Ispc, UseISPC_MT = true)]
public struct EntityMtJob : IJobEntity {
    public void Execute(ref Cmp c) { c.V = c.V + 1; }
}");

        Assert.Contains(result.Diagnostics, d => d.Id == "NT037" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ChunkJobWithIspcMtDoesNotReportNt037()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.ECS;
public struct Cmp { public int V; }
[NativeTranspile(Target = BackendTarget.Ispc, UseISPC_MT = true)]
public struct ChunkMtJob : IJobChunk {
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        var items = chunk.GetComponentDataNativeArray<Cmp>();
        for (int i = 0; i < items.Length; i++) items[i].V = items[i].V + 1;
    }
}");

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "NT037");
    }
}
