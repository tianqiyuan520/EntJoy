namespace NativeTranspiler.Analyzer
{
    /// <summary>EntJoy 数学 2 分量向量的 C++ 类型名判定（精确匹配，不做子串猜测）。</summary>
    internal static class SimdVectorTypes
    {
        public const string Float2 = "EntJoy::Mathematics::float2";
        public const string Int2 = "EntJoy::Mathematics::int2";
        public const string Uint2 = "EntJoy::Mathematics::uint2";

        public static bool IsFloat2(string cppType) => cppType == Float2;
        public static bool IsInt2(string cppType) => cppType == Int2;
        public static bool IsUint2(string cppType) => cppType == Uint2;
        public static bool IsPair(string cppType) => cppType == Float2 || cppType == Int2 || cppType == Uint2;
    }
}
