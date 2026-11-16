using System;

namespace NativeTranspiler.Analyzer
{
    /// <summary>掩码文本的来源：哨兵、生成器分配的变量名、还是一段表达式。</summary>
    internal enum MaskOrigin
    {
        /// <summary>任意掩码表达式（`simd_mask{ … }` 之类），不能当变量名复用。`default` 即此值。</summary>
        Expression = 0,

        /// <summary>全通掩码（无掩码上下文）的哨兵。</summary>
        AllTrue = 1,

        /// <summary>生成器分配的简单变量名，可保存/复用。</summary>
        NamedVar = 2
    }

    /// <summary>
    /// 一个掩码引用。掩码在产物里是文本，但"是不是哨兵、能不能当变量名"是结构信息，
    /// 由这个类型统一回答，调用点不再拿字符串比较。
    /// </summary>
    internal readonly struct SimdMask : IEquatable<SimdMask>
    {
        public const string AllTrueText = "simd_mask::all_true()";

        public static readonly SimdMask AllTrue = new(AllTrueText, MaskOrigin.AllTrue);
        public static readonly SimdMask Empty = new("", MaskOrigin.Expression);

        public SimdMask(string text, MaskOrigin origin)
        {
            Text = text;
            Origin = origin;
        }

        public string Text { get; }
        public MaskOrigin Origin { get; }

        public bool IsAllTrue => Origin == MaskOrigin.AllTrue;
        public bool IsNamedVar => Origin == MaskOrigin.NamedVar;

        /// <summary>按文本推断来源：哨兵 / 简单标识符 / 其它表达式。</summary>
        public static SimdMask Of(string text)
        {
            if (string.Equals(text, AllTrueText, StringComparison.Ordinal)) return AllTrue;
            return IsIdentifier(text)
                ? new SimdMask(text, MaskOrigin.NamedVar)
                : new SimdMask(text, MaskOrigin.Expression);
        }

        private static bool IsIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }

        public static implicit operator string(SimdMask mask) => mask.Text ?? "";
        public static implicit operator SimdMask(string text) => Of(text);

        public bool Equals(SimdMask other)
            => Origin == other.Origin && string.Equals(Text, other.Text, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SimdMask other && Equals(other);

        public override int GetHashCode() => ((int)Origin * 397) ^ (Text?.GetHashCode() ?? 0);

        public static bool operator ==(SimdMask left, SimdMask right) => left.Equals(right);
        public static bool operator !=(SimdMask left, SimdMask right) => !left.Equals(right);

        public override string ToString() => Text ?? "";
    }
}
