using System.IO;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

public class MutableJobFieldTests
{
    [Theory]
    [InlineData("IJob", "", "")]
    [InlineData("IJobParallelFor", "int index", "")]
    [InlineData("IJobChunk", "ArchetypeChunk chunk, in ChunkEnabledMask mask", "")]
    [InlineData("IJobChunk", "ArchetypeChunk chunk, in ChunkEnabledMask mask",
        "if ((Flags & 1) != 0) Counter++; if ((Flags & 2) != 0) Counter++; if ((Flags & 4) != 0) Counter++;")]
    public void RefAndNestedWrites_UseWritableBindings(string kind, string parameters, string switches)
    {
        var result = GeneratorHarness.EmitForJob(@"
using EntJoy.JobSystem; using EntJoy.ECS; using NativeTranspiler;
namespace MutationProbe {
public struct Cursor { public int Value; }
public static class Helpers { public static void Advance(ref int n) { n++; } }
[NativeTranspile] public struct MutableJob : " + kind + @" {
 public int Counter; public Cursor Position; public int Flags;
 public void Execute(" + parameters + @") {
  Helpers.Advance(ref Counter);
  this.Position.Value += Counter;
  " + switches + @"
 }
}}");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var implementation = File.ReadAllText(Path.Combine(result.OutputDir,
            "SharpNative_Job_MutationProbe_MutableJob_Execute.cpp"));
        Assert.Contains("int& Counter = *Counter_ptr;", implementation);
        Assert.Contains("MutationProbe::Cursor& Position = *Position_ptr;", implementation);
        Assert.DoesNotContain("const int& Counter", implementation);
        Assert.DoesNotContain("const MutationProbe::Cursor& Position", implementation);
        if (kind == "IJobChunk")
        {
            var adapter = File.ReadAllText(Path.Combine(result.OutputDir,
                "SharpNative_Job_MutationProbe_MutableJob_Execute_Adapter.cpp"));
            Assert.DoesNotContain("const int& Counter", adapter);
            Assert.DoesNotContain("const MutationProbe::Cursor& Position", adapter);
        }
    }
}
