using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NativeTranspiler.Analyzer.Common;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer
{
    public static partial class CppJobGenerator
    {
        // 只读句柄的指针可在批入口取值；元素访问不改变句柄，长度仍独立绑定。
        private static HashSet<IFieldSymbol>? FindStableArrayFields(
            INamedTypeSymbol jobStruct, SemanticModel model, MethodDeclarationSyntax method)
        {
            if (method.Body == null) return null;
            var nodes = method.Body.DescendantNodes().ToArray();
            if (HasIndirectJobFieldWrites(jobStruct, model, nodes)) return null;
            var stable = new HashSet<IFieldSymbol>(jobStruct.GetMembers().OfType<IFieldSymbol>()
                .Where(f => !f.IsStatic && !f.IsVolatile
                    && NativeTranspiler.IsEntJoyContainerNamed(f.Type, Config.NativeArray)),
                SymbolEqualityComparer.Default);
            var used = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
            foreach (var id in nodes.OfType<IdentifierNameSyntax>())
            {
                if (model.GetSymbolInfo(id).Symbol is not IFieldSymbol field || !stable.Contains(field)) continue;
                used.Add(field);
                ExpressionSyntax reference = id;
                if (id.Parent is MemberAccessExpressionSyntax qualified && qualified.Name == id)
                {
                    if (qualified.Expression is not ThisExpressionSyntax) { stable.Remove(field); continue; }
                    reference = qualified;
                }
                if (reference.Parent is ElementAccessExpressionSyntax element && element.Expression == reference) continue;
                if (reference.Parent is MemberAccessExpressionSyntax member && member.Expression == reference)
                {
                    if (member.Name.Identifier.ValueText == "Length") continue;
                    if (member.Name.Identifier.ValueText == Config.GetUnsafePtr
                        && member.Parent is InvocationExpressionSyntax call && call.ArgumentList.Arguments.Count == 0) continue;
                }
                stable.Remove(field);
            }
            stable.IntersectWith(used);
            return stable;
        }
    }
}
