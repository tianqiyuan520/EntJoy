using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-12 / NT-13（Critical，由 NT-08 修复过程中发现的同族缺陷）——都在 **AutoSIMD 向量化部分**：
    ///
    /// **NT-12**：`SimdExpressionTranslator.EmitElementStore` 从不读 `assign.OperatorToken`
    /// ⇒ 复合赋值被当成普通 store：`m[i] |= x` 发射成 `((ulong*)(m_ptr))[idx] = x;`（覆盖写，丢掉旧值）。
    ///
    /// **NT-13**：`SimdExpressionTranslator.TranslateLiteral` 不做 §8.1 的数值后缀归一化
    /// ⇒ 向量化部分保留 C# 的 `1UL`（C++/LLP64 下 32 位）⇒ `1UL &lt;&lt; (i &amp; 63)` 位移 ≥32 = UB/静默错值。
    /// </summary>
    public class NT12_NT13_SimdStoreAndLiteralTests
    {
        private readonly ITestOutputHelper _out;
        public NT12_NT13_SimdStoreAndLiteralTests(ITestOutputHelper output) => _out = output;

        private const string CompoundOr = @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
public static class NT12Bits {
    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public static void OrBits(NativeArray<ulong> m, int n) {
        for (int i = 0; i < n; i++) m[i] |= 1UL << (i & 63);
    }
}";

        private const string WholeAssignment = @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
public static class NT13Bits {
    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public static void SetBits(NativeArray<ulong> m, int n) {
        for (int i = 0; i < n; i++) m[i] = 1UL << (i & 63);
    }
}";

        /// <summary>截出"向量化部分"（余数循环之前）。NT-12 用例用它做取证。</summary>
        private static string VectorizedPart(EmitResult r)
        {
            var m = Regex.Match(r.Cpp, @"for \(int \w+ = vec_count;");
            return m.Success ? r.Cpp.Substring(0, m.Index) : r.Cpp;
        }

        [Fact]
        public void CompoundAssignment_MustReadModifyWrite_NotOverwrite()
        {
            var r = GeneratorHarness.EmitFor(CompoundOr);
            _out.WriteLine(r.Cpp);
            var vec = VectorizedPart(r);

            // 判据：向量化部分必须出现复合赋值运算符。覆盖写形态里**任何位置**都不会有 `|=`
            // （RHS 只有 `1ULL << (i & 63)`），因此这一条同时是 RED 判别式。
            Assert.True(vec.Contains("|=") || vec.Contains("n_or_"),
                GeneratorHarness.Fail(r, "复合赋值 `m[i] |= …` 的向量化部分没有 load-modify-write（被写成覆盖写）:\n" + vec));
        }

        [Fact]
        public void VectorizedLiteral_MustUse64BitSuffix()
        {
            var r = GeneratorHarness.EmitFor(WholeAssignment);
            _out.WriteLine(r.Cpp);

            // 不依赖"余数循环头"这类易变标记：直接挑出向量化 per-lane store 行
            // （形如 `...]n_extract_lane_epi32((v_i).v,__l)] = ...`），逐行判定后缀。
            var storeLines = r.Cpp.Split('\n')
                .Where(l => l.Contains("n_extract_lane_epi32((v_i).v,__l)]"))
                .ToArray();
            Assert.NotEmpty(storeLines);

            foreach (var line in storeLines)
            {
                // ⚠ 不能用 `Contains("1UL")`：`1ULL` 以 `1UL` 为前缀，会误报。
                //   用"32 位 UL 后缀（后面不再跟 L）"的正则判定。
                Assert.False(Regex.IsMatch(line, @"\dUL(?!L)"),
                    GeneratorHarness.Fail(r, "向量化 store 行仍使用 C# 的 32 位后缀 `…UL`（C++/LLP64 ⇒ 移位 ≥32 为 UB）:\n" + line));
            }
            Assert.Contains("1ULL", r.Cpp);
        }
    }
}
