using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;

namespace NativeTranspiler.Analyzer
{
    public partial class SimdControlFlowGenerator
    {
        /// <summary>
        /// 批内下标分解：`v_i`、`常量 + v_i`、`v_i ± 标量` 统一拆成"标量偏移"。
        /// 只接受标量来源（字面量、标量循环变量、展开期常量、uniform 局部/字段及其算术组合），
        /// 任何 simd 值都不参与偏移，避免把向量表达式塞进指针运算。
        /// </summary>
        private bool TryResolveBatchOffset(ExpressionSyntax expr, out string offset)
        {
            offset = null;
            if (expr == null || string.IsNullOrEmpty(_batchLoopVar)) return false;
            ExpressionSyntax e = StripParens(expr);
            if (e is IdentifierNameSyntax id && id.Identifier.Text == _indexParamName)
            {
                offset = "0";
                return true;
            }
            if (e is not BinaryExpressionSyntax bin) return false;
            if (bin.IsKind(SyntaxKind.AddExpression))
            {
                if (TryResolveBatchOffset(bin.Left, out string left) && TryScalarOffsetText(bin.Right, out string right))
                {
                    offset = MergeOffset(left, "+", right);
                    return true;
                }
                if (TryResolveBatchOffset(bin.Right, out string right2) && TryScalarOffsetText(bin.Left, out string left2))
                {
                    offset = MergeOffset(left2, "+", right2);
                    return true;
                }
            }
            else if (bin.IsKind(SyntaxKind.SubtractExpression)
                && TryResolveBatchOffset(bin.Left, out string leftSub)
                && TryScalarOffsetText(bin.Right, out string sub))
            {
                offset = MergeOffset(leftSub, "-", sub);
                return true;
            }
            return false;
        }

        /// <summary>连续指针偏移的完整文本（偏移 + 批内游标）；不是 `v_i ± 标量` 时返回 false。</summary>
        private bool TryResolveContiguousOffset(ExpressionSyntax indexSyntax, out string pointerOffset)
        {
            pointerOffset = null;
            if (!TryResolveBatchOffset(indexSyntax, out string offset)) return false;
            pointerOffset = offset == "0" ? _batchLoopVar : $"({offset}) + {_batchLoopVar}";
            return true;
        }

        private static string MergeOffset(string offset, string op, string scalar)
            => offset == "0" && op == "+" ? scalar : $"({offset}) {op} ({scalar})";

        /// <summary>标量偏移的 C++ 文本。只放行能保证生成标量代码的来源。</summary>
        private bool TryScalarOffsetText(ExpressionSyntax expr, out string scalar)
        {
            scalar = null;
            ExpressionSyntax e = StripParens(expr);
            switch (e)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression):
                    scalar = literal.Token.Text;
                    return true;

                case IdentifierNameSyntax id:
                {
                    string name = id.Identifier.Text;
                    if (_uniformIterationConstants.TryGetValue(name, out int constant))
                    {
                        scalar = constant.ToString(CultureInfo.InvariantCulture);
                        return true;
                    }
                    if (_uniformLoopVars.Contains(name))
                    {
                        scalar = name;
                        return true;
                    }
                    // 生成物里已声明成标量的局部（含由循环变量派生的 base/stride 之类）。
                    if (_scalarUniformNames.Contains(name))
                    {
                        scalar = name;
                        return true;
                    }
                    // for 循环变量（非 uniform 绑定）在 SIMD 侧是 simd_<name>，不能当标量用。
                    if (_forLoopVars.Contains(name)) return false;
                    if (_variables.TryGetValue(name, out var info))
                    {
                        if (info.Kind != VarKind.Uniform) return false;
                        scalar = name;
                        return true;
                    }
                    if (_jobStruct != null)
                    {
                        foreach (var member in _jobStruct.GetMembers(name))
                            if (member is IFieldSymbol { IsStatic: false })
                            {
                                scalar = name;
                                return true;
                            }
                    }
                    return false;
                }

                case BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.AddExpression)
                    || bin.IsKind(SyntaxKind.SubtractExpression)
                    || bin.IsKind(SyntaxKind.MultiplyExpression)
                    || bin.IsKind(SyntaxKind.LeftShiftExpression)
                    || bin.IsKind(SyntaxKind.RightShiftExpression)
                    || bin.IsKind(SyntaxKind.BitwiseAndExpression)
                    || bin.IsKind(SyntaxKind.BitwiseOrExpression):
                    if (TryScalarOffsetText(bin.Left, out string left) && TryScalarOffsetText(bin.Right, out string right))
                    {
                        scalar = $"({left}) {bin.OperatorToken.Text} ({right})";
                        return true;
                    }
                    return false;

                case CastExpressionSyntax cast:
                    if (TryScalarOffsetText(cast.Expression, out string inner))
                    {
                        scalar = $"(({cast.Type}) ({inner}))";
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        private static ExpressionSyntax StripParens(ExpressionSyntax expr)
        {
            while (expr is ParenthesizedExpressionSyntax paren) expr = paren.Expression;
            return expr;
        }
    }
}
