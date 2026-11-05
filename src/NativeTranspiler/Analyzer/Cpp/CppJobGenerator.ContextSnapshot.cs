using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NativeTranspiler.Analyzer.Common;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    public static partial class CppJobGenerator
    {
        // A private copy separates immutable job metadata from stores through component/data pointers.
        // Keep the stack copy within 1 KiB; larger or unproven contexts retain their original bindings.
        private static int GetReadOnlyContextSize(INamedTypeSymbol job, SemanticModel model, MethodDeclarationSyntax method)
        {
            if (method.Body == null) return 0;
            var fields = job.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
            bool CanCopy(IFieldSymbol field)
            {
                if (field.IsVolatile || field.IsFixedSizeBuffer || field.Type.IsReferenceType
                    || field.Type.TypeKind == TypeKind.Error) return false;
                if (field.Type is not INamedTypeSymbol value || !value.IsValueType
                    || value.SpecialType != SpecialType.None || value.TypeKind == TypeKind.Enum) return true;
                return value.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).All(CanCopy);
            }
            if (fields.Length == 0 || !fields.All(CanCopy)) return 0;
            int size = 0;
            foreach (var field in fields) CalculateFieldOffset(field, ref size);
            if (size > 1024) return 0;

            var nodes = method.Body.DescendantNodes().ToArray();
            if (HasIndirectJobFieldWrites(job, model, nodes)) return 0;

            // Element stores address external data. Member stores before an element access address metadata.
            bool IsMetadata(ExpressionSyntax expression)
            {
                switch (expression)
                {
                    case ParenthesizedExpressionSyntax p: return IsMetadata(p.Expression);
                    case ElementAccessExpressionSyntax e:
                        return !IsDataView(e.Expression) && IsMetadata(e.Expression);
                    case MemberAccessExpressionSyntax m:
                        if (m.IsKind(SyntaxKind.PointerMemberAccessExpression)) return false;
                        if (m.Expression is not ThisExpressionSyntax) return IsMetadata(m.Expression);
                        break;
                    case IdentifierNameSyntax: break;
                    default: return false;
                }
                return model.GetSymbolInfo(expression).Symbol is IFieldSymbol f && !f.IsStatic
                    && SymbolEqualityComparer.Default.Equals(f.ContainingType, job);
            }

            bool IsDataView(ExpressionSyntax expression)
            {
                var type = model.GetTypeInfo(expression).Type;
                return type is IPointerTypeSymbol || (type != null && NativeTranspiler.IsEntJoyNativeContainerType(type));
            }

            foreach (var node in nodes)
            {
                if (node is AssignmentExpressionSyntax assignment && IsMetadata(assignment.Left)) return 0;
                if (node is PrefixUnaryExpressionSyntax prefix
                    && (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)
                        || prefix.IsKind(SyntaxKind.AddressOfExpression)) && IsMetadata(prefix.Operand)) return 0;
                if (node is PostfixUnaryExpressionSyntax postfix && IsMetadata(postfix.Operand)) return 0;
                if (node is ArgumentSyntax argument && argument.RefOrOutKeyword.RawKind != 0
                    && IsMetadata(argument.Expression)) return 0;
                if (node is InvocationExpressionSyntax call && call.Expression is MemberAccessExpressionSyntax receiver
                    && IsMetadata(receiver.Expression))
                {
                    var symbol = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                    if (symbol == null || !(IsDataView(receiver.Expression) && symbol.Name == "GetUnsafePtr"
                        && symbol.Parameters.Length == 0)) return 0;
                }
                if (node is MemberAccessExpressionSyntax member && IsMetadata(member.Expression)
                    && model.GetSymbolInfo(member).Symbol is IPropertySymbol property
                    && !(IsDataView(member.Expression) && property.Name is "Length" or "Capacity" or "IsCreated")) return 0;
                if (node is ElementAccessExpressionSyntax element && IsMetadata(element.Expression)
                    && !IsDataView(element.Expression)) return 0;
            }
            return size;
        }

        private static void AppendReadOnlyContextSnapshot(StringBuilder sb, int size)
        {
            if (size == 0) return;
            sb.AppendLine($"    alignas(16) unsigned char __jobSnapshot[{size}];");
            sb.AppendLine($"    std::memcpy(__jobSnapshot, __jobContext, {size});");
            sb.AppendLine("    __jobContext = reinterpret_cast<char*>(__jobSnapshot);");
        }
    }
}
