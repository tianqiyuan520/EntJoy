namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>
    /// 运行时 API 面（从 NativeTranspilerGenerator 拆出）：
    /// 源生成器注入到用户编译单元中的运行时 Attribute / 枚举定义。
    /// 与代码生成编排、文件 I/O 彻底解耦。
    /// </summary>
    internal static class RuntimeApi
    {
        public const string AttributeName = "NativeTranspile";
        public const string AttributeNamespace = "NativeTranspiler";

        /// <summary>生成注入用户程序的 [NativeTranspile] Attribute 及配套枚举源码。</summary>
        public static string GenerateAttributeSource() => $@"
using System;
namespace {AttributeNamespace}
{{
    public enum BackendTarget
    {{
        Cpp,
        Ispc
    }}

    public enum IspcMathLib
    {{
        system,
        fast,
        @default
    }}

    public enum CppMathLib
    {{
        @default,
        fast
    }}

    public enum AutoSIMD
    {{
        Enabled,
        Disabled,
        Vectorize
    }}

    public enum SimdMathPrecision
    {{
        Fastest,
        High,
        IEEE
    }}

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Struct)]
    public sealed class {AttributeName}Attribute : Attribute
    {{
        public BackendTarget Target {{ get; set; }} = BackendTarget.Cpp;
        public bool DisabledAutoRefresh {{ get; set; }} = false;
        public bool UseISPC_MT {{ get; set; }} = false;
        public IspcMathLib MathLib {{ get; set; }} = IspcMathLib.fast;
        public CppMathLib CppMathLib {{ get; set; }} = CppMathLib.@default;
        public AutoSIMD AutoSIMD {{ get; set; }} = AutoSIMD.Disabled;
        public SimdMathPrecision MathPrecision {{ get; set; }} = SimdMathPrecision.Fastest;
    }}
}}
";
    }
}
