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
                string simdBody = cfGenerator.Generate(_methodSyntax.Body);

                // ★ Merge double write → single write: remove all per-lane extract write lines
                simdBody = RemovePerLaneWrites(simdBody);

                foreach (var line in simdBody.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.Append("            ").AppendLine(line);

                // Unified write for ALL lanes (replaces the two per-lane one-liner writes)
                // Extract the RHS expression from scalar body's own write pattern, e.g.:
                //   Results[index] = HashIndex[bestIdx].y  →  HashIndex_ptr[bestIdx_lane].y()
                string? writeRHS = ExtractResultWriteRHS(scalarBody);
                if (writeRHS != null)
                {
                    sb.AppendLine("            // Unified write from v_bestIdx");
                    sb.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++) {");
                    sb.AppendLine("                int bestIdx_lane = n_extract_lane_epi32(v_bestIdx.v, lane);");
                    sb.AppendLine("                if (bestIdx_lane != -1)");
                    sb.AppendLine($"                    Results_ptr[si + lane] = {writeRHS};");
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

        /// <summary>Extract RHS from last Results_ptr[index] = EXPR; → EXPR with bestIdx→bestIdx_lane</summary>
        private static string? ExtractResultWriteRHS(string scalarBody)
        {
            int idx = scalarBody.LastIndexOf("Results_ptr[index]");
            if (idx < 0) return null;
            int eq = scalarBody.IndexOf('=', idx);
            int semi = scalarBody.IndexOf(';', eq);
            if (eq < 0 || semi < 0) return null;
            string rhs = scalarBody.Substring(eq + 1, semi - eq - 1).Trim();
            return rhs == "-1" ? null : rhs.Replace("[bestIdx]", "[bestIdx_lane]");
        }

        /// <summary>Remove all per-lane result write constructs from SIMD body</summary>
        private static string RemovePerLaneWrites(string simdBody)
        {
            // Pattern: {int __sg=n_mask_to_bitmask(...) ... }}
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
