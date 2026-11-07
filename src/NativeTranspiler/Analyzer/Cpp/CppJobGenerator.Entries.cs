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
    public static partial class CppJobGenerator
    {

        // 只有整个 Execute 体就是一个受稳定边界保护的 if，才能缩短外层循环。
        // 保留原始条件，让 C++ 编译器消除重复比较，不做生成文本替换。
        private static string GetElementLoopEnd(INamedTypeSymbol job, SemanticModel model,
            MethodDeclarationSyntax method, string indexName)
        {
            const string originalEnd = "__startIndex + __count";
            if (method.Body?.Statements.Count != 1
                || method.Body.Statements[0] is not IfStatementSyntax guard || guard.Else != null)
                return originalEnd;
            ExpressionSyntax first = guard.Condition;
            while (first is BinaryExpressionSyntax conjunction && conjunction.IsKind(SyntaxKind.LogicalAndExpression))
                first = conjunction.Left;
            if (first is not BinaryExpressionSyntax comparison || !comparison.IsKind(SyntaxKind.LessThanExpression)
                || comparison.Left is not IdentifierNameSyntax index || index.Identifier.ValueText != indexName
                || model.GetSymbolInfo(comparison.Right).Symbol is not IFieldSymbol field
                || field.IsStatic || field.IsVolatile || field.Type.SpecialType != SpecialType.System_Int32
                || !SymbolEqualityComparer.Default.Equals(field.ContainingType, job))
                return originalEnd;
            var nodes = method.Body.DescendantNodes().ToArray();
            if (HasIndirectJobFieldWrites(job, model, nodes) || GetFieldLoopUse(job, model).Written.Contains(field.Name))
                return originalEnd;
            return $"std::min({originalEnd}, {field.Name})";
        }

        /// <summary>发标量逐元素循环。体内有 `return;` 时把体包成立即调用 lambda（`return` 只退 lambda）。</summary>
        private static void EmitElementLoop(StringBuilder sb, string indexParamName, string loopEnd,
            string body, bool anyReturn)
        {
            sb.AppendLine($"    for (int {indexParamName} = __startIndex; {indexParamName} < {loopEnd}; ++{indexParamName})");
            sb.AppendLine("    {");
            if (anyReturn) sb.AppendLine("        [&]() {");
            sb.Append(body);
            if (anyReturn) sb.AppendLine("        }();");
            sb.AppendLine("    }");
        }

        private static void GenerateBatchFunction(INamedTypeSymbol jobStruct, SemanticModel semanticModel,
            MethodDeclarationSyntax methodSyntax, StringBuilder sb, bool useFastMath,
            NativeTranspiler.AutoSIMD autoSIMD, NativeTranspiler.SimdMathPrecision simdMathPrecision,
            bool isRangeJob, List<IFieldSymbol>? boolFields = null, List<bool>? values = null)
        {
            bool isVariant = boolFields != null;
            string funcName = GetCppJobFunctionName(jobStruct, isBatch: true)
                + (isVariant ? BoolVariants.Suffix(values!) : "");
            string paramsStr = BatchJobParameters;
            var zeroMaskField = !isVariant && !isRangeJob && autoSIMD == NativeTranspiler.AutoSIMD.Disabled
                ? FindZeroMaskField(jobStruct, semanticModel, methodSyntax) : null;
            var stableArrays = isVariant ? null : FindStableArrayFields(jobStruct, semanticModel, methodSyntax);
            // 普通和 bool 特化批次共用签名与内联约定。
            sb.AppendLine("#ifndef EJ_BATCH_FORCEINLINE_PRE");
            sb.AppendLine("#  if defined(_MSC_VER) && !defined(__clang__)");
            sb.AppendLine("#    define EJ_BATCH_FORCEINLINE_PRE __forceinline");
            sb.AppendLine("#  else");
            sb.AppendLine("#    define EJ_BATCH_FORCEINLINE_PRE");
            sb.AppendLine("#  endif");
            sb.AppendLine("#endif");
            sb.AppendLine("#ifndef EJ_BATCH_ALWAYS_INLINE");
            sb.AppendLine("#  if defined(__clang__)");
            sb.AppendLine("#    define EJ_BATCH_ALWAYS_INLINE __attribute__((always_inline))");
            sb.AppendLine("#  else");
            sb.AppendLine("#    define EJ_BATCH_ALWAYS_INLINE");
            sb.AppendLine("#  endif");
            sb.AppendLine("#endif");
            if (zeroMaskField != null)
                sb.AppendLine("template<bool __ejZeroMask>");
            string linkage = zeroMaskField == null ? "GENERATED_API" : "static";
            string bodyName = zeroMaskField == null ? funcName : funcName + "_MaskImpl";
            sb.AppendLine($"{linkage} EJ_BATCH_FORCEINLINE_PRE void CALLINGCONVENTION {bodyName}({paramsStr}) EJ_BATCH_ALWAYS_INLINE");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, semanticModel: semanticModel, contextAbi: true,
                zeroMaskField: zeroMaskField, stableArrays: stableArrays);
            var indexParamName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;

            // IJobParallelForBatch：Execute(startIndex, count) 就是一次区间调用 ⇒ 不生成 index 循环，
            // 两个形参分别映射到 C++ 形参 `__startIndex` / `__count`（共用 BatchJobParameters 声明）。
            if (isRangeJob)
            {
                string countParamName = methodSyntax.ParameterList.Parameters.Count > 1
                    ? methodSyntax.ParameterList.Parameters[1].Identifier.Text
                    : null;
                var rangeTranslator = new CppBatchStatementTranslator(semanticModel, jobStruct,
                    indexParamName, "__startIndex", useFastMath, false,
                    countParamName, "__count");
                sb.Append(rangeTranslator.Translate(methodSyntax.Body));
                sb.AppendLine("}");
                sb.AppendLine();
                return;
            }

            // 标量翻译结果也用于 SIMD 的回退及尾部。
            var scalarTranslator = new CppBatchStatementTranslator(semanticModel, jobStruct, indexParamName, indexParamName, useFastMath, /* scalar body, no SIMD */ false);
            var scalarBody = scalarTranslator.Translate(methodSyntax.Body);

            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
            {
                Dictionary<string, string>? boolFieldValues = null;
                if (isVariant)
                {
                    boolFieldValues = new Dictionary<string, string>();
                    for (int i = 0; i < boolFields!.Count; i++)
                        boolFieldValues[boolFields[i].Name] = values![i] ? "true" : "false";
                }
                var simdGen = new OuterSimdGenerator(methodSyntax, semanticModel, indexParamName, boolFieldValues, jobStruct, simdMathPrecision);
                var simdCode = simdGen.Generate(scalarBody);
                sb.Append(simdCode);
                sb.AppendLine("}");
                sb.AppendLine();
                return;
            }

            // Execute 中的 return 仅结束当前 index，由立即调用的 lambda 保持语义。
            bool bodyHasReturn = scalarBody.Contains("return;");
            EmitElementLoop(sb, indexParamName, GetElementLoopEnd(jobStruct, semanticModel, methodSyntax, indexParamName), scalarBody, bodyHasReturn);
            sb.AppendLine("}");
            sb.AppendLine();
            if (isVariant) sb.AppendLine();
            if (zeroMaskField != null)
            {
                int offset = 0, currentOffset = 0;
                foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    offset = CalculateFieldOffset(field, ref currentOffset);
                    if (SymbolEqualityComparer.Default.Equals(field, zeroMaskField)) break;
                }
                sb.AppendLine($"GENERATED_API EJ_BATCH_FORCEINLINE_PRE void CALLINGCONVENTION {funcName}({paramsStr}) EJ_BATCH_ALWAYS_INLINE");
                sb.AppendLine("{");
                var maskType = NativeTranspiler.MapCSharpTypeToCpp(zeroMaskField.Type);
                sb.AppendLine($"    if (*({maskType}*)((char*)__job + {offset}) == 0)");
                sb.AppendLine($"        {bodyName}<true>(__job, __startIndex, __count);");
                sb.AppendLine("    else");
                sb.AppendLine($"        {bodyName}<false>(__job, __startIndex, __count);");
                sb.AppendLine("}");
                sb.AppendLine();
            }
        }

        private static void GenerateSingleFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath,
            NativeTranspiler.AutoSIMD autoSIMD = NativeTranspiler.AutoSIMD.Disabled,
            NativeTranspiler.SimdMathPrecision simdMathPrecision = NativeTranspiler.SimdMathPrecision.Fastest)
        {
            var singleParams = BuildJobParameters(jobStruct);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({singleParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
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
                        // 单 IJob：没有生成器可知的批循环 ⇒ 偏移未知。先前传字面量 "0" 等于断言
                        // "v_i 相对基址从 0 开始"，而用户循环起点未知 ⇒ 现用哨兵值让
                        // EmitElementStore 退回逐 lane scatter（NT-01(b) 同类陷阱封死）。
                        batchOffsetVar: SimdControlFlowGenerator.UnknownBatchOffset,
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
                sb.AppendLine("    // (empty Execute body)");
            }
            sb.AppendLine("}");
        }

        private static void GenerateChunkFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
        {
            bool usesSendEvent = JobUsesSendEvent(jobStruct, compilation);
            var chunkParams = BuildChunkJobParameters(jobStruct, includeHeader: usesSendEvent);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);
            sb.AppendLine("    EntityMatchMask __queryMask(__chunkData->filter, __chunkData->entityCount);");
            sb.AppendLine("    if (!__queryMask.HasMatches()) return;");
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
                var sharedTypes = CollectSharedComponentTypes(jobStruct, compilation);
                var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, sharedTypes, useFastMath);
                var bodyCode = translator.Translate(methodSyntax.Body);
                sb.Append(bodyCode);
            }
            else
            {
                sb.AppendLine("    // (empty IJobChunk Execute body)");
            }
            sb.AppendLine("}");
        }

        private static void GenerateChunkFunctionVectorize(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
        {
            // Generate auto-vectorizable scalar loop — Clang generates @llvm.sin.v8f32 / cos.v8f32
            bool usesSendEvent = JobUsesSendEvent(jobStruct, compilation);
            var chunkParams = BuildChunkJobParameters(jobStruct, includeHeader: usesSendEvent);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);
            sb.AppendLine("    EntityMatchMask __queryMask(__chunkData->filter, __chunkData->entityCount);");
            sb.AppendLine("    if (!__queryMask.HasMatches()) return;");
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
                var sharedTypes = CollectSharedComponentTypes(jobStruct, compilation);
                // Use same chunk translator as standard mode — produces scalar ptr[index].field
                var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, sharedTypes, useFastMath);
                var bodyCode = translator.Translate(methodSyntax.Body);
                // Insert auto-vectorize pragma before the entity for-loop
                string pragma = "#ifdef __clang__\n#pragma clang loop vectorize(enable) interleave(enable)\n#endif\n";
                int forPos = bodyCode.IndexOf("for (");
                if (forPos >= 0)
                    bodyCode = bodyCode.Insert(forPos, pragma);
                sb.Append(bodyCode);
            }
            else
            {
                sb.AppendLine("    // (empty IJobChunk Execute body)");
            }
            sb.AppendLine("}");
        }

        private static void GenerateEntityFunctionVectorize(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
            => GenerateEntityChunkFunctionStandard(jobStruct, compilation, sb, useFastMath, vectorize: true);
        private static void GenerateEntityFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
        {
            var chunkParams = BuildChunkJobParameters(jobStruct, includeTypeIds: false);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);
            sb.AppendLine("    EntityMatchMask __queryMask(__chunkData->filter, __chunkData->entityCount);");
            sb.AppendLine("    if (!__queryMask.HasMatches()) return;");

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            for (int i = 0; i < executeMethod.Parameters.Length; i++)
            {
                var param = executeMethod.Parameters[i];
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                if (NativeTranspileValidator.IsRelationComponentType(param.Type)) cppType = "const " + cppType;
                sb.AppendLine($"    auto* __entity_param_{i}_ptr = reinterpret_cast<{cppType}*>(__chunkData->componentArrays[{i}]);");
                sb.AppendLine($"    (void)((intptr_t)__entity_param_{i}_ptr % 64 == 0);");
            }

            // Pre-translate scalar body
            string scalarBody = "";
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var translator = new CppEntityStatementTranslator(semanticModel, jobStruct, useFastMath);
                scalarBody = translator.Translate(methodSyntax.Body);
            }

            // NT-02/NT-07：index 层级的 `return;` 配合外层 do-while 降级为 `break;`（结束本次实体）；
            // 嵌在循环里的 `return;` 不能降级为 break（只跳内层循环），改写为唯一标记 ⇒ 构建期失败。
            bool hasReturn = scalarBody.Contains("return;");
            if (hasReturn)
                scalarBody = ReturnStatementRewriter.Rewrite(scalarBody, methodSyntax?.Body, "break;");

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
                AppendEntityParameterLocal(sb, param.p, param.i, "                ");
            }
            if (hasReturn)
            {
                sb.AppendLine("                do {");
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append("                    ").AppendLine(line);
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
                AppendEntityParameterLocal(sb, param.p, param.i, "        ");
            }
            if (hasReturn)
            {
                sb.AppendLine("        do {");
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append("            ").AppendLine(line);
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

        /// <summary>
        /// 生成 IJobEntity 的独立 C++ 函数（对标 GenerateChunkFunctionStandard）。
        /// 函数签名：
        /// void Execute(const ChunkData* __chunkData, ... field_ptrs ...)
        /// 使用 ChunkData 轻量结构（不含 __requiredComponentTypeIds），componentArrays 直接索引。
        /// </summary>
        private static void GenerateEntityChunkFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath, bool vectorize = false)
        {
            var chunkParams = BuildChunkJobParameters(jobStruct, includeTypeIds: false);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);
            sb.AppendLine("    EntityMatchMask __queryMask(__chunkData->filter, __chunkData->entityCount);");
            sb.AppendLine("    if (!__queryMask.HasMatches()) return;");

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);

            // 从 __chunkData->componentArrays 提取组件数组指针（Entity 参数不占组件列，需独立计数）
            int compIdx = 0;
            for (int i = 0; i < executeMethod.Parameters.Length; i++)
            {
                var param = executeMethod.Parameters[i];
                if (NativeTranspiler.IsEntityType(param.Type)) continue;
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                string constPrefix = param.RefKind == RefKind.In || NativeTranspileValidator.IsRelationComponentType(param.Type) ? "const " : "";
                sb.AppendLine($"    {constPrefix}auto* __entity_param_{i}_ptr = reinterpret_cast<{constPrefix}{cppType}*>(__chunkData->componentArrays[{compIdx}]);");
                sb.AppendLine($"    (void)((intptr_t)__entity_param_{i}_ptr % 64 == 0);");
                compIdx++;
            }

            // 预翻译标量 body
            string scalarBody = "";
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var translator = new CppEntityStatementTranslator(semanticModel, jobStruct, useFastMath);
                scalarBody = translator.Translate(methodSyntax.Body);
            }

            // NT-02/NT-07：index 层级的 `return;` 配合外层 do-while 降级为 `break;`；
            // 嵌在循环里的 `return;` 降级为 break 只会跳内层循环 ⇒ 改写为唯一标记让构建期失败。
            bool hasReturn = scalarBody.Contains("return;");
            if (hasReturn)
                scalarBody = ReturnStatementRewriter.Rewrite(scalarBody, methodSyntax?.Body, "break;");

            // Entity 参数：用函数内局部结构体（{ int Id; int Version; } 对齐 C# Entity）。
            // 不用 EntJoy.ECS.Entity 结构头：Entity 是 IJobEntity 的注入参数，不是 chunk 组件列，
            // CollectChunkNativeArrayTypes 已把它跳过（见该函数的 IsEntityType 过滤），
            // 局部结构体让生成的批函数自包含，不依赖该框架类型是否被别的 job 带进 userStructs。
            bool hasEntityParam = executeMethod.Parameters.Any(p => NativeTranspiler.IsEntityType(p.Type));
            if (hasEntityParam)
                sb.AppendLine("    struct __EntJoyEntity { int Id; int Version; };");

            // 实体循环
            sb.AppendLine();
            sb.AppendLine("    int __entity_count = __chunkData->entityCount;");
            if (vectorize)
            {
                sb.AppendLine("#ifdef __clang__");
                sb.AppendLine("#pragma clang loop vectorize(enable) interleave(enable)");
                sb.AppendLine("#endif");
            }
            sb.AppendLine("    for (int __entity_index = 0; __entity_index < __entity_count; ++__entity_index)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (__queryMask.Length != 0 && !__queryMask.IsEnabled(__entity_index)) continue;");
            foreach (var (p, i) in executeMethod.Parameters.Select((p, i) => (p, i)))
            {
                if (NativeTranspiler.IsEntityType(p.Type))
                {
                    sb.AppendLine($"        __EntJoyEntity {p.Name} = ((__EntJoyEntity*)__chunkData->entityArray)[__entity_index];");
                    continue;
                }
                AppendEntityParameterLocal(sb, p, i, "        ");
            }

            if (hasReturn)
            {
                sb.AppendLine("        do {");
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    sb.AppendLine($"            {trimmed}");
                }
                sb.AppendLine("        } while(false);");
            }
            else
            {
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    sb.AppendLine($"        {trimmed}");
                }
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }
        private static void AppendEntityParameterLocal(StringBuilder sb, IParameterSymbol parameter, int index, string indent)
        {
            string type = NativeTranspiler.MapCSharpTypeToCpp(parameter.Type);
            string qualifier = parameter.RefKind == RefKind.In ? "const " : "";
            string reference = parameter.RefKind == RefKind.None && NativeTranspileValidator.IsRelationComponentType(parameter.Type) ? "" : "&";
            sb.AppendLine($"{indent}{qualifier}{type}{reference} {parameter.Name} = __entity_param_{index}_ptr[__entity_index];");
        }
    }
}
