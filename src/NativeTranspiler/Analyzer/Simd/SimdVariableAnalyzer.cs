using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 变量分类：uniform（所有 lane 相同）、varying（每 lane 不同）、reduction（需水平归约的 varying）
    /// </summary>
    public enum VarKind
    {
        Uniform,
        Varying,
        Reduction
    }

    /// <summary>
    /// SIMD 上下文中变量的类型信息
    /// </summary>
    public class SimdVariableInfo
    {
        /// <summary>C# 变量名</summary>
        public string Name { get; set; } = "";

        /// <summary>uniform/varying/reduction</summary>
        public VarKind Kind { get; set; } = VarKind.Uniform;

        /// <summary>
        /// C++ 类型字符串。
        /// Uniform → 标量类型（float, int, EntJoy::Mathematics::float2）
        /// Varying/Reduction → simd_value 类型（simd_value&lt;float&gt;）
        /// </summary>
        public string CppType { get; set; } = "";

        /// <summary>原始 C# 类型（uint/int/float 等）。SemanticModel 在源生成器上下文中
        /// 不能可靠区分 uint 和 int，需要此字段来判断 uint 右移应使用逻辑移位。</summary>
        public string? CSharpType { get; set; }

        /// <summary>SIMD 变量的初始值表达式（如 "simd_value&lt;float&gt;::broadcast(0.0f)"）</summary>
        public string? InitSIMDExpr { get; set; }

        /// <summary>对于 float2/int2：分解后的 x/y 子变量</summary>
        public List<SimdVariableInfo>? Components { get; set; }
    }

    /// <summary>
    /// 分析 Execute 方法体，自动推导每个变量是 uniform/varying/reduction。
    ///
    /// 推导规则：
    /// 1. Execute index 参数 → varying
    /// 2. 从 NativeArray[index] 读取 → varying
    /// 3. 标量字段/常量 → uniform
    /// 4. 赋值 lhs = rhs: lhs 继承 rhs 的分类
    /// 5. 复合赋值 lhs op= varying: 如果 lhs 原是 uniform，提升为 reduction
    /// 6. min/max 规约模式 if(val &lt; best) best = val → best 是 reduction
    /// 7. 函数调用结果：operands 的分类取 max（uniform &lt; varying &lt; reduction）
    /// </summary>
    public class SimdVariableAnalyzer
    {
        private readonly SemanticModel _semanticModel;
        private readonly INamedTypeSymbol _jobStruct;
        private readonly string _indexParamName;
        private readonly Dictionary<string, SimdVariableInfo> _variables = new();

        // 已检测到的规约模式：在 if(x < best) { best = x; ... } 中标记 best 为 reduction
        private readonly HashSet<string> _reductionTargets = new();

        public SimdVariableAnalyzer(SemanticModel semanticModel, INamedTypeSymbol jobStruct, string indexParamName = "index")
        {
            _semanticModel = semanticModel;
            _jobStruct = jobStruct;
            _indexParamName = indexParamName;
        }

        /// <summary>
        /// 分析 Execute 方法体，返回变量分类字典。
        /// 返回空字典表示分析失败（该方法体不适合通用 SIMD 生成）。
        /// </summary>
        public Dictionary<string, SimdVariableInfo> Analyze(MethodDeclarationSyntax method)
        {
            _variables.Clear();
            _reductionTargets.Clear();

            if (method.Body == null)
                return _variables;

            try
            {
                // === Step 1: 种子分类 ===
                // 1a: Execute 参数（保守：直接用参数名判断，避免 GetDeclaredSymbol 在 source gen 上下文中抛异常）
                foreach (var param in method.ParameterList.Parameters)
                {
                    string name = param.Identifier.Text;
                    bool isIndex = name == _indexParamName;
                    // ★ Type from the parameter's declared type — static-method scalar params
                    //   (e.g. float threshold) must not be classified as int, otherwise float
                    //   comparisons compile to n_cmp_*_epi32 and produce wrong results.
                    string paramCppType = "int";
                    if (param.Type is PredefinedTypeSyntax pts)
                        paramCppType = pts.Keyword.Text; // "float", "double", "bool", ...
                    else if (param.Type is ArrayTypeSyntax)
                        paramCppType = "int";
                    AddVariable(name, paramCppType, isIndex ? VarKind.Varying : VarKind.Uniform, null);
                }

                // 1b：Job struct 字段预分类（在 OuterSimdGenerator 或 CppJobGenerator 层级处理）
                //      标量字段 → uniform；容器字段不参与变量分析
                // 1c：方法体内变量声明
                CollectDeclarations(method.Body);

                // === Step 2: 表达式传播 ===
                // 遍历所有赋值，传播 classification
                PropagateAssignments(method.Body);

                // === Step 3: Reduction 模式检测 ===
                // 检测 if (val &lt; best) { best = val; } 规约模式
                DetectReductionPatterns(method.Body);

                // 应用 reduction 标记
                ApplyReductionMarkers();
            }
            catch
            {
                // Any error in variable analysis → return empty (fallback to per-lane)
                _variables.Clear();
            }

            return _variables;
        }

        /// <summary>
        /// 获取变量分类结果（只读）
        /// </summary>
        public IReadOnlyDictionary<string, SimdVariableInfo> Variables => _variables;

        /// <summary>
        /// 判断一个表达式是否产生 varying 值。
        /// 供 SimdControlFlowGenerator 在表达式翻译时使用。
        /// </summary>
        public VarKind ClassifyExpression(ExpressionSyntax expr)
        {
            try
            {
                return ClassifyExpressionInternal(expr, new HashSet<string>());
            }
            catch
            {
                return VarKind.Uniform;
            }
        }

        // ================================================================
        // 内部实现
        // ================================================================

        private void AddVariable(string name, string cppType, VarKind kind, string? initExpr, string? csharpType = null)
        {
            _variables[name] = new SimdVariableInfo
            {
                Name = name,
                Kind = kind,
                CppType = cppType,
                InitSIMDExpr = initExpr,
                CSharpType = csharpType
            };
        }

        /// <summary>
        /// 从方法体重收集所有局部变量声明
        /// </summary>
        private void CollectDeclarations(SyntaxNode node)
        {
            foreach (var localDecl in node.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
            {
                foreach (var variable in localDecl.Declaration.Variables)
                {
                    string name = variable.Identifier.Text;
                    if (_variables.ContainsKey(name)) continue;

                    // Text-based type detection (safe without semantic model)
                    string typeText = localDecl.Declaration.Type.ToString();
                    string cppType = "int";
                    if (typeText.Contains("float2")) cppType = "EntJoy::Mathematics::float2";
                    else if (typeText.Contains("int2")) cppType = "EntJoy::Mathematics::int2";
                    else if (typeText == "float") cppType = "float";
                    else if (typeText == "bool") cppType = "bool";
                    AddVariable(name, cppType, VarKind.Uniform, null, csharpType: typeText);
                }
            }

            // for 循环变量 → varying
            foreach (var forStmt in node.DescendantNodes().OfType<ForStatementSyntax>())
            {
                if (forStmt.Declaration != null)
                {
                    foreach (var v in forStmt.Declaration.Variables)
                    {
                        string name = v.Identifier.Text;
                        if (!_variables.ContainsKey(name))
                            AddVariable(name, "int", VarKind.Varying, null);
                        else { _variables[name].Kind = VarKind.Varying; _variables[name].CppType = "int"; }
                    }
                }
            }
        }

        /// <summary>
        /// 遍历所有赋值和局部初始化，按文档顺序传播 classification。
        /// 关键：必须按文档顺序处理，使 `float distSq = init; if(...) best = distSq;`
        /// 中 distSq 在赋给 best 之前已被正确分类为 Varying。
        /// </summary>
        private void PropagateAssignments(SyntaxNode node)
        {
            foreach (var child in node.DescendantNodes())
            {
                if (child is AssignmentExpressionSyntax assignment)
                {
                    ProcessAssignment(assignment);
                }
                else if (child is VariableDeclaratorSyntax varDecl && varDecl.Initializer != null)
                {
                    string name = varDecl.Identifier.Text;
                    if (_variables.ContainsKey(name))
                        ProcessLocalInitializer(name, varDecl.Initializer.Value);
                }
            }
        }

        private void ProcessLocalInitializer(string varName, ExpressionSyntax initValue)
        {
            if (!_variables.TryGetValue(varName, out var lhsInfo))
                return;
            VarKind rhsKind = ClassifyExpressionInternal(initValue, new HashSet<string>());
            if (rhsKind > lhsInfo.Kind)
                lhsInfo.Kind = rhsKind;
        }

        private void ProcessAssignment(AssignmentExpressionSyntax assignment)
        {
            string? lhsName = GetLHSVariableName(assignment.Left);
            if (lhsName == null || !_variables.TryGetValue(lhsName, out var lhsInfo))
                return;

            VarKind rhsKind = ClassifyExpressionInternal(assignment.Right, new HashSet<string>());

            bool isCompound = assignment.IsKind(SyntaxKind.AddAssignmentExpression)
                || assignment.IsKind(SyntaxKind.SubtractAssignmentExpression)
                || assignment.IsKind(SyntaxKind.MultiplyAssignmentExpression);

            if (isCompound)
            {
                // 复合赋值：如果 lhs 是 uniform 但 rhs 是 varying → 提升为 reduction
                if (lhsInfo.Kind == VarKind.Uniform && rhsKind >= VarKind.Varying)
                    lhsInfo.Kind = VarKind.Reduction;
                else if (lhsInfo.Kind < rhsKind)
                    lhsInfo.Kind = rhsKind;
            }
            else
            {
                // 简单赋值：lhs 继承 rhs 的分类
                if (rhsKind > lhsInfo.Kind)
                    lhsInfo.Kind = rhsKind;
            }
        }

        /// <summary>
        /// 检测 if(val &lt; best) { best = val; } 规约模式
        /// </summary>
        private void DetectReductionPatterns(SyntaxNode node)
        {
            foreach (var ifStmt in node.DescendantNodes().OfType<IfStatementSyntax>())
            {
                if (ifStmt.Condition is BinaryExpressionSyntax condition
                    && (condition.IsKind(SyntaxKind.LessThanExpression)
                        || condition.IsKind(SyntaxKind.GreaterThanExpression)))
                {
                    string? leftName = GetExprVariableName(condition.Left);
                    string? rightName = GetExprVariableName(condition.Right);

                    // 检查 true 分支：格式 "best = val" 或 "best = val; idx = i;"
                    var trueBlock = ifStmt.Statement is BlockSyntax blk
                        ? blk.Statements.ToList()
                        : new List<StatementSyntax> { ifStmt.Statement };

                    // 检测 if(left &lt; right) { right = left; ... }
                    // 或 if(right &lt; left) { left = right; ... }
                    foreach (var stmt in trueBlock)
                    {
                        if (stmt is ExpressionStatementSyntax es
                            && es.Expression is AssignmentExpressionSyntax assign)
                        {
                            string? assignTarget = GetExprVariableName(assign.Left);
                            string? assignValue = GetExprVariableName(assign.Right);

                            if (assignTarget != null && assignValue != null)
                            {
                                // 模式: if(left &lt; right) { right = left; ... }
                                if (assignTarget == rightName && assignValue == leftName
                                    && condition.IsKind(SyntaxKind.LessThanExpression))
                                {
                                    _reductionTargets.Add(assignTarget);
                                }
                                // 模式: if(left &gt; right) { left = right; ... }
                                else if (assignTarget == leftName && assignValue == rightName
                                    && condition.IsKind(SyntaxKind.GreaterThanExpression))
                                {
                                    _reductionTargets.Add(assignTarget);
                                }
                            }
                        }
                    }
                }
            }
        }

        private void ApplyReductionMarkers()
        {
            foreach (string target in _reductionTargets)
            {
                if (_variables.TryGetValue(target, out var info) && info.Kind == VarKind.Varying)
                {
                    info.Kind = VarKind.Reduction;
                }
            }
        }

        // ================================================================
        // 表达式分类
        // ================================================================

        private VarKind ClassifyExpressionInternal(ExpressionSyntax expr, HashSet<string> visiting)
        {
            switch (expr)
            {
                case LiteralExpressionSyntax _:
                    return VarKind.Uniform;

                case IdentifierNameSyntax identifier:
                    return ClassifyIdentifier(identifier);

                case MemberAccessExpressionSyntax memberAccess:
                    return ClassifyMemberAccess(memberAccess);

                case ElementAccessExpressionSyntax elementAccess:
                    return ClassifyElementAccess(elementAccess);

                case BinaryExpressionSyntax binary:
                    return ClassifyBinary(binary, visiting);

                case InvocationExpressionSyntax invocation:
                    return ClassifyInvocation(invocation, visiting);

                case PrefixUnaryExpressionSyntax prefix:
                    return ClassifyExpressionInternal(prefix.Operand, visiting);

                case ParenthesizedExpressionSyntax paren:
                    return ClassifyExpressionInternal(paren.Expression, visiting);

                case CastExpressionSyntax cast:
                    return ClassifyExpressionInternal(cast.Expression, visiting);

                case ConditionalExpressionSyntax ternary:
                    var cond = ClassifyExpressionInternal(ternary.Condition, visiting);
                    var whenTrue = ClassifyExpressionInternal(ternary.WhenTrue, visiting);
                    var whenFalse = ClassifyExpressionInternal(ternary.WhenFalse, visiting);
                    return Max(cond, Max(whenTrue, whenFalse));

                case AssignmentExpressionSyntax assign:
                    // 处理链式赋值 a = b = c
                    return ClassifyExpressionInternal(assign.Right, visiting);

                case CheckedExpressionSyntax checkedExpr:
                    // ★ E2 fix: `unchecked(x + y)` / `checked(x + y)` — classification flows
                    //   from the inner expression (both are CheckedExpressionSyntax in Roslyn).
                    return ClassifyExpressionInternal(checkedExpr.Expression, visiting);

                case ObjectCreationExpressionSyntax _:
                    // new float2(x, y) — 看参数
                    return VarKind.Uniform; // 保守

                default:
                    return VarKind.Uniform; // 未知表达式保守返回 uniform
            }
        }

        
        private VarKind ClassifyIdentifier(IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.Text;
            if (_variables.TryGetValue(name, out var info)) return info.Kind;
            if (_jobStruct != null)
            {
                try
                {
                    var members = _jobStruct.GetMembers(name);
                    if (members.Length > 0 && members[0] is IFieldSymbol field && !field.IsStatic)
                        return NativeTranspiler.IsEntJoyNativeContainerType(field.Type) ? VarKind.Uniform : VarKind.Uniform;
                }
                catch { }
            }
            return VarKind.Uniform;
        }

private VarKind ClassifyMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            // 处理 float2.x / float2.y
            string memberName = memberAccess.Name.Identifier.Text;
            var exprKind = ClassifyExpressionInternal(memberAccess.Expression, new HashSet<string>());

            // 如果是 .x / .y 成员访问且 source 是 varying，则分量也是 varying
            if ((memberName == "x" || memberName == "y") && exprKind >= VarKind.Varying)
                return VarKind.Varying;

            // 成员方法调用（如 .Length, .x()）— 表达式本身是 uniform 或看上下文
            return exprKind;
        }

        private VarKind ClassifyElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
            // arr[index] — 如果 arr 是 NativeArray 且 index 是 varying → varying
            var arrKind = ClassifyExpressionInternal(elementAccess.Expression, new HashSet<string>());

            // 索引表达式分类
            VarKind indexKind = VarKind.Uniform;
            if (elementAccess.ArgumentList?.Arguments.Count > 0)
            {
                indexKind = ClassifyExpressionInternal(
                    elementAccess.ArgumentList.Arguments[0].Expression, new HashSet<string>());
            }

            // NativeArray[varying_index] → varying
            if (indexKind >= VarKind.Varying)
                return VarKind.Varying;

            // arr[varying_index] → varying (保守)
            if (arrKind >= VarKind.Varying)
                return VarKind.Varying;

            return VarKind.Uniform;
        }

        private VarKind ClassifyBinary(BinaryExpressionSyntax binary, HashSet<string> visiting)
        {
            var leftKind = ClassifyExpressionInternal(binary.Left, visiting);
            var rightKind = ClassifyExpressionInternal(binary.Right, visiting);

            // 比较运算（&lt;, &gt;, ==, !=, &amp;&amp;, ||）→ 结果用于 mask
            if (binary.IsKind(SyntaxKind.LessThanExpression)
                || binary.IsKind(SyntaxKind.GreaterThanExpression)
                || binary.IsKind(SyntaxKind.LessThanOrEqualExpression)
                || binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression)
                || binary.IsKind(SyntaxKind.EqualsExpression)
                || binary.IsKind(SyntaxKind.NotEqualsExpression)
                || binary.IsKind(SyntaxKind.LogicalAndExpression)
                || binary.IsKind(SyntaxKind.LogicalOrExpression))
            {
                // mask 的分类：operand 的 max
                return Max(leftKind, rightKind);
            }

            // 算术运算：结果分类 = operand 的 max
            return Max(leftKind, rightKind);
        }

        
        private VarKind ClassifyInvocation(InvocationExpressionSyntax invocation, HashSet<string> visiting)
        {
            try
            {
                var symbol = _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (symbol != null)
                {
                    string ct = symbol.ContainingType?.ToDisplayString() ?? "";
                    if (ct == "EntJoy.Mathematics.math" || ct == "System.MathF")
                    {
                        VarKind mk = VarKind.Uniform;
                        foreach (var arg in invocation.ArgumentList.Arguments)
                        {
                            var ak = ClassifyExpressionInternal(arg.Expression, visiting);
                            if (ak > mk) mk = ak;
                        }
                        return mk;
                    }
                }

                // ★ Fallback: when GetSymbolInfo fails (e.g. on SyntaxFactory-created AST nodes),
                //   try name-based matching for known math functions. If the function name matches
                //   a known math function (Sin, Cos, Sqrt, etc.) and any argument is Varying,
                //   return Varying — this keeps inner-loop SIMD propagation alive.
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    string fnName = memberAccess.Name.Identifier.Text;
                    if (IsKnownMathFunction(fnName))
                    {
                        VarKind mk = VarKind.Uniform;
                        foreach (var arg in invocation.ArgumentList.Arguments)
                        {
                            var ak = ClassifyExpressionInternal(arg.Expression, visiting);
                            if (ak > mk) mk = ak;
                        }
                        return mk;
                    }
                }
            }
            catch { }
            return VarKind.Uniform;
        }

        private static bool IsKnownMathFunction(string name)
        {
            switch (name)
            {
                case "Sin": case "Cos": case "Tan":
                case "Asin": case "Acos": case "Atan":
                case "Sinh": case "Cosh": case "Tanh":
                case "Exp": case "Log": case "Log10":
                case "Sqrt": case "Abs":
                case "Min": case "Max":
                case "Ceiling": case "Floor": case "Round": case "Truncate":
                case "Atan2": case "Pow":
                case "sin": case "cos": case "tan":
                case "asin": case "acos": case "atan":
                case "sinh": case "cosh": case "tanh":
                case "exp": case "log": case "log10":
                case "sqrt": case "abs":
                case "min": case "max":
                case "ceil": case "floor": case "round": case "trunc":
                case "atan2": case "pow":
                    return true;
                default:
                    return false;
            }
        }


        private static string? GetLHSVariableName(ExpressionSyntax expr)
        {
            return expr is IdentifierNameSyntax id ? id.Identifier.Text : null;
        }

        private static string? GetExprVariableName(ExpressionSyntax expr)
        {
            return expr is IdentifierNameSyntax id ? id.Identifier.Text : null;
        }

        private static VarKind Max(VarKind a, VarKind b)
        {
            return a > b ? a : b;
        }

        /// <summary>
        /// 将 C# 类型映射为 SIMD 上下文的 C++ 类型字符串。
        /// 对于 float2/int2 类型只返回 EntJoy::Mathematics 格式。
        /// </summary>
        private static string GetCppTypeString(ITypeSymbol type)
        {
            return NativeTranspiler.MapCSharpTypeToCpp(type);
        }
    }
}
