using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 通用 ISPC 风格 SIMD 控制流生成器。
    /// 从 C# AST 直接生成 mask-managed SIMD C++ 代码。
    ///
    /// 设计原则：
    /// - 所有循环统一使用 for(iter) 计数循环模式（已验证 e2d27a4）
    /// - if/else → mask push/pop
    /// - break → kill lane via simd_tracker
    /// - continue → goto body-end label
    /// - return → goto __simd_func_exit
    /// - 表达式翻译根据 uniform/varying 分类选择 SIMD 或标量操作
    /// </summary>
    public class SimdControlFlowGenerator
    {
        private readonly StringBuilder _builder = new();
        private int _indent;

        private readonly SemanticModel _semanticModel;
        private readonly INamedTypeSymbol _jobStruct;
        private readonly Dictionary<string, SimdVariableInfo> _variables;
        private readonly SimdVariableAnalyzer _varAnalyzer;
        private readonly string _indexParamName;
        private readonly string _simdIndexVar; // "v_i" — the SIMD index variable from outer loop
        private readonly bool _useFastMath;
        private readonly NativeTranspiler.SimdMathPrecision _simdMathPrecision;

        // Mask management
        private string _currentMask = "simd_mask::all_true()";
        private int _maskCounter;
        private int _labelCounter;
        private bool _isUniformScalarLoop;
        // For-loop induction variables (skip in GenerateVariableDeclarations)
        private readonly HashSet<string> _forLoopVars = new();
        // Induction variables from uniform-bound reduction loops (for broadcast optimization)
        private readonly HashSet<string> _uniformLoopVars = new();

        // Loop tracking (for break/continue)
        private struct LoopFrame
        {
            public string TrackerVar;     // simd_tracker variable name
            public string IterActiveVar;  // iter_active variable name
            public string ExitLabel;      // goto label for break
            public string ContinueLabel;  // goto label for continue
        }
        private readonly Stack<LoopFrame> _loopStack = new();

        // Variable naming tracking for float2 component decomposition
        private readonly HashSet<string> _float2VaryingVars = new();

        // Bool field name→literal mapping for dead-branch elimination
        private readonly Dictionary<string, string> _boolFields;
        // Varying reduction loop context for inner-loop gather optimization
        private bool _inVaryingReductionLoop;
        private string? _hoistedSafeMaxVar;
        private string? _hoistedSafeMaxExpr;
        // Variable tracking for per-lane region transitions (save/merge)
        private readonly HashSet<string> _simdVaryingVarNames = new();
        private readonly Dictionary<string, string> _simdVaryingCppType = new();
        // Track which varying locals have had their type declaration emitted (for scope narrowing)
        private readonly HashSet<string> _varDeclEmitted = new();
        // Hoisted uniform broadcasts: "fieldName.comp" → "varName"
        private readonly Dictionary<string, string> _uniformHoistMap = new();
        // Pending broadcasts: "varName" → "simd_value<T> varName = ...";
        private readonly Dictionary<string, string> _uniformHoistPending = new();
        // Batch offset variable ("si" for OuterSimdGenerator batch, "0" for standalone IJob/static)
        private readonly string _batchOffsetVar;
        // NativeArray parameter names → elemCppType (for static methods without job struct)
        private readonly Dictionary<string, string> _nativeArrayParams;
        // Batch loop variable name (e.g. "si") for contiguous access optimization
        private readonly string _batchLoopVar;
        // Early-exit sentinel for reduction loops (from scalar body write pattern, e.g. "bestIdx"/"-1")
        internal string _sentinelVar = "";
        internal string _sentinelVal = "";
        // Track goto targets for dead label elimination (suppress labels in hot paths)
        private readonly HashSet<string> _gotoTargets = new();
        // Reduction folding: when set, TranslateAssignment uses n_min_ps/n_max_ps instead of blend
        private string _foldReduceFn = null;
        // Variables whose last value came from a clamped gather — skip redundant clamp
        private readonly HashSet<string> _clampedVars = new();
        // Struct varying locals: localName → (arrayName, elemCppType, indexExpr)
        // These are struct-typed locals initialized from array[idx] where array is a struct-typed NativeArray.
        // Instead of creating a SIMD register for the whole struct, field accesses (temp.Field) are
        // decomposed into field-level gather/scatter at code-gen time (ISPC-style).
        private readonly Dictionary<string, (string arrName, string elemCppType, string indexExpr)> _structVaryingLocals = new();

        public SimdControlFlowGenerator(
            SemanticModel semanticModel,
            INamedTypeSymbol jobStruct,
            Dictionary<string, SimdVariableInfo> variables,
            SimdVariableAnalyzer varAnalyzer,
            string indexParamName = "index",
            string simdIndexVar = "v_i",
            bool useFastMath = false,
            Dictionary<string, string>? boolFields = null,
            string batchOffsetVar = "si",
            NativeTranspiler.SimdMathPrecision simdMathPrecision = NativeTranspiler.SimdMathPrecision.Fastest,
            Dictionary<string, string>? nativeArrayParams = null,
            string batchLoopVar = "")
        {
            _semanticModel = semanticModel;
            _jobStruct = jobStruct;
            _variables = variables;
            _varAnalyzer = varAnalyzer;
            _indexParamName = indexParamName;
            _simdIndexVar = simdIndexVar;
            _useFastMath = useFastMath;
            _simdMathPrecision = simdMathPrecision;
            _boolFields = boolFields ?? new Dictionary<string, string>();
            _batchOffsetVar = batchOffsetVar;
            _nativeArrayParams = nativeArrayParams ?? new Dictionary<string, string>();
            _batchLoopVar = batchLoopVar;

            // Pre-identify float2/int2 variables that are varying
            foreach (var kvp in _variables)
            {
                if (kvp.Value.Kind >= VarKind.Varying)
                {
                    var cppType = kvp.Value.CppType;
                    if (cppType.Contains("float2") || cppType.Contains("int2"))
                        _float2VaryingVars.Add(kvp.Key);
                }
            }
        }

        // ================================================================
        // Entry Point
        // ================================================================

        /// <summary>
        /// 从 Execute 方法体生成 SIMD C++ 代码。
        /// varying-bound 循环 → per-lane full body（已验证正确）
        /// uniform 循环 → 全 SIMD mask 模式
        /// </summary>
        public string Generate(BlockSyntax body)
        {
            _builder.Clear();
            _forLoopVars.Clear();
            foreach (var fs in body.DescendantNodes().OfType<ForStatementSyntax>())
                if (fs.Declaration != null)
                    foreach (var v in fs.Declaration.Variables)
                        _forLoopVars.Add(v.Identifier.Text);

            // ★ Prescan: find variables assigned from gather/gathf with clamp (skip redundant clamp)
            _clampedVars.Clear();
            foreach (var assign in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                if (assign.Left is IdentifierNameSyntax lhs && assign.Right is InvocationExpressionSyntax inv)
                    if (IsGatherCall(inv))
                        _clampedVars.Add(lhs.Identifier.Text);
            foreach (var decl in body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                foreach (var v in decl.Declaration.Variables)
                    if (v.Initializer?.Value is InvocationExpressionSyntax inv2 && IsGatherCall(inv2))
                        _clampedVars.Add(v.Identifier.Text);

            // ★ Enhanced: reduction loops use count-loop, only non-reduction varying → per-lane
            if (HasVaryingNonReductionLoop(body))
                GeneratePerLaneFullBody(body);
            else {
                GenerateVariableDeclarations();
                // ★ Pre-scan and emit hoisted uniform broadcasts (GridDimensions.x/y etc.)
                PreScanUniformHoists(body);
                EmitUniformHoistPrologue();
                GenerateBlock(body, skipBraces: true);
            }

            return _builder.ToString();
        }

        /// <summary>
        /// 检查方法体中是否有边界 varying 的 for 循环。
        /// 如果有，整个 body 走 per-lane 避免 SIMD mask/gather 的开销。
        /// </summary>
        private bool HasVaryingBoundsLoop(SyntaxNode node)
        {
            foreach (var fs in node.DescendantNodes().OfType<ForStatementSyntax>())
            {
                if (fs.Declaration == null || fs.Declaration.Variables.Count != 1) continue;
                var decl = fs.Declaration.Variables[0];
                if (decl.Initializer != null && _varAnalyzer.ClassifyExpression(decl.Initializer.Value) >= VarKind.Varying)
                    return true;
                if (fs.Condition is BinaryExpressionSyntax cond)
                {
                    if (_varAnalyzer.ClassifyExpression(cond.Right) >= VarKind.Varying)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 检查方法体中是否有 varying-bound 且非 reduction 的 for 循环。
        /// reduction 循环可用 count-loop / broadcast 优化。
        /// </summary>
        private bool HasVaryingNonReductionLoop(SyntaxNode node)
        {
            foreach (var fs in node.DescendantNodes().OfType<ForStatementSyntax>())
            {
                if (fs.Declaration == null || fs.Declaration.Variables.Count != 1) continue;
                var decl = fs.Declaration.Variables[0];
                bool hasVaryingBounds = false;
                if (decl.Initializer != null && _varAnalyzer.ClassifyExpression(decl.Initializer.Value) >= VarKind.Varying)
                    hasVaryingBounds = true;
                if (fs.Condition is BinaryExpressionSyntax cond)
                {
                    if (_varAnalyzer.ClassifyExpression(cond.Right) >= VarKind.Varying)
                        hasVaryingBounds = true;
                }
                if (hasVaryingBounds && !IsReductionLoop(fs))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 检测 for 循环体是否 DIRECT 包含 reduction 模式（if(val &lt; best) { best = val; ... }）。
        /// 只考虑直接体中的 if，排除嵌套循环体内的 if。
        /// </summary>
        private bool IsReductionLoop(ForStatementSyntax stmt)
        {
            var body = stmt.Statement;
            foreach (var ifStmt in body.DescendantNodes().OfType<IfStatementSyntax>())
            {
                var closestFor = ifStmt.Ancestors().OfType<ForStatementSyntax>().FirstOrDefault();
                if (closestFor != stmt) continue;
                if (ifStmt.Condition is BinaryExpressionSyntax bin &&
                    (bin.IsKind(SyntaxKind.LessThanExpression) ||
                     bin.IsKind(SyntaxKind.GreaterThanExpression) ||
                     bin.IsKind(SyntaxKind.LessThanOrEqualExpression)))
                {
                    var trueBlock = ifStmt.Statement is BlockSyntax tb
                        ? tb.Statements.AsEnumerable()
                        : (IEnumerable<StatementSyntax>)new[] { ifStmt.Statement };
                    if (trueBlock.Any(s => s is ExpressionStatementSyntax es
                        && es.Expression is AssignmentExpressionSyntax))
                    {
                        bool accessesArray = body.DescendantNodes()
                            .OfType<ElementAccessExpressionSyntax>()
                            .Any(ea => ea.ArgumentList?.Arguments.Count > 0);
                        if (accessesArray) return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 生成 per-lane 全 body 版本 (ISPC foreach 风格)。
        /// 当 mask=all_true 时生成简约代码匹配参考 73549457 的 0.6ms。
        /// </summary>
        /// <summary>
        /// For varying-bound loops: pure scalar code matching remainder loop perf (~0.75ms).
        /// No SIMD gather — just for(lane) + direct NativeArray reads.
        /// </summary>
        private void GeneratePerLaneFullBody(BlockSyntax body)
        {
            var scalarTranslator = new CppBatchStatementTranslator(
                _semanticModel, _jobStruct, _indexParamName, _indexParamName,
                _useFastMath, false);
            string scalarBody = scalarTranslator.Translate(body);

            foreach (var kvp in _boolFields)
                scalarBody = System.Text.RegularExpressions.Regex.Replace(
                    scalarBody, $@"\b{System.Text.RegularExpressions.Regex.Escape(kvp.Key)}\b", kvp.Value);

            bool hasReturn = scalarBody.Contains("return;");
            if (hasReturn)
                scalarBody = scalarBody.Replace("return;", "break;");

            AppendLine("for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
            AppendLine("{");
            _indent++;
            AppendLine($"int {_indexParamName} = si + lane;");

            foreach (var line in scalarBody.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    AppendLine(line.TrimEnd());

            _indent--;
            AppendLine("}");
        }

        // ================================================================
        // Variable Declarations
        // ================================================================

        private void GenerateVariableDeclarations()
        {
            foreach (var kvp in _variables)
            {
                string name = kvp.Key;
                var info = kvp.Value;

                // Skip index parameter (it maps to v_i from outer loop)
                if (name == _indexParamName) continue;
                if (_forLoopVars.Contains(name)) continue;

                // Skip uniform variables (handled as scalar by outer scope)
                if (info.Kind == VarKind.Uniform) continue;

                // Varying or Reduction
                string varType = GetSIMDTypeString(info.CppType);
                if (varType == null) continue;

                // Default construct: all variables are immediately overwritten
                // (v_q=gather, v_cell=convert, v_bestDistSq=max, etc.)
                // broadcast(0) was wasted instructions & register pressure.
                if (info.InitSIMDExpr != null)
                {
                    AppendLine($"{varType} v_{name} = {info.InitSIMDExpr};");
                    _varDeclEmitted.Add(name);
                }
                else
                {
                    // Scope narrowing: defer declaration to first assignment site
                    // (reduces live-range overlap -> less register pressure -> better stability)
                }
                // Track for per-lane save/merge
                _simdVaryingVarNames.Add(name);
                _simdVaryingCppType[name] = info.CppType;
            }
        }

        // ================================================================
        // Uniform Broadcast Hoisting (generic, no hardcoded field names)
        // ================================================================

        /// <summary>
        /// Pre-scan body for .x/.y access on uniform int2/float2 job struct fields.
        /// These should be pre-broadcast once and reused, instead of re-broadcast at each use.
        /// Fully generic: applies to ANY job with ANY uniform int2/float2 fields.
        /// </summary>
        private void PreScanUniformHoists(SyntaxNode body)
        {
            _uniformHoistMap.Clear();
            _uniformHoistPending.Clear();

            foreach (var ma in body.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                string memberName = ma.Name.Identifier.Text;
                if (memberName != "x" && memberName != "y") continue;
                if (!(ma.Expression is IdentifierNameSyntax id)) continue;

                string fieldName = id.Identifier.Text;
                if (_jobStruct == null) continue;

                try
                {
                    var members = _jobStruct.GetMembers(fieldName);
                    if (members.Length == 0) continue;
                    if (!(members[0] is IFieldSymbol field) || field.IsStatic) continue;

                    string typeName = field.Type.Name;
                    string simdType, key;
                    if (typeName == "int2")
                    {
                        simdType = "simd_value<int>";
                        key = $"{fieldName}.{memberName}";
                    }
                    else if (typeName == "float2")
                    {
                        simdType = "simd_value<float>";
                        key = $"{fieldName}.{memberName}";
                    }
                    else continue;

                    if (_uniformHoistMap.ContainsKey(key)) continue;

                    string varName = $"__uni_{fieldName}_{memberName}";
                    _uniformHoistMap[key] = varName;
                    _uniformHoistPending[varName] = $"{simdType} {varName} = {simdType}::broadcast({fieldName}.{memberName}());";
                }
                catch { }
            }
        }

        /// <summary>Emit all hoisted uniform broadcasts in the prologue</summary>
        private void EmitUniformHoistPrologue()
        {
            if (_uniformHoistPending.Count == 0) return;
            AppendLine("// Hoisted uniform broadcasts");
            foreach (var kvp in _uniformHoistPending)
                AppendLine(kvp.Value);
        }

        // ================================================================
        // Statement Generators
        // ================================================================

        private void GenerateStatement(StatementSyntax stmt)
        {
            switch (stmt)
            {
                case BlockSyntax block:
                    GenerateBlock(block, skipBraces: false);
                    break;

                case LocalDeclarationStatementSyntax localDecl:
                    GenerateLocalDeclaration(localDecl);
                    break;

                case ForStatementSyntax forStmt:
                    GenerateForStatement(forStmt);
                    break;

                case WhileStatementSyntax whileStmt:
                    GenerateWhileStatement(whileStmt);
                    break;

                case DoStatementSyntax doStmt:
                    GenerateDoStatement(doStmt);
                    break;

                case IfStatementSyntax ifStmt:
                    GenerateIfStatement(ifStmt);
                    break;

                case ExpressionStatementSyntax exprStmt:
                    GenerateExpressionStatement(exprStmt);
                    break;

                case ReturnStatementSyntax returnStmt:
                    GenerateReturnStatement(returnStmt);
                    break;

                case BreakStatementSyntax _:
                    GenerateBreakStatement();
                    break;

                case ContinueStatementSyntax _:
                    GenerateContinueStatement();
                    break;

                case EmptyStatementSyntax _:
                    AppendLine(";");
                    break;

                default:
                    AppendLine($"// Unsupported SIMD statement: {stmt.Kind()}");
                    break;
            }
        }

        /// <summary>
        /// 生成代码块
        /// </summary>
        private void GenerateBlock(BlockSyntax block, bool skipBraces)
        {
            if (!skipBraces)
            {
                AppendLine("{");
                _indent++;
            }

            foreach (var stmt in block.Statements)
                GenerateStatement(stmt);

            if (!skipBraces)
            {
                _indent--;
                AppendLine("}");
            }
        }

        // ================================================================
        // IfStatement → mask push/pop
        // ================================================================

        private void GenerateIfStatement(IfStatementSyntax stmt)
        {
            // Collect all conditions and bodies (if / else-if chain)
            var conditions = new List<string>();
            var current = stmt;
            StatementSyntax? elseBody = null;
            string savedMask = "";
            bool savedMaskEmitted = false;

            while (true)
            {
                string condExpr = TranslateCondition(current.Condition);

                // ★ Dead condition detection (通用, 适用所有上下文)
                bool isDeadFalse = condExpr.Contains("n_cmp_ne_epi32(n_set1_epi32(0), n_set1_epi32(0))");
                bool isDeadTrue = condExpr == "simd_mask::all_true()";

                if (isDeadFalse)
                {
                    // 条件永远假 → 跳过整个 if-body
                    if (current.Else != null)
                    {
                        if (current.Else.Statement is IfStatementSyntax elseif)
                        {
                            current = elseif;
                            continue;
                        }
                        elseBody = current.Else.Statement;
                    }
                    break;
                }

                if (isDeadTrue)
                {
                    // 条件永远真 → 执行 if-body, 跳过所有 else
                    GenerateBlock(EnsureBlock(current.Statement), skipBraces: false);
                    break;
                }

                // ★ 空 if-true + else (非 else-if) → 反转条件, 直接走 else
                if (IsEmptyBlock(current.Statement) && current.Else != null
                    && !(current.Else.Statement is IfStatementSyntax))
                {
                    string negCond;
                    if (condExpr.Contains("n_cmp_ne_epi32"))
                        negCond = condExpr.Replace("n_cmp_ne_epi32", "n_cmp_eq_epi32");
                    else if (condExpr.Contains("n_cmp_eq_epi32"))
                        negCond = condExpr.Replace("n_cmp_eq_epi32", "n_cmp_ne_epi32");
                    else
                        negCond = $"simd_mask{{ n_not_mask({condExpr}.m) }}";

                    string cm = $"__cm_{_maskCounter++}";
                    if (_currentMask != "simd_mask::all_true()")
                        AppendLine($"simd_mask {cm} = simd_mask{{ n_and_mask({_currentMask}.m, {negCond}.m) }};");
                    else
                        AppendLine($"simd_mask {cm} = {negCond};");
                    string entryMask = _currentMask;
                    _currentMask = cm;
                    GenerateBlock(EnsureBlock(current.Else.Statement), skipBraces: false);
                    _currentMask = entryMask;
                    return;
                }

                // ★ Docs-style uniform scalar: invert bad condition, narrow mask, continue when all dead.
                //   Skip __cond_N, skip savedMask (all_true is redundant), go straight to __good_N.
                if (_isUniformScalarLoop && IsSingleContinue(current.Statement))
                {
                    // Dead conditions already handled above — condition is always non-trivial here
                    string goodExpr = condExpr
                        .Replace("n_cmp_lt_", "##TMP##")
                        .Replace("n_cmp_ge_", "n_cmp_lt_")
                        .Replace("n_cmp_gt_", "n_cmp_le_")
                        .Replace("n_cmp_le_", "n_cmp_gt_")
                        .Replace("n_cmp_eq_", "n_cmp_ne_")
                        .Replace("n_cmp_ne_", "n_cmp_eq_")
                        .Replace("##TMP##", "n_cmp_ge_");
                    string goodName = $"__good_{_labelCounter++}";
                    AppendLine($"simd_mask {goodName} = {goodExpr};");
                    // ★ Combine with previous mask to narrow unfound lanes
                    string prev = string.IsNullOrEmpty(savedMask) ? _currentMask : savedMask;
                    if (prev != "simd_mask::all_true()")
                    {
                        string combined = $"simd_mask{{ n_and_mask({prev}.m, {goodName}.m) }}";
                        AppendLine($"{goodName} = {combined};");
                    }
                    _currentMask = goodName;
                    AppendLine($"if (!{_currentMask}.any_true()) {{ continue; }}");
                    savedMask = goodName;
                }
                else
                {
                    bool isAllTrue = _currentMask == "simd_mask::all_true()";

                    // ★ When all_true: skip emitting savedMask, but register it for restore
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

                    bool simpleCmp = condExpr.Contains("n_cmp_") && !condExpr.Contains("n_and_mask(") && !condExpr.Contains("n_not_mask(");

                    string trueMask;
                    if (isAllTrue)
                    {
                        trueMask = "simd_mask::all_true()";
                    }
                    else if (conditions.Count == 0 && simpleCmp && savedMask.Contains("v_act"))
                    {
                        // Inline simple condition into __cm_N, skip __cond_N
                        string cm = $"__cm_{_maskCounter++}";
                        AppendLine($"simd_mask {cm} = simd_mask{{ n_and_mask({savedMask}.m, {condExpr}.m) }};");
                        trueMask = cm;
                        _currentMask = cm;
                    }
                    else
                    {
                        // ★ Folding: if (d < best) best = d → n_min_ps / n_max_ps
                        if ((isAllTrue || savedMaskEmitted)
                            && TryFoldReduction(condExpr, current.Statement, out var foldFn))
                        {
                            _foldReduceFn = foldFn;
                            GenerateBlock(EnsureBlock(current.Statement), skipBraces: false);
                            _foldReduceFn = null;
                            current = null;
                            break; // skip mask push/pop
                        }

                        string condVar = $"__cond_{_maskCounter++}";
                        AppendLine($"simd_mask {condVar} = {condExpr};");

                        if (isAllTrue)
                            trueMask = condVar;
                        else if (conditions.Count == 0)
                            trueMask = $"simd_mask{{ n_and_mask({savedMask}.m, {condVar}.m) }}";
                        else
                        {
                            string notPrev = BuildNotChain(conditions);
                            trueMask = $"simd_mask{{ n_and_mask({notPrev}.m, {condVar}.m) }}";
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
                    if (!bodyEmpty && HasControlFlowGoto(current.Statement))
                        AppendLine($"if ({trueMask}.any_true())");
                    if (!bodyEmpty)
                        GenerateBlock(EnsureBlock(current.Statement), skipBraces: false);
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

            // Final else: saved & ~all_conds
            if (elseBody != null)
            {
                if (savedMaskEmitted)
                    _currentMask = $"simd_mask{{ n_and_mask({savedMask}.m, {BuildNotChain(conditions)}.m) }}";
                else if (savedMask == "simd_mask::all_true()")
                    _currentMask = BuildNotChain(conditions); // all_true AND X → just X
                // else: dead-false — _currentMask unchanged, use directly
                string elseMask = _currentMask;
                bool elseBodyEmpty = elseBody is BlockSyntax elseBlk && elseBlk.Statements.Count == 0;
                if (!elseBodyEmpty && HasControlFlowGoto(elseBody))
                    AppendLine($"if ({elseMask}.any_true())");
                if (!elseBodyEmpty)
                    GenerateBlock(EnsureBlock(elseBody), skipBraces: false);
            }

            // Restore (skip if mask was never saved — e.g., dead-false continue)
            if (savedMaskEmitted || !string.IsNullOrEmpty(savedMask))
                _currentMask = savedMask;
        }

        /// <summary>
        /// 构建 ~c0 & ~c1 & ~c2 链
        /// </summary>
        private string BuildNotChain(List<string> condVars)
        {
            if (condVars.Count == 0)
                return "simd_mask::all_true()";
            if (condVars.Count == 1)
                return $"simd_mask{{ n_not_mask({condVars[0]}.m) }}";

            // ~c0 & ~c1 = n_and_mask(n_not_mask(c0), n_not_mask(c1))
            string expr = $"simd_mask{{ n_not_mask({condVars[0]}.m) }}";
            for (int i = 1; i < condVars.Count; i++)
                expr = $"simd_mask{{ n_and_mask({expr}.m, simd_mask{{ n_not_mask({condVars[i]}.m) }}.m) }}";
            return expr;
        }

        /// <summary>检查 if 语句体是否是单条的 continue;</summary>
        private static bool IsSingleContinue(StatementSyntax stmt)
        {
            if (stmt is ContinueStatementSyntax) return true;
            if (stmt is BlockSyntax blk && blk.Statements.Count == 1
                && blk.Statements[0] is ContinueStatementSyntax) return true;
            return false;
        }

        /// <summary>检查 if 语句体是否为空</summary>
        private static bool IsEmptyBlock(StatementSyntax stmt)
        {
            if (stmt is BlockSyntax blk && blk.Statements.Count == 0) return true;
            if (stmt is EmptyStatementSyntax) return true;
            return false;
        }

        /// <summary>
        /// Detect if this if-statement is a simple reduction pattern like if(d<best) best=d
        /// that can be folded to n_min_ps/n_max_ps instead of mask+blend.
        /// </summary>
        private static bool TryFoldReduction(string condExpr, StatementSyntax body, out string foldFn)
        {
            foldFn = null;
            // Condition must be simple comparison: simd_mask{ n_cmp_lt_ps(lhs.v, rhs.v) }
            bool isLt = condExpr.Contains("n_cmp_lt_ps(");
            bool isGt = condExpr.Contains("n_cmp_gt_ps(");
            if (!isLt && !isGt) return false;
            foldFn = isLt ? "n_min_ps" : "n_max_ps";

            // Body must be a block with single statement that is an assignment
            var stmts = body is BlockSyntax blk ? blk.Statements : new SyntaxList<StatementSyntax>(body);
            if (stmts.Count != 1) return false;
            if (!(stmts[0] is ExpressionStatementSyntax ess) || !(ess.Expression is AssignmentExpressionSyntax))
                return false;
            return true;
        }

        /// <summary>
        /// 查找 varying reduction 循环中的主 gather 数组，用于 broadcast len-1 提升
        /// </summary>
        private string? FindSortedLengthVar(ForStatementSyntax stmt)
        {
            foreach (var elem in stmt.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
            {
                if (elem.Expression is IdentifierNameSyntax arrId &&
                    elem.ArgumentList?.Arguments.Count > 0)
                {
                    string arrName = arrId.Identifier.Text;
                    var idxExpr = elem.ArgumentList.Arguments[0].Expression;
                    VarKind idxKind = _varAnalyzer.ClassifyExpression(idxExpr);
                    if (idxKind >= VarKind.Varying)
                    {
                        _hoistedSafeMaxExpr = arrName;
                        return arrName + "_length";
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 检查语句中是否包含会翻译为 goto 的控制流（break/continue/return）。
        /// 如果为 false，则 if-body 只包含 blend/赋值操作，any_true() 守卫是冗余的。
        /// </summary>
        private static bool HasControlFlowGoto(SyntaxNode node)
        {
            foreach (var child in node.DescendantNodesAndSelf())
            {
                if (child is BreakStatementSyntax or ContinueStatementSyntax or ReturnStatementSyntax)
                    return true;
            }
            return false;
        }

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

        // ================================================================
        // Break / Continue / Return
        // ================================================================

        private void GenerateBreakStatement()
        {
            if (_loopStack.Count == 0)
            {
                AppendLine("// break outside loop — ignoring in SIMD context");
                return;
            }
            var frame = _loopStack.Peek();
            _gotoTargets.Add(frame.ExitLabel);
            AppendLine($"{frame.TrackerVar} = {frame.TrackerVar} & ~{frame.IterActiveVar};");
            AppendLine($"goto {frame.ExitLabel};");
        }

        private void GenerateContinueStatement()
        {
            if (_loopStack.Count == 0)
            {
                AppendLine("// continue outside loop — ignoring");
                return;
            }
            var frame = _loopStack.Peek();
            _gotoTargets.Add(frame.ContinueLabel);
            // ★ Docs-style scalar for loop: mask already narrowed by if-body's early exit.
            //   The `if(!active.any_true()) continue;` is emitted inline by GenerateIfStatement.
            //   We just emit the goto for the loop frame (used by tracker-based while-true loops).
            if (string.IsNullOrEmpty(frame.TrackerVar))
            {
                // No tracker → scalar for loop → goto is actually a real C++ continue target
                // The goto is inside an `any_true()` guard, so it's fine
                AppendLine($"goto {frame.ContinueLabel};");
            }
            else
            {
                // While-true mask loop: kill current lanes from tracker before goto
                AppendLine($"{frame.TrackerVar} = {frame.TrackerVar} & simd_mask{{ n_not_mask({_currentMask}.m) }};");
                AppendLine($"goto {frame.ContinueLabel};");
            }
        }

        private void GenerateReturnStatement(ReturnStatementSyntax stmt)
        {
            // return → goto __simd_func_exit (exit current batch)
            AppendLine("goto __simd_exit;");
        }

        // ================================================================
        // Local Declaration
        // ================================================================

        private void GenerateLocalDeclaration(LocalDeclarationStatementSyntax stmt)
        {
            foreach (var variable in stmt.Declaration.Variables)
            {
                string name = variable.Identifier.Text;

                                // ★ [Priority] Struct-typed local initialized from chunk array element access.
                //   e.g., MoveVelocity velocity = velocities[index];
                //   → defer to _structVaryingLocals for field-level decomposition.
                //   Check BEFORE the general Varying check because the variable analyzer
                //   may classify it as Varying (via PropagateAssignments from the index)
                //   but with wrong CppType ("int" for unknown struct types).
                if (variable.Initializer?.Value is ElementAccessExpressionSyntax structInitEA
                    && structInitEA.Expression is IdentifierNameSyntax structInitArrId
                    && _nativeArrayParams.TryGetValue(structInitArrId.Identifier.Text, out var structInitElemType)
                    && structInitElemType != "float" && structInitElemType != "int"
                    && !structInitElemType.Contains("float2") && !structInitElemType.Contains("int2"))
                {
                    string idxExpr = structInitEA.ArgumentList?.Arguments.Count > 0
                        ? TranslateExpression(structInitEA.ArgumentList.Arguments[0].Expression)
                        : "0";
                    _structVaryingLocals[name] = (structInitArrId.Identifier.Text, structInitElemType, idxExpr);
                    continue;
                }

                if (_variables.TryGetValue(name, out var info) && info.Kind >= VarKind.Varying)
                {
                    string varType = GetSIMDTypeString(info.CppType);
                    if (varType == null) continue;

                    if (variable.Initializer != null)
                    {
                        string initExpr = TranslateExpression(variable.Initializer.Value);
                        {
                            _varDeclEmitted.Add(name);
                            _simdVaryingVarNames.Add(name);
                            _simdVaryingCppType[name] = info.CppType;
                            if (initExpr.Contains("simd_min") && initExpr.Contains("simd_max"))
                                _clampedVars.Add($"v_{name}");
                            AppendLine($"{varType} v_{name} = {initExpr};");
                        }
                    }
                }
                else
                {
                    // Uniform local — emit as scalar// Uniform local — emit as scalar
                    // Try semantic model first (may fail on SyntaxFactory-created AST nodes)
                    string cppType = "float";
                    try
                    {
                        var typeInfo = _semanticModel.GetTypeInfo(stmt.Declaration.Type);
                        if (typeInfo.Type != null)
                            cppType = NativeTranspiler.MapCSharpTypeToCpp(typeInfo.Type);
                    }
                    catch
                    {
                        // Fallback: use type from variable analyzer or default to "float"
                        if (_variables.TryGetValue(name, out var vInfo) && !string.IsNullOrEmpty(vInfo.CppType))
                            cppType = vInfo.CppType;
                        else if (stmt.Declaration.Type is PredefinedTypeSyntax pts)
                            cppType = pts.Keyword.Text; // "float", "int", "double", etc.
                    }

                    if (variable.Initializer != null)
                    {
                        string initExpr = TranslateExpression(variable.Initializer.Value);
                        // ★ ISPC-style SIMD promotion: when a Uniform local is initialized from a
                        //   SIMD register expression (e.g. gather result), promote to Varying and
                        //   emit a SIMD register declaration instead of extracting lane 0.
                        //   This keeps the data in SIMD registers throughout the computation chain.
                        bool isSimdExpr = initExpr.StartsWith("simd_")
                            || initExpr.StartsWith("(simd_")
                            || initExpr.StartsWith("(v_")
                            || (initExpr.Contains("simd_value<") && initExpr.Contains("n_"))
                            || initExpr.Contains("n_gather_ps<")
                            || initExpr.Contains("n_sin_ps")
                            || initExpr.Contains("n_cos_ps")
                            || initExpr.Contains("n_sqrt_ps");
                        if (isSimdExpr)
                        {
                            string simdType = GetSIMDTypeString(cppType);
                            if (simdType != null)
                            {
                                _varDeclEmitted.Add(name);
                                _simdVaryingVarNames.Add(name);
                                _simdVaryingCppType[name] = cppType;
                                if (!_variables.ContainsKey(name))
                                    _variables[name] = new SimdVariableInfo { Name = name, Kind = VarKind.Varying, CppType = cppType };
                                else
                                    _variables[name].Kind = VarKind.Varying;
                                AppendLine($"{simdType} v_{name} = {initExpr};");
                                continue;
                            }
                        }
                        AppendLine($"{cppType} {name} = {initExpr};");
                    }
                    else
                    {
                        AppendLine($"{cppType} {name};");
                    }
                }
            }
        }

        // ================================================================
        // Expression Statement
        // ================================================================

        private void GenerateExpressionStatement(ExpressionStatementSyntax stmt)
        {
            string expr = TranslateExpression(stmt.Expression);
            if (!string.IsNullOrEmpty(expr))
                AppendLine($"{expr};");
        }

        // ================================================================
        // Expression Translation (core)
        // ================================================================

        /// <summary>
        /// 将 C# 表达式翻译为 C++ 表达式字符串。
        /// 返回的字符串可能是标量（uniform）或 simd_value（varying）。
        /// </summary>
        private string TranslateExpression(ExpressionSyntax expr)
        {
            switch (expr)
            {
                case LiteralExpressionSyntax literal:
                    return TranslateLiteral(literal);

                case IdentifierNameSyntax identifier:
                    return TranslateIdentifier(identifier);

                case MemberAccessExpressionSyntax memberAccess:
                    return TranslateMemberAccess(memberAccess);

                case ElementAccessExpressionSyntax elementAccess:
                    return TranslateElementAccess(elementAccess);

                case InvocationExpressionSyntax invocation:
                    return TranslateInvocation(invocation);

                case BinaryExpressionSyntax binary:
                    return TranslateBinary(binary);

                case PrefixUnaryExpressionSyntax prefix:
                    // For !, -, etc.
                    if (prefix.IsKind(SyntaxKind.LogicalNotExpression))
                        return $"simd_mask{{ n_not_mask({TranslateExpression(prefix.Operand)}.m) }}";
                    return $"-{TranslateExpression(prefix.Operand)}";

                case ParenthesizedExpressionSyntax paren:
                    return $"({TranslateExpression(paren.Expression)})";

                case CastExpressionSyntax cast:
                    return TranslateCast(cast);

                case AssignmentExpressionSyntax assign:
                    return TranslateAssignment(assign);

                case ConditionalExpressionSyntax ternary:
                    return TranslateTernary(ternary);

                case ObjectCreationExpressionSyntax objCreation:
                    return TranslateObjectCreation(objCreation);

                default:
                    return $"/* unsupported expr: {expr.Kind()} */ 0";
            }
        }

        /// <summary>
        /// 将条件表达式翻译为 simd_mask。
        /// </summary>
        private string TranslateCondition(ExpressionSyntax expr)
        {
            if (expr is IdentifierNameSyntax id)
            {
                string name = id.Identifier.Text;
                // ★ Bool field with known constant → skip computation, MSVC handles DCE
                if (_boolFields.TryGetValue(name, out var bv))
                    return bv == "true" ? "simd_mask::all_true()" : "simd_mask::all_false()";
                // Uniform bool: broadcast to all lanes then compare !=0 to produce proper n_mask
                if (_variables.TryGetValue(name, out var info) && info.Kind == VarKind.Uniform)
                    return $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({name} ? -1 : 0).v, n_set1_epi32(0)) }}";
                // Varying int/bool: compare register against zero
                if (_variables.TryGetValue(name, out var info2) && info2.Kind >= VarKind.Varying)
                    return $"simd_mask{{ n_cmp_ne_epi32(v_{name}.v, n_set1_epi32(0)) }}";
            }

            string result = TranslateExpression(expr);
            // If result is a scalar bool, wrap it as simd_mask via broadcast+compare
            if (!result.Contains("simd_mask") && !result.Contains("n_cmp_"))
                return $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({result} ? -1 : 0).v, n_set1_epi32(0)) }}";
            return result;
        }

        // ================================================================
        // Expression Sub-Translators
        // ================================================================

        private string TranslateLiteral(LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
                return "true";
            if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
                return "false";
            return literal.Token.Text;
        }

        private string TranslateIdentifier(IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.Text;

            // The Execute index parameter → SIMD index var
            if (name == _indexParamName)
                return _simdIndexVar;

            // For-loop induction variables → use simd_ prefix (no conflict risk)
            if (_forLoopVars.Contains(name))
                return $"simd_{name}";

            // ★ Bool field with known constant → return literal (MSVC DCE handles the rest)
            if (_boolFields.TryGetValue(name, out var bv))
                return bv;  // "true" or "false"

            // Deferred struct local (initialized from struct array element access)
            if (_structVaryingLocals.ContainsKey(name))
                return name;

            // Known variable (from SimdVariableAnalyzer)
            if (_variables.TryGetValue(name, out var info))
            {
                if (info.Kind == VarKind.Uniform)
                    return name; // scalar

                // Varying or Reduction
                if (IsFloat2Type(info.CppType))
                {
                    return $"v_{name}"; // float2 — use member access for components
                }
                return $"v_{name}";
            }

            // Job struct field (resolve via _jobStruct symbol, not semantic model)
            if (_jobStruct != null)
            {
                var members = _jobStruct.GetMembers(name);
                if (members.Length > 0 && members[0] is IFieldSymbol field && !field.IsStatic)
                {
                    if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                        return name;
                    return name;
                }
            }

            // Fallback: use name as-is
            return name;
        }

        private string TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            string memberName = memberAccess.Name.Identifier.Text;

                        // ★ Struct NativeArray field access: structArray[idx].fieldName
            //   Generate field-level gather with struct stride (ISPC-style AoS pattern).
            if (memberAccess.Expression is ElementAccessExpressionSyntax ea
                && ea.Expression is IdentifierNameSyntax arrId)
            {
                string arrName = arrId.Identifier.Text;
                if (_nativeArrayParams.TryGetValue(arrName, out var structElemType)
                    && structElemType != "float" && structElemType != "int"
                    && !structElemType.Contains("float2") && !structElemType.Contains("int2"))
                {
                    return TranslateStructArrayFieldAccess(arrName, structElemType, memberName,
                        ea.ArgumentList?.Arguments.Count > 0 ? ea.ArgumentList.Arguments[0].Expression : null);
                }
            }

            // ★ Deferred struct local field access: structLocal.fieldName
            //   Where structLocal was initialized from structArray[idx].
            //   Example: position.Value  (where position = positions[i])
            //   → field-level gather with struct stride
            if (memberAccess.Expression is IdentifierNameSyntax structLocalId
                && _structVaryingLocals.TryGetValue(structLocalId.Identifier.Text, out var structLocalInfo))
            {
                return TranslateStructFieldAccess(structLocalInfo.arrName, structLocalInfo.elemCppType,
                    memberName, structLocalInfo.indexExpr);
            }

            string objExpr = TranslateExpression(memberAccess.Expression);

            // Check if the object is a varying float2/int2
            string objName = memberAccess.Expression is IdentifierNameSyntax id ? id.Identifier.Text : null;
            bool isVaryingFloat2 = objName != null && _float2VaryingVars.Contains(objName);

            // .MaxValue / .MinValue on float → numeric_limits
            if (memberName == "MaxValue" || memberName == "MinValue")
            {
                string sign = memberName == "MaxValue" ? "max" : "lowest";
                return $"std::numeric_limits<float>::{sign}()";
            }

            // .zero on int2/float2 → constructor
            if (memberName == "zero" && (objExpr.Contains("int2") || objExpr.Contains("float2")))
            {
                string prefix = objExpr.Contains("int2") ? "EntJoy::Mathematics::int2" : "EntJoy::Mathematics::float2";
                return $"{prefix}(0, 0)";
            }

            // .Length on NativeArray → _length suffix
            if (memberName == "Length")
            {
                return $"{objExpr}_length";
            }

            // .x or .y on float2 — use .x/.y member on simd_value<float2>
            if ((memberName == "x" || memberName == "y") && isVaryingFloat2)
            {
                return $"{objExpr}.{memberName}";
            }

            // ★ Struct field gather result: n_gather_ps already returns the component value.
            //   For .x on a float2 field gather, just return the gather (it's already x).
            //   For .y, we need the gather at offset+1 (y is at +4 bytes).
            if ((memberName == "x" || memberName == "y") && objExpr.Contains("n_gather_ps<"))
            {
                                if (memberName == "y")
                {
                    // For .y: use same gather expression but at base+1 float offset.
                    // The n_gather_ps reads float at arr_ptr[v_i].field.
                    // For .y we need: arr_ptr[v_i].field_y which is at offset +4 bytes.
                    // Simple approach: append " + 1" before v_i.v to advance pointer by 1 float.
                    string modified = objExpr.Replace(", v_i.v)", " + 1, v_i.v)");
                    return modified;
                }// For .x: the n_gather_ps already reads at the field offset, returning x component
                return objExpr;
            }

            // ★ Check for hoisted uniform broadcast (pre-broadcast once, reuse in SIMD)
            if ((memberName == "x" || memberName == "y") && !isVaryingFloat2)
            {
                if (memberAccess.Expression is IdentifierNameSyntax hoistId)
                {
                    string key = $"{hoistId.Identifier.Text}.{memberName}";
                    if (_uniformHoistMap.TryGetValue(key, out var hoistVar))
                        return hoistVar;
                }
                // EntJoy Mathematics types use method syntax: .x() not .x
                // BUT: SIMD expressions (containing ::) use .x/.y as member access, not function call
                if (objExpr.Contains("::") || objExpr.StartsWith("simd_"))
                    return $"{objExpr}.{memberName}";
                return $"{objExpr}.{memberName}()";
            }
            return $"{objExpr}.{memberName}";
        }

        private string TranslateElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
                        // Resolve NativeArray type via _jobStruct symbol (avoid semantic model in source gen context)
            bool isNativeArray = false;
            string elemCppType = "float";
            if (_jobStruct != null && elementAccess.Expression is IdentifierNameSyntax id)
            {
                var members = _jobStruct.GetMembers(id.Identifier.Text);
                if (members.Length > 0 && members[0] is IFieldSymbol field && !field.IsStatic
                    && NativeTranspiler.IsEntJoyNativeContainerType(field.Type)
                    && field.Type.Name == "NativeArray")
                {
                    isNativeArray = true;
                    var typeArg = ((INamedTypeSymbol)field.Type).TypeArguments.FirstOrDefault();
                    if (typeArg != null)
                        elemCppType = NativeTranspiler.MapCSharpTypeToCpp(typeArg);
                }
            }
            // Also check _nativeArrayParams (for static methods without job struct)
            if (!isNativeArray && elementAccess.Expression is IdentifierNameSyntax naId
                && _nativeArrayParams.TryGetValue(naId.Identifier.Text, out var paramElemType))
            {
                isNativeArray = true;
                elemCppType = paramElemType;
            }
            string baseExpr = TranslateExpression(elementAccess.Expression);

            string indexExpr = "0";
            if (elementAccess.ArgumentList?.Arguments.Count > 0)
                indexExpr = TranslateExpression(elementAccess.ArgumentList.Arguments[0].Expression);

            // Detect if index is varying (SIMD gather needed)
            VarKind indexKind = VarKind.Uniform;
            if (elementAccess.ArgumentList?.Arguments.Count > 0)
            {
                var argExpr = elementAccess.ArgumentList.Arguments[0].Expression;
                indexKind = _varAnalyzer.ClassifyExpression(argExpr);
            }

            // Handle NativeList: use Ptr->data access
            if (!isNativeArray && _jobStruct != null && elementAccess.Expression is IdentifierNameSyntax id2)
            {
                var members2 = _jobStruct.GetMembers(id2.Identifier.Text);
                if (members2.Length > 0 && members2[0] is IFieldSymbol f2
                    && NativeTranspiler.IsEntJoyNativeContainerType(f2.Type)
                    && f2.Type.Name == "NativeList")
                {
                    isNativeArray = true;
                    var typeArg = ((INamedTypeSymbol)f2.Type).TypeArguments.FirstOrDefault();
                    if (typeArg != null)
                        elemCppType = NativeTranspiler.MapCSharpTypeToCpp(typeArg);
                    if (indexKind >= VarKind.Varying)
                    {
                        // ★ Safety clamp: mask ctx → clamp to [0, Length-1] for unmasked gather
                        string safeIdx = _currentMask != "simd_mask::all_true()"
                            ? $"simd_min(simd_max({indexExpr}, simd_value<int>(0)), simd_value<int>::broadcast({baseExpr}.Length - 1))"
                            : indexExpr;
                        if (elemCppType.Contains("float2"))
                            return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>::gathf(({elemCppType}*){baseExpr}.Ptr, {safeIdx}.v), simd_value<float>::gathfy(({elemCppType}*){baseExpr}.Ptr, {safeIdx}.v) }}";
                        if (elemCppType.Contains("int2"))
                            return $"simd_value<EntJoy::Mathematics::int2>::gather(({elemCppType}*){baseExpr}.Ptr, {safeIdx})";
                        return $"simd_value<float>::gathf(({elemCppType}*){baseExpr}.Ptr, {safeIdx}.v)";
                    }
                    return $"(({elemCppType}*){baseExpr}.Ptr)[{indexExpr}]";
                }
            }

            if (isNativeArray && indexKind >= VarKind.Varying)
            {
                // ★ Check if index is from a uniform-bound reduction loop induction variable
                //   → emit broadcast of scalar load instead of gather.
                //   This is the key optimization for fallback loops like for(i=0; i<N; i++):
                //   one scalar load + broadcast to all 8 lanes.
                if (elementAccess.ArgumentList?.Arguments.Count > 0)
                {
                    var rawArg = elementAccess.ArgumentList.Arguments[0].Expression;
                    if (rawArg is IdentifierNameSyntax rawId && _uniformLoopVars.Contains(rawId.Identifier.Text))
                    {
                        string scalarIdx = rawId.Identifier.Text;
                        if (elemCppType.Contains("float2"))
                            return $"simd_value<{elemCppType}>::broadcast({baseExpr}_ptr[{scalarIdx}])";
                        if (elemCppType.Contains("int2"))
                            return $"simd_value<EntJoy::Mathematics::int2>::broadcast({baseExpr}_ptr[{scalarIdx}])";
                        if (elemCppType == "float")
                            return $"simd_value<float>::broadcast({baseExpr}_ptr[{scalarIdx}])";
                        if (elemCppType == "int")
                            return $"simd_value<int>::broadcast({baseExpr}_ptr[{scalarIdx}])";
                        return $"simd_value<float>::broadcast({baseExpr}_ptr[{scalarIdx}])";
                    }
                }

                // ★ Safety clamp for gather: when in mask context, clamp indices to
                //   [0, arr_length-1] to prevent AVX2 unmasked gather OOB.
                //   Skip if index variable was already clamped by a prior gather.
                string safeIdx;
                if (elementAccess.ArgumentList?.Arguments.Count > 0
                    && elementAccess.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax idxId
                    && (_clampedVars.Contains(idxId.Identifier.Text) || _clampedVars.Contains("v_" + idxId.Identifier.Text)))
                {
                    safeIdx = indexExpr; // already clamped by prior gather
                }
                else if (_inVaryingReductionLoop && _hoistedSafeMaxVar != null && baseExpr == _hoistedSafeMaxExpr)
                {
                    // Varying reduction loop: index pre-clamped to >=0, use hoisted broadcast
                    safeIdx = $"simd_min({indexExpr}, {_hoistedSafeMaxVar})";
                }
                else if (_currentMask != "simd_mask::all_true()")
                {
                    safeIdx = $"simd_min(simd_max({indexExpr}, simd_value<int>(0)), simd_value<int>::broadcast({baseExpr}_length - 1))";
                }
                else
                {
                    safeIdx = indexExpr;
                }

                // Contiguous index optimization: when index is _simdIndexVar (v_i/v_j) in a batch loop,
                // or uniform_part + _simdIndexVar (like i*100 + v_j), use contiguous load instead of gather.
                if (!string.IsNullOrEmpty(_batchLoopVar))
                {
                    string contBase = null;
                    if (indexExpr == _simdIndexVar)
                        contBase = _batchLoopVar;  // simple: ptr + si
                    else
                    {
                        // Detect: uniform_expr + _simdIndexVar (like "i*100 + v_j")
                        string suffix = $"+ {_simdIndexVar}";
                        if (indexExpr.EndsWith(suffix))
                            contBase = indexExpr.Substring(0, indexExpr.Length - suffix.Length).Trim();
                        else if (indexExpr.EndsWith($"+ {_simdIndexVar})"))
                            contBase = indexExpr.Substring(0, indexExpr.Length - ($"+ {_simdIndexVar})").Length).Trim().TrimStart('(');
                    }
                    if (contBase != null)
                    {
                        string baseOff = contBase == _batchLoopVar ? contBase : $"({contBase}) + {_batchLoopVar}";
                        if (elemCppType == "float")
                            return $"simd_value<float>{{ n_load_ps({baseExpr}_ptr + {baseOff}) }}";
                        if (elemCppType == "int")
                            return $"simd_value<int>{{ n_load_epi32({baseExpr}_ptr + {baseOff}) }}";
                    }
                }

                // SIMD gather
                if (elemCppType.Contains("float2"))
                {
                    if (safeIdx != indexExpr)
                    {
                        string tidx = $"__ci_{_labelCounter++}";
                        AppendLine($"simd_value<int> {tidx} = {safeIdx};");
                        return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>::gathf({baseExpr}_ptr, {tidx}.v), simd_value<float>::gathfy({baseExpr}_ptr, {tidx}.v) }}";
                    }
                    return $"simd_value<EntJoy::Mathematics::float2>{{ simd_value<float>::gathf({baseExpr}_ptr, {safeIdx}.v), simd_value<float>::gathfy({baseExpr}_ptr, {safeIdx}.v) }}";
                }
                if (elemCppType.Contains("int2"))
                    return $"simd_value<EntJoy::Mathematics::int2>::gather({baseExpr}_ptr, {safeIdx})";
                if (elemCppType == "float")
                    return $"simd_value<float>::gathf({baseExpr}_ptr, {safeIdx}.v)";
                if (elemCppType == "int")
                    return $"simd_value<int>::gather({baseExpr}_ptr, {safeIdx})";
                return $"simd_value<float>::gathf({baseExpr}_ptr, {safeIdx}.v)";
            }

            // Scalar access
            return $"{baseExpr}_ptr[{indexExpr}]";
        }

        private string TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            IMethodSymbol? symbol = null;
            try { symbol = _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol; } catch { }
            if (symbol == null)
            {
                // Fallback: try name-based matching for common math functions
                // (GetSymbolInfo can fail on SyntaxFactory-created AST nodes)
                var ident = invocation.Expression as MemberAccessExpressionSyntax;
                string? fnName = ident?.Name.Identifier.Text;
                if (fnName != null)
                {
                    // Try TranslateMathFFunction first (has Sin/Cos/Sqrt/SLEEF cases for PascalCase names)
                    string fc1 = TranslateMathFFunction(fnName, invocation);
                    if (!fc1.Contains("/* unknown") && !fc1.Contains("EntJoy::Mathematics"))
                        return fc1;
                    // Fallback: TranslateMathFunction for EntJoy mathematics functions
                    string fc2 = TranslateMathFunction(fnName, invocation);
                    if (!fc2.Contains("/* unknown */"))
                        return fc2;
                }
                return "/* unknown function */ 0";
            }

            string containingType = symbol.ContainingType?.ToDisplayString() ?? "";
            string methodName = symbol.Name;

            // EntJoy.Mathematics.math functions
            if (containingType == "EntJoy.Mathematics.math")
            {
                return TranslateMathFunction(methodName, invocation);
            }

            // System.MathF / System.Math
            if (containingType == "System.MathF" || containingType == "System.Math")
            {
                return TranslateMathFFunction(methodName, invocation);
            }

            // NativeArray.GetUnsafePtr
            if (symbol.ContainingType?.Name == "NativeArray" && methodName == "GetUnsafePtr")
            {
                if (invocation.Expression is MemberAccessExpressionSyntax ma
                    && ma.Expression is IdentifierNameSyntax id)
                {
                    return $"{id.Identifier.Text}_ptr";
                }
            }

            // Fallback: emit as regular function call
            string funcCall = "";
            if (invocation.Expression is MemberAccessExpressionSyntax member)
            {
                funcCall = $"{TranslateExpression(member.Expression)}.{methodName}(";
            }
            else
            {
                funcCall = $"{methodName}(";
            }

            for (int i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
            {
                if (i > 0) funcCall += ", ";
                funcCall += TranslateExpression(invocation.ArgumentList.Arguments[i].Expression);
            }
            funcCall += ")";
            return funcCall;
        }

        private string TranslateMathFunction(string methodName, InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;

            switch (methodName)
            {
                case "min":
                case "max":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        VarKind k1 = _varAnalyzer.ClassifyExpression(args[1].Expression);
                        if (k0 >= VarKind.Varying || k1 >= VarKind.Varying)
                        {
                            string func = methodName == "min" ? "min" : "max";
                            // Ensure both are SIMD by broadcasting uniform ones
                            if (k0 < VarKind.Varying && k1 >= VarKind.Varying)
                                a = $"{b}.broadcast({a})";
                            else if (k0 >= VarKind.Varying && k1 < VarKind.Varying)
                                b = $"{a}.broadcast({b})";
                            return $"{func}({a}, {b})";
                        }
                        return $"{methodName}({a}, {b})";
                    }
                    break;
                }

                case "clamp":
                {
                    if (args.Count >= 3)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        string lo = TranslateExpression(args[1].Expression);
                        string hi = TranslateExpression(args[2].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                        {
                            // For float2/int2: decompose to component-wise clamp
                            if (args[0].Expression is IdentifierNameSyntax clampId
                                && _float2VaryingVars.Contains(clampId.Identifier.Text)
                                && _variables.TryGetValue(clampId.Identifier.Text, out var clampInfo))
                            {
                                string simdType = GetSIMDTypeString(clampInfo.CppType);
                                // simdType is "simd_value<EntJoy::Mathematics::int2>" — use directly, NOT wrapping in simd_value<>
                                return $"{simdType}(max(min({v}.x, {hi}.x()), {lo}.x()), max(min({v}.y, {hi}.y()), {lo}.y()))";
                            }
                            // Default: use friend functions max/min (works for all SIMD types)
                            return $"max(min({v}, {hi}), {lo})";
                        }
                        return $"EntJoy::Mathematics::clamp({v}, {lo}, {hi})";
                    }
                    break;
                }

                case "abs":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_max({v}, -{v})"; // simple SIMD abs
                        return $"EntJoy::Mathematics::abs({v})";
                    }
                    break;
                }

                case "floor":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"{v}.floor()";
                        return $"EntJoy::Mathematics::floor({v})";
                    }
                    break;
                }

                case "MaxValue":
                    return "std::numeric_limits<float>::max()";
                case "MinValue":
                    return "std::numeric_limits<float>::lowest()";

                case "distancesq":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        VarKind k1 = _varAnalyzer.ClassifyExpression(args[1].Expression);
                        if (k0 >= VarKind.Varying || k1 >= VarKind.Varying)
                        {
                            // Expand as SIMD using .x/.y member access on whole-type simd_value<float2>
                            // Keep inline: explicit temps increase register pressure, MSVC CSE is sufficient
                            string ax = $"{a}.x", ay = $"{a}.y";
                            string bx = $"{b}.x", by = $"{b}.y";

                            return $"({ax} - {bx}) * ({ax} - {bx}) + ({ay} - {by}) * ({ay} - {by})";
                        }
                        return $"EntJoy::Mathematics::distancesq({a}, {b})";
                    }
                    break;
                }

                case "dot":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (k0 >= VarKind.Varying)
                        {
                            // Use member access on whole-type: v_a.x * v_b.x + v_a.y * v_b.y
                            string ax = $"{a}.x", ay = $"{a}.y";
                            string bx = $"{b}.x", by = $"{b}.y";
                            return $"{ax} * {bx} + {ay} * {by}";
                        }
                        return $"EntJoy::Mathematics::dot({a}, {b})";
                    }
                    break;
                }
            }

            // Default: emit as regular function call
            string call = $"EntJoy::Mathematics::{methodName}(";
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) call += ", ";
                call += TranslateExpression(args[i].Expression);
            }
            call += ")";
            return call;
        }

        private string TranslateMathFFunction(string methodName, InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;

            switch (methodName)
            {
                case "Min":
                case "Max":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (k0 >= VarKind.Varying)
                        {
                            string func = methodName == "Min" ? "min" : "max";
                            return $"{func}({a}, {b})";
                        }
                        return $"std::{methodName.ToLower()}({a}, {b})";
                    }
                    break;
                }

                case "Sqrt":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                        {
                            // SIMD sqrt via native instruction
                            return $"simd_value<float>{{ n_sqrt_ps({v}.v) }}";
                        }
                        return $"std::sqrt({v})";
                    }
                    break;
                }

                case "Abs":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_max({v}, -{v})";
                        return $"std::abs({v})";
                    }
                    break;
                }

                // Lightweight native SIMD (no SLEEF needed)
                case "Ceiling":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_ceil_ps({v}.v) }}";
                        return $"std::ceil({v})";
                    }
                    break;
                }
                case "Round":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_round_ps({v}.v) }}";
                        return $"std::round({v})";
                    }
                    break;
                }
                case "Truncate":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_trunc_ps({v}.v) }}";
                        return $"std::trunc({v})";
                    }
                    break;
                }

                // SLEEF transcendental functions (single-argument)
                case "Sin":  case "Cos":  case "Tan":
                case "Asin": case "Acos": case "Atan":
                case "Sinh": case "Cosh": case "Tanh":
                case "Exp":  case "Log":  case "Log10":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                        {
                            string sleefFn = $"n_{methodName.ToLowerInvariant()}_ps";
                            return $"simd_value<float>{{ {sleefFn}({v}.v) }}";
                        }
                        return $"std::{methodName.ToLowerInvariant()}({v})";
                    }
                    break;
                }

                // SLEEF two-argument functions
                case "Atan2":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_atan2_ps({a}.v, {b}.v) }}";
                        return $"std::atan2({a}, {b})";
                    }
                    break;
                }
                case "Pow":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_pow_ps({a}.v, {b}.v) }}";
                        return $"std::pow({a}, {b})";
                    }
                    break;
                }
            }

            // Fallback: lowercase mapping (MathF.Sin → sin for SIMD ADL, std::sin for scalar)
            bool anyVarying = false;
            for (int i = 0; i < args.Count; i++)
                if (_varAnalyzer.ClassifyExpression(args[i].Expression) >= VarKind.Varying)
                    { anyVarying = true; break; }
            string funcPrefix = anyVarying ? "" : "std::";
            string call = $"{funcPrefix}{methodName.ToLowerInvariant()}(";
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) call += ", ";
                call += TranslateExpression(args[i].Expression);
            }
            call += ")";
            return call;
        }

        private string TranslateBinary(BinaryExpressionSyntax binary)
        {
            string left = TranslateExpression(binary.Left);
            string right = TranslateExpression(binary.Right);
            VarKind leftKind = _varAnalyzer.ClassifyExpression(binary.Left);
            VarKind rightKind = _varAnalyzer.ClassifyExpression(binary.Right);
            bool anyVarying = leftKind >= VarKind.Varying || rightKind >= VarKind.Varying;

            string op = binary.OperatorToken.Text;

            // Comparison operators → simd_mask
            if (binary.IsKind(SyntaxKind.LessThanExpression)
                || binary.IsKind(SyntaxKind.GreaterThanExpression)
                || binary.IsKind(SyntaxKind.LessThanOrEqualExpression)
                || binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression)
                || binary.IsKind(SyntaxKind.EqualsExpression)
                || binary.IsKind(SyntaxKind.NotEqualsExpression))
            {
                if (!anyVarying)
                {
                    // Uniform comparison
                    string cppOp = op switch
                    {
                        "<" => "<", ">" => ">", "<=" => "<=",
                        ">=" => ">=", "==" => "==", "!=" => "!=",
                        _ => op
                    };
                    return $"({left} {cppOp} {right})";
                }

                // Varying comparison → SIMD compare (detect int vs float)
                // Wrap complex expressions in parens so .v binds to the whole expression, not just the last term
                string leftV = leftKind >= VarKind.Varying ? $"({left}).v" : $"{left}";
                string rightV = rightKind >= VarKind.Varying ? $"({right}).v" : $"{right}";

                bool cmpIsInt = false;
                foreach (var side in new ExpressionSyntax[] { binary.Left, binary.Right }) {
                    if (side is IdentifierNameSyntax cid && _variables.TryGetValue(cid.Identifier.Text, out var civ) && civ.CppType == "int") cmpIsInt = true;
                    if (side is CastExpressionSyntax cce && cce.Expression is IdentifierNameSyntax ccid && _variables.TryGetValue(ccid.Identifier.Text, out var cciv) && cciv.CppType == "int") cmpIsInt = true;
                }
                string bc = cmpIsInt ? "n_set1_epi32" : "n_set1_ps";
                // ★ Hoisted broadcasts (__uni_ prefixed) are already SIMD — use .v, don't re-broadcast
                bool rightIsHoisted = right.StartsWith("__uni_");
                bool leftIsHoisted = left.StartsWith("__uni_");
                if (leftKind < VarKind.Varying && rightKind >= VarKind.Varying)
                {
                    if (leftIsHoisted)
                        rightV = $"({right}).v";
                    else
                        leftV = $"{bc}({left})";
                }
                else if (leftKind >= VarKind.Varying && rightKind < VarKind.Varying)
                {
                    if (rightIsHoisted)
                        rightV = $"({right}).v";
                    else
                        rightV = $"{bc}({right})";
                }
                // If hoisted broadcast ended up on wrong side, correct
                if (rightIsHoisted && !rightV.Contains(".v")) rightV = $"({right}).v";
                if (leftIsHoisted && !leftV.Contains(".v")) leftV = $"({left}).v";

                if (cmpIsInt) {
                    string ic = op switch {
                        "<" => "n_cmp_lt_epi32", ">" => "n_cmp_gt_epi32", "<=" => "n_cmp_le_epi32",
                        ">=" => "n_cmp_ge_epi32", "==" => "n_cmp_eq_epi32", "!=" => "n_cmp_ne_epi32",
                        _ => "n_cmp_eq_epi32"
                    };
                    return $"simd_mask{{ {ic}({leftV}, {rightV}) }}";
                }
                string fc = op switch {
                    "<" => "n_cmp_lt_ps", ">" => "n_cmp_gt_ps", "<=" => "n_cmp_le_ps",
                    ">=" => "n_cmp_ge_ps", "==" => "n_cmp_eq_ps", "!=" => "n_cmp_ne_ps",
                    _ => "n_cmp_eq_ps"
                };
                return $"simd_mask{{ {fc}({leftV}, {rightV}) }}";
            }

            // Logical operators
            if (binary.IsKind(SyntaxKind.LogicalAndExpression))
            {
                if (anyVarying)
                {
                    // ★ Compile-time constant folding: false && expr → false, true && expr → expr
                    if (left == "false" || left == "0") return "simd_mask{ n_cmp_ne_epi32(n_set1_epi32(0), n_set1_epi32(0)) }";
                    if (left == "true") return right;
                    if (right == "false" || right == "0") return "simd_mask{ n_cmp_ne_epi32(n_set1_epi32(0), n_set1_epi32(0)) }";
                    if (right == "true") return left;
                    // Wrap scalar bools in simd_mask before accessing .m
                    if (!left.Contains("simd_mask") && !left.Contains("n_cmp_"))
                        left = $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({left} ? -1 : 0).v, n_set1_epi32(0)) }}";
                    if (!right.Contains("simd_mask") && !right.Contains("n_cmp_"))
                        right = $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({right} ? -1 : 0).v, n_set1_epi32(0)) }}";
                    return $"simd_mask{{ n_and_mask({left}.m, {right}.m) }}";
                }
                return $"({left} && {right})";
            }
            if (binary.IsKind(SyntaxKind.LogicalOrExpression))
            {
                if (anyVarying)
                {
                    if (left == "true") return "simd_mask::all_true()";
                    if (left == "false") return right;
                    if (right == "true") return "simd_mask::all_true()";
                    if (right == "false") return left;
                    if (!left.Contains("simd_mask") && !left.Contains("n_cmp_"))
                        left = $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({left} ? -1 : 0).v, n_set1_epi32(0)) }}";
                    if (!right.Contains("simd_mask") && !right.Contains("n_cmp_"))
                        right = $"simd_mask{{ n_cmp_ne_epi32(simd_value<int>::broadcast({right} ? -1 : 0).v, n_set1_epi32(0)) }}";
                    return $"simd_mask{{ n_or_mask({left}.m, {right}.m) }}";
                }
                return $"({left} || {right})";
            }

            // Arithmetic operators
            if (!anyVarying)
            {
                return $"({left} {op} {right})";
            }

            // ★ FMA detection: a*a + b*b → n_fmadd_ps(a, a, n_mul_ps(b, b))
            if (anyVarying && op == "+" && binary.Left is BinaryExpressionSyntax lmul
                && lmul.OperatorToken.Text == "*"
                && binary.Right is BinaryExpressionSyntax rmul
                && rmul.OperatorToken.Text == "*")
            {
                string la = TranslateExpression(lmul.Left);
                string lb = TranslateExpression(lmul.Right);
                string ra = TranslateExpression(rmul.Left);
                string rb = TranslateExpression(rmul.Right);
                if (la == lb && ra == rb)
                    return $"simd_value<float>{{ n_fmadd_ps({la}.v, {la}.v, n_mul_ps({ra}.v, {ra}.v)) }}";
            }

            // At least one varying — SIMD arithmetic
            string simdOp = op switch
            {
                "+" => "+",
                "-" => "-",
                "*" => "*",
                "/" => "/",
                _ => "+"
            };

            return $"({left} {simdOp} {right})";
        }

        private string TranslateCast(CastExpressionSyntax cast)
        {
            string inner = TranslateExpression(cast.Expression);
            VarKind innerKind = _varAnalyzer.ClassifyExpression(cast.Expression);
            string targetTypeStr = cast.Type.ToString();

            // ★ Hoisted broadcasts are already the correct SIMD type — skip cast entirely
            if (inner.StartsWith("__uni_"))
                return inner;

            // For varying int -> unsigned int: keep {inner} as-is (n_cmp_*_epi32 works on raw n_int)
            if (innerKind >= VarKind.Varying && (targetTypeStr == "uint" || targetTypeStr == "unsigned int"))
                return $"{inner}";

            // SIMD type conversions: (int2)simd_value<float2> → simd_value<int2>::convert(...)
            if (innerKind >= VarKind.Varying)
            {
                if (targetTypeStr.Contains("int2"))
                    return $"simd_value<EntJoy::Mathematics::int2>::convert({inner})";
                if (targetTypeStr == "int" || targetTypeStr == "System.Int32")
                    return $"simd_value<int>::convert({inner})";
                // (float2)simd_value<float2> → just the inner value (identity conversion)
                if (targetTypeStr.Contains("float2"))
                    return inner;
            }

            // (int)floatExpr → scalar convert (uniform path)
            try
            {
                var targetType = _semanticModel.GetTypeInfo(cast.Type).Type;
                if (targetType != null)
                    return $"({NativeTranspiler.MapCSharpTypeToCpp(targetType)}){inner}";
            }
            catch { }

            return $"({targetTypeStr.Replace(".", "::")}){inner}";
        }

        private string TranslateAssignment(AssignmentExpressionSyntax assign)
        {
            // NativeArray writes: detect element-access LHS
            string baseName = null;
            string elemType = "float";
            ElementAccessExpressionSyntax elemAccess = null;
            if (assign.Left is ElementAccessExpressionSyntax ea
                && (elemAccess = ea).Expression is IdentifierNameSyntax id)
            {
                // 1. Check _nativeArrayParams first (for static methods without job struct)
                if (_nativeArrayParams.TryGetValue(id.Identifier.Text, out var paramElemType))
                {
                    baseName = id.Identifier.Text;
                    elemType = paramElemType;
                }
                // 2. Check job struct fields (for IJob/IJobFor paths)
                else if (_jobStruct != null)
                {
                    var members = _jobStruct.GetMembers(id.Identifier.Text);
                    if (members.Length > 0 && members[0] is IFieldSymbol f
                        && NativeTranspiler.IsEntJoyNativeContainerType(f.Type)
                        && f.Type.Name == "NativeArray")
                    {
                        baseName = id.Identifier.Text;
                        elemType = NativeTranspiler.MapCSharpTypeToCpp(((INamedTypeSymbol)f.Type).TypeArguments[0]);
                    }
                }
            }

            if (baseName != null)
            {
                string idxExpr = TranslateExpression(
                    elemAccess.ArgumentList?.Arguments[0].Expression ?? assign.Left);
                string rhsExpr = TranslateExpression(assign.Right);
                VarKind idxKind = _varAnalyzer.ClassifyExpression(
                    elemAccess.ArgumentList?.Arguments[0].Expression ?? assign.Left);
                VarKind rhsKind = _varAnalyzer.ClassifyExpression(assign.Right);
                string extractFn = elemType == "float" ? "n_extract_lane_f32" : "n_extract_lane_epi32";
                string storeFnScalar = elemType == "float" ? "n_store_ps" : "n_store_epi32";
                string setFnScalar = elemType == "float" ? "n_set1_ps" : "n_set1_epi32";

                if (idxKind >= VarKind.Varying && rhsKind < VarKind.Varying)
                    {
                        return $"{storeFnScalar}(&{baseName}_ptr[{_batchOffsetVar}], {setFnScalar}({rhsExpr}))";
                    }

                    if (idxKind >= VarKind.Varying)
                    {
                        // Contiguous index optimization: when idx == simdIndexVar or
                        // uniform_part + simdIndexVar, use contiguous store instead of per-lane scatter.
                        if (!string.IsNullOrEmpty(_batchLoopVar))
                        {
                            string contBase = null;
                            if (idxExpr == _simdIndexVar)
                                contBase = _batchLoopVar;
                            else
                            {
                                string suffix = $"+ {_simdIndexVar}";
                                if (idxExpr.EndsWith(suffix))
                                    contBase = idxExpr.Substring(0, idxExpr.Length - suffix.Length).Trim();
                                else if (idxExpr.EndsWith($"+ {_simdIndexVar})"))
                                    contBase = idxExpr.Substring(0, idxExpr.Length - ($"+ {_simdIndexVar})").Length).Trim().TrimStart('(');
                            }
                            if (contBase != null)
                            {
                                string storeFn = elemType == "float" ? "n_store_ps" : "n_store_epi32";
                                string off = contBase == _batchLoopVar ? contBase : $"({contBase}) + {_batchLoopVar}";
                                return $"{storeFn}({baseName}_ptr + {off}, {rhsExpr}.v)";
                            }
                        }
                        // ★ Mask-guarded per-lane scatter:
                        //   When _currentMask is narrowed (if/else context), the per-lane extract
                        //   loop must NOT write to lanes excluded by the mask. Otherwise lanes
                        //   where the condition was false get garbage Results.
                        //   From ISPC LLVM IR (closestpoint_ispc.ll line 130-135):
                        //   "notequal_bestIdx_load_ = icmp ne <8 x i32> %bestIdx.1, splat (-1)"
                        //   → per-lane guard on the write.
                        if (_currentMask != "simd_mask::all_true()")
                        {
                            return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<NSIMD_WIDTH;__l++){{if(__sg&(1<<__l)){{{baseName}_ptr[n_extract_lane_epi32({idxExpr}.v,__l)]={extractFn}({rhsExpr}.v,__l);}}}}}}";
                        }
                        return $"{{for(int __l=0;__l<NSIMD_WIDTH;__l++){{{baseName}_ptr[n_extract_lane_epi32({idxExpr}.v,__l)]={extractFn}({rhsExpr}.v,__l);}}}}";
                    }

                    // uniform idx + varying rhs -> extract lane 0
                    if (rhsKind >= VarKind.Varying)
                    {
                        return $"{baseName}_ptr[{idxExpr}] = {extractFn}({rhsExpr}.v, 0)";
                    }

                    return $"{baseName}_ptr[{idxExpr}] = {rhsExpr}";
                }

            // ★ Struct NativeArray field assignment: structArray[idx].field = rhs
            //   Handle positions[i].Value = expr; pattern with per-lane field scatter.
            if (assign.Left is MemberAccessExpressionSyntax ma
                && ma.Expression is ElementAccessExpressionSyntax ea2
                && ea2.Expression is IdentifierNameSyntax id2)
            {
                string arrName2 = id2.Identifier.Text;
                if (_nativeArrayParams.TryGetValue(arrName2, out var saElemType)
                    && saElemType != "float" && saElemType != "int"
                    && !saElemType.Contains("float2") && !saElemType.Contains("int2"))
                {
                    string fieldName2 = ma.Name.Identifier.Text;
                    string idxExpr2 = ea2.ArgumentList?.Arguments.Count > 0 ? TranslateExpression(ea2.ArgumentList?.Arguments[0]?.Expression) : "0";
                    string rhsExpr2 = TranslateExpression(assign.Right);
                    VarKind idxKind2 = VarKind.Uniform;
                    if (ea2.ArgumentList?.Arguments.Count > 0)
                        idxKind2 = _varAnalyzer.ClassifyExpression(ea2.ArgumentList?.Arguments[0]?.Expression);
                    VarKind rhsKind2 = _varAnalyzer.ClassifyExpression(assign.Right);

                    if (idxKind2 >= VarKind.Varying)
                    {
                        // Per-lane scatter for struct field write (SIMD context)
                        if (_currentMask != "simd_mask::all_true()")
                        {
                            return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<NSIMD_WIDTH;__l++){{if(__sg&(1<<__l)){{{id2.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr2}.v,__l)].{fieldName2}=n_extract_lane_f32({rhsExpr2}.v,__l);}}}}}}";
                        }
                        return $"{{for(int __l=0;__l<NSIMD_WIDTH;__l++){{{id2.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr2}.v,__l)].{fieldName2}=n_extract_lane_f32({rhsExpr2}.v,__l);}}}}";
                    }
                    // Uniform index: scalar field assignment
                    return $"{id2.Identifier.Text}_ptr[{idxExpr2}].{fieldName2} = {rhsExpr2}";
                }
            }

            // ★ Deferred struct local field assignment: structLocal.field = rhs
            //   Where structLocal = structArray[idx]; decompose into per-lane field scatter
            //   Example: position.Value = expr  →  positions_ptr[v_i].Value = expr (per-lane scatter)
            if (assign.Left is MemberAccessExpressionSyntax ma3
                && ma3.Expression is IdentifierNameSyntax structLocalId2
                && _structVaryingLocals.TryGetValue(structLocalId2.Identifier.Text, out var structLocalAssignInfo))
            {
                string fieldName3 = ma3.Name.Identifier.Text;
                string arrName3 = structLocalAssignInfo.arrName;
                string idxExpr3 = structLocalAssignInfo.indexExpr;
                string rhsExpr3 = TranslateExpression(assign.Right);
                string op3 = assign.OperatorToken.Text;

                // SIMD context: per-lane field scatter
                // For varying index, generate per-lane scatter to arr_ptr[v_i_lane].field
                if (idxExpr3.Contains("v_") || idxExpr3 == "v_i" || _currentMask != "simd_mask::all_true()")
                {
                    string extractFn3 = "n_extract_lane_f32";
                    string combineExpr = op3 == "=" ? rhsExpr3 : $"{idxExpr3} {op3.Replace("=", "")} {rhsExpr3}";

                    if (_currentMask != "simd_mask::all_true()")
                    {
                        return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<NSIMD_WIDTH;__l++){{if(__sg&(1<<__l)){{{arrName3}_ptr[n_extract_lane_epi32({idxExpr3}.v,__l)].{fieldName3}={extractFn3}({combineExpr}.v,__l);}}}}}}";
                    }
                    return $"{{for(int __l=0;__l<NSIMD_WIDTH;__l++){{{arrName3}_ptr[n_extract_lane_epi32({idxExpr3}.v,__l)].{fieldName3}={extractFn3}({combineExpr}.v,__l);}}}}";
                }
                // Uniform index: scalar field access
                return $"{arrName3}_ptr[{idxExpr3}].{fieldName3} {op3} {rhsExpr3}";
            }

            // ★ Struct field sub-field assignment: array[idx].field1.field2 = rhs
            //   Handle positions[i].Value.x = expr; pattern per-lane field scatter.
            if (assign.Left is MemberAccessExpressionSyntax ma5
                && ma5.Expression is MemberAccessExpressionSyntax ma6
                && ma6.Expression is ElementAccessExpressionSyntax ea5
                && ea5.Expression is IdentifierNameSyntax id5)
            {
                string arrName5 = id5.Identifier.Text;
                if (_nativeArrayParams.TryGetValue(arrName5, out var saElemType5)
                    && saElemType5 != "float" && saElemType5 != "int"
                    && !saElemType5.Contains("float2") && !saElemType5.Contains("int2"))
                {
                    string fieldPath = ma6.Name.Identifier.Text + "." + ma5.Name.Identifier.Text + "()";
                    string idxExpr5 = ea5.ArgumentList?.Arguments.Count > 0 ? TranslateExpression(ea5.ArgumentList?.Arguments[0]?.Expression) : "0";
                    string rhsExpr5 = TranslateExpression(assign.Right);
                    VarKind idxKind5 = VarKind.Uniform;
                    if (ea5.ArgumentList?.Arguments.Count > 0)
                        idxKind5 = _varAnalyzer.ClassifyExpression(ea5.ArgumentList?.Arguments[0]?.Expression);

                    if (idxKind5 >= VarKind.Varying)
                    {
                        // Per-lane scatter for struct sub-field write
                        if (_currentMask != "simd_mask::all_true()")
                            return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<NSIMD_WIDTH;__l++){{if(__sg&(1<<__l)){{{id5.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr5}.v,__l)].{fieldPath}=n_extract_lane_f32({rhsExpr5}.v,__l);}}}}}}";
                        return $"{{for(int __l=0;__l<NSIMD_WIDTH;__l++){{{id5.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr5}.v,__l)].{fieldPath}=n_extract_lane_f32({rhsExpr5}.v,__l);}}}}";
                    }
                    return $"{id5.Identifier.Text}_ptr[{idxExpr5}].{fieldPath} = {rhsExpr5}";
                }
            }

            // ★ Struct field sub-field assignment (two-level member access on struct NativeArray):
            //   Positions[i].Value.x = expr; → per-lane field scatter with .x() method syntax
            if (assign.Left is MemberAccessExpressionSyntax _ma5
                && _ma5.Expression is MemberAccessExpressionSyntax _ma6
                && _ma6.Expression is ElementAccessExpressionSyntax _ea5
                && _ea5.Expression is IdentifierNameSyntax _id5)
            {
                string arrName5 = _id5.Identifier.Text;
                if (_nativeArrayParams.TryGetValue(arrName5, out var saElemType5)
                    && saElemType5 != "float" && saElemType5 != "int"
                    && !saElemType5.Contains("float2") && !saElemType5.Contains("int2"))
                {
                    string fieldPath = _ma6.Name.Identifier.Text + "." + _ma5.Name.Identifier.Text + "()";
                    string idxExpr5 = _ea5.ArgumentList?.Arguments.Count > 0 ? TranslateExpression(_ea5.ArgumentList?.Arguments[0]?.Expression) : "0";
                    string rhsExpr5 = TranslateExpression(assign.Right);
                    VarKind idxKind5 = VarKind.Uniform;
                    if (_ea5.ArgumentList?.Arguments.Count > 0)
                        try { idxKind5 = _varAnalyzer.ClassifyExpression(_ea5.ArgumentList?.Arguments[0]?.Expression); } catch { idxKind5 = VarKind.Varying; }

                    if (idxKind5 >= VarKind.Varying)
                    {
                        if (_currentMask != "simd_mask::all_true()")
                            return $"{{int __sg=n_mask_to_bitmask(({_currentMask}).m);for(int __l=0;__l<NSIMD_WIDTH;__l++){{if(__sg&(1<<__l)){{{_id5.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr5}.v,__l)].{fieldPath}=n_extract_lane_f32({rhsExpr5}.v,__l);}}}}}}";
                        return $"{{for(int __l=0;__l<NSIMD_WIDTH;__l++){{{_id5.Identifier.Text}_ptr[n_extract_lane_epi32({idxExpr5}.v,__l)].{fieldPath}=n_extract_lane_f32({rhsExpr5}.v,__l);}}}}";
                    }
                    return $"{_id5.Identifier.Text}_ptr[{idxExpr5}].{fieldPath} = {rhsExpr5}";
                }
            }

            string lhs = TranslateExpression(assign.Left);
            string rhs = TranslateExpression(assign.Right);
            string op = assign.OperatorToken.Text;

            // ★ struct-varying local 重赋值刷新（#13）：structLocal = array[other_idx] 后，
            //   _structVaryingLocals 里的 (arrName, elemType, indexExpr) 必须同步更新，
            //   否则后续 structLocal.field 访问仍用旧 indexExpr → gather/scatter 地址错误。
            //   仅整体重赋值（左值是纯 identifier）适用；字段赋值（structLocal.field = x）
            //   已在上方 scatter 分支处理，不在此刷新。
            if (op == "=" && assign.Left is IdentifierNameSyntax svlId
                && _structVaryingLocals.ContainsKey(svlId.Identifier.Text))
            {
                if (assign.Right is ElementAccessExpressionSyntax svlEA
                    && svlEA.Expression is IdentifierNameSyntax svlArrId
                    && _nativeArrayParams.TryGetValue(svlArrId.Identifier.Text, out var svlElemType)
                    && svlElemType != "float" && svlElemType != "int"
                    && !svlElemType.Contains("float2") && !svlElemType.Contains("int2"))
                {
                    string svlIdxExpr = svlEA.ArgumentList?.Arguments.Count > 0
                        ? TranslateExpression(svlEA.ArgumentList.Arguments[0].Expression)
                        : "0";
                    _structVaryingLocals[svlId.Identifier.Text] = (svlArrId.Identifier.Text, svlElemType, svlIdxExpr);
                }
                else
                {
                    // 重赋值为非数组元素（普通值/其他表达式）→ 该 local 不再指向数组元素，
                    // 移除映射；后续 field 访问回落通用路径（可能按标量/其他方式处理）。
                    _structVaryingLocals.Remove(svlId.Identifier.Text);
                }
            }

            // Scope narrowing: declare at first assignment
            string? declLhs = assign.Left is IdentifierNameSyntax declId ? declId.Identifier.Text : null;
            if (op == "=" && declLhs != null && _variables.TryGetValue(declLhs, out var lhsInfo) && lhsInfo.Kind >= VarKind.Varying && !_varDeclEmitted.Contains(declLhs))
            {
                string declType = GetSIMDTypeString(lhsInfo.CppType);
                if (declType != null)
                {
                    _varDeclEmitted.Add(declLhs);
                    _simdVaryingVarNames.Add(declLhs);
                    _simdVaryingCppType[declLhs] = lhsInfo.CppType;
                    return $"{declType} {lhs} = {rhs}";
                }
            }

            // ★ Reduction folding: return n_min_ps/n_max_ps instead of blend
            if (op == "=" && _foldReduceFn != null)
            {
                string fn = _foldReduceFn;
                _foldReduceFn = null;
                // Both operands are simd_value<T>, unwrap .v for raw n_float/n_int
                return $"{lhs} = simd_value<float>{{ {fn}({lhs}.v, {rhs}.v) }}";
            }

            // ★ CRITICAL: inside mask-narrowed context (if/else), SIMD assignment to a varying
            //   variable must use blend() to preserve inactive lanes.
            //   Without this, ALL lanes overwrite = lanes where condition is false lose their data.
            //   Affects reduction patterns: if(distSq < bestDistSq) { bestDistSq = distSq; bestIdx = i; }
            if (op == "=" && _currentMask != "simd_mask::all_true()")
            {
                string? lhsVar = assign.Left is IdentifierNameSyntax lhsId ? lhsId.Identifier.Text : null;
                if (lhsVar != null && _variables.TryGetValue(lhsVar, out var blInfo) && blInfo.Kind >= VarKind.Varying)
                {
                    return $"{lhs} = blend({lhs}, {rhs}, {_currentMask})";
                }
            }

            return $"{lhs} {op} {rhs}";
        }
        private string TranslateTernary(ConditionalExpressionSyntax ternary)
        {
            string condition = TranslateCondition(ternary.Condition);
            string whenTrue = TranslateExpression(ternary.WhenTrue);
            string whenFalse = TranslateExpression(ternary.WhenFalse);
            VarKind kind = _varAnalyzer.ClassifyExpression(ternary);

            if (kind >= VarKind.Varying)
            {
                // SIMD blend: mask ? true_val : false_val
                return $"blend({whenFalse}, {whenTrue}, {condition})";
            }

            return $"({condition} ? {whenTrue} : {whenFalse})";
        }

        

        /// <summary>
        /// Translate field access on struct NativeArray with direct element access.
        /// Handles: structArray[idx].fieldName
        /// </summary>
        private string TranslateStructArrayFieldAccess(string arrName, string structElemType, string fieldName, ExpressionSyntax? indexExpr)
        {
            if (indexExpr == null)
                return $"{arrName}_ptr[0].{fieldName}";

            string idxExpr = TranslateExpression(indexExpr);
            // ClassifyExpression may throw on SyntaxFactory nodes (modified AST)
            VarKind idxKind = VarKind.Varying;
            try { idxKind = _varAnalyzer.ClassifyExpression(indexExpr); } catch { }

            if (idxKind >= VarKind.Varying)
            {
                string safeIdx = _currentMask != "simd_mask::all_true()"
                    ? $"simd_min(simd_max({idxExpr}, simd_value<int>(0)), simd_value<int>::broadcast({arrName}_length - 1))"
                    : idxExpr;
                return $"simd_value<float>{{ n_gather_ps<sizeof({structElemType})>((const float*)(&{arrName}_ptr[0].{fieldName}), {safeIdx}.v) }}";
            }
            return $"{arrName}_ptr[{idxExpr}].{fieldName}";
        }

        /// <summary>
        /// Translate field access on a deferred struct local.
        /// The local was initialized from structArray[idx]; field access becomes
        /// a field-level gather with struct stride (ISPC-style).
        /// Handles: structLocal.fieldName  (where structLocal = structArray[idx])
        /// </summary>
        private string TranslateStructFieldAccess(string arrName, string structElemType, string fieldName, string idxExpr)
        {
            // Check if the index expression is varying (SIMD context)
            bool isVarying = idxExpr.Contains("v_") || idxExpr == "v_i" || idxExpr.Contains("simd_");
            // Also check the current mask context
            if (_currentMask != "simd_mask::all_true()" || isVarying)
            {
                string safeIdx = _currentMask != "simd_mask::all_true()"
                    ? $"simd_min(simd_max({idxExpr}, simd_value<int>(0)), simd_value<int>::broadcast({arrName}_length - 1))"
                    : idxExpr;
                return $"simd_value<float>{{ n_gather_ps<sizeof({structElemType})>((const float*)(&{arrName}_ptr[0].{fieldName}), {safeIdx}.v) }}";
            }
            return $"{arrName}_ptr[{idxExpr}].{fieldName}";
        }

        /// <summary>
        /// Check if a NativeArray element type is a user-defined struct (not SIMD-primitive).
        /// </summary>
        private static bool IsStructNativeArrayType(string elemCppType)
        {
            return elemCppType != "float" && elemCppType != "int"
                && !elemCppType.Contains("float2") && !elemCppType.Contains("int2");
        }
