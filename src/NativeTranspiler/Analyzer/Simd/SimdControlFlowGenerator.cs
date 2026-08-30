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
            public string BodyMaskVar;    // 循环体当前 mask 变量（break 需窄化它，见 GenerateBreakStatement）
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
        // ★ Per-batch return mask: when a lane executes `return` (exit-Execute for that lane),
        //   we record it here and kill it from active. After the body, only lanes NOT in
        //   this mask get the "default" store (e.g. R[i] = 777 after a search loop).
        //   Avoids the old bug: one lane hits return → goto __simd_exit → all lanes skip
        //   the default store → non-returning lanes output 0 instead of 777 (EC5/FZ4 E7).
        private string _returnedMaskVar = "";

        // ★ E7 helpers: check C# expression type via SemanticModel.
        //   Used to detect int expressions being stored into float arrays.
        private bool IsInt32Type(ExpressionSyntax expr)
        {
            try { var t = _semanticModel.GetTypeInfo(expr).Type; return t != null && (t.SpecialType == SpecialType.System_Int32 || t.SpecialType == SpecialType.System_UInt32); }
            catch { return false; }
        }
        private bool IsInt32Expr(ExpressionSyntax expr) => IsInt32Type(expr);
        // Struct varying locals: localName → (arrayName, elemCppType, indexExpr)
        // These are struct-typed locals initialized from array[idx] where array is a struct-typed NativeArray.
        // Instead of creating a SIMD register for the whole struct, field accesses (temp.Field) are
        // decomposed into field-level gather/scatter at code-gen time (ISPC-style).
        private readonly Dictionary<string, (string arrName, string elemCppType, string indexExpr)> _structVaryingLocals = new();

        // ★ Scope-aware variable tracking: stack-based scope management
        // Each scope frame tracks which variables were declared in that scope
        private readonly Stack<HashSet<string>> _scopeStack = new();
        private int _scopeDepth = 0;

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
                // ★ E7 pre-scan: if body contains `return`, allocate a per-batch exit mask.
                //   After all loops, lanes that DID NOT return get the default store.
                _returnedMaskVar = "";
                if (body.DescendantNodes().OfType<ReturnStatementSyntax>().Any())
                {
                    _returnedMaskVar = $"__returned_{_labelCounter++}";
                    AppendLine($"simd_mask {_returnedMaskVar} = {{}};");
                }
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

            AppendLine("for (int lane = 0; lane < g_simdWidthInt; lane++)");
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
        // Scope Management
        // ================================================================

        /// <summary>Push a new scope frame.</summary>
        private int PushScope()
        {
            _scopeStack.Push(new HashSet<string>());
            return _scopeDepth++;
        }

        /// <summary>Pop a scope frame and remove its variables from _variables.</summary>
        private void PopScope()
        {
            if (_scopeStack.Count == 0) return;
            var frame = _scopeStack.Pop();
            _scopeDepth--;

            foreach (string varName in frame)
            {
                _variables.Remove(varName);
                _simdVaryingVarNames.Remove(varName);
                _simdVaryingCppType.Remove(varName);
                _varDeclEmitted.Remove(varName);
            }
        }

        /// <summary>Register a variable as declared in the current scope.</summary>
        private void RegisterScopedVariable(string name)
        {
            if (_scopeStack.Count > 0)
                _scopeStack.Peek().Add(name);
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
            var conditions = new List<string>();
            var current = stmt;
            StatementSyntax? elseBody = null;
            string savedMask = "";
            bool savedMaskEmitted = false;
            // ★ P1-2: 递推"已排除条件"组合掩码。excludedMaskVar 持有 ~c0 & ~c1 & ... & ~c_{k-1}，
            //   每个分支只在前一个变量上增量 AND 一个 ~c_k（O(1) 深度），替代 BuildNotChain 的
            //   O(N) 嵌套内联 —— 5+ 分支链会生成 5 层 n_and_mask 嵌套，寄存器压力剧增。
            string excludedMaskVar = null;
            // excludedCount = excludedMaskVar 已包含的条件个数（= conditions.Count 在上一分支时的值）
            int excludedCount = 0;

            // Pre-declaration needed when the chain writes a varying variable (regardless of
            // else). 赋值的掩码已由 TranslateAssignment 完成（纯赋值与复合赋值都 blend），
            // 无需再 save-blend——那会叠加一次冗余 blend + save。
            bool hasElseClause = HasElseClause(stmt);
            HashSet<string> modifiedVars = AnalyzeModifiedVars(stmt);
            bool needsPredeclare = hasElseClause || modifiedVars.Count > 0;
            if (needsPredeclare)
            {
                // ★ Pre-declare branch-written varying variables at the outer scope.
                //   The "declare at first assignment" rule would put the declaration
                //   inside the first branch block, making it invisible to later
                //   branches (e.g. float v; if (a>40) v=...; else if ... v=...;).
                foreach (var name in modifiedVars)
                    PredeclareBranchVar(name);
            }

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
                    // ★ De Morgan negation: flip comparisons AND swap AND/OR.
                    //   Use separate placeholders for ALL six comparison operators
                    //   to prevent self-canceling (old code: gt→le→gt because step 4
                    //   replaced the le created by step 3).
                    string goodExpr = condExpr
                        .Replace("n_cmp_lt_", "##T_LT##")
                        .Replace("n_cmp_ge_", "##T_GE##")
                        .Replace("n_cmp_gt_", "##T_GT##")
                        .Replace("n_cmp_le_", "##T_LE##")
                        .Replace("n_cmp_eq_", "##T_EQ##")
                        .Replace("n_cmp_ne_", "##T_NE##")
                        .Replace("##T_LT##", "n_cmp_ge_")
                        .Replace("##T_GE##", "n_cmp_lt_")
                        .Replace("##T_GT##", "n_cmp_le_")
                        .Replace("##T_LE##", "n_cmp_gt_")
                        .Replace("##T_EQ##", "n_cmp_ne_")
                        .Replace("##T_NE##", "n_cmp_eq_")
                        // ★ De Morgan: !(A && B) = !A || !B
                        .Replace("n_and_mask(", "##OR##(")
                        .Replace("n_or_mask(", "n_and_mask(")
                        .Replace("##OR##(", "n_or_mask(");
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
                    // ★ inside an UNROLLED loop there is no real C++ loop, so a
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
                        // All lanes active on entry: the branch mask is JUST the condition
                        // (no savedMask AND needed). The condition MUST still be emitted —
                        // otherwise conditions list stays empty, later branches get no
                        // exclusion chain and the final else mask becomes all_true → all
                        // branches execute unconditionally and overwrite each other.
                        // Saved mask = all_true (entry state), so later else-if branches
                        // combine as all_true & !c0 & c1 (not c0 & !c0 & c1 = false).
                        savedMask = "simd_mask::all_true()";
                        savedMaskEmitted = true;
                        // ★ 通解：全活跃 lane 时也折叠归约（if (v<best) best=v → n_min_ps），
                        //   否则 unroll 后的归约循环会退化成 cmp+blend（每轮多 1 次比较）。
                        if (TryFoldReduction(condExpr, current.Statement, out var foldFnAllTrue))
                        {
                            _foldReduceFn = foldFnAllTrue;
                            GenerateBlock(EnsureBlock(current.Statement), skipBraces: false);
                            _foldReduceFn = null;
                            current = null;
                            break; // skip mask push/pop
                        }
                        string condVar = $"__cond_{_maskCounter++}";
                        AppendLine($"simd_mask {condVar} = {condExpr};");
                        trueMask = condVar;
                        conditions.Add(condVar);
                        _currentMask = trueMask;
                    }
                    else if (conditions.Count == 0 && simpleCmp && savedMask.Contains("v_act"))
                    {
                        // Inline simple condition into __cm_N, skip __cond_N
                        string cm = $"__cm_{_maskCounter++}";
                        AppendLine($"simd_mask {cm} = simd_mask{{ n_and_mask({savedMask}.m, {condExpr}.m) }};");
                        trueMask = cm;
                        _currentMask = cm;
                        // ★ Register the raw condition for NOT chain computation by subsequent
                        //   branches. Without this, conditions stays empty and the else/elif
                        //   mask generates NOT(all_true) = all_true — losing the exclusion of
                        //   this condition (EC6 bug: else mask = v_active AND all_true).
                        string inlineCondVar = $"__cond_{_maskCounter++}";
                        AppendLine($"simd_mask {inlineCondVar} = {condExpr};");
                        conditions.Add(inlineCondVar);
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
                            // ★ P1-2: 引用递推排除变量（已含 ~c0 & ... & ~c_{k-1}），
                            //   不再内联 BuildNotChain 的 O(N) 嵌套。
                            EnsureExcludedMaskUpTo(conditions, ref excludedMaskVar, ref excludedCount);
                            string notPrev = excludedMaskVar ?? BuildNotChain(conditions);
                            trueMask = $"simd_mask{{ n_and_mask({notPrev}.m, {condVar}.m) }}";
                            // ★ Skip redundant AND when savedMask is all_true: all_true & X == X
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
                // ★ P1-2: 优先复用递推排除变量（~c0 & ... & ~c_{k-1}），避免内联 O(N) 嵌套。
                //   else 分支需排除全部条件 → 确保递推变量覆盖到 conditions.Count。
                EnsureExcludedMaskUpTo(conditions, ref excludedMaskVar, ref excludedCount);
                string notChain = excludedMaskVar ?? BuildNotChain(conditions);
                if (savedMaskEmitted)
                {
                    // ★ Skip redundant AND when savedMask is all_true: all_true & X == X
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

        /// <summary>
        /// ★ P1-2: 确保递推排除变量 excludedMaskVar 已覆盖 ~c0 & ~c1 & ... & ~c_{k-1}
        /// （conditions 中的全部历史条件 —— 调用时 conditions 为已加入条件，不含当前分支条件；
        ///  else 分支时含全部条件，语义相同：排除它们全部）。增量式：每个条件只生成一次
        /// n_and_mask，分支掩码深度从 O(N) 降到 O(1)，总生成量保持 O(N)。
        /// </summary>
        private void EnsureExcludedMaskUpTo(List<string> conditions, ref string excludedMaskVar, ref int excludedCount)
        {
            int target = conditions.Count;
            if (target <= 0)
            {
                if (target < 0) { excludedMaskVar = null; excludedCount = 0; }
                return;
            }

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

        /// <summary>检查 if 语句体是否为空</summary>
        private static bool IsEmptyBlock(StatementSyntax stmt)
        {
            if (stmt is BlockSyntax blk && blk.Statements.Count == 0) return true;
            if (stmt is EmptyStatementSyntax) return true;
            return false;
        }

        /// <summary>Check if an if-statement has an else clause (else-if or plain else).</summary>
        private static bool HasElseClause(IfStatementSyntax stmt)
        {
            var current = stmt;
            while (true)
            {
                if (current.Else == null) return false;
                if (current.Else.Statement is IfStatementSyntax nextIf)
                    current = nextIf;
                else
                    return true;
            }
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
            if (!(stmts[0] is ExpressionStatementSyntax ess) || !(ess.Expression is AssignmentExpressionSyntax assign))
                return false;
            // ★ Reduction recognition: only fold `if (x < y) x = y` or `if (x > y) x = y`
            //   (and mirror patterns where target is the RHS of the condition).
            //   Requires ALL of:
            //   1. Simple assignment (=), not compound (+=, -=, etc.)
            //   2. Both condition operands are identifiers (not literals)
            //   3. Assignment target matches one condition operand, RHS matches the other
            //   WITHOUT these checks, `if (acc > 1000) acc -= 5` would be wrongly folded
            //   to n_max_ps(acc, 1000) — producing completely wrong results (EC9 regression).
            if (!assign.IsKind(SyntaxKind.SimpleAssignmentExpression)) return false;
            if (!(assign.Left is IdentifierNameSyntax assignTarget)) return false;
            string assignTargetV = $"v_{assignTarget.Identifier.Text}";
            if (!condExpr.Contains(assignTargetV)) return false;
            if (!(assign.Right is IdentifierNameSyntax rhsId)) return false;
            string rhsV = $"v_{rhsId.Identifier.Text}";
            if (rhsV == assignTargetV) return false;
            if (!condExpr.Contains(rhsV)) return false;
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
        /// maskContinue=true（while-true 循环内）：continue 已被 mask 化（BodyMaskVar 排除），
        /// 不产生 goto → 不计入守卫判定（分支收敛：C12 条件1 消除 if 分支）。
        /// </summary>
        private static bool HasControlFlowGoto(SyntaxNode node, bool maskContinue)
        {
            foreach (var child in node.DescendantNodesAndSelf())
            {
                if (child is BreakStatementSyntax or ReturnStatementSyntax)
                    return true;
                if (child is ContinueStatementSyntax && !maskContinue)
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
            if (string.IsNullOrEmpty(frame.TrackerVar))
            {
                // Uniform scalar for-loop: all lanes share the loop counter, so a
                // break is a plain goto to the exit label (no per-lane tracker to kill).
                // The old code emitted "<empty> = <empty> & ~<empty>;" here.
                AppendLine($"goto {frame.ExitLabel};");
            }
            else if (string.IsNullOrEmpty(frame.IterActiveVar))
            {
                // Uniform-count loop with a per-lane (varying) break condition:
                // kill the matching lanes from the tracker; only when NO lane is left
                // active do we exit the loop. Otherwise unmatched lanes must keep
                // iterating (e.g. "find first j where A>50; break").
                AppendLine($"{frame.TrackerVar} = {frame.TrackerVar} & simd_mask{{ n_not_mask({_currentMask}.m) }};");
                AppendLine($"if (!({frame.TrackerVar}).any_true()) {{ goto {frame.ExitLabel}; }}");
            }
            else
            {
                // While-true / do-while 循环（有 tracker 与 body mask）：varying break。
                // 剔除命中 break 的 lane（用 _currentMask，即 break 条件的掩码），并窄化循环体
                // mask（BodyMaskVar），使后续语句跳过已 break 的 lane；仅当所有 lane 都退出才
                // 离开循环。旧的 `& ~IterActiveVar` 用错了掩码，`goto exit` 又会让单条 lane
                // 触发即整组退出。
                AppendLine($"{frame.TrackerVar} = {frame.TrackerVar} & simd_mask{{ n_not_mask({_currentMask}.m) }};");
                if (!string.IsNullOrEmpty(frame.BodyMaskVar))
                    AppendLine($"{frame.BodyMaskVar} = {frame.BodyMaskVar} & simd_mask{{ n_not_mask({_currentMask}.m) }};");
                // ★ 分支收敛：删除「tracker 空则立即 goto exit」——tracker 被清空后
                //   下一轮循环头（`wm = wcond & tracker; if (!wm.any_true()) break`）自然退出，
                //   语义等价（空 mask 的 blend 无副作用），省每轮 1 次 any_true + 1 分支。
            }
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
                // While-true mask loop：continue 只跳过本次迭代剩余语句，lane 仍保留在
                // 循环中（下一轮继续）。不能剔除 tracker——那是 break 的语义；否则命中
                // continue 的 lane 会被永久移出循环，后续迭代不再参与（C12）。
                // ★ 分支收敛：不再 goto——改为从循环体 mask（BodyMaskVar）排除
                //   continue lane，后续语句照常执行但 mask 为空（无副作用），消除每轮 1 次
                //   any_true + 1 分支（C12：3 any_true/4 分支 → 2/2，对齐 ISPC）。
                //   continue 语句体（如 j+=1）已在 if 块内以 blend 形式执行完毕。
                AppendLine($"{frame.BodyMaskVar} = {frame.BodyMaskVar} & simd_mask{{ n_not_mask({_currentMask}.m) }};");
                _currentMask = frame.BodyMaskVar;
            }
        }

        private void GenerateReturnStatement(ReturnStatementSyntax stmt)
        {
            if (!string.IsNullOrEmpty(_returnedMaskVar))
            {
                // ★ mark this lane as returned and kill from active mask.
                AppendLine($"{_returnedMaskVar} = simd_mask{{ n_or_mask({_returnedMaskVar}.m, {_currentMask}.m) }};");
                AppendLine($"{_currentMask} = simd_mask{{ n_and_mask({_currentMask}.m, n_not_mask({_returnedMaskVar}.m)) }};");
            }
            else
            {
                AppendLine("goto __simd_exit;");
            }
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

        // ================================================================
        // Save-blend: precise variable modification analysis
        // ================================================================

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
                // ★ Prefix unary: ONLY ++a / --a are writes. Unary minus (-a) / !a / ~a are reads —
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
            // ★ 初始化为 broadcast(0)：移除 save-blend 后，首个分支的 blend 会读旧值；
            //   未初始化的 varying 局部变量（float r; 由 if/else 定值）需先归零避免读垃圾。
            AppendLine($"{vType} v_{name} = {(mInfo.CppType == "float" ? "0.0f" : "0")};");
            _varDeclEmitted.Add(name);
            _simdVaryingVarNames.Add(name);
            _simdVaryingCppType[name] = mInfo.CppType;
        }
    }
}
