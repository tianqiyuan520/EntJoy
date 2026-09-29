using System;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-03（Critical）：SimdControlFlowGenerator.GeneratePerLaneFullBody 把 `return;` 换成 `break;`
    /// 却**没有** do{}while(false) 包裹 ⇒ `if (bad(index)) return;` 变成 `break;`，跳出的是
    /// **lane 循环** ⇒ 静默跳过最多 7 个 index。
    /// 对照：OuterSimdGenerator.GeneratePerLane 有 do{}while(false)（:201-209）。
    ///
    /// NT-04（Critical）：`IJob`（Execute() 无索引形参）也走同一个 per-lane 渲染器，
    /// 于是整段 body 被 g_simdWidthInt 次重复执行（AVX2 上 ×8），lane 变量还是死的。
    /// 代码注释自己写着"整段走标量翻译"——让代码与注释一致（无 lane 循环）。
    /// </summary>
    public class NT03_NT04_PerLaneRenderTests
    {
        private readonly ITestOutputHelper _out;
        public NT03_NT04_PerLaneRenderTests(ITestOutputHelper output) => _out = output;

        // ─── NT-03：per-lane 回退 + 早退出 ───
        // 触发条件：body 里出现"边界为 varying 的非 reduction 循环" ⇒ HasVaryingNonReductionLoop ⇒
        // SimdControlFlowGenerator.GeneratePerLaneFullBody（注意：不是 OuterSimdGenerator.GeneratePerLane，
        // 后者本来就有 do{}while(false)）。
        private const string PerLaneEarlyExitJob = @"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct PerLaneReturnJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public float Bias;
    public void Execute(int index)
    {
        if (Out[index] < 0) return;
        for (int k = 0; k < index; k = k + 1) { Bias = Bias + 1.0f; }
        Out[index] = Bias;
    }
}
";

        [Fact]
        public void PerLaneFallback_EarlyReturn_IsWrappedInDoWhile()
        {
            var result = GeneratorHarness.EmitFor(PerLaneEarlyExitJob);
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);

            Assert.Contains("SharpNative_Job__global_namespace__PerLaneReturnJob_Execute_Batch", result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            // 前置：确实走的是 SimdControlFlowGenerator 的 per-lane 全 body（空格风格 + 无 Outer 头）
            int lane = text.IndexOf("for (int __ej_lane = 0; __ej_lane < g_simdWidthInt; __ej_lane++)", StringComparison.Ordinal);
            Assert.True(lane >= 0, GeneratorHarness.Fail(result, "预期产物里出现 per-lane 全 body 循环"));

            string region = text.Substring(lane, Math.Min(900, text.Length - lane));

            // 缺陷点：`if (bad(index)) return;` 被翻成裸 `break;` ⇒ 跳出 lane 循环、静默跳过剩余 lane。
            // 修法：与 OuterSimdGenerator.GeneratePerLane 同构，用 do{}while(false) 把 break 收进本次 lane。
            Assert.Matches(new Regex(@"do\s*\{[\s\S]*?break;[\s\S]*?\}\s*while\s*\(\s*false\s*\)"), region);
        }

        // ─── NT-04：IJob（无索引形参）不得生成 lane 循环 ───
        private const string SingleJob = @"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct SingleScalarJob : IJob
{
    public NativeArray<float> Out;
    public void Execute()
    {
        for (int i = 0; i < Out.Length; i++)
        {
            Out[i] = Out[i] + 1.0f;
        }
    }
}
";

        [Fact]
        public void SingleJobWithoutIndex_EmitsScalarBody_NoLaneLoop()
        {
            var result = GeneratorHarness.EmitFor(SingleJob);
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);

            string text = result.Cpp.Replace("\r\n", "\n");
            int fn = text.IndexOf("SingleScalarJob_Execute(", StringComparison.Ordinal);
            Assert.True(fn >= 0, GeneratorHarness.Fail(result, "产物里找不到 SingleScalarJob_Execute"));
            string region = text.Substring(fn);

            // 缺陷点：整段 body 被 lane 循环跑了 g_simdWidthInt 次（死 lane 变量）
            Assert.DoesNotContain("__ej_lane", region);
            Assert.DoesNotContain("g_simdWidthInt", region);

            // 标量翻译仍然要在（注释承诺的"整段走标量翻译"）
            Assert.Contains("for (int i = 0; i < Out_length; i++)", region);
            Assert.Contains("Out_ptr[i] = Out_ptr[i] + 1.0f;", region);
        }
    }
}
