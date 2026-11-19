// 数据并行循环识别：整段方法体/Execute 体恰为一个逐元素计数循环时，
// 后端可以把它摊到整条 SIMD 链路上（ISPC 用 foreach，AutoSIMD 用 si 循环）。
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer.Common
{
    internal static class DataParallelLoop
    {
        /// <summary>
        /// 方法体是否"恰好一个可并行化的逐元素循环"。
        /// 成立时返回该循环的元素级范围，可直接发射成 <c>foreach (v = start ... end)</c>。
        /// </summary>
        public static bool TryGetSole(BlockSyntax body, SemanticModel model,
            out ForStatementSyntax loop, out string loopVar,
            out ExpressionSyntax startExpr, out ExpressionSyntax boundExpr)
        {
            loop = null;
            loopVar = null;
            startExpr = null;
            boundExpr = null;

            if (body.Statements.Count != 1 || body.Statements[0] is not ForStatementSyntax candidate)
                return false;
            if (!IntForLoop.TryAnalyze(candidate, model, out var info))
                return false;
            if (info.Step != 1 || info.Comparison != SyntaxKind.LessThanExpression)
                return false;
            if (candidate.Declaration?.Variables.Count != 1)
                return false;
            string name = candidate.Declaration.Variables[0].Identifier.Text;
            if (!IsUniformScalarExpression(info.Start, model) || !IsUniformScalarExpression(info.Bound, model))
                return false;
            if (!IsLaneIndependentLoopBody(candidate.Statement, name, model))
                return false;

            loop = candidate;
            loopVar = name;
            startExpr = info.Start;
            boundExpr = info.Bound;
            return true;
        }

        /// <summary>循环变量与边界的取值在 gang 内一致（字面量、参数、字段、循环外局部及其算术）。</summary>
        private static bool IsUniformScalarExpression(ExpressionSyntax expr, SemanticModel model)
        {
            if (model.GetConstantValue(expr).HasValue) return true;
            switch (expr)
            {
                case ParenthesizedExpressionSyntax paren:
                    return IsUniformScalarExpression(paren.Expression, model);
                case CastExpressionSyntax cast:
                    return IsUniformScalarExpression(cast.Expression, model);
                case IdentifierNameSyntax id:
                    return model.GetSymbolInfo(id).Symbol is IParameterSymbol or IFieldSymbol or ILocalSymbol;
                case BinaryExpressionSyntax bin:
                    return IsUniformScalarExpression(bin.Left, model) && IsUniformScalarExpression(bin.Right, model);
                case PrefixUnaryExpressionSyntax unary:
                    return IsUniformScalarExpression(unary.Operand, model);
                default:
                    return false;
            }
        }

        /// <summary>
        /// 循环体是否与 lane 无关：没有跳出、没有跨迭代写、没有副作用调用。
        /// 只改循环体内声明的局部，数组只在"同一个下标"上读写。
        /// </summary>
        private static bool IsLaneIndependentLoopBody(StatementSyntax body, string loopVar, SemanticModel model)
        {
            var innerLocals = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            // 局部声明有两种：语句形式（`int x = ...;`）与 for/using 的声明形式（`for (int k = 0; ...)`）。
            foreach (var declaration in body.DescendantNodesAndSelf().OfType<VariableDeclarationSyntax>())
                foreach (var variable in declaration.Variables)
                    if (model.GetDeclaredSymbol(variable) is ILocalSymbol symbol)
                        innerLocals.Add(symbol);

            // 数组名 → (是否被写, 是否所有访问都用循环变量本身作下标)
            var arrays = new Dictionary<string, (bool Written, bool AllExact)>();

            foreach (var node in body.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case BreakStatementSyntax:
                    case ContinueStatementSyntax:
                    case ReturnStatementSyntax:
                    case ThrowStatementSyntax:
                    case GotoStatementSyntax:
                        return false;
                }

                if (node is InvocationExpressionSyntax invocation && !IsPureMathCall(invocation, model))
                    return false;

                if (node is AssignmentExpressionSyntax assignment)
                {
                    if (!IsLaneLocalTarget(assignment.Left, innerLocals, model)) return false;
                    if (assignment.Left is ElementAccessExpressionSyntax leftAccess)
                    {
                        if (!RecordArrayAccess(leftAccess, loopVar, model, arrays, written: true)) return false;
                    }
                }

                if (node is PrefixUnaryExpressionSyntax prefix
                    && (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression))
                    && prefix.Operand is IdentifierNameSyntax)
                {
                    if (!IsLaneLocalTarget(prefix.Operand, innerLocals, model)) return false;
                }
                if (node is PostfixUnaryExpressionSyntax postfix
                    && (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression))
                    && postfix.Operand is IdentifierNameSyntax)
                {
                    if (!IsLaneLocalTarget(postfix.Operand, innerLocals, model)) return false;
                }

                if (node is ElementAccessExpressionSyntax access && access.ArgumentList.Arguments.Count == 1)
                {
                    bool written = access.Parent is AssignmentExpressionSyntax parentAssignment
                        && ReferenceEquals(parentAssignment.Left, access);
                    if (!RecordArrayAccess(access, loopVar, model, arrays, written)) return false;
                }
            }

            foreach (var (written, allExact) in arrays.Values)
                if (written && !allExact)
                    return false;

            return true;
        }

        /// <summary>赋值/自增减的目标只能是循环体内声明的局部；数组元素由 <see cref="RecordArrayAccess"/> 单独判定。</summary>
        private static bool IsLaneLocalTarget(ExpressionSyntax left, HashSet<ISymbol> innerLocals, SemanticModel model)
        {
            switch (left)
            {
                case IdentifierNameSyntax id:
                    return model.GetSymbolInfo(id).Symbol is ILocalSymbol local && innerLocals.Contains(local);
                case ElementAccessExpressionSyntax access:
                    return access.Expression is IdentifierNameSyntax;
                default:
                    return false;
            }
        }

        /// <summary>记录数组访问；被写的数组必须所有访问都用循环变量本身作下标，否则存在跨迭代依赖。</summary>
        private static bool RecordArrayAccess(ElementAccessExpressionSyntax access, string loopVar, SemanticModel model,
            Dictionary<string, (bool Written, bool AllExact)> arrays, bool written)
        {
            if (access.ArgumentList.Arguments.Count != 1) return false;
            if (access.Expression is not IdentifierNameSyntax arrayName) return false;

            // NativeArray 字段之外的东西（指针、局部数组）不在本判定范围内，直接拒绝。
            var symbol = model.GetSymbolInfo(arrayName).Symbol;
            if (symbol is not IFieldSymbol && symbol is not IParameterSymbol) return false;

            bool exact = IsLoopVarIndex(access.ArgumentList.Arguments[0].Expression, loopVar);
            string name = arrayName.Identifier.Text;
            if (arrays.TryGetValue(name, out var existing))
                arrays[name] = (existing.Written || written, existing.AllExact && exact);
            else
                arrays[name] = (written, exact);
            return true;
        }

        private static bool IsLoopVarIndex(ExpressionSyntax index, string loopVar)
            => index is IdentifierNameSyntax id && id.Identifier.Text == loopVar;

        /// <summary>只放行 System.Math / System.MathF 的静态调用（纯函数，无副作用）。</summary>
        private static bool IsPureMathCall(InvocationExpressionSyntax invocation, SemanticModel model)
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
            if (!method.IsStatic) return false;
            string type = method.ContainingType?.ToDisplayString() ?? "";
            return type is "System.Math" or "System.MathF";
        }
    }
}
