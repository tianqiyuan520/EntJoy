// ISPC 生成器共用的类型收集、include 闭包与源代码格式。
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
    /// <summary>
    /// ISPC 代码统一生成器（static partial）。
    /// 方法入口（静态方法 IMethodSymbol）见 IspcGenerator.Method.cs，
    /// 本文件保留共享辅助 + Job 入口（INamedTypeSymbol）。
    /// </summary>
    public static partial class IspcGenerator
    {
        private const string Indent = "    ";

        // 共享内部类

        private class TypeSymbolComparer : IEqualityComparer<ITypeSymbol>
        {
            public bool Equals(ITypeSymbol x, ITypeSymbol y) => SymbolEqualityComparer.Default.Equals(x, y);
            public int GetHashCode(ITypeSymbol obj) => SymbolEqualityComparer.Default.GetHashCode(obj);
        }

        private static bool IsVectorType(string cppType) =>
            cppType == "EntJoy::Mathematics::float2" ||
            cppType == "EntJoy::Mathematics::int2" ||
            cppType == "EntJoy::Mathematics::uint2";

        /// <summary>用户自定义 struct（非内置、非 EntJoy 预定义容器）。</summary>
        private static bool IsUserStructType(ITypeSymbol? type) =>
            type is { TypeKind: TypeKind.Struct } &&
            !NativeTranspiler.IsBuiltinUnmanaged(type) &&
            !NativeTranspiler.IsEntJoyPredefinedType(type);

        // 共享 ISPC 结构体生成

        /// <summary>
        /// 生成 UnsafeList_Context 结构体定义 + include 头文件。
        /// </summary>
        private static void WriteIspcPreamble(StringBuilder sb,
            IEnumerable<(ITypeSymbol type, string name)> fields,
            List<string> extraIncludes)
        {
            sb.AppendLine("#include \"EntJoyCommon.ispc\"");
            foreach (var include in extraIncludes)
                sb.AppendLine($"#include \"{include}.ispc\"");
            sb.AppendLine();

            foreach (var (type, _) in fields)
            {
                if (!NativeTranspiler.IsEntJoyNativeContainerType(type) || type.Name != Config.NativeList)
                    continue;
                var elemType = ((INamedTypeSymbol)type).TypeArguments[0];
                var ispcElem = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                sb.AppendLine($"struct UnsafeList_Context_{ispcElem} {{");
                sb.AppendLine($"{Indent}void* uniform _data;");
                sb.AppendLine($"{Indent}uniform int _length;");
                sb.AppendLine($"{Indent}uniform int _capacity;");
                sb.AppendLine($"{Indent}uniform int _allocator;");
                sb.AppendLine($"{Indent}void (* uniform ResizeFunc)(void* uniform * uniform _data, uniform int * uniform _length, uniform int * uniform _capacity, uniform int * uniform _allocator, uniform int newSize, uniform bool clear);");
                sb.AppendLine("};");
                sb.AppendLine();
            }
        }

        // 字段提取 & include 收集（共享）

        private static List<(ITypeSymbol type, string name)> GetFieldsFromMethod(IMethodSymbol method)
            => method.Parameters.Select(p => (p.Type, p.Name)).ToList();

        private static List<(ITypeSymbol type, string name)> GetFieldsFromJob(INamedTypeSymbol jobStruct)
            => jobStruct.GetMembers().OfType<IFieldSymbol>()
                .Where(f => !f.IsStatic)
                .Select(f => (f.Type, f.Name))
                .ToList();

        private static HashSet<string> CollectIncludesFromFields(
            IEnumerable<(ITypeSymbol type, string name)> fields)
        {
            var includes = new HashSet<string>();
            foreach (var (type, _) in fields)
                CollectTypeInclude(type, includes);
            return includes;
        }

        private static void CollectTypeInclude(ITypeSymbol type, HashSet<string> includes)
        {
            if (type is IPointerTypeSymbol pointer)
            {
                CollectTypeInclude(pointer.PointedAtType, includes);
                return;
            }
            if (type is IPointerTypeSymbol ptr)
            {
                CollectTypeInclude(ptr.PointedAtType, includes);
                return;
            }
            // NativeArray<T>/NativeList<T> 自身是 EntJoy 预定义类型（无头文件），
            // 但元素类型 T 是用户结构体，必须递归收集，否则 ISPC 侧 T 未声明。
            if (NativeTranspiler.IsEntJoyNativeContainerType(type))
            {
                foreach (var arg in ((INamedTypeSymbol)type).TypeArguments)
                    CollectTypeInclude(arg, includes);
                return;
            }
            if (NativeTranspiler.IsEntJoyPredefinedType(type)) return;
            if (type.IsValueType && !NativeTranspiler.IsBuiltinUnmanaged(type))
                includes.Add(NativeTranspiler.GetStructHeaderFileName((INamedTypeSymbol)type));
        }

        private static void AppendIspcBody(StringBuilder output, string code, int depth = 1)
        {
            using var reader = new StringReader(code);
            string padding = new string(' ', depth * Indent.Length);
            string? line;
            while ((line = reader.ReadLine()) != null)
                if (line.Length > 0) output.Append(padding).AppendLine(line);
        }

        private static HashSet<string> CollectMethodIncludes(IMethodSymbol method, Compilation compilation,
            IEnumerable<(ITypeSymbol type, string name)> fields)
        {
            var includes = CollectIncludesFromFields(fields);
            CollectTypeInclude(method.ReturnType, includes);
            foreach (var parameter in method.Parameters)
                if (!NativeTranspiler.IsEntityType(parameter.Type)
                    && parameter.Type.ToDisplayString() != Config.TypeArchetypeChunk
                    && parameter.Type.ToDisplayString() != "EntJoy.ECS.ChunkEnabledMask")
                    CollectTypeInclude(parameter.Type, includes);
            var syntax = SymbolHelper.GetMethodSyntax(method);
            if (syntax?.Body != null)
            {
                var model = compilation.GetSemanticModel(syntax.SyntaxTree);
                foreach (var local in syntax.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                    if (model.GetTypeInfo(local.Declaration.Type).Type is ITypeSymbol type) CollectTypeInclude(type, includes);
            }
            foreach (var helper in CollectIspcHelperClosure(method, compilation)) includes.Add(GetIspcHelperFileName(helper));
            return includes;
        }
    }
}
