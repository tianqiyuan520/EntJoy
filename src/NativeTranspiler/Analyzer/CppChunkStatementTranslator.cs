using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer
{
    public sealed class CppChunkStatementTranslator : CppPointerStatementTranslator
    {
        private readonly List<INamedTypeSymbol> _requiredComponentTypes;

        public CppChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct, List<INamedTypeSymbol> requiredComponentTypes)
            : base(semanticModel, jobStruct)
        {
            _requiredComponentTypes = requiredComponentTypes;
        }

        protected override void TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
                methodSymbol.ContainingType?.ToDisplayString() == "EntJoy.ArchetypeChunk" &&
                methodSymbol.Name == "GetComponentDataNativeArray" &&
                methodSymbol.TypeArguments.Length > 0)
            {
                var componentType = methodSymbol.TypeArguments[0];
                int componentIndex = _requiredComponentTypes.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, componentType));
                if (componentIndex < 0)
                    componentIndex = 0;

                var cppType = NativeTranspiler.MapCSharpTypeToCpp(componentType);
                _builder.Append("EntJoy::ChunkNativeArray::GetChunkNativeArray<").Append(cppType).Append(">(__chunkData, __requiredComponentTypeIds[").Append(componentIndex).Append("])");
                return;
            }

            base.TranslateInvocation(invocation);
        }
    }
}
