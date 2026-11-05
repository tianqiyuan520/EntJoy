using Xunit;

namespace NativeTranspiler.Tests;

public class CppSimdTranslationTests
{
    [Fact]
    public void EnabledChunkAdapterUsesTheSamePreciseCompilationAsItsImplementation()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.ECS;
public struct Value { public float X; }
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct PreciseChunk : IJobChunk {
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        var values = chunk.GetComponentDataNativeArray<Value>();
        for (int i = 0; i < values.Length; i++) { var value = values[i]; value.X += 1f; values[i] = value; }
    }
}");
        string cmake = System.IO.File.ReadAllText(System.IO.Path.Combine(result.OutputDir, "CMakeLists.txt"));
        var precise = System.Text.RegularExpressions.Regex.Match(cmake, @"set\(AUTOSIMD_SOURCES([\s\S]*?)\)").Groups[1].Value;
        Assert.Contains("PreciseChunk_Execute.cpp", precise);
        Assert.Contains("PreciseChunk_Execute_Adapter.cpp", precise);
        Assert.Contains("add_library(NativeTranspiledPrecise OBJECT", cmake);
    }

    [Theory]
    [InlineData("Cpp", "Disabled", false)]
    [InlineData("Cpp", "Enabled", false)]
    [InlineData("Ispc", "Disabled", false)]
    [InlineData("Ispc", "Disabled", true)]
    public void BooleanVariantNamesMatchAcrossSourcesWrappersAndBindings(string backend, string simd, bool tasks)
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target = BackendTarget." + backend + ", AutoSIMD = AutoSIMD." + simd
            + ", UseISPC_MT = " + (tasks ? "true" : "false") + @")]
public struct VariantJob : IJobParallelFor {
    public NativeArray<int> Output; public bool First, Second;
    public void Execute(int index) {
        int value = index;
        if (First) value += 1;
        if (Second) value += 2;
        Output[index] = value;
    }
}");
        foreach (string suffix in new[] { "_false_false", "_true_false", "_false_true", "_true_true" })
        {
            Assert.Contains("VariantJob_Execute_Batch" + suffix, result.Cpp);
            Assert.Contains("VariantJob_Execute_Batch" + suffix, result.Bindings);
            if (tasks)
            {
                Assert.Contains(suffix + "_mt_impl", result.Cpp);
                Assert.Contains(suffix + "_mt", result.Bindings);
            }
        }
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", result.Cpp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompilerVectorizeUsesPreciseCompilationUnlessFastMathIsExplicit(bool fastMath)
    {
        string option = fastMath ? ", CppMathLib = CppMathLib.fast" : "";
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.ECS;
public struct FloatComponent { public float Value; }
[NativeTranspile(AutoSIMD = AutoSIMD.Vectorize" + option + @")]
public struct VectorizePrecisionJob : IJobEntity {
    public float Scale;
    public void Execute(ref FloatComponent value) { value.Value += value.Value * Scale; }
}");
        string cmake = System.IO.File.ReadAllText(System.IO.Path.Combine(result.OutputDir, "CMakeLists.txt"));
        var precise = System.Text.RegularExpressions.Regex.Match(cmake, @"set\(AUTOSIMD_SOURCES([\s\S]*?)\)").Groups[1].Value;
        const string name = "SharpNative_Job__global_namespace__VectorizePrecisionJob_Execute";
        Assert.Equal(!fastMath, precise.Contains(name + ".cpp"));
        Assert.Equal(!fastMath, precise.Contains(name + "_Adapter.cpp"));
    }

    [Theory]
    [InlineData("end--;")]
    [InlineData("if (i == 2) break;")]
    [InlineData("if (i == 2) return;")]
    public void VaryingReductionWithMutableBoundOrExitUsesMaskLoop(string body)
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct Reduction : IJobParallelFor {
    public NativeArray<int> Starts, Ends, Values, Output;
    public void Execute(int index) {
        int end = Ends[index]; int best = int.MaxValue;
        for (int i = Starts[index]; i < end; i++) {
            int candidate = Values[index] - i;
            if (candidate < best) best = candidate;
            " + body + @"
        }
        Output[index] = best;
    }
}");
        Assert.Contains("SIMD mask-loop", result.Cpp);
        Assert.DoesNotContain("maxIter", result.Cpp);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", result.Cpp);
    }

    [Fact]
    public void PointerLocalShadowRetainsLocalSymbol()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile] public unsafe struct Shadow : IJobParallelFor {
    public int* Data; public NativeArray<int> Output;
    public void Execute(int index) { int Data = index + 7; Output[index] = Data; }
}");
        Assert.Contains("Output_ptr[index] = Data;", result.Cpp);
        Assert.DoesNotContain("Output_ptr[index] = Data_ptr;", result.Cpp);
    }

    [Fact]
    public void BoolValuesAndLogicalOperatorsUseTypedMasks()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)] public struct BooleanValues : IJobParallelFor {
    public NativeArray<int> Output; public bool Active;
    public void Execute(int index) {
        bool Active = (index & 1) == 0;
        bool other = index < 3;
        Active = !Active;
        bool result = Active && other;
        bool selected = other ? Active : !Active;
        Output[index] = (result || selected) && (Active == other) ? 11 : 22;
    }
}");
        Assert.DoesNotContain("v_Active.m", result.Cpp);
        Assert.DoesNotContain("v_other.m", result.Cpp);
        Assert.DoesNotContain("broadcast(v_", result.Cpp);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", result.Cpp);
        Assert.Contains("n_and_mask", result.Cpp);
        Assert.Contains("n_or_mask", result.Cpp);
    }
}
