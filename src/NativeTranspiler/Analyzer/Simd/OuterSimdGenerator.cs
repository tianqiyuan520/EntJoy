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

                var varAnalyzer = new SimdVariableAnalyzer(_semanticModel, _jobStruct, _idx);
                var variables = varAnalyzer.Analyze(_methodSyntax);
                if (!variables.ContainsKey(_idx) || variables.Count == 0) return "";

                var sb = new StringBuilder();
                sb.AppendLine("    // Universal Full-SIMD (ISPC-style)");
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
                    simdMathPrecision: _simdMathPrecision,
                    batchLoopVar: "si");   // 启用连续 load/store 优化（v_i → ptr+si）

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

        private string GeneratePerLane(string scalarBody)
        {
            string body = scalarBody;

            bool hr = body.Contains("return;");
            // per-lane 回退循环固定声明 `int index`，因此实体体里出现的 index 形参名
            //   （Execute(int tid) 的 `tid`）必须一并改名为 `index` —— 否则生成 `tid` 未声明。
            if (!string.IsNullOrEmpty(_idx) && _idx != "index")
                body = Regex.Replace(body, $@"\b{Regex.Escape(_idx)}\b", "index");
            var sb = new StringBuilder();
            sb.AppendLine("    // Outer SIMD: per-lane");
            sb.AppendLine("    int simd_end_=__startIndex+((__count)/g_simdWidthInt)*g_simdWidthInt;");
            sb.AppendLine("    if(simd_end_>__startIndex){");
            sb.AppendLine("        simd_value<int> v_base=simd_value<int>::sequence(0);");
            sb.AppendLine("        for(int si=__startIndex;si<simd_end_;si+=g_simdWidthInt){");
            sb.AppendLine("            for(int __ej_lane=0;__ej_lane<g_simdWidthInt;__ej_lane++){");
            sb.AppendLine("                int index=si+__ej_lane;");
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
            var sb = new StringBuilder();
            sb.AppendLine($"    for (int {_idx} = simd_end_; {_idx} < __startIndex + __count; ++{_idx})");
            sb.AppendLine("    {");
            bool hr = substituted.Contains("return;");
            string doneLabel = $"__scalar_returned_{_idx}";
            foreach (var line in substituted.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var l = line.TrimEnd();
                // Fix: use goto to exit the j-loop and skip the default store,
                //   instead of break which only exits the k-loop (EC5/FZ4 bug:
                //   break exits k-loop → j-loop continues → default store overwrites).
                l = l.Replace("return;", $"goto {doneLabel};");
                sb.Append("    ").AppendLine(l);
            }
            if (hr)
            {
                sb.AppendLine($"    {doneLabel}: ;");
            }
            sb.AppendLine("    }");
            return sb.ToString();
        }
    }
}
