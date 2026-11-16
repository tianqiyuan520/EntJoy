using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class RelationshipReadOnlyTranslationTests
    {
        private const string Types = @"
namespace EntJoy.ECS {
 public sealed class RelationshipComponentAttribute : System.Attribute {
  public bool Multiple { get; set; }
  public int MaxTargets { get; set; }
  public bool Hierarchy { get; set; }
  public bool ExclusiveTarget { get; set; }
 }
 public interface IRelationComponent { }
 public struct RelationSlot { public int TargetId; public int TargetVersion; }
}
public struct Link : EntJoy.ECS.IRelationComponent { public EntJoy.ECS.RelationSlot Target; }
public struct Output { public int Value; }
";

        private static EmitResult EmitChunk(string accessor, string backend = "Cpp", string simd = "Disabled") => GeneratorHarness.EmitFor(@"
using EntJoy.ECS; using NativeTranspiler;
" + Types + @"
[NativeTranspile(Target = BackendTarget." + backend + @", AutoSIMD = AutoSIMD." + simd + @")]
public struct ReadLink : IJobChunk {
 public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
  var links = chunk." + accessor + @"<Link>();
  var output = chunk.GetComponentDataSpan<Output>();
  for (int i = 0; i < chunk.Count; i++) output[i].Value = links[i].Target.TargetId;
 }
}");

        [Theory]
        [InlineData("", false)]
        [InlineData("Hierarchy = true", false)]
        [InlineData("ExclusiveTarget = true", false)]
        [InlineData("Multiple = true", true)]
        [InlineData("MaxTargets = 4", false)]
        [InlineData("Multiple = true, MaxTargets = 4", false)]
        public void UnifiedDeclaration_RejectsOnlyManagedListReads(string options, bool managedList)
        {
            var types = Types.Replace("public struct Link", "[EntJoy.ECS.RelationshipComponent(" + options + ")] public struct Link");
            foreach (var backend in new[] { "Cpp", "Ispc" })
            foreach (var chunk in new[] { false, true })
            {
                var job = chunk
                    ? "public struct ReadLink : IJobChunk { public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) { var links = chunk.GetComponentReadOnlyDataSpan<Link>(); var output = chunk.GetComponentDataSpan<Output>(); for (int i = 0; i < chunk.Count; i++) output[i].Value = links[i].Target.TargetId; } }"
                    : "public struct ReadLink : IJobEntity { public void Execute(in Link link, ref Output output) { output.Value = link.Target.TargetId; } }";
                var emitted = GeneratorHarness.EmitFor("using EntJoy.ECS; using NativeTranspiler;" + types
                    + "[NativeTranspile(Target = BackendTarget." + backend + ")] " + job);
                Assert.Equal(managedList, emitted.HasDiagnostic("NT017"));
                if (!managedList) Assert.DoesNotContain(emitted.Diagnostics, value => value.Severity == DiagnosticSeverity.Error);
            }
        }

        [Theory]
        [InlineData("GetComponentReadOnlyDataSpan", "Disabled")]
        [InlineData("GetComponentReadOnlyDataNativeArray", "Disabled")]
        [InlineData("GetComponentReadOnlyDataSpan", "Enabled")]
        [InlineData("GetComponentReadOnlyDataNativeArray", "Enabled")]
        [InlineData("GetComponentReadOnlyDataSpan", "Vectorize")]
        [InlineData("GetComponentReadOnlyDataNativeArray", "Vectorize")]
        public void CppReadOnlyColumns_EmitConstPointers(string accessor, string simd)
        {
            var emitted = EmitChunk(accessor, simd: simd);
            Assert.DoesNotContain(emitted.Diagnostics, value => value.Severity == DiagnosticSeverity.Error);
            Assert.Contains("reinterpret_cast<const Link*>", emitted.Cpp);
        }

        [Theory]
        [InlineData("GetComponentReadOnlyDataSpan")]
        [InlineData("GetComponentReadOnlyDataNativeArray")]
        public void IspcReadOnlyColumns_StayInTheSupportedChunkLoop(string accessor)
        {
            var emitted = EmitChunk(accessor, "Ispc");
            Assert.DoesNotContain(emitted.Diagnostics, value => value.Severity == DiagnosticSeverity.Error);
            Assert.Contains("uniform const Link links_ptr[]", emitted.Cpp);
            Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", emitted.Cpp);
            Assert.DoesNotContain("#include \"System_ReadOnlySpan", emitted.Cpp);
            Assert.DoesNotContain("#include \"System_Span", emitted.Cpp);
        }

        [Theory]
        [InlineData("GetComponentDataSpan")]
        [InlineData("GetComponentDataNativeArray")]
        public void MutableRelationshipAccess_IsRejected(string accessor)
            => Assert.True(EmitChunk(accessor).HasDiagnostic("NT036"));

        [Theory]
        [InlineData("ref")]
        [InlineData("out")]
        public void WritableEntityRelationshipParameters_AreRejected(string modifier)
        {
            var emitted = GeneratorHarness.EmitFor(@"
using EntJoy.ECS; using NativeTranspiler;
" + Types + @"
[NativeTranspile] public struct WriteLink : IJobEntity {
 public void Execute(" + modifier + @" Link link) { link = default; }
}");
            Assert.True(emitted.HasDiagnostic("NT036"));
        }

        [Theory]
        [InlineData("Cpp", "Disabled")]
        [InlineData("Cpp", "Vectorize")]
        [InlineData("Ispc", "Disabled")]
        public void ValueEntityRelationshipParameters_AreLocalCopies(string backend, string simd)
        {
            var emitted = GeneratorHarness.EmitFor(@"
using EntJoy.ECS; using NativeTranspiler;
" + Types + @"
[NativeTranspile(Target = BackendTarget." + backend + @", AutoSIMD = AutoSIMD." + simd + @")]
public struct CopyLink : IJobEntity {
 public void Execute(Link link, ref Output output) {
  link.Target.TargetId += 5;
  output.Value = link.Target.TargetId;
 }
}");
            Assert.DoesNotContain(emitted.Diagnostics, value => value.Severity == DiagnosticSeverity.Error);
            Assert.Contains("Link link = ", emitted.Cpp);
            Assert.Contains(backend == "Cpp" ? "reinterpret_cast<const Link*>" : "uniform const Link link_ptr[]", emitted.Cpp);
        }
    }
}
