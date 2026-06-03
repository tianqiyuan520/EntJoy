using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    public sealed class CppChunkStatementTranslator : CppPointerStatementTranslator
    {
        private readonly List<INamedTypeSymbol> _requiredComponentTypes;
        private readonly HashSet<string> _chunkArrayLocalNames = new();

        public CppChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct, List<INamedTypeSymbol> requiredComponentTypes)
            : base(semanticModel, jobStruct)
        {
            _requiredComponentTypes = requiredComponentTypes;
        }

        protected override void TranslateLocalDeclaration(LocalDeclarationStatementSyntax localDecl)
        {
            if (TryTranslateChunkArrayLocal(localDecl))
                return;

            base.TranslateLocalDeclaration(localDecl);
        }

        private bool TryTranslateChunkArrayLocal(LocalDeclarationStatementSyntax localDecl)
        {
            if (localDecl.Declaration.Variables.Count == 0)
                return false;

            var localType = _semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
            bool isSpan = localType?.Name == "Span" && localType.ContainingNamespace?.ToDisplayString() == "System";
            bool isNativeArray = localType != null && NativeTranspiler.IsEntJoyNativeContainerType(localType) && localType.Name == "NativeArray";
            if (!isSpan && !isNativeArray)
                return false;

            var lines = new StringBuilder();
            foreach (var variable in localDecl.Declaration.Variables)
            {
                if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation)
                    return false;
                if (!TryBuildChunkArrayExpression(invocation, out var cppType, out var expression))
                    return false;

                _chunkArrayLocalNames.Add(variable.Identifier.Text);
                lines.Append(new string(' ', _indentLevel * 4));
                lines.Append("EntJoy::Collections::NativeArray<");
                lines.Append(cppType);
                lines.Append("> ");
                lines.Append(variable.Identifier.Text);
                lines.Append(" = ");
                lines.Append(expression);
                lines.AppendLine(";");
            }

            _builder.Append(lines);
            return true;
        }

        protected override void TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            if (TryBuildChunkArrayExpression(invocation, out _, out var expression))
            {
                _builder.Append(expression);
                return;
            }

            base.TranslateInvocation(invocation);
        }

        private bool TryBuildChunkArrayExpression(InvocationExpressionSyntax invocation, out string cppType, out string expression)
        {
            cppType = "";
            expression = "";

            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
                return false;
            if (methodSymbol.ContainingType?.ToDisplayString() != "EntJoy.ArchetypeChunk")
                return false;
            if (methodSymbol.Name != "GetComponentDataNativeArray" && methodSymbol.Name != "GetComponentDataSpan")
                return false;
            if (methodSymbol.TypeArguments.Length == 0)
                return false;

            var componentType = methodSymbol.TypeArguments[0];
            int componentIndex = _requiredComponentTypes.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, componentType));
            if (componentIndex < 0)
                componentIndex = 0;

            cppType = NativeTranspiler.MapCSharpTypeToCpp(componentType);
            expression = $"EntJoy::ChunkNativeArray::GetChunkNativeArray<{cppType}>(__chunkData, __requiredComponentTypeIds[{componentIndex}])";
            return true;
        }

        protected override void TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is IdentifierNameSyntax identifier &&
                _chunkArrayLocalNames.Contains(identifier.Identifier.Text) &&
                memberAccess.Name.Identifier.Text == "Length")
            {
                _builder.Append(identifier.Identifier.Text).Append(".length()");
                return;
            }

            base.TranslateMemberAccess(memberAccess);
        }

        protected override void TranslateElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
            if (elementAccess.Expression is IdentifierNameSyntax identifier &&
                _chunkArrayLocalNames.Contains(identifier.Identifier.Text))
            {
                _builder.Append(identifier.Identifier.Text).Append('[');
                var args = elementAccess.ArgumentList.Arguments;
                if (args.Count > 0)
                    TranslateExpression(args[0].Expression);
                _builder.Append(']');
                return;
            }

            base.TranslateElementAccess(elementAccess);
        }
    }
}
