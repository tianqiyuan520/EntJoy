using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 从 C# 语法树生成 SIMD 值与活动掩码，按 uniform/varying 分类选择标量或向量操作。
    /// 本文件负责生成状态、语句分派及 lane 退出；分支、局部变量、循环与表达式各自位于对应 partial 文件。
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
        // Track goto targets for dead label elimination (suppress labels in hot paths)
        private readonly HashSet<string> _gotoTargets = new();
        // Variables whose last value came from a clamped gather — skip redundant clamp
        private readonly HashSet<string> _clampedVars = new();
        // Per-batch return mask: when a lane executes `return` (exit-Execute for that lane),
        //   we record it here and kill it from active. After the body, only lanes NOT in
        //   this mask get the "default" store (e.g. R[i] = 777 after a search loop).
        //   Avoids the old bug: one lane hits return → goto __simd_exit → all lanes skip
        //   the default store → non-returning lanes output 0 instead of 777 (EC5/FZ4 E7).
        private string _returnedMaskVar = "";
        // Struct varying locals: localName → (arrayName, elemCppType, indexExpr)
        // These are struct-typed locals initialized from array[idx] where array is a struct-typed NativeArray.
        // decomposed into field-level gather/scatter at code-gen time (ISPC-style).
        private readonly Dictionary<string, (string arrName, string elemCppType, string indexExpr)> _structVaryingLocals = new();

        // 指针符号 → 元素 C++ 类型：不再在本类拷贝一份，直接用 SimdVariableAnalyzer 的
        // LocalPointerElemCpp / ParamPointerElemCpp（同一份数据，避免"两个真相"）。
        //   源生成器里 SemanticModel 对局部符号不可靠（框架既有代码同样只用名字/`_jobStruct.GetMembers`），
        //   因此判据仍然只依赖分析器按语法收集的这份表，否则
        //   `int* hpPtr = (int*)HP.GetUnsafePtr();` 会被当成 NativeArray 字段而生成 `hpPtr_ptr`。

        /// <summary>
        /// <c>batchOffsetVar</c> 的哨兵值："没有可知的批循环偏移"（单 `IJob` 路径：用户的循环
        /// 起点不属于生成器可知的批循环）。此时不允许发"假定索引相对基址连续"的无掩码向量 store
        /// （见 <c>SimdExpressionTranslator.EmitElementStore</c> 的 NT-01 判定）。
        /// </summary>
        public const string UnknownBatchOffset = "0";

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

        // Entry Point

        /// <summary>
        /// 从 Execute 方法体生成 SIMD C++ 代码。
        /// uniform 循环共享计数器；varying 循环使用逐 lane 活动掩码。
        /// </summary>
        public string Generate(BlockSyntax body)
        {
            _builder.Clear();
            _forLoopVars.Clear();
            foreach (var fs in body.DescendantNodes().OfType<ForStatementSyntax>())
                if (fs.Declaration != null)
                    foreach (var v in fs.Declaration.Variables)
                        _forLoopVars.Add(v.Identifier.Text);

            // Prescan: find variables assigned from gather/gathf with clamp (skip redundant clamp)
            _clampedVars.Clear();
            foreach (var assign in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                if (assign.Left is IdentifierNameSyntax lhs && assign.Right is InvocationExpressionSyntax inv)
                    if (IsGatherCall(inv))
                        _clampedVars.Add(lhs.Identifier.Text);
            foreach (var decl in body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                foreach (var v in decl.Declaration.Variables)
                    if (v.Initializer?.Value is InvocationExpressionSyntax inv2 && IsGatherCall(inv2))
                        _clampedVars.Add(v.Identifier.Text);

            // IJob 的 Execute() 没有索引形参：整个实体体就是"整批标量循环"，per-lane 分组没有意义
            //   （si 无处可去，还会把体里自己的循环变量改名）。整段走标量翻译。
            // P0 兜底：体内出现「无法向量化」的调用（原子操作/取引用/用户静态辅助函数被喂 varying 实参）
            if (string.IsNullOrEmpty(_indexParamName)
                || RequiresPerLaneLoopReturn(body) || SimdVectorizability.HasNonVectorizableCall(body, _varAnalyzer))
            {
                GeneratePerLaneFullBody(body);
            }
            else {
                // E7 pre-scan: if body contains `return`, allocate a per-batch exit mask.
                //   After all loops, lanes that DID NOT return get the default store.
                _returnedMaskVar = "";
                if (body.DescendantNodes().OfType<ReturnStatementSyntax>().Any())
                {
                    _returnedMaskVar = $"__returned_{_labelCounter++}";
                    AppendLine($"simd_mask {_returnedMaskVar} = {{}};");
                }
                GenerateVariableDeclarations();
                // Pre-scan and emit hoisted uniform broadcasts (GridDimensions.x/y etc.)
                PreScanUniformHoists(body);
                EmitUniformHoistPrologue();
                GenerateBlock(body, skipBraces: true);
            }

            return _builder.ToString();
        }

        /// <summary>SIMD 路径原生支持（可向量化）的方法名——不属于这些名字且实参含 varying 的调用一律视为不可向量化。</summary>
        private static HashSet<string> VectorizableCallNames => SimdVectorizability.VectorizableCallNames;

        // 带 return 的 varying 非归约循环沿用逐 lane 返回路径。
        private bool RequiresPerLaneLoopReturn(SyntaxNode body)
        {
            if (!body.DescendantNodes().OfType<ReturnStatementSyntax>().Any()) return false;
            return body.DescendantNodes().OfType<ForStatementSyntax>().Any(statement =>
                IntForLoop.TryAnalyze(statement, _semanticModel, out var loop)
                && (_varAnalyzer.ClassifyExpression(loop.Start) >= VarKind.Varying
                    || _varAnalyzer.ClassifyExpression(loop.Bound) >= VarKind.Varying)
                && !IsReductionLoop(statement));
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

            bool hasReturn = scalarBody.Contains("return;");

            // NT-04：`Execute()`（IJob，无索引形参）根本没有 lane 可分 —— 索引形参名是空的，
            //   包 lane 循环只会把整段 body 跑 g_simdWidthInt 次（AVX2 ×8）且 lane 变量是个死变量。
            //   与 Generate() 里那句注释一致："整段走标量翻译"。此时 `return;` 保持原样
            //   （IJob 一次调用 ⇒ 退出整个 job，与 C# 语义一致）。
            if (string.IsNullOrEmpty(_indexParamName))
            {
                foreach (var line in scalarBody.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        AppendLine(line.TrimEnd());
                return;
            }

            // NT-03：`return;`（Execute 内）只结束本次 index。直接换 `break;` 会跳出
            //   lane 循环 ⇒ 静默跳过最多 g_simdWidthInt-1 个 index。这里与
            //   OuterSimdGenerator.GeneratePerLane 同构：do{}while(false) 把 break 收进"本次 lane"。
            //   （嵌在体内循环里的 return 无法用 break 表达 ⇒ 由 ReturnStatementRewriter 写标记，
            //     让构建期扫描失败 —— 见 NT-07。）
            if (hasReturn)
                scalarBody = ReturnStatementRewriter.Rewrite(scalarBody, body, "break;");

            AppendLine("for (int __ej_lane = 0; __ej_lane < g_simdWidthInt; __ej_lane++)");
            AppendLine("{");
            _indent++;
            AppendLine($"int {_indexParamName} = si + __ej_lane;");
            if (hasReturn) { AppendLine("do {"); _indent++; }

            foreach (var line in scalarBody.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    AppendLine(line.TrimEnd());

            if (hasReturn) { _indent--; AppendLine("} while(false);"); }
            _indent--;
            AppendLine("}");
        }

        // Statement Generators

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
                    // 静默丢弃 SIMD 语句 = 编译通过但算错；改写唯一标记，由构建期扫描拦下。
                    AppendLine($"// {UnsupportedMarkers.Stmt}SIMD_{stmt.Kind()}");
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

        // Loop Generators
        // (For / While / Do / Unroll / Reduction loop generators moved to
        // SimdLoopGenerator.cs partial)

        // Break / Continue / Return

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
                // mask（BodyMaskVar），使后续语句跳过已 break 的 lane；仅当所有 lane 都退出才
                // 触发即整组退出。
                AppendLine($"{frame.TrackerVar} = {frame.TrackerVar} & simd_mask{{ n_not_mask({_currentMask}.m) }};");
                if (!string.IsNullOrEmpty(frame.BodyMaskVar))
                    AppendLine($"{frame.BodyMaskVar} = {frame.BodyMaskVar} & simd_mask{{ n_not_mask({_currentMask}.m) }};");
                // 分支收敛：删除「tracker 空则立即 goto exit」——tracker 被清空后
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
            // Docs-style scalar for loop: mask already narrowed by if-body's early exit.
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
                // 分支收敛：不再 goto——改为从循环体 mask（BodyMaskVar）排除
                //   continue lane，后续语句照常执行但 mask 为空（无副作用），消除每轮 1 次
                //   any_true + 1 分支（C12：3 any_true/4 分支 → 2/2，对齐 ISPC）。
                //   continue 语句体（如 j+=1）已在 if 块内以 blend 形式执行完毕。
                if (string.IsNullOrEmpty(frame.BodyMaskVar))
                {
                    // 没有 body mask 的 tracker 循环：只能整组跳到 continue 标签。
                    AppendLine($"goto {frame.ContinueLabel};");
                    return;
                }
                AppendLine($"{frame.BodyMaskVar} = {frame.BodyMaskVar} & simd_mask{{ n_not_mask({_currentMask}.m) }};");
                _currentMask = frame.BodyMaskVar;
            }
        }

        private void GenerateReturnStatement(ReturnStatementSyntax stmt)
        {
            if (!string.IsNullOrEmpty(_returnedMaskVar))
            {
                // mark this lane as returned and kill from active mask.
                AppendLine($"{_returnedMaskVar} = simd_mask{{ n_or_mask({_returnedMaskVar}.m, {_currentMask}.m) }};");
                AppendLine($"{_currentMask} = simd_mask{{ n_and_mask({_currentMask}.m, n_not_mask({_returnedMaskVar}.m)) }};");
            }
            else
            {
                AppendLine("goto __simd_exit;");
            }
        }

        // Expression Statement

        private void GenerateExpressionStatement(ExpressionStatementSyntax stmt)
        {
            string expr = TranslateExpression(stmt.Expression);
            if (!string.IsNullOrEmpty(expr))
                AppendLine($"{expr};");
        }

        // Expression Translation (core)
        // (Expression translation methods — TranslateExpression / TranslateMath* /
        // SimdExpressionTranslator.cs partial)

        // Utilities

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
