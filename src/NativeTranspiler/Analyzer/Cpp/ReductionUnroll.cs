using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 通解：把归约循环 `acc = callee(acc, arr[i])` / `acc += arr[i]` 生成为 4 路独立累加器 + 标量收尾。
    /// 单一累加链是串行依赖，编译器不会自行拆开；拆成 4 路后 load 与 min/max 可重叠执行。
    /// 前提：callee 满足交换律与结合律（min/max/add…），折叠顺序改变不影响结果。
    /// </summary>
    internal static class ReductionUnroll
    {
        private static bool IsIdent(ExpressionSyntax expr, string name)
            => expr is IdentifierNameSyntax id && id.Identifier.Text == name;

        private static bool IsIndexedBy(ExpressionSyntax expr, string ivName)
            => expr is ElementAccessExpressionSyntax ea
               && ea.ArgumentList.Arguments.Count == 1
               && IsIdent(ea.ArgumentList.Arguments[0].Expression, ivName);

        private static string SubstituteToken(string text, string token, string replacement)
            => Regex.Replace(text, $@"(?<![\w:.]){Regex.Escape(token)}(?![\w])", replacement);

        /// <summary>
        /// 识别并生成 4 路展开。识别不了返回 false，调用方走原有路径。
        /// 返回的每一行都已按 baseIndent 缩进好，调用方直接 AppendLine。
        /// </summary>
        internal static bool TryBuild(StatementTranslator scalar, ForStatementSyntax forStmt, int serial,
            int baseIndent, List<string> lines)
        {
            if (forStmt.Declaration == null || forStmt.Declaration.Variables.Count != 1) return false;
            var cond = forStmt.Condition as BinaryExpressionSyntax;
            if (cond == null || !cond.IsKind(SyntaxKind.LessThanExpression)) return false;
            if (forStmt.Incrementors.Count != 1) return false;
            if (!(forStmt.Incrementors[0] is PostfixUnaryExpressionSyntax inc)
                || !inc.IsKind(SyntaxKind.PostIncrementExpression)) return false;

            var decl = forStmt.Declaration.Variables[0];
            if (decl.Initializer == null) return false;
            string ivName = decl.Identifier.Text;

            var bodyStatements = forStmt.Statement is BlockSyntax blk
                ? blk.Statements.ToList()
                : new List<StatementSyntax> { forStmt.Statement };
            if (bodyStatements.Count == 0) return false;

            var accNames = new List<string>();
            var callees = new List<string>();
            foreach (var statement in bodyStatements)
            {
                if (!(statement is ExpressionStatementSyntax stmtExpr)) return false;
                if (!(stmtExpr.Expression is AssignmentExpressionSyntax assignment)) return false;
                if (!(assignment.Left is IdentifierNameSyntax accIdent)) return false;
                string accName = accIdent.Identifier.Text;
                if (accNames.Contains(accName)) return false;

                if (assignment.IsKind(SyntaxKind.AddAssignmentExpression))
                {
                    if (!IsIndexedBy(assignment.Right, ivName)) return false;
                    accNames.Add(accName);
                    callees.Add("+");
                    continue;
                }
                if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return false;
                if (!(assignment.Right is InvocationExpressionSyntax inv) || inv.ArgumentList.Arguments.Count != 2) return false;
                var arg0 = inv.ArgumentList.Arguments[0].Expression;
                var arg1 = inv.ArgumentList.Arguments[1].Expression;
                bool accIsFirst = IsIdent(arg0, accName);
                if (!accIsFirst && !IsIdent(arg1, accName)) return false;
                if (!IsIndexedBy(accIsFirst ? arg1 : arg0, ivName)) return false;

                string rhs = scalar.CaptureExpression(assignment.Right);
                int paren = rhs.IndexOf('(');
                if (paren <= 0) return false;
                accNames.Add(accName);
                callees.Add(rhs.Substring(0, paren));
            }

            string startExpr = scalar.CaptureExpression(decl.Initializer.Value);
            string endExpr = scalar.CaptureExpression(cond.Right);
            string bodyText = scalar.CaptureStatement(forStmt.Statement).Trim();

            string idx = $"__red{serial}_i";
            string n = $"__red{serial}_n";
            string Lane(int acc, int lane) => $"__red{serial}_{acc}_{lane}";
            void Add(int depth, string line) => lines.Add(new string(' ', (baseIndent + depth) * 4) + line);

            Add(0, "{");
            for (int lane = 0; lane < 4; lane++)
                for (int acc = 0; acc < accNames.Count; acc++)
                    Add(1, $"auto {Lane(acc, lane)} = {accNames[acc]};");
            Add(1, $"int {idx} = {startExpr};");
            Add(1, $"const int {n} = {endExpr};");
            Add(1, $"for (; {idx} + 3 < {n}; {idx} += 4)");
            Add(1, "{");
            for (int lane = 0; lane < 4; lane++)
            {
                string laneBody = bodyText;
                for (int acc = 0; acc < accNames.Count; acc++)
                    laneBody = SubstituteToken(laneBody, accNames[acc], Lane(acc, lane));
                laneBody = SubstituteToken(laneBody, ivName, lane == 0 ? idx : $"{idx} + {lane}");
                foreach (var line in laneBody.Split('\n'))
                    Add(2, line.TrimEnd());
            }
            Add(1, "}");
            for (int acc = 0; acc < accNames.Count; acc++)
                Add(1, callees[acc] == "+"
                    ? $"{accNames[acc]} = {Lane(acc, 0)} + {Lane(acc, 1)} + {Lane(acc, 2)} + {Lane(acc, 3)};"
                    : $"{accNames[acc]} = {callees[acc]}({callees[acc]}({Lane(acc, 0)}, {Lane(acc, 1)}), {callees[acc]}({Lane(acc, 2)}, {Lane(acc, 3)}));");
            Add(1, $"for (; {idx} < {n}; {idx}++)");
            Add(1, "{");
            foreach (var line in SubstituteToken(bodyText, ivName, idx).Split('\n'))
                Add(2, line.TrimEnd());
            Add(1, "}");
            Add(0, "}");
            return true;
        }
    }
}
