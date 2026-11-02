using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class NT19_ZeroMaskTests
    {
        [Theory]
        [InlineData("int", "0")]
        [InlineData("uint", "0u")]
        [InlineData("ulong", "0UL")]
        public void ChunkMask_SpecializesAllScalarAdapterEntrypoints(string type, string zero)
        {
            var result = GeneratorHarness.EmitForJob(@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.ECS;
public struct Value : IComponentData { public int Number; }
[NativeTranspile] public struct ChunkMask : IJobChunk {
 public " + type + @" Flags;
 public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabled) {
  var values = chunk.GetComponentDataNativeArray<Value>();
  for(int i=0;i<values.Length;i++) {
   int n=0;
   if((Flags & 1)==" + zero + @") n+=1;
   if((Flags & 2)==" + zero + @") n+=10;
   if((Flags & 4)==" + zero + @") n+=100;
   Value value=values[i]; value.Number=n; values[i]=value;
  }
 }
}");
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            var cpp = File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__ChunkMask_Execute_Adapter.cpp"));
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(cpp, "template<bool __ejZeroMask>").Count);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(cpp, "_MaskImpl<true>").Count);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(cpp, "_MaskImpl<false>").Count);
            Assert.Contains("__header->allEnabledCount + __header->requiredComponentTypeIdCount", cpp);
        }

        [Fact]
        public void WrittenChunkMask_KeepsOriginalAdapters()
        {
            var result = GeneratorHarness.EmitForJob(@"
using NativeTranspiler; using EntJoy.ECS;
public struct Value : IComponentData { public int Number; }
[NativeTranspile] public struct ChunkMask : IJobChunk {
 public int Flags;
 public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabled) {
  var values=chunk.GetComponentDataNativeArray<Value>();
  for(int i=0;i<values.Length;i++) {
   Flags++;
   int n=0;
   if((Flags & 1)==0) n+=1;
   if((Flags & 2)==0) n+=2;
   if((Flags & 4)==0) n+=3;
   Value value=values[i]; value.Number=n; values[i]=value;
  }
 }
}");
            var cpp = File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__ChunkMask_Execute_Adapter.cpp"));
            Assert.DoesNotContain("template<bool __ejZeroMask>", cpp);
        }

        private static EmitResult Emit(string extra = "", string masks = "Flags")
            => GeneratorHarness.EmitForJob(@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile]
