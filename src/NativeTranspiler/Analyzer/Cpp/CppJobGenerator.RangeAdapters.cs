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


        private static void AppendRangeAdapter(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb)
        {
            var baseFuncName = GetCppJobFunctionName(jobStruct);
            var adapterFuncName = baseFuncName + "_Adapter";
            var boolFields = GetBoolConditionalFields(jobStruct, compilation);

            // 生成适配函数
            // 纯值字段打包：结构体定义必须在本 TU 可见（适配器是独立 TU）⇒ 放在函数之前。
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {adapterFuncName}(void* context, int __startIndex, int __count)");
            sb.AppendLine("{");

            // 生成字段读取代码
            // 批入口收 job 结构体地址 ⇒ adapter 只转发，不再逐字段解包。
            var callArgs = new List<string> { "context", "__startIndex", "__count" };

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
                foreach (var (mask, values) in BoolVariants.Enumerate(boolFields.Count))
                {

                    string suffix = BoolVariants.Suffix(values);
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
            sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{adapterFuncName}Ptr()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){adapterFuncName};");
            sb.AppendLine("}");

            // IJobFor's index entry executes one element through the same
            // context ABI and variant selection as its batch adapter.
            if (IsForJob(jobStruct))
            {
                string indexAdapterFuncName = GetIndexAdapterFunctionName(jobStruct);
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {indexAdapterFuncName}(void* context, int __index)");
                sb.AppendLine("{");
                sb.AppendLine($"    {adapterFuncName}(context, __index, 1);");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{indexAdapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){indexAdapterFuncName};");
                sb.AppendLine("}");
            }
        }

        private static void AppendSingleAdapter(INamedTypeSymbol jobStruct, Compilation compilation,
            StringBuilder sb, List<string> discoveredEventTypes)
        {
            var baseFuncName = GetCppJobFunctionName(jobStruct);
            var adapterFuncName = baseFuncName + "_Adapter";
            // IJob（非 ParallelFor）：适配函数签名匹配 JobFunc(void* context)
            // 同样生成适配函数
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {adapterFuncName}(void* context)");
            sb.AppendLine("{");

            var fieldReads = new StringBuilder();
            var callArgs = new List<string>();
            int currentOffset = 0;

            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                int offset = CalculateFieldOffset(field, ref currentOffset);

                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                    {
                        fieldReads.AppendLine($"    auto* {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(field.Type)}>**)((char*)context + {offset});");
                        callArgs.Add($"{field.Name}_listData");
                    }
                    else
                    {
                        var cppElemType = GetCppElementType(field.Type);
                        fieldReads.AppendLine($"    auto* {field.Name}_ptr = *({cppElemType}**)((char*)context + {offset});");
                        fieldReads.AppendLine($"    int& {field.Name}_length = *(int*)((char*)context + {offset + 8});");
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
            sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{adapterFuncName}Ptr()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){adapterFuncName};");
            sb.AppendLine("}");

            // SendEvent: 事件类型查询导出函数
            if (discoveredEventTypes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"// Event Buffer metadata ({discoveredEventTypes.Count} types)");
                sb.AppendLine($"GENERATED_API int CALLINGCONVENTION Get_{baseFuncName}_EventBufferCount()");
                sb.AppendLine("{");
                sb.AppendLine($"    return {discoveredEventTypes.Count};");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"GENERATED_API int CALLINGCONVENTION Get_{baseFuncName}_EventBufferElementSize(int index)");
                sb.AppendLine("{");
                for (int ei = 0; ei < discoveredEventTypes.Count; ei++)
                {
                    string cppType = NativeTranspiler.MapCSharpTypeToCpp(
                        compilation.GetTypeByMetadataName(discoveredEventTypes[ei]));
                    sb.AppendLine($"    if (index == {ei}) return sizeof({cppType});");
                }
                sb.AppendLine("    return 0;");
                sb.AppendLine("}");
            }
        }
    }
}
