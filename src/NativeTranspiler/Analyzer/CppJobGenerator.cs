using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NativeTranspiler.Analyzer.Common;
using System;

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
            else if (IsEntityJob(jobStruct))
            {
                // auto-SIMD 启用时声明独立函数
                var attrSymbol = compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute");
                var autoSIMD = attrSymbol != null ? AttributeHelper.GetAutoSIMD(jobStruct, attrSymbol) : NativeTranspiler.AutoSIMD.Disabled;
                if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                {
                    var chunkParams = BuildChunkJobParameters(jobStruct);
                    var singleFuncName = GetCppJobFunctionName(jobStruct);
                    sb.AppendLine($"HEAD void CALLINGCONVENTION {singleFuncName}({chunkParams});");
                }
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
            var autoSIMD = AttributeHelper.GetAutoSIMD(jobStruct, attrSymbol);
            var simdMathPrecision = AttributeHelper.GetMathPrecision(jobStruct, attrSymbol);
            sb.AppendLine($"#include \"{baseFuncName}.h\"");
            sb.AppendLine("#include <algorithm>");
            sb.AppendLine("#include <cmath>");
            sb.AppendLine("#include <cstdio>");
            sb.AppendLine("#include \"../../NativeDll/NativeSIMD.h\"");
            sb.AppendLine("#include \"../../NativeDll/SimdValue.h\"");
            sb.AppendLine();

            // IJobChunk: 生成独立 Execute 函数
            if (IsChunkJob(jobStruct))
            {
                if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                    GenerateChunkFunctionSIMD(jobStruct, compilation, sb, useFastMath, simdMathPrecision);
                else
                    GenerateChunkFunctionStandard(jobStruct, compilation, sb, useFastMath);
            }
            // IJobEntity: 无独立 Execute，循环体内联到 Adapter 中
            else if (IsEntityJob(jobStruct))
            {
                if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                {
                    GenerateEntityFunctionStandard(jobStruct, compilation, sb, useFastMath);
                }
                // auto-SIMD 关闭时：不生成独立函数，体量内联到适配器
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
                        GenerateBatchFunctionVariant(jobStruct, boolFields, values, semanticModel, methodSyntax, sb, useFastMath, autoSIMD, simdMathPrecision);
                    }
                }
                else
                {
                    GenerateBatchFunctionStandard(jobStruct, semanticModel, methodSyntax, sb, useFastMath, autoSIMD, simdMathPrecision);
                }
            }
            else
            {
                GenerateSingleFunctionStandard(jobStruct, compilation, sb, useFastMath, autoSIMD, simdMathPrecision);
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

        private static void GenerateBatchFunctionStandard(INamedTypeSymbol jobStruct, SemanticModel semanticModel, MethodDeclarationSyntax methodSyntax, StringBuilder sb, bool useFastMath, NativeTranspiler.AutoSIMD autoSIMD = NativeTranspiler.AutoSIMD.Disabled, NativeTranspiler.SimdMathPrecision simdMathPrecision = NativeTranspiler.SimdMathPrecision.Fastest)
        {
            string funcName = GetCppJobFunctionName(jobStruct, isBatch: true);
            string paramsStr = BuildBatchJobParameters(jobStruct);
            sb.AppendLine($"HEAD void CALLINGCONVENTION {funcName}({paramsStr})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb);
            var indexParamName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;

            // 先用标量翻译器翻译 body（余量循环需要标量体）
            var scalarTranslator = new CppBatchStatementTranslator(semanticModel, jobStruct, indexParamName, indexParamName, useFastMath, /* scalar body, no SIMD */ false);
            var scalarBody = scalarTranslator.Translate(methodSyntax.Body);

            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
            {
                // Per-lane SIMD: gather queries once, extract to scalar for each lane
                var simdGen = new OuterSimdGenerator(methodSyntax, semanticModel, indexParamName, jobStruct: jobStruct, simdMathPrecision: simdMathPrecision);
                var simdCode = simdGen.Generate(scalarBody);
                sb.Append(simdCode);
                sb.AppendLine("}");
                sb.AppendLine();
                return;
            }

            // 回退标量路径
            sb.AppendLine($"    #pragma loop(ivdep)");
            sb.AppendLine($"    #pragma loop(vector)");
            sb.AppendLine($"    #pragma loop(unroll(4))");
            sb.AppendLine($"    for (int {indexParamName} = __startIndex; {indexParamName} < __startIndex + __count; ++{indexParamName})");
            sb.AppendLine("    {");
            sb.Append(scalarBody);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        private static void GenerateBatchFunctionVariant(INamedTypeSymbol jobStruct, List<IFieldSymbol> boolFields, List<bool> values, SemanticModel semanticModel, MethodDeclarationSyntax methodSyntax, StringBuilder sb, bool useFastMath, NativeTranspiler.AutoSIMD autoSIMD = NativeTranspiler.AutoSIMD.Disabled, NativeTranspiler.SimdMathPrecision simdMathPrecision = NativeTranspiler.SimdMathPrecision.Fastest)
        {
            string suffix = BuildBoolVariantSuffix(boolFields, values);
            string funcName = GetCppJobFunctionName(jobStruct, isBatch: true) + suffix;
            string paramsStr = BuildBatchJobParameters(jobStruct);
            sb.AppendLine($"HEAD void CALLINGCONVENTION {funcName}({paramsStr})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb);
            var indexParamName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;

            // 先用标量翻译器翻译 body + 替换 bool 常量
            bool scalar_noSIMD = false;
            var translator = new CppBatchStatementTranslator(semanticModel, jobStruct, indexParamName, indexParamName, useFastMath, scalar_noSIMD);
            var bodyCode = translator.Translate(methodSyntax.Body);
            for (int i = 0; i < boolFields.Count; i++)
            {
                string constantLiteral = values[i] ? "true" : "false";
                string pattern = $@"{Regex.Escape(boolFields[i].Name)}";
                bodyCode = Regex.Replace(bodyCode, pattern, constantLiteral);
            }

            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
            {
                // Per-lane SIMD: gather queries once, extract to scalar for each lane
                // Works for any Job body — no eligibility check needed.
                var boolFieldValues = new System.Collections.Generic.Dictionary<string, string>();
                for (int i_ = 0; i_ < boolFields.Count; i_++)
                    boolFieldValues[boolFields[i_].Name] = values[i_] ? "true" : "false";
                var simdGen = new OuterSimdGenerator(methodSyntax, semanticModel, indexParamName, boolFieldValues, jobStruct, simdMathPrecision);
                var simdCode = simdGen.Generate(bodyCode);
                sb.Append(simdCode);
                sb.AppendLine("}");
                sb.AppendLine();
                return;
            }

            // 标量回退
            sb.AppendLine($"    #pragma loop(ivdep)");
            sb.AppendLine($"    #pragma loop(vector)");
            sb.AppendLine($"    #pragma loop(unroll(4))");
            sb.AppendLine($"    for (int {indexParamName} = __startIndex; {indexParamName} < __startIndex + __count; ++{indexParamName})");
            sb.AppendLine("    {");
            sb.Append(bodyCode);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine();
        }

        private static void GenerateSingleFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath,
            NativeTranspiler.AutoSIMD autoSIMD = NativeTranspiler.AutoSIMD.Disabled,
            NativeTranspiler.SimdMathPrecision simdMathPrecision = NativeTranspiler.SimdMathPrecision.Fastest)
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

                if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                {
                    // IJob/static: no OuterSimdGenerator batch wrapper, use SimdControlFlowGenerator directly
                    // Inner for-loops, if-else, gather-blend all become SIMD via mask management.
                    // Output writes with varying index → per-lane scatter; uniform index → extract lane 0.
                    var varAnalyzer = new SimdVariableAnalyzer(semanticModel, jobStruct, "");
                    var variables = varAnalyzer.Analyze(methodSyntax);
                    var simdGen = new SimdControlFlowGenerator(
                        semanticModel, jobStruct, variables, varAnalyzer,
                        indexParamName: "", simdIndexVar: "v_i",
                        batchOffsetVar: "0",
                        simdMathPrecision: simdMathPrecision);
                    var simdBody = simdGen.Generate(methodSyntax.Body);
                    sb.Append(simdBody);
                }
                else
                {
                    var translator = new CppPointerStatementTranslator(semanticModel, jobStruct, useFastMath);
                    var bodyCode = translator.Translate(methodSyntax.Body);
                    sb.Append(bodyCode);
                }
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
                sb.AppendLine($"    __assume((intptr_t)__entity_param_{i}_ptr % 64 == 0);");
            }

            // Pre-translate scalar body
            string scalarBody = "";
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var translator = new CppStatementTranslator(semanticModel, useFastMath);
                scalarBody = translator.Translate(methodSyntax.Body);
            }

            // Generate per-lane SIMD wrapper + remainder loop
            sb.AppendLine("    int __entity_count = __chunkData->entityCount;");
            sb.AppendLine("    int __simd_end = (__entity_count / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine("    if (__simd_end > 0)");
            sb.AppendLine("    {");
            sb.AppendLine("        for (int si = 0; si < __simd_end; si += NSIMD_WIDTH)");
            sb.AppendLine("        {");
            sb.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
            sb.AppendLine("            {");
            sb.AppendLine("                int __entity_index = si + lane;");
            foreach (var param in executeMethod.Parameters.Select((p, i) => (p, i)))
            {
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.p.Type);
                string constPrefix = param.p.RefKind == RefKind.In ? "const " : "";
                sb.AppendLine($"                {constPrefix}{cppType}& {param.p.Name} = __entity_param_{param.i}_ptr[__entity_index];");
            }
            // Append scalar body (with return; → break; for per-lane context)
            bool hasReturn = scalarBody.Contains("return;");
            if (hasReturn)
            {
                sb.AppendLine("                do {");
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append("                    ").AppendLine(line.Replace("return;", "break;"));
                }
                sb.AppendLine("                } while(false);");
            }
            else
            {
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append("                ").AppendLine(line);
                }
            }
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            // Remainder scalar loop
            sb.AppendLine("    for (int __entity_index = __simd_end; __entity_index < __entity_count; ++__entity_index)");
            sb.AppendLine("    {");
            foreach (var param in executeMethod.Parameters.Select((p, i) => (p, i)))
            {
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.p.Type);
                string constPrefix = param.p.RefKind == RefKind.In ? "const " : "";
                sb.AppendLine($"        {constPrefix}{cppType}& {param.p.Name} = __entity_param_{param.i}_ptr[__entity_index];");
            }
            if (hasReturn)
            {
                sb.AppendLine("        do {");
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append("            ").AppendLine(line.Replace("return;", "break;"));
                }
                sb.AppendLine("        } while(false);");
            }
            else
            {
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append("        ").AppendLine(line);
                }
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

        private static void AppendEntityBatchAdapter(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath, NativeTranspiler.AutoSIMD autoSIMD = NativeTranspiler.AutoSIMD.Disabled)
        {
            var adapterFuncName = GetEntityBatchAdapterFunctionName(jobStruct);
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);

            sb.AppendLine($"HEAD void CALLINGCONVENTION {adapterFuncName}(void* context, const EntityBatchData* __batches, int __batch_start, int __batch_count)");
            sb.AppendLine("{");
            sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
            sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
            sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
            sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
            sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");

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

            sb.AppendLine("    const int __batch_end = __batch_start + __batch_count;");
            sb.AppendLine("    for (int __batch_index = __batch_start; __batch_index < __batch_end; ++__batch_index)");
            sb.AppendLine("    {");
            sb.AppendLine("        const EntityBatchData* __batchData = &__batches[__batch_index];");
            for (int i = 0; i < executeMethod.Parameters.Length; i++)
            {
                var param = executeMethod.Parameters[i];
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                string constPrefix = param.RefKind == RefKind.In ? "const " : "";
                sb.AppendLine($"        {constPrefix}auto* RESTRICT __entity_param_{i}_ptr = reinterpret_cast<{constPrefix}{cppType}*>(__batchData->componentArrays[{i}]);");
                sb.AppendLine($"        __assume((intptr_t)__entity_param_{i}_ptr % 64 == 0);");
            }

            // Pre-translate scalar body
            string scalarBody = "";
            string adaptedBody = "";
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var translator = new CppStatementTranslator(semanticModel, useFastMath);
                scalarBody = translator.Translate(methodSyntax.Body);
                // Replace param.Name. references with __entity_param_N_ptr[__entity_index].
                adaptedBody = scalarBody;
                foreach (var param in executeMethod.Parameters.Select((p, i) => (p, i)))
                {
                    string indexedParam = $"__entity_param_{param.i}_ptr[__entity_index]";
                    adaptedBody = Regex.Replace(adaptedBody, $@"\b{Regex.Escape(param.p.Name)}\.", indexedParam + ".");
                }
            }

            bool hasReturn = adaptedBody.Contains("return;");

            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
            {
                // Per-lane SIMD entity loop
                sb.AppendLine("        int __entity_count = __batchData->entityCount;");
                sb.AppendLine("        int __simd_end = (__entity_count / NSIMD_WIDTH) * NSIMD_WIDTH;");
                sb.AppendLine("        if (__simd_end > 0)");
                sb.AppendLine("        {");
                sb.AppendLine("            for (int si = 0; si < __simd_end; si += NSIMD_WIDTH)");
                sb.AppendLine("            {");
                sb.AppendLine("                for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
                sb.AppendLine("                {");
                sb.AppendLine("                    int __entity_index = si + lane;");
                if (hasReturn)
                {
                    sb.AppendLine("                    do {");
                    foreach (var line in adaptedBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    {
                        string trimmed = line.TrimEnd();
                        if (trimmed.Length == 0) continue;
                        sb.AppendLine($"                        {trimmed.Replace("return;", "break;")}");
                    }
                    sb.AppendLine("                    } while(false);");
                }
                else
                {
                    foreach (var line in adaptedBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    {
                        string trimmed = line.TrimEnd();
                        if (trimmed.Length == 0) continue;
                        sb.AppendLine($"                        {trimmed}");
                    }
                }
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                // Remainder scalar loop
                sb.AppendLine("        for (int __entity_index = __simd_end; __entity_index < __batchData->entityCount; ++__entity_index)");
                sb.AppendLine("        {");
                if (hasReturn)
                {
                    sb.AppendLine("            do {");
                    foreach (var line in adaptedBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    {
                        string trimmed = line.TrimEnd();
                        if (trimmed.Length == 0) continue;
                        sb.AppendLine($"                {trimmed.Replace("return;", "break;")}");
                    }
                    sb.AppendLine("            } while(false);");
                }
                else
                {
                    foreach (var line in adaptedBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    {
                        string trimmed = line.TrimEnd();
                        if (trimmed.Length == 0) continue;
                        sb.AppendLine($"                {trimmed}");
                    }
                }
                sb.AppendLine("        }");
            }
            else
            {
                // Scalar entity loop
                sb.AppendLine("        int __entity_count = __batchData->entityCount;");
                sb.AppendLine("        #pragma loop(ivdep)");
                sb.AppendLine("        #pragma loop(vector)");
                sb.AppendLine("        #pragma unroll(4)");
                sb.AppendLine("        for (int __entity_index = 0; __entity_index < __entity_count; ++__entity_index)");
                sb.AppendLine("        {");
                foreach (var line in adaptedBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.AppendLine($"            {line}");
                }
                sb.AppendLine("        }");
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"HEAD void* CALLINGCONVENTION Get_{adapterFuncName}Ptr()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){adapterFuncName};");
            sb.AppendLine("}");
        }

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
                _ => type is INamedTypeSymbol namedType && namedType.IsValueType && namedType.TypeKind != TypeKind.Enum
                    ? GetStructSizeRecursive(namedType) : 4 // 默认
            };
        }

        /// <summary>
        /// 递归计算 struct 类型的大小（按 Sequential 布局）
        /// </summary>
        private static int GetStructSizeRecursive(INamedTypeSymbol structType)
        {
            int maxAlignment = 1;
            int offset = 0;
            foreach (var member in structType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                int fieldSize = GetCSharpFieldSize(member.Type);
                int fieldAlignment = GetCSharpFieldAlignment(member.Type);
                if (fieldAlignment > maxAlignment) maxAlignment = fieldAlignment;
                // 对齐
                offset = (offset + fieldAlignment - 1) / fieldAlignment * fieldAlignment;
                offset += fieldSize;
            }
            // 最终对齐到结构体自身对齐要求
            offset = (offset + maxAlignment - 1) / maxAlignment * maxAlignment;
            return Math.Max(1, offset); // C# struct 最小为 1
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
                _ => type is INamedTypeSymbol namedType && namedType.IsValueType
                    ? GetStructAlignmentRecursive(namedType) : 4
            };
        }

        /// <summary>
        /// 递归计算 struct 类型的对齐要求（字段对齐的 max）
        /// </summary>
        private static int GetStructAlignmentRecursive(INamedTypeSymbol structType)
        {
            int maxAlign = 1;
            foreach (var member in structType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                int align = GetCSharpFieldAlignment(member.Type);
                if (align > maxAlign) maxAlign = align;
            }
            return maxAlign;
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
                sb.AppendLine("#include \"../../NativeDll/EntityBatchData.h\"");
                foreach (var include in CollectJobStructIncludes(jobStruct, compilation))
                    sb.AppendLine($"#include \"{include}.h\"");
            }
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();

            // 检查是否为 ISPC job 和 auto-SIMD
            var attrSymbol = AttributeHelper.GetAttributeSymbol(compilation);
            var autoSIMD = attrSymbol != null
                ? AttributeHelper.GetAutoSIMD(jobStruct, attrSymbol)
                : NativeTranspiler.AutoSIMD.Disabled;
            bool isIspcJob = attrSymbol != null && 
                AttributeHelper.GetBackendTarget(jobStruct, attrSymbol) == NativeTranspiler.BackendTarget.Ispc;

            if (isIspcJob)
            {
                // ISPC job: 声明 wrapper 函数为 extern（在 wrapper.cpp 中实现）
                bool isp_batch = IsParallelForJob(jobStruct) || IsForJob(jobStruct);
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);

                if (isp_batch)
                {
                    var batchFuncName = GetCppJobFunctionName(jobStruct, isBatch: true);
                    var batchParams = BuildBatchJobParameters(jobStruct);
                    sb.AppendLine($"// ISPC wrapper function (defined in wrapper.cpp)");
                    GenerateBoolVariantDeclarations(jobStruct, boolFields, batchFuncName, batchParams, sb);
                }
                else
                {
                    // IJob: non-batch wrapper
                    var singleFuncName = GetCppJobFunctionName(jobStruct, isBatch: false);
                    var singleParams = BuildJobParameters(jobStruct);
                    sb.AppendLine($"// ISPC wrapper function (defined in wrapper.cpp)");
                    sb.AppendLine($"HEAD void CALLINGCONVENTION {singleFuncName}({singleParams});");
                }
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

                if (isEntityJob)
                {
                    AppendEntityBatchAdapter(jobStruct, compilation, sb, useFastMath, autoSIMD);
                }
                else
                {
                sb.AppendLine($"HEAD void CALLINGCONVENTION {adapterFuncName}(void* context, const ChunkJobData* __chunkData)");
                sb.AppendLine("{");
                sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
                sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
                sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
                sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
                sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
                sb.AppendLine("    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;");

                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
                var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);

                if (isEntityJob)
                {
                    // IJobEntity: 直接在 Adapter 内联循环体
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

                    for (int i = 0; i < executeMethod.Parameters.Length; i++)
                    {
                        var param = executeMethod.Parameters[i];
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                        sb.AppendLine($"    auto* RESTRICT __entity_param_{i}_ptr = reinterpret_cast<{cppType}*>(__chunkData->requiredComponentArrays[{i}]);");
                sb.AppendLine($"    __assume((intptr_t)__entity_param_{i}_ptr % 64 == 0);");
                    }

                    string _scalarBody2 = "";
                    if (methodSyntax?.Body != null)
                    {
                        var _sm2 = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                        var _tr2 = new CppStatementTranslator(_sm2, useFastMath);
                        _scalarBody2 = _tr2.Translate(methodSyntax.Body);
                    }
                    bool _hasRet2 = _scalarBody2.Contains("return;");

                    if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                    {
                        sb.AppendLine("    int __entity_count = __chunkData->entityCount;");
                        sb.AppendLine("    int __simd_end = (__entity_count / NSIMD_WIDTH) * NSIMD_WIDTH;");
                        sb.AppendLine("    if (__simd_end > 0)");
                        sb.AppendLine("    {");
                        sb.AppendLine("        for (int si = 0; si < __simd_end; si += NSIMD_WIDTH)");
                        sb.AppendLine("        {");
                        sb.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
                        sb.AppendLine("            {");
                        sb.AppendLine("                int __entity_index = si + lane;");
                        foreach (var param in executeMethod.Parameters.Select((p, i) => (p, i)))
                        {
                            var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.p.Type);
                            string constPrefix = param.p.RefKind == RefKind.In ? "const " : "";
                            sb.AppendLine($"                {constPrefix}{cppType}& {param.p.Name} = __entity_param_{param.i}_ptr[__entity_index];");
                        }
                        if (_hasRet2)
                        {
                            sb.AppendLine("                do {");
                            foreach (var _l2 in _scalarBody2.Split('\n'))
                            { if (_l2.Trim().Length == 0) continue; sb.AppendLine($"                    {_l2.TrimEnd().Replace("return;", "break;")}"); }
                            sb.AppendLine("                } while(false);");
                        }
                        else
                        {
                            foreach (var _l2 in _scalarBody2.Split('\n'))
                            { if (_l2.Trim().Length == 0) continue; sb.AppendLine($"                {_l2.TrimEnd()}"); }
                        }
                        sb.AppendLine("            }");
                        sb.AppendLine("        }");
                        sb.AppendLine("    }");
                        sb.AppendLine("    for (int __entity_index = __simd_end; __entity_index < __chunkData->entityCount; ++__entity_index)");
                        sb.AppendLine("    {");
                        foreach (var param in executeMethod.Parameters.Select((p, i) => (p, i)))
                        {
                            var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.p.Type);
                            string constPrefix = param.p.RefKind == RefKind.In ? "const " : "";
                            sb.AppendLine($"        {constPrefix}{cppType}& {param.p.Name} = __entity_param_{param.i}_ptr[__entity_index];");
                        }
                        if (_hasRet2)
                        {
                            sb.AppendLine("        do {");
                            foreach (var _l2 in _scalarBody2.Split('\n'))
                            { if (_l2.Trim().Length == 0) continue; sb.AppendLine($"            {_l2.TrimEnd().Replace("return;", "break;")}"); }
                            sb.AppendLine("        } while(false);");
                        }
                        else
                        {
                            foreach (var _l2 in _scalarBody2.Split('\n'))
                            { if (_l2.Trim().Length == 0) continue; sb.AppendLine($"            {_l2.TrimEnd()}"); }
                        }
                        sb.AppendLine("    }");
                    }
                    else
                    {
                        sb.AppendLine("    int __entity_count = __chunkData->entityCount;");
                        sb.AppendLine("    #pragma loop(ivdep)");
                        sb.AppendLine("    #pragma loop(vector)");
                        sb.AppendLine("    #pragma unroll(4)");
                        sb.AppendLine("    for (int __entity_index = 0; __entity_index < __entity_count; ++__entity_index)");
                        sb.AppendLine("    {");
                        foreach (var param in executeMethod.Parameters.Select((p, i) => (p, i)))
                        {
                            var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.p.Type);
                            string constPrefix = param.p.RefKind == RefKind.In ? "const " : "";
                            sb.AppendLine($"        {constPrefix}{cppType}& {param.p.Name} = __entity_param_{param.i}_ptr[__entity_index];");
                        }
                        foreach (var _l2 in _scalarBody2.Split('\n'))
                        { if (_l2.Trim().Length == 0) continue; sb.AppendLine($"        {_l2.TrimEnd()}"); }
                        sb.AppendLine("    }");
                    }
                }
                else
                {
                    // IJobChunk: 内联 Execute 循环体

                    // 解包作业字段到局部变量
                    int currentOffset = 0;
                    foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    {
                        int offset = CalculateFieldOffset(field, ref currentOffset);

                        if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                        {
                            if (field.Type.Name == "NativeList")
                            {
                                sb.AppendLine($"    auto* {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(field.Type)}>**)(__jobContext + {offset});");
                            }
                            else
                            {
                                var cppElemType = GetCppElementType(field.Type);
                                sb.AppendLine($"    auto* {field.Name}_ptr = *({cppElemType}**)(__jobContext + {offset});");
                                sb.AppendLine($"    int {field.Name}_length = *(int*)(__jobContext + {offset + 8});");
                            }
                        }
                        else if (field.Type is IPointerTypeSymbol)
                        {
                            var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                            sb.AppendLine($"    auto* {field.Name}_ptr = *({cppType}*)(__jobContext + {offset});");
                        }
                        else
                        {
                            var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                            sb.AppendLine($"    auto* {field.Name}_ptr = ({cppType}*)(__jobContext + {offset});");
                        }
                    }

                    // 将字段指针解引用为局部变量引用（原独立 Execute 函数中由 AppendLocalVariableDeclarations 完成）
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

                    // Auto-SIMD: 调用独立函数而非内联
                    if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                    {
                        string funcName = GetCppJobFunctionName(jobStruct);
                        string callArgs = BuildChunkExecuteCallArgs(jobStruct);
                        sb.AppendLine($"    {funcName}({callArgs});");
                    }
                    else
                    {
                        // 内联 Execute 函数体（如同 IJobEntity 的做法）
                        if (methodSyntax?.Body != null)
                        {
                            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                            var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
                            var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, useFastMath);
                            var bodyCode = translator.Translate(methodSyntax.Body);
                            foreach (var line in bodyCode.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                            {
                                if (line.Length == 0) continue;
                                sb.Append("    ").AppendLine(line);
                            }
                        }
                    }
                }

                sb.AppendLine("}");
                sb.AppendLine();

                sb.AppendLine($"HEAD void* CALLINGCONVENTION Get_{adapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){adapterFuncName};");
                sb.AppendLine("}");
                sb.AppendLine();

                var rangeAdapterFuncName = GetRangeAdapterFunctionName(jobStruct);
                sb.AppendLine($"HEAD void CALLINGCONVENTION {rangeAdapterFuncName}(void* context, const ChunkJobData* __chunks, int __startIndex, int __count)");
                sb.AppendLine("{");
                // 内联 Adapter：将 header + job 字段提至循环外
                sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
                sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
                sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
                sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
                sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
                sb.AppendLine("    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;");
                // job field 指针
                int rOff = 0;
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    int off = CalculateFieldOffset(f, ref rOff);
                    if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type))
                    {
                        if (f.Type.Name == "NativeList")
                            sb.AppendLine($"    auto* {f.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(f.Type)}>**)(__jobContext + {off});");
                        else
                        {
                            var e = GetCppElementType(f.Type);
                            sb.AppendLine($"    auto* {f.Name}_ptr = *({e}**)(__jobContext + {off});");
                            sb.AppendLine($"    int {f.Name}_length = *(int*)(__jobContext + {off + 8});");
                        }
                    }
                    else if (f.Type is IPointerTypeSymbol)
                    {
                        var t = NativeTranspiler.MapCSharpTypeToCpp(f.Type);
                        sb.AppendLine($"    auto* {f.Name}_ptr = *({t}*)(__jobContext + {off});");
                    }
                    else
                    {
                        var t = NativeTranspiler.MapCSharpTypeToCpp(f.Type);
                        sb.AppendLine($"    auto* {f.Name}_ptr = ({t}*)(__jobContext + {off});");
                    }
                }
                // field refs
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type) && f.Type.Name == "NativeList")
                    { var e = ((INamedTypeSymbol)f.Type).TypeArguments[0]; var c = NativeTranspiler.MapCSharpTypeToCpp(e); sb.AppendLine($"    EntJoy::Collections::UnsafeList<{c}>& {f.Name} = *{f.Name}_listData;"); }
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type)) continue;
                    if (f.Type is IPointerTypeSymbol) continue;
                    sb.AppendLine($"    const {NativeTranspiler.MapCSharpTypeToCpp(f.Type)}& {f.Name} = *{f.Name}_ptr;");
                }
                sb.AppendLine("    const int __endIndex = __startIndex + __count;");
                sb.AppendLine("    for (int __chunkIndex = __startIndex; __chunkIndex < __endIndex; ++__chunkIndex)");
                sb.AppendLine("    {");
                sb.AppendLine("        auto* __chunkData = &__chunks[__chunkIndex];");
                // inline the adapter body into range loop
                if (isEntityJob)
                {
                    for (int i = 0; i < executeMethod.Parameters.Length; i++)
                    {
                        var param = executeMethod.Parameters[i];
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                        sb.AppendLine($"        auto* RESTRICT __entity_param_{i}_ptr = reinterpret_cast<{cppType}*>(__chunkData->requiredComponentArrays[{i}]);");
                        sb.AppendLine($"        __assume((intptr_t)__entity_param_{i}_ptr % 64 == 0);");
                    }
                    sb.AppendLine("        int __entity_count = __chunkData->entityCount;");
                    sb.AppendLine("        #pragma loop(ivdep)");
                sb.AppendLine("        #pragma loop(vector)");
                sb.AppendLine("        #pragma unroll(4)");
                    sb.AppendLine("        for (int __entity_index = 0; __entity_index < __entity_count; ++__entity_index)");
                    sb.AppendLine("        {");
                    foreach (var (p, i) in executeMethod.Parameters.Select((p, i) => (p, i)))
                    {
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(p.Type);
                        string constPrefix = p.RefKind == RefKind.In ? "const " : "";
                        sb.AppendLine($"            {constPrefix}{cppType}& {p.Name} = __entity_param_{i}_ptr[__entity_index];");
                    }
                    if (methodSyntax?.Body != null)
                    {
                        var sm = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                        var tr = new CppStatementTranslator(sm, useFastMath);
                        foreach (var l in tr.Translate(methodSyntax.Body).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                            if (l.Length > 0) sb.Append("            ").AppendLine(l);
                    }
                    sb.AppendLine("        }");
                }
                else
                {
                    // IJobChunk: Range adapter inline Execute body
                    if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                    {
                        string funcName = GetCppJobFunctionName(jobStruct);
                        string fieldArgs = BuildChunkExecuteFieldArgs(jobStruct);
                        sb.AppendLine($"        {funcName}(&__chunks[__chunkIndex], __requiredComponentTypeIds, {fieldArgs});");
                    }
                    else
                    {
                        if (methodSyntax?.Body != null)
                        {
                            var sm = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                            var rt = CollectChunkNativeArrayTypes(jobStruct, compilation);
                            var tr = new CppChunkStatementTranslator(sm, jobStruct, rt, useFastMath);
                            foreach (var l in tr.Translate(methodSyntax.Body).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                                if (l.Length > 0) sb.Append("        ").AppendLine(l);
                        }
                    }
                }
                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"HEAD void* CALLINGCONVENTION Get_{rangeAdapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){rangeAdapterFuncName};");
                sb.AppendLine("}");

                // ★ Unity 风格 EntityBatch 适配器（替代 ChunkJobData 间接层）
                // 接收 EntityBatchData* 而非 ChunkJobData*，消除 requiredComponentArrays 指针追访
                // EntityBatchData 只含 componentArrays + entityCount，共 16 字节
                // 比 ChunkJobData（72 字节）更紧凑，cache 效率更高
                var entityBatchAdapterFuncName = GetEntityBatchAdapterFunctionName(jobStruct);
                var entityBatchHeader = $@"HEAD void CALLINGCONVENTION {entityBatchAdapterFuncName}(void* context, const EntityBatchData* __batches, int __startIndex, int __count)
{{
    auto* __header = (__EntJoyChunkContextHeader*)context;
    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);
    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);
    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);
    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;";
                sb.Append(entityBatchHeader);
                sb.AppendLine();
                // job field 指针（从 RangeAdapter 复制）
                rOff = 0;
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    int off = CalculateFieldOffset(f, ref rOff);
                    if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type))
                    {
                        if (f.Type.Name == "NativeList")
                            sb.AppendLine($"    auto* {f.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(f.Type)}>**)(__jobContext + {off});");
                        else
                        {
                            var e = GetCppElementType(f.Type);
                            sb.AppendLine($"    auto* {f.Name}_ptr = *({e}**)(__jobContext + {off});");
                            sb.AppendLine($"    int {f.Name}_length = *(int*)(__jobContext + {off + 8});");
                        }
                    }
                    else if (f.Type is IPointerTypeSymbol)
                    {
                        var t = NativeTranspiler.MapCSharpTypeToCpp(f.Type);
                        sb.AppendLine($"    auto* {f.Name}_ptr = *({t}*)(__jobContext + {off});");
                    }
                    else
                    {
                        var t = NativeTranspiler.MapCSharpTypeToCpp(f.Type);
                        sb.AppendLine($"    auto* {f.Name}_ptr = ({t}*)(__jobContext + {off});");
                    }
                }
                // field refs
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type) && f.Type.Name == "NativeList")
                    { var e = ((INamedTypeSymbol)f.Type).TypeArguments[0]; var c = NativeTranspiler.MapCSharpTypeToCpp(e); sb.AppendLine($"    EntJoy::Collections::UnsafeList<{c}>& {f.Name} = *{f.Name}_listData;"); }
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type)) continue;
                    if (f.Type is IPointerTypeSymbol) continue;
                    sb.AppendLine($"    const {NativeTranspiler.MapCSharpTypeToCpp(f.Type)}& {f.Name} = *{f.Name}_ptr;");
                }
                sb.AppendLine("    const int __endIndex = __startIndex + __count;");
                sb.AppendLine("    for (int __batchIndex = __startIndex; __batchIndex < __endIndex; ++__batchIndex)");
                sb.AppendLine("    {");
                sb.AppendLine("        const EntityBatchData* __batchData = &__batches[__batchIndex];");
                if (methodSyntax?.Body != null)
                {
                    var sm = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                    var rt = CollectChunkNativeArrayTypes(jobStruct, compilation);
                    var tr = new CppChunkStatementTranslator(sm, jobStruct, rt, useFastMath);
                    var bodyCode = tr.Translate(methodSyntax.Body);
                    bodyCode = bodyCode.Replace("__chunkData->requiredComponentArrays", "__batchData->componentArrays");
                    bodyCode = bodyCode.Replace("__chunkData->entityCount", "__batchData->entityCount");
                    foreach (var l in bodyCode.Split(new[] { "\n" }, StringSplitOptions.None))
                    {
                        if (l.Length > 0) sb.Append("        ").AppendLine(l);
                        string trimmed = l.TrimStart();
                        if (trimmed.StartsWith("auto*") && trimmed.Contains("reinterpret_cast<"))
                        {
                            string varName = trimmed.Split('=')[0].Trim().Split(' ').Last();
                            sb.AppendLine($"        __assume((intptr_t){varName} % 64 == 0);");
                        }
                    }
                }
                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"HEAD void* CALLINGCONVENTION Get_{entityBatchAdapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){entityBatchAdapterFuncName};");
                sb.AppendLine("}");

                }
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

        public static string GetRangeAdapterFunctionName(INamedTypeSymbol jobStruct)
        {
            return GetCppJobFunctionName(jobStruct) + "_RangeAdapter";
        }

        // ===================================================================
        // IJobChunk Auto-SIMD: Preprocess AST
        // ===================================================================

        /// <summary>
        /// 预处理 IJobChunk 的 Execute AST 用于 SIMD 生成：
        /// 1. 收集 chunk 数组声明（GetComponentDataNativeArray/GetComponentDataSpan）
        /// 2. 找到实体 for-loop
        /// 3. SyntaxRewriter: 删除 chunk 数组声明、删除实体 for-loop 头、替换 chunk.Count
        /// </summary>
        private static (List<(string name, string elemType, int compIndex)> chunkArrays,
                        string entityLoopIv,
                        BlockSyntax modifiedBody)
            PreprocessIJobChunkAST(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel,
                                   INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var chunkArrays = new List<(string name, string elemType, int compIndex)>();
            var chunkArrayNames = new HashSet<string>();
            string chunkParamName = methodSyntax.ParameterList.Parameters.Count > 0
                ? methodSyntax.ParameterList.Parameters[0].Identifier.Text
                : "chunk";

            // 1. Scan for GetComponentDataNativeArray / GetComponentDataSpan calls
            var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
            foreach (var localDecl in methodSyntax.Body?.DescendantNodes().OfType<LocalDeclarationStatementSyntax>() ?? Enumerable.Empty<LocalDeclarationStatementSyntax>())
            {
                foreach (var variable in localDecl.Declaration.Variables)
                {
                    if (variable.Initializer?.Value is InvocationExpressionSyntax inv)
                    {
                        var symbol = semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                        if (symbol != null &&
                            symbol.ContainingType?.ToDisplayString() == "EntJoy.ArchetypeChunk" &&
                            (symbol.Name == "GetComponentDataNativeArray" || symbol.Name == "GetComponentDataSpan") &&
                            symbol.TypeArguments.Length > 0)
                        {
                            string varName = variable.Identifier.Text;
                            var compType = symbol.TypeArguments[0] as INamedTypeSymbol;
                            if (compType != null)
                            {
                                int idx = requiredTypes.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, compType));
                                string elemCppType = NativeTranspiler.MapCSharpTypeToCpp(compType);
                                chunkArrays.Add((varName, elemCppType, idx));
                                chunkArrayNames.Add(varName);
                            }
                        }
                    }
                }
            }

            // 2. Find entity for-loop
            string entityLoopIv = "";
            StatementSyntax? loopBody = null;
            if (methodSyntax.Body != null)
            {
                foreach (var stmt in methodSyntax.Body.Statements)
                {
                    if (stmt is ForStatementSyntax forStmt &&
                        forStmt.Declaration?.Variables.Count == 1)
                    {
                        var decl = forStmt.Declaration.Variables[0];
                        string ivName = decl.Identifier.Text;

                        // Check if this loop's body has element access on chunk arrays
                        bool hasChunkAccess = forStmt.DescendantNodes()
                            .OfType<ElementAccessExpressionSyntax>()
                            .Any(ea => ea.Expression is IdentifierNameSyntax id
                                      && chunkArrayNames.Contains(id.Identifier.Text));

                        if (hasChunkAccess)
                        {
                            entityLoopIv = ivName;
                            loopBody = forStmt.Statement;
                            break;
                        }
                    }
                }
            }

            // 3. Apply SyntaxRewriter (remove chunk decls, for-loop header, replace chunk.Count)
            var rewriter = new IJobChunkSimdRewriter(chunkArrayNames, chunkParamName, entityLoopIv);
            var afterFirstPass = methodSyntax.Body != null
                ? (BlockSyntax)rewriter.Visit(methodSyntax.Body)
                : methodSyntax.Body;

            // 4. Decompose struct read-modify-write pattern (ISPC-style: eliminate intermediate struct locals)
            //    Detects: StructType temp = array[idx]; temp.Field += ...; array[idx] = temp;
            //    Rewrites to: array[idx].Field += ...;
            var decomposedBody = DecomposeStructLocals(afterFirstPass, chunkArrayNames, entityLoopIv);

            return (chunkArrays, entityLoopIv, decomposedBody);
        }

        /// <summary>
        /// Decompose struct read-modify-write pattern into direct field access.
        /// Replaces:
        ///   StructType temp = array[idx];   → removed
        ///   temp.Field += rhs;               → array[idx].Field += rhs
        ///   array[idx] = temp;               → removed
        /// This enables SimdControlFlowGenerator to handle struct field access
        /// directly via field-level gather/scatter (ISPC-style).
        /// </summary>
        private static BlockSyntax DecomposeStructLocals(BlockSyntax body, HashSet<string> chunkArrayNames, string entityLoopIv)
        {
            if (body == null) return body;

            // First, flatten any nested blocks (e.g., from for-loop body extraction)
            var flatStatements = FlattenBlockStatements(body);

            var newStatements = new List<StatementSyntax>();
            int i = 0;
            var statements = flatStatements.ToArray();

            while (i < statements.Length)
            {
                var stmt = statements[i];

                // Detect: StructType temp = array[idx]; (local declaration with element access initializer)
                if (stmt is LocalDeclarationStatementSyntax localDecl
                    && localDecl.Declaration.Variables.Count == 1)
                {
                    var varDecl = localDecl.Declaration.Variables[0];
                    string tempName = varDecl.Identifier.Text;

                    if (varDecl.Initializer?.Value is ElementAccessExpressionSyntax initEA
                        && initEA.Expression is IdentifierNameSyntax initArrId
                        && chunkArrayNames.Contains(initArrId.Identifier.Text))
                    {
                        string arrName = initArrId.Identifier.Text;
                        string idxText = initEA.ArgumentList?.Arguments.Count > 0
                            ? initEA.ArgumentList.Arguments[0].ToString()
                            : "0";

                        // Find write-back: array[idx] = tempName (within next few statements)
                        int writeBackIdx = -1;
                        for (int j = i + 1; j < statements.Length; j++)
                        {
                            if (statements[j] is ExpressionStatementSyntax es
                                && es.Expression is AssignmentExpressionSyntax ae
                                && ae.IsKind(SyntaxKind.SimpleAssignmentExpression)
                                && ae.Left is ElementAccessExpressionSyntax wbEA
                                && wbEA.Expression is IdentifierNameSyntax wbArrId
                                && wbArrId.Identifier.Text == arrName
                                && wbEA.ArgumentList?.Arguments.Count > 0
                                && wbEA.ArgumentList.Arguments[0].ToString() == idxText
                                && ae.Right is IdentifierNameSyntax rhsId
                                && rhsId.Identifier.Text == tempName)
                            {
                                writeBackIdx = j;
                                break;
                            }
                        }

                        if (writeBackIdx > 0)
                        {
                            // Rewrite mutation statements between decl and write-back.
                            for (int k = i + 1; k < writeBackIdx; k++)
                            {
                                var mutationStmt = statements[k];
                                var rewritten = RewriteTempFieldRefs(mutationStmt, tempName, arrName, idxText);
                                if (rewritten != null)
                                    newStatements.Add(rewritten);
                            }
                            i = writeBackIdx + 1;
                            continue;
                        }

                    }
                }

                newStatements.Add(stmt);
                i++;
            }

            return SyntaxFactory.Block(newStatements);
        }

        /// <summary>
        /// Flatten nested blocks into a single list of statements.
        /// </summary>
        private static List<StatementSyntax> FlattenBlockStatements(BlockSyntax block)
        {
            var result = new List<StatementSyntax>();
            foreach (var stmt in block.Statements)
            {
                if (stmt is BlockSyntax nestedBlock)
                    result.AddRange(FlattenBlockStatements(nestedBlock));
                else
                    result.Add(stmt);
            }
            return result;
        }

        /// <summary>
        /// Replace tempName.Field with arrName[idxText].Field in a statement.
        /// Returns null if no replacement needed (keep original).
        /// </summary>
        private static StatementSyntax? RewriteTempFieldRefs(StatementSyntax stmt, string tempName, string arrName, string idxText)
        {
            // Build the replacement expression: arrName[idxText]
            var arrayAccess = SyntaxFactory.ElementAccessExpression(
                SyntaxFactory.IdentifierName(arrName))
                .WithArgumentList(SyntaxFactory.BracketedArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(SyntaxFactory.ParseExpression(idxText)))));

            // Walk the statement tree and replace tempName.Field with arrName[idxText].Field
            var rewriter = new TempFieldRewriter(tempName, arrayAccess);
            return (StatementSyntax)rewriter.Visit(stmt);
        }

        /// <summary>Rewriter that replaces tempName.Field with arrExpr.Field in member access expressions.</summary>
        private sealed class TempFieldRewriter : CSharpSyntaxRewriter
        {
            private readonly string _tempName;
            private readonly ExpressionSyntax _replacementExpr;

            public TempFieldRewriter(string tempName, ExpressionSyntax replacementExpr)
            {
                _tempName = tempName;
                _replacementExpr = replacementExpr;
            }

            public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                // tempName.Field → replacementExpr.Field
                if (node.Expression is IdentifierNameSyntax id
                    && id.Identifier.Text == _tempName)
                {
                    return SyntaxFactory.MemberAccessExpression(
                        node.Kind(),
                        _replacementExpr,
                        node.Name)
                        .WithTriviaFrom(node);
                }
                return base.VisitMemberAccessExpression(node);
            }
        }

        /// <summary>
        /// ISPC-style struct field decomposition rewriter.
        /// Detects the read-modify-write pattern on struct locals from chunk arrays:
        ///   StructType temp = array[idx];   // local copy
        ///   temp.Field += ...;               // field mutation
        ///   array[idx] = temp;               // write back
        /// Rewrites to direct field access:
        ///   array[idx].Field += ...;
        /// This enables SimdControlFlowGenerator to handle struct field access
        /// via n_gather_ps<sizeof(T)> with struct stride (matching ISPC behavior).
        /// </summary>
        

        /// <summary>
        /// SyntaxRewriter for IJobChunk SIMD preprocessing:
        /// - Removes chunk array local declarations
        /// - Replaces chunk.Count with __entityCount
        /// - Replaces entity for-loop with its body (keeps body, removes for-header)
        /// </summary>
        private sealed class IJobChunkSimdRewriter : CSharpSyntaxRewriter
        {
            private readonly HashSet<string> _chunkArrayNames;
            private readonly string _chunkParamName;
            private readonly string _entityLoopIvName;

            public IJobChunkSimdRewriter(HashSet<string> chunkArrayNames, string chunkParamName, string entityLoopIvName)
            {
                _chunkArrayNames = chunkArrayNames;
                _chunkParamName = chunkParamName;
                _entityLoopIvName = entityLoopIvName;
            }

            public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
            {
                foreach (var variable in node.Declaration.Variables)
                {
                    if (_chunkArrayNames.Contains(variable.Identifier.Text))
                        return null; // Remove: this is a chunk array declaration
                }
                return base.VisitLocalDeclarationStatement(node);
            }

            public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                // chunk.Count → __entityCount
                if (node.Name.Identifier.Text == "Count"
                    && node.Expression is IdentifierNameSyntax id
                    && id.Identifier.Text == _chunkParamName)
                {
                    return SyntaxFactory.IdentifierName("__entityCount");
                }
                return base.VisitMemberAccessExpression(node);
            }

            public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
            {
                if (node.Declaration?.Variables.Count == 1)
                {
                    string ivName = node.Declaration.Variables[0].Identifier.Text;
                    if (ivName == _entityLoopIvName)
                    {
                        // Remove for-header, keep body
                        return node.Statement;
                    }
                }
                return base.VisitForStatement(node);
            }
        }

        /// <summary>
        /// 从 chunkArrayInfo 构建 _nativeArrayParams 字典
        /// (SimdControlFlowGenerator 用这个来识别 NativeArray 访问)
        /// </summary>
        private static Dictionary<string, string> BuildChunkArrayNativeArrayParams(
            List<(string name, string elemType, int compIndex)> chunkArrays)
        {
            var result = new Dictionary<string, string>();
            foreach (var (name, elemType, _) in chunkArrays)
                result[name] = elemType;
            return result;
        }

        // ===================================================================
        // IJobChunk Auto-SIMD: Generate SIMD Code
        // ===================================================================

        /// <summary>
        /// 生成 IJobChunk 的 Register-Level SIMD Execute 函数体。
        /// 流程：
        ///   1. 生成 C++ prelude（_ptr / _length 声明）
        ///   2. 生成外层 batch loop（for si; v_i = v_base + si）
        ///   3. SimdControlFlowGenerator on 修改后的 body（无 for-loop 头）
        ///   4. 标量 remainder 循环
        /// </summary>
        private static void GenerateChunkFunctionSIMD(
            INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb,
            bool useFastMath, NativeTranspiler.SimdMathPrecision simdMathPrecision)
        {
            // Output function signature (same as GenerateChunkFunctionStandard)
            var chunkParams = BuildChunkJobParameters(jobStruct);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"HEAD void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb);

            try
            {
                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
                var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
                if (methodSyntax?.Body == null) return;
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

                // 1. Preprocess AST
                var (chunkArrays, entityLoopIv, modifiedBody) =
                    PreprocessIJobChunkAST(methodSyntax, semanticModel, jobStruct, compilation);

                if (string.IsNullOrEmpty(entityLoopIv) || chunkArrays.Count == 0)
                {
                    // Fallback: use scalar translator
                    var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
                    var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, useFastMath);
                    sb.Append(translator.Translate(methodSyntax.Body));
                    return;
                }

                                // 2. Generate C++ prelude
                foreach (var (name, elemType, compIdx) in chunkArrays)
                {
                    sb.AppendLine($"    auto* RESTRICT {name}_ptr = reinterpret_cast<{elemType}*>(__chunkData->requiredComponentArrays[{compIdx}]);");
                    sb.AppendLine($"    int {name}_length = __chunkData->entityCount;");
                }
                sb.AppendLine("    int __entityCount = __chunkData->entityCount;");

                // 3. Build fake method with virtual int i parameter
                var newParamList = methodSyntax.ParameterList.AddParameters(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier(entityLoopIv))
                        .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))));
                var fakeMethod = methodSyntax.WithParameterList(newParamList).WithBody(modifiedBody);

                // 4. Variable analysis
                var varAnalyzer = new SimdVariableAnalyzer(semanticModel, jobStruct, entityLoopIv);
                var variables = varAnalyzer.Analyze(fakeMethod);
                var nativeArrayParams = BuildChunkArrayNativeArrayParams(chunkArrays);

                                // 5. Generate SIMD batch loop
                sb.AppendLine("    int __simd_end = (__entityCount / NSIMD_WIDTH) * NSIMD_WIDTH;");
                sb.AppendLine("    if (__simd_end > 0)");
                sb.AppendLine("    {");
                sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0);");
                sb.AppendLine("        for (int si = 0; si < __simd_end; si += NSIMD_WIDTH)");
                sb.AppendLine("        {");
                sb.AppendLine("            simd_value<int> v_i = v_base + si;");

            // 6. SimdControlFlowGenerator on modified body
            var simdGen = new SimdControlFlowGenerator(
                semanticModel, jobStruct, variables, varAnalyzer,
                indexParamName: entityLoopIv,
                simdIndexVar: "v_i",
                batchOffsetVar: "0",
                batchLoopVar: "",
                nativeArrayParams: nativeArrayParams,
                simdMathPrecision: simdMathPrecision);

            string simdBody = simdGen.Generate(modifiedBody);
            foreach (var line in simdBody.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine($"            {line.TrimEnd()}");

            sb.AppendLine("        __simd_exit: ;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            // 7. Scalar remainder loop
            GenerateChunkFunctionRemainder(jobStruct, compilation, sb, useFastMath, chunkArrays, entityLoopIv);
            }
            catch (Exception ex)
            {
                // Catch block: gracefully close the function with scalar body
                try
                {
                    sb.AppendLine("        __simd_exit: ;");
                    sb.AppendLine("        }");
                    sb.AppendLine("    }");
                    var em = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
                    var ms = em != null ? SymbolHelper.GetMethodSyntax(em) : null;
                    if (ms?.Body != null)
                    {
                        var sm = compilation.GetSemanticModel(ms.SyntaxTree);
                        var rt = CollectChunkNativeArrayTypes(jobStruct, compilation);
                        var tr = new CppChunkStatementTranslator(sm, jobStruct, rt, useFastMath);
                        string scalarBody = tr.Translate(ms.Body);
                        // Remove duplicate pointer/length declarations
                        try { scalarBody = Regex.Replace(scalarBody, @"auto\* RESTRICT \w+_ptr = reinterpret_cast<[^>]+>\(__chunkData->requiredComponentArrays\[\d+\]\);\r?\n?", ""); } catch { }
                        try { scalarBody = Regex.Replace(scalarBody, @"int \w+_length = __chunkData->entityCount;\r?\n?", ""); } catch { }
                        try { scalarBody = Regex.Replace(scalarBody, @"int __entityCount = __chunkData->entityCount;\r?\n?", ""); } catch { }
                        scalarBody = scalarBody.Replace("#pragma loop(ivdep)\r\n", "").Replace("#pragma loop(ivdep)\n", "");
                        scalarBody = scalarBody.Replace("#pragma loop(vector)\r\n", "").Replace("#pragma loop(vector)\n", "");
                        scalarBody = scalarBody.Replace("#pragma unroll(4)\r\n", "").Replace("#pragma unroll(4)\n", "");
                        sb.Append(scalarBody);
                    }
                }
                catch { }
            }

            sb.AppendLine("}");
        }

        /// <summary>
        /// 生成 IJobChunk SIMD 的 scalar remainder 循环。
        /// 复用 CppChunkStatementTranslator 的标量输出，仅修改实体循环起始值为 __simd_end。
        /// </summary>
        private static void GenerateChunkFunctionRemainder(
            INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb,
            bool useFastMath,
            List<(string name, string elemType, int compIndex)> chunkArrays,
            string entityLoopIv)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return;
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
            var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, useFastMath);
            string scalarBody = translator.Translate(methodSyntax.Body);

            // Remove prelude declarations (already emitted by SIMD generator)
            foreach (var (name, _, _) in chunkArrays)
            {
                try
                {
                    string ptrDecl = $"auto* RESTRICT {name}_ptr = reinterpret_cast<";
                    int idx = scalarBody.IndexOf(ptrDecl);
                    if (idx >= 0)
                    {
                        int semiEnd = scalarBody.IndexOf(';', idx);
                        if (semiEnd >= 0)
                        {
                            int lineEnd = scalarBody.IndexOf('\n', semiEnd);
                            if (lineEnd >= 0)
                                scalarBody = scalarBody.Remove(idx, lineEnd - idx + 1);
                            else
                                scalarBody = scalarBody.Remove(idx);
                        }
                    }

                    string lenDecl = $"int {name}_length =";
                    idx = scalarBody.IndexOf(lenDecl);
                    if (idx >= 0)
                    {
                        int lineEnd = scalarBody.IndexOf('\n', idx);
                        if (lineEnd >= 0)
                            scalarBody = scalarBody.Remove(idx, lineEnd - idx + 1);
                        else
                            scalarBody = scalarBody.Remove(idx);
                    }
                }
                catch { }
            }

            // Remove __entityCount declaration if present (already emitted)
            string ecDecl = "int __entityCount = ";
            int ecIdx = scalarBody.IndexOf(ecDecl);
            if (ecIdx >= 0)
            {
                int ecEnd = scalarBody.IndexOf('\n', ecIdx);
                if (ecEnd >= 0)
                    scalarBody = scalarBody.Remove(ecIdx, ecEnd - ecIdx + 1);
                else
                    scalarBody = scalarBody.Remove(ecIdx);
            }

            // Remove pragma hints (already in SIMD loop or not needed)
            scalarBody = scalarBody.Replace("#pragma loop(ivdep)\r\n", "");
            scalarBody = scalarBody.Replace("#pragma loop(ivdep)\n", "");
            scalarBody = scalarBody.Replace("#pragma loop(vector)\r\n", "");
            scalarBody = scalarBody.Replace("#pragma loop(vector)\n", "");
            scalarBody = scalarBody.Replace("#pragma unroll(4)\r\n", "");
            scalarBody = scalarBody.Replace("#pragma unroll(4)\n", "");

            // Change entity loop start from 0 to __simd_end
            string loopPattern = $"for (int {entityLoopIv} = 0; {entityLoopIv} <";
            string loopReplacement = $"for (int {entityLoopIv} = __simd_end; {entityLoopIv} <";
            scalarBody = scalarBody.Replace(loopPattern, loopReplacement);

            sb.Append(scalarBody);
        }

        /// <summary>
        /// 生成调用独立 IJobChunk Execute 函数的实参列表。
        /// 用于适配器中替代内联 Execute 体。
        /// </summary>
        private static string BuildChunkExecuteCallArgs(INamedTypeSymbol jobStruct)
        {
            return $"__chunkData, __requiredComponentTypeIds, {BuildChunkExecuteFieldArgs(jobStruct)}";
        }

        /// <summary>
        /// 仅生成字段参数部分（不含 __chunkData 和 __requiredComponentTypeIds）
        /// </summary>
        private static string BuildChunkExecuteFieldArgs(INamedTypeSymbol jobStruct)
        {
            var args = new List<string>();
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (field.Type.Name == "NativeList")
                        args.Add($"{field.Name}_listData");
                    else
                    {
                        args.Add($"{field.Name}_ptr");
                        args.Add($"{field.Name}_length");
                    }
                }
                else if (field.Type is IPointerTypeSymbol)
                    args.Add($"{field.Name}_ptr");
                else
                    args.Add($"{field.Name}_ptr");
            }
            return string.Join(", ", args);
        }

        /// <summary>
        /// 为 IJobEntity 的实体循环生成 per-lane SIMD 包装代码。
        /// 将 "for (int __entity_index = 0; ... < ... ; ...)" 替换为
        /// per-lane batch + remainder 循环。
        /// </summary>
        private static string WrapEntityLoopSIMD(string scalarBodyWithLoop, string entityIndexVar, string entityCountExpr)
        {
            string loopStartPattern = $"for (int {entityIndexVar} = 0; {entityIndexVar} <";
            string loopEndPattern = $"; ++{entityIndexVar})";

            int loopIdx = scalarBodyWithLoop.IndexOf(loopStartPattern);
            if (loopIdx < 0)
            {
                // Try alternate increment pattern
                loopStartPattern = $"for (int {entityIndexVar} = 0; {entityIndexVar} <";
                loopEndPattern = $"; {entityIndexVar}++)";
                loopIdx = scalarBodyWithLoop.IndexOf(loopStartPattern);
                if (loopIdx < 0)
                    return scalarBodyWithLoop; // fallback: no entity loop found
            }

            // Find the bounds expression (between "<" and ";")
            int condStart = scalarBodyWithLoop.IndexOf('<', loopIdx);
            int semiPos = scalarBodyWithLoop.IndexOf(';', condStart);
            string boundExpr = scalarBodyWithLoop.Substring(condStart + 1, semiPos - condStart - 1).Trim();

            // Find the loop body boundaries
            int openBrace = scalarBodyWithLoop.IndexOf('{', loopIdx);
            if (openBrace < 0) return scalarBodyWithLoop;
            int depth = 1;
            int closeBrace = -1;
            for (int i = openBrace + 1; i < scalarBodyWithLoop.Length; i++)
            {
                if (scalarBodyWithLoop[i] == '{') depth++;
                else if (scalarBodyWithLoop[i] == '}')
                {
                    depth--;
                    if (depth == 0) { closeBrace = i; break; }
                }
            }
            if (closeBrace < 0) return scalarBodyWithLoop;

            // Extract the loop body content (without braces)
            string loopBody = scalarBodyWithLoop.Substring(openBrace + 1, closeBrace - openBrace - 1);

            // Remove #pragma lines from body
            loopBody = Regex.Replace(loopBody, @"#pragma\s+\w+\([^)]*\)\s*\r?\n?", "");
            loopBody = Regex.Replace(loopBody, @"#pragma\s+unroll\s*\(\s*\d+\s*\)\s*\r?\n?", "");

            bool hasReturn = loopBody.Contains("return;");

            // Build per-lane SIMD section
            var simdSection = new StringBuilder();
            simdSection.AppendLine($"    int __simd_end = ({boundExpr} / NSIMD_WIDTH) * NSIMD_WIDTH;");
            simdSection.AppendLine("    if (__simd_end > 0)");
            simdSection.AppendLine("    {");
            simdSection.AppendLine("        for (int si = 0; si < __simd_end; si += NSIMD_WIDTH)");
            simdSection.AppendLine("        {");
            simdSection.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
            simdSection.AppendLine("            {");
            simdSection.AppendLine($"                int {entityIndexVar} = si + lane;");

            if (hasReturn)
            {
                simdSection.AppendLine("                do {");
                foreach (var line in loopBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    trimmed = trimmed.Replace("return;", "break;");
                    simdSection.AppendLine($"                    {trimmed}");
                }
                simdSection.AppendLine("                } while(false);");
            }
            else
            {
                foreach (var line in loopBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    simdSection.AppendLine($"                {trimmed}");
                }
            }
            simdSection.AppendLine("            }");
            simdSection.AppendLine("        }");
            simdSection.AppendLine("    }");

            // Build remainder loop
            simdSection.AppendLine($"    for (int {entityIndexVar} = __simd_end; {entityIndexVar} < {boundExpr}; ++{entityIndexVar})");
            simdSection.AppendLine("    {");
            if (hasReturn)
            {
                simdSection.AppendLine("        do {");
                foreach (var line in loopBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    trimmed = trimmed.Replace("return;", "break;");
                    simdSection.AppendLine($"            {trimmed}");
                }
                simdSection.AppendLine("        } while(false);");
            }
            else
            {
                foreach (var line in loopBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    simdSection.AppendLine($"            {trimmed}");
                }
            }
            simdSection.AppendLine("    }");

            // Replace the original for-loop with per-lane SIMD section
            string beforeLoop = scalarBodyWithLoop.Substring(0, loopIdx);
            string afterLoop = scalarBodyWithLoop.Substring(closeBrace + 1);
            return beforeLoop + simdSection.ToString() + afterLoop;
        }

        public static string GetEntityBatchAdapterFunctionName(INamedTypeSymbol jobStruct)
        {
            return GetCppJobFunctionName(jobStruct) + "_EntityBatchAdapter";
        }

        /// <summary>
        /// 获取适配函数指针的获取函数名
        /// </summary>
        public static string GetAdapterPtrGetterName(INamedTypeSymbol jobStruct)
        {
            return "Get_" + GetAdapterFunctionName(jobStruct) + "Ptr";
        }

        public static string GetRangeAdapterPtrGetterName(INamedTypeSymbol jobStruct)
        {
            return "Get_" + GetRangeAdapterFunctionName(jobStruct) + "Ptr";
        }

        public static string GetEntityBatchAdapterPtrGetterName(INamedTypeSymbol jobStruct)
        {
            return "Get_" + GetEntityBatchAdapterFunctionName(jobStruct) + "Ptr";
        }
    }
}
