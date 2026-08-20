using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// CppJobGenerator 的纯命名/类型判断部分：导出函数名、适配器名、Job 接口判别、
    /// bool 变体后缀、C++ 元素类型名。拆分自 CppJobGenerator，行为完全不变。
    /// </summary>
    public static partial class CppJobGenerator
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
            jobStruct.AllInterfaces.Any(i => SymbolHelper.IsEntJoyJobInterface(i, Config.IJobParallelFor));
        public static bool IsForJob(INamedTypeSymbol jobStruct) =>
            jobStruct.AllInterfaces.Any(i => SymbolHelper.IsEntJoyJobInterface(i, Config.IJobFor));
        public static bool IsIJob(INamedTypeSymbol jobStruct) =>
            jobStruct.AllInterfaces.Any(i => SymbolHelper.IsEntJoyJobInterface(i, Config.IJob));
        public static bool IsChunkJob(INamedTypeSymbol jobStruct) =>
            jobStruct.AllInterfaces.Any(i => SymbolHelper.IsEntJoyJobInterface(i, Config.IJobChunk));
        public static bool IsEntityJob(INamedTypeSymbol jobStruct) =>
            jobStruct.AllInterfaces.Any(i => SymbolHelper.IsEntJoyJobInterface(i, Config.IJobEntity));
        public static bool IsChunkScheduledJob(INamedTypeSymbol jobStruct) =>
            IsChunkJob(jobStruct) || IsEntityJob(jobStruct);

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
