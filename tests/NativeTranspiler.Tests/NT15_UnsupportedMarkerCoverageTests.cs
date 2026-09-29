using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-15（审计原 NT-12「marker 缺口」）：**不允许静默降级**。
    ///
    /// 机制：生成器遇到无法转译的构造必须① 在产物里写 `__ENTJOY_UNSUPPORTED` 标记（构建期
    /// `NativeCompileTask.CheckGeneratedMarkers` 扫描并让构建失败），或② 报诊断（NT001–NT030）。
    /// 两者皆无 = 静默降级（历史上踩过：`unchecked { … }` 生成空函数体、`histPtr[key]++` 被翻成
    /// `/* unsupported expr */ 0` 且无任何提示）。
    ///
    /// 本用例把"按设计不支持"的构造逐个喂给生成器，强制断言 **marker 或 诊断** 至少有一个。
    /// 新增翻译分支时若漏写标记，这里会红。
    /// </summary>
    public class NT15_UnsupportedMarkerCoverageTests
    {
        private readonly ITestOutputHelper _out;
        public NT15_UnsupportedMarkerCoverageTests(ITestOutputHelper output) => _out = output;

        private static string Method(string body) => @"
using System; using System.Collections.Generic; using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
public static class NT15Probe {
    [NativeTranspile]
    public static void Probe(NativeArray<int> a, int n) {
        " + body + @"
    }
}";

        public static IEnumerable<object[]> Probes() => new[]
        {
            // ⚠ `unchecked 语句块` 与 `switch 语句` 已从本表移除：它们在 C++ 后端的**可译前提下**
            //   现在真的会被翻译（分别见 ChunkTranslatorGapTests 的
            //   `UncheckedBlock_*` / `SwitchStatement_*`）。仍属"按设计不支持"的构造一律留下。
            new object[] { "goto/label",      Method("goto done;\ndone: a[0] = 1;") },
            new object[] { "throw",           Method("throw new InvalidOperationException();") },
            new object[] { "try/catch",       Method("try { a[0] = 1; } catch { a[0] = 2; }") },
            new object[] { "lock",            Method("lock (typeof(NT15Probe)) { a[0] = 1; }") },
            new object[] { "using 语句",      Method("using (var s = new System.IO.MemoryStream()) { a[0] = 1; }") },
            new object[] { "checked 语句块",  Method("checked { a[0] = n * 2; }") },
            new object[] { "stackalloc",      Method("Span<int> s = stackalloc int[4]; a[0] = s.Length;") },
            new object[] { "lambda/委托",     Method("Func<int> f = () => 1; a[0] = f();") },
            new object[] { "foreach 非数组",  Method("foreach (var x in new List<int> { 1, 2 }) a[0] = x;") },
            new object[] { "switch 模式 case", Method("switch (n) { case int x when x > 0: a[0] = 1; break; default: a[0] = 2; break; }") },
        };

        [Theory]
        [MemberData(nameof(Probes))]
        public void UnsupportedConstruct_MustBeMarkedOrDiagnosed(string name, string source)
        {
            var r = GeneratorHarness.EmitFor(source);
            _out.WriteLine($"[{name}]");

            bool marked = r.Cpp.Contains("__ENTJOY_UNSUPPORTED") || r.Bindings.Contains("__ENTJOY_UNSUPPORTED");
            bool diagnosed = r.Diagnostics.Any(d => d.Severity >= DiagnosticSeverity.Warning);

            Assert.True(marked || diagnosed,
                GeneratorHarness.Fail(r, $"[{name}] 既无 __ENTJOY_UNSUPPORTED 标记、也无任何诊断 ⇒ 静默降级（必须二者其一）"));
        }
    }
}
