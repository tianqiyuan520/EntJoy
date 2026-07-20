using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;

namespace NativeTranspiler.Analyzer
{
    public sealed class IspcChunkStatementTranslator : IspcStatementTranslator
    {
        private readonly HashSet<string> _chunkNativeArrayNames = new();
        private readonly Dictionary<string, string> _chunkNativeArrayLengthAliases = new();
        private readonly string? _foreachStartName;
        private readonly string? _foreachEndName;

        /// <summary>
        /// 跟踪从 ChunkNativeArray 元素初始化的局部 struct 变量。
        /// T var = positions[index] 模式的 var 会被映射到 positions_ptr[index] 的直接访问。
        /// Key: 局部变量名，Value: 完整的 ISPC 替换文本（如 "positions_ptr[index]"）
        /// </summary>
        private readonly Dictionary<string, string> _chunkProxyLocalVars = new();

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

        protected override void TranslateLocalDeclaration(LocalDeclarationStatementSyntax localDecl)
        {
            bool emittedAny = false;
            foreach (var variable in localDecl.Declaration.Variables)
            {
                if (IsChunkNativeArrayInitializer(variable.Initializer?.Value))
                {
                    _chunkNativeArrayNames.Add(variable.Identifier.Text);
                    _nativeArrayListNames.Add(variable.Identifier.Text);
                    continue;
                }

                if (TryGetChunkNativeArrayLengthAlias(variable.Initializer?.Value, out var arrayName))
                {
                    _chunkNativeArrayLengthAliases[variable.Identifier.Text] = arrayName;
                }

                // 检测 T var = positions[index] 模式，将 var 标记为数组代理
                // 后续 var.field 的访问将被翻译为 positions_ptr[index].field
                // 从而消除 struct copy-in/copy-out 开销
                if (IsChunkNativeArrayElementAccess(variable.Initializer?.Value, out var proxyArrayName, out var proxyIndexExpr))
                {
                    string indexText = CaptureExpressionText(proxyIndexExpr);
                    _chunkProxyLocalVars[variable.Identifier.Text] = $"{proxyArrayName}_ptr[{indexText}]";
                    continue; // 跳过此局部变量的声明和拷贝
                }

                if (!emittedAny)
                {
                    AppendIndent();
                    var type = _semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type!);
                    _builder.Append(ToIspcTypeName(cppType));
                    emittedAny = true;
                }
                else
                {
                    _builder.Append(", ");
                }

                _builder.Append(' ').Append(variable.Identifier.Text);
                if (variable.Initializer != null)
                {
                    _builder.Append(" = ");
                    TranslateExpression(variable.Initializer.Value);
                }
            }

            if (emittedAny)
            {
                _builder.AppendLine(";");
            }
        }

        protected override void TranslateForStatement(ForStatementSyntax forStmt)
        {
            if (TryTranslateChunkArrayForEach(forStmt))
            {
                return;
            }

            base.TranslateForStatement(forStmt);
        }

        protected override void TranslateIdentifier(IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.Text;
            // 结构体代理变量：直接替换为 array_ptr[index] 消除 struct copy
            if (_chunkProxyLocalVars.TryGetValue(name, out string proxyReplacement))
            {
                _builder.Append(proxyReplacement);
                return;
            }
            base.TranslateIdentifier(identifier);
        }

