using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 为 SIMD-eligible 的 IJobParallelFor 生成外层 SIMD 代码。
    /// 外层 SIMD = 8 个连续 index 同时执行 Execute(index)。
    ///
    /// 生成的代码：
    ///   // SIMD 主体（使用 simd_f / simd_i / simd_mask）
    ///   for (int si = start; si + WIDTH <= end; si += WIDTH) {
    ///       simd_f v_xxx = simd_f::load(&xxx_ptr[si]);
    ///       // ... 运算 ...
    ///       simd_f::store(&out_ptr[si], v_out);
    ///   }
    ///   // 水平规约（reduction 变量）
    ///   max = v_max.hmax();
    ///   // 余量标量
    ///   for (; si < end; ++si) { 标量体 }
    /// </summary>
    public class OuterSimdGenerator
    {
        private readonly MethodDeclarationSyntax _methodSyntax;
        private readonly SemanticModel _semanticModel;
        private readonly string _indexVarName;

        public OuterSimdGenerator(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel, string indexVarName)
        {
            _methodSyntax = methodSyntax;
            _semanticModel = semanticModel;
            _indexVarName = indexVarName;
        }

        /// <summary>
        /// 生成外层 SIMD 代码。
        /// </summary>
        /// <param name="scalarBody">CppBatchStatementTranslator 翻译的标量体</param>
        /// <param name="pattern">循环分析结果</param>
        public string Generate(string scalarBody)
        {
            var sb = new StringBuilder();
            string idx = _indexVarName;

            // ===== 分析 body 中的变量 =====
            var arrayReads = new HashSet<string>();   // 被读取的数组
            var arrayWrites = new HashSet<string>();  // 被写入的数组
            var reductions = new List<(string name, string kind)>(); // 规约变量
            var hasIfReduction = false;

            if (_methodSyntax.Body != null)
            {
                foreach (var stmt in _methodSyntax.Body.Statements)
                {
                    AnalyzeStatements(stmt, idx, arrayReads, arrayWrites, reductions, ref hasIfReduction);
                }
            }

            // ===== 生成 SIMD 代码 =====

            // SIMD 循环
            sb.AppendLine("    // --- Outer SIMD vectorization ---");
            sb.AppendLine($"    #include \"../../NativeDll/SimdValue.h\"");
            sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;");

            // Reduction 变量的 SIMD 版本
            foreach (var (name, kind) in reductions)
            {
                if (kind == "sum")
                    sb.AppendLine($"    simd_f v_{name} = simd_f::broadcast(0);");
                else
                    sb.AppendLine($"    simd_f v_{name} = simd_f::broadcast({name});");
            }

            // 索引基向量
            sb.AppendLine($"    simd_i v_i_base = simd_i::sequence(0);");
            sb.AppendLine($"    for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)");
            sb.AppendLine("    {");
            sb.AppendLine($"        simd_i v_i = v_i_base + si;");

            // 数组读取 → gather/load
            foreach (var arr in arrayReads)
            {
                if (arrayWrites.Contains(arr)) continue; // 读写数组在 body 翻译中处理
                sb.AppendLine($"        simd_f v_{arr} = simd_f::load(&{arr}_ptr[si]);");
            }

            // body 中的运算（从 scalarbody 提取并包裹）
            // 对于简单体，直接生成 scalar body 加类型包装
            // 这里用简化的方式：直接复用 scalar body 但将 index 替换为 si
            // 实际 body 由外层包装
            AppendSIMDBody(sb, scalarBody, idx, reductions, arrayReads, arrayWrites);

            sb.AppendLine("    }");

            // 水平规约
            foreach (var (name, kind) in reductions)
            {
                if (kind == "sum")
                    sb.AppendLine($"    {name} += v_{name}.hsum();");
                else if (kind == "min")
                    sb.AppendLine($"    {name} = v_{name}.hmin();");
                else
                    sb.AppendLine($"    {name} = v_{name}.hmax();");
            }

            // 标量余量
            sb.AppendLine($"    for (int {idx} = simd_end_; {idx} < __startIndex + __count; ++{idx})");
            sb.AppendLine("    {");
            foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                sb.Append("        ").AppendLine(line.TrimEnd());
            }
            sb.AppendLine("    }");

            return sb.ToString();
        }

        /// <summary>
        /// 分析语句，收集数组访问和规约信息
        /// </summary>
        private void AnalyzeStatements(StatementSyntax stmt, string idxVar,
            HashSet<string> reads, HashSet<string> writes,
            List<(string, string)> reductions, ref bool hasIf)
        {
            switch (stmt)
            {
                case ExpressionStatementSyntax es:
                    AnalyzeExpression(es.Expression, idxVar, reads, writes, reductions, ref hasIf);
                    break;
                case IfStatementSyntax ifStmt:
                    hasIf = true;
                    var body = ifStmt.Statement is BlockSyntax blk ? blk.Statements.ToList()
                        : new List<StatementSyntax> { ifStmt.Statement };
                    foreach (var s in body)
                        AnalyzeStatements(s, idxVar, reads, writes, reductions, ref hasIf);
                    break;
                case LocalDeclarationStatementSyntax ld:
                    foreach (var v in ld.Declaration.Variables)
                        if (v.Initializer != null)
                            AnalyzeExpression(v.Initializer.Value, idxVar, reads, writes, reductions, ref hasIf);
                    break;
            }
        }

        private void AnalyzeExpression(ExpressionSyntax expr, string idxVar,
            HashSet<string> reads, HashSet<string> writes,
            List<(string, string)> reductions, ref bool hasIf)
        {
            // 检查输入: arr[i]
            var elemAccesses = expr.DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>();
            foreach (var ea in elemAccesses)
            {
                if (ea.ArgumentList.Arguments.Count == 1 && ea.ArgumentList.Arguments[0].Expression.ToString() == idxVar)
                {
                    string arrName = ea.Expression.ToString();
                    reads.Add(arrName);
                }
            }

            // 检查输出: out[i] = val
            if (expr is AssignmentExpressionSyntax assign)
            {
                if (assign.Left is ElementAccessExpressionSyntax leftEa
                    && leftEa.ArgumentList.Arguments.Count == 1
                    && leftEa.ArgumentList.Arguments[0].Expression.ToString() == idxVar)
                {
                    string outName = leftEa.Expression.ToString();
                    writes.Add(outName);
                }

                // 检查规约: total += val 或 max = max > x ? max : x
                if (assign.Kind() == SyntaxKind.AddAssignmentExpression
                    && assign.Left is IdentifierNameSyntax lid)
                {
                    reductions.Add((lid.Identifier.Text, "sum"));
                }
            }
        }

        /// <summary>
        /// 生成 SIMD 体：将每个数组读写操作翻译成 SIMD 版本
        /// </summary>
        private static void AppendSIMDBody(StringBuilder sb, string scalarBody, string idx,
            List<(string name, string kind)> reductions,
            HashSet<string> reads, HashSet<string> writes)
        {
            // 对于简单体，关键是将 arr[i] 替换为 v_arr（SIMD 向量）
            // 将 reduction 变量替换为 v_xxx 向量
            // 将 if (cond) x = y 替换为 blend + min/max

            // 简化做法：生成模板化代码
            // 每个写入数组
            foreach (var w in writes)
            {
                // 检查是规约写入还是普通写入
                bool isReduction = reductions.Any(r => r.name == w);
                if (isReduction) continue; // 规约在循环外处理

                // 普通连续写入
                sb.AppendLine($"        simd_f::store(&{w}_ptr[si], v_{w});");
            }

            // 如果有 if-reduction，添加 blend/min 操作
            if (reductions.Any(r => r.kind != "sum"))
            {
                foreach (var (name, kind) in reductions)
                {
                    if (kind == "min")
                        sb.AppendLine($"        // auto min reduction: v_{name} = min(v_{name}, v_data);");
                    else if (kind == "max")
                        sb.AppendLine($"        // auto max reduction: v_{name} = max(v_{name}, v_data);");
                }
            }
        }


    }
}
