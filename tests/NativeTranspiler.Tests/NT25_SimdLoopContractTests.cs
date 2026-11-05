using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class NT25_SimdLoopContractTests
    {
        private static EmitResult Emit(string loop) => GeneratorHarness.EmitFor(@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct LoopContractJob : IJobParallelFor {
 public NativeArray<int> Output;
 public void Execute(int index) {
  int sum = index;
  " + loop + @"
  Output[index] = sum;
 }
}");

        [Theory]
        [InlineData("for (int k=0; k<4; k++) { sum += k+1; k++; }")]
        [InlineData("for (int k=0; k<4; k++) { sum += k+1; k=4; }")]
        [InlineData("for (uint k=0; k<4; k++) sum += (int)k+1;")]
        [InlineData("for (int k=0; k<4; k+=index) sum += k;")]
        [InlineData("for (int k=0; k<4; k*=2) sum += k;")]
        [InlineData("for (int k=0; k<4; k+=0) { sum++; break; }")]
        public void UnsupportedLoopControl_ReportsDiagnostic(string loop)
        {
            var result = Emit(loop);
            Assert.True(result.HasDiagnostic("NT031"),
                GeneratorHarness.Fail(result, "SIMD must not replace this loop with an ascending unit-stride loop."));
        }

        [Fact]
        public void InclusiveMaximumBound_RemainsARealLoop()
        {
            var result = Emit("for (int k=2147483646; k<=2147483647; k++) { sum++; break; }");
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.DoesNotContain("// Unrolled loop:", result.Cpp);
        }

        [Fact]
        public void LargeConstantTripCount_DoesNotOverflowIntoUnroll()
        {
            var result = Emit("for (int k=-1073741824; k<1073741825; k++) sum += k;");
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.DoesNotContain("// Unrolled loop:", result.Cpp);
        }

        [Theory]
        [InlineData("for (int k=-2; k<=1; ++k) sum += k;")]
        [InlineData("for (int k=0; k<4; k++) sum += k;")]
        [InlineData("for (int k=3; k>=0; k--) sum += k;")]
        [InlineData("for (int k=0; 4>k; k++) sum += k;")]
        public void CanonicalFixedLoop_RemainsVectorized(string loop)
        {
            var result = Emit(loop);
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.Contains("// Unrolled loop: 4 iterations", result.Cpp);
        }
    }
}
