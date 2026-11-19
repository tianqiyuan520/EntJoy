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

        /// <summary>
        /// <paramref name="expr"/> 是否出现在另一个元素访问的下标位置（即 `A[B[i]]` 里的 `B[i]`）。
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
        /// <paramref name="asScalarIndex"/>：调用方需要标量下标（本表达式是另一个元素访问的下标），
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
            SimdElemClass elemClass = SimdElemClass.Other;
            // 统一解析：NativeArray 字段 → `X_ptr`；裸指针变量（局部/形参）→ `X`
            if (NativeArrayBaseInfo(elementAccess.Expression) is { } naInfo)
            {
                isNativeArray = true;
                elemCppType = naInfo.ElemType;
                baseExpr = naInfo.Base;
                // 元素类别决定访存路径：32 位标量/pair 可直接向量化，8/16 位整数走扩宽 load，
                // 其余（结构体等）逐 lane。
                elemClass = SimdVectorTypes.ClassifyElem(elemCppType);
                vectorizableElem = elemClass is SimdElemClass.Lane32 or SimdElemClass.Pair;
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
                        // Safety clamp: mask ctx → clamp to [0, Length-1] for unmasked gather
                        string safeIdx = !_currentMask.IsAllTrue
                            ? $"simd_min(simd_max({indexExpr}, simd_value<int>(0)), simd_value<int>::broadcast({baseExpr}.Length - 1))"
                            : indexExpr;
                        if (SimdVectorTypes.IsFloat2(elemCppType))
                            return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>::gathf(({elemCppType}*){baseExpr}.Ptr, {safeIdx}.v), simd_value<float>::gathfy(({elemCppType}*){baseExpr}.Ptr, {safeIdx}.v) }}";
                        if (SimdVectorTypes.IsInt2(elemCppType))
                            return $"simd_value<EntJoy::Mathematics::int2>::gather(({elemCppType}*){baseExpr}.Ptr, {safeIdx})";
                        return $"simd_value<float>::gathf(({elemCppType}*){baseExpr}.Ptr, {safeIdx}.v)";
                    }
                    return $"(({elemCppType}*){baseExpr}.Ptr)[{indexExpr}]";
                }
            }

            // 向量 load/gather 只支持 float/int（n_load_ps / n_load_epi32 / gather）。
            //   byte / uint 等窄或宽元素、以及不可向量化的基址 → 退回标量下标读。

            if (isNativeArray && vectorizableElem && indexKind >= VarKind.Varying)
            {
                // Check if index is from a uniform-bound reduction loop induction variable
                //   → emit broadcast of scalar load instead of gather.
                //   This is the key optimization for fallback loops like for(i=0; i<N; i++):
                //   one scalar load + broadcast to all 8 lanes.
                if (elementAccess.ArgumentList?.Arguments.Count > 0)
                {
                    var rawArg = elementAccess.ArgumentList.Arguments[0].Expression;
                    if (rawArg is IdentifierNameSyntax rawId && _uniformLoopVars.Contains(rawId.Identifier.Text))
                    {
                        string scalarIdx = rawId.Identifier.Text;
                        // 基址是数组本身（baseExpr = X_ptr），不是下标变量；按下标取基址会生成 `i[i]`。
                        string naBase = baseExpr;
                        if (SimdVectorTypes.IsFloat2(elemCppType))
                            return $"simd_value<{elemCppType}>::broadcast({naBase}[{scalarIdx}])";
                        if (SimdVectorTypes.IsInt2(elemCppType) || SimdVectorTypes.IsUint2(elemCppType))
                            return $"simd_value<EntJoy::Mathematics::int2>::broadcast({naBase}[{scalarIdx}])";
                        if (elemCppType == "float")
                            return $"simd_value<float>::broadcast({naBase}[{scalarIdx}])";
                        if (elemCppType == "int" || elemCppType == "unsigned int")
                            return $"simd_value<int>::broadcast((int){naBase}[{scalarIdx}])";
                        return $"simd_value<float>::broadcast({naBase}[{scalarIdx}])";
                    }
                }

                // Safety clamp for gather: when in mask context, clamp indices to
                //   [0, arr_length-1] to prevent AVX2 unmasked gather OOB.
                //   Skip if index variable was already clamped by a prior gather.
                string safeIdx;
                if (elementAccess.ArgumentList?.Arguments.Count > 0
                    && elementAccess.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax idxId
                    && _clampedVars.Contains(idxId.Identifier.Text))
                {
                    safeIdx = indexExpr; // already clamped by prior gather
                }
                else if (_inVaryingReductionLoop && _hoistedSafeMaxVar != null && baseExpr == _hoistedSafeMaxExpr)
                {
                    // Varying reduction loop: index pre-clamped to >=0, use hoisted broadcast
                    safeIdx = $"simd_min({indexExpr}, {_hoistedSafeMaxVar})";
                }
                else if (!_currentMask.IsAllTrue)
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

                // 连续访问：下标能写成 `v_i ± 标量` 时用整宽 load，不做 gather。
                if (elementAccess.ArgumentList?.Arguments.Count > 0
                    && TryResolveContiguousOffset(elementAccess.ArgumentList.Arguments[0].Expression, out string pointerOffset))
                {
                    if (elemCppType == "float")
                        return $"simd_value<float>{{ n_load_ps({baseExpr} + {pointerOffset}) }}";
                    if (elemCppType == "int")
                        return $"simd_value<int>{{ n_load_epi32({baseExpr} + {pointerOffset}) }}";
                    // uint 与 int 同宽同布局：按 int 指针搬运，位模式不变。
                    if (elemCppType == "unsigned int")
                        return $"simd_value<int>{{ n_load_epi32((const int*)({baseExpr}) + {pointerOffset}) }}";
                }

                // SIMD gather
                if (SimdVectorTypes.IsFloat2(elemCppType))
                {
                    if (safeIdx != indexExpr)
                    {
                        string tidx = $"__ci_{_labelCounter++}";
                        AppendLine($"simd_value<int> {tidx} = {safeIdx};");
                        return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>::gathf({baseExpr}, {tidx}.v), simd_value<float>::gathfy({baseExpr}, {tidx}.v) }}";
                    }
                    return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>::gathf({baseExpr}, {safeIdx}.v), simd_value<float>::gathfy({baseExpr}, {safeIdx}.v) }}";
                }
                if (SimdVectorTypes.IsInt2(elemCppType))
                    return $"simd_value<EntJoy::Mathematics::int2>::gather({baseExpr}, {safeIdx})";
                // uint2 与 int2 同寄存器布局：gather 按位重新解释指针即可（元素内容不变）。
                if (SimdVectorTypes.IsUint2(elemCppType))
                    return $"simd_value<EntJoy::Mathematics::int2>::gather((const EntJoy::Mathematics::int2*){baseExpr}, {safeIdx})";
                if (elemCppType == "float")
                    return $"simd_value<float>::gathf({baseExpr}, {safeIdx}.v)";
                if (elemCppType == "int")
                    return $"simd_value<int>::gather({baseExpr}, {safeIdx})";
                // uint 的 gather 走同一套 32 位整数收集；指针按 int* 重新解释，位模式不变。
                if (elemCppType == "unsigned int")
                    return $"simd_value<int>::gather((const int*)({baseExpr}), {safeIdx})";
                return $"simd_value<float>::gathf({baseExpr}, {safeIdx}.v)";
            }

            // 窄整数（u8/s8/u16/s16）连续访问：一次扩宽 load 取 8/16 个元素，替代逐 lane 读。
            if (isNativeArray && elemClass == SimdElemClass.NarrowInt && indexKind >= VarKind.Varying
                && elementAccess.ArgumentList?.Arguments.Count > 0
                && SimdVectorTypes.NarrowLoadFn(elemCppType) is { } widenLoad
                && TryResolveContiguousOffset(elementAccess.ArgumentList.Arguments[0].Expression, out string narrowOffset))
            {
                return $"simd_value<int>{{ {widenLoad}((const {elemCppType}*)({baseExpr}) + {narrowOffset}) }}";
            }

            // A1 通解：varying 下标 + 不可向量化元素（byte/sbyte/float2/结构体）的向量读。
            //   会退化成 lane0 标量读（`n_extract_lane_epi32((v_i).v, 0)`）——那是"每 lane 读同一个
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
                bool masked = !_currentMask.IsAllTrue;
                AppendLine($"simd_value<int> {seqVar} = simd_value<int>::sequence(0, g_simdWidthInt);");
                AppendLine($"simd_value<int> {resVar} = simd_value<int>::broadcast(0);");
                AppendLine($"for (int __gl = 0; __gl < g_simdWidthInt; __gl++)");
                AppendLine("{");
                if (masked)
                    AppendLine($"    if ((n_mask_to_bitmask({CppIntegerSemantics.MemberOperand(_currentMask)}.m) & (1 << __gl)) == 0) continue;");
                AppendLine($"    int __gx = n_extract_lane_epi32({idxVar}.v, __gl);");
                AppendLine($"    simd_value<int> __gb = simd_value<int>::broadcast((int){elemPtr}[__gx]);");
                AppendLine($"    simd_mask __gm{{ n_cmp_eq_epi32({CppIntegerSemantics.MemberOperand(seqVar)}.v, n_set1_epi32(__gl)) }};");
                AppendLine($"    {resVar} = blend({resVar}, __gb, __gm);");
                AppendLine("}");
                return resVar;
            }

            // Scalar access（varying 下标 + 不可向量化元素）
            // ⚠ 这里返回裸标量 `baseExpr[lane0]`（无 `.v`）。
            //   尝试过在"非下标位置"包一层 `simd_value<int>{...}` 让它 `.v`-able，实测失败且更糟：
            //   - 结构体元素：`simd_value<int>{ (int)(cfgPtr[i]) }` → cannot convert 'CpuUnitConfigData' to 'int'
            //   - 结构体字段：`simd_value<int>{ (int)(cfgPtr[i]) }.FramesDeath` → 字段取在 int 包装上
            //   - 左值位置：`simd_value<int>{ (int)(velPtr[i]) } = float2(...)` → 临时量不可赋值
            //   ⇒ 包装点选错了：不该在"元素访问出口"包，而应在消费点
            //     （条件构造 / 赋值 RHS 取 `.v` 的那几处）按需包装/取 lane0。
            //     详见 docs  的设计修正。
            if (indexKind >= VarKind.Varying)
            {
                indexExpr = SimdExprShapes.Of(indexExpr).HasFlag(SimdExprTraits.LaneAccess)
                    ? $"n_extract_lane_epi32({indexExpr}, 0)"
                    : $"n_extract_lane_epi32({CppIntegerSemantics.MemberOperand(indexExpr)}.v, 0)";
            }
            return $"{baseExpr}[{indexExpr}]";
        }

        /// <summary>元素的 C++ 类型是否为内置标量（可安全按 (int) 扩展成 simd_value&lt;int&gt;）。</summary>
        private static bool IsBuiltinScalarElem(string cppType)
            => cppType == "float" || cppType == "int" || cppType == "unsigned char"
            || cppType == "signed char" || cppType == "unsigned int" || cppType == "short"
            || cppType == "unsigned short" || cppType == "bool";

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
                if (SymbolHelper.GetBoundSymbol(_semanticModel, inv) is IMethodSymbol sym
                    && sym.ContainingType?.ToDisplayString() != "EntJoy.Collections.UnsafeUtility")
                    return null;
            }
            catch { }
            string elemCpp = "int";
            if (ma.Name is GenericNameSyntax gname && gname.TypeArgumentList.Arguments.Count > 0)
            {
                var t = SymbolHelper.GetBoundType(_semanticModel, gname.TypeArgumentList.Arguments[0]);
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
            if (_varAnalyzer.LocalPointerElemCpp.TryGetValue(name, out var le)) return le;
            if (_varAnalyzer.ParamPointerElemCpp.TryGetValue(name, out var pe)) return pe;
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
                        return SimdVectorTypes.IsFloat2(NativeTranspiler.MapCSharpTypeToCpp(field.Type));
                }
            }
            catch { }
            // 兜底：字段名常见 float2 命名启发（Value/Position/Velocity 常为 float2）
            return false;
        }

        private string StructFieldType(ExpressionSyntax expression, string structType, string fieldName)
        {
            var type = SymbolHelper.GetBoundType(_semanticModel, expression);
            if (type == null && expression is MemberAccessExpressionSyntax member
                && SymbolHelper.GetBoundType(_semanticModel, member.Expression) is INamedTypeSymbol owner)
                type = owner.GetMembers(fieldName).OfType<IFieldSymbol>().FirstOrDefault()?.Type;
            return type != null ? NativeTranspiler.MapCSharpTypeToCpp(type)
                : IsStructFieldFloat2(structType, fieldName) ? "EntJoy::Mathematics::float2" : "float";
        }

        private int StructFieldStride(MemberAccessExpressionSyntax member)
            => SymbolHelper.GetBoundSymbol(_semanticModel, member) is IFieldSymbol field
                ? CppJobGenerator.GetStructSizeRecursive(field.ContainingType)
                : SymbolHelper.GetBoundType(_semanticModel, member.Expression) is INamedTypeSymbol owner
                    ? CppJobGenerator.GetStructSizeRecursive(owner) : 0;

        private string TranslateStructArrayFieldAccess(string arrName, string structElemType, string fieldName, ExpressionSyntax? indexExpr, string fieldType, int stride)
        {
            if (indexExpr == null) return $"{arrName}_ptr[0].{fieldName}";
            string index = TranslateExpression(indexExpr);
            return _varAnalyzer.ClassifyExpression(indexExpr) >= VarKind.Varying
                ? GatherStructField(arrName, structElemType, fieldName, index, fieldType, stride)
                : $"{arrName}_ptr[{index}].{fieldName}";
        }

        private string TranslateStructFieldAccess(string arrName, string structElemType, string fieldName, string index, string fieldType, int stride, bool varyingIndex)
            => !_currentMask.IsAllTrue || varyingIndex
                ? GatherStructField(arrName, structElemType, fieldName, index, fieldType, stride)
                : $"{arrName}_ptr[{index}].{fieldName}";

        private string GatherStructField(string array, string structure, string field, string index, string fieldType, int stride)
        {
            bool pair = SimdVectorTypes.IsPair(fieldType);
            bool integer = fieldType == "int" || fieldType == "unsigned int"
                || SimdVectorTypes.IsInt2(fieldType) || SimdVectorTypes.IsUint2(fieldType);
            if (!integer && !pair && fieldType != "float")
                return $"/*{UnsupportedMarkers.Expr}SIMD_StructFieldType*/ 0";
            // gather 的基址指针按寄存器元素类型取：uint2 与 int2 同布局，按 int2* 重新解释。
            string pointerElem = SimdVectorTypes.IsUint2(fieldType) ? SimdVectorTypes.Int2 : fieldType;
            string scalar = integer ? "int" : "float";
            string gather = integer ? "n_gather_epi32" : "n_gather_ps";
            string safe = !_currentMask.IsAllTrue
                ? $"simd_min(simd_max({index}, simd_value<int>(0)), simd_value<int>::broadcast({array}_length - 1))" : index;
            string pointer = $"(const {scalar}*)(&{array}_ptr[0].{field})";
            string scale = stride is 1 or 2 or 4 or 8 ? $"sizeof({structure})" : "1";
            string offsets = scale == "1" ? $"n_mullo_epi32({CppIntegerSemantics.MemberOperand(safe)}.v, n_set1_epi32(sizeof({structure})))" : $"{CppIntegerSemantics.MemberOperand(safe)}.v";
            string first = $"simd_value<{scalar}>{{ {gather}<{scale}>({pointer}, {offsets}) }}";
            return pair ? $"simd_value<{pointerElem}>{{ {first}, simd_value<{scalar}>{{ {gather}<{scale}>(({pointer}) + 1, {offsets}) }} }}" : first;
        }

        /// <summary>
        /// Check if a NativeArray element type is a user-defined struct (not SIMD-primitive).
        /// </summary>
        private static bool IsStructNativeArrayType(string elemCppType)
        {
            // ⚠ 语义是"该类型能否用 `T(v)` 构造"（float2/int2/用户结构体），不是"不是标量"。
            //   用 `!= "float" && != "int"` 判定，把 unsigned char/signed char/uint 等
            //   标量误判成构造类型，生成 `unsigned char(0)` —— C++ 读成 `unsigned` + `char(0)`，
            //   报 "expected '(' for function-style cast or type construction"。
            string leaf = elemCppType;
            int sep = leaf.LastIndexOf("::");
            if (sep >= 0) leaf = leaf.Substring(sep + 2);
            return SimdVectorTypes.IsPairLeaf(leaf);
        }

        /// <summary>
        /// 标识符是否是 NativeArray 字段（需要 `{name}_ptr` 才能得到裸指针）。
        /// 只用名字 + `_jobStruct.GetMembers` 解析 —— 源生成器里 SemanticModel 对名字也不可靠。
        /// 局部指针变量（如 <c>int* hpPtr = (int*)HP.GetUnsafePtr();</c>）本身就是指针，
        /// 不能再套 `_ptr` —— 否则生成 `hpPtr_ptr[v_i]` 这个从未声明的符号。
        /// </summary>
        private bool IsNativeArrayField(IdentifierNameSyntax id)
        {
            string name = id.Identifier.Text;
            if (_nativeArrayParams.ContainsKey(name)) return true;
            if (_jobStruct == null) return false;
            var members = _jobStruct.GetMembers(name);
            return members.Length > 0 && members[0] is IFieldSymbol f && !f.IsStatic
                && NativeTranspiler.IsEntJoyContainerNamed(f.Type, Config.NativeArray);
        }

        /// <summary>
        /// 生成 NativeArray 基址：字段 → <c>{name}_ptr</c>；局部指针变量 → <c>{name}</c>。
        /// </summary>
        private string NativeArrayBase(ExpressionSyntax expr, string translated)
        {
            if (expr is IdentifierNameSyntax id)
                return IsNativeArrayField(id) ? $"{translated}_ptr" : translated;
            return translated;
        }
    }
}