public struct MaskJob : IJobParallelFor {
    public NativeArray<int> Values;
    public int Flags;
    public void Execute(int index) {
        " + extra + @"
        int mode = " + masks + @";
        bool a = (mode & 1) == 0;
        bool b = (mode & 2) == 0;
        bool c = (mode & 4) == 0;
        int sum = 0;
        for (int k = 0; k < 8; k++) {
            if (a) sum += k;
            if (b) sum += 2;
            if (c) sum += 3;
        }
        Values[index] = sum;
    }
}");

        private static string Job(EmitResult result)
        {
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            return File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__MaskJob_Execute.cpp"));
        }

        [Fact]
        public void ReadOnlyMask_SelectsZeroOncePerBatch_WithOriginalFallbackAndAbi()
        {
            var result = Emit();
            var cpp = Job(result);
            Assert.Contains("template<bool __ejZeroMask>", cpp);
            Assert.Contains("const int Flags = __ejZeroMask ? __ejZeroMaskValue : *Flags_ptr;", cpp);
            Assert.Contains("if (*(int*)((char*)__job + 16) == 0)", cpp);
            Assert.Contains("_MaskImpl<true>(__job, __startIndex, __count);", cpp);
            Assert.Contains("_MaskImpl<false>(__job, __startIndex, __count);", cpp);
            Assert.Contains("MaskJob_Execute_Batch(void* __job, int __startIndex, int __count)", cpp);
        }

        [Theory]
        [InlineData("Flags = index;")]
        [InlineData("this.Flags = index;")]
        [InlineData("(this.Flags) = index;")]
        [InlineData("Flags++;")]
        [InlineData("System.Threading.Interlocked.Exchange(ref Flags, index);")]
        [InlineData("System.Threading.Interlocked.Exchange(ref (Flags), index);")]
        [InlineData("int* escaped = &Flags;")]
        public void MutableOrEscapedField_DoesNotSpecialize(string write)
        {
            // Address-of only needs unsafe context in the test source.
            var result = GeneratorHarness.EmitForJob((@"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile] public unsafe struct MaskJob : IJobParallelFor {
 public NativeArray<int> Values; public int Flags;
 public void Execute(int index) { " + write + @"
  for(int k=0;k<8;k++) {
   if((Flags & 1)==0) Values[index] += k;
   if((Flags & 2)==0) Values[index] += 2;
   if((Flags & 4)==0) Values[index] += 3;
  }
 }
}"));
            Assert.DoesNotContain("template<bool __ejZeroMask>", Job(result));
        }

        [Fact]
        public void ShadowedLocal_DoesNotSpecializeUnrelatedField()
            => Assert.DoesNotContain("template<bool __ejZeroMask>", Job(Emit("int Flags = index;")));

        [Fact]
        public void VolatileField_DoesNotSpecialize()
        {
            var source = @"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile] public struct MaskJob : IJobParallelFor {
 public NativeArray<int> Values; public volatile int Flags;
 public void Execute(int index) {
  for(int k=0;k<8;k++) {
   if((Flags & 1)==0) Values[index] += k;
   if((Flags & 2)==0) Values[index] += 2;
   if((Flags & 4)==0) Values[index] += 3;
  }
 }
}";
            Assert.DoesNotContain("template<bool __ejZeroMask>", Job(GeneratorHarness.EmitForJob(source)));
        }

        [Fact]
        public void OneMask_DoesNotDuplicateLoop()
        {
            var source = @"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
[NativeTranspile] public struct MaskJob : IJobParallelFor {
 public NativeArray<int> Values; public int Flags;
 public void Execute(int index) {
  for(int k=0;k<8;k++) if((Flags & 1)==0) Values[index] += k;
 }
}";
            Assert.DoesNotContain("template<bool __ejZeroMask>", Job(GeneratorHarness.EmitForJob(source)));
        }

        public static IEnumerable<object[]> IntegerMasks()
        {
            yield return new object[] { "sbyte", "signed char", "1", "2", "-128", 17 };
            yield return new object[] { "byte", "unsigned char", "1", "2", "128", 17 };
            yield return new object[] { "short", "short", "1", "2", "-32768", 18 };
            yield return new object[] { "ushort", "unsigned short", "1", "2", "32768", 18 };
            yield return new object[] { "char", "unsigned short", "1", "2", "32768", 18 };
            yield return new object[] { "int", "int", "1", "2", "-2147483648", 20 };
            yield return new object[] { "uint", "unsigned int", "1u", "2u", "0x80000000u", 20 };
            yield return new object[] { "long", "long long", "1L", "2L", "(-9223372036854775807L - 1L)", 24 };
            yield return new object[] { "ulong", "unsigned long long", "1UL", "2UL", "0x8000000000000000UL", 24 };
            yield return new object[] { "nint", "intptr_t", "(nint)1", "(nint)2", "(nint)4", 24 };
            yield return new object[] { "nuint", "uintptr_t", "(nuint)1", "(nuint)2", "(nuint)4", 24 };
        }

        private static string TypedMaskSource(string type, string first, string second, string third,
            string extra = "", string declarations = "") => @"
