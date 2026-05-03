using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer
{
    public class CppPointerStatementTranslator : CppStatementTranslator
    {
        protected readonly HashSet<string> _valueParameterNames;
        protected readonly HashSet<string> _pointerParameterNames;
        protected readonly HashSet<string> _nativeArrayListNames;
        protected readonly HashSet<string> _nativeListNames;

        public CppPointerStatementTranslator(SemanticModel semanticModel, IMethodSymbol method)
            : base(semanticModel)
        {
            _valueParameterNames = new HashSet<string>();
            _pointerParameterNames = new HashSet<string>();
            _nativeArrayListNames = new HashSet<string>();
            _nativeListNames = new HashSet<string>();

            foreach (var p in method.Parameters)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(p.Type))
                {
                    if (p.Type.Name == "NativeList")
                        _nativeListNames.Add(p.Name);
                    else
                        _nativeArrayListNames.Add(p.Name);
                }
                else if (p.Type is IPointerTypeSymbol)
                    _pointerParameterNames.Add(p.Name);
                else
                    _valueParameterNames.Add(p.Name);
            }
        }

        public CppPointerStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct)
            : base(semanticModel)
        {
            _valueParameterNames = new HashSet<string>();
            _pointerParameterNames = new HashSet<string>();
            _nativeArrayListNames = new HashSet<string>();
            _nativeListNames = new HashSet<string>();

            var fields = jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic);
            foreach (var f in fields)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type))
                {
                    if (f.Type.Name == "NativeList")
                        _nativeListNames.Add(f.Name);
                    else
                        _nativeArrayListNames.Add(f.Name);
                }
                else if (f.Type is IPointerTypeSymbol)
                    _pointerParameterNames.Add(f.Name);
                else
                    _valueParameterNames.Add(f.Name);
            }
        }

        protected override void TranslateIdentifier(IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.Text;
            if (TryInlineConstant(identifier)) return;

            if (_nativeArrayListNames.Contains(name) || _nativeListNames.Contains(name))
            {
                _builder.Append(name);
                return;
            }
            if (_valueParameterNames.Contains(name))
            {
                _builder.Append(name);
                return;
            }
            if (_pointerParameterNames.Contains(name))
            {
                _builder.Append(name + "_ptr");
                return;
            }
            base.TranslateIdentifier(identifier);
        }

        protected override void TranslateAssignment(AssignmentExpressionSyntax assignment)
        {
            if (assignment.Left is IdentifierNameSyntax id)
            {
                string name = id.Identifier.Text;
                if (_nativeArrayListNames.Contains(name) || _nativeListNames.Contains(name))
                    _builder.Append(name);
                else if (_valueParameterNames.Contains(name))
                    _builder.Append(name);
                else if (_pointerParameterNames.Contains(name))
                    _builder.Append(name + "_ptr");
                else
                    TranslateExpression(assignment.Left);
            }
            else
            {
                TranslateExpression(assignment.Left);
            }

            _builder.Append(' ').Append(assignment.OperatorToken.Text).Append(' ');
            TranslateExpression(assignment.Right);
        }

        protected override void TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            var exprType = _semanticModel.GetTypeInfo(memberAccess.Expression).Type;
            string memberName = memberAccess.Name.Identifier.Text;

            bool isNativeArray = exprType != null && NativeTranspiler.IsEntJoyNativeContainerType(exprType) && exprType.Name == "NativeArray";
            bool isNativeList = exprType != null && NativeTranspiler.IsEntJoyNativeContainerType(exprType) && exprType.Name == "NativeList";

            if (isNativeArray)
            {
                string fieldName = null;
                if (memberAccess.Expression is IdentifierNameSyntax id)
                    fieldName = id.Identifier.Text;

                if (memberName == "Length")
                {
                    if (fieldName != null && _nativeArrayListNames.Contains(fieldName))
                        _builder.Append(fieldName + "_length");
                    else
                    {
                        TranslateExpression(memberAccess.Expression);
                        _builder.Append(".length()");
                    }
                    return;
                }
                // GetUnsafePtr 作为属性访问（虽然它是方法，但可能作为成员访问出现，这里只处理属性，方法走 TranslateInvocation）
                if (memberName == "GetUnsafePtr")
                {
                    // 不会进入这里，因为调用是 InvocationExpression，但以防万一
                    if (fieldName != null && _nativeArrayListNames.Contains(fieldName))
                        _builder.Append(fieldName + "_ptr");
                    else
                    {
                        TranslateExpression(memberAccess.Expression);
                        _builder.Append(".GetUnsafePtr()");
                    }
                    return;
                }
            }

            if (isNativeList)
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

            base.TranslateMemberAccess(memberAccess);
        }

        protected override void TranslateElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
            var exprType = _semanticModel.GetTypeInfo(elementAccess.Expression).Type;
            if (exprType != null && NativeTranspiler.IsEntJoyNativeContainerType(exprType) && exprType.Name == "NativeArray")
            {
                string fieldName = null;
                if (elementAccess.Expression is IdentifierNameSyntax id)
                    fieldName = id.Identifier.Text;

                if (fieldName != null && _nativeArrayListNames.Contains(fieldName))
                    _builder.Append(fieldName + "_ptr");
                else
                    TranslateExpression(elementAccess.Expression);

                var args = elementAccess.ArgumentList.Arguments;
                if (args.Count > 0)
                {
                    _builder.Append('[');
                    TranslateExpression(args[0].Expression);
                    _builder.Append(']');
                }
                else
                {
                    base.TranslateElementAccess(elementAccess);
                }
                return;
            }
            base.TranslateElementAccess(elementAccess);
        }

        protected override void TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                // ---- 新增：处理 NativeArray 的方法调用 ----
                if (methodSymbol.ContainingType?.Name == "NativeArray" &&
                    NativeTranspiler.IsEntJoyNativeContainerType(methodSymbol.ContainingType))
                {
                    if (methodSymbol.Name == "GetUnsafePtr")
                    {
                        // 获取调用目标，例如 Counts.GetUnsafePtr() 中的 Counts
                        var targetExpr = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
                        if (targetExpr is IdentifierNameSyntax id && _nativeArrayListNames.Contains(id.Identifier.Text))
                        {
                            _builder.Append(id.Identifier.Text + "_ptr");
                        }
                        else
                        {
                            // 如果不是简单字段，回退到常规翻译
                            TranslateExpression(targetExpr);
                            _builder.Append(".GetUnsafePtr()");
                        }
                        return;
                    }
                    // 其他 NativeArray 方法（如果有）可以继续添加
                }

                // 处理 UnsafeUtility.ArrayElementAsRef<T>(void*, int)
                if (methodSymbol.ContainingType?.ToDisplayString() == "EntJoy.Collections.UnsafeUtility" &&
                    methodSymbol.Name == "ArrayElementAsRef")
                {
                    var args = invocation.ArgumentList.Arguments;
                    if (args.Count >= 2)
                    {
                        ITypeSymbol elementType = null;
                        if (methodSymbol.ReturnType is INamedTypeSymbol namedReturn && namedReturn.TypeArguments.Length > 0)
                            elementType = namedReturn.TypeArguments[0];
                        else if (methodSymbol.TypeArguments.Length > 0)
                            elementType = methodSymbol.TypeArguments[0];
                        else
                            elementType = _semanticModel.Compilation.GetSpecialType(SpecialType.System_Int32);

                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        _builder.Append("((").Append(cppElementType).Append("*)");
                        TranslateExpression(args[0].Expression);
                        _builder.Append(")[");
                        TranslateExpression(args[1].Expression);
                        _builder.Append(']');
                        return;
                    }
                    base.TranslateInvocation(invocation);
                    return;
                }

                // 处理 NativeList 的方法调用（Resize, Add 等）
                if (methodSymbol.ContainingType?.Name == "NativeList" &&
                    NativeTranspiler.IsEntJoyNativeContainerType(methodSymbol.ContainingType))
                {
                    TranslateNativeListMethodCall(methodSymbol, invocation);
                    return;
                }

                // 其他情况交给基类（Math、Interlocked、用户自定义等）
                base.TranslateInvocation(invocation);
                return;
            }

            base.TranslateInvocation(invocation);
        }

        private void TranslateNativeListMethodCall(IMethodSymbol method, InvocationExpressionSyntax invocation)
        {
            var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
            TranslateExpression(memberAccess.Expression);
            _builder.Append('.').Append(method.Name).Append('(');
            var args = invocation.ArgumentList.Arguments;
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) _builder.Append(", ");
                if (method.Name == "Resize" && i == 1)
                {
                    _builder.Append("static_cast<EntJoy::Collections::NativeArrayOptions>(");
                    TranslateExpression(args[i].Expression);
                    _builder.Append(')');
                }
                else
                {
                    TranslateExpression(args[i].Expression);
                }
            }
            _builder.Append(')');
        }
    }
}