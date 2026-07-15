// ============================================================
// AttributeHelper.cs — 共享属性解析
//   统一解析 [NativeTranspile] 特性的所有命名参数，
//   消除 NativeTranspilerGenerator.cs 和 BindingsGenerator.cs 中的重复代码。
// ============================================================
using Microsoft.CodeAnalysis;
using System.Linq;

namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>
    /// 提供 [NativeTranspile] 特性的共享解析方法。
    /// 所有生成器统一通过此类读取特性参数，避免重复解析逻辑。
    /// </summary>
    public static class AttributeHelper
    {
        private const string AttributeName = "NativeTranspileAttribute";
        private const string AttributeNamespace = "NativeTranspiler";

        /// <summary>
        /// 获取 [NativeTranspile] 特性的元数据符号。
        /// 用于在多个文件中复用同一个符号比对。
        /// </summary>
        public static INamedTypeSymbol? GetAttributeSymbol(Compilation compilation)
            => compilation.GetTypeByMetadataName($"{AttributeNamespace}.{AttributeName}");

        /// <summary>
        /// 读取 Target 参数：指定后端类型 Cpp 或 Ispc
        /// </summary>
        public static NativeTranspiler.BackendTarget GetBackendTarget(ISymbol symbol, INamedTypeSymbol? attrSymbol)
        {
            if (attrSymbol == null) return NativeTranspiler.BackendTarget.Cpp;
            var attrData = symbol.GetAttributes().FirstOrDefault(ad =>
                SymbolEqualityComparer.Default.Equals(ad.AttributeClass, attrSymbol));
            if (attrData == null) return NativeTranspiler.BackendTarget.Cpp;

            var ctorArgs = attrData.ConstructorArguments;
            if (ctorArgs.Length > 0)
            {
                int? val = ConvertEnumArgToInt(ctorArgs[0].Value);
                if (val.HasValue)
                    return (NativeTranspiler.BackendTarget)val.Value;
            }

            foreach (var namedArg in attrData.NamedArguments)
            {
                if (namedArg.Key == "Target")
                {
                    int? val = ConvertEnumArgToInt(namedArg.Value.Value);
                    if (val.HasValue)
                        return (NativeTranspiler.BackendTarget)val.Value;
                }
            }

            return NativeTranspiler.BackendTarget.Cpp;
        }

        /// <summary>
        /// 读取 UseISPC_MT 参数：是否启用 ISPC 多任务变体
        /// </summary>
        public static bool HasUseISPC_MT(ISymbol symbol, INamedTypeSymbol? attrSymbol)
        {
            if (attrSymbol == null) return false;
            return TryGetNamedArgument(symbol, attrSymbol, "UseISPC_MT", false);
        }

        /// <summary>
        /// 读取 MathLib 参数：ISPC 数学库类型
        /// </summary>
        public static NativeTranspiler.IspcMathLib GetMathLib(ISymbol symbol, INamedTypeSymbol? attrSymbol)
        {
            if (attrSymbol == null) return NativeTranspiler.IspcMathLib.fast;
            var attrData = symbol.GetAttributes().FirstOrDefault(ad =>
                SymbolEqualityComparer.Default.Equals(ad.AttributeClass, attrSymbol));
            if (attrData == null) return NativeTranspiler.IspcMathLib.fast;

            foreach (var namedArg in attrData.NamedArguments)
                if (namedArg.Key == "MathLib")
                {
                    int? val = ConvertEnumArgToInt(namedArg.Value.Value);
                    if (val.HasValue) return (NativeTranspiler.IspcMathLib)val.Value;
                }

            return NativeTranspiler.IspcMathLib.fast;
        }

        public static NativeTranspiler.CppMathLib GetCppMathLib(ISymbol symbol, INamedTypeSymbol? attrSymbol)
        {
            if (attrSymbol == null) return NativeTranspiler.CppMathLib.@default;
            var attrData = symbol.GetAttributes().FirstOrDefault(ad =>
                SymbolEqualityComparer.Default.Equals(ad.AttributeClass, attrSymbol));
            if (attrData == null) return NativeTranspiler.CppMathLib.@default;

            foreach (var namedArg in attrData.NamedArguments)
                if (namedArg.Key == "CppMathLib")
                {
                    int? val = ConvertEnumArgToInt(namedArg.Value.Value);
                    if (val.HasValue) return (NativeTranspiler.CppMathLib)val.Value;
                }

            return NativeTranspiler.CppMathLib.@default;
        }

        public static bool HasFastCppMathLib(ISymbol symbol, INamedTypeSymbol? attrSymbol)
        {
            return GetCppMathLib(symbol, attrSymbol) == NativeTranspiler.CppMathLib.fast;
        }

        /// <summary>
        /// 读取 DisableAutoRefresh 参数：
        /// 为 true 时跳过已存在文件的重新生成（用于增量缓存）
        /// </summary>
        public static bool GetDisableAutoRefresh(ISymbol symbol, INamedTypeSymbol? attrSymbol)
        {
            if (attrSymbol == null) return false;
            return TryGetNamedArgument(symbol, attrSymbol, "DisableAutoRefresh", false);
        }

        // ---------- 辅助方法 ----------

        /// <summary>
        /// 将枚举构造参数的值转为 int，处理 byte/int/long 等不同底层类型。
        /// </summary>
        private static int? ConvertEnumArgToInt(object? value)
        {
            if (value == null) return null;
            // enum 的底层类型可能是 int/byte/short/long，Roslyn box 为运行时对应类型
            if (value is int i) return i;
            if (value is byte b) return b;
            if (value is sbyte sb) return sb;
            if (value is short s) return s;
            if (value is ushort us) return us;
            if (value is uint ui) return (int)ui;
            if (value is long l) return (int)l;
            if (value is ulong ul) return (int)ul;
            return null;
        }

        private static T TryGetNamedArgument<T>(ISymbol symbol, INamedTypeSymbol attrSymbol, string name, T defaultValue)
        {
            var attrData = symbol.GetAttributes().FirstOrDefault(ad =>
                SymbolEqualityComparer.Default.Equals(ad.AttributeClass, attrSymbol));
            if (attrData == null) return defaultValue;

            foreach (var namedArg in attrData.NamedArguments)
                if (namedArg.Key == name && namedArg.Value.Value is T val)
                    return val;

            return defaultValue;
        }
    }
}
