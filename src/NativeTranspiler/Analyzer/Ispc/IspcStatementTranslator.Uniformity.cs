using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public partial class IspcStatementTranslator
    {
        private readonly HashSet<ILocalSymbol> _uniformLocals = new(SymbolEqualityComparer.Default);

        // 只提升方法顶层的单变量声明；后续写入、取址、捕获及引用传参均保留 varying。
        protected bool CanDeclareUniformLocal(LocalDeclarationStatementSyntax declaration, VariableDeclaratorSyntax variable)
        {
            if (declaration.Declaration.Variables.Count != 1
                || declaration.Parent is not BlockSyntax body || body.Parent is not MethodDeclarationSyntax
                || variable.Initializer == null
                || _semanticModel.GetDeclaredSymbol(variable) is not ILocalSymbol local
                || local.RefKind != RefKind.None
                || local.Type.SpecialType is not (SpecialType.System_Boolean or SpecialType.System_Byte
                    or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
                    or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64
                    or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double)
                || !(_semanticModel.GetConstantValue(variable.Initializer.Value).HasValue
                    || IsUniformExpr(variable.Initializer.Value)))
                return false;

            int next = body.Statements.IndexOf(declaration) + 1;
            if (next >= body.Statements.Count) return false;
            var flow = _semanticModel.AnalyzeDataFlow(body.Statements[next], body.Statements.Last());
            if (flow == null || !flow.Succeeded
                || flow.WrittenInside.Contains(local, SymbolEqualityComparer.Default)
                || flow.UnsafeAddressTaken.Contains(local, SymbolEqualityComparer.Default)
                || flow.Captured.Contains(local, SymbolEqualityComparer.Default))
                return false;

            bool IsLocal(ExpressionSyntax expression)
            {
                while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
                return SymbolEqualityComparer.Default.Equals(_semanticModel.GetSymbolInfo(expression).Symbol, local);
            }
            foreach (var node in body.Statements.Skip(next).SelectMany(statement => statement.DescendantNodesAndSelf()))
            {
                if (node is RefExpressionSyntax reference && IsLocal(reference.Expression)) return false;
                if (node is ArgumentSyntax argument && IsLocal(argument.Expression)
                    && (argument.RefOrOutKeyword.RawKind != 0
                        || _semanticModel.GetOperation(argument) is IArgumentOperation operation
                            && operation.Parameter?.RefKind != RefKind.None))
                    return false;
            }
            return true;
        }

        /// <summary>判断表达式在 foreach 上下文中是否保证为 uniform。
        /// 字面量 → 总是 uniform。
        /// 字段 → AppendUniformVariableDeclarations 在 foreach 前已复制为 uniform 局部变量。
        /// NativeArray 字段或形参的长度由 uniform 元数据传入；元素访问保持 varying。
        /// </summary>
        private bool IsUniformExpr(ExpressionSyntax expr)
        {
            if (expr is LiteralExpressionSyntax) return true;
            if (expr is PrefixUnaryExpressionSyntax pre
                && (pre.IsKind(SyntaxKind.UnaryPlusExpression) || pre.IsKind(SyntaxKind.UnaryMinusExpression)
                    || pre.IsKind(SyntaxKind.LogicalNotExpression) || pre.IsKind(SyntaxKind.BitwiseNotExpression)))
                return IsUniformExpr(pre.Operand);
            if (expr is BinaryExpressionSyntax bin)
                return IsUniformExpr(bin.Left) && IsUniformExpr(bin.Right);
            if (expr is ParenthesizedExpressionSyntax paren)
                return IsUniformExpr(paren.Expression);
            if (expr is CastExpressionSyntax cast)
                return IsUniformExpr(cast.Expression);
            if (expr is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Length"
                && member.Expression is IdentifierNameSyntax arrayName
                && IsUniformArrayReceiver(arrayName)
                && _semanticModel.GetTypeInfo(member.Expression).Type is ITypeSymbol arrayType
                && NativeTranspiler.IsEntJoyContainerNamed(arrayType, Config.NativeArray))
                return true;
            if (expr is IdentifierNameSyntax id)
            {
                // 字段引用 → 已复制为 uniform 局部变量
                var sym = _semanticModel.GetSymbolInfo(id).Symbol;
                return sym is IFieldSymbol || sym is ILocalSymbol local && _uniformLocals.Contains(local);
            }
            // 数组访问、方法调用等 → varying
            return false;
        }

        protected virtual bool IsUniformArrayReceiver(IdentifierNameSyntax array)
            => _nativeArrayListNames.Contains(array.Identifier.ValueText)
                && _semanticModel.GetSymbolInfo(array).Symbol is IFieldSymbol or IParameterSymbol;

    }
}
