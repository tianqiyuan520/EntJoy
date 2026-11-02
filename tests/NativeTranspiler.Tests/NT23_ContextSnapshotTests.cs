using System.IO;
using System.Linq;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class NT23_ContextSnapshotTests
    {
        private static string Adapter(string statement, string declarations = "", string fields = "", string options = "")
        {
            var result = GeneratorHarness.EmitForJob(@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.ECS;
public struct Value : IComponentData { public int Number; }
public struct PointData { public float x; public float y; }
" + declarations + @"
[NativeTranspile(" + options + @")] public unsafe struct SnapshotJob : IJobChunk {
 public NativeArray<int> Values; public int Count; public PointData Point;
 " + fields + @"
 public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabled) {
  var values=chunk.GetComponentDataNativeArray<Value>();
  for(int i=0;i<values.Length;i++) { " + statement + @" }
 }
}");
            return File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__SnapshotJob_Execute_Adapter.cpp"));
        }

        [Fact]
        public void ExternalElementWrites_SnapshotMetadataInAllThreeAdapters()
        {
            var cpp = Adapter("Values[i] = Count + (int)Point.x;");
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(cpp, "std::memcpy\\(__jobSnapshot").Count);
            Assert.Contains("#include <cstring>", cpp);
            Assert.Contains("__jobContext = reinterpret_cast<char*>(__jobSnapshot)", cpp);
        }

        [Fact]
        public void NativeColumnElementWrites_SnapshotMetadata()
            => Assert.Contains("__jobSnapshot", Adapter("Column[i] = Values[i] + Count;", fields: "public NativeColumn<int> Column;"));

        [Fact]
        public void NativeColumnElementPointerWrites_SnapshotMetadata()
            => Assert.Contains("__jobSnapshot", Adapter("System.Threading.Interlocked.Add(ref *Column.GetElementPtr(i), Count);", fields: "public NativeColumn<int> Column;"));

        [Fact]
        public void NativeColumnLengthWrite_UsesOriginalContext()
            => Assert.DoesNotContain("__jobSnapshot", Adapter("Column.Length = Count;", fields: "public NativeColumn<int> Column;"));

        [Theory]
        [InlineData("Count++;")]
        [InlineData("this.Count = i;")]
        [InlineData("(Point).x += i;")]
        [InlineData("this.Point.x++;")]
        [InlineData("Values = default;")]
        [InlineData("System.Threading.Interlocked.Increment(ref Count);")]
        [InlineData("fixed (float* p = &Point.x) { }")]
        [InlineData("ref float p = ref Point.x;")]
        public void WrittenOrEscapedMetadata_UsesOriginalContext(string statement)
            => Assert.DoesNotContain("__jobSnapshot", Adapter(statement));

        [Fact]
        public void MutableNestedProperty_UsesOriginalContext()
            => Assert.DoesNotContain("__jobSnapshot", Adapter("Values[i]=Meta.Next;",
                "public struct Metadata { public int N; public int Next { get { return ++N; } } }", "public Metadata Meta;"));

        [Fact]
        public void MutableNestedMethod_UsesOriginalContext()
            => Assert.DoesNotContain("__jobSnapshot", Adapter("Values[i]=Meta.Next();",
                "public struct Metadata { public int N; public int Next() { return ++N; } }", "public Metadata Meta;"));

        [Fact]
        public void NestedVolatileField_UsesOriginalContext()
            => Assert.DoesNotContain("__jobSnapshot", Adapter("Values[i]=Meta.N;",
                "public struct Metadata { public volatile int N; }", "public Metadata Meta;"));

        [Fact]
        public void LargeValueContext_UsesOriginalContext()
            => Assert.DoesNotContain("__jobSnapshot", Adapter("Values[i]=Count;",
                "public struct Metadata { " + string.Join(" ", Enumerable.Range(0, 260).Select(i => $"public int N{i};")) + " }",
                "public Metadata Meta;"));

        [Theory]
        [InlineData("public volatile int Guard;")]
        [InlineData("public fixed byte Padding[2048];")]
        public void VolatileOrLargeContexts_UseOriginalContext(string fields)
            => Assert.DoesNotContain("__jobSnapshot", Adapter("Values[i]=Count;", fields: fields));

        [Fact]
        public void ThisEscape_UsesOriginalContext()
            => Assert.DoesNotContain("__jobSnapshot", Adapter("var copy = this; Values[i]=copy.Count;"));

        [Fact]
        public void AutoSimd_UsesOriginalContext()
            => Assert.DoesNotContain("__jobSnapshot", Adapter("Values[i]=Count;", options: "AutoSIMD=AutoSIMD.Enabled"));
    }
}
