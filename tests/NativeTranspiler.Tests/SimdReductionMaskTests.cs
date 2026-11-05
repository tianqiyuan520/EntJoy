using System.Text.RegularExpressions;
using Xunit;
namespace NativeTranspiler.Tests;
public class SimdReductionMaskTests
{
    [Theory]
    [InlineData("<", false)]
    [InlineData("<=", true)]
    public void VaryingReductionRetainsEntryMaskAndSafeTripCount(string comparison, bool inclusive)
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct MaskedReductionJob : IJobParallelFor {
    public NativeArray<int> Starts;
    public NativeArray<int> Ends;
    public NativeArray<float> Values;
    public NativeArray<float> Output;
    public void Execute(int index) {
        float best = 1000000f;
        int end = Ends[index];
        if (Starts[index] >= 0) {
            for (int i = Starts[index]; i " + comparison + @" end; i++) {
                float candidate = Values[i];
                if (candidate < 0f) continue;
                if (candidate < best) best = candidate;
            }
        }
        Output[index] = best;
    }
}");
        if (inclusive)
        {
            Assert.Contains("SIMD mask-loop", result.Cpp);
            Assert.Contains("n_cmp_le_epi32", result.Cpp);
            Assert.DoesNotContain("maxIter", result.Cpp);
        }
        else
        {
            Assert.Contains("Varying-bound reduction", result.Cpp);
            Assert.Matches(@"v_count_\d+ = blend\(simd_value<int>\(0\), simd_end_i - simd_i", result.Cpp);
            Assert.Matches(@"unsigned maxIter_\d+ = .*hmax\(v_count_\d+ \^ .*0x80000000u", result.Cpp);
            Assert.Contains("for (unsigned iter", result.Cpp);
            Assert.Matches(@"__saved_\d+ = __saved_\d+ & simd_mask", result.Cpp);
        }
        Assert.DoesNotContain("simd_i = simd_max(simd_i", result.Cpp);
        Assert.DoesNotContain("goto __vr_cont_", result.Cpp);
    }
    [Fact]
    public void NearestPointFallbackScansPastFirstCandidate()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct NearestFallbackJob : IJobParallelFor {
    public NativeArray<float> Distances;
    public NativeArray<int> Indices;
    public NativeArray<int> Results;
    public int Length;
    public void Execute(int index) {
        int bestIdx = -1;
        float best = 1000000f;
        for (int i = 0; i < Length; i++) {
            float delta = Distances[i] - Distances[index];
            float distance = delta * delta;
            if (distance < best) { best = distance; bestIdx = i; }
        }
        if (bestIdx != -1) Results[index] = Indices[bestIdx];
    }
}");
        Assert.Contains("Uniform-bound reduction", result.Cpp);
        Assert.DoesNotMatch(@"n_cmp_eq_epi32\(v_bestIdx\.v, n_set1_epi32\(-1\)\).*break", result.Cpp);
    }}
