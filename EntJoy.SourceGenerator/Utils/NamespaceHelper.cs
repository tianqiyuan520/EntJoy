using Microsoft.CodeAnalysis;

namespace EntJoy.SourceGenerator.Utils
{
    /// <summary>
    /// 命名空间生成器
    /// </summary>
    internal static class NamespaceHelper
    {
        public static string GetNamespacePath(INamespaceSymbol symbol)
        {
            // 根命名空间 || 全局命名空间
            if (symbol == null || symbol.IsGlobalNamespace)
            {
                return string.Empty;
            }
            // 非根命名空间
            string parentNamespace = GetNamespacePath(symbol.ContainingNamespace);
            string currentNamespace = symbol.Name;
            if(!string.IsNullOrEmpty(parentNamespace)) {
                return parentNamespace + "." + currentNamespace;
            }
            return currentNamespace;
        }
    }
}
