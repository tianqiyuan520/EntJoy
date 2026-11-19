using Microsoft.CodeAnalysis;
using System.Linq;
using System.IO;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class RelationshipMaskTranslationTests
    {
        [Fact]
        public void ValidatorDiagnostics_UseDistinctIds()
        {
            var duplicates = typeof(NativeTranspiler.Analyzer.NativeTranspileValidator)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Select(field => field.GetValue(null)).OfType<DiagnosticDescriptor>()
                .GroupBy(descriptor => descriptor.Id).Where(group => group.Count() > 1).Select(group => group.Key);
            Assert.Empty(duplicates);
        }

        private static EmitResult Chunk(string backend, string condition, string simd = "Disabled") => GeneratorHarness.EmitFor(@"
using EntJoy.ECS; using NativeTranspiler;
public struct Value { public int Count; }
[NativeTranspile(Target = BackendTarget." + backend + @", AutoSIMD = AutoSIMD." + simd + @")]
public struct Filtered : IJobChunk {
 public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
  var values = chunk.GetComponentDataSpan<Value>();
  for (int i = 0; i < chunk.Count; i++) {
   if (" + condition + @") continue;
   values[i].Count += 1;
  }
 }
}");

        [Theory]
        [InlineData("Cpp")]
        [InlineData("Ispc")]
        public void MaskLengthAndIndex_AreNativeIntrinsics(string backend)
        {
            var emitted = Chunk(backend, "mask.Length != 0 && !mask.IsEnabled(i)");
            Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", emitted.Cpp);
            Assert.Contains("EntityMatchMask __queryMask", emitted.Cpp);
            Assert.Contains(backend == "Cpp" ? "__queryMask.IsEnabled(i)" : "__query_bits[(i) >> 6]", emitted.Cpp);
        }

        [Fact]
        public void ManualSimd_ReportsItsExplicitMaskLimitation()
            => Assert.True(Chunk("Cpp", "mask.Length != 0 && !mask.IsEnabled(i)", "Enabled").HasDiagnostic("NT031"));

        [Theory]
        [InlineData("Cpp")]
        [InlineData("Ispc")]
        public void EntityLoops_ApplyTheQueryMaskWithoutRelationshipParameters(string backend)
        {
            var emitted = GeneratorHarness.EmitFor(@"
using EntJoy.ECS; using NativeTranspiler;
public struct Value { public int Count; }
[NativeTranspile(Target = BackendTarget." + backend + @")]
public struct SelectedEntity : IJobEntity { public void Execute(ref Value value) { value.Count++; } }");
            Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", emitted.Cpp);
            Assert.Contains(backend == "Cpp" ? "!__queryMask.IsEnabled(__entity_index)" : "__query_bits[__entity_index >> 6]", emitted.Cpp);
            Assert.Contains("NativeTranspiled_GetAbiVersion()", emitted.Cpp);
        }

        [Fact]
        public void CppEntityLoop_SeparatesUnfilteredAndFilteredPaths()
        {
            var emitted = GeneratorHarness.EmitFor(@"
using EntJoy.ECS; using NativeTranspiler;
public struct Value { public int Count; }
[NativeTranspile(Target = BackendTarget.Cpp)]
public struct SelectedEntity : IJobEntity { public void Execute(ref Value value) { value.Count++; } }");
            Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            var start = emitted.Cpp.IndexOf("if (__queryMask.Length == 0)");
            Assert.True(start >= 0);
            var maskCheck = emitted.Cpp.IndexOf("!__queryMask.IsEnabled(__entity_index)", start);
            Assert.True(maskCheck > start);
            var unfiltered = emitted.Cpp.Substring(start, maskCheck - start);
            Assert.Contains("for (int __entity_index", unfiltered);
            Assert.Contains("else", unfiltered);
            Assert.DoesNotContain("__queryMask.IsEnabled", unfiltered);
        }

        [Fact]
        public void CppEntityAdapters_LeaveMaskEvaluationToEntityLoop()
        {
            var emitted = GeneratorHarness.EmitForJob(@"
using EntJoy.ECS; using NativeTranspiler;
public struct Value { public int Count; }
[NativeTranspile(Target = BackendTarget.Cpp)]
public struct SelectedEntity : IJobEntity { public void Execute(ref Value value) { value.Count++; } }");
            Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            var source = File.ReadAllText(Assert.Single(Directory.GetFiles(
                emitted.OutputDir, "*SelectedEntity*_Execute.cpp", SearchOption.AllDirectories)));
            // 适配函数是同一文件的尾部：从这里往后的文本里不得重新求值掩码。
            int adapterStart = source.IndexOf("_RangeAdapter(", System.StringComparison.Ordinal);
            Assert.True(adapterStart > 0, "RangeAdapter 未生成");
            var adapters = source.Substring(adapterStart);
            Assert.Contains("_RangeAdapter(", adapters);
            Assert.DoesNotContain("EntityMatchMask __queryMask", adapters);
            Assert.Contains("EntityMatchMask __queryMask", emitted.Cpp);
        }
    }
}
