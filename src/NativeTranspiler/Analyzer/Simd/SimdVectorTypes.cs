using System;

namespace NativeTranspiler.Analyzer
{
    /// <summary>EntJoy 数学 2 分量向量的 C++ 类型名判定（精确匹配，不做子串猜测）。</summary>
    internal enum SimdElemClass
    {
        /// <summary>32 位标量：一条 lane 就是一个元素，可直接向量 load/store。</summary>
        Lane32,
        /// <summary>8/16 位整数：需要扩宽 load、截断 pack store。</summary>
        NarrowInt,
        /// <summary>float2/int2/uint2：两分量成对，按 pair 专用路径处理。</summary>
        Pair,
        /// <summary>用户结构体等：逐 lane 标量访问。</summary>
        Other
    }

    internal static class SimdVectorTypes
    {
        public const string Float2 = "EntJoy::Mathematics::float2";
        public const string Int2 = "EntJoy::Mathematics::int2";
        public const string Uint2 = "EntJoy::Mathematics::uint2";

        /// <summary>只读访问器会给元素类型加 `const ` 前缀。</summary>
        private static string StripConst(string cppType)
            => cppType.StartsWith("const ", StringComparison.Ordinal) ? cppType.Substring(6) : cppType;

        public static bool IsFloat2(string cppType) => StripConst(cppType) == Float2;
        public static bool IsInt2(string cppType) => StripConst(cppType) == Int2;
        public static bool IsUint2(string cppType) => StripConst(cppType) == Uint2;
        public static bool IsPair(string cppType) => IsFloat2(cppType) || IsInt2(cppType) || IsUint2(cppType);

        /// <summary>元素类型的访存类别（决定连续 load/store、扩宽、逐 lane 三条路径）。</summary>
        public static SimdElemClass ClassifyElem(string cppType)
        {
            string t = StripConst(cppType);
            if (t is "float" or "int" or "unsigned int") return SimdElemClass.Lane32;
            if (t is "unsigned char" or "signed char" or "unsigned short" or "short") return SimdElemClass.NarrowInt;
            if (IsPair(cppType)) return SimdElemClass.Pair;
            return SimdElemClass.Other;
        }

        public static bool IsLane32(string cppType) => ClassifyElem(cppType) == SimdElemClass.Lane32;
        public static bool IsNarrowInt(string cppType) => ClassifyElem(cppType) == SimdElemClass.NarrowInt;

        /// <summary>窄整数元素的扩宽 load 函数名；非窄整数返回 null。</summary>
        public static string NarrowLoadFn(string cppType) => StripConst(cppType) switch
        {
            "unsigned char" => "n_load_widen_u8",
            "signed char" => "n_load_widen_s8",
            "unsigned short" => "n_load_widen_u16",
            "short" => "n_load_widen_s16",
            _ => null
        };

        /// <summary>窄整数元素的截断 pack store 函数名；非窄整数返回 null。</summary>
        public static string NarrowStoreFn(string cppType) => StripConst(cppType) switch
        {
            "unsigned char" => "n_store_pack_u8",
            "signed char" => "n_store_pack_s8",
            "unsigned short" => "n_store_pack_u16",
            "short" => "n_store_pack_s16",
            _ => null
        };

        /// <summary>窄整数 cast 的向量截断函数名（把值留在 int32 lane 内）；非窄整数返回 null。</summary>
        public static string NarrowTruncFn(string cppType) => StripConst(cppType) switch
        {
            "unsigned char" => "simd_trunc_u8",
            "signed char" => "simd_trunc_s8",
            "unsigned short" => "simd_trunc_u16",
            "short" => "simd_trunc_s16",
            _ => null
        };

        /// <summary>类型名去掉命名空间后的叶子名（`…::float2` → `float2`）。</summary>
        public static string LeafName(string cppType)
        {
            string name = StripConst(cppType);
            int sep = name.LastIndexOf("::", StringComparison.Ordinal);
            return sep >= 0 ? name.Substring(sep + 2) : name;
        }

        /// <summary>叶子名是否 2 分量向量（调用方已把全限定名剥成叶子名）。</summary>
        public static bool IsPairLeaf(string leaf) => leaf is "float2" or "int2" or "uint2";
    }
}
