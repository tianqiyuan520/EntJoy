using System.IO;
using System.Linq;
using Xunit;

namespace NativeTranspiler.Tests;

public class IspcEntryContractTests
{
    [Fact]
    public void SingleJobExecutesOnceAndWritesItsActualFields()
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)] public struct OnceJob : IJob {
 public int Counter; public NativeArray<int> Output;
 public void Execute() { Counter++; for(int k=0;k<4;k++) Counter+=k; Output[0]=Counter; }
}");
        var source = File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__OnceJob_Execute.ispc"));
        Assert.Contains("if (programIndex != 0) return;", source);
        Assert.Contains("(*Counter_ptr)++", source);
        Assert.Contains("for (uniform int k", source);
        Assert.DoesNotContain("foreach", source);
    }

    [Theory]
    [InlineData("int", "int")]
    [InlineData("long", "int64")]
    [InlineData("ushort", "uint16")]
    public void SequentialLoopsRetainTheirTypeAndOriginalHeader(string type, string native)
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)] public struct LoopJob : IJob {
 public NativeArray<int> Output;
 public void Execute() { for(" + type + @" k=2; k<7; k+=2) Output[0]+=(int)k; }
}");
        Assert.Contains("for (uniform " + native + " k = 2; k < 7; k += 2)", result.Cpp);
    }

    [Fact]
    public void StaticReturnMethodUsesScalarValuesAndOriginalLoopOrder()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler;
public static class EntryMethods {
 [NativeTranspile(Target=BackendTarget.Ispc)] public static int Fold(int seed) {
  int value=seed; for(int k=0;k<4;k++) value=value*3+k; return value;
 }
}");
        var source = File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative__global_namespace__EntryMethods_Fold.ispc"));
        Assert.Contains("if (programIndex != 0) return;", source);
        Assert.Contains("uniform int value", source);
        Assert.DoesNotContain("foreach", source);
        Assert.Contains("*__result_ptr = value;", source);
    }

    [Fact]
    public void MultiTaskMethodsPreserveNestedLoopsAndSynchronizeSourceLoops()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.Collections;
public static class MultiMethods {
 [NativeTranspile(Target=BackendTarget.Ispc,UseISPC_MT=true)] public static void Fill(NativeArray<int> data) {
  for(int i=0;i<19;i++) data[i]=i;
  for(int i=0;i<19;i++) { for(int k=0;k<3;k++) data[i]+=k; }
 }
}");
        var source = File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative__global_namespace__MultiMethods_Fill_mt.ispc"));
        Assert.Contains("for (uniform int k", source);
        Assert.Equal(2, source.Split('\n').Count(line => line.Trim() == "sync;"));
        Assert.Contains("launch[max(1, numTasks)]", source);
    }

    [Fact]
    public void IspcRefLocalReportsADiagnosticInsteadOfCrashing()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem;
