// WGSL 类型映射辅助（WgslGenerator / WgslStatementTranslator 共享）
using Microsoft.CodeAnalysis;
using System;

namespace NativeTranspiler.Analyzer
{
    internal static class WgslTypes
    {
        /// <summary>C# 类型 → WGSL 类型；不支持返回 null。</summary>
        public static string ToWgslType(ITypeSymbol? type)
        {
            if (type == null) return null!;
            if (type is IPointerTypeSymbol) return null!;
            if (NativeTranspiler.IsEntJoyNativeContainerType(type)) return null!;

            var ns = type.ContainingNamespace?.ToDisplayString();
            if (ns == "EntJoy.Mathematics")
            {
                return type.Name switch
                {
                    "float2" => "vec2f",
                    "int2" => "vec2i",
                    "uint2" => "vec2u",
                    "float3" => "vec3f",
                    "float4" => "vec4f",
                    _ => null!
                };
            }
            if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol et)
                return ToWgslType(et.EnumUnderlyingType);

            return type.SpecialType switch
            {
                SpecialType.System_Single => "f32",
                SpecialType.System_Int32 => "i32",
                SpecialType.System_UInt32 => "u32",
                SpecialType.System_Boolean => "bool",
                SpecialType.System_Double => "f64", // 字段级由校验器拒绝；局部可用（需 f64 扩展）
                _ => null!
            };
        }

        public static bool IsMathVectorType(ITypeSymbol? type)
            => type != null && type.ContainingNamespace?.ToDisplayString() == "EntJoy.Mathematics";

        /// <summary>向量类型的标量分量（vec2f→f32）</summary>
        public static string ToWgslScalarOfVector(string wgslVec) => wgslVec switch
        {
            "vec2f" => "f32",
            "vec2i" => "i32",
            "vec2u" => "u32",
            "vec3f" => "f32",
            "vec4f" => "f32",
            _ => wgslVec
        };

        /// <summary>WGSL 标量类型类别（用于提升判断）</summary>
        public static bool IsFloatScalar(ITypeSymbol? t)
            => t != null && t.SpecialType is SpecialType.System_Single or SpecialType.System_Double;
        public static bool IsIntScalar(ITypeSymbol? t)
            => t != null && t.SpecialType is SpecialType.System_Int32 or SpecialType.System_UInt32;

        /// <summary>剥离 float 字面量后缀（WGSL 无 f 后缀）</summary>
        public static string ToWgslFloatLiteral(string text)
        {
            string n = text.Substring(0, text.Length - 1);
            if (!n.Contains(".") && !n.Contains("e") && !n.Contains("E"))
                return n + ".0";
            return n;
        }

        /// <summary>表达式类型对应的 WGSL 标量名（用于提升判断）</summary>
        public static string ScalarKindOf(ITypeSymbol? t)
        {
            string w = ToWgslType(t);
            return w ?? "";
        }
    }
}
