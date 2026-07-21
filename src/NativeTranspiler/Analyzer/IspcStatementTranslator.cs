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

        /// <summary>
        /// 在 IJobEntity ISPC 生成中，Execute 方法的 ref/in 参数不应复制为本地 struct，
        /// 而应直接通过指针+索引访问（例如 position_ptr[__entity_index]）。
        /// 此集合记录需要这种直接访问的参数名。
        /// </summary>
        protected readonly HashSet<string> _entityRefParamNames = new();

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
            AppendIndent();
            var type = _semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
            var cppType = NativeTranspiler.MapCSharpTypeToCpp(type!);
            var ispcType = ToIspcType(cppType);
            for (int i = 0; i < localDecl.Declaration.Variables.Count; i++)
            {
                var variable = localDecl.Declaration.Variables[i];
                if (i > 0) _builder.Append(", ");
                if (_useUniformVars)
                    _builder.Append("uniform ");
                _builder.Append(ispcType).Append(' ').Append(variable.Identifier.Text);
                if (variable.Initializer != null)
                {
                    _builder.Append(" = ");
                    TranslateExpression(variable.Initializer.Value);
                }
            }
            _builder.AppendLine(";");
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
    }
}
