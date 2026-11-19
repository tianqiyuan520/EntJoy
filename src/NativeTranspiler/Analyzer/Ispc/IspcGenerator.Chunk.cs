// IspcGenerator.cs — 统一 ISPC 代码生成器
//   合并了原 IspcMethodGenerator.cs + IspcJobGenerator.cs 的功能，
//   共享 NativeList 上下文结构、类型转换、回调生成等逻辑。
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer.Common
{
    public static partial class IspcGenerator
    {

        private static string GenerateCppEntityBatchWrapper(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            var ispcBase = GetIspcBaseName(jobStruct);
            var ispcHeaderBase = ispcBase;
            var ispcImplBase = ispcBase;
            var ispcImplName = ispcImplBase + "_impl";
            var adapterFuncName = CppJobGenerator.GetEntityBatchAdapterFunctionName(jobStruct);
            var adapterGetterName = CppJobGenerator.GetEntityBatchAdapterPtrGetterName(jobStruct);
            var fields = jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);

            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine("#include \"EntityBatchData.h\"");
            sb.AppendLine($"#include \"{ispcHeaderBase}_ispc.h\"");
            sb.AppendLine("#include \"NativeGeneratedSupport.h\"");
            sb.AppendLine();
            GenerateResizeCallbacks(sb, fields.Select(f => (f.Type, f.Name)));
            sb.AppendLine();
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {adapterFuncName}(void* context, const EntityBatchData* __batches, int __batch_start, int __batch_count)");
            sb.AppendLine("{");
            sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
            sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
            sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
            sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
            sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
            sb.AppendLine();

            var fieldArgs = new List<string>();
            foreach (var slot in CppJobGenerator.BuildFieldLayout(jobStruct))
            {
                var cppType = slot.CppType;
                var ispcType = IspcTypeMapper.FromCpp(cppType);
                string address = CppIntegerSemantics.CastOperand(
                    CppIntegerSemantics.JobAddress("__jobContext", slot.Offset));
                if (IsVectorType(cppType) || cppType.Contains("::") || IsUserStructType(slot.Field.Type))
                    sb.AppendLine($"    auto* {slot.Field.Name}_ptr = reinterpret_cast<ispc::{ispcType}*>(({cppType}*){address});");
                else
                    sb.AppendLine($"    auto* {slot.Field.Name}_ptr = ({cppType}*){address};");
                fieldArgs.Add($"{slot.Field.Name}_ptr");
            }

            // Per-batch loop: read from EntityBatchData directly (single hop).
            // Both MT and non-MT use this path. ISPC MT is scheduled with
            // so the parallelization is entirely in the tile scheduler.
            {
                sb.AppendLine("    const int __batch_end = __batch_start + __batch_count;");
                sb.AppendLine("    for (int __batch_index = __batch_start; __batch_index < __batch_end; ++__batch_index)");
                sb.AppendLine("    {");
                sb.AppendLine("        const EntityBatchData* __batchData = &__batches[__batch_index];");

                var callArgs = new List<string>();
                for (int i = 0; i < executeMethod.Parameters.Length; i++)
                {
                    var parameter = executeMethod.Parameters[i];
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(parameter.Type);
                    var ispcType = IspcTypeMapper.FromCpp(cppType);
                    sb.AppendLine($"        auto* {parameter.Name}_ptr = reinterpret_cast<ispc::{ispcType}*>(__batchData->componentArrays[{i}]);");
                    sb.AppendLine($"        (void)((intptr_t){parameter.Name}_ptr % 64 == 0);");
                    callArgs.Add($"{parameter.Name}_ptr");
                }
                callArgs.Add("__batchData->entityCount");
                callArgs.AddRange(fieldArgs);
                sb.AppendLine($"        ispc::{ispcImplName}({string.Join(", ", callArgs)});");
                sb.AppendLine("    }");
            }
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION {adapterGetterName}()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){adapterFuncName};");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string GenerateCppChunkWrapper(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            var ispcBase = GetIspcBaseName(jobStruct);
            string ispcImplName = ispcBase + "_impl";
            string ispcHeaderBase = ispcBase;
            var adapterFuncName = CppJobGenerator.GetAdapterFunctionName(jobStruct);
            var adapterGetterName = CppJobGenerator.GetAdapterPtrGetterName(jobStruct);
            var rangeAdapterFuncName = CppJobGenerator.GetRangeAdapterFunctionName(jobStruct);
            var rangeAdapterGetterName = CppJobGenerator.GetRangeAdapterPtrGetterName(jobStruct);
            // IJobEntity + ISPC: 用 Execute 参数的组件类型构建 component array 列表
            // IJobChunk: 用 ChunkNativeArray locals
            List<(INamedTypeSymbol type, string name)> chunkArrays;
            var entityParamNames = new List<string>();
            if (CppJobGenerator.IsEntityJob(jobStruct))
            {
                chunkArrays = new List<(INamedTypeSymbol type, string name)>();
                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
                if (executeMethod != null)
                {
                    foreach (var param in executeMethod.Parameters)
                    {
                        if (NativeTranspiler.IsEntityType(param.Type))
                        {
                            entityParamNames.Add(param.Name);
                            continue;
                        }
                        if (param.Type is INamedTypeSymbol namedType)
                            chunkArrays.Add((namedType, param.Name));
                    }
                }
            }
            else
            {
                chunkArrays = CollectChunkNativeArrayLocals(jobStruct, compilation);
            }
            var fields = jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();

            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine("#include \"ChunkJobData.h\"");
            sb.AppendLine("#include \"ChunkNativeArray.h\"");
            sb.AppendLine($"#include \"{ispcHeaderBase}_ispc.h\"");
            sb.AppendLine("#include \"NativeGeneratedSupport.h\"");
            sb.AppendLine();
            GenerateResizeCallbacks(sb, fields.Select(f => (f.Type, f.Name)));
            sb.AppendLine();
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {adapterFuncName}(void* context, const ChunkJobData* __chunkData)");
            sb.AppendLine("{");
            sb.AppendLine("    EntityMatchMask __queryMask(__chunkData->filter, __chunkData->entityCount);");
            sb.AppendLine("    if (!__queryMask.HasMatches()) return;");
            sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
            sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
            sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
            sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
            sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
            sb.AppendLine("    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;");
            // IJobEntity：轻量 ChunkData 路径（转换 ChunkJobData → ChunkData）
            if (CppJobGenerator.IsEntityJob(jobStruct))
            {
                sb.AppendLine("    ChunkData __chunkDataLite{};");
                sb.AppendLine("    __chunkDataLite.filter = __chunkData->filter;");
                sb.AppendLine("    __chunkDataLite.componentArrays = __chunkData->requiredComponentArrays;");
                sb.AppendLine("    __chunkDataLite.entityCount = __chunkData->entityCount;");
                sb.AppendLine("    __chunkDataLite.requiredComponentCount = __chunkData->requiredComponentCount;");
                sb.AppendLine("    __chunkDataLite.enableBitMaps = __chunkData->requiredEnableBitMaps != nullptr ? __chunkData->requiredEnableBitMaps : __chunkData->enableBitMaps;   // 逐组件 enable 位图");
                sb.AppendLine("    __chunkDataLite.enableBitmapCount = __chunkData->requiredEnableBitMaps != nullptr ? __chunkData->requiredComponentCount : __chunkData->componentCount;");
                if (entityParamNames.Count > 0)
                    sb.AppendLine("    __chunkDataLite.entityArray = __chunkData->entityArray;");
            }
            sb.AppendLine();

            var callArgs = new List<string>();
            for (int i = 0; i < chunkArrays.Count; i++)
            {
                var (type, name) = chunkArrays[i];
                var ispcType = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(type));
                sb.AppendLine(CppJobGenerator.IsEntityJob(jobStruct)
                    ? $"    auto* {name}_ptr = reinterpret_cast<ispc::{ispcType}*>(__chunkDataLite.componentArrays[{i}]);"
                    : $"    auto* {name}_ptr = reinterpret_cast<ispc::{ispcType}*>(__chunkData->requiredComponentArrays[{i}]);");
                sb.AppendLine($"    (void)((intptr_t){name}_ptr % 64 == 0);");
                callArgs.Add($"{name}_ptr");
            }

            if (chunkArrays.Count > 0)
            {
                sb.AppendLine();
            }

            int currentOffset = 0;
            foreach (var field in fields)
            {
                int offset = CppJobGenerator.CalculateFieldOffset(field, ref currentOffset);
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                    {
                        var elemType = NativeTranspiler.GetContainerElementType(field.Type);
                        var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                        var ispcElem = IspcTypeMapper.FromCpp(cppElem);
                        sb.AppendLine($"    auto* {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{cppElem}>**)(__jobContext + {offset});");
                        sb.AppendLine($"    ispc::UnsafeList_Context_{ispcElem} {field.Name}_ctx;");
                        sb.AppendLine($"    {field.Name}_ctx._data = {field.Name}_listData->Ptr;");
                        sb.AppendLine($"    {field.Name}_ctx._length = {field.Name}_listData->Length;");
                        sb.AppendLine($"    {field.Name}_ctx._capacity = {field.Name}_listData->Capacity;");
                        sb.AppendLine($"    {field.Name}_ctx._allocator = static_cast<int>({field.Name}_listData->Allocator);");
                        sb.AppendLine($"    {field.Name}_ctx.ResizeFunc = UnsafeList_Resize_{ispcElem}_callback;");
                        callArgs.Add($"&{field.Name}_ctx");
                    }
                    else
                    {
                        var elemType = NativeTranspiler.GetContainerElementType(field.Type);
                        var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                        var ispcElem = IspcTypeMapper.FromCpp(cppElem);
                        string qualifier = NativeTranspiler.IsReadOnlyNativeArray(field.Type) ? "const " : "";
                        sb.AppendLine($"    auto* {field.Name}_ptr = reinterpret_cast<{qualifier}ispc::{ispcElem}*>(*({qualifier}{cppElem}**)(__jobContext + {offset}));");
                        sb.AppendLine($"    int {field.Name}_length = *(int*)(__jobContext + {offset + 8});");
                        callArgs.Add($"{field.Name}_ptr");
                        callArgs.Add($"{field.Name}_length");
                    }
                }
                else if (field.Type is IPointerTypeSymbol ptrType)
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(ptrType.PointedAtType);
                    var ispcType = IspcTypeMapper.FromCpp(cppType);
                    sb.AppendLine($"    auto* {field.Name}_ptr = reinterpret_cast<ispc::{ispcType}*>(*({cppType}**)(__jobContext + {offset}));");
                    callArgs.Add($"{field.Name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    var ispcType = IspcTypeMapper.FromCpp(cppType);
                    if (IsVectorType(cppType) || cppType.Contains("::") || IsUserStructType(field.Type))
                    {
                        sb.AppendLine($"    auto* {field.Name}_ptr = reinterpret_cast<ispc::{ispcType}*>(({cppType}*)(__jobContext + {offset}));");
                    }
                    else
                    {
                        sb.AppendLine($"    auto* {field.Name}_ptr = ({cppType}*)(__jobContext + {offset});");
                    }
                    callArgs.Add($"{field.Name}_ptr");
                }
            }

            sb.AppendLine();
            // __entity_count 放在参数末尾（对齐 ISPC 函数签名）
            callArgs.Add(CppJobGenerator.IsEntityJob(jobStruct)
                ? "__chunkDataLite.entityCount"
                : "__chunkData->entityCount");
            callArgs.Add("__queryMask.Bits");
            callArgs.Add("__queryMask.Length");
            // DOTS 式 Entity 参数：实体数组指针（ispc::__EntJoyEntity*，与 ISPC 签名 uniform __EntJoyEntity {name}_ptr[] 对应）
            foreach (var epName in entityParamNames)
                callArgs.Add($"reinterpret_cast<ispc::__EntJoyEntity*>(__chunkDataLite.entityArray)");
            // SendEvent: 传递 EventBuffer 元数据（无事件时传 0/null，非 event job 零开销）
            bool usesSendEvent = CppJobGenerator.JobUsesSendEvent(jobStruct, compilation);
            if (usesSendEvent)
            {
                callArgs.Add("(void*)__header->eventBufferHeaders");
                callArgs.Add("__header->eventBufferCount");
            }
            sb.AppendLine($"    ispc::{ispcImplName}({string.Join(", ", callArgs)});");

            foreach (var field in fields)
            {
                if (!NativeTranspiler.IsEntJoyNativeContainerType(field.Type) || field.Type.Name != Config.NativeList)
                {
                    continue;
                }

                var elemType = NativeTranspiler.GetContainerElementType(field.Type);
                var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                sb.AppendLine($"    {field.Name}_listData->Length = {field.Name}_ctx._length;");
                sb.AppendLine($"    {field.Name}_listData->Capacity = {field.Name}_ctx._capacity;");
                sb.AppendLine($"    {field.Name}_listData->Ptr = static_cast<{cppElem}*>({field.Name}_ctx._data);");
                sb.AppendLine($"    {field.Name}_listData->Allocator = static_cast<EntJoy::Collections::Allocator>({field.Name}_ctx._allocator);");
            }

            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION {adapterGetterName}()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){adapterFuncName};");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {rangeAdapterFuncName}(void* context, const ChunkJobData* __chunks, int __startIndex, int __count)");
            sb.AppendLine("{");
            sb.AppendLine("    const int __endIndex = __startIndex + __count;");
            sb.AppendLine("    for (int __chunkIndex = __startIndex; __chunkIndex < __endIndex; ++__chunkIndex)");
            sb.AppendLine("    {");
            sb.AppendLine($"        {adapterFuncName}(context, &__chunks[__chunkIndex]);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION {rangeAdapterGetterName}()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){rangeAdapterFuncName};");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>发射「每个 lane 一个标量槽」的循环，带查询过滤时换算回实体下标查掩码。</summary>
        private static void EmitFlatLoop(StringBuilder sb, string flatBody, int slots)
        {
            var lines = flatBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.RemoveEmptyEntries);
            sb.AppendLine($"{Indent}const uniform int __slots = {slots};");
            sb.AppendLine($"{Indent}if (__query_length == 0) {{");
            sb.AppendLine($"{Indent}{Indent}foreach_tiled (__flat = 0 ... __entity_count * __slots)");
            sb.AppendLine($"{Indent}{Indent}{{");
            foreach (var line in lines) sb.Append($"{Indent}{Indent}{Indent}").Append(line).AppendLine();
            sb.AppendLine($"{Indent}{Indent}}}");
            sb.AppendLine($"{Indent}}} else {{");
            sb.AppendLine($"{Indent}{Indent}foreach_tiled (__flat = 0 ... __entity_count * __slots)");
            sb.AppendLine($"{Indent}{Indent}{{");
            sb.AppendLine($"{Indent}{Indent}{Indent}int __ei = __flat / __slots;");
            sb.AppendLine($"{Indent}{Indent}{Indent}if ((__query_bits[__ei >> 6] & ((uint64)1 << (__ei & 63))) == 0) continue;");
            foreach (var line in lines) sb.Append($"{Indent}{Indent}{Indent}").Append(line).AppendLine();
            sb.AppendLine($"{Indent}{Indent}}}");
            sb.AppendLine($"{Indent}}}");
        }

        /// <summary>
        /// 摊平实体内核：判据在 <see cref="FlatShape"/> 里按语法树给出，这里只负责发射。
        /// 访问重写只针对生成器自己发射的 `参数_ptr[__entity_index].字段`，判据已证明没有别的访问。
        /// </summary>
        private static bool TryEmitFlattenedEntityLoop(StringBuilder sb, IspcStatementTranslator translator,
            string bodyCode, BlockSyntax body, IMethodSymbol executeMethod, SemanticModel semanticModel, out int slots)
        {
            slots = 0;
            var shape = FlatShape.TryAnalyze(body, executeMethod, semanticModel);
            if (shape == null) return false;

            string flatBody;
            if (shape.ScalarFields)
            {
                var canonical = shape.Statements[0];
                var ordinals = new Dictionary<LiteralExpressionSyntax, int>();
                foreach (var literal in canonical.DescendantNodes().OfType<LiteralExpressionSyntax>())
                    if (literal.Token.IsKind(SyntaxKind.NumericLiteralToken))
                        ordinals[literal] = ordinals.Count;
                translator.SetFlatLiteralFormatter(literal =>
                    ordinals.TryGetValue(literal, out int index) ? EmitSlotConstants(translator, shape, index) : null);
                flatBody = translator.CaptureStatement(canonical);
                translator.SetFlatLiteralFormatter(null);
            }
            else
            {
                flatBody = bodyCode;
            }

            flatBody = System.Text.RegularExpressions.Regex.Replace(flatBody,
                @"([A-Za-z_]\w*)_ptr\[__entity_index\]\.[A-Za-z_]\w*",
                m => $"(({shape.ScalarType}*){m.Groups[1].Value}_ptr)[__flat]");
            if (flatBody.Contains("__entity_index")) return false;

            EmitFlatLoop(sb, flatBody, shape.Slots);
            slots = shape.Slots;
            return true;
        }

        /// <summary>按槽取常量：各槽一致就放原字面量，否则发 `__flat % __slots` 的 select 链。</summary>
        private static string EmitSlotConstants(IspcStatementTranslator translator, FlatShape shape, int ordinal)
        {
            var perSlot = new List<string>();
            foreach (var literals in shape.LiteralsPerSlot)
            {
                if (literals == null || ordinal >= literals.Count) return null;
                perSlot.Add(translator.CaptureExpression(literals[ordinal]));
            }
            if (perSlot.Distinct().Count() == 1) return perSlot[0];
            string expression = perSlot[perSlot.Count - 1];
            for (int slot = perSlot.Count - 2; slot >= 0; slot--)
                expression = $"(  __flat % __slots == {slot} ? {perSlot[slot]} : {expression} )";
            return expression;
        }
        /// <summary>
        /// 翻译 chunk Execute 体。出现串行循环时整个函数按 uniform 语义重译一遍：
        /// 循环外的中转/累加局部（`T head = values[0];`、`int sum = 0;`）必须是 uniform，
        /// 否则 varying 值赋给 uniform 定位过不了 ISPC 编译（G297C951C）。
        /// </summary>
        private static string TranslateChunkBody(SemanticModel semanticModel, INamedTypeSymbol jobStruct,
            MethodDeclarationSyntax methodSyntax, string? foreachStart, string? foreachEnd,
            bool usesSendEvent, out bool usesSerial)
        {
            IspcChunkStatementTranslator Create(bool uniformVars)
            {
                var translator = foreachStart == null
                    ? new IspcChunkStatementTranslator(semanticModel, jobStruct, uniformVars)
                    : new IspcChunkStatementTranslator(semanticModel, jobStruct, foreachStart, foreachEnd, uniformVars);
                if (usesSendEvent)
                    translator.SetEventBufferParamName("__eventBufferHeaders");
                return translator;
            }

            var first = Create(false);
            string body = first.Translate(methodSyntax.Body);
            usesSerial = first.UsesSerialChunkLoop;
            if (!usesSerial) return body;
            return Create(true).Translate(methodSyntax.Body);
        }

        private static void GenerateIspcChunkFunction(StringBuilder sb, string functionName,
            INamedTypeSymbol jobStruct, Compilation compilation, SemanticModel semanticModel,
            MethodDeclarationSyntax methodSyntax)
        {
            var chunkArrays = CppJobGenerator.IsEntityJob(jobStruct)
                ? CollectEntityNativeArrays(jobStruct)
                : CollectChunkNativeArrayLocals(jobStruct, compilation);
            var fields = GetFieldsFromJob(jobStruct);
            var paramsList = BuildIspcChunkParamList(chunkArrays, fields);

            // DOTS 式 Entity 参数：追加 uniform __EntJoyEntity {name}_ptr[]（数组指针，与 entityArray 对应）。
            // 用局部结构体 __EntJoyEntity 而非全局 Entity，避免与生成的 EntJoy_ECS_Entity.ispc（用户结构字段）redefinition。
            var entityParams = CppJobGenerator.IsEntityJob(jobStruct)
                ? jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute)
                    .Parameters.Where(p => NativeTranspiler.IsEntityType(p.Type)).ToList()
                : new List<IParameterSymbol>();
            if (entityParams.Count > 0)
                sb.AppendLine("struct __EntJoyEntity { int Id; int Version; };");
            foreach (var ep in entityParams)
                paramsList += $", uniform __EntJoyEntity {ep.Name}_ptr[]";

            // SendEvent: 追加 EventBuffer 参数（仅当 Job 使用 SendEvent）
            bool usesSendEvent = CppJobGenerator.JobUsesSendEvent(jobStruct, compilation);
            if (usesSendEvent)
                // 注意：ISPC 中 "uniform void*" 非法（void 不能带 uniform 限定），必须用 "void* uniform"（指针 uniform）
                paramsList += ", void* uniform __eventBufferHeaders, uniform int __eventBufferCount";

            sb.AppendLine($"export void {functionName}({paramsList})");
            sb.AppendLine("{");
            AppendUniformVariableDeclarations(sb, jobStruct);

            if (CppJobGenerator.IsEntityJob(jobStruct))
            {
                // IJobEntity: use IspcStatementTranslator + AddEntityRefParam + foreach_tiled
                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
                bool scalar = HasMutableValueFields(jobStruct, semanticModel, methodSyntax);
                if (scalar) sb.AppendLine($"{Indent}if (programIndex != 0) return;");
                var entityTranslator = new IspcStatementTranslator(semanticModel, jobStruct, null, false, useUniformVars: scalar);
                foreach (var parameter in executeMethod.Parameters)
                    if (!NativeTranspiler.IsEntityType(parameter.Type)
                        && !(NativeTranspileValidator.IsRelationComponentType(parameter.Type) && parameter.RefKind == RefKind.None))
                        entityTranslator.AddEntityRefParam(parameter.Name);
                entityTranslator.SetInsideForeach(!scalar);
                entityTranslator.SetInsideBatchIndexLoop(true);
                // SendEvent 需要 EventBuffer 参数名（ISPC 侧用 __eventBufferHeaders 直接访问）
                if (usesSendEvent)
                    entityTranslator.SetEventBufferParamName("__eventBufferHeaders");
                var bodyCode = entityTranslator.Translate(methodSyntax.Body);

                // 分量摊平：组件是「单字段包装的 float2/3/4」且循环体对所有分量做同一套运算时，
                // 把每个 lane 从"一个结构体"改成"一个标量分量"，访存变连续。
                // 实测轻量实体内核：逐字段 0.3861 ms → 摊平 0.0311 ms（标量 C++ 0.0330 ms）。
                if (!scalar && entityParams.Count == 0
                    && TryEmitFlattenedEntityLoop(sb, entityTranslator, bodyCode, methodSyntax.Body,
                        executeMethod, semanticModel, out _))
                {
                    sb.AppendLine("}");
                    return;
                }


                // 实体循环：先按"是否带查询过滤"分成两版，而不是每 lane 查一次掩码。
                // 掩码检查在无过滤（__query_length == 0，最常见）时纯属开销；实测轻量体 1.2×。
                void EmitEntityLoop(bool masked, string extraIndent)
                {
                    sb.AppendLine($"{extraIndent}    foreach_tiled (__entity_index = 0 ... __entity_count) {{");
                    if (masked)
                        sb.AppendLine($"{extraIndent}        if ((__query_bits[__entity_index >> 6] & ((uint64)1 << (__entity_index & 63))) == 0) continue;");
                    // Entity 参数解引用为局部结构体（body 内通过 entity.Id / entity.Version 访问）
                    foreach (var ep in entityParams)
                        sb.AppendLine($"{extraIndent}{Indent}{Indent}__EntJoyEntity {ep.Name} = {ep.Name}_ptr[__entity_index];");
                    foreach (var parameter in executeMethod.Parameters)
                        if (NativeTranspileValidator.IsRelationComponentType(parameter.Type) && parameter.RefKind == RefKind.None)
                            sb.AppendLine($"{extraIndent}{Indent}{Indent}{IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(parameter.Type))} {parameter.Name} = {parameter.Name}_ptr[__entity_index];");
                    foreach (var line in bodyCode.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                    {
                        if (line.Length == 0) continue;
                        sb.Append(extraIndent).Append(Indent).Append(line).AppendLine();
                    }
                    sb.AppendLine($"{extraIndent}    }}");
                }

                if (scalar)
                {
                    sb.AppendLine("    for (uniform int __entity_index = 0; __entity_index < __entity_count; ++__entity_index) {");
                    sb.AppendLine("        if (__query_length != 0 && (__query_bits[__entity_index >> 6] & ((uint64)1 << (__entity_index & 63))) == 0) continue;");
                    foreach (var ep in entityParams)
                        sb.AppendLine($"{Indent}{Indent}uniform __EntJoyEntity {ep.Name} = {ep.Name}_ptr[__entity_index];");
                    foreach (var parameter in executeMethod.Parameters)
                        if (NativeTranspileValidator.IsRelationComponentType(parameter.Type) && parameter.RefKind == RefKind.None)
                            sb.AppendLine($"{Indent}{Indent}uniform {IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(parameter.Type))} {parameter.Name} = {parameter.Name}_ptr[__entity_index];");
                    foreach (var line in bodyCode.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                    {
                        if (line.Length == 0) continue;
                        sb.Append(Indent).Append(line).AppendLine();
                    }
                    sb.AppendLine("    }");
                }
                else
                {
                    sb.AppendLine("    if (__query_length == 0) {");
                    EmitEntityLoop(masked: false, extraIndent: Indent);
                    sb.AppendLine("    } else {");
                    EmitEntityLoop(masked: true, extraIndent: Indent);
                    sb.AppendLine("    }");
                }
            }
            else
            {
                if (HasMutableValueFields(jobStruct, semanticModel, methodSyntax))
                {
                    sb.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_ChunkJobValueFieldMutation");
                    sb.AppendLine("}");
                    return;
                }
                // IJobChunk: use IspcChunkStatementTranslator
                var bodyCode2 = TranslateChunkBody(semanticModel, jobStruct, methodSyntax, null, null,
                    usesSendEvent, out bool usesSerialLoop);
                if (usesSerialLoop)
                    sb.AppendLine($"{Indent}if (programIndex != 0) return;");
                AppendIspcBody(sb, bodyCode2);
            }

            sb.AppendLine("}");
        }

        private static string BuildIspcChunkParamList(
            List<(INamedTypeSymbol type, string name)> chunkArrays,
            IEnumerable<(ITypeSymbol type, string name)> fields)
        {
            var pars = new StringBuilder();
            foreach (var (type, name) in chunkArrays)
            {
                if (pars.Length > 0) pars.Append(", ");
                var ispcType = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(type));
                string readOnly = NativeTranspileValidator.IsRelationComponentType(type) ? "const " : "";
                pars.Append($"uniform {readOnly}{ispcType} {name}_ptr[]");
            }

            foreach (var (type, name) in fields)
            {
                if (pars.Length > 0) pars.Append(", ");
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(type, Config.NativeList))
                    {
                        var elemType = NativeTranspiler.GetContainerElementType(type);
                        var ispcElem = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                        pars.Append($"uniform UnsafeList_Context_{ispcElem}* uniform {name}");
                    }
                    else
                    {
                        var elemType = NativeTranspiler.GetContainerElementType(type);
                        var ispcElem = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                        string qualifier = NativeTranspiler.IsReadOnlyNativeArray(type) ? "const " : "";
                        pars.Append($"uniform {qualifier}{ispcElem} {name}_ptr[], uniform int {name}_length");
                    }
                }
                else if (type is IPointerTypeSymbol ptrType)
                {
                    var baseCpp = NativeTranspiler.MapCSharpTypeToCpp(ptrType.PointedAtType);
                    pars.Append($"uniform {IspcTypeMapper.FromCpp(baseCpp)} * uniform {name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                    pars.Append($"uniform {IspcTypeMapper.FromCpp(cppType)} * uniform {name}_ptr");
                }
            }

            if (pars.Length > 0)
                pars.Append(", ");
            pars.Append("uniform int __entity_count, uniform const uint64 __query_bits[], uniform int __query_length");

            return pars.ToString();
        }

        private static List<(INamedTypeSymbol type, string name)> CollectChunkNativeArrayLocals(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var result = new List<(INamedTypeSymbol type, string name)>();
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            var methodSyntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return result;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var localDecl in methodSyntax.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
            {
                foreach (var variable in localDecl.Declaration.Variables)
                {
                    if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation)
                        continue;
                    if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
                        continue;
                    if (methodSymbol.ContainingType?.ToDisplayString() != Config.TypeArchetypeChunk ||
                        !Config.IsChunkArrayAccess(methodSymbol.Name) ||
                        methodSymbol.TypeArguments.Length == 0 ||
                        methodSymbol.TypeArguments[0] is not INamedTypeSymbol componentType)
                    {
                        continue;
                    }

                    result.Add((componentType, variable.Identifier.Text));
                }
            }

            return result;
        }

        /// <summary>
        /// 从 IJobEntity 的 Execute 参数收集组件类型列表
        /// </summary>
        private static List<(INamedTypeSymbol type, string name)> CollectEntityNativeArrays(
            INamedTypeSymbol jobStruct)
        {
            var result = new List<(INamedTypeSymbol type, string name)>();
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            if (executeMethod == null) return result;
            foreach (var param in executeMethod.Parameters)
            {
                if (NativeTranspiler.IsEntityType(param.Type))
                    continue;   // Entity 参数不是组件列
                if (param.Type is INamedTypeSymbol namedType)
                    result.Add((namedType, param.Name));
            }
            return result;
        }

    }
}

