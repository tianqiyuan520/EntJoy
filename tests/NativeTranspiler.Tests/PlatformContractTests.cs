using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

/// <summary>后端产物契约：Cpp 只发 .cpp、ISPC 只发 .ispc，不会串台。</summary>
public class PlatformContractTests
{
    [Fact]
    public void CppBackendEmitsNoIspcUnit()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.ECS;
public struct Cmp { public int V; }
[NativeTranspile(Target = BackendTarget.Cpp)]
public struct CppOnlyJob : IJobChunk {
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        var items = chunk.GetComponentDataNativeArray<Cmp>();
        for (int i = 0; i < items.Length; i++) items[i].V = items[i].V + 1;
    }
}");

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var files = Directory.GetFiles(result.OutputDir, "*", SearchOption.AllDirectories);
        Assert.Contains(files, f => f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.EndsWith(".ispc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IspcBackendEmitsNoCppJobUnit()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.ECS;
public struct Cmp { public int V; }
[NativeTranspile(Target = BackendTarget.Ispc)]
public struct IspcOnlyJob : IJobChunk {
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        var items = chunk.GetComponentDataNativeArray<Cmp>();
        for (int i = 0; i < items.Length; i++) items[i].V = items[i].V + 1;
    }
}");

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var files = Directory.GetFiles(result.OutputDir, "*", SearchOption.AllDirectories);
        Assert.Contains(files, f => f.EndsWith(".ispc", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Contains("IspcOnlyJob_Execute.cpp", StringComparison.OrdinalIgnoreCase));
    }
}
