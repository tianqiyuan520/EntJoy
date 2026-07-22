using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 分析 Execute 方法体，判断是否适合外层 SIMD 向量化。
    /// 外层 SIMD = 每个 SIMD 通道跑一个完整的 Execute(index) 实例。
    ///
    /// 适合条件：
    ///   - Execute 体内无嵌套 for/while（常量展开的固定循环除外）
    ///   - 无 return/break/continue
    ///   - 无间接索引 arr[hash[i]]
    ///   - 无函数调用
    /// </summary>
    public class SimdEligibilityAnalyzer
    {
        private readonly SemanticModel _semanticModel;

        public SimdEligibilityAnalyzer(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel;
        }

        public bool IsEligible { get; private set; }
        public string? Reason { get; private set; }

        /// <summary>
        /// 分析 Execute 体，返回 true 表示可外层 SIMD。
        /// </summary>
        public bool Analyze(MethodDeclarationSyntax method)
        {
            IsEligible = true;
            Reason = null;

            if (method.Body == null)
            {
                MarkNotEligible("No method body");
                return false;
            }

            foreach (var stmt in method.Body.Statements)
            {
                if (!CheckStatement(stmt))
                    return false;
            }

            return IsEligible;
        }

        private bool CheckStatement(StatementSyntax stmt)
        {
            switch (stmt)
            {
                case ForStatementSyntax forStmt:
                    // for 循环对外层 SIMD 是安全的——每个 SIMD 通道
                    // 独立跑自己的内层循环，互不干扰（for_masked 处理）。
                    // 递归检查 for 体中的语句
                    if (forStmt.Statement is BlockSyntax forBlock)
                    {
                        foreach (var s in forBlock.Statements)
                            if (!CheckStatementLoose(s)) return false;
                    }
                    return true;

                case WhileStatementSyntax _:
                case DoStatementSyntax _:
                    MarkNotEligible("Contains while/do loop");
                    return false;

                case ReturnStatementSyntax _:
                case BreakStatementSyntax _:
                case ContinueStatementSyntax _:
                    MarkNotEligible("Contains return/break/continue");
                    return false;

                case IfStatementSyntax ifStmt:
                    // 检查 if 条件中是否有函数调用
                    if (HasUnsupportedCall(ifStmt.Condition))
                    {
                        MarkNotEligible("Contains function call in condition");
                        return false;
                    }
                    // 检查 if 体
                    var ifBody = ifStmt.Statement is BlockSyntax ifBlock ? ifBlock.Statements.ToList()
                        : new List<StatementSyntax> { ifStmt.Statement };
                    foreach (var s in ifBody)
                        if (!CheckStatement(s)) return false;

                    // 检查 else 体
                    if (ifStmt.Else != null)
                    {
                        var elseBody = ifStmt.Else.Statement is BlockSyntax elseBlock ? elseBlock.Statements.ToList()
                            : new List<StatementSyntax> { ifStmt.Else.Statement };
                        foreach (var s in elseBody)
                            if (!CheckStatement(s)) return false;
                    }
                    return true;

                case ExpressionStatementSyntax exprStmt:
                    // 检查表达式中的函数调用
                    if (HasUnsupportedCall(exprStmt.Expression))
                    {
                        MarkNotEligible("Contains function call");
                        return false;
                    }
                    // 检查间接索引 arr[hash[i]]
                    if (HasIndirectIndex(exprStmt.Expression))
                    {
                        MarkNotEligible("Contains indirect index");
                        return false;
                    }
                    return true;

                case LocalDeclarationStatementSyntax localDecl:
                    foreach (var v in localDecl.Declaration.Variables)
                    {
                        if (v.Initializer != null && HasUnsupportedCall(v.Initializer.Value))
                        {
                            MarkNotEligible("Contains function call in initialization");
                            return false;
                        }
                        if (v.Initializer != null && HasIndirectIndex(v.Initializer.Value))
                        {
                            MarkNotEligible("Contains indirect index in initialization");
                            return false;
                        }
                    }
                    return true;

                case EmptyStatementSyntax _:
                    return true;

                case BlockSyntax block:
                    foreach (var s in block.Statements)
                        if (!CheckStatement(s)) return false;
                    return true;

                default:
                    MarkNotEligible($"Unsupported statement: {stmt.Kind()}");
                    return false;
            }
        }

        /// <summary>
        /// 判断 for 是否为常量边界循环（如 for dx=-1;dx<=1;dx++）。
        /// 数据循环（for i=s;i<e;i++）的边界是变量。
        /// </summary>
        private static bool IsConstantBoundForLoop(ForStatementSyntax forStmt)
        {
            if (forStmt.Declaration == null) return false;
            if (forStmt.Declaration.Variables.Count != 1) return false;
            var varDecl = forStmt.Declaration.Variables[0];
            if (varDecl.Initializer == null) return false;

            // 检查条件右值是常量
            if (forStmt.Condition is BinaryExpressionSyntax cond)
            {
                string rightStr = cond.Right.ToString();
                // 简单的常量检测：数字字面量
                if (rightStr.All(c => char.IsDigit(c) || c == '-' || c == '+' || c == '.'))
                    return true;
                // int2.zero 或 GridDimensions.x 这种也不一定
                // 保守判断：含字母的表达式不算常量
            }
            return false;
        }

        /// <summary>
        /// Loose check for for-loop bodies: allows break/continue.
        /// For outer SIMD, each channel runs its own for loop,
        /// so break/continue only affects that channel, not others.
        /// </summary>
        private bool CheckStatementLoose(StatementSyntax stmt)
        {
            switch (stmt)
            {
                case BreakStatementSyntax _:
                case ContinueStatementSyntax _:
                case ReturnStatementSyntax _:
                    return true; // allowed inside for loops (generator replaces return with break)
                case ForStatementSyntax forStmt:
                    if (forStmt.Statement is BlockSyntax fb)
                        foreach (var s in fb.Statements)
                            if (!CheckStatementLoose(s)) return false;
                    return true;
                case IfStatementSyntax ifStmt:
                    var body = ifStmt.Statement is BlockSyntax blk ? blk.Statements.ToList()
                        : new List<StatementSyntax> { ifStmt.Statement };
                    foreach (var s in body)
                        if (!CheckStatementLoose(s)) return false;
                    if (ifStmt.Else != null)
                    {
                        var elseBody = ifStmt.Else.Statement is BlockSyntax eblk ? eblk.Statements.ToList()
                            : new List<StatementSyntax> { ifStmt.Else.Statement };
                        foreach (var s in elseBody)
                            if (!CheckStatementLoose(s)) return false;
                    }
                    return true;
                case ExpressionStatementSyntax es:
                    if (HasUnsupportedCall(es.Expression)) return false;
                    if (HasIndirectIndex(es.Expression)) return false;
                    return true;
                case LocalDeclarationStatementSyntax ld:
                    foreach (var v in ld.Declaration.Variables)
                    {
                        if (v.Initializer != null && HasUnsupportedCall(v.Initializer.Value)) return false;
                        if (v.Initializer != null && HasIndirectIndex(v.Initializer.Value)) return false;
                    }
                    return true;
                case EmptyStatementSyntax _:
                    return true;
                case BlockSyntax block:
                    foreach (var s in block.Statements)
                        if (!CheckStatementLoose(s)) return false;
                    return true;
                default:
                    MarkNotEligible($"Unsupported in for body: {stmt.Kind()}");
                    return false;
            }
        }

        private bool HasUnsupportedCall(ExpressionSyntax expr)
        {
            var invocations = expr.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>();
            foreach (var inv in invocations)
            {
                var symbol = _semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (symbol != null)
                {
                    // 允许 math 函数
                    string containingType = symbol.ContainingType?.ToDisplayString() ?? "";
                    if (containingType == "EntJoy.Mathematics.math") continue;
                    if (containingType == "System.MathF") continue;
                    if (symbol.Name == "min" || symbol.Name == "max" || symbol.Name == "clamp"
                        || symbol.Name == "floor" || symbol.Name == "ceil" || symbol.Name == "abs"
                        || symbol.Name == "dot" || symbol.Name == "distancesq" || symbol.Name == "lengthsq")
                        continue;
                    return true;
                }
            }
            return false;
        }

        private static bool HasIndirectIndex(ExpressionSyntax expr)
        {
            // 检查 arr[hash[i]] 这种间接索引
            var elementAccesses = expr.DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>();
            foreach (var ea in elementAccesses)
            {
                foreach (var arg in ea.ArgumentList.Arguments)
                {
                    // 索引表达式中含有数组访问 → 间接索引
                    if (arg.Expression.DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>().Any())
                        return true;
                }
            }
            return false;
        }

        private void MarkNotEligible(string reason)
        {
            IsEligible = false;
            Reason = reason;
        }
    }
}
