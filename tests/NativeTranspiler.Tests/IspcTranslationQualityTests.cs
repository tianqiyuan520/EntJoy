using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

public class IspcTranslationQualityTests
{
    private static string Emit(string body, string kind = "IJobParallelFor")
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)]
public unsafe struct TranslationJob : " + kind + @" {
 public NativeArray<int> Input; public NativeArray<int> Output;
 public int Begin; public int End;
 public void Execute(int index) { " + body + @" }
}");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return File.ReadAllText(Path.Combine(result.OutputDir,
            "SharpNative_Job__global_namespace__TranslationJob_Execute_Batch.ispc"));
    }

    [Theory]
    [InlineData("IJobParallelFor")]
    [InlineData("IJobFor")]
    public void BatchRangeComesFromTheScheduler(string kind)
    {
        var source = Emit("Output[index]=Input[index % Input.Length]+index;", kind);
        Assert.Contains("foreach (index = __startIndex ... __startIndex + __count)", source);
        Assert.DoesNotContain("min(__startIndex + __count", source);
    }

    [Fact]
    public void AtomicBatchRangeAlsoComesFromTheScheduler()
    {
        var source = Emit("Output[index]=System.Threading.Interlocked.Increment(ref ((int*)Input.GetUnsafePtr())[index % Input.Length]);");
        Assert.Contains("for (uniform int index = __startIndex; index < __startIndex + __count;", source);
        Assert.DoesNotContain("min(__startIndex + __count", source);
    }

    [Theory]
    [InlineData("for(int k=0;k<9;k++) Output[index]=Output[index]*3+Input[k % Input.Length];")]
    [InlineData("for(int k=0;k<9;k++) { if(k==3) continue; if(k==7) break; Output[index]=Input[k % Input.Length]; }")]
    [InlineData("int k=0; while(k<9) { Output[index]=Output[index]*3+k; k++; }")]
    public void InnerLoopsKeepTheirSequentialSemantics(string body)
    {
        var source = Emit(body);
        Assert.Contains("foreach (index =", source);
        Assert.Single(Regex.Matches(source, @"foreach\s*\("));
        Assert.DoesNotContain("foreach (k", source);
    }

    [Theory]
    [InlineData("Begin", "End")]
    [InlineData("(int)Begin", "Input.Length")]
    [InlineData("Input.Length - 5", "Input.Length")]
    public void UniformRuntimeBoundsUseUniformCounters(string begin, string end)
    {
        var source = Emit("int sum=index; for(int k=" + begin + ";k<" + end
            + ";k+=2) sum+=Input[k % Input.Length]; Output[index]=sum;");
        Assert.Contains("for (uniform int k", source);
        Assert.DoesNotContain("#pragma", source);
    }

    [Fact]
    public void VaryingBoundsKeepVaryingCounters()
    {
        var source = Emit("int sum=index; for(int k=Input[index % Input.Length];k<End;k++) sum+=k; Output[index]=sum;");
        Assert.Contains("for (int k", source);
        Assert.DoesNotContain("for (uniform int k", source);
    }

    [Fact]
    public void NestedReturnIsNotTranslatedAsAnInnerContinue()
    {
        var source = Emit("for(int k=0;k<9;k++) { if(k==index) return; Output[index]=k; } Output[index]=99;");
        Assert.Contains("__ENTJOY_UNSUPPORTED_STMT__ISPC_ReturnInsideNestedLoopInBatch", source);
    }

    [Fact]
    public void IndexLevelReturnStillEndsOnlyThatIndex()
    {
        var source = Emit("if(index==3) return; Output[index]=index;");
        Assert.Contains("continue;", source);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", source);
    }

    [Theory]
    [InlineData("for(int k=0;k<4;k++) value=value*3+k;", "for (uniform int k")]
    [InlineData("for(int k=0;k<limit;k++) value=value*3+k;", "for (int k")]
    public void LaneCallableHelpersKeepEveryIteration(string body, string loop)
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)] public struct HelperJob : IJobParallelFor {
 public NativeArray<int> Output;
 public void Execute(int index) { Output[index]=Fold(index, index%5); }
 private static int Fold(int value, int limit) { " + body + @" return value; }
}");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var source = File.ReadAllText(Path.Combine(result.OutputDir,
            "SharpNative__global_namespace__HelperJob_Fold_helper.ispc"));
        Assert.Contains(loop, source);
        Assert.DoesNotContain("foreach", source);
        var cmake = File.ReadAllText(Path.Combine(result.OutputDir, "CMakeLists.txt"));
        Assert.Contains(cmake.Split('\n'), line => line.Contains("DEPENDS")
            && line.Contains("HelperJob_Execute_Batch.ispc") && line.Contains("HelperJob_Fold_helper.ispc")
            && line.Contains("EntJoyCommon.ispc"));
    }
}
