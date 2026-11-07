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

        // 参数列表构建（共享）

        /// <summary>
        /// 为 ISPC 函数构建参数列表。
        /// 支持两种调用者：静态方法 (IMethodSymbol) 和 Job 结构体 (INamedTypeSymbol)。
        /// </summary>
        private static string BuildIspcParamList(
            IEnumerable<(ITypeSymbol type, string name)> fields,
            bool includeStartCount,
            string? resultType = null)
        {
            var pars = new StringBuilder();
            if (includeStartCount)
                pars.Append("uniform int __startIndex, uniform int __count");

            foreach (var (type, name) in fields)
            {
                if (pars.Length > 0) pars.Append(",\n    ");
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
                    pars.Append($"{IspcTypeMapper.PointerTo(baseCpp, true)} {name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                    pars.Append($"uniform {IspcTypeMapper.FromCpp(cppType)} * uniform {name}_ptr");
                }
            }
            if (resultType != null)
            {
                if (pars.Length > 0) pars.Append(",\n    ");
                pars.Append($"uniform {resultType} * uniform __result_ptr");
            }
            return pars.ToString();
        }

        private static string BuildIspcCallArgs(IEnumerable<(ITypeSymbol type, string name)> fields, bool includeStartCount)
        {
            var args = new StringBuilder();
            if (includeStartCount)
                args.Append("__startIndex, __count");
            bool first = !includeStartCount;
            foreach (var (type, name) in fields)
            {
                if (!first) args.Append(", ");
                first = false;
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                    args.Append(NativeTranspiler.IsEntJoyContainerNamed(type, Config.NativeList) ? name : $"{name}_ptr, {name}_length");
                else
                    args.Append($"{name}_ptr");
            }
            return args.ToString();
        }

        /// <summary>
        /// 构建 C++ Wrapper 的参数列表（供 wrapper.cpp 使用）。
        /// </summary>
        private static string BuildCppWrapperParamList(
            IEnumerable<(ITypeSymbol type, string name)> fields,
            bool includeStartCount)
        {
            var pars = new StringBuilder();
            if (includeStartCount)
                pars.Append("int __startIndex, int __count");

            foreach (var (type, name) in fields)
            {
                if (pars.Length > 0) pars.Append(", ");
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(type, Config.NativeList))
                    {
                        var elemType = NativeTranspiler.GetContainerElementType(type);
                        var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                        pars.Append($"EntJoy::Collections::UnsafeList<{cppElem}>* {name}_listData");
                    }
                    else
                    {
                        var elemType = NativeTranspiler.GetContainerElementType(type);
                        var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                        string qualifier = NativeTranspiler.IsReadOnlyNativeArray(type) ? "const " : "";
                        pars.Append($"{qualifier}{cppElem}* {name}_ptr, int {name}_length");
                    }
                }
                else if (type is IPointerTypeSymbol)
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                    pars.Append($"{cppType} {name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                    pars.Append($"{cppType}* {name}_ptr");
                }
            }
            return pars.ToString();
        }

        private static string BuildIspcCallArgsForWrapper(
            IEnumerable<(ITypeSymbol type, string name)> fields,
            bool includeStartCount)
        {
            var args = new StringBuilder();
            if (includeStartCount)
                args.Append("__startIndex, __count");
            bool first = !includeStartCount;
            foreach (var (type, name) in fields)
            {
                if (!first) args.Append(", ");
                first = false;
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(type, Config.NativeList))
                        args.Append($"&{name}_ctx");
                    else
                    {
                        var elemType = NativeTranspiler.GetContainerElementType(type);
                        var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                        if (IsVectorType(cppElem))
                        {
                            var ispcType = IspcTypeMapper.FromCpp(cppElem);
                            args.Append($"reinterpret_cast<{(NativeTranspiler.IsReadOnlyNativeArray(type) ? "const " : "")}ispc::{ispcType}*>({name}_ptr), {name}_length");
                        }
                        else if (IsUserStructType(elemType))
                            args.Append($"reinterpret_cast<{(NativeTranspiler.IsReadOnlyNativeArray(type) ? "const " : "")}ispc::{elemType.Name}*>({name}_ptr), {name}_length");
                        else
                            args.Append($"{name}_ptr, {name}_length");
                    }
                }
                else if (type is IPointerTypeSymbol ptrType)
                {
                    // 用户 struct 在 ISPC 侧是 ispc::T（与 C++ 的 Namespace::T 同名不同类型），必须 reinterpret_cast
                    args.Append(IsUserStructType(ptrType.PointedAtType)
                        ? $"reinterpret_cast<ispc::{ptrType.PointedAtType.Name}*>({name}_ptr)"
                        : $"{name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                    if (IsVectorType(cppType))
                    {
                        var ispcType = IspcTypeMapper.FromCpp(cppType);
                        args.Append($"reinterpret_cast<ispc::{ispcType}*>({name}_ptr)");
                    }
                    else if (IsUserStructType(type))
                        args.Append($"reinterpret_cast<ispc::{type.Name}*>({name}_ptr)");
                    else
                        args.Append($"{name}_ptr");
                }
            }
            return args.ToString();
        }

        // 共享 C++ Resize 回调生成

        private static void GenerateResizeCallbacks(StringBuilder sb,
            IEnumerable<(ITypeSymbol type, string name)> fields)
        {
            var types = fields
                .Where(f => NativeTranspiler.IsEntJoyContainerNamed(f.type, Config.NativeList))
                .Select(f => ((INamedTypeSymbol)f.type).TypeArguments[0])
                .Distinct(new TypeSymbolComparer());

            foreach (var elemType in types)
            {
                var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                var ispcElem = IspcTypeMapper.FromCpp(cppElem);
                sb.AppendLine($"static void UnsafeList_Resize_{ispcElem}_callback(void** data, int* length, int* capacity, int* allocator, int newSize, bool clear) {{");
                sb.AppendLine($"    using Alloc = EntJoy::Collections::Allocator;");
                sb.AppendLine($"    EntJoy::Collections::UnsafeList<{cppElem}> tmp;");
                sb.AppendLine($"    tmp.Ptr = static_cast<{cppElem}*>(*data);");
                sb.AppendLine($"    tmp.Length = *length;");
                sb.AppendLine($"    tmp.Capacity = *capacity;");
                sb.AppendLine($"    tmp.Allocator = static_cast<Alloc>(*allocator);");
                sb.AppendLine($"    EntJoy::Collections::NativeArrayOptions opts = clear ? EntJoy::Collections::NativeArrayOptions::ClearMemory : EntJoy::Collections::NativeArrayOptions::UninitializedMemory;");
                sb.AppendLine($"    tmp.Resize(newSize, opts);");
                sb.AppendLine($"    *data = tmp.Ptr;");
                sb.AppendLine($"    *length = tmp.Length;");
                sb.AppendLine($"    *capacity = tmp.Capacity;");
                sb.AppendLine($"    *allocator = static_cast<int>(tmp.Allocator);");
                sb.AppendLine("}");
            }
        }

        /// <summary>
        /// 生成 C++ wrapper 中 NativeList 上下文的填充/更新代码。
        /// </summary>
        private static string GenerateContextFillCode(
            IEnumerable<(ITypeSymbol type, string name)> fields,
            bool isFill) // true = fill, false = update
        {
            var sb = new StringBuilder();
            foreach (var (fieldType, name) in fields)
            {
                if (!NativeTranspiler.IsEntJoyNativeContainerType(fieldType) || fieldType.Name != Config.NativeList)
                    continue;
                var elemType = ((INamedTypeSymbol)fieldType).TypeArguments[0];
                var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                var ispcElem = IspcTypeMapper.FromCpp(cppElem);

                if (isFill)
                {
                    sb.AppendLine($"    ispc::UnsafeList_Context_{ispcElem} {name}_ctx;");
                    sb.AppendLine($"    {name}_ctx._data = {name}_listData->Ptr;");
                    sb.AppendLine($"    {name}_ctx._length = {name}_listData->Length;");
                    sb.AppendLine($"    {name}_ctx._capacity = {name}_listData->Capacity;");
                    sb.AppendLine($"    {name}_ctx._allocator = static_cast<int>({name}_listData->Allocator);");
                    sb.AppendLine($"    {name}_ctx.ResizeFunc = UnsafeList_Resize_{ispcElem}_callback;");
                }
                else
                {
                    sb.AppendLine($"    {name}_listData->Length = {name}_ctx._length;");
                    sb.AppendLine($"    {name}_listData->Capacity = {name}_ctx._capacity;");
                    sb.AppendLine($"    {name}_listData->Ptr = static_cast<{cppElem}*>({name}_ctx._data);");
                    sb.AppendLine($"    {name}_listData->Allocator = static_cast<EntJoy::Collections::Allocator>({name}_ctx._allocator);");
                }
            }
            return sb.ToString();
        }

    }
}
