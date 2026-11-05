using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public partial class IspcStatementTranslator
    {

        /// <summary>uniform 模式下 ref/out 参数"物化槽"的命名计数器（每个调用点一个唯一名）。</summary>
        private int _uniformRefSlotCounter;

        /// <summary>当前语句正在桥接的 ref/out 参数 → 传给 helper 的槽表达式（形如 <c>&amp;__ej_uref0[0]</c>）。</summary>
        private Dictionary<int, string> _uniformRefSlots;

        /// <summary>语句级桥接期间抑制"返回值包 extract"（语句形式会丢弃返回值，包了反而非法/多余）。</summary>
        private bool _suppressUniformReturnExtract;

        protected override void TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                string fullTypeName = methodSymbol.ContainingType?.ToDisplayString();

                if (fullTypeName == "EntJoy.Collections.UnsafeUtility" &&
                    methodSymbol.Name == Config.ArrayElementAsRef)
                {
                    // 这里是求值上下文（赋值目标是另一条路径，ref 实参在 TranslateInterlockedCall）
                    if (TranslateArrayElementAsRef(invocation, methodSymbol, asLvalue: true))
                        return;
                    base.TranslateInvocation(invocation);
                    return;
                }

                if (NativeTranspiler.IsEntJoyContainerNamed(methodSymbol.ContainingType, Config.NativeList) &&
                    methodSymbol.Name == Config.Resize)
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
                if (NativeTranspiler.IsEntJoyContainerNamed(methodSymbol.ContainingType, Config.NativeArray) &&
                    methodSymbol.Name == Config.GetUnsafePtr)
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
                // float.IsNaN / double.IsInfinity（ContainingType 是 Single/Double，不是 Math）
                if ((methodSymbol.Name == "IsNaN" || methodSymbol.Name == "IsInfinity") &&
                    (methodSymbol.ContainingType?.SpecialType == SpecialType.System_Single ||
                     methodSymbol.ContainingType?.SpecialType == SpecialType.System_Double))
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

                // uniform 模式下的「用户 helper 返回值」桥接
                // uniform 循环体内所有局部量都是 uniform，而用户 helper 的 ISPC 签名返回值一律是 varying
                // （见 IspcGenerator.Helper.cs：`static {ispcReturn} name(...)`，未加 uniform 限定）
                // ⇒ `uniform float x = Helper(...)` 报 "Can't convert from type varying to uniform for ="。
                // 语义：uniform 路径一次只处理一个 index，各 lane 的值完全相同 ⇒ extract(...,0) 取回即等价标量。
                // ⚠ ISPC 的 extract() 只支持标量（struct 返回值取不了，实测报 "Unable to find any matching overload"）
                //   ⇒ 本桥接只覆盖标量返回值；struct 返回值仍然编不过（见框架文档「已知边界」）。
                // ⚠ 只处理同程序集的静态方法：EntJoy.Collections.UnsafeUtility / EntJoy.Mathematics.math 等
                //   框架方法位于别的程序集，已在上面各自分支处理，不能被这里包 extract（否则 extract(常量,0) 非法）。
                if (_useUniformVars
                    && methodSymbol.IsStatic
                    && !methodSymbol.ReturnsVoid
                    && !_suppressUniformReturnExtract
                    && SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingAssembly, _semanticModel.Compilation.Assembly)
                    && IsIspcExtractableScalar(methodSymbol.ReturnType))
                {
                    _builder.Append("extract(");
                    base.TranslateInvocation(invocation);
                    _builder.Append(", 0)");
                    return;
                }
            }
            base.TranslateInvocation(invocation);
        }

        /// <summary>ISPC <c>extract()</c> 支持的类型（标量）；struct/向量/指针不支持。</summary>
        private static bool IsIspcExtractableScalar(ITypeSymbol type)
        {
            if (type == null) return false;
            if (type.TypeKind == TypeKind.Enum) return true;
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// uniform 模式下的实参转型（helper 的 ISPC 签名一律是 varying 形态，见 IspcGenerator.Helper.cs）。
        /// - 指针形参：helper 侧是 <c>uniform T * varying</c>（varying 指针），而 uniform 上下文里的实参是
        /// <c>uniform T * uniform</c> ⇒ ISPC 不做隐式转换（实测 "Unable to find any matching overload"）。
        /// 指针值广播合法（各 lane 指向同一地址）。只动指针级，绝不动 pointee 的 uniform/varying ——
        /// varying pointee 是 gang 宽连续（SOA）布局，改了会读越界（见框架文档）。
        /// - 标量值形参：显式广播为 varying。
        /// - ref/out 形参：由语句级"物化槽"路径接管（见 TryTranslateUniformHelperCallWithRefOut）。
        /// </summary>
        protected override void TranslateArgumentForCppCall(ExpressionSyntax argument, IParameterSymbol parameter)
        {
            if (parameter != null && _useUniformVars)
            {
                if ((parameter.RefKind == RefKind.Ref || parameter.RefKind == RefKind.Out)
                    && _uniformRefSlots != null
                    && _uniformRefSlots.TryGetValue(parameter.Ordinal, out var slotExpr))
                {
                    _builder.Append(slotExpr);
                    return;
                }
                if (parameter.Type is IPointerTypeSymbol ptrType)
                {
                    string elem = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(ptrType.PointedAtType));
                    if (elem != "void")
                        _builder.Append("(uniform ").Append(elem).Append(" * varying)");
                    TranslateExpression(argument);
                    return;
                }
                if (parameter.RefKind != RefKind.Ref && parameter.RefKind != RefKind.Out
                    && IsIspcExtractableScalar(parameter.Type))
                {
                    _builder.Append("(varying ").Append(IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(parameter.Type))).Append(')');
                    TranslateExpression(argument);
                    return;
                }
            }
            base.TranslateArgumentForCppCall(argument, parameter);
        }

        /// <summary>
        /// uniform 模式 + ref/out 形参的用户 helper 调用桥接（框架文档）：
        /// helper 的 ISPC 签名把 ref/out 编译成 <c>varying T * uniform</c>（gang 宽连续布局，
        /// 即 lane i 读写第 i 个元素），而 uniform 上下文里的 <c>&amp;局部量</c> 是 <c>uniform T * uniform</c>。
        /// 两者不能靠类型转换糊过去（那会让 helper 读写该局部量之后的栈内存），唯一正确做法是把值物化到
        /// gang 宽的槽数组：
        /// <code>
        /// varying T slot[1];
        /// slot[0].f = (varying F)x.f; // 广播进槽（结构体逐字段）
        /// Helper(&amp;slot[0], ...);
        /// x.f = extract(slot[0].f, 0); // 取回 uniform
        /// </code>
        /// 语义依据：uniform 路径一次只处理一个 index，各 lane 值完全相同 ⇒ 广播 + 取 lane0 等价标量。
        /// 返回 false 表示"不是可桥接的调用"，交回原路径（保持既有行为）。
        /// </summary>
        private bool TryTranslateUniformHelperCallWithRefOut(InvocationExpressionSyntax invocation)
        {
            if (!_useUniformVars) return false;
            if (_semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
            if (!method.IsStatic) return false;
            if (!SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, _semanticModel.Compilation.Assembly)) return false;
            var containing = method.ContainingType?.ToDisplayString();
            if (containing == "EntJoy.Mathematics.math" || containing == "EntJoy.Collections.UnsafeUtility") return false;

            var args = invocation.ArgumentList.Arguments;
            if (args.Count != method.Parameters.Length) return false;

            bool hasRefOut = false;
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var p = method.Parameters[i];
                if (p.RefKind == RefKind.Ref || p.RefKind == RefKind.Out)
                {
                    // ref/out 只支持"标量或仅含标量字段的结构体"（含指针/容器/嵌套结构体则不做）
                    if (p.Type is IPointerTypeSymbol || NativeTranspiler.IsEntJoyNativeContainerType(p.Type)) return false;
                    if (!IsIspcExtractableScalar(p.Type) && GetScalarFields(p.Type) == null) return false;
                    hasRefOut = true;
                }
                else if (p.Type is IPointerTypeSymbol)
                {
                    // 裸指针：由 TranslateArgumentForCppCall 转型处理
                }
                else if (NativeTranspiler.IsEntJoyNativeContainerType(p.Type) || !IsIspcExtractableScalar(p.Type))
                {
                    // NativeArray/NativeList（一个 C# 形参对应两个 ISPC 形参）与结构体按值传参都不在此桥接范围内
                    return false;
                }
            }
            if (!hasRefOut) return false;

            var slots = new Dictionary<int, string>();
            _uniformRefSlots = slots;

            // 桥接会展开成多条语句 ⇒ 必须自带一对大括号：
            // C# 里 `if (cond) Helper(ref x);` 是无大括号单语句，展开后若不加括号，
            // 只有槽声明属于 if 体，其余语句会掉到 if 外面（实测：Undeclared symbol "__ej_uref0"）。
            AppendIndent();
            _builder.AppendLine("{");
            _indentLevel++;

            // 1) 物化槽：varying T slot[1]; + 广播赋值
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var p = method.Parameters[i];
                if (p.RefKind != RefKind.Ref && p.RefKind != RefKind.Out) continue;

                string slotName = $"__ej_uref{_uniformRefSlotCounter++}";
                string ispcType = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(p.Type));
                slots[p.Ordinal] = $"&{slotName}[0]";

                AppendIndent();
                _builder.Append("varying ").Append(ispcType).Append(' ').Append(slotName).AppendLine("[1];");

                var fields = GetScalarFields(p.Type);
                if (fields == null)
                {
                    AppendIndent();
                    _builder.Append(slotName).Append("[0] = (varying ").Append(ispcType).Append(')');
                    TranslateExpression(args[i].Expression);
                    _builder.AppendLine(";");
                }
                else
                {
                    foreach (var f in fields)
                    {
                        AppendIndent();
                        _builder.Append(slotName).Append("[0].").Append(f.Name).Append(" = (varying ")
                            .Append(IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(f.Type))).Append(')');
                        TranslateExpression(args[i].Expression);
                        _builder.Append('.').Append(f.Name).AppendLine(";");
                    }
                }
            }

            // 2) 调用本体（ref/out 实参由 TranslateArgumentForCppCall 换成 &slot[0]）
            AppendIndent();
            _suppressUniformReturnExtract = true;
            TranslateExpression(invocation);
            _suppressUniformReturnExtract = false;
            _builder.AppendLine(";");

            // 3) 取回 uniform 值
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var p = method.Parameters[i];
                if (p.RefKind != RefKind.Ref && p.RefKind != RefKind.Out) continue;
                string slotName = slots[p.Ordinal].Substring(1, slots[p.Ordinal].IndexOf('[') - 1); // &name[0] → name

                var fields = GetScalarFields(p.Type);
                if (fields == null)
                {
                    AppendIndent();
                    TranslateExpression(args[i].Expression);
                    _builder.Append(" = extract(").Append(slotName).AppendLine("[0], 0);");
                }
                else
                {
                    foreach (var f in fields)
                    {
                        AppendIndent();
                        TranslateExpression(args[i].Expression);
                        _builder.Append('.').Append(f.Name).Append(" = extract(")
                            .Append(slotName).Append("[0].").Append(f.Name).AppendLine(", 0);");
                    }
                }
            }

            _uniformRefSlots = null;
            _indentLevel--;
            AppendIndent();
            _builder.AppendLine("}");
            return true;
        }

        /// <summary>结构体的非静态字段（全部是标量时返回列表）；否则返回 null（不做桥接）。</summary>
        private static List<IFieldSymbol> GetScalarFields(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol named) return null;
            if (named.TypeKind != TypeKind.Struct) return null;
            var fields = new List<IFieldSymbol>();
            foreach (var f in named.GetMembers().OfType<IFieldSymbol>())
            {
                if (f.IsStatic) continue;
                if (!IsIspcExtractableScalar(f.Type)) return null;
                fields.Add(f);
            }
            return fields.Count > 0 ? fields : null;
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
                "Clamp" => "clamp",
                "Floor" => "floor",
                "Ceiling" => "ceil",
                "IsNaN" => "isnan",
                "IsInfinity" => "isinf",
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

        protected virtual void TranslateInterlockedCall(IMethodSymbol method, InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;
            if (args.Count == 0) return;

            // ISPC 原子是 fetch-add/sub（返回旧值），而 C# Interlocked.Add/Increment/Decrement 返回新值（add-fetch）。
            // 翻译后需补回增量使返回值语义与 C# 一致：
            //   CompareExchange → atomic_compare_exchange_global(ptr, comparand, value)（返回旧值，与 C# 一致）
            string ispcFunc = method.Name switch
            {
                "Increment" => "atomic_add_global",
                "Decrement" => "atomic_subtract_global",
                Config.Add => "atomic_add_global",
                // C# Interlocked.Exchange(ref loc, val) → 返回旧值；ISPC atomic_swap_global(ptr, val) 语义一致
                Config.Exchange => "atomic_swap_global",
                Config.CompareExchange => "atomic_compare_exchange_global",
                _ => null
            };

            if (ispcFunc == null)
            {
                base.TranslateInvocation(invocation);
                return;
            }

            // 是否需要补回增量（Add/Increment/Decrement 返回新值）
            string? returnCompensation = method.Name switch
            {
                "Increment" => " + 1",
                "Decrement" => " - 1",
                Config.Add => null,  // 动态值，见下方
                _ => null
            };

            _builder.Append('(');
            _builder.Append(ispcFunc).Append('(');

            var targetExpr = args[0].Expression;
            if (targetExpr is RefExpressionSyntax refExpr)
                targetExpr = refExpr.Expression;

            if (targetExpr is InvocationExpressionSyntax innerInvoke)
            {
                var innerSymbol = _semanticModel.GetSymbolInfo(innerInvoke).Symbol as IMethodSymbol;
                if (innerSymbol != null &&
                    innerSymbol.ContainingType?.ToDisplayString() == "EntJoy.Collections.UnsafeUtility" &&
                    innerSymbol.Name == Config.ArrayElementAsRef)
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
                    string ispcElem = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(elemType));
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

            string? addValueText = null;
            if (method.Name == Config.CompareExchange)
            {
                // C#: CompareExchange(ref loc, value, comparand) → 返回旧值
                // ISPC: atomic_compare_exchange_global(ptr, oldval, newval) → 返回旧值（语义一致，无需补回）
                // 参数序：comparand 在前（=ISPC oldval），value 在后（=ISPC newval）
                if (args.Count >= 3)
                {
                    _builder.Append(", ");
                    TranslateExpression(args[2].Expression); // comparand（期望旧值）
                    _builder.Append(", ");
                    TranslateExpression(args[1].Expression); // value（新值）
                }
                else
                {
                    _builder.Append(", 0, 0");
                }
            }
            else if (method.Name == Config.Add && args.Count >= 2)
            {
                _builder.Append(", ");
                addValueText = CaptureExpressionText(args[1].Expression);
                _builder.Append(addValueText);
            }
            else if (method.Name == Config.Exchange && args.Count >= 2)
            {
                // atomic_exchange_global 返回旧值，与 C# 一致，无需补回
                _builder.Append(", ");
                TranslateExpression(args[1].Expression);
            }
            else
            {
                _builder.Append(", 1");
            }
            _builder.Append(')');

            // 补回增量使返回值语义与 C# 一致（ISPC fetch-add 返回旧值，C# 返回新值）
            if (method.Name == "Increment")
                _builder.Append(" + 1");
            else if (method.Name == "Decrement")
                _builder.Append(" - 1");
            else if (method.Name == Config.Add && addValueText != null)
                _builder.Append(" + ").Append(addValueText);

            _builder.Append(')');
        }

    }
}
