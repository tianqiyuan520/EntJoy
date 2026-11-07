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

        public IspcChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct)
            : base(semanticModel, jobStruct, null, false)
        {
        }

        public IspcChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct, string foreachStartName, string foreachEndName)
            : base(semanticModel, jobStruct, null, false)
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

        protected override void TranslateWhileStatement(WhileStatementSyntax statement)
        {
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
                !TryGetChunkNativeArrayLengthBound(condition.Right, out var arrayName))
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
            if (forStmt.Statement.DescendantNodesAndSelf().OfType<BreakStatementSyntax>().Any(statement =>
                statement.Ancestors().FirstOrDefault(node => node is ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax) == forStmt))
            {
                AppendIndent();
                _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_ChunkLoopBreak");
                return true;
            }
            var counter = _semanticModel.GetDeclaredSymbol(forStmt.Declaration.Variables[0]);
            foreach (var access in forStmt.Statement.DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>())
            {
                if (access.Expression is not IdentifierNameSyntax array || !_chunkNativeArrayNames.Contains(array.Identifier.ValueText)) continue;
                ExpressionSyntax index = access.ArgumentList.Arguments[0].Expression;
                while (index is ParenthesizedExpressionSyntax parentheses) index = parentheses.Expression;
                if (!SymbolEqualityComparer.Default.Equals(_semanticModel.GetSymbolInfo(index).Symbol, counter))
                {
                    AppendIndent();
                    _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_ChunkCrossIndexAccess");
                    return true;
                }
            }
            var flow = _semanticModel.AnalyzeDataFlow(forStmt.Statement);
            if (flow == null || !flow.Succeeded || flow.WrittenInside.OfType<ILocalSymbol>().Any(local =>
                !flow.VariablesDeclared.Contains(local, SymbolEqualityComparer.Default)
                && !_chunkNativeArrayNames.Contains(local.Name)))
            {
                AppendIndent();
                _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_ChunkOuterLocalWrite");
                return true;
            }
            // 注：SendEvent 场景保持 foreach_tiled（SIMD）。
            // ISPC atomic_add_global 是 fetch-add，对 uniform 地址 + varying 值每个 active lane 独立原子，
            // 返回各自唯一旧值 → SIMD 下 per-lane 槽位分配正确（实测 PASS），无需退化为标量。
            AppendIndent();
            string startName2 = _foreachStartName ?? "0";
            string endName2 = _foreachEndName ?? "__entity_count";
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
