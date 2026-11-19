using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

public class IspcTranslationQualityTests
{
    private static string Emit(string body, string kind = "IJobParallelFor")
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)]
public unsafe struct TranslationJob : " + kind + @" {
 public NativeArray<int> Input; public NativeArray<int> Output;
 public int Begin; public int End;
 public void Execute(int index) { " + body + @" }
}");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return File.ReadAllText(Path.Combine(result.OutputDir,
            "SharpNative_Job__global_namespace__TranslationJob_Execute_Batch.ispc"));
    }

    [Theory]
    [InlineData("IJobParallelFor")]
    [InlineData("IJobFor")]
    public void BatchRangeComesFromTheScheduler(string kind)
    {
        var source = Emit("Output[index]=Input[index % Input.Length]+index;", kind);
        Assert.Contains("foreach (__batchRel = 0 ... __count)", source);
        Assert.Contains("int index = __batchRel + __startIndex;", source);
        Assert.DoesNotContain("min(__startIndex + __count", source);
    }

    [Fact]
    public void AffineIndexUsesShiftedBasePointer()
    {
        var source = Emit("Output[index]=Input[index+1]+Input[index];");
        Assert.Contains("(Input_ptr + __startIndex)[__batchRel]", source);
        Assert.Contains("(Input_ptr + __startIndex)[__batchRel + (1)]", source);
        Assert.Contains("(Output_ptr + __startIndex)[__batchRel]", source);
    }

    [Fact]
    public void NegativeOffsetKeepsItsSign()
    {
        var source = Emit("Output[index]=Input[index-1]+Input[index];");
        Assert.Contains("(Input_ptr + __startIndex)[__batchRel - (1)]", source);
        Assert.Contains("(Input_ptr + __startIndex)[__batchRel]", source);
    }

    [Fact]
    public void ShadowedIndexLocalIsNotTheBatchIndex()
    {
        // 体内 for 自带同名局部 index：里面那次访问用的是内层局部，不能当成批索引折算。
        var source = Emit("int acc=0; for (int index = 0; index < 4; index++) acc += Input[index]; Output[index]=acc;");
        Assert.Contains("Input_ptr[index]", source);
        Assert.DoesNotContain("(Input_ptr + __startIndex)[__batchRel]", source);
    }

    [Fact]
    public void NonAffineIndexKeepsAbsoluteAddressing()
    {
        var source = Emit("Output[index]=Input[index % Input.Length];");
        Assert.DoesNotContain("(Input_ptr + __startIndex)[", source);
        Assert.Contains("Input_ptr[index % ", source);
    }

    [Fact]
    public void SoleElementwiseLoopBecomesForeach()
    {
        var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler; using EntJoy.Collections;
public static class StcFuncs {
    [NativeTranspile(Target=BackendTarget.Ispc)]
    public static void StcIspc(NativeArray<float> a, NativeArray<float> b, NativeArray<float> result, int count) {
        for (int i = 0; i < count; i++) result[i] = a[i] * b[i];
    }
}");
        var source = ReadGenerated(result, "StcIspc.ispc");
        Assert.Contains("foreach (i = 0 ... ", source);
        Assert.DoesNotContain("programIndex != 0", source);
    }

    [Fact]
    public void CrossIterationDependencyKeepsScalarLoop()
    {
        // a[i] 读 a[i-1]：跨迭代依赖，foreach 会算错。
        var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler; using EntJoy.Collections;
public static class StcFuncs {
    [NativeTranspile(Target=BackendTarget.Ispc)]
    public static void StcIspc(NativeArray<float> a, int count) {
        for (int i = 1; i < count; i++) a[i] = a[i - 1] + 1.0f;
    }
}");
        var source = ReadGenerated(result, "StcIspc.ispc");
        Assert.DoesNotContain("foreach", source);
        Assert.Contains("programIndex != 0", source);
    }

    [Fact]
    public void SingleJobElementwiseLoopBecomesForeach()
    {
        // IJob 没有调度索引，唯一的数据循环同样要摊到整条 gang 上。
        var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)]
