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
                    && !structInitElemType.Contains("float2") && !structInitElemType.Contains("int2"))
                {
                    string idxExpr = structInitEA.ArgumentList?.Arguments.Count > 0
                        ? TranslateExpression(structInitEA.ArgumentList.Arguments[0].Expression)
                        : "0";
                    _structVaryingLocals[name] = (structInitArrId.Identifier.Text, structInitElemType, idxExpr);
                    continue;
                }

                if (_variables.TryGetValue(name, out var info) && info.Kind >= VarKind.Varying)
                {
                    string varType = GetSIMDTypeString(info.CppType);
                    if (varType == null) continue;

                    if (variable.Initializer != null)
                    {
                        string initExpr = TranslateVaryingValue(variable.Initializer.Value, info.CppType);
                        {
                            _varDeclEmitted.Add(name);
                            _simdVaryingVarNames.Add(name);
                            _simdVaryingCppType[name] = info.CppType;
                            if (initExpr.Contains("simd_min") && initExpr.Contains("simd_max"))
                                _clampedVars.Add($"v_{name}");
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
                        bool isSimdExpr = initExpr.StartsWith("simd_")
                            || initExpr.StartsWith("(simd_")
                            || initExpr.StartsWith("(v_")
                            || (initExpr.Contains("simd_value<") && initExpr.Contains("n_"))
                            || initExpr.Contains("n_gather_ps<")
                            || initExpr.Contains("n_sin_ps")
                            || initExpr.Contains("n_cos_ps")
                            || initExpr.Contains("n_sqrt_ps");
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
