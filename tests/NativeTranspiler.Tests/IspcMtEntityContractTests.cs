using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

/// <summary>
/// chunk 级作业的并行度由调度器跨 chunk 提供，chunk 内再开 ISPC 任务会与外层线程池嵌套
/// （实测死锁且慢 70–80×）⇒ 组合 UseISPC_MT 在校验期报 NT038。IJobEntity 另有 NT037。
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
    public void ChunkJobWithIspcMtReportsNt038()
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

        Assert.Contains(result.Diagnostics, d => d.Id == "NT038" && d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "NT037");
    }
}
