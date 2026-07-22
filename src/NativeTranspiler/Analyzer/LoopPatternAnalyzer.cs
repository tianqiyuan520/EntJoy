using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 规约操作类型
    /// </summary>
    public enum ReductionKind
    {
        Min,        // if (x < best) best = x;
        Max,        // if (x > best) best = x;
        MinIdx,     // if (x < best) { best = x; idx = i; }
        MaxIdx,     // if (x > best) { best = x; idx = i; }
        Sum,        // total += x;
        CondAssign, // if (cond) a = x;
    }

    /// <summary>
    /// 检测到的一条规约操作
    /// </summary>
    public class ReductionOp
    {
        public ReductionKind Kind;
        /// <summary>规约目标字段（被反复写入的字段）</summary>
        public string TargetField;
        /// <summary>从哪个 NativeArray 读取数据</summary>
        public string? DataField;
        /// <summary>索引字段（MinIdx/MaxIdx 时的跟踪索引）</summary>
        public string? IndexField;
        /// <summary>循环不变量的 C++ 表达式（如 q.x()）</summary>
        public List<string> InvariantExprs = new();
    }

    /// <summary>
    /// 循环分析结果
    /// </summary>
    public class LoopPattern
    {
        public bool IsVectorizable = false;
        public string? NonVectorizableReason;

        /// <summary>Execute 的索引参数名</summary>
        public string IndexVarName = "index";

        /// <summary>检测到的规约操作列表</summary>
        public List<ReductionOp> Reductions = new();

        /// <summary>被索引访问的 NativeArray 字段列表</summary>
        public List<string> IndexedArrays = new();

        /// <summary>循环不变量（q.x() 等，需要 broadcast）</summary>
        public List<string> Invariants = new();
    }

    /// <summary>
    /// 分析 IJobParallelFor.Execute 方法体，判断是否可向量化。
    /// 阶段一支持的场景：
    ///   - 顶级语句为纯算术、if 条件规约、复合赋值
    ///   - 无嵌套 for/while、return、break、continue、函数调用、间接索引
    /// </summary>
    public class LoopPatternAnalyzer
    {
        private readonly SemanticModel _semanticModel;

        public LoopPatternAnalyzer(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel;
        }

        /// <summary>
        /// 分析方法体，返回可向量化模式（或标记为不可向量化）。
        /// </summary>
        public LoopPattern Analyze(MethodDeclarationSyntax method, string indexVarName)
        {
            var pattern = new LoopPattern
            {
                IndexVarName = indexVarName,
                IsVectorizable = true
            };

            if (method.Body == null)
            {
                pattern.IsVectorizable = false;
                pattern.NonVectorizableReason = "No method body";
                return pattern;
            }

            // 只分析顶级语句
            foreach (var stmt in method.Body.Statements)
            {
                if (!AnalyzeStatement(stmt, pattern, indexVarName))
                {
                    pattern.IsVectorizable = false;
                    return pattern;
                }
            }

            // 必须至少有一个规约或索引数组访问，否则无意义
            if (pattern.Reductions.Count == 0 && pattern.IndexedArrays.Count == 0)
            {
                pattern.IsVectorizable = false;
                pattern.NonVectorizableReason = "No reduction or indexed array access found";
            }

            return pattern;
        }

        /// <summary>
        /// 递归分析语句，返回 false 表示不可向量化。
        /// </summary>
        private bool AnalyzeStatement(StatementSyntax stmt, LoopPattern pattern, string indexVarName)
        {
            switch (stmt)
            {
                case ForStatementSyntax forStmt:
                    // 嵌套 for → 不可向量化
                    pattern.NonVectorizableReason = "Contains inner for loop";
                    return false;

                case WhileStatementSyntax whileStmt:
                case DoStatementSyntax doStmt:
                    pattern.NonVectorizableReason = "Contains while/do loop";
                    return false;

                case BreakStatementSyntax _:
                case ContinueStatementSyntax _:
                    pattern.NonVectorizableReason = "Contains break/continue";
                    return false;

                case ReturnStatementSyntax _:
                    pattern.NonVectorizableReason = "Contains return";
                    return false;

                case LocalDeclarationStatementSyntax localDecl:
                    // 局部变量声明：检查初始化表达式
                    foreach (var variable in localDecl.Declaration.Variables)
                    {
                        if (variable.Initializer != null)
                        {
                            if (!AnalyzeExpression(variable.Initializer.Value, pattern, indexVarName))
                                return false;
                        }
                    }
                    return true;

                case ExpressionStatementSyntax exprStmt:
                    return AnalyzeExpression(exprStmt.Expression, pattern, indexVarName);

                case IfStatementSyntax ifStmt:
                    return AnalyzeIfStatement(ifStmt, pattern, indexVarName);

                case BlockSyntax block:
                    foreach (var s in block.Statements)
                    {
                        if (!AnalyzeStatement(s, pattern, indexVarName))
                            return false;
                    }
                    return true;

                case EmptyStatementSyntax _:
                    return true;

                default:
                    pattern.NonVectorizableReason = $"Unsupported statement type: {stmt.Kind()}";
                    return false;
            }
        }

        /// <summary>
        /// 分析 if 语句：必须是比较 + 简单规约赋值
        /// </summary>
        private bool AnalyzeIfStatement(IfStatementSyntax ifStmt, LoopPattern pattern, string indexVarName)
        {
            // 条件必须是比较表达式
            if (ifStmt.Condition is not BinaryExpressionSyntax binCond ||
                (binCond.Kind() != SyntaxKind.LessThanExpression &&
                 binCond.Kind() != SyntaxKind.GreaterThanExpression &&
                 binCond.Kind() != SyntaxKind.LessThanOrEqualExpression &&
                 binCond.Kind() != SyntaxKind.GreaterThanOrEqualExpression))
            {
                pattern.NonVectorizableReason = "If condition is not a simple comparison";
                return false;
            }

            // 检查 if 体中的赋值语句
            var bodyStatements = GetBodyStatements(ifStmt.Statement);
            if (bodyStatements.Count == 0)
            {
                pattern.NonVectorizableReason = "If body is empty";
                return false;
            }

            // 分析每个赋值
            bool hasMinMax = false;
            bool hasIndexTracking = false;

            foreach (var s in bodyStatements)
            {
                if (s is not ExpressionStatementSyntax exprStmt ||
                    exprStmt.Expression is not AssignmentExpressionSyntax assignment ||
                    (assignment.Kind() != SyntaxKind.SimpleAssignmentExpression &&
                     assignment.Kind() != SyntaxKind.AddAssignmentExpression))
                {
                    pattern.NonVectorizableReason = "If body contains non-assignment statement";
                    return false;
                }

                // 左值必须是简单标识符或字段访问
                string leftName = GetSimpleName(assignment.Left);
                if (leftName == null)
                {
                    pattern.NonVectorizableReason = "Assignment target is not a simple variable";
                    return false;
                }

                // 检查右值是否包含左值（reduction 特征）
                string rightText = assignment.Right.ToString();
                bool isReduction = rightText.Contains(leftName);

                // 检查右值中是否有数组索引访问
                if (!CheckExpressionForIndexedAccess(assignment.Right, pattern, indexVarName))
                    return false;

                if (isReduction)
                {
                    // 这是 reduction 模式
                    bool isMin = binCond.Kind() == SyntaxKind.LessThanExpression ||
                                 binCond.Kind() == SyntaxKind.LessThanOrEqualExpression;
                    hasMinMax = true;

                    // 检查是否是索引跟踪（左值不是条件变量）
                    // 如 bestIdx = i — i 是循环索引
                    if (assignment.Right.ToString() == indexVarName)
                    {
                        hasIndexTracking = true;
                    }
                }
                else
                {
                    // 简单条件赋值
                    pattern.Reductions.Add(new ReductionOp
                    {
                        Kind = ReductionKind.CondAssign,
                        TargetField = leftName,
                    });
                }

                // 检查右值中的数组索引访问
                var indexAccesses = FindIndexedArrayAccesses(assignment.Right, indexVarName);
                foreach (var arr in indexAccesses)
                {
                    if (!pattern.IndexedArrays.Contains(arr))
                        pattern.IndexedArrays.Add(arr);
                }
            }

            // 如果有 min/max + index tracking，合并为一个 MinIdx/MaxIdx
            if (hasMinMax)
            {
                bool isMin = binCond.Kind() == SyntaxKind.LessThanExpression ||
                             binCond.Kind() == SyntaxKind.LessThanOrEqualExpression;
                var kind = hasIndexTracking
                    ? (isMin ? ReductionKind.MinIdx : ReductionKind.MaxIdx)
                    : (isMin ? ReductionKind.Min : ReductionKind.Max);

                // 找出哪个字段是条件中比较的目标
                string condField = binCond.Right.ToString();
                string dataField = binCond.Left.ToString();

                pattern.Reductions.Add(new ReductionOp
                {
                    Kind = kind,
                    TargetField = condField,
                    DataField = dataField,
                    IndexField = hasIndexTracking ? bodyStatements
                        .Select(s => (s as ExpressionStatementSyntax)?.Expression as AssignmentExpressionSyntax)
                        .FirstOrDefault(a => a?.Right.ToString() == indexVarName)?.Left.ToString() : null,
                });
            }

            return true;
        }

        /// <summary>
        /// 分析表达式：检查是否有间接索引、函数调用等
        /// </summary>
        private bool AnalyzeExpression(ExpressionSyntax expr, LoopPattern pattern, string indexVarName)
        {
            // 检查函数调用（数学函数已内联，不应出现）
            var invocations = expr.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>();
            foreach (var inv in invocations)
            {
                var symbol = _semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (symbol != null)
                {
                    // 允许 EntJoy.Mathematics 中的函数
                    if (symbol.ContainingType?.ToDisplayString() == "EntJoy.Mathematics.math")
                        continue;
                    // 允许 MathF 函数
                    if (symbol.ContainingType?.ToDisplayString() == "System.MathF")
                        continue;

                    pattern.NonVectorizableReason = $"Contains function call: {symbol.Name}";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 检查表达式中是否有数组索引访问，并记录被访问的数组
        /// </summary>
        private bool CheckExpressionForIndexedAccess(ExpressionSyntax expr, LoopPattern pattern, string indexVarName)
        {
            var accesses = FindIndexedArrayAccesses(expr, indexVarName);
            foreach (var arr in accesses)
            {
                if (!pattern.IndexedArrays.Contains(arr))
                    pattern.IndexedArrays.Add(arr);
            }
            return true;
        }

        /// <summary>
        /// 在表达式中搜索 NativeArray[indexVarName] 的访问
        /// </summary>
        private List<string> FindIndexedArrayAccesses(ExpressionSyntax expr, string indexVarName)
        {
            var result = new List<string>();
            var elementAccesses = expr.DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>();
            foreach (var ea in elementAccesses)
            {
                if (ea.ArgumentList.Arguments.Count == 1)
                {
                    var argText = ea.ArgumentList.Arguments[0].Expression.ToString();
                    // 索引是 indexVarName 或者 indexVarName 的线性函数
                    if (argText == indexVarName || argText.Contains(indexVarName))
                    {
                        string arrText = ea.Expression.ToString();
                        if (!result.Contains(arrText))
                            result.Add(arrText);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 从语句中提取 body 语句列表（处理 BlockSyntax 或单语句）
        /// </summary>
        private List<StatementSyntax> GetBodyStatements(StatementSyntax stmt)
        {
            if (stmt is BlockSyntax block)
                return block.Statements.ToList();
            return new List<StatementSyntax> { stmt };
        }

        /// <summary>
        /// 获取表达式的简单名字（标识符或成员访问的最后部分）
        /// </summary>
        private static string? GetSimpleName(ExpressionSyntax expr)
        {
            return expr switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                _ => null
            };
        }
    }
}
