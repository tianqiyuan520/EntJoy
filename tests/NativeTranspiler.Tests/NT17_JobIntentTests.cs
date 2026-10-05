using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-17（托管 job 清点 / NT032）：把"漏写 <c>[NativeTranspile]</c>"从**静默降级**变成一条可读的清点。
    ///
    /// <para>判据只有一条：<c>[NativeTranspile]</c> 在 ⇒ 原生；**不在 ⇒ 托管**。
    /// 不引入、也不需要任何"我是托管"的标记 —— 缺属性本身就是托管的定义。</para>
    ///
    /// <para>缺陷形态（本仓实测踩过：游戏仓 `ZeroCellsJob` 清 351,233 个 int 的串行关键路径趟）：
    /// job struct 实现了 job 接口但没标 `[NativeTranspile]` ⇒ **三处都不会发声** ——
    /// ① 生成器的语法提供器谓词要求"有属性列表"⇒ 它**从不看**未标记的 struct（NT001~NT031 全以"已标记"为输入）；
    /// ② 托管路径是**始终存在**的泛型扩展 `JobExtensions.Schedule&lt;T&gt;`，具体扩展缺失时 C# 重载决议
    ///    无歧义地落到它 ⇒ 编译期零警告；③ 运行期 `UseNative` 只反映 NativeDll 是否可用，与"这个 job
    ///    有没有原生内核"无关。</para>
    /// </summary>
    public class NT17_JobIntentTests
    {
        private readonly ITestOutputHelper _out;
        public NT17_JobIntentTests(ITestOutputHelper output) => _out = output;

        private const string Head = "using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;\n";

        /// <summary>混合单元：一个标了属性的原生 job + 一个没标的（= 托管 + 潜在漏写形态）。</summary>
        private const string MixedSource = Head + @"
[NativeTranspile]
public struct NativeOneJob : IJob {
    public NativeArray<int> Out;
    public void Execute() { Out[0] = 1; }
}
public struct ManagedNoAttrJob : IJob {
    public NativeArray<int> Out;
    public void Execute() { Out[0] = 1; }
}";

        /// <summary>纯托管单元：两个 job 都没标（**正常形态**，不该报）。</summary>
        private const string PureManagedSource = Head + @"
public struct ManagedOneJob : IJob {
    public NativeArray<int> Out;
    public void Execute() { Out[0] = 1; }
}
public struct ManagedTwoJob : IJobParallelFor {
    public NativeArray<int> Out;
    public void Execute(int i) { Out[i] = 1; }
}";

        private static Dictionary<string, string> Policy(string v)
            => new() { ["build_property.EntJoyJobIntent"] = v };

        private static (string[] ids, int errors) Diag(string src, Dictionary<string, string> props = null)
        {
            var r = GeneratorHarness.EmitFor(src, extraProperties: props);
            return (r.Diagnostics.Select(d => d.Id).Distinct().OrderBy(x => x).ToArray(),
                    r.Diagnostics.Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        }

        [Fact]
        public void Default_InventoriesManagedJobsInMixedProject()
        {
            var (ids, errors) = Diag(MixedSource);
            _out.WriteLine("ids=" + string.Join(",", ids) + " errors=" + errors);
            Assert.Contains("NT032", ids);
            Assert.Equal(0, errors);           // 默认是 warning：不阻断构建
        }

        [Fact]
        public void PureManagedProject_IsSilentByDefault()
        {
            // 本仓 16 个纯托管项目（如 EntJoy.ECS.Tests 的 20 个 job）一个字都不该报
            var (ids, _) = Diag(PureManagedSource);
            Assert.DoesNotContain("NT032", ids);
        }

        [Fact]
        public void PolicyOff_DisablesEntirely()
        {
            var (ids, _) = Diag(MixedSource, Policy("off"));
            Assert.DoesNotContain("NT032", ids);
        }

        [Fact]
        public void PolicyStrict_ReportsEvenInPureManagedProject_AndIsError()
        {
            var (ids, errors) = Diag(PureManagedSource, Policy("strict"));
            Assert.Contains("NT032", ids);
            Assert.True(errors >= 1, "strict 策略下 NT032 必须升级为 error");
            var (ids2, errors2) = Diag(MixedSource, Policy("strict"));
            Assert.Contains("NT032", ids2);
            Assert.True(errors2 >= 1);
        }

        [Fact]
        public void Inventory_NamesTheManagedJobsAndNeedsNoMarker()
        {
            var r = GeneratorHarness.EmitFor(MixedSource);
            var msg = string.Join("\n", r.Diagnostics.Select(d => d.GetMessage()));
            _out.WriteLine(msg);
            Assert.Contains("ManagedNoAttrJob", msg);   // 列出没有原生内核的 job
            Assert.DoesNotContain("NativeOneJob", msg); // 已标记的不在名单里
            Assert.Contains("托管", msg);               // 说明"缺属性就是托管"
        }

        [Fact]
        public void AttributeOnlyJob_ProducesNoInventory()
        {
            var src = Head + @"
[NativeTranspile]
public struct OnlyNativeJob : IJob {
    public NativeArray<int> Out;
    public void Execute() { Out[0] = 1; }
}";
            var (ids, _) = Diag(src);
            Assert.DoesNotContain("NT032", ids);
        }
    }
}
