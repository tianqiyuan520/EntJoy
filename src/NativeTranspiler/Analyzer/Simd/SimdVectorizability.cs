using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// "这段 body 能不能向量化"的唯一判据（F-2 / B3 项）。
    ///
    /// 背景：`SimdControlFlowGenerator` 命中本判据时整段退回 per-lane 标量循环——产物正确但没有
    /// 任何 SIMD 收益，且原先是静默的。现在同一判据同时被两处使用：
    /// · 发射侧 <see cref="SimdControlFlowGenerator"/>：决定是否走 per-lane；
    /// · 校验侧 <c>NativeTranspileValidator</c>：命中即报 NT031（error），让"以为开了向量化、实际
    /// 跑标量"这件事在构建期就暴露。
    /// 必须是同一份实现：把规则抄两遍，只修一处，正是本仓库踩过的坑
    /// （NT-13 数字字面量后缀）。
    /// </summary>
    internal static class SimdVectorizability
    {
        /// <summary>SIMD 路径原生支持（可向量化）的方法名——不属于这些名字且实参含 varying 的调用一律视为不可向量化。</summary>
        public static readonly System.Collections.Generic.HashSet<string> VectorizableCallNames = new()
        {
            "min", "max", "clamp", "abs", "sqrt", "floor", "ceil", "round", "trunc", "lerp",
            "sin", "cos", "tan", "atan", "atan2", "asin", "acos", "exp", "log", "log2", "log10",
            "pow", "sign", "dot", "length", "distance", "normalize", "saturate", "step", "smoothstep",
            "fma", "mad", "rsqrt", "rcp", "frac", "mod", "fmod", "hypot", "deg2rad", "rad2deg",
            "IsNaN", "IsInfinity", "CountBits", "CountLeadingZeros", "CountTrailingZeros",
            "ReverseBits", "RotateLeft", "RotateRight", "DivRem", "BitIncrement", "BitDecrement",
            "CopySign", "FusedMultiplyAdd", "ScaleB", "Cbrt", "Sinh", "Cosh", "Tanh",
            "Asinh", "Acosh", "Atanh", "SinCos", "IEEERemainder", "GetUnsafePtr", "GetUnsafeReadOnlyPtr",
            "Increment", "Decrement", "Add", "Exchange", "CompareExchange", "Read",
            "ArrayElementAsRef", "AsRef", "GetRef",
            // System.Math / System.MathF 的 PascalCase 形式（TranslateMathFFunction 的 case 集合）。
            // 缺了它们，`Math.Max(cx, Math.Min(cx, n))` 这种常见钳位写法会被误判成"不可向量化"。
            "Min", "Max", "Clamp", "Sqrt", "Abs", "Ceiling", "Round", "Truncate",
            "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2",
            "Sinh", "Cosh", "Tanh", "Exp", "Log", "Log10", "Pow",
        };

        /// <summary>
        /// 是否存在「无法向量化」的调用：
        /// - <c>Interlocked.*</c>：per-lane 原子递增/加（源里每 lane 各自一次原子操作，向量化无意义）
        /// 命中即整段退回 per-lane 标量循环 —— 宁可慢，不可生成错代码。
        /// </summary>
        public static bool HasNonVectorizableCall(SyntaxNode node, SimdVariableAnalyzer varAnalyzer)
            => HasNonVectorizableCall(node, varAnalyzer, out _);

        /// <summary>同上，并给出触发原因（供 NT031 报错信息指名道姓，而不是只说"不可向量化"）。</summary>
        public static bool HasNonVectorizableCall(SyntaxNode node, SimdVariableAnalyzer varAnalyzer, out string? reason)
        {
            reason = null;
            foreach (var inv in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string name = null;
                if (inv.Expression is MemberAccessExpressionSyntax ma)
                    name = ma.Name.Identifier.Text;
                else if (inv.Expression is IdentifierNameSyntax idn)
                    name = idn.Identifier.Text;
                if (name == null) continue;

                // 原子操作：任何实参 varying 就无法向量化（每 lane 一次原子）
                if (name == "Increment" || name == "Decrement" || name == "Add" ||
                    name == "Exchange" || name == "CompareExchange")
                {
                    if (inv.Expression is MemberAccessExpressionSyntax mac
                        && mac.Expression.ToString().EndsWith("Interlocked"))
                    {
                        reason = $"Interlocked.{name}";
                        return true;
                    }
                    continue;
                }
                if (name == "ArrayElementAsRef")
                {
                    reason = "UnsafeUtility.ArrayElementAsRef";
                    return true;
                }
                if (VectorizableCallNames.Contains(name)) continue;

                foreach (var arg in inv.ArgumentList?.Arguments ?? default)
                {
                    try
                    {
                        if (varAnalyzer.ClassifyExpression(arg.Expression) >= VarKind.Varying)
                        {
                            reason = $"user/static call {name}(...) with a varying argument";
                            return true;
                        }
                    }
                    catch { }
                }
            }

            // 裸指针 + varying 下标 + 宽元素（float2/int2/自定义结构）：SIMD 路径只能取 lane0，
            // 等于每 lane 重复写同一地址 —— 静默错解，必须整段退回。
            // EmitElementStore 的掩码写，能正确向量化。
            foreach (var ea in node.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
            {
                if (ea.ArgumentList?.Arguments.Count == 0) continue;
                if (ea.Expression is not IdentifierNameSyntax baseId) continue;
                if (PointerElemCpp(baseId.Identifier.Text, varAnalyzer) is not { } elemCpp) continue;
                if (elemCpp == "float" || elemCpp == "int" || elemCpp == "unsigned char" || elemCpp == "signed char"
                    || elemCpp == "unsigned int" || elemCpp == "short" || elemCpp == "unsigned short" || elemCpp == "bool")
                    continue;
                try
                {
                    if (varAnalyzer.ClassifyExpression(ea.ArgumentList.Arguments[0].Expression) >= VarKind.Varying)
                    {
                        reason = $"deref of pointer '{baseId.Identifier.Text}' ({elemCpp}) at a varying index";
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        /// <summary>裸指针变量/形参名 → 元素 C++ 类型（无则 null）。</summary>
        private static string? PointerElemCpp(string name, SimdVariableAnalyzer varAnalyzer)
        {
            if (varAnalyzer.LocalPointerElemCpp.TryGetValue(name, out var le)) return le;
            if (varAnalyzer.ParamPointerElemCpp.TryGetValue(name, out var pe)) return pe;
            return null;
        }
    }
}
