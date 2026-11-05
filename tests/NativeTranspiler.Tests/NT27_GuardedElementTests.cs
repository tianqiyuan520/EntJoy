using System.IO;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class NT27_GuardedElementTests
    {
        private static string Emit(string body)
        {
            var result = GeneratorHarness.EmitForJob(@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile] public struct GuardJob : IJobParallelFor {
 public NativeArray<int> Values; public int Limit;
 public void Execute(int index) { " + body + @" }
}");
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            return File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__GuardJob_Execute.cpp"));
        }

        [Theory]
        [InlineData("Values[index] = index; if (index < Limit && index > 0) Values[index] += 1;")]
        [InlineData("if (index < Limit && index > 0) Values[index] = 1; Values[index] += 2;")]
        [InlineData("if (index > 0) { if (index < Limit && index > 1) Values[index] = 1; } else Values[index] = 2;")]
        [InlineData("if (index < Limit && index > 0) Values[index] = 1; else Values[index] = 2;")]
        public void SurroundingOrAlternativeWork_KeepsTheFullIterationRange(string body)
        {
            var cpp = Emit(body);
            Assert.Contains("index < __startIndex + __count", cpp);
            Assert.DoesNotContain("std::min(__startIndex + __count, Limit)", cpp);
            Assert.Contains("index < Limit &&", cpp);
        }

        [Fact]
        public void EntireGuardedBody_PreservesConditionWithBoundedIteration()
        {
            var cpp = Emit("if (index < Limit && index > 0) Values[index] = index;");
            Assert.Contains("std::min(__startIndex + __count, Limit)", cpp);
            Assert.Contains("if (index < Limit && index > 0)", cpp);
        }

        [Theory]
        [InlineData("if (index < Limit && index > 0) { Limit = 0; Values[index] = index; }")]
        [InlineData("if (index < Limit && index > 0) { int* bound = &Limit; Values[index] = *bound; }")]
        public void MutableOrAddressedBound_KeepsTheFullIterationRange(string body)
        {
            var cpp = Emit(body);
            Assert.DoesNotContain("std::min(__startIndex + __count, Limit)", cpp);
            Assert.Contains("index < Limit &&", cpp);
        }
    }
}
