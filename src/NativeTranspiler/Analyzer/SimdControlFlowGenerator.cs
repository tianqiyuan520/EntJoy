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

        // Mask management
        private string _currentMask = "simd_mask::all_true()";
        private int _maskCounter;
        private int _labelCounter;

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

        public SimdControlFlowGenerator(
            SemanticModel semanticModel,
            INamedTypeSymbol jobStruct,
            Dictionary<string, SimdVariableInfo> variables,
            SimdVariableAnalyzer varAnalyzer,
            string indexParamName = "index",
            string simdIndexVar = "v_i",
            bool useFastMath = false)
        {
            _semanticModel = semanticModel;
            _jobStruct = jobStruct;
            _variables = variables;
            _varAnalyzer = varAnalyzer;
            _indexParamName = indexParamName;
            _simdIndexVar = simdIndexVar;
            _useFastMath = useFastMath;

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
        /// </summary>
        public string Generate(BlockSyntax body)
        {
            _builder.Clear();
            GenerateVariableDeclarations();
            GenerateBlock(body, skipBraces: true);
            return _builder.ToString();
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

                // Skip uniform variables (handled as scalar by outer scope)
                if (info.Kind == VarKind.Uniform) continue;

                // Varying or Reduction
                string varType = GetSIMDTypeString(info.CppType);
                if (varType == null) continue;

                if (IsFloat2Type(info.CppType))
                {
                    // Decompose float2/int2 into x/y components
                    string elemType = info.CppType.Contains("float2") ? "simd_value<float>" : "simd_value<int>";
                    string initVal = info.InitSIMDExpr ?? (elemType + "::broadcast(0)");
                    AppendLine($"{elemType} v_{name}_x = {GetComponentInit(name, info, ".x()", initVal)};");
                    AppendLine($"{elemType} v_{name}_y = {GetComponentInit(name, info, ".y()", initVal)};");
                }
                else
                {
                    string initVal = info.InitSIMDExpr ?? $"{varType}::broadcast(0)";
                    AppendLine($"{varType} v_{name} = {initVal};");
                }
            }
        }

        private string GetComponentInit(string varName, SimdVariableInfo info, string member, string fallback)
        {
            // For varying float2 QueryPositions[index] gather, generate:
            // simd_value<float> v_q_x = simd_value<float>::gathf(QueryPositions_ptr, v_i);
            // But that's done in expression translation, not here.
            return fallback;
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
            string savedMask = $"__mask_{_maskCounter++}";
            AppendLine($"simd_mask {savedMask} = {_currentMask};");

            // Collect all conditions and bodies (if / else-if chain)
            var conditions = new List<string>();
            var current = stmt;
            StatementSyntax? elseBody = null;

            while (true)
            {
                string condVar = $"__cond_{_maskCounter++}";
                string condExpr = TranslateCondition(current.Condition);
                AppendLine($"simd_mask {condVar} = {condExpr};");

                // True branch mask: saved & cond (with all previous not-conditions ANDed in for else-if)
                string trueMask;
                if (conditions.Count == 0)
                {
                    trueMask = $"simd_mask{{ n_and_mask({savedMask}.m, {condVar}.m) }}";
                }
                else
                {
                    // For else-if: saved & ~prev_cond & cond
                    string notPrev = BuildNotChain(conditions);
                    trueMask = $"simd_mask{{ n_and_mask({notPrev}.m, {condVar}.m) }}";
                    trueMask = $"simd_mask{{ n_and_mask({savedMask}.m, {trueMask}.m) }}";
                }

                conditions.Add(condVar);
                _currentMask = trueMask;
                GenerateBlock(EnsureBlock(current.Statement), skipBraces: false);

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
                _currentMask = $"simd_mask{{ n_and_mask({savedMask}.m, {BuildNotChain(conditions)}.m) }}";
                GenerateBlock(EnsureBlock(elseBody), skipBraces: false);
            }

            // Restore
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
                // Typically: for (i = start; i < end; i++)
                // end is cond.Right (assuming cond.Left is the induction variable)
                endExpr = TranslateExpression(cond.Right);
            }

            // Check if the loop is a simple (i++) increment pattern
            bool isSimpleIncrement = stmt.Incrementors.Count == 1;
            if (!isSimpleIncrement)
            {
                AppendLine("// Unsupported SIMD for-loop increment pattern");
                return;
            }

            // Count-loop pattern
            string tracker = $"__tracker_{_maskCounter++}";
            string iterActive = $"__iter_active_{_maskCounter++}";
            string exitLabel = $"__loop_exit_{_labelCounter++}";
            string continueLabel = $"__loop_continue_{_labelCounter++}";

            AppendLine($"// SIMD count-loop: for (int {ivName} = {startExpr}; {ivName} < ...; {ivName}++)");
            AppendLine($"simd_value<int> simd_{ivName} = {startExpr};");
            AppendLine($"simd_value<int> simd_end_{ivName} = {endExpr};");
            AppendLine($"simd_mask {tracker} = simd_mask::all_true();");
            AppendLine($"int simd_max_iter_{ivName} = hmax(simd_end_{ivName} - simd_{ivName});");
            AppendLine($"for (int __iter_{ivName} = 0; __iter_{ivName} < simd_max_iter_{ivName}; __iter_{ivName}++)");
            AppendLine("{");
            _indent++;

            AppendLine($"simd_mask {iterActive} = simd_mask{{ n_cmp_lt_epi32(simd_{ivName}.v, simd_end_{ivName}.v) }} & {tracker};");
            string savedMask = $"__mask_{_maskCounter++}";
            AppendLine($"simd_mask {savedMask} = {_currentMask};");
            _currentMask = $"{_currentMask} & {iterActive}";
            AppendLine($"if (!{_currentMask}.any_true()) {{ {_currentMask} = {savedMask}; goto {exitLabel}; }}");

            // Push loop frame
            _loopStack.Push(new LoopFrame
            {
                TrackerVar = tracker,
                IterActiveVar = iterActive,
                ExitLabel = exitLabel,
                ContinueLabel = continueLabel
            });

            // Generate loop body
            GenerateBlock(stmt.Statement is BlockSyntax fb ? fb : SyntaxFactory.Block(stmt.Statement), skipBraces: false);

            // Pop loop frame
            _loopStack.Pop();

            // Continue label + restore + increment
            AppendLine($"{continueLabel}: ;");
            _currentMask = savedMask;
            AppendLine($"simd_{ivName} = simd_{ivName} + 1;");
            AppendLine("}");
            _indent--;
            AppendLine($"{exitLabel}: ;");
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

            AppendLine($"{continueLabel}: ;");
            _currentMask = savedMask;
            AppendLine("}");
            _indent--;
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

            AppendLine($"{continueLabel}: ;");
            _currentMask = savedMask;

            string condExpr = TranslateCondition(stmt.Condition);
            string condVar = $"__dcond_{_maskCounter++}";
            AppendLine($"simd_mask {condVar} = {condExpr};");
            AppendLine($"}} while(({_currentMask} & {condVar} & {tracker}).any_true());");
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
            AppendLine($"goto {frame.ContinueLabel};");
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

                // If already classified by SimdVariableAnalyzer, use that
                if (_variables.TryGetValue(name, out var info) && info.Kind >= VarKind.Varying)
                {
                    string varType = GetSIMDTypeString(info.CppType);
                    if (varType == null) continue;

                    if (IsFloat2Type(info.CppType))
                    {
                        string elemType = info.CppType.Contains("float2") ? "simd_value<float>" : "simd_value<int>";
                        if (variable.Initializer != null)
                        {
                            string initExpr = TranslateExpression(variable.Initializer.Value);
                            AppendLine($"{elemType} v_{name}_x = {GetSIMDComponent(initExpr, "x")};");
                            AppendLine($"{elemType} v_{name}_y = {GetSIMDComponent(initExpr, "y")};");
                        }
                        else
                        {
                            AppendLine($"{elemType} v_{name}_x = {elemType}::broadcast(0);");
                            AppendLine($"{elemType} v_{name}_y = {elemType}::broadcast(0);");
                        }
                    }
                    else
                    {
                        if (variable.Initializer != null)
                        {
                            string initExpr = TranslateExpression(variable.Initializer.Value);
                            AppendLine($"{varType} v_{name} = {initExpr};");
                        }
                        else
                        {
                            AppendLine($"{varType} v_{name} = {varType}::broadcast(0);");
                        }
                    }
                }
                else
                {
                    // Uniform local — emit as scalar
                    var typeInfo = _semanticModel.GetTypeInfo(stmt.Declaration.Type);
                    string cppType = typeInfo.Type != null
                        ? NativeTranspiler.MapCSharpTypeToCpp(typeInfo.Type)
                        : "int";

                    if (variable.Initializer != null)
                    {
                        string initExpr = TranslateExpression(variable.Initializer.Value);
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
                // bool variable → broadcast to mask
                string name = id.Identifier.Text;
                if (_variables.TryGetValue(name, out var info) && info.Kind == VarKind.Uniform)
                {
                    // Uniform bool scalar: broadcast to mask
                    return $"simd_mask{{ n_and_mask(simd_mask{{ simd_value<int>::broadcast({name} ? -1 : 0).v }}.m, simd_mask::all_true().m) }}";
                }
                // Varying bool → already a mask?
                return $"simd_mask{{ n_cmp_ne_epi32(v_{name}.v, n_set1_epi32(0)) }}";
            }

            return TranslateExpression(expr);
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

            // Known variable
            if (_variables.TryGetValue(name, out var info))
            {
                if (info.Kind == VarKind.Uniform)
                    return name; // scalar

                // Varying or Reduction
                if (IsFloat2Type(info.CppType))
                {
                    // Return the whole float2 — x/y are decomposed as v_name_x, v_name_y
                    // The caller must use .x or .y member to access components
                    // For now, note that translating float2 as a whole is complex
                    return $"v_{name}_x"; // partial — caller should use member access
                }
                return $"v_{name}";
            }

            // Job struct field
            var symbol = _semanticModel.GetSymbolInfo(identifier).Symbol;
            if (symbol is IFieldSymbol field && !field.IsStatic
                && field.ContainingType.Equals(_jobStruct, SymbolEqualityComparer.Default))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    // NativeArray → _ptr suffix
                    if (_variables.TryGetValue(name, out var finfo) && finfo.Kind == VarKind.Uniform)
                        return name;
                    return name; // The container itself is uniform
                }

                // Scalar field → use name directly (it's a const ref)
                return name;
            }

            // Fallback: use name as-is
            return name;
        }

        private string TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            string memberName = memberAccess.Name.Identifier.Text;
            string objExpr = TranslateExpression(memberAccess.Expression);

            // Check if the object is a varying float2/int2
            string objName = memberAccess.Expression is IdentifierNameSyntax id ? id.Identifier.Text : null;
            bool isVaryingFloat2 = objName != null && _float2VaryingVars.Contains(objName);

            // .Length on NativeArray → _length suffix
            if (memberName == "Length")
            {
                return $"{objExpr}_length";
            }

            // .x or .y on float2
            if ((memberName == "x" || memberName == "y") && isVaryingFloat2)
            {
                return $"v_{objName}_{memberName}";
            }

            // Default: obj.member
            return $"{objExpr}.{memberName}";
        }

        private string TranslateElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
            var exprType = _semanticModel.GetTypeInfo(elementAccess.Expression).Type;
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

            bool isNativeArray = exprType != null
                && NativeTranspiler.IsEntJoyNativeContainerType(exprType)
                && exprType.Name == "NativeArray";

            if (isNativeArray && indexKind >= VarKind.Varying)
            {
                // SIMD gather: base_ptr[index]
                // baseExpr is the field name; we need base_ptr, and the element type
                string elemType = "";
                if (exprType is INamedTypeSymbol named)
                {
                    var typeArg = named.TypeArguments.FirstOrDefault();
                    if (typeArg != null)
                    {
                        string cppType = NativeTranspiler.MapCSharpTypeToCpp(typeArg);
                        if (cppType.Contains("float2"))
                            return $"simd_value<{cppType}>::gather({baseExpr}_ptr, {indexExpr})";
                        if (cppType == "float")
                            return $"simd_value<float>::gathf({baseExpr}_ptr, {indexExpr}.v)";
                        if (cppType == "int")
                            return $"simd_value<int>::gather({baseExpr}_ptr, {indexExpr}.v)";
                    }
                }
                // Fallback gather
                return $"simd_value<float>::gathf({baseExpr}_ptr, {indexExpr}.v)";
            }

            // Scalar access
            return $"{baseExpr}_ptr[{indexExpr}]";
        }

        private string TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            var symbol = _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (symbol == null)
                return "/* unknown function */ 0";

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
                            return $"simd_max(simd_min({v}, {hi}), {lo})";
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
                            // Expand as SIMD: (ax-bx)*(ax-bx) + (ay-by)*(ay-by)
                            // Assumes float2 with _x and _y components
                            string ax = $"{a}.x";
                            string ay = $"{a}.y";
                            string bx = $"{b}.x";
                            string by = $"{b}.y";

                            // For varying float2: use v_a_x, v_a_y components
                            if (args[0].Expression is IdentifierNameSyntax id0 && _float2VaryingVars.Contains(id0.Identifier.Text))
                            {
                                ax = $"{a}_x";
                                ay = $"{a}_y";
                            }
                            if (args[1].Expression is IdentifierNameSyntax id1 && _float2VaryingVars.Contains(id1.Identifier.Text))
                            {
                                bx = $"{b}_x";
                                by = $"{b}_y";
                            }

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
                            string ax = $"{a}_x", ay = $"{a}_y";
                            string bx = $"{b}_x", by = $"{b}_y";
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
                            // SIMD sqrt not always available; fallback to per-lane
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
            }

            // Default
            string call = $"std::{methodName}(";
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

                // Varying comparison → SIMD compare
                // Ensure both sides have .v for SIMD registers
                string leftV = leftKind >= VarKind.Varying ? $"{left}.v" : $"{left}";
                string rightV = rightKind >= VarKind.Varying ? $"{right}.v" : $"{right}";
                if (leftKind < VarKind.Varying && rightKind >= VarKind.Varying)
                    leftV = $"n_set1_ps({left})";
                else if (leftKind >= VarKind.Varying && rightKind < VarKind.Varying)
                    rightV = $"n_set1_ps({right})";

                string cmpFunc = op switch
                {
                    "<" => "n_cmp_lt_ps",
                    ">" => "n_cmp_gt_ps",
                    "<=" => "n_cmp_le_ps",
                    ">=" => "n_cmp_ge_ps",
                    "==" => "n_cmp_eq_ps",
                    "!=" => "n_cmp_ne_ps",
                    _ => "n_cmp_eq_ps"
                };

                // For int comparisons, use epi32 variants
                if (leftKind >= VarKind.Varying && _variables.Values.Any(v => v.CppType == "int"))
                {
                    // Check if the expression involves ints
                    string intCmp = op switch
                    {
                        "<" => "n_cmp_lt_epi32",
                        ">" => "n_cmp_gt_epi32",
                        "<=" => "n_cmp_le_epi32",
                        ">=" => "n_cmp_ge_epi32",
                        "==" => "n_cmp_eq_epi32",
                        "!=" => "n_cmp_ne_epi32",
                        _ => "n_cmp_eq_epi32"
                    };
                    return $"simd_mask{{ {intCmp}({leftV}, {rightV}) }}";
                }

                return $"simd_mask{{ {cmpFunc}({leftV}, {rightV}) }}";
            }

            // Logical operators
            if (binary.IsKind(SyntaxKind.LogicalAndExpression))
            {
                return anyVarying
                    ? $"simd_mask{{ n_and_mask({left}.m, {right}.m) }}"
                    : $"({left} && {right})";
            }
            if (binary.IsKind(SyntaxKind.LogicalOrExpression))
            {
                return anyVarying
                    ? $"simd_mask{{ n_or_mask({left}.m, {right}.m) }}"
                    : $"({left} || {right})";
            }

            // Arithmetic operators
            if (!anyVarying)
            {
                return $"({left} {op} {right})";
            }

            // At least one varying — SIMD arithmetic
            string simdOp = op switch
            {
                "+" => "+",
                "-" => "-",
                "*" => "*",
                "/" => "/",  // Not overloaded in simd_value — will use scalar division broadcast
                _ => "+"
            };

            return $"({left} {simdOp} {right})";
        }

        private string TranslateCast(CastExpressionSyntax cast)
        {
            // (int)floatExpr → convert
            string inner = TranslateExpression(cast.Expression);
            var targetType = _semanticModel.GetTypeInfo(cast.Type).Type;
            VarKind innerKind = _varAnalyzer.ClassifyExpression(cast.Expression);

            if (targetType?.SpecialType == SpecialType.System_Int32 && innerKind >= VarKind.Varying)
            {
                return $"simd_value<int>::convert({inner})";
            }

            return $"({NativeTranspiler.MapCSharpTypeToCpp(targetType!)}){inner}";
        }

        private string TranslateAssignment(AssignmentExpressionSyntax assign)
        {
            string lhs = TranslateExpression(assign.Left);
            string rhs = TranslateExpression(assign.Right);
            string op = assign.OperatorToken.Text;

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

        private string TranslateObjectCreation(ObjectCreationExpressionSyntax objCreation)
        {
            var type = _semanticModel.GetTypeInfo(objCreation).Type;
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
        private string GetSIMDComponent(string simdExpr, string component)
        {
            // If simdExpr is already a gather call, extract component
            if (simdExpr.Contains("::gather"))
            {
                if (component == "x")
                    return simdExpr.Replace("::gather(", "::gathf(");
                if (component == "y")
                    return simdExpr.Replace("::gather(", "::gathfy(");
            }
            return simdExpr;
        }

        /// <summary>
        /// 获取 C# 类型对应的 SIMD C++ 类型字符串
        /// </summary>
        private static string? GetSIMDTypeString(string cppType)
        {
            if (cppType == "float" || cppType.Contains("float2"))
                return "simd_value<float>";
            if (cppType == "int" || cppType.Contains("int2"))
                return "simd_value<int>";
            if (cppType == "bool")
                return "simd_value<int>"; // bool stored as 0/-1 in int reg
            return null;
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
