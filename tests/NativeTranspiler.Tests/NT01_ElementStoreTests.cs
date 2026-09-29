using System;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-01（Critical）：SimdExpressionTranslator.EmitElementStore 在"varying 下标 + uniform RHS"时
    /// 无条件发**无掩码 W 宽连续 store**，并假定下标连续。
    ///   (a) 下标不连续（如 `Out[index*2] = 1.0f`）时整段写错；
    ///   (b) CppJobGenerator 的 IJobChunk/EntityBatch 批路径把 batchOffsetVar 传成 "0"，
    ///       而外层循环推进的是 `si` ⇒ 每组都重写元素 [0,W)、≥W 的元素**永远写不到**。
    /// </summary>
    public class NT01_ElementStoreTests
    {
        private readonly ITestOutputHelper _out;
        public NT01_ElementStoreTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void NonContiguousIndex_WithUniformRhs_MustNotEmitContiguousVectorStore()
        {
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct StridedStoreJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index * 2] = 1.0f;
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            // 缺陷点：下标 `index*2` 不连续，却发了 `n_store_ps(Out_ptr + si, …)`（写 [si,si+W)）
            Assert.DoesNotContain("n_store_ps(Out_ptr + si,", text);

            // 正确形态：逐 lane 掩码 scatter（按真实下标逐个写）
            Assert.Contains("for(int __l=0;__l<g_simdWidthInt;__l++)", text);
            Assert.Contains("Out_ptr[n_extract_lane_epi32(", text);
        }

        [Fact]
        public void ContiguousIndex_WithUniformRhs_StillUsesVectorStore()
        {
            // 反向守卫：可证明连续的 `Out[index] = 1.0f` 必须保留连续向量 store（不得退化成 scatter）
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct ContiguousStoreJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index] = 1.0f;
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");
            Assert.Contains("n_store_ps(Out_ptr + si, n_set1_ps(1.0f))", text);
        }

        [Fact]
        public void NoShape_EmitsZeroOffsetContiguousVectorStore()
        {
            // 不变量（NT-01 收尾）：无掩码连续向量 store 的偏移必须是**生成器可知的批循环变量**。
            // `+ 0` 只有"索引相对基址确实从 0 开始"才成立，而只有批路径能证明这一点；
            // 单 IJob 路径（用户循环起点未知）现用 UnknownBatchOffset 哨兵 ⇒ 任何产物里都不该出现
            // `n_store_*(<ptr> + 0, …)`。否则任何将来被向量化的形状都会重演 NT-01(b) 的静默错值。
            // ⚠ 每个形状都必须先断言"真的生成了对应函数"，否则断言是空跑（实测踩到：
            //   紧凑写法被 NT004/NT008 判为非 job ⇒ 产物为空、永不触发断言）。
            var shapes = new (string name, string fn, string src)[]
            {
                ("IJobParallelFor.fill", "ContiguousStoreJob_Execute", @"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct ContiguousStoreJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index] = 1.0f;
    }
}"),
                ("IJobParallelFor.stride", "StridedStoreJob_Execute", @"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct StridedStoreJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index * 2] = 1.0f;
    }
}"),
                ("IJobChunk.fill", "ChunkFillJob_Execute", @"
using NativeTranspiler;
using EntJoy.ECS;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct ChunkFillJob : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var values = chunk.GetComponentDataNativeArray<float>();
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = 1.0f;
        }
    }
}"),
                ("IJobParallelForBatch.fill", "RangeJob_Execute", @"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile]
public struct RangeJob : IJobParallelForBatch
{
    public NativeArray<int> Values;
    public int Delta;
    public void Execute(int startIndex, int count)
    {
        for (int i = startIndex; i < startIndex + count; i++) { Values[i] += Delta; }
    }
}"),
                ("IJob.fill", "SingleSimdJob_Execute", @"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct SingleSimdJob : IJob
{
    public NativeArray<float> Out;
    public void Execute()
    {
        for (int i = 0; i < Out.Length; i++) { Out[i] = Out[i] + 1.0f; }
    }
}"),
                ("IJobEntity.simd", "EntitySimdJob_Execute", @"
using NativeTranspiler;
using EntJoy.ECS;

public struct LSnapC { public float X; public float Marker; }

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct EntitySimdJob : IJobEntity
{
    public void Execute(ref LSnapC p)
    {
        if (p.X < 0.0f) return;
        p.Marker = p.X * 2.0f;
    }
}"),
            };

            foreach (var (name, fn, src) in shapes)
            {
                var result = GeneratorHarness.EmitFor(src);
                string text = result.Cpp.Replace("\r\n", "\n");
                Assert.True(text.Contains(fn),
                    GeneratorHarness.Fail(result, $"[{name}] 形状没有被生成 ⇒ 该形状的断言是空跑（先修形状再谈不变量）"));

                foreach (var line in text.Split('\n'))
                {
                    if (line.Contains("n_store_ps(") || line.Contains("n_store_epi32("))
                    {
                        Assert.False(line.Contains(" + 0,"),
                            GeneratorHarness.Fail(result, $"[{name}] 出现零偏移连续向量 store（偏移未知时不得断言连续）：{line.Trim()}"));
                    }
                }
            }
        }

        [Fact]
        public void ChunkSimdBatchStore_UsesBatchStartOffsetInsteadOfZero()
        {
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct ChunkFillJob : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var values = chunk.GetComponentDataNativeArray<float>();
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = 1.0f;
        }
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            Assert.Contains("values_ptr", text);

            // 缺陷点：偏移写成常量 0 ⇒ 每个 simd 组都重写 [0,W)，元素 ≥ W 永远不被写。
            Assert.DoesNotContain("n_store_ps(values_ptr + 0,", text);

            // 正确形态：偏移是外层批循环变量 si
            Assert.Contains("n_store_ps(values_ptr + si,", text);
        }
    }
}
