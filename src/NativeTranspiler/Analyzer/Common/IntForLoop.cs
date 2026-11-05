using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>单个只读 int 计数器、关系边界和常量非零步长的 for 循环。</summary>
    internal sealed class IntForLoop
    {
        public ExpressionSyntax Start { get; }
        public ExpressionSyntax Bound { get; }
        public SyntaxKind Comparison { get; }
        public int Step { get; }
        public string Operator => Comparison switch
        {
            SyntaxKind.LessThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => "<=",
            SyntaxKind.GreaterThanExpression => ">",
            _ => ">="
        };

        private IntForLoop(ExpressionSyntax start, ExpressionSyntax bound, SyntaxKind comparison, int step)
        {
            Start = start;
            Bound = bound;
            Comparison = comparison;
            Step = step;
        }

        public static bool TryAnalyze(ForStatementSyntax loop, SemanticModel model, out IntForLoop info)
        {
            info = null!;
            if (loop.SyntaxTree != model.SyntaxTree)
            {
                if (SourceSyntax.Original(model, loop) is not ForStatementSyntax original
                    || !TryAnalyze(original, model, out var source)
                    || loop.Declaration?.Variables[0].Initializer is not EqualsValueClauseSyntax loweredStart
                    || Unwrap(loop.Condition) is not BinaryExpressionSyntax loweredCondition
                    || Unwrap(original.Condition) is not BinaryExpressionSyntax sourceCondition) return false;
                var loweredBound = source.Bound.Span == sourceCondition.Left.Span ? loweredCondition.Left : loweredCondition.Right;
                info = new IntForLoop(loweredStart.Value, loweredBound, source.Comparison, source.Step);
                return true;
            }
            if (loop.Declaration?.Variables.Count != 1 || loop.Initializers.Count != 0
                || loop.Incrementors.Count != 1
                || loop.Declaration.Variables[0].Initializer is not EqualsValueClauseSyntax initializer
                || model.GetDeclaredSymbol(loop.Declaration.Variables[0]) is not ILocalSymbol counter
                || counter.Type.SpecialType != SpecialType.System_Int32
                || Unwrap(loop.Condition) is not BinaryExpressionSyntax condition)
                return false;

            bool IsCounter(ExpressionSyntax expression) => SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(Unwrap(expression)).Symbol, counter);

            SyntaxKind comparison = condition.Kind();
            if (comparison != SyntaxKind.LessThanExpression && comparison != SyntaxKind.LessThanOrEqualExpression
                && comparison != SyntaxKind.GreaterThanExpression && comparison != SyntaxKind.GreaterThanOrEqualExpression)
                return false;
            ExpressionSyntax bound;
            if (IsCounter(condition.Left)) bound = condition.Right;
            else if (IsCounter(condition.Right))
            {
                bound = condition.Left;
                comparison = comparison switch
                {
                    SyntaxKind.LessThanExpression => SyntaxKind.GreaterThanExpression,
                    SyntaxKind.LessThanOrEqualExpression => SyntaxKind.GreaterThanOrEqualExpression,
                    SyntaxKind.GreaterThanExpression => SyntaxKind.LessThanExpression,
                    _ => SyntaxKind.LessThanOrEqualExpression
                };
            }
            else return false;
            if (bound.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id => IsCounter(id)))
                return false;

            ExpressionSyntax increment = Unwrap(loop.Incrementors[0]);
            long step;
            if (increment is PostfixUnaryExpressionSyntax post && IsCounter(post.Operand)
                && (post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression)))
                step = post.IsKind(SyntaxKind.PostIncrementExpression) ? 1 : -1;
            else if (increment is PrefixUnaryExpressionSyntax pre && IsCounter(pre.Operand)
                && (pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression)))
                step = pre.IsKind(SyntaxKind.PreIncrementExpression) ? 1 : -1;
            else if (increment is AssignmentExpressionSyntax assignment && IsCounter(assignment.Left))
            {
                ExpressionSyntax amount = assignment.Right;
                bool subtract = assignment.IsKind(SyntaxKind.SubtractAssignmentExpression);
                if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && Unwrap(amount) is BinaryExpressionSyntax addition)
                {
                    if (IsCounter(addition.Left))
                    {
                        subtract = addition.IsKind(SyntaxKind.SubtractExpression);
                        if (!subtract && !addition.IsKind(SyntaxKind.AddExpression)) return false;
                        amount = addition.Right;
                    }
                    else if (addition.IsKind(SyntaxKind.AddExpression) && IsCounter(addition.Right))
                        amount = addition.Left;
                    else return false;
                }
                else if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression) && !subtract) return false;
                if (model.GetConstantValue(amount).Value is not int value) return false;
                step = subtract ? -(long)value : value;
            }
            else return false;
            if (step == 0 || step < int.MinValue || step > int.MaxValue) return false;

            var flow = model.AnalyzeDataFlow(loop.Statement);
            if (flow == null || !flow.Succeeded
                || flow.WrittenInside.Contains(counter, SymbolEqualityComparer.Default)
                || flow.UnsafeAddressTaken.Contains(counter, SymbolEqualityComparer.Default)
                || loop.Statement.DescendantNodesAndSelf().OfType<RefExpressionSyntax>().Any(r => IsCounter(r.Expression)))
                return false;

            info = new IntForLoop(initializer.Value, bound, comparison, (int)step);
            return true;
        }

        // 包含最后一次增量；溢出或超过展开预算时保持原循环。
        public bool TryGetTripCount(SemanticModel model, int limit, out int start, out int count)
        {
            start = count = 0;
            if (SourceSyntax.Constant(model, Start).Value is not int first
                || SourceSyntax.Constant(model, Bound).Value is not int last) return false;
            start = first;
            long current = first;
            while (Comparison switch
            {
                SyntaxKind.LessThanExpression => current < last,
                SyntaxKind.LessThanOrEqualExpression => current <= last,
                SyntaxKind.GreaterThanExpression => current > last,
                _ => current >= last
            })
            {
                if (count == limit) return false;
                count++;
                current += Step;
                if (current < int.MinValue || current > int.MaxValue) return false;
            }
            return true;
        }

        private static ExpressionSyntax Unwrap(ExpressionSyntax? expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
            return expression!;
        }
    }
}