[NativeTranspile(Target=BackendTarget.Ispc)] public unsafe struct RefJob : IJob {
 public int* Data;
 public void Execute() { ref int value=ref Data[0]; value++; }
}");
        Assert.True(result.HasDiagnostic("NT035"));
        Assert.False(result.HasDiagnostic("NT026"));
    }

    [Fact]
    public void MutableBatchFieldsSelectTheSerializedFieldBinding()
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)] public struct CounterJob : IJobFor {
 public NativeArray<int> Output; public int Counter;
 public void Execute(int index) { Counter++; Output[index]=Counter; }
}");
        Assert.Contains("for (uniform int index", result.Cpp);
        Assert.Contains("(*Counter_ptr)++", result.Cpp);
    }

    [Fact]
    public void VoidAndNestedPointersHaveExplicitNativeMemoryQualifiers()
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.JobSystem;
[NativeTranspile(Target=BackendTarget.Ispc)] public unsafe struct PointerJob : IJob {
 public void* Data; public long** Table;
 public void Execute() { int* local=(int*)Data; *local=1; }
}");
        var source = File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__PointerJob_Execute.ispc"));
        Assert.Contains("void * uniform Data_ptr", source);
        Assert.Contains("uniform int64 * uniform * uniform Table_ptr", source);
        Assert.DoesNotContain("uniform void", source);
        Assert.Contains("uniform int * uniform local", source);
    }

    [Theory]
    [InlineData("2", "++i")]
    [InlineData("0", "i+=2")]
    public void NonCanonicalChunkEntityLoopsAreRejected(string start, string step)
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.ECS;
public struct Small { public byte Value; }
[NativeTranspile(Target=BackendTarget.Ispc)] public struct ChunkJob : IJobChunk {
 public void Execute(ArchetypeChunk chunk,in ChunkEnabledMask mask) {
  var values=chunk.GetComponentDataNativeArray<Small>();
  for(int i=" + start + ";i<values.Length;" + step + @") { var value=values[i]; value.Value=1; values[i]=value; }
 }
}");
        Assert.Contains("__ENTJOY_UNSUPPORTED_STMT__ISPC_ChunkLoopRequiresZeroBasedUnitStride", result.Cpp);
    }

    [Fact]
    public void ChunkLocalsKeepValueCopiesAndUseTheSharedTypeMapper()
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.ECS;
public struct Small { public byte Value; }
[NativeTranspile(Target=BackendTarget.Ispc)] public struct ChunkJob : IJobChunk {
 public void Execute(ArchetypeChunk chunk,in ChunkEnabledMask mask) {
  var values=chunk.GetComponentDataNativeArray<Small>();
  for(int i=0;i<values.Length;i++) { var value=values[i]; byte increment=1; value.Value=(byte)(value.Value+increment); values[i]=value; }
 }
}");
        Assert.Contains("Small value = values_ptr[i]", result.Cpp);
        Assert.Contains("uint8 increment", result.Cpp);
        Assert.Contains("values_ptr[i] = value", result.Cpp);
    }

    [Fact]
    public void MutableEntityFieldsSelectTheSerializedEntityBinding()
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.ECS;
public struct Value { public int Number; }
[NativeTranspile(Target=BackendTarget.Ispc)] public struct CountEntities : IJobEntity {
 public int Counter;
 public void Execute(ref Value value) { Counter++; value.Number=Counter; }
}");
        Assert.Contains("for (uniform int __entity_index", result.Cpp);
        Assert.Contains("(*Counter_ptr)++", result.Cpp);
    }

    [Theory]
    [InlineData("for(int i=0;i<values.Length;i++) { var v=values[i+1]; values[i]=v; }", "ISPC_ChunkCrossIndexAccess")]
    [InlineData("for(int i=0;i<values.Length;i++) { if(i==3) break; }", "ISPC_ChunkLoopBreak")]
    [InlineData("int sum=0; for(int i=0;i<values.Length;i++) sum+=values[i].Number;", "ISPC_ChunkOuterLocalWrite")]
    public void UnprovenChunkParallelismIsExplicitlyRejected(string loop, string marker)
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.ECS;
public struct Value { public int Number; }
[NativeTranspile(Target=BackendTarget.Ispc)] public struct ChunkJob : IJobChunk {
 public void Execute(ArchetypeChunk chunk,in ChunkEnabledMask mask) {
  var values=chunk.GetComponentDataNativeArray<Value>(); " + loop + @"
 }
}");
        Assert.Contains("__ENTJOY_UNSUPPORTED_STMT__" + marker, result.Cpp);
    }

    [Fact]
    public void MultiTaskJobsIncludeTheirLaneCallableHelpers()
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc,UseISPC_MT=true)] public struct MtJob : IJobParallelFor {
 public NativeArray<int> Output;
 public void Execute(int index) { Output[index]=Fold(index); }
 private static int Fold(int value) { for(int i=0;i<3;i++) value+=i; return value; }
}");
        var source = File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative_Job__global_namespace__MtJob_Execute_Batch_mt.ispc"));
        Assert.Contains("#include \"SharpNative__global_namespace__MtJob_Fold_helper.ispc\"", source);
        Assert.Contains("launch[max(1, numTasks)]", source);
    }

    [Fact]
    public void ExpressionBodiedStaticMethodsPreserveTheirResult()
    {
        var result = GeneratorHarness.EmitFor(@"using NativeTranspiler;
public static class Expressions {
 [NativeTranspile(Target=BackendTarget.Ispc)] public static int Value(int input)=>input*3+1;
}");
        var source = File.ReadAllText(Path.Combine(result.OutputDir, "SharpNative__global_namespace__Expressions_Value.ispc"));
        Assert.Contains("*__result_ptr =", source);
        Assert.Contains("(*input_ptr)", source);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", source);
    }

    [Fact]
    public void ChunkScalarSideEffectsAreNotRepeatedAcrossLanes()
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.ECS; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)] public unsafe struct ChunkAtomic : IJobChunk {
 public NativeArray<int> Counter;
 public void Execute(ArchetypeChunk chunk,in ChunkEnabledMask mask) {
  System.Threading.Interlocked.Increment(ref ((int*)Counter.GetUnsafePtr())[0]);
 }
}");
        Assert.Contains("__ENTJOY_UNSUPPORTED_STMT__ISPC_ChunkScalarCall", result.Cpp);
    }
}
