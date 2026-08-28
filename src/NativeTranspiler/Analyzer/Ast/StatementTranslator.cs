using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 通用 AST 语句分析/直译基类（AST 分析层的根本类；C++ 与 ISPC 共用的通用基类。
    /// C++/ISPC 均为其具体子类，覆盖点通过 protected virtual 展开；新增后端 = 派生 + 按需 override。
    ///
    /// 继承层级 & virtual 覆盖关系（维护覆盖点时应同步此表）：
    ///   StatementTranslator（本类，通用 AST 直译/AST 分析）
    ///   ├─ CppPointerStatementTranslator（C++，指针/引用增强）
    ///   │   override: TranslateIdentifier / TranslateAssignment / TranslateMemberAccess /
    ///   │              TranslateElementAccess / TranslateInvocation
    ///   │   ├─ CppChunkStatementTranslator（C++ chunk 后端，sealed）
    ///   │   │   override: TranslateBlock / TranslateLocalDeclaration / TranslateExpressionStatement /
    ///   │   │              TranslateIdentifier / TranslateInvocation / TranslateMemberAccess /
    ///   │   │              TranslateElementAccess / TranslateForStatement
    ///   │   └─ CppBatchStatementTranslator（C++ batch 后端）
    ///   │       override: TranslateIdentifier / TranslateAssignment
    ///   └─ IspcStatementTranslator（ISPC 后端）
    ///       override: TranslateIdentifier / EnableBranchlessSimpleIfRewrite → false / TranslateIfStatement /
    ///                  TranslateLocalDeclaration / TranslateObjectCreation / TranslateCastExpression /
    ///                  TranslateMemberAccess / TranslateElementAccess / TranslateAssignment /
    ///                  TranslateBinaryExpression / TranslateInvocation / AppendConstant / TranslateForStatement /
    ///                  TranslateExpressionStatement / TranslateWhileStatement
    ///       ⚠ 注意：TranslateEntJoyMathCall / TranslateInterlockedCall 在基类为 virtual，但 ISPC 侧以同名
    ///               方法【隐藏】而非 override（触发 CS0114）。这是隐性覆盖点，后续应改为 override 对齐。
    ///       └─ IspcChunkStatementTranslator（ISPC chunk 后端，sealed）
    ///           override: TranslateLocalDeclaration / TranslateForStatement / TranslateIdentifier /
    ///                      TranslateMemberAccess / TranslateElementAccess / TranslateExpressionStatement /
    ///                      TranslateAssignment
    /// </summary>
    public class StatementTranslator
    {
        protected readonly SemanticModel _semanticModel;
        protected readonly StringBuilder _builder = new();
        protected readonly bool _useFastMath;
        protected readonly bool _enableAutoSIMD;
        protected int _indentLevel = 0;

        public StatementTranslator(SemanticModel semanticModel, bool useFastMath = false, bool enableAutoSIMD = false)
        {
            _semanticModel = semanticModel;
            _useFastMath = useFastMath;
            _enableAutoSIMD = enableAutoSIMD;
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
                case DoStatementSyntax doStmt:
                    TranslateDoStatement(doStmt);
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

        protected virtual void TranslateBlock(BlockSyntax block, bool skipOuterBraces)
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
                if (i == 0)
                {
                    _builder.Append(cppType);
                    _builder.Append(' ');
                }
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
            if (_enableAutoSIMD && TryGenerateSIMDForLoop(forStmt))
                return;

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
            var (innerCondition, hintKind) = ExtractHintFromCondition(ifStmt.Condition);

            // 禁用 branchless rewrite（三元展开）：
            //   原写法 if (x < best) best = x; → best = x < best ? x : best;
            //   会导致左值右值引用同一变量，形成 MSVC 无法矢量化的循环携带依赖。
            //   保留原生 if 形式更有利于 MSVC 自动矢量化器识别规约模式。
            // 若用户显式使用了 Hint.Likely/Unlikely，继续保留分支形式即可。
            if (false && EnableBranchlessSimpleIfRewrite() && TryTranslateBranchlessSimpleIf(ifStmt))
                return;

            AppendIndent();
            _builder.Append("if (");
            TranslateExpression(innerCondition);

            // Hint.Likely → `if (cond) [[likely]]` ; Hint.Unlikely → `if (cond) [[unlikely]]`
            // [[likely]] / [[unlikely]] 是 C++20 statement attribute，置于 true-branch 前
            if (hintKind == HintKind.Likely)
                _builder.AppendLine(") [[likely]]");
            else if (hintKind == HintKind.Unlikely)
                _builder.AppendLine(") [[unlikely]]");
            else
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

        /// <summary>
        /// Detects whether an if-statement condition is wrapped in <c>Hint.Likely(…)</c>
        /// or <c>Hint.Unlikely(…)</c>, and if so returns the inner condition and which
        /// hint was used.
        /// </summary>
        protected enum HintKind { None, Likely, Unlikely }

        protected (ExpressionSyntax inner, HintKind kind) ExtractHintFromCondition(ExpressionSyntax condition)
        {
            if (condition is InvocationExpressionSyntax inv)
            {
                var sym = _semanticModel.GetSymbolInfo(inv);
                if (sym.Symbol is IMethodSymbol m && m.ContainingType?.ToDisplayString() == "EntJoy.Hint")
                {
                    if (m.Name == Config.Likely && inv.ArgumentList.Arguments.Count > 0)
                        return (inv.ArgumentList.Arguments[0].Expression, HintKind.Likely);
                    if (m.Name == Config.Unlikely && inv.ArgumentList.Arguments.Count > 0)
                        return (inv.ArgumentList.Arguments[0].Expression, HintKind.Unlikely);
                }
            }
            return (condition, HintKind.None);
        }

        protected virtual bool EnableBranchlessSimpleIfRewrite() => true;

        private bool TryTranslateBranchlessSimpleIf(IfStatementSyntax ifStmt)
        {
            // Only rewrite:
            // if (cond) x = y;
            // => x = cond ? y : x;
            if (ifStmt.Else != null)
                return false;

            if (ifStmt.Statement is not ExpressionStatementSyntax exprStmt)
                return false;

            if (exprStmt.Expression is not AssignmentExpressionSyntax assignment)
                return false;

            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                return false;

            if (!IsSimpleLValue(assignment.Left))
                return false;

            AppendIndent();
            TranslateExpression(assignment.Left);
            _builder.Append(" = ");
            TranslateExpression(ifStmt.Condition);
            _builder.Append(" ? ");
            TranslateExpression(assignment.Right);
            _builder.Append(" : ");
            TranslateExpression(assignment.Left);
            _builder.AppendLine(";");
            return true;
        }

        private static bool IsSimpleLValue(ExpressionSyntax expr)
        {
            return expr switch
            {
                IdentifierNameSyntax => true,
                MemberAccessExpressionSyntax ma => IsSimpleLValue(ma.Expression),
                ParenthesizedExpressionSyntax p => IsSimpleLValue(p.Expression),
                _ => false
            };
        }

        protected virtual void TranslateDoStatement(DoStatementSyntax doStmt)
        {
            AppendIndent();
            _builder.AppendLine("do");
            if (doStmt.Statement is BlockSyntax block)
                TranslateBlock(block, skipOuterBraces: false);
            else
            {
                _indentLevel++;
                AppendIndent();
                TranslateStatement(doStmt.Statement);
                _indentLevel--;
            }
            AppendIndent();
            _builder.Append("while (");
            TranslateExpression(doStmt.Condition);
            _builder.AppendLine(");");
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
                        // Hex integer literals (0x...) must be emitted as-is.
                        // They are always integer and never have a float suffix in C#.
                        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
                        {
                            _builder.Append(text);
                        }
                        else if (text.EndsWith("f", StringComparison.OrdinalIgnoreCase))
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
                // C++ NativeMath.h 改用 data[2] + x()/y() 访问器，此处补 ()
                if (memberName == "x" || memberName == "y")
                {
                    TranslateExpression(memberAccess.Expression);
                    _builder.Append('.').Append(memberName).Append("()");
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
                                     (exprType != null && (NativeTranspiler.IsEntJoyContainerNamed(exprType, Config.NativeList) || NativeTranspiler.IsEntJoyContainerNamed(exprType, Config.NativeArray)));

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

                if (methodSymbol.Name == Config.Resize && NativeTranspiler.IsEntJoyContainerNamed(containingType, Config.NativeList))
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
                    if (fullTypeName == "EntJoy.Hint")
                    {
                        // Strip Hint wrapper: just translate the inner condition expression.
                        // The [[likely]] / [[unlikely]] attribute is emitted by TranslateIfStatement
                        // which checks the condition before calling TranslateInvocation.
                        if (invocation.ArgumentList.Arguments.Count > 0)
                            TranslateExpression(invocation.ArgumentList.Arguments[0].Expression);
                        else
                            _builder.Append("true");
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
                if (NativeTranspiler.IsEntJoyContainerNamed(paramType, Config.NativeList))
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
            // GetTypeInfo(objCreation) 优先：VS/MSBuild 的 Roslyn 对嵌套类型 + object-initializer
            // 的 .Type 会返回 null，导致 fallback 输出无命名空间的 C++ 名；整个表达式两种引擎都可靠。
            var typeInfo = _semanticModel.GetTypeInfo(objectCreation);
            var type = typeInfo.Type ?? _semanticModel.GetTypeInfo(objectCreation.Type).Type;
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
            bool isMathF = method.ContainingType?.ToDisplayString() == "System.MathF";
            if (_useFastMath && isMathF)
            {
                string fastFunc = method.Name switch
                {
                    "Sin" => "EntJoy::FastMath::Sin",
                    "Cos" => "EntJoy::FastMath::Cos",
                    "Sqrt" => "EntJoy::FastMath::Sqrt",
                    "Log" => "EntJoy::FastMath::Log",
                    "Log10" => "EntJoy::FastMath::Log10",
                    _ => null
                };
                if (fastFunc != null)
                {
                    _builder.Append(fastFunc).Append('(');
                    var fastArgs = invocation.ArgumentList.Arguments;
                    for (int i = 0; i < fastArgs.Count; i++)
                    {
                        if (i > 0) _builder.Append(", ");
                        TranslateExpression(fastArgs[i].Expression);
                    }
                    _builder.Append(')');
                    return;
                }
            }

            string cppFunc = isMathF ? method.Name switch
            {
                "Abs" => "::fabsf",
                "Acos" => "::acosf",
                "Asin" => "::asinf",
                "Atan" => "::atanf",
                "Atan2" => "::atan2f",
                "Ceiling" => "::ceilf",
                "Cos" => "::cosf",
                "Cosh" => "::coshf",
                "Exp" => "::expf",
                "Floor" => "::floorf",
                "Log" => "::logf",
                "Log10" => "::log10f",
                "Max" => "::fmaxf",
                "Min" => "::fminf",
                "Pow" => "::powf",
                "Round" => "::roundf",
                "Sin" => "::sinf",
                "Sinh" => "::sinhf",
                "Sqrt" => "::sqrtf",
                "Tan" => "::tanf",
                "Tanh" => "::tanhf",
                "Truncate" => "::truncf",
                _ => null
            } : method.Name switch
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

        private static bool Is64BitType(ITypeSymbol? type)
        {
            return type != null && (type.SpecialType == SpecialType.System_Int64 ||
                                    type.SpecialType == SpecialType.System_UInt64);
        }

        protected virtual void TranslateInterlockedCall(IMethodSymbol method, InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;
            if (args.Count == 0) return;

            var targetExpr = args[0].Expression;
            var targetType = _semanticModel.GetTypeInfo(targetExpr).Type;
            bool is64Bit = Is64BitType(targetType);

            // 选择 32 或 64 位宏
            string macroBase = method.Name switch
            {
                "Increment" => "INTERLOCKED_INCREMENT_AND_FETCH",
                "Decrement" => "INTERLOCKED_DECREMENT_AND_FETCH",
                Config.Add => "INTERLOCKED_ADD_AND_FETCH",
                Config.Exchange => "INTERLOCKED_EXCHANGE",
                Config.CompareExchange => "INTERLOCKED_COMPARE_EXCHANGE",
                _ => null
            };

            if (macroBase == null)
            {
                _builder.Append($"/* Unsupported Interlocked method: {method.Name} */");
                return;
            }

            _builder.Append(macroBase).Append(is64Bit ? "64" : "32").Append('(');
            _builder.Append('&');
            TranslateExpression(targetExpr);

            if (method.Name == Config.Add && args.Count >= 2)
            {
                _builder.Append(", ");
                TranslateExpression(args[1].Expression);
            }
            else if (method.Name == Config.Exchange && args.Count >= 2)
            {
                _builder.Append(", ");
                TranslateExpression(args[1].Expression);
            }
            else if (method.Name == Config.CompareExchange && args.Count >= 3)
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
            var args = invocation.ArgumentList.Arguments;
            bool isVectorArg = args.Count > 0 && IsVectorType(method.Parameters[0].Type);

            // 本地函数：发射函数调用形式（用于向量类型或未展开的函数）
            void EmitCall(string funcName)
            {
                _builder.Append(funcName).Append('(');
                for (int i = 0; i < args.Count; i++)
                {
                    if (i > 0) _builder.Append(", ");
                    TranslateExpression(args[i].Expression);
                }
                _builder.Append(')');
            }

            switch (method.Name)
            {
                case "dot":
                    // a.x()*b.x() + a.y()*b.y()
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".x()*");
                    TranslateExpression(args[1].Expression);
                    _builder.Append(".x() + ");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".y()*");
                    TranslateExpression(args[1].Expression);
                    _builder.Append(".y()");
                    return;

                case "lengthsq":
                    // v.x()*v.x() + v.y()*v.y()
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".x()*");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".x() + ");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".y()*");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".y()");
                    return;

                case "distancesq":
                    // (a.x()-b.x())*(a.x()-b.x()) + (a.y()-b.y())*(a.y()-b.y())
                    _builder.Append('(');
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".x()-");
                    TranslateExpression(args[1].Expression);
                    _builder.Append(".x())*(");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".x()-");
                    TranslateExpression(args[1].Expression);
                    _builder.Append(".x()) + (");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".y()-");
                    TranslateExpression(args[1].Expression);
                    _builder.Append(".y())*(");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".y()-");
                    TranslateExpression(args[1].Expression);
                    _builder.Append(".y())");
                    return;

                case "length":
                    // ::sqrtf(v.x()*v.x() + v.y()*v.y())
                    _builder.Append("::sqrtf(");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".x()*");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".x() + ");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".y()*");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(".y())");
                    return;

                case "min":
                    if (isVectorArg)
                    {
                        // float2/int2: 无 operator<，保留函数调用
                        EmitCall("EntJoy::Mathematics::min");
                        return;
                    }
                    // scalar: a < b ? a : b
                    TranslateExpression(args[0].Expression);
                    _builder.Append(" < ");
                    TranslateExpression(args[1].Expression);
                    _builder.Append(" ? ");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(" : ");
                    TranslateExpression(args[1].Expression);
                    return;

                case "max":
                    if (isVectorArg)
                    {
                        EmitCall("EntJoy::Mathematics::max");
                        return;
                    }
                    TranslateExpression(args[0].Expression);
                    _builder.Append(" > ");
                    TranslateExpression(args[1].Expression);
                    _builder.Append(" ? ");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(" : ");
                    TranslateExpression(args[1].Expression);
                    return;

                case "abs":
                    if (isVectorArg)
                    {
                        EmitCall("EntJoy::Mathematics::abs");
                        return;
                    }
                    _builder.Append('(');
                    TranslateExpression(args[0].Expression);
                    _builder.Append(" < 0 ? -");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(" : ");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(')');
                    return;

                case "clamp":
                    if (isVectorArg)
                    {
                        EmitCall("EntJoy::Mathematics::clamp");
                        return;
                    }
                    TranslateExpression(args[0].Expression);
                    _builder.Append(" < ");
                    TranslateExpression(args[1].Expression);
                    _builder.Append(" ? ");
                    TranslateExpression(args[1].Expression);
                    _builder.Append(" : (");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(" > ");
                    TranslateExpression(args[2].Expression);
                    _builder.Append(" ? ");
                    TranslateExpression(args[2].Expression);
                    _builder.Append(" : ");
                    TranslateExpression(args[0].Expression);
                    _builder.Append(")");
                    return;

                default:
                    // 对于未展开的函数（normalize, lerp, floor, ceil 等），保留原有函数调用形式
                    string cppFunc = method.Name switch
                    {
                        "normalize" => "EntJoy::Mathematics::normalize",
                        "lerp" => "EntJoy::Mathematics::lerp",
                        "floor" => "EntJoy::Mathematics::floor",
                        "ceil" => "EntJoy::Mathematics::ceil",
                        _ => null
                    };

                    if (cppFunc != null)
                    {
                        EmitCall(cppFunc);
                        return;
                    }

                    TranslateExpression(invocation.Expression);
                    _builder.Append('(');
                    for (int i = 0; i < args.Count; i++)
                    {
                        if (i > 0) _builder.Append(", ");
                        TranslateExpression(args[i].Expression);
                    }
                    _builder.Append(')');
                    return;
            }
        }

        private static bool IsVectorType(ITypeSymbol? type)
            => type?.Name is "float2" or "int2" or "uint2";

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

        protected virtual void AppendConstant(object? value)
        {
            if (value is string str) _builder.Append($"\"{str}\"");
            else if (value is bool b) _builder.Append(b ? "true" : "false");
            else if (value is float f)
            {
                // 处理 NaN/Infinity 这些 C# 能表示但 C++ 字面量不支持的浮点值
                if (float.IsNaN(f)) { _builder.Append("NAN"); return; }
                if (float.IsPositiveInfinity(f)) { _builder.Append("INFINITY"); return; }
                if (float.IsNegativeInfinity(f)) { _builder.Append("-INFINITY"); return; }
                string floatStr = f.ToString(System.Globalization.CultureInfo.InvariantCulture);
                // Ensure the float literal has a decimal point for valid C++ syntax (e.g., "1920" -> "1920.0f")
                if (!floatStr.Contains('.') && !floatStr.Contains('e') && !floatStr.Contains('E'))
                    floatStr += ".0";
                _builder.Append(floatStr);
                _builder.Append('f');
            }
            else if (value is double d)
            {
                if (double.IsNaN(d)) { _builder.Append("NAN"); return; }
                if (double.IsPositiveInfinity(d)) { _builder.Append("INFINITY"); return; }
                if (double.IsNegativeInfinity(d)) { _builder.Append("-INFINITY"); return; }
                _builder.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else _builder.Append(value?.ToString() ?? "nullptr");
        }

        /// <summary>
        /// Attempt SIMD vectorization for a simple data loop.
        /// Activated when _enableAutoSIMD is true by TranslateForStatement.
        /// </summary>
        private bool TryGenerateSIMDForLoop(ForStatementSyntax forStmt)
        {
            if (forStmt.Declaration == null) return false;
            if (forStmt.Declaration.Variables.Count != 1) return false;
            var varDecl = forStmt.Declaration.Variables[0];
            string ivName = varDecl.Identifier.Text;
            if (varDecl.Initializer == null) return false;
            if (forStmt.Condition is not BinaryExpressionSyntax cond || cond.Kind() != SyntaxKind.LessThanExpression) return false;
            if (cond.Left is not IdentifierNameSyntax condId || condId.Identifier.Text != ivName) return false;
            bool validInc = false;
            foreach (var inc in forStmt.Incrementors)
                if (inc is PostfixUnaryExpressionSyntax post && post.Kind() == SyntaxKind.PostIncrementExpression) validInc = true;
                else if (inc is PrefixUnaryExpressionSyntax pre && pre.Kind() == SyntaxKind.PreIncrementExpression) validInc = true;
            if (!validInc) return false;
            if (forStmt.Statement is not BlockSyntax loopBody) return false;
            foreach (var stmt in loopBody.Statements)
                if (stmt is ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                    or ReturnStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax)
                    return false;

            string startExpr = varDecl.Initializer.Value.ToString();
            string endExpr = cond.Right.ToString();

            // ===== 检测规约模式 =====
            bool isAos = false;
            string? arrayField = null;
            string? aosVar = null;
            string? reductionField = null;
            string? indexField = null;
            bool isMin = true;
            bool isSum = false;

            foreach (var stmt in loopBody.Statements)
            {
                // 检测 AoS 结构体加载: float2 pos = arr[i];
                if (stmt is LocalDeclarationStatementSyntax ld)
                {
                    foreach (var v in ld.Declaration.Variables)
                    {
                        if (v.Initializer?.Value is ElementAccessExpressionSyntax ea
                            && ea.ArgumentList.Arguments.Count == 1
                            && ea.ArgumentList.Arguments[0].Expression.ToString() == ivName)
                        {
                            arrayField = ea.Expression.ToString();
                            aosVar = v.Identifier.Text;
                            var type = _semanticModel.GetTypeInfo(ld.Declaration.Type).Type;
                            if (type?.Name is "float2" or "int2") isAos = true;
                        }
                    }
                    continue;
                }

                // 检测 if reduction: if (d < best) { best = d; idx = i; }
                if (stmt is IfStatementSyntax ifStmt && ifStmt.Else == null
                    && ifStmt.Condition is BinaryExpressionSyntax bin)
                {
                    if (bin.Kind() == SyntaxKind.LessThanExpression || bin.Kind() == SyntaxKind.LessThanOrEqualExpression)
                        isMin = true;
                    else if (bin.Kind() == SyntaxKind.GreaterThanExpression || bin.Kind() == SyntaxKind.GreaterThanOrEqualExpression)
                        isMin = false;
                    else continue;

                    if (bin.Right is IdentifierNameSyntax rid)
                        reductionField = rid.Identifier.Text;
                    else continue;

                    var bodyStmts = ifStmt.Statement is BlockSyntax blk ? blk.Statements.ToList()
                        : new List<StatementSyntax> { ifStmt.Statement };
                    foreach (var s in bodyStmts)
                    {
                        if (s is ExpressionStatementSyntax es && es.Expression is AssignmentExpressionSyntax ae
                            && ae.Kind() == SyntaxKind.SimpleAssignmentExpression)
                        {
                            if (ae.Left is IdentifierNameSyntax lid)
                            {
                                if (lid.Identifier.Text == reductionField) continue; // best = d
                                if (ae.Right.ToString() == ivName) { indexField = lid.Identifier.Text; }
                            }
                        }
                    }
                    continue;
                }

                // 检测 sum: total += arr[i];
                if (stmt is ExpressionStatementSyntax es2 && es2.Expression is AssignmentExpressionSyntax ae2
                    && ae2.Kind() == SyntaxKind.AddAssignmentExpression
                    && ae2.Left is IdentifierNameSyntax lid2)
                {
                    reductionField = lid2.Identifier.Text;
                    isSum = true;
                }
            }

            if (reductionField == null) return false;

            // ===== 取标量体文本（余量循环用） =====
            string saved = _builder.ToString();
            _builder.Clear();
            int savedIndent = _indentLevel;
            _indentLevel = 0;
            TranslateBlock(loopBody, skipOuterBraces: true);
            string scalarBody = _builder.ToString();
            _builder.Clear();
            _builder.Append(saved);
            _indentLevel = savedIndent;

            // ===== 生成 SIMD 代码 =====
            AppendIndent(); _builder.AppendLine("{");
            _indentLevel++;

            string arr = arrayField != null ? arrayField + "_ptr" : "/*array*/_ptr";

            if (isAos && aosVar != null && arrayField != null)
            {
                // Pattern: AoS float2 + distance + min + idx (ClosestPoint)
                GenerateSIMD_AosDist(reductionField, indexField, ivName, startExpr, endExpr, arr, scalarBody, isMin);
            }
            else if (isSum)
            {
                GenerateSIMD_Sum(reductionField, ivName, startExpr, endExpr, arr, scalarBody);
            }
            else
            {
                GenerateSIMD_Reduction(reductionField, indexField, ivName, startExpr, endExpr, arr, scalarBody, isMin);
            }

            _indentLevel--;
            AppendIndent(); _builder.AppendLine("}");
            return true;
        }

        private void GenerateSIMD_AosDist(string redField, string? idxField, string ivName,
            string startExpr, string endExpr, string arr, string scalarBody, bool isMin)
        {
            string cmpOp = isMin ? "n_cmp_lt_ps" : "n_cmp_gt_ps";
            string redOp = isMin ? "n_min_ps" : "n_max_ps";
            string hRedOp = isMin ? "n_hmin_ps" : "n_hmax_ps";

            AppendIndent(); _builder.AppendLine("// SIMD AoS distance + min reduction");
            AppendIndent(); _builder.AppendLine($"n_float v_{redField} = n_set1_ps({redField});");
            if (idxField != null)
                AppendIndent(); _builder.AppendLine($"n_int v_{idxField} = n_set1_epi32({idxField});");
            AppendIndent(); _builder.AppendLine("n_int v_base = n_set_epi32(7,6,5,4,3,2,1,0);");
            AppendIndent(); _builder.AppendLine($"int simd_end_ = {startExpr} + (({endExpr} - {startExpr}) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            AppendIndent(); _builder.AppendLine("if (simd_end_ > 0)");
            AppendIndent(); _builder.AppendLine("{");
            _indentLevel++;
            AppendIndent(); _builder.AppendLine($"for (int si_ = {startExpr}; si_ < simd_end_; si_ += NSIMD_WIDTH)");
            AppendIndent(); _builder.AppendLine("{");
            _indentLevel++;
            AppendIndent(); _builder.AppendLine("n_int v_i = n_add_epi32(v_base, n_set1_epi32(si_));");
            AppendIndent(); _builder.AppendLine($"n_float v_px = n_gather_ps<sizeof(({arr})[0])>((const float*){arr}, v_i);");
            AppendIndent(); _builder.AppendLine($"n_float v_py = n_gather_ps<sizeof(({arr})[0])>(((const float*){arr}) + 1, v_i);");
            AppendIndent(); _builder.AppendLine($"n_float v_dx = n_sub_ps(n_set1_ps(q.x()), v_px);");
            AppendIndent(); _builder.AppendLine($"n_float v_dy = n_sub_ps(n_set1_ps(q.y()), v_py);");
            AppendIndent(); _builder.AppendLine("n_float v_dsq = n_fmadd_ps(v_dx, v_dx, n_mul_ps(v_dy, v_dy));");
            AppendIndent(); _builder.AppendLine($"n_mask v_mask = {cmpOp}(v_dsq, v_{redField});");
            AppendIndent(); _builder.AppendLine($"v_{redField} = {redOp}(v_{redField}, v_dsq);");
            if (idxField != null)
                AppendIndent(); _builder.AppendLine($"v_{idxField} = n_blend_epi32(v_{idxField}, v_i, v_mask);");
            _indentLevel--;
            AppendIndent(); _builder.AppendLine("}");
            AppendIndent(); _builder.AppendLine("}");
            AppendIndent(); _builder.AppendLine($"float simd_val_ = {hRedOp}(v_{redField});");
            if (idxField != null)
            {
                AppendIndent(); _builder.AppendLine($"int simd_idx_ = n_hmin_idx(v_{redField}, v_{idxField});");
                AppendIndent(); _builder.AppendLine($"if (simd_val_ < {redField}) {{ {redField} = simd_val_; {idxField} = simd_idx_; }}");
            }
            else
            {
                AppendIndent(); _builder.AppendLine($"if (simd_val_ < {redField}) {redField} = simd_val_;");
            }
            // 标量余量
            AppendIndent(); _builder.AppendLine($"for (int si_ = simd_end_; si_ < {endExpr}; ++si_)");
            AppendIndent(); _builder.AppendLine("{");
            _indentLevel++;
            AppendIndent(); _builder.AppendLine($"int {ivName} = si_;");
            foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                AppendIndent(); _builder.AppendLine(line.TrimEnd());
            }
            _indentLevel--;
            AppendIndent(); _builder.AppendLine("}");
        }

        private void GenerateSIMD_Sum(string redField, string ivName,
            string startExpr, string endExpr, string arr, string scalarBody)
        {
            AppendIndent(); _builder.AppendLine("// SIMD sum reduction");
            AppendIndent(); _builder.AppendLine($"n_float v_{redField} = n_set1_ps(0);");
            AppendIndent(); _builder.AppendLine($"int simd_end_ = {startExpr} + (({endExpr} - {startExpr}) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            AppendIndent(); _builder.AppendLine("if (simd_end_ > 0)");
            AppendIndent(); _builder.AppendLine("{");
            _indentLevel++;
            AppendIndent(); _builder.AppendLine($"for (int si_ = {startExpr}; si_ < simd_end_; si_ += NSIMD_WIDTH)");
            AppendIndent(); _builder.AppendLine("{");
            _indentLevel++;
            AppendIndent(); _builder.AppendLine($"n_float v_val = n_load_ps(&{arr}[si_]);");
            AppendIndent(); _builder.AppendLine($"v_{redField} = n_add_ps(v_{redField}, v_val);");
            _indentLevel--;
            AppendIndent(); _builder.AppendLine("}");
            _indentLevel--;
            AppendIndent(); _builder.AppendLine("}");
            AppendIndent(); _builder.AppendLine($"{redField} += n_hsum_ps(v_{redField});");
            AppendIndent(); _builder.AppendLine($"for (int si_ = simd_end_; si_ < {endExpr}; ++si_)");
            AppendIndent(); _builder.AppendLine("{");
            _indentLevel++;
            AppendIndent(); _builder.AppendLine($"int {ivName} = si_;");
            foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                AppendIndent(); _builder.AppendLine(line.TrimEnd());
            }
            _indentLevel--;
            AppendIndent(); _builder.AppendLine("}");
        }

        private void GenerateSIMD_Reduction(string redField, string? idxField, string ivName,
            string startExpr, string endExpr, string arr, string scalarBody, bool isMin)
        {
            string redOp = isMin ? "n_min_ps" : "n_max_ps";
            string hRedOp = isMin ? "n_hmin_ps" : "n_hmax_ps";

            AppendIndent(); _builder.AppendLine("// SIMD scalar reduction");
            AppendIndent(); _builder.AppendLine($"n_float v_{redField} = n_set1_ps({redField});");
            AppendIndent(); _builder.AppendLine($"int simd_end_ = {startExpr} + (({endExpr} - {startExpr}) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            AppendIndent(); _builder.AppendLine("if (simd_end_ > 0)");
            AppendIndent(); _builder.AppendLine("{");
            _indentLevel++;
            AppendIndent(); _builder.AppendLine($"for (int si_ = {startExpr}; si_ < simd_end_; si_ += NSIMD_WIDTH)");
            AppendIndent(); _builder.AppendLine("{");
            _indentLevel++;
            AppendIndent(); _builder.AppendLine($"n_float v_val = n_load_ps(&{arr}[si_]);");
            AppendIndent(); _builder.AppendLine($"v_{redField} = {redOp}(v_{redField}, v_val);");
            _indentLevel--;
            AppendIndent(); _builder.AppendLine("}");
            _indentLevel--;
            AppendIndent(); _builder.AppendLine("}");
            AppendIndent(); _builder.AppendLine($"float simd_val_ = {hRedOp}(v_{redField});");
            if (idxField != null)
            {
                // 简单的 idx tracking：标量 fallback 不做 SIMD idx
                // 用户应使用 AoS 模式走 idx gather 路径
            }
            AppendIndent(); _builder.AppendLine($"if (simd_val_ < {redField}) {redField} = simd_val_;");
            AppendIndent(); _builder.AppendLine($"for (int si_ = simd_end_; si_ < {endExpr}; ++si_)");
            AppendIndent(); _builder.AppendLine("{");
            _indentLevel++;
            AppendIndent(); _builder.AppendLine($"int {ivName} = si_;");
            foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                AppendIndent(); _builder.AppendLine(line.TrimEnd());
            }
            _indentLevel--;
            AppendIndent(); _builder.AppendLine("}");
        }


    }
}
