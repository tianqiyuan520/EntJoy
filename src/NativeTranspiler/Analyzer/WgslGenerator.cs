// ============================================================
// WgslGenerator.cs — 把 [NativeTranspile(Target = BackendTarget.Gpu)] Job
//   翻译为 WGSL compute shader（.wgsl 文件）。
//   支持 IJobParallelFor / IJobFor（global_invocation_id 索引）、IJob（单次）。
//   契约（供运行时实现对照）：
//     - group(0) binding 0..n-1：NativeArray 字段 → var<storage, read_write> array<T>
//     - group(0) binding n：标量字段 → var<uniform> jobParams : {Job}_Params
//     - jobParams.__count = 调度长度（i32），内核入口按它做越界保护
//     - struct 布局按 C# Sequential 推导（@align/@size 强制对齐），与 NativeArray 内存一致
// ============================================================
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    public static class WgslGenerator
    {
        public const int WorkgroupSize = 64;

        /// <summary>WGSL 文件名基名（与 C++ Job 函数名一致，保证唯一）</summary>
        public static string GetWgslBaseName(INamedTypeSymbol jobStruct)
            => CppJobGenerator.GetCppJobFunctionName(jobStruct, isBatch: false);

        /// <summary>是否为 GPU 支持的 Job 形态（IJobParallelFor / IJobFor / IJob）</summary>
        public static bool IsSupportedGpuJob(INamedTypeSymbol jobStruct)
            => CppJobGenerator.IsParallelForJob(jobStruct) ||
               CppJobGenerator.IsForJob(jobStruct) ||
               CppJobGenerator.IsIJob(jobStruct);

        /// <summary>生成 WGSL 源；不支持时返回错误注释（校验器先行拦截）</summary>
        public static string GenerateWgslSource(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// Auto-generated WGSL for {jobStruct.Name} (Target = BackendTarget.Gpu)");
            sb.AppendLine($"// 由 NativeTranspiler WgslGenerator 生成，勿手改。");

            bool isIndexed = CppJobGenerator.IsParallelForJob(jobStruct) || CppJobGenerator.IsForJob(jobStruct);
            string indexParamName = "";
            if (isIndexed)
            {
                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
                if (executeMethod != null && executeMethod.Parameters.Length > 0)
                    indexParamName = executeMethod.Parameters[0].Name;
            }

            var fields = jobStruct.GetMembers().OfType<IFieldSymbol>()
                .Where(f => !f.IsStatic)
                .ToList();

            var arrayFields = new List<IFieldSymbol>();
            var scalarFields = new List<IFieldSymbol>();
            foreach (var f in fields)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type))
                    arrayFields.Add(f);
                else
                    scalarFields.Add(f);
            }

            // 收集数组元素中的用户 struct，递归生成 struct 声明
            var userStructs = new List<INamedTypeSymbol>();
            foreach (var f in arrayFields)
            {
                var elem = ((INamedTypeSymbol)f.Type).TypeArguments[0];
                CollectUserStructs(elem, userStructs);
            }
            foreach (var s in userStructs)
            {
                sb.AppendLine(GenerateStructDefinition(s));
            }
            if (userStructs.Count > 0) sb.AppendLine();

            // 标量参数 struct（uniform buffer）
            if (scalarFields.Count > 0 || isIndexed)
            {
                sb.AppendLine($"struct {jobStruct.Name}_Params {{");
                foreach (var f in scalarFields)
                    sb.AppendLine($"    {f.Name} : {ToWgslScalarField(f.Type)},");
                if (isIndexed)
                    sb.AppendLine("    _count : i32,");
                sb.AppendLine("};");
                sb.AppendLine();
            }

            // storage buffers（binding 0..n-1）
            int binding = 0;
            foreach (var f in arrayFields)
            {
                string elemWgsl = ToWgslArrayElementType(((INamedTypeSymbol)f.Type).TypeArguments[0]);
                sb.AppendLine($"@group(0) @binding({binding}) var<storage, read_write> {f.Name} : array<{elemWgsl}>;");
                binding++;
            }
            // uniform buffer（binding n）
            if (scalarFields.Count > 0 || isIndexed)
            {
                sb.AppendLine($"@group(0) @binding({binding}) var<uniform> jobParams : {jobStruct.Name}_Params;");
                binding++;
            }
            sb.AppendLine();

            // compute 入口
            var executeMethod2 = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
            var methodSyntax = executeMethod2 != null ? SymbolHelper.GetMethodSyntax(executeMethod2) : null;
            if (methodSyntax?.Body == null)
            {
                sb.AppendLine("// Error: no Execute body found.");
                return sb.ToString();
            }
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            sb.AppendLine($"@compute @workgroup_size({WorkgroupSize})");
            sb.AppendLine("fn main(@builtin(global_invocation_id) gid : vec3<u32>) {");
            if (isIndexed)
            {
                sb.AppendLine("    let i : i32 = i32(gid.x);");
                sb.AppendLine("    if (i >= i32(jobParams._count)) { return; }");
            }

            var translator = new WgslStatementTranslator(semanticModel, jobStruct, indexParamName);
            translator.SetEntryIndent();
            string body = translator.Translate(methodSyntax.Body);
            sb.Append(body);
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ---------- 类型映射 ----------

        private static string ToWgslScalarField(ITypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol et)
                return ToWgslScalarField(et.EnumUnderlyingType);
            string w = WgslTypes.ToWgslType(type);
            if (w == "bool") return "i32"; // uniform buffer 不允许 bool（运行时按 0/1 打包）
            return w ?? "i32";
        }

        private static string ToWgslArrayElementType(ITypeSymbol elem)
        {
            string w = WgslTypes.ToWgslType(elem);
            if (w != null) return w;
            if (elem.IsValueType && !NativeTranspiler.IsBuiltinUnmanaged(elem))
                return elem.Name;
            return "f32";
        }

        // ---------- 用户 struct 声明（按 C# Sequential 布局，@align/@size 强制对齐） ----------

        private static void CollectUserStructs(ITypeSymbol type, List<INamedTypeSymbol> collected)
        {
            if (type is IPointerTypeSymbol ptr) { CollectUserStructs(ptr.PointedAtType, collected); return; }
            if (NativeTranspiler.IsBuiltinUnmanaged(type) || NativeTranspiler.IsEntJoyPredefinedType(type)) return;
            if (type is INamedTypeSymbol named && named.IsValueType)
            {
                if (collected.Any(s => SymbolEqualityComparer.Default.Equals(s, named))) return;
                collected.Add(named);
                foreach (var f in named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    CollectUserStructs(f.Type, collected);
            }
        }

        private static string GenerateStructDefinition(INamedTypeSymbol s)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"struct {s.Name} {{");
            int offset = 0;
            foreach (var f in s.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                int csharpSize = CppJobGenerator.GetCSharpFieldSize(f.Type);
                int csharpAlign = CppJobGenerator.GetCSharpFieldAlignment(f.Type);
                offset = (offset + csharpAlign - 1) / csharpAlign * csharpAlign;

                string memberWgsl = WgslTypes.ToWgslType(f.Type) ?? ToWgslArrayElementType(f.Type);
                int natAlign = WgslNaturalAlign(f.Type, memberWgsl);
                int natSize = WgslNaturalSize(f.Type, memberWgsl);

                var hints = new List<string>();
                if (csharpAlign != natAlign) hints.Add($"@align({csharpAlign})");
                if (csharpSize != natSize) hints.Add($"@size({csharpSize})");
                string hintStr = hints.Count > 0 ? string.Join(" ", hints) + " " : "";
                sb.AppendLine($"    {hintStr}{memberWgsl} {f.Name},");
                offset += csharpSize;
            }
            sb.AppendLine("};");
            return sb.ToString();
        }

        private static int WgslNaturalAlign(ITypeSymbol type, string wgslType)
        {
            if (WgslTypes.IsMathVectorType(type))
            {
                return wgslType is "vec3f" or "vec4f" ? 16 : 8;
            }
            if (type.IsValueType && !NativeTranspiler.IsBuiltinUnmanaged(type) && !NativeTranspiler.IsEntJoyPredefinedType(type))
            {
                int maxAlign = 1;
                foreach (var f in ((INamedTypeSymbol)type).GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    string mw = WgslTypes.ToWgslType(f.Type) ?? ToWgslArrayElementType(f.Type);
                    int a = WgslNaturalAlign(f.Type, mw);
                    if (a > maxAlign) maxAlign = a;
                }
                return maxAlign;
            }
            return wgslType switch
            {
                "f32" or "i32" or "u32" => 4,
                "vec2f" or "vec2i" or "vec2u" => 8,
                "vec3f" => 16,
                "vec4f" => 16,
                _ => 4
            };
        }

        private static int WgslNaturalSize(ITypeSymbol type, string wgslType)
        {
            if (WgslTypes.IsMathVectorType(type))
            {
                return wgslType switch
                {
                    "vec2f" or "vec2i" or "vec2u" => 8,
                    "vec3f" => 12,
                    "vec4f" => 16,
                    _ => 8
                };
            }
            if (type.IsValueType && !NativeTranspiler.IsBuiltinUnmanaged(type) && !NativeTranspiler.IsEntJoyPredefinedType(type))
            {
                int maxAlign = 1, size = 0;
                foreach (var f in ((INamedTypeSymbol)type).GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    string mw = WgslTypes.ToWgslType(f.Type) ?? ToWgslArrayElementType(f.Type);
                    int a = WgslNaturalAlign(f.Type, mw);
                    int sz = WgslNaturalSize(f.Type, mw);
                    if (a > maxAlign) maxAlign = a;
                    size = (size + a - 1) / a * a + sz;
                }
                return (size + maxAlign - 1) / maxAlign * maxAlign;
            }
            return wgslType switch
            {
                "f32" or "i32" or "u32" => 4,
                "vec2f" or "vec2i" or "vec2u" => 8,
                "vec3f" => 12,
                "vec4f" => 16,
                _ => 4
            };
        }
    }
}
