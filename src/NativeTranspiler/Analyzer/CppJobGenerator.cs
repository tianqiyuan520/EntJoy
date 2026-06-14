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
        public static bool IsChunkJob(INamedTypeSymbol jobStruct) =>
            jobStruct.AllInterfaces.Any(i => i.Name == "IJobChunk");
        public static bool IsEntityJob(INamedTypeSymbol jobStruct) =>
            jobStruct.AllInterfaces.Any(i => i.Name == "IJobEntity");
        public static bool IsChunkScheduledJob(INamedTypeSymbol jobStruct) =>
            IsChunkJob(jobStruct) || IsEntityJob(jobStruct);

        /// <summary>
        /// 获取所有 bool 条件字段列表
        /// </summary>
        private static List<IFieldSymbol> GetBoolConditionalFields(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
            if (executeMethod == null) return new List<IFieldSymbol>();
            
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax == null) return new List<IFieldSymbol>();
            
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var conditionalFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel);
            return conditionalFields.Where(f => f.Type.SpecialType == SpecialType.System_Boolean).ToList();
        }

        /// <summary>
        /// 为 bool 条件字段组合生成变体函数名后缀
        /// 使用索引而非字段名，避免不同 Job 中相同字段名冲突
        /// 例如: boolFields=[a,b], values=[true,false] => "_0_true_1_false"
        /// </summary>
        public static string BuildBoolVariantSuffix(List<IFieldSymbol> boolFields, List<bool> values)
        {
            var parts = new List<string>();
            for (int i = 0; i < boolFields.Count; i++)
            {
                parts.Add($"{(values[i] ? "true" : "false")}");
            }
            return "_" + string.Join("_", parts);
        }

        /// <summary>
        /// 生成所有 bool 条件字段组合的变体函数声明
        /// </summary>
        private static void GenerateBoolVariantDeclarations(INamedTypeSymbol jobStruct, List<IFieldSymbol> boolFields, string baseFuncName, string batchParams, StringBuilder sb)
        {
            if (boolFields.Count == 0)
            {
                sb.AppendLine($"HEAD void CALLINGCONVENTION {baseFuncName}({batchParams});");
                return;
            }

            int totalVariants = 1 << boolFields.Count; // 2^n
            for (int mask = 0; mask < totalVariants; mask++)
            {
                var values = new List<bool>();
                for (int i = 0; i < boolFields.Count; i++)
                    values.Add((mask & (1 << i)) != 0);
                
                string suffix = BuildBoolVariantSuffix(boolFields, values);
                sb.AppendLine($"HEAD void CALLINGCONVENTION {baseFuncName}{suffix}({batchParams});");
            }
        }

        public static string GenerateJobHeader(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine();
            sb.AppendLine("#include \"../../NativeDll/NativeMath.h\"");
            sb.AppendLine("#include \"../../NativeDll/NativeContainers.h\"");
            if (IsChunkScheduledJob(jobStruct))
            {
                sb.AppendLine("#include \"../../NativeDll/ChunkJobData.h\"");
                sb.AppendLine("#include \"../../NativeDll/ChunkNativeArray.h\"");
            }
            foreach (var include in CollectJobStructIncludes(jobStruct, compilation))
                sb.AppendLine($"#include \"{include}.h\"");
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateAtomicMacros());
            sb.AppendLine();

            // IJobEntity: 无独立 Execute 函数，循环体内联到 Adapter 中
            if (IsChunkJob(jobStruct))
            {
                var chunkParams = BuildChunkJobParameters(jobStruct);
                var singleFuncName = GetCppJobFunctionName(jobStruct);
                sb.AppendLine($"HEAD void CALLINGCONVENTION {singleFuncName}({chunkParams});");
            }
            else if (IsParallelForJob(jobStruct) || IsForJob(jobStruct))
            {
                var batchParams = BuildBatchJobParameters(jobStruct);
                var baseFuncName = GetCppJobFunctionName(jobStruct, isBatch: true);
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);
                GenerateBoolVariantDeclarations(jobStruct, boolFields, baseFuncName, batchParams, sb);
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
            var attrSymbol = compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute");
            bool useFastMath = AttributeHelper.HasFastCppMathLib(jobStruct, attrSymbol);
            sb.AppendLine($"#include \"{baseFuncName}.h\"");
            sb.AppendLine("#include <algorithm>");
            sb.AppendLine("#include <cmath>");
            sb.AppendLine("#include <cstdio>");
            sb.AppendLine();

            // IJobChunk: 生成独立 Execute 函数
            if (IsChunkJob(jobStruct))
            {
                GenerateChunkFunctionStandard(jobStruct, compilation, sb, useFastMath);
            }
            // IJobEntity: 无独立 Execute，循环体内联到 Adapter 中
            else if (IsEntityJob(jobStruct))
            {
                // IJobEntity Execute 函数体内联在 Adapter 中，此处不生成独立函数
            }
            else if (IsParallelForJob(jobStruct) || IsForJob(jobStruct))
            {
                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
                var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
                if (methodSyntax == null)
                {
                    sb.AppendLine("// Error: Could not find method syntax");
                    return sb.ToString();
                }

                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);

                if (boolFields.Count > 0)
                {
                    // 生成所有 2^n 个 bool 组合变体
                    int totalVariants = 1 << boolFields.Count;
                    for (int mask = 0; mask < totalVariants; mask++)
                    {
                        var values = new List<bool>();
                        for (int i = 0; i < boolFields.Count; i++)
                            values.Add((mask & (1 << i)) != 0);
                        GenerateBatchFunctionVariant(jobStruct, boolFields, values, semanticModel, methodSyntax, sb, useFastMath);
                    }
                }
                else
                {
                    GenerateBatchFunctionStandard(jobStruct, semanticModel, methodSyntax, sb, useFastMath);
                }
            }
            else
            {
                GenerateSingleFunctionStandard(jobStruct, compilation, sb, useFastMath);
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

        private static void GenerateBatchFunctionStandard(INamedTypeSymbol jobStruct, SemanticModel semanticModel, MethodDeclarationSyntax methodSyntax, StringBuilder sb, bool useFastMath)
        {
            string funcName = GetCppJobFunctionName(jobStruct, isBatch: true);
            string paramsStr = BuildBatchJobParameters(jobStruct);
            sb.AppendLine($"HEAD void CALLINGCONVENTION {funcName}({paramsStr})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb);
            var indexParamName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;
            sb.AppendLine($"    for (int {indexParamName} = __startIndex; {indexParamName} < __startIndex + __count; ++{indexParamName})");
            sb.AppendLine("    {");
            var translator = new CppBatchStatementTranslator(semanticModel, jobStruct, indexParamName, indexParamName, useFastMath);
            var bodyCode = translator.Translate(methodSyntax.Body);
            sb.Append(bodyCode);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        private static void GenerateBatchFunctionVariant(INamedTypeSymbol jobStruct, List<IFieldSymbol> boolFields, List<bool> values, SemanticModel semanticModel, MethodDeclarationSyntax methodSyntax, StringBuilder sb, bool useFastMath)
        {
            string suffix = BuildBoolVariantSuffix(boolFields, values);
            string funcName = GetCppJobFunctionName(jobStruct, isBatch: true) + suffix;
            string paramsStr = BuildBatchJobParameters(jobStruct);
            sb.AppendLine($"HEAD void CALLINGCONVENTION {funcName}({paramsStr})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb);
            var indexParamName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;
            sb.AppendLine($"    for (int {indexParamName} = __startIndex; {indexParamName} < __startIndex + __count; ++{indexParamName})");
            sb.AppendLine("    {");
            var translator = new CppBatchStatementTranslator(semanticModel, jobStruct, indexParamName, indexParamName, useFastMath);
            var bodyCode = translator.Translate(methodSyntax.Body);
            // 将所有 bool 条件字段替换为常量值
            for (int i = 0; i < boolFields.Count; i++)
            {
                string constantLiteral = values[i] ? "true" : "false";
                string pattern = $@"\b{boolFields[i].Name}\b";
                bodyCode = Regex.Replace(bodyCode, pattern, constantLiteral);
            }
            sb.Append(bodyCode);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        private static void GenerateSingleFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
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
                var translator = new CppPointerStatementTranslator(semanticModel, jobStruct, useFastMath);
                var bodyCode = translator.Translate(methodSyntax.Body);
                sb.Append(bodyCode);
            }
            else
            {
                sb.AppendLine("    // TODO: Translate Execute body");
            }
            sb.AppendLine("}");
        }

        private static void GenerateChunkFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
        {
            var chunkParams = BuildChunkJobParameters(jobStruct);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"HEAD void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb);
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
                var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, useFastMath);
                var bodyCode = translator.Translate(methodSyntax.Body);
                sb.Append(bodyCode);
            }
            else
            {
                sb.AppendLine("    // TODO: Translate IJobChunk Execute body");
            }
            sb.AppendLine("}");
        }

        private static void GenerateEntityFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
        {
            var chunkParams = BuildChunkJobParameters(jobStruct);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"HEAD void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb);

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            for (int i = 0; i < executeMethod.Parameters.Length; i++)
            {
                var param = executeMethod.Parameters[i];
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                sb.AppendLine($"    auto* RESTRICT __entity_param_{i}_ptr = reinterpret_cast<{cppType}*>(__chunkData->requiredComponentArrays[{i}]);");
            }
            sb.AppendLine("    int __entity_count = __chunkData->entityCount;");
            sb.AppendLine("    for (int __entity_index = 0; __entity_index < __entity_count; ++__entity_index)");
            sb.AppendLine("    {");
            foreach (var param in executeMethod.Parameters.Select((p, i) => (p, i)))
            {
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.p.Type);
                string constPrefix = param.p.RefKind == RefKind.In ? "const " : "";
                sb.AppendLine($"        {constPrefix}{cppType}& {param.p.Name} = __entity_param_{param.i}_ptr[__entity_index];");
            }

            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var translator = new CppStatementTranslator(semanticModel, useFastMath);
                var bodyCode = translator.Translate(methodSyntax.Body);
                foreach (var line in bodyCode.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append("    ").AppendLine(line);
                }
            }
            else
            {
                sb.AppendLine("        // TODO: Translate IJobEntity Execute body");
            }
            sb.AppendLine("    }");
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

        private static string BuildChunkJobParameters(INamedTypeSymbol jobStruct)
        {
            var parameters = new List<string> { "const ChunkJobData* __chunkData", "const int* __requiredComponentTypeIds" };
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

        public static List<INamedTypeSymbol> CollectChunkNativeArrayTypes(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var result = new List<INamedTypeSymbol>();
            if (IsEntityJob(jobStruct))
            {
                var execute = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
                if (execute != null)
                {
                    foreach (var parameter in execute.Parameters)
                    {
                        if (parameter.Type is INamedTypeSymbol componentType &&
                            !result.Any(t => SymbolEqualityComparer.Default.Equals(t, componentType)))
                        {
                            result.Add(componentType);
                        }
                    }
                }
                return result;
            }

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
            var methodSyntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return result;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var invocation in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
                    continue;
                if (methodSymbol.ContainingType?.ToDisplayString() != "EntJoy.ArchetypeChunk" ||
                    (methodSymbol.Name != "GetComponentDataNativeArray" && methodSymbol.Name != "GetComponentDataSpan"))
                    continue;
                if (methodSymbol.TypeArguments.Length == 0 || methodSymbol.TypeArguments[0] is not INamedTypeSymbol componentType)
                    continue;
                if (!result.Any(t => SymbolEqualityComparer.Default.Equals(t, componentType)))
                    result.Add(componentType);
            }
            return result;
        }

        private static List<string> CollectJobStructIncludes(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var includes = new HashSet<string>();
            void AddType(ITypeSymbol type)
            {
                if (type is IPointerTypeSymbol ptr)
                {
                    AddType(ptr.PointedAtType);
                    return;
                }
                if (type is INamedTypeSymbol named && named.IsGenericType && NativeTranspiler.IsEntJoyNativeContainerType(type))
                {
                    AddType(named.TypeArguments[0]);
                    return;
                }
                if (type is INamedTypeSymbol namedType &&
                    type.TypeKind == TypeKind.Struct &&
                    !NativeTranspiler.IsBuiltinUnmanaged(type) &&
                    !NativeTranspiler.IsEntJoyPredefinedType(type))
                {
                    includes.Add(NativeTranspiler.GetStructHeaderFileName(namedType));
                }
            }

            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                AddType(field.Type);
            foreach (var type in CollectChunkNativeArrayTypes(jobStruct, compilation))
                AddType(type);

            return includes.OrderBy(x => x).ToList();
        }

        // ===================================================================
        //              新增：适配函数生成（消除 C# 委托桥接）
        // ===================================================================

        /// <summary>
        /// 计算 C# struct 中字段的偏移量（基于 [StructLayout(LayoutKind.Sequential)]，64位 Windows）
        /// </summary>
        internal static int CalculateFieldOffset(IFieldSymbol field, ref int currentOffset)
        {
            int size = GetCSharpFieldSize(field.Type);
            int alignment = GetCSharpFieldAlignment(field.Type);
            
            // 对齐到 alignment 的倍数
            currentOffset = (currentOffset + alignment - 1) / alignment * alignment;
            int result = currentOffset;
            currentOffset += size;
            return result;
        }

        /// <summary>
        /// 获取 C# 类型在 Sequential 布局下的大小（64位）
        /// </summary>
        private static int GetCSharpFieldSize(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol) return 8;
            
            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                var fullName = named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                // FullyQualifiedFormat includes "global::" prefix, so check both variants
                if (fullName == "EntJoy.Collections.NativeArray<T>" || fullName == "global::EntJoy.Collections.NativeArray<T>")
                    return 32; // _buffer(8) + _length(4) + _allocator(4) + _safety(8) + _isOwner(1) + padding(7)
                if (fullName == "EntJoy.Collections.NativeList<T>" || fullName == "global::EntJoy.Collections.NativeList<T>")
                    return 24; // _listData(8) + _allocator(4) + _safety(8) + padding(4)
                if (fullName == "EntJoy.Collections.UnsafeList<T>" || fullName == "global::EntJoy.Collections.UnsafeList<T>")
                    return 20; // Ptr(8) + Length(4) + Capacity(4) + Allocator(4)
            }

            // 检查是否为 EntJoy.Mathematics 向量类型
            var ns = type.ContainingNamespace?.ToDisplayString();
            if (ns == "EntJoy.Mathematics")
            {
                return type.Name switch
                {
                    "float2" => 8,
                    "int2" => 8,
                    "uint2" => 8,
                    _ => 8
                };
            }

            return type.SpecialType switch
            {
                SpecialType.System_Int32 => 4,
                SpecialType.System_UInt32 => 4,
                SpecialType.System_Int64 => 8,
                SpecialType.System_UInt64 => 8,
                SpecialType.System_Single => 4,
                SpecialType.System_Double => 8,
                SpecialType.System_Boolean => 1,
                _ => 4 // 默认
            };
        }

        /// <summary>
        /// 获取 C# 类型在 Sequential 布局下的对齐要求（64位）
        /// </summary>
        private static int GetCSharpFieldAlignment(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol) return 8;
            
            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                var fullName = named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (fullName == "EntJoy.Collections.NativeArray<T>" || fullName == "global::EntJoy.Collections.NativeArray<T>")
                    return 8;
                if (fullName == "EntJoy.Collections.NativeList<T>" || fullName == "global::EntJoy.Collections.NativeList<T>")
                    return 8;
                if (fullName == "EntJoy.Collections.UnsafeList<T>" || fullName == "global::EntJoy.Collections.UnsafeList<T>")
                    return 8;
            }

            var ns = type.ContainingNamespace?.ToDisplayString();
            if (ns == "EntJoy.Mathematics")
            {
                return type.Name switch
                {
                    "float2" => 4,
                    "int2" => 4,
                    "uint2" => 4,
                    _ => 4
                };
            }

            return type.SpecialType switch
            {
                SpecialType.System_Int32 => 4,
                SpecialType.System_UInt32 => 4,
                SpecialType.System_Int64 => 8,
                SpecialType.System_UInt64 => 8,
                SpecialType.System_Single => 4,
                SpecialType.System_Double => 8,
                SpecialType.System_Boolean => 1,
                _ => 4
            };
        }

        /// <summary>
        /// 生成适配函数代码（C++），用于消除 C# 委托桥接。
        /// 适配函数签名匹配 BatchJobFunc(void* context, int startIndex, int count)，
        /// 内部从 context 中按偏移量读取字段，调用实际的 Batch 函数。
        /// </summary>
        public static string GenerateJobAdapter(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            var baseFuncName = GetCppJobFunctionName(jobStruct);
            var adapterFuncName = baseFuncName + "_Adapter";
            
            sb.AppendLine("#include \"../../NativeDll/NativeMath.h\"");
            sb.AppendLine("#include \"../../NativeDll/NativeContainers.h\"");
            if (IsChunkScheduledJob(jobStruct))
            {
                sb.AppendLine("#include \"../../NativeDll/ChunkJobData.h\"");
                foreach (var include in CollectJobStructIncludes(jobStruct, compilation))
                    sb.AppendLine($"#include \"{include}.h\"");
            }
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();

            // 检查是否为 ISPC job
            var attrSymbol = AttributeHelper.GetAttributeSymbol(compilation);
            bool isIspcJob = attrSymbol != null && 
                AttributeHelper.GetBackendTarget(jobStruct, attrSymbol) == NativeTranspiler.BackendTarget.Ispc;

            if (isIspcJob)
            {
                // ISPC job: 声明 wrapper 函数为 extern（在 wrapper.cpp 中实现）
                var batchFuncName = GetCppJobFunctionName(jobStruct, isBatch: true);
                var batchParams = BuildBatchJobParameters(jobStruct);
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);

                sb.AppendLine($"// ISPC wrapper function (defined in wrapper.cpp)");
                GenerateBoolVariantDeclarations(jobStruct, boolFields, batchFuncName, batchParams, sb);
                sb.AppendLine();
            }
            else
            {
                // C++ job: 包含 header 文件
                sb.AppendLine($"#include \"{baseFuncName}.h\"");
                sb.AppendLine();
            }

            bool isChunkJob = IsChunkScheduledJob(jobStruct);
            bool isParallelFor = IsParallelForJob(jobStruct) || IsForJob(jobStruct);

            if (isChunkJob)
            {
                sb.AppendLine("struct __EntJoyChunkContextHeader");
                sb.AppendLine("{");
                sb.AppendLine("    int chunkCount;");
                sb.AppendLine("    int hasEnabledFilter;");
                sb.AppendLine("    void* queryAllEnabledTypes;");
                sb.AppendLine("    int allEnabledCount;");
                sb.AppendLine("    int gcHandleStartIndex;");
                sb.AppendLine("    void* chunksPtr;");
                sb.AppendLine("    int cleanupInProgress;");
                sb.AppendLine("    void* requiredComponentTypeIds;");
                sb.AppendLine("    int requiredComponentTypeIdCount;");
                sb.AppendLine("};");
                sb.AppendLine();

                bool isEntityJob = IsEntityJob(jobStruct);
                bool useFastMath = AttributeHelper.HasFastCppMathLib(jobStruct, attrSymbol);

                sb.AppendLine($"HEAD void CALLINGCONVENTION {adapterFuncName}(void* context, const ChunkJobData* __chunkData)");
                sb.AppendLine("{");
                sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
                sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
                sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
                sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
                sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
                sb.AppendLine("    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;");

                if (isEntityJob)
                {
                    // IJobEntity: 直接在 Adapter 内联循环体，消除独立 Execute 函数调用
                    int currentOffset = 0;
                    foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    {
                        int offset = CalculateFieldOffset(field, ref currentOffset);
                        if (!NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                        {
                            var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                            sb.AppendLine($"    auto {field.Name} = *({cppType}*)(__jobContext + {offset});");
                        }
                    }

                    var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
                    var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
                    for (int i = 0; i < executeMethod.Parameters.Length; i++)
                    {
                        var param = executeMethod.Parameters[i];
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                        sb.AppendLine($"    auto* RESTRICT __entity_param_{i}_ptr = reinterpret_cast<{cppType}*>(__chunkData->requiredComponentArrays[{i}]);");
                    }
                    sb.AppendLine("    int __entity_count = __chunkData->entityCount;");
                    sb.AppendLine("    for (int __entity_index = 0; __entity_index < __entity_count; ++__entity_index)");
                    sb.AppendLine("    {");
                    foreach (var param in executeMethod.Parameters.Select((p, i) => (p, i)))
                    {
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.p.Type);
                        string constPrefix = param.p.RefKind == RefKind.In ? "const " : "";
                        sb.AppendLine($"        {constPrefix}{cppType}& {param.p.Name} = __entity_param_{param.i}_ptr[__entity_index];");
                    }

                    if (methodSyntax?.Body != null)
                    {
                        var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                        var translator = new CppStatementTranslator(semanticModel, useFastMath);
                        var bodyCode = translator.Translate(methodSyntax.Body);
                        foreach (var line in bodyCode.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                        {
                            if (line.Length == 0) continue;
                            sb.Append("        ").AppendLine(line);
                        }
                    }
                    sb.AppendLine("    }");
                }
                else
                {
                    // IJobChunk: 解包字段指针，调用独立 Execute 函数
                    var callArgs = new List<string> { "__chunkData", "__requiredComponentTypeIds" };
                    int currentOffset = 0;
                    foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    {
                        int offset = CalculateFieldOffset(field, ref currentOffset);

                        if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                        {
                            if (field.Type.Name == "NativeList")
                            {
                                sb.AppendLine($"    auto* {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(field.Type)}>**)(__jobContext + {offset});");
                                callArgs.Add($"{field.Name}_listData");
                            }
                            else
                            {
                                var cppElemType = GetCppElementType(field.Type);
                                sb.AppendLine($"    auto* {field.Name}_ptr = *({cppElemType}**)(__jobContext + {offset});");
                                sb.AppendLine($"    int {field.Name}_length = *(int*)(__jobContext + {offset + 8});");
                                callArgs.Add($"{field.Name}_ptr, {field.Name}_length");
                            }
                        }
                        else if (field.Type is IPointerTypeSymbol)
                        {
                            var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                            sb.AppendLine($"    auto* {field.Name}_ptr = *({cppType}*)(__jobContext + {offset});");
                            callArgs.Add($"{field.Name}_ptr");
                        }
                        else
                        {
                            var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                            sb.AppendLine($"    auto* {field.Name}_ptr = ({cppType}*)(__jobContext + {offset});");
                            callArgs.Add($"{field.Name}_ptr");
                        }
                    }

                    string chunkFuncName = GetCppJobFunctionName(jobStruct);
                    sb.AppendLine($"    {chunkFuncName}({string.Join(", ", callArgs)});");
                }

                sb.AppendLine("}");
                sb.AppendLine();

                sb.AppendLine($"HEAD void* CALLINGCONVENTION Get_{adapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){adapterFuncName};");
                sb.AppendLine("}");
            }
            else if (isParallelFor)
            {
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);

                // 生成适配函数
                sb.AppendLine($"HEAD void CALLINGCONVENTION {adapterFuncName}(void* context, int __startIndex, int __count)");
                sb.AppendLine("{");
                
                // 生成字段读取代码
                var fieldReads = new StringBuilder();
                var callArgs = new List<string> { "__startIndex", "__count" };
                int currentOffset = 0;
                
                foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    int offset = CalculateFieldOffset(field, ref currentOffset);
                    
                    if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                    {
                        if (field.Type.Name == "NativeList")
                        {
                            // NativeList: _listData 在偏移 0（指针）
                            fieldReads.AppendLine($"    auto* {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(field.Type)}>**)((char*)context + {offset});");
                            callArgs.Add($"{field.Name}_listData");
                        }
                        else // NativeArray
                        {
                            // NativeArray: _buffer 在偏移 0, _length 在偏移 8
                            var cppElemType = GetCppElementType(field.Type);
                            fieldReads.AppendLine($"    auto* {field.Name}_ptr = *({cppElemType}**)((char*)context + {offset});");
                            fieldReads.AppendLine($"    int {field.Name}_length = *(int*)((char*)context + {offset + 8});");
                            callArgs.Add($"{field.Name}_ptr, {field.Name}_length");
                        }
                    }
                    else if (field.Type is IPointerTypeSymbol)
                    {
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                        fieldReads.AppendLine($"    auto* {field.Name}_ptr = *({cppType}*)((char*)context + {offset});");
                        callArgs.Add($"{field.Name}_ptr");
                    }
                    else
                    {
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                        fieldReads.AppendLine($"    auto* {field.Name}_ptr = ({cppType}*)((char*)context + {offset});");
                        callArgs.Add($"{field.Name}_ptr");
                    }
                }

                sb.Append(fieldReads);
                sb.AppendLine();

                // 调用 Batch 函数（根据所有 bool 条件字段的值选择变体）
                string batchFuncName = GetCppJobFunctionName(jobStruct, isBatch: true);
                if (boolFields.Count > 0)
                {
                    // 读取所有 bool 字段的值
                    var boolValues = new List<string>();
                    foreach (var bf in boolFields)
                    {
                        int boolOffset = GetBoolFieldOffset(jobStruct, bf.Name);
                        string varName = $"__{bf.Name}";
                        sb.AppendLine($"    bool {varName} = *(bool*)((char*)context + {boolOffset});");
                        boolValues.Add(varName);
                    }
                    sb.AppendLine();

                    // 使用 if-else 链选择正确的变体
                    // 生成 2^n 个 if-else 分支
                    int totalVariants = 1 << boolFields.Count;
                    for (int mask = 0; mask < totalVariants; mask++)
                    {
                        var values = new List<bool>();
                        for (int i = 0; i < boolFields.Count; i++)
                            values.Add((mask & (1 << i)) != 0);
                        
                        string suffix = BuildBoolVariantSuffix(boolFields, values);
                        string condition = string.Join(" && ", boolValues.Select((v, i) => values[i] ? v : $"!{v}"));
                        
                        if (mask == 0)
                            sb.AppendLine($"    if ({condition})");
                        else if (mask == totalVariants - 1)
                            sb.AppendLine("    else");
                        else
                            sb.AppendLine($"    else if ({condition})");
                        
                        sb.AppendLine($"        {batchFuncName}{suffix}({string.Join(", ", callArgs)});");
                    }
                }
                else
                {
                    sb.AppendLine($"    {batchFuncName}({string.Join(", ", callArgs)});");
                }
                
                sb.AppendLine("}");
                sb.AppendLine();

                // 生成 Get_XXX_AdapterPtr 导出函数
                sb.AppendLine($"HEAD void* CALLINGCONVENTION Get_{adapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){adapterFuncName};");
                sb.AppendLine("}");
            }
            else
            {
                // IJob（非 ParallelFor）：适配函数签名匹配 JobFunc(void* context)
                // 同样生成适配函数
                sb.AppendLine($"HEAD void CALLINGCONVENTION {adapterFuncName}(void* context)");
                sb.AppendLine("{");
                
                var fieldReads = new StringBuilder();
                var callArgs = new List<string>();
                int currentOffset = 0;
                
                foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    int offset = CalculateFieldOffset(field, ref currentOffset);
                    
                    if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                    {
                        if (field.Type.Name == "NativeList")
                        {
                            fieldReads.AppendLine($"    auto* {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(field.Type)}>**)((char*)context + {offset});");
                            callArgs.Add($"{field.Name}_listData");
                        }
                        else
                        {
                            var cppElemType = GetCppElementType(field.Type);
                            fieldReads.AppendLine($"    auto* {field.Name}_ptr = *({cppElemType}**)((char*)context + {offset});");
                            fieldReads.AppendLine($"    int {field.Name}_length = *(int*)((char*)context + {offset + 8});");
                            callArgs.Add($"{field.Name}_ptr, {field.Name}_length");
                        }
                    }
                    else if (field.Type is IPointerTypeSymbol)
                    {
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                        fieldReads.AppendLine($"    auto* {field.Name}_ptr = *({cppType}*)((char*)context + {offset});");
                        callArgs.Add($"{field.Name}_ptr");
                    }
                    else
                    {
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                        fieldReads.AppendLine($"    auto* {field.Name}_ptr = ({cppType}*)((char*)context + {offset});");
                        callArgs.Add($"{field.Name}_ptr");
                    }
                }

                sb.Append(fieldReads);
                sb.AppendLine();
                
                string singleFuncName = GetCppJobFunctionName(jobStruct);
                sb.AppendLine($"    {singleFuncName}({string.Join(", ", callArgs)});");
                sb.AppendLine("}");
                sb.AppendLine();

                // 生成 Get_XXX_AdapterPtr 导出函数
                sb.AppendLine($"HEAD void* CALLINGCONVENTION Get_{adapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){adapterFuncName};");
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 获取容器类型的元素类型的 C++ 表示
        /// </summary>
        private static string GetCppElementType(ITypeSymbol containerType)
        {
            if (containerType is INamedTypeSymbol named && named.IsGenericType)
            {
                var elemType = named.TypeArguments[0];
                return NativeTranspiler.MapCSharpTypeToCpp(elemType);
            }
            return "void";
        }

        /// <summary>
        /// 获取 bool 条件字段在 job struct 中的偏移量
        /// </summary>
        private static int GetBoolFieldOffset(INamedTypeSymbol jobStruct, string boolFieldName)
        {
            int currentOffset = 0;
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                int offset = CalculateFieldOffset(field, ref currentOffset);
                if (field.Name == boolFieldName)
                    return offset;
            }
            return -1;
        }

        /// <summary>
        /// 获取适配函数的导出函数名（用于 C# 侧 DllImport）
        /// </summary>
        public static string GetAdapterFunctionName(INamedTypeSymbol jobStruct)
        {
            return GetCppJobFunctionName(jobStruct) + "_Adapter";
        }

        /// <summary>
        /// 获取适配函数指针的获取函数名
        /// </summary>
        public static string GetAdapterPtrGetterName(INamedTypeSymbol jobStruct)
        {
            return "Get_" + GetAdapterFunctionName(jobStruct) + "Ptr";
        }
    }
}
