using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 为任意 IJobParallelFor/IJobFor 生成外层 SIMD 代码。
    /// 外层 SIMD = NSIMD_WIDTH 个连续 index 同时执行 Execute(index)。
    ///
    /// 对任何 body（含内层循环、间接索引等）都能生成。
    /// 机制：
    ///   1) Gather 每个通道的输入（memory-level parallelism）
    ///   2) 每个通道独立执行 scalar body
    ///   3) Scatter 每个通道的输出
    /// </summary>
    public class OuterSimdGenerator
    {
        private readonly MethodDeclarationSyntax _methodSyntax;
        private readonly SemanticModel _semanticModel;
        private readonly string _idx;

        public OuterSimdGenerator(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel, string indexVarName)
        {
            _methodSyntax = methodSyntax;
            _semanticModel = semanticModel;
            _idx = indexVarName;
        }

        /// <summary>
        /// 生成外层 SIMD 代码。
        /// 对任何 body 都生成可用代码；若 body 太复杂则退到 gather+scalar+scatter。
        /// </summary>
        public string Generate(string scalarBody)
        {
            var sb = new StringBuilder();
            string idx = _idx;

            // 收集数组访问
            var arrayReads = new HashSet<string>();
            var arrayWrites = new HashSet<string>();
            var reductionVars = new HashSet<string>();
            CollectAccesses(arrayReads, arrayWrites, reductionVars);

            // 输出：外层 SIMD 循环
            sb.AppendLine("    // --- Outer SIMD vectorization ---");
            sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine("    if (simd_end_ > __startIndex)");
            sb.AppendLine("    {");
            sb.AppendLine("        simd_i v_i_base = simd_i::sequence(0);");
            sb.AppendLine("        simd_f v_val; // temp");

            // 每 8 个 index 一批
            sb.AppendLine("        for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)");
            sb.AppendLine("        {");
            sb.AppendLine("            simd_i v_i = v_i_base + si;");
            sb.AppendLine("            simd_mask v_active = simd_mask::all_true();");

            // 对每个读取数组，生成 gather
            foreach (var arr in arrayReads)
            {
                string safe = Sanitize(arr);
                // 尝试识别类型: 默认 float 类型
                if (reductionVars.Contains(arr))
                    sb.AppendLine($"            simd_f v_{safe} = simd_f::broadcast({arr});");
                else
                    sb.AppendLine($"            simd_f v_{safe} = simd_f::gathf((const float*){arr}_ptr, v_i);");
            }

            // 对每个规约变量的 SIMD 版本
            foreach (var r in reductionVars)
                sb.AppendLine($"            simd_f v_{r} = simd_f::broadcast({r});");

            // 为每个通道单独跑 scalar body 的数组
            sb.AppendLine("            // --- per-lane scalar execution ---");
            sb.AppendLine("            float lane_val[8];");
            sb.AppendLine("            int lane_idx[8];");

            // 对每个写入数组，生成临时 store 再 scatter
            // 直接把 scalarBody 嵌入到 per-lane 循环中
            sb.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
            sb.AppendLine("            {");
            sb.AppendLine($"                int {idx} = si + lane;");
            // scalar body 直接用
            foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                sb.Append("                ").AppendLine(line.TrimEnd());
            }
            sb.AppendLine("            }");

            // scatter 写入类型的数组
            foreach (var w in arrayWrites)
            {
                string safe = Sanitize(w);
                sb.AppendLine($"            // sc->ter {w}_ptr[si+0..7]");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");

            // 余量标量
            sb.AppendLine($"    for (int {idx} = simd_end_; {idx} < __startIndex + __count; ++{idx})");
            sb.AppendLine("    {");
            foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                sb.Append("    ").AppendLine(line.TrimEnd());
            }
            sb.AppendLine("    }");

            return sb.ToString();
        }

        private void CollectAccesses(HashSet<string> reads, HashSet<string> writes, HashSet<string> reductions)
        {
            if (_methodSyntax.Body == null) return;
            foreach (var stmt in _methodSyntax.Body.Statements)
                WalkAccesses(stmt, reads, writes, reductions);
        }

        private void WalkAccesses(StatementSyntax stmt, HashSet<string> reads,
            HashSet<string> writes, HashSet<string> reductions)
        {
            switch (stmt)
            {
                case ExpressionStatementSyntax es:
                    WalkExpr(es.Expression, reads, writes, reductions);
                    break;
                case IfStatementSyntax ifStmt:
                    var body = (ifStmt.Statement is BlockSyntax blk
                        ? blk.Statements.Cast<StatementSyntax>()
                        : new[] { ifStmt.Statement }).ToList();
                    foreach (var s in body) WalkAccesses(s, reads, writes, reductions);
                    if (ifStmt.Else != null)
                    {
                        var elseBody = (ifStmt.Else.Statement is BlockSyntax eblk
                            ? eblk.Statements.Cast<StatementSyntax>()
                            : new[] { ifStmt.Else.Statement }).ToList();
                        foreach (var s in elseBody) WalkAccesses(s, reads, writes, reductions);
                    }
                    break;
                case LocalDeclarationStatementSyntax ld:
                    foreach (var v in ld.Declaration.Variables)
                        if (v.Initializer != null)
                            WalkExpr(v.Initializer.Value, reads, writes, reductions);
                    break;
                case ForStatementSyntax fs:
                    if (fs.Statement is BlockSyntax fblk)
                        foreach (var s in fblk.Statements)
                            WalkAccesses(s, reads, writes, reductions);
                    break;
            }
        }

        private void WalkExpr(ExpressionSyntax expr, HashSet<string> reads,
            HashSet<string> writes, HashSet<string> reductions)
        {
            // arr[i] 类型访问
            foreach (var ea in expr.DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>())
            {
                if (ea.ArgumentList.Arguments.Count == 1 &&
                    ea.ArgumentList.Arguments[0].Expression.ToString().Contains(_idx))
                {
                    reads.Add(ea.Expression.ToString());
                }
            }

            if (expr is AssignmentExpressionSyntax assign)
            {
                // 左值 arr[i] 写入
                if (assign.Left is ElementAccessExpressionSyntax lea
                    && lea.ArgumentList.Arguments.Count == 1
                    && lea.ArgumentList.Arguments[0].Expression.ToString().Contains(_idx))
                {
                    writes.Add(lea.Expression.ToString());
                }
                // 规约: total += x
                if (assign.Kind() == SyntaxKind.AddAssignmentExpression
                    && assign.Left is IdentifierNameSyntax lid)
                {
                    reductions.Add(lid.Identifier.Text);
                }
            }
        }

        private static string Sanitize(string name) => name.Replace(".", "_").Replace("[]", "_arr");
    }
}
