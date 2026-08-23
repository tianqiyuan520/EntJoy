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
    public class OuterSimdGenerator
    {
        private readonly MethodDeclarationSyntax _methodSyntax;
        private readonly SemanticModel _semanticModel;
        private readonly string _idx;
        private readonly Dictionary<string, string> _boolFields;
        private readonly INamedTypeSymbol? _jobStruct;
        private readonly NativeTranspiler.SimdMathPrecision _simdMathPrecision;

        public OuterSimdGenerator(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel, string indexVarName,
            Dictionary<string, string>? boolFieldValues = null,
            INamedTypeSymbol? jobStruct = null,
            NativeTranspiler.SimdMathPrecision simdMathPrecision = NativeTranspiler.SimdMathPrecision.Fastest)
        {
            _methodSyntax = methodSyntax;
            _semanticModel = semanticModel;
            _idx = indexVarName;
            _boolFields = boolFieldValues ?? new Dictionary<string, string>();
            _jobStruct = jobStruct;
            _simdMathPrecision = simdMathPrecision;
        }

        public string Generate(string scalarBody)
        {
            string simdResult = GenerateFullSIMDFromAST(scalarBody);
            if (!string.IsNullOrEmpty(simdResult))
                return simdResult;
            return GeneratePerLane(scalarBody);
        }

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
                sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / g_simdWidthInt) * g_simdWidthInt;");
                sb.AppendLine("    if (simd_end_ > __startIndex)");
                sb.AppendLine("    {");
                sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0, g_simdWidthInt);");
                sb.AppendLine("        for (int si = __startIndex; si < simd_end_; si += g_simdWidthInt)");
                sb.AppendLine("        {");
                sb.AppendLine("            simd_value<int> v_i = v_base + si;");

                var cfGenerator = new SimdControlFlowGenerator(
                    _semanticModel, _jobStruct, variables, varAnalyzer,
                    indexParamName: _idx, simdIndexVar: "v_i",
                    boolFields: _boolFields,
                    simdMathPrecision: _simdMathPrecision);

                var writePattern = ExtractResultWritePattern(scalarBody);
                if (writePattern != null)
                {
                    cfGenerator._sentinelVar = writePattern.IndexVar;
                    cfGenerator._sentinelVal = writePattern.Sentinel;
                }

                string simdBody = cfGenerator.Generate(_methodSyntax.Body);
                // ★ RemovePerLaneWrites only applies to the sentinel "unified write" pattern
                //   (ExtractResultWritePattern). It strips per-lane scatters that are replaced
                //   by the unified write loop. For plain conditionals with narrowed masks the
                //   masked per-lane scatter is REQUIRED and must be kept — stripping it empties
                //   branch bodies and leaves dangling __cond_N references.
                if (writePattern != null)
                    simdBody = RemovePerLaneWrites(simdBody);
                simdBody = CleanupDeadIfBodies(simdBody);

                foreach (var line in simdBody.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.Append("            ").AppendLine(line);

                if (writePattern != null)
                {
                    sb.AppendLine("            // Unified write");
                    sb.AppendLine("            for (int lane = 0; lane < g_simdWidthInt; lane++) {");
                    sb.AppendLine($"                int {writePattern.IndexVar}_lane = n_extract_lane_epi32(v_{writePattern.IndexVar}.v, lane);");
                    sb.AppendLine($"                if ({writePattern.IndexVar}_lane != {writePattern.Sentinel})");
                    sb.AppendLine($"                    {writePattern.WriteExpr};");
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

        /// <summary>清理 RemovePerLaneWrites 留下的死 __cond_N/__cm_N + { ; } 块</summary>
        private static string CleanupDeadIfBodies(string simdBody)
        {
            // 模式: __cond_N = expr; \n __cm_N = expr; \n { \n ; \n } → 全部移除
            simdBody = Regex.Replace(simdBody,
                @"simd_mask\s+__cond_\d+\s*=\s*[^;]+;\s*\n\s*simd_mask\s+__cm_\d+\s*=\s*[^;]+;\s*\n\s*\{\s*\n\s*;\s*\n\s*\}[^\n]*",
                "", RegexOptions.Multiline);
            return simdBody;
        }

        private class WritePattern
        {
            public string WriteExpr;
            public string IndexVar;
            public string Sentinel;
        }

        private static WritePattern? ExtractResultWritePattern(string scalarBody)
        {
            int idx = scalarBody.LastIndexOf("_ptr[index]");
            if (idx < 0) return null;

            int eq = scalarBody.IndexOf('=', idx);
            int semi = scalarBody.IndexOf(';', eq);
            if (eq < 0 || semi < 0) return null;

            string rhs = scalarBody.Substring(eq + 1, semi - eq - 1).Trim();
            if (rhs == "-1") return null;

            var bracketMatch = Regex.Match(rhs, @"\[(\w+)\]");
            if (!bracketMatch.Success) return null;

            string indexVar = bracketMatch.Groups[1].Value;
            var sentinelMatch = Regex.Match(scalarBody, $@"\b{Regex.Escape(indexVar)}\s*!=\s*(-?\d+)");
            string sentinel = sentinelMatch.Success ? sentinelMatch.Groups[1].Value : "-1";

            string writeRHS = rhs.Replace($"[{indexVar}]", $"[{indexVar}_lane]");
            int ptrIdx = scalarBody.LastIndexOf("_ptr[index]", idx);
            int lhsStart = scalarBody.LastIndexOfAny(" \n\r;{".ToCharArray(), ptrIdx) + 1;
            string lhsArray = scalarBody.Substring(lhsStart, ptrIdx - lhsStart);
            string writeExpr = $"{lhsArray}_ptr[si + lane] = {writeRHS}";

            return new WritePattern { WriteExpr = writeExpr, IndexVar = indexVar, Sentinel = sentinel };
        }

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
            sb.AppendLine("    int simd_end_=__startIndex+((__count)/g_simdWidthInt)*g_simdWidthInt;");
            sb.AppendLine("    if(simd_end_>__startIndex){");
            sb.AppendLine("        simd_value<int> v_base=simd_value<int>::sequence(0);");
            sb.AppendLine("        for(int si=__startIndex;si<simd_end_;si+=g_simdWidthInt){");
            sb.AppendLine("            for(int lane=0;lane<g_simdWidthInt;lane++){");
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
