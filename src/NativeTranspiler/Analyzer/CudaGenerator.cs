using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// CUDA 后端生成器：C# Job（IJobParallelFor / IJobFor / IJob）→ .cu 内核源（nvcc 预编译 cubin，驱动 API 加载）。
    /// 复用 CppBatchStatementTranslator 翻译 Execute body（CUDA C 与 C++ 语句语法一致），
    /// 后处理差异点：EntJoy::Mathematics::float2/int2 → CUDA 内建（.x/.y 字段 vs 基类输出的 .x()/.y() 方法）。
    /// </summary>
    public static class CudaGenerator
    {
        /// <summary>CUDA kernel 函数名（= C++ Job 函数名，保证唯一）</summary>
        public static string GetCudaKernelName(INamedTypeSymbol jobStruct)
            => CppJobGenerator.GetCppJobFunctionName(jobStruct, isBatch: false);

        /// <summary>生成完整 .cu 源（含 kernel 包装；nvcc 编译为 cubin 后由 GpuComputeCuda_* 加载）</summary>
        public static string GenerateCudaSource(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax == null) return "// Error: Could not find method syntax";
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            bool indexed = CppJobGenerator.IsParallelForJob(jobStruct) || CppJobGenerator.IsForJob(jobStruct);
            string indexParam = indexed ? methodSyntax.ParameterList.Parameters[0].Identifier.Text : null;

            string body;
            if (indexed)
            {
                var translator = new CppBatchStatementTranslator(semanticModel, jobStruct, indexParam, indexParam,
                    /* useFastMath */ false, /* scalar body */ false);
                body = translator.Translate(methodSyntax.Body);
            }
            else
            {
                var translator = new CppPointerStatementTranslator(semanticModel, jobStruct);
                body = translator.Translate(methodSyntax.Body);
            }
            // C++ 语法 → CUDA：类型内建 + 字段访问（.x() 方法 → .x 字段）+ 容器参数名（_ptr 后缀 → 裸名）
            body = body
                .Replace("EntJoy::Mathematics::float2", "float2")
                .Replace("EntJoy::Mathematics::int2", "int2")
                .Replace("EntJoy::Mathematics::uint2", "uint2")
                .Replace("EntJoy::Mathematics::float3", "float3")
                .Replace("EntJoy::Mathematics::float4", "float4")
                .Replace(".x()", ".x")
                .Replace(".y()", ".y")
                .Replace(".z()", ".z")
                .Replace(".w()", ".w");
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                    body = body.Replace($"{field.Name}_ptr", field.Name);

            var sb = new StringBuilder();
            sb.AppendLine("// Auto-generated CUDA kernel (BackendTarget.Cuda) - do not edit.");
            sb.AppendLine("#include <vector_types.h>");
            sb.AppendLine("#include <device_atomic_functions.h>");
            sb.AppendLine("#ifndef RESTRICT");
            sb.AppendLine("#ifdef _MSC_VER");
            sb.AppendLine("#define RESTRICT __restrict");
            sb.AppendLine("#else");
            sb.AppendLine("#define RESTRICT restrict");
            sb.AppendLine("#endif");
            sb.AppendLine("#endif");
            sb.AppendLine();
            sb.AppendLine($"extern \"C\" __global__ void {GetCudaKernelName(jobStruct)}({BuildKernelParams(jobStruct)} int count)");
            sb.AppendLine("{");
            if (indexed)
            {
                sb.AppendLine("    int i = blockIdx.x * blockDim.x + threadIdx.x;");
                sb.AppendLine("    if (i >= count) return;");
            }
            sb.Append(body);
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>kernel 参数：NativeArray → 指针（RESTRICT）；标量 → 值参（CUDA 内核直接传值）</summary>
        private static string BuildKernelParams(INamedTypeSymbol jobStruct)
        {
            var sb = new StringBuilder();
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                string cpp = NativeTranspiler.MapCSharpTypeToCpp(
                    NativeTranspiler.IsEntJoyNativeContainerType(field.Type)
                        ? ((INamedTypeSymbol)field.Type).TypeArguments[0]
                        : field.Type);
                cpp = CudaType(cpp);
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                    sb.Append($"{cpp}* RESTRICT {field.Name}, ");
                else
                    sb.Append($"{cpp} {field.Name}, ");
            }
            return sb.ToString();
        }

        /// <summary>C++ 类型名 → CUDA 内建（EntJoy::Mathematics::float2 → float2 等）</summary>
        internal static string CudaType(string cppType) => cppType
            .Replace("EntJoy::Mathematics::float2", "float2")
            .Replace("EntJoy::Mathematics::int2", "int2")
            .Replace("EntJoy::Mathematics::uint2", "uint2")
            .Replace("EntJoy::Mathematics::float3", "float3")
            .Replace("EntJoy::Mathematics::float4", "float4");
    }
}
