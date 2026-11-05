using System.IO;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

public class IspcUniformLocalTests
{
    private static string Emit(string body)
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)] public unsafe struct UniformLocalJob : IJobParallelFor {
 public NativeArray<int> Input; public NativeArray<int> Output; public int Begin; public int End;
 public void Execute(int index) { " + body + @" }
 private static void Shrink(ref int value) { value--; }
}");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return File.ReadAllText(Path.Combine(result.OutputDir,
            "SharpNative_Job__global_namespace__UniformLocalJob_Execute_Batch.ispc"));
    }

    [Fact]
    public void ImmutableLocalChainsKeepUniformBoundsAndLengths()
    {
        var source = Emit(@"int begin=Begin; int end=End; int length=Input.Length; int period=length;
int sum=index; for(int k=begin;k<end;k+=2) sum+=Input[k%period]; Output[index]=sum;");
        Assert.Contains("uniform int begin = Begin;", source);
        Assert.Contains("uniform int end = End;", source);
        Assert.Contains("uniform int length = Input_length;", source);
        Assert.Contains("uniform int period = length;", source);
        Assert.Contains("for (uniform int k", source);
        Assert.Contains("int sum = index;", source);
    }

    [Theory]
    [InlineData("int value=4; Output[index]=value;", "uniform int value")]
    [InlineData("float value=0.5f; Output[index]=(int)value;", "uniform float value")]
    [InlineData("bool value=Begin>0; if(value) Output[index]=1;", "uniform bool value")]
    [InlineData("const int value=4; Output[index]=value;", "uniform int value")]
    public void ImmutableScalarInitializersCanBeUniform(string body, string declaration)
        => Assert.Contains(declaration, Emit(body));

    [Theory]
    [InlineData("int value=index; Output[index]=value;")]
    [InlineData("int value=Input[index%Input.Length]; Output[index]=value;")]
    [InlineData("int value=End; if(index>0) value--; Output[index]=value;")]
    [InlineData("int value=End; Shrink(ref value); Output[index]=value;")]
    [InlineData("int value=0; while(value<index) value++; Output[index]=value;")]
    [InlineData("if(index>0) { int value=End; Output[index]=value; }")]
    [InlineData("int value=End, other=Begin; Output[index]=value+other;")]
    public void VaryingMutableOrNestedLocalsRetainTheirStorage(string body)
    {
        var source = Emit(body);
        Assert.Contains("int value", source);
        Assert.DoesNotContain("uniform int value", source);
    }

    [Fact]
    public void AddressTakingPreventsPromotion()
    {
        var address = Emit("int value=End; int* pointer=&value; *pointer-=index; Output[index]=value;");
        Assert.DoesNotContain("uniform int value", address);
    }

    [Fact]
    public void ConstantInitializedAccumulatorStillVariesPerLane()
    {
        var source = Emit("int value=0; for(int k=0;k<End;k++) { if(index>k) value+=k; } Output[index]=value;");
        Assert.DoesNotContain("uniform int value", source);
        Assert.Contains("for (uniform int k", source);
    }

    [Fact]
    public void BothIspcBuildEntrypointsPreserveSignedWraparound()
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)] public struct OverflowJob : IJobParallelFor {
 public NativeArray<int> Output; public int Value;
 public void Execute(int index) { Output[index]=-Value; }
}");
        foreach (var name in new[] { "CMakeLists.txt", "run_ispc.bat" })
            Assert.Contains("--wrap-signed-int", File.ReadAllText(Path.Combine(result.OutputDir, name)));
    }
}
