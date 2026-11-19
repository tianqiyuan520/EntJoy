using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public sealed class IspcChunkStatementTranslator : IspcStatementTranslator
    {
        private readonly HashSet<string> _chunkNativeArrayNames = new();
        private readonly Dictionary<string, string> _chunkNativeArrayLengthAliases = new();
        private readonly string? _foreachStartName;
        private readonly string? _foreachEndName;

        /// <summary>本次翻译发过串行循环：调用方要在函数体首部补 `if (programIndex != 0) return;`。</summary>
        public bool UsesSerialChunkLoop { get; private set; }

        public IspcChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct, bool useUniformVars = false)
            : base(semanticModel, jobStruct, null, false, useUniformVars)
        {
        }

        public IspcChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct, string foreachStartName, string foreachEndName, bool useUniformVars = false)
            : base(semanticModel, jobStruct, null, false, useUniformVars)
        {
            _foreachStartName = foreachStartName;
            _foreachEndName = foreachEndName;
        }

        protected override void TranslateLocalDeclaration(LocalDeclarationStatementSyntax declaration)
        {
            if (declaration.Declaration.Variables.All(variable => IsChunkNativeArrayInitializer(variable.Initializer?.Value)))
            {
                foreach (var variable in declaration.Declaration.Variables)
                {
                    _chunkNativeArrayNames.Add(variable.Identifier.ValueText);
                    _nativeArrayListNames.Add(variable.Identifier.ValueText);
                }
                return;
            }
            foreach (var variable in declaration.Declaration.Variables)
                if (TryGetChunkNativeArrayLengthAlias(variable.Initializer?.Value, out var array)
                    && CanDeclareUniformLocal(declaration, variable))
                    _chunkNativeArrayLengthAliases[variable.Identifier.ValueText] = array;
            base.TranslateLocalDeclaration(declaration);
        }

        protected override bool IsUniformArrayReceiver(IdentifierNameSyntax array)
            => _chunkNativeArrayNames.Contains(array.Identifier.ValueText) || base.IsUniformArrayReceiver(array);

        protected override void TranslateForStatement(ForStatementSyntax forStmt)
        {
            if (TryTranslateChunkArrayForEach(forStmt))
            {
                return;
            }
            if (!_insideForeach && UsesChunkView(forStmt))
            {
                AppendIndent();
                _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_ChunkLoopRequiresEntityLengthBound");
                return;
            }

            base.TranslateForStatement(forStmt);
        }

        /// <summary>
        /// 分量摊平：`T local = X[i]; …local 更新…; X[i] = local;` 且组件是单字段包装的
        /// float2/3/4 时，直接按标量槽展开，避免"一 lane 一结构体"的 gather。
        /// 不满足条件返回 false，调用方保持原来的 foreach_tiled。
        /// </summary>
        private bool TryEmitFlattenedSlotLoop(ForStatementSyntax forStmt,
            string startName, string endName)
        {
            // 形状：首句把 X[i] 读进局部，末句写回同一元素，中间的语句更新这个局部。
            // 中间语句条数不限，只要它们都是对同一元素的更新。
            if (forStmt.Statement is not BlockSyntax block || block.Statements.Count < 3) return false;
            if (block.Statements[0] is not LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } declaration)
                return false;
            var variable = declaration.Declaration.Variables[0];
            if (variable.Initializer?.Value is not ElementAccessExpressionSyntax load
                || load.Expression is not IdentifierNameSyntax loadArray
                || load.ArgumentList.Arguments.Count != 1) return false;
            if (block.Statements[block.Statements.Count - 1] is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax store }
                || !store.IsKind(SyntaxKind.SimpleAssignmentExpression)
                || store.Left is not ElementAccessExpressionSyntax storeTarget
                || storeTarget.Expression is not IdentifierNameSyntax storeArray
                || storeArray.Identifier.Text != loadArray.Identifier.Text
                || store.Right is not IdentifierNameSyntax storeValue
                || storeValue.Identifier.Text != variable.Identifier.Text) return false;

            string localName = variable.Identifier.Text;
            string arrayName = loadArray.Identifier.Text;

            // 首句读、末句写都必须是同一个循环下标：`T t = X[0]; …; X[i] = t;` 摊平后槽位对不上。
            var counter = _semanticModel.GetDeclaredSymbol(forStmt.Declaration.Variables[0]);
            bool IsCounter(ExpressionSyntax expression)
                => SymbolEqualityComparer.Default.Equals(_semanticModel.GetSymbolInfo(expression).Symbol, counter);
            if (!IsCounter(load.ArgumentList.Arguments[0].Expression)
                || !IsCounter(storeTarget.ArgumentList.Arguments[0].Expression)) return false;

            if (_semanticModel.GetTypeInfo(loadArray).Type is not INamedTypeSymbol arrayType
                || arrayType.TypeArguments.Length != 1
                || arrayType.TypeArguments[0] is not INamedTypeSymbol element) return false;
            var fields = element.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
            if (fields.Count != 1) return false;
            string typeName = fields[0].Type.Name;
            char arity = typeName.Length > 1 ? typeName[typeName.Length - 1] : '\0';
            string prefix = typeName.Length > 1 ? typeName.Substring(0, typeName.Length - 1) : typeName;
            int slots = arity is '2' or '3' or '4' ? arity - '0' : 0;
            if (slots == 0) return false;
            string scalarType = prefix == "float" ? "float"
                : prefix == "uint" ? "unsigned int"
                : prefix == "int" ? "int" : null;
            if (scalarType == null) return false;
            string fieldName = fields[0].Name;

            var middleStatements = block.Statements.Skip(1).Take(block.Statements.Count - 2).ToList();
            // 摊平后一个 slot 是标量：组件字段必须整体使用，写成 `position.Value.x` 这种再取分量就没法摊平。
            foreach (var access in middleStatements.SelectMany(s => s.DescendantNodes()).OfType<MemberAccessExpressionSyntax>())
                if (access.Name.Identifier.Text == fieldName && access.Parent is MemberAccessExpressionSyntax)
                    return false;

            // 本列必须就地下标访问；另一列允许 `[i + k]` 的常量偏移 stencil 读。
            // 别名判定只认「组件类型不同 ⇒ 必然不是同一列」，同类型跨列一律不摊（宁可退回 gather）。
            var stencilOffsets = new Dictionary<string, int>(System.StringComparer.Ordinal);
            foreach (var access in middleStatements.SelectMany(s => s.DescendantNodes()).OfType<ElementAccessExpressionSyntax>())
            {
                if (access.Expression is not IdentifierNameSyntax receiver) return false;
                if (access.ArgumentList.Arguments.Count != 1) return false;
                if (!TryGetCounterOffset(access.ArgumentList.Arguments[0].Expression, IsCounter, out int offset)) return false;
                if (_semanticModel.GetTypeInfo(receiver).Type is not INamedTypeSymbol arrayLike
                    || arrayLike.TypeArguments.Length != 1) return false;
                if (receiver.Identifier.Text == arrayName)
                {
                    if (offset != 0) return false;
                    continue;
                }
                if (offset == 0) continue;
                // 负偏移在零基循环里等价于读列首元素之前的内存：源程序本来就越界，不进快路径。
                if (offset < 0) return false;
                // 同类型跨列无法证明不是同一块内存（自引用 stencil 会跨 lane 竞争），一律不摊。
                if (SymbolEqualityComparer.Default.Equals(arrayLike.TypeArguments[0], element)) return false;
                if (stencilOffsets.TryGetValue(receiver.Identifier.Text, out int existing))
                {
                    if (existing != offset) return false;
                }
                else stencilOffsets[receiver.Identifier.Text] = offset;
            }
            foreach (var identifier in middleStatements.SelectMany(s => s.DescendantNodes()).OfType<IdentifierNameSyntax>())
            {
                string name = identifier.Identifier.Text;
                if (name == localName) continue;
                if (SymbolEqualityComparer.Default.Equals(_semanticModel.GetSymbolInfo(identifier).Symbol, counter)) continue;
                if (_semanticModel.GetTypeInfo(identifier).Type is not INamedTypeSymbol otherType
                    || otherType.TypeArguments.Length != 1
                    || otherType.TypeArguments[0] is not INamedTypeSymbol otherElement) continue;
                var otherFields = otherElement.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
                if (otherFields.Count != 1 || otherFields[0].Type.Name != typeName) return false;
            }

            int startLength = _builder.Length;
            foreach (var statement in middleStatements) TranslateStatement(statement);
            string middleCode = _builder.ToString(startLength, _builder.Length - startLength);
            _builder.Length = startLength;
            if (middleCode.Contains(UnsupportedMarkers.Stmt)) return false;

            var ptrPattern = new System.Text.RegularExpressions.Regex(
                @"([A-Za-z_]\w*)_ptr\[[^\]]*\]\." + fieldName);
            middleCode = ptrPattern.Replace(middleCode, m =>
            {
                string receiver = m.Groups[1].Value;
                int offset = stencilOffsets.TryGetValue(receiver + "_ptr", out int raw)
                    ? raw
                    : stencilOffsets.TryGetValue(receiver, out raw) ? raw : 0;
                string index = offset == 0 ? "__flat" : $"__flat + {offset * slots}";
                return $"(({scalarType}*){receiver}_ptr)[{index}]";
            });
            middleCode = System.Text.RegularExpressions.Regex.Replace(middleCode,
                @"\b" + System.Text.RegularExpressions.Regex.Escape(localName) + @"\s*\.\s*" + fieldName + @"\b",
                $"(({scalarType}*){arrayName}_ptr)[__flat]");
            if (!middleCode.Contains("__flat")) return false;

            _builder.Append("foreach_tiled (__flat = (").Append(startName).Append(") * ").Append(slots)
                .Append(" ... (").Append(endName).Append(") * ").Append(slots).AppendLine(")");
            AppendIndent();
            _builder.AppendLine("{");
            _indentLevel++;
            foreach (var line in middleCode.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
            {
                if (line.Length == 0) continue;
                AppendIndent();
                _builder.AppendLine(line);
            }
            _indentLevel--;
            AppendIndent();
            _builder.AppendLine("}");
            return true;
        }

        /// <summary>把下标表达式解析成「循环变量 + 常量偏移」。不是该形态返回 false。</summary>
        private bool TryGetCounterOffset(ExpressionSyntax expression, System.Func<ExpressionSyntax, bool> isCounter, out int offset)
        {
            offset = 0;
            if (isCounter(expression)) return true;
            if (expression is not BinaryExpressionSyntax binary) return false;
            bool subtract = binary.IsKind(SyntaxKind.SubtractExpression);
            if (!binary.IsKind(SyntaxKind.AddExpression) && !subtract) return false;
            if (isCounter(binary.Left))
            {
                if (_semanticModel.GetConstantValue(binary.Right).Value is not int right) return false;
                offset = subtract ? -right : right;
                return true;
            }
            if (subtract) return false;
            if (isCounter(binary.Right) && _semanticModel.GetConstantValue(binary.Left).Value is int left)
            {
                offset = left;
                return true;
            }
            return false;
        }

        protected override void TranslateWhileStatement(WhileStatementSyntax statement)        {
            if (!_insideForeach && UsesChunkView(statement))
                _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_ChunkLoopRequiresEntityLengthBound");
            else base.TranslateWhileStatement(statement);
        }

        protected override void TranslateDoStatement(DoStatementSyntax statement)
        {
            if (!_insideForeach && UsesChunkView(statement))
                _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_ChunkLoopRequiresEntityLengthBound");
            else base.TranslateDoStatement(statement);
        }

        private bool UsesChunkView(SyntaxNode statement) => statement.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
            .Any(access => access.Expression is IdentifierNameSyntax array && _chunkNativeArrayNames.Contains(array.Identifier.ValueText));

        protected override void TranslateExpressionStatement(ExpressionStatementSyntax statement)
        {
            if (!_insideForeach && statement.Expression is InvocationExpressionSyntax call && !IsScalarMetadataCall(call))
                _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_ChunkScalarCall");
            else base.TranslateExpressionStatement(statement);
        }

        protected override void TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            if (_semanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol maskMethod
                && maskMethod.ContainingType.ToDisplayString() == Config.TypeChunkEnabledMask && maskMethod.Name == "IsEnabled")
            {
                string index = CaptureExpressionText(invocation.ArgumentList.Arguments[0].Expression);
                _builder.Append("((__query_bits[(").Append(index).Append(") >> 6] & ((uint64)1 << ((")
                    .Append(index).Append(") & 63))) != 0)");
                return;
            }
            if (!_insideForeach && !IsScalarMetadataCall(invocation))
                _builder.Append($"/*{UnsupportedMarkers.Expr}ISPC_ChunkScalarCall*/");
            else base.TranslateInvocation(invocation);
        }

        private bool IsScalarMetadataCall(InvocationExpressionSyntax invocation)
        {
            if (_semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
            if (method.ContainingType.ToDisplayString() is "System.Math" or "System.MathF"
                || method.ContainingNamespace.ToDisplayString() == Config.NamespaceEntJoyMathematics) return true;
            return NativeTranspiler.IsEntJoyNativeContainerType(method.ContainingType)
                && method.Name is "GetUnsafePtr" or "GetUnsafeReadOnlyPtr";
        }

        protected override void TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            if (SymbolHelper.GetBoundSymbol(_semanticModel, memberAccess) is IPropertySymbol maskProperty
                && maskProperty.ContainingType.ToDisplayString() == Config.TypeChunkEnabledMask)
            {
                _builder.Append(maskProperty.Name == "Length" ? "__query_length"
                    : maskProperty.Name == "UlongCount" ? "((__query_length + 63) / 64)" : "__query_bits");
                return;
            }
            if (SymbolHelper.GetBoundSymbol(_semanticModel, memberAccess) is IPropertySymbol property
                && property.Name == "Count" && property.ContainingType.ToDisplayString() == Config.TypeArchetypeChunk)
            {
                _builder.Append("__entity_count");
                return;
            }
            if (memberAccess.Expression is IdentifierNameSyntax id &&
                _chunkNativeArrayNames.Contains(id.Identifier.Text) &&
                memberAccess.Name.Identifier.Text == "Length")
            {
                _builder.Append("__entity_count");
                return;
            }

            base.TranslateMemberAccess(memberAccess);
        }

        protected override void TranslateElementAccess(ElementAccessExpressionSyntax elementAccess)
        {
            if (elementAccess.Expression is IdentifierNameSyntax id && _chunkNativeArrayNames.Contains(id.Identifier.Text))
            {
                _builder.Append(id.Identifier.Text).Append("_ptr[");
                var args = elementAccess.ArgumentList.Arguments;
                if (args.Count > 0)
                {
                    TranslateExpression(args[0].Expression);
                }
                _builder.Append(']');
                return;
            }

            base.TranslateElementAccess(elementAccess);
        }

        private bool TryTranslateChunkArrayForEach(ForStatementSyntax forStmt)
        {
            if (forStmt.Declaration == null ||
                forStmt.Declaration.Variables.Count != 1 ||
                forStmt.Condition is not BinaryExpressionSyntax condition ||
                !condition.OperatorToken.IsKind(SyntaxKind.LessThanToken) ||
                condition.Left is not IdentifierNameSyntax conditionIndex ||
                !TryGetChunkNativeArrayLengthBound(condition.Right, out _, out int boundOffset))
            {
                return false;
            }

            string indexName = forStmt.Declaration.Variables[0].Identifier.Text;
            if (conditionIndex.Identifier.Text != indexName)
            {
                return false;
            }


            if (!IntForLoop.TryAnalyze(forStmt, _semanticModel, out var loop)
                || loop.Step != 1 || loop.Comparison != SyntaxKind.LessThanExpression
                || _semanticModel.GetConstantValue(loop.Start).Value is not int start || start != 0)
            {
                AppendIndent();
                _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_ChunkLoopRequiresZeroBasedUnitStride");
                return true;
            }
            string startName2 = _foreachStartName ?? "0";
            string endName2 = _foreachEndName ?? "__entity_count";
            if (boundOffset != 0) endName2 = $"({endName2}) - {boundOffset}";
            var counter = _semanticModel.GetDeclaredSymbol(forStmt.Declaration.Variables[0]);
            if (!IsLaneIndependent(forStmt, counter))
            {
                EmitSerialUniformLoop(forStmt, indexName, startName2, endName2);
                return true;
            }
            // 注：SendEvent 场景保持 foreach_tiled（SIMD）。
            // ISPC atomic_add_global 是 fetch-add，对 uniform 地址 + varying 值每个 active lane 独立原子，
            // 返回各自唯一旧值 → SIMD 下 per-lane 槽位分配正确（实测 PASS），无需退化为标量。
            AppendIndent();
            if (TryEmitFlattenedSlotLoop(forStmt, startName2, endName2)) return true;
            _builder.Append("foreach_tiled (").Append(indexName).Append(" = ").Append(startName2).Append(" ... ").Append(endName2).Append(") ");
            var savedForeach = _insideForeach;
            var savedBatch = _insideBatchIndexLoop;
            _insideBatchIndexLoop = true;
            _insideForeach = true;
            if (forStmt.Statement is BlockSyntax block)
            {
                _builder.AppendLine("{");
                _indentLevel++;
                foreach (var statement in block.Statements)
                {
                    TranslateStatement(statement);
                }
                _indentLevel--;
                AppendIndent();
                _builder.AppendLine("}");
            }
            else
            {
                _builder.AppendLine("{");
                _indentLevel++;
                TranslateStatement(forStmt.Statement);
                _indentLevel--;
                AppendIndent();
                _builder.AppendLine("}");
            }
            _insideForeach = savedForeach;
            _insideBatchIndexLoop = savedBatch;

            return true;
        }

        /// <summary>
        /// 能否「一个 lane 处理一个实体」：chunk 数组访问全部用循环变量、不在本层 break、
        /// 不写循环外的局部。任一不成立就退化为串行循环，而不是把整条循环丢掉。
        /// </summary>
        private bool IsLaneIndependent(ForStatementSyntax forStmt, ISymbol counter)
        {
            bool IsCounter(ExpressionSyntax expression)
                => SymbolEqualityComparer.Default.Equals(_semanticModel.GetSymbolInfo(expression).Symbol, counter);
            // 被写的列：stencil 读另一列时，只要读列不在写列里且组件类型不同，就还是「一 lane 一实体」。
            var writtenArrays = new HashSet<string>();
            foreach (var assignment in forStmt.Statement.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is ElementAccessExpressionSyntax target
                    && target.Expression is IdentifierNameSyntax owner) writtenArrays.Add(owner.Identifier.Text);
            }
            ITypeSymbol ElementOf(string array)
            {
                foreach (var identifier in forStmt.Statement.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    if (identifier.Identifier.Text != array) continue;
                    if (_semanticModel.GetTypeInfo(identifier).Type is INamedTypeSymbol named && named.TypeArguments.Length == 1)
                        return named.TypeArguments[0];
                }
                return null;
            }

            foreach (var access in forStmt.Statement.DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>())
            {
                if (access.Expression is not IdentifierNameSyntax array || !_chunkNativeArrayNames.Contains(array.Identifier.ValueText)) continue;
                ExpressionSyntax index = access.ArgumentList.Arguments[0].Expression;
                while (index is ParenthesizedExpressionSyntax parentheses) index = parentheses.Expression;
                if (IsCounter(index)) continue;
                if (!TryGetCounterOffset(index, IsCounter, out int offset)) return false;
                // 零基循环里 `[i-k]` 读列首之前的内存：源程序本来就越界，不按 lane 并行。
                if (offset < 0) return false;
                if (writtenArrays.Contains(array.Identifier.Text)) return false;
                ITypeSymbol readElement = ElementOf(array.Identifier.Text);
                if (readElement == null) return false;
                foreach (var written in writtenArrays)
                {
                    if (SymbolEqualityComparer.Default.Equals(readElement, ElementOf(written))) return false;
                }
            }
            if (forStmt.Statement.DescendantNodesAndSelf().OfType<BreakStatementSyntax>().Any(statement =>
                statement.Ancestors().FirstOrDefault(node => node is ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax) == forStmt))
                return false;
            var flow = _semanticModel.AnalyzeDataFlow(forStmt.Statement);
            return flow != null && flow.Succeeded && !flow.WrittenInside.OfType<ILocalSymbol>().Any(local =>
                !flow.VariablesDeclared.Contains(local, SymbolEqualityComparer.Default)
                && !_chunkNativeArrayNames.Contains(local.Name));
        }

        /// <summary>
        /// 逐实体串行循环 `for (uniform int i ...)`：跨索引（stencil）、break、循环外累加都成立，
        /// 但没有 SIMD 收益。chunk 元素是 varying struct，写入 uniform 定位必须用 uniform 局部
        /// （varying 值赋给 uniform 定位是 ISPC 编译错误，G297C951C），故循环体内按 uniform 翻译。
        /// 调用方据 <see cref="UsesSerialChunkLoop"/> 在函数体首部补 `if (programIndex != 0) return;`，
        /// 否则每个 lane 都会把整条循环跑一遍。
        /// </summary>
        private void EmitSerialUniformLoop(ForStatementSyntax forStmt, string indexName, string startName, string endName)
        {
            UsesSerialChunkLoop = true;
            AppendIndent();
            _builder.Append("for (uniform int ").Append(indexName).Append(" = ").Append(startName)
                .Append("; ").Append(indexName).Append(" < ").Append(endName)
                .Append("; ").Append(indexName).Append("++) ");
            var savedForeach = _insideForeach;
            var savedBatch = _insideBatchIndexLoop;
            var savedUniformVars = _useUniformVars;
            _insideForeach = false;
            _insideBatchIndexLoop = true;
            _useUniformVars = true;
            if (forStmt.Statement is BlockSyntax block)
            {
                _builder.AppendLine("{");
                _indentLevel++;
                foreach (var statement in block.Statements)
                    TranslateStatement(statement);
                _indentLevel--;
                AppendIndent();
                _builder.AppendLine("}");
            }
            else
            {
                _builder.AppendLine("{");
                _indentLevel++;
                TranslateStatement(forStmt.Statement);
                _indentLevel--;
                AppendIndent();
                _builder.AppendLine("}");
            }
            _insideForeach = savedForeach;
            _insideBatchIndexLoop = savedBatch;
            _useUniformVars = savedUniformVars;
        }

        private bool TryGetChunkNativeArrayLengthAlias(ExpressionSyntax? expression, out string arrayName)
        {
            arrayName = string.Empty;
            if (expression is MemberAccessExpressionSyntax countAccess
                && SymbolHelper.GetBoundSymbol(_semanticModel, countAccess) is IPropertySymbol count
                && count.Name == "Count" && count.ContainingType.ToDisplayString() == Config.TypeArchetypeChunk)
                return true;
            if (expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.Text == "Length" &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                _chunkNativeArrayNames.Contains(id.Identifier.Text))
            {
                arrayName = id.Identifier.Text;
                return true;
            }

            return false;
        }

        private bool TryGetChunkNativeArrayLengthBound(ExpressionSyntax expression, out string arrayName)
            => TryGetChunkNativeArrayLengthBound(expression, out arrayName, out _);

        /// <summary>循环上界：`X.Length` 或 `X.Length - k`。offset 是那个常量 k。</summary>
        private bool TryGetChunkNativeArrayLengthBound(ExpressionSyntax expression, out string arrayName, out int offset)
        {
            offset = 0;
            if (TryGetChunkNativeArrayLengthBoundCore(expression, out arrayName)) return true;
            if (expression is BinaryExpressionSyntax binary
                && binary.IsKind(SyntaxKind.SubtractExpression)
                && _semanticModel.GetConstantValue(binary.Right).Value is int right
                && TryGetChunkNativeArrayLengthBoundCore(binary.Left, out arrayName))
            {
                offset = right;
                return true;
            }
            arrayName = string.Empty;
            return false;
        }

        private bool TryGetChunkNativeArrayLengthBoundCore(ExpressionSyntax expression, out string arrayName)
        {
            if (TryGetChunkNativeArrayLengthAlias(expression, out arrayName))
            {
                return true;
            }

            if (expression is IdentifierNameSyntax id &&
                _chunkNativeArrayLengthAliases.TryGetValue(id.Identifier.Text, out arrayName))
            {
                return true;
            }

            arrayName = string.Empty;
            return false;
        }

        private bool IsChunkNativeArrayInitializer(ExpressionSyntax? expression)
        {
            if (expression is not InvocationExpressionSyntax invocation)
            {
                return false;
            }

            return _semanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol methodSymbol &&
                   methodSymbol.ContainingType?.ToDisplayString() == Config.TypeArchetypeChunk &&
                   Config.IsChunkArrayAccess(methodSymbol.Name);
        }
    }
}
