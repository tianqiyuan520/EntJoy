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

        // E7 helpers: check C# expression type via SemanticModel.
        //   Used to detect int expressions being stored into float arrays.
        private bool IsInt32Type(ExpressionSyntax expr)
        {
            var type = SymbolHelper.GetBoundType(_semanticModel, expr);
            return type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_UInt32;
        }
        private bool IsInt32Expr(ExpressionSyntax expr) => IsInt32Type(expr);

        // 「无法向量化」的判据已抽到 SimdVectorizability.HasNonVectorizableCall（唯一一份实现：
        // 发射侧与校验侧共用；校验侧命中即报 NT031，让"以为开了向量化、实际跑 per-lane 标量"不再静默）。

        /// <summary>裸指针变量（局部/形参）指向的元素 C++ 类型；不是裸指针返回 null。</summary>
        private string PointerElemCpp(string name)
        {
            if (_varAnalyzer.LocalPointerElemCpp.TryGetValue(name, out var le)) return le;
            if (_varAnalyzer.ParamPointerElemCpp.TryGetValue(name, out var pe)) return pe;
            return null;
        }

        // Variable Declarations

        private void GenerateVariableDeclarations()
        {
            foreach (var kvp in _variables)
            {
                string name = kvp.Key;
                var info = kvp.Value;

                // Skip index parameter (it maps to v_i from outer loop)
                if (name == _indexParamName) continue;
                if (_forLoopVars.Contains(name)) continue;

                // Skip uniform variables (handled as scalar by outer scope)
                if (info.Kind == VarKind.Uniform) continue;

                // Varying or Reduction
                string varType = GetSIMDTypeString(info.CppType);
                if (varType == null) continue;

                // Default construct: all variables are immediately overwritten
                // (v_q=gather, v_cell=convert, v_bestDistSq=max, etc.)
                // broadcast(0) was wasted instructions & register pressure.
                if (info.InitSIMDExpr != null)
                {
                    AppendLine($"{varType} v_{name} = {info.InitSIMDExpr};");
                    _varDeclEmitted.Add(name);
                }
                else
                {
                    // Scope narrowing: defer declaration to first assignment site
                    // (reduces live-range overlap -> less register pressure -> better stability)
                }
                // Track for per-lane save/merge
                _simdVaryingVarNames.Add(name);
                _simdVaryingCppType[name] = info.CppType;
            }
        }

        // Uniform Broadcast Hoisting (generic, no hardcoded field names)

        /// <summary>
        /// Pre-scan body for .x/.y access on uniform int2/float2 job struct fields.
        /// These should be pre-broadcast once and reused, instead of re-broadcast at each use.
        /// Fully generic: applies to ANY job with ANY uniform int2/float2 fields.
        /// </summary>
        private void PreScanUniformHoists(SyntaxNode body)
        {
            _uniformHoistMap.Clear();
            _uniformHoistPending.Clear();

            foreach (var ma in body.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                string memberName = ma.Name.Identifier.Text;
                if (memberName != "x" && memberName != "y") continue;
                if (!(ma.Expression is IdentifierNameSyntax id)) continue;

                string fieldName = id.Identifier.Text;
                if (_jobStruct == null) continue;

                try
                {
                    var members = _jobStruct.GetMembers(fieldName);
                    if (members.Length == 0) continue;
                    if (!(members[0] is IFieldSymbol field) || field.IsStatic) continue;

                    string typeName = field.Type.Name;
                    string simdType, key;
                    if (typeName == "int2")
                    {
                        simdType = "simd_value<int>";
                        key = $"{fieldName}.{memberName}";
                    }
                    else if (typeName == "float2")
                    {
                        simdType = "simd_value<float>";
                        key = $"{fieldName}.{memberName}";
                    }
                    else continue;

                    if (_uniformHoistMap.ContainsKey(key)) continue;

                    string varName = $"__uni_{fieldName}_{memberName}";
                    _uniformHoistMap[key] = varName;
                    _uniformHoistPending[varName] = $"{simdType} {varName} = {simdType}::broadcast({fieldName}.{memberName}());";
                }
                catch { }
            }
        }

        /// <summary>Emit all hoisted uniform broadcasts in the prologue</summary>
        private void EmitUniformHoistPrologue()
        {
            if (_uniformHoistPending.Count == 0) return;
            AppendLine("// Hoisted uniform broadcasts");
            foreach (var kvp in _uniformHoistPending)
                AppendLine(kvp.Value);
        }

        // Local Declaration

        /// <summary>初始化式是否是钳位表达式（clamp / min(max(…), …)）：这类索引已保证在界内，后续 gather 不再钳位。</summary>
        private static bool IsClampingExpression(ExpressionSyntax? expr)
        {
            if (expr is not InvocationExpressionSyntax invocation) return false;
            string name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                _ => ""
            };
            if (name is "clamp" or "Clamp") return true;
            if (name is not ("min" or "Min")) return false;
            foreach (var argument in invocation.ArgumentList.Arguments)
                if (argument.Expression is InvocationExpressionSyntax inner
                    && inner.Expression is MemberAccessExpressionSyntax innerMember
                    && innerMember.Name.Identifier.Text is "max" or "Max")
                    return true;
            return false;
        }
        private void GenerateLocalDeclaration(LocalDeclarationStatementSyntax stmt)
        {
            foreach (var variable in stmt.Declaration.Variables)
            {
                string name = variable.Identifier.Text;

                                // [Priority] Struct-typed local initialized from chunk array element access.
                //   e.g., MoveVelocity velocity = velocities[index];
                //   → defer to _structVaryingLocals for field-level decomposition.
                //   Check BEFORE the general Varying check because the variable analyzer
                //   may classify it as Varying (via PropagateAssignments from the index)
                //   but with wrong CppType ("int" for unknown struct types).
                if (variable.Initializer?.Value is ElementAccessExpressionSyntax structInitEA
                    && structInitEA.Expression is IdentifierNameSyntax structInitArrId
                    && _nativeArrayParams.TryGetValue(structInitArrId.Identifier.Text, out var structInitElemType)
                    && structInitElemType != "float" && structInitElemType != "int"
                    && !SimdVectorTypes.IsFloat2(structInitElemType) && !SimdVectorTypes.IsInt2(structInitElemType))
                {
                    ExpressionSyntax? idxSyntax = structInitEA.ArgumentList?.Arguments.Count > 0
                        ? structInitEA.ArgumentList.Arguments[0].Expression
                        : null;
                    string idxExpr = idxSyntax != null ? TranslateExpression(idxSyntax) : "0";
                    bool varyingIndex = idxSyntax != null && _varAnalyzer.ClassifyExpression(idxSyntax) >= VarKind.Varying;
                    _structVaryingLocals[name] = (structInitArrId.Identifier.Text, structInitElemType, idxExpr, varyingIndex);
                    continue;
                }

                if (_variables.TryGetValue(name, out var info) && info.Kind >= VarKind.Varying)
                {
                    string varType = GetSIMDTypeString(info.CppType);
                    if (varType == null) continue;

                    if (variable.Initializer != null)
                    {
                        string initExpr = TranslateVaryingValue(variable.Initializer.Value, info.CppType);
                        // uint2 用 int2 寄存器承载：标量 pair 值（如数组元素）要显式转成 int2，
                        // 否则 uint2 → int2 → simd_value<int2> 需要两次用户转换，clang 拒绝。
                        if (SimdVectorTypes.IsUint2(info.CppType)
                            && SimdExprShapes.Of(initExpr) == SimdExprTraits.None)
                            initExpr = $"simd_value<EntJoy::Mathematics::int2>(EntJoy::Mathematics::int2({initExpr}))";
                        {
                            _varDeclEmitted.Add(name);
                            _simdVaryingVarNames.Add(name);
                            _simdVaryingCppType[name] = info.CppType;
                            if (IsClampingExpression(variable.Initializer?.Value))
                                _clampedVars.Add(name);
                            AppendLine($"{varType} v_{name} = {initExpr};");
                        }
                    }
                }
                else
                {
                    var boundType = SymbolHelper.GetBoundType(_semanticModel, stmt.Declaration.Type);
                    string cppType = boundType != null ? NativeTranspiler.MapCSharpTypeToCpp(boundType)
                        : _variables.TryGetValue(name, out var local) ? local.CppType : "float";

                    if (variable.Initializer != null)
                    {
                        string initExpr = TranslateExpression(variable.Initializer.Value);
                        // ISPC-style SIMD promotion: when a Uniform local is initialized from a
                        //   SIMD register expression (e.g. gather result), promote to Varying and
                        //   This keeps the data in SIMD registers throughout the computation chain.
                        bool isSimdExpr = _varAnalyzer.ClassifyExpression(variable.Initializer.Value) >= VarKind.Varying
                            && SimdExprShapes.Of(initExpr) != SimdExprTraits.None;
                        if (isSimdExpr)
                        {
                            string simdType = GetSIMDTypeString(cppType);
                            if (simdType != null)
                            {
                                _varDeclEmitted.Add(name);
                                _simdVaryingVarNames.Add(name);
                                _simdVaryingCppType[name] = cppType;
                                if (!_variables.ContainsKey(name))
                                    _variables[name] = new SimdVariableInfo { Name = name, Kind = VarKind.Varying, CppType = cppType };
                                else
                                    _variables[name].Kind = VarKind.Varying;
                                AppendLine($"{simdType} v_{name} = {initExpr};");
                                continue;
                            }
                        }
                        AppendLine($"{cppType} {name} = {initExpr};");
                    }
                    else
                    {
                        AppendLine($"{cppType} {name};");
                    }
                }
            }
        }
    }
}
