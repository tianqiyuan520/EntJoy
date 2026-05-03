using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer
{
    public static class SymbolHelper
    {
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

        public static string Sanitize(string input) =>
            new string(input.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
    }
}