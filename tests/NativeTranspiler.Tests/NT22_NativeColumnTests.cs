using System.IO;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class NT22_NativeColumnTests
    {
        [Theory]
        [InlineData("Target=BackendTarget.Ispc")]
        [InlineData("AutoSIMD=AutoSIMD.Enabled")]
        public void UnsupportedBackends_ReportExplicitBoundary(string options)
        {
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile(" + options + @")] public struct ColumnJob : IJobParallelFor {
 public NativeColumn<int> Values; public void Execute(int index) { Values[index] = index; }
}");
            Assert.Contains(result.Diagnostics, d => d.Id == "NT033" && d.Severity == DiagnosticSeverity.Error);
        }

        [Fact]
        public void ColumnView_UsesItsValueAbiAndIndexedNativeAccess()
        {
            var result = GeneratorHarness.EmitForJob(@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile] public unsafe struct ColumnJob : IJobParallelFor {
 public NativeColumn<int> Values; public int Addend;
 public void Execute(int index) {
  Values[index] += Addend;
  System.Threading.Interlocked.Add(ref *Values.GetElementPtr(index), 1);
 }
}");
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            var cpp = File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__ColumnJob_Execute.cpp"));
            Assert.Contains("EntJoy::Collections::NativeColumn<int>", cpp);
            Assert.Contains("(char*)__job + 32", cpp);
            Assert.Contains("Values[index] += Addend", cpp);
            Assert.Contains("Values.GetElementPtr(index)", cpp);
            Assert.DoesNotContain("Values_ptr[index]", cpp);
        }
    }
}
