using System;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-08（Critical）：AutoSIMD 的**余数循环**发射的是**原始 C# 源文本**。
    ///
    /// <c>CppGenerator.GenerateBatchLoopSIMD</c> 的尾部（n % NSIMD_WIDTH）循环把
    /// <c>stmt.GetText()</c>（C# 文本）过一张 9 条手写替换表就交付 —— 绕过了真正的转译器。
    /// 于是文档 §8.1 记为"已修"的缺陷原样复活：C# 的 <c>1UL</c> 是 64 位，而 C++（Windows/LLP64）
    /// 的 <c>UL</c> 是 32 位 ⇒ 余数循环里出现 <c>m_ptr[i] |= 1UL &lt;&lt; (i &amp; 63)</c>：
    /// 位移量可达 63 &gt;= 32 ⇒ <c>shift count &gt;= width of type</c>（UB，clang 折叠成 &amp; 31）
    /// ⇒ 尾部残留元素的位掩码**静默错值**。
    ///
    /// 判据：余数循环体必须由 <c>CppPointerStatementTranslator</c> 产出 —— 字面量后缀归一化
    /// （<c>1UL</c> → <c>1ULL</c>）、<c>MathF.*</c> 映射、类型/成员映射都走真转译器。
    /// </summary>
    public class NT08_AutoSimdRemainderTests
    {
        private readonly ITestOutputHelper _out;
        public NT08_AutoSimdRemainderTests(ITestOutputHelper output) => _out = output;

        private const string SetBits = @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
public static class NT08Bits {
    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public static void SetBits(NativeArray<ulong> m, int n) {
        for (int i = 0; i < n; i++) m[i] |= 1UL << (i & 63);
    }
}";

        private const string MathFTail = @"
using System; using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
public static class NT08Math {
    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public static void ClampDown(NativeArray<float> v, float lo, int n) {
        for (int i = 0; i < n; i++) v[i] = MathF.Max(v[i], lo);
    }
}";

        /// <summary>截出"余数循环"（从 <c>for (int x = vec_count; …)</c> 起到文件尾）。</summary>
        private static string RemainderLoop(EmitResult r)
        {
            var m = Regex.Match(r.Cpp, @"for \(int \w+ = vec_count;");
            Assert.True(m.Success, GeneratorHarness.Fail(r, "产物里找不到余数循环头 `for (int x = vec_count;`"));
            return r.Cpp.Substring(m.Index);
        }

        [Fact]
        public void RemainderLoop_UlongMask_ShiftsIn64Bit()
        {
            var r = GeneratorHarness.EmitFor(SetBits);
            _out.WriteLine(r.Cpp);
            var tail = RemainderLoop(r);

            Assert.False(tail.Contains("1UL <<"),
                GeneratorHarness.Fail(r, "余数循环里出现 C# 的 `1UL <<`（C++/LLP64 下 32 位 ⇒ UB/静默错值）:\n" + tail));
            Assert.True(tail.Contains("1ULL <<") || tail.Contains("(unsigned long long)"),
                GeneratorHarness.Fail(r, "余数循环的 ulong 掩码位移不是 64 位:\n" + tail));
        }

        [Fact]
        public void RemainderLoop_IsTranslated_NotRawCSharpText()
        {
            var r = GeneratorHarness.EmitFor(MathFTail);
            _out.WriteLine(r.Cpp);
            var tail = RemainderLoop(r);

            Assert.False(tail.Contains("MathF.Max("),
                GeneratorHarness.Fail(r, "余数循环把 C# 的 `MathF.Max(` 原样泄漏进 C++:\n" + tail));
            Assert.True(tail.Contains("::fmaxf("),
                GeneratorHarness.Fail(r, "余数循环没有走真转译器的 MathF 映射:\n" + tail));
        }

        /// <summary>余数循环仍必须是"从 vec_count 到上界"的正确区间（不能把整段循环改坏）。</summary>
        [Fact]
        public void RemainderLoop_HeaderUnchanged()
        {
            var r = GeneratorHarness.EmitFor(SetBits);
            var tail = RemainderLoop(r);
            var header = tail.Split('\n')[0].Trim();
            Assert.Equal("for (int i = vec_count; i < n; i++)", header);
            Assert.Contains("m_ptr[i] |=", tail);
        }
    }
}
