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

            // 非 float/int 元素（byte/sbyte/float2/结构体…）：向量 store 的 intrinsic 签名只覆盖
            //   float*/int*。这里统一走"逐 lane 标量写"，按元素类型正确转换指针，
            //   掩码语义与其他路径一致（不再生成 `(int*)(byte_ptr)[i] = <float2>` 这类非法赋值）。
            bool isNarrowElem = elemType != "float" && elemType != "int";

            // NT-12（Critical）：复合赋值必须"读-改-写"。
            //   若完全忽略 `assign.OperatorToken`，按普通 store 发 `addr = rhs`
            //   ⇒ `m[i] |= x` 变成覆盖写（`+=` / `&=` / `<<=` 等同样中招），静默错值。
            //   分支各写一份复合版本，正确性优先、代价是复合赋值不走向量 intrinsic。
            string compoundOp = assign.OperatorToken.Text;
            if (compoundOp != "=")
            {
                if (IsStructNativeArrayType(elemType))
                    throw new System.NotSupportedException(
                        $"复合赋值不支持结构体元素：`{elemType} {compoundOp}` —— 请拆成两步（先读局部变量、再整体写回）。");
                string elemPtr = $"(({elemType}*)({basePtr}))";
                bool narrowedCmp = _currentMask != "simd_mask::all_true()";
                if (idxKind >= VarKind.Varying)
                {
                    string laneRhs = rhsKind >= VarKind.Varying
                        ? ((elemType == "float" && IsInt32Expr(assign.Right))
                            ? $"n_extract_lane_i2f(({rhsExpr}).v,__l)"
                            : $"{extractFn}(({rhsExpr}).v,__l)")
                        : rhsExpr;
                    string guardCmp = narrowedCmp
                        ? $"if((n_mask_to_bitmask(({_currentMask}).m)&(1<<__l))!=0)"
                        : "";
                    return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{guardCmp}{elemPtr}[n_extract_lane_epi32(({idxExpr}).v,__l)] {compoundOp} {laneRhs};}}}}";
                }
                return $"{elemPtr}[{idxExpr}] {compoundOp} {rhsExpr};";
            }

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
                    // Conditional (if/else) store: when the current mask is narrowed,
                    //   ANY store (contiguous or not, uniform or varying rhs) must be
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
                        // E7 int→float store fix: use n_extract_lane_i2f for numeric conversion
                        //   (extract int lane, convert to float — not bit reinterpretation)
                        string extractExpr = (elemType == "float" && IsInt32Expr(assign.Right))
                            ? $"n_extract_lane_i2f(({rhsSimdExpr}).v,__l)"
                            : $"{extractFn}({rhsSimdExpr}.v,__l)";
                        return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<g_simdWidthInt;__l++){{if(__sg&(1<<__l)){{{basePtr}[n_extract_lane_epi32({idxExpr}.v,__l)]={extractExpr};}}}}}}";
                    }
                    // NT-01(a)：无掩码连续 store 的前提是"索引可证明等于 SIMD 索引变量本身"
                    //   （`v_i`，或 `<uniform> + v_i` 且批内循环变量已知）。在任意 varying
                    //   下标 + 均匀 RHS 时都发 `n_store_ps(basePtr + offset, set1(rhs))`（假定连续），
                    //   例如 `Out[index * 2] = 1.0f;` 会把 [offset, offset+W) 整段写成一个值（静默错值）。
                    //   先做连续性判定，再决定 store 形态；否则回退逐 lane 掩码 scatter。
                    string contBase = null;
                    string contOffset = null;
                    if (!string.IsNullOrEmpty(_batchLoopVar))
                    {
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
                            contOffset = contBase == _batchLoopVar ? contBase : $"({contBase}) + {_batchLoopVar}";
                    }
                    else if (idxExpr == _simdIndexVar && _batchOffsetVar != SimdControlFlowGenerator.UnknownBatchOffset)
                    {
                        // NT-01(b)：批内循环变量未登记（IJobChunk/IJobEntity 的 AutoSIMD 批路径，
                        //   见 CppJobGenerator 的两处 `batchLoopVar: ""`）时索引就是 `v_i` ⇒ 连续，
                        //   但偏移必须是外层批循环的当前位置（调用点传 `si`）。
                        //   传 `"0"` 会让每个 simd 组都重写 [0,W)，元素 ≥W 永远写不到。
                        //
                        // 收尾加固：`UnknownBatchOffset` 是单 IJob 路径（用户循环的起点不属于
                        //   生成器可知的批循环，见 CppJobGenerator.GenerateSingleFunctionStandard）的哨兵 ——
                        //   此时"v_i 相对基址从 0 开始"无法证明 ⇒ 不发无掩码连续 store，退回逐 lane
                        //   scatter（`basePtr[extract(idxExpr,lane)]`，永远按真实下标写）。
                        //   否则任何将来在该路径被向量化的形状都会重演 NT-01(b) 的静默错值。
                        contBase = _batchOffsetVar;
                        contOffset = _batchOffsetVar;
                    }

                    if (contOffset != null)
                    {
                        // when _returnedMaskVar is set (batch body has `return`), use per-lane
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
                        // 均匀 RHS + 连续索引：整宽 set1 + 无掩码 store（真正的向量化路径）
                        if (rhsKind < VarKind.Varying)
                            return $"{storeFnScalar}({basePtr} + {contOffset}, {setFnScalar}({rhsExpr}))";
                        // int→float 跨类型写回：向量转换 + 向量 store（避免 scatter）
                        if (elemType == "float" && IsInt32Expr(assign.Right))
                            return $"{storeFn}({basePtr} + {contOffset}, n_cvtepi32_ps({rhsExpr}.v))";
                        return $"{storeFn}({basePtr} + {contOffset}, {rhsExpr}.v)";
                    }

                    // 非连续 varying 下标：逐 lane scatter。
                    if (rhsKind >= VarKind.Varying)
                        return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{basePtr}[n_extract_lane_epi32({idxExpr}.v,__l)]={(elemType == "float" && IsInt32Expr(assign.Right) ? $"n_extract_lane_i2f(({rhsExpr}).v,__l)" : $"{extractFn}({rhsExpr}.v,__l)")};}}}}";
                    // 均匀标量 RHS + 非连续 varying 下标：把标量写到每个 lane 各自的地址
                    // （旧代码在这里错误地发整宽连续 store）。
                    return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{basePtr}[n_extract_lane_epi32({idxExpr}.v,__l)]={rhsExpr};}}}}";
                }

                // uniform idx + varying rhs -> extract lane 0
                if (rhsKind >= VarKind.Varying)
                {
                    return $"{basePtr}[{idxExpr}] = {extractFn}({rhsExpr}.v, 0)";
                }

                return $"{basePtr}[{idxExpr}] = {rhsExpr}";
        }

        private string EmitStructFieldStore(AssignmentExpressionSyntax assign, MemberAccessExpressionSyntax member,
            ElementAccessExpressionSyntax element, string array, string structure)
        {
            string field = member.Name.Identifier.Text;
            string index = TranslateExpression(element.ArgumentList.Arguments[0].Expression);
            string rhs = TranslateExpression(assign.Right);
            string op = assign.OperatorToken.Text;
            string fieldType = StructFieldType(member, structure, field);
            if (_varAnalyzer.ClassifyExpression(element.ArgumentList.Arguments[0].Expression) < VarKind.Varying)
                return $"{array}_ptr[{index}].{field} {op} {rhs}";

            bool integer = fieldType == "int" || fieldType == "unsigned int" || fieldType == "EntJoy::Mathematics::int2";
            bool pair = fieldType == "EntJoy::Mathematics::float2" || fieldType == "EntJoy::Mathematics::int2";
            if (!integer && !pair && fieldType != "float")
                return $"/*{UnsupportedMarkers.Expr}SIMD_StructFieldStore*/ 0";
            string extract = integer ? "n_extract_lane_epi32" : "n_extract_lane_f32";
            string laneValue = rhs;
            if (_varAnalyzer.ClassifyExpression(assign.Right) >= VarKind.Varying)
                laneValue = pair
                    ? $"{fieldType}{{{extract}(({rhs}).x.v,__l),{extract}(({rhs}).y.v,__l)}}"
                    : fieldType == "float" && IsInt32Expr(assign.Right)
                        ? $"n_extract_lane_i2f(({rhs}).v,__l)" : $"{extract}(({rhs}).v,__l)";
            string target = $"{array}_ptr[n_extract_lane_epi32(({index}).v,__l)].{field}";
            string write = fieldType == "int" && op is "+=" or "-=" or "*="
                ? $"{target} = (int)((unsigned){target} {op[0]} (unsigned)({laneValue}));"
                : $"{target} {op} {laneValue};";
            string guard = _currentMask == "simd_mask::all_true()" ? ""
                : $"if(n_mask_to_bitmask(({_currentMask}).m)&(1<<__l))";
            return $"{{for(int __l=0;__l<g_simdWidthInt;__l++){{{guard}{{{write}}}}}}}";
        }

        private string TranslateAssignment(AssignmentExpressionSyntax assign)
        {
            // UnsafeUtility.ArrayElementAsRef<T>(ptr, i) = value
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

            // Struct NativeArray field assignment: structArray[idx].field = rhs
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
                    return EmitStructFieldStore(assign, ma, ea2, arrName2, saElemType);
                }
            }

            // Deferred struct local field assignment: structLocal.field = rhs
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

            // Struct field sub-field assignment: array[idx].field1.field2 = rhs
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

            string lhs = TranslateExpression(assign.Left);
            string rhs = TranslateExpression(assign.Right);
            string op = assign.OperatorToken.Text;
            if (assign.Left is IdentifierNameSyntax booleanTarget
                && _variables.TryGetValue(booleanTarget.Identifier.Text, out var booleanInfo)
                && booleanInfo.Kind >= VarKind.Varying && booleanInfo.CppType == "bool")
                rhs = TranslateVaryingValue(assign.Right, booleanInfo.CppType);

            // struct-varying local 重赋值刷新：structLocal = array[other_idx] 后，
            //   _structVaryingLocals 里的 (arrName, elemType, indexExpr) 必须同步更新，
            //   否则后续 structLocal.field 访问仍用旧 indexExpr → gather/scatter 地址错误。
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

            // 向量复合赋值复用普通运算；局部值在收窄掩码下保留未激活 lane。
            if (assign.Left is IdentifierNameSyntax lhsId
                && _variables.TryGetValue(lhsId.Identifier.Text, out var vector) && vector.Kind >= VarKind.Varying)
            {
                string value = op == "=" ? rhs : $"{lhs} {op.Substring(0, op.Length - 1)} ({rhs})";
                return _currentMask == "simd_mask::all_true()"
                    ? $"{lhs} = {value}" : $"{lhs} = blend({lhs}, {value}, {_currentMask})";
            }

            return $"{lhs} {op} {rhs}";
        }
    }
}
