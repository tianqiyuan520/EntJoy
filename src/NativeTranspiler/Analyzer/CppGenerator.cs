using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public static class CppGenerator
    {
        private static readonly HashSet<string> SkipIncludeTypeNames = new()
        {
            "EntJoy.Mathematics.math",
            "EntJoy.Collections.UnsafeUtility",
            "EntJoy.Hint"
        };

        public static string GetCppFunctionName(IMethodSymbol method)
        {
            var containingNamespace = method.ContainingNamespace?.ToDisplayString() ?? "";
            var typePath = SymbolHelper.BuildFullTypePath(method.ContainingType);
            var methodName = method.Name;
            var safeNamespace = SymbolHelper.Sanitize(containingNamespace);
            var safeTypePath = SymbolHelper.Sanitize(typePath);
            var safeMethod = SymbolHelper.Sanitize(methodName);
            return $"SharpNative_{safeNamespace}_{safeTypePath}_{safeMethod}";
        }

        public static string GenerateHeader(IMethodSymbol method)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine();
            sb.AppendLine("#include \"../../NativeDll/NativeContainers.h\"");
            sb.AppendLine("#include \"../../NativeDll/NativeMath.h\"");
            sb.AppendLine("#include <cstddef>");
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateAtomicMacros());
            sb.AppendLine();
            sb.AppendLine(GenerateCppFunctionSignature(method, fullyQualified: true) + ";");
            return sb.ToString();
        }

    public static string GenerateImplementation(IMethodSymbol method, Compilation compilation,
        HashSet<INamedTypeSymbol>? userStructs = null,
        NativeTranspiler.AutoSIMD autoSIMD = NativeTranspiler.AutoSIMD.Disabled)
        {
            var sb = new StringBuilder();
            var functionName = GetCppFunctionName(method);
            sb.AppendLine($"#include \"{functionName}.h\"");

            var dependencies = CollectCalledStaticMethods(method, compilation);
            foreach (var dep in dependencies)
            {
                var depFuncName = GetCppFunctionName(dep);
                sb.AppendLine($"#include \"{depFuncName}.h\"");
            }

            // 为用户自定义结构体添加 include
            if (userStructs != null)
            {
                foreach (var us in userStructs)
                {
                    var headerName = NativeTranspiler.GetStructHeaderFileName(us);
                    sb.AppendLine($"#include \"{headerName}.h\"");
                }
            }

            sb.AppendLine("#include <algorithm>");
            sb.AppendLine("#include <cstdio>");

            var methodSyntax = SymbolHelper.GetMethodSyntax(method);

            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
            {
                sb.AppendLine("#include \"../../NativeDll/NativeSIMD.h\"");
                sb.AppendLine("#include \"../../NativeDll/SimdValue.h\"");
            }
            else
                sb.AppendLine("#include <cmath>");

            sb.AppendLine();
            sb.AppendLine(GenerateCppFunctionSignature(method, fullyQualified: true));
            sb.AppendLine("{");

            // 1. 仅保留 NativeList 的引用声明，NativeArray 不生成任何局部变量
            foreach (var param in method.Parameters.Where(p => NativeTranspiler.IsEntJoyNativeContainerType(p.Type)))
            {
                if (param.Type.Name == "NativeList")
                {
                    var elementType = ((INamedTypeSymbol)param.Type).TypeArguments[0];
                    var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                    sb.AppendLine($"    EntJoy::Collections::UnsafeList<{cppElementType}>& {param.Name} = *{param.Name}_listData;");
                }
                // NativeArray: nothing to declare
            }

            // 2. 为普通值类型参数创建局部引用（跳过容器和指针类型），移除 const
            foreach (var param in method.Parameters)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(param.Type)) continue;
                if (param.Type is IPointerTypeSymbol) continue;
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                sb.AppendLine($"    {cppType}& {param.Name} = *{param.Name}_ptr;");
            }

            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                bool useFastMath = AttributeHelper.HasFastCppMathLib(method,
                    compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute"));

                if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                {
                    string simdCode = GenerateSimdViaCFG(method, methodSyntax.Body, semanticModel, useFastMath);
                    sb.Append(simdCode);
                }
                else
                {
                    var translator = new CppPointerStatementTranslator(semanticModel, method, useFastMath);
                    var bodyCode = translator.Translate(methodSyntax.Body);
                    sb.Append(bodyCode);
                }
            }
            else
            {
                sb.AppendLine("    // TODO: Translate method body");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }



        private static int GetStrideCoeff(ExpressionSyntax expr, string var)
        {
            if (expr is IdentifierNameSyntax id)
                return id.Identifier.Text == var ? 1 : 0;
            if (expr is LiteralExpressionSyntax) return 0;
            if (expr is BinaryExpressionSyntax bin)
            {
                int left = GetStrideCoeff(bin.Left, var);
                int right = GetStrideCoeff(bin.Right, var);
                if (bin.OperatorToken.Text == "*")
                {
                    if (left > 0 && bin.Right is LiteralExpressionSyntax rLit)
                        return left * (int)(rLit.Token.Value ?? 0);
                    if (right > 0 && bin.Left is LiteralExpressionSyntax lLit)
                        return right * (int)(lLit.Token.Value ?? 0);
                    return left * right;
                }
                if (bin.OperatorToken.Text == "+" || bin.OperatorToken.Text == "-")
                    return bin.OperatorToken.Text == "+" ? left + right : left - right;
            }
            if (expr is ParenthesizedExpressionSyntax paren)
                return GetStrideCoeff(paren.Expression, var);
            if (expr is CastExpressionSyntax cast)
                return GetStrideCoeff(cast.Expression, var);
            return 0;
        }

        private static string PickBestVectorVar(List<string> loopVars, BlockSyntax body,
            Dictionary<string, string> nativeArrayParams)
        {
            if (loopVars.Count <= 1) return loopVars.FirstOrDefault();
            var accesses = body.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
                .Where(ea => ea.Expression is IdentifierNameSyntax id
                    && nativeArrayParams.ContainsKey(id.Identifier.Text)).ToList();
            if (accesses.Count == 0) return loopVars[0];
            var sums = new Dictionary<string, int>();
            foreach (var v in loopVars) sums[v] = 0;
            foreach (var ea in accesses)
            {
                if (ea.ArgumentList == null || ea.ArgumentList.Arguments.Count == 0) continue;
                var firstArg = ea.ArgumentList.Arguments[0].Expression;
                foreach (var v in loopVars)
                    sums[v] += GetStrideCoeff(firstArg, v);
            }
            var valid = sums.Where(kv => kv.Value > 0).ToList();
            if (valid.Count == 0) return loopVars[0];
            bool hasIndirect = body.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
                .Any(ea => ea.Expression is IdentifierNameSyntax id2
                    && nativeArrayParams.ContainsKey(id2.Identifier.Text)
                    && ea.ArgumentList?.Arguments.Count > 0
                    && ea.ArgumentList.Arguments[0].Expression is ElementAccessExpressionSyntax);
            if (hasIndirect) return loopVars[0];
            return valid.OrderBy(kv => kv.Value).First().Key;
        }

        private static string GenerateSimdViaCFG(IMethodSymbol method, BlockSyntax body,
            SemanticModel semanticModel, bool useFastMath)
        {
            var forStmt = body.Statements.OfType<ForStatementSyntax>().FirstOrDefault();
            if (forStmt == null) return FallbackScalarTranslation(method, body, semanticModel, useFastMath);
            string outerVar = forStmt.Declaration.Variables[0].Identifier.Text;
            var limitExpr = ((BinaryExpressionSyntax)forStmt.Condition).Right;
            string limitStr = limitExpr.GetText().ToString().Trim();
            var nap = new Dictionary<string, string>();
            foreach (var param in method.Parameters)
                if (NativeTranspiler.IsEntJoyNativeContainerType(param.Type) && param.Type.Name == "NativeArray")
                {
                    var ta = ((INamedTypeSymbol)param.Type).TypeArguments.FirstOrDefault();
                    nap[param.Name] = ta != null ? NativeTranspiler.MapCSharpTypeToCpp(ta) : "float";
                }
            var innerFor = (forStmt.Statement is BlockSyntax ob)
                ? ob.Statements.OfType<ForStatementSyntax>().FirstOrDefault() : null;
            if (innerFor != null)
            {
                string iVar = innerFor.Declaration.Variables[0].Identifier.Text;
                string iLim = ((BinaryExpressionSyntax)innerFor.Condition).Right.GetText().ToString().Trim();
                if (int.TryParse(iLim, out int ib) && ib <= 512)
                {
                    var ibody = innerFor.Statement is BlockSyntax ibs ? ibs
                        : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(new SyntaxList<StatementSyntax>(innerFor.Statement));
                    // Only apply inner vectorization when there's a single input array (simple reduction like Reduce)
                    var readArrays = ibody.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
                        .Where(ea => ea.Expression is IdentifierNameSyntax rid && nap.ContainsKey(rid.Identifier.Text)
                            && !(ea.Parent is AssignmentExpressionSyntax aes && aes.Left == ea))
                        .Select(ea => ((IdentifierNameSyntax)ea.Expression).Identifier.Text).Distinct().ToList();
                    if (readArrays.Count == 1 && PickBestVectorVar(new List<string>{outerVar,iVar}, ibody, nap) == iVar)
                        return GenerateVectorizedInnerLoop(forStmt, innerFor, ibody, forStmt.Statement, nap, semanticModel);
                }
            }
            return GenerateBatchLoopSIMD(method, forStmt, nap, semanticModel);
        }

        private static string GenerateBatchLoopSIMD(IMethodSymbol method, ForStatementSyntax forStmt,
            Dictionary<string, string> nap, SemanticModel semanticModel)
        {
            var sb = new StringBuilder();
            string idx = forStmt.Declaration.Variables[0].Identifier.Text;
            string lim = ((BinaryExpressionSyntax)forStmt.Condition).Right.GetText().ToString().Trim();
            BlockSyntax ib = forStmt.Statement is BlockSyntax bs ? bs
                : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(new SyntaxList<StatementSyntax>(forStmt.Statement));
            var ms = SymbolHelper.GetMethodSyntax(method);
            var va2 = new SimdVariableAnalyzer(semanticModel, null, idx);
            var vars = va2.Analyze(ms);
            if (vars.TryGetValue(idx, out var ii)) ii.Kind = VarKind.Varying;
            else vars[idx] = new SimdVariableInfo { Name = idx, Kind = VarKind.Varying, CppType = "int" };
            var sg = new SimdControlFlowGenerator(semanticModel, null, vars, va2,
                indexParamName: idx, simdIndexVar: "v_i", batchOffsetVar: "0",
                simdMathPrecision: NativeTranspiler.SimdMathPrecision.Fastest,
                nativeArrayParams: nap, batchLoopVar: "si");
            sb.AppendLine(string.Format("    int vec_count = (({0}) / NSIMD_WIDTH) * NSIMD_WIDTH;", lim));
            sb.AppendLine("    simd_value<int> v_base = simd_value<int>::sequence(0);");
            sb.AppendLine("    if (vec_count > 0) {");
            sb.AppendLine("        for (int si = 0; si < vec_count; si += NSIMD_WIDTH) {");
            sb.AppendLine("            simd_value<int> v_i = v_base + si;");
            foreach (var line in sg.Generate(ib).Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine("            " + line.TrimEnd());
            sb.AppendLine("        __simd_exit: ; } }");
            sb.AppendLine();
            var rm = new Dictionary<string, string>();
            foreach (var kv in nap) rm[string.Format("{0}[", kv.Key)] = string.Format("{0}_ptr[", kv.Key);
            rm["MathF.Sin("] = "::sinf("; rm["MathF.Cos("] = "::cosf(";
            rm["MathF.Sqrt("] = "::sqrtf("; rm["MathF.Log("] = "::logf(";
            rm["MathF.Log10("] = "::log10f("; rm["MathF.Exp("] = "::expf(";
            rm["MathF.Abs("] = "::fabsf("; rm["float.MaxValue"] = "3.402823466e+38f";
            sb.AppendLine(string.Format("    for (int {0} = vec_count; {0} < {1}; {0}++)", idx, lim));
            sb.AppendLine("    {");
            foreach (var stmt in (forStmt.Statement is BlockSyntax ? ib.Statements : new SyntaxList<StatementSyntax>(forStmt.Statement)))
            {
                string l = stmt.GetText().ToString().Trim();
                foreach (var kv in rm) l = l.Replace(kv.Key, kv.Value);
                sb.AppendLine(string.Format("    {0}", l));
            }
            sb.AppendLine("    }");
            return sb.ToString();
        }

        private static string GenerateVectorizedInnerLoop(ForStatementSyntax ofs,
            ForStatementSyntax ifs, BlockSyntax ibody, StatementSyntax outerBodyStmt,
            Dictionary<string, string> nap, SemanticModel semanticModel)
        {
            var sb = new StringBuilder();
            string ov = ofs.Declaration.Variables[0].Identifier.Text;
            string ol = ((BinaryExpressionSyntax)ofs.Condition).Right.GetText().ToString().Trim();
            string iv = ifs.Declaration.Variables[0].Identifier.Text;
            string il = ((BinaryExpressionSyntax)ifs.Condition).Right.GetText().ToString().Trim();
            string ra = null;
            var ias = new HashSet<string>();
            // Scan for result array and input arrays (type-agnostic)
            foreach (var ea in (outerBodyStmt as BlockSyntax ?? outerBodyStmt).DescendantNodes().OfType<ElementAccessExpressionSyntax>())
                if (ea.Expression is IdentifierNameSyntax id && nap.ContainsKey(id.Identifier.Text)
                    && ea.Parent is AssignmentExpressionSyntax aes && aes.Left == ea)
                    ra = id.Identifier.Text;
            foreach (var ea in ibody.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
                if (ea.Expression is IdentifierNameSyntax id && nap.ContainsKey(id.Identifier.Text)
                    && !(ea.Parent is AssignmentExpressionSyntax aes && aes.Left == ea))
                    ias.Add(id.Identifier.Text);

            // Detect reduction type: min (v < best) or max (v > best)
            string reduceFn = "n_min_ps";
            string initVal = "3.402823466e+38f";
            string cmpOp = "<";
            foreach (var ifStmt in ibody.DescendantNodes().OfType<IfStatementSyntax>())
            {
                if (ifStmt.Condition is BinaryExpressionSyntax cond && cond.OperatorToken.Text == "<")
                { reduceFn = "n_min_ps"; initVal = "3.402823466e+38f"; cmpOp = "<"; break; }
                if (ifStmt.Condition is BinaryExpressionSyntax cond2 && cond2.OperatorToken.Text == ">")
                { reduceFn = "n_max_ps"; initVal = "-3.402823466e+38f"; cmpOp = ">"; break; }
            }

            // Build type-aware load/reduce/store per array

            sb.AppendLine(string.Format("    for (int {0} = 0; {0} < {1}; {0}++) {{", ov, ol));
            sb.AppendLine(string.Format("        n_float v_best = n_set1_ps({0});", initVal));
            sb.AppendLine(string.Format("        int base = {0} * {1};", ov, il));
            sb.AppendLine(string.Format("        for (int {0} = 0; {0} < {1}; {0} += NSIMD_WIDTH) {{", iv, il));
            foreach (var arr in ias)
                sb.AppendLine(string.Format("            v_best = {0}(v_best, n_load_ps({1}_ptr + base + {2}));", reduceFn, arr, iv));
            sb.AppendLine("        }");
            sb.AppendLine("        float lane[NSIMD_WIDTH]; n_store_ps(lane, v_best);");
            sb.AppendLine("        float h = lane[0];");
            sb.AppendLine("        for (int i = 1; i < NSIMD_WIDTH; i++)");
            sb.AppendLine(string.Format("            if (lane[i] {0} h) h = lane[i];", cmpOp));
            if (ra != null)
                sb.AppendLine(string.Format("        {0}_ptr[{1}] = h;", ra, ov));
            sb.AppendLine("    }");
            return sb.ToString();
        }


        /// <summary>Fallback: translate entire body as scalar C++.</summary>
        private static string FallbackScalarTranslation(IMethodSymbol method, BlockSyntax body,
            SemanticModel semanticModel, bool useFastMath)
        {
            var translator = new CppPointerStatementTranslator(semanticModel, method, useFastMath);
            return translator.Translate(body);
        }

        private static string GenerateCppFunctionSignature(IMethodSymbol method, bool fullyQualified)
        {
            var returnType = NativeTranspiler.MapCSharpTypeToCpp(method.ReturnType);
            // MapCSharpTypeToCpp 已处理指针类型（追加 *），此处只需处理引用类型返回指针
            if (!(method.ReturnType is IPointerTypeSymbol) &&
                method.ReturnType.SpecialType != SpecialType.System_Void &&
                !method.ReturnType.IsValueType) returnType += "*";

            var funcName = fullyQualified ? GetCppFunctionName(method) : method.Name;
            var parameters = new List<string>();
            foreach (var p in method.Parameters)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(p.Type))
                {
                    if (p.Type.Name == "NativeList")
                    {
                        var elementType = ((INamedTypeSymbol)p.Type).TypeArguments[0];
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        parameters.Add($"EntJoy::Collections::UnsafeList<{cppElementType}>* RESTRICT {p.Name}_listData");
                    }
                    else // NativeArray
                    {
                        var elementType = ((INamedTypeSymbol)p.Type).TypeArguments[0];
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        parameters.Add($"{cppElementType}* RESTRICT {p.Name}_ptr, int {p.Name}_length");
                    }
                }
                else if (p.Type is IPointerTypeSymbol)
                {
                    // ★ 修改：不再添加多余的 *，MapCSharpTypeToCpp 已返回带 * 的类型
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(p.Type);
                    parameters.Add($"{cppType} RESTRICT {p.Name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(p.Type);
                    parameters.Add($"{cppType}* RESTRICT {p.Name}_ptr");
                }
            }
            string paramStr = string.Join(", ", parameters);
            if (fullyQualified)
                return $"HEAD {returnType} CALLINGCONVENTION {funcName}({paramStr})";
            else
                return $"{returnType} {funcName}({paramStr})";
        }

        private static IEnumerable<IMethodSymbol> CollectCalledStaticMethods(IMethodSymbol method, Compilation compilation)
        {
            var calledMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var methodSyntax = SymbolHelper.GetMethodSyntax(method);
            if (methodSyntax?.Body == null) return calledMethods;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var node in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(node);
                if (symbolInfo.Symbol is IMethodSymbol calledMethod && calledMethod.IsStatic)
                {
                    var containingTypeFullName = calledMethod.ContainingType?.ToDisplayString();
                    if (containingTypeFullName != null && SkipIncludeTypeNames.Contains(containingTypeFullName)) continue;
                    if (SymbolEqualityComparer.Default.Equals(calledMethod.ContainingAssembly, compilation.Assembly))
                        calledMethods.Add(calledMethod);
                }
            }
            return calledMethods;
        }

    }
}
