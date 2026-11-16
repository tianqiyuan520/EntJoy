using System;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// SIMD 掩码文本的唯一出处。掩码在当前实现里是"要么是变量名、要么是一段表达式"的字符串，
    /// 全通掩码同时兼作"无掩码上下文"的哨兵；判定与文本都在这里，调用点不再写字面量。
    /// </summary>
    internal static class SimdMaskText
    {
        /// <summary>全通掩码（无掩码上下文）的哨兵文本。</summary>
        public const string AllTrue = "simd_mask::all_true()";

        public static bool IsAllTrue(string? mask) => string.Equals(mask, AllTrue, StringComparison.Ordinal);

        /// <summary>掩码文本是否是一个可直接复用/保存的简单变量名（不是表达式）。</summary>
        public static bool IsVariableName(string? mask)
        {
            if (string.IsNullOrEmpty(mask)) return false;
            foreach (char c in mask)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }
    }
}
