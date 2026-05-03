using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public static class CppJobGenerator
    {
        public static string GetCppJobFunctionName(INamedTypeSymbol jobStruct, bool isBatch = false)
        {
            var containingNamespace = jobStruct.ContainingNamespace?.ToDisplayString() ?? "";
            var typePath = SymbolHelper.BuildFullTypePath(jobStruct);
            var safeNamespace = SymbolHelper.Sanitize(containingNamespace);
            var safeTypePath = SymbolHelper.Sanitize(typePath);
            string suffix = isBatch ? "_Batch" : "";
            return $"SharpNative_Job_{safeNamespace}_{safeTypePath}_Execute{suffix}";
        }

        public static bool IsParallelForJob(INamedTypeSymbol jobStruct) =>
            jobStruct.AllInterfaces.Any(i => i.Name == "IJobParallelFor");
        public static bool IsForJob(INamedTypeSymbol jobStruct) =>
            jobStruct.AllInterfaces.Any(i => i.Name == "IJobFor");

        public static string GenerateJobHeader(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine();
            sb.AppendLine("#include \"../../NativeDll/NativeMath.h\"");
            sb.AppendLine("#include \"../../NativeDll/NativeContainers.h\"");
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateAtomicMacros());
            sb.AppendLine();

            if (IsParallelForJob(jobStruct) || IsForJob(jobStruct))
            {
                var batchParams = BuildBatchJobParameters(jobStruct);
                var baseFuncName = GetCppJobFunctionName(jobStruct, isBatch: true);

                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
                var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
                if (methodSyntax != null)
                {
                    var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                    var conditionalFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel);
                    var boolField = conditionalFields.FirstOrDefault(f => f.Type.SpecialType == SpecialType.System_Boolean);
                    if (boolField != null)
                    {
                        sb.AppendLine($"HEAD void CALLINGCONVENTION {baseFuncName}_true({batchParams});");
                        sb.AppendLine($"HEAD void CALLINGCONVENTION {baseFuncName}_false({batchParams});");
                    }
                    else
                    {
                        sb.AppendLine($"HEAD void CALLINGCONVENTION {baseFuncName}({batchParams});");
                    }
                }
                else
                {
                    sb.AppendLine($"HEAD void CALLINGCONVENTION {baseFuncName}({batchParams});");
                }
            }
            else
            {
                var singleParams = BuildJobParameters(jobStruct);
                var singleFuncName = GetCppJobFunctionName(jobStruct);
                sb.AppendLine($"HEAD void CALLINGCONVENTION {singleFuncName}({singleParams});");
            }
            return sb.ToString();
        }

        public static string GenerateJobImplementation(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            var baseFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"#include \"{baseFuncName}.h\"");
            sb.AppendLine("#include <algorithm>");
            sb.AppendLine("#include <cmath>");
            sb.AppendLine("#include <cstdio>");
            sb.AppendLine();

            if (IsParallelForJob(jobStruct) || IsForJob(jobStruct))
            {
                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
                var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
                if (methodSyntax == null)
                {
                    sb.AppendLine("// Error: Could not find method syntax");
                    return sb.ToString();
                }

                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var conditionalFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel);
                var boolField = conditionalFields.FirstOrDefault(f => f.Type.SpecialType == SpecialType.System_Boolean);

                if (boolField != null)
                {
                    GenerateBatchFunctionVariant(jobStruct, boolField, true, semanticModel, methodSyntax, sb);
                    GenerateBatchFunctionVariant(jobStruct, boolField, false, semanticModel, methodSyntax, sb);
                }
                else
                {
                    GenerateBatchFunctionStandard(jobStruct, semanticModel, methodSyntax, sb);
                }
            }
            else
            {
                GenerateSingleFunctionStandard(jobStruct, compilation, sb);
            }

            return sb.ToString();
        }

        // 局部变量声明：仅保留 NativeList 引用，移除 NativeArray 包装
        private static void AppendLocalVariableDeclarations(INamedTypeSymbol jobStruct, StringBuilder sb)
        {
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (field.Type.Name == "NativeList")
                    {
                        var elementType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        sb.AppendLine($"    EntJoy::Collections::UnsafeList<{cppElementType}>& {field.Name} = *{field.Name}_listData;");
                    }
                    // NativeArray: nothing
                }
            }

            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type)) continue;
                if (field.Type is IPointerTypeSymbol) continue;
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                sb.AppendLine($"    const {cppType}& {field.Name} = *{field.Name}_ptr;");
            }
        }

        private static void GenerateBatchFunctionStandard(INamedTypeSymbol jobStruct, SemanticModel semanticModel, MethodDeclarationSyntax methodSyntax, StringBuilder sb)
        {
            string funcName = GetCppJobFunctionName(jobStruct, isBatch: true);
            string paramsStr = BuildBatchJobParameters(jobStruct);
            sb.AppendLine($"HEAD void CALLINGCONVENTION {funcName}({paramsStr})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb);
            var indexParamName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;
            sb.AppendLine($"    for (int {indexParamName} = __startIndex; {indexParamName} < __startIndex + __count; ++{indexParamName})");
            sb.AppendLine("    {");
            var translator = new CppBatchStatementTranslator(semanticModel, jobStruct, indexParamName, indexParamName);
            var bodyCode = translator.Translate(methodSyntax.Body);
            sb.Append(bodyCode);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        private static void GenerateBatchFunctionVariant(INamedTypeSymbol jobStruct, IFieldSymbol constField, bool constantValue, SemanticModel semanticModel, MethodDeclarationSyntax methodSyntax, StringBuilder sb)
        {
            string suffix = constantValue ? "true" : "false";
            string funcName = GetCppJobFunctionName(jobStruct, isBatch: true) + "_" + suffix;
            string paramsStr = BuildBatchJobParameters(jobStruct);
            sb.AppendLine($"HEAD void CALLINGCONVENTION {funcName}({paramsStr})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb);
            var indexParamName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;
            sb.AppendLine($"    for (int {indexParamName} = __startIndex; {indexParamName} < __startIndex + __count; ++{indexParamName})");
            sb.AppendLine("    {");
            var translator = new CppBatchStatementTranslator(semanticModel, jobStruct, indexParamName, indexParamName);
            var bodyCode = translator.Translate(methodSyntax.Body);
            string constantLiteral = constantValue ? "true" : "false";
            string pattern = $@"\b{constField.Name}\b";
            bodyCode = Regex.Replace(bodyCode, pattern, constantLiteral);
            sb.Append(bodyCode);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        private static void GenerateSingleFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb)
        {
            var singleParams = BuildJobParameters(jobStruct);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"HEAD void CALLINGCONVENTION {singleFuncName}({singleParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb);
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var translator = new CppPointerStatementTranslator(semanticModel, jobStruct);
                var bodyCode = translator.Translate(methodSyntax.Body);
                sb.Append(bodyCode);
            }
            else
            {
                sb.AppendLine("    // TODO: Translate Execute body");
            }
            sb.AppendLine("}");
        }

        private static string BuildJobParameters(INamedTypeSymbol jobStruct)
        {
            var parameters = new List<string>();
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            if (executeMethod.Parameters.Length == 1 && executeMethod.Parameters[0].Type.SpecialType == SpecialType.System_Int32)
                parameters.Add($"int {executeMethod.Parameters[0].Name}");
            AppendFieldParameters(jobStruct, parameters);
            return string.Join(", ", parameters);
        }

        private static string BuildBatchJobParameters(INamedTypeSymbol jobStruct)
        {
            var parameters = new List<string> { "int __startIndex", "int __count" };
            AppendFieldParameters(jobStruct, parameters);
            return string.Join(", ", parameters);
        }

        private static void AppendFieldParameters(INamedTypeSymbol jobStruct, List<string> parameters)
        {
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (field.Type.Name == "NativeList")
                    {
                        var elementType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        parameters.Add($"EntJoy::Collections::UnsafeList<{cppElementType}>* RESTRICT {field.Name}_listData");
                    }
                    else // NativeArray
                    {
                        var elementType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        parameters.Add($"{cppElementType}* RESTRICT {field.Name}_ptr, int {field.Name}_length");
                    }
                }
                else if (field.Type is IPointerTypeSymbol)
                {
                    // ★ 修改：不再添加多余的 *，MapCSharpTypeToCpp 已包含 *
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    parameters.Add($"{cppType} RESTRICT {field.Name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    parameters.Add($"{cppType}* RESTRICT {field.Name}_ptr");
                }
            }
        }

    }
}
