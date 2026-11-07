using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class ReadOnlyArrayFieldTranslationTests
    {
        [Theory]
        [InlineData("Cpp")]
        [InlineData("Ispc")]
        public void ReadOnlyArrayFields_UseElementPointersAndKeepConst(string backend)
        {
            var result = GeneratorHarness.EmitFor(@"
using EntJoy.Collections; using EntJoy.JobSystem; using NativeTranspiler;
public struct InputValue { public int Id; public int Version; }
[NativeTranspile(Target = BackendTarget." + backend + @")]
public struct ReadSnapshot : IJobParallelFor {
 public NativeArray<InputValue>.ReadOnly Input;
 public NativeArray<int> Output;
 public void Execute(int index) { Output[index] = Input[index].Id + Input.Length; }
}");
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Contains("Input_ptr[index].Id", result.Cpp);
            Assert.Contains(backend == "Cpp" ? "const InputValue" : "uniform const InputValue Input_ptr[]", result.Cpp);
            Assert.DoesNotContain("Input[index].Id", result.Cpp);
            Assert.DoesNotContain("ReadOnly__", result.Cpp);
            Assert.Contains("Input.GetUnsafeReadOnlyPtr()", result.Bindings);
            Assert.Contains("_InputValue.h", result.Cpp);
        }
    }
}
