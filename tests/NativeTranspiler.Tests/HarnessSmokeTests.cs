using System;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>夹具自检：确认进程内驱动生成器能拿到 C++ 产物 + C# 绑定 + 诊断。</summary>
    public class HarnessSmokeTests
    {
        private readonly ITestOutputHelper _out;
        public HarnessSmokeTests(ITestOutputHelper output) => _out = output;

        private const string ParallelForJob = @"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile]
public struct SmokeJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public float Value;
    public void Execute(int index)
    {
        Out[index] = Value;
    }
}
";

        [Fact]
        public void EmitsCppAndBindings_ForSimpleParallelForJob()
        {
            var result = GeneratorHarness.EmitFor(ParallelForJob);
            _out.WriteLine(result.DiagnosticSummary);

            Assert.Contains("SharpNative_Job__global_namespace__SmokeJob_Execute_Batch", result.Cpp);
            Assert.Contains("Out_ptr[index] = Value;", result.Cpp);
            Assert.Contains("SmokeJob", result.Bindings);
            Assert.False(result.HasDiagnostic("NT026"), GeneratorHarness.Fail(result, "生成器崩溃（NT026）"));
        }

        [Fact]
        public void EmptySource_ProducesNoArtifacts()
        {
            var result = GeneratorHarness.EmitFor("public class Nothing { }");
            Assert.DoesNotContain("// =====", result.Cpp);
        }
    }
}
