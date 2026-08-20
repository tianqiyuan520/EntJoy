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
    ///
    /// 本类以 partial class 拆分到三个文件：
    /// - SimdControlFlowGenerator.cs   （本文件：状态、构造函数、主流程编排）
    /// - SimdExpressionTranslator.cs   （表达式翻译）
    /// - SimdLoopGenerator.cs          （循环/控制流产生）
    /// </summary>
    public partial class SimdControlFlowGenerator
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
        // Loop Generators
        // ================================================================
        // (For / While / Do / Unroll / Reduction loop generators moved to
        //  SimdLoopGenerator.cs partial)

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
        // (Expression translation methods — TranslateExpression / TranslateMath* /
        //  TranslateBinary* / TranslateCast* / TranslateAssignment* — moved to
        //  SimdExpressionTranslator.cs partial)

        // ================================================================
        // Utilities
        // ================================================================

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
