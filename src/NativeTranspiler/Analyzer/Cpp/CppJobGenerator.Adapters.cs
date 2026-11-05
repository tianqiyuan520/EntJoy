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


        /// <summary>
        /// 生成适配函数代码（C++），用于消除 C# 委托桥接。
        /// 适配函数签名匹配 BatchJobFunc(void* context, int startIndex, int count)，
        /// 内部从 context 中按偏移量读取字段，调用实际的 Batch 函数。
        /// </summary>
        public static (string code, List<string> eventTypes) GenerateJobAdapter(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            var discoveredEventTypes = new List<string>();
            var baseFuncName = GetCppJobFunctionName(jobStruct);
            var adapterFuncName = baseFuncName + "_Adapter";

            // 检查是否为 ISPC job 和 auto-SIMD（include 部分需要 autoSIMD 判断 SimdValue.h）
            var attrSymbol = AttributeHelper.GetAttributeSymbol(compilation);
            var autoSIMD = attrSymbol != null
                ? AttributeHelper.GetAutoSIMD(jobStruct, attrSymbol)
                : NativeTranspiler.AutoSIMD.Disabled;
            bool isIspcJob = attrSymbol != null &&
                AttributeHelper.GetBackendTarget(jobStruct, attrSymbol) == NativeTranspiler.BackendTarget.Ispc;

            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine("#include <cstring>");
            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                sb.AppendLine("#include \"SimdValue.h\"");
            if (IsChunkScheduledJob(jobStruct))
            {
                sb.AppendLine("#include \"ChunkJobData.h\"");
                sb.AppendLine("#include \"EntityBatchData.h\"");
            }
            // 字段里 NativeArray<用户结构体> 的元素类型头文件：adapter/wrapper 的形参列表
            // 直接写 CPUBattle::OrcaLine* 这类限定名，缺头文件就会 use of undeclared identifier。
            foreach (var include in CollectJobStructIncludes(jobStruct, compilation))
                sb.AppendLine($"#include \"{include}.h\"");
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();

            if (isIspcJob)
            {
                // ISPC job: 声明 wrapper 函数为 extern（在 wrapper.cpp 中实现）
                bool isp_batch = IsParallelForJob(jobStruct) || IsForJob(jobStruct);
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);

                if (isp_batch)
                {
                    var batchFuncName = GetCppJobFunctionName(jobStruct, isBatch: true);
                    var batchParams = BatchJobParameters;
                    sb.AppendLine($"// ISPC wrapper function (defined in wrapper.cpp)");
                    GenerateBoolVariantDeclarations(boolFields, batchFuncName, batchParams, sb);
                }
                else
                {
                    // IJob: non-batch wrapper
                    var singleFuncName = GetCppJobFunctionName(jobStruct, isBatch: false);
                    var singleParams = BuildJobParameters(jobStruct);
                    sb.AppendLine($"// ISPC wrapper function (defined in wrapper.cpp)");
                    sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({singleParams});");
                }
                sb.AppendLine();
            }
            else
            {
                // C++ job: 包含 header 文件
                sb.AppendLine($"#include \"{baseFuncName}.h\"");
                sb.AppendLine();
            }

            if (IsChunkScheduledJob(jobStruct))
                AppendChunkAdapters(jobStruct, compilation, sb, discoveredEventTypes);
            else if (IsRangeScheduledJob(jobStruct))
                AppendRangeAdapter(jobStruct, compilation, sb);
            else
                AppendSingleAdapter(jobStruct, compilation, sb, discoveredEventTypes);

            return (sb.ToString(), discoveredEventTypes);
        }
    }
}
