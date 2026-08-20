using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 本 partial 文件负责 SIMD 循环/控制流生成：
    /// for / while / do-while / unroll / reduction 等循环产生方法。
    /// 与 SimdControlFlowGenerator 主文件和 SimdExpressionTranslator
    /// 属于同一个 partial class，可自由互相调用。
    /// </summary>
    public partial class SimdControlFlowGenerator
    {
        // ================================================================
        // ForStatement → for(iter) count-loop
        // ================================================================

        private void GenerateForStatement(ForStatementSyntax stmt)
        {
            // For now, only handle: for (int i = start; i < end; i++)
            if (stmt.Declaration == null || stmt.Declaration.Variables.Count != 1)
            {
                AppendLine("// Unsupported SIMD for-loop pattern (non-standard declaration)");
                return;
            }

            var decl = stmt.Declaration.Variables[0];
            string ivName = decl.Identifier.Text;

            // Ensure ivName is tracked as varying in _variables
            if (!_variables.ContainsKey(ivName))
            {
                _variables[ivName] = new SimdVariableInfo
                {
                    Name = ivName,
                    Kind = VarKind.Varying,
                    CppType = "int"
                };
            }
            else
            {
                _variables[ivName].Kind = VarKind.Varying;
                _variables[ivName].CppType = "int";
            }

            // Determine start and end expressions
            string startExpr = "0";
            if (decl.Initializer != null)
                startExpr = TranslateExpression(decl.Initializer.Value);

            string endExpr = "simd_value<int>::broadcast(0)";
            if (stmt.Condition is BinaryExpressionSyntax cond)
            {
                endExpr = TranslateExpression(cond.Right);
            }

            // Check if the loop is a simple (i++) increment pattern
            bool isSimpleIncrement = stmt.Incrementors.Count == 1;
            if (!isSimpleIncrement)
            {
                AppendLine("// Unsupported SIMD for-loop increment pattern");
                return;
            }

            // Classify bounds + reduction
            bool isUniformBounds = true;
            if (decl.Initializer != null)
                isUniformBounds = isUniformBounds && _varAnalyzer.ClassifyExpression(decl.Initializer.Value) < VarKind.Varying;
            if (stmt.Condition is BinaryExpressionSyntax condBounds)
                isUniformBounds = isUniformBounds && _varAnalyzer.ClassifyExpression(condBounds.Right) < VarKind.Varying;

            bool isReduction = IsReductionLoop(stmt);

            // Dispatch: uniform → scalar for, varying reduction → count-loop, other → while-true
            if (isUniformBounds && isReduction)
                GenerateUniformReductionLoop(ivName, startExpr, endExpr, stmt);
            else if (!isUniformBounds && isReduction)
                GenerateVaryingReductionLoop(ivName, startExpr, endExpr, stmt);
            else if (isUniformBounds && !isReduction)
            {
                // Detect small-constant uniform-bound loops for unrolling.
                // Parse start/end as integer constants: "0" .. "16", "start" .. "endValue", etc.
                int unrollStart = 0, unrollEnd = 0, unrollCount = 0;
                bool canUnroll = int.TryParse(startExpr, out unrollStart)
                    && int.TryParse(endExpr, out unrollEnd)
                    && unrollEnd > unrollStart
                    && (unrollCount = unrollEnd - unrollStart) <= 64;

                if (canUnroll)
                {
                    // ★ Full unroll for small uniform-bound loops (HeavyMove 16-iteration sin/cos).
                    //   Eliminates loop-carried dependencies so MSVC can globally schedule
                    //   the entire computation chain across all SIMD iterations.
                    GenerateUnrolledLoop(ivName, unrollStart, unrollCount, stmt);
                }
                else
                {
                    // Docs-style: scalar for() with mask-narrowed body (dx/dy loops)
                    //   for(int dx) {
                    //     v_active = cmp_ult(v_nx, dims);
                    //     if(!v_active.any_true()) continue;
                    //     // body uses v_active as mask
                    //   }
                    // continue emits real C++ continue; (skip iteration when ALL lanes done)
                    string csOpStr2 = stmt.Condition is BinaryExpressionSyntax cbin2
                        && cbin2.IsKind(SyntaxKind.LessThanOrEqualExpression) ? "<=" : "<";
                    AppendLine($"// Docs-style scalar for: for (int {ivName} = {startExpr}; {ivName} {csOpStr2} {endExpr}; {ivName}++)");
                    AppendLine($"simd_value<int> simd_{ivName};");
                    // simd_end_{ivName} not needed — uniform loops never compare against end in SIMD
                    string preLoopMask2 = _currentMask;
                    AppendLine($"for (int {ivName} = {startExpr}; {ivName} {csOpStr2} {endExpr}; {ivName}++)");
                    AppendLine("{");
                    _indent++;
                    AppendLine($"simd_{ivName} = simd_value<int>::broadcast({ivName});");
                    // Loop frame for continue (real C++ continue;)
                    string exitL = $"__uni_exit_{_labelCounter++}";
                    string contL = $"__uni_cont_{_labelCounter++}";
                    _isUniformScalarLoop = true;
                    _loopStack.Push(new LoopFrame { TrackerVar = "", IterActiveVar = "", ExitLabel = exitL, ContinueLabel = contL });
                    var bodyB = stmt.Statement is BlockSyntax fb2 ? fb2 : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(stmt.Statement);
                    GenerateBlock(bodyB, skipBraces: false);
                    _loopStack.Pop();
                    _isUniformScalarLoop = false;
                    if (_gotoTargets.Contains(contL))
                    AppendLine($"{contL}: ;");
                    _indent--;
                    AppendLine("}");
                    _currentMask = preLoopMask2;
                    if (_gotoTargets.Contains(exitL))
                    AppendLine($"{exitL}: ;");
                }
            }
            else
                GenerateStandardSIMDLoop(ivName, startExpr, endExpr, stmt, false);
        }

        // ================================================================
        // Strategy 0: Full unroll for small uniform-bound non-reduction loops.
        //   Eliminates loop-carried dependencies so MSVC can globally schedule
        //   the entire computation chain (like ISPC's LLVM PHI-node approach).
        //   Only used when iteration count ≤ 64 (HeavyMove: 16, typical inner loops).
        // ================================================================
        private void GenerateUnrolledLoop(string ivName, int start, int count, ForStatementSyntax stmt)
        {
            string exitLabel = $"__unr_exit_{_labelCounter++}";
            AppendLine($"// Unrolled loop: {count} iterations (eliminates loop-carried dependencies)");
            AppendLine($"simd_value<int> simd_{ivName};");
            string savedMask = _currentMask;

            var bodyBlock = stmt.Statement is BlockSyntax bs
                ? bs
                : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(stmt.Statement);

            // Pre-scan: does the body contain break/continue?
            bool hasBreak = bodyBlock.DescendantNodes().OfType<BreakStatementSyntax>().Any();
            bool hasContinue = bodyBlock.DescendantNodes().OfType<ContinueStatementSyntax>().Any();

            for (int i = start; i < count; i++)
            {
                string contLabel = $"__unr_cont_{_labelCounter}_{i}";

                // Reset mask for each iteration
                _currentMask = savedMask;

                // Broadcast the iteration value
                AppendLine($"simd_{ivName} = simd_value<int>::broadcast({i});");

                // Body scope: isolate local variables per iteration
                AppendLine("{");
                _indent++;

                if (hasBreak || hasContinue)
                {
                    // Push a pseudo-loop frame so break/continue generate valid gotos
                    _loopStack.Push(new LoopFrame
                    {
                        TrackerVar = "",
                        IterActiveVar = "",
                        ExitLabel = exitLabel,
                        ContinueLabel = contLabel
                    });
                }
                _isUniformScalarLoop = true;

                GenerateBlock(bodyBlock, skipBraces: false);

                _isUniformScalarLoop = false;
                if (hasBreak || hasContinue)
                    _loopStack.Pop();

                _indent--;
                AppendLine("}");

                // Continue target: next iteration starts here
                if (hasContinue)
                {
                    if (_gotoTargets.Contains(contLabel))
                        AppendLine($"{contLabel}: ;");
                }
            }

            _currentMask = savedMask;
            if (hasBreak)
                AppendLine($"{exitLabel}: ;");
        }

        // ================================================================
        // Strategy 1: Uniform-bound reduction → scalar for + SIMD broadcast
        //   scalar load + broadcast → 8-wide SIMD op + blend
        //   Mask scope fix: save _currentMask as named var BEFORE the for
        // ================================================================
        private void GenerateUniformReductionLoop(string ivName, string startExpr, string endExpr, ForStatementSyntax stmt)
        {
            _uniformLoopVars.Add(ivName);

            string endSuffix = $"_{_maskCounter++}";
            string exitLabel = $"__uni_exit_{_labelCounter++}";
            string continueLabel = $"__uni_cont_{_labelCounter++}";

            // ★ Mask scope fix: save current mask as a named variable OUTSIDE the for scope
            string savedMask = $"__saved_{_maskCounter++}";
            AppendLine($"simd_mask {savedMask} = {_currentMask};");

            string csOpStr = stmt.Condition is BinaryExpressionSyntax condCmp
                && condCmp.IsKind(SyntaxKind.LessThanOrEqualExpression) ? "<=" : "<";

            // ★ Pre-scan for SIMD loop-invariant hoisting: detect v_i * N patterns
            var bodyBlock = stmt.Statement is BlockSyntax bs
                ? bs
                : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(stmt.Statement);
            var hoistVars = new List<(string name, string expr)>();
            foreach (var bin in bodyBlock.DescendantNodes().OfType<BinaryExpressionSyntax>())
            {
                if (bin.OperatorToken.Text == "*")
                {
                    string constVal = null;
                    if (bin.Left is IdentifierNameSyntax hid && hid.Identifier.Text == _indexParamName
                        && bin.Right is LiteralExpressionSyntax lit)
                        constVal = lit.Token.Text;
                    else if (bin.Right is IdentifierNameSyntax hid2 && hid2.Identifier.Text == _indexParamName
                        && bin.Left is LiteralExpressionSyntax lit2)
                        constVal = lit2.Token.Text;
                    if (constVal != null)
                    {
                        string hn = $"__hoist_{_maskCounter++}";
                        hoistVars.Add((hn, $"{_simdIndexVar} * {constVal}"));
                    }
                }
            }
            // Emit hoisted variables BEFORE the loop
            foreach (var (hn, hexpr) in hoistVars)
                AppendLine($"simd_value<int> {hn} = {hexpr};");

            AppendLine($"// Uniform-bound reduction: scalar for + broadcast SIMD");
            AppendLine($"int {ivName}_end{endSuffix} = {endExpr};");
            AppendLine($"for (int {ivName} = {startExpr}; {ivName} < {ivName}_end{endSuffix}; {ivName}++)");
            AppendLine("{");
            _indent++;
            // simd_i for blend targets (bestIdx = simd_i)
            AppendLine($"simd_value<int> simd_{ivName} = simd_value<int>::broadcast({ivName});");

            // Within the loop body, use savedMask (it's in scope)
            string outerMask = _currentMask;
            _currentMask = savedMask;

            _loopStack.Push(new LoopFrame
            {
                TrackerVar = "",
                IterActiveVar = "",
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel
            });

            int hoistStart = _builder.Length;
            GenerateBlock(bodyBlock, skipBraces: false);

            // Replace v_i * N patterns in the generated body with hoisted variables
            if (hoistVars.Count > 0)
            {
                string bodyText = _builder.ToString(hoistStart, _builder.Length - hoistStart);
                foreach (var (hn, hexpr) in hoistVars)
                {
                    // Only replace exact pattern with delimiters: (v_i * N) or v_i * N+space
                    // Avoids corrupting larger expressions like v_i * 500
                    bodyText = bodyText.Replace($"({hexpr})", hn);
                    bodyText = bodyText.Replace($"{hexpr},", $"{hn},");
                    bodyText = bodyText.Replace($"{hexpr} ", $"{hn} ");
                    bodyText = bodyText.Replace($"{hexpr})", $"{hn})");
                    bodyText = bodyText.Replace($"{hexpr};", $"{hn};");
                }
                _builder.Length = hoistStart;
                _builder.Append(bodyText);
            }

            // Exit early when all lanes resolved (sentinel from scalar write pattern)
            if (!string.IsNullOrEmpty(_sentinelVar))
                AppendLine($"if (!({savedMask} & simd_mask{{ n_cmp_eq_epi32(v_{_sentinelVar}.v, n_set1_epi32({_sentinelVal})) }}).any_true()) {{ break; }}");

            _loopStack.Pop();

            if (_gotoTargets.Contains(continueLabel))
                AppendLine($"{continueLabel}: ;");
            _indent--;
            AppendLine("}");
            // ★ Restore mask after loop: use the saved variable (still in scope)
            _currentMask = savedMask;
            if (_gotoTargets.Contains(exitLabel))
                AppendLine($"{exitLabel}: ;");
        }

        // ================================================================
        // Strategy 2: Varying-bound reduction → count-loop + hmax + ivdep
        //   hmax(end-start) + for(iter) + clamp + SIMD gather + blend
        //   Mask scope fix: save _currentMask as named var BEFORE the for
        // ================================================================
        private void GenerateVaryingReductionLoop(string ivName, string startExpr, string endExpr, ForStatementSyntax stmt)
        {
            _inVaryingReductionLoop = true;

            string sid = $"_{_maskCounter++}";
            string exitLabel = $"__vr_exit_{_labelCounter++}";
            string continueLabel = $"__vr_cont_{_labelCounter++}";

            bool isLessOrEqual = stmt.Condition is BinaryExpressionSyntax condBinary
                && condBinary.IsKind(SyntaxKind.LessThanOrEqualExpression);
            string simdCmpFunc = isLessOrEqual ? "n_cmp_le_epi32" : "n_cmp_lt_epi32";

            // ★ Mask scope fix: save current mask as a named variable OUTSIDE the for scope
            string savedMask = $"__saved_{_maskCounter++}";
            AppendLine($"simd_mask {savedMask} = {_currentMask};");

            AppendLine($"// Varying-bound reduction: count-loop + hmax + ivdep");
            AppendLine($"simd_value<int> simd_{ivName} = {startExpr};");
            AppendLine($"simd_value<int> simd_end_{ivName} = {endExpr};");
            // ★ Zero masked-lane start so garbage doesn't inflate hmax
            AppendLine($"simd_{ivName} = simd_max(simd_{ivName}, simd_value<int>(0));");
            // ★ Hoist SortedPositions_length-1 broadcast for safe gather inside loop
            string sortedLenVar = FindSortedLengthVar(stmt);
            if (sortedLenVar != null)
            {
                _hoistedSafeMaxVar = "v_sortedLast";
                AppendLine($"simd_value<int> v_sortedLast = simd_value<int>::broadcast({sortedLenVar} - 1);");
            }
            AppendLine($"simd_value<int> v_count{sid} = simd_end_{ivName} - simd_{ivName};");
            AppendLine($"int maxIter{sid} = hmax(v_count{sid});");
            // ivdep: ignore loop-carried dependencies so MSVC can auto-vectorize/reduction-fold
            AppendLine($"#pragma loop(ivdep)");
            AppendLine($"for (int iter{sid} = 0; iter{sid} < maxIter{sid}; iter{sid}++)");
            AppendLine("{");
            _indent++;

            AppendLine($"simd_mask v_active{sid}{{ {simdCmpFunc}(simd_{ivName}.v, simd_end_{ivName}.v) }};");

            // ★ Use v_active directly — no and with savedMask (redundant: dead lanes have v_active=false)
            //   simd_i/simd_end_i were clamped to ≥0 above, so dead-cell lanes have v_active=false.
            _currentMask = $"v_active{sid}";

            _loopStack.Push(new LoopFrame
            {
                TrackerVar = "",
                IterActiveVar = "",
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel
            });

            var bodyBlock = stmt.Statement is BlockSyntax bsb
                ? bsb
                : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(stmt.Statement);
            GenerateBlock(bodyBlock, skipBraces: false);

            _loopStack.Pop();

            if (_gotoTargets.Contains(continueLabel))
                AppendLine($"{continueLabel}: ;");
            _currentMask = savedMask;
            AppendLine($"simd_{ivName} = simd_{ivName} + 1;");
            _indent--;
            AppendLine("}");
            // ★ Restore mask after loop: use the saved variable (still in scope)
            _currentMask = savedMask;
            _inVaryingReductionLoop = false;
            _hoistedSafeMaxVar = null;
            _hoistedSafeMaxExpr = null;
            if (_gotoTargets.Contains(exitLabel))
                AppendLine($"{exitLabel}: ;");
        }

        // ================================================================
        // Strategy 3: Standard SIMD loop (while-true + mask) — original pattern
        // ================================================================
        private void GenerateStandardSIMDLoop(string ivName, string startExpr, string endExpr, ForStatementSyntax stmt, bool isUniformBounds)
        {
            string tracker = $"__tracker_{_maskCounter++}";
            string iterActive = $"__iter_active_{_maskCounter++}";
            string exitLabel = $"__loop_exit_{_labelCounter++}";
            string continueLabel = $"__loop_continue_{_labelCounter++}";

            bool isLessOrEqual = stmt.Condition is BinaryExpressionSyntax condBinary
                && condBinary.IsKind(SyntaxKind.LessThanOrEqualExpression);
            string simdCmpFunc = isLessOrEqual ? "n_cmp_le_epi32" : "n_cmp_lt_epi32";
            string csOpStr = isLessOrEqual ? "<=" : "<";

            string simdStartVal = isUniformBounds
                ? $"simd_value<int>::broadcast({startExpr})"
                : startExpr;
            string simdEndVal = isUniformBounds
                ? $"simd_value<int>::broadcast({endExpr})"
                : endExpr;

            AppendLine($"// SIMD mask-loop: for (int {ivName} = {startExpr}; {ivName} {csOpStr} {endExpr}; {ivName}++)");
            AppendLine($"simd_value<int> simd_{ivName} = {simdStartVal};");
            AppendLine($"simd_value<int> simd_end_{ivName} = {simdEndVal};");
            AppendLine($"simd_mask {tracker} = simd_mask::all_true();");

            string preLoopMask = _currentMask;
            AppendLine("while (true)");
            AppendLine("{");
            _indent++;

            AppendLine($"simd_mask {iterActive} = simd_mask{{ {simdCmpFunc}(simd_{ivName}.v, simd_end_{ivName}.v) }} & {tracker};");
            string savedMask = $"__mask_{_maskCounter++}";
            AppendLine($"simd_mask {savedMask} = {_currentMask};");
            _currentMask = savedMask;
            AppendLine($"if (!({savedMask} & {iterActive}).any_true()) {{ break; }}");
            string narrowedMask = $"simd_mask{{ n_and_mask({savedMask}.m, {iterActive}.m) }}";
            _currentMask = narrowedMask;

            _loopStack.Push(new LoopFrame
            {
                TrackerVar = tracker,
                IterActiveVar = iterActive,
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel
            });

            var bodyBlock = stmt.Statement is BlockSyntax fb
                ? fb
                : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(stmt.Statement);
            GenerateBlock(bodyBlock, skipBraces: false);

            _loopStack.Pop();

            if (_gotoTargets.Contains(continueLabel))
                AppendLine($"{continueLabel}: ;");
            _currentMask = savedMask;
            AppendLine($"simd_{ivName} = simd_{ivName} + 1;");
            AppendLine("}");
            _indent--;
            _currentMask = preLoopMask;
            if (_gotoTargets.Contains(exitLabel))
                AppendLine($"{exitLabel}: ;");
        }

        // ================================================================
        // Per-lane for-loop (varying bounds — gather vs sequential trade-off)
        // ================================================================

        /// <summary>
        /// 生成 per-lane scalar 版本的 for 循环，用于边界 varying 的场景。
        /// 原理：SIMD 全宽度 gather 在循环范围 per-lane 各不相同时 cache 不友好，
        /// 改为提取到标量 → 顺序读 → 合并回 SIMD。
        /// </summary>
        private void GeneratePerLaneForLoop(ForStatementSyntax stmt, string ivName)
        {
            // --- 1. 收集被 per-lane 体引用的 SIMD 变量（排除局部声明和 induction var）---
            var referencedVars = new HashSet<string>();
            var locallyDeclared = new HashSet<string>();
            foreach (var id in stmt.Statement.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                string name = id.Identifier.Text;
                if (_simdVaryingVarNames.Contains(name))
                    referencedVars.Add(name);
            }
            foreach (var localDecl in stmt.Statement.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                foreach (var v in localDecl.Declaration.Variables)
                    locallyDeclared.Add(v.Identifier.Text);
            referencedVars.ExceptWith(locallyDeclared);
            referencedVars.Remove(ivName);

            // --- 2. 区分只读和读写变量 ---
            var writtenVars = new HashSet<string>();
            foreach (var assign in stmt.Statement.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                string? lhs = assign.Left is IdentifierNameSyntax lhsId ? lhsId.Identifier.Text : null;
                if (lhs != null && referencedVars.Contains(lhs))
                    writtenVars.Add(lhs);
            }
            var readOnlyVars = new HashSet<string>(referencedVars);
            readOnlyVars.ExceptWith(writtenVars);

            string sid = $"{_maskCounter++}";

            // --- 3. Save phase: SIMD 寄存器 → 缓冲区 ---
            AppendLine("// --- Per-lane region (varying bounds) ---");
            AppendLine("{");
            _indent++;
            AppendLine($"int __mask_{sid} = n_mask_to_bitmask(({_currentMask}).m);");

            foreach (var name in writtenVars)
            {
                string ct = _simdVaryingCppType[name];
                if (ct.Contains("float2"))
                {
                    AppendLine($"float __{name}_x_{sid}[NSIMD_WIDTH]; n_store_ps(__{name}_x_{sid}, v_{name}.x.v);");
                    AppendLine($"float __{name}_y_{sid}[NSIMD_WIDTH]; n_store_ps(__{name}_y_{sid}, v_{name}.y.v);");
                }
                else if (ct.Contains("int2"))
                {
                    AppendLine($"int __{name}_x_{sid}[NSIMD_WIDTH]; n_store_epi32(__{name}_x_{sid}, v_{name}.x.v);");
                    AppendLine($"int __{name}_y_{sid}[NSIMD_WIDTH]; n_store_epi32(__{name}_y_{sid}, v_{name}.y.v);");
                }
                else
                {
                    string store = ct == "float" ? "n_store_ps" : "n_store_epi32";
                    AppendLine($"{ct} __{name}_{sid}[NSIMD_WIDTH]; {store}(__{name}_{sid}, v_{name}.v);");
                }
            }
            AppendLine($"int __start_{sid}[NSIMD_WIDTH]; n_store_epi32(__start_{sid}, simd_{ivName}.v);");
            AppendLine($"int __end_{sid}[NSIMD_WIDTH]; n_store_epi32(__end_{sid}, simd_end_{ivName}.v);");

            // --- 4. Per-lane scalar loop ---
            AppendLine("for (int __lane = 0; __lane < NSIMD_WIDTH; __lane++)");
            AppendLine("{");
            _indent++;
            AppendLine($"if (!(__mask_{sid} & (1 << __lane))) continue;");

            // C++ references for written vars (auto-modify buffer)
            foreach (var name in writtenVars)
            {
                string ct = _simdVaryingCppType[name];
                if (ct.Contains("float2"))
                {
                    AppendLine($"float& __{name}_x = __{name}_x_{sid}[__lane];");
                    AppendLine($"float& __{name}_y = __{name}_y_{sid}[__lane];");
                }
                else if (ct.Contains("int2"))
                {
                    AppendLine($"int& __{name}_x = __{name}_x_{sid}[__lane];");
                    AppendLine($"int& __{name}_y = __{name}_y_{sid}[__lane];");
                }
                else
                    AppendLine($"{ct}& {name} = __{name}_{sid}[__lane];");
            }

            // Extract read-only SIMD vars to per-lane scalars
            foreach (var name in readOnlyVars)
            {
                string ct = _simdVaryingCppType[name];
                if (ct.Contains("float2"))
                {
                    AppendLine($"EntJoy::Mathematics::float2 {name};");
                    AppendLine($"{name}.x() = n_extract_lane_f32(v_{name}.x.v, __lane);");
                    AppendLine($"{name}.y() = n_extract_lane_f32(v_{name}.y.v, __lane);");
                }
                else if (ct.Contains("int2"))
                {
                    AppendLine($"EntJoy::Mathematics::int2 {name};");
                    AppendLine($"{name}.x() = n_extract_lane_epi32(v_{name}.x.v, __lane);");
                    AppendLine($"{name}.y() = n_extract_lane_epi32(v_{name}.y.v, __lane);");
                }
                else if (ct == "float")
                    AppendLine($"float {name} = n_extract_lane_f32(v_{name}.v, __lane);");
                else
                    AppendLine($"int {name} = n_extract_lane_epi32(v_{name}.v, __lane);");
            }

            // Scalar for-loop with induction variable
            AppendLine($"int {ivName} = __start_{sid}[__lane];");
            AppendLine($"int {ivName}_end = __end_{sid}[__lane];");
            AppendLine($"for (; {ivName} < {ivName}_end; {ivName}++)");
            AppendLine("{");
            _indent++;

            // --- 5. Scalar body via CppBatchStatementTranslator ---
            var stmtBody = stmt.Statement is BlockSyntax bs
                ? bs
                : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(stmt.Statement);
            var scalarTranslator = new CppBatchStatementTranslator(
                _semanticModel, _jobStruct, "", "", _useFastMath, false);
            string scalarBody = scalarTranslator.Translate(stmtBody);

            // Replace float2/int2 component access for written vars: name.x() → __name_x
            foreach (var name in writtenVars)
            {
                string ct = _simdVaryingCppType[name];
                if (ct.Contains("float2") || ct.Contains("int2"))
                {
                    scalarBody = System.Text.RegularExpressions.Regex.Replace(
                        scalarBody, $@"\b{name}\.x\(\)", $"__{name}_x");
                    scalarBody = System.Text.RegularExpressions.Regex.Replace(
                        scalarBody, $@"\b{name}\.y\(\)", $"__{name}_y");
                }
            }

            foreach (var line in scalarBody.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    AppendLine(line.TrimEnd());

            _indent--;
            AppendLine("}"); // end for(ivName)
            _indent--;
            AppendLine("}"); // end for(__lane)

            // --- 6. Merge phase: reload buffers→ SIMD registers ---
            foreach (var name in writtenVars)
            {
                string ct = _simdVaryingCppType[name];
                if (ct.Contains("float2"))
                {
                    AppendLine($"v_{name}.x = simd_value<float>::load(__{name}_x_{sid});");
                    AppendLine($"v_{name}.y = simd_value<float>::load(__{name}_y_{sid});");
                }
                else if (ct.Contains("int2"))
                {
                    AppendLine($"v_{name}.x = simd_value<int>::load(__{name}_x_{sid});");
                    AppendLine($"v_{name}.y = simd_value<int>::load(__{name}_y_{sid});");
                }
                else
                {
                    string load = ct == "float" ? "simd_value<float>::load" : "simd_value<int>::load";
                    AppendLine($"v_{name} = {load}(__{name}_{sid});");
                }
            }

            _indent--;
            AppendLine("}"); // end per-lane scope
        }

        // ================================================================
        // WhileStatement → for(iter) where possible, else while(any_true)
        // ================================================================

        private void GenerateWhileStatement(WhileStatementSyntax stmt)
        {
            // For general while, use while(true) + mask check pattern
            string tracker = $"__while_tracker_{_maskCounter++}";
            string exitLabel = $"__while_exit_{_labelCounter++}";
            string continueLabel = $"__while_continue_{_labelCounter++}";

            AppendLine($"simd_mask {tracker} = simd_mask::all_true();");
            AppendLine("while (true)");
            AppendLine("{");
            _indent++;

            string condExpr = TranslateCondition(stmt.Condition);
            string condVar = $"__wcond_{_maskCounter++}";
            AppendLine($"simd_mask {condVar} = {condExpr};");

            string savedMask = $"__mask_{_maskCounter++}";
            AppendLine($"simd_mask {savedMask} = {_currentMask};");
            _currentMask = $"{_currentMask} & {condVar} & {tracker}";
            AppendLine($"if (!{_currentMask}.any_true()) {{ {_currentMask} = {savedMask}; break; }}");

            _loopStack.Push(new LoopFrame
            {
                TrackerVar = tracker,
                IterActiveVar = condVar,
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel
            });

            GenerateBlock(EnsureBlock(stmt.Statement), skipBraces: false);

            _loopStack.Pop();

            if (_gotoTargets.Contains(continueLabel))
                AppendLine($"{continueLabel}: ;");
            _currentMask = savedMask;
            AppendLine("}");
            _indent--;
            if (_gotoTargets.Contains(exitLabel))
                AppendLine($"{exitLabel}: ;");
        }

        // ================================================================
        // DoStatement
        // ================================================================

        private void GenerateDoStatement(DoStatementSyntax stmt)
        {
            string tracker = $"__do_tracker_{_maskCounter++}";
            string exitLabel = $"__do_exit_{_labelCounter++}";
            string continueLabel = $"__do_continue_{_labelCounter++}";

            AppendLine($"simd_mask {tracker} = simd_mask::all_true();");
            AppendLine("do");
            AppendLine("{");
            _indent++;

            string savedMask = $"__mask_{_maskCounter++}";
            AppendLine($"simd_mask {savedMask} = {_currentMask};");
            _currentMask = $"{_currentMask} & {tracker}";

            _loopStack.Push(new LoopFrame
            {
                TrackerVar = tracker,
                IterActiveVar = savedMask,
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel
            });

            GenerateBlock(EnsureBlock(stmt.Statement), skipBraces: false);

            _loopStack.Pop();

            if (_gotoTargets.Contains(continueLabel))
                AppendLine($"{continueLabel}: ;");
            _currentMask = savedMask;

            string condExpr = TranslateCondition(stmt.Condition);
            string condVar = $"__dcond_{_maskCounter++}";
            AppendLine($"simd_mask {condVar} = {condExpr};");
            AppendLine($"}} while(({_currentMask} & {condVar} & {tracker}).any_true());");
            if (_gotoTargets.Contains(exitLabel))
                AppendLine($"{exitLabel}: ;");
            _indent--;
        }
    }
}
