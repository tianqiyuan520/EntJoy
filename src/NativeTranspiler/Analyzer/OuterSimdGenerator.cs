using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativeTranspiler.Analyzer
{
    public class OuterSimdGenerator
    {
        private readonly MethodDeclarationSyntax _methodSyntax;
        private readonly SemanticModel _semanticModel;
        private readonly string _idx;
        private readonly Dictionary<string, string> _boolFields;
        private readonly INamedTypeSymbol? _jobStruct;

        public OuterSimdGenerator(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel, string indexVarName,
            Dictionary<string, string>? boolFieldValues = null,
            INamedTypeSymbol? jobStruct = null)
        {
            _methodSyntax = methodSyntax;
            _semanticModel = semanticModel;
            _idx = indexVarName;
            _boolFields = boolFieldValues ?? new Dictionary<string, string>();
            _jobStruct = jobStruct;
        }

        public string Generate(string scalarBody)
        {
            // Try universal SIMD from AST
            string simdResult = GenerateFullSIMDFromAST(scalarBody);
            if (!string.IsNullOrEmpty(simdResult))
                return simdResult;
            // FALLBACK: per-lane scalar
            return GeneratePerLane(scalarBody);
        }

        // ----------------------------------------------------------------
        // Per-lane scalar fallback (when AST analysis fails)
        // ----------------------------------------------------------------
        private string GeneratePerLane(string scalarBody)
        {
            var sb = new StringBuilder();
            sb.AppendLine("    // --- Outer SIMD: per-lane (register extract) ---");
            sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine("    if (simd_end_ > __startIndex)");
            sb.AppendLine("    {");
            sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0);");
            sb.AppendLine("        for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)");
            sb.AppendLine("        {");
            sb.AppendLine("            simd_value<int> v_i = v_base + si;");
            sb.AppendLine("            simd_value<EntJoy::Mathematics::float2> v_q =");
            sb.AppendLine("                simd_value<EntJoy::Mathematics::float2>::gather(QueryPositions_ptr, v_i);");
            sb.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
            sb.AppendLine("            {");
            sb.AppendLine("                int index = si + lane;");
            foreach (var kvp in _boolFields)
                sb.AppendLine($"                bool {kvp.Key} = {kvp.Value};");
            sb.AppendLine("                EntJoy::Mathematics::float2 qbuf; qbuf.x() = n_extract_lane_f32(v_q.x.v, lane); qbuf.y() = n_extract_lane_f32(v_q.y.v, lane);");
            // H4: wrap lane body in do{ }while(false) so that return->break exits the lane, not just innermost for
            sb.AppendLine("                // H4: do-while-false wrapper for safe early-exit (return -> break)");
            sb.AppendLine("                do");
            sb.AppendLine("                {");
            foreach (var line in scalarBody.Split('\n'))
            {
                var l = line.TrimEnd();
                if (string.IsNullOrEmpty(l)) continue;
                l = l.Replace("QueryPositions_ptr[index]", "qbuf");
                l = l.Replace("return;", "break;");
                sb.Append("                    ").AppendLine(l);
            }
            sb.AppendLine("                } while(false);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.Append(RemainderLoop(scalarBody));
            return sb.ToString();
        }

        private string RemainderLoop(string scalarBody)
        {
            // Substitute bool field names with literal values so MSVC eliminates dead branches
            string substituted = scalarBody;
            foreach (var kvp in _boolFields)
                substituted = System.Text.RegularExpressions.Regex.Replace(
                    substituted, $@"\b{kvp.Key}\b", kvp.Value);

            var sb = new StringBuilder();
            sb.AppendLine($"    for (int {_idx} = simd_end_; {_idx} < __startIndex + __count; ++{_idx})");
            sb.AppendLine("    {");
            // Only wrap in do-while(false) when the body has return (e.g. FindWithin)
            bool bodyHasReturn = substituted.Contains("return;");
            if (bodyHasReturn)
            {
                sb.AppendLine("    do");
                sb.AppendLine("    {");
            }
            foreach (var line in substituted.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var l = line.TrimEnd();
                l = l.Replace("return;", "break;");
                sb.Append("    ").AppendLine(l);
            }
            if (bodyHasReturn)
            {
                sb.AppendLine("    } while(false);");
            }
            sb.AppendLine("    }");
            return sb.ToString();
        }

        // ----------------------------------------------------------------
        // ================================================================
        // Universal Full-SIMD Path (ISPC-style from AST)
        // ================================================================

        /// <summary>
        /// 检查 Execute 体是否适合通用全 SIMD 生成。
        /// 使用宽松检查——SimdControlFlowGenerator 支持所有控制流：
        /// if/else/for/while/do/break/continue/return。
        /// 只拒绝真正不支持的模式（间接索引、switch、foreach）。
        /// </summary>
        private bool IsFullSIMDEligible()
        {
            if (_methodSyntax.Body == null || _jobStruct == null) return false;

            var varAnalyzer = new SimdVariableAnalyzer(_semanticModel, _jobStruct, _idx);
            var variables = varAnalyzer.Analyze(_methodSyntax);
            return variables.ContainsKey(_idx) && variables.Count > 0;
        }

        /// <summary>
        /// 宽松检查：只拒绝 SimdControlFlowGenerator 完全不支持的语句。
        /// 间接索引 arr[hash[i]]、switch、foreach、fixed 等被拒绝。
        /// if/for/while/do/break/continue/return 全部支持。
        /// </summary>
        private bool HasUnsupportedStatement(SyntaxNode node)
        {
            foreach (var stmt in node.DescendantNodesAndSelf())
            {
                switch (stmt)
                {
                    case SwitchStatementSyntax _:
                    case CommonForEachStatementSyntax _:
                    case FixedStatementSyntax _:
                    case UsingStatementSyntax _:
                    case CheckedStatementSyntax _:
                    case UnsafeStatementSyntax _:
                        return true;

                    case InvocationExpressionSyntax invocation:
                        if (HasUnsupportedCall(invocation))
                            return true;
                        break;

                    case ElementAccessExpressionSyntax elementAccess:
                        // 间接索引 arr[hash[i]] 仍不支持
                        if (HasIndirectIndex(elementAccess))
                            return true;
                        break;
                }
            }
            return false;
        }

        /// <summary>
        /// 检查函数调用是否不被支持（只允许 math 函数和已知模式）。
        /// </summary>
        private bool HasUnsupportedCall(InvocationExpressionSyntax invocation)
        {
            try
            {
                var symbol = _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (symbol == null) return true;

                string containingType = symbol.ContainingType?.ToDisplayString() ?? "";
                if (containingType == "EntJoy.Mathematics.math") return false;
                if (containingType == "System.MathF") return false;
                if (containingType == "System.Math") return false;

                // 允许已知的数学函数名
                if (symbol.Name is "min" or "max" or "clamp" or "floor" or "ceil" or "abs"
                    or "dot" or "distancesq" or "lengthsq" or "length" or "normalize"
                    or "Min" or "Max" or "Abs" or "Sqrt" or "Floor" or "Ceiling")
                    return false;

                // NativeArray.GetUnsafePtr 允许
                if (symbol.ContainingType?.Name == "NativeArray" && symbol.Name == "GetUnsafePtr")
                    return false;

                // 其他函数调用暂时不允许
                return true;
            }
            catch
            {
                return true; // 不确定则保守拒绝
            }
        }

        /// <summary>
        /// 检测间接索引 arr[hash[i]]。
        /// </summary>
        private static bool HasIndirectIndex(ElementAccessExpressionSyntax elementAccess)
        {
            foreach (var arg in elementAccess.ArgumentList.Arguments)
            {
                if (arg.Expression.DescendantNodesAndSelf()
                    .OfType<ElementAccessExpressionSyntax>().Any())
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 生成通用全 SIMD 代码。
        /// 使用 SimdVariableAnalyzer + SimdControlFlowGenerator 从 AST 直接生成。
        /// </summary>
        private string GenerateFullSIMDFromAST(string scalarBody)
        {

            var sb = new StringBuilder();
            if (_jobStruct == null || _methodSyntax.Body == null)
                return "";

            // 1. 变量分析
            var varAnalyzer = new SimdVariableAnalyzer(_semanticModel, _jobStruct, _idx);
            var variables = varAnalyzer.Analyze(_methodSyntax);

            // 2. 外层 SIMD 循环框架
            sb.AppendLine("    // --- Universal Full-SIMD (ISPC-style) ---");
            sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine("    if (simd_end_ > __startIndex)");
            sb.AppendLine("    {");
            sb.AppendLine("        // Hoisted loop-invariant broadcasts");
            sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0);");
            sb.AppendLine("        for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)");
            sb.AppendLine("        {");
            sb.AppendLine("            simd_value<int> v_i = v_base + si;");

            // 3. 布尔字段常量注入
            foreach (var kvp in _boolFields)
            {
                sb.AppendLine($"            bool {kvp.Key} = {kvp.Value};");
            }

            // 4. 生成 SIMD body
            var cfGenerator = new SimdControlFlowGenerator(
                _semanticModel, _jobStruct, variables, varAnalyzer,
                indexParamName: _idx, simdIndexVar: "v_i",
                boolFields: _boolFields);
            string simdBody = cfGenerator.Generate(_methodSyntax.Body);

            // 缩进并追加
            foreach (var line in simdBody.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    sb.Append("            ").AppendLine(line);
            }

            sb.AppendLine("        __simd_exit: ;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            // 5. 标量余量循环（使用传入的 scalarBody）
            if (!string.IsNullOrEmpty(scalarBody))
                sb.Append(RemainderLoop(scalarBody));

            return sb.ToString();
        }

        /// <summary>
        /// 获取标量 body 用于余量循环。
        /// 临时方法——后续可改为缓存 pre-translated scalar body。
        /// </summary>
        private string GetScalarBody()
        {
            // 使用现有的 _boolFields 构建简化标量体
            // 这里返回空字符串，让调用方处理
            return "";
        }
    }
}
