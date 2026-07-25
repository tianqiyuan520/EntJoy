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

        /// <summary>
        /// Detect if the method body contains MathF.Sin/Cos/Sqrt/Log calls that need SLEEF.
        /// </summary>

        private static string GenerateSimdViaCFG(IMethodSymbol method, BlockSyntax body,
            SemanticModel semanticModel, bool useFastMath)
        {
            var sb = new StringBuilder();
            var forStmt = body.Statements.OfType<ForStatementSyntax>().FirstOrDefault();
            if (forStmt == null)
                return FallbackScalarTranslation(method, body, semanticModel, useFastMath);

            string indexName = forStmt.Declaration.Variables[0].Identifier.Text;
            var limitExpr = ((BinaryExpressionSyntax)forStmt.Condition).Right;
            string limitStr = limitExpr.GetText().ToString().Trim();
            BlockSyntax innerBody = forStmt.Statement is BlockSyntax bs ? bs
                : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(new SyntaxList<StatementSyntax>(forStmt.Statement));

            // Build NativeArray param dict: C# param name → element C++ type
            var nativeArrayParams = new Dictionary<string, string>();
            foreach (var param in method.Parameters)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(param.Type) && param.Type.Name == "NativeArray")
                {
                    var typeArg = ((INamedTypeSymbol)param.Type).TypeArguments.FirstOrDefault();
                    string elemCppType = typeArg != null ? NativeTranspiler.MapCSharpTypeToCpp(typeArg) : "float";
                    nativeArrayParams[param.Name] = elemCppType;
                }
            }

            var methodSyntax = SymbolHelper.GetMethodSyntax(method);
            var varAnalyzer = new SimdVariableAnalyzer(semanticModel, null, indexName);
            var variables = varAnalyzer.Analyze(methodSyntax);
            // Mark for-loop variable as Varying for SIMD gather
            if (variables.TryGetValue(indexName, out var idxInfo))
                idxInfo.Kind = VarKind.Varying;
            else
                variables[indexName] = new SimdVariableInfo { Name = indexName, Kind = VarKind.Varying, CppType = "int" };

            var simdGen = new SimdControlFlowGenerator(
                semanticModel, null, variables, varAnalyzer,
                indexParamName: indexName, simdIndexVar: "v_i",
                batchOffsetVar: "0",
                simdMathPrecision: NativeTranspiler.SimdMathPrecision.Fastest,
                nativeArrayParams: nativeArrayParams,
                batchLoopVar: "si");

            sb.AppendLine($"    int vec_count = (({limitStr}) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine("    simd_value<int> v_base = simd_value<int>::sequence(0);");
            sb.AppendLine("    if (vec_count > 0)");
            sb.AppendLine("    {");
            sb.AppendLine("        for (int si = 0; si < vec_count; si += NSIMD_WIDTH)");
            sb.AppendLine("        {");
            sb.AppendLine("            simd_value<int> v_i = v_base + si;");

            string simdBody = simdGen.Generate(innerBody);
            foreach (var line in simdBody.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine("            " + line.TrimEnd());

            sb.AppendLine("        __simd_exit: ;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Scalar remainder
            var remap = new Dictionary<string, string>();
            foreach (var param in method.Parameters)
                if (NativeTranspiler.IsEntJoyNativeContainerType(param.Type))
                    remap[$"{param.Name}["] = $"{param.Name}_ptr[";
            remap["MathF.Sin("] = "::sinf("; remap["MathF.Cos("] = "::cosf(";
            remap["MathF.Sqrt("] = "::sqrtf("; remap["MathF.Log("] = "::logf(";
            remap["MathF.Log10("] = "::log10f("; remap["MathF.Exp("] = "::expf(";
            remap["MathF.Abs("] = "::fabsf("; remap["float.MaxValue"] = "3.402823466e+38f";
            sb.AppendLine($"    for (int {indexName} = vec_count; {indexName} < {limitStr}; {indexName}++)");
            sb.AppendLine("    {");
            foreach (var stmt in (forStmt.Statement is BlockSyntax ? innerBody.Statements
                : new SyntaxList<StatementSyntax>(forStmt.Statement)))
            {
                string line = stmt.GetText().ToString().Trim();
                foreach (var kvp in remap) line = line.Replace(kvp.Key, kvp.Value);
                sb.AppendLine($"    {line}");
            }
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
