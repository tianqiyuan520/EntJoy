using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-17（托管 job 清点 / NT032）：列出"实现了 job 接口但没标 <c>[NativeTranspile]</c>"的 struct。
    /// <c>EntJoyJobIntent</c> = off（**默认，关闭**）| warn（只在混合项目清点）| strict（一律报且升级为 error）。
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
        public void Default_IsOff_SoMixedProjectIsSilent()
        {
            // 默认 off：这条诊断是**可选**的，不参与默认构建噪声
            var (ids, errors) = Diag(MixedSource);
            _out.WriteLine("ids=" + string.Join(",", ids) + " errors=" + errors);
            Assert.DoesNotContain("NT032", ids);
            Assert.Equal(0, errors);
        }

        [Fact]
        public void PolicyWarn_InventoriesManagedJobsInMixedProject()
        {
            var (ids, errors) = Diag(MixedSource, Policy("warn"));
            _out.WriteLine("ids=" + string.Join(",", ids) + " errors=" + errors);
            Assert.Contains("NT032", ids);
            Assert.Equal(0, errors);           // warn：不阻断构建
        }

        [Fact]
        public void PolicyWarn_PureManagedProject_IsSilent()
        {
            // warn 只在**混合**项目清点；纯托管项目里"全托管"是正常形态
            var (ids, _) = Diag(PureManagedSource, Policy("warn"));
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
        public void PolicyWarn_NamesTheManagedJobsAndNeedsNoMarker()
        {
            var r = GeneratorHarness.EmitFor(MixedSource, extraProperties: Policy("warn"));
            var msg = string.Join("\n", r.Diagnostics.Select(d => d.GetMessage()));
            _out.WriteLine(msg);
            Assert.Contains("ManagedNoAttrJob", msg);   // 列出没有原生内核的 job
            Assert.DoesNotContain("NativeOneJob", msg); // 已标记的不在名单里
            Assert.Contains("托管", msg);               // 说明"缺属性就是托管"
        }

        [Fact]
        public void PolicyWarn_AttributeOnlyJob_ProducesNoInventory()
        {
            var src = Head + @"
[NativeTranspile]
public struct OnlyNativeJob : IJob {
    public NativeArray<int> Out;
    public void Execute() { Out[0] = 1; }
}";
            var (ids, _) = Diag(src, Policy("warn"));
            Assert.DoesNotContain("NT032", ids);
        }
    }
}
