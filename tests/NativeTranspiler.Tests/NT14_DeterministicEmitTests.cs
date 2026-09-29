using System;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-14（确定性，审计原 NT-13 的一半）：源生成器的输出必须是**可复现**的。
    ///
    /// 缺陷形态：`NativeTranspilerGenerator` 在生成的 `NativeTranspiler_GeneratedMarker.g.cs` 里写入
    /// `// Generated at {DateTime.UtcNow}`（`generator.stamp` 里还有 `writtenUtc=`）。
    /// 生成产物是**编译器输入** ⇒ 每次构建输入都变：增量构建恒定失效、可复现构建不可能成立、
    /// 产物 diff 永远非空（此前 EmitSnapshot 里唯一的差异就是这一行）。
    ///
    /// 判据：同一份输入连跑两次生成器，全部生成源（含 marker）必须逐字节相同。
    /// </summary>
    public class NT14_DeterministicEmitTests
    {
        private readonly ITestOutputHelper _out;
        public NT14_DeterministicEmitTests(ITestOutputHelper output) => _out = output;

        private const string Source = @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
public static class NT14Det {
    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public static void Bits(NativeArray<ulong> m, int n) {
        for (int i = 0; i < n; i++) m[i] |= 1UL << (i & 63);
    }
}";

        [Fact]
        public void GeneratedSources_MustBeByteIdenticalAcrossTwoRuns()
        {
            var a = GeneratorHarness.EmitFor(Source);
            var b = GeneratorHarness.EmitFor(Source);
            _out.WriteLine(a.Bindings);

            // 先确认判据真的覆盖到 marker 文件（否则测试会因为"比较了空集合"而假绿）
            Assert.Contains("NativeTranspiler generated marker", a.Bindings);

            // 时间戳（或任何"本次运行的环境"）一旦进入产物，这里必然不等
            Assert.True(a.Bindings == b.Bindings,
                GeneratorHarness.Fail(a, "同一输入两次生成的源不一致（产物非确定性）：\n" +
                    FirstDifference(a.Bindings, b.Bindings)));

            // 负向守卫：易变来源不得再出现
            Assert.DoesNotContain("Generated at ", a.Bindings);
        }

        private static string FirstDifference(string x, string y)
        {
            int n = Math.Min(x.Length, y.Length);
            for (int i = 0; i < n; i++)
                if (x[i] != y[i])
                    return $"首位差异 @{i}: A='{Snippet(x, i)}' B='{Snippet(y, i)}'";
            return x.Length == y.Length ? "(长度相同但内容比较判定不等？)" : $"长度不同 A={x.Length} B={y.Length}";
        }

        private static string Snippet(string s, int at)
            => s.Substring(Math.Max(0, at - 40), Math.Min(80, s.Length - Math.Max(0, at - 40))).Replace("\n", "\\n");
    }
}
