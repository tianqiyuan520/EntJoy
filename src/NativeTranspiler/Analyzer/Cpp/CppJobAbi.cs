using Microsoft.CodeAnalysis;
using System;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// C# struct → C++/ISPC ABI 计算（字段偏移/大小/对齐）。
    /// 与原生侧 NativeDll 的字段布局（Sequential，64 位 Windows）逐字节对应，
    /// 原生侧有 static_assert 兜底。拆分自 CppJobGenerator，行为完全不变。
    /// </summary>
    public static partial class CppJobGenerator
    {
        /// <summary>
        /// 计算 C# struct 中字段的偏移量（基于 [StructLayout(LayoutKind.Sequential)]，64位 Windows）
        /// </summary>
        internal static int CalculateFieldOffset(IFieldSymbol field, ref int currentOffset)
        {
            int size = GetCSharpFieldSize(field.Type);
            int alignment = GetCSharpFieldAlignment(field.Type);
            
            // 对齐到 alignment 的倍数
            currentOffset = (currentOffset + alignment - 1) / alignment * alignment;
            int result = currentOffset;
            currentOffset += size;
            return result;
        }

        /// <summary>
        /// 获取 C# 类型在 Sequential 布局下的大小（64位）
        /// </summary>
        internal static int GetCSharpFieldSize(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol) return 8;

            // 引用类型字段（DisposeSentinel / string / class）= 64 位指针，占 8 字节。
            // 这使 #if DEBUG 下的 sentinel 被正确计入 Debug 布局（40B），Release 无 sentinel 为 32B。
            if (type.IsReferenceType) return 8;

            // 枚举：大小等于其底层类型（默认 int→4）。
            if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
                return GetCSharpFieldSize(enumType.EnumUnderlyingType);

            // 检查是否为 EntJoy.Mathematics 向量类型
            var ns = type.ContainingNamespace?.ToDisplayString();
            if (ns == Config.NamespaceEntJoyMathematics)
            {
                return type.Name switch
                {
                    "float2" => 8,
                    "int2" => 8,
                    "uint2" => 8,
                    _ => 8
                };
            }

            return type.SpecialType switch
            {
                SpecialType.System_Int32 => 4,
                SpecialType.System_UInt32 => 4,
                SpecialType.System_Int64 => 8,
                SpecialType.System_UInt64 => 8,
                SpecialType.System_Single => 4,
                SpecialType.System_Double => 8,
                SpecialType.System_Boolean => 1,
                // 容器（NativeArray/NativeList/UnsafeList）不再硬编码，改为按真实字段布局递归推导，
                // 与运行时编译配置自动保持一致（Unity/Burst 的做法）：
                //   Release（无 sentinel）：NativeArray=32，NativeList=24，UnsafeList=20
                //   Debug  （#if DEBUG sentinel 存在）：NativeArray=40，NativeList=32，UnsafeList=20
                _ => type is INamedTypeSymbol namedType && namedType.IsValueType
                    ? GetStructSizeRecursive(namedType) : 4 // 默认
            };
        }

        /// <summary>
        /// 递归计算 struct 类型的大小（按 Sequential 布局）
        /// </summary>
        internal static int GetStructSizeRecursive(INamedTypeSymbol structType)
        {
            int maxAlignment = 1;
            int offset = 0;
            foreach (var member in structType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                int fieldSize = GetCSharpFieldSize(member.Type);
                int fieldAlignment = GetCSharpFieldAlignment(member.Type);
                if (fieldAlignment > maxAlignment) maxAlignment = fieldAlignment;
                // 对齐
                offset = (offset + fieldAlignment - 1) / fieldAlignment * fieldAlignment;
                offset += fieldSize;
            }
            // 最终对齐到结构体自身对齐要求
            offset = (offset + maxAlignment - 1) / maxAlignment * maxAlignment;
            return Math.Max(1, offset); // C# struct 最小为 1
        }

        /// <summary>
        /// 获取 C# 类型在 Sequential 布局下的对齐要求（64位）
        /// </summary>
        internal static int GetCSharpFieldAlignment(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol) return 8;
            if (type.IsReferenceType) return 8;

            // 枚举：对齐等于其底层类型（默认 int→4）。
            if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
                return GetCSharpFieldAlignment(enumType.EnumUnderlyingType);

            var ns = type.ContainingNamespace?.ToDisplayString();
            if (ns == Config.NamespaceEntJoyMathematics)
            {
                return type.Name switch
                {
                    "float2" => 4,
                    "int2" => 4,
                    "uint2" => 4,
                    _ => 4
                };
            }

            return type.SpecialType switch
            {
                SpecialType.System_Int32 => 4,
                SpecialType.System_UInt32 => 4,
                SpecialType.System_Int64 => 8,
                SpecialType.System_UInt64 => 8,
                SpecialType.System_Single => 4,
                SpecialType.System_Double => 8,
                SpecialType.System_Boolean => 1,
                // 容器对齐同样按真实字段布局递归推导（NativeArray/NativeList/UnsafeList 首字段均为指针 → 8）。
                _ => type is INamedTypeSymbol namedType && namedType.IsValueType
                    ? GetStructAlignmentRecursive(namedType) : 4
            };
        }

        /// <summary>
        /// 递归计算 struct 类型的对齐要求（字段对齐的 max）
        /// </summary>
        internal static int GetStructAlignmentRecursive(INamedTypeSymbol structType)
        {
            int maxAlign = 1;
            foreach (var member in structType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                int align = GetCSharpFieldAlignment(member.Type);
                if (align > maxAlign) maxAlign = align;
            }
            return maxAlign;
        }

        /// <summary>
        /// 获取 bool 条件字段在 job struct 中的偏移量
        /// </summary>
        private static int GetBoolFieldOffset(INamedTypeSymbol jobStruct, string boolFieldName)
        {
            int currentOffset = 0;
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                int offset = CalculateFieldOffset(field, ref currentOffset);
                if (field.Name == boolFieldName)
                    return offset;
            }
            return -1;
        }
    }
}
