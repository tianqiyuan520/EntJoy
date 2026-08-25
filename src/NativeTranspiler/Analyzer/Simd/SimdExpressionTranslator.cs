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
                    return TranslateElementAccess(elementAccess);

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
                    return $"(0 - ({TranslateExpression(prefix.Operand)}))";

                case ParenthesizedExpressionSyntax paren:
                    return $"({TranslateExpression(paren.Expression)})";

                case CastExpressionSyntax cast:
                    return TranslateCast(cast);

                case AssignmentExpressionSyntax assign:
                    return TranslateAssignment(assign);

                case CheckedExpressionSyntax checkedExpr:
                    // ★ E2 fix: `unchecked(x + y)` / `checked(x + y)` → translate the inner expr.
                    //   EntJoy arithmetic is always unchecked (wraps), so the flag is a no-op.
                    return TranslateExpression(checkedExpr.Expression);

                case ConditionalExpressionSyntax ternary:
                    return TranslateTernary(ternary);

                case ObjectCreationExpressionSyntax objCreation:
                    return TranslateObjectCreation(objCreation);

                default:
                    return $"/* unsupported expr: {expr.Kind()} */ 0";
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

            // .MaxValue / .MinValue — pick the numeric_limits TYPE from the receiver
            // (int.MaxValue → numeric_limits<int>::max(), float.MinValue → numeric_limits<float>::lowest()).
            // The old code always emitted float limits, so `x == int.MaxValue` compared against
            // FLT_MAX garbage (E3).
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

            // .zero on int2/float2 → constructor
            if (memberName == "zero" && (objExpr.Contains("int2") || objExpr.Contains("float2")))
            {
                string prefix = objExpr.Contains("int2") ? "EntJoy::Mathematics::int2" : "EntJoy::Mathematics::float2";
                return $"{prefix}(0, 0)";
            }

            // .Length on NativeArray → _length suffix
            if (memberName == "Length")
            {
                return $"{objExpr}_length";
            }

            // .x or .y on float2 — use .x/.y member on simd_value<float2>
            if ((memberName == "x" || memberName == "y") && isVaryingFloat2)
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
                // BUT: SIMD expressions (containing ::) use .x/.y as member access, not function call
                if (objExpr.Contains("::") || objExpr.StartsWith("simd_"))
                    return $"{objExpr}.{memberName}";
                return $"{objExpr}.{memberName}()";
            }
            return $"{objExpr}.{memberName}";
        }

        private string TranslateElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
                        // Resolve NativeArray type via _jobStruct symbol (avoid semantic model in source gen context)
            bool isNativeArray = false;
            string elemCppType = "float";
            if (_jobStruct != null && elementAccess.Expression is IdentifierNameSyntax id)
            {
                var members = _jobStruct.GetMembers(id.Identifier.Text);
                if (members.Length > 0 && members[0] is IFieldSymbol field && !field.IsStatic
                    && NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeArray))
                {
                    isNativeArray = true;
                    var typeArg = ((INamedTypeSymbol)field.Type).TypeArguments.FirstOrDefault();
                    if (typeArg != null)
                        elemCppType = NativeTranspiler.MapCSharpTypeToCpp(typeArg);
                }
            }
            // Also check _nativeArrayParams (for static methods without job struct)
            if (!isNativeArray && elementAccess.Expression is IdentifierNameSyntax naId
                && _nativeArrayParams.TryGetValue(naId.Identifier.Text, out var paramElemType))
            {
                isNativeArray = true;
                elemCppType = paramElemType;
            }
            string baseExpr = TranslateExpression(elementAccess.Expression);

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

            if (isNativeArray && indexKind >= VarKind.Varying)
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
                        if (elemCppType.Contains("float2"))
                            return $"simd_value<{elemCppType}>::broadcast({baseExpr}_ptr[{scalarIdx}])";
                        if (elemCppType.Contains("int2"))
                            return $"simd_value<EntJoy::Mathematics::int2>::broadcast({baseExpr}_ptr[{scalarIdx}])";
                        if (elemCppType == "float")
                            return $"simd_value<float>::broadcast({baseExpr}_ptr[{scalarIdx}])";
                        if (elemCppType == "int")
                            return $"simd_value<int>::broadcast({baseExpr}_ptr[{scalarIdx}])";
                        return $"simd_value<float>::broadcast({baseExpr}_ptr[{scalarIdx}])";
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
                    safeIdx = $"simd_min(simd_max({indexExpr}, simd_value<int>(0)), simd_value<int>::broadcast({baseExpr}_length - 1))";
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
                            return $"simd_value<float>{{ n_load_ps({baseExpr}_ptr + {baseOff}) }}";
                        if (elemCppType == "int")
                            return $"simd_value<int>{{ n_load_epi32({baseExpr}_ptr + {baseOff}) }}";
                    }
                }

                // SIMD gather
                if (elemCppType.Contains("float2"))
                {
                    if (safeIdx != indexExpr)
                    {
                        string tidx = $"__ci_{_labelCounter++}";
                        AppendLine($"simd_value<int> {tidx} = {safeIdx};");
                        return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>::gathf({baseExpr}_ptr, {tidx}.v), simd_value<float>::gathfy({baseExpr}_ptr, {tidx}.v) }}";
                    }
                    return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>::gathf({baseExpr}_ptr, {safeIdx}.v), simd_value<float>::gathfy({baseExpr}_ptr, {safeIdx}.v) }}";
                }
                if (elemCppType.Contains("int2"))
                    return $"simd_value<EntJoy::Mathematics::int2>::gather({baseExpr}_ptr, {safeIdx})";
                if (elemCppType == "float")
                    return $"simd_value<float>::gathf({baseExpr}_ptr, {safeIdx}.v)";
                if (elemCppType == "int")
                    return $"simd_value<int>::gather({baseExpr}_ptr, {safeIdx})";
                return $"simd_value<float>::gathf({baseExpr}_ptr, {safeIdx}.v)";
            }

            // Scalar access
            return $"{baseExpr}_ptr[{indexExpr}]";
        }

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
                    return $"{id.Identifier.Text}_ptr";
                }
            }

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
                // ★ E1 fix: fallback to SemanticModel for int type detection.
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

            // ★ FMA detection: a*a + b*b → n_fmadd_ps(a, a, n_mul_ps(b, b))
            if (anyVarying && op == "+" && binary.Left is BinaryExpressionSyntax lmul
                && lmul.OperatorToken.Text == "*"
                && binary.Right is BinaryExpressionSyntax rmul
                && rmul.OperatorToken.Text == "*")
            {
                string la = TranslateExpression(lmul.Left);
                string lb = TranslateExpression(lmul.Right);
                string ra = TranslateExpression(rmul.Left);
                string rb = TranslateExpression(rmul.Right);
                if (la == lb && ra == rb)
                    return $"simd_value<float>{{ n_fmadd_ps({la}.v, {la}.v, n_mul_ps({ra}.v, {ra}.v)) }}";
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
            //   Without this, large uint values (> INT_MAX) produce wrong results (S6 bug).
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

        private string TranslateAssignment(AssignmentExpressionSyntax assign)
        {
            // NativeArray writes: detect element-access LHS
            string baseName = null;
            string elemType = "float";
            ElementAccessExpressionSyntax elemAccess = null;
            if (assign.Left is ElementAccessExpressionSyntax ea
                && (elemAccess = ea).Expression is IdentifierNameSyntax id)
            {
                // 1. Check _nativeArrayParams first (for static methods without job struct)
                if (_nativeArrayParams.TryGetValue(id.Identifier.Text, out var paramElemType))
                {
                    baseName = id.Identifier.Text;
                    elemType = paramElemType;
                }
                // 2. Check job struct fields (for IJob/IJobFor paths)
                else if (_jobStruct != null)
                {
                    var members = _jobStruct.GetMembers(id.Identifier.Text);
                    if (members.Length > 0 && members[0] is IFieldSymbol f
                        && NativeTranspiler.IsEntJoyContainerNamed(f.Type, Config.NativeArray))
                    {
                        baseName = id.Identifier.Text;
                        elemType = NativeTranspiler.MapCSharpTypeToCpp(((INamedTypeSymbol)f.Type).TypeArguments[0]);
                    }
                }
            }

            if (baseName != null)
            {
                string idxExpr = TranslateExpression(
                    elemAccess.ArgumentList?.Arguments[0].Expression ?? assign.Left);
                string rhsExpr = TranslateExpression(assign.Right);
                VarKind idxKind = _varAnalyzer.ClassifyExpression(
                    elemAccess.ArgumentList?.Arguments[0].Expression ?? assign.Left);
                VarKind rhsKind = _varAnalyzer.ClassifyExpression(assign.Right);
                string extractFn = elemType == "float" ? "n_extract_lane_f32" : "n_extract_lane_epi32";
                string storeFnScalar = elemType == "float" ? "n_store_ps" : "n_store_epi32";
                string setFnScalar = elemType == "float" ? "n_set1_ps" : "n_set1_epi32";

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
                            return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<g_simdWidthInt;__l++){{if(__sg&(1<<__l)){{{baseName}_ptr[n_extract_lane_epi32({idxExpr}.v,__l)]={extractExpr};}}}}}}";
                        }
                        if (rhsKind < VarKind.Varying)
                        {
                            return $"{storeFnScalar}({baseName}_ptr + {_batchOffsetVar}, {setFnScalar}({rhsExpr}))";
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
                                // ★ E7 fix: when _returnedMaskVar is set (batch body has `return`), use per-lane
                                //   masked store to avoid overwriting lanes that already returned with their result.
                                //   Only write to non-returned lanes (complement of _returnedMaskVar).
                                if (!string.IsNullOrEmpty(_returnedMaskVar) && contBase == _batchLoopVar)
                                {
                                    string rhsSimdExpr;
                                    if (rhsKind < VarKind.Varying && !rhsExpr.StartsWith("n_") && !rhsExpr.Contains(".v"))
                                        rhsSimdExpr = $"simd_value<{elemType}>{{ {setFnScalar}({rhsExpr}) }}";
                                    else
                                        rhsSimdExpr = rhsExpr;
                                    return $"{{int __sg=n_mask_to_bitmask(n_not_mask({_returnedMaskVar}.m));for(int __l=0;__l<g_simdWidthInt;__l++){{if(__sg&(1<<__l)){{{baseName}_ptr[n_extract_lane_epi32({idxExpr}.v,__l)]={(elemType == "float" && IsInt32Expr(assign.Right) ? $"n_extract_lane_i2f(({rhsSimdExpr}).v,__l)" : $"{extractFn}({rhsSimdExpr}.v,__l)")};}}}}}}";
                                }
                                string storeFn = elemType == "float" ? "n_store_ps" : "n_store_epi32";
                                string off = contBase == _batchLoopVar ? contBase : $"({contBase}) + {_batchLoopVar}";
                                return $"{storeFn}({baseName}_ptr + {off}, {rhsExpr}.v)";
                            }
                        }
                        return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{baseName}_ptr[n_extract_lane_epi32({idxExpr}.v,__l)]={(elemType == "float" && IsInt32Expr(assign.Right) ? $"n_extract_lane_i2f(({rhsExpr}).v,__l)" : $"{extractFn}({rhsExpr}.v,__l)")};}}}}";
                    }

                    // uniform idx + varying rhs -> extract lane 0
                    if (rhsKind >= VarKind.Varying)
                    {
                        return $"{baseName}_ptr[{idxExpr}] = {extractFn}({rhsExpr}.v, 0)";
                    }

                    return $"{baseName}_ptr[{idxExpr}] = {rhsExpr}";
                }

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

                    if (idxKind2 >= VarKind.Varying)
                    {
                        // Per-lane scatter for struct field write (SIMD context)
                        if (_currentMask != "simd_mask::all_true()")
                        {
                            return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<g_simdWidthInt;__l++){{if(__sg&(1<<__l)){{{id2.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr2}.v,__l)].{fieldName2}=n_extract_lane_f32({rhsExpr2}.v,__l);}}}}}}";
                        }
                        return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{id2.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr2}.v,__l)].{fieldName2}=n_extract_lane_f32({rhsExpr2}.v,__l);}}}}";
                    }
                    // Uniform index: scalar field assignment
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
            //   variable must use blend() to preserve inactive lanes.
            //   Without this, ALL lanes overwrite = lanes where condition is false lose their data.
            //   Affects reduction patterns: if(distSq < bestDistSq) { bestDistSq = distSq; bestIdx = i; }
            if (op == "=" && _currentMask != "simd_mask::all_true()")
            {
                string? lhsVar = assign.Left is IdentifierNameSyntax lhsId ? lhsId.Identifier.Text : null;
                if (lhsVar != null && _variables.TryGetValue(lhsVar, out var blInfo) && blInfo.Kind >= VarKind.Varying)
                {
                    return $"{lhs} = blend({lhs}, {rhs}, {_currentMask})";
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
                return $"simd_value<float>{{ n_gather_ps<sizeof({structElemType})>((const float*)(&{arrName}_ptr[0].{fieldName}), {safeIdx}.v) }}";
            }
            return $"{arrName}_ptr[{idxExpr}].{fieldName}";
        }

        /// <summary>
        /// Check if a NativeArray element type is a user-defined struct (not SIMD-primitive).
        /// </summary>
        private static bool IsStructNativeArrayType(string elemCppType)
        {
            return elemCppType != "float" && elemCppType != "int"
                && !elemCppType.Contains("float2") && !elemCppType.Contains("int2");
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
