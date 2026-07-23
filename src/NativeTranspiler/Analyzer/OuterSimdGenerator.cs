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
                foreach (var kvp in _boolFields)
                    sb.AppendLine($"            bool {kvp.Key} = {kvp.Value};");

                var cfGenerator = new SimdControlFlowGenerator(
                    _semanticModel, _jobStruct, variables, varAnalyzer,
                    indexParamName: _idx, simdIndexVar: "v_i",
                    boolFields: _boolFields);
                string simdBody = cfGenerator.Generate(_methodSyntax.Body);

                foreach (var line in simdBody.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.Append("            ").AppendLine(line);

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
