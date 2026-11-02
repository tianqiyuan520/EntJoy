using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class NT24_ReductionLoopTests
    {
        [Theory]
        [InlineData("AutoSIMD.Disabled")]
        [InlineData("AutoSIMD.Enabled")]
        public void DynamicReduction_DoesNotSynthesizeIndependentAccumulators(string mode)
        {
            var result = GeneratorHarness.EmitForJob(@"
using EntJoy.Collections; using EntJoy.JobSystem; using NativeTranspiler;
[NativeTranspile(AutoSIMD = " + mode + @")]
public struct ReductionJob : IJobParallelFor {
 public NativeArray<int> Input, Output; public int Count, Seed;
 public void Execute(int index) {
  int sum = Seed + index;
  for (int k=0; k<Count; k++) sum += Input[k];
  Output[index] = sum;
 }
}");
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.DoesNotContain("__red", result.Cpp);
        }
    }
}
