#nullable enable
using System;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EntJoy.ECS.SourceGenerator
{
    // 三类 N 元查询共用调用形状与接收者识别，保留各自的生成模板。
    internal static class TupleQuerySyntax
    {
        internal static bool IsCandidate(SyntaxNode node, string methodName)
            => node is InvocationExpressionSyntax invocation
                && invocation.Expression is MemberAccessExpressionSyntax member
                && member.Name is GenericNameSyntax generic
                && generic.Identifier.Text == methodName
                && generic.TypeArgumentList.Arguments.Count >= Config.MinTupleArity;

        internal static int GetArity(GeneratorSyntaxContext context, CancellationToken cancellationToken, string receiverName)
        {
            if (context.Node is not InvocationExpressionSyntax invocation
                || invocation.Expression is not MemberAccessExpressionSyntax member
                || member.Name is not GenericNameSyntax generic
                || generic.TypeArgumentList.Arguments.Count < Config.MinTupleArity)
                return -1;

            // 首次编译时扩展方法还不存在，只按接收者类型识别。
            var type = context.SemanticModel.GetTypeInfo(member.Expression, cancellationToken).Type;
            string name = type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "";
            if (name.StartsWith("global::", StringComparison.Ordinal)) name = name.Substring("global::".Length);
            return name == receiverName ? generic.TypeArgumentList.Arguments.Count : -1;
        }
    }
}
