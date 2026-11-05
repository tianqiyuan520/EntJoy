using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

public class ForLoopOptimizationTests
{
    private static EmitResult Emit(string loop, string target = "AutoSIMD = AutoSIMD.Enabled") =>
        GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(" + target + @")]
public struct GeneralLoopJob : IJobParallelFor {
 public NativeArray<int> Output;
 public NativeArray<int> Starts;
 public NativeArray<int> Ends;
 public int Limit;
 public void Execute(int index) {
  int sum = index;
  " + loop + @"
  Output[index] = sum;
 }
}");

    [Theory]
    [InlineData("for (int k=-3; k<=3; k+=2) sum += k;", "-3,-1,1,3")]
    [InlineData("for (int k=6; k>0; k-=2) sum += k;", "6,4,2")]
    [InlineData("for (int k=6; 0<=(k); --k) sum += k;", "6,5,4,3,2,1,0")]
    [InlineData("for (int k=(1+1); (k)<8; k=k+2) sum += k;", "2,4,6")]
    [InlineData("for (int k=5; k>=0; k=k-2) sum += k;", "5,3,1")]
    [InlineData("for (int k=0; k<6; k=2+k) sum += k;", "0,2,4")]
    [InlineData("for (int k=0; k<6; k-= -2) sum += k;", "0,2,4")]
    [InlineData("for (int k=0; k<1; ++k) sum += k;", "0")]
    [InlineData("for (int k=0; k>1; --k) sum += k;", "")]
    public void FixedLoopsRetainTheirOrderedCounterValues(string loop, string values)
    {
        var result = Emit(loop);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var actual = Regex.Matches(result.Cpp, @"simd_k = simd_value<int>::broadcast\((-?\d+)\)")
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(values, string.Join(",", actual));
        Assert.Contains("// Unrolled loop:", result.Cpp);
    }

    [Theory]
    [InlineData("for (int k=-3; k<=3; k+=2) sum += k;")]
    [InlineData("for (int k=6; 0<=(k); --k) sum += k;")]
    [InlineData("for (int k=(1+1); k<8; k=k+2) sum += k;")]
    public void FixedLoopsKeepBackendSpecificUnrollPolicies(string loop)
    {
        var ispc = Emit(loop, "Target = BackendTarget.Ispc");
        Assert.DoesNotContain("#pragma unroll", ispc.Cpp);
        Assert.DoesNotContain("#pragma clang loop unroll", ispc.Cpp);
        Assert.Contains("for (uniform int k", ispc.Cpp);
        var scalar = Emit(loop, "AutoSIMD = AutoSIMD.Disabled");
        Assert.Contains("#pragma clang loop unroll(full)", scalar.Cpp);
    }

    [Theory]
    [InlineData("for (int k=2147483646; k<=2147483647; ++k) { sum++; break; }")]
    [InlineData("for (int k=-2147483647; k>=-2147483648; --k) { sum++; break; }")]
    [InlineData("for (int k=2147483646; k<2147483647; k+=2) { sum++; break; }")]
    [InlineData("for (int k=0; k<4; k--) { sum++; break; }")]
    [InlineData("for (int k=0; k<130; k+=2) sum+=k;")]
    public void OverflowOrLargeTripsRetainRealLoops(string loop)
    {
        var simd = Emit(loop);
        Assert.DoesNotContain(simd.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("// Unrolled loop:", simd.Cpp);
        var ispc = Emit(loop, "Target = BackendTarget.Ispc");
        Assert.DoesNotContain("#pragma unroll", ispc.Cpp);
    }

    [Theory]
    [InlineData("for (int k=0; k<8; k++) { sum+=k; k++; }")]
    [InlineData("for (int k=0; k<8; k++) { ref int alias=ref k; alias=8; }")]
    public void MutableOrAliasedCountersAreNotExpanded(string loop)
    {
        Assert.True(Emit(loop).HasDiagnostic("NT031"));
        Assert.DoesNotContain("#pragma unroll", Emit(loop, "Target = BackendTarget.Ispc").Cpp);
    }

    [Theory]
    [InlineData("for (int k=0; Limit>k; k+=2) sum+=k;", "k < Limit", "2u")]
    [InlineData("for (int k=20; k>=Limit; --k) sum+=k;", "k >= Limit", "4294967295u")]
    public void UniformRuntimeLoopsKeepDirectionAndStride(string loop, string comparison, string step)
    {
        var result = Emit(loop);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(comparison, result.Cpp);
        Assert.Contains("(unsigned)k + " + step, result.Cpp);
    }

    [Theory]
    [InlineData("for (int k=Starts[index]; k<Ends[index]; k+=2) { if(k==2) continue; sum+=k; }", "n_cmp_lt_epi32", "+ 2")]
    [InlineData("for (int k=Starts[index]; Ends[index]<=k; k-=3) { if(k==2) break; sum+=k; }", "n_cmp_ge_epi32", "+ -3")]
    public void VaryingRuntimeLoopsUseLaneMasks(string loop, string comparison, string step)
    {
        var result = Emit(loop);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("// SIMD mask-loop:", result.Cpp);
        Assert.Contains(comparison, result.Cpp);
        Assert.Contains("simd_k = simd_k " + step, result.Cpp);
        Assert.DoesNotMatch(@"goto __loop_continue_", result.Cpp);
    }

    [Fact]
    public void NestedUnrollingHasACumulativeLimit()
    {
        var result = Emit("for(int j=0;j<16;j++) for(int k=0;k<16;k++) sum+=j+k;");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Single(Regex.Matches(result.Cpp, "// Unrolled loop: 16 iterations"));
        Assert.Contains("for (int k", result.Cpp);
    }

    [Fact]
    public void NativeArrayLengthCanBoundAndIndexGeneralLoops()
    {
        var result = Emit("for(int k=0;k<Starts.Length;k+=2) sum+=Starts[k % Starts.Length];");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("Starts.Length", result.Cpp);
        Assert.Contains("Starts_length", result.Cpp);
    }

    [Fact]
    public void LoopReturnMaskIsDeclaredOutsideTheCounterScope()
    {
        var result = Emit("for(int k=0;k<8;k+=2) { if(k==index) { Output[index]=sum; return; } sum+=k; }");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Matches(@"\}\s+simd_mask __after_for_\d+ =", result.Cpp);
        Assert.Contains("n_not_mask(__returned_", result.Cpp);
    }
}
