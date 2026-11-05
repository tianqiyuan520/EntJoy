using System;

namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>所有 ISPC 入口共用的类型拼写；地址和 lane 限定由调用上下文决定。</summary>
    internal static class IspcTypeMapper
    {
        public static string PointerTo(string cppElement, bool uniformAddress)
        {
            string element = cppElement.TrimEnd();
            string target = element.EndsWith("*", StringComparison.Ordinal)
                ? PointerTo(element.Substring(0, element.Length - 1), true)
                : element == "void" ? "void" : "uniform " + FromCpp(element);
            return target + " * " + (uniformAddress ? "uniform" : "varying");
        }

        public static string FromCpp(string type)
        {
            if (type.EndsWith("*", StringComparison.Ordinal))
                return FromCpp(type.Substring(0, type.Length - 1).TrimEnd()) + "*";
            return type switch
            {
                "signed char" => "int8",
                "unsigned char" => "uint8",
                "short" => "int16",
                "unsigned short" => "uint16",
                "long long" => "int64",
                "unsigned long long" => "uint64",
                _ when type.Contains("::") => type.Substring(type.LastIndexOf("::", StringComparison.Ordinal) + 2),
                _ => type
            };
        }
    }
}
