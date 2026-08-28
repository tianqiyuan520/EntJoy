using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public sealed class CppChunkStatementTranslator : CppPointerStatementTranslator
    {
        private sealed class NativeArrayElementAlias
        {
            public string ArrayName { get; set; } = "";
            public string IndexExpression { get; set; } = "";
        }

        private readonly List<INamedTypeSymbol> _requiredComponentTypes;
        private readonly List<INamedTypeSymbol> _requiredSharedTypes;  // SharedComponent 类型（blittable，per-chunk 值指针）
        private readonly HashSet<string> _chunkArrayLocalNames = new();
        private readonly Dictionary<string, NativeArrayElementAlias> _nativeArrayElementAliases = new();

        // ─── SendEvent 支持 ───
        /// <summary>Execute 中发现的 SendEvent 事件类型（有序，index 对应 eventBufferHeaders 数组）。</summary>
        public List<INamedTypeSymbol> EventTypes { get; } = new();

        /// <summary>托管事件类型错误（编译时报错）。</summary>
        public List<(INamedTypeSymbol eventType, InvocationExpressionSyntax invocation)> ManagedEventErrors { get; } = new();

        public CppChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct, List<INamedTypeSymbol> requiredComponentTypes, List<INamedTypeSymbol>? requiredSharedTypes = null, bool useFastMath = false, bool enableAutoSIMD = false)
            : base(semanticModel, jobStruct, useFastMath, enableAutoSIMD)
        {
            _requiredComponentTypes = requiredComponentTypes;
            _requiredSharedTypes = requiredSharedTypes ?? new List<INamedTypeSymbol>();
        }

        protected override void TranslateBlock(BlockSyntax block, bool skipOuterBraces)
        {
            var previousAliases = new Dictionary<string, NativeArrayElementAlias>(_nativeArrayElementAliases);
            RegisterNativeArrayElementAliases(block);

            base.TranslateBlock(block, skipOuterBraces);

            _nativeArrayElementAliases.Clear();
            foreach (var pair in previousAliases)
                _nativeArrayElementAliases[pair.Key] = pair.Value;
        }

        protected override void TranslateLocalDeclaration(LocalDeclarationStatementSyntax localDecl)
        {
            if (TryTranslateChunkArrayLocal(localDecl))
                return;

            if (IsNativeArrayElementAliasLocal(localDecl))
                return;

            base.TranslateLocalDeclaration(localDecl);
        }

        protected override void TranslateExpressionStatement(ExpressionStatementSyntax exprStmt)
        {
            if (exprStmt.Expression is AssignmentExpressionSyntax assignment && IsNativeArrayAliasWriteBack(assignment))
                return;

            // ─── SendEvent 拦截 ───
            if (exprStmt.Expression is InvocationExpressionSyntax invocation
                && TryTranslateSendEvent(invocation))
                return;

            base.TranslateExpressionStatement(exprStmt);
        }

        protected override void TranslateIdentifier(IdentifierNameSyntax identifier)
        {
            if (_nativeArrayElementAliases.TryGetValue(identifier.Identifier.Text, out var alias))
            {
                _builder.Append(alias.ArrayName).Append("_ptr[").Append(alias.IndexExpression).Append(']');
                return;
            }

            base.TranslateIdentifier(identifier);
        }

        private bool TryTranslateChunkArrayLocal(LocalDeclarationStatementSyntax localDecl)
        {
            if (localDecl.Declaration.Variables.Count == 0)
                return false;

            var localType = _semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
            bool isSpan = localType?.Name == Config.Span && localType.ContainingNamespace?.ToDisplayString() == Config.NamespaceSystem;
            bool isNativeArray = localType != null && NativeTranspiler.IsEntJoyContainerNamed(localType, Config.NativeArray);
            if (!isSpan && !isNativeArray)
                return false;

            var lines = new StringBuilder();
            foreach (var variable in localDecl.Declaration.Variables)
            {
                if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation)
                    return false;
                if (!TryBuildChunkArrayExpression(invocation, out var cppType, out var expression))
                    return false;

                _chunkArrayLocalNames.Add(variable.Identifier.Text);
                lines.Append(new string(' ', _indentLevel * 4));
                lines.Append("auto* RESTRICT ");
                lines.Append(variable.Identifier.Text);
                lines.Append("_ptr = reinterpret_cast<");
                lines.Append(cppType);
                lines.Append("*>(");
                lines.Append(expression);
                lines.AppendLine(");");

                lines.Append(new string(' ', _indentLevel * 4));
                lines.Append("int ");
                lines.Append(variable.Identifier.Text);
                lines.Append("_length = __chunkData->entityCount");
                lines.AppendLine(";");
            }

            _builder.Append(lines);
            return true;
        }

        protected override void TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            if (TryBuildChunkArrayExpression(invocation, out _, out var expression))
            {
                _builder.Append(expression);
                return;
            }

            base.TranslateInvocation(invocation);
        }

        private bool TryBuildChunkArrayExpression(InvocationExpressionSyntax invocation, out string cppType, out string expression)
        {
            cppType = "";
            expression = "";

            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
                return false;
            if (methodSymbol.ContainingType?.ToDisplayString() != Config.TypeArchetypeChunk)
                return false;

            // ======================== SharedComponent：GetSharedComponent<T>() → 单值指针 ========================
            // 返回 per-chunk 共享值（blittable，内联于 chunk 内存块 Shared values 区）。
            // 翻译为 `reinterpret_cast<T*>(__chunkData->sharedValuePtrs[sharedIndex])`。
            if (methodSymbol.Name == Config.GetSharedComponent && methodSymbol.TypeArguments.Length == 1)
            {
                var sharedType = methodSymbol.TypeArguments[0];
                int sharedIndex = _requiredSharedTypes.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, sharedType));
                if (sharedIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"SharedComponent type {sharedType.ToDisplayString()} used in chunk job body but " +
                        "was not found in requiredSharedTypes. Fix CollectSharedComponentTypes " +
                        "to include this type.");
                }
                cppType = NativeTranspiler.MapCSharpTypeToCpp(sharedType);
                // 解引用指针：GetSharedComponent<T>() 返回值，C++ 侧需 *reinterpret_cast<T*>(...)
                expression = $"*reinterpret_cast<{cppType}*>(__chunkData->sharedValuePtrs[{sharedIndex}])";
                return true;
            }

            // ======================== 原有：GetComponentDataNativeArray / GetComponentDataSpan → 数组指针 ========================
            if (methodSymbol.Name != Config.GetComponentDataNativeArray && methodSymbol.Name != Config.GetComponentDataSpan)
                return false;
            if (methodSymbol.TypeArguments.Length == 0)
                return false;

            var componentType = methodSymbol.TypeArguments[0];
            int componentIndex = _requiredComponentTypes.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, componentType));
            if (componentIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Component type {componentType.ToDisplayString()} used in chunk job body but " +
                    "was not found in requiredComponentTypes. Fix CollectChunkNativeArrayTypes " +
                    "to include this type, or mark the parameter with proper attributes.");
            }

            cppType = NativeTranspiler.MapCSharpTypeToCpp(componentType);
            expression = $"__chunkData->requiredComponentArrays[{componentIndex}]";
            return true;
        }

        protected override void TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is IdentifierNameSyntax identifier &&
                _chunkArrayLocalNames.Contains(identifier.Identifier.Text) &&
                memberAccess.Name.Identifier.Text == "Length")
            {
                _builder.Append(identifier.Identifier.Text).Append("_length");
                return;
            }

            base.TranslateMemberAccess(memberAccess);
        }

        protected override void TranslateElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
            if (elementAccess.Expression is IdentifierNameSyntax identifier &&
                _chunkArrayLocalNames.Contains(identifier.Identifier.Text))
            {
                _builder.Append(identifier.Identifier.Text).Append("_ptr[");
                var args = elementAccess.ArgumentList.Arguments;
                if (args.Count > 0)
                    TranslateExpression(args[0].Expression);
                _builder.Append(']');
                return;
            }

            base.TranslateElementAccess(elementAccess);
        }

        private void RegisterNativeArrayElementAliases(BlockSyntax block)
        {
            foreach (var statement in block.Statements)
            {
                if (statement is not LocalDeclarationStatementSyntax localDecl)
                    continue;
                if (!TryGetNativeArrayElementAliasLocal(localDecl, out var aliasName, out var alias))
                    continue;
                bool hasWriteBack = BlockContainsAliasWriteBack(block, aliasName, alias);
                bool isReadOnlySource = !BlockWritesAlias(block, aliasName) && !BlockWritesChunkArray(block, alias.ArrayName);
                if (!hasWriteBack && !isReadOnlySource)
                    continue;

                _nativeArrayElementAliases[aliasName] = alias;
            }
        }

        private bool IsNativeArrayElementAliasLocal(LocalDeclarationStatementSyntax localDecl)
            => TryGetNativeArrayElementAliasLocal(localDecl, out var aliasName, out _)
               && _nativeArrayElementAliases.ContainsKey(aliasName);

        private bool TryGetNativeArrayElementAliasLocal(
            LocalDeclarationStatementSyntax localDecl,
            out string aliasName,
            out NativeArrayElementAlias alias)
        {
            aliasName = "";
            alias = null!;

            if (localDecl.Declaration.Variables.Count != 1)
                return false;

            var variable = localDecl.Declaration.Variables[0];
            if (variable.Initializer?.Value is not ElementAccessExpressionSyntax elementAccess)
                return false;
            if (elementAccess.Expression is not IdentifierNameSyntax arrayIdentifier)
                return false;
            if (!_chunkArrayLocalNames.Contains(arrayIdentifier.Identifier.Text))
                return false;

            var args = elementAccess.ArgumentList.Arguments;
            if (args.Count != 1)
                return false;

            aliasName = variable.Identifier.Text;
            alias = new NativeArrayElementAlias
            {
                ArrayName = arrayIdentifier.Identifier.Text,
                IndexExpression = NormalizeExpression(args[0].Expression)
            };
            return true;
        }

        private bool BlockContainsAliasWriteBack(BlockSyntax block, string aliasName, NativeArrayElementAlias alias)
        {
            foreach (var statement in block.Statements)
            {
                if (statement is not ExpressionStatementSyntax exprStmt)
                    continue;
                if (exprStmt.Expression is not AssignmentExpressionSyntax assignment)
                    continue;
                if (!assignment.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression))
                    continue;
                if (assignment.Right is not IdentifierNameSyntax right || right.Identifier.Text != aliasName)
                    continue;
                if (!TryGetChunkArrayElement(assignment.Left, out var arrayName, out var indexExpression))
                    continue;
                if (arrayName == alias.ArrayName && indexExpression == alias.IndexExpression)
                    return true;
            }

            return false;
        }

        private bool BlockWritesAlias(BlockSyntax block, string aliasName)
        {
            foreach (var statement in block.Statements)
            {
                if (statement is not ExpressionStatementSyntax exprStmt)
                    continue;

                if (exprStmt.Expression is AssignmentExpressionSyntax assignment &&
                    ExpressionStartsWithIdentifier(assignment.Left, aliasName))
                {
                    return true;
                }

                if (exprStmt.Expression is PrefixUnaryExpressionSyntax prefix &&
                    ExpressionStartsWithIdentifier(prefix.Operand, aliasName))
                {
                    return true;
                }

                if (exprStmt.Expression is PostfixUnaryExpressionSyntax postfix &&
                    ExpressionStartsWithIdentifier(postfix.Operand, aliasName))
                {
                    return true;
                }
            }

            return false;
        }

        private bool BlockWritesChunkArray(BlockSyntax block, string arrayName)
        {
            foreach (var statement in block.Statements)
            {
                if (statement is not ExpressionStatementSyntax exprStmt)
                    continue;

                if (exprStmt.Expression is AssignmentExpressionSyntax assignment &&
                    TryGetChunkArrayElement(assignment.Left, out var writtenArray, out _) &&
                    writtenArray == arrayName)
                {
                    return true;
                }

                if (exprStmt.Expression is PrefixUnaryExpressionSyntax prefix &&
                    TryGetChunkArrayElement(prefix.Operand, out writtenArray, out _) &&
                    writtenArray == arrayName)
                {
                    return true;
                }

                if (exprStmt.Expression is PostfixUnaryExpressionSyntax postfix &&
                    TryGetChunkArrayElement(postfix.Operand, out writtenArray, out _) &&
                    writtenArray == arrayName)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ExpressionStartsWithIdentifier(ExpressionSyntax expression, string identifier)
        {
            return expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text == identifier,
                MemberAccessExpressionSyntax memberAccess => ExpressionStartsWithIdentifier(memberAccess.Expression, identifier),
                ElementAccessExpressionSyntax elementAccess => ExpressionStartsWithIdentifier(elementAccess.Expression, identifier),
                ParenthesizedExpressionSyntax parenthesized => ExpressionStartsWithIdentifier(parenthesized.Expression, identifier),
                _ => false
            };
        }

        // ——— 向量化提示 ———
        protected override void TranslateForStatement(ForStatementSyntax forStmt)
        {
            AppendIndent();
            base.TranslateForStatement(forStmt);
        }

        // ——— 向量类型运算 ———
        // 不做 x()/y() 分量拆解，交由基类 StatementTranslator 直接生成
        // 完整的 Value += 调用。现代 MSVC 能完全消除 float2 临时对象，
        // 生成单条 addps/mulps/paddd 指令。分量拆解反而阻止了这种
        // SIMD 自动向量化。

        private bool IsNativeArrayAliasWriteBack(AssignmentExpressionSyntax assignment)
        {
            if (!assignment.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression))
                return false;
            if (assignment.Right is not IdentifierNameSyntax right)
                return false;
            if (!_nativeArrayElementAliases.TryGetValue(right.Identifier.Text, out var alias))
                return false;
            if (!TryGetChunkArrayElement(assignment.Left, out var arrayName, out var indexExpression))
                return false;

            return arrayName == alias.ArrayName && indexExpression == alias.IndexExpression;
        }

        private bool TryGetChunkArrayElement(ExpressionSyntax expression, out string arrayName, out string indexExpression)
        {
            arrayName = "";
            indexExpression = "";

            if (expression is not ElementAccessExpressionSyntax elementAccess)
                return false;
            if (elementAccess.Expression is not IdentifierNameSyntax arrayIdentifier)
                return false;
            if (!_chunkArrayLocalNames.Contains(arrayIdentifier.Identifier.Text))
                return false;

            var args = elementAccess.ArgumentList.Arguments;
            if (args.Count != 1)
                return false;

            arrayName = arrayIdentifier.Identifier.Text;
            indexExpression = NormalizeExpression(args[0].Expression);
            return true;
        }

        private static string NormalizeExpression(ExpressionSyntax expression)
            => expression.NormalizeWhitespace().ToFullString();

        // ─── SendEvent 翻译 ───

        /// <summary>
        /// 检测 world.SendEvent&lt;T&gt;(new T { ... }) 调用，生成 C++ EventBuffer 写入代码。
        /// 返回 true 表示已翻译（调用方应 return）。
        /// </summary>
        private bool TryTranslateSendEvent(InvocationExpressionSyntax invocation)
        {
            // 情况 1：裸调用 SendEvent<T>(...) — Expression 直接是 IdentifierName
            if (invocation.Expression is IdentifierNameSyntax bareName
                && bareName.Identifier.Text == Config.SendEvent)
            {
                // 通过泛型实参推断事件类型（裸调用必须用显式类型参数 SendEvent<T>）
                if (invocation.ArgumentList?.Arguments.Count == 1)
                {
                    var typeInfo = _semanticModel.GetTypeInfo(bareName);
                    // 用 NativeTranspileValidator.IsUnmanagedType（递归实现）：Roslyn 原生
                    // IsUnmanagedType 对嵌套 struct 可能返回 false（VS/MSBuild 下尤其不稳）
                    if (typeInfo.Type is INamedTypeSymbol evtType && NativeTranspileValidator.IsUnmanagedType(evtType))
                        return GenerateSendEventCpp(invocation, evtType, typeInfo.Type.ToDisplayString());
                }
            }

            // 情况 2：xxx.SendEvent<T>(...) — MemberAccess 链（ECS.SendEvent / World.SendEvent）
            if (invocation.Expression is MemberAccessExpressionSyntax mac
                && mac.Name is GenericNameSyntax genericName
                && genericName.Identifier.Text == Config.SendEvent
                && genericName.TypeArgumentList?.Arguments.Count == 1)
            {
                var typeArg = genericName.TypeArgumentList.Arguments[0];
                var typeInfo = _semanticModel.GetTypeInfo(typeArg);
                if (typeInfo.Type is INamedTypeSymbol evtType && NativeTranspileValidator.IsUnmanagedType(evtType))
                    return GenerateSendEventCpp(invocation, evtType, typeInfo.Type.ToDisplayString());
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
                    return GenerateSendEventCpp(invocation, eventType, eventType.ToDisplayString());
                if (eventType != null && !NativeTranspileValidator.IsUnmanagedType(eventType))
                {
                    ManagedEventErrors.Add((eventType, invocation));
                    return false;
                }
            }

            return false;
        }

        /// <summary>生成 SendEvent 的 C++ EventBuffer 写入代码。</summary>
        private bool GenerateSendEventCpp(InvocationExpressionSyntax invocation, INamedTypeSymbol eventType, string cppTypeName)
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
            string tempVar = $"__evt_{eventType.Name}_{typeIndex}";

            _builder.AppendLine("{");
            if (false) // EnableSendEventDiag
            {
                _builder.AppendLine($"    fprintf(stderr, \"[SendEvent-CPP] header=%p evtHeaders=%p count=%d\\n\", (void*)__header, __header ? __header->eventBufferHeaders : 0, __header ? __header->eventBufferCount : 0);");
            }
            _builder.AppendLine($"    if (__header != nullptr && __header->eventBufferHeaders != nullptr) {{");
            _builder.AppendLine($"    auto* {tempVar}_buf = ((__EntJoyEventBuffer**)__header->eventBufferHeaders)[{typeIndex}];");
            if (false) // EnableSendEventDiag
            {
                _builder.AppendLine($"    fprintf(stderr, \"[SendEvent-CPP2] buf=%p data=%p countPtr=%p capacity=%d\\n\", (void*){tempVar}_buf, {tempVar}_buf ? {tempVar}_buf->data : 0, {tempVar}_buf ? (void*){tempVar}_buf->count : 0, {tempVar}_buf ? {tempVar}_buf->capacity : 0);");
            }
            _builder.AppendLine($"    if ({tempVar}_buf != nullptr) {{");
            _builder.AppendLine($"    int {tempVar}_idx = INTERLOCKED_ADD_AND_FETCH32({tempVar}_buf->count, 1) - 1;");
            _builder.AppendLine($"    if ({tempVar}_idx < {tempVar}_buf->capacity) {{");

            // 翻译事件对象初始化
            if (invocation.ArgumentList.Arguments.Count > 0)
            {
                var argExpr = invocation.ArgumentList.Arguments[0].Expression;
                if (argExpr is ObjectCreationExpressionSyntax objCreate)
                {
                    _builder.Append($"       (({cppEventType}*){tempVar}_buf->data)[{tempVar}_idx] = {{ ");
                    bool first = true;
                    foreach (var init in objCreate.Initializer?.Expressions ?? Enumerable.Empty<ExpressionSyntax>())
                    {
                        if (!first) _builder.Append(", ");
                        first = false;
                        if (init is AssignmentExpressionSyntax assign)
                            TranslateExpression(assign.Right);
                    }
                    _builder.AppendLine(" };");
                }
                else
                {
                    _builder.AppendLine($"        auto {tempVar}_evt = ");
                    TranslateExpression(argExpr);
                    _builder.AppendLine(";");
                    _builder.AppendLine($"        (({cppEventType}*){tempVar}_buf->data)[{tempVar}_idx] = {tempVar}_evt;");
                }
            }

            _builder.AppendLine("    }");
            _builder.AppendLine("    }");
            _builder.AppendLine("    }");
            _builder.AppendLine("}");
            return true;
        }
    }
}
