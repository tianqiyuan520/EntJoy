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
            // EXISTING: proven paths first (register SIMD, ISPC ClosestPoint, ISPC FindWithin)
            bool isSimpleSoa = IsSimpleSoaBody();
            if (isSimpleSoa)
                return GenerateRegisterSIMD(scalarBody);
            else if (IsReductionBody())
                return GenerateReductionSIMD(scalarBody);
            else if (IsFindWithinJob())
                return GenerateISPCFindWithinSIMD(scalarBody);

            // NEW: universal full-SIMD from AST (catches jobs that previously fell to per-lane)
            if (IsFullSIMDEligible())
                return GenerateFullSIMDFromAST(scalarBody);

            // FALLBACK: per-lane scalar
            return GeneratePerLane(scalarBody);
        }

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
        // Path 3: Reduction inner SIMD (complex bodies like ClosestPoint)
        // ISPC-style: keeps 8 queries in SIMD registers throughout,
        // uses mask-managed while loop for inner reduction (no >=8 guard).
        // For non-ClosestPoint reduction bodies, falls back to per-lane.
        // ----------------------------------------------------------------
        private string GenerateReductionSIMD(string scalarBody)
        {
            // ISPC-style SIMD for ClosestPoint pattern (dx/dy neighbor loops)
            if (scalarBody.Contains("for (int dx = -1; dx <= 1; dx++)"))
                return GenerateISPCClosestPointSIMD(scalarBody);

            // Fallback: per-lane extraction + inner reduction SIMD batch
            return GenerateReductionSIMDPerLane(scalarBody);
        }

        /// <summary>
        /// Original per-lane reduction path (renamed for fallback use).
        /// Wraps query gather in SIMD, extracts each lane, then replaces
        /// inner reduction loops with SIMD batch (if >= 8 elements).
        /// </summary>
        private string GenerateReductionSIMDPerLane(string scalarBody)
        {
            var sb = new StringBuilder();
            sb.AppendLine("    // --- Outer SIMD: reduction per-lane + inner SIMD batch ---");
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
            // H4: wrap lane body in do-while(false) so return->break exits entire lane
            sb.AppendLine("                // H4: do-while-false wrapper for safe early-exit (return -> break)");
            sb.AppendLine("                do");
            sb.AppendLine("                {");
            foreach (var line in transformed.Split('\n'))
            {
                var l = line.TrimEnd();
                if (string.IsNullOrEmpty(l)) continue;
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

        /// <summary>
        /// ISPC-style SIMD for ClosestPoint pattern.
        /// Keeps 8 queries in SIMD registers for the entire function body,
        /// using mask-managed while loops instead of per-lane extraction.
        /// This matches ISPC's foreach model where each SIMD lane runs one query independently.
        ///
        /// Inner reduction loop uses while(active.any_true()) with per-lane i,
        /// avoiding the per-lane extraction + if(>=8) guard anti-pattern.
        /// Global fallback uses scalar load + broadcast (not gather) since all
        /// active lanes share the same i in that path.
        /// </summary>
        private string GenerateISPCClosestPointSIMD(string scalarBody)
        {
            var sb = new StringBuilder();

            // Check if IgnoreSelf is active (from bool field variant dispatch)
            // The scalar body retains "IgnoreSelf" as variable name (not substituted to true/false),
            // so we check _boolFields to know which variant is being generated.
            // H5: use TryGetValue("IgnoreSelf", ...) instead of .Any() to avoid false positives
            // from unrelated bool fields on future jobs
            bool ignoreSelfActive = _boolFields.TryGetValue("IgnoreSelf", out var ignoreSelfVal) && ignoreSelfVal == "true";

            sb.AppendLine("    // --- ISPC-style SIMD: 8-wide mask-managed (no per-lane extraction) ---");
            sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine("    if (simd_end_ > __startIndex)");
            sb.AppendLine("    {");
            // Hoist loop-invariant broadcasts outside the per-si loop
            sb.AppendLine("        // Hoisted loop-invariant broadcasts");
            sb.AppendLine("        simd_value<float> v_GridOrigin_x = simd_value<float>::broadcast(GridOrigin.x());");
            sb.AppendLine("        simd_value<float> v_GridOrigin_y = simd_value<float>::broadcast(GridOrigin.y());");
            if (ignoreSelfActive)
            {
                sb.AppendLine("        simd_value<float> v_sqEpsilon = simd_value<float>::broadcast(SquaredEpsilonSelf);");
            }
            sb.AppendLine();
            sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0);");
            sb.AppendLine("        for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)");
            sb.AppendLine("        {");
            sb.AppendLine("            // Gather 8 query positions (SIMD)");
            sb.AppendLine("            simd_value<int> v_i = v_base + si;");
            sb.AppendLine("            simd_value<EntJoy::Mathematics::float2> v_q =");
            sb.AppendLine("                simd_value<EntJoy::Mathematics::float2>::gather(QueryPositions_ptr, v_i);");
            sb.AppendLine();
            sb.AppendLine("            // ===== ISPC-style body (all SIMD, no per-lane extraction) =====");
            sb.AppendLine("            {");
            sb.AppendLine("                // Broadcast grid constants");
            sb.AppendLine("                simd_value<int> v_grid_dims_x = simd_value<int>::broadcast(GridDimensions.x());");
            sb.AppendLine("                simd_value<int> v_grid_dims_y = simd_value<int>::broadcast(GridDimensions.y());");
            sb.AppendLine("                simd_value<int> v_zero = simd_value<int>::broadcast(0);");
            sb.AppendLine("                simd_value<int> v_maxCellHash = v_grid_dims_x * v_grid_dims_y - 1;");
            sb.AppendLine();
            sb.AppendLine("                // Compute cell positions (SIMD: floor, convert, clamp)");
            sb.AppendLine("                simd_value<float> v_cell_fx = (v_q.x - v_GridOrigin_x) * GridResolutionInv;");
            sb.AppendLine("                v_cell_fx = v_cell_fx.floor();");
            sb.AppendLine("                simd_value<float> v_cell_fy = (v_q.y - v_GridOrigin_y) * GridResolutionInv;");
            sb.AppendLine("                v_cell_fy = v_cell_fy.floor();");
            sb.AppendLine("                simd_value<int> v_cell_x = simd_value<int>::convert(v_cell_fx);");
            sb.AppendLine("                simd_value<int> v_cell_y = simd_value<int>::convert(v_cell_fy);");
            sb.AppendLine("                v_cell_x = simd_max(v_cell_x, v_zero);");
            sb.AppendLine("                v_cell_y = simd_max(v_cell_y, v_zero);");
            sb.AppendLine("                v_cell_x = simd_min(v_cell_x, v_grid_dims_x - 1);");
            sb.AppendLine("                v_cell_y = simd_min(v_cell_y, v_grid_dims_y - 1);");
            sb.AppendLine();
            // Use std::numeric_limits<float>::max() matching the generated C++ style
            string fltMax = "std::numeric_limits<float>::max()";

            sb.AppendLine("                // Initialize best values (per-lane in SIMD regs)");
            sb.AppendLine("                simd_value<float> v_bestDistSq = simd_value<float>::broadcast(" + fltMax + ");");
            sb.AppendLine("                simd_value<int> v_bestIdx = simd_value<int>::broadcast(-1);");
            sb.AppendLine();
            sb.AppendLine("                // Results initialization: all -1");
            sb.AppendLine("                n_store_epi32(&Results_ptr[si], n_set1_epi32(-1));");
            sb.AppendLine();
            sb.AppendLine("                // ---- dx loop (SIMD mask-managed) ----");
            sb.AppendLine("                for (int dx = -1; dx <= 1; dx++)");
            sb.AppendLine("                {");
            sb.AppendLine("                    simd_value<int> v_nx = v_cell_x + dx;");
            sb.AppendLine("                    simd_mask v_nx_active{ n_cmp_ult_epi32(v_nx.v, v_grid_dims_x.v) };");
            sb.AppendLine("                    if (!v_nx_active.any_true()) continue;");
            sb.AppendLine("                    for (int dy = -1; dy <= 1; dy++)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        simd_value<int> v_ny = v_cell_y + dy;");
            sb.AppendLine("                        simd_mask v_cell_active = v_nx_active & simd_mask{ n_cmp_ult_epi32(v_ny.v, v_grid_dims_y.v) };");
            sb.AppendLine("                        if (!v_cell_active.any_true()) continue;");
            sb.AppendLine();
            sb.AppendLine("                        // Cell hash (clamped for gather safety)");
            sb.AppendLine("                        simd_value<int> v_cellHash = v_ny * v_grid_dims_x + v_nx;");
            sb.AppendLine("                        v_cellHash = simd_max(v_cellHash, v_zero);");
            sb.AppendLine("                        v_cellHash = simd_min(v_cellHash, v_maxCellHash);");
            sb.AppendLine();
            sb.AppendLine("                        // Gather CellStartEnd range (int2 per cell)");
            sb.AppendLine("                        simd_value<EntJoy::Mathematics::int2> v_range =");
            sb.AppendLine("                            simd_value<EntJoy::Mathematics::int2>::gather(CellStartEnd.Ptr, v_cellHash);");
            sb.AppendLine("                        simd_mask v_start_valid{ n_cmp_ge_epi32(v_range.x.v, v_zero.v) };");
            sb.AppendLine("                        simd_mask v_active = v_cell_active & v_start_valid;");
            sb.AppendLine("                        if (!v_active.any_true()) continue;");
            sb.AppendLine();
            sb.AppendLine("                        // ===== Inner reduction loop (ISPC-style count for) =====");
            sb.AppendLine("                        // Each lane has its own i (start..end), advances independently");
            sb.AppendLine("                        // Pre-compute max iterations = hmax(end-start) so all lanes march together");
            sb.AppendLine("                        // Finished lanes do wasted work, result masked out by blend (ISPC semantics)");
            sb.AppendLine("                        simd_value<int> v_i_red = v_range.x;");
            sb.AppendLine("                        v_i_red = simd_max(v_i_red, v_zero);");
            sb.AppendLine("                        simd_value<int> v_end = v_range.y;");
            sb.AppendLine("                        simd_value<int> v_maxIter = v_end - v_i_red;");
            sb.AppendLine("                        int maxIter = hmax(v_maxIter);");
            sb.AppendLine("                        simd_value<int> v_sortedLast = simd_value<int>::broadcast(SortedLength - 1);");
            sb.AppendLine("                        #pragma loop(ivdep)");
            sb.AppendLine("                        for (int iter = 0; iter < maxIter; iter++)");
            sb.AppendLine("                        {");
            sb.AppendLine("                            // Per-iteration mask: lanes with i < end stay active");
            sb.AppendLine("                            simd_mask v_mask{ n_cmp_lt_epi32(v_i_red.v, v_end.v) };");
            sb.AppendLine("                            // Clamp i to safe bounds for gather (finished lanes read 0, blend discards)");
            sb.AppendLine("                            simd_value<int> v_safe_i = simd_min(v_i_red, v_sortedLast);");
            sb.AppendLine("                            simd_value<float> v_px = simd_value<float>::gathf(SortedPositions_ptr, v_safe_i.v);");
            sb.AppendLine("                            simd_value<float> v_py = simd_value<float>::gathfy(SortedPositions_ptr, v_safe_i.v);");
            sb.AppendLine();
            sb.AppendLine("                            // 8-wide distance squared: (qx-px)^2 + (qy-py)^2");
            sb.AppendLine("                            simd_value<float> v_dx = v_q.x - v_px;");
            sb.AppendLine("                            simd_value<float> v_dy = v_q.y - v_py;");
            sb.AppendLine("                            simd_value<float> v_distSq = v_dx * v_dx + v_dy * v_dy;");
            sb.AppendLine();
            sb.AppendLine("                            // Masked blend: update if distSq < bestDistSq AND lane active");
            sb.AppendLine("                            simd_mask v_improve{ n_cmp_lt_ps(v_distSq.v, v_bestDistSq.v) };");
            sb.AppendLine("                            v_improve = v_improve & v_mask;");

            if (ignoreSelfActive)
            {
                sb.AppendLine("                            // Self-point exclusion (IgnoreSelf=true)");
                sb.AppendLine("                            simd_mask v_not_self{ n_not_mask(n_cmp_lt_ps(v_distSq.v, v_sqEpsilon.v)) };");
                sb.AppendLine("                            v_improve = v_improve & v_not_self;");
            }

            sb.AppendLine("                            v_bestDistSq = blend(v_bestDistSq, v_distSq, v_improve);");
            sb.AppendLine("                            v_bestIdx = blend(v_bestIdx, v_i_red, v_improve);");
            sb.AppendLine();
            sb.AppendLine("                            // Advance i (finished lanes keep going, blend discards)");
            sb.AppendLine("                            v_i_red = v_i_red + 1;");
            sb.AppendLine("                        }");
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
            sb.AppendLine();
            sb.AppendLine("                // ---- Global fallback (lanes where bestIdx is still -1) ----");
            sb.AppendLine("                simd_mask v_need_fallback{ n_cmp_eq_epi32(v_bestIdx.v, n_set1_epi32(-1)) };");
            sb.AppendLine("                if (v_need_fallback.any_true())");
            sb.AppendLine("                {");
            sb.AppendLine("                    // Scalar load + broadcast (all active lanes share same index i_fb)");
            sb.AppendLine("                    simd_value<float> v_fb_bestDistSq = v_bestDistSq;");
            sb.AppendLine("                    simd_value<int> v_fb_bestIdx = v_bestIdx;");
            sb.AppendLine("                    simd_mask v_fb_active = v_need_fallback;");
            sb.AppendLine("                    int sortedLen = SortedLength;");
            sb.AppendLine("                    for (int i_fb = 0; i_fb < sortedLen; i_fb++)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        if (!v_fb_active.any_true()) break;");
            sb.AppendLine("                        // Scalar load once, broadcast to all lanes");
            sb.AppendLine("                        EntJoy::Mathematics::float2 fb_pos = SortedPositions_ptr[i_fb];");
            sb.AppendLine("                        simd_value<float> v_fb_px = simd_value<float>::broadcast(fb_pos.x());");
            sb.AppendLine("                        simd_value<float> v_fb_py = simd_value<float>::broadcast(fb_pos.y());");
            sb.AppendLine("                        simd_value<float> v_fb_dx = v_q.x - v_fb_px;");
            sb.AppendLine("                        simd_value<float> v_fb_dy = v_q.y - v_fb_py;");
            sb.AppendLine("                        simd_value<float> v_fb_distSq = v_fb_dx * v_fb_dx + v_fb_dy * v_fb_dy;");
            sb.AppendLine("                        simd_mask v_fb_improve{ n_cmp_lt_ps(v_fb_distSq.v, v_fb_bestDistSq.v) };");
            sb.AppendLine("                        v_fb_improve = v_fb_improve & v_fb_active;");

            if (ignoreSelfActive)
            {
                sb.AppendLine("                        simd_mask v_fb_not_self{ n_not_mask(n_cmp_lt_ps(v_fb_distSq.v, v_sqEpsilon.v)) };");
                sb.AppendLine("                        v_fb_improve = v_fb_improve & v_fb_not_self;");
            }

            sb.AppendLine("                        v_fb_bestDistSq = blend(v_fb_bestDistSq, v_fb_distSq, v_fb_improve);");
            sb.AppendLine("                        v_fb_bestIdx = blend(v_fb_bestIdx, simd_value<int>::broadcast(i_fb), v_fb_improve);");
            sb.AppendLine("                    }");
            sb.AppendLine("                    // Merge fallback results into main SIMD registers");
            sb.AppendLine("                    v_bestDistSq = blend(v_bestDistSq, v_fb_bestDistSq, v_need_fallback);");
            sb.AppendLine("                    v_bestIdx = blend(v_bestIdx, v_fb_bestIdx, v_need_fallback);");
            sb.AppendLine("                }");
            sb.AppendLine();
            sb.AppendLine("                // ---- Write results: HashIndex_ptr[bestIdx].y for found lanes ----");
            sb.AppendLine("                // Per-lane scalar write (safe: unmasked gather with -1 indices would AV)");
            sb.AppendLine("                for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
            sb.AppendLine("                {");
            sb.AppendLine("                    int bestIdx_lane = n_extract_lane_epi32(v_bestIdx.v, lane);");
            sb.AppendLine("                    if (bestIdx_lane != -1)");
            sb.AppendLine("                        Results_ptr[si + lane] = HashIndex_ptr[bestIdx_lane].y();");
            sb.AppendLine("                }");
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
        /// ISPC-style SIMD for FindWithin pattern.
        /// SIMD cell compute (8-wide floor/convert/clamp), then per-lane scalar inner scan.
        /// The write pattern (list construction) doesn't SIMDize well, so per-lane extraction
        /// is used for the inner loops. Main benefit: SIMD cell compute + broadcast reuse.
        /// </summary>
        private string GenerateISPCFindWithinSIMD(string scalarBody)
        {
            var sb = new StringBuilder();
            sb.AppendLine("    // --- ISPC-style SIMD: FindWithin (SIMD cell compute + per-lane scan) ---");
            sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine("    if (simd_end_ > __startIndex)");
            sb.AppendLine("    {");
            // Hoisted loop-invariant broadcasts
            sb.AppendLine("        // Hoisted loop-invariant broadcasts");
            sb.AppendLine("        simd_value<float> v_GridOrigin_x = simd_value<float>::broadcast(GridOrigin.x());");
            sb.AppendLine("        simd_value<float> v_GridOrigin_y = simd_value<float>::broadcast(GridOrigin.y());");
            sb.AppendLine();
            sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0);");
            sb.AppendLine("        for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)");
            sb.AppendLine("        {");
            sb.AppendLine("            // Gather 8 query positions (SIMD)");
            sb.AppendLine("            simd_value<int> v_i = v_base + si;");
            sb.AppendLine("            simd_value<EntJoy::Mathematics::float2> v_q =");
            sb.AppendLine("                simd_value<EntJoy::Mathematics::float2>::gather(QueryPositions_ptr, v_i);");
            sb.AppendLine();
            sb.AppendLine("            // SIMD cell compute (floor, convert, clamp)");
            sb.AppendLine("            simd_value<int> v_grid_dims_x = simd_value<int>::broadcast(GridDimensions.x());");
            sb.AppendLine("            simd_value<int> v_grid_dims_y = simd_value<int>::broadcast(GridDimensions.y());");
            sb.AppendLine("            simd_value<int> v_zero = simd_value<int>::broadcast(0);");
            sb.AppendLine("            simd_value<float> v_cell_fx = (v_q.x - v_GridOrigin_x) * GridResolutionInv;");
            sb.AppendLine("            v_cell_fx = v_cell_fx.floor();");
            sb.AppendLine("            simd_value<float> v_cell_fy = (v_q.y - v_GridOrigin_y) * GridResolutionInv;");
            sb.AppendLine("            v_cell_fy = v_cell_fy.floor();");
            sb.AppendLine("            simd_value<int> v_cell_x = simd_value<int>::convert(v_cell_fx);");
            sb.AppendLine("            simd_value<int> v_cell_y = simd_value<int>::convert(v_cell_fy);");
            sb.AppendLine("            v_cell_x = simd_max(v_cell_x, v_zero);");
            sb.AppendLine("            v_cell_y = simd_max(v_cell_y, v_zero);");
            sb.AppendLine("            v_cell_x = simd_min(v_cell_x, v_grid_dims_x - 1);");
            sb.AppendLine("            v_cell_y = simd_min(v_cell_y, v_grid_dims_y - 1);");
            sb.AppendLine();
            sb.AppendLine("            // Per-lane: extract cell + q, then scalar inner scan");
            sb.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
            sb.AppendLine("            {");
            sb.AppendLine("                int index = si + lane;");
            sb.AppendLine("                int cell_x = n_extract_lane_epi32(v_cell_x.v, lane);");
            sb.AppendLine("                int cell_y = n_extract_lane_epi32(v_cell_y.v, lane);");
            sb.AppendLine("                EntJoy::Mathematics::float2 qbuf;");
            sb.AppendLine("                qbuf.x() = n_extract_lane_f32(v_q.x.v, lane);");
            sb.AppendLine("                qbuf.y() = n_extract_lane_f32(v_q.y.v, lane);");
            sb.AppendLine("                // Inject SIMD-computed cell (avoids redundant floor/convert/clamp per lane)");
            sb.AppendLine("                EntJoy::Mathematics::int2 centerCell = EntJoy::Mathematics::int2(cell_x, cell_y);");

            // Modify scalar body: replace QueryPositions_ptr[index] with qbuf,
            // remove the two centerCell compute lines, guard writes,
            // and exit the lane on found==MaxNeighbor (not whole function)
            string modifiedBody = scalarBody.Replace("QueryPositions_ptr[index]", "qbuf");
            // Remove auto-generated int2 centerCell compute lines
            var bodyLines = new List<string>();
            foreach (var line in modifiedBody.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Contains("int2 centerCell = ((EntJoy::Mathematics::int2)") ||
                    trimmed.StartsWith("centerCell = EntJoy::Mathematics::clamp"))
                    continue;
                bodyLines.Add(line);
            }
            modifiedBody = string.Join("\n", bodyLines);
            // Guard result writes against out-of-bounds after MaxNeighbor reached
            modifiedBody = modifiedBody.Replace(
                "Results_ptr[baseIdx + found] = HashIndex_ptr[iCell].y();",
                "if (found < MaxNeighbor) { Results_ptr[baseIdx + found] = HashIndex_ptr[iCell].y(); }");
            // goto _simd_exit replaces return to exit nested loops directly (standard C++ pattern)
            sb.AppendLine("                // goto-exit from nested loops (C++ standard pattern)");
            sb.AppendLine("                {");

            foreach (var line in modifiedBody.Split('\n'))
            {
                var l = line.TrimEnd();
                if (string.IsNullOrEmpty(l)) continue;
                l = l.Replace("return;", "goto _simd_exit;");
                sb.Append("                    ").AppendLine(l);
            }

            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            _simd_exit: ;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            // For remainder loop, apply only the write guard, not the qbuf replacement
            string remainderBody = scalarBody.Replace(
                "Results_ptr[baseIdx + found] = HashIndex_ptr[iCell].y();",
                "if (found < MaxNeighbor) { Results_ptr[baseIdx + found] = HashIndex_ptr[iCell].y(); }");
            sb.Append(RemainderLoop(remainderBody));
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

        // ----------------------------------------------------------------
        // FindWithin detection (via AST, not string matching)
        // ----------------------------------------------------------------
        private bool IsFindWithinJob()
        {
            // Check whether the containing struct type has a field named "CellsToLoop".
            // Only FindWithinJobPointer has this field — much more robust than string matching
            // on generated C++ code (M4).
            var containingType = _methodSyntax.Ancestors().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>().FirstOrDefault();
            if (containingType == null) return false;
            var typeSymbol = _semanticModel.GetDeclaredSymbol(containingType) as INamedTypeSymbol;
            if (typeSymbol == null) return false;
            return typeSymbol.GetMembers("CellsToLoop").Any();
        }

        // ================================================================
        // NEW: Universal Full-SIMD Path (ISPC-style from AST)
        // ================================================================

        /// <summary>
        /// 检查 Execute 体是否适合通用全 SIMD 生成。
        /// 使用宽松检查——SimdControlFlowGenerator 支持所有控制流：
        /// if/else/for/while/do/break/continue/return。
        /// 只拒绝真正不支持的模式（间接索引、switch、foreach）。
        /// </summary>
        private bool IsFullSIMDEligible()
        {
            if (_methodSyntax.Body == null) return false;

            // 只拒绝完全 AST 不支持的模式
            if (HasUnsupportedStatement(_methodSyntax.Body))
                return false;

            // SimdVariableAnalyzer 检查（新）
            var jobStruct = GetJobStruct();
            if (jobStruct == null) return false;

            try
            {
                var varAnalyzer = new SimdVariableAnalyzer(_semanticModel, jobStruct, _idx);
                var variables = varAnalyzer.Analyze(_methodSyntax);
                // 必须至少成功分类 index 参数
                return variables.ContainsKey(_idx) && variables.Count > 0;
            }
            catch
            {
                return false;
            }
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
        /// 获取 Execute 方法所在的 Job struct 类型符号。
        /// </summary>
        private INamedTypeSymbol? GetJobStruct()
        {
            var containingType = _methodSyntax.Ancestors()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault();
            if (containingType == null) return null;
            return _semanticModel.GetDeclaredSymbol(containingType) as INamedTypeSymbol;
        }

        /// <summary>
        /// 生成通用全 SIMD 代码。
        /// 使用 SimdVariableAnalyzer + SimdControlFlowGenerator 从 AST 直接生成。
        /// </summary>
        private string GenerateFullSIMDFromAST(string scalarBody)
        {
            var sb = new StringBuilder();
            var jobStruct = GetJobStruct();
            if (jobStruct == null || _methodSyntax.Body == null)
                return "";

            // 1. 变量分析
            var varAnalyzer = new SimdVariableAnalyzer(_semanticModel, jobStruct, _idx);
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
                _semanticModel, jobStruct, variables, varAnalyzer,
                indexParamName: _idx, simdIndexVar: "v_i");
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
