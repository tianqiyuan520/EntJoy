using System.IO;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class NT20_ArrayPointerTests
    {
        private static string Emit(string body, string outputType = "int")
        {
            var result = GeneratorHarness.EmitForJob(@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile] public unsafe struct AliasJob : IJobParallelFor {
 public NativeArray<int> A;
 public NativeArray<" + outputType + @"> B;
 public void Execute(int index) { " + body + @" }
}");
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            return File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__AliasJob_Execute.cpp"));
        }

        [Theory]
        [InlineData("B[index] = A[index];")]
        [InlineData("A[index] = B[index];")]
        [InlineData("this.B[index] = this.A[index];")]
        [InlineData("B[index + 1] = A[index];")]
        [InlineData("B[index] = A[index] + 1;")]
        [InlineData("int* input = (int*)A.GetUnsafePtr(); int* output = (int*)B.GetUnsafePtr(); output[index] = input[index];")]
        public void StableHandles_CachePointers_WithoutAliasAssumptions(string body)
        {
            var cpp = Emit(body);
            Assert.Contains("int* const A_ptr = *(int**)((char*)__job + 0);", cpp);
            Assert.Contains("int* const B_ptr = *(int**)((char*)__job + 16);", cpp);
            Assert.DoesNotContain("__restrict", cpp);
        }

        [Fact]
        public void ArrayLengths_RemainIndependent()
        {
            var cpp = Emit("if (index < A.Length && index < B.Length) B[index] = A[index];");
            Assert.Contains("int& A_length = *(int*)((char*)__job + 8);", cpp);
            Assert.Contains("int& B_length = *(int*)((char*)__job + 24);", cpp);
            Assert.Contains("int* const A_ptr", cpp);
        }

        [Theory]
        [InlineData("B = A; B[index] = A[index];")]
        [InlineData("this.B = A; B[index] = A[index];")]
        [InlineData("NativeArray<int> A = B; B[index] = A[index];")]
        public void MutableOrEscapedHandle_DoesNotCache(string body)
            => Assert.DoesNotContain("int* const A_ptr", Emit(body));

        [Fact]
        public void DifferentElementTypes_CacheIndependently()
        {
            var cpp = Emit("B[index] = A[index];", "float");
            Assert.Contains("int* const A_ptr", cpp);
            Assert.Contains("float* const B_ptr", cpp);
        }

        [Fact]
        public void ReassignedOutput_PreservesItsPointerSlot()
        {
            var cpp = Emit("B = default; A[index] = index;");
            Assert.Contains("int* const A_ptr", cpp);
            Assert.Contains("int*& B_ptr", cpp);
        }

        [Theory]
        [InlineData("Reset(ref B);", "public static void Reset(ref NativeArray<int> value) { value = default; }")]
        [InlineData("ref NativeArray<int> alias = ref B; alias = A;", "")]
        [InlineData("NativeArray<int>* escaped = &B;", "")]
        public void IndirectWrites_PreserveMutableBindings(string action, string helper)
        {
            var result = GeneratorHarness.EmitForJob(@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile] public unsafe struct AliasJob : IJobParallelFor {
 public NativeArray<int> A, B;
 public void Execute(int index) { " + action + @" B[index] = A[index]; }
 " + helper + @"
}");
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            var cpp = File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__AliasJob_Execute.cpp"));
            Assert.Contains("int*& B_ptr", cpp);
        }

        [Fact]
        public void AllStableArrays_CacheAlongsideZeroMaskSpecialization()
        {
            var result = GeneratorHarness.EmitForJob(@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile] public struct AliasJob : IJobParallelFor {
 public NativeArray<int> A, B, C; public int Flags;
 public void Execute(int index) {
  for(int k=0;k<8;k++) {
   if((Flags & 1)==0) A[index] += k;
   if((Flags & 2)==0) B[index] += 2;
   if((Flags & 4)==0) C[index] += 3;
  }
 }
}");
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            var cpp = File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__AliasJob_Execute.cpp"));
            Assert.Contains("template<bool __ejZeroMask>", cpp);
            foreach (var name in new[] { "A", "B", "C" }) Assert.Contains("int* const " + name + "_ptr", cpp);
        }
    }
}
