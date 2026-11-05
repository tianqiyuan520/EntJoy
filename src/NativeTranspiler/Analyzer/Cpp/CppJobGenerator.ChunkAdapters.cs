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


        private static void AppendChunkAdapters(INamedTypeSymbol jobStruct, Compilation compilation,
            StringBuilder sb, List<string> discoveredEventTypes)
        {
            var baseFuncName = GetCppJobFunctionName(jobStruct);
            var adapterFuncName = baseFuncName + "_Adapter";
            var attrSymbol = AttributeHelper.GetAttributeSymbol(compilation);
            var autoSIMD = attrSymbol != null ? AttributeHelper.GetAutoSIMD(jobStruct, attrSymbol) : NativeTranspiler.AutoSIMD.Disabled;
            bool isIspcJob = attrSymbol != null && AttributeHelper.GetBackendTarget(jobStruct, attrSymbol) == NativeTranspiler.BackendTarget.Ispc;
            sb.AppendLine("#ifndef __EntJoyChunkContextHeader_DEFINED");
            sb.AppendLine("#define __EntJoyChunkContextHeader_DEFINED");
            sb.AppendLine("struct __EntJoyChunkContextHeader");
            sb.AppendLine("{");
            sb.AppendLine("    int chunkCount;");
            sb.AppendLine("    int hasEnabledFilter;");
            sb.AppendLine("    void* queryAllEnabledTypes;");
            sb.AppendLine("    int allEnabledCount;");
            sb.AppendLine("    int gcHandleStartIndex;");
            sb.AppendLine("    void* chunksPtr;");
            sb.AppendLine("    int cleanupInProgress;");
            sb.AppendLine("    int ownsChunkData;");
            sb.AppendLine("    void* requiredComponentTypeIds;");
            sb.AppendLine("    int requiredComponentTypeIdCount;");
            sb.AppendLine("    int jobIsBoxed;");
            sb.AppendLine("    void* chunkArrayHandle;");
            sb.AppendLine("    // Event Buffer");
            sb.AppendLine("    int eventBufferCount;");
            sb.AppendLine("    void* eventBufferHeaders;");
            sb.AppendLine("    void* eventWorldHandle;");
            sb.AppendLine("};");
            sb.AppendLine("#endif");
            sb.AppendLine();
            sb.AppendLine("#ifndef __EntJoyEventBuffer_DEFINED");
            sb.AppendLine("#define __EntJoyEventBuffer_DEFINED");
            sb.AppendLine("struct __EntJoyEventBuffer {");
            sb.AppendLine("    void* data;");
            sb.AppendLine("    int* count;");
            sb.AppendLine("    int capacity;");
            sb.AppendLine("    int elementSize;");
            sb.AppendLine("};");
            sb.AppendLine("#endif");
            sb.AppendLine();

            bool isEntityJob = IsEntityJob(jobStruct);
            bool useFastMath = AttributeHelper.HasFastCppMathLib(jobStruct, attrSymbol);
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            var scalarModel = !isEntityJob && !isIspcJob && autoSIMD == NativeTranspiler.AutoSIMD.Disabled && methodSyntax?.Body != null
                ? compilation.GetSemanticModel(methodSyntax.SyntaxTree) : null;
            var adapterMask = scalarModel != null ? FindZeroMaskField(jobStruct, scalarModel, methodSyntax!) : null;
            var adapterLoopUse = GetFieldLoopUse(jobStruct, TryGetSemanticModel(jobStruct, compilation));
            int contextSnapshotSize = scalarModel != null ? GetReadOnlyContextSize(jobStruct, scalarModel, methodSyntax!) : 0;

            // IJobEntity 和 IJobChunk 统一走 ChunkAdapter 路径
            {
            AppendMaskAdapterHeader(sb, adapterFuncName, "void* context, const ChunkJobData* __chunkData", adapterMask);
            sb.AppendLine("{");
            sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
            sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
            sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
            sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
            sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
            AppendReadOnlyContextSnapshot(sb, contextSnapshotSize);
            sb.AppendLine("    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;");

            // IJobEntity：轻量 ChunkData 路径（将 ChunkJobData 转换为 ChunkData，跳过冗余字段）
            if (isEntityJob)
            {
                sb.AppendLine("    // 轻量 ChunkData：只保留 Execute 实际需要的字段");
                sb.AppendLine("    ChunkData __chunkDataLite;");
                sb.AppendLine("    __chunkDataLite.componentArrays = __chunkData->requiredComponentArrays;");
                sb.AppendLine("    __chunkDataLite.entityCount = __chunkData->entityCount;");
                sb.AppendLine("    __chunkDataLite.requiredComponentCount = __chunkData->requiredComponentCount;");
                sb.AppendLine("    __chunkDataLite.enableBitMaps = __chunkData->requiredEnableBitMaps != nullptr ? __chunkData->requiredEnableBitMaps : __chunkData->enableBitMaps;   // 逐组件 enable 位图（优先与 componentArrays 同序的 required 版）");
                sb.AppendLine("    __chunkDataLite.enableBitmapCount = __chunkData->requiredEnableBitMaps != nullptr ? __chunkData->requiredComponentCount : __chunkData->componentCount;");
                sb.AppendLine("    // 位图下标与 componentArrays 同序（required 序）；读写原语见 src/NativeDll/NativeEnableMask.h");
                // 仅当 Execute 声明 Entity 参数时才传实体数组（没有就不传，避免无谓拷贝/解引用）
                if (executeMethod.Parameters.Any(p => NativeTranspiler.IsEntityType(p.Type)))
                    sb.AppendLine("    __chunkDataLite.entityArray = __chunkData->entityArray;");
            }

            AppendChunkAdapterFields(sb, jobStruct, adapterMask, adapterLoopUse, mutableLengths: true);

                // IJobEntity 或 Auto-SIMD: 调用独立函数而非内联
                // IJobEntity 使用轻量 ChunkData 路径（&__chunkDataLite）
                if (isEntityJob || autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                {
                    string funcName = GetCppJobFunctionName(jobStruct);
                    bool usesSendEvent = JobUsesSendEvent(jobStruct, compilation);
                    string callArgs = isEntityJob
                        ? BuildLiteChunkExecuteCallArgs(jobStruct, includeHeader: usesSendEvent)
                        : BuildChunkExecuteCallArgs(jobStruct, includeHeader: usesSendEvent);
                    sb.AppendLine($"    {funcName}({callArgs});");
                }
                else
                {
                    // 内联 Execute 函数体（如同 IJobEntity 的做法）
                    if (methodSyntax?.Body != null)
                    {
                        var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                        var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
                        var sharedTypes = CollectSharedComponentTypes(jobStruct, compilation);
                        var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, sharedTypes, useFastMath);
                        var bodyCode = translator.Translate(methodSyntax.Body);

                        foreach (var line in bodyCode.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                        {
                            if (line.Length == 0) continue;
                            sb.Append("    ").AppendLine(line);
                        }

                        // SendEvent: 生成 EventBuffer 变量声明（在 __header 作用域内）
                        if (translator.EventTypes.Count > 0)
                        {
                            sb.AppendLine($"    // Event Buffer declarations ({translator.EventTypes.Count} types)");
                            for (int ei = 0; ei < translator.EventTypes.Count; ei++)
                            {
                                string evtName = translator.EventTypes[ei].Name;
                                sb.AppendLine($"    auto* __evtBuf_{evtName}_{ei} = ((__EntJoyEventBuffer**)__header->eventBufferHeaders)[{ei}];");
                            }
                        }

                        // 收集 SendEvent 发现的事件类型
                        foreach (var evtType in translator.EventTypes)
                            discoveredEventTypes.Add(evtType.ToDisplayString());

                        // 托管事件类型错误：报告编译器诊断
                        if (translator.ManagedEventErrors.Count > 0)
                        {
                            foreach (var (evtSym, invoc) in translator.ManagedEventErrors)
                            {
                                var diag = Diagnostic.Create(
                                    new DiagnosticDescriptor(
                                        "NT015",
                                        "SendEvent requires unmanaged type",
                                        $"SendEvent<{evtSym.Name}>: event type must be unmanaged (blittable). " +
                                        $"Managed types (string, class, Dictionary) are not supported. " +
                                        $"Use a blittable signal struct.",
                                        "NativeTranspiler",
                                        DiagnosticSeverity.Error,
                                        isEnabledByDefault: true),
                                    invoc.GetLocation());
                                // 通过 Compilation 添加诊断
                                // 注意：此处无法直接修改 compilation，诊断在后续验证阶段统一报告
                            }
                            // 标记有错误，不生成 SendEvent 代码
                            discoveredEventTypes.Clear();
                        }
                    }
                }

            sb.AppendLine("}");
            sb.AppendLine();

            AppendMaskAdapterDispatch(sb, jobStruct, adapterMask, adapterFuncName,
                "void* context, const ChunkJobData* __chunkData", "context, __chunkData");
            sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{adapterFuncName}Ptr()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){adapterFuncName};");
            sb.AppendLine("}");
            sb.AppendLine();

            var rangeAdapterFuncName = GetRangeAdapterFunctionName(jobStruct);
            AppendMaskAdapterHeader(sb, rangeAdapterFuncName, "void* context, const ChunkJobData* __chunks, int __startIndex, int __count", adapterMask);
            sb.AppendLine("{");
            // 内联 Adapter：将 header + job 字段提至循环外
            sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
            sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
            sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
            sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
            sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
            AppendReadOnlyContextSnapshot(sb, contextSnapshotSize);
            sb.AppendLine("    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;");
            AppendChunkAdapterFields(sb, jobStruct, adapterMask, adapterLoopUse);
            sb.AppendLine("    const int __endIndex = __startIndex + __count;");
            sb.AppendLine("    for (int __chunkIndex = __startIndex; __chunkIndex < __endIndex; ++__chunkIndex)");
            sb.AppendLine("    {");
            sb.AppendLine("        auto* __chunkData = &__chunks[__chunkIndex];");
            // inline the adapter body into range loop
            if (isEntityJob)
            {
                // IJobEntity：走轻量 ChunkData 路径（转换 ChunkJobData → ChunkData）
                sb.AppendLine("        ChunkData __chunkDataLite;");
                sb.AppendLine("        __chunkDataLite.componentArrays = __chunkData->requiredComponentArrays;");
                sb.AppendLine("        __chunkDataLite.entityCount = __chunkData->entityCount;");
                sb.AppendLine("        __chunkDataLite.requiredComponentCount = __chunkData->requiredComponentCount;");
                sb.AppendLine("        __chunkDataLite.enableBitMaps = __chunkData->requiredEnableBitMaps != nullptr ? __chunkData->requiredEnableBitMaps : __chunkData->enableBitMaps;   // 逐组件 enable 位图");
                sb.AppendLine("        __chunkDataLite.enableBitmapCount = __chunkData->requiredEnableBitMaps != nullptr ? __chunkData->requiredComponentCount : __chunkData->componentCount;");
                // 仅当 Execute 声明 Entity 参数时才传实体数组
                if (executeMethod.Parameters.Any(p => NativeTranspiler.IsEntityType(p.Type)))
                    sb.AppendLine("        __chunkDataLite.entityArray = __chunkData->entityArray;");
                string funcName = GetCppJobFunctionName(jobStruct);
                string fieldArgs = BuildChunkExecuteFieldArgs(jobStruct);
                string rangeCallArgs = string.IsNullOrEmpty(fieldArgs)
                    ? $"&__chunkDataLite"
                    : $"&__chunkDataLite, {fieldArgs}";
                sb.AppendLine($"        {funcName}({rangeCallArgs});");
            }
            else
            {
                // IJobChunk: Range adapter inline Execute body
                if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                {
                    string funcName = GetCppJobFunctionName(jobStruct);
                    string fieldArgs = BuildChunkExecuteFieldArgs(jobStruct);
                    // usesSendEvent：Execute 签名多一个 __header 参数（SendEvent 需要）
                    bool rangeUsesSendEvent = JobUsesSendEvent(jobStruct, compilation);
                    string headerArg = rangeUsesSendEvent ? ", __header" : "";
                    string rangeCallArgs = string.IsNullOrEmpty(fieldArgs)
                        ? $"&__chunks[__chunkIndex], __requiredComponentTypeIds{headerArg}"
                        : $"&__chunks[__chunkIndex], __requiredComponentTypeIds, {fieldArgs}{headerArg}";
                    sb.AppendLine($"        {funcName}({rangeCallArgs});");
                }
                else
                {
                    if (methodSyntax?.Body != null)
                    {
                        var sm = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                        var rt = CollectChunkNativeArrayTypes(jobStruct, compilation);
                        var st = CollectSharedComponentTypes(jobStruct, compilation);
                        var tr = new CppChunkStatementTranslator(sm, jobStruct, rt, st, useFastMath);
                        foreach (var l in tr.Translate(methodSyntax.Body).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                            if (l.Length > 0) sb.Append("        ").AppendLine(l);
                    }
                }
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            AppendMaskAdapterDispatch(sb, jobStruct, adapterMask, rangeAdapterFuncName,
                "void* context, const ChunkJobData* __chunks, int __startIndex, int __count", "context, __chunks, __startIndex, __count");
            sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{rangeAdapterFuncName}Ptr()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){rangeAdapterFuncName};");
            sb.AppendLine("}");

            // Unity 风格 EntityBatch 适配器（IJobChunk 专用，IJobEntity 已走 ChunkRangeRaw）
            // EntityBatchAdapter 无法支持 shared components（shared 值是 per-chunk 的，
            // ISPC jobs: ISPC wrapper (IspcGenerator) 已生成 EntityBatch 函数，跳过 C++ 适配器。
            var sharedTypesForBatch = CollectSharedComponentTypes(jobStruct, compilation);
            if (!isEntityJob && sharedTypesForBatch.Count == 0 && !isIspcJob)
            {
            // 接收 EntityBatchData* 而非 ChunkJobData*，消除 requiredComponentArrays 指针追访
            // EntityBatchData 只含 componentArrays + entityCount，共 16 字节
            // 比 ChunkJobData（72 字节）更紧凑，cache 效率更高
            var entityBatchAdapterFuncName = GetEntityBatchAdapterFunctionName(jobStruct);
            AppendMaskAdapterHeader(sb, entityBatchAdapterFuncName,
                "void* context, const EntityBatchData* __batches, int __startIndex, int __count", adapterMask);
            var entityBatchHeader = $@"
{{
auto* __header = (__EntJoyChunkContextHeader*)context;
int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);
int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);
int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);
char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;";
            sb.Append(entityBatchHeader);
            sb.AppendLine();
            AppendReadOnlyContextSnapshot(sb, contextSnapshotSize);
            AppendChunkAdapterFields(sb, jobStruct, adapterMask, adapterLoopUse);
            sb.AppendLine("    const int __endIndex = __startIndex + __count;");
            sb.AppendLine("    for (int __batchIndex = __startIndex; __batchIndex < __endIndex; ++__batchIndex)");
            sb.AppendLine("    {");
            sb.AppendLine("        const EntityBatchData* __batchData = &__batches[__batchIndex];");
            bool returnsFromChunk = methodSyntax?.Body?.DescendantNodes().OfType<ReturnStatementSyntax>().Any() == true;
            if (returnsFromChunk) sb.AppendLine("        [&]() {");

            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled && methodSyntax?.Body != null)
            {
                // AutoSIMD: SimdControlFlowGenerator 真 SIMD
                // 与 GenerateChunkFunctionSIMD 相同的预处理（PreprocessIJobChunkAST 内含
                // SIMD 生成写入临时 simdSb，成功后才 append 到 sb —— 失败时无半截代码残留。
                var sm = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var simdSb = new StringBuilder();
                var (chunkArrays, entityLoopIv, modifiedBody) =
                    PreprocessIJobChunkAST(methodSyntax, sm, jobStruct, compilation);
                if (!JobUsesSendEvent(jobStruct, compilation) && !string.IsNullOrEmpty(entityLoopIv) && chunkArrays.Count > 0)
                {

                    // 组件数组指针声明（batch 级）
                    foreach (var (name, elemType, compIdx) in chunkArrays)
                    {
                        simdSb.AppendLine($"        auto* {name}_ptr = reinterpret_cast<{elemType}*>(__batchData->componentArrays[{compIdx}]);");
                        simdSb.AppendLine($"        (void)((intptr_t){name}_ptr % 64 == 0);");
                        simdSb.AppendLine($"        int {name}_length = __batchData->entityCount;");
                    }
                    simdSb.AppendLine("        int __entity_count = __batchData->entityCount;");

                    // 假方法（加 entityIdx 参数）供 SimdVariableAnalyzer
                    var newParams = methodSyntax.ParameterList.AddParameters(
                        SyntaxFactory.Parameter(SyntaxFactory.Identifier(entityLoopIv))
                            .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))));
                    var fakeMethod = methodSyntax.WithParameterList(newParams).WithBody(modifiedBody);

                    var varAnalyzer = new SimdVariableAnalyzer(sm, jobStruct, entityLoopIv);
                    var variables = varAnalyzer.Analyze(fakeMethod);
                    var nativeArrayParams = BuildChunkArrayNativeArrayParams(chunkArrays);
                    var _attrSym = AttributeHelper.GetAttributeSymbol(compilation);
                    var simdMathPrecision = _attrSym != null ? AttributeHelper.GetMathPrecision(jobStruct, _attrSym) : NativeTranspiler.SimdMathPrecision.Fastest;

                    // SIMD batch loop
                    simdSb.AppendLine("        int __simd_end = (__entity_count / NSIMD_WIDTH) * NSIMD_WIDTH;");
                    simdSb.AppendLine("        if (__simd_end > 0)");
                    simdSb.AppendLine("        {");
                    simdSb.AppendLine("            simd_value<int> v_base = simd_value<int>::sequence(0);");
                    simdSb.AppendLine("            for (int si = 0; si < __simd_end; si += NSIMD_WIDTH)");
                    simdSb.AppendLine("            {");
                    simdSb.AppendLine("                simd_value<int> v_i = v_base + si;");

                    var simdGen = new SimdControlFlowGenerator(
                        sm, jobStruct, variables, varAnalyzer,
                        indexParamName: entityLoopIv,
                        simdIndexVar: "v_i",
                        // NT-01(b)：偏移必须是外层批循环的当前位置 `si`（v_i = v_base + si）。
                        // 旧值 "0" 让每个 simd 组都重写 [0,W) ⇒ 元素 ≥W 永远写不到（静默错值）。
                        batchOffsetVar: "si",
                        batchLoopVar: "",
                        nativeArrayParams: nativeArrayParams,
                        simdMathPrecision: simdMathPrecision);
                    string simdBody = simdGen.Generate(modifiedBody);
                    foreach (var line in simdBody.Split('\n'))
                        if (!string.IsNullOrWhiteSpace(line))
                            simdSb.AppendLine($"                {line.TrimEnd()}");

                    simdSb.AppendLine("                __simd_exit: ;");
                    simdSb.AppendLine("            }");
                    simdSb.AppendLine("        }");

                    // 标量余量循环（__chunkData → __batchData 替换）
                    var remTr = new CppChunkStatementTranslator(sm, jobStruct,
                        CollectChunkNativeArrayTypes(jobStruct, compilation),
                        CollectSharedComponentTypes(jobStruct, compilation), useFastMath);
                    string remBody = remTr.Translate(methodSyntax.Body);
                    remBody = remBody.Replace("__chunkData->requiredComponentArrays", "__batchData->componentArrays");
                    remBody = remBody.Replace("__chunkData->requiredEnableBitMaps", "__batchData->enableBitMaps");
                    remBody = remBody.Replace("__chunkData->entityCount", "__batchData->entityCount");
                    // 移除 SIMD prelude 已声明的 ptr/length/entityCount
                    remBody = Regex.Replace(remBody, @"auto\* \w+_ptr = reinterpret_cast<[^>]+>\(__batchData->componentArrays\[\d+\]\);\r?\n?", "");
                    remBody = Regex.Replace(remBody, @"int \w+_length = __batchData->entityCount;\r?\n?", "");
                    remBody = Regex.Replace(remBody, @"int __entity_count = __batchData->entityCount;\r?\n?", "");
                    // 实体循环从 __simd_end 开始
                    string loopPattern = $"for (int {entityLoopIv} = 0; {entityLoopIv} <";
                    string loopReplacement = $"for (int {entityLoopIv} = __simd_end; {entityLoopIv} <";
                    remBody = remBody.Replace(loopPattern, loopReplacement);
                    foreach (var l in remBody.Split('\n'))
                        if (!string.IsNullOrWhiteSpace(l))
                            simdSb.AppendLine($"        {l.TrimEnd()}");

                    // 全部成功 → 合并到 sb
                    sb.Append(simdSb);
                }
                else
                {
                    // 不满足 SIMD 入口条件时，标量翻译完整实体循环，保持每个实体的执行次数。
                    var fbTr = new CppChunkStatementTranslator(sm, jobStruct,
                        CollectChunkNativeArrayTypes(jobStruct, compilation),
                        CollectSharedComponentTypes(jobStruct, compilation), useFastMath);
                    string fbBody = fbTr.Translate(methodSyntax.Body);
                    fbBody = fbBody.Replace("__chunkData->requiredComponentArrays", "__batchData->componentArrays");
                    fbBody = fbBody.Replace("__chunkData->requiredEnableBitMaps", "__batchData->enableBitMaps");
                    fbBody = fbBody.Replace("__chunkData->entityCount", "__batchData->entityCount");
                    foreach (var l in fbBody.Split('\n'))
                        if (!string.IsNullOrWhiteSpace(l))
                            sb.AppendLine($"        {l.TrimEnd()}");
                }
            }
            else if (methodSyntax?.Body != null)
            {
                // 标量路径（原有逻辑）
                var sm = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var rt = CollectChunkNativeArrayTypes(jobStruct, compilation);
                var st = CollectSharedComponentTypes(jobStruct, compilation);
                var tr = new CppChunkStatementTranslator(sm, jobStruct, rt, st, useFastMath);
                var bodyCode = tr.Translate(methodSyntax.Body);
                bodyCode = bodyCode.Replace("__chunkData->requiredComponentArrays", "__batchData->componentArrays");
                bodyCode = bodyCode.Replace("__chunkData->requiredEnableBitMaps", "__batchData->enableBitMaps");
                bodyCode = bodyCode.Replace("__chunkData->entityCount", "__batchData->entityCount");
                foreach (var l in bodyCode.Split(new[] { "\n" }, StringSplitOptions.None))
                {
                    if (l.Length > 0) sb.Append("        ").AppendLine(l);
                    string trimmed = l.TrimStart();
                    if (trimmed.StartsWith("auto*") && trimmed.Contains("reinterpret_cast<"))
                    {
                        string varName = trimmed.Split('=')[0].Trim().Split(' ').Last();
                        sb.AppendLine($"        (void)((intptr_t){varName} % 64 == 0);");
                    }
                }
            }
            if (returnsFromChunk) sb.AppendLine("        }();");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            AppendMaskAdapterDispatch(sb, jobStruct, adapterMask, entityBatchAdapterFuncName,
                "void* context, const EntityBatchData* __batches, int __startIndex, int __count", "context, __batches, __startIndex, __count");
            sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{entityBatchAdapterFuncName}Ptr()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){entityBatchAdapterFuncName};");
            sb.AppendLine("}");

            } // end if (!isEntityJob)

            }
        }
    }
}
