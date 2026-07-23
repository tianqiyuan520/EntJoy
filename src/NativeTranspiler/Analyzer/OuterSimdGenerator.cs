using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 通用 Outer SIMD 生成器。
    /// 为 IJobParallelFor/IJobFor 添加外层 batch 循环 + SIMD index gather。
    ///
    /// 职责：
    ///   1. batch loop: for(si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)
    ///   2. SIMD index: v_i = v_base + si
    ///   3. 内层 body 由 SimdControlFlowGenerator 生成（mask-managed SIMD）
    ///   4. 移除 SIMD body 中的多重 per-lane 写回（保留一份统一写回）
    ///   5. 标量尾循环处理剩余元素
    ///
    /// 无硬编码字段名。所有变量名从 AST 自动推导。
    /// 统一写回：从 scalar body 的最后一条写入提取表达式和变量名，
    /// 生成 per-lane 循环，sentinel 值从标量代码的条件推断。
    /// </summary>
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

        /// <summary>生成完整的 SIMD C++ 代码。先尝试全 SIMD，失败后退避到 per-lane。</summary>
        public string Generate(string scalarBody)
        {
            string simdResult = GenerateFullSIMDFromAST(scalarBody);
            if (!string.IsNullOrEmpty(simdResult))
                return simdResult;
            return GeneratePerLane(scalarBody);
        }

        /// <summary>
        /// 全 SIMD 路径：batch loop + SimdControlFlowGenerator。
        /// 写回策略：
        ///   1. SimdControlFlowGenerator 生成每处写入（包括 if/else 分支中的）
        ///   2. OuterSimdGenerator 移除所有 per-lane scatter 循环
        ///   3. 从 scalar body 的最后一条写入推导统一写回表达式
        /// </summary>
        private string GenerateFullSIMDFromAST(string scalarBody)
        {
            if (_jobStruct == null || _methodSyntax.Body == null) return "";
            try
            {
                var varAnalyzer = new SimdVariableAnalyzer(_semanticModel, _jobStruct, _idx);
                var variables = varAnalyzer.Analyze(_methodSyntax);
                if (!variables.ContainsKey(_idx) || variables.Count == 0) return "";

                var sb = new StringBuilder();
                sb.AppendLine("    // --- Universal Full-SIMD (ISPC-style) ---");
                sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;");
                sb.AppendLine("    if (simd_end_ > __startIndex)");
                sb.AppendLine("    {");
                sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0);");
                sb.AppendLine("        for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)");
                sb.AppendLine("        {");
                sb.AppendLine("            simd_value<int> v_i = v_base + si;");

                var cfGenerator = new SimdControlFlowGenerator(
                    _semanticModel, _jobStruct, variables, varAnalyzer,
                    indexParamName: _idx, simdIndexVar: "v_i",
                    boolFields: _boolFields);

                // Pass sentinel info for uniform reduction loop early-exit (generic, no hardcoded names)
                var writePattern = ExtractResultWritePattern(scalarBody);
                if (writePattern != null)
                {
                    cfGenerator._sentinelVar = writePattern.IndexVar;
                    cfGenerator._sentinelVal = writePattern.Sentinel;
                }

                string simdBody = cfGenerator.Generate(_methodSyntax.Body);

                // 移除多重 per-lane scatter 循环（n_mask_to_bitmask 模式）
                // 这些是 SimdControlFlowGenerator 在 if/else 分支内生成的写入，
                // 统一写回会代替它们，避免多次写回。
                simdBody = RemovePerLaneWrites(simdBody);

                foreach (var line in simdBody.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.Append("            ").AppendLine(line);

                // 统一写回：从 scalar body 最后一条写入分析索引变量名
                // 推导出 SIMD 寄存器名（v_ 前缀），生成 per-lane 提取+写入
                var writeInfo = ExtractResultWritePattern(scalarBody);
                if (writeInfo != null)
                {
                    sb.AppendLine("            // Unified write");
                    sb.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++) {");
                    sb.AppendLine($"                int {writeInfo.IndexVar}_lane = n_extract_lane_epi32(v_{writeInfo.IndexVar}.v, lane);");
                    sb.AppendLine($"                if ({writeInfo.IndexVar}_lane != {writeInfo.Sentinel})");
                    sb.AppendLine($"                    {writeInfo.WriteExpr};");
                    sb.AppendLine("            }");
                }

                sb.AppendLine("        __simd_exit: ;");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                if (!string.IsNullOrEmpty(scalarBody))
                    sb.Append(RemainderLoop(scalarBody));
                return sb.ToString();
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>从标量 body 分析最后一条写入的模式</summary>
        private class WritePattern
        {
            public string WriteExpr;   // 写入表达式（如 "Results_ptr[si + lane] = HashIndex_ptr[bestIdx_lane].y()"）
            public string IndexVar;    // 结果索引变量名（如 "bestIdx"）
            public string Sentinel;    // 哨兵值（如 "-1"）
        }

        /// <summary>
        /// 分析 scalar body 的最后一条 _ptr 写入，提取结果索引变量名和哨兵值。
        /// 例如：Results_ptr[index] = HashIndex_ptr[bestIdx].y();
        ///   → IndexVar = "bestIdx", Sentinel = "-1"
        /// 完全基于标量代码的文本模式推导，无硬编码。
        /// </summary>
        private static WritePattern? ExtractResultWritePattern(string scalarBody)
        {
            // 查找最后一条形如  XXX_ptr[index] = ... 的写入
            int idx = scalarBody.LastIndexOf("_ptr[index]");
            if (idx < 0) return null;

            // 向左找最近的变量名作为输出数组前缀
            int eq = scalarBody.IndexOf('=', idx);
            int semi = scalarBody.IndexOf(';', eq);
            if (eq < 0 || semi < 0) return null;

            // 提取 RHS：从 = 到 ; 之间
            string rhs = scalarBody.Substring(eq + 1, semi - eq - 1).Trim();

            // 如果 RHS 是 -1，说明是初始化为空结果，不是实际写入
            if (rhs == "-1") return null;

            // 在 RHS 中寻找 [变量名] 模式（如 HashIndex_ptr[bestIdx].y()）
            var bracketMatch = Regex.Match(rhs, @"\[(\w+)\]");
            if (!bracketMatch.Success) return null;

            string indexVar = bracketMatch.Groups[1].Value;

            // 查找标量 body 中形如 "indexVar != -1" 或 "indexVar == -1" 的模式
            var sentinelMatch = Regex.Match(scalarBody, $@"\b{Regex.Escape(indexVar)}\s*!=\s*(-?\d+)");
            string sentinel = sentinelMatch.Success ? sentinelMatch.Groups[1].Value : "-1";

            // 构建写入表达式：将 RHS 中的 [indexVar] 替换为 [indexVar_lane]
            string writeRHS = rhs.Replace($"[{indexVar}]", $"[{indexVar}_lane]");
            // 提取 LHS 数组名：从 "_ptr[index]" 左边找标识符
            int ptrIdx = scalarBody.LastIndexOf("_ptr[index]", idx);
            int lhsStart = scalarBody.LastIndexOfAny(" \n\r;{".ToCharArray(), ptrIdx) + 1;
            string lhsArray = scalarBody.Substring(lhsStart, ptrIdx - lhsStart);

            string writeExpr = $"{lhsArray}_ptr[si + lane] = {writeRHS}";

            return new WritePattern
            {
                WriteExpr = writeExpr,
                IndexVar = indexVar,
                Sentinel = sentinel
            };
        }

        /// <summary>按 n_mask_to_bitmask 模式移除 per-lane scatter 块</summary>
        private static string RemovePerLaneWrites(string simdBody)
        {
            string marker = "n_mask_to_bitmask";
            int idx = simdBody.IndexOf(marker);
            while (idx >= 0)
            {
                int openBrace = simdBody.LastIndexOf('{', idx);
                if (openBrace < 0) break;
                int depth = 0, end = -1;
                for (int i = openBrace; i < simdBody.Length; i++)
                {
                    if (simdBody[i] == '{') depth++;
                    else if (simdBody[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { end = i; break; }
                    }
                }
                if (end < 0) break;
                simdBody = simdBody.Remove(openBrace, end + 1 - openBrace);
                idx = simdBody.IndexOf(marker);
            }
            return simdBody;
        }

        private string GeneratePerLane(string scalarBody)
        {
            string body = scalarBody;
            foreach (var kvp in _boolFields)
                body = Regex.Replace(body, $@"\b{kvp.Key}\b", kvp.Value);

            bool hr = body.Contains("return;");
            var sb = new StringBuilder();
            sb.AppendLine("    // --- Outer SIMD: per-lane ---");
            sb.AppendLine("    int simd_end_=__startIndex+((__count)/NSIMD_WIDTH)*NSIMD_WIDTH;");
            sb.AppendLine("    if(simd_end_>__startIndex){");
            sb.AppendLine("        simd_value<int> v_base=simd_value<int>::sequence(0);");
            sb.AppendLine("        for(int si=__startIndex;si<simd_end_;si+=NSIMD_WIDTH){");
            sb.AppendLine("            for(int lane=0;lane<NSIMD_WIDTH;lane++){");
            sb.AppendLine("                int index=si+lane;");
            if (hr) sb.AppendLine("                do{");
            foreach (var line in body.Split('\n'))
            {
                var x = line.TrimEnd();
                if (string.IsNullOrEmpty(x)) continue;
                if (hr) x = x.Replace("return;", "break;");
                sb.Append("                ").AppendLine(x);
            }
            if (hr) sb.AppendLine("                }while(false);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    for(int index=simd_end_;index<__startIndex+__count;++index){");
            if (hr) sb.AppendLine("    do{");
            foreach (var line in body.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var x = line.TrimEnd();
                if (hr) x = x.Replace("return;", "break;");
                sb.Append("    ").AppendLine(x);
            }
            if (hr) sb.AppendLine("    }while(false);");
            sb.AppendLine("    }");
            return sb.ToString();
        }

        private string RemainderLoop(string scalarBody)
        {
            string substituted = scalarBody;
            foreach (var kvp in _boolFields)
                substituted = Regex.Replace(substituted, $@"\b{kvp.Key}\b", kvp.Value);
            var sb = new StringBuilder();
            sb.AppendLine($"    for (int {_idx} = simd_end_; {_idx} < __startIndex + __count; ++{_idx})");
            sb.AppendLine("    {");
            bool hr = substituted.Contains("return;");
            if (hr) { sb.AppendLine("    do {"); }
            foreach (var line in substituted.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var l = line.TrimEnd();
                l = l.Replace("return;", "break;");
                sb.Append("    ").AppendLine(l);
            }
            if (hr) sb.AppendLine("    } while(false);");
            sb.AppendLine("    }");
            return sb.ToString();
        }
    }
}
