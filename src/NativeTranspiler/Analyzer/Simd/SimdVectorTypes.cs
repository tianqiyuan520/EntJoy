using System;

namespace NativeTranspiler.Analyzer
{
    /// <summary>EntJoy 数学 2 分量向量的 C++ 类型名判定（精确匹配，不做子串猜测）。</summary>
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
