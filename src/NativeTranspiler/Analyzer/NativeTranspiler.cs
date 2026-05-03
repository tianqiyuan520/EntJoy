using Microsoft.CodeAnalysis;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    public static partial class NativeTranspiler
    {
        /// <summary>
        /// 后端目标类型枚举。
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

        public static string MapCSharpTypeToCpp(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol pointerType)
                return MapCSharpTypeToCpp(pointerType.PointedAtType) + "*";

            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                var fullName = named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (fullName == "EntJoy.Collections.NativeArray<T>")
                {
                    var elem = named.TypeArguments[0];
                    return $"EntJoy::Collections::NativeArray<{MapCSharpTypeToCpp(elem)}>";
                }
                if (fullName == "EntJoy.Collections.NativeList<T>")
                {
                    var elem = named.TypeArguments[0];
                    return $"EntJoy::Collections::UnsafeList<{MapCSharpTypeToCpp(elem)}>";
                }
                if (fullName == "EntJoy.Collections.UnsafeList<T>")
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

            return type.SpecialType switch
            {
                SpecialType.System_Int32 => "int",
                SpecialType.System_UInt32 => "unsigned int",
                SpecialType.System_Int64 => "long long",
                SpecialType.System_UInt64 => "unsigned long long",
                SpecialType.System_Single => "float",
                SpecialType.System_Double => "double",
                SpecialType.System_Boolean => "bool",
                SpecialType.System_Void => "void",
                _ => type.Name
            };
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
            return type.SpecialType != SpecialType.None ||
                   type.ToDisplayString().StartsWith("EntJoy.Mathematics.");
        }

        public static string MapCSharpTypeToIspc(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol pointerType)
                return MapCSharpTypeToIspc(pointerType.PointedAtType) + " *";

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
                .Where(f => !f.IsStatic)
                .OrderBy(f => f.MetadataToken))
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
            sb.AppendLine("#include \"../../NativeDll/NativeContainers.h\"");
            sb.AppendLine("#include \"../../NativeDll/NativeMath.h\"");
            sb.AppendLine();
            var ns = structSymbol.ContainingNamespace?.ToDisplayString() ?? "";
            bool hasNs = !string.IsNullOrEmpty(ns) && ns != "<global namespace>";
            if (hasNs)
                sb.AppendLine($"namespace {ns} {{");
            sb.AppendLine($"struct {structSymbol.Name} {{");
            foreach (var f in structSymbol.GetMembers().OfType<IFieldSymbol>()
                .Where(f => !f.IsStatic)
                .OrderBy(f => f.MetadataToken))
            {
                string cppType = MapCSharpTypeToCpp(f.Type);
                sb.AppendLine($"    {cppType} {f.Name};");
            }
            sb.AppendLine("};");
            if (hasNs)
                sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
