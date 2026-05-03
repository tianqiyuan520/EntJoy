using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    public class CppStatementTranslator
    {
        protected readonly SemanticModel _semanticModel;
        protected readonly StringBuilder _builder = new();
        protected int _indentLevel = 0;

        public CppStatementTranslator(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel;
        }

        public string Translate(BlockSyntax? block)
        {
            if (block == null) return "";
            TranslateBlock(block, skipOuterBraces: true);
            return _builder.ToString();
        }

        protected void AppendIndent() => _builder.Append(new string(' ', _indentLevel * 4));

        protected virtual void TranslateStatement(StatementSyntax statement)
        {
            switch (statement)
            {
                case BlockSyntax block:
                    TranslateBlock(block, skipOuterBraces: false);
                    break;
                case LocalDeclarationStatementSyntax localDecl:
                    TranslateLocalDeclaration(localDecl);
                    break;
                case ForStatementSyntax forStmt:
                    TranslateForStatement(forStmt);
                    break;
                case ExpressionStatementSyntax exprStmt:
                    TranslateExpressionStatement(exprStmt);
                    break;
                case ReturnStatementSyntax returnStmt:
                    TranslateReturnStatement(returnStmt);
                    break;
                case EmptyStatementSyntax empty:
                    AppendIndent();
                    _builder.AppendLine(";");
                    break;
                case IfStatementSyntax ifStmt:
                    TranslateIfStatement(ifStmt);
                    break;
                case WhileStatementSyntax whileStmt:
                    TranslateWhileStatement(whileStmt);
                    break;
                case BreakStatementSyntax breakStmt:
                    AppendIndent();
                    _builder.AppendLine("break;");
                    break;
                case ContinueStatementSyntax continueStmt:
                    AppendIndent();
                    _builder.AppendLine("continue;");
                    break;
                default:
                    AppendIndent();
                    _builder.AppendLine($"// Unsupported statement: {statement.Kind()}");
                    break;
            }
        }

        protected void TranslateBlock(BlockSyntax block, bool skipOuterBraces)
        {
            if (!skipOuterBraces)
            {
                AppendIndent();
                _builder.AppendLine("{");
                _indentLevel++;
            }

            foreach (var stmt in block.Statements)
                TranslateStatement(stmt);

            if (!skipOuterBraces)
            {
                _indentLevel--;
                AppendIndent();
                _builder.AppendLine("}");
            }
        }

        protected virtual void TranslateLocalDeclaration(LocalDeclarationStatementSyntax localDecl)
        {
            AppendIndent();
            var type = _semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
            var cppType = NativeTranspiler.MapCSharpTypeToCpp(type!);
            for (int i = 0; i < localDecl.Declaration.Variables.Count; i++)
            {
                var variable = localDecl.Declaration.Variables[i];
                if (i > 0) _builder.Append(", ");
                _builder.Append(cppType);
                _builder.Append(' ');
                _builder.Append(variable.Identifier.Text);
                if (variable.Initializer != null)
                {
                    _builder.Append(" = ");
                    if (variable.Initializer.Value is ObjectCreationExpressionSyntax objectCreation)
                    {
                        _builder.Append(cppType).Append('(');
                        var args = objectCreation.ArgumentList?.Arguments ?? new SeparatedSyntaxList<ArgumentSyntax>();
                        for (int j = 0; j < args.Count; j++)
                        {
                            if (j > 0) _builder.Append(", ");
                            TranslateExpression(args[j].Expression);
                        }
                        _builder.Append(')');
                    }
                    else
                    {
                        TranslateExpression(variable.Initializer.Value);
                    }
                }
            }
            _builder.AppendLine(";");
        }

        protected virtual void TranslateForStatement(ForStatementSyntax forStmt)
        {
            AppendIndent();
            _builder.Append("for (");
            if (forStmt.Declaration != null)
            {
                var type = _semanticModel.GetTypeInfo(forStmt.Declaration.Type).Type;
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(type!);
                for (int i = 0; i < forStmt.Declaration.Variables.Count; i++)
                {
                    var v = forStmt.Declaration.Variables[i];
                    if (i > 0) _builder.Append(", ");
                    _builder.Append($"{cppType} {v.Identifier.Text}");
                    if (v.Initializer != null)
                    {
                        _builder.Append(" = ");
                        TranslateExpression(v.Initializer.Value);
                    }
                }
            }
            else if (forStmt.Initializers.Count > 0)
            {
                for (int i = 0; i < forStmt.Initializers.Count; i++)
                {
                    if (i > 0) _builder.Append(", ");
                    TranslateExpression(forStmt.Initializers[i]);
                }
            }
            _builder.Append("; ");

            if (forStmt.Condition != null)
                TranslateExpression(forStmt.Condition);
            _builder.Append("; ");

            if (forStmt.Incrementors.Count > 0)
            {
                for (int i = 0; i < forStmt.Incrementors.Count; i++)
                {
                    if (i > 0) _builder.Append(", ");
                    TranslateExpression(forStmt.Incrementors[i]);
                }
            }
            _builder.AppendLine(")");

            if (forStmt.Statement is BlockSyntax block)
                TranslateBlock(block, skipOuterBraces: false);
            else if (forStmt.Statement is EmptyStatementSyntax)
            {
                _indentLevel++;
                AppendIndent();
                _builder.AppendLine(";");
                _indentLevel--;
            }
            else
            {
                _indentLevel++;
                AppendIndent();
                TranslateStatement(forStmt.Statement);
                _indentLevel--;
            }
        }

        protected virtual void TranslateExpressionStatement(ExpressionStatementSyntax exprStmt)
        {
            AppendIndent();
            TranslateExpression(exprStmt.Expression);
            _builder.AppendLine(";");
        }

        protected virtual void TranslateReturnStatement(ReturnStatementSyntax returnStmt)
        {
            AppendIndent();
            _builder.Append("return");
            if (returnStmt.Expression != null)
            {
                _builder.Append(' ');
                TranslateExpression(returnStmt.Expression);
            }
            _builder.AppendLine(";");
        }

        protected virtual void TranslateIfStatement(IfStatementSyntax ifStmt)
        {
            AppendIndent();
            _builder.Append("if (");
            TranslateExpression(ifStmt.Condition);
            _builder.AppendLine(")");

            if (ifStmt.Statement is BlockSyntax block)
                TranslateBlock(block, skipOuterBraces: false);
            else
            {
                _indentLevel++;
                AppendIndent();
                TranslateStatement(ifStmt.Statement);
                _indentLevel--;
            }

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
        }

        protected virtual void TranslateWhileStatement(WhileStatementSyntax whileStmt)
        {
            AppendIndent();
            _builder.Append("while (");
            TranslateExpression(whileStmt.Condition);
            _builder.AppendLine(")");

            if (whileStmt.Statement is BlockSyntax block)
                TranslateBlock(block, skipOuterBraces: false);
            else
            {
                _indentLevel++;
                AppendIndent();
                TranslateStatement(whileStmt.Statement);
                _indentLevel--;
            }
        }

        protected virtual void TranslateExpression(ExpressionSyntax expr)
        {
            switch (expr)
            {
                case LiteralExpressionSyntax literal:
                    var token = literal.Token;
                    if (token.Kind() == SyntaxKind.NumericLiteralToken)
                    {
                        var text = token.Text;
                        if (text.EndsWith("f", StringComparison.OrdinalIgnoreCase))
                        {
                            string numberPart = text.Substring(0, text.Length - 1);
                            if (!numberPart.Contains('.') && !numberPart.Contains('e') && !numberPart.Contains('E'))
                                _builder.Append(numberPart).Append(".0f");
                            else
                                _builder.Append(text);
                        }
                        else if (text.EndsWith("d", StringComparison.OrdinalIgnoreCase) || text.EndsWith("m", StringComparison.OrdinalIgnoreCase))
                        {
                            _builder.Append(text.Substring(0, text.Length - 1));
                        }
                        else
                        {
                            _builder.Append(text);
                        }
                    }
                    else
                    {
                        _builder.Append(token.Text);
                    }
                    break;

                case IdentifierNameSyntax identifier:
                    TranslateIdentifier(identifier);
                    break;
                case BinaryExpressionSyntax binary:
                    TranslateBinaryExpression(binary);
                    break;
                case AssignmentExpressionSyntax assignment:
                    TranslateAssignment(assignment);
                    break;
                case PostfixUnaryExpressionSyntax postfix:
                    TranslateExpression(postfix.Operand);
                    _builder.Append(postfix.OperatorToken.Text);
                    break;
                case PrefixUnaryExpressionSyntax prefix:
                    _builder.Append(prefix.OperatorToken.Text);
                    TranslateExpression(prefix.Operand);
                    break;
                case MemberAccessExpressionSyntax memberAccess:
                    TranslateMemberAccess(memberAccess);
                    break;
                case InvocationExpressionSyntax invocation:
                    TranslateInvocation(invocation);
                    break;
                case ElementAccessExpressionSyntax elementAccess:
                    TranslateElementAccess(elementAccess);
                    break;
                case ParenthesizedExpressionSyntax paren:
                    _builder.Append('(');
                    TranslateExpression(paren.Expression);
                    _builder.Append(')');
                    break;
                case CastExpressionSyntax cast:
                    TranslateCastExpression(cast);
                    break;
                case ObjectCreationExpressionSyntax objectCreation:
                    TranslateObjectCreation(objectCreation);
                    break;
                case ArrayCreationExpressionSyntax arrayCreation:
                    TranslateArrayCreation(arrayCreation);
                    break;
                case ConditionalExpressionSyntax conditional:
                    TranslateConditional(conditional);
                    break;
                case CheckedExpressionSyntax checkedExpr:
                    TranslateExpression(checkedExpr.Expression);
                    break;
                default:
                    _builder.Append($"/* Unsupported expression: {expr.Kind()} */");
                    break;
            }
        }

        protected virtual void TranslateIdentifier(IdentifierNameSyntax identifier)
        {
            if (TryInlineConstant(identifier))
                return;
            _builder.Append(identifier.Identifier.Text);
        }

        protected virtual void TranslateAssignment(AssignmentExpressionSyntax assignment)
        {
            TranslateExpression(assignment.Left);
            _builder.Append(' ').Append(assignment.OperatorToken.Text).Append(' ');
            TranslateExpression(assignment.Right);
        }

        protected virtual void TranslateBinaryExpression(BinaryExpressionSyntax binary)
        {
            TranslateExpression(binary.Left);
            _builder.Append(' ').Append(binary.OperatorToken.Text).Append(' ');
            TranslateExpression(binary.Right);
        }

        protected virtual void TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            var exprType = _semanticModel.GetTypeInfo(memberAccess.Expression).Type;
            string memberName = memberAccess.Name.Identifier.Text;
            var typeName = exprType?.ToDisplayString();

            if (exprType != null && (exprType.Name == "float2" || exprType.Name == "int2" || exprType.Name == "uint2"))
            {
                if (memberName == "zero")
                {
                    _builder.Append(NativeTranspiler.MapCSharpTypeToCpp(exprType)).Append("(0)");
                    return;
                }
            }

            if (memberName == "MaxValue" || memberName == "MinValue")
            {
                if (typeName == "float" || typeName == "System.Single")
                {
                    _builder.Append(memberName == "MaxValue" ? "std::numeric_limits<float>::max()" : "std::numeric_limits<float>::lowest()");
                    return;
                }
                if (typeName == "double" || typeName == "System.Double")
                {
                    _builder.Append(memberName == "MaxValue" ? "std::numeric_limits<double>::max()" : "std::numeric_limits<double>::lowest()");
                    return;
                }
                if (typeName == "int" || typeName == "System.Int32")
                {
                    _builder.Append(memberName == "MaxValue" ? "std::numeric_limits<int>::max()" : "std::numeric_limits<int>::min()");
                    return;
                }
            }

            if (exprType?.TypeKind == TypeKind.Enum)
            {
                var symbol = _semanticModel.GetSymbolInfo(memberAccess.Name).Symbol;
                if (symbol is IFieldSymbol field && field.HasConstantValue)
                {
                    AppendConstant(field.ConstantValue);
                    return;
                }
            }

            bool isNativeContainer = NativeTranspiler.IsEntJoyNativeContainerType(exprType) ||
                                     (exprType != null && (exprType.Name == "NativeList" || exprType.Name == "NativeArray"));

            if (isNativeContainer)
            {
                if (memberName == "Length")
                {
                    TranslateExpression(memberAccess.Expression);
                    _builder.Append(".length()");
                    return;
                }
                if (memberName == "Capacity")
                {
                    TranslateExpression(memberAccess.Expression);
                    _builder.Append(".capacity()");
                    return;
                }
            }

            TranslateExpression(memberAccess.Expression);
            _builder.Append('.').Append(memberName);
        }

        protected virtual void TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                var containingType = methodSymbol.ContainingType;

                if (methodSymbol.Name == "Resize" && containingType?.Name == "NativeList")
                {
                    if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        TranslateExpression(memberAccess.Expression);
                        _builder.Append(".Resize(");
                        TranslateExpression(invocation.ArgumentList.Arguments[0].Expression);
                        if (invocation.ArgumentList.Arguments.Count >= 2)
                        {
                            _builder.Append(", ");
                            TranslateExpression(invocation.ArgumentList.Arguments[1].Expression);
                        }
                        _builder.Append(')');
                        return;
                    }
                }

                if (containingType != null && methodSymbol.IsStatic)
                {
                    var fullTypeName = containingType.ToDisplayString();
                    if (fullTypeName == "System.Math" || fullTypeName == "System.MathF")
                    {
                        TranslateMathFunctionCall(methodSymbol, invocation);
                        return;
                    }
                    if (fullTypeName == "System.Threading.Interlocked")
                    {
                        TranslateInterlockedCall(methodSymbol, invocation);
                        return;
                    }
                    if (fullTypeName == "EntJoy.Mathematics.math")
                    {
                        TranslateEntJoyMathCall(methodSymbol, invocation);
                        return;
                    }

                    var compilation = _semanticModel.Compilation;
                    if (SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingAssembly, compilation.Assembly))
                    {
                        string cppFunctionName = CppGenerator.GetCppFunctionName(methodSymbol);
                        _builder.Append(cppFunctionName);
                        _builder.Append('(');

                        var args = invocation.ArgumentList.Arguments;
                        for (int i = 0; i < args.Count; i++)
                        {
                            if (i > 0) _builder.Append(", ");
                            if (i < methodSymbol.Parameters.Length)
                                TranslateArgumentForCppCall(args[i].Expression, methodSymbol.Parameters[i]);
                            else
                                TranslateExpression(args[i].Expression);
                        }

                        _builder.Append(')');
                        return;
                    }
                }
            }

            TranslateExpression(invocation.Expression);
            _builder.Append('(');
            var argsList = invocation.ArgumentList.Arguments;
            for (int i = 0; i < argsList.Count; i++)
            {
                if (i > 0) _builder.Append(", ");
                TranslateExpression(argsList[i].Expression);
            }
            _builder.Append(')');
        }

        protected virtual void TranslateArgumentForCppCall(ExpressionSyntax argument, IParameterSymbol parameter)
        {
            if (parameter == null)
            {
                TranslateExpression(argument);
                return;
            }

            var paramType = parameter.Type;

            if (NativeTranspiler.IsEntJoyNativeContainerType(paramType))
            {
                if (paramType.Name == "NativeList")
                {
                    TranslateExpression(argument);
                    _builder.Append(".GetListData()");
                }
                else
                {
                    _builder.Append($"/* NativeArray argument not yet supported */");
                }
                return;
            }

            if (paramType is IPointerTypeSymbol)
            {
                TranslateExpression(argument);
                return;
            }

            var argType = _semanticModel.GetTypeInfo(argument).Type;
            if (argType is IPointerTypeSymbol)
            {
                TranslateExpression(argument);
            }
            else
            {
                _builder.Append('&');
                TranslateExpression(argument);
            }
        }

        protected virtual void TranslateElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
            var exprType = _semanticModel.GetTypeInfo(elementAccess.Expression).Type;

            var args = elementAccess.ArgumentList.Arguments;

            if (NativeTranspiler.IsEntJoyNativeContainerType(exprType))
            {
                TranslateExpression(elementAccess.Expression);
                _builder.Append('[');
                if (args.Count > 0)
                    TranslateExpression(args[0].Expression);
                _builder.Append(']');
                return;
            }

            TranslateExpression(elementAccess.Expression);
            _builder.Append('[');

            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) _builder.Append(", ");
                TranslateExpression(args[i].Expression);
            }
            _builder.Append(']');
        }

        protected virtual void TranslateCastExpression(CastExpressionSyntax cast)
        {
            var type = _semanticModel.GetTypeInfo(cast.Type).Type;
            var cppType = NativeTranspiler.MapCSharpTypeToCpp(type!);
            _builder.Append("((").Append(cppType).Append(')');
            TranslateExpression(cast.Expression);
            _builder.Append(')');
        }

        protected virtual void TranslateObjectCreation(ObjectCreationExpressionSyntax objectCreation)
        {
            var typeInfo = _semanticModel.GetTypeInfo(objectCreation.Type);
            var type = typeInfo.Type;
            string cppType;

            if (type != null)
            {
                cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
            }
            else
            {
                string typeName = objectCreation.Type.ToString();
                cppType = typeName switch
                {
                    "int2" => "EntJoy::Mathematics::int2",
                    "float2" => "EntJoy::Mathematics::float2",
                    "uint2" => "EntJoy::Mathematics::uint2",
                    _ => typeName
                };
            }

            _builder.Append(cppType).Append('(');
            var args = objectCreation.ArgumentList?.Arguments ?? new SeparatedSyntaxList<ArgumentSyntax>();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) _builder.Append(", ");
                TranslateExpression(args[i].Expression);
            }
            _builder.Append(')');
        }

        protected virtual void TranslateArrayCreation(ArrayCreationExpressionSyntax arrayCreation)
        {
            var type = _semanticModel.GetTypeInfo(arrayCreation.Type.ElementType).Type;
            var cppType = NativeTranspiler.MapCSharpTypeToCpp(type!);
            _builder.Append("new ").Append(cppType).Append("[] { ");
            if (arrayCreation.Initializer != null)
            {
                var exprs = arrayCreation.Initializer.Expressions;
                for (int i = 0; i < exprs.Count; i++)
                {
                    if (i > 0) _builder.Append(", ");
                    TranslateExpression(exprs[i]);
                }
            }
            _builder.Append(" }");
        }

        protected virtual void TranslateConditional(ConditionalExpressionSyntax conditional)
        {
            TranslateExpression(conditional.Condition);
            _builder.Append(" ? ");
            TranslateExpression(conditional.WhenTrue);
            _builder.Append(" : ");
            TranslateExpression(conditional.WhenFalse);
        }

        // ========== 以下方法改为 protected virtual，允许 ISPC 翻译器重写 ==========
        protected virtual void TranslateMathFunctionCall(IMethodSymbol method, InvocationExpressionSyntax invocation)
        {
            string cppFunc = method.Name switch
            {
                "Abs" => "std::abs",
                "Acos" => "std::acos",
                "Asin" => "std::asin",
                "Atan" => "std::atan",
                "Atan2" => "std::atan2",
                "Ceiling" => "std::ceil",
                "Cos" => "std::cos",
                "Cosh" => "std::cosh",
                "Exp" => "std::exp",
                "Floor" => "std::floor",
                "Log" => "std::log",
                "Log10" => "std::log10",
                "Max" => "std::max",
                "Min" => "std::min",
                "Pow" => "std::pow",
                "Round" => "std::round",
                "Sin" => "std::sin",
                "Sinh" => "std::sinh",
                "Sqrt" => "std::sqrt",
                "Tan" => "std::tan",
                "Tanh" => "std::tanh",
                "Truncate" => "std::trunc",
                _ => null
            };
            if (cppFunc == null)
            {
                _builder.Append($"/* Unsupported Math function: {method.Name} */");
                return;
            }
            _builder.Append(cppFunc).Append('(');
            var args = invocation.ArgumentList.Arguments;
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) _builder.Append(", ");
                TranslateExpression(args[i].Expression);
            }
            _builder.Append(')');
        }

        protected virtual void TranslateInterlockedCall(IMethodSymbol method, InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;
            if (args.Count == 0) return;

            var targetExpr = args[0].Expression;

            string macroName = method.Name switch
            {
                "Increment" => "INTERLOCKED_INCREMENT_AND_FETCH",
                "Decrement" => "INTERLOCKED_DECREMENT_AND_FETCH",
                "Add" => "INTERLOCKED_ADD_AND_FETCH",
                "Exchange" => "INTERLOCKED_EXCHANGE",
                "CompareExchange" => "INTERLOCKED_COMPARE_EXCHANGE",
                _ => null
            };

            if (macroName == null)
            {
                _builder.Append($"/* Unsupported Interlocked method: {method.Name} */");
                return;
            }

            _builder.Append(macroName).Append('(');
            _builder.Append('&');
            TranslateExpression(targetExpr);

            if (method.Name == "Add" && args.Count >= 2)
            {
                _builder.Append(", ");
                TranslateExpression(args[1].Expression);
            }
            else if (method.Name == "Exchange" && args.Count >= 2)
            {
                _builder.Append(", ");
                TranslateExpression(args[1].Expression);
            }
            else if (method.Name == "CompareExchange" && args.Count >= 3)
            {
                _builder.Append(", ");
                TranslateExpression(args[1].Expression);
                _builder.Append(", ");
                TranslateExpression(args[2].Expression);
            }
            _builder.Append(')');
        }

        protected virtual void TranslateEntJoyMathCall(IMethodSymbol method, InvocationExpressionSyntax invocation)
        {
            string cppFunc = method.Name switch
            {
                "dot" => "EntJoy::Mathematics::dot",
                "lengthsq" => "EntJoy::Mathematics::lengthsq",
                "length" => "EntJoy::Mathematics::length",
                "normalize" => "EntJoy::Mathematics::normalize",
                "abs" => "EntJoy::Mathematics::abs",
                "min" => "EntJoy::Mathematics::min",
                "max" => "EntJoy::Mathematics::max",
                "clamp" => "EntJoy::Mathematics::clamp",
                "lerp" => "EntJoy::Mathematics::lerp",
                "floor" => "EntJoy::Mathematics::floor",
                "ceil" => "EntJoy::Mathematics::ceil",
                "distancesq" => "EntJoy::Mathematics::distancesq",
                _ => null
            };

            if (cppFunc == null)
            {
                TranslateExpression(invocation.Expression);
                _builder.Append('(');
                var args = invocation.ArgumentList.Arguments;
                for (int i = 0; i < args.Count; i++)
                {
                    if (i > 0) _builder.Append(", ");
                    TranslateExpression(args[i].Expression);
                }
                _builder.Append(')');
                return;
            }

            _builder.Append(cppFunc).Append('(');
            var argsList = invocation.ArgumentList.Arguments;
            for (int i = 0; i < argsList.Count; i++)
            {
                if (i > 0) _builder.Append(", ");
                TranslateExpression(argsList[i].Expression);
            }
            _builder.Append(')');
        }

        protected bool TryInlineConstant(IdentifierNameSyntax identifier)
        {
            var symbol = _semanticModel.GetSymbolInfo(identifier).Symbol;
            if (symbol is IFieldSymbol field && field.HasConstantValue)
            {
                AppendConstant(field.ConstantValue);
                return true;
            }
            if (symbol is ILocalSymbol local && local.HasConstantValue)
            {
                AppendConstant(local.ConstantValue);
                return true;
            }
            return false;
        }

        protected void AppendConstant(object? value)
        {
            if (value is string str) _builder.Append($"\"{str}\"");
            else if (value is bool b) _builder.Append(b ? "true" : "false");
            else if (value is float f)
            {
                _builder.Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _builder.Append('f');
            }
            else if (value is double d) _builder.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else _builder.Append(value?.ToString() ?? "nullptr");
        }
    }
}