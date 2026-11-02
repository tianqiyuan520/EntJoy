using System.IO;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class NT18_HelperInliningTests
    {
        private static EmitResult Emit(string methodImpl)
            => GeneratorHarness.EmitForJob(@"
using NativeTranspiler;
using EntJoy.Collections;
using EntJoy.JobSystem;
using System.Runtime.CompilerServices;
namespace InlineProbe {
    public static class Helpers {
        " + methodImpl + @"
        public static int Transform(int x) { return x * 3 + 1; }
        public static int Chain(int x) { return Transform(x) + 2; }
    }
    [NativeTranspile]
    public struct HelperJob : IJobParallelFor {
        public NativeArray<int> Values;
        public void Execute(int index) { Values[index] = Helpers.Chain(Values[index]); }
    }
}");

        private static string ReadHelper(EmitResult result, string method)
        {
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            string path = Path.Combine(result.OutputDir, "SharpNative_InlineProbe_Helpers_" + method);
            Assert.False(File.Exists(path + ".cpp"));
            Assert.DoesNotContain("Helpers_" + method, result.Bindings);
            return File.ReadAllText(path + ".h");
        }

        [Fact]
        public void OrdinaryAndTransitiveHelpers_KeepDefaultInliningAndBodiesInHeaders()
        {
            var result = Emit("");
            var transform = ReadHelper(result, "Transform");
            var chain = ReadHelper(result, "Chain");
            Assert.Contains("static __forceinline int SharpNative_InlineProbe_Helpers_Transform(", transform);
            Assert.Contains("static __forceinline int SharpNative_InlineProbe_Helpers_Chain(", chain);
            Assert.Contains("SharpNative_InlineProbe_Helpers_Transform(x)", chain);
            Assert.DoesNotContain("always_inline", transform + chain);
            Assert.DoesNotContain("GENERATED_API", transform + chain);
            Assert.Contains("EJ_BATCH_ALWAYS_INLINE", result.Cpp);
        }

        [Theory]
        [InlineData("MethodImplOptions.AggressiveInlining")]
        [InlineData("MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization")]
        [InlineData("(short)256")]
        public void AggressiveInlining_IsPreservedForBothCompilerFamilies(string options)
        {
            var result = Emit("[MethodImpl(" + options + ")]");
            var helper = ReadHelper(result, "Transform");
            Assert.Contains("#if defined(_MSC_VER)\nstatic __forceinline", helper);
            Assert.Contains("static inline __attribute__((always_inline))", helper);
            Assert.DoesNotContain("noinline", helper);
            Assert.Contains("static __forceinline int SharpNative_InlineProbe_Helpers_Chain(", ReadHelper(result, "Chain"));
        }

        [Theory]
        [InlineData("MethodImplOptions.NoInlining")]
        [InlineData("MethodImplOptions.NoInlining | MethodImplOptions.AggressiveInlining")]
        public void NoInlining_IsPreservedAndTakesPrecedence(string options)
        {
            var helper = ReadHelper(Emit("[MethodImpl(" + options + ")]"), "Transform");
            Assert.Contains("static __declspec(noinline)", helper);
            Assert.Contains("static __attribute__((noinline))", helper);
            Assert.DoesNotContain("__forceinline", helper);
            Assert.DoesNotContain("always_inline", helper);
        }
    }
}
