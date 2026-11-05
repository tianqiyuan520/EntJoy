using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public partial class SimdControlFlowGenerator
    {

        private string TranslateBinary(BinaryExpressionSyntax binary)
        {
            string left = TranslateExpression(binary.Left);
            string right = TranslateExpression(binary.Right);
            VarKind leftKind = _varAnalyzer.ClassifyExpression(binary.Left);
            VarKind rightKind = _varAnalyzer.ClassifyExpression(binary.Right);
            bool anyVarying = leftKind >= VarKind.Varying || rightKind >= VarKind.Varying;

            string op = binary.OperatorToken.Text;
            bool booleanOperands = IsBooleanExpression(binary.Left) && IsBooleanExpression(binary.Right);
            if (booleanOperands && anyVarying
                && !binary.IsKind(SyntaxKind.LogicalAndExpression) && !binary.IsKind(SyntaxKind.LogicalOrExpression))
            {
                if (leftKind >= VarKind.Varying) left = TranslateVaryingValue(binary.Left, "bool");
                if (rightKind >= VarKind.Varying) right = TranslateVaryingValue(binary.Right, "bool");
            }

            // Comparison operators → simd_mask
            if (binary.IsKind(SyntaxKind.LessThanExpression)
                || binary.IsKind(SyntaxKind.GreaterThanExpression)
                || binary.IsKind(SyntaxKind.LessThanOrEqualExpression)
                || binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression)
                || binary.IsKind(SyntaxKind.EqualsExpression)
                || binary.IsKind(SyntaxKind.NotEqualsExpression))
            {
                if (!anyVarying)
                {
                    // Uniform comparison
                    string cppOp = op switch
                    {
                        "<" => "<", ">" => ">", "<=" => "<=",
                        ">=" => ">=", "==" => "==", "!=" => "!=",
                        _ => op
                    };
                    return $"({left} {cppOp} {right})";
                }

                // Varying comparison → SIMD compare (detect int vs float)
                // Wrap complex expressions in parens so .v binds to the whole expression, not just the last term
                string leftV = leftKind >= VarKind.Varying ? $"({left}).v" : $"{left}";
                string rightV = rightKind >= VarKind.Varying ? $"({right}).v" : $"{right}";

                bool cmpIsInt = booleanOperands;
                bool cmpIsUint = false;   // (uint)x-style comparisons need unsigned semantics
                foreach (var side0 in new ExpressionSyntax[] { binary.Left, binary.Right })
                {
                    // Unwrap parentheses so (j & 1) is seen as a bitwise op, (uint)x as a cast.
                    var side = (side0 as ParenthesizedExpressionSyntax)?.Expression ?? side0;
                    // (uint)x / (int)x casts
                    if (side is CastExpressionSyntax castExpr)
                    {
                        if (castExpr.Type.ToString().Contains("uint"))
                            cmpIsUint = true;
                        var castInner = (castExpr.Expression as ParenthesizedExpressionSyntax)?.Expression ?? castExpr.Expression;
                        if (castInner is IdentifierNameSyntax ccid && _variables.TryGetValue(ccid.Identifier.Text, out var cciv) && cciv.CppType == "int") cmpIsInt = true;
                    }
                    if (side is IdentifierNameSyntax cid && _variables.TryGetValue(cid.Identifier.Text, out var civ) && civ.CppType == "int") cmpIsInt = true;
                    // Detect integer bitwise operations: x & 7u, x | mask, x ^ val
                    if (side is BinaryExpressionSyntax bitwise &&
                        (bitwise.IsKind(SyntaxKind.BitwiseAndExpression) ||
                         bitwise.IsKind(SyntaxKind.BitwiseOrExpression) ||
                         bitwise.IsKind(SyntaxKind.ExclusiveOrExpression)))
                        cmpIsInt = true;
                    // Detect unsigned integer literals: 7u, 0u, etc.
                    if (side is LiteralExpressionSyntax litExpr && litExpr.Token.Text.EndsWith("u"))
                        cmpIsUint = true;
                }
                // fallback to SemanticModel for int type detection.
                //   The above pattern matching only catches direct variable refs and bitwise ops,
                //   Use Roslyn GetTypeInfo to get the actual result type of each comparison operand.
                if (!cmpIsInt && !cmpIsUint)
                {
                    var leftType = SymbolHelper.GetBoundType(_semanticModel, binary.Left);
                    var rightType = SymbolHelper.GetBoundType(_semanticModel, binary.Right);
                    cmpIsInt = leftType?.SpecialType is SpecialType.System_Int32 or SpecialType.System_UInt32
                        || rightType?.SpecialType is SpecialType.System_Int32 or SpecialType.System_UInt32;
                    cmpIsUint = leftType?.SpecialType == SpecialType.System_UInt32 || rightType?.SpecialType == SpecialType.System_UInt32;
                }
                // Any explicit uint operand (u-literal or (uint) cast) makes the compare unsigned —
                // the variable may be recorded as int but its value is uint semantics.
                bool useUnsignedCmp = cmpIsUint;
                string bc = useUnsignedCmp || cmpIsInt ? "n_set1_epi32" : "n_set1_ps";
                // Hoisted broadcasts (__uni_ prefixed) are already SIMD — use .v, don't re-broadcast
                bool rightIsHoisted = right.StartsWith("__uni_");
                bool leftIsHoisted = left.StartsWith("__uni_");
                if (leftKind < VarKind.Varying && rightKind >= VarKind.Varying)
                {
                    if (leftIsHoisted)
                        rightV = $"({right}).v";
                    else
                        leftV = $"{bc}({left})";
                }
                else if (leftKind >= VarKind.Varying && rightKind < VarKind.Varying)
                {
                    if (rightIsHoisted)
                        rightV = $"({right}).v";
                    else
                        rightV = $"{bc}({right})";
                }
                // If hoisted broadcast ended up on wrong side, correct
                if (rightIsHoisted && !rightV.Contains(".v")) rightV = $"({right}).v";
                if (leftIsHoisted && !leftV.Contains(".v")) leftV = $"({left}).v";

                if (useUnsignedCmp) {
                    // Unsigned compare: x^0x80000000 converts two's-complement order to
                    // sign-magnitude order, so the signed compare works on the flipped values.
                    string flipConst = "(int)0x80000000";
                    string leftUniform = left.StartsWith("__uni_") ? $"({left}).v" : $"n_set1_epi32({left})";
                    string rightUniform = right.StartsWith("__uni_") ? $"({right}).v" : $"n_set1_epi32({right})";
                    string leftFlip = leftKind >= VarKind.Varying ? $"simd_value<int>{{ n_xor_epi32({leftV}, n_set1_epi32({flipConst})) }}.v" : $"n_xor_epi32({leftUniform}, n_set1_epi32({flipConst}))";
                    string rightFlip = rightKind >= VarKind.Varying ? $"simd_value<int>{{ n_xor_epi32({rightV}, n_set1_epi32({flipConst})) }}.v" : $"n_xor_epi32({rightUniform}, n_set1_epi32({flipConst}))";
                    string ic2 = op switch {
                        "<" => "n_cmp_lt_epi32", ">" => "n_cmp_gt_epi32", "<=" => "n_cmp_le_epi32",
                        ">=" => "n_cmp_ge_epi32", "==" => "n_cmp_eq_epi32", "!=" => "n_cmp_ne_epi32",
                        _ => "n_cmp_eq_epi32"
                    };
                    return $"simd_mask{{ {ic2}({leftFlip}, {rightFlip}) }}";
                }
                if (cmpIsInt) {
                    string ic = op switch {
                        "<" => "n_cmp_lt_epi32", ">" => "n_cmp_gt_epi32", "<=" => "n_cmp_le_epi32",
                        ">=" => "n_cmp_ge_epi32", "==" => "n_cmp_eq_epi32", "!=" => "n_cmp_ne_epi32",
                        _ => "n_cmp_eq_epi32"
                    };
                    return $"simd_mask{{ {ic}({leftV}, {rightV}) }}";
                }
                string fc = op switch {
                    "<" => "n_cmp_lt_ps", ">" => "n_cmp_gt_ps", "<=" => "n_cmp_le_ps",
                    ">=" => "n_cmp_ge_ps", "==" => "n_cmp_eq_ps", "!=" => "n_cmp_ne_ps",
                    _ => "n_cmp_eq_ps"
                };
                return $"simd_mask{{ {fc}({leftV}, {rightV}) }}";
            }

            // 布尔局部变量存为整数向量；逻辑运算统一通过条件转换生成掩码。
            if (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression))
            {
                if (!anyVarying) return $"({left} {op} {right})";
                string maskOp = binary.IsKind(SyntaxKind.LogicalAndExpression) ? "n_and_mask" : "n_or_mask";
                return $"simd_mask{{ {maskOp}(({TranslateCondition(binary.Left)}).m, ({TranslateCondition(binary.Right)}).m) }}";
            }

            // Arithmetic operators
            if (!anyVarying)
            {
                return $"({left} {op} {right})";
            }

            // FMA detection: float a*b + c → n_fmadd_ps(a, b, c)
            //   显式生成 FMA 融合，回收 clang 因 int→float 转换（n_cvtepi32_ps）而
            //   未收缩 mul+add 的那条指令（C12 acc*(j+1)+B[i] 等模式）。
            if (anyVarying && op == "+" && binary.Left is BinaryExpressionSyntax lmul
                && lmul.OperatorToken.Text == "*" && !IsInt32Type(lmul))
            {
                string fa = ToFloatVec(TranslateExpression(lmul.Left), lmul.Left);
                string fb = ToFloatVec(TranslateExpression(lmul.Right), lmul.Right);
                string fc = ToFloatVec(TranslateExpression(binary.Right), binary.Right);
                return $"simd_value<float>{{ n_fmadd_ps({fa}, {fb}, {fc}) }}";
            }

            // At least one varying — SIMD arithmetic
            // Type-specific optimization: constant modulo
            if (anyVarying && op == "%" && binary.Right is LiteralExpressionSyntax lit
                && lit.Token.Value is uint modVal && modVal > 0)
            {
                // Power of 2: x % (2^n) = x & (2^n - 1)
                if ((modVal & (modVal - 1)) == 0)
                    return $"({left} & {modVal - 1}u)";
                // General case: optimized magic number multiplication
                if (modVal <= 10000)
                    return $"simd_mod_u32({left}, {modVal}u)";
            }

            string simdOp = op switch
            {
                "+" => "+",
                "-" => "-",
                "*" => "*",
                "/" => "/",
                ">>" => ">>",
                "<<" => "<<",
                "&" => "&",
                "|" => "|",
                "^" => "^",
                "%" => "%",
                _ => "+"
            };

            // uint right shift: C# `uint >> n` is logical (zero-extended), but C++
            //   `int >> n` is arithmetic (sign-extended). Detect uint left operand and
            //   so we use the variable analyzer's CSharpType field instead.
            if (op == ">>" && anyVarying)
            {
                bool leftIsUint = false;
                // Check variable analyzer's CSharpType for the left operand
                if (binary.Left is IdentifierNameSyntax id && _variables.TryGetValue(id.Identifier.Text, out var varInfo))
                {
                    if (varInfo.CSharpType == "uint")
                        leftIsUint = true;
                }
                // Fallback: check SemanticModel
                if (!leftIsUint)
                    leftIsUint = SymbolHelper.GetBoundType(_semanticModel, binary.Left)?.SpecialType == SpecialType.System_UInt32;

                if (leftIsUint)
                {
                    string leftV = leftKind >= VarKind.Varying ? $"({left}).v" : left;
                    return $"simd_value<int>{{ n_srli_epi32({leftV}, {right}) }}";
                }
            }

            return $"({left} {simdOp} {right})";
        }

        /// <summary>
        /// 将算术操作数转成 n_float 向量表达式（供 n_fmadd_ps 使用）。
        /// 处理 varying/uniform 广播、int→float 转换（n_cvtepi32_ps）、
        /// 以及已 hoist 的广播（__uni_ 前缀）。
        /// </summary>
        private string ToFloatVec(string translated, ExpressionSyntax expr)
        {
            bool isInt = IsInt32Type(expr);
            if (translated.StartsWith("__uni_"))
                return isInt ? $"n_cvtepi32_ps(({translated}).v)" : $"({translated}).v";
            VarKind kind = _varAnalyzer.ClassifyExpression(expr);
            if (kind >= VarKind.Varying)
                return isInt ? $"n_cvtepi32_ps(({translated}).v)" : $"({translated}).v";
            return isInt ? $"n_cvtepi32_ps(n_set1_epi32({translated}))" : $"n_set1_ps({translated})";
        }

        private string TranslateCast(CastExpressionSyntax cast)
        {
            string inner = TranslateExpression(cast.Expression);
            VarKind innerKind = _varAnalyzer.ClassifyExpression(cast.Expression);
            string targetTypeStr = cast.Type.ToString();

            // Hoisted broadcasts are already the correct SIMD type — skip cast entirely
            if (inner.StartsWith("__uni_"))
                return inner;

            // For varying int -> unsigned int: keep {inner} as-is (n_cmp_*_epi32 works on raw n_int)
            if (innerKind >= VarKind.Varying && (targetTypeStr == "uint" || targetTypeStr == "unsigned int"))
                return $"{inner}";

            // SIMD type conversions: (int2)simd_value<float2> → simd_value<int2>::convert(...)
            if (innerKind >= VarKind.Varying)
            {
                if (targetTypeStr.Contains("int2"))
                    return $"simd_value<EntJoy::Mathematics::int2>::convert({inner})";
                if (targetTypeStr == "int" || targetTypeStr == "System.Int32")
                    return $"simd_value<int>::convert({inner})";
                // (float2)simd_value<float2> → just the inner value (identity conversion)
                if (targetTypeStr.Contains("float2"))
                    return inner;
            }

            // (int)floatExpr → scalar convert (uniform path)
            var targetType = SymbolHelper.GetBoundType(_semanticModel, cast.Type);
            if (targetType != null)
                return $"({NativeTranspiler.MapCSharpTypeToCpp(targetType)}){inner}";

            return $"({targetTypeStr.Replace(".", "::")}){inner}";
        }

        /// <summary>
        /// 语句位置的自增/自减按活动掩码更新目标；表达式位置由向量化能力校验处理。
        /// </summary>
        private string TranslateIncrementExpression(ExpressionSyntax operand, string op)
        {
            if (operand is MemberAccessExpressionSyntax)
                return TranslateAssignment(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.AssignmentExpression(
                    op == "+=" ? SyntaxKind.AddAssignmentExpression : SyntaxKind.SubtractAssignmentExpression,
                    operand, Microsoft.CodeAnalysis.CSharp.SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Literal(1))));
            string marker = $"/*{UnsupportedMarkers.Expr}IncrementOnNonLvalue({operand.Kind()})*/ 0";
            if (operand is ElementAccessExpressionSyntax ea
                && ea.Expression is IdentifierNameSyntax aid
                && ea.ArgumentList?.Arguments.Count > 0
                && NativeArrayBaseInfo(aid) is { } info)
            {
                string idxExpr = TranslateExpression(ea.ArgumentList.Arguments[0].Expression);
                VarKind idxKind = _varAnalyzer.ClassifyExpression(ea.ArgumentList.Arguments[0].Expression);
                // 数组字段 `X_ptr` 已是目标元素类型的指针，直接用；
                // 裸指针变量本身是指针，同样直接用（不要转型 —— `(T*)p[i]` 是非法 lvalue 转型）。
                string elemPtr = info.Base;
                bool narrowed = _currentMask != "simd_mask::all_true()";
                string guard = narrowed ? $"if((n_mask_to_bitmask(({_currentMask}).m)&(1<<__l))!=0)" : "";
                if (idxKind >= VarKind.Varying)
                {
                    string lv = $"{elemPtr}[n_extract_lane_epi32(({idxExpr}).v,__l)]";
                    return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{guard}{lv} {op} 1;}}}}";
                }
                return $"{elemPtr}[{idxExpr}] {op} 1;";
            }
            if (operand is IdentifierNameSyntax identifier)
            {
                string target = TranslateExpression(operand);
                if (target.Contains(UnsupportedMarkers.Prefix)) return marker;
                if (_currentMask != "simd_mask::all_true()"
                    && (_forLoopVars.Contains(identifier.Identifier.Text)
                        || (_variables.TryGetValue(identifier.Identifier.Text, out var variable)
                            && variable.Kind >= VarKind.Varying)))
                {
                    string operation = op == "+=" ? "+" : "-";
                    return $"{target} = blend({target}, {target} {operation} 1, {_currentMask});";
                }
                return $"{target} {op} 1;";
            }
            return marker;
        }
        private string TranslateTernary(ConditionalExpressionSyntax ternary)
        {
            string condition = TranslateCondition(ternary.Condition);
            string whenTrue = TranslateExpression(ternary.WhenTrue);
            string whenFalse = TranslateExpression(ternary.WhenFalse);
            VarKind kind = _varAnalyzer.ClassifyExpression(ternary);

            if (kind >= VarKind.Varying)
            {
                if (IsBooleanExpression(ternary))
                {
                    whenTrue = TranslateVaryingValue(ternary.WhenTrue, "bool");
                    whenFalse = TranslateVaryingValue(ternary.WhenFalse, "bool");
                    return $"blend({whenFalse}, {whenTrue}, {condition})";
                }
                if (_varAnalyzer.ClassifyExpression(ternary.WhenTrue) < VarKind.Varying
                    && _varAnalyzer.ClassifyExpression(ternary.WhenFalse) < VarKind.Varying)
                {
                    var type = SymbolHelper.GetBoundType(_semanticModel, ternary);
                    var cppType = type != null ? NativeTranspiler.MapCSharpTypeToCpp(type) : "";
                    var simdType = GetSIMDTypeString(cppType);
                    if (simdType == null) return $"/*{UnsupportedMarkers.Expr}SIMD_ConditionalValueType*/ 0";
                    whenTrue = $"{simdType}({whenTrue})";
                    whenFalse = $"{simdType}({whenFalse})";
                }
                // SIMD blend: mask ? true_val : false_val
                return $"blend({whenFalse}, {whenTrue}, {condition})";
            }

            return $"({TranslateExpression(ternary.Condition)} ? {whenTrue} : {whenFalse})";
        }
    }
}
