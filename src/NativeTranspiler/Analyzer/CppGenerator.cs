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
            "EntJoy.Collections.UnsafeUtility"
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

    public static string GenerateImplementation(IMethodSymbol method, Compilation compilation, HashSet<INamedTypeSymbol>? userStructs = null)
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
            sb.AppendLine("#include <cmath>");
            sb.AppendLine("#include <cstdio>");
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
                // ★ 修改：去掉 const，允许修改原值
                sb.AppendLine($"    {cppType}& {param.Name} = *{param.Name}_ptr;");
            }

            var methodSyntax = SymbolHelper.GetMethodSyntax(method);
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var translator = new CppPointerStatementTranslator(semanticModel, method);
                var bodyCode = translator.Translate(methodSyntax.Body);
                sb.Append(bodyCode);
            }
            else
            {
                sb.AppendLine("    // TODO: Translate method body");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string GenerateCppFunctionSignature(IMethodSymbol method, bool fullyQualified)
        {
            var returnType = NativeTranspiler.MapCSharpTypeToCpp(method.ReturnType);
            if (method.ReturnType is IPointerTypeSymbol) returnType += "*";
            else if (method.ReturnType.SpecialType != SpecialType.System_Void && !method.ReturnType.IsValueType) returnType += "*";

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
