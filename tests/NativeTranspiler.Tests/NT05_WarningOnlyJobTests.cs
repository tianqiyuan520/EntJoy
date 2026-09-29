using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-05（High）：只报 warning 的 job 必须照常生成绑定/调度。
    ///
    /// 文档 docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md §4 末尾：
    /// 「warning 不阻断生成：NativeTranspilerGenerator 只在存在 error 时终止」。
    ///
    /// 现场症状（历史）：`[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)] struct X : IJobParallelFor`
    /// 只报 NT024（当时是 warning），但 ValidateJobStruct 用 `diagnostics.Count == 0` 判定有效 ⇒
    /// X 被塞进 invalidJobs、从 validJobs 摘掉 ⇒ 不生成 Schedule 绑定 ⇒ 调用点 CS0103 + 误导性 NT028。
    ///
    /// ★ B3 变更（2026-09-27）：NT024 已经从 warning 升为 **error**（AutoSIMD = Enabled 在
    /// IJobParallelFor/IJobFor/IJob 上实测慢 ~10%，要开必须显式声明 EntJoyAutoSimdMeasured=true）。
    /// 因此本文件拆成：
    ///   · 默认（未声明"已量过"）⇒ NT024 error + job 被排除（红/绿都断言）；
    ///   · 声明后 ⇒ 无 NT024、正常出绑定；
    ///   · warning-only 的回归守卫改用仍然是 warning 的 NT023（MathPrecision = High）。
    ///   · B3 ①：body 含不可向量化构造 ⇒ NT031 error（原先静默退回 per-lane 标量）。
    /// </summary>
    public class NT05_WarningOnlyJobTests
    {
        private readonly ITestOutputHelper _out;
        public NT05_WarningOnlyJobTests(ITestOutputHelper output) => _out = output;

        private const string Source = @"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct WarnOnlyJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public float Value;
    public void Execute(int index)
    {
        Out[index] = Value;
    }
}
";

        /// <summary>B3 ②：默认（未声明"我已量过"）⇒ AutoSIMD = Enabled 是 **error**，job 被排除。</summary>
        [Fact]
        public void AutoSimdEnabled_Default_IsErrorAndExcluded()
        {
            var result = GeneratorHarness.EmitFor(Source, autoSimdMeasured: false);
            _out.WriteLine(result.DiagnosticSummary);

            Assert.True(result.HasDiagnostic("NT024"),
                GeneratorHarness.Fail(result, "预期 NT024（默认 error）出现"));
            Assert.All(result.Of("NT024"), d => Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, d.Severity));
            // error 语义：该 job 从绑定/发射集中排除（调用点会明确报错，而不是静默拿到没向量化的产物）
            Assert.DoesNotContain("Schedule_WarnOnlyJob", result.Bindings);
            Assert.True(result.HasDiagnostic("NT028"),
                GeneratorHarness.Fail(result, "被排除的 job 应有 NT028 告知"));
        }

        /// <summary>B3 ②：显式声明"我已量过" ⇒ 放行，且照常生成绑定（warning-only 语义回归）。</summary>
        [Fact]
        public void AutoSimdEnabled_WithMeasuredOptIn_StillGetsScheduleBinding()
        {
            var result = GeneratorHarness.EmitFor(Source, autoSimdMeasured: true);
            _out.WriteLine(result.DiagnosticSummary);

            Assert.False(result.HasDiagnostic("NT024"),
                GeneratorHarness.Fail(result, "显式声明已量过后不应再报 NT024"));
            Assert.False(result.HasDiagnostic("NT028"),
                GeneratorHarness.Fail(result, "放行的 job 不应被排除出绑定生成（NT028）"));

            Assert.Contains("Schedule_WarnOnlyJob", result.Bindings);
            Assert.Contains("SharpNative_Job__global_namespace__WarnOnlyJob_Execute_Batch", result.Cpp);
        }

        /// <summary>NT-05 原守卫（改用仍是 warning 的 NT023）：warning 不阻断生成、不排除 job。</summary>
        [Fact]
        public void WarningOnlyJob_StillGetsScheduleBinding()
        {
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(Target = BackendTarget.Cpp, MathPrecision = SimdMathPrecision.High)]
public struct HighPrecisionJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public float Value;
    public void Execute(int index)
    {
        Out[index] = Value;
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);

            // 前提：这条 job 只触发 warning（NT023 = High 没有 SIMD 实现，产物等同 IEEE）。
            Assert.True(result.HasDiagnostic("NT023"),
                GeneratorHarness.Fail(result, "预期 NT023（warning）出现"));
            Assert.All(result.Of("NT023"), d => Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, d.Severity));

            // 缺陷点（历史）：warning 被当成"校验失败" ⇒ NT028（已从本批绑定中排除）+ 没有 Schedule 绑定。
            Assert.False(result.HasDiagnostic("NT028"),
                GeneratorHarness.Fail(result, "warning-only 的 job 不应被排除出绑定生成（NT028）"));

            Assert.Contains("Schedule_HighPrecisionJob", result.Bindings);
        }

        /// <summary>B3 ①：body 含 Interlocked ⇒ 发射侧整段退回 per-lane 标量 ⇒ 现在必须报 NT031（error）。</summary>
        [Fact]
        public void AutoSimdBody_FallsBackToPerLane_IsError()
        {
            var result = GeneratorHarness.EmitFor(@"
using System.Threading;
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct AtomicJob : IJobParallelFor
{
    public NativeArray<int> Counters;
    public int Delta;
    public void Execute(int index)
    {
        Interlocked.Add(ref Counters[index], Delta);
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);

            Assert.True(result.HasDiagnostic("NT031"),
                GeneratorHarness.Fail(result, "预期 NT031（per-lane 退回，error）出现"));
            Assert.All(result.Of("NT031"), d => Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, d.Severity));
            // 报错信息必须指名道姓（否则又变成"只说要改、不说改哪"）
            Assert.Contains("Interlocked.Add", string.Join(" || ", result.Of("NT031").SelectMessage()));

            Assert.DoesNotContain("Schedule_AtomicJob", result.Bindings);
        }

        /// <summary>B3 ①反向守卫：可向量化的 body 不得误报 NT031（否则等于用报错替代静默）。</summary>
        [Fact]
        public void AutoSimdBody_Vectorizable_NoNT031()
        {
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct PlainJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public NativeArray<float> A;
    public NativeArray<float> B;
    public float C;
    public void Execute(int index)
    {
        Out[index] = A[index] * B[index] + C;
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);

            Assert.False(result.HasDiagnostic("NT031"),
                GeneratorHarness.Fail(result, "可向量化的 body 不应报 NT031"));
            Assert.Contains("Schedule_PlainJob", result.Bindings);
        }

        [Fact]
        public void ErrorJob_IsStillExcludedFromBindings()
        {
            // 反向守卫：真正的 error（这里用 Vectorize + 非 chunk/entity job ⇒ NT018）仍必须
            // 把该 job 排除出绑定集，并报 NT028 —— 修 warning 门控不得顺手放过 error。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Vectorize)]
public struct VectorizeOnParallelForJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public void Execute(int index) { Out[index] = 1.0f; }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            Assert.True(result.HasDiagnostic("NT018"), GeneratorHarness.Fail(result, "预期 NT018（error）出现"));
            Assert.DoesNotContain("Schedule_VectorizeOnParallelForJob", result.Bindings);
        }
    }

    internal static class DiagnosticMessageExtensions
    {
        public static System.Collections.Generic.IEnumerable<string> SelectMessage(
            this System.Collections.Generic.IEnumerable<Microsoft.CodeAnalysis.Diagnostic> diagnostics)
        {
            foreach (var d in diagnostics) yield return d.GetMessage();
        }
    }
}
