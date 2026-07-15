using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    public static class SymbolHelper
    {
        // C++/ISPC 保留关键字，用作标识符时会编译错误
        private static readonly HashSet<string> CppKeywords = new()
        {
            "alignas", "alignof", "and", "and_eq", "asm", "auto", "bitand", "bitor",
            "bool", "break", "case", "catch", "char", "class", "compl", "const",
            "constexpr", "const_cast", "continue", "decltype", "default", "delete",
            "do", "double", "dynamic_cast", "else", "enum", "explicit", "export",
            "extern", "false", "float", "for", "friend", "goto", "if", "inline",
            "int", "long", "mutable", "namespace", "new", "noexcept", "not", "not_eq",
            "nullptr", "operator", "or", "or_eq", "override", "private", "protected",
            "public", "register", "reinterpret_cast", "return", "short", "signed",
            "sizeof", "static", "static_cast", "struct", "switch", "template", "this",
            "throw", "true", "try", "typedef", "typeid", "typename", "union",
            "unsigned", "using", "virtual", "void", "volatile", "wchar_t", "while",
            "xor", "xor_eq", "int8_t", "int16_t", "int32_t", "int64_t",
            "uint8_t", "uint16_t", "uint32_t", "uint64_t", "size_t", "ptrdiff_t"
        };

        public static MethodDeclarationSyntax? GetMethodSyntax(IMethodSymbol methodSymbol)
        {
            var syntaxRef = methodSymbol.DeclaringSyntaxReferences.FirstOrDefault();
            return syntaxRef?.GetSyntax() as MethodDeclarationSyntax;
        }

        public static BlockSyntax? GetMethodBody(IMethodSymbol methodSymbol)
        {
            var methodSyntax = GetMethodSyntax(methodSymbol);
            return methodSyntax?.Body;
        }

        public static string BuildFullTypePath(INamedTypeSymbol? typeSymbol)
        {
            if (typeSymbol == null) return "";
            var parts = new Stack<string>();
            var current = typeSymbol;
            while (current != null)
            {
                parts.Push(current.Name);
                current = current.ContainingType;
            }
            return string.Join(".", parts);
        }

        /// <summary>
        /// 将 C# 标识符转为安全的 C++/ISPC 标识符。
        /// 处理：首数字前缀 _、C++ 关键字后缀 _、Unicode 转 ASCII、空输入
        /// </summary>
        public static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "_empty";

            var sb = new StringBuilder(input.Length);
            foreach (char ch in input)
            {
                // 仅保留 ASCII 字母数字和下划线
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_')
                    sb.Append(ch);
                else
                    sb.Append('_');
            }

            string result = sb.ToString();

            // 首字符不能是数字
            if (result.Length > 0 && result[0] >= '0' && result[0] <= '9')
                result = "_" + result;

            // 不能是 C++ 保留关键字
            if (result.Length > 0 && CppKeywords.Contains(result))
                result = result + "_";

            // 不能是空标识符（全部被替换为 _ 的情况）
            if (result.All(c => c == '_'))
                result = result + "_id";

            return result;
        }
    }
}