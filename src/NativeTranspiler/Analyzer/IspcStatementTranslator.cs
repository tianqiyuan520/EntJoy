using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer
{
    public class IspcStatementTranslator : CppPointerStatementTranslator
    {
        private readonly Dictionary<string, bool> _constBoolFields = new();
        private readonly bool _useUniformVars;

        protected readonly HashSet<string> _entityRefParamNames = new();

        /// <summary>是否在 foreach 体内部（内层循环变量需加 uniform 修饰）</summary>
        protected bool _insideForeach;

        /// <summary>设置 foreach 上下文标志（由外部生成器在发射 foreach 后调用）</summary>
        public void SetInsideForeach(bool value) => _insideForeach = value;

        /// <summary>设置 uniform-for 上下文标志（由外部生成器在发射 uniform for 后调用）</summary>
        public void SetInsideUniformFor(bool value) => _insideUniformFor = value;

        /// <summary>预扫描方法体，收集在循环中被赋值的局部变量（reduction 累加器）</summary>
        public void PreScanAccumulatorVars(MethodDeclarationSyntax methodSyntax)
        {
            if (methodSyntax?.Body == null) return;

            // 收集所有局部变量名
            var localVars = new HashSet<string>();
            foreach (var localDecl in methodSyntax.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                foreach (var v in localDecl.Declaration.Variables)
                    localVars.Add(v.Identifier.Text);

            // 找出在 for/while 循环体中被赋值（左值出现）的局部变量
            foreach (var loopNode in methodSyntax.Body.DescendantNodes())
            {
                if (loopNode is ForStatementSyntax || loopNode is WhileStatementSyntax)
                {
                    foreach (var assign in loopNode.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    {
                        if (assign.Left is IdentifierNameSyntax id && localVars.Contains(id.Identifier.Text))
                            _varyingAccumulatorVars.Add(id.Identifier.Text);
                    }
                }
            }
        }

        /// <summary>是否在 uniform for 体内部（内层 for 可转为 foreach 协作 SIMD）</summary>
        protected bool _insideUniformFor;

        /// <summary>在 uniform-for + foreach 模式下，被内层 foreach 写入的局部变量（累加器）</summary>
        protected readonly HashSet<string> _varyingAccumulatorVars = new();

        /// <summary>是否有累加器变量（供外部生成器决定外层循环策略）</summary>
        public bool HasAccumulatorVars() => _varyingAccumulatorVars.Count > 0;

        public IspcStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct,
            string? constBoolFieldName, bool constBoolValue, bool useUniformVars = false)
            : base(semanticModel, jobStruct)
        {
            if (constBoolFieldName != null)
                _constBoolFields[constBoolFieldName] = constBoolValue;
            _useUniformVars = useUniformVars;
        }

        public IspcStatementTranslator(SemanticModel semanticModel, IMethodSymbol method,
            string? constBoolFieldName, bool constBoolValue, bool useUniformVars = false)
            : base(semanticModel, method)
        {
            if (constBoolFieldName != null)
                _constBoolFields[constBoolFieldName] = constBoolValue;
            _useUniformVars = useUniformVars;
        }

        /// <summary>
        /// 支持多个 bool 条件字段的构造函数
        /// </summary>
        public IspcStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct,
            List<IFieldSymbol>? constBoolFields, List<bool>? constBoolValues, bool useUniformVars = false)
            : base(semanticModel, jobStruct)
        {
            if (constBoolFields != null && constBoolValues != null)
            {
                for (int i = 0; i < constBoolFields.Count; i++)
                    _constBoolFields[constBoolFields[i].Name] = constBoolValues[i];
            }
            _useUniformVars = useUniformVars;
        }

        public IspcStatementTranslator(SemanticModel semanticModel, IMethodSymbol method,
            List<IFieldSymbol>? constBoolFields, List<bool>? constBoolValues, bool useUniformVars = false)
            : base(semanticModel, method)
        {
            if (constBoolFields != null && constBoolValues != null)
            {
                for (int i = 0; i < constBoolFields.Count; i++)
                    _constBoolFields[constBoolFields[i].Name] = constBoolValues[i];
            }
            _useUniformVars = useUniformVars;
        }

        private static bool IsVectorType(ITypeSymbol? type)
        {
            if (type == null) return false;
            string name = type.ToDisplayString();
            return name == "EntJoy.Mathematics.float2" ||
                   name == "EntJoy.Mathematics.int2" ||
                   name == "EntJoy.Mathematics.uint2";
        }

        private static string ToIspcType(string cppType) => cppType switch
        {
            "EntJoy::Mathematics::float2" => "float2",
            "EntJoy::Mathematics::int2" => "int2",
            "EntJoy::Mathematics::uint2" => "uint2",
            "unsigned int" => "unsigned int",
            "float" => "float",
            "int" => "int",
            "bool" => "bool",
            _ when cppType.Contains("::") => cppType.Substring(cppType.LastIndexOf("::") + 2),
            _ => cppType
        };

        private string? GetNativeListElementCppType(ExpressionSyntax expr)
        {
            var typeInfo = _semanticModel.GetTypeInfo(expr);
            if (typeInfo.Type is INamedTypeSymbol named && named.Name == "NativeList" && named.TypeArguments.Length > 0)
                return NativeTranspiler.MapCSharpTypeToCpp(named.TypeArguments[0]);
            return null;
        }

        private void TranslateNativeListPointerPrefix(ExpressionSyntax expr)
        {
            if (expr is IdentifierNameSyntax id)
                _builder.Append(id.Identifier.Text).Append("->");
            else
            {
                TranslateExpression(expr);
                _builder.Append("->");
            }
        }

        protected override void TranslateIdentifier(IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.Text;
            if (_constBoolFields.TryGetValue(name, out bool constValue))
            {
                _builder.Append(constValue ? "true" : "false");
                return;
            }
            if (_nativeListNames.Contains(name))
            {
                _builder.Append(name);
                return;
            }
            // IJobEntity ref/in struct params: 直接通过指针+索引访问，消除 struct copy
            if (_entityRefParamNames.Contains(name))
            {
                _builder.Append(name).Append("_ptr[__entity_index]");
                return;
            }
            base.TranslateIdentifier(identifier);
        }

        /// <summary>
        /// 添加需要在 ISPC body 中通过 <c>name_ptr[__entity_index]</c> 直接访问的参数名。
        /// 用于 IJobEntity 的 execute 参数类型，消除 struct copy-in/copy-out 开销。
        /// </summary>
        public void AddEntityRefParam(string name) => _entityRefParamNames.Add(name);

        // Keep branch form in ISPC for better static-path stability on this workload.
        protected override bool EnableBranchlessSimpleIfRewrite() => false;

        protected override void TranslateIfStatement(IfStatementSyntax ifStmt)
        {
            var (innerCondition, hintKind) = ExtractHintFromCondition(ifStmt.Condition);

            if (hintKind != HintKind.None)
            {
                // ISPC does not support __builtin_expect or [[likely]].
                // Silently strip the Hint wrapper and emit a normal if-statement.
                AppendIndent();
                _builder.Append("if (");
                TranslateExpression(innerCondition);
                _builder.AppendLine(")");

                // Translate true-branch body
                if (ifStmt.Statement is BlockSyntax block)
                    TranslateBlock(block, skipOuterBraces: false);
                else
                {
                    _indentLevel++;
                    AppendIndent();
                    TranslateStatement(ifStmt.Statement);
                    _indentLevel--;
                }

                // Translate else-branch if present
                if (ifStmt.Else != null)
                {
                    AppendIndent();
                    _builder.AppendLine("else");
                    if (ifStmt.Else.Statement is BlockSyntax elseBlock)
                        TranslateBlock(elseBlock, skipOuterBraces: false);
                    else
                    {
                        _indentLevel++;
                        AppendIndent();
                        TranslateStatement(ifStmt.Else.Statement);
                        _indentLevel--;
                    }
                }
                return;
            }

            // No hint: use base implementation (which also handles branchless rewrite etc.)
            base.TranslateIfStatement(ifStmt);
        }

        protected override void TranslateLocalDeclaration(LocalDeclarationStatementSyntax localDecl)
        {
            // uniform-for + foreach 模式下：累加器变量需加 varying 前缀
            if (_insideUniformFor)
            {
                bool hasAccum = false;
                foreach (var variable in localDecl.Declaration.Variables)
                    if (_varyingAccumulatorVars.Contains(variable.Identifier.Text)) { hasAccum = true; break; }
                if (hasAccum)
                {
                    var accumType = _semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
                    var accumCppType = NativeTranspiler.MapCSharpTypeToCpp(accumType!);
                    var accumIspcType = ToIspcType(accumCppType);
                    foreach (var variable in localDecl.Declaration.Variables)
                    {
                        AppendIndent();
                        if (_varyingAccumulatorVars.Contains(variable.Identifier.Text))
                            _builder.Append("varying ");
                        _builder.Append(accumIspcType).Append(' ').Append(variable.Identifier.Text);
                        if (variable.Initializer != null)
                        {
                            _builder.Append(" = ");
                            TranslateExpression(variable.Initializer.Value);
                        }
                        _builder.AppendLine(";");
                    }
                    return;
                }
            }

            AppendIndent();
            var type = _semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
            var cppType = NativeTranspiler.MapCSharpTypeToCpp(type!);
            var ispcType = ToIspcType(cppType);
            // ISPC 不支持在逗号声明中引用同列表的兄弟变量（如 float dx=x, dy=y, d=dx*dx; 中 d 看不到 dx/dy）
            // 每个变量独立声明，以分号结束
            for (int i = 0; i < localDecl.Declaration.Variables.Count; i++)
            {
                var variable = localDecl.Declaration.Variables[i];
                if (i > 0) { _builder.AppendLine(); AppendIndent(); }
                if (_useUniformVars)
                    _builder.Append("uniform ");
                _builder.Append(ispcType).Append(' ').Append(variable.Identifier.Text);
                if (variable.Initializer != null)
                {
                    _builder.Append(" = ");
                    TranslateExpression(variable.Initializer.Value);
                }
                _builder.Append(';');
            }
            _builder.AppendLine();
        }

        protected override void TranslateObjectCreation(ObjectCreationExpressionSyntax objectCreation)
        {
            var typeInfo = _semanticModel.GetTypeInfo(objectCreation.Type);
            var type = typeInfo.Type;
            string cppType = type != null ? NativeTranspiler.MapCSharpTypeToCpp(type) : objectCreation.Type.ToString();
            string ispcType = ToIspcType(cppType);

            string maker = ispcType switch
            {
                "float2" => "make_float2",
                "int2" => "make_int2",
                "uint2" => "make_uint2",
                _ => null
            };

            if (maker != null)
            {
                // 使用 make_* 辅助函数（返回 varying struct）
                // 在 uniform 上下文中，赋值给 uniform LHS 时会出类型错误。
                // 但调用方（GenerateIspcFunction）已知此问题，会在生成后对赋值语句做后处理替换。
                _builder.Append(maker).Append('(');
                var args = objectCreation.ArgumentList?.Arguments ?? new SeparatedSyntaxList<ArgumentSyntax>();
                for (int i = 0; i < args.Count; i++)
                {
                    if (i > 0) _builder.Append(", ");
                    TranslateExpression(args[i].Expression);
                }
                _builder.Append(')');
            }
            else
            {
                base.TranslateObjectCreation(objectCreation);
            }
        }

        protected override void TranslateCastExpression(CastExpressionSyntax cast)
        {
            var targetType = _semanticModel.GetTypeInfo(cast.Type).Type;
            var sourceType = _semanticModel.GetTypeInfo(cast.Expression).Type;
            string targetCpp = NativeTranspiler.MapCSharpTypeToCpp(targetType!);
            string targetIspc = ToIspcType(targetCpp);

            if (targetIspc == "unsigned int" && sourceType?.SpecialType == SpecialType.System_Int32)
            {
                _builder.Append("(unsigned int)");
                TranslateExpression(cast.Expression);
                return;
            }

            if (targetIspc == "int2" && IsVectorType(sourceType) && sourceType?.Name == "float2")
            {
                _builder.Append("int2_from_float2(");
                TranslateExpression(cast.Expression);
                _builder.Append(')');
                return;
            }
            if (targetIspc == "float2" && IsVectorType(sourceType) && sourceType?.Name == "int2")
            {
                _builder.Append("float2_from_int2(");
                TranslateExpression(cast.Expression);
                _builder.Append(')');
                return;
            }

            _builder.Append('(').Append(targetIspc).Append(')');
            TranslateExpression(cast.Expression);
        }

        protected override void TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            var exprType = _semanticModel.GetTypeInfo(memberAccess.Expression).Type;
            string memberName = memberAccess.Name.Identifier.Text;
            bool isNativeList = exprType != null && NativeTranspiler.IsEntJoyNativeContainerType(exprType) && exprType.Name == "NativeList";

            if (isNativeList)
            {
                if (memberName == "Length")
                {
                    TranslateNativeListPointerPrefix(memberAccess.Expression);
                    _builder.Append("_length");
                    return;
                }
                if (memberName == "Capacity")
                {
                    TranslateNativeListPointerPrefix(memberAccess.Expression);
                    _builder.Append("_capacity");
                    return;
                }
            }

            if (memberName == "MaxValue" || memberName == "MinValue")
            {
                var typeName = exprType?.ToDisplayString();
                if (typeName == "float" || typeName == "System.Single")
                {
                    _builder.Append(memberName == "MaxValue" ? "3.402823466e+38f" : "-3.402823466e+38f");
                    return;
                }
                if (typeName == "int" || typeName == "System.Int32")
                {
                    _builder.Append(memberName == "MaxValue" ? "2147483647" : "-2147483647 - 1");
                    return;
                }
            }
            if (memberName == "zero" && IsVectorType(exprType))
            {
                string ispcType = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(exprType!));
                string maker = ispcType switch
                {
                    "float2" => "make_float2",
                    "int2" => "make_int2",
                    "uint2" => "make_uint2",
                    _ => ispcType
                };
                _builder.Append(maker).Append("(0, 0)");
                return;
            }
            // ISPC float2/int2/uint2 使用成员字段（非方法），直接发射成员名
            if (IsVectorType(exprType) && (memberName == "x" || memberName == "y"))
            {
                TranslateExpression(memberAccess.Expression);
                _builder.Append('.').Append(memberName);
                return;
            }
            base.TranslateMemberAccess(memberAccess);
        }

        protected override void TranslateElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
            var exprType = _semanticModel.GetTypeInfo(elementAccess.Expression).Type;
            if (exprType != null && NativeTranspiler.IsEntJoyNativeContainerType(exprType) && exprType.Name == "NativeList")
            {
                var elemCppType = GetNativeListElementCppType(elementAccess.Expression);
                string ispcElem = elemCppType != null ? ToIspcType(elemCppType) : null;
                string cast = !string.IsNullOrEmpty(ispcElem) ? $"({ispcElem}*)" : "";

                _builder.Append("((").Append(cast);
                TranslateNativeListPointerPrefix(elementAccess.Expression);
                _builder.Append("_data)");
                _builder.Append('[');
                var args = elementAccess.ArgumentList.Arguments;
                if (args.Count > 0)
                    TranslateExpression(args[0].Expression);
                _builder.Append("])");
                return;
            }
            base.TranslateElementAccess(elementAccess);
        }

        protected override void TranslateAssignment(AssignmentExpressionSyntax assignment)
        {
            // ISPC structs don't support compound assignment operators (+=, -=, *=, /=)
            // on struct types. Convert to "left = left op right" so that ISPC emits a
            // single struct-level gather + scatter (optimal for AoS data layout):
            //   position_ptr[idx].Value += vel_ptr[idx].Value * Dt
            //     → position_ptr[idx].Value = position_ptr[idx].Value + vel_ptr[idx].Value * Dt
            // Per-field decomposition (vec.x += val.x; vec.y += val.y) would double
            // gather/scatter count and regress memory-bound (Light) workloads.
            string op = assignment.OperatorToken.Text;
            if (op == "+=" || op == "-=" || op == "*=" || op == "/=")
            {
                var leftType = _semanticModel.GetTypeInfo(assignment.Left).Type;
                if (IsVectorType(leftType))
                {
                    TranslateExpression(assignment.Left);
                    _builder.Append(" = ");
                    TranslateExpression(assignment.Left);
                    _builder.Append(' ').Append(op[0]).Append(' ');
                    TranslateExpression(assignment.Right);
                    return;
                }
            }
            base.TranslateAssignment(assignment);
        }

        protected override void TranslateBinaryExpression(BinaryExpressionSyntax binary)
        {
            if (binary.IsKind(SyntaxKind.SubtractExpression) &&
                binary.Right is LiteralExpressionSyntax lit && lit.Token.ValueText == "1")
            {
                var left = binary.Left;
                if (left is InvocationExpressionSyntax inv)
                {
                    var sym = _semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                    if (sym != null && sym.Name == "Add" &&
                        sym.ContainingType?.ToDisplayString() == "System.Threading.Interlocked")
                    {
                        TranslateInterlockedCall(sym, inv);
                        return;
                    }
                }
            }
            base.TranslateBinaryExpression(binary);
        }

        protected override void TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                string fullTypeName = methodSymbol.ContainingType?.ToDisplayString();

                if (fullTypeName == "EntJoy.Collections.UnsafeUtility" &&
                    methodSymbol.Name == "ArrayElementAsRef")
                {
                    var args = invocation.ArgumentList.Arguments;
                    if (args.Count >= 2)
                    {
                        _builder.Append("&((");
                        ITypeSymbol? elementType = null;
                        if (methodSymbol.TypeArguments.Length > 0)
                            elementType = methodSymbol.TypeArguments[0];
                        else if (methodSymbol.ReturnType is INamedTypeSymbol namedReturn && namedReturn.TypeArguments.Length > 0)
                            elementType = namedReturn.TypeArguments[0];
                        else
                            elementType = _semanticModel.Compilation.GetSpecialType(SpecialType.System_Int32);

                        string ispcElemType = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(elementType));
                        _builder.Append(ispcElemType).Append("*)");
                        TranslateExpression(args[0].Expression);
                        _builder.Append(")[");
                        TranslateExpression(args[1].Expression);
                        _builder.Append(']');
                        return;
                    }
                    base.TranslateInvocation(invocation);
                    return;
                }

                if (methodSymbol.ContainingType?.Name == "NativeList" &&
                    NativeTranspiler.IsEntJoyNativeContainerType(methodSymbol.ContainingType) &&
                    methodSymbol.Name == "Resize")
                {
                    var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
                    if (memberAccess != null)
                    {
                        var listExpr = memberAccess.Expression;
                        TranslateNativeListPointerPrefix(listExpr);
                        _builder.Append("ResizeFunc(&");
                        TranslateNativeListPointerPrefix(listExpr);
                        _builder.Append("_data, &");
                        TranslateNativeListPointerPrefix(listExpr);
                        _builder.Append("_length, &");
                        TranslateNativeListPointerPrefix(listExpr);
                        _builder.Append("_capacity, &");
                        TranslateNativeListPointerPrefix(listExpr);
                        _builder.Append("_allocator, ");
                        TranslateExpression(invocation.ArgumentList.Arguments[0].Expression);
                        _builder.Append(", ");
                        string clearFlag = "true";
                        if (invocation.ArgumentList.Arguments.Count >= 2)
                        {
                            var optArg = invocation.ArgumentList.Arguments[1];
                            var constVal = _semanticModel.GetConstantValue(optArg.Expression);
                            if (constVal.HasValue && constVal.Value is int val && val == 1)
                                clearFlag = "false";
                        }
                        _builder.Append(clearFlag).Append(')');
                        return;
                    }
                }

                // 处理 NativeArray.GetUnsafePtr() — 翻译为 fieldName_ptr
                if (methodSymbol.ContainingType?.Name == "NativeArray" &&
                    NativeTranspiler.IsEntJoyNativeContainerType(methodSymbol.ContainingType) &&
                    methodSymbol.Name == "GetUnsafePtr")
                {
                    var targetExpr = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
                    if (targetExpr is IdentifierNameSyntax id)
                        _builder.Append(id.Identifier.Text + "_ptr");
                    else
                        base.TranslateInvocation(invocation);
                    return;
                }

                if (fullTypeName == "EntJoy.Mathematics.math")
                {
                    TranslateEntJoyMathCall(methodSymbol, invocation);
                    return;
                }
                if (fullTypeName == "System.Math" || fullTypeName == "System.MathF")
                {
                    TranslateSystemMathCall(methodSymbol, invocation);
                    return;
                }
                if (fullTypeName == "System.Threading.Interlocked")
                {
                    TranslateInterlockedCall(methodSymbol, invocation);
                    return;
                }
                if (fullTypeName == "EntJoy.Hint")
                {
                    // ISPC does not support __builtin_expect or [[likely]].
                    // Silently strip the Hint wrapper and translate the inner condition.
                    if (invocation.ArgumentList.Arguments.Count > 0)
                        TranslateExpression(invocation.ArgumentList.Arguments[0].Expression);
                    return;
                }
            }
            base.TranslateInvocation(invocation);
        }

        private void TranslateEntJoyMathCall(IMethodSymbol method, InvocationExpressionSyntax invocation)
        {
            string ispcFunc = method.Name;
            _builder.Append(ispcFunc).Append('(');
            var args = invocation.ArgumentList.Arguments;
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) _builder.Append(", ");
                TranslateExpression(args[i].Expression);
            }
            _builder.Append(')');
        }

        private void TranslateSystemMathCall(IMethodSymbol method, InvocationExpressionSyntax invocation)
        {
            string ispcFunc = method.Name switch
            {
                "Sin" => "sin",
                "Cos" => "cos",
                "Sqrt" => "sqrt",
                "Exp" => "exp",
                "Log" => "log",
                "Abs" => "abs",
                "Floor" => "floor",
                "Ceiling" => "ceil",
                _ => method.Name.ToLower()
            };
            _builder.Append(ispcFunc).Append('(');
            var args = invocation.ArgumentList.Arguments;
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) _builder.Append(", ");
                TranslateExpression(args[i].Expression);
            }
            _builder.Append(')');
        }

        protected override void AppendConstant(object? value)
        {
            if (value is float f)
            {
                // ISPC accepts "1920f" format (no decimal point needed, unlike C++ which needs "1920.0f")
                string floatStr = f.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _builder.Append(floatStr);
                _builder.Append('f');
                return;
            }
            base.AppendConstant(value);
        }

        protected virtual void TranslateInterlockedCall(IMethodSymbol method, InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;
            if (args.Count == 0) return;

            string ispcFunc = method.Name switch
            {
                "Increment" => "atomic_add_global",
                "Decrement" => "atomic_subtract_global",
                "Add" => "atomic_add_global",
                _ => null
            };

            if (ispcFunc == null)
            {
                base.TranslateInvocation(invocation);
                return;
            }

            _builder.Append(ispcFunc).Append('(');

            var targetExpr = args[0].Expression;
            if (targetExpr is RefExpressionSyntax refExpr)
                targetExpr = refExpr.Expression;

            if (targetExpr is InvocationExpressionSyntax innerInvoke)
            {
                var innerSymbol = _semanticModel.GetSymbolInfo(innerInvoke).Symbol as IMethodSymbol;
                if (innerSymbol != null &&
                    innerSymbol.ContainingType?.ToDisplayString() == "EntJoy.Collections.UnsafeUtility" &&
                    innerSymbol.Name == "ArrayElementAsRef")
                {
                    var innerArgs = innerInvoke.ArgumentList.Arguments;
                    _builder.Append("&((");
                    ITypeSymbol? elemType = null;
                    if (innerSymbol.TypeArguments.Length > 0)
                        elemType = innerSymbol.TypeArguments[0];
                    else if (innerSymbol.ReturnType is INamedTypeSymbol namedRet && namedRet.TypeArguments.Length > 0)
                        elemType = namedRet.TypeArguments[0];
                    else
                        elemType = _semanticModel.Compilation.GetSpecialType(SpecialType.System_Int32);
                    string ispcElem = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                    _builder.Append(ispcElem).Append("*)");
                    TranslateExpression(innerArgs[0].Expression);
                    _builder.Append(")[");
                    TranslateExpression(innerArgs[1].Expression);
                    _builder.Append(']');
                }
                else
                {
                    TranslateExpression(targetExpr);
                }
            }
            else if (targetExpr is PrefixUnaryExpressionSyntax prefix
                     && prefix.OperatorToken.IsKind(SyntaxKind.AsteriskToken)
                     && prefix.Operand is IdentifierNameSyntax id)
            {
                if (_valueParameterNames.Contains(id.Identifier.Text))
                {
                    _builder.Append(id.Identifier.Text + "_ptr");
                }
                else if (_pointerParameterNames.Contains(id.Identifier.Text))
                {
                    _builder.Append(id.Identifier.Text + "_ptr");
                }
                else
                {
                    _builder.Append("&");
                    TranslateExpression(targetExpr);
                }
            }
            else
            {
                _builder.Append("&");
                TranslateExpression(targetExpr);
            }

            if (method.Name == "Add" && args.Count >= 2)
            {
                _builder.Append(", ");
                TranslateExpression(args[1].Expression);
            }
            else
            {
                _builder.Append(", 1");
            }
            _builder.Append(')');
        }

        // ============================================================
        // 嵌套循环优化：检测外层 for + 内层 for/while 顺序访问模式，
        // 将外层转为 uniform for、内层转为 foreach、累加器加 varying、
        // 输出点加 reduce_min() 跨 lane 归约。
        // ============================================================

        protected override void TranslateForStatement(ForStatementSyntax forStmt)
        {
            // 标准 for (int i = 0; i < limit; i++) 模式检测
            if (!_insideForeach && !_insideUniformFor &&
                forStmt.Declaration != null &&
                forStmt.Declaration.Variables.Count == 1 &&
                forStmt.Condition is BinaryExpressionSyntax binExpr &&
                binExpr.OperatorToken.IsKind(SyntaxKind.LessThanToken) &&
                forStmt.Incrementors.Count == 1 &&
                forStmt.Incrementors[0] is PostfixUnaryExpressionSyntax postfix &&
                postfix.OperatorToken.IsKind(SyntaxKind.PlusPlusToken))
            {
                var varDecl = forStmt.Declaration.Variables[0];
                string indexName = varDecl.Identifier.Text;

                if (postfix.Operand is IdentifierNameSyntax incId &&
                    incId.Identifier.Text == indexName &&
                    varDecl.Initializer?.Value is LiteralExpressionSyntax initLit &&
                    initLit.Token.ValueText == "0")
                {
                    if (binExpr.Left is IdentifierNameSyntax condId &&
                        condId.Identifier.Text == indexName)
                    {
                        bool hasNestedLoops = HasNestedLoop(forStmt);

                        if (!hasNestedLoops)
                        {
                            // 无嵌套 → foreach（最佳 SIMD 加速）
                            AppendIndent();
                            _builder.Append("foreach (");
                            _builder.Append(indexName);
                            _builder.Append(" = 0 ... ");
                            TranslateExpression(binExpr.Right);
                            _builder.AppendLine(")");
                            var saved = _insideForeach;
                            _insideForeach = true;
                            if (forStmt.Statement is BlockSyntax block)
                                TranslateBlock(block, skipOuterBraces: false);
                            else
                            {
                                _indentLevel++; AppendIndent();
                                TranslateStatement(forStmt.Statement); _indentLevel--;
                            }
                            _insideForeach = saved;
                            return;
                        }
                        else
                        {
                            // 有嵌套循环 → uniform for + 内层 foreach
                            // 外层 index 为 uniform → arr[i*K+j] 无 gather
                            var accumVars = CollectAccumulatorVars(forStmt);

                            AppendIndent();
                            _builder.Append("for (uniform int ");
                            _builder.Append(indexName);
                            _builder.Append(" = 0; ");
                            _builder.Append(indexName);
                            _builder.Append(" < ");
                            TranslateExpression(binExpr.Right);
                            _builder.Append("; ++");
                            _builder.Append(indexName);
                            _builder.AppendLine(")");

                            var savedF = _insideForeach;
                            var savedU = _insideUniformFor;
                            var savedAccum = new HashSet<string>(_varyingAccumulatorVars);

                            _insideForeach = false;
                            _insideUniformFor = true;
                            foreach (var v in accumVars) _varyingAccumulatorVars.Add(v);

                            if (forStmt.Statement is BlockSyntax block)
                                TranslateBlock(block, skipOuterBraces: false);
                            else
                            {
                                _indentLevel++; AppendIndent();
                                TranslateStatement(forStmt.Statement); _indentLevel--;
                            }

                            _insideForeach = savedF;
                            _insideUniformFor = savedU;
                            _varyingAccumulatorVars.Clear();
                            foreach (var v in savedAccum) _varyingAccumulatorVars.Add(v);
                            return;
                        }
                    }
                }
            }

            if (_insideForeach)
            {
                // foreach 内部：内层循环变量加 uniform 修饰，防止 ISPC 默认视为 varying。
                // EmitUniformFor 内部已检查边界是否为 uniform-safe，非 uniform 边界自动回退到 int。
                EmitUniformFor(forStmt);
            }
            else if (_insideUniformFor)
            {
                // uniform for 内部：将 for 转为 foreach 获得协作 SIMD
                TryEmitForeachOrFallback(forStmt);
            }
            else
            {
                base.TranslateForStatement(forStmt);
            }
        }

        private static bool HasNestedLoop(ForStatementSyntax forStmt)
        {
            var body = forStmt.Statement;
            if (body == null) return false;
            bool foundSelf = false;
            foreach (var node in body.DescendantNodesAndSelf())
            {
                if (!foundSelf && node == body) { foundSelf = true; continue; }
                if (node is ForStatementSyntax || node is WhileStatementSyntax)
                    return true;
            }
            return false;
        }

        private static HashSet<string> CollectAccumulatorVars(ForStatementSyntax outerFor)
        {
            var vars = new HashSet<string>();
            var body = outerFor.Statement is BlockSyntax blk ? blk : null;
            if (body == null) return vars;

            var localVars = new HashSet<string>();
            foreach (var localDecl in body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                foreach (var v in localDecl.Declaration.Variables)
                    localVars.Add(v.Identifier.Text);

            foreach (var nestedFor in body.DescendantNodes().OfType<ForStatementSyntax>())
            {
                if (nestedFor == outerFor) continue;
                CollectAssignmentsInNode(nestedFor, localVars, vars);
            }
            foreach (var nestedWhile in body.DescendantNodes().OfType<WhileStatementSyntax>())
                CollectAssignmentsInNode(nestedWhile, localVars, vars);

            return vars;
        }

        private static void CollectAssignmentsInNode(SyntaxNode node, HashSet<string> localVars, HashSet<string> results)
        {
            foreach (var assign in node.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                if (assign.Left is IdentifierNameSyntax id && localVars.Contains(id.Identifier.Text))
                    results.Add(id.Identifier.Text);
        }

        /// <summary>判断表达式是否为字面量或 -literal/+literal（如 -1 实际是 PrefixUnary(-)(1)）</summary>
        private static bool IsLiteralOrUniformMinus(ExpressionSyntax expr)
        {
            if (expr is LiteralExpressionSyntax) return true;
            if (expr is PrefixUnaryExpressionSyntax pre &&
                (pre.OperatorToken.IsKind(SyntaxKind.MinusToken) ||
                 pre.OperatorToken.IsKind(SyntaxKind.PlusToken)) &&
                pre.Operand is LiteralExpressionSyntax)
                return true;
            return false;
        }

        /// <summary>判断表达式在 foreach 上下文中是否保证为 uniform。
        /// 字面量 → 总是 uniform。
        /// 字段 → AppendUniformVariableDeclarations 在 foreach 前已复制为 uniform 局部变量。
        /// 标识符 → 可能是局部变量（可能 varying），不是字段名则不保证 uniform。
        /// 数组/列表访问 → 必定 varying（foreach 上下文中 gather）。
        /// </summary>
        private bool IsUniformExpr(ExpressionSyntax expr)
        {
            if (expr is LiteralExpressionSyntax) return true;
            if (expr is PrefixUnaryExpressionSyntax pre)
                return IsUniformExpr(pre.Operand);
            if (expr is BinaryExpressionSyntax bin)
                return IsUniformExpr(bin.Left) && IsUniformExpr(bin.Right);
            if (expr is ParenthesizedExpressionSyntax paren)
                return IsUniformExpr(paren.Expression);
            if (expr is IdentifierNameSyntax id)
            {
                // 字段引用 → 已复制为 uniform 局部变量
                var sym = _semanticModel.GetSymbolInfo(id).Symbol;
                return sym is IFieldSymbol;
            }
            // 数组访问、方法调用等 → varying
            return false;
        }

        private void EmitUniformFor(ForStatementSyntax forStmt)
        {
            // 检查初始化值和边界是否为 uniform-safe（字面量/字段）。
            // 如果边界表达式引用局部变量（可能 varying），不能强制 uniform int。
            bool safeInitializer = forStmt.Declaration?.Variables.Count > 0 &&
                forStmt.Declaration.Variables[0].Initializer?.Value is ExpressionSyntax init &&
                IsLiteralOrUniformMinus(init);
            bool safeCondition = forStmt.Condition is BinaryExpressionSyntax cond &&
                IsUniformExpr(cond.Right);
            if (!safeInitializer || !safeCondition)
            {
                base.TranslateForStatement(forStmt);
                return;
            }

            AppendIndent();
            _builder.Append("for (uniform ");
            if (forStmt.Declaration != null)
            {
                var type = _semanticModel.GetTypeInfo(forStmt.Declaration.Type).Type;
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(type!);
                _builder.Append($"{cppType} {forStmt.Declaration.Variables[0].Identifier.Text}");
                if (forStmt.Declaration.Variables[0].Initializer != null)
                {
                    _builder.Append(" = ");
                    TranslateExpression(forStmt.Declaration.Variables[0].Initializer.Value);
                }
            }
            else if (forStmt.Initializers.Count > 0)
            {
                TranslateExpression(forStmt.Initializers[0]);
            }
            _builder.Append("; ");
            if (forStmt.Condition != null) TranslateExpression(forStmt.Condition);
            _builder.Append("; ");
            if (forStmt.Incrementors.Count > 0) TranslateExpression(forStmt.Incrementors[0]);
            _builder.AppendLine(")");
            if (forStmt.Statement is BlockSyntax block)
                TranslateBlock(block, skipOuterBraces: false);
            else
            {
                _indentLevel++; AppendIndent();
                TranslateStatement(forStmt.Statement); _indentLevel--;
            }
        }

        private void TryEmitForeachOrFallback(ForStatementSyntax forStmt)
        {
            if (forStmt.Declaration != null &&
                forStmt.Declaration.Variables.Count == 1 &&
                forStmt.Condition is BinaryExpressionSyntax binExpr &&
                binExpr.OperatorToken.IsKind(SyntaxKind.LessThanToken) &&
                forStmt.Incrementors.Count == 1 &&
                forStmt.Incrementors[0] is PostfixUnaryExpressionSyntax postfix &&
                postfix.OperatorToken.IsKind(SyntaxKind.PlusPlusToken))
            {
                var varDecl = forStmt.Declaration.Variables[0];
                string indexName = varDecl.Identifier.Text;
                if (postfix.Operand is IdentifierNameSyntax incId &&
                    incId.Identifier.Text == indexName &&
                    varDecl.Initializer?.Value is LiteralExpressionSyntax initLit &&
                    initLit.Token.ValueText == "0" &&
                    binExpr.Left is IdentifierNameSyntax condId &&
                    condId.Identifier.Text == indexName)
                {
                    AppendIndent();
                    _builder.Append("foreach (");
                    _builder.Append(indexName);
                    _builder.Append(" = 0 ... ");
                    TranslateExpression(binExpr.Right);
                    _builder.AppendLine(")");
                    var saved = _insideForeach;
                    _insideForeach = true;
                    if (forStmt.Statement is BlockSyntax block)
                        TranslateBlock(block, skipOuterBraces: false);
                    else
                    {
                        _indentLevel++; AppendIndent();
                        TranslateStatement(forStmt.Statement); _indentLevel--;
                    }
                    _insideForeach = saved;
                    return;
                }
            }
            // 非标准 for（如 dx = -1; dx <= 1; dx++）→ 边界为 uniform-safe 用 EmitUniformFor
            EmitUniformFor(forStmt);
        }


        protected override void TranslateExpressionStatement(ExpressionStatementSyntax exprStmt)
        {
            if (_insideUniformFor && exprStmt.Expression is AssignmentExpressionSyntax assign &&
                assign.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                assign.Right is IdentifierNameSyntax rightId &&
                _varyingAccumulatorVars.Contains(rightId.Identifier.Text))
            {
                AppendIndent();
                TranslateExpression(assign.Left);
                _builder.Append(" = reduce_min(");
                TranslateExpression(assign.Right);
                _builder.AppendLine(");");
                return;
            }


            base.TranslateExpressionStatement(exprStmt);
        }
        protected override void TranslateWhileStatement(WhileStatementSyntax whileStmt)
        {
            if (_insideForeach && !_insideUniformFor)
            {
                AppendIndent();
                _builder.Append("while (");
                TranslateExpression(whileStmt.Condition);
                _builder.AppendLine(")");
                if (whileStmt.Statement is BlockSyntax block)
                    TranslateBlock(block, skipOuterBraces: false);
                else
                {
                    _indentLevel++; AppendIndent();
                    TranslateStatement(whileStmt.Statement); _indentLevel--;
                }
            }
            else
            {
                base.TranslateWhileStatement(whileStmt);
            }
        }
    }
}
