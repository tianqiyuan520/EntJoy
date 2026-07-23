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
            // Per-lane scalar (full SIMD path has pre-existing bugs with nested loops)
            return GeneratePerLane(scalarBody);
        }

        // ----------------------------------------------------------------
        // Full SIMD from AST (via SimdControlFlowGenerator)
        // ----------------------------------------------------------------
        private string GenerateFullSIMDFromAST(string scalarBody)
        {
            if (_jobStruct == null || _methodSyntax.Body == null)
                return "";
            try
            {
                var varAnalyzer = new SimdVariableAnalyzer(_semanticModel, _jobStruct, _idx);
                var variables = varAnalyzer.Analyze(_methodSyntax);
                if (!variables.ContainsKey(_idx) || variables.Count == 0)
                    return "";

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
            catch
            {
                return "";
            }
        }

        // ----------------------------------------------------------------
        // Per-lane scalar fallback
        // ----------------------------------------------------------------
        private string GeneratePerLane(string scalarBody)
        {
            // Substitute bool field constants for MSVC dead-branch elimination
            string body = scalarBody;
            foreach (var kvp in _boolFields)
                body = Regex.Replace(body, $@"\b{kvp.Key}\b", kvp.Value);

            bool bodyHasReturn = body.Contains("return;");

            var sb = new StringBuilder();
            sb.AppendLine("    // --- Outer SIMD: per-lane ---");
            sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine("    if (simd_end_ > __startIndex)");
            sb.AppendLine("    {");
            sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0);");
            sb.AppendLine("        for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)");
            sb.AppendLine("        {");
            sb.AppendLine("            simd_value<int> v_i = v_base + si;");
            sb.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
            sb.AppendLine("            {");
            sb.AppendLine($"                int {_idx} = si + lane;");

            if (bodyHasReturn)
            {
                sb.AppendLine("                do");
                sb.AppendLine("                {");
            }

            foreach (var line in body.Split('\n'))
            {
                var l = line.TrimEnd();
                if (string.IsNullOrEmpty(l)) continue;
                if (bodyHasReturn)
                    l = l.Replace("return;", "break;");
                sb.Append("                    ").AppendLine(l);
            }

            if (bodyHasReturn)
                sb.AppendLine("                } while(false);");

            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            // Remainder loop
            sb.AppendLine($"    for (int {_idx} = simd_end_; {_idx} < __startIndex + __count; ++{_idx})");
            sb.AppendLine("    {");
            if (bodyHasReturn)
            {
                sb.AppendLine("    do");
                sb.AppendLine("    {");
            }
            foreach (var line in body.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var l = line.TrimEnd();
                if (bodyHasReturn)
                    l = l.Replace("return;", "break;");
                sb.Append("    ").AppendLine(l);
            }
            if (bodyHasReturn)
                sb.AppendLine("    } while(false);");
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
                sb.AppendLine("    } while(false);");
            sb.AppendLine("    }");
            return sb.ToString();
        }
    }
}
