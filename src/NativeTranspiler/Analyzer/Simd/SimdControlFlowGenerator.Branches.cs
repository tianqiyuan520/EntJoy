using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public partial class SimdControlFlowGenerator
    {

        // IfStatement → mask push/pop

        private void GenerateIfStatement(IfStatementSyntax stmt)
        {
            var conditions = new List<string>();
            var current = stmt;
            StatementSyntax? elseBody = null;
            string savedMask = "";
            bool savedMaskEmitted = false;
            // 已处理分支的条件按顺序排除，后续分支只接收尚未命中的 lane。
            string excludedMaskVar = null;
            // excludedCount = excludedMaskVar 已包含的条件个数（= conditions.Count 在上一分支时的值）
            int excludedCount = 0;

            foreach (var name in AnalyzeModifiedVars(stmt))
                PredeclareBranchVar(name);

            while (true)
            {
                string condExpr = TranslateCondition(current.Condition);

                // Docs-style uniform scalar: invert bad condition, narrow mask, continue when all dead.
                //   Skip __cond_N, skip savedMask (all_true is redundant), go straight to __good_N.
                if (_isUniformScalarLoop && IsSingleContinue(current.Statement))
                {
                    string goodExpr = $"simd_mask{{ n_not_mask(({condExpr}).m) }}";
                    string goodName = $"__good_{_labelCounter++}";
                    AppendLine($"simd_mask {goodName} = {goodExpr};");
                    // Combine with previous mask to narrow unfound lanes
                    string prev = string.IsNullOrEmpty(savedMask) ? _currentMask : savedMask;
                    if (prev != "simd_mask::all_true()")
                    {
                        string combined = $"simd_mask{{ n_and_mask({prev}.m, {goodName}.m) }}";
                        AppendLine($"{goodName} = {combined};");
                    }
                    _currentMask = goodName;
                    // inside an UNROLLED loop there is no real C++ loop, so a
                    //   `continue;` would jump to the outermost batch loop (for si), skipping
                    //   the remaining unrolled iterations AND the final store — output all zeros.
                    //   Unrolled-loop frames have TrackerVar == "" (see GenerateUnrolledLoop);
                    //   real scalar-for frames carry a non-empty saved-mask tracker.
                    if (_loopStack.Count > 0 && string.IsNullOrEmpty(_loopStack.Peek().TrackerVar))
                    {
                        string contTarget = _loopStack.Peek().ContinueLabel;
                        _gotoTargets.Add(contTarget);
                        AppendLine($"if (!{_currentMask}.any_true()) {{ goto {contTarget}; }}");
                    }
                    else
                    {
                        AppendLine($"if (!{_currentMask}.any_true()) {{ continue; }}");
                    }
                    savedMask = goodName;
                }
                else
                {
                    bool isAllTrue = _currentMask == "simd_mask::all_true()";

                    // When all_true: skip emitting savedMask, but register it for restore
                    if (!isAllTrue)
                    {
                        if (!savedMaskEmitted)
                        {
                            if (_currentMask.StartsWith("v_") || _currentMask.StartsWith("__"))
                            {
                                savedMask = _currentMask;
                            }
                            else
                            {
                                savedMask = $"__mask_{_maskCounter++}";
                                AppendLine($"simd_mask {savedMask} = {_currentMask};");
                            }
                            savedMaskEmitted = true;
                        }
                    }
                    else
                    {
                        // Sentinel: no emit needed, but restore will set _currentMask=all_true()
                        savedMask = "simd_mask::all_true()";
                    }

                    string trueMask;
                    if (isAllTrue)
                    {
                        // All lanes active on entry: the branch mask is JUST the condition
                        // (no savedMask AND needed). The condition MUST still be emitted —
                        // combine as all_true & !c0 & c1 (not c0 & !c0 & c1 = false).
                        savedMask = "simd_mask::all_true()";
                        savedMaskEmitted = true;
                        string condVar = $"__cond_{_maskCounter++}";
                        AppendLine($"simd_mask {condVar} = {condExpr};");
                        trueMask = condVar;
                        conditions.Add(condVar);
                        _currentMask = trueMask;
                    }
                    else
                    {
                        string condVar = $"__cond_{_maskCounter++}";
                        AppendLine($"simd_mask {condVar} = {condExpr};");

                        if (isAllTrue)
                            trueMask = condVar;
                        else if (conditions.Count == 0)
                            trueMask = $"simd_mask{{ n_and_mask({savedMask}.m, {condVar}.m) }}";
                        else
                        {
                            EnsureExcludedMaskUpTo(conditions, ref excludedMaskVar, ref excludedCount);
                            string notPrev = excludedMaskVar ?? "simd_mask::all_true()";
                            trueMask = $"simd_mask{{ n_and_mask({notPrev}.m, {condVar}.m) }}";
                            // Skip redundant AND when savedMask is all_true: all_true & X == X
                            if (savedMask != "simd_mask::all_true()")
                                trueMask = $"simd_mask{{ n_and_mask({savedMask}.m, {trueMask}.m) }}";
                        }

                        conditions.Add(condVar);
                        _currentMask = trueMask;
                        if (trueMask.Contains("n_and_mask("))
                        {
                            string cm = $"__cm_{_maskCounter++}";
                            AppendLine($"simd_mask {cm} = {trueMask};");
                            _currentMask = cm;
                        }
                    }
                    bool bodyEmpty = current.Statement is BlockSyntax blk && blk.Statements.Count == 0;
                    bool maskContinue = _loopStack.Count > 0 && !string.IsNullOrEmpty(_loopStack.Peek().BodyMaskVar);
                    bool bodyHasGoto = !bodyEmpty && HasControlFlowGoto(current.Statement, maskContinue);
                    if (bodyHasGoto)
                        AppendLine($"if ({trueMask}.any_true())");
                    if (!bodyEmpty)
                    {
                        GenerateBlock(EnsureBlock(current.Statement), skipBraces: false);
                    }
                }

                if (current.Else == null) break;

                if (current.Else.Statement is IfStatementSyntax nextIf)
                {
                    current = nextIf;
                }
                else
                {
                    elseBody = current.Else.Statement;
                    break;
                }
            }

            // Final else: precompute else mask and emit as variable
            if (elseBody != null)
            {
                string elseMaskExpr;
                // 优先复用递推排除变量（~c0 & ... & ~c_{k-1}），避免内联 O(N) 嵌套。
                //   else 分支需排除全部条件 → 确保递推变量覆盖到 conditions.Count。
                EnsureExcludedMaskUpTo(conditions, ref excludedMaskVar, ref excludedCount);
                string notChain = excludedMaskVar ?? "simd_mask::all_true()";
                if (savedMaskEmitted)
                {
                    // Skip redundant AND when savedMask is all_true: all_true & X == X
                    if (savedMask == "simd_mask::all_true()")
                        elseMaskExpr = notChain;
                    else
                        elseMaskExpr = $"simd_mask{{ n_and_mask({savedMask}.m, {notChain}.m) }}";
                }
                else if (savedMask == "simd_mask::all_true()")
                    elseMaskExpr = notChain;
                else
                    elseMaskExpr = _currentMask;

                // Precompute else mask as a variable (avoids repeated inline computation)
                string elseMaskVar = $"__else_mask_{_maskCounter++}";
                AppendLine($"simd_mask {elseMaskVar} = {elseMaskExpr};");
                _currentMask = elseMaskVar;

                bool elseBodyEmpty = elseBody is BlockSyntax elseBlk && elseBlk.Statements.Count == 0;
                bool maskContinue = _loopStack.Count > 0 && !string.IsNullOrEmpty(_loopStack.Peek().BodyMaskVar);
                bool elseHasGoto = !elseBodyEmpty && HasControlFlowGoto(elseBody, maskContinue);
                if (elseHasGoto)
                    AppendLine($"if ({elseMaskVar}.any_true())");
                if (!elseBodyEmpty)
                {
                    GenerateBlock(EnsureBlock(elseBody), skipBraces: false);
                }
            }

            // Restore (skip if mask was never saved — e.g., dead-false continue)
            if (savedMaskEmitted || !string.IsNullOrEmpty(savedMask))
                _currentMask = savedMask;
            // A branch may have removed lanes through break/continue. Restoring
            // its entry mask must not reactivate those lanes for later statements.
            if (_loopStack.Count > 0 && !string.IsNullOrEmpty(_loopStack.Peek().BodyMaskVar)
                && _currentMask != _loopStack.Peek().BodyMaskVar)
            {
                string bodyMask = _loopStack.Peek().BodyMaskVar;
                string postMask = $"__post_if_{_maskCounter++}";
                AppendLine($"simd_mask {postMask} = simd_mask{{ n_and_mask({_currentMask}.m, {bodyMask}.m) }};");
                _currentMask = postMask;
            }
        }

        /// <summary>
        /// 确保递推排除变量 excludedMaskVar 已覆盖 ~c0 & ~c1 & ... & ~c_{k-1}
        /// （conditions 中的全部历史条件 —— 调用时 conditions 为已加入条件，不含当前分支条件；
        /// else 分支时含全部条件，语义相同：排除它们全部）。增量式：每个条件只生成一次
        /// n_and_mask，分支掩码深度从 O(N) 降到 O(1)，总生成量保持 O(N)。
        /// </summary>
        private void EnsureExcludedMaskUpTo(List<string> conditions, ref string excludedMaskVar, ref int excludedCount)
        {
            int target = conditions.Count;
            if (target == 0) return;

            if (excludedCount == 0)
            {
                // 首次：生成 ~c0
                excludedMaskVar = $"__excl_{_maskCounter++}";
                AppendLine($"simd_mask {excludedMaskVar} = simd_mask{{ n_not_mask({conditions[0]}.m) }};");
                excludedCount = 1;
            }
            // 增量扩展：从 excludedCount 到 target，逐个 AND ~c_i
            for (int i = excludedCount; i < target; i++)
            {
                string newExcl = $"__excl_{_maskCounter++}";
                AppendLine($"simd_mask {newExcl} = simd_mask{{ n_and_mask({excludedMaskVar}.m, simd_mask{{ n_not_mask({conditions[i]}.m) }}.m) }};");
                excludedMaskVar = newExcl;
                excludedCount = i + 1;
            }
        }

        /// <summary>检查 if 语句体是否是单条的 continue;</summary>
        private static bool IsSingleContinue(StatementSyntax stmt)
        {
            if (stmt is ContinueStatementSyntax) return true;
            if (stmt is BlockSyntax blk && blk.Statements.Count == 1
                && blk.Statements[0] is ContinueStatementSyntax) return true;
            return false;
        }

        // Save-blend: precise variable modification analysis

        /// <summary>
        /// Analyze an if-else chain and collect all variables that are written in any branch.
        /// </summary>
        private HashSet<string> AnalyzeModifiedVars(IfStatementSyntax ifStmt)
        {
            var modified = new HashSet<string>();
            var current = ifStmt;

            while (true)
            {
                CollectWrites(current.Statement, modified);
                if (current.Else == null) break;
                if (current.Else.Statement is IfStatementSyntax nextIf)
                    current = nextIf;
                else
                {
                    CollectWrites(current.Else.Statement, modified);
                    break;
                }
            }
            return modified;
        }

        /// <summary>
        /// Recursively collect variable write targets from a statement.
        /// Handles: assignments (a = ..., a += ..., a ^= ...), ++/--, ref parameters (conservative).
        /// </summary>
        private static void CollectWrites(StatementSyntax stmt, HashSet<string> vars)
        {
            foreach (var node in stmt.DescendantNodesAndSelf())
            {
                if (node is AssignmentExpressionSyntax assign && assign.Left is IdentifierNameSyntax id)
                    vars.Add(id.Identifier.Text);
                else if (node is PostfixUnaryExpressionSyntax postfix && postfix.Operand is IdentifierNameSyntax id2)
                    vars.Add(id2.Identifier.Text);
                // Prefix unary: ONLY ++a / --a are writes. Unary minus (-a) / !a / ~a are reads —
                //   treating them as writes caused redundant save/blend (e.g. else v = -a * 3f
                //   wrongly saved+blended v_a even though a is never modified).
                else if (node is PrefixUnaryExpressionSyntax prefix
                    && prefix.Operand is IdentifierNameSyntax id3
                    && (prefix.OperatorToken.IsKind(SyntaxKind.PlusPlusToken)
                        || prefix.OperatorToken.IsKind(SyntaxKind.MinusMinusToken)))
                    vars.Add(id3.Identifier.Text);
            }
        }

        /// <summary>Pre-declare a branch-written varying var at the outer scope.</summary>
        private void PredeclareBranchVar(string name)
        {
            if (name == _indexParamName || _forLoopVars.Contains(name)) return;
            if (!_variables.TryGetValue(name, out var mInfo)) return;
            if (mInfo.Kind < VarKind.Varying || _varDeclEmitted.Contains(name)) return;
            string vType = GetSIMDTypeString(mInfo.CppType);
            if (vType == null) return;
            // 初始化为 broadcast(0)：移除 save-blend 后，首个分支的 blend 会读旧值；
            //   未初始化的 varying 局部变量（float r; 由 if/else 定值）需先归零避免读垃圾。
            AppendLine($"{vType} v_{name} = {(mInfo.CppType == "float" ? "0.0f" : "0")};");
            _varDeclEmitted.Add(name);
            _simdVaryingVarNames.Add(name);
            _simdVaryingCppType[name] = mInfo.CppType;
        }
    }
}
