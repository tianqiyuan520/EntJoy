using Microsoft.CodeAnalysis;
using System;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    public static partial class NativeTranspiler
    {
        /// <summary>
        /// 后端目标类型枚举。（GPU/WGSL/CUDA 后端已拆分至 feature/gpu-offload 分支）
        /// </summary>
        public enum BackendTarget
        {
            Cpp,
            Ispc
        }

        /// <summary>
        /// ISPC 数学库类型枚举。
        /// </summary>
        public enum IspcMathLib
        {
            system,
            fast,
            @default
        }

        /// <summary>
        /// C++ 数学编译模式枚举。
        /// </summary>
        public enum CppMathLib
        {
            @default,
            fast
        }

        /// <summary>
        /// SIMD 数学函数精度等级（用于 SLEEF 向量数学库）。
        /// Fastest = ~3.5 ULP, High = ~1.0 ULP, IEEE = 标量精确。
        /// </summary>
        public enum SimdMathPrecision
        {
            Fastest,
            High,
            IEEE
        }

        /// <summary>
        /// 自动 SIMD 向量化开关。
        /// </summary>
        public enum AutoSIMD
        {
            Enabled,
            Disabled,
            Vectorize
        }

        public static string MapCSharpTypeToCpp(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol pointerType)
                return MapCSharpTypeToCpp(pointerType.PointedAtType) + "*";

            // 引用字段（object/string/class）无法跨语言：C++ 侧映射为 void* 8B 槽位（零填充，Execute 不应访问）
            if (type.IsReferenceType) return "void*";

            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                var fullName = named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (fullName == "EntJoy.Collections.NativeArray<T>" || fullName == "global::EntJoy.Collections.NativeArray<T>")
                {
                    var elem = named.TypeArguments[0];
                    return $"EntJoy::Collections::NativeArray<{MapCSharpTypeToCpp(elem)}>";
                }
                if (fullName == "EntJoy.Collections.NativeList<T>" || fullName == "global::EntJoy.Collections.NativeList<T>")
                {
                    var elem = named.TypeArguments[0];
                    return $"EntJoy::Collections::UnsafeList<{MapCSharpTypeToCpp(elem)}>";
                }
                if (fullName == "EntJoy.Collections.UnsafeList<T>" || fullName == "global::EntJoy.Collections.UnsafeList<T>")
                {
                    var elem = named.TypeArguments[0];
                    return $"EntJoy::Collections::UnsafeList<{MapCSharpTypeToCpp(elem)}>";
                }
            }

            var ns = GetNamespace(type);
            if (ns == "EntJoy.Mathematics")
            {
                return type.Name switch
                {
                    "float2" => "EntJoy::Mathematics::float2",
                    "int2" => "EntJoy::Mathematics::int2",
                    "uint2" => "EntJoy::Mathematics::uint2",
                    _ => $"EntJoy::Mathematics::{type.Name}"
                };
            }

            var mapped = type.SpecialType switch
            {
                SpecialType.System_Int32 => "int",
                SpecialType.System_UInt32 => "unsigned int",
                SpecialType.System_Int64 => "long long",
                SpecialType.System_UInt64 => "unsigned long long",
                SpecialType.System_Single => "float",
                SpecialType.System_Double => "double",
                SpecialType.System_Boolean => "bool",
                SpecialType.System_Void => "void",
                SpecialType.System_Byte => "unsigned char",
                SpecialType.System_SByte => "signed char",
                SpecialType.System_Int16 => "short",
                SpecialType.System_UInt16 => "unsigned short",
                SpecialType.System_Char => "unsigned short",  // C# char=16bit, C++ char=8bit
                SpecialType.System_IntPtr => "intptr_t",
                SpecialType.System_UIntPtr => "uintptr_t",
                _ => null
            };
            if (mapped != null) return mapped;

            return string.IsNullOrEmpty(ns) ? type.Name : $"{ns.Replace(".", "::")}::{type.Name}";
        }

        private static string GetNamespace(ITypeSymbol type)
        {
            var ns = type.ContainingNamespace;
            if (ns == null || ns.IsGlobalNamespace) return "";
            return ns.ToDisplayString();
        }

        public static bool IsEntJoyNativeContainerType(ITypeSymbol? type)
        {
            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                var ns = named.ContainingNamespace?.ToDisplayString();
                if (ns == "EntJoy.Collections")
                {
                    var typeName = named.Name;
                    if (typeName == "NativeArray" || typeName == "NativeList" || typeName == "UnsafeList")
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 所有在 EntJoy.Collections 命名空间下的类型（无论是否泛型）都视为"预定义"，不应生成头文件。
        /// </summary>
        public static bool IsEntJoyPredefinedType(ITypeSymbol type)
        {
            var ns = type.ContainingNamespace?.ToDisplayString();
            return ns == "EntJoy.Collections";
        }

        /// <summary>
        /// 获取用户结构体对应的 C++ 头文件名（不含 .h 扩展名）。
        /// 对于嵌套类型，会包含外部类型名称，例如 NativeColletionStructTest_Particle。
        /// </summary>
        public static string GetStructHeaderFileName(INamedTypeSymbol structSymbol)
        {
            var containingNamespace = structSymbol.ContainingNamespace?.ToDisplayString() ?? "";
            var typePath = SymbolHelper.BuildFullTypePath(structSymbol);
            var safeNamespace = SymbolHelper.Sanitize(containingNamespace);
            var safeTypePath = SymbolHelper.Sanitize(typePath);
            return $"{safeNamespace}_{safeTypePath}";
        }

        /// <summary>判断是否为内置非托管类型（基础类型或数学类型）</summary>
        public static bool IsBuiltinUnmanaged(ITypeSymbol type)
        {
            // 仅限值类型；string/object/array 等的 SpecialType != None 但非 unmanaged
            return (type.IsValueType && type.SpecialType != SpecialType.None) ||
                   type.ToDisplayString().StartsWith("EntJoy.Mathematics.");
        }

        public static string MapCSharpTypeToIspc(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol pointerType)
                return MapCSharpTypeToIspc(pointerType.PointedAtType) + " *";

            // 引用字段（object/string/class）→ ISPC void* 8B 槽位（零填充，不应访问）
            if (type.IsReferenceType) return "void*";

            if (type is INamedTypeSymbol named && named.IsGenericType)
                return type.Name; // ISPC 无泛型，不应出现

            var ns = GetNamespace(type);
            if (ns == "EntJoy.Mathematics")
                return type.Name; // float2, int2, uint2

            return type.SpecialType switch
            {
                SpecialType.System_Int32 => "int",
                SpecialType.System_UInt32 => "unsigned int",
                SpecialType.System_Single => "float",
                SpecialType.System_Double => "double",
                SpecialType.System_Boolean => "bool",
                SpecialType.System_Void => "void",
                _ => type.Name
            };
        }

        public static string GenerateIspcStructDefinition(INamedTypeSymbol structSymbol)
        {
            var sb = new StringBuilder();
            // 前置声明（用于自引用指针）
            sb.AppendLine($"struct {structSymbol.Name};");
            sb.AppendLine($"struct {structSymbol.Name} {{");
            foreach (var f in structSymbol.GetMembers().OfType<IFieldSymbol>()
                .Where(f => !f.IsStatic))
            {
                string ispcType = MapCSharpTypeToIspc(f.Type);
                sb.AppendLine($"    {ispcType} {f.Name};");
            }
            sb.AppendLine("};");
            return sb.ToString();
        }

        public static string GenerateCppStructDefinition(INamedTypeSymbol structSymbol)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine();
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include <cstddef>");
            sb.AppendLine();

            // 自动计算结构体总大小并生成 static_assert 验证，
            // 无需用户手动添加 [StructLayout(LayoutKind.Sequential)]
            int totalSize = ComputeStructSize(structSymbol);

            var ns = structSymbol.ContainingNamespace?.ToDisplayString() ?? "";
            bool hasNs = !string.IsNullOrEmpty(ns) && ns != "<global namespace>";
            if (hasNs)
                sb.AppendLine($"namespace {ns.Replace(".", "::")} {{");
            sb.AppendLine($"struct {structSymbol.Name} {{");
            foreach (var f in structSymbol.GetMembers().OfType<IFieldSymbol>()
                .Where(f => !f.IsStatic))
            {
                string cppType = MapCSharpTypeToCpp(f.Type);
                sb.AppendLine($"    {cppType} {f.Name};");
            }
            sb.AppendLine("};");
            // static_assert 确保 C++ 布局与 C# 计算大小一致
            sb.AppendLine($"static_assert(sizeof({structSymbol.Name}) == {totalSize}, \"Size mismatch for {structSymbol.Name}: check struct layout\");");
            if (hasNs)
                sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// 计算 C# struct 在 Sequential 布局下的总大小（64位）。
        /// 用于生成的 C++ static_assert 校验，无需用户手动加 [StructLayout]。
        /// 尺寸/对齐统一委托给 CppJobGenerator 的递归布局推导（单一事实来源），
        /// 消除第二份硬编码容器尺寸表：容器字段按真实字段布局推导，
        /// Release 无 sentinel = 32/24/20，Debug 带 #if DEBUG sentinel = 40/32/20。
        /// </summary>
        private static int ComputeStructSize(INamedTypeSymbol structType)
            => CppJobGenerator.GetStructSizeRecursive(structType);
    }
}