using NativeTranspiler; using EntJoy.Collections; using EntJoy.JobSystem;
" + declarations + @"
[NativeTranspile] public struct MaskJob : IJobParallelFor {
 public NativeArray<int> Values; public byte Padding; public " + type + @" Flags; public uint Sentinel;
 public void Execute(int index) {
  " + extra + @"
  for(int k=0;k<8;k++) {
   if((Flags & " + first + @")==0) Values[index] += k;
   if((Flags & " + second + @")==0) Values[index] += 2;
   if((Flags & " + third + @")==0) Values[index] += 3;
  }
 }
}";

        [Theory]
        [MemberData(nameof(IntegerMasks))]
        public void IntegerWidths_PreserveTypeAndAlignedDispatch(string type, string cppType,
            string first, string second, string third, int offset)
        {
            var cpp = Job(GeneratorHarness.EmitForJob(TypedMaskSource(type, first, second, third)));
            Assert.Contains("template<bool __ejZeroMask>", cpp);
            Assert.Contains($"static constexpr {cppType} __ejZeroMaskValue = 0;", cpp);
            Assert.Contains($"const {cppType} Flags = __ejZeroMask ? __ejZeroMaskValue : *Flags_ptr;", cpp);
            Assert.Contains($"if (*({cppType}*)((char*)__job + {offset}) == 0)", cpp);
        }

        [Theory]
        [MemberData(nameof(IntegerMasks))]
        public void IntegerWidths_MutableFieldUsesFallback(string type, string cppType,
            string first, string second, string third, int offset)
        {
            var cpp = Job(GeneratorHarness.EmitForJob(TypedMaskSource(type, first, second, third, "Flags = default;")));
            Assert.DoesNotContain("template<bool __ejZeroMask>", cpp);
            Assert.Contains($"{cppType}* Flags_ptr = ({cppType}*)((char*)__job + {offset});", cpp);
        }

        public static IEnumerable<object[]> EnumMasks()
        {
            foreach (var data in IntegerMasks())
                if ((string)data[0] is not ("char" or "nint" or "nuint")) yield return data;
        }

        [Theory]
        [MemberData(nameof(EnumMasks))]
        public void EnumMasks_UseUnderlyingWidth(string type, string cppType,
            string first, string second, string third, int offset)
        {
            string declaration = $"[System.Flags] public enum Mode : {type} {{ A={first}, B={second}, C={third} }}";
            var cpp = Job(GeneratorHarness.EmitForJob(TypedMaskSource("Mode", "Mode.A", "Mode.B", "Mode.C", declarations: declaration)));
            Assert.Contains("template<bool __ejZeroMask>", cpp);
            Assert.Contains($"static constexpr {cppType} __ejZeroMaskValue = 0;", cpp);
            Assert.Contains($"if (*({cppType}*)((char*)__job + {offset}) == 0)", cpp);
        }

        [Theory]
        [MemberData(nameof(EnumMasks))]
        public void EnumMasks_MutableFieldUsesFallback(string type, string cppType,
            string first, string second, string third, int offset)
        {
            string declaration = $"public enum Mode : {type} {{ A={first}, B={second}, C={third} }}";
            var cpp = Job(GeneratorHarness.EmitForJob(TypedMaskSource("Mode", "Mode.A", "Mode.B", "Mode.C", "Flags = default;", declaration)));
            Assert.DoesNotContain("template<bool __ejZeroMask>", cpp);
            Assert.Contains($"{cppType}* Flags_ptr = ({cppType}*)((char*)__job + {offset});", cpp);
        }

        [Theory]
        [MemberData(nameof(EnumMasks))]
        public void EnumComplement_KeepsUnderlyingWidth(string type, string cppType,
            string first, string second, string third, int offset)
        {
            string declaration = $"public enum Mode : {type} {{ A={first}, B={second}, C={third} }}";
            var cpp = Job(GeneratorHarness.EmitForJob(TypedMaskSource("Mode", "Mode.A", "Mode.B", "Mode.C",
                "if ((~Flags) == 0) Values[index] = -1;", declaration)));
            Assert.Contains($"({cppType})(~(Flags))", cpp);
            Assert.Contains($"if (*({cppType}*)((char*)__job + {offset}) == 0)", cpp);
        }

        [Fact]
        public void DynamicMasks_DoNotDuplicateLoop()
        {
            var source = TypedMaskSource("ulong", "1UL", "2UL", "Sentinel");
            Assert.DoesNotContain("template<bool __ejZeroMask>", Job(GeneratorHarness.EmitForJob(source)));
        }
    }
}
