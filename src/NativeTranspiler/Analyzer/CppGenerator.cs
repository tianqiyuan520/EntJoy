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

    // Map C# operators to n_xxx_ps SIMD functions
    private static string OpToSimdFn(string op) => op switch
    {
        "+" => "n_add_ps", "-" => "n_sub_ps", "*" => "n_mul_ps", "/" => "n_div_ps",
        ">" => "n_cmp_gt_ps", "<" => "n_cmp_lt_ps", ">=" => "n_cmp_ge_ps", "<=" => "n_cmp_le_ps",
        _ => null
    };

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
            bool hasSimdMath = false;
            bool hasSimdIfElse = false;

            // When AutoSIMD is enabled, detect SIMD-izable patterns in the method body.
            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled && methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                hasSimdMath = DetectSimdMathCalls(methodSyntax.Body, semanticModel);
                hasSimdIfElse = DetectSimdIfElse(methodSyntax.Body);
            }

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

                if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled && (hasSimdMath || hasSimdIfElse))
                {
                    // Generate SIMD vector code with branchless blend for if/else + SLEEF math
                    string simdCode = GenerateSimdStaticCode(method, methodSyntax.Body, semanticModel, useFastMath);
                    sb.Append(simdCode);
                }
                else
                {
                    // Scalar code path
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

        /// <summary>
        /// Detect if the method body contains MathF.Sin/Cos/Sqrt/Log calls that need SLEEF.
        /// </summary>
        private static bool DetectSimdMathCalls(BlockSyntax body, SemanticModel semanticModel)
        {
            foreach (var invoc in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var sym = semanticModel.GetSymbolInfo(invoc).Symbol as IMethodSymbol;
                if (sym != null && IsMathFMethod(sym))
                    return true;
            }
            return false;
        }

        /// <summary>Detect if the for-loop body has if/else patterns suitable for SIMD.</summary>
        private static bool DetectSimdIfElse(BlockSyntax body)
        {
            var forStmt = body.Statements.OfType<ForStatementSyntax>().FirstOrDefault();
            if (forStmt == null) return false;
            var loopBody = forStmt.Statement is BlockSyntax bs ? bs.Statements
                : new SyntaxList<StatementSyntax>(forStmt.Statement);
            return loopBody.Any(s => s is IfStatementSyntax);
        }

        private static bool IsMathFMethod(IMethodSymbol method)
        {
            string? ns = method.ContainingType?.ToDisplayString();
            if (ns != "System.MathF" && ns != "System.Math") return false;
            return method.Name switch
            {
                "Sin" or "Cos" or "Sqrt" or "Log" or "Log10" or "Exp" or "Pow" or "Tan" => true,
                _ => false
            };
        }

        /// <summary>
        /// Generate SIMD vector code for static methods with AutoSIMD.
        /// Handles: MathF calls (sin/cos/sqrt/log), if/else branchless blend, simple arithmetic.
        /// </summary>
        private static string GenerateSimdStaticCode(IMethodSymbol method, BlockSyntax body,
            SemanticModel semanticModel, bool useFastMath)
        {
            var sb = new StringBuilder();

            var forStmt = body.Statements.OfType<ForStatementSyntax>().FirstOrDefault();
            if (forStmt == null) return FallbackScalarTranslation(method, body, semanticModel, useFastMath);

            string indexName = forStmt.Declaration.Variables[0].Identifier.Text;
            var limitExpr = ((BinaryExpressionSyntax)forStmt.Condition).Right;
            var loopBody = forStmt.Statement is BlockSyntax bs ? bs.Statements : new SyntaxList<StatementSyntax>(forStmt.Statement);
            string limitStr = limitExpr.GetText().ToString().Trim();

            // Gather array parameter names, local float variables, and scalar float params
            var arrayParams = new HashSet<string>();
            var floatParams = new HashSet<string>();
            foreach (var param in method.Parameters)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(param.Type))
                    arrayParams.Add(param.Name);
                else if (param.Type.SpecialType == SpecialType.System_Single)
                    floatParams.Add(param.Name);
            }

            var floatLocals = new HashSet<string>();
            foreach (var stmt in loopBody)
            {
                if (stmt is LocalDeclarationStatementSyntax localDecl)
                {
                    foreach (var v in localDecl.Declaration.Variables)
                    {
                        var type = semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
                        if (type != null && type.SpecialType == SpecialType.System_Single)
                            floatLocals.Add(v.Identifier.Text);
                    }
                }
            }

            sb.AppendLine($"    int vec_count = (count / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine();

            // SIMD main loop
            sb.AppendLine("    for (int si = 0; si < vec_count; si += NSIMD_WIDTH)");
            sb.AppendLine("    {");
            foreach (var stmt in loopBody)
            {
                if (stmt is IfStatementSyntax ifStmt)
                {
                    // Branchless SIMD: compute all branches + blend with masks
                    string ifCode = GenSimdIfElse(ifStmt, indexName, arrayParams, floatLocals, semanticModel);
                    sb.Append(ifCode);
                }
                else
                {
                    string? simdLine = GenSimdStmt(stmt, indexName, arrayParams, floatLocals, semanticModel);
                    if (simdLine != null)
                        sb.AppendLine($"        {simdLine}");
                }
            }
            sb.AppendLine("    }");
            sb.AppendLine();

            // Scalar remainder
            var remap = new Dictionary<string, string>();
            foreach (var ap in arrayParams) remap[$"{ap}["] = $"{ap}_ptr[";
            remap["MathF.Sin("] = "::sinf("; remap["MathF.Cos("] = "::cosf(";
            remap["MathF.Sqrt("] = "::sqrtf("; remap["MathF.Log("] = "::logf(";
            remap["MathF.Log10("] = "::log10f("; remap["MathF.Exp("] = "::expf(";
            remap["MathF.Abs("] = "::fabsf("; remap["float.MaxValue"] = "3.402823466e+38f";
            sb.AppendLine($"    for (int {indexName} = vec_count; {indexName} < {limitStr}; {indexName}++)");
            sb.AppendLine("    {");
            foreach (var stmt in loopBody)
            {
                string line = stmt.GetText().ToString().Trim();
                foreach (var kvp in remap) line = line.Replace(kvp.Key, kvp.Value);
                sb.AppendLine($"    {line}");
            }
            sb.AppendLine("    }");

            return sb.ToString();
        }

        // ──  Single SIMD statement (assignment, local decl)  ──
        private static string? GenSimdStmt(StatementSyntax stmt,
            string indexName, HashSet<string> arrayParams, HashSet<string> floatLocals,
            SemanticModel semanticModel)
        {
            if (stmt is LocalDeclarationStatementSyntax localDecl)
            {
                foreach (var v in localDecl.Declaration.Variables)
                {
                    if (v.Initializer != null && floatLocals.Contains(v.Identifier.Text))
                    {
                        string init = ExprToSimdStr(v.Initializer.Value, indexName, arrayParams, floatLocals, semanticModel);
                        return $"n_float v_{v.Identifier.Text} = {init};";
                    }
                }
                return null;
            }

            if (stmt is ExpressionStatementSyntax exprStmt && exprStmt.Expression is AssignmentExpressionSyntax assign)
            {
                if (assign.Left is ElementAccessExpressionSyntax lhsElem)
                {
                    string lhsArr = (lhsElem.Expression as IdentifierNameSyntax)?.Identifier.Text ?? "";
                    if (arrayParams.Contains(lhsArr))
                    {
                        string rhs = ExprToSimdStr(assign.Right, indexName, arrayParams, floatLocals, semanticModel);
                        return $"n_store_ps({lhsArr}_ptr + si, {rhs});";
                    }
                }
            }
            return null;
        }

        // ── Branchless if/else: compute all branches, blend with masks  ──
        private static string GenSimdIfElse(IfStatementSyntax ifStmt,
            string indexName, HashSet<string> arrayParams, HashSet<string> floatLocals,
            SemanticModel semanticModel)
        {
            // Collect all conditions and bodies: [if-cond, body], [elseif-cond, body], ..., [null, else-body]
            var conds = new List<string>();      // null = final else (unconditional)
            var bodies = new List<List<StatementSyntax>>();

            IfStatementSyntax cur = ifStmt;
            while (true)
            {
                string cond = ExprToSimdStr(cur.Condition, indexName, arrayParams, floatLocals, semanticModel);
                conds.Add(cond);
                bodies.Add(GetBodyStmts(cur.Statement));

                if (cur.Else != null)
                {
                    if (cur.Else.Statement is IfStatementSyntax elif) { cur = elif; continue; }
                    conds.Add(null); // final else
                    bodies.Add(GetBodyStmts(cur.Else.Statement));
                }
                break;
            }

            return BuildSimdBlendChain(conds, bodies, indexName, arrayParams, floatLocals, semanticModel);
        }

        // ── Build the mask + blend chain for if/else  ──
        private static string BuildSimdBlendChain(List<string> conds,
            List<List<StatementSyntax>> bodies,
            string indexName, HashSet<string> arrayParams, HashSet<string> floatLocals,
            SemanticModel semanticModel)
        {
            var sb = new StringBuilder();
            int maskId = _maskCounter++;

            if (conds.Count == 0) return "";

            // Find which array is written in each branch (e.g. result_ptr[i] = expr)
            // Extract the RHS expression for each branch
            var branchRhs = new List<string>();

            // Find the output array name (assume all branches write to the same array)
            string targetArray = null;
            foreach (var body in bodies)
            {
                foreach (var stmt in body)
                {
                    if (stmt is ExpressionStatementSyntax es && es.Expression is AssignmentExpressionSyntax ae
                        && ae.Left is ElementAccessExpressionSyntax ea)
                    {
                        targetArray = (ea.Expression as IdentifierNameSyntax)?.Identifier.Text ?? "";
                        break;
                    }
                }
                if (targetArray != null) break;
            }

            // Extract RHS for each branch, and compute default (all-masked-false) value from last else
            foreach (var body in bodies)
            {
                string rhs = null;
                foreach (var stmt in body)
                {
                    if (stmt is ExpressionStatementSyntax es && es.Expression is AssignmentExpressionSyntax ae)
                        rhs = ExprToSimdStr(ae.Right, indexName, arrayParams, floatLocals, semanticModel);
                }
                branchRhs.Add(rhs ?? "n_set1_ps(0.0f)");
            }

            // Generate masks and blend chain
            // mask0 = (cond0); then for each cond[i]: mask[i] = cond[i] & !any_prev
            // build result bottom-up: r = else_val; for i in reverse: r = blend(r, branch_val[i], mask[i])

            // Emit comparison masks; else-if masks are AND-NOT with first mask
            string m0 = $"__m{maskId}_0";
            sb.AppendLine($"        n_float {m0} = {conds[0]};");
            string anyPrev = m0;
            for (int i = 1; i < conds.Count; i++)
            {
                if (conds[i] == null) continue; // final else
                string mi = $"__m{maskId}_{i}";
                sb.AppendLine($"        n_float {mi} = n_and_mask({conds[i]}, n_not_mask({anyPrev}));");
            }

            // Build blend chain from last to first
            string accum = $"{branchRhs[branchRhs.Count - 1]}";
            for (int i = conds.Count - 2; i >= 0; i--)
            {
                string mask = conds[i] != null ? $"__m{maskId}_{i}" : null;
                if (mask != null)
                    accum = $"n_blendv_ps({accum}, {branchRhs[i]}, {mask})";
                else
                    accum = branchRhs[i];
            }

            sb.AppendLine($"        n_store_ps({targetArray}_ptr + si, {accum});");
            return sb.ToString();
        }

        private static int _maskCounter; // shared counter for unique mask names

        private static List<StatementSyntax> GetBodyStmts(StatementSyntax stmt)
        {
            if (stmt is BlockSyntax block) return block.Statements.ToList();
            return new List<StatementSyntax> { stmt };
        }

        // ── Expression to SIMD C++ (n_float operations, comparison masks)  ──
        private static string ExprToSimdStr(ExpressionSyntax expr,
            string indexName, HashSet<string> arrayParams, HashSet<string> floatLocals,
            SemanticModel semanticModel)
        {
            // MathF calls → n_sin_ps / n_cos_ps / n_sqrt_ps / n_log_ps
            if (expr is InvocationExpressionSyntax invoc)
            {
                var sym = semanticModel.GetSymbolInfo(invoc).Symbol as IMethodSymbol;
                if (sym != null)
                {
                    string? ns = sym.ContainingType?.ToDisplayString();
                    if (ns == "System.MathF" || ns == "System.Math")
                    {
                        string name = sym.Name;
                        if (name is "Pow" or "Exp" or "Tan")
                            return invoc.GetText().ToString().Trim();
                        var args = invoc.ArgumentList.Arguments;
                        if (args.Count == 1)
                        {
                            string a = ExprToSimdStr(args[0].Expression, indexName, arrayParams, floatLocals, semanticModel);
                            return $"n_{GetSimdName(name)}_ps({a})";
                        }
                    }
                }
                return invoc.GetText().ToString().Trim();
            }

            // Binary ops: comparison → n_cmp_*_ps, arithmetic → n_*_ps
            if (expr is BinaryExpressionSyntax bin)
            {
                string left = ExprToSimdStr(bin.Left, indexName, arrayParams, floatLocals, semanticModel);
                string right = ExprToSimdStr(bin.Right, indexName, arrayParams, floatLocals, semanticModel);
                string op = bin.OperatorToken.Text;
                string fn = op switch
                {
                    "+" => "n_add_ps", "-" => "n_sub_ps", "*" => "n_mul_ps", "/" => "n_div_ps",
                    ">" => "n_cmp_gt_ps", "<" => "n_cmp_lt_ps",
                    ">=" => "n_cmp_ge_ps", "<=" => "n_cmp_le_ps",
                    _ => null
                };
                if (fn != null) return $"{fn}({left}, {right})";
                return $"({left} {op} {right})";
            }

            if (expr is PrefixUnaryExpressionSyntax prefix)
            {
                string operand = ExprToSimdStr(prefix.Operand, indexName, arrayParams, floatLocals, semanticModel);
                if (prefix.OperatorToken.Text == "-")
                    return $"n_sub_ps(n_set1_ps(0.0f), {operand})";
                return $"{prefix.OperatorToken.Text}{operand}";
            }

            if (expr is IdentifierNameSyntax id)
            {
                string name = id.Identifier.Text;
                if (name == indexName) return "si";
                if (floatLocals.Contains(name)) return $"v_{name}";
                // Float scalar params need n_set1_ps in SIMD context
                var typeInfo = semanticModel.GetTypeInfo(id);
                if (typeInfo.Type != null && typeInfo.Type.SpecialType == SpecialType.System_Single)
                    return $"n_set1_ps({name})";
                return name;
            }

            // a[i] → n_load_ps(a_ptr + si)
            if (expr is ElementAccessExpressionSyntax elem)
            {
                string arrName = (elem.Expression as IdentifierNameSyntax)?.Identifier.Text ?? "";
                string idx = ExprToSimdStr(elem.ArgumentList.Arguments[0].Expression, indexName, arrayParams, floatLocals, semanticModel);
                if (arrayParams.Contains(arrName))
                    return $"n_load_ps({arrName}_ptr + {idx})";
                return $"{arrName}_ptr[{idx}]";
            }

            if (expr is ParenthesizedExpressionSyntax paren)
                return ExprToSimdStr(paren.Expression, indexName, arrayParams, floatLocals, semanticModel);

            // Literals → n_set1_ps(value)
            if (expr is LiteralExpressionSyntax lit)
            {
                if (lit.Token.Value is float f) return $"n_set1_ps({f}f)";
                if (lit.Token.Value is int iv) return $"n_set1_ps({iv}.0f)";
                if (lit.Token.Value is double d) return $"n_set1_ps({d})";
                return lit.Token.Text;
            }

            if (expr is CastExpressionSyntax cast)
                return ExprToSimdStr(cast.Expression, indexName, arrayParams, floatLocals, semanticModel);

            return expr.GetText().ToString().Trim();
        }

        private static string GetSimdName(string mathName)
        {
            return mathName switch
            {
                "Sqrt" => "sqrt", "Sin" => "sin", "Cos" => "cos",
                "Log" => "log", "Log10" => "log10",
                "Exp" => "exp", "Tan" => "tan",
                "Pow" => "pow", "Abs" => "abs",
                _ => "sqrt"
            };
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
