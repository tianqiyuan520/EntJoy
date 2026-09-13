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
    /// 本 partial 文件负责表达式翻译：
    /// TranslateExpression / TranslateMath* / TranslateBinary* /
    /// TranslateCast* / TranslateAssignment* 等表达式生成方法。
    /// 与 SimdControlFlowGenerator 主文件和 SimdLoopGenerator
    /// 属于同一个 partial class，可自由互相调用。
    /// </summary>
    public partial class SimdControlFlowGenerator
    {
        // ================================================================
        // Expression Translation (core)
        // ================================================================

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
                    // ★ 按**父节点**决定返回形态（不能用位置标志位，见文档 §13.20）：
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
                        return $"simd_mask{{ n_not_mask({TranslateExpression(prefix.Operand)}.m) }}";
                    if (prefix.IsKind(SyntaxKind.BitwiseNotExpression))
                    {
                        // ★ ~x → x ^ -1 (bitwise NOT). The old code emitted "-x" for "~x",
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
                    // ★ `unchecked(x + y)` / `checked(x + y)` → translate the inner expr.
                    //   EntJoy arithmetic is always unchecked (wraps), so the flag is a no-op.
                    return TranslateExpression(checkedExpr.Expression);

                case ConditionalExpressionSyntax ternary:
                    return TranslateTernary(ternary);

                case ObjectCreationExpressionSyntax objCreation:
                    return TranslateObjectCreation(objCreation);

                // ★ 自增/自减：`histPtr[key]++` 之前落到兜底 → 写入被静默丢弃（Y 排序直方图算错）。
                case PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    return TranslateIncrementExpression(postfix.Operand, postfix.IsKind(SyntaxKind.PostIncrementExpression) ? "+=" : "-=");

                // ★ 兜底必须是**可检测标记**，不能是静默的 `0`。
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
                // ★ Bool field with known constant → skip computation, MSVC handles DCE
                if (_boolFields.TryGetValue(name, out var bv))
                    return bv == "true" ? "simd_mask::all_true()" : "simd_mask::all_false()";
                // Uniform bool: broadcast to all lanes then compare !=0 to produce proper n_mask
                if (_variables.TryGetValue(name, out var info) && info.Kind == VarKind.Uniform)
                    return $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({name} ? -1 : 0).v, n_set1_epi32(0)) }}";
                // Varying int/bool: compare register against zero
                if (_variables.TryGetValue(name, out var info2) && info2.Kind >= VarKind.Varying)
                    return $"simd_mask{{ n_cmp_ne_epi32(v_{name}.v, n_set1_epi32(0)) }}";
            }

            string result = TranslateExpression(expr);
            // If result is a scalar bool, wrap it as simd_mask via broadcast+compare
            if (!result.Contains("simd_mask") && !result.Contains("n_cmp_"))
                return $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({result} ? -1 : 0).v, n_set1_epi32(0)) }}";
            return result;
        }

        // ================================================================
        // Expression Sub-Translators
        // ================================================================

        private string TranslateLiteral(LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
                return "true";
            if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
                return "false";
            string text = literal.Token.Text;
            // ★ Fix: integer-valued float literals (40f, -3f, 2f) must become
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
            return text;
        }

        private string TranslateIdentifier(IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.Text;

            // The Execute index parameter → SIMD index var
            if (name == _indexParamName)
                return _simdIndexVar;

            // For-loop induction variables → use simd_ prefix (no conflict risk)
            if (_forLoopVars.Contains(name))
                return $"simd_{name}";

            // ★ Bool field with known constant → return literal (MSVC DCE handles the rest)
            if (_boolFields.TryGetValue(name, out var bv))
                return bv;  // "true" or "false"

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
            string memberName = memberAccess.Name.Identifier.Text;

                        // ★ Struct NativeArray field access: structArray[idx].fieldName
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
                        ea.ArgumentList?.Arguments.Count > 0 ? ea.ArgumentList.Arguments[0].Expression : null);
                }
            }

            // ★ Deferred struct local field access: structLocal.fieldName
            //   Where structLocal was initialized from structArray[idx].
            //   Example: position.Value  (where position = positions[i])
            //   → field-level gather with struct stride
            if (memberAccess.Expression is IdentifierNameSyntax structLocalId
                && _structVaryingLocals.TryGetValue(structLocalId.Identifier.Text, out var structLocalInfo))
            {
                return TranslateStructFieldAccess(structLocalInfo.arrName, structLocalInfo.elemCppType,
                    memberName, structLocalInfo.indexExpr);
            }

            string objExpr = TranslateExpression(memberAccess.Expression);

            // Check if the object is a varying float2/int2
            string objName = memberAccess.Expression is IdentifierNameSyntax id ? id.Identifier.Text : null;
            bool isVaryingFloat2 = objName != null && _float2VaryingVars.Contains(objName);

            // ★ 标量位置的字段读取：接收者不是向量 → 绝不做 `.v` 包装。
            //   （`CpuUnitConfigData cfg = cfgPtr[cfgIdPtr[index]]; cfg.FramesDeath` 的接收者翻译出来是
            //    `simd_value<int>{ n_load_epi32(cfgIdPtr + si) }` 这种**标量** SIMD 包装，本身无 `.v`。）
            bool scalarReceiver = !objExpr.Contains(".v");

            // .MaxValue / .MinValue — 必须**早于** scalarReceiver 的标量回退分支：
            // 接收者是预定义类型（`float.MaxValue` / `int.MaxValue`）时，TranslateExpression(接收者)
            // 会去翻译一个裸 `float` 关键字节点 → 落兜底标记（`/*…PredefinedType*/ 0`），
            // 于是标量回退把它拼成 `/*标记*/ 0.MaxValue` —— 编译不过，且整个 AutoSIMD 单元被标记拦下。
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
                        var t = _semanticModel.GetTypeInfo(memberAccess.Expression).Type;
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

            // ★ 标量 SIMD 包装上取字段：退回标量 C++ 表达式。
            //   结构体 → `expr.field`；数学向量 → `expr.x()`（上面已处理）。
            //   旧代码在这里无条件补 `.v`，生成 `simd_value<int>{...}.FramesDeath` → clang 报
            //   「member reference base type 'int' is not a structure or union」（MarkDeadJob 实测）。
            if (scalarReceiver) return $"{objExpr}.{memberName}";

            // .MaxValue / .MinValue 已在上面（scalarReceiver 回退之前）处理。

            // .zero on int2/float2 → constructor
            if (memberName == "zero" && (objExpr.Contains("int2") || objExpr.Contains("float2")))
            {
                string prefix = objExpr.Contains("int2") ? "EntJoy::Mathematics::int2" : "EntJoy::Mathematics::float2";
                return $"{prefix}(0, 0)";
            }

            // .Length on NativeArray → _length suffix
            // ⚠ NativeArray 字段的 C++ 名是 `X_ptr`（NativeArrayBase），但长度形参是 `X_length`。
            //   直接用 objExpr 会生成 `X_ptr_length`（未声明）。
            if (memberName == "Length")
            {
                if (memberAccess.Expression is IdentifierNameSyntax lenId && IsNativeArrayField(lenId))
                    return $"{lenId.Identifier.Text}_length";
                return $"{objExpr}_length";
            }

            // .x or .y on float2 — use .x/.y member on simd_value<float2>
            if ((memberName == "x" || memberName == "y") && isVaryingFloat2)
            {
                return $"{objExpr}.{memberName}";
            }

            // ★ simd_value<float2> 整体（双通道 gather）→ .x/.y 直接取成员
            if ((memberName == "x" || memberName == "y") && objExpr.StartsWith("simd_value<EntJoy::Mathematics::float2>"))
            {
                return $"{objExpr}.{memberName}";
            }

            // ★ Struct field gather result: n_gather_ps already returns the component value.
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

            // ★ Check for hoisted uniform broadcast (pre-broadcast once, reuse in SIMD)
            if ((memberName == "x" || memberName == "y") && !isVaryingFloat2)
            {
                if (memberAccess.Expression is IdentifierNameSyntax hoistId)
                {
                    string key = $"{hoistId.Identifier.Text}.{memberName}";
                    if (_uniformHoistMap.TryGetValue(key, out var hoistVar))
                        return hoistVar;
                }
                // EntJoy Mathematics types use method syntax: .x() not .x
                // ⚠ 只有**确实是向量**的表达式才用 `.x/.y` 成员形式。旧判据 `Contains("::") || StartsWith("simd_")`
                //   过宽：`simd_value<int>{ n_load_epi32(cfgIdPtr + si) }` 这种**标量** SIMD 包装也命中，
                //   于是 `.FramesDeath` 被翻成 `simd_value<int>{...}.FramesDeath` → clang 报
                //   「member reference base type 'int' is not a structure or union」（MarkDeadJob 实测）。
                //   真正的向量表达式一定含 `.v`。
                if (objExpr.Contains(".v") || objExpr.StartsWith("simd_value<"))
                    return $"{objExpr}.{memberName}";
                return $"{objExpr}.{memberName}()";
            }
            return $"{objExpr}.{memberName}";
        }

        /// <summary>
        /// <paramref name="expr"/> 是否出现在**另一个元素访问的下标位置**（即 `A[B[i]]` 里的 `B[i]`）。
        /// 那种位置需要标量下标；其余位置需要 `.v`-able 的 SIMD 值。
        /// </summary>
        private static bool IsSubscriptOfAnotherElementAccess(ExpressionSyntax expr)
        {
            if (expr.Parent is not ElementAccessExpressionSyntax parentEa) return false;
            var args = parentEa.ArgumentList?.Arguments;
            if (args == null) return false;
            foreach (var a in args.Value)
                if (a.Expression == expr) return true;
            return false;
        }

        /// <summary>
        /// 翻译元素访问。
        /// <paramref name="asScalarIndex"/>：调用方需要**标量下标**（本表达式是另一个元素访问的下标），
        /// 此时返回裸标量 `baseExpr[sidx]`。
        /// 否则返回 `simd_value&lt;T&gt;{ ... }` 包装 —— 因为条件/赋值 RHS 位置会无条件取 `.v`，
        /// 返回裸标量会生成 `(alivePtr[i]).v` → clang 报
        /// 「member reference base type 'unsigned char' is not a structure or union」（MarkDeadJob 实测）。
        /// </summary>
        private string TranslateElementAccess(ElementAccessExpressionSyntax elementAccess, bool asScalarIndex)
        {
            // Resolve NativeArray type via _jobStruct symbol (avoid semantic model in source gen context)
            bool isNativeArray = false;
            string elemCppType = "float";
            string baseExpr = null;
            bool vectorizableElem = false;
            // ★ 统一解析：NativeArray 字段 → `X_ptr`；裸指针变量（局部/形参）→ `X`（A2）
            if (NativeArrayBaseInfo(elementAccess.Expression) is { } naInfo)
            {
                isNativeArray = true;
                elemCppType = naInfo.ElemType;
                baseExpr = naInfo.Base;
                // 向量 load/gather 只支持 float/int（n_load_ps / n_load_epi32 / gather）。
                // byte / uint 等窄或宽元素 → 退回标量下标读，避免把 byte* 当 float*/int* 用。
                vectorizableElem = elemCppType == "float" || elemCppType == "int"
                    || elemCppType.Contains("float2") || elemCppType.Contains("int2");
            }
            baseExpr ??= TranslateExpression(elementAccess.Expression);
            string indexExpr = "0";
            if (elementAccess.ArgumentList?.Arguments.Count > 0)
                indexExpr = TranslateExpression(elementAccess.ArgumentList.Arguments[0].Expression);

            // Detect if index is varying (SIMD gather needed)
            VarKind indexKind = VarKind.Uniform;
            if (elementAccess.ArgumentList?.Arguments.Count > 0)
            {
                var argExpr = elementAccess.ArgumentList.Arguments[0].Expression;
                indexKind = _varAnalyzer.ClassifyExpression(argExpr);
            }

            // Handle NativeList: use Ptr->data access
            if (!isNativeArray && _jobStruct != null && elementAccess.Expression is IdentifierNameSyntax id2)
            {
                var members2 = _jobStruct.GetMembers(id2.Identifier.Text);
                if (members2.Length > 0 && members2[0] is IFieldSymbol f2
                    && NativeTranspiler.IsEntJoyContainerNamed(f2.Type, Config.NativeList))
                {
                    isNativeArray = true;
                    var typeArg = ((INamedTypeSymbol)f2.Type).TypeArguments.FirstOrDefault();
                    if (typeArg != null)
                        elemCppType = NativeTranspiler.MapCSharpTypeToCpp(typeArg);
                    if (indexKind >= VarKind.Varying)
                    {
                        // ★ Safety clamp: mask ctx → clamp to [0, Length-1] for unmasked gather
                        string safeIdx = _currentMask != "simd_mask::all_true()"
                            ? $"simd_min(simd_max({indexExpr}, simd_value<int>(0)), simd_value<int>::broadcast({baseExpr}.Length - 1))"
                            : indexExpr;
                        if (elemCppType.Contains("float2"))
                            return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>::gathf(({elemCppType}*){baseExpr}.Ptr, {safeIdx}.v), simd_value<float>::gathfy(({elemCppType}*){baseExpr}.Ptr, {safeIdx}.v) }}";
                        if (elemCppType.Contains("int2"))
                            return $"simd_value<EntJoy::Mathematics::int2>::gather(({elemCppType}*){baseExpr}.Ptr, {safeIdx})";
                        return $"simd_value<float>::gathf(({elemCppType}*){baseExpr}.Ptr, {safeIdx}.v)";
                    }
                    return $"(({elemCppType}*){baseExpr}.Ptr)[{indexExpr}]";
                }
            }

            // ★ 向量 load/gather 只支持 float/int（n_load_ps / n_load_epi32 / gather）。
            //   byte / uint 等窄或宽元素、以及不可向量化的基址 → 退回标量下标读。

            if (isNativeArray && vectorizableElem && indexKind >= VarKind.Varying)
            {
                // ★ Check if index is from a uniform-bound reduction loop induction variable
                //   → emit broadcast of scalar load instead of gather.
                //   This is the key optimization for fallback loops like for(i=0; i<N; i++):
                //   one scalar load + broadcast to all 8 lanes.
                if (elementAccess.ArgumentList?.Arguments.Count > 0)
                {
                    var rawArg = elementAccess.ArgumentList.Arguments[0].Expression;
                    if (rawArg is IdentifierNameSyntax rawId && _uniformLoopVars.Contains(rawId.Identifier.Text))
                    {
                        string scalarIdx = rawId.Identifier.Text;
                        string naBase = NativeArrayBase(rawId, scalarIdx);
                        if (elemCppType.Contains("float2"))
                            return $"simd_value<{elemCppType}>::broadcast({naBase}[{scalarIdx}])";
                        if (elemCppType.Contains("int2"))
                            return $"simd_value<EntJoy::Mathematics::int2>::broadcast({naBase}[{scalarIdx}])";
                        if (elemCppType == "float")
                            return $"simd_value<float>::broadcast({naBase}[{scalarIdx}])";
                        if (elemCppType == "int")
                            return $"simd_value<int>::broadcast({naBase}[{scalarIdx}])";
                        return $"simd_value<float>::broadcast({naBase}[{scalarIdx}])";
                    }
                }

                // ★ Safety clamp for gather: when in mask context, clamp indices to
                //   [0, arr_length-1] to prevent AVX2 unmasked gather OOB.
                //   Skip if index variable was already clamped by a prior gather.
                string safeIdx;
                if (elementAccess.ArgumentList?.Arguments.Count > 0
                    && elementAccess.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax idxId
                    && (_clampedVars.Contains(idxId.Identifier.Text) || _clampedVars.Contains("v_" + idxId.Identifier.Text)))
                {
                    safeIdx = indexExpr; // already clamped by prior gather
                }
                else if (_inVaryingReductionLoop && _hoistedSafeMaxVar != null && baseExpr == _hoistedSafeMaxExpr)
                {
                    // Varying reduction loop: index pre-clamped to >=0, use hoisted broadcast
                    safeIdx = $"simd_min({indexExpr}, {_hoistedSafeMaxVar})";
                }
                else if (_currentMask != "simd_mask::all_true()")
                {
                    // ⚠ 长度形参名是 `<字段名>_length`，而 baseExpr 是 `<字段名>_ptr` ——
                    //   直接用 baseExpr 拼会生成未声明的 `A_ptr_length`（实测：EdgeCase4_SIMD_For 编译失败）。
                    var lenArrId2 = elementAccess.Expression as IdentifierNameSyntax;
                    string lengthName = lenArrId2 != null
                        ? lenArrId2.Identifier.Text + "_length"
                        : baseExpr + "_length";
                    safeIdx = $"simd_min(simd_max({indexExpr}, simd_value<int>(0)), simd_value<int>::broadcast({lengthName} - 1))";
                }
                else
                {
                    safeIdx = indexExpr;
                }

                // Contiguous index optimization: when index is _simdIndexVar (v_i/v_j) in a batch loop,
                // or uniform_part + _simdIndexVar (like i*100 + v_j), use contiguous load instead of gather.
                if (!string.IsNullOrEmpty(_batchLoopVar))
                {
                    string contBase = null;
                    if (indexExpr == _simdIndexVar)
                        contBase = _batchLoopVar;  // simple: ptr + si
                    else
                    {
                        // Detect: uniform_expr + _simdIndexVar (like "i*100 + v_j")
                        string suffix = $"+ {_simdIndexVar}";
                        if (indexExpr.EndsWith(suffix))
                            contBase = indexExpr.Substring(0, indexExpr.Length - suffix.Length).Trim();
                        else if (indexExpr.EndsWith($"+ {_simdIndexVar})"))
                            contBase = indexExpr.Substring(0, indexExpr.Length - ($"+ {_simdIndexVar})").Length).Trim().TrimStart('(');
                    }
                    if (contBase != null)
                    {
                        string baseOff = contBase == _batchLoopVar ? contBase : $"({contBase}) + {_batchLoopVar}";
                        if (elemCppType == "float")
                            return $"simd_value<float>{{ n_load_ps({baseExpr} + {baseOff}) }}";
                        if (elemCppType == "int")
                            return $"simd_value<int>{{ n_load_epi32({baseExpr} + {baseOff}) }}";
                    }
                }

                // SIMD gather
                if (elemCppType.Contains("float2"))
                {
                    if (safeIdx != indexExpr)
                    {
                        string tidx = $"__ci_{_labelCounter++}";
                        AppendLine($"simd_value<int> {tidx} = {safeIdx};");
                        return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>::gathf({baseExpr}, {tidx}.v), simd_value<float>::gathfy({baseExpr}, {tidx}.v) }}";
                    }
                    return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>::gathf({baseExpr}, {safeIdx}.v), simd_value<float>::gathfy({baseExpr}, {safeIdx}.v) }}";
                }
                if (elemCppType.Contains("int2"))
                    return $"simd_value<EntJoy::Mathematics::int2>::gather({baseExpr}, {safeIdx})";
                if (elemCppType == "float")
                    return $"simd_value<float>::gathf({baseExpr}, {safeIdx}.v)";
                if (elemCppType == "int")
                    return $"simd_value<int>::gather({baseExpr}, {safeIdx})";
                return $"simd_value<float>::gathf({baseExpr}, {safeIdx}.v)";
            }

            // ★ A1 通解：varying 下标 + 不可向量化元素（byte/sbyte/uint/float2/结构体）的**向量读**。
            //   旧实现退化成 lane0 标量读（`n_extract_lane_epi32((v_i).v, 0)`）——那是"每 lane 读同一个
            //   地址"的静默错解，所以整段 job 还被 `HasNonVectorizableCall` 兜底成 per-lane 标量循环。
            //   这里按元素宽度走"逐 lane 取值 + 掩码 blend"：只在语义上需要 gather 时逐 lane 读，
            //   不做跨元素错位 load（byte 元素相邻下标在内存里只差 1 字节，8 宽 load 会错位）。
            if (isNativeArray && !vectorizableElem && indexKind >= VarKind.Varying)
            {
                string idxVar = $"__gi_{_labelCounter++}";
                AppendLine($"simd_value<int> {idxVar} = {indexExpr};");
                // 结果类型：窄/整型 → simd_value<int>（byte/uint 按元素的 C++ 转换规则扩展）；
                // 宽元素（float2/int2/自定义结构）没有对应的 simd 元素类型 → 保持标量 lane0，
                // 由写入侧（EmitElementStore）与兜底判定共同保证不出错。
                if (elemCppType == "float2" || elemCppType == "int2"
                    || (!IsBuiltinScalarElem(elemCppType) && elemCppType.Contains("::")))
                    return $"{baseExpr}[n_extract_lane_epi32({idxVar}.v, 0)]";

                string elemPtr = $"(({elemCppType}*)({baseExpr}))";
                string resVar = $"__gv_{_labelCounter++}";
                string seqVar = $"__gl_seq_{_labelCounter++}";
                bool masked = _currentMask != "simd_mask::all_true()";
                AppendLine($"simd_value<int> {seqVar} = simd_value<int>::sequence(0, g_simdWidthInt);");
                AppendLine($"simd_value<int> {resVar} = simd_value<int>::broadcast(0);");
                AppendLine($"for (int __gl = 0; __gl < g_simdWidthInt; __gl++)");
                AppendLine("{");
                if (masked)
                    AppendLine($"    if ((n_mask_to_bitmask(({_currentMask}).m) & (1 << __gl)) == 0) continue;");
                AppendLine($"    int __gx = n_extract_lane_epi32({idxVar}.v, __gl);");
                AppendLine($"    simd_value<int> __gb = simd_value<int>::broadcast((int){elemPtr}[__gx]);");
                AppendLine($"    simd_mask __gm{{ n_cmp_eq_epi32(({seqVar}).v, n_set1_epi32(__gl)) }};");
                AppendLine($"    {resVar} = blend({resVar}, __gb, __gm);");
                AppendLine("}");
                return resVar;
            }

            // Scalar access（varying 下标 + 不可向量化元素）
            // ⚠ 这里返回**裸标量** `baseExpr[lane0]`（无 `.v`）。
            //   尝试过在"非下标位置"包一层 `simd_value<int>{...}` 让它 `.v`-able，实测**失败且更糟**：
            //   - 结构体元素：`simd_value<int>{ (int)(cfgPtr[i]) }` → cannot convert 'CpuUnitConfigData' to 'int'
            //   - 结构体字段：`simd_value<int>{ (int)(cfgPtr[i]) }.FramesDeath` → 字段取在 int 包装上
            //   - 左值位置：`simd_value<int>{ (int)(velPtr[i]) } = float2(...)` → 临时量不可赋值
            //   ⇒ **包装点选错了**：不该在"元素访问出口"包，而应在**消费点**
            //     （条件构造 / 赋值 RHS 取 `.v` 的那几处）按需包装/取 lane0。
            //     详见 docs §13.21 的设计修正。
            if (indexKind >= VarKind.Varying)
            {
                indexExpr = indexExpr.Contains(".v")
                    ? $"n_extract_lane_epi32({indexExpr}, 0)"
                    : $"n_extract_lane_epi32(({indexExpr}).v, 0)";
            }
            return $"{baseExpr}[{indexExpr}]";
        }

        /// <summary>元素的 C++ 类型是否为内置标量（可安全按 (int) 扩展成 simd_value&lt;int&gt;）。</summary>
        private static bool IsBuiltinScalarElem(string cppType)
            => cppType == "float" || cppType == "int" || cppType == "unsigned char"
            || cppType == "signed char" || cppType == "unsigned int" || cppType == "short"
            || cppType == "unsigned short" || cppType == "bool";

        private string TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            IMethodSymbol? symbol = null;
            try { symbol = _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol; } catch { }
            if (symbol == null)
            {
                // Fallback: try name-based matching for common math functions
                // (GetSymbolInfo can fail on SyntaxFactory-created AST nodes)
                var ident = invocation.Expression as MemberAccessExpressionSyntax;
                string? fnName = ident?.Name.Identifier.Text;
                if (fnName != null)
                {
                    // Try TranslateMathFFunction first (has Sin/Cos/Sqrt/SLEEF cases for PascalCase names)
                    string fc1 = TranslateMathFFunction(fnName, invocation);
                    if (!fc1.Contains("/* unknown") && !fc1.Contains("EntJoy::Mathematics"))
                        return fc1;
                    // Fallback: TranslateMathFunction for EntJoy mathematics functions
                    string fc2 = TranslateMathFunction(fnName, invocation);
                    if (!fc2.Contains("/* unknown */"))
                        return fc2;
                }
                return "/* unknown function */ 0";
            }

            string containingType = symbol.ContainingType?.ToDisplayString() ?? "";
            string methodName = symbol.Name;

            // EntJoy.Mathematics.math functions
            if (containingType == "EntJoy.Mathematics.math")
            {
                return TranslateMathFunction(methodName, invocation);
            }

            // System.MathF / System.Math
            if (containingType == "System.MathF" || containingType == "System.Math")
            {
                return TranslateMathFFunction(methodName, invocation);
            }

            // NativeArray.GetUnsafePtr
            if (NativeTranspiler.IsEntJoyContainerNamed(symbol.ContainingType, Config.NativeArray) && methodName == Config.GetUnsafePtr)
            {
                if (invocation.Expression is MemberAccessExpressionSyntax ma
                    && ma.Expression is IdentifierNameSyntax id)
                {
                    return NativeArrayBase(id, id.Identifier.Text);
                }
            }

            // UnsafeUtility.ArrayElementAsRef<T>(ptr, i) — 求值上下文（写上下文在 TranslateAssignment）
            if (InvocationName(invocation) == Config.ArrayElementAsRef && TryGetArrayElementAsRef(invocation) is { } asRefRead)
                return TranslateArrayElementAsRefValue(asRefRead.ElemType, asRefRead.PtrExpr, asRefRead.IdxExpr);

            // Fallback: emit as regular function call
            string funcCall = "";
            if (invocation.Expression is MemberAccessExpressionSyntax member)
            {
                funcCall = $"{TranslateExpression(member.Expression)}.{methodName}(";
            }
            else
            {
                funcCall = $"{methodName}(";
            }

            for (int i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
            {
                if (i > 0) funcCall += ", ";
                funcCall += TranslateExpression(invocation.ArgumentList.Arguments[i].Expression);
            }
            funcCall += ")";
            return funcCall;
        }

        private string TranslateMathFunction(string methodName, InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;

            switch (methodName)
            {
                case "min":
                case "max":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        VarKind k1 = _varAnalyzer.ClassifyExpression(args[1].Expression);
                        if (k0 >= VarKind.Varying || k1 >= VarKind.Varying)
                        {
                            string func = methodName == "min" ? "min" : "max";
                            // Ensure both are SIMD by broadcasting uniform ones
                            if (k0 < VarKind.Varying && k1 >= VarKind.Varying)
                                a = $"{b}.broadcast({a})";
                            else if (k0 >= VarKind.Varying && k1 < VarKind.Varying)
                                b = $"{a}.broadcast({b})";
                            return $"{func}({a}, {b})";
                        }
                        return $"{methodName}({a}, {b})";
                    }
                    break;
                }

                case "clamp":
                {
                    if (args.Count >= 3)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        string lo = TranslateExpression(args[1].Expression);
                        string hi = TranslateExpression(args[2].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                        {
                            // For float2/int2: decompose to component-wise clamp
                            if (args[0].Expression is IdentifierNameSyntax clampId
                                && _float2VaryingVars.Contains(clampId.Identifier.Text)
                                && _variables.TryGetValue(clampId.Identifier.Text, out var clampInfo))
                            {
                                string simdType = GetSIMDTypeString(clampInfo.CppType);
                                // simdType is "simd_value<EntJoy::Mathematics::int2>" — use directly, NOT wrapping in simd_value<>
                                return $"{simdType}(max(min({v}.x, {hi}.x()), {lo}.x()), max(min({v}.y, {hi}.y()), {lo}.y()))";
                            }
                            // Default: use friend functions max/min (works for all SIMD types)
                            return $"max(min({v}, {hi}), {lo})";
                        }
                        return $"EntJoy::Mathematics::clamp({v}, {lo}, {hi})";
                    }
                    break;
                }

                case "abs":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_max({v}, -{v})"; // simple SIMD abs
                        return $"EntJoy::Mathematics::abs({v})";
                    }
                    break;
                }

                case "floor":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"{v}.floor()";
                        return $"EntJoy::Mathematics::floor({v})";
                    }
                    break;
                }

                case "MaxValue":
                    return "std::numeric_limits<float>::max()";
                case "MinValue":
                    return "std::numeric_limits<float>::lowest()";

                case "distancesq":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        VarKind k1 = _varAnalyzer.ClassifyExpression(args[1].Expression);
                        if (k0 >= VarKind.Varying || k1 >= VarKind.Varying)
                        {
                            // Expand as SIMD using .x/.y member access on whole-type simd_value<float2>
                            // Keep inline: explicit temps increase register pressure, MSVC CSE is sufficient
                            string ax = $"{a}.x", ay = $"{a}.y";
                            string bx = $"{b}.x", by = $"{b}.y";

                            return $"({ax} - {bx}) * ({ax} - {bx}) + ({ay} - {by}) * ({ay} - {by})";
                        }
                        return $"EntJoy::Mathematics::distancesq({a}, {b})";
                    }
                    break;
                }

                case "dot":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (k0 >= VarKind.Varying)
                        {
                            // Use member access on whole-type: v_a.x * v_b.x + v_a.y * v_b.y
                            string ax = $"{a}.x", ay = $"{a}.y";
                            string bx = $"{b}.x", by = $"{b}.y";
                            return $"{ax} * {bx} + {ay} * {by}";
                        }
                        return $"EntJoy::Mathematics::dot({a}, {b})";
                    }
                    break;
                }
            }

            // Default: emit as regular function call
            string call = $"EntJoy::Mathematics::{methodName}(";
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) call += ", ";
                call += TranslateExpression(args[i].Expression);
            }
            call += ")";
            return call;
        }

        private string TranslateMathFFunction(string methodName, InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;

            switch (methodName)
            {
                case "Min":
                case "Max":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (k0 >= VarKind.Varying)
                        {
                            string func = methodName == "Min" ? "min" : "max";
                            return $"{func}({a}, {b})";
                        }
                        return $"std::{methodName.ToLower()}({a}, {b})";
                    }
                    break;
                }

                case "Sqrt":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                        {
                            // SIMD sqrt via native instruction
                            return $"simd_value<float>{{ n_sqrt_ps({v}.v) }}";
                        }
                        return $"std::sqrt({v})";
                    }
                    break;
                }

                case "Abs":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_max({v}, -{v})";
                        return $"std::abs({v})";
                    }
                    break;
                }

                case "Clamp":
                case "clamp":
                {
                    // Math.Clamp / math.clamp → SIMD: clamp(v, lo, hi) = min(max(v, lo), hi)
                    if (args.Count >= 3)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        string lo = TranslateExpression(args[1].Expression);
                        string hi = TranslateExpression(args[2].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"min(max({v}, {lo}), {hi})";
                        return $"std::min(std::max({v}, {lo}), {hi})";
                    }
                    break;
                }

                // Lightweight native SIMD (no SLEEF needed)
                case "Ceiling":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_ceil_ps({v}.v) }}";
                        return $"std::ceil({v})";
                    }
                    break;
                }
                case "Round":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_round_ps({v}.v) }}";
                        return $"std::round({v})";
                    }
                    break;
                }
                case "Truncate":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_trunc_ps({v}.v) }}";
                        return $"std::trunc({v})";
                    }
                    break;
                }

                // SLEEF transcendental functions (single-argument)
                case "Sin":  case "Cos":  case "Tan":
                case "Asin": case "Acos": case "Atan":
                case "Sinh": case "Cosh": case "Tanh":
                case "Exp":  case "Log":  case "Log10":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                        {
                            string sleefFn = $"n_{methodName.ToLowerInvariant()}_ps";
                            return $"simd_value<float>{{ {sleefFn}({v}.v) }}";
                        }
                        return $"std::{methodName.ToLowerInvariant()}({v})";
                    }
                    break;
                }

                // SLEEF two-argument functions
                case "Atan2":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_atan2_ps({a}.v, {b}.v) }}";
                        return $"std::atan2({a}, {b})";
                    }
                    break;
                }
                case "Pow":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_pow_ps({a}.v, {b}.v) }}";
                        return $"std::pow({a}, {b})";
                    }
                    break;
                }
            }

            // Fallback: lowercase mapping (MathF.Sin → sin for SIMD ADL, std::sin for scalar)
            bool anyVarying = false;
            for (int i = 0; i < args.Count; i++)
                if (_varAnalyzer.ClassifyExpression(args[i].Expression) >= VarKind.Varying)
                    { anyVarying = true; break; }
            string funcPrefix = anyVarying ? "" : "std::";
            string call = $"{funcPrefix}{methodName.ToLowerInvariant()}(";
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) call += ", ";
                call += TranslateExpression(args[i].Expression);
            }
            call += ")";
            return call;
        }

        private string TranslateBinary(BinaryExpressionSyntax binary)
        {
            string left = TranslateExpression(binary.Left);
            string right = TranslateExpression(binary.Right);
            VarKind leftKind = _varAnalyzer.ClassifyExpression(binary.Left);
            VarKind rightKind = _varAnalyzer.ClassifyExpression(binary.Right);
            bool anyVarying = leftKind >= VarKind.Varying || rightKind >= VarKind.Varying;

            string op = binary.OperatorToken.Text;

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

                bool cmpIsInt = false;
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
                // ★ fallback to SemanticModel for int type detection.
                //   The above pattern matching only catches direct variable refs and bitwise ops,
                //   but misses computed int expressions like `dx * dy`, `i % 3`, `i & 1`.
                //   Use Roslyn GetTypeInfo to get the actual result type of each comparison operand.
                if (!cmpIsInt && !cmpIsUint)
                {
                    try
                    {
                        var leftType = _semanticModel.GetTypeInfo(binary.Left).Type;
                        var rightType = _semanticModel.GetTypeInfo(binary.Right).Type;
                        bool leftIsInt = leftType != null && (leftType.SpecialType == SpecialType.System_Int32 || leftType.SpecialType == SpecialType.System_UInt32);
                        bool rightIsInt = rightType != null && (rightType.SpecialType == SpecialType.System_Int32 || rightType.SpecialType == SpecialType.System_UInt32);
                        if (leftIsInt || rightIsInt)
                            cmpIsInt = true;
                        // Check for uint semantics on either side
                        if (leftType != null && leftType.SpecialType == SpecialType.System_UInt32) cmpIsUint = true;
                        if (rightType != null && rightType.SpecialType == SpecialType.System_UInt32) cmpIsUint = true;
                    }
                    catch { /* SemanticModel may fail on synthetic AST nodes */ }
                }
                // Any explicit uint operand (u-literal or (uint) cast) makes the compare unsigned —
// the variable may be recorded as int but its value is uint semantics.
bool useUnsignedCmp = cmpIsUint;
string bc = useUnsignedCmp ? "n_set1_epi32" : (cmpIsInt ? "n_set1_epi32" : "n_set1_ps");
                // ★ Hoisted broadcasts (__uni_ prefixed) are already SIMD — use .v, don't re-broadcast
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

            // Logical operators
            if (binary.IsKind(SyntaxKind.LogicalAndExpression))
            {
                if (anyVarying)
                {
                    // ★ Compile-time constant folding: false && expr → false, true && expr → expr
                    if (left == "false" || left == "0") return "simd_mask{ n_cmp_ne_epi32(n_set1_epi32(0), n_set1_epi32(0)) }";
                    if (left == "true") return right;
                    if (right == "false" || right == "0") return "simd_mask{ n_cmp_ne_epi32(n_set1_epi32(0), n_set1_epi32(0)) }";
                    if (right == "true") return left;
                    // Wrap scalar bools in simd_mask before accessing .m
                    if (!left.Contains("simd_mask") && !left.Contains("n_cmp_"))
                        left = $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({left} ? -1 : 0).v, n_set1_epi32(0)) }}";
                    if (!right.Contains("simd_mask") && !right.Contains("n_cmp_"))
                        right = $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({right} ? -1 : 0).v, n_set1_epi32(0)) }}";
                    return $"simd_mask{{ n_and_mask({left}.m, {right}.m) }}";
                }
                return $"({left} && {right})";
            }
            if (binary.IsKind(SyntaxKind.LogicalOrExpression))
            {
                if (anyVarying)
                {
                    if (left == "true") return "simd_mask::all_true()";
                    if (left == "false") return right;
                    if (right == "true") return "simd_mask::all_true()";
                    if (right == "false") return left;
                    if (!left.Contains("simd_mask") && !left.Contains("n_cmp_"))
                        left = $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({left} ? -1 : 0).v, n_set1_epi32(0)) }}";
                    if (!right.Contains("simd_mask") && !right.Contains("n_cmp_"))
                        right = $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({right} ? -1 : 0).v, n_set1_epi32(0)) }}";
                    return $"simd_mask{{ n_or_mask({left}.m, {right}.m) }}";
                }
                return $"({left} || {right})";
            }

            // Arithmetic operators
            if (!anyVarying)
            {
                return $"({left} {op} {right})";
            }

            // ★ FMA detection: float a*b + c → n_fmadd_ps(a, b, c)
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

            // ★ uint right shift: C# `uint >> n` is logical (zero-extended), but C++
            //   `int >> n` is arithmetic (sign-extended). Detect uint left operand and
            //   generate n_srli_epi32 (logical shift) instead of `>>` (arithmetic shift).
            //   Without this, large uint values (> INT_MAX) produce wrong results.
            //   Note: SemanticModel returns Int32 for uint locals in source generator context,
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
                {
                    try
                    {
                        var leftTypeInfo = _semanticModel.GetTypeInfo(binary.Left);
                        if (leftTypeInfo.Type != null && leftTypeInfo.Type.SpecialType == SpecialType.System_UInt32)
                            leftIsUint = true;
                    }
                    catch { }
                }

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
            VarKind kind;
            try { kind = _varAnalyzer.ClassifyExpression(expr); } catch { kind = VarKind.Varying; }
            if (kind >= VarKind.Varying)
                return isInt ? $"n_cvtepi32_ps(({translated}).v)" : $"({translated}).v";
            return isInt ? $"n_cvtepi32_ps(n_set1_epi32({translated}))" : $"n_set1_ps({translated})";
        }

        private string TranslateCast(CastExpressionSyntax cast)
        {
            string inner = TranslateExpression(cast.Expression);
            VarKind innerKind = _varAnalyzer.ClassifyExpression(cast.Expression);
            string targetTypeStr = cast.Type.ToString();

            // ★ Hoisted broadcasts are already the correct SIMD type — skip cast entirely
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
            try
            {
                var targetType = _semanticModel.GetTypeInfo(cast.Type).Type;
                if (targetType != null)
                    return $"({NativeTranspiler.MapCSharpTypeToCpp(targetType)}){inner}";
            }
            catch { }

            return $"({targetTypeStr.Replace(".", "::")}){inner}";
        }

        /// <summary>
        /// <c>arr[i] = v</c> 的写入分派：统一解析「NativeArray 字段 → <c>X_ptr</c>」与「裸指针变量 → <c>X</c>」，
        /// 元素类型分别取自容器泛型实参 / 指针目标类型。返回 null 表示不是可识别的元素写入。
        /// </summary>
        private string? TryEmitElementWrite(AssignmentExpressionSyntax assign, ElementAccessExpressionSyntax elemAccess, IdentifierNameSyntax id)
        {
            string name = id.Identifier.Text;
            if (IsNativeArrayField(id))
            {
                if (_nativeArrayParams.TryGetValue(name, out var pet))
                    return EmitElementStore(assign, name + "_ptr", pet, name + "_ptr", elemAccess.ArgumentList?.Arguments.Count > 0 ? elemAccess.ArgumentList.Arguments[0].Expression : null);
                if (_jobStruct != null)
                {
                    var members = _jobStruct.GetMembers(name);
                    if (members.Length > 0 && members[0] is IFieldSymbol f && f.Type is INamedTypeSymbol nt && nt.TypeArguments.Length > 0)
                        return EmitElementStore(assign, name + "_ptr", NativeTranspiler.MapCSharpTypeToCpp(nt.TypeArguments[0]), name + "_ptr", elemAccess.ArgumentList?.Arguments.Count > 0 ? elemAccess.ArgumentList.Arguments[0].Expression : null);
                }
                return null;
            }
            // 裸指针变量（局部或形参）：本身已是指针，元素类型来自指针目标类型
            if (PointerElemType(id) is { } elemType)
                return EmitElementStore(assign, name, elemType, name, elemAccess.ArgumentList?.Arguments.Count > 0 ? elemAccess.ArgumentList.Arguments[0].Expression : null);
            return null;
        }

        /// <summary>元素读/写的基址与元素类型（约定：数组字段 → <c>X_ptr</c>，裸指针变量 → <c>X</c>）。</summary>
        private (string Base, string ElemType, bool IsField)? NativeArrayBaseInfo(ExpressionSyntax collection)
        {
            if (collection is not IdentifierNameSyntax id) return null;
            string name = id.Identifier.Text;
            if (IsNativeArrayField(id))
            {
                if (_nativeArrayParams.TryGetValue(name, out var pet)) return (name + "_ptr", pet, true);
                if (_jobStruct != null)
                {
                    var members = _jobStruct.GetMembers(name);
                    if (members.Length > 0 && members[0] is IFieldSymbol f && f.Type is INamedTypeSymbol nt && nt.TypeArguments.Length > 0)
                        return (name + "_ptr", NativeTranspiler.MapCSharpTypeToCpp(nt.TypeArguments[0]), true);
                }
            }
            if (PointerElemType(id) is { } pe) return (name, pe, false);
            return null;
        }

        /// <summary>
        /// 自增/自减 → 增广赋值（<c>x++</c> / <c>++x</c> ⇒ <c>x += 1</c>）。
        /// 复用 TranslateAssignment 的掩码 + 形状分派（varying 逐 lane scatter / 连续向量 store）。
        /// ⚠ 只保证**语句用法**的语义（旧值被丢弃）。C# 的 <c>x++</c> 作为表达式时求值为旧值，
        ///   本实现返回新值 —— 该形态在 job 代码中不存在；一旦出现会走下面的兜底标记，不会静默算错。
        /// </summary>
        private string TranslateIncrementExpression(ExpressionSyntax operand, string op)
        {
            string marker = $"/*{UnsupportedMarkers.Expr}IncrementOnNonLvalue({operand.Kind()})*/ 0";
            if (operand is ElementAccessExpressionSyntax ea
                && ea.Expression is IdentifierNameSyntax aid
                && ea.ArgumentList?.Arguments.Count > 0
                && NativeArrayBaseInfo(aid) is { } info)
            {
                string idxExpr = TranslateExpression(ea.ArgumentList.Arguments[0].Expression);
                VarKind idxKind = _varAnalyzer.ClassifyExpression(ea.ArgumentList.Arguments[0].Expression);
                // 数组字段 `X_ptr` 已是目标元素类型的指针，直接用；
                // 裸指针变量本身是指针，同样直接用（**不要**转型 —— `(T*)p[i]` 是非法 lvalue 转型）。
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
            if (operand is IdentifierNameSyntax)
            {
                string target = TranslateExpression(operand);
                if (target.Contains(UnsupportedMarkers.Prefix)) return marker;
                return $"{target} {op} 1;";
            }
            return marker;
        }

        /// <summary>取被调用方法名（`X.M(...)` 或 `M(...)`）。</summary>
        private static string InvocationName(ExpressionSyntax expr) => expr switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => ""
        };

        /// <summary>
        /// 识别 <c>UnsafeUtility.ArrayElementAsRef&lt;T&gt;(ptr, idx)</c> 调用，返回元素 C++ 类型与两个实参。
        /// C++ 后端 (<c>CppPointerStatementTranslator</c>) 与 ISPC 后端都支持它；SIMD 路径此前完全缺失（A4）。
        /// </summary>
        private (string ElemType, ExpressionSyntax PtrExpr, ExpressionSyntax IdxExpr)? TryGetArrayElementAsRef(ExpressionSyntax? expr)
        {
            if (expr is not InvocationExpressionSyntax inv) return null;
            if (inv.ArgumentList?.Arguments.Count < 2) return null;
            if (inv.Expression is not MemberAccessExpressionSyntax ma) return null;
            if (ma.Name.Identifier.Text != Config.ArrayElementAsRef) return null;
            // 符号可用时校验容器类型；符号不可用（源生成器里常见）时按名字放行——
            // 该名字在项目里唯一，且实参形态（ptr 表达式 + 下标）已足够判别。
            try
            {
                if (_semanticModel.GetSymbolInfo(inv).Symbol is IMethodSymbol sym
                    && sym.ContainingType?.ToDisplayString() != "EntJoy.Collections.UnsafeUtility")
                    return null;
            }
            catch { }
            string elemCpp = "int";
            if (ma.Name is GenericNameSyntax gname && gname.TypeArgumentList.Arguments.Count > 0)
            {
                var t = _semanticModel.GetTypeInfo(gname.TypeArgumentList.Arguments[0]).Type;
                if (t != null) elemCpp = NativeTranspiler.MapCSharpTypeToCpp(t);
            }
            return (elemCpp, inv.ArgumentList.Arguments[0].Expression, inv.ArgumentList.Arguments[1].Expression);
        }

        /// <summary>生成 <c>((T*)ptr)[idx]</c>（裸指针元素访问，与 C++/ISPC 后端同形）。</summary>
        private string BuildArrayElementAsRefLValue(string elemCppType, ExpressionSyntax ptrExpr, ExpressionSyntax idxExpr)
        {
            return $"(({elemCppType}*){TranslateExpression(ptrExpr)})[{TranslateExpression(idxExpr)}]";
        }

        /// <summary>指针符号的目标类型（C++ 名）；不是指针则返回 null。以名字解析为主（SemanticModel 在源生成器里不可靠）。</summary>
        private string? PointerElemType(IdentifierNameSyntax id)
        {
            string name = id.Identifier.Text;
            if (_nativeArrayParams.TryGetValue(name, out var pet)) return pet;
            if (_localPointerElemCpp.TryGetValue(name, out var le)) return le;
            if (_paramPointerElemCpp.TryGetValue(name, out var pe)) return pe;
            return null;
        }

        /// <summary>求值上下文的 <c>ArrayElementAsRef&lt;T&gt;(ptr, i)</c> → <c>((T*)ptr)[i]</c>。</summary>
        private string TranslateArrayElementAsRefValue(string declaredElemType, ExpressionSyntax ptrExpr, ExpressionSyntax idxExpr)
        {
            if (ptrExpr is InvocationExpressionSyntax inv
                && inv.Expression is MemberAccessExpressionSyntax pma
                && pma.Expression is IdentifierNameSyntax pid)
            {
                string b = IsNativeArrayField(pid) ? pid.Identifier.Text + "_ptr" : pid.Identifier.Text;
                return $"(({declaredElemType}*){b})[{TranslateExpression(idxExpr)}]";
            }
            if (ptrExpr is IdentifierNameSyntax bid)
                return $"(({declaredElemType}*){bid.Identifier.Text})[{TranslateExpression(idxExpr)}]";
            return BuildArrayElementAsRefLValue(declaredElemType, ptrExpr, idxExpr);
        }

        /// <summary>
        /// <c>UnsafeUtility.ArrayElementAsRef&lt;T&gt;(ptr, i) = v</c> 的写入。
        /// 与 <c>arr[i] = v</c> 同义 → 解析出基址/元素类型后复用 EmitElementStore，掩码语义一致。
        /// </summary>
        private string TranslateArrayElementAsRefStore(
            string declaredElemType, ExpressionSyntax ptrExpr, ExpressionSyntax idxExpr, AssignmentExpressionSyntax assign)
        {
            string basePtr = null;
            string elemType = declaredElemType;
            if (ptrExpr is IdentifierNameSyntax bid)
            {
                if (PointerElemType(bid) != null || IsNativeArrayField(bid)) basePtr = bid.Identifier.Text;
            }
            else if (ptrExpr is InvocationExpressionSyntax inv
                && inv.Expression is MemberAccessExpressionSyntax pma
                && pma.Expression is IdentifierNameSyntax pid)
            {
                basePtr = IsNativeArrayField(pid) ? pid.Identifier.Text + "_ptr" : pid.Identifier.Text;
                elemType = PointerElemType(pid) ?? declaredElemType;
            }

            if (basePtr != null)
                return EmitElementStore(assign, basePtr, elemType, basePtr, idxExpr);

            // 非可解析基址（自定义指针表达式）→ 标量写法，保正确性
            string op = assign.OperatorToken.Text;
            string lv = BuildArrayElementAsRefLValue(declaredElemType, ptrExpr, idxExpr);
            string rv = TranslateExpression(assign.Right);
            return op == "=" ? $"{lv} = {rv}" : $"{lv} {op} {rv}";
        }

        /// <summary>
        /// NativeArray / 裸指针元素写入的统一分派（掩码感知）。
        /// <paramref name="isSimdBase"/> = 基址表达式是否可用于向量 intrinsic（字段 → <c>X_ptr</c>）；
        /// 局部指针变量本身已是裸指针，仍按指针语义分派（<c>hpPtr[i]</c> 而非 <c>hpPtr_ptr[i]</c>）。
        /// </summary>
        private string EmitElementStore(
            AssignmentExpressionSyntax assign, string baseName, string elemType, string basePtr,
            ExpressionSyntax? indexSyntax)
        {
            string idxExpr = TranslateExpression(indexSyntax ?? assign.Left);
            string rhsExpr = TranslateExpression(assign.Right);
            VarKind idxKind = _varAnalyzer.ClassifyExpression(indexSyntax ?? assign.Left);
            VarKind rhsKind = _varAnalyzer.ClassifyExpression(assign.Right);
            string extractFn = elemType == "float" ? "n_extract_lane_f32" : "n_extract_lane_epi32";
            string storeFnScalar = elemType == "float" ? "n_store_ps" : "n_store_epi32";
            string setFnScalar = elemType == "float" ? "n_set1_ps" : "n_set1_epi32";

            // ★ 非 float/int 元素（byte/sbyte/float2/结构体…）：向量 store 的 intrinsic 签名只覆盖
            //   float*/int*（A1）。这里统一走"逐 lane 标量写"，按元素类型正确转换指针，
            //   掩码语义与其他路径一致（不再生成 `(int*)(byte_ptr)[i] = <float2>` 这类非法赋值）。
            bool isNarrowElem = elemType != "float" && elemType != "int";

            if (isNarrowElem)
            {
                bool narrowed = _currentMask != "simd_mask::all_true()";
                string guard = narrowed ? $"if((n_mask_to_bitmask(({_currentMask}).m)&(1<<__l))!=0)" : "";
                string ep = $"(({elemType}*)({basePtr}))";
                // 结构体元素（float2/int2/自定义）用构造语法；内置标量用 static_cast。
                // ⚠ `(T){v}` 是 C 复合字面量语法，在 C++ 里会被读成"转型到指针类型"而报错。
                // ⚠ 多词类型名（`unsigned char`）不能用 `unsigned char(v)` —— C++ 把它读成
                //    `unsigned` 后跟 `char(v)`，报 "expected '(' for function-style cast"。
                bool structElem = IsStructNativeArrayType(elemType);
                if (idxKind >= VarKind.Varying)
                {
                    string rhsOperand = rhsKind >= VarKind.Varying
                        ? (structElem ? $"{elemType}({extractFn}(({rhsExpr}).v,__l))" : $"static_cast<{elemType}>({extractFn}(({rhsExpr}).v,__l))")
                        : (structElem ? $"{elemType}({rhsExpr})" : $"static_cast<{elemType}>({rhsExpr})");
                    return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{guard}{ep}[n_extract_lane_epi32(({idxExpr}).v,__l)] = {rhsOperand};}}}}";
                }
                string uniformRhs = rhsKind >= VarKind.Varying
                    ? (structElem ? $"{elemType}({extractFn}(({rhsExpr}).v,0))" : $"static_cast<{elemType}>({extractFn}(({rhsExpr}).v,0))")
                    : (structElem ? $"{elemType}({rhsExpr})" : $"static_cast<{elemType}>({rhsExpr})");
                return $"{ep}[{idxExpr}] = {uniformRhs}";
            }

            if (idxKind >= VarKind.Varying)
                {
                    // ★ Conditional (if/else) store: when the current mask is narrowed,
                    //   ANY store (contiguous or not, uniform or varying rhs) must be
                    //   masked per-lane — otherwise branch bodies write unconditionally
                    //   and later branches overwrite earlier ones.
                    bool inNarrowedContext = _currentMask != "simd_mask::all_true()";
                    if (inNarrowedContext)
                    {
                        // rhs may be uniform (scalar literal) or varying (SIMD expr).
                        // Normalize to a SIMD expression so per-lane extract works.
                        string rhsSimdExpr;
                        if (rhsKind < VarKind.Varying && !rhsExpr.StartsWith("n_") && !rhsExpr.Contains(".v"))
                            rhsSimdExpr = $"simd_value<{elemType}>{{ {setFnScalar}({rhsExpr}) }}";
                        else
                            rhsSimdExpr = rhsExpr;
                        // ★ E7 int→float store fix: use n_extract_lane_i2f for numeric conversion
                        //   (extract int lane, convert to float — not bit reinterpretation)
                        string extractExpr = (elemType == "float" && IsInt32Expr(assign.Right))
                            ? $"n_extract_lane_i2f(({rhsSimdExpr}).v,__l)"
                            : $"{extractFn}({rhsSimdExpr}.v,__l)";
                        return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<g_simdWidthInt;__l++){{if(__sg&(1<<__l)){{{basePtr}[n_extract_lane_epi32({idxExpr}.v,__l)]={extractExpr};}}}}}}";
                    }
                    if (rhsKind < VarKind.Varying)
                    {
                        return $"{storeFnScalar}({basePtr} + {_batchOffsetVar}, {setFnScalar}({rhsExpr}))";
                    }

                    // Contiguous index optimization: when idx == simdIndexVar or
                    // uniform_part + simdIndexVar, use contiguous store instead of per-lane scatter.
                    if (!string.IsNullOrEmpty(_batchLoopVar))
                    {
                        string contBase = null;
                        if (idxExpr == _simdIndexVar)
                            contBase = _batchLoopVar;
                        else
                        {
                            string suffix = $"+ {_simdIndexVar}";
                            if (idxExpr.EndsWith(suffix))
                                contBase = idxExpr.Substring(0, idxExpr.Length - suffix.Length).Trim();
                            else if (idxExpr.EndsWith($"+ {_simdIndexVar})"))
                                contBase = idxExpr.Substring(0, idxExpr.Length - ($"+ {_simdIndexVar})").Length).Trim().TrimStart('(');
                        }
                        if (contBase != null)
                        {
                            // ★ when _returnedMaskVar is set (batch body has `return`), use per-lane
                            //   masked store to avoid overwriting lanes that already returned with their result.
                            //   Only write to non-returned lanes (complement of _returnedMaskVar).
                            if (!string.IsNullOrEmpty(_returnedMaskVar) && contBase == _batchLoopVar)
                            {
                                string rhsSimdExpr;
                                if (rhsKind < VarKind.Varying && !rhsExpr.StartsWith("n_") && !rhsExpr.Contains(".v"))
                                    rhsSimdExpr = $"simd_value<{elemType}>{{ {setFnScalar}({rhsExpr}) }}";
                                else
                                    rhsSimdExpr = rhsExpr;
                                return $"{{int __sg=n_mask_to_bitmask(n_not_mask({_returnedMaskVar}.m));for(int __l=0;__l<g_simdWidthInt;__l++){{if(__sg&(1<<__l)){{{basePtr}[n_extract_lane_epi32({idxExpr}.v,__l)]={(elemType == "float" && IsInt32Expr(assign.Right) ? $"n_extract_lane_i2f(({rhsSimdExpr}).v,__l)" : $"{extractFn}({rhsSimdExpr}.v,__l)")};}}}}}}";
                            }
                            string storeFn = elemType == "float" ? "n_store_ps" : "n_store_epi32";
                            string off = contBase == _batchLoopVar ? contBase : $"({contBase}) + {_batchLoopVar}";
                            // ★ int→float 跨类型写回：向量转换 + 向量 store（避免 scatter）
                            if (elemType == "float" && IsInt32Expr(assign.Right))
                                return $"{storeFn}({basePtr} + {off}, n_cvtepi32_ps({rhsExpr}.v))";
                            return $"{storeFn}({basePtr} + {off}, {rhsExpr}.v)";
                        }
                    }
                    return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{basePtr}[n_extract_lane_epi32({idxExpr}.v,__l)]={(elemType == "float" && IsInt32Expr(assign.Right) ? $"n_extract_lane_i2f(({rhsExpr}).v,__l)" : $"{extractFn}({rhsExpr}.v,__l)")};}}}}";
                }

                // uniform idx + varying rhs -> extract lane 0
                if (rhsKind >= VarKind.Varying)
                {
                    return $"{basePtr}[{idxExpr}] = {extractFn}({rhsExpr}.v, 0)";
                }

                return $"{basePtr}[{idxExpr}] = {rhsExpr}";
        }

        private string TranslateAssignment(AssignmentExpressionSyntax assign)
        {
            // ★ UnsafeUtility.ArrayElementAsRef<T>(ptr, i) = value
            //   之前完全无处理 → 原样吐 C#（A4）。
            if (TryGetArrayElementAsRef(assign.Left) is { } asRefTarget)
                return TranslateArrayElementAsRefStore(asRefTarget.ElemType, asRefTarget.PtrExpr, asRefTarget.IdxExpr, assign);
            // NativeArray writes: detect element-access LHS
            ElementAccessExpressionSyntax elemAccess = null;
            IdentifierNameSyntax id = null;
            if (assign.Left is ElementAccessExpressionSyntax ea)
            {
                elemAccess = ea;
                id = ea.Expression as IdentifierNameSyntax;
            }
            // 元素写入：NativeArray 字段（`X_ptr`）与裸指针变量（`X`）统一在此分派
            if (id != null && TryEmitElementWrite(assign, elemAccess, id) is { } elementWrite)
                return elementWrite;

            // ★ Struct NativeArray field assignment: structArray[idx].field = rhs
            //   Handle positions[i].Value = expr; pattern with per-lane field scatter.
            if (assign.Left is MemberAccessExpressionSyntax ma
                && ma.Expression is ElementAccessExpressionSyntax ea2
                && ea2.Expression is IdentifierNameSyntax id2)
            {
                string arrName2 = id2.Identifier.Text;
                if (_nativeArrayParams.TryGetValue(arrName2, out var saElemType)
                    && saElemType != "float" && saElemType != "int"
                    && !saElemType.Contains("float2") && !saElemType.Contains("int2"))
                {
                    string fieldName2 = ma.Name.Identifier.Text;
                    string idxExpr2 = ea2.ArgumentList?.Arguments.Count > 0 ? TranslateExpression(ea2.ArgumentList?.Arguments[0]?.Expression) : "0";
                    string rhsExpr2 = TranslateExpression(assign.Right);
                    VarKind idxKind2 = VarKind.Uniform;
                    if (ea2.ArgumentList?.Arguments.Count > 0)
                        idxKind2 = _varAnalyzer.ClassifyExpression(ea2.ArgumentList?.Arguments[0]?.Expression);
                    VarKind rhsKind2 = _varAnalyzer.ClassifyExpression(assign.Right);
                    // 判断该字段是否为 float2（如 MovePosition.Value）→ 需要 x/y 双通道 scatter
                    bool fieldIsFloat2 = IsStructFieldFloat2(saElemType, fieldName2);
                    string op2 = assign.OperatorToken.Text;
                    bool isCompound2 = op2 != "=";
                    // 复合赋值 RHS：float2 字段 → 旧值（gather x/y）OP rhs 的分量；标量字段 → 单通道
                    // 标量复合（float += rhs）：旧值 = arr[lane].field，用 OP= 保留语义
                    if (idxKind2 >= VarKind.Varying)
                    {
                        if (fieldIsFloat2)
                        {
                            // float2 字段复合赋值：逐 lane 读旧 x/y + 运算 + 写回
                            // rhs 若是 simd_value<float2>（velocities[i].Value * dt），需取 x/y 分量
                            string rhsX2 = $"n_extract_lane_f32(({rhsExpr2}).x.v,__l)";
                            string rhsY2 = $"n_extract_lane_f32(({rhsExpr2}).y.v,__l)";
                            string lane2 = $"n_extract_lane_epi32({idxExpr2}.v,__l)";
                            string lhsX2 = $"{id2.Identifier.Text}_ptr[{lane2}].{fieldName2}.x()";
                            string lhsY2 = $"{id2.Identifier.Text}_ptr[{lane2}].{fieldName2}.y()";
                            string newX2 = isCompound2 ? $"({lhsX2} {op2.Replace("=", "")} {rhsX2})" : rhsX2;
                            string newY2 = isCompound2 ? $"({lhsY2} {op2.Replace("=", "")} {rhsY2})" : rhsY2;
                            string body2 = $"{lhsX2}={newX2};{lhsY2}={newY2};";
                            if (_currentMask != "simd_mask::all_true()")
                                return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<g_simdWidthInt;__l++){{if(__sg&(1<<__l)){{{body2}}}}}}}";
                            return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{body2}}}}}";
                        }
                        // 标量字段：复合赋值读旧值 + 运算 + 写回
                        string rhsLane2 = $"n_extract_lane_f32({rhsExpr2}.v,__l)";
                        string lhsLane2 = $"{id2.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr2}.v,__l)].{fieldName2}";
                        string combineRhs2 = isCompound2
                            ? $"({lhsLane2} {op2} {rhsLane2})"
                            : rhsLane2;
                        if (_currentMask != "simd_mask::all_true()")
                        {
                            return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<g_simdWidthInt;__l++){{if(__sg&(1<<__l)){{{id2.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr2}.v,__l)].{fieldName2}={combineRhs2};}}}}}}";
                        }
                        return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{id2.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr2}.v,__l)].{fieldName2}={combineRhs2};}}}}";
                    }
                    // Uniform index: scalar field assignment（复合赋值原样保留）
                    if (isCompound2)
                        return $"{id2.Identifier.Text}_ptr[{idxExpr2}].{fieldName2} {op2} {rhsExpr2}";
                    return $"{id2.Identifier.Text}_ptr[{idxExpr2}].{fieldName2} = {rhsExpr2}";
                }
            }

            // ★ Deferred struct local field assignment: structLocal.field = rhs
            //   Where structLocal = structArray[idx]; decompose into per-lane field scatter
            //   Example: position.Value = expr  →  positions_ptr[v_i].Value = expr (per-lane scatter)
            if (assign.Left is MemberAccessExpressionSyntax ma3
                && ma3.Expression is IdentifierNameSyntax structLocalId2
                && _structVaryingLocals.TryGetValue(structLocalId2.Identifier.Text, out var structLocalAssignInfo))
            {
                string fieldName3 = ma3.Name.Identifier.Text;
                string arrName3 = structLocalAssignInfo.arrName;
                string idxExpr3 = structLocalAssignInfo.indexExpr;
                string rhsExpr3 = TranslateExpression(assign.Right);
                string op3 = assign.OperatorToken.Text;

                // SIMD context: per-lane field scatter
                // For varying index, generate per-lane scatter to arr_ptr[v_i_lane].field
                if (idxExpr3.Contains("v_") || idxExpr3 == "v_i" || _currentMask != "simd_mask::all_true()")
                {
                    string extractFn3 = "n_extract_lane_f32";
                    string combineExpr = op3 == "=" ? rhsExpr3 : $"{idxExpr3} {op3.Replace("=", "")} {rhsExpr3}";

                    if (_currentMask != "simd_mask::all_true()")
                    {
                        return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<g_simdWidthInt;__l++){{if(__sg&(1<<__l)){{{arrName3}_ptr[n_extract_lane_epi32({idxExpr3}.v,__l)].{fieldName3}={extractFn3}({combineExpr}.v,__l);}}}}}}";
                    }
                    return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{arrName3}_ptr[n_extract_lane_epi32({idxExpr3}.v,__l)].{fieldName3}={extractFn3}({combineExpr}.v,__l);}}}}";
                }
                // Uniform index: scalar field access
                return $"{arrName3}_ptr[{idxExpr3}].{fieldName3} {op3} {rhsExpr3}";
            }

            // ★ Struct field sub-field assignment: array[idx].field1.field2 = rhs
            //   Handle positions[i].Value.x = expr; pattern per-lane field scatter.
            if (assign.Left is MemberAccessExpressionSyntax ma5
                && ma5.Expression is MemberAccessExpressionSyntax ma6
                && ma6.Expression is ElementAccessExpressionSyntax ea5
                && ea5.Expression is IdentifierNameSyntax id5)
            {
                string arrName5 = id5.Identifier.Text;
                if (_nativeArrayParams.TryGetValue(arrName5, out var saElemType5)
                    && saElemType5 != "float" && saElemType5 != "int"
                    && !saElemType5.Contains("float2") && !saElemType5.Contains("int2"))
                {
                    string fieldPath = ma6.Name.Identifier.Text + "." + ma5.Name.Identifier.Text + "()";
                    string idxExpr5 = ea5.ArgumentList?.Arguments.Count > 0 ? TranslateExpression(ea5.ArgumentList?.Arguments[0]?.Expression) : "0";
                    string rhsExpr5 = TranslateExpression(assign.Right);
                    VarKind idxKind5 = VarKind.Uniform;
                    if (ea5.ArgumentList?.Arguments.Count > 0)
                        idxKind5 = _varAnalyzer.ClassifyExpression(ea5.ArgumentList?.Arguments[0]?.Expression);

                    if (idxKind5 >= VarKind.Varying)
                    {
                        // Per-lane scatter for struct sub-field write
                        if (_currentMask != "simd_mask::all_true()")
                            return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<g_simdWidthInt;__l++){{if(__sg&(1<<__l)){{{id5.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr5}.v,__l)].{fieldPath}=n_extract_lane_f32({rhsExpr5}.v,__l);}}}}}}";
                        return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{id5.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr5}.v,__l)].{fieldPath}=n_extract_lane_f32({rhsExpr5}.v,__l);}}}}";
                    }
                    return $"{id5.Identifier.Text}_ptr[{idxExpr5}].{fieldPath} = {rhsExpr5}";
                }
            }

            // ★ Struct field sub-field assignment (two-level member access on struct NativeArray):
            //   Positions[i].Value.x = expr; → per-lane field scatter with .x() method syntax
            if (assign.Left is MemberAccessExpressionSyntax _ma5
                && _ma5.Expression is MemberAccessExpressionSyntax _ma6
                && _ma6.Expression is ElementAccessExpressionSyntax _ea5
                && _ea5.Expression is IdentifierNameSyntax _id5)
            {
                string arrName5 = _id5.Identifier.Text;
                if (_nativeArrayParams.TryGetValue(arrName5, out var saElemType5)
                    && saElemType5 != "float" && saElemType5 != "int"
                    && !saElemType5.Contains("float2") && !saElemType5.Contains("int2"))
                {
                    string fieldPath = _ma6.Name.Identifier.Text + "." + _ma5.Name.Identifier.Text + "()";
                    string idxExpr5 = _ea5.ArgumentList?.Arguments.Count > 0 ? TranslateExpression(_ea5.ArgumentList?.Arguments[0]?.Expression) : "0";
                    string rhsExpr5 = TranslateExpression(assign.Right);
                    VarKind idxKind5 = VarKind.Uniform;
                    if (_ea5.ArgumentList?.Arguments.Count > 0)
                        try { idxKind5 = _varAnalyzer.ClassifyExpression(_ea5.ArgumentList?.Arguments[0]?.Expression); } catch { idxKind5 = VarKind.Varying; }

                    if (idxKind5 >= VarKind.Varying)
                    {
                        if (_currentMask != "simd_mask::all_true()")
                            return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<g_simdWidthInt;__l++){{if(__sg&(1<<__l)){{{_id5.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr5}.v,__l)].{fieldPath}=n_extract_lane_f32({rhsExpr5}.v,__l);}}}}}}";
                        return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{_id5.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr5}.v,__l)].{fieldPath}=n_extract_lane_f32({rhsExpr5}.v,__l);}}}}";
                    }
                    return $"{_id5.Identifier.Text}_ptr[{idxExpr5}].{fieldPath} = {rhsExpr5}";
                }
            }

            string lhs = TranslateExpression(assign.Left);
            string rhs = TranslateExpression(assign.Right);
            string op = assign.OperatorToken.Text;

            // ★ struct-varying local 重赋值刷新（#13）：structLocal = array[other_idx] 后，
            //   _structVaryingLocals 里的 (arrName, elemType, indexExpr) 必须同步更新，
            //   否则后续 structLocal.field 访问仍用旧 indexExpr → gather/scatter 地址错误。
            //   仅整体重赋值（左值是纯 identifier）适用；字段赋值（structLocal.field = x）
            //   已在上方 scatter 分支处理，不在此刷新。
            if (op == "=" && assign.Left is IdentifierNameSyntax svlId
                && _structVaryingLocals.ContainsKey(svlId.Identifier.Text))
            {
                if (assign.Right is ElementAccessExpressionSyntax svlEA
                    && svlEA.Expression is IdentifierNameSyntax svlArrId
                    && _nativeArrayParams.TryGetValue(svlArrId.Identifier.Text, out var svlElemType)
                    && svlElemType != "float" && svlElemType != "int"
                    && !svlElemType.Contains("float2") && !svlElemType.Contains("int2"))
                {
                    string svlIdxExpr = svlEA.ArgumentList?.Arguments.Count > 0
                        ? TranslateExpression(svlEA.ArgumentList.Arguments[0].Expression)
                        : "0";
                    _structVaryingLocals[svlId.Identifier.Text] = (svlArrId.Identifier.Text, svlElemType, svlIdxExpr);
                }
                else
                {
                    // 重赋值为非数组元素（普通值/其他表达式）→ 该 local 不再指向数组元素，
                    // 移除映射；后续 field 访问回落通用路径（可能按标量/其他方式处理）。
                    _structVaryingLocals.Remove(svlId.Identifier.Text);
                }
            }

            // Scope narrowing: declare at first assignment
            string? declLhs = assign.Left is IdentifierNameSyntax declId ? declId.Identifier.Text : null;
            if (op == "=" && declLhs != null && _variables.TryGetValue(declLhs, out var lhsInfo) && lhsInfo.Kind >= VarKind.Varying && !_varDeclEmitted.Contains(declLhs))
            {
                string declType = GetSIMDTypeString(lhsInfo.CppType);
                if (declType != null)
                {
                    _varDeclEmitted.Add(declLhs);
                    _simdVaryingVarNames.Add(declLhs);
                    _simdVaryingCppType[declLhs] = lhsInfo.CppType;
                    return $"{declType} {lhs} = {rhs}";
                }
            }

            // ★ Reduction folding: return n_min_ps/n_max_ps instead of blend
            if (op == "=" && _foldReduceFn != null)
            {
                string fn = _foldReduceFn;
                _foldReduceFn = null;
                // Both operands are simd_value<T>, unwrap .v for raw n_float/n_int
                return $"{lhs} = simd_value<float>{{ {fn}({lhs}.v, {rhs}.v) }}";
            }

            // ★ CRITICAL: inside mask-narrowed context (if/else), SIMD assignment to a varying
            //   variable must use blend() to preserve inactive lanes. 通解：纯赋值（=）与复合
            //   赋值（+= -= *= /= %= &= |= ^= <<= >>=）都要掩码——否则复合赋值会对所有 lane
            //   无条件执行，破坏 if/else 的 lane 隔离。
            if (_currentMask != "simd_mask::all_true()")
            {
                string? lhsVar = assign.Left is IdentifierNameSyntax lhsId ? lhsId.Identifier.Text : null;
                if (lhsVar != null && _variables.TryGetValue(lhsVar, out var blInfo) && blInfo.Kind >= VarKind.Varying)
                {
                    if (op == "=")
                    {
                        return $"{lhs} = blend({lhs}, {rhs}, {_currentMask})";
                    }
                    if (op.Length > 1 && op[op.Length - 1] == '=')
                    {
                        // 复合赋值 lhs op= rhs ⟺ lhs = lhs <base-op> rhs（再按掩码 blend）
                        string baseOp = op.Substring(0, op.Length - 1);
                        return $"{lhs} = blend({lhs}, {lhs} {baseOp} {rhs}, {_currentMask})";
                    }
                }
            }

            return $"{lhs} {op} {rhs}";
        }
        private string TranslateTernary(ConditionalExpressionSyntax ternary)
        {
            string condition = TranslateCondition(ternary.Condition);
            string whenTrue = TranslateExpression(ternary.WhenTrue);
            string whenFalse = TranslateExpression(ternary.WhenFalse);
            VarKind kind = _varAnalyzer.ClassifyExpression(ternary);

            if (kind >= VarKind.Varying)
            {
                // SIMD blend: mask ? true_val : false_val
                return $"blend({whenFalse}, {whenTrue}, {condition})";
            }

            return $"({condition} ? {whenTrue} : {whenFalse})";
        }

        

        /// <summary>
        /// Translate field access on struct NativeArray with direct element access.
        /// Handles: structArray[idx].fieldName
        /// </summary>
        /// <summary>
        /// 判断 struct 类型的某字段是否为 float2（如 MovePosition.Value）。
        /// saElemType 是 C++ 类型名（如 EntJoySample::...::MovePosition），
        /// 通过语义模型解析对应 C# 类型后查字段类型。
        /// </summary>
        private bool IsStructFieldFloat2(string structCppType, string fieldName)
        {
            // 从 C++ 类型名提取 C# 类型：EntJoySample::NS::MovePosition → EntJoySample.NS.MovePosition
            string csTypeName = structCppType.Replace("::", ".");
            try
            {
                var csType = _semanticModel.Compilation.GetTypeByMetadataName(csTypeName);
                if (csType != null)
                {
                    var field = csType.GetMembers(fieldName).OfType<IFieldSymbol>().FirstOrDefault();
                    if (field != null)
                        return field.Type.ToDisplayString().Contains("float2");
                }
            }
            catch { }
            // 兜底：字段名常见 float2 命名启发（Value/Position/Velocity 常为 float2）
            return false;
        }

        private string TranslateStructArrayFieldAccess(string arrName, string structElemType, string fieldName, ExpressionSyntax? indexExpr)
        {
            if (indexExpr == null)
                return $"{arrName}_ptr[0].{fieldName}";

            string idxExpr = TranslateExpression(indexExpr);
            // ClassifyExpression may throw on SyntaxFactory nodes (modified AST)
            VarKind idxKind = VarKind.Varying;
            try { idxKind = _varAnalyzer.ClassifyExpression(indexExpr); } catch { }

            if (idxKind >= VarKind.Varying)
            {
                string safeIdx = _currentMask != "simd_mask::all_true()"
                    ? $"simd_min(simd_max({idxExpr}, simd_value<int>(0)), simd_value<int>::broadcast({arrName}_length - 1))"
                    : idxExpr;
                // float2 字段 → simd_value<float2>（x/y 双通道）
                if (IsStructFieldFloat2(structElemType, fieldName))
                {
                    return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>{{ n_gather_ps<sizeof({structElemType})>((const float*)(&{arrName}_ptr[0].{fieldName}), {safeIdx}.v) }}, simd_value<float>{{ n_gather_ps<sizeof({structElemType})>(((const float*)(&{arrName}_ptr[0].{fieldName})) + 1, {safeIdx}.v) }} }}";
                }
                return $"simd_value<float>{{ n_gather_ps<sizeof({structElemType})>((const float*)(&{arrName}_ptr[0].{fieldName}), {safeIdx}.v) }}";
            }
            return $"{arrName}_ptr[{idxExpr}].{fieldName}";
        }

        /// <summary>
        /// Translate field access on a deferred struct local.
        /// The local was initialized from structArray[idx]; field access becomes
        /// a field-level gather with struct stride (ISPC-style).
        /// Handles: structLocal.fieldName  (where structLocal = structArray[idx])
        /// </summary>
        private string TranslateStructFieldAccess(string arrName, string structElemType, string fieldName, string idxExpr)
        {
            // Check if the index expression is varying (SIMD context)
            bool isVarying = idxExpr.Contains("v_") || idxExpr == "v_i" || idxExpr.Contains("simd_");
            // Also check the current mask context
            if (_currentMask != "simd_mask::all_true()" || isVarying)
            {
                string safeIdx = _currentMask != "simd_mask::all_true()"
                    ? $"simd_min(simd_max({idxExpr}, simd_value<int>(0)), simd_value<int>::broadcast({arrName}_length - 1))"
                    : idxExpr;
                // float2 字段（如 MovePosition.Value）→ 返回 simd_value<float2>（x/y 双通道）
                if (IsStructFieldFloat2(structElemType, fieldName))
                {
                    return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>{{ n_gather_ps<sizeof({structElemType})>((const float*)(&{arrName}_ptr[0].{fieldName}), {safeIdx}.v) }}, simd_value<float>{{ n_gather_ps<sizeof({structElemType})>(((const float*)(&{arrName}_ptr[0].{fieldName})) + 1, {safeIdx}.v) }} }}";
                }
                return $"simd_value<float>{{ n_gather_ps<sizeof({structElemType})>((const float*)(&{arrName}_ptr[0].{fieldName}), {safeIdx}.v) }}";
            }
            return $"{arrName}_ptr[{idxExpr}].{fieldName}";
        }

        /// <summary>
        /// Check if a NativeArray element type is a user-defined struct (not SIMD-primitive).
        /// </summary>
        private static bool IsStructNativeArrayType(string elemCppType)
        {
            // ⚠ 语义是"该类型能否用 `T(v)` 构造"（float2/int2/用户结构体），**不是**"不是标量"。
            //   旧实现用 `!= "float" && != "int"` 判定，把 unsigned char/signed char/uint 等
            //   标量误判成构造类型，生成 `unsigned char(0)` —— C++ 读成 `unsigned` + `char(0)`，
            //   报 "expected '(' for function-style cast or type construction"。
            string leaf = elemCppType;
            int sep = leaf.LastIndexOf("::");
            if (sep >= 0) leaf = leaf.Substring(sep + 2);
            return leaf.Contains("float2") || leaf.Contains("int2");
        }

        /// <summary>批量路径的 NativeArray 形参名（chunk 路径由 _nativeArrayParams 提供）。</summary>
        private readonly HashSet<string> _nativeArrayParamNames = new();

        private void RegisterNativeArrayParamName(string name)
        {
            if (!string.IsNullOrEmpty(name)) _nativeArrayParamNames.Add(name);
        }

        /// <summary>
        /// 标识符是否是 NativeArray **字段**（需要 `{name}_ptr` 才能得到裸指针）。
        /// 只用名字 + `_jobStruct.GetMembers` 解析 —— 源生成器里 SemanticModel 对名字也不可靠。
        /// 局部指针变量（如 <c>int* hpPtr = (int*)HP.GetUnsafePtr();</c>）**本身就是指针**，
        /// 不能再套 `_ptr` —— 否则生成 `hpPtr_ptr[v_i]` 这个从未声明的符号。
        /// </summary>
        private bool IsNativeArrayField(IdentifierNameSyntax id)
        {
            string name = id.Identifier.Text;
            if (_nativeArrayParams.ContainsKey(name)) return true;
            if (_nativeArrayParamNames.Contains(name)) return true;
            if (_jobStruct == null) return false;
            var members = _jobStruct.GetMembers(name);
            return members.Length > 0 && members[0] is IFieldSymbol f && !f.IsStatic
                && NativeTranspiler.IsEntJoyContainerNamed(f.Type, Config.NativeArray);
        }

        private bool IsNativeArrayName(IdentifierNameSyntax id) => IsNativeArrayField(id);

        /// <summary>
        /// 生成 NativeArray 基址：字段 → <c>{name}_ptr</c>；局部指针变量 → <c>{name}</c>。
        /// </summary>
        private string NativeArrayBase(ExpressionSyntax expr, string translated)
        {
            if (expr is IdentifierNameSyntax id)
                return IsNativeArrayName(id) ? $"{translated}_ptr" : translated;
            return translated;
        }

        private string TranslateObjectCreation(ObjectCreationExpressionSyntax objCreation)
        {
                        INamedTypeSymbol? type = null;
            try { type = _semanticModel.GetTypeInfo(objCreation).Type as INamedTypeSymbol; } catch { }
            string cppType = type != null ? NativeTranspiler.MapCSharpTypeToCpp(type) : "int";

            if (objCreation.ArgumentList != null && objCreation.ArgumentList.Arguments.Count > 0)
            {
                string args = string.Join(", ",
                    objCreation.ArgumentList.Arguments.Select(a => TranslateExpression(a.Expression)));
                return $"{cppType}({args})";
            }

            return $"{cppType}()";
        }

        // ================================================================
        // Utilities
        // ================================================================

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

        /// <summary>Check if an invocation is a gather/gathf call (produces clamped indices).</summary>
        private static bool IsGatherCall(InvocationExpressionSyntax inv)
        {
            if (inv.Expression is IdentifierNameSyntax id)
                return id.Identifier.Text == "gather" || id.Identifier.Text == "gathf";
            if (inv.Expression is MemberAccessExpressionSyntax ma)
                return ma.Name.Identifier.Text == "gather" || ma.Name.Identifier.Text == "gathf";
            return false;
        }

        private static bool IsFloat2Type(string cppType)
        {
            return cppType.Contains("float2") || cppType.Contains("int2");
        }
    }
}
