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
            if (ctorArgs.Length > 0 && ctorArgs[0].Value is int ctorInt)
                return (NativeTranspiler.BackendTarget)ctorInt;

            foreach (var namedArg in attrData.NamedArguments)
                if (namedArg.Key == "Target" && namedArg.Value.Value is int enumVal)
                    return (NativeTranspiler.BackendTarget)enumVal;

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
                if (namedArg.Key == "MathLib" && namedArg.Value.Value is int enumVal)
                    return (NativeTranspiler.IspcMathLib)enumVal;

            return NativeTranspiler.IspcMathLib.fast;
        }

        /// <summary>
        /// 读取 DisabledAutoRefresh 参数：
        /// 为 true 时跳过已存在文件的重新生成（用于增量缓存）
        /// </summary>
        public static bool GetDisabledAutoRefresh(ISymbol symbol, INamedTypeSymbol? attrSymbol)
        {
            if (attrSymbol == null) return false;
            return TryGetNamedArgument(symbol, attrSymbol, "DisabledAutoRefresh", false);
        }

        // ---------- 辅助方法 ----------

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
