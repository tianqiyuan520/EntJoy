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
        private static string GenerateIspcChunkMTSource(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            var baseName = GetIspcBaseName(jobStruct);
            sb.AppendLine($"// Auto-generated ISPC MT source for {jobStruct.Name}");

            var fields = GetFieldsFromJob(jobStruct);
            var includes = CollectMethodIncludes(jobStruct.GetMembers().OfType<IMethodSymbol>().First(method => method.Name == Config.Execute), compilation, fields);
            var chunkArrays = CppJobGenerator.IsEntityJob(jobStruct)
                ? CollectEntityNativeArrays(jobStruct)
                : CollectChunkNativeArrayLocals(jobStruct, compilation);
            foreach (var component in CppJobGenerator.CollectChunkNativeArrayTypes(jobStruct, compilation))
                includes.Add(NativeTranspiler.GetStructHeaderFileName(component));
            // SendEvent: 事件类型 include
            foreach (var evtType in CppJobGenerator.CollectSendEventTypes(jobStruct, compilation))
                includes.Add(NativeTranspiler.GetStructHeaderFileName(evtType));
            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return "// Error: no Execute body";
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var paramsList = BuildIspcChunkParamList(chunkArrays, fields);
            // SendEvent: 追加 EventBuffer 参数（仅当 Job 使用 SendEvent）
            bool usesSendEvent = CppJobGenerator.JobUsesSendEvent(jobStruct, compilation);
            if (usesSendEvent)
                // 注意：ISPC 中 "uniform void*" 非法（void 不能带 uniform 限定），必须用 "void* uniform"（指针 uniform）
                paramsList += ", void* uniform __eventBufferHeaders, uniform int __eventBufferCount";
            string entityCountExpr = "__entity_count";
            string taskParams = paramsList;

            sb.AppendLine($"task void {baseName}_task({taskParams})");
            sb.AppendLine("{");
            sb.AppendLine($"{Indent}uniform int n_per_task = max(1, {entityCountExpr} / taskCount);");
            sb.AppendLine($"{Indent}uniform int __task_start = taskIndex * n_per_task;");
            sb.AppendLine($"{Indent}uniform int __task_end = (taskIndex == taskCount - 1) ? {entityCountExpr} : min(__task_start + n_per_task, {entityCountExpr});");
            AppendUniformVariableDeclarations(sb, jobStruct);

            var translator = new IspcChunkStatementTranslator(semanticModel, jobStruct, "__task_start", "__task_end");
            if (usesSendEvent)
                translator.SetEventBufferParamName("__eventBufferHeaders");
            sb.Append(translator.Translate(methodSyntax.Body));
            sb.AppendLine("}");
            sb.AppendLine();

            string mtParams = $"{paramsList}, uniform int numTasks";
            sb.AppendLine($"export void {baseName}_mt_impl({mtParams})");
            sb.AppendLine("{");
            string callArgs = $"{BuildIspcCallArgsForChunkMT(chunkArrays, fields)}, __entity_count";
            if (usesSendEvent)
                callArgs += ", __eventBufferHeaders, __eventBufferCount";
            sb.AppendLine($"{Indent}launch[max(1, numTasks)] {baseName}_task({callArgs});");
            sb.AppendLine($"{Indent}sync;");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string BuildIspcCallArgsForChunkMT(
            List<(INamedTypeSymbol type, string name)> chunkArrays,
            IEnumerable<(ITypeSymbol type, string name)> fields)
        {
            var args = new List<string>();
            foreach (var (_, name) in chunkArrays)
            {
                args.Add($"{name}_ptr");
            }
            foreach (var (type, name) in fields)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                {
                    args.Add(NativeTranspiler.IsEntJoyContainerNamed(type, Config.NativeList) ? name : $"{name}_ptr, {name}_length");
                }
                else
                {
                    args.Add($"{name}_ptr");
                }
            }
            return string.Join(", ", args);
        }

        private static string GenerateCppEntityBatchWrapper(INamedTypeSymbol jobStruct, Compilation compilation, bool useMt = false)
        {
            var sb = new StringBuilder();
            var ispcBase = GetIspcBaseName(jobStruct);
            var ispcHeaderBase = ispcBase;
            var ispcImplBase = ispcBase;  // for MT, redirect to non-MT ISPC functions
            if (useMt)
            {
                ispcHeaderBase = ispcBase.Replace("IspcMt", "Ispc");
                ispcImplBase = ispcBase.Replace("IspcMt", "Ispc");
            }
            var ispcImplName = ispcImplBase + "_impl";
            var adapterFuncName = CppJobGenerator.GetEntityBatchAdapterFunctionName(jobStruct);
            var adapterGetterName = CppJobGenerator.GetEntityBatchAdapterPtrGetterName(jobStruct);
            var fields = jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);

            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine("#include \"EntityBatchData.h\"");
            sb.AppendLine($"#include \"{ispcHeaderBase}_ispc.h\"");
            if (useMt)
            {
                sb.AppendLine("#include <thread>");
                sb.AppendLine("#include <cstdint>");
            }
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();
            GenerateResizeCallbacks(sb, fields.Select(f => (f.Type, f.Name)));
            sb.AppendLine();
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
            sb.AppendLine("};");
            sb.AppendLine("#endif");
            sb.AppendLine();
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {adapterFuncName}(void* context, const EntityBatchData* __batches, int __batch_start, int __batch_count)");
            sb.AppendLine("{");
            sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
            sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
            sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
            sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
            sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
            sb.AppendLine();

            int currentOffset = 0;
            var fieldArgs = new List<string>();
            foreach (var field in fields)
            {
                int offset = CppJobGenerator.CalculateFieldOffset(field, ref currentOffset);
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                var ispcType = IspcTypeMapper.FromCpp(cppType);
                if (IsVectorType(cppType) || cppType.Contains("::"))
                    sb.AppendLine($"    auto* {field.Name}_ptr = reinterpret_cast<ispc::{ispcType}*>(({cppType}*)(__jobContext + {offset}));");
                else
                    sb.AppendLine($"    auto* {field.Name}_ptr = ({cppType}*)(__jobContext + {offset});");
                fieldArgs.Add($"{field.Name}_ptr");
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

        private static string GenerateCppChunkWrapper(INamedTypeSymbol jobStruct, Compilation compilation, bool useMt = false)
        {
            var sb = new StringBuilder();
            var ispcBase = GetIspcBaseName(jobStruct);
            string ispcImplName;
            string ispcHeaderBase;
            if (useMt && CppJobGenerator.IsEntityJob(jobStruct))
            {
                // IJobEntity ISPC MT: 复用非 MT 的 ISPC 实现（函数名相同）
                var nonMtBase = ispcBase.Replace("IspcMt", "Ispc");
                ispcImplName = nonMtBase + "_impl";
                ispcHeaderBase = nonMtBase;  // 非 MT header，无 _mt 后缀
            }
            else
            {
                ispcImplName = useMt ? ispcBase + "_mt_impl" : ispcBase + "_impl";
                ispcHeaderBase = useMt ? ispcBase + "_mt" : ispcBase;
            }
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
            if (useMt)
                sb.AppendLine("#include <thread>");
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();
            GenerateResizeCallbacks(sb, fields.Select(f => (f.Type, f.Name)));
            sb.AppendLine();
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
            sb.AppendLine("    // Event Buffer（与 C# ChunkContextHeader 对齐，SendEvent 需要）");
            sb.AppendLine("    int eventBufferCount;");
            sb.AppendLine("    void* eventBufferHeaders;");
            sb.AppendLine("    void* eventWorldHandle;");
            sb.AppendLine("};");
            sb.AppendLine("#endif");
            sb.AppendLine();
            sb.AppendLine("#ifndef __EntJoyEventBuffer_DEFINED");
            sb.AppendLine("#define __EntJoyEventBuffer_DEFINED");
            sb.AppendLine("struct __EntJoyEventBuffer {");
            sb.AppendLine("    int* data;");
            sb.AppendLine("    int* count;");
            sb.AppendLine("    int capacity;");
            sb.AppendLine("    int elementSize;");
            sb.AppendLine("};");
            sb.AppendLine("#endif");
            sb.AppendLine();
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {adapterFuncName}(void* context, const ChunkJobData* __chunkData)");
            sb.AppendLine("{");
            sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
            sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
            sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
            sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
            sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
            sb.AppendLine("    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;");
            // IJobEntity：轻量 ChunkData 路径（转换 ChunkJobData → ChunkData）
            if (CppJobGenerator.IsEntityJob(jobStruct))
            {
                sb.AppendLine("    ChunkData __chunkDataLite;");
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
                        var elemType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
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
                        var elemType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                        var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                        var ispcElem = IspcTypeMapper.FromCpp(cppElem);
                        sb.AppendLine($"    auto* {field.Name}_ptr = reinterpret_cast<ispc::{ispcElem}*>(*({cppElem}**)(__jobContext + {offset}));");
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
                    if (IsVectorType(cppType) || cppType.Contains("::"))
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
            // DOTS 式 Entity 参数：实体数组指针（ispc::__EntJoyEntity*，与 ISPC 签名 uniform __EntJoyEntity {name}_ptr[] 对应）
            foreach (var epName in entityParamNames)
                callArgs.Add($"reinterpret_cast<ispc::__EntJoyEntity*>(__chunkDataLite.entityArray)");
            // ISPC MT for non-entity jobs uses a separate _mt_impl that takes numTasks
            if (useMt && !CppJobGenerator.IsEntityJob(jobStruct))
                callArgs.Add("std::thread::hardware_concurrency()");
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

                var elemType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
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
                    if (!NativeTranspiler.IsEntityType(parameter.Type))
                        entityTranslator.AddEntityRefParam(parameter.Name);
                entityTranslator.SetInsideForeach(!scalar);
                entityTranslator.SetInsideBatchIndexLoop(true);
                // SendEvent 需要 EventBuffer 参数名（ISPC 侧用 __eventBufferHeaders 直接访问）
                if (usesSendEvent)
                    entityTranslator.SetEventBufferParamName("__eventBufferHeaders");
                var bodyCode = entityTranslator.Translate(methodSyntax.Body);
                sb.AppendLine(scalar ? "    for (uniform int __entity_index = 0; __entity_index < __entity_count; ++__entity_index) {"
                    : "    foreach_tiled (__entity_index = 0 ... __entity_count) {");
                // Entity 参数解引用为局部结构体（body 内通过 entity.Id / entity.Version 访问）
                foreach (var ep in entityParams)
                    sb.AppendLine($"{Indent}{Indent}{(scalar ? "uniform " : "")}__EntJoyEntity {ep.Name} = {ep.Name}_ptr[__entity_index];");
                foreach (var line in bodyCode.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append(Indent).Append(line).AppendLine();
                }
                sb.AppendLine("    }");
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
                var translator = new IspcChunkStatementTranslator(semanticModel, jobStruct);
                if (usesSendEvent)
                    translator.SetEventBufferParamName("__eventBufferHeaders");
                var bodyCode2 = translator.Translate(methodSyntax.Body);
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
                pars.Append($"uniform {ispcType} {name}_ptr[]");
            }

            foreach (var (type, name) in fields)
            {
                if (pars.Length > 0) pars.Append(", ");
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(type, Config.NativeList))
                    {
                        var elemType = ((INamedTypeSymbol)type).TypeArguments[0];
                        var ispcElem = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                        pars.Append($"uniform UnsafeList_Context_{ispcElem}* uniform {name}");
                    }
                    else
                    {
                        var elemType = ((INamedTypeSymbol)type).TypeArguments[0];
                        var ispcElem = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                        pars.Append($"uniform {ispcElem} {name}_ptr[], uniform int {name}_length");
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
            pars.Append("uniform int __entity_count");

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
                        methodSymbol.Name != Config.GetComponentDataNativeArray ||
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
