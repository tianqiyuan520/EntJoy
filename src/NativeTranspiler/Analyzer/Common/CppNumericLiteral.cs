using System;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// C# → C++ 数值字面量后缀归一化（Windows/LLP64 差异的唯一真值来源）。
    ///
    /// 差异背景：C# 的 `1UL` / `1L` 是 64 位（`ulong` / `long`），而 C++ 在 Windows/LLP64 下
    /// `1UL` / `1L` 只有 32 位。若不归一化，`(1UL &lt;&lt; 40) &gt;&gt; 32` 这类表达式会从 256 变成 -4，
    /// 位掩码/移位类内核会静默错值（历史实测：50,000 实体 enable 位图错 78%）。
    ///
    /// 标量转译器（<see cref="Cpp.CppPointerStatementTranslator.NormalizeNumericLiteral"/>）一直走这条规则；
    /// SIMD 表达式转译器此前漏了它 ⇒ 同一条 `SetBits` 的向量化部分仍然是错的。
    /// 现在两边共用本方法，避免再出现"同一规则两处实现、只修一处"的分叉。
    /// </summary>
    internal static class CppNumericLiteral
    {
        /// <summary>把 C# 的 64 位整型后缀改成 C++/LLP64 下同为 64 位的写法。</summary>
        internal static string NormalizeSuffix(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.EndsWith("UL", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith("LU", StringComparison.OrdinalIgnoreCase))
                return text.Substring(0, text.Length - 2) + "ULL";
            if (text.EndsWith("L", StringComparison.OrdinalIgnoreCase))
                return text.Substring(0, text.Length - 1) + "LL";
            return text;   // U/u（C# uint ↔ C++ unsigned int）两语言一致，原样保留
        }
    }
}
