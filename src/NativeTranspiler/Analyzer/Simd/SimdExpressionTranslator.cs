using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 表达式分派、字面量、符号绑定与标量/向量布尔值转换。
    /// </summary>
    public partial class SimdControlFlowGenerator
    {
        // Expression Translation (core)

        /// <summary>
        /// 将 C# 表达式翻译为 C++ 表达式字符串。
        /// 返回的字符串可能是标量（uniform）或 simd_value（varying）。
        /// </summary>
        private string TranslateExpression(ExpressionSyntax expr)
        {
            switch (expr)
            {
                case LiteralExpressionSyntax literal:
                    return TranslateLiteral(literal);

                case IdentifierNameSyntax identifier:
                    return TranslateIdentifier(identifier);

                case MemberAccessExpressionSyntax memberAccess:
                    return TranslateMemberAccess(memberAccess);

                case ElementAccessExpressionSyntax elementAccess:
                    // 按父节点决定返回形态（不能用位置标志位，见文档）：
                    //   出现在另一个元素访问的下标位置（`A[B[i]]`）→ 必须是标量；
                    //   其它位置（条件/赋值 RHS）→ 调用方会取 `.v`，所以必须是 `.v`-able 的 SIMD 包装。
                    return TranslateElementAccess(elementAccess, asScalarIndex: IsSubscriptOfAnotherElementAccess(elementAccess));

                case InvocationExpressionSyntax invocation:
                    return TranslateInvocation(invocation);

                case BinaryExpressionSyntax binary:
                    return TranslateBinary(binary);

                case PrefixUnaryExpressionSyntax prefix:
                    // For !, -, ~
                    if (prefix.IsKind(SyntaxKind.LogicalNotExpression))
                    {
                        if (_varAnalyzer.ClassifyExpression(prefix) < VarKind.Varying)
                            return $"(!({TranslateExpression(prefix.Operand)}))";
                        return $"simd_mask{{ n_not_mask(({TranslateCondition(prefix.Operand)}).m) }}";
                    }
                    if (prefix.IsKind(SyntaxKind.BitwiseNotExpression))
                    {
                        // ~x → x ^ -1 (bitwise NOT). The old code emitted "-x" for "~x",
                        //   producing ~3 = -3 instead of -4 (ST6: s & ~3 → s & -3, off-by-one).
                        string inner = TranslateExpression(prefix.Operand);
                        return $"({inner} ^ -1)";
                    }
                    if (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression))
                        return TranslateIncrementExpression(prefix.Operand, prefix.IsKind(SyntaxKind.PreIncrementExpression) ? "+=" : "-=");
                    return $"(0 - ({TranslateExpression(prefix.Operand)}))";

                case ParenthesizedExpressionSyntax paren:
                    return $"({TranslateExpression(paren.Expression)})";

                case CastExpressionSyntax cast:
                    return TranslateCast(cast);

                case AssignmentExpressionSyntax assign:
                    return TranslateAssignment(assign);

                case CheckedExpressionSyntax checkedExpr:
                    // `unchecked(x + y)` / `checked(x + y)` → translate the inner expr.
                    //   EntJoy arithmetic is always unchecked (wraps), so the flag is a no-op.
                    return TranslateExpression(checkedExpr.Expression);

                case ConditionalExpressionSyntax ternary:
                    return TranslateTernary(ternary);

                case ObjectCreationExpressionSyntax objCreation:
                    return TranslateObjectCreation(objCreation);

                // 自增/自减：`histPtr[key]++` 之前落到兜底 → 写入被静默丢弃（Y 排序直方图算错）。
                case PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    return TranslateIncrementExpression(postfix.Operand, postfix.IsKind(SyntaxKind.PostIncrementExpression) ? "+=" : "-=");

                // 兜底必须是可检测标记，不能是静默的 `0`。
                //   返回 `0` 会让整条语句变成 `0;` —— 写入被丢弃且编译通过（N-10 / YSortRangeJob 两次事故）。
                default:
                    return $"/*{UnsupportedMarkers.Expr}{expr.Kind()}*/ 0";
            }
        }

        /// <summary>
        /// 将条件表达式翻译为 simd_mask。
        /// </summary>
        private string TranslateCondition(ExpressionSyntax expr)
        {
            if (expr is IdentifierNameSyntax id)
            {
                string name = id.Identifier.Text;
                // Bool field with known constant → skip computation, MSVC handles DCE
                if ((SymbolHelper.GetBoundSymbol(_semanticModel, id) is IFieldSymbol
                    || id.SyntaxTree != _semanticModel.SyntaxTree && !_variables.ContainsKey(name))
                    && _boolFields.TryGetValue(name, out var bv))
                    return bv == "true" ? "simd_mask::all_true()" : "simd_mask{ n_cmp_ne_epi32(n_set1_epi32(0), n_set1_epi32(0)) }";
                // Uniform bool: broadcast to all lanes then compare !=0 to produce proper n_mask
                bool isLocalValue = SymbolHelper.GetBoundSymbol(_semanticModel, id) is not IFieldSymbol;
                if (isLocalValue && _variables.TryGetValue(name, out var info) && info.Kind == VarKind.Uniform)
                    return $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({name} ? -1 : 0).v, n_set1_epi32(0)) }}";
                // Varying int/bool: compare register against zero
                if (isLocalValue && _variables.TryGetValue(name, out var info2) && info2.Kind >= VarKind.Varying)
                    return $"simd_mask{{ n_cmp_ne_epi32(v_{name}.v, n_set1_epi32(0)) }}";
            }

            string result = TranslateExpression(expr);
            if (_varAnalyzer.ClassifyExpression(expr) < VarKind.Varying)
                return $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({result} ? -1 : 0).v, n_set1_epi32(0)) }}";
            return ProducesMask(expr) ? result : $"simd_mask{{ n_cmp_ne_epi32(({result}).v, n_set1_epi32(0)) }}";
        }

        private static bool ProducesMask(ExpressionSyntax expression)
            => expression switch
            {
                ParenthesizedExpressionSyntax parenthesized => ProducesMask(parenthesized.Expression),
                CheckedExpressionSyntax checkedExpression => ProducesMask(checkedExpression.Expression),
                PrefixUnaryExpressionSyntax prefix => prefix.IsKind(SyntaxKind.LogicalNotExpression),
                BinaryExpressionSyntax binary => binary.Kind() is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
                    or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression
                    or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
                    or SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression,
                _ => false
            };

        private bool IsBooleanExpression(ExpressionSyntax expression)
            => SymbolHelper.GetBoundType(_semanticModel, expression)?.SpecialType == SpecialType.System_Boolean
                || expression is IdentifierNameSyntax id && _variables.TryGetValue(id.Identifier.Text, out var info) && info.CppType == "bool";

        // Expression Sub-Translators

        // bool 值存为 0/1 的整数向量；条件掩码只用于控制流。
        private string TranslateVaryingValue(ExpressionSyntax expression, string cppType)
        {
            if (cppType != "bool") return TranslateExpression(expression);
            if (_varAnalyzer.ClassifyExpression(expression) < VarKind.Varying)
                return $"simd_value<int>::broadcast(({TranslateExpression(expression)}) ? 1 : 0)";
            return ProducesMask(expression)
                ? $"blend(simd_value<int>(0), simd_value<int>(1), {TranslateCondition(expression)})"
                : TranslateExpression(expression);
        }

        private string TranslateLiteral(LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
                return "true";
            if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
                return "false";
            string text = literal.Token.Text;
            // Fix: integer-valued float literals (40f, -3f, 2f) must become
            //   40.0f / -3.0f / 2.0f — "40f" is an invalid C++ decimal constant.
            if (literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
                (text.EndsWith("f") || text.EndsWith("F")))
            {
                string num = text.Substring(0, text.Length - 1);
                if (!num.Contains('.') && !num.Contains('e') && !num.Contains('E'))
                    text = num + ".0f";
            }
            else if (literal.IsKind(SyntaxKind.NumericLiteralExpression) && text is ("." or "0" or "-0"))
            {
                // edge: integer literals are passed through as-is (valid C++)
            }
            // NT-13：C# 的 64 位后缀（1UL / 1L）在 C++/LLP64 下只有 32 位 ⇒ 必须归一化，
            //   否则向量化部分的移位/掩码表达式静默错值（与标量路径共用同一规则，避免再次分叉）。
            if (literal.IsKind(SyntaxKind.NumericLiteralExpression))
                text = CppNumericLiteral.NormalizeSuffix(text);
            return text;
        }

        private string TranslateIdentifier(IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.Text;
            if (SymbolHelper.GetBoundSymbol(_semanticModel, identifier) is IFieldSymbol boundField && !boundField.IsStatic
                && SymbolEqualityComparer.Default.Equals(boundField.ContainingType, _jobStruct))
            {
                if (_boolFields.TryGetValue(name, out var constant)) return constant;
                return boundField.Type is IPointerTypeSymbol ? name + "_ptr" : name;
            }

            // The Execute index parameter → SIMD index var
            if (name == _indexParamName)
                return _simdIndexVar;

            // For-loop induction variables → use simd_ prefix (no conflict risk)
            if (_forLoopVars.Contains(name))
                return $"simd_{name}";

            if (identifier.SyntaxTree != _semanticModel.SyntaxTree && !_variables.ContainsKey(name)
                && _boolFields.TryGetValue(name, out var fieldConstant)) return fieldConstant;

            // Bool field with known constant → return literal (MSVC DCE handles the rest)
            // Deferred struct local (initialized from struct array element access)
            if (_structVaryingLocals.ContainsKey(name))
                return name;

            // Known variable (from SimdVariableAnalyzer)
            if (_variables.TryGetValue(name, out var info))
            {
                if (info.Kind == VarKind.Uniform)
                    return name; // scalar

                // Varying or Reduction
                if (IsFloat2Type(info.CppType))
                {
                    return $"v_{name}"; // float2 — use member access for components
                }
                return $"v_{name}";
            }

            // Job struct field (resolve via _jobStruct symbol, not semantic model)
            if (_jobStruct != null)
            {
                var members = _jobStruct.GetMembers(name);
                if (members.Length > 0 && members[0] is IFieldSymbol field && !field.IsStatic)
                {
                    if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                        return name;
                    return name;
                }
            }

            // Fallback: use name as-is
            return name;
        }

        private string TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is ThisExpressionSyntax && memberAccess.Name is IdentifierNameSyntax field)
                return TranslateIdentifier(field);
            string memberName = memberAccess.Name.Identifier.Text;

                        // Struct NativeArray field access: structArray[idx].fieldName
            //   Generate field-level gather with struct stride (ISPC-style AoS pattern).
            if (memberAccess.Expression is ElementAccessExpressionSyntax ea
                && ea.Expression is IdentifierNameSyntax arrId)
            {
                string arrName = arrId.Identifier.Text;
                if (_nativeArrayParams.TryGetValue(arrName, out var structElemType)
                    && structElemType != "float" && structElemType != "int"
                    && !structElemType.Contains("float2") && !structElemType.Contains("int2"))
                {
                    return TranslateStructArrayFieldAccess(arrName, structElemType, memberName,
                        ea.ArgumentList?.Arguments.Count > 0 ? ea.ArgumentList.Arguments[0].Expression : null,
                        StructFieldType(memberAccess, structElemType, memberName), StructFieldStride(memberAccess));
                }
            }

            // Deferred struct local field access: structLocal.fieldName
            //   Where structLocal was initialized from structArray[idx].
            //   → field-level gather with struct stride
            if (memberAccess.Expression is IdentifierNameSyntax structLocalId
                && _structVaryingLocals.TryGetValue(structLocalId.Identifier.Text, out var structLocalInfo))
            {
                return TranslateStructFieldAccess(structLocalInfo.arrName, structLocalInfo.elemCppType,
                    memberName, structLocalInfo.indexExpr, StructFieldType(memberAccess, structLocalInfo.elemCppType, memberName), StructFieldStride(memberAccess));
            }

            string objExpr = TranslateExpression(memberAccess.Expression);

            // Check if the object is a varying float2/int2
            string objName = memberAccess.Expression is IdentifierNameSyntax id ? id.Identifier.Text : null;
            bool isVaryingFloat2 = objName != null && _float2VaryingVars.Contains(objName);

            // 标量位置的字段读取：接收者不是向量 → 绝不做 `.v` 包装。
            //   （`CpuUnitConfigData cfg = cfgPtr[cfgIdPtr[index]]; cfg.FramesDeath` 的接收者翻译出来是
            //    `simd_value<int>{ n_load_epi32(cfgIdPtr + si) }` 这种标量 SIMD 包装，本身无 `.v`。）
            //   2 分量向量（变量名或 `simd_value<int2>::gather(...)` 这类表达式）的分量是字段不是方法，
            //   所以它既不能走标量回退，也不能被 `!objExpr.Contains(".v")` 判成标量。
            bool twoComponentVectorExpr = objExpr.Contains("simd_value<EntJoy::Mathematics::float2")
                || objExpr.Contains("simd_value<EntJoy::Mathematics::int2")
                || objExpr.Contains("simd_value<EntJoy::Mathematics::uint2");
            bool scalarReceiver = !objExpr.Contains(".v") && !isVaryingFloat2 && !twoComponentVectorExpr;

            // .MaxValue / .MinValue — 必须早于 scalarReceiver 的标量回退分支：
            // 接收者是预定义类型（`float.MaxValue` / `int.MaxValue`）时，TranslateExpression(接收者)
            // 本分支不需要 objExpr（类型信息从接收者文本/语义模型取），因此放在最前面即可。
            if (memberName == "MaxValue" || memberName == "MinValue")
            {
                bool isInt = true;
                string recv = memberAccess.Expression.ToString();
                if (recv.Contains("float") || recv.Contains("double") || recv.Contains("Single") || recv.Contains("Double"))
                    isInt = false;
                // SemanticModel fallback: resolve the receiver's type when it's not a literal keyword
                if (recv != "int" && recv != "float" && recv != "double" && recv != "long")
                {
                    try
                    {
                        var t = SymbolHelper.GetBoundType(_semanticModel, memberAccess.Expression);
                        if (t != null && (t.SpecialType == SpecialType.System_Single || t.SpecialType == SpecialType.System_Double))
                            isInt = false;
                    }
                    catch { }
                }
                if (isInt)
                {
                    string sign = memberName == "MaxValue" ? "max" : "min";
                    return $"std::numeric_limits<int>::{sign}()";
                }
                string fsign = memberName == "MaxValue" ? "max" : "lowest";
                return $"std::numeric_limits<float>::{fsign}()";
            }

            // .zero on int2/uint2/float2 → 构造函数。接收者是类型名、翻译结果不含 `.v`，
            // 必须早于 scalarReceiver 回退，否则会被原样输出成 `int2.zero`。
            if (memberName == "zero")
            {
                if (objExpr.Contains("uint2"))
                    return "EntJoy::Mathematics::uint2(0u, 0u)";
                if (objExpr.Contains("int2"))
                    return "EntJoy::Mathematics::int2(0, 0)";
                if (objExpr.Contains("float2"))
                    return "EntJoy::Mathematics::float2(0.0f, 0.0f)";
            }

            // .x / .y 的向量成员形式只有真向量才成立；标量接收者走值类型路径（`p.x()`）。
            if ((memberName == "x" || memberName == "y") && scalarReceiver)
            {
                if (memberAccess.Expression is IdentifierNameSyntax hoistId2)
                {
                    string key2 = $"{hoistId2.Identifier.Text}.{memberName}";
                    if (_uniformHoistMap.TryGetValue(key2, out var hoistVar2))
                        return hoistVar2;
                }
                return $"{objExpr}.{memberName}()";
            }

            // NativeArray 的长度由独立的标量形参传入。
            if (memberName == "Length" && memberAccess.Expression is IdentifierNameSyntax arrayId
                && IsNativeArrayField(arrayId))
                return $"{arrayId.Identifier.Text}_length";

            // 标量 SIMD 包装上取字段：退回标量 C++ 表达式。
            //   结构体 → `expr.field`；数学向量 → `expr.x()`（上面已处理）。
            //   若在这里无条件补 `.v`，生成 `simd_value<int>{...}.FramesDeath` → clang 报
            //   「member reference base type 'int' is not a structure or union」（MarkDeadJob 实测）。
            if (scalarReceiver) return $"{objExpr}.{memberName}";

            // .MaxValue / .MinValue 与 .zero 已在上面（scalarReceiver 回退之前）处理。

            // .Length on NativeArray → _length suffix
            // ⚠ NativeArray 字段的 C++ 名是 `X_ptr`（NativeArrayBase），但长度形参是 `X_length`。
            //   直接用 objExpr 会生成 `X_ptr_length`（未声明）。
            if (memberName == "Length")
            {
                return $"{objExpr}_length";
            }

            // .x or .y on float2 — use .x/.y member on simd_value<float2>
            if ((memberName == "x" || memberName == "y") && isVaryingFloat2)
            {
                return $"{objExpr}.{memberName}";
            }

            // simd_value<float2> 整体（双通道 gather）→ .x/.y 直接取成员
            if ((memberName == "x" || memberName == "y") && objExpr.StartsWith("simd_value<EntJoy::Mathematics::float2>"))
            {
                return $"{objExpr}.{memberName}";
            }

            // Struct field gather result: n_gather_ps already returns the component value.
            //   For .x on a float2 field gather, just return the gather (it's already x).
            //   For .y, we need the gather at offset+1 (y is at +4 bytes).
            if ((memberName == "x" || memberName == "y") && objExpr.Contains("n_gather_ps<"))
            {
                                if (memberName == "y")
                {
                    // For .y: use same gather expression but at base+1 float offset.
                    // The n_gather_ps reads float at arr_ptr[v_i].field.
                    // For .y we need: arr_ptr[v_i].field_y which is at offset +4 bytes.
                    // Simple approach: append " + 1" before v_i.v to advance pointer by 1 float.
                    string modified = objExpr.Replace(", v_i.v)", " + 1, v_i.v)");
                    return modified;
                }// For .x: the n_gather_ps already reads at the field offset, returning x component
                return objExpr;
            }

            // Check for hoisted uniform broadcast (pre-broadcast once, reuse in SIMD)
            if ((memberName == "x" || memberName == "y") && !isVaryingFloat2)
            {
                if (memberAccess.Expression is IdentifierNameSyntax hoistId)
                {
                    string key = $"{hoistId.Identifier.Text}.{memberName}";
                    if (_uniformHoistMap.TryGetValue(key, out var hoistVar))
                        return hoistVar;
                }
                // EntJoy Mathematics types use method syntax: .x() not .x
                // ⚠ 只有确实是向量的表达式才用 `.x/.y` 成员形式。旧判据 `Contains("::") || StartsWith("simd_")`
                //   过宽：`simd_value<int>{ n_load_epi32(cfgIdPtr + si) }` 这种标量 SIMD 包装也命中，
                //   于是 `.FramesDeath` 被翻成 `simd_value<int>{...}.FramesDeath` → clang 报
                //   「member reference base type 'int' is not a structure or union」（MarkDeadJob 实测）。
                //   真正的向量表达式一定含 `.v`。
                if (objExpr.Contains(".v") || objExpr.StartsWith("simd_value<"))
                    return $"{objExpr}.{memberName}";
                return $"{objExpr}.{memberName}()";
            }
            return $"{objExpr}.{memberName}";
        }

        private string TranslateObjectCreation(ObjectCreationExpressionSyntax objCreation)
        {
                        INamedTypeSymbol? type = null;
            type = SymbolHelper.GetBoundType(_semanticModel, objCreation) as INamedTypeSymbol;
            string cppType = type != null ? NativeTranspiler.MapCSharpTypeToCpp(type) : "int";

            if (objCreation.ArgumentList != null && objCreation.ArgumentList.Arguments.Count > 0)
            {
                string args = string.Join(", ",
                    objCreation.ArgumentList.Arguments.Select(a => TranslateExpression(a.Expression)));
                return $"{cppType}({args})";
            }

            return $"{cppType}()";
        }

        // Utilities

        /// <summary>
        /// 提取 SIMD 值中的 x/y 分量。
        /// 用于 float2 SIMD gather → 组件赋值
        /// </summary>
        /// <summary>
        /// 获取 C# 类型对应的 SIMD C++ 类型字符串
        /// </summary>
        private static string? GetSIMDTypeString(string cppType)
        {
            if (cppType.Contains("float2"))
                return "simd_value<EntJoy::Mathematics::float2>";
            if (cppType.Contains("int2"))
                return "simd_value<EntJoy::Mathematics::int2>";
            if (cppType == "float")
                return "simd_value<float>";
            if (cppType == "int")
                return "simd_value<int>";
            if (cppType == "bool")
                return "simd_value<int>";
            return null;
        }

        private static bool IsFloat2Type(string cppType)
        {
            return cppType.Contains("float2") || cppType.Contains("int2");
        }
    }
}
