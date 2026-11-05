using System.IO;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

public class ChunkLoopBindingTests
{
    private static EmitResult Emit(string body, string prelude = "")
        => GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.ECS;
public struct LoopValue { public float Value; public int Limit; public byte Flag; }
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct BoundChunkJob : IJobChunk {
    public int Iterations;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        var values = chunk.GetComponentDataNativeArray<LoopValue>();
        " + prelude + @"
        for (int index = 0; index < values.Length; index++) {
            var value = values[index];
            " + body + @"
            values[index] = value;
        }
    }
}");

    [Theory]
    [InlineData("for (int k = 0; k < 3; k++) { var delta = 0.25f; value.Value += delta; }", "Unrolled loop")]
    [InlineData("for (int k = 0; k < Iterations; k += 2) { value.Value += k; }", "Scalar for")]
    [InlineData("var end = value.Limit; for (int k = 0; k < end; k++) { value.Value += k; end--; }", "SIMD mask-loop")]
    [InlineData("for (int k = value.Limit; k >= 0; k -= 2) { value.Value += k; }", "SIMD mask-loop")]
    [InlineData("for (int k = 0; k < chunk.Count; k++) { if (k == value.Limit) break; value.Value += k; }", "Scalar for")]
    [InlineData("var delta = value.Limit > 2 ? 0.5f : 0.25f; for (int k = 0; k < 3; k++) value.Value += delta;", "Unrolled loop")]
    public void LoweredInnerLoopRetainsBindingAndVectorization(string body, string strategy)
    {
        var result = Emit(body);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", result.Cpp);
        Assert.DoesNotContain("Scalar chunk body:", result.Cpp);
        Assert.Contains(strategy, result.Cpp);
        Assert.Contains("v_i", result.Cpp);
    }

    [Theory]
    [InlineData("for (int k = 0; k < 3; k++) { if (value.Limit == k) return; value.Value += 1; }")]
    [InlineData("value.Value += 1; var snapshot = values[index].Value; for (int k = 0; k < 3; k++) value.Value += snapshot;")]
    [InlineData("for (int k = 0; k < 3; k++) value.Flag++;")]
    [InlineData("for (int k = 0; k < 3; k++) Iterations += k;")]
    [InlineData("for (int k = 0; k < 3; k++) value.Value += k; var copy = (values[index] = value);")]
    public void ChunkExitAndObservableStructCopiesKeepOriginalScalarBody(string body)
    {
        var result = Emit(body);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("Scalar chunk body:", result.Cpp);
    }

    [Fact]
    public void ChunkSideEffectsOutsideEntityLoopExecuteAtChunkScope()
    {
        var result = Emit("for (int k = 0; k < 3; k++) value.Value += k;", "Iterations++;");
        Assert.Contains("Scalar chunk body:", result.Cpp);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", result.Cpp);
    }

    [Fact]
    public void DistinctScopedLocalsRetainTheirOwnTypes()
    {
        var result = Emit("for (int x = 0; x < 3; x++) value.Value += x; { var x = 0.5f; for (int k = 0; k < 3; k++) value.Value += x; }");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("Scalar chunk body:", result.Cpp);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", result.Cpp);
        Assert.Contains("__ej_x_", result.Cpp);
    }

    [Fact]
    public void ConditionalWriteBackRemainsAtItsSourceBranch()
    {
        var result = Emit("for (int k = 0; k < 3; k++) value.Value += k; if (value.Limit > 0) values[index] = value;");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", result.Cpp);
        Assert.DoesNotContain("Scalar chunk body:", result.Cpp);
    }

    [Fact]
    public void ObservableCopyKeepsScalarValueAndChunkReturnIsIsolated()
    {
        var snapshot = Emit("value.Value += 1; var saved = values[index].Value; for (int k = 0; k < 3; k++) value.Value += saved;");
        Assert.Contains("LoopValue value = values_ptr[index];", snapshot.Cpp);
        var exit = Emit("for (int k = 0; k < 3; k++) { if (value.Limit == k) return; value.Value += k; }");
        Assert.Matches(@"const EntityBatchData\* __batchData[^\n]*\r?\n\s*\[&\]\(\) \{", exit.Cpp);
    }
}