        protected override void TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is IdentifierNameSyntax id &&
                _chunkNativeArrayNames.Contains(id.Identifier.Text) &&
                memberAccess.Name.Identifier.Text == "Length")
            {
                _builder.Append(id.Identifier.Text).Append("_length");
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

        protected override void TranslateExpressionStatement(ExpressionStatementSyntax exprStmt)
        {
            // 检测并跳过冗余的 struct writeback 语句: positions[index] = position;
            if (exprStmt.Expression is AssignmentExpressionSyntax assignment &&
                assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                assignment.Right is IdentifierNameSyntax rightId &&
                _chunkProxyLocalVars.ContainsKey(rightId.Identifier.Text) &&
                assignment.Left is ElementAccessExpressionSyntax leftElem &&
                leftElem.Expression is IdentifierNameSyntax leftArrayId &&
                _chunkNativeArrayNames.Contains(leftArrayId.Identifier.Text))
            {
                if (_chunkProxyLocalVars.TryGetValue(rightId.Identifier.Text, out string proxyReplacement) &&
                    proxyReplacement.StartsWith(leftArrayId.Identifier.Text + "_ptr["))
                {
                    return; // 跳过整个语句（不 emit 分号）
                }
            }
            base.TranslateExpressionStatement(exprStmt);
        }

        protected override void TranslateAssignment(AssignmentExpressionSyntax assignment)
        {
            // 检测并消除 struct writeback 模式: nativeArray[index] = proxyVar
            // 因为 proxyVar 的所有 per-field 写入已经通过 TranslateIdentifier 重定向直接写入了数组，
            // 这里的整 struct 回写是冗余拷贝，可以安全跳过。
            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                assignment.Right is IdentifierNameSyntax rightId &&
                _chunkProxyLocalVars.ContainsKey(rightId.Identifier.Text) &&
                assignment.Left is ElementAccessExpressionSyntax leftElem &&
                leftElem.Expression is IdentifierNameSyntax leftArrayId &&
                _chunkNativeArrayNames.Contains(leftArrayId.Identifier.Text))
            {
                // 确认代理变量对应的数组与 LHS 数组匹配（避免误跳过 cross-array 赋值）
                if (_chunkProxyLocalVars.TryGetValue(rightId.Identifier.Text, out string proxyReplacement) &&
                    proxyReplacement.StartsWith(leftArrayId.Identifier.Text + "_ptr["))
                {
                    return; // 跳过冗余的 struct writeback
                }
            }
            base.TranslateAssignment(assignment);
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

            AppendIndent();
            string startName = _foreachStartName ?? "0";
            string endName = _foreachEndName ?? $"{arrayName}_length";
            _builder.Append("foreach (").Append(indexName).Append(" = ").Append(startName).Append(" ... ").Append(endName).Append(") ");
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

            return true;
        }

        private bool TryGetChunkNativeArrayLengthAlias(ExpressionSyntax? expression, out string arrayName)
        {
            arrayName = string.Empty;
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

        /// <summary>
        /// 将表达式翻译为 ISPC 文本并返回，不影响 _builder 的当前状态。
        /// </summary>
        private string CaptureExpressionText(ExpressionSyntax expr)
        {
            int savedLen = _builder.Length;
            TranslateExpression(expr);
            string text = _builder.ToString(savedLen, _builder.Length - savedLen);
            _builder.Length = savedLen;
            return text;
        }

        /// <summary>
        /// 检测表达式是否为 ChunkNativeArray 的元素访问（如 positions[index]）。
        /// 用于 struct proxy 模式的检测。
        /// </summary>
        private bool IsChunkNativeArrayElementAccess(ExpressionSyntax? expression, out string arrayName, out ExpressionSyntax indexExpr)
        {
            arrayName = string.Empty;
            indexExpr = null!;
            if (expression is ElementAccessExpressionSyntax elemAccess &&
                elemAccess.Expression is IdentifierNameSyntax id &&
                _chunkNativeArrayNames.Contains(id.Identifier.Text) &&
                elemAccess.ArgumentList.Arguments.Count >= 1)
            {
                arrayName = id.Identifier.Text;
                indexExpr = elemAccess.ArgumentList.Arguments[0].Expression;
                return true;
            }
            return false;
        }

        private bool IsChunkNativeArrayInitializer(ExpressionSyntax? expression)
        {
            if (expression is not InvocationExpressionSyntax invocation)
            {
                return false;
            }

            return _semanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol methodSymbol &&
                   methodSymbol.ContainingType?.ToDisplayString() == "EntJoy.ArchetypeChunk" &&
                   methodSymbol.Name == "GetComponentDataNativeArray";
        }

        private static string ToIspcTypeName(string cppType)
        {
            return cppType switch
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
        }
    }
}