private string TranslateObjectCreation(ObjectCreationExpressionSyntax objCreation)
        {
                        INamedTypeSymbol? type = null;
            try { type = _semanticModel.GetTypeInfo(objCreation).Type as INamedTypeSymbol; } catch { }
            string cppType = type != null ? NativeTranspiler.MapCSharpTypeToCpp(type) : "int";

            if (objCreation.ArgumentList != null && objCreation.ArgumentList.Arguments.Count > 0)
            {
                string args = string.Join(", ",
                    objCreation.ArgumentList.Arguments.Select(a => TranslateExpression(a.Expression)));
                return $"{cppType}({args})";
            }

            return $"{cppType}()";
        }

        // ================================================================
        // Utilities
        // ================================================================

        /// <summary>
        /// 提取 SIMD 值中的 x/y 分量。
        /// 用于 float2 SIMD gather → 组件赋值
        /// </summary>
        /// <summary>
        /// 获取 C# 类型对应的 SIMD C++ 类型字符串
        /// </summary>
        private static string? GetSIMDTypeString(string cppType)
        {
            if (cppType.Contains("float2"))
                return "simd_value<EntJoy::Mathematics::float2>";
            if (cppType.Contains("int2"))
                return "simd_value<EntJoy::Mathematics::int2>";
            if (cppType == "float")
                return "simd_value<float>";
            if (cppType == "int")
                return "simd_value<int>";
            if (cppType == "bool")
                return "simd_value<int>";
            return null;
        }

        /// <summary>Check if an invocation is a gather/gathf call (produces clamped indices).</summary>
        private static bool IsGatherCall(InvocationExpressionSyntax inv)
        {
            if (inv.Expression is IdentifierNameSyntax id)
                return id.Identifier.Text == "gather" || id.Identifier.Text == "gathf";
            if (inv.Expression is MemberAccessExpressionSyntax ma)
                return ma.Name.Identifier.Text == "gather" || ma.Name.Identifier.Text == "gathf";
            return false;
        }

        private static bool IsFloat2Type(string cppType)
        {
            return cppType.Contains("float2") || cppType.Contains("int2");
        }

        private static BlockSyntax EnsureBlock(StatementSyntax stmt)
        {
            return stmt is BlockSyntax block ? block : SyntaxFactory.Block(stmt);
        }

        private void AppendLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                _builder.AppendLine();
                return;
            }
            _builder.Append(' ', _indent * 4);
            _builder.AppendLine(text);
        }

        // Helper for syntax factory methods
        private static class SyntaxFactory
        {
            public static BlockSyntax Block(StatementSyntax stmt)
            {
                return Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(stmt);
            }
        }
    }
}
