using Xunit;
using System.IO;
namespace NativeTranspiler.Tests;
public class IspcConstantLoopTests
{
    [Theory]
    [InlineData("-1", "<=", "1")]
    [InlineData("0", "<", "16")]
    [InlineData("0", "<", "17")]
    [InlineData("0", "<", "Limit")]
    public void UniformLoopsLeaveUnrollingToIspc(string start, string comparison, string end)
    {
        string source = @"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target = BackendTarget.Ispc)]
public struct SmallLoopJob : IJobParallelFor {
    public NativeArray<int> Output;
    public int Limit;
    public void Execute(int index) {
        int sum = 0;
        for (int k = " + start + "; k " + comparison + " " + end + @"; k++) { sum += k; }
        Output[index] = sum;
    }
}";
        var result = GeneratorHarness.EmitFor(source);
        Assert.Contains("for (uniform int k", result.Cpp);
        Assert.DoesNotContain("#pragma unroll", result.Cpp);
        Assert.DoesNotContain("#pragma clang loop unroll", result.Cpp);
    }

    [Fact]
    public void HelperLoopDoesNotInheritCppUnrollHints()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target = BackendTarget.Ispc)]
public struct HelperLoopJob : IJobParallelFor {
 public NativeArray<int> Output;
 public void Execute(int index) { Output[index] = Sum(); }
 private static int Sum() {
  int sum=0;
  for(int k=4;k>0;--k) sum+=k;
  return sum;
 }
}");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var helper = File.ReadAllText(Path.Combine(result.OutputDir,
            "SharpNative__global_namespace__HelperLoopJob_Sum_helper.ispc"));
        Assert.Contains("k > 0", helper);
        Assert.DoesNotContain("#pragma unroll", helper);
        Assert.DoesNotContain("#pragma clang loop unroll", helper);
    }
}
