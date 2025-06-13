using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Text;

namespace EntJoy.SourceGenerator.Utils
{
    internal static class TypeDeclarationSyntaxHelper
    {
        // 为类型声明语法节点生成类型名称
        public static string WriteTypeName(in SemanticModel semanticModel, TypeDeclarationSyntax typeDeclarationSyntax)
        {
            var typeBuilder = new StringBuilder().
                Append("partial ").
                Append(typeDeclarationSyntax.Keyword.ValueText).
                Append(" ").
                Append(typeDeclarationSyntax.Identifier.ToString()).
                Append(typeDeclarationSyntax.TypeParameterList);

            foreach (var constraintClause in typeDeclarationSyntax.ConstraintClauses)
            {
                typeBuilder.Append("where ");
                foreach(var childNode in constraintClause.ChildNodes())
                {
                    switch (childNode)
                    {
                        case IdentifierNameSyntax identifierNameSyntax:
                            typeBuilder.Append(childNode).Append(" : ");
                            break;
                        case TypeConstraintSyntax typeConstraintSyntax:
                            typeBuilder.Append(semanticModel.GetTypeInfo(typeConstraintSyntax.Type).Type.ToDisplayString());
                            break;
                    }
                }

            }

            return typeBuilder.ToString();
        }
    }
}
