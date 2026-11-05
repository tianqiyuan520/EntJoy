using System.IO;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class NT21_SmallLoopTests
    {
        private static string Emit(string body)
        {
            var result = GeneratorHarness.EmitForJob(@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile] public struct LoopJob : IJobParallelFor {
 public NativeArray<int> Values; public int Limit;
 public void Execute(int index) { " + body + @" }
}");
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            return File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__LoopJob_Execute.cpp"));
        }

        [Theory]
        [InlineData("for(int k=-1;k<=1;k++) { if(k==0) continue; Values[index] += k; }")]
        [InlineData("for(int k=0;k<8;++k) { if(Values[index]<k) break; Values[index] = k; }")]
        [InlineData("for(int k=0;k<8;k+=2) { if(Values[index]<k) break; Values[index] = k; }")]
        public void ShortFixedLoop_PreservesControlFlowAndAddsCompilerHint(string body)
        {
            var cpp = Emit(body);
            Assert.Contains("#if defined(__clang__)\n#pragma clang loop unroll(full)\n#endif", cpp.Replace("\r\n", "\n"));
            Assert.Contains(body.Contains("continue") ? "continue;" : "break;", cpp);
        }

        [Theory]
        [InlineData("for(int k=0;k<Limit;k++) Values[index] = k;")]
        [InlineData("for(int k=0;k<64;k++) Values[index] = k;")]
        [InlineData("for(int k=0;k<8;k++) { if(k==2) k++; Values[index] = k; }")]
        public void DynamicLargeOrMutableLoop_UsesDefaultPolicy(string body)
            => Assert.DoesNotContain("#pragma clang loop unroll", Emit(body));

        [Fact]
        public void NestedLoops_OnlyHintInnermostLoop()
        {
            var cpp = Emit("for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++) Values[index] = x+y;");
            Assert.Equal(1, cpp.Split(new[] { "#pragma clang loop unroll(full)" }, System.StringSplitOptions.None).Length - 1);
        }
    }
}
