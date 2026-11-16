using System;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 译文具备的特征。判定只针对本翻译器自己发射的文本：
    /// `__uni_` 是广播提升前缀、`n_` 是 intrinsic 前缀、`simd_value<…>` 是向量包装、`.v` 是 lane 访问。
    /// </summary>
    [Flags]
    internal enum SimdExprTraits
    {
        None = 0,

        /// <summary>`… .v` 形态：已暴露 SIMD 寄存器 lane，可直接作 intrinsic 实参。</summary>
        LaneAccess = 1 << 0,

        /// <summary>`simd_value<T>{ … }` 包装，未取 `.v`。</summary>
        Wrapper = 1 << 1,

        /// <summary>包装的元素类型是 2 分量数学向量（float2/int2/uint2），其成员是字段而非方法。</summary>
        TwoComponentWrapper = 1 << 2,

        /// <summary>`n_*` intrinsic 调用的结果（单 lane 标量）。</summary>
        Intrinsic = 1 << 3,

        /// <summary>提升到循环外的广播值 `__uni_*`，用时需要取 `.v`。</summary>
        HoistedBroadcast = 1 << 4,

        /// <summary>gather 结果（`n_gather_*`），分量访问按向量成员形式处理。</summary>
        Gather = 1 << 5,

        /// <summary>生成器分配的 varying 变量名（`v_*`）。</summary>
        VaryingVar = 1 << 6
    }

    internal static class SimdExprShapes
    {
        /// <summary>按译文特征给出标志位；`None` 表示普通标量文本。</summary>
        public static SimdExprTraits Of(string translated)
        {
            var traits = SimdExprTraits.None;
            if (translated.StartsWith("__uni_", StringComparison.Ordinal))
                traits |= SimdExprTraits.HoistedBroadcast;
            if (HasLaneAccess(translated))
                traits |= SimdExprTraits.LaneAccess;
            if (translated.StartsWith("simd_value<", StringComparison.Ordinal))
            {
                traits |= SimdExprTraits.Wrapper;
                if (IsTwoComponentWrapper(translated))
                    traits |= SimdExprTraits.TwoComponentWrapper;
            }
            if (translated.StartsWith("n_", StringComparison.Ordinal))
                traits |= SimdExprTraits.Intrinsic;
            if (translated.StartsWith("n_gather", StringComparison.Ordinal))
                traits |= SimdExprTraits.Gather;
            if (translated.StartsWith("v_", StringComparison.Ordinal))
                traits |= SimdExprTraits.VaryingVar;
            return traits;
        }

        /// <summary>`.v` 后紧跟标识符字符时不算 lane 访问（避免把 `.value` 之类误判）。</summary>
        private static bool HasLaneAccess(string text)
        {
            int index = text.IndexOf(".v", StringComparison.Ordinal);
            while (index >= 0)
            {
                int next = index + 2;
                if (next >= text.Length || !(char.IsLetterOrDigit(text[next]) || text[next] == '_'))
                    return true;
                index = text.IndexOf(".v", next, StringComparison.Ordinal);
            }
            return false;
        }

        private static bool IsTwoComponentWrapper(string text)
        {
            int end = text.IndexOf('>');
            if (end < 0) return false;
            string element = text.Substring("simd_value<".Length, end - "simd_value<".Length);
            return element == "EntJoy::Mathematics::float2"
                || element == "EntJoy::Mathematics::int2"
                || element == "EntJoy::Mathematics::uint2";
        }
    }
}
