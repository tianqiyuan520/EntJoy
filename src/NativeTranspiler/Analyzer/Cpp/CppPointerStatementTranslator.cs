using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public class CppPointerStatementTranslator : StatementTranslator
    {
        protected readonly HashSet<string> _valueParameterNames;
        protected readonly HashSet<string> _pointerParameterNames;
        protected readonly HashSet<string> _nativeArrayListNames;
        protected readonly HashSet<string> _nativeListNames;

        public CppPointerStatementTranslator(SemanticModel semanticModel, IMethodSymbol method, bool useFastMath = false, bool enableAutoSIMD = false)
            : base(semanticModel, useFastMath, enableAutoSIMD)
        {
            _valueParameterNames = new HashSet<string>();
            _pointerParameterNames = new HashSet<string>();
            _nativeArrayListNames = new HashSet<string>();
            _nativeListNames = new HashSet<string>();

            foreach (var p in method.Parameters)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(p.Type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(p.Type, Config.NativeList))
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

        public CppPointerStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct, bool useFastMath = false, bool enableAutoSIMD = false)
            : base(semanticModel, useFastMath, enableAutoSIMD)
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
                    if (NativeTranspiler.IsEntJoyContainerNamed(f.Type, Config.NativeList))
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

        /// <summary>该成员访问是否处于"纯读"上下文（不是赋值/复合赋值的左值、也不是自增自减的操作数）。
        /// 只有纯读才可换成返回值访问器；其余一律保留返回引用的访问器。</summary>
        private static bool IsReadContext(MemberAccessExpressionSyntax memberAccess)
        {
            switch (memberAccess.Parent)
            {
                case AssignmentExpressionSyntax assign when assign.Left == memberAccess:
                    return false;
                case PrefixUnaryExpressionSyntax pre
                    when (pre.IsKind(SyntaxKind.PreIncrementExpression)
                       || pre.IsKind(SyntaxKind.PreDecrementExpression))
                    && pre.Operand == memberAccess:
                    return false;
                case PostfixUnaryExpressionSyntax post
                    when (post.IsKind(SyntaxKind.PostIncrementExpression)
                       || post.IsKind(SyntaxKind.PostDecrementExpression))
                    && post.Operand == memberAccess:
                    return false;
                default:
                    return true;
            }
        }

        protected override void TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            var exprType = _semanticModel.GetTypeInfo(memberAccess.Expression).Type;
            string memberName = memberAccess.Name.Identifier.Text;

            bool isNativeArray = exprType != null && NativeTranspiler.IsEntJoyContainerNamed(exprType, Config.NativeArray);
            bool isNativeList = exprType != null && NativeTranspiler.IsEntJoyContainerNamed(exprType, Config.NativeList);

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
                if (memberName == Config.GetUnsafePtr)
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

            // float2 读路径改用**值**访问器 xr()/yr()：x()/y() 返回引用，会让 `float2 q = p[i]`
            // 的读退化成两次 4 字节标量读（逐元素热循环实测 2.3× 代价，见 NativeMath.h 注释）。
            // 只在"纯读"上下文改写；赋值/复合赋值/自增减的左值必须保留引用版本。
            if (memberName is "x" or "y" && IsReadContext(memberAccess))
            {
                TranslateExpression(memberAccess.Expression);
                _builder.Append(memberName == "x" ? ".xr()" : ".yr()");
                return;
            }

            base.TranslateMemberAccess(memberAccess);
        }

        protected override void TranslateElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
            var exprType = _semanticModel.GetTypeInfo(elementAccess.Expression).Type;
            if (exprType != null && NativeTranspiler.IsEntJoyContainerNamed(exprType, Config.NativeArray))
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
                if (NativeTranspiler.IsEntJoyContainerNamed(methodSymbol.ContainingType, Config.NativeArray))
                {
                    if (methodSymbol.Name == Config.GetUnsafePtr)
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
                    methodSymbol.Name == Config.ArrayElementAsRef)
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
                if (NativeTranspiler.IsEntJoyContainerNamed(methodSymbol.ContainingType, Config.NativeList))
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
                if (method.Name == Config.Resize && i == 1)
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

        // ================================================================
        // ★ Wrap-safe int arithmetic (C# unchecked semantics)
        //   C# `int` ops wrap on overflow (unchecked by default); naive C++ `a*b`
        //   is signed-overflow UB — clang -O2 folds `x*2` / `-x` on INT_MIN to 0
        //   (EC10/FZ3 remainder path). Emit unsigned arithmetic (well-defined wrap)
        //   for int * + - and unary minus. Reinterpretation back to int is
        //   implementation-defined but bit-preserving on all supported compilers.
        //   ISPC subclasses disable this (ISPC has no `(unsigned)` cast).
        // ================================================================
        protected virtual bool EnableWrapSafeIntArithmetic => true;

        /// <summary>
        /// C++（Windows/LLP64）字面量后缀修正：C# 的 <c>long</c>/<c>ulong</c> 是 **64 位**，而 C++ 的
        /// <c>long</c>/<c>unsigned long</c> 是 **32 位** ⇒ `1UL`/`1L` 必须译成 `1ULL`/`1LL`。
        ///
        /// 实测（框架自带样例可复现）：C# 写 `v | (1UL &lt;&lt; b)`（b 可达 32..63）→ 生成 C++ `1UL &lt;&lt; b`
        /// ⇒ clang 报 `shift count &gt;= width of type`（UB，实际按 `&amp; 31` 折叠）⇒ **掩码/位图静默写错**
        /// （50,000 实体 enable 位图错 78%；`(1UL&lt;&lt;40)&gt;&gt;32` 由 256 变成 -4）。
        /// </summary>
        protected override string NormalizeNumericLiteral(string text)
        {
            if (text.EndsWith("UL", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith("LU", StringComparison.OrdinalIgnoreCase))
                return text.Substring(0, text.Length - 2) + "ULL";
            if (text.EndsWith("L", StringComparison.OrdinalIgnoreCase))
                return text.Substring(0, text.Length - 1) + "LL";
            return text;   // U/u（C# uint ↔ C++ unsigned int）两语言一致，原样保留
        }

        private bool IsInt32Type(ExpressionSyntax expr)
        {
            try
            {
                var t = _semanticModel.GetTypeInfo(expr).Type;
                return t != null && (t.SpecialType == SpecialType.System_Int32 || t.SpecialType == SpecialType.System_UInt32);
            }
            catch { return false; }
        }

        protected override void TranslateExpression(ExpressionSyntax expr)
        {
            // Unary minus on int → (int)(0u - (unsigned)x) — wrap-safe.
            if (EnableWrapSafeIntArithmetic
                && expr is PrefixUnaryExpressionSyntax pre
                && pre.IsKind(SyntaxKind.UnaryMinusExpression)
                && IsInt32Type(pre.Operand))
            {
                _builder.Append("(int)(0u - (unsigned)(");
                TranslateExpression(pre.Operand);
                _builder.Append("))");
                return;
            }
            base.TranslateExpression(expr);
        }

        protected override void TranslateBinaryExpression(BinaryExpressionSyntax binary)
        {
            string op = binary.OperatorToken.Text;
            if (EnableWrapSafeIntArithmetic
                && (op == "*" || op == "+" || op == "-")
                && IsInt32Type(binary.Left) && IsInt32Type(binary.Right))
            {
                _builder.Append("(int)((unsigned)(");
                TranslateExpression(binary.Left);
                _builder.Append(") ").Append(op).Append(" (unsigned)(");
                TranslateExpression(binary.Right);
                _builder.Append("))");
                return;
            }
            base.TranslateBinaryExpression(binary);
        }
    }
}
