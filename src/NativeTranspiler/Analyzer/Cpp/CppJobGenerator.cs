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
        // 批入口统一传 Job 地址及区间，声明、实现和适配器共用此签名。
        private const string BatchJobParameters = "void* __job, int __startIndex, int __count";

        /// <summary>
        /// 获取所有 bool 条件字段列表
        /// 与 CppGenerator.GenerateImplementation 对静态方法的处理对齐。
        /// </summary>
        private static List<IFieldSymbol> GetBoolConditionalFields(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            if (executeMethod == null) return new List<IFieldSymbol>();
            
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax == null) return new List<IFieldSymbol>();
            
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var conditionalFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel);
            return conditionalFields.Where(f => f.Type.SpecialType == SpecialType.System_Boolean).ToList();
        }

        /// <summary>
        /// 生成所有 bool 条件字段组合的变体函数声明
        /// </summary>
        private static void GenerateBoolVariantDeclarations(List<IFieldSymbol> boolFields, string baseFuncName, string batchParams, StringBuilder sb)
        {
            if (boolFields.Count == 0)
            {
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {baseFuncName}({batchParams});");
                return;
            }

            foreach (var (_, values) in BoolVariants.Enumerate(boolFields.Count))
            {
                
                string suffix = BoolVariants.Suffix(values);
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {baseFuncName}{suffix}({batchParams});");
            }
        }

        public static string GenerateJobHeader(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine();
            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            if (IsChunkScheduledJob(jobStruct))
            {
                sb.AppendLine("#include \"ChunkJobData.h\"");
                sb.AppendLine("#include \"ChunkNativeArray.h\"");
            }
            foreach (var include in CollectJobStructIncludes(jobStruct, compilation))
                sb.AppendLine($"#include \"{include}.h\"");
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateAtomicMacros());
            // SendEvent: __EntJoyChunkContextHeader 前向声明（指针参数）
            sb.AppendLine("#ifndef __EntJoyChunkContextHeader_FWD");
            sb.AppendLine("#define __EntJoyChunkContextHeader_FWD");
            sb.AppendLine("struct __EntJoyChunkContextHeader;");
            sb.AppendLine("#endif");
            sb.AppendLine();

            // IJobEntity: 无独立 Execute 函数，循环体内联到 Adapter 中
            if (IsChunkJob(jobStruct))
            {
                bool usesSendEvent = JobUsesSendEvent(jobStruct, compilation);
                var chunkParams = BuildChunkJobParameters(jobStruct, includeHeader: usesSendEvent);
                var singleFuncName = GetCppJobFunctionName(jobStruct);
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams});");
            }
            else if (IsEntityJob(jobStruct))
            {
                // IJobEntity：始终生成独立的 Chunk 级 Execute 函数声明（不含 __requiredComponentTypeIds）
                var chunkParams = BuildChunkJobParameters(jobStruct, includeTypeIds: false);
                var singleFuncName = GetCppJobFunctionName(jobStruct);
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams});");
            }
            else if (IsRangeScheduledJob(jobStruct))
            {
                var batchParams = BatchJobParameters;
                var baseFuncName = GetCppJobFunctionName(jobStruct, isBatch: true);
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);
                GenerateBoolVariantDeclarations(boolFields, baseFuncName, batchParams, sb);
            }
            else
            {
                var singleParams = BuildJobParameters(jobStruct);
                var singleFuncName = GetCppJobFunctionName(jobStruct);
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({singleParams});");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 收集 Job Execute 直接/间接调用的同程序集静态方法（用于生成 #include）。
        /// </summary>
        private static HashSet<IMethodSymbol> CollectAllCalledStaticMethods(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var result = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == Config.Execute);
            if (executeMethod == null) return result;

            var queue = new Queue<IMethodSymbol>();
            foreach (var m in CppGenerator.CollectCalledStaticMethods(executeMethod, compilation))
                queue.Enqueue(m);
            while (queue.Count > 0)
            {
                var m = queue.Dequeue();
                if (!result.Add(m)) continue;
                foreach (var d in CppGenerator.CollectCalledStaticMethods(m, compilation))
                    queue.Enqueue(d);
            }
            return result;
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
            // 依赖的静态方法头文件（Execute 内调用的同程序集静态方法，含传递依赖）
            // 缺失会导致调用点引用未声明的函数（use of undeclared identifier）。
            foreach (var dep in CollectAllCalledStaticMethods(jobStruct, compilation))
                sb.AppendLine($"#include \"{CppGenerator.GetCppFunctionName(dep)}.h\"");
            sb.AppendLine("#include <algorithm>");
            sb.AppendLine("#include <cmath>");
            sb.AppendLine("#include <cstdio>");
            // 逐 job 精度覆盖（C1）
            // CMake 的 NATIVE_SIMD_MATH_PRECISION 是全库一刀切；NativeSIMD_math.h 用
            //   3 = IEEE （逐通道标量，与 C# 逐位一致）
            sb.AppendLine($"#define SIMD_MATH_PRECISION {(int)simdMathPrecision + 1}");
            sb.AppendLine("#include \"NativeSIMD.h\"");
            sb.AppendLine("#include \"SimdValue.h\"");

            // Event Buffer POD struct（SendEvent 生成的代码依赖）
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

            // ChunkContextHeader 完整定义（SendEvent 需要解引用 __header->eventBufferHeaders）
            if (JobUsesSendEvent(jobStruct, compilation))
            {
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
            }

            // IJobChunk: 生成独立 Execute 函数
            if (IsChunkJob(jobStruct))
            {
                if (autoSIMD == NativeTranspiler.AutoSIMD.Vectorize)
                    GenerateChunkFunctionVectorize(jobStruct, compilation, sb, useFastMath);
                else if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                    GenerateChunkFunctionSIMD(jobStruct, compilation, sb, useFastMath, simdMathPrecision);
                else
                    GenerateChunkFunctionStandard(jobStruct, compilation, sb, useFastMath);
            }
            // IJobEntity：生成独立的 Chunk 级 Execute 函数（与 IJobChunk 一致）
            else if (IsEntityJob(jobStruct))
            {
                if (autoSIMD == NativeTranspiler.AutoSIMD.Vectorize)
                    GenerateEntityFunctionVectorize(jobStruct, compilation, sb, useFastMath);
                else if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                    GenerateEntityFunctionStandard(jobStruct, compilation, sb, useFastMath);
                else
                    GenerateEntityChunkFunctionStandard(jobStruct, compilation, sb, useFastMath);
            }
            else if (IsParallelForJob(jobStruct) || IsForJob(jobStruct) || IsParallelForBatchJob(jobStruct))
            {
                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
                var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
                if (methodSyntax == null)
                {
                    sb.AppendLine("// Error: Could not find method syntax");
                    return sb.ToString();
                }

                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);
                // IJobParallelForBatch：Execute(startIndex, count) 自身就是一段区间，C++ 侧不再包 index 循环。
                bool isRangeJob = IsParallelForBatchJob(jobStruct);

                if (boolFields.Count > 0)
                {
                    // 生成所有 2^n 个 bool 组合变体
                    foreach (var (_, values) in BoolVariants.Enumerate(boolFields.Count))
                    {
                        GenerateBatchFunction(jobStruct, semanticModel, methodSyntax, sb, useFastMath, autoSIMD, simdMathPrecision, isRangeJob, boolFields, values);
                    }
                }
                else
                {
                    GenerateBatchFunction(jobStruct, semanticModel, methodSyntax, sb, useFastMath, autoSIMD, simdMathPrecision, isRangeJob);
                }
            }
            else
            {
                GenerateSingleFunctionStandard(jobStruct, compilation, sb, useFastMath, autoSIMD, simdMathPrecision);
            }

            return sb.ToString();
        }
    }
}
