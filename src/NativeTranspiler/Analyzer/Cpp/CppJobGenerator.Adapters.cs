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
        /// 只返回函数体：调用方把它接在同文件的实现之后，内联时才能看到被调函数的定义。
        /// 宿主负责给出这些依赖：NativeGeneratedSupport.h（导出/调用约定宏）、NativeContainers.h、NativeMath.h，
        /// chunk 入口另需 ChunkJobData.h / EntityBatchData.h 与 <cstring>。
        /// </summary>
        public static (string code, List<string> eventTypes) GenerateJobAdapter(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            var discoveredEventTypes = new List<string>();
            sb.AppendLine();

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
