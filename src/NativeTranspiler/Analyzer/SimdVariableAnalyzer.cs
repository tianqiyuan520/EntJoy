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

            // === Step 1: 种子分类 ===
            // 1a: Execute 参数
            foreach (var param in method.ParameterList.Parameters)
            {
                string name = param.Identifier.Text;
                var typeInfo = _semanticModel.GetDeclaredSymbol(param);
                if (typeInfo is IParameterSymbol paramSym)
                {
                    bool isIndex = paramSym.Type.SpecialType == SpecialType.System_Int32 && name == _indexParamName;
                    AddVariable(name, GetCppTypeString(paramSym.Type),
                        isIndex ? VarKind.Varying : VarKind.Uniform, null);
                }
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
            return ClassifyExpressionInternal(expr, new HashSet<string>());
        }

        // ================================================================
        // 内部实现
        // ================================================================

        private void AddVariable(string name, string cppType, VarKind kind, string? initExpr)
        {
            _variables[name] = new SimdVariableInfo
            {
                Name = name,
                Kind = kind,
                CppType = cppType,
                InitSIMDExpr = initExpr
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

                    var typeInfo = _semanticModel.GetTypeInfo(localDecl.Declaration.Type);
                    if (typeInfo.Type == null) continue;

                    string cppType = GetCppTypeString(typeInfo.Type);
                    // 先标记为 uniform，后续传播会修正
                    AddVariable(name, cppType, VarKind.Uniform, null);
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
                        {
                            AddVariable(name, "int", VarKind.Varying, null);
                        }
                        else
                        {
                            // for 循环变量覆盖之前的分类
                            _variables[name].Kind = VarKind.Varying;
                            _variables[name].CppType = "int";
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 遍历所有赋值语句，传播 classification
        /// </summary>
        private void PropagateAssignments(SyntaxNode node)
        {
            foreach (var assignment in node.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                ProcessAssignment(assignment);
            }
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

            // 检查是否是已知变量
            if (_variables.TryGetValue(name, out var info))
                return info.Kind;

            // 检查是否是 Job struct 字段
            var symbol = _semanticModel.GetSymbolInfo(identifier).Symbol;
            if (symbol is IFieldSymbol field && !field.IsStatic)
            {
                if (field.ContainingType.Equals(_jobStruct, SymbolEqualityComparer.Default))
                {
                    // 容器类型字段不直接分类；容器名本身是 uniform
                    if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                        return VarKind.Uniform;

                    // 标量字段 → uniform
                    return VarKind.Uniform;
                }
            }

            // 未知标识符 → uniform
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
            var symbol = _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (symbol == null) return VarKind.Uniform;

            string containingType = symbol.ContainingType?.ToDisplayString() ?? "";

            // math.min/max/clamp/abs/floor/distancesq/dot — 结果分类 = parameters 的 max
            if (containingType == "EntJoy.Mathematics.math"
                || containingType == "System.MathF")
            {
                VarKind maxKind = VarKind.Uniform;
                foreach (var arg in invocation.ArgumentList.Arguments)
                {
                    var argKind = ClassifyExpressionInternal(arg.Expression, visiting);
                    if (argKind > maxKind) maxKind = argKind;
                }
                return maxKind;
            }

            // 其他函数调用 → 保守返回 uniform
            return VarKind.Uniform;
        }

        /// <summary>从表达式左侧提取变量名</summary>
        private static string? GetLHSVariableName(ExpressionSyntax expr)
        {
            switch (expr)
            {
                case IdentifierNameSyntax id:
                    return id.Identifier.Text;
                case MemberAccessExpressionSyntax member:
                    // float2.x = y — 取左侧对象名
                    if (member.Expression is IdentifierNameSyntax obj)
                        return obj.Identifier.Text;
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>从表达式提取变量名（如果是简单标识符）</summary>
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
