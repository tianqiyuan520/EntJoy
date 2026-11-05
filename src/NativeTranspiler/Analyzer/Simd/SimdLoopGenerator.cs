using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NativeTranspiler.Analyzer.Common;

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
        private int _unrollProduct = 1;
        // ForStatement → for(iter) count-loop

        private void GenerateForStatement(ForStatementSyntax stmt)
        {
            if (!IntForLoop.TryAnalyze(stmt, _semanticModel, out var loop))
            {
                AppendLine($"// {UnsupportedMarkers.Stmt}SIMD_ForControl");
                return;
            }

            // 保留 C# for 声明的作用域，允许相邻循环复用计数器名。
            string entryMask = _currentMask;
            AppendLine("{");
            _indent++;

            var decl = stmt.Declaration!.Variables[0];
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
            string startExpr = TranslateExpression(loop.Start);
            string endExpr = TranslateExpression(loop.Bound);

            // Classify bounds + reduction
            bool isUniformBounds = _varAnalyzer.ClassifyExpression(loop.Start) < VarKind.Varying
                && _varAnalyzer.ClassifyExpression(loop.Bound) < VarKind.Varying;

            bool isReduction = IsReductionLoop(stmt);

            // 保持原计数顺序；只有完整行程及最后一次增量均可证明安全时展开。
            bool canUnroll = loop.TryGetTripCount(_semanticModel, 64, out int constStart, out int fixedIterations)
                && (long)_unrollProduct * fixedIterations <= 64;

            // Dispatch: unroll → uniform reduction → varying reduction → scalar-for → while-true
            if (canUnroll)
            {
                // Full unroll for small uniform-bound loops（含 min/max/sum 归约）。
                GenerateUnrolledLoop(ivName, constStart, fixedIterations, loop.Step, stmt);
            }
            else if (isUniformBounds && isReduction)
                GenerateUniformReductionLoop(ivName, startExpr, endExpr, stmt, loop);
            else if (!isUniformBounds && isReduction && CanUseReductionCountLoop(stmt, loop))
                GenerateVaryingReductionLoop(ivName, startExpr, endExpr, stmt, loop);
            else if (isUniformBounds && !isReduction)
            {
                    // Docs-style: scalar for() with mask-narrowed body (dx/dy loops)
                    //   for(int dx) {
                    //     v_active = cmp_ult(v_nx, dims);
                    //     if(!v_active.any_true()) continue;
                    //     // body uses v_active as mask
                    //   }
                    // continue emits real C++ continue; (skip iteration when ALL lanes done)
                    string csOpStr2 = loop.Operator;
                    AppendLine($"// Scalar for: step {loop.Step}");
                    AppendLine($"simd_value<int> simd_{ivName};");
                    // simd_end_{ivName} not needed — uniform loops never compare against end in SIMD
                    string preLoopMask2 = _currentMask;
                    // Save mask to variable so if-bodies inside the loop get a non-all_true mask
                    string loopSavedMask = $"__saved_{_maskCounter++}";
                    AppendLine($"simd_mask {loopSavedMask} = {_currentMask};");
                    _currentMask = loopSavedMask;
                    AppendLine($"for (int {ivName} = {startExpr}; {ivName} {csOpStr2} {endExpr}; {ivName} = (int)((unsigned){ivName} + {unchecked((uint)loop.Step)}u))");
                    AppendLine("{");
                    _indent++;
                    AppendLine($"simd_{ivName} = simd_value<int>::broadcast({ivName});");
                    // Loop frame for continue (real C++ continue;)
                    string exitL = $"__uni_exit_{_labelCounter++}";
                    string contL = $"__uni_cont_{_labelCounter++}";
                    bool outerScalarLoop = _isUniformScalarLoop;
                    _isUniformScalarLoop = true;
                    var bodyB = stmt.Statement is BlockSyntax fb2 ? fb2 : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(stmt.Statement);
                    // 循环体含 continue 时，逐 lane 跳过剩余语句需要一个每轮重置的 body mask：
                    //   continue 只影响本次迭代，不能改 tracker（那是 break 的语义）。
                    string bodyMaskB = "";
                    if (bodyB.DescendantNodes().OfType<ContinueStatementSyntax>().Any())
                    {
                        bodyMaskB = $"__bm_{_maskCounter++}";
                        AppendLine($"simd_mask {bodyMaskB} = {loopSavedMask};");
                    }
                    // Uniform loop with a per-lane break condition: the saved mask doubles
                    //   as the lane tracker so break can drop matched lanes and keep matching
                    //   others (see GenerateBreakStatement).
                    _loopStack.Push(new LoopFrame { TrackerVar = loopSavedMask, IterActiveVar = "", ExitLabel = exitL, ContinueLabel = contL, BodyMaskVar = bodyMaskB });
                    GenerateStatement(stmt.Statement);
                    _loopStack.Pop();
                    _isUniformScalarLoop = outerScalarLoop;
                    if (_gotoTargets.Contains(contL))
                    AppendLine($"{contL}: ;");
                    _indent--;
                    AppendLine("}");
                    _currentMask = preLoopMask2;
                    if (_gotoTargets.Contains(exitL))
                    AppendLine($"{exitL}: ;");
            }
            else
                GenerateStandardSIMDLoop(ivName, startExpr, endExpr, stmt, loop);
            _indent--;
            AppendLine("}");
            _currentMask = entryMask;
            if (!string.IsNullOrEmpty(_returnedMaskVar))
            {
                string afterMask = $"__after_for_{_maskCounter++}";
                AppendLine($"simd_mask {afterMask} = simd_mask{{ n_and_mask({entryMask}.m, n_not_mask({_returnedMaskVar}.m)) }};");
                _currentMask = afterMask;
            }
        }

        // 短常量步长循环按原顺序展开，嵌套展开共用次数预算。
        private void GenerateUnrolledLoop(string ivName, int start, int count, int step, ForStatementSyntax stmt)
        {
            int outerUnrollProduct = _unrollProduct;
            _unrollProduct *= System.Math.Max(count, 1);
            string exitLabel = $"__unr_exit_{_labelCounter++}";
            AppendLine($"// Unrolled loop: {count} iterations, step {step}");
            AppendLine($"simd_value<int> simd_{ivName};");
            string savedMask = _currentMask;

            var bodyBlock = stmt.Statement is BlockSyntax bs
                ? bs
                : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(stmt.Statement);

            // Pre-scan: does the body contain break/continue?
            bool hasBreak = bodyBlock.DescendantNodes().OfType<BreakStatementSyntax>().Any();
            bool hasContinue = bodyBlock.DescendantNodes().OfType<ContinueStatementSyntax>().Any();
            string tracker = "";
            if (hasBreak || hasContinue)
            {
                tracker = $"__unrolled_tracker_{_maskCounter++}";
                AppendLine($"simd_mask {tracker} = {savedMask};");
            }

            for (int iteration = 0; iteration < count; iteration++)
            {
                int i = (int)(start + (long)iteration * step);
                // Negative iteration values would produce labels like "__unr_cont_1_-2",
                //   which is not a valid C++ identifier ('-2' parses as subtraction).
                //   Encode-the-sign into the suffix: -2 → "m2", 3 → "3".
                string iSuffix = i < 0 ? "m" + (-(long)i).ToString() : i.ToString();
                string contLabel = $"__unr_cont_{_labelCounter++}_{iSuffix}";

                // Reset mask for each iteration
                _currentMask = savedMask;
                string bodyMask = "";
                if (hasBreak || hasContinue)
                {
                    bodyMask = $"__unrolled_body_{_maskCounter++}";
                    AppendLine($"simd_mask {bodyMask} = {tracker};");
                    _currentMask = bodyMask;
                }
                // kill lanes that have already returned (e.g. found their match
                //   in a previous unrolled j iteration and wrote R[i]=result).
                //   result because its mask is restored to all_true by savedMask.
                if (!string.IsNullOrEmpty(_returnedMaskVar))
                {
                    // If savedMask is the all_true literal (not a named variable), we can't
                    // assign to it. Introduce a named variable first.
                    if (savedMask == "simd_mask::all_true()")
                    {
                        string namedMask = $"__init_mask_{_labelCounter++}";
                        AppendLine($"simd_mask {namedMask} = simd_mask{{ n_and_mask({_currentMask}.m, n_not_mask({_returnedMaskVar}.m)) }};");
                        _currentMask = namedMask;
                    }
                    else
                    {
                        AppendLine($"{_currentMask} = simd_mask{{ n_and_mask({_currentMask}.m, n_not_mask({_returnedMaskVar}.m)) }};");
                    }
                    // Jump to the unrolled loop's exit label when all lanes have returned.
                    //   exitLabel (which would skip the post_mask init above → UB).
                    //   __simd_exit is always emitted by the batch loop generator.
                    _gotoTargets.Add("__simd_exit");
                    AppendLine($"if (!{_currentMask}.any_true()) {{ goto __simd_exit; }}");
                }

                // Broadcast the iteration value
                AppendLine($"simd_{ivName} = simd_value<int>::broadcast({i});");

                // Body scope: isolate local variables per iteration
                AppendLine("{");
                _indent++;

                if (hasBreak || hasContinue)
                {
                    // Break removes a lane from future iterations; continue only
                    // removes it from this iteration's body.
                    _loopStack.Push(new LoopFrame
                    {
                        TrackerVar = tracker,
                        IterActiveVar = bodyMask,
                        BodyMaskVar = bodyMask,
                        ExitLabel = exitLabel,
                        ContinueLabel = contLabel
                    });
                }
                bool outerScalarLoop = _isUniformScalarLoop;
                _isUniformScalarLoop = false;

                GenerateStatement(stmt.Statement);

                _isUniformScalarLoop = outerScalarLoop;
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
            // compute the post-loop mask for the default store.
            //   - ALL lanes returned → no lane needs default → skip store entirely (goto exitLabel)
            //   - NO lanes returned → default store writes 777 for all lanes (post_mask = all lanes)
            if (!string.IsNullOrEmpty(_returnedMaskVar))
            {
                // Compute non-returned lanes mask (for the default store)
                if (savedMask == "simd_mask::all_true()")
                {
                    string postMaskVar = $"__post_mask_{_labelCounter++}";
                    AppendLine($"simd_mask {postMaskVar} = simd_mask{{ n_and_mask({_currentMask}.m, n_not_mask({_returnedMaskVar}.m)) }};");
                    _currentMask = postMaskVar;
                }
                else
                {
                    AppendLine($"{_currentMask} = simd_mask{{ n_and_mask({_currentMask}.m, n_not_mask({_returnedMaskVar}.m)) }};");
                }
                // Skip default store ONLY when ALL lanes returned (post_mask is empty).
                //   The goto is from AFTER post_mask init, so no UB.
                //   `__returned_0.any_true()` = at least one lane returned;
                //   when the k-loop exits and post_mask is all-false, ALL lanes returned.
                //   (If post_mask has lanes → some didn't return → they need the default store.)
                _gotoTargets.Add(exitLabel);
                AppendLine($"if ({_returnedMaskVar}.any_true() && !{_currentMask}.any_true()) {{ goto {exitLabel}; }}");
            }
            if (hasBreak)
                AppendLine($"{exitLabel}: ;");
            else if (!string.IsNullOrEmpty(_returnedMaskVar))
                AppendLine($"{exitLabel}: ;");
            _unrollProduct = outerUnrollProduct;
        }

        // Strategy 1: Uniform-bound reduction → scalar for + SIMD broadcast
        //   scalar load + broadcast → 8-wide SIMD op + blend
        //   Mask scope fix: save _currentMask as named var BEFORE the for
        private void GenerateUniformReductionLoop(string ivName, string startExpr, string endExpr, ForStatementSyntax stmt, IntForLoop loop)
        {
            bool alreadyUniform = !_uniformLoopVars.Add(ivName);

            string exitLabel = $"__uni_exit_{_labelCounter++}";
            string continueLabel = $"__uni_cont_{_labelCounter++}";

            // Save the entry mask BEFORE the loop: __saved_N doubles as the lane
            //   tracker (breaks drop lanes from it), so restoring _currentMask from
            //   that follows (e.g. the final R[i]=... store skips broken lanes).
            string preLoopMask = _currentMask;
            // Save current mask as a named variable outside the for scope
            string savedMask = $"__saved_{_maskCounter++}";
            AppendLine($"simd_mask {savedMask} = {_currentMask};");

            string csOpStr = loop.Operator;

            // Pre-scan for SIMD loop-invariant hoisting: detect v_i * N patterns
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
            // Reevaluate the original bound each iteration, preserving <= without
            // adding one to its endpoint. The int counter uses C# unchecked wrap.
            AppendLine($"for (int {ivName} = {startExpr}; {ivName} {csOpStr} {endExpr}; {ivName} = (int)((unsigned){ivName} + {unchecked((uint)loop.Step)}u))");
            AppendLine("{");
            _indent++;
            // simd_i for blend targets (bestIdx = simd_i)
            AppendLine($"simd_value<int> simd_{ivName} = simd_value<int>::broadcast({ivName});");

            // Within the loop body, use savedMask (it's in scope)
            string outerMask = _currentMask;
            _currentMask = savedMask;

            // 循环体含 continue 时补一个每轮重置的 body mask（见 Docs-style 分支同处理）。
            string bodyMaskVar = "";
            if (bodyBlock.DescendantNodes().OfType<ContinueStatementSyntax>().Any())
            {
                bodyMaskVar = $"__bm_{_maskCounter++}";
                AppendLine($"simd_mask {bodyMaskVar} = {savedMask};");
            }

            _loopStack.Push(new LoopFrame
            {
                // Saved mask doubles as lane tracker so per-lane breaks drop matched
                //   lanes and keep unmatched ones iterating.
                TrackerVar = savedMask,
                IterActiveVar = "",
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel,
                BodyMaskVar = bodyMaskVar
            });

            int hoistStart = _builder.Length;
            GenerateStatement(stmt.Statement);

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

            _loopStack.Pop();

            if (_gotoTargets.Contains(continueLabel))
                AppendLine($"{continueLabel}: ;");
            _indent--;
            AppendLine("}");
            // Restore the ENTRY mask (not the tracker — breaks may have narrowed it)
            _currentMask = preLoopMask;
            if (_gotoTargets.Contains(exitLabel))
                AppendLine($"{exitLabel}: ;");
            if (!alreadyUniform) _uniformLoopVars.Remove(ivName);
        }

        // 只有不变的值边界才能提前计算行程；内存读取和控制转移走逐轮求值的 mask loop。
        private bool CanUseReductionCountLoop(ForStatementSyntax statement, IntForLoop loop)
        {
            if (loop.Step != 1 || loop.Comparison != SyntaxKind.LessThanExpression
                || statement.Statement.DescendantNodesAndSelf().Any(n => n is BreakStatementSyntax || n is ReturnStatementSyntax))
                return false;
            var original = SourceSyntax.Original(_semanticModel, statement) as ForStatementSyntax;
            if (original == null) return false;
            var flow = _semanticModel.AnalyzeDataFlow(original.Statement);
            if (flow == null || !flow.Succeeded) return false;
            foreach (var node in loop.Bound.DescendantNodesAndSelf())
            {
                if (node is PrefixUnaryExpressionSyntax prefix
                    && (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression))) return false;
                if (node is LiteralExpressionSyntax || node is ParenthesizedExpressionSyntax
                    || node is BinaryExpressionSyntax || node is PrefixUnaryExpressionSyntax || node is CastExpressionSyntax
                    || node is PredefinedTypeSyntax) continue;
                if (node is not IdentifierNameSyntax identifier) return false;
                var symbol = SymbolHelper.GetBoundSymbol(_semanticModel, identifier);
                if (symbol is not ILocalSymbol && symbol is not IParameterSymbol
                    || symbol is ILocalSymbol local && local.RefKind != RefKind.None
                    || symbol is IParameterSymbol parameter && parameter.RefKind != RefKind.None) return false;
                if (flow.WrittenInside.Contains(symbol, SymbolEqualityComparer.Default)
                    || flow.UnsafeAddressTaken.Contains(symbol, SymbolEqualityComparer.Default)) return false;
                var method = original.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (method != null && method.DescendantNodes().OfType<RefExpressionSyntax>().Any(r =>
                    SymbolEqualityComparer.Default.Equals(SymbolHelper.GetBoundSymbol(_semanticModel, r.Expression), symbol))) return false;
            }
            return true;
        }

        // 不变的排他上界：unsigned 行程覆盖完整 int 区间，单调 tracker 防止退出的 lane 再激活。
        private void GenerateVaryingReductionLoop(string ivName, string startExpr, string endExpr, ForStatementSyntax stmt, IntForLoop loop)
        {
            string preLoopMask = _currentMask;
            _inVaryingReductionLoop = true;

            string sid = $"_{_maskCounter++}";
            string exitLabel = $"__vr_exit_{_labelCounter++}";
            string continueLabel = $"__vr_cont_{_labelCounter++}";

            // Save current mask as a named variable outside the for scope
            string savedMask = $"__saved_{_maskCounter++}";
            AppendLine($"simd_mask {savedMask} = {_currentMask};");

            AppendLine($"// Varying-bound reduction: count-loop + hmax + ivdep");
            AppendLine($"simd_value<int> simd_{ivName} = {startExpr};");
            AppendLine($"simd_value<int> simd_end_{ivName} = {endExpr};");

            // Hoist SortedPositions_length-1 broadcast for safe gather inside loop
            string sortedLenVar = FindSortedLengthVar(stmt);
            if (sortedLenVar != null)
            {
                _hoistedSafeMaxVar = "v_sortedLast";
                AppendLine($"simd_value<int> v_sortedLast = simd_value<int>::broadcast({sortedLenVar} - 1);");
            }
            AppendLine($"{savedMask} = {savedMask} & simd_mask{{ n_cmp_lt_epi32(simd_{ivName}.v, simd_end_{ivName}.v) }};");
            AppendLine($"simd_value<int> v_count{sid} = blend(simd_value<int>(0), simd_end_{ivName} - simd_{ivName}, {savedMask});");
            // 翻转符号位，把 unsigned 排序映射到 signed hmax。
            AppendLine($"unsigned maxIter{sid} = (unsigned)hmax(v_count{sid} ^ simd_value<int>((int)0x80000000u)) ^ 0x80000000u;");
            AppendLine($"for (unsigned iter{sid} = 0; iter{sid} < maxIter{sid}; iter{sid}++)");
            AppendLine("{");
            _indent++;

            AppendLine($"{savedMask} = {savedMask} & simd_mask{{ n_cmp_lt_epi32(simd_{ivName}.v, simd_end_{ivName}.v) }};");
            AppendLine($"simd_mask v_active{sid} = {savedMask};");
            // kill lanes that have already returned before using as active mask.
            //   Without this, returned lanes would re-activate each count-loop iteration
            //   (v_active is recomputed fresh each iter) and overwrite their result.
            if (!string.IsNullOrEmpty(_returnedMaskVar))
                AppendLine($"v_active{sid} = simd_mask{{ n_and_mask(v_active{sid}.m, n_not_mask({_returnedMaskVar}.m)) }};");

            // Retain the enclosing branch mask for every reduction iteration.
            _currentMask = $"v_active{sid}";

            _loopStack.Push(new LoopFrame
            {
                TrackerVar = savedMask,
                IterActiveVar = $"v_active{sid}",
                BodyMaskVar = $"v_active{sid}",
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel
            });

            GenerateStatement(stmt.Statement);

            _loopStack.Pop();

            if (_gotoTargets.Contains(continueLabel))
                AppendLine($"{continueLabel}: ;");
            _currentMask = savedMask;
            AppendLine($"simd_{ivName} = simd_{ivName} + 1;");
            _indent--;
            AppendLine("}");
            _currentMask = preLoopMask;
            _inVaryingReductionLoop = false;
            _hoistedSafeMaxVar = null;
            _hoistedSafeMaxExpr = null;
            if (_gotoTargets.Contains(exitLabel))
                AppendLine($"{exitLabel}: ;");
        }

        // Strategy 3: Standard SIMD loop (while-true + mask) — original pattern
        private void GenerateStandardSIMDLoop(string ivName, string startExpr, string endExpr, ForStatementSyntax stmt, IntForLoop loop)
        {
            string tracker = $"__tracker_{_maskCounter++}";
            string iterActive = $"__iter_active_{_maskCounter++}";
            string exitLabel = $"__loop_exit_{_labelCounter++}";
            string continueLabel = $"__loop_continue_{_labelCounter++}";

            string preLoopMask = _currentMask;
            string simdCmpFunc = loop.Comparison switch
            {
                SyntaxKind.LessThanExpression => "n_cmp_lt_epi32",
                SyntaxKind.LessThanOrEqualExpression => "n_cmp_le_epi32",
                SyntaxKind.GreaterThanExpression => "n_cmp_gt_epi32",
                _ => "n_cmp_ge_epi32"
            };
            AppendLine($"// SIMD mask-loop: step {loop.Step}");
            AppendLine($"simd_value<int> simd_{ivName} = {startExpr};");
            AppendLine($"simd_mask {tracker} = {preLoopMask};");
            bool outerScalarLoop = _isUniformScalarLoop;
            _isUniformScalarLoop = false;
            AppendLine("while (true)");
            AppendLine("{");
            _indent++;

            AppendLine($"simd_value<int> simd_end_{ivName} = {endExpr};");
            // 已退出的 lane 不再参与后续条件求值；每轮 body mask 单独承接 continue。
            AppendLine($"{tracker} = {tracker} & simd_mask{{ {simdCmpFunc}(simd_{ivName}.v, simd_end_{ivName}.v) }};");
            if (!string.IsNullOrEmpty(_returnedMaskVar))
                AppendLine($"{tracker} = {tracker} & simd_mask{{ n_not_mask({_returnedMaskVar}.m) }};");
            AppendLine($"if (!{tracker}.any_true()) {{ break; }}");
            AppendLine($"simd_mask {iterActive} = {tracker};");
            _currentMask = iterActive;

            _loopStack.Push(new LoopFrame
            {
                TrackerVar = tracker,
                IterActiveVar = iterActive,
                BodyMaskVar = iterActive,
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel
            });

            GenerateStatement(stmt.Statement);

            _loopStack.Pop();

            if (_gotoTargets.Contains(continueLabel))
                AppendLine($"{continueLabel}: ;");
            _currentMask = tracker;
            AppendLine($"simd_{ivName} = simd_{ivName} + {loop.Step};");
            AppendLine("}");
            _indent--;
            _currentMask = preLoopMask;
            _isUniformScalarLoop = outerScalarLoop;
            if (_gotoTargets.Contains(exitLabel))
                AppendLine($"{exitLabel}: ;");
        }
        // WhileStatement → for(iter) where possible, else while(any_true)

        /// <summary>
        /// 方向2：识别 while 条件里的「uniform 子条件 && varying 子条件」结构（如
        /// `while (energy &gt; 1 && frames &lt; 16)`，frames 是同步递增的 uniform 计数器），
        /// 生成「标量 while + varying tracker」，消除 uniform 标量广播成 SIMD mask 的格式转换。
        /// 返回 true 表示已按此模式生成。
        /// </summary>
        private bool TryGenerateUniformBoundedWhile(WhileStatementSyntax stmt)
        {
            ExpressionSyntax? uniformCond = null;
            ExpressionSyntax? varyingCond = null;

            if (stmt.Condition is BinaryExpressionSyntax cond && cond.IsKind(SyntaxKind.LogicalAndExpression))
            {
                var left = cond.Left;
                var right = cond.Right;
                VarKind leftKind = _varAnalyzer.ClassifyExpression(left);
                VarKind rightKind = _varAnalyzer.ClassifyExpression(right);
                if (leftKind < VarKind.Varying && rightKind >= VarKind.Varying)
                { uniformCond = left; varyingCond = right; }
                else if (rightKind < VarKind.Varying && leftKind >= VarKind.Varying)
                { uniformCond = right; varyingCond = left; }
                else
                    return false;
            }
            else
            {
                // 单条件：纯 uniform（如 while(k<16)）→ 标量化；纯 varying → 旧路径
                if (_varAnalyzer.ClassifyExpression(stmt.Condition) < VarKind.Varying)
                    uniformCond = stmt.Condition;
                else
                    return false;
            }

            // uniform 条件必须是纯标量表达式（翻译后不含 simd_value/n_cmp/v_）
            string uniformExpr = TranslateExpression(uniformCond!);
            if (uniformExpr.Contains("simd_value") || uniformExpr.Contains("n_cmp") || uniformExpr.Contains("v_"))
                return false;

            string tracker = $"__while_tracker_{_maskCounter++}";
            string exitLabel = $"__while_exit_{_labelCounter++}";
            string continueLabel = $"__while_continue_{_labelCounter++}";
            string savedMask = $"__mask_{_maskCounter++}";
            bool savedMaskIsAllTrue = _currentMask == "simd_mask::all_true()";

            AppendLine($"simd_mask {tracker} = simd_mask::all_true();");
            AppendLine($"simd_mask {savedMask} = {_currentMask};");

            // 标量 while：uniform 条件（标量）&& tracker 有 active lane
            AppendLine($"while (({uniformExpr}) && {tracker}.any_true())");
            AppendLine("{");
            _indent++;

            // varying 条件 → SIMD mask（纯 uniform 时无 varying 子条件）
            string? condVar = null;
            if (varyingCond != null)
            {
                string condExpr = TranslateCondition(varyingCond);
                condVar = $"__wcond_{_maskCounter++}";
                AppendLine($"simd_mask {condVar} = {condExpr};");
                // tracker 更新：不满足 varying 条件的 lane 退出
                AppendLine($"{tracker} = simd_mask{{ n_and_mask({tracker}.m, {condVar}.m) }};");
            }
            _currentMask = tracker;

            _loopStack.Push(new LoopFrame
            {
                TrackerVar = tracker,
                IterActiveVar = condVar ?? "",
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel,
                BodyMaskVar = tracker
            });

            GenerateBlock(EnsureBlock(stmt.Statement), skipBraces: false);

            _loopStack.Pop();

            if (_gotoTargets.Contains(continueLabel))
                AppendLine($"{continueLabel}: ;");
            _currentMask = savedMaskIsAllTrue ? "simd_mask::all_true()" : savedMask;
            AppendLine("}");
            _indent--;
            if (_gotoTargets.Contains(exitLabel))
                AppendLine($"{exitLabel}: ;");

            return true;
        }

        private void GenerateWhileStatement(WhileStatementSyntax stmt)
        {
            // 方向2：uniform 子条件标量化（标量 while + varying tracker）
            if (TryGenerateUniformBoundedWhile(stmt))
                return;

            // For general while, use while(true) + mask check pattern
            string tracker = $"__while_tracker_{_maskCounter++}";
            string exitLabel = $"__while_exit_{_labelCounter++}";
            string continueLabel = $"__while_continue_{_labelCounter++}";

            AppendLine($"simd_mask {tracker} = simd_mask::all_true();");

            // savedMask 必须在 while 之前声明：循环结束后的结果 store 会引用它
            //（`_currentMask` 在循环后被恢复为 savedMask），放循环体内会越作用域。
            string savedMask = $"__mask_{_maskCounter++}";
            bool savedMaskIsAllTrue = _currentMask == "simd_mask::all_true()";
            AppendLine($"simd_mask {savedMask} = {_currentMask};");

            // 循环条件常量提升：`j < 16` 翻译出的 n_set1_epi32(16)/n_set1_ps(常量)
            //   在循环体内每轮求值，clang 逐轮 vpbroadcastd 重广播（C12 实测）。把字面量 broadcast
            //   提升到循环外（构造一次、循环内直接复用寄存器），省 ~1 条/轮。
            string condExpr = HoistConditionBroadcastConstants(TranslateCondition(stmt.Condition));

            AppendLine("while (true)");
            AppendLine("{");
            _indent++;

            string condVar = $"__wcond_{_maskCounter++}";
            AppendLine($"simd_mask {condVar} = {condExpr};");

            // 把组合掩码（current & cond & tracker）物化到单个变量，再赋给 _currentMask。
            //   否则 _currentMask 是 "a & b & c" 复合字符串，`.any_true()`/`.m` 会因运算符
            //   优先级错绑（`!a & b & c.any_true()`、`a & b & c.m`）→ 非法 C++。
            string combined = $"__wm_{_maskCounter++}";
            if (_currentMask == "simd_mask::all_true()")
                AppendLine($"simd_mask {combined} = simd_mask{{ n_and_mask({condVar}.m, {tracker}.m) }};");
            else
                AppendLine($"simd_mask {combined} = simd_mask{{ n_and_mask(n_and_mask({_currentMask}.m, {condVar}.m), {tracker}.m) }};");
            _currentMask = combined;
            AppendLine($"if (!{_currentMask}.any_true()) {{ {_currentMask} = {savedMask}; break; }}");

            _loopStack.Push(new LoopFrame
            {
                TrackerVar = tracker,
                IterActiveVar = condVar,
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel,
                BodyMaskVar = combined
            });

            GenerateBlock(EnsureBlock(stmt.Statement), skipBraces: false);

            _loopStack.Pop();

            if (_gotoTargets.Contains(continueLabel))
                AppendLine($"{continueLabel}: ;");
            // 若 savedMask 原本就是 all_true，循环后恢复为字面量，避免写回时被
            //   inNarrowedContext 误判成 narrowed 而走 per-lane scatter。
            _currentMask = savedMaskIsAllTrue ? "simd_mask::all_true()" : savedMask;
            AppendLine("}");
            _indent--;
            if (_gotoTargets.Contains(exitLabel))
                AppendLine($"{exitLabel}: ;");
        }

        // DoStatement

        /// <summary>
        /// 把循环条件表达式中的 n_set1_epi32/ps(字面量) 提升为循环外构造的 simd_value：
        /// clang 对循环体内的逐轮 broadcast 会重生成 vpbroadcastd（C12 汇编实测每轮一次），
        /// 提升后循环内直接复用寄存器。提升声明在调用点（循环头之前）即时输出。
        /// </summary>
        private string HoistConditionBroadcastConstants(string condExpr)
        {
            int count = 0;
            condExpr = Regex.Replace(condExpr, @"n_set1_epi32\((-?\d+)\)", m =>
            {
                string bound = $"__bound_epi32_{count++}";
                AppendLine($"simd_value<int> {bound}(n_set1_epi32({m.Groups[1].Value}));");
                return $"{bound}.v";
            });
            condExpr = Regex.Replace(condExpr, @"n_set1_ps\((-?[\d.eE+-]+f?)\)", m =>
            {
                string bound = $"__bound_ps_{count++}";
                AppendLine($"simd_value<float> {bound}(n_set1_ps({m.Groups[1].Value}));");
                return $"{bound}.v";
            });
            return condExpr;
        }

        private void GenerateDoStatement(DoStatementSyntax stmt)
        {
            string tracker = $"__do_tracker_{_maskCounter++}";
            string exitLabel = $"__do_exit_{_labelCounter++}";
            string continueLabel = $"__do_continue_{_labelCounter++}";

            AppendLine($"simd_mask {tracker} = simd_mask::all_true();");

            // savedMask 必须在 do 之前声明：`} while(...)` 条件在 do 块之后引用它（越作用域）。
            string savedMask = $"__mask_{_maskCounter++}";
            bool savedMaskIsAllTrue = _currentMask == "simd_mask::all_true()";
            AppendLine($"simd_mask {savedMask} = {_currentMask};");

            AppendLine("do");
            AppendLine("{");
            _indent++;

            // 物化组合掩码（current & tracker），避免 _currentMask 变成 "a & b" 复合串
            //   （嵌套 if 里的 `.m`/`.any_true()` 会优先级错绑）。
            string combined = $"__dm_{_maskCounter++}";
            if (_currentMask == "simd_mask::all_true()")
                AppendLine($"simd_mask {combined} = {tracker};");
            else
                AppendLine($"simd_mask {combined} = simd_mask{{ n_and_mask({_currentMask}.m, {tracker}.m) }};");
            _currentMask = combined;

            _loopStack.Push(new LoopFrame
            {
                TrackerVar = tracker,
                IterActiveVar = savedMask,
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel,
                BodyMaskVar = combined
            });

            GenerateBlock(EnsureBlock(stmt.Statement), skipBraces: false);

            _loopStack.Pop();

            if (_gotoTargets.Contains(continueLabel))
                AppendLine($"{continueLabel}: ;");
            _currentMask = savedMaskIsAllTrue ? "simd_mask::all_true()" : savedMask;

            string condExpr = TranslateCondition(stmt.Condition);
            string condVar = $"__dcond_{_maskCounter++}";
            AppendLine($"simd_mask {condVar} = {condExpr};");
            AppendLine($"}} while(({savedMask} & {condVar} & {tracker}).any_true());");
            if (_gotoTargets.Contains(exitLabel))
                AppendLine($"{exitLabel}: ;");
            _indent--;
        }
    }
}
