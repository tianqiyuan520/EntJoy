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

        public OuterSimdGenerator(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel, string indexVarName,
            Dictionary<string, string>? boolFieldValues = null)
        {
            _methodSyntax = methodSyntax;
            _semanticModel = semanticModel;
            _idx = indexVarName;
            _boolFields = boolFieldValues ?? new Dictionary<string, string>();
        }

        public string Generate(string scalarBody)
        {
            // Check if the body is simple SoA (continuous read + arithmetic + write)
            bool isSimpleSoa = IsSimpleSoaBody();
            if (isSimpleSoa)
                return GenerateRegisterSIMD(scalarBody);
            else
                return GeneratePerLane(scalarBody);
        }

        // ----------------------------------------------------------------
        // Path 1: Register SIMD (for simple SoA jobs: pos += vel * dt)
        // ----------------------------------------------------------------
        private string GenerateRegisterSIMD(string scalarBody)
        {
            var sb = new StringBuilder();
            sb.AppendLine("    // --- Outer SIMD: register-to-register ---");
            sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine("    if (simd_end_ > __startIndex)");
            sb.AppendLine("    {");
            sb.AppendLine("        for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)");
            sb.AppendLine("        {");
            // Parse arr[i] = arr[i] op rhs — translate to load + op + store
            foreach (var line in scalarBody.Split('\n'))
            {
                var t = line.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                string translated = TranslateAssignmentLine(t);
                if (translated != null)
                    sb.Append("            ").AppendLine(translated);
                else
                    sb.Append("            ").AppendLine(t);
            }
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine(RemainderLoop(scalarBody));
            return sb.ToString();
        }

        private string? TranslateAssignmentLine(string line)
        {
            // Pattern: arr_ptr[index] = arr_ptr[index] + other_ptr[index] * dt;
            //        field_ptr[index] = field_ptr[index] + val;
            var match = Regex.Match(line,
                @"^\s*(?<arr>\w+)_ptr\[index\]\s*=\s*(?:\k<arr>_ptr\[index\]|(?<lhs>\w+)_ptr\[index\])\s*" +
                @"(?<op>[+\-*/])\s*(?:(?<rhs>\w+)_ptr\[index\]|(?<con>[a-zA-Z_]\w*))\s*;\s*$");
            if (match.Success)
            {
                string arr = match.Groups["arr"].Value;
                string op = match.Groups["op"].Value;
                string rhs = match.Groups["rhs"].Success ? match.Groups["rhs"].Value : match.Groups["con"].Value;

                // detect if rhs from group "con" is a scalar constant (dt, gravity, etc.)
                if (match.Groups["con"].Success)
                {
                    // arr[index] = arr[index] op scalar
                    return $"simd_value<float> v_{arr} = simd_value<float>::load(&{arr}_ptr[si]);\n" +
                           $"            v_{arr} = v_{arr} {op} {rhs};\n" +
                           $"            v_{arr}.store(&{arr}_ptr[si]);";
                }
                else
                {
                    // arr[index] = arr[index] op other[index]
                    return $"simd_value<float> v_{arr} = simd_value<float>::load(&{arr}_ptr[si]);\n" +
                           $"            simd_value<float> v_{rhs} = simd_value<float>::load(&{rhs}_ptr[si]);\n" +
                           $"            v_{arr} = v_{arr} {op} v_{rhs};\n" +
                           $"            v_{arr}.store(&{arr}_ptr[si]);";
                }
            }
            return null;
        }

        // ----------------------------------------------------------------
        // Path 2: Per-lane scalar (for complex bodies like ClosestPoint)
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
            foreach (var line in scalarBody.Split('\n'))
            {
                var l = line.TrimEnd();
                if (string.IsNullOrEmpty(l)) continue;
                l = l.Replace("QueryPositions_ptr[index]", "qbuf");
                sb.Append("                ").AppendLine(l);
            }
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.Append(RemainderLoop(scalarBody));
            return sb.ToString();
        }

        private string RemainderLoop(string scalarBody)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"    for (int {_idx} = simd_end_; {_idx} < __startIndex + __count; ++{_idx})");
            sb.AppendLine("    {");
            foreach (var line in scalarBody.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                sb.Append("    ").AppendLine(line.TrimEnd());
            }
            sb.AppendLine("    }");
            return sb.ToString();
        }

        // ----------------------------------------------------------------
        // Body analysis
        // ----------------------------------------------------------------
        private bool IsSimpleSoaBody()
        {
            if (_methodSyntax.Body == null) return false;
            foreach (var stmt in _methodSyntax.Body.Statements)
            {
                if (stmt is ForStatementSyntax || stmt is WhileStatementSyntax || stmt is DoStatementSyntax
                    || stmt is IfStatementSyntax)
                    return false;
            }
            return true;
        }
    }
}
