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

        private static bool IsVectorType(ITypeSymbol? type)
        {
            if (type == null) return false;
            string name = type.ToDisplayString();
            return name == "EntJoy.Mathematics.float2" ||
                   name == "EntJoy.Mathematics.int2" ||
                   name == "EntJoy.Mathematics.uint2";
        }

        private string? GetNativeListElementCppType(ExpressionSyntax expr)
        {
            var typeInfo = _semanticModel.GetTypeInfo(expr);
            if (typeInfo.Type is INamedTypeSymbol named && NativeTranspiler.IsEntJoyContainerNamed(named, Config.NativeList) && named.TypeArguments.Length > 0)
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

        /// <summary>
        /// 翻译 <c>UnsafeUtility.ArrayElementAsRef&lt;T&gt;(ptr, idx)</c>。
        /// asLvalue=true：求值/赋值目标 → 直接 <c>((T*)ptr)[idx]</c>；
        /// asLvalue=false：作为 <c>ref/out</c> 实参 → 取址 <c>&amp;((T*)ptr)[idx]</c>
        /// （只有 Interlocked 那条路径需要取址，它在 TranslateInterlockedCall 里自行拼接）。
        /// ⚠ 修正前 TranslateInvocation 走的是 asLvalue=false，导致 <c>int s = ArrayElementAsRef&lt;int&gt;(ptr, i)</c>
        /// 生成 <c>int s = &amp;((int*)ptr)[i];</c> → ISPC 报
        /// 「Can't convert between from pointer type "uniform int32 * varying" to non-pointer type "varying int32"」。
        /// </summary>
        private bool TranslateArrayElementAsRef(InvocationExpressionSyntax invocation,
            IMethodSymbol methodSymbol, bool asLvalue)
        {
            if (methodSymbol.ContainingType?.ToDisplayString() != "EntJoy.Collections.UnsafeUtility" ||
                methodSymbol.Name != Config.ArrayElementAsRef)
                return false;

            var args = invocation.ArgumentList.Arguments;
            if (args.Count < 2) return false;

            ITypeSymbol? elementType = null;
            if (methodSymbol.TypeArguments.Length > 0)
                elementType = methodSymbol.TypeArguments[0];
            else if (methodSymbol.ReturnType is INamedTypeSymbol namedReturn && namedReturn.TypeArguments.Length > 0)
                elementType = namedReturn.TypeArguments[0];
            else
                elementType = _semanticModel.Compilation.GetSpecialType(SpecialType.System_Int32);

            string ispcElemType = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(elementType));
            _builder.Append(asLvalue ? "((" : "&((");
            _builder.Append(ispcElemType).Append("*)");
            TranslateExpression(args[0].Expression);
            _builder.Append(")[");
            TranslateExpression(args[1].Expression);
            _builder.Append(']');
            return true;
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
            if (_useUniformVars && _valueParameterNames.Contains(name)
                && _semanticModel.GetSymbolInfo(identifier).Symbol is IFieldSymbol { IsStatic: false })
            {
                _builder.Append("(*").Append(name).Append("_ptr)");
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

        protected override void TranslateObjectCreation(ObjectCreationExpressionSyntax objectCreation)
        {
            // GetTypeInfo(objCreation) 优先（同 C++ 侧：VS/MSBuild Roslyn 对嵌套类型 .Type 返回 null）
            var typeInfo = _semanticModel.GetTypeInfo(objectCreation);
            var type = typeInfo.Type ?? _semanticModel.GetTypeInfo(objectCreation.Type).Type;
            string cppType = type != null ? NativeTranspiler.MapCSharpTypeToCpp(type) : objectCreation.Type.ToString();
            string ispcType = IspcTypeMapper.FromCpp(cppType);

            // uniform 上下文（原子返回值/自修改的串行路径）用 make_uniform_*（make_* 返回 varying struct）
            string prefix = _useUniformVars ? "make_uniform_" : "make_";
            string maker = ispcType switch
            {
                "float2" => prefix + "float2",
                "int2" => prefix + "int2",
                "uint2" => prefix + "uint2",
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
            string targetIspc = IspcTypeMapper.FromCpp(targetCpp);
            if (targetType is IPointerTypeSymbol pointer)
                targetIspc = IspcTypeMapper.PointerTo(NativeTranspiler.MapCSharpTypeToCpp(pointer.PointedAtType), _useUniformVars);

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
            bool isNativeList = exprType != null && NativeTranspiler.IsEntJoyContainerNamed(exprType, Config.NativeList);

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
                string ispcType = IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(exprType!));
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
            if (exprType != null && NativeTranspiler.IsEntJoyContainerNamed(exprType, Config.NativeList))
            {
                var elemCppType = GetNativeListElementCppType(elementAccess.Expression);
                string ispcElem = elemCppType != null ? IspcTypeMapper.FromCpp(elemCppType) : null;
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

        protected override void TranslateExpression(ExpressionSyntax expr)
        {
            // `default` / `default(T)`：C++ 生成 T{} 值初始化，ISPC 无该语法（会原样输出
            // EntJoy::Mathematics::float2{} → 语法错）。向量走 make_*，标量走 0/0.0f，
            // 用户 struct 走生成头文件里的 make_zero_X()；其余类型不改写（显式报错而非静默错译）。
            ITypeSymbol? defaultType = null;
            if (expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.DefaultLiteralExpression))
                defaultType = _semanticModel.GetTypeInfo(lit).Type;
            else if (expr is DefaultExpressionSyntax def)
                defaultType = _semanticModel.GetTypeInfo(def.Type).Type;

            if (defaultType != null && defaultType.TypeKind != TypeKind.Error)
            {
                switch (IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(defaultType)))
                {
                    case "float2": _builder.Append("make_float2(0.0f, 0.0f)"); return;
                    case "int2": _builder.Append("make_int2(0, 0)"); return;
                    case "uint2": _builder.Append("make_uint2(0u, 0u)"); return;
                    case "float": _builder.Append("0.0f"); return;
                    case "double": _builder.Append("0.0"); return;
                    case "bool": _builder.Append("false"); return;
                    case "int":
                    case "unsigned int":
                    case "int8":
                    case "uint8":
                    case "int16":
                    case "uint16":
                    case "int64":
                    case "uint64":
                        _builder.Append('0'); return;
                }
                // 用户 struct：ISPC 无 T{} 字面量 → 用生成的头文件里的零值构造
                if (defaultType.TypeKind == TypeKind.Struct &&
                    !NativeTranspiler.IsBuiltinUnmanaged(defaultType) &&
                    !NativeTranspiler.IsEntJoyPredefinedType(defaultType))
                {
                    _builder.Append($"make_zero_{defaultType.Name}()");
                    return;
                }
            }

            base.TranslateExpression(expr);
        }

        protected override void TranslateAssignment(AssignmentExpressionSyntax assignment)
        {
            // ArrayElementAsRef<T>() 作为赋值目标：不能再套取址前缀。
            if (assignment.Left is InvocationExpressionSyntax lvalueInv &&
                _semanticModel.GetSymbolInfo(lvalueInv).Symbol is IMethodSymbol lvalueSym &&
                TranslateArrayElementAsRef(lvalueInv, lvalueSym, asLvalue: true))
            {
                _builder.Append(' ').Append(assignment.OperatorToken.Text).Append(' ');
                TranslateExpression(assignment.Right);
                return;
            }

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
                    if (sym != null && sym.Name == Config.Add &&
                        sym.ContainingType?.ToDisplayString() == "System.Threading.Interlocked")
                    {
                        TranslateInterlockedCall(sym, inv);
                        return;
                    }
                }
            }
            base.TranslateBinaryExpression(binary);
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

    }
}
