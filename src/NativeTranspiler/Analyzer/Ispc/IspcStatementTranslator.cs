using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public class IspcStatementTranslator : CppPointerStatementTranslator
    {
        // ISPC 语言不支持 C 的 (unsigned) 重解释转换，禁用 wrap-safe 整数算术。
        // ISPC 对 signed overflow 的行为虽也是 UB，但实际没有像 clang -O2 那样
        // 激进地优化 -INT_MIN → 0（ISPC 使用 LLVM 但不同优化 pass）。
        protected override bool EnableWrapSafeIntArithmetic => false;

        private readonly Dictionary<string, bool> _constBoolFields = new();
        private readonly bool _useUniformVars;

        protected readonly HashSet<string> _entityRefParamNames = new();

        // ─── SendEvent 支持（ISPC EventBuffer 写入） ───
        /// <summary>ISPC 函数中 EventBuffer 指针数组的参数名（null = 未启用 SendEvent）。</summary>
        private string? _eventBufferParamName;

        /// <summary>Execute 中发现的 SendEvent 事件类型（有序，index 对应 eventBufferHeaders 数组）。</summary>
        public List<INamedTypeSymbol> EventTypes { get; } = new();

        /// <summary>托管事件类型错误（编译时报错）。</summary>
        public List<(INamedTypeSymbol eventType, InvocationExpressionSyntax invocation)> ManagedEventErrors { get; } = new();

        /// <summary>设置 EventBuffer 参数名（由外部生成器在检测到 Job 使用 SendEvent 后调用）。</summary>
        public void SetEventBufferParamName(string name) => _eventBufferParamName = name;

        /// <summary>是否启用 SendEvent 翻译（有 EventBuffer 参数）。</summary>
        public bool HasEventBufferSupport => _eventBufferParamName != null;

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
            // GetTypeInfo(objCreation) 优先（同 C++ 侧：VS/MSBuild Roslyn 对嵌套类型 .Type 返回 null）
            var typeInfo = _semanticModel.GetTypeInfo(objectCreation);
            var type = typeInfo.Type ?? _semanticModel.GetTypeInfo(objectCreation.Type).Type;
            string cppType = type != null ? NativeTranspiler.MapCSharpTypeToCpp(type) : objectCreation.Type.ToString();
            string ispcType = ToIspcType(cppType);

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
            if (exprType != null && NativeTranspiler.IsEntJoyContainerNamed(exprType, Config.NativeList))
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

        protected override void TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                string fullTypeName = methodSymbol.ContainingType?.ToDisplayString();

                if (fullTypeName == "EntJoy.Collections.UnsafeUtility" &&
                    methodSymbol.Name == Config.ArrayElementAsRef)
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

            // ISPC 原子是 fetch-add/sub（返回旧值），而 C# Interlocked.Add/Increment/Decrement 返回新值（add-fetch）。
            // 翻译后需补回增量使返回值语义与 C# 一致：
            //   Increment → atomic_add_global(ptr, 1) + 1
            //   Add       → atomic_add_global(ptr, val) + val
            //   Decrement → atomic_subtract_global(ptr, 1) - 1
            string ispcFunc = method.Name switch
            {
                "Increment" => "atomic_add_global",
                "Decrement" => "atomic_subtract_global",
                Config.Add => "atomic_add_global",
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

            string? addValueText = null;
            if (method.Name == Config.Add && args.Count >= 2)
            {
                _builder.Append(", ");
                addValueText = CaptureExpressionText(args[1].Expression);
                _builder.Append(addValueText);
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
            // ─── SendEvent 拦截（ISPC EventBuffer 写入） ───
            if (_eventBufferParamName != null &&
                exprStmt.Expression is InvocationExpressionSyntax invocation &&
                TryTranslateSendEvent(invocation))
            {
                return;
            }

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

        // ─── SendEvent 翻译（ISPC EventBuffer 写入） ───

        /// <summary>
        /// 检测 world.SendEvent&lt;T&gt;(new T { ... }) 调用，生成 ISPC EventBuffer 写入代码。
        /// 返回 true 表示已翻译（调用方应 return）。
        /// </summary>
        private bool TryTranslateSendEvent(InvocationExpressionSyntax invocation)
        {
            // 情况 1：裸调用 SendEvent<T>(...) — Expression 直接是 IdentifierName
            if (invocation.Expression is IdentifierNameSyntax bareName
                && bareName.Identifier.Text == Config.SendEvent)
            {
                if (invocation.ArgumentList?.Arguments.Count == 1)
                {
                    var typeInfo = _semanticModel.GetTypeInfo(bareName);
                    if (typeInfo.Type is INamedTypeSymbol evtType && NativeTranspileValidator.IsUnmanagedType(evtType))
                        return GenerateSendEventIspc(invocation, evtType, typeInfo.Type.ToDisplayString());
                }
            }

            // 情况 2：xxx.SendEvent<T>(...) — MemberAccess 链（EventBus.SendEvent / World.SendEvent）
            if (invocation.Expression is MemberAccessExpressionSyntax mac
                && mac.Name is GenericNameSyntax genericName
                && genericName.Identifier.Text == Config.SendEvent
                && genericName.TypeArgumentList?.Arguments.Count == 1)
            {
                var typeArg = genericName.TypeArgumentList.Arguments[0];
                var typeInfo = _semanticModel.GetTypeInfo(typeArg);
                if (typeInfo.Type is INamedTypeSymbol evtType && NativeTranspileValidator.IsUnmanagedType(evtType))
                    return GenerateSendEventIspc(invocation, evtType, typeInfo.Type.ToDisplayString());
                if (typeInfo.Type is INamedTypeSymbol managedEvtType && !NativeTranspileValidator.IsUnmanagedType(managedEvtType))
                {
                    ManagedEventErrors.Add((managedEvtType, invocation));
                    return false;
                }
            }

            // 情况 3：GetSymbolInfo 回退
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol method
                && method.Name == Config.SendEvent && method.IsGenericMethod)
            {
                var eventType = method.TypeArguments[0] as INamedTypeSymbol;
                if (eventType != null && NativeTranspileValidator.IsUnmanagedType(eventType))
                    return GenerateSendEventIspc(invocation, eventType, eventType.ToDisplayString());
                if (eventType != null && !NativeTranspileValidator.IsUnmanagedType(eventType))
                {
                    ManagedEventErrors.Add((eventType, invocation));
                    return false;
                }
            }

            return false;
        }

        /// <summary>生成 SendEvent 的 ISPC EventBuffer 写入代码（per-lane 原子槽分配）。</summary>
        private bool GenerateSendEventIspc(InvocationExpressionSyntax invocation, INamedTypeSymbol eventType, string cppTypeName)
        {
            // 记录事件类型（去重）
            int typeIndex = -1;
            for (int i = 0; i < EventTypes.Count; i++)
            {
                if (SymbolEqualityComparer.Default.Equals(EventTypes[i], eventType))
                {
                    typeIndex = i;
                    break;
                }
            }
            if (typeIndex < 0)
            {
                typeIndex = EventTypes.Count;
                EventTypes.Add(eventType);
            }

            string cppEventType = NativeTranspiler.MapCSharpTypeToCpp(eventType);
            string ispcEventType = ToIspcType(cppEventType);
            string tempVar = $"__evt_{eventType.Name}_{typeIndex}";
            string bufVar = _eventBufferParamName!;
            // 事件 buffer 访问表达式（内联，避免在 divergent 块内声明 uniform 局部变量 —— ISPC 禁止）
            string bufExpr = $"((uniform __EntJoyEventBuffer* uniform*){bufVar})[{typeIndex}]";

            // ISPC SendEvent 正确性要点（实测验证）：
            //  1) 原子槽分配用 atomic_add_global —— ISPC 的 atomic_add_global 是 fetch-add 语义，
            //     直接返回旧值（= 槽位索引）。⚠ 不要像 C++ 宏 INTERLOCKED_ADD_AND_FETCH32 那样减 1！
            //     C++ 宏是 add-fetch（返回新值）需减 1 得旧值；ISPC 返回旧值，减 1 会导致 idx=-1 越界写。
            //  2) SIMD foreach 下 atomic_add_global(uniform ptr, varying val) 对每个 active lane 独立原子，
            //     返回各自唯一旧值 → 每个 lane 拿唯一槽位（实测 PASS）。
            //  3) 写入必须用 uniform int* + 显式 int 偏移（AoS 布局）：
            //     varying struct 指针是 SoA 布局（sizeof = lanes×元素），会越界写坏内存 → 禁止。
            //  4) uniform 局部变量不能声明在 divergent（varying 条件）块内 → SendEvent 代码全部内联表达式。
            AppendIndent();
            _builder.AppendLine($"varying int {tempVar}_idx = atomic_add_global({bufExpr}->count, (varying int)1);");
            AppendIndent();

            // 翻译事件对象初始化
            if (invocation.ArgumentList.Arguments.Count > 0)
            {
                var argExpr = invocation.ArgumentList.Arguments[0].Expression;
                if (argExpr is ObjectCreationExpressionSyntax objCreate)
                {
                    string dataPtr = $"((uniform int*){bufExpr}->data)";
                    // 事件类型字段 → C# 布局字节偏移（紧凑 Sequential，无 padding；4 字节标量 / 8 字节 Entity）
                    int stride = ComputeUnmanagedSize(eventType);
                    int fieldOffset = 0;
                    foreach (var init in objCreate.Initializer?.Expressions ?? Enumerable.Empty<ExpressionSyntax>())
                    {
                        if (init is AssignmentExpressionSyntax assign && assign.Left is IdentifierNameSyntax fieldName)
                        {
                            // 找字段符号确定类型（Entity → 拆 Id/Version 两个 int）
                            var fieldSym = eventType.GetMembers(fieldName.Identifier.Text)
                                .OfType<IFieldSymbol>().FirstOrDefault();
                            int fieldSize = fieldSym != null ? ComputeUnmanagedSize(fieldSym.Type) : 4;
                            if (fieldSym != null && IsEntityLike(fieldSym.Type))
                            {
                                // Entity { int Id; int Version; } → 2 个 int
                                string rhs = CaptureExpressionText(assign.Right);
                                AppendIndent();
                                _builder.AppendLine($"{dataPtr}[{tempVar}_idx * {stride / 4} + {fieldOffset / 4 + 0}] = {rhs}.Id;");
                                AppendIndent();
                                _builder.AppendLine($"{dataPtr}[{tempVar}_idx * {stride / 4} + {fieldOffset / 4 + 1}] = {rhs}.Version;");
                            }
                            else
                            {
                                AppendIndent();
                                _builder.Append($"{dataPtr}[{tempVar}_idx * {stride / 4} + {fieldOffset / 4}] = ");
                                TranslateExpression(assign.Right);
                                _builder.AppendLine(";");
                            }
                            fieldOffset += fieldSize;
                        }
                    }
                }
                else
                {
                    // 非对象创建参数：无法逐字段写（字段名未知），不支持。
                    _builder.AppendLine($"// ISPC SendEvent: non-object-creation argument not supported; use new T {{ ... }}.");
                }
            }

            return true;
        }

        /// <summary>计算 unmanaged 类型的 C# 布局大小（紧凑 Sequential，无 padding）。</summary>
        private static int ComputeUnmanagedSize(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol) return IntPtr.Size;
            if (type is INamedTypeSymbol named)
            {
                string fullName = named.ToDisplayString();
                switch (fullName)
                {
                    case "int": case "float": case "uint": case "bool": case "System.Int32":
                    case "System.Single": case "System.UInt32": case "System.Boolean":
                        return 4;
                    case "long": case "ulong": case "double": case "System.Int64":
                    case "System.UInt64": case "System.Double":
                        return 8;
                }
                if (IsEntityLike(named))
                    return 8;
                // 自定义 struct：递归累加字段（紧凑布局，无 padding）
                int total = 0;
                foreach (var f in named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    total += ComputeUnmanagedSize(f.Type);
                return total;
            }
            return 4;
        }

        /// <summary>是否为 Entity 类型（含 int Id + int Version 两个 int 字段）。</summary>
        private static bool IsEntityLike(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol named) return false;
            var fields = named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
            return fields.Count == 2 &&
                   fields.All(f => f.Type.ToDisplayString() is "int" or "System.Int32");
        }

        /// <summary>将表达式翻译为 ISPC 文本并返回，不影响 _builder 的当前状态。</summary>
        private string CaptureExpressionText(ExpressionSyntax expr)
        {
            int savedLen = _builder.Length;
            TranslateExpression(expr);
            string text = _builder.ToString(savedLen, _builder.Length - savedLen);
            _builder.Length = savedLen;
            return text;
        }
    }
}