public struct ReduceJob : IJob {
    public NativeArray<float> A;
    public NativeArray<float> Result;
    public int Count;
    public void Execute() {
        for (int i = 0; i < Count; i++) Result[i] = A[i] * 2.0f;
    }
}");
        var source = ReadGenerated(result, "ReduceJob_Execute.ispc");
        Assert.Contains("foreach (i = 0 ... ", source);
        Assert.DoesNotContain("programIndex != 0", source);
    }

    [Fact]
    public void StaticMethodAndJobPathsAgreeOnShape()
    {
        // 两条"没有调度索引"的入口必须发同样的形态，否则后续改动会只改一边。
        string body = @"
        for (int i = 0; i < Count; i++) Out[i] = In[i] * 2.0f;";
        var job = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)]
public struct ParJob : IJob {
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public int Count;
    public void Execute() {" + body + @"
    }
}");
        var method = GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.Collections;
public static class ParFuncs {
    [NativeTranspile(Target=BackendTarget.Ispc)]
    public static void ParFunc(NativeArray<float> In, NativeArray<float> Out, int Count) {" + body + @"
    }
}");
        var jobSource = ReadGenerated(job, "ParJob_Execute.ispc");
        var methodSource = ReadGenerated(method, "ParFunc.ispc");
        foreach (var source in new[] { jobSource, methodSource })
        {
            Assert.Contains("foreach (i = 0 ... ", source);
            Assert.DoesNotContain("programIndex != 0", source);
        }
    }

    [Fact]
    public void EntityScalarFieldsFlattenToSlots()
    {
        // 组件是多个同类型标量字段、每个字段一条同构语句时，也应摊平成「一 lane 一分量」。
        var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler; using EntJoy.ECS; using EntJoy.JobSystem; using EntJoy.Collections;
public struct FlatL2 { public float X; public float Y; }
[NativeTranspile(Target=BackendTarget.Ispc)]
public struct FlatEntityJob : IJobEntity {
    public void Execute(ref FlatL2 p, in FlatL2 q) {
        p.X = p.X + q.X * 2.0f;
        p.Y = p.Y + q.Y * 2.0f;
    }
}");
        var source = ReadGenerated(result, "FlatEntityJob_Execute.ispc");
        Assert.Contains("__slots = 2", source);
        Assert.Contains("((float*)p_ptr)[__flat] = ((float*)p_ptr)[__flat] + ((float*)q_ptr)[__flat] * 2.0f;", source);
    }

    [Fact]
    public void EntityScalarFieldsWithDifferentConstantsFlattenWithSelect()
    {
        // 只有常量不同也摊平：常量变成按 __flat % __slots 取出的 varying 值。
        var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler; using EntJoy.ECS; using EntJoy.JobSystem; using EntJoy.Collections;
public struct FlatL3 { public float X; public float Y; }
[NativeTranspile(Target=BackendTarget.Ispc)]
public struct FlatEntityJob2 : IJobEntity {
    public void Execute(ref FlatL3 p, in FlatL3 q) {
        p.X = p.X + q.X * 5.0f;
        p.Y = p.Y + q.Y * 3.0f;
    }
}");
        var source = ReadGenerated(result, "FlatEntityJob2_Execute.ispc");
        Assert.Contains("__slots = 2", source);
        Assert.Contains("__flat % __slots == 0 ? 5.0f : 3.0f", source);
    }

    [Fact]
    public void EntityScalarFieldsWithSameShapeFlatten()
    {
        var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler; using EntJoy.ECS; using EntJoy.JobSystem; using EntJoy.Collections;
public struct FlatL4 { public float X; public float Y; public float Z; }
[NativeTranspile(Target=BackendTarget.Ispc)]
public struct FlatEntityJob3 : IJobEntity {
    public void Execute(ref FlatL4 p, in FlatL4 q) {
        p.X = p.X * 2.0f + q.X;
        p.Y = p.Y * 2.0f + q.Y;
        p.Z = p.Z * 2.0f + q.Z;
    }
}");
        var source = ReadGenerated(result, "FlatEntityJob3_Execute.ispc");
        Assert.Contains("__slots = 3", source);
    }

    [Fact]
    public void ChunkComponentMemberAccessKeepsWholeStructLoop()
    {
        // 摊平后一个 slot 是标量，`.Value.x` 拿不到分量：整条循环保持按结构体处理。
        var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler; using EntJoy.ECS; using EntJoy.JobSystem; using EntJoy.Collections; using EntJoy.Mathematics;
public struct V2 { public float2 Value; }
[NativeTranspile(Target=BackendTarget.Ispc)]
public struct MemberJob : IJobChunk {
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        NativeArray<V2> ps = chunk.GetComponentDataNativeArray<V2>();
        for (int index = 0; index < ps.Length; index++) {
            V2 t = ps[index];
            float x = t.Value.x;
            t.Value.y = x + 1.0f;
            ps[index] = t;
        }
    }
}");
        var source = ReadGenerated(result, "MemberJob_Execute.ispc");
        Assert.DoesNotContain("__flat", source);
        Assert.Contains("t.Value.y", source);
    }

    [Fact]
    public void ChunkCopyLoopFlattensToSlots()
    {
        var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler; using EntJoy.ECS; using EntJoy.JobSystem; using EntJoy.Collections; using EntJoy.Mathematics;
public struct FlatC1 { public float2 Value; }
[NativeTranspile(Target=BackendTarget.Ispc)]
public struct FlatChunkJob : IJobChunk {
    public float DeltaTime;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask) {
        NativeArray<FlatC1> ps = chunk.GetComponentDataNativeArray<FlatC1>();
        NativeArray<FlatC1> vs = chunk.GetComponentDataNativeArray<FlatC1>();
        for (int index = 0; index < ps.Length; index++) {
            FlatC1 t = ps[index];
            t.Value += vs[index].Value * DeltaTime;
            ps[index] = t;
        }
    }
}");
        var source = ReadGenerated(result, "FlatChunkJob_Execute.ispc");
        Assert.Contains("__flat", source);
        Assert.Contains("((float*)", source);
    }

    [Fact]
    public void ChunkStencilLoopStaysSerialInsteadOfFlattening()
    {
        var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler; using EntJoy.ECS; using EntJoy.JobSystem; using EntJoy.Collections; using EntJoy.Mathematics;
public struct FlatC2 { public float2 Value; }
[NativeTranspile(Target=BackendTarget.Ispc)]
public struct OffsetChunkJob : IJobChunk {
    public float DeltaTime;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask) {
        NativeArray<FlatC2> ps = chunk.GetComponentDataNativeArray<FlatC2>();
        NativeArray<FlatC2> vs = chunk.GetComponentDataNativeArray<FlatC2>();
        for (int index = 0; index < ps.Length; index++) {
            FlatC2 t = ps[index];
            t.Value += vs[index + 1].Value * DeltaTime;
            ps[index] = t;
        }
    }
}");
        var source = ReadGenerated(result, "OffsetChunkJob_Execute.ispc");
        // 跨索引无法按槽摊平（相邻实体的同分量不是相邻槽），整条循环按单 lane 串行跑。
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", source);
        Assert.DoesNotContain("__flat", source);
        Assert.Contains("if (programIndex != 0) return;", source);
        Assert.Contains("for (uniform int index = 0; index < __entity_count; index++)", source);
        Assert.Contains("vs_ptr[index + 1]", source);
    }

    private static string ReadGenerated(EmitResult result, string nameFragment)
    {
        foreach (var file in Directory.GetFiles(result.OutputDir, "*.ispc", SearchOption.AllDirectories))
            if (Path.GetFileName(file).EndsWith(nameFragment, StringComparison.OrdinalIgnoreCase))
                return File.ReadAllText(file);
        throw new InvalidOperationException($"没有找到包含 {nameFragment} 的 ISPC 产物");
    }

    [Fact]
    public void AtomicBatchRangeAlsoComesFromTheScheduler()
    {
        var source = Emit("Output[index]=System.Threading.Interlocked.Increment(ref ((int*)Input.GetUnsafePtr())[index % Input.Length]);");
        Assert.Contains("for (uniform int index = __startIndex; index < __startIndex + __count;", source);
        Assert.DoesNotContain("min(__startIndex + __count", source);
    }

    [Theory]
    [InlineData("for(int k=0;k<9;k++) Output[index]=Output[index]*3+Input[k % Input.Length];")]
    [InlineData("for(int k=0;k<9;k++) { if(k==3) continue; if(k==7) break; Output[index]=Input[k % Input.Length]; }")]
    [InlineData("int k=0; while(k<9) { Output[index]=Output[index]*3+k; k++; }")]
    public void InnerLoopsKeepTheirSequentialSemantics(string body)
    {
        var source = Emit(body);
        Assert.Contains("foreach (__batchRel =", source);
        Assert.Single(Regex.Matches(source, @"foreach\s*\("));
        Assert.DoesNotContain("foreach (k", source);
    }

    [Theory]
    [InlineData("Begin", "End")]
    [InlineData("(int)Begin", "Input.Length")]
    [InlineData("Input.Length - 5", "Input.Length")]
    public void UniformRuntimeBoundsUseUniformCounters(string begin, string end)
    {
        var source = Emit("int sum=index; for(int k=" + begin + ";k<" + end
            + ";k+=2) sum+=Input[k % Input.Length]; Output[index]=sum;");
        Assert.Contains("for (uniform int k", source);
        Assert.DoesNotContain("#pragma", source);
    }

    [Fact]
    public void VaryingBoundsKeepVaryingCounters()
    {
        var source = Emit("int sum=index; for(int k=Input[index % Input.Length];k<End;k++) sum+=k; Output[index]=sum;");
        Assert.Contains("for (int k", source);
        Assert.DoesNotContain("for (uniform int k", source);
    }

    [Fact]
    public void NestedReturnIsNotTranslatedAsAnInnerContinue()
    {
        var source = Emit("for(int k=0;k<9;k++) { if(k==index) return; Output[index]=k; } Output[index]=99;");
        Assert.Contains("__ENTJOY_UNSUPPORTED_STMT__ISPC_ReturnInsideNestedLoopInBatch", source);
    }

    [Fact]
    public void IndexLevelReturnStillEndsOnlyThatIndex()
    {
        var source = Emit("if(index==3) return; Output[index]=index;");
        Assert.Contains("continue;", source);
        Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", source);
    }

    [Theory]
    [InlineData("for(int k=0;k<4;k++) value=value*3+k;", "for (uniform int k")]
    [InlineData("for(int k=0;k<limit;k++) value=value*3+k;", "for (int k")]
    public void LaneCallableHelpersKeepEveryIteration(string body, string loop)
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(Target=BackendTarget.Ispc)] public struct HelperJob : IJobParallelFor {
 public NativeArray<int> Output;
 public void Execute(int index) { Output[index]=Fold(index, index%5); }
 private static int Fold(int value, int limit) { " + body + @" return value; }
}");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var source = File.ReadAllText(Path.Combine(result.OutputDir,
            "SharpNative__global_namespace__HelperJob_Fold_helper.ispc"));
        Assert.Contains(loop, source);
        Assert.DoesNotContain("foreach", source);
        var cmake = File.ReadAllText(Path.Combine(result.OutputDir, "CMakeLists.txt"));
        Assert.Contains(cmake.Split('\n'), line => line.Contains("DEPENDS")
            && line.Contains("HelperJob_Execute_Batch.ispc") && line.Contains("HelperJob_Fold_helper.ispc")
            && line.Contains("EntJoyCommon.ispc"));
    }
}
