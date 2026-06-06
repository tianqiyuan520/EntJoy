using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;

namespace NativeTranspiler.Analyzer
{
    public sealed class IspcChunkStatementTranslator : IspcStatementTranslator
    {
        private readonly HashSet<string> _chunkNativeArrayNames = new();

        public IspcChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct)
            : base(semanticModel, jobStruct, null, false)
        {
        }

        protected override void TranslateLocalDeclaration(LocalDeclarationStatementSyntax localDecl)
        {
            bool emittedAny = false;
            foreach (var variable in localDecl.Declaration.Variables)
            {
                if (IsChunkNativeArrayInitializer(variable.Initializer?.Value))
                {
                    _chunkNativeArrayNames.Add(variable.Identifier.Text);
                    _nativeArrayListNames.Add(variable.Identifier.Text);
                    continue;
                }

                if (!emittedAny)
                {
                    AppendIndent();
                    var type = _semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type!);
                    _builder.Append(ToIspcTypeName(cppType));
                    emittedAny = true;
                }
                else
                {
                    _builder.Append(", ");
                }

                _builder.Append(' ').Append(variable.Identifier.Text);
                if (variable.Initializer != null)
                {
                    _builder.Append(" = ");
                    TranslateExpression(variable.Initializer.Value);
                }
            }

            if (emittedAny)
            {
                _builder.AppendLine(";");
            }
        }

        protected override void TranslateForStatement(ForStatementSyntax forStmt)
        {
            if (TryTranslateChunkArrayForEach(forStmt))
            {
                return;
            }

            base.TranslateForStatement(forStmt);
        }

        protected override void TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is IdentifierNameSyntax id &&
                _chunkNativeArrayNames.Contains(id.Identifier.Text) &&
                memberAccess.Name.Identifier.Text == "Length")
            {
                _builder.Append(id.Identifier.Text).Append("_length");
                return;
            }

            base.TranslateMemberAccess(memberAccess);
        }

        protected override void TranslateElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
            if (elementAccess.Expression is IdentifierNameSyntax id && _chunkNativeArrayNames.Contains(id.Identifier.Text))
            {
                _builder.Append(id.Identifier.Text).Append("_ptr[");
                var args = elementAccess.ArgumentList.Arguments;
                if (args.Count > 0)
                {
                    TranslateExpression(args[0].Expression);
                }
                _builder.Append(']');
                return;
            }

            base.TranslateElementAccess(elementAccess);
        }

        private bool TryTranslateChunkArrayForEach(ForStatementSyntax forStmt)
        {
            if (forStmt.Declaration == null ||
                forStmt.Declaration.Variables.Count != 1 ||
                forStmt.Condition is not BinaryExpressionSyntax condition ||
                !condition.OperatorToken.IsKind(SyntaxKind.LessThanToken) ||
                condition.Left is not IdentifierNameSyntax conditionIndex ||
                condition.Right is not MemberAccessExpressionSyntax lengthAccess ||
                lengthAccess.Expression is not IdentifierNameSyntax arrayId ||
                !_chunkNativeArrayNames.Contains(arrayId.Identifier.Text))
            {
                return false;
            }

            string indexName = forStmt.Declaration.Variables[0].Identifier.Text;
            if (conditionIndex.Identifier.Text != indexName)
            {
                return false;
            }

            AppendIndent();
            _builder.Append("foreach (").Append(indexName).Append(" = 0 ... ").Append(arrayId.Identifier.Text).Append("_length) ");
            if (forStmt.Statement is BlockSyntax block)
            {
                _builder.AppendLine("{");
                _indentLevel++;
                foreach (var statement in block.Statements)
                {
                    TranslateStatement(statement);
                }
                _indentLevel--;
                AppendIndent();
                _builder.AppendLine("}");
            }
            else
            {
                _builder.AppendLine("{");
                _indentLevel++;
                TranslateStatement(forStmt.Statement);
                _indentLevel--;
                AppendIndent();
                _builder.AppendLine("}");
            }

            return true;
        }

        private bool IsChunkNativeArrayInitializer(ExpressionSyntax? expression)
        {
            if (expression is not InvocationExpressionSyntax invocation)
            {
                return false;
            }

            return _semanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol methodSymbol &&
                   methodSymbol.ContainingType?.ToDisplayString() == "EntJoy.ArchetypeChunk" &&
                   methodSymbol.Name == "GetComponentDataNativeArray";
        }

        private static string ToIspcTypeName(string cppType)
        {
            return cppType switch
            {
                "EntJoy::Mathematics::float2" => "float2",
                "EntJoy::Mathematics::int2" => "int2",
                "EntJoy::Mathematics::uint2" => "uint2",
                "unsigned int" => "unsigned int",
                "float" => "float",
                "int" => "int",
                "bool" => "bool",
                _ when cppType.Contains("::") => cppType.Substring(cppType.LastIndexOf("::") + 2),
                _ => cppType
            };
        }
    }
}
