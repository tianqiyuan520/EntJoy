using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

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
        private readonly HashSet<string> _chunkArrayLocalNames = new();
        private readonly Dictionary<string, NativeArrayElementAlias> _nativeArrayElementAliases = new();

        public CppChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct, List<INamedTypeSymbol> requiredComponentTypes, bool useFastMath = false)
            : base(semanticModel, jobStruct, useFastMath)
        {
            _requiredComponentTypes = requiredComponentTypes;
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
            bool isSpan = localType?.Name == "Span" && localType.ContainingNamespace?.ToDisplayString() == "System";
            bool isNativeArray = localType != null && NativeTranspiler.IsEntJoyNativeContainerType(localType) && localType.Name == "NativeArray";
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
            if (methodSymbol.ContainingType?.ToDisplayString() != "EntJoy.ArchetypeChunk")
                return false;
            if (methodSymbol.Name != "GetComponentDataNativeArray" && methodSymbol.Name != "GetComponentDataSpan")
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
        // #pragma loop(ivdep) 强制 MSVC 消除指针别名保守性生成 SIMD。
        protected override void TranslateForStatement(ForStatementSyntax forStmt)
        {
            AppendIndent();
            _builder.AppendLine("#pragma loop(ivdep)");
            base.TranslateForStatement(forStmt);
        }

        // ——— 向量类型运算 ———
        // 不做 x()/y() 分量拆解，交由基类 CppStatementTranslator 直接生成
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
    }
}
