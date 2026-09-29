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

        /// <summary>
        /// 把成员访问的"接收者"归一化为字段名：`X` 与 **`this.X`** 都返回 "X"。
        ///
        /// `this.X` 是合法的 C# 字段访问（局部变量遮蔽同名字段时必须这样写）。旧实现只认裸标识符
        /// ⇒ `this.X` 落到基类的 `default:` 分支，产出 `/*__ENTJOY_UNSUPPORTED_EXPR__ThisExpression*/`
        /// （构建失败，独立验收 C21）。字段名相等时两者的绑定完全一致（`X_ptr` / `X_length`），
        /// 且 `this.X` 的**符号**就是字段，不存在遮蔽歧义。
        /// </summary>
        protected static string SimpleMemberName(ExpressionSyntax? expression)
            => expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } thisAccess
                    => thisAccess.Name.Identifier.Text,
                _ => null
            };

        protected override void TranslateStatement(StatementSyntax statement)
        {
            // ① `unchecked { ... }`：C# 默认语义就是 unchecked，而**本路径**的 int `+ - *`／一元负号
            //    本来就按 `(unsigned)` 环绕发（EnableWrapSafeIntArithmetic）⇒ 等价于普通块。
            //    旧实现落到基类 default 分支 ⇒ 发 `__ENTJOY_UNSUPPORTED_STMT__CheckedStatement` 标记，
            //    把完全可译的代码打成构建失败。
            //    ⚠ 实体路径（CppEntityStatementTranslator，wrap-safe = false）**不满足前提**：
            //      那边的算术是裸 C++（有符号溢出 UB）⇒ 由本属性保证仍走标记。
            //    ⚠ `checked { }` **必须**继续发标记：C++ 无法表达"溢出即抛"。
            if (EnableWrapSafeIntArithmetic
                && statement is CheckedStatementSyntax uncheckedStmt
                && uncheckedStmt.Keyword.IsKind(SyntaxKind.UncheckedKeyword))
            {
                TranslateBlock(uncheckedStmt.Block, skipOuterBraces: false);
                return;
            }

            // ② `switch` 语句（仅常量 case + 整数/字符/布尔选择子）→ C++ `switch`。语义依据：
            //    C# 要求每个 section 以 break/goto/return/throw 结束（空 section 可穿透），
            //    常量 case 的比较语义与 C++ 一致 ⇒ 合法 C# 的一一映射不改变行为。
            //    其余形态（模式 case、枚举/字符串选择子、非常量 case）返回 false ⇒ 基类发标记。
            if (statement is SwitchStatementSyntax switchStmt && TryTranslateSwitchStatement(switchStmt))
                return;

            base.TranslateStatement(statement);
        }

        /// <summary>
        /// `switch` 语句 → C++ `switch`（**仅**常量 case + 整数/字符/布尔选择子）。任何不确定的形态
        /// 都返回 false，由基类写唯一标记让构建失败（绝不静默降级）。
        /// </summary>
        private bool TryTranslateSwitchStatement(SwitchStatementSyntax switchStmt)
        {
            var selectorType = _semanticModel.GetTypeInfo(switchStmt.Expression).Type;
            if (selectorType == null || !IsSwitchableSelectorType(selectorType))
                return false;

            foreach (var section in switchStmt.Sections)
            {
                foreach (var label in section.Labels)
                {
                    if (label is DefaultSwitchLabelSyntax)
                        continue;
                    if (label is not CaseSwitchLabelSyntax caseLabel)
                        return false;   // 模式 case（`case int x when ...`）/ `case var`
                    if (!_semanticModel.GetConstantValue(caseLabel.Value).HasValue)
                        return false;   // 非常量 case 值：C++ 要求整型常量表达式
                }
            }

            AppendIndent();
            _builder.Append("switch (");
            TranslateExpression(switchStmt.Expression);
            _builder.AppendLine(")");
            AppendIndent();
            _builder.AppendLine("{");
            _indentLevel++;
            foreach (var section in switchStmt.Sections)
            {
                foreach (var label in section.Labels)
                {
                    AppendIndent();
                    if (label is CaseSwitchLabelSyntax caseLabel)
                    {
                        _builder.Append("case ");
                        TranslateExpression(caseLabel.Value);
                        _builder.AppendLine(":");
                    }
                    else
                    {
                        _builder.AppendLine("default:");
                    }
                }

                _indentLevel++;
                foreach (var sectionStatement in section.Statements)
                    TranslateStatement(sectionStatement);
                _indentLevel--;
            }

            _indentLevel--;
            AppendIndent();
            _builder.AppendLine("}");
            return true;
        }

        private static bool IsSwitchableSelectorType(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Char:
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                    return true;
                default:
                    return false;
            }
        }

        protected override void TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            // `this.X` ≡ 裸字段访问 `X`：**先**归一到标识符翻译，与裸写形态逐字一致
            // （`X[k]` / `X.GetUnsafePtr()` / `X.Length` 都走与裸名相同的既定路径）。
            // 旧实现让 `this` 落到基类 default 分支 ⇒ 产物是
            // `/*__ENTJOY_UNSUPPORTED_EXPR__ThisExpression*/`（构建失败，独立验收 C21）。
            if (memberAccess.Expression is ThisExpressionSyntax && memberAccess.Name is IdentifierNameSyntax thisMember)
            {
                TranslateIdentifier(thisMember);
                return;
            }

            var exprType = _semanticModel.GetTypeInfo(memberAccess.Expression).Type;
            string memberName = memberAccess.Name.Identifier.Text;

            bool isNativeArray = exprType != null && NativeTranspiler.IsEntJoyContainerNamed(exprType, Config.NativeArray);
            bool isNativeList = exprType != null && NativeTranspiler.IsEntJoyContainerNamed(exprType, Config.NativeList);

            if (isNativeArray)
            {
                string fieldName = SimpleMemberName(memberAccess.Expression);

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
                string fieldName = SimpleMemberName(elementAccess.Expression);

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
                        // 获取调用目标，例如 Counts.GetUnsafePtr() 中的 Counts（也支持 this.Counts）
                        var targetExpr = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
                        string targetField = SimpleMemberName(targetExpr);
                        if (targetField != null && _nativeArrayListNames.Contains(targetField))
                        {
                            _builder.Append(targetField + "_ptr");
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
            => CppNumericLiteral.NormalizeSuffix(text);

        /// <summary>C# 表达式类型（解析失败返回 <see cref="SpecialType.None"/>）。</summary>
        private SpecialType GetExpressionSpecialType(ExpressionSyntax expr)
        {
            try
            {
                return _semanticModel.GetTypeInfo(expr).Type?.SpecialType ?? SpecialType.None;
            }
            catch { return SpecialType.None; }
        }

        private bool Is32BitIntType(SpecialType t)
            => t == SpecialType.System_Int32 || t == SpecialType.System_UInt32;

        protected override void TranslateExpression(ExpressionSyntax expr)
        {
            if (EnableWrapSafeIntArithmetic
                && expr is PrefixUnaryExpressionSyntax pre
                && pre.IsKind(SyntaxKind.UnaryMinusExpression))
            {
                var operandType = GetExpressionSpecialType(pre.Operand);
                // ★ C# `-uint`：结果类型是 **long**（不是 uint、更不是 int）。按 32 位回绕发出
                //   会得到 `1` 而不是 `-4294967295L`（uint.MaxValue 取负）。uint 的最大值取负
                //   一定能放进 int64 ⇒ 直接 64 位取负，无溢出。
                if (operandType == SpecialType.System_UInt32)
                {
                    _builder.Append("(-(long)(");
                    TranslateExpression(pre.Operand);
                    _builder.Append("))");
                    return;
                }
                // 一元负号 int → (int)(0u - (unsigned)x) — wrap-safe（C# 的 `-int.MinValue` 仍是 int）。
                if (operandType == SpecialType.System_Int32)
                {
                    _builder.Append("(int)(0u - (unsigned)(");
                    TranslateExpression(pre.Operand);
                    _builder.Append("))");
                    return;
                }
            }
            base.TranslateExpression(expr);
        }

        protected override void TranslateBinaryExpression(BinaryExpressionSyntax binary)
        {
            string op = binary.OperatorToken.Text;
            if (EnableWrapSafeIntArithmetic && (op == "*" || op == "+" || op == "-"))
            {
                var leftType = GetExpressionSpecialType(binary.Left);
                var rightType = GetExpressionSpecialType(binary.Right);
                if (Is32BitIntType(leftType) && Is32BitIntType(rightType))
                {
                    // ★ C# 二元数值提升（spec 12.4.7）：一边 int、一边 uint ⇒ **两侧都提升为 long**
                    //   再运算，结果类型是 long。按 `(int)((unsigned)L op (unsigned)R)` 发出是
                    //   32 位无符号算术 ⇒ 静默错值：
                    //     100000 * 100000u   C# = 10,000,000,000 / 旧发射 = 1,410,065,408
                    //   二阶后果：回绕后的节点类型成了 int，随后的 `>>` / `%` 变成有符号语义。
                    if (leftType != rightType)
                    {
                        _builder.Append("(long)(");
                        TranslateExpression(binary.Left);
                        _builder.Append(") ").Append(op).Append(" (long)(");
                        TranslateExpression(binary.Right);
                        _builder.Append(')');
                        return;
                    }
                    // 同号 32 位（int op int / uint op uint）的 C# 结果**仍是 32 位**：
                    // 回绕不是 UB ⇒ 保留无符号回绕写法。
                    _builder.Append("(int)((unsigned)(");
                    TranslateExpression(binary.Left);
                    _builder.Append(") ").Append(op).Append(" (unsigned)(");
                    TranslateExpression(binary.Right);
                    _builder.Append("))");
                    return;
                }
            }
            base.TranslateBinaryExpression(binary);
        }
    }
}
