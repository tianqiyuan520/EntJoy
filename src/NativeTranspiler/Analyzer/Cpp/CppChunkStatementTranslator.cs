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

            /// <summary>
            /// 该局部数组名背后的**存储身份**（`component:&lt;idx&gt;` / `shared:&lt;idx&gt;`）。
            /// 同一个分量列可以用**多个局部名**绑定（`componentArrays[i]` 是同一块内存）⇒ 任何按"名字"
            /// 判定的读/写分析都会漏掉"透过另一个名字的写"（独立验收实测：静默错值 1344/4093）。
            /// </summary>
            public string StorageKey { get; set; } = "";

            public string IndexExpression { get; set; } = "";

            /// <summary>
            /// 索引表达式的**语法节点**：别名每次使用都重新翻译它（而不是原样抄 C# 文本）。
            /// 抄文本会让 `arr[Indices[k]]`（NativeArray 字段索引）漏出未声明的 `Indices`、
            /// 让 `arr[this.Offset]` 漏出 `this.` ⇒ 生成物编译失败。
            /// </summary>
            public ExpressionSyntax IndexSyntax { get; set; } = null!;

            /// <summary>索引表达式里出现的标识符（别名要求它们在别名作用域内不被改写，见 <see cref="IsAliasSafe"/>）。</summary>
            public List<string> IndexIdentifiers { get; } = new List<string>();
        }

        private readonly List<INamedTypeSymbol> _requiredComponentTypes;
        private readonly List<INamedTypeSymbol> _requiredSharedTypes;  // SharedComponent 类型（blittable，per-chunk 值指针）
        private readonly HashSet<string> _chunkArrayLocalNames = new();
        /// <summary>局部数组名 → 存储身份（见 <see cref="NativeArrayElementAlias.StorageKey"/>）。</summary>
        private readonly Dictionary<string, string> _chunkArrayStorageKeys = new();
        /// <summary>局部变量**符号** → 存储身份（预扫描填充，符号优先 ⇒ 遮蔽/顺序安全）。</summary>
        private readonly Dictionary<ISymbol, string> _chunkArraySymbolStorageKeys =
            new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
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
            if (TryTranslateEnableBitMapLocal(localDecl))
                return;

            if (TryTranslateChunkArrayLocal(localDecl))
                return;

            if (IsNativeArrayElementAliasLocal(localDecl))
                return;

            base.TranslateLocalDeclaration(localDecl);
        }

        /// <summary>
        /// P1-6/P1-7：`ulong* mask = chunk.GetEnableBitMapPtr&lt;T&gt;();` →
        /// C++ `auto* mask = reinterpret_cast&lt;unsigned long long*&gt;(__chunkData-&gt;requiredEnableBitMaps[requiredIdx]);`
        /// </summary>
        private bool TryTranslateEnableBitMapLocal(LocalDeclarationStatementSyntax localDecl)
        {
            if (localDecl.Declaration.Variables.Count != 1) return false;
            var variable = localDecl.Declaration.Variables[0];
            if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation) return false;
            if (!TryBuildEnableBitMapExpression(invocation, out var expression)) return false;

            AppendIndent();
            _builder.Append("auto* ");
            _builder.Append(variable.Identifier.Text);
            _builder.Append(" = ");
            _builder.Append(expression);
            _builder.AppendLine(";");
            return true;
        }

        private bool TryBuildEnableBitMapExpression(InvocationExpressionSyntax invocation, out string expression)
        {
            expression = "";
            if (_semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
                return false;
            if (methodSymbol.ContainingType?.ToDisplayString() != Config.TypeArchetypeChunk)
                return false;
            if (methodSymbol.Name != Config.GetEnableBitMapPtr || methodSymbol.TypeArguments.Length != 1)
                return false;

            var componentType = methodSymbol.TypeArguments[0];
            int requiredIndex = _requiredComponentTypes.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, componentType));
            if (requiredIndex < 0)
                throw new InvalidOperationException(
                    $"GetEnableBitMapPtr<{componentType.ToDisplayString()}> 的类型不在 required 组件列表中。" +
                    "原生 job 内必须同时用 GetComponentDataNativeArray<T>() 访问该组件列，required 序号才能对齐。");

            expression = $"reinterpret_cast<unsigned long long*>(__chunkData->requiredEnableBitMaps[{requiredIndex}])";
            return true;
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
                _builder.Append(alias.ArrayName).Append("_ptr[");
                // 重新翻译索引（见 IndexSyntax 注释）：抄文本会漏出未声明的字段名/`this.`
                TranslateExpression(alias.IndexSyntax);
                _builder.Append(']');
                return;
            }

            base.TranslateIdentifier(identifier);
        }

        /// <summary>
        /// `arr.GetUnsafePtr()` / `arr.GetUnsafeReadOnlyPtr()`（chunk 数组局部）→ `arr_ptr`。
        /// 旧实现只认**字段**（`_nativeArrayListNames`）⇒ chunk 局部会原样输出 `arr.GetUnsafePtr()`
        /// 这种 C# 文本，生成物编译失败（独立验收 C25）。
        /// </summary>
        private bool TryTranslateChunkArrayUnsafePtr(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                return false;
            string methodName = memberAccess.Name.Identifier.Text;
            if (methodName != Config.GetUnsafePtr && methodName != "GetUnsafeReadOnlyPtr")
                return false;
            if (memberAccess.Expression is not IdentifierNameSyntax target)
                return false;
            if (!_chunkArrayLocalNames.Contains(target.Identifier.Text))
                return false;

            _builder.Append(target.Identifier.Text).Append("_ptr");
            return true;
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
                if (!TryBuildChunkArrayExpression(invocation, out var cppType, out var expression, out var storageKey))
                    return false;

                _chunkArrayLocalNames.Add(variable.Identifier.Text);
                _chunkArrayStorageKeys[variable.Identifier.Text] = storageKey;
                lines.Append(new string(' ', _indentLevel * 4));
                lines.Append("auto* ");
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
            if (TryTranslateChunkArrayUnsafePtr(invocation))
                return;

            if (TryBuildEnableBitMapExpression(invocation, out var bitmapExpression))
            {
                _builder.Append(bitmapExpression);
                return;
            }

            if (TryBuildChunkArrayExpression(invocation, out _, out var expression, out _))
            {
                _builder.Append(expression);
                return;
            }

            base.TranslateInvocation(invocation);
        }

        private bool TryBuildChunkArrayExpression(InvocationExpressionSyntax invocation, out string cppType, out string expression, out string storageKey)
        {
            cppType = "";
            expression = "";
            storageKey = "";

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
                storageKey = $"shared:{sharedIndex}";
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
            storageKey = $"component:{componentIndex}";
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
            // ★ 必须先预扫描：别名安全判定按"存储身份"统计写入，而局部名的登记是**惰性**的
            //   （翻译到那条声明时才有）。顺序不当就会漏掉"别名之后才声明 / 声明在嵌套块里"的
            //   同一分量列局部写的（独立验收 F1/F2：静默错值 4093/4093）。
            PreScanChunkArrayStorageKeys(block);

            foreach (var statement in block.Statements)
            {
                if (statement is not LocalDeclarationStatementSyntax localDecl)
                    continue;
                if (!TryGetNativeArrayElementAliasLocal(localDecl, out var aliasName, out var alias))
                    continue;
                if (!IsAliasSafe(block, aliasName, alias))
                    continue;

                _nativeArrayElementAliases[aliasName] = alias;
            }
        }

        /// <summary>
        /// 预扫描本块子树里所有 chunk 数组局部 → 存储身份（名字表 + 符号表），使别名安全判定
        /// **与翻译顺序无关**。外层 Execute 体的第一次调用即覆盖整个方法体 ⇒ 符号表完整。
        /// </summary>
        private void PreScanChunkArrayStorageKeys(BlockSyntax block)
        {
            foreach (var decl in block.DescendantNodesAndSelf().OfType<LocalDeclarationStatementSyntax>())
            {
                foreach (var variable in decl.Declaration.Variables)
                {
                    if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation)
                        continue;
                    string storageKey;
                    try
                    {
                        // 这里只做"识别 + 取键"：未登记在 required 列表里的分量类型仍由真正翻译时报错，
                        // 预扫描静默跳过（不能让错误提前到错误的位置/被吞掉）。
                        if (!TryBuildChunkArrayExpression(invocation, out _, out _, out storageKey))
                            continue;
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }

                    _chunkArrayStorageKeys[variable.Identifier.Text] = storageKey;
                    var symbol = _semanticModel.GetDeclaredSymbol(variable);
                    if (symbol != null)
                        _chunkArraySymbolStorageKeys[symbol] = storageKey;
                }
            }
        }

        /// <summary>
        /// NT-11（Critical）：`var e = arr[i];` 在 C# 里是**值拷贝**，别名成 `arr_ptr[i]` 只有在
        /// "别名作用域内 `arr[i]` 的值不会变"时才等价。旧判定只看 `block.Statements`（直接语句），
        /// 因此写在 `if` / `for` 体内的数组写看不见 ⇒ 别名读到被改写过的值（静默错值）。
        ///
        /// 这里按**整块（含嵌套语句 + 元素字段写）**判定，并且：
        ///   · 数组在本块内的写**只允许**是"读-改-写回"（`arr[i] = e;`）那一处
        ///     （按**存储身份**判定：同一分量列的另一个局部名、别名之后才声明的名字、嵌套块里的
        ///      名字、以及 `arr[i].Field = x` 这类元素字段写都算，见 <see cref="WritesAliasStorage"/>）；
        ///   · 索引表达式里用到的变量在本块内不得被改写（否则别名的求值时机从"声明处一次"
        ///     变成"每次使用"，副作用/取值都会重复或错位）；
        ///   · `e` 被改写却没有写回 ⇒ 别名会把改动泄漏进数组（C# 只改拷贝）。
        /// 任一条不满足就退回真拷贝（C# 语义，永远正确，只少一次优化）。
        /// </summary>
        private bool IsAliasSafe(BlockSyntax block, string aliasName, NativeArrayElementAlias alias)
        {
            bool writeBack = BlockContainsAliasWriteBack(block, aliasName, alias);
            int arrayWrites = CountAliasStorageWrites(block, alias);

            // 数组在本块内被改写：别名读到的是改写后的值，C# 读的是声明处的拷贝。
            if (arrayWrites != (writeBack ? 1 : 0))
                return false;

            if (alias.IndexIdentifiers.Count > 0 && BlockWritesAnyIdentifier(block, alias.IndexIdentifiers))
                return false;

            if (!writeBack && BlockWritesAnyIdentifier(block, new[] { aliasName }))
                return false;

            return true;
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

            // NT-11：索引表达式有副作用（`arr[cursor++]`、`arr[Next()]`…）时不得别名 ——
            // 别名会把索引的求值从"声明处一次"推迟到"每次使用"，副作用被重复执行。
            if (HasSideEffects(args[0].Expression))
                return false;

            // NT-11：属性 getter 本质是方法调用（可能每次取值不同/有副作用）⇒ 同样不得别名。
            if (ContainsPropertyAccess(args[0].Expression))
                return false;

            if (!TryResolveChunkStorageKey(arrayIdentifier, out var storageKey))
                return false;

            aliasName = variable.Identifier.Text;
            alias = new NativeArrayElementAlias
            {
                ArrayName = arrayIdentifier.Identifier.Text,
                StorageKey = storageKey,
                IndexExpression = NormalizeExpression(args[0].Expression),
                IndexSyntax = args[0].Expression
            };
            foreach (var id in args[0].Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                string n = id.Identifier.Text;
                if (!alias.IndexIdentifiers.Contains(n))
                    alias.IndexIdentifiers.Add(n);
            }
            return true;
        }

        /// <summary>索引表达式里是否有属性访问（属性 getter 是方法调用：每次取值可能不同、可能有副作用）。</summary>
        private bool ContainsPropertyAccess(ExpressionSyntax expression)
        {
            foreach (var node in expression.DescendantNodesAndSelf())
            {
                if (node is MemberAccessExpressionSyntax memberAccess
                    && _semanticModel.GetSymbolInfo(memberAccess).Symbol is IPropertySymbol)
                    return true;
                if (node is IdentifierNameSyntax identifier
                    && _semanticModel.GetSymbolInfo(identifier).Symbol is IPropertySymbol)
                    return true;
            }

            return false;
        }

        /// <summary>索引表达式是否含副作用（赋值 / ++ / -- / 调用 / 对象创建 / await）。</summary>
        private static bool HasSideEffects(ExpressionSyntax expression)
        {
            foreach (var node in expression.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case AssignmentExpressionSyntax:
                    case InvocationExpressionSyntax:
                    case ObjectCreationExpressionSyntax:
                    case Microsoft.CodeAnalysis.CSharp.Syntax.AwaitExpressionSyntax:
                        return true;
                    case PrefixUnaryExpressionSyntax prefix
                        when prefix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreIncrementExpression)
                          || prefix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreDecrementExpression):
                        return true;
                    case PostfixUnaryExpressionSyntax postfix
                        when postfix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostIncrementExpression)
                          || postfix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostDecrementExpression):
                        return true;
                }
            }
            return false;
        }

        private bool BlockContainsAliasWriteBack(BlockSyntax block, string aliasName, NativeArrayElementAlias alias)
        {
            // 逐节点（而不是只看 ExpressionStatement）：写回语句也可能嵌在表达式里。
            foreach (var node in block.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!node.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression))
                    continue;
                if (node.Right is not IdentifierNameSyntax right || right.Identifier.Text != aliasName)
                    continue;
                if (!TryGetElementAccessParts(node.Left, out var baseExpression, out var indexExpression))
                    continue;
                // 按存储身份比较：透过同一分量列的另一个局部名 / 直接调用写回同样成立（符号优先 ⇒ 遮蔽安全）。
                if (TryResolveElementBaseStorageKey(baseExpression, out var key)
                    && key == alias.StorageKey
                    && indexExpression == alias.IndexExpression)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 本块（含嵌套语句 + 任意表达式上下文）里是否改写了任一 <paramref name="identifiers"/>
        /// （赋值 / ++ / --）。NT-11：递归，且不能只看 ExpressionStatement。
        /// </summary>
        private static bool BlockWritesAnyIdentifier(BlockSyntax block, IReadOnlyList<string> identifiers)
        {
            foreach (var expression in block.DescendantNodes().OfType<ExpressionSyntax>())
            {
                foreach (var identifier in identifiers)
                {
                    if (ExpressionWritesIdentifier(expression, identifier))
                        return true;
                }
            }

            return false;
        }

        private static bool ExpressionWritesIdentifier(ExpressionSyntax expression, string identifier)
        {
            switch (expression)
            {
                case AssignmentExpressionSyntax assignment:
                    return ExpressionStartsWithIdentifier(assignment.Left, identifier);
                case PrefixUnaryExpressionSyntax prefix
                    when prefix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreIncrementExpression)
                      || prefix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreDecrementExpression):
                    return ExpressionStartsWithIdentifier(prefix.Operand, identifier);
                case PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostIncrementExpression)
                      || postfix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostDecrementExpression):
                    return ExpressionStartsWithIdentifier(postfix.Operand, identifier);
                default:
                    return false;
            }
        }

        /// <summary>
        /// 本块（含嵌套语句 + **任意表达式上下文**）里对**别名那块内存**的写次数。
        ///
        /// ⚠ 不能只看 <c>ExpressionStatementSyntax</c>：写可以嵌在任何表达式里 ——
        /// 调用实参 `Eat(arr[i] = x)`、局部声明初始化器 `var t = arr[i] = x`、
        /// `if`/`while`/`do` 条件、`for` 的初始化器/增量器、三元表达式、lambda、局部函数体……
        /// 逐节点扫赋值/++/-- 才完备（独立验收 C01–C10/C14/C32/C33/C39 实测漏判 ⇒ 静默错值）。
        /// 另外 `ref s[i]` / `arr.GetUnsafePtr()` / `ArrayElementAsRef(arr, i)` 会把该列**逃逸**成
        /// 引用/裸指针别名，之后透过它写的值同样改到这块内存 ⇒ 一律按"本块内被写"处理（保守）。
        /// </summary>
        private int CountAliasStorageWrites(BlockSyntax block, NativeArrayElementAlias alias)
        {
            int count = 0;
            foreach (var node in block.DescendantNodes())
            {
                switch (node)
                {
                    case AssignmentExpressionSyntax assignment:
                        if (TargetsAliasStorage(assignment.Left, alias))
                            count++;
                        break;
                    case PrefixUnaryExpressionSyntax prefix
                        when prefix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreIncrementExpression)
                          || prefix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreDecrementExpression):
                        if (TargetsAliasStorage(prefix.Operand, alias))
                            count++;
                        break;
                    case PostfixUnaryExpressionSyntax postfix
                        when postfix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostIncrementExpression)
                          || postfix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostDecrementExpression):
                        if (TargetsAliasStorage(postfix.Operand, alias))
                            count++;
                        break;
                    case RefExpressionSyntax refExpression:
                        // ref 别名：`ref s[i]` / `ref arr[i].Field`
                        if (TargetsAliasStorage(refExpression.Expression, alias))
                            count++;
                        break;
                    case InvocationExpressionSyntax invocation
                        when InvocationEscapesAliasStorage(invocation, alias):
                        count++;
                        break;
                }
            }

            return count;
        }

        /// <summary>
        /// 该调用的结果是否是指向别名那块内存的引用/裸指针（`arr.GetUnsafePtr()`、
        /// `UnsafeUtility.ArrayElementAsRef(arr, i)`…）。命中即视为"本块内该列被写"。
        /// </summary>
        private bool InvocationEscapesAliasStorage(InvocationExpressionSyntax invocation, NativeArrayElementAlias alias)
        {
            string name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
                GenericNameSyntax generic => generic.Identifier.Text,
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                _ => ""
            };
            switch (name)
            {
                case "GetUnsafePtr":
                case "GetUnsafeReadOnlyPtr":
                case "ArrayElementAsRef":
                case "GetRef":
                case "AsRef":
                    break;
                default:
                    return false;
            }

            // 实例调用：`arr.GetUnsafePtr()`
            if (invocation.Expression is MemberAccessExpressionSyntax receiverAccess
                && IdentifierTargetsAliasStorage(receiverAccess.Expression, alias))
                return true;

            // 静态调用：`UnsafeUtility.ArrayElementAsRef(arr, i)` —— 任一实参指向该列即可
            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                if (IdentifierTargetsAliasStorage(argument.Expression, alias))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 表达式是否**指向**别名那块内存：`arr`（裸标识符）或 `arr[i]` / `arr[i].Field`。
        /// 符号解析不出来时保守返回 true（宁可退回真拷贝）。
        /// </summary>
        private bool IdentifierTargetsAliasStorage(ExpressionSyntax expression, NativeArrayElementAlias alias)
        {
            if (expression is IdentifierNameSyntax identifier)
            {
                if (TryResolveChunkStorageKey(identifier, out var key))
                    return key == alias.StorageKey;
                return _semanticModel.GetSymbolInfo(identifier).Symbol == null;
            }

            return TargetsAliasStorage(expression, alias);
        }

        /// <summary>写目标是否落在别名那块内存上（`arr[i]` / `arr[i].Field` / `arr[i].Field.Sub`…）。</summary>
        private bool TargetsAliasStorage(ExpressionSyntax target, NativeArrayElementAlias alias)
        {
            if (!TryGetElementAccessParts(target, out var baseExpression, out _))
                return false;

            if (TryResolveElementBaseStorageKey(baseExpression, out var key))
                return key == alias.StorageKey;

            // 基名**解析不出来** ⇒ 保守当"同一块内存"；解析出来但不是分量列（普通数组/字段…）⇒ 不是。
            return baseExpression is IdentifierNameSyntax identifier
                   && _semanticModel.GetSymbolInfo(identifier).Symbol == null;
        }

        /// <summary>
        /// 元素访问的基表达式 → 存储身份。支持两类基：
        ///   · 局部名：`var arr = chunk.GetComponentDataNativeArray&lt;T&gt;(); arr[i]`
        ///   · **直接调用**：`chunk.GetComponentDataSpan&lt;T&gt;()[i] = x`（独立验收 C10：之前完全看不见这次写）
        /// </summary>
        private bool TryResolveElementBaseStorageKey(ExpressionSyntax baseExpression, out string storageKey)
        {
            if (baseExpression is IdentifierNameSyntax identifier)
                return TryResolveChunkStorageKey(identifier, out storageKey);

            if (baseExpression is InvocationExpressionSyntax invocation)
            {
                try
                {
                    return TryBuildChunkArrayExpression(invocation, out _, out _, out storageKey);
                }
                catch (InvalidOperationException)
                {
                    storageKey = "";
                    return false;
                }
            }

            storageKey = "";
            return false;
        }

        /// <summary>
        /// 从元素访问（可带成员访问链）解出基表达式与索引文本：`arr[i]`、`arr[i].Field`、
        /// `chunk.GetComponentDataSpan&lt;T&gt;()[i]` 都返回基表达式与索引文本。
        /// </summary>
        private static bool TryGetElementAccessParts(
            ExpressionSyntax expression, out ExpressionSyntax baseExpression, out string indexExpression)
        {
            baseExpression = null!;
            indexExpression = "";

            var target = expression;
            while (target is MemberAccessExpressionSyntax memberAccess)
                target = memberAccess.Expression;
            if (target is not ElementAccessExpressionSyntax elementAccess)
                return false;

            var args = elementAccess.ArgumentList.Arguments;
            if (args.Count != 1)
                return false;

            baseExpression = elementAccess.Expression;
            indexExpression = NormalizeExpression(args[0].Expression);
            return true;
        }

        /// <summary>
        /// 把标识符解析为 chunk 分量列的存储身份。**符号优先**（预扫描表）：只有符号解析不出来时才
        /// 退回名字表 —— 名字表在变量遮蔽（同名不同列）时会给出错误结论，不能用于"写回识别/写回省略"
        /// 这类会**省掉一条语句**的判定。
        /// </summary>
        private bool TryResolveChunkStorageKey(IdentifierNameSyntax identifier, out string storageKey)
        {
            var symbol = _semanticModel.GetSymbolInfo(identifier).Symbol;
            if (symbol != null && _chunkArraySymbolStorageKeys.TryGetValue(symbol, out storageKey))
                return true;
            if (symbol == null && _chunkArrayStorageKeys.TryGetValue(identifier.Identifier.Text, out storageKey))
                return true;

            storageKey = "";
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
            if (!TryGetElementAccessParts(assignment.Left, out var baseExpression, out var indexExpression))
                return false;

            // 与 BlockContainsAliasWriteBack 同口径：按**符号优先**的存储身份（同一分量列的另一个
            // 局部名也算写回；遮蔽时不会误判成写回而省掉一条语句）。
            return TryResolveElementBaseStorageKey(baseExpression, out var key)
                   && key == alias.StorageKey
                   && indexExpression == alias.IndexExpression;
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
