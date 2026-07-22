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
            else if (IsReductionBody())
                return GenerateReductionSIMD(scalarBody);
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
        // Path 3: Reduction inner SIMD (complex bodies like ClosestPoint)
        // Wraps query gather in SIMD, extracts each lane, then replaces
        // inner reduction loops (for i = start..end over array[i]) with
        // SIMD batch: gather 8 array elements → SIMD arithmetic → blend reduction
        // ----------------------------------------------------------------
        private string GenerateReductionSIMD(string scalarBody)
        {
            var sb = new StringBuilder();
            sb.AppendLine("    // --- Outer SIMD: reduction inner (SIMD inner loops) ---");
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

            // Transform the scalar body: replace reduction loops with SIMD versions
            string transformed = TransformReductionLoops(scalarBody);
            foreach (var line in transformed.Split('\n'))
            {
                var l = line.TrimEnd();
                if (string.IsNullOrEmpty(l)) continue;
                sb.Append("                ").AppendLine(l);
            }

            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.Append(RemainderLoop(scalarBody));
            return sb.ToString();
        }

        /// <summary>
        /// Scans C++ text for reduction for-loops (iterating over array_ptr[var])
        /// and replaces each with a SIMD batch loop + horizontal reduction + scalar remainder.
        /// </summary>
        private string TransformReductionLoops(string body)
        {
            // First apply the QueryPositions_ptr[index] -> qbuf text replacement
            string text = "";
            foreach (var line in body.Split('\n'))
            {
                var l = line.TrimEnd();
                if (string.IsNullOrEmpty(l)) continue;
                l = l.Replace("QueryPositions_ptr[index]", "qbuf");
                text += l + "\n";
            }

            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                int forIdx = text.IndexOf("for (int ", i);
                if (forIdx < 0) { sb.Append(text.Substring(i)); break; }

                sb.Append(text.Substring(i, forIdx - i));

                // Extract for-loop header: "for (int VAR = START; ...; VAR++)"
                int parenStart = text.IndexOf('(', forIdx);
                int parenEnd = FindMatchingParen(text, parenStart);
                if (parenEnd < 0) { sb.Append(text.Substring(forIdx)); break; }

                string forHeader = text.Substring(forIdx, parenEnd + 1 - forIdx);

                // Only match: for (int VAR = START; VAR < END; VAR++)   (strict <, not <=)
                var headerMatch = Regex.Match(forHeader,
                    @"for\s*\(int\s+(?<var>\w+)\s*=\s*(?<start>[^;]+);" +
                    @"\s*\k<var>\s*<\s*(?!=)(?<end>[^;]+);" +
                    @"\s*\k<var>\+\+\)$");
                if (!headerMatch.Success)
                { sb.Append(forHeader); i = parenEnd + 1; continue; }

                string var = headerMatch.Groups["var"].Value;
                string startVal = headerMatch.Groups["start"].Value.Trim();
                string endVal = headerMatch.Groups["end"].Value.Trim();

                // Find opening brace of loop body (skip whitespace)
                int bracePos = parenEnd + 1;
                while (bracePos < text.Length && (text[bracePos] == ' ' || text[bracePos] == '\t' || text[bracePos] == '\n' || text[bracePos] == '\r'))
                    bracePos++;
                if (bracePos >= text.Length || text[bracePos] != '{')
                { sb.Append(forHeader); i = parenEnd + 1; continue; }

                // Find matching closing brace
                int braceEnd = FindMatchingBrace(text, bracePos);
                if (braceEnd < 0) { sb.Append(text.Substring(forIdx)); break; }

                string loopBody = text.Substring(bracePos + 1, braceEnd - bracePos - 1);

                // Detect array reduction pattern: array_ptr[VAR] + if (VAL < BEST) { BEST = VAL; IDX = VAR; }
                string loopContent = loopBody;
                var redMatch = Regex.Match(loopContent,
                    @"(?<arr>\w+)_ptr\[" + var + @"\]" +
                    @"[\s\S]*?if\s*\((?<val>\w+)\s*<\s*(?<best>\w+)\)" +
                    @"[\s\S]*?\k<best>\s*=\s*\k<val>;[\s\S]*" +
                    @"(?<idxVar>\w+)\s*=\s*" + var + @";",
                    RegexOptions.IgnoreCase);

                if (redMatch.Success)
                {
                    string arr = redMatch.Groups["arr"].Value;
                    string val = redMatch.Groups["val"].Value;
                    string best = redMatch.Groups["best"].Value;
                    string idxVar = redMatch.Groups["idxVar"].Value;

                    // fallback: directly find the last "WORD = loopVar;" in body
                    var directMatches = Regex.Matches(loopBody, @"(\w+)\s*=\s*" + var + @"\s*;");
                    if (directMatches.Count > 0)
                        idxVar = directMatches[directMatches.Count - 1].Groups[1].Value;

                    string simdCode = GenerateSimdInnerLoop(arr, var, startVal, endVal, val, best, idxVar, loopBody);
                    sb.Append(simdCode);
                }
                else
                {
                    sb.Append(text.Substring(forIdx, braceEnd + 1 - forIdx));
                }

                i = braceEnd + 1;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Generate SIMD batch loop + horizontal reduction + scalar remainder
        /// for a single reduction loop over an AoS array.
        /// </summary>
        private static string GenerateSimdInnerLoop(string arr, string var, string start, string endVal, string val, string best, string idxVar, string originalBody)
        {
            var sb = new StringBuilder();
            // Only use SIMD when there are enough elements to amortize gather+reduction overhead
            sb.AppendLine($"    if (({endVal} - {start}) >= NSIMD_WIDTH)");
            sb.AppendLine("    {");
            sb.AppendLine("    simd_value<float> v_qx = simd_value<float>::broadcast(qbuf.x());");
            sb.AppendLine("    simd_value<float> v_qy = simd_value<float>::broadcast(qbuf.y());");
            sb.AppendLine($"    simd_value<float> v_{best} = simd_value<float>::broadcast({best});");
            sb.AppendLine($"    simd_value<int> v_{idxVar} = simd_value<int>::broadcast({idxVar});");
            sb.AppendLine($"    simd_value<int> v_idx = simd_value<int>::sequence({start});");
            sb.AppendLine($"    int i_simd_end = {endVal} - (({endVal} - {start}) % NSIMD_WIDTH);");
            sb.AppendLine($"    for (int i_si = {start}; i_si < i_simd_end; i_si += NSIMD_WIDTH)");
            sb.AppendLine("    {");
            sb.AppendLine($"        simd_value<float> v_px = simd_value<float>::gathf({arr}_ptr, v_idx.v);");
            sb.AppendLine($"        simd_value<float> v_py = simd_value<float>::gathfy({arr}_ptr, v_idx.v);");
            sb.AppendLine($"        simd_value<float> v_dx = v_qx - v_px;");
            sb.AppendLine($"        simd_value<float> v_dy = v_qy - v_py;");
            sb.AppendLine($"        simd_value<float> v_{val} = v_dx * v_dx + v_dy * v_dy;");
            sb.AppendLine($"        simd_mask mask = simd_mask{{ n_cmp_lt_ps(v_{val}.v, v_{best}.v) }};");
            sb.AppendLine($"        v_{best} = blend(v_{best}, v_{val}, mask);");
            sb.AppendLine($"        v_{idxVar} = blend(v_{idxVar}, v_idx, mask);");
            sb.AppendLine($"        v_idx = v_idx + NSIMD_WIDTH;");
            sb.AppendLine("    }");
            sb.AppendLine($"    {best} = hmin(v_{best});");
            sb.AppendLine($"    {idxVar} = hmin_idx(v_{best}, v_{idxVar});");
            // scalar remainder
            sb.AppendLine($"    for (int {var} = i_simd_end; {var} < {endVal}; {var}++)");
            sb.AppendLine("    {");
            foreach (var origLine in originalBody.Split('\n'))
                sb.AppendLine($"        {origLine}");
            sb.AppendLine("    }");
            sb.AppendLine("    }");
            sb.AppendLine("    else");
            sb.AppendLine("    {");
            sb.AppendLine($"    for (int {var} = {start}; {var} < {endVal}; {var}++)");
            sb.AppendLine("    {");
            foreach (var origLine in originalBody.Split('\n'))
                sb.AppendLine($"        {origLine}");
            sb.AppendLine("    }");
            sb.AppendLine("    }");
            return sb.ToString();
        }

        /// <summary>
        /// Find matching closing brace for an opening brace at openPos.
        /// Handles nested braces by counting depth.
        /// </summary>
        private static int FindMatchingBrace(string text, int openPos)
        {
            int depth = 0;
            for (int i = openPos; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Find matching closing parenthesis for an opening paren at openPos.
        /// </summary>
        private static int FindMatchingParen(string text, int openPos)
        {
            int depth = 0;
            for (int i = openPos; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
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

        // ----------------------------------------------------------------
        // Reduction loop detection (C# AST level)
        // ----------------------------------------------------------------
        private bool IsReductionBody()
        {
            if (_methodSyntax.Body == null) return false;
            foreach (var stmt in _methodSyntax.Body.Statements)
            {
                if (ContainsReductionForLoop(stmt))
                    return true;
            }
            return false;
        }

        private bool ContainsReductionForLoop(StatementSyntax stmt)
        {
            switch (stmt)
            {
                case ForStatementSyntax forStmt:
                    if (IsDataReductionLoop(forStmt))
                        return true;
                    // Recurse into for body to find nested loops
                    var inner = forStmt.Statement is BlockSyntax blk ? blk.Statements.ToArray() : new[] { forStmt.Statement };
                    foreach (var s in inner)
                        if (ContainsReductionForLoop(s)) return true;
                    return false;
                case IfStatementSyntax ifStmt:
                    var ifBody = ifStmt.Statement is BlockSyntax ib ? ib.Statements.ToArray() : new[] { ifStmt.Statement };
                    foreach (var s in ifBody)
                        if (ContainsReductionForLoop(s)) return true;
                    if (ifStmt.Else != null)
                    {
                        var elseBody = ifStmt.Else.Statement is BlockSyntax eb ? eb.Statements.ToArray() : new[] { ifStmt.Else.Statement };
                        foreach (var s in elseBody)
                            if (ContainsReductionForLoop(s)) return true;
                    }
                    return false;
                case BlockSyntax block:
                    foreach (var s in block.Statements)
                        if (ContainsReductionForLoop(s)) return true;
                    return false;
                default:
                    return false;
            }
        }

        private bool IsDataReductionLoop(ForStatementSyntax forStmt)
        {
            if (forStmt.Declaration == null || forStmt.Declaration.Variables.Count != 1)
                return false;
            if (forStmt.Condition == null)
                return false;
            // Check body for min/max reduction if statement
            var forBody = forStmt.Statement is BlockSyntax fb ? fb.Statements.ToArray() : new[] { forStmt.Statement };
            foreach (var s in forBody)
            {
                if (IsReductionIfStatement(s))
                    return true;
            }
            return false;
        }

        private bool IsReductionIfStatement(StatementSyntax stmt)
        {
            if (stmt is IfStatementSyntax ifStmt)
            {
                if (ifStmt.Condition is BinaryExpressionSyntax bin &&
                    (bin.IsKind(SyntaxKind.LessThanExpression) || bin.IsKind(SyntaxKind.GreaterThanExpression)))
                {
                    var trueBlock = ifStmt.Statement is BlockSyntax tb ? tb.Statements.ToArray() : new[] { ifStmt.Statement };
                    foreach (var s in trueBlock)
                    {
                        if (s is ExpressionStatementSyntax es && es.Expression is AssignmentExpressionSyntax)
                            return true;
                    }
                }
            }
            return false;
        }
    }
}
