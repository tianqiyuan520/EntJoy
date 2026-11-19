// 实体内核的摊平形态：从语法树 + 语义模型判定，不看生成文本。
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 摊平所需的形态：单字段 float/int/uint 向量包装，或 N 个同类型标量字段各一条同构语句。
    /// 判定只依赖语法与符号：语句都是赋值、无控制流、无数组访问、无副作用调用，
    /// 且读写的都是组件参数的那个字段。
    /// </summary>
    internal sealed class FlatShape
    {
        public bool ScalarFields;
        public int Slots;
        public string ScalarType;
        public string FieldName;
        public List<ExpressionStatementSyntax> Statements;
        /// <summary>标量字段形态：按槽收集语句里的数值常量（与 Statements[0] 的出现顺序对齐）。</summary>
        public List<List<LiteralExpressionSyntax>> LiteralsPerSlot;

        public static FlatShape TryAnalyze(BlockSyntax body, IMethodSymbol execute, SemanticModel model)
        {
            var components = execute.Parameters
                .Where(p => !NativeTranspiler.IsEntityType(p.Type)
                    && !(NativeTranspileValidator.IsRelationComponentType(p.Type) && p.RefKind == RefKind.None))
                .ToList();
            if (components.Count == 0) return null;
            if (components.Any(p => p.Type.GetMembers().OfType<IFieldSymbol>().Any(f => !f.IsStatic && !f.Type.IsValueType)))
                return null;

            string vectorField = null, vectorElement = null, scalarType = null;
            int vectorSlots = 0, scalarSlots = 0;
            bool canVector = true, canScalar = true;
            foreach (var parameter in components)
            {
                var fields = parameter.Type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
                string element = null;
                int arity = fields.Count == 1 ? VectorArity(fields[0].Type.Name, out element) : 0;
                if (arity == 0) canVector = false;
                else if (vectorField == null) { vectorField = fields[0].Name; vectorSlots = arity; vectorElement = element; }
                else if (vectorField != fields[0].Name || vectorSlots != arity || vectorElement != element) canVector = false;

                if (fields.Count < 2) canScalar = false;
                else if (canScalar)
                {
                    string scalarFieldType = ScalarElement(fields[0].Type);
                    if (scalarFieldType == null || fields.Any(f => ScalarElement(f.Type) != scalarFieldType)) canScalar = false;
                    else if (scalarType == null) { scalarType = scalarFieldType; scalarSlots = fields.Count; }
                    else if (scalarType != scalarFieldType || scalarSlots != fields.Count) canScalar = false;
                }
            }
            if (!canVector && !canScalar) return null;

            var statements = new List<ExpressionStatementSyntax>();
            foreach (var statement in body.Statements)
            {
                if (statement is not ExpressionStatementSyntax expressionStatement
                    || expressionStatement.Expression is not AssignmentExpressionSyntax assignment) return null;
                if (!IsSimpleCompoundAssignment(assignment.Kind())) return null;
                if (!IsPureStatement(statement, model)) return null;
                statements.Add(expressionStatement);
            }
            if (statements.Count == 0) return null;

            if (canVector && StatementsTargetVectorField(statements, components, vectorField, model))
            {
                return new FlatShape
                {
                    ScalarFields = false,
                    Slots = vectorSlots,
                    ScalarType = vectorElement,
                    FieldName = vectorField,
                    Statements = statements,
                };
            }
            if (!canScalar) return null;
            return TryAnalyzeScalarFields(statements, components, scalarSlots, scalarType, model);
        }

        private static FlatShape TryAnalyzeScalarFields(List<ExpressionStatementSyntax> statements,
            List<IParameterSymbol> components, int slots, string scalarType, SemanticModel model)
        {
            if (statements.Count != slots) return null;
            string canonical = null;
            var seen = new HashSet<int>();
            var literals = new List<List<LiteralExpressionSyntax>>();
            while (literals.Count < slots) literals.Add(null);
            foreach (var statement in statements)
            {
                if (!TryGetScalarFieldIndex(statement, components, model, out int index)) return null;
                if (index >= slots || !seen.Add(index)) return null;
                string text = Canonicalize(statement, components, model, out var statementLiterals);
                if (canonical == null) canonical = text;
                else if (canonical != text) return null;
                literals[index] = statementLiterals;
            }
            if (seen.Count != slots) return null;
            return new FlatShape
            {
                ScalarFields = true,
                Slots = slots,
                ScalarType = scalarType,
                Statements = statements,
                LiteralsPerSlot = literals,
            };
        }

        /// <summary>所有赋值目标都是可写组件参数的同一个字段，读也只碰这些字段。</summary>
        private static bool StatementsTargetVectorField(List<ExpressionStatementSyntax> statements,
            List<IParameterSymbol> components, string fieldName, SemanticModel model)
        {
            foreach (var statement in statements)
            {
                var assignment = (AssignmentExpressionSyntax)statement.Expression;
                if (assignment.Left is not MemberAccessExpressionSyntax target
                    || target.Expression is not IdentifierNameSyntax targetParameter
                    || target.Name.Identifier.Text != fieldName
                    || !IsWritableComponent(targetParameter, components, model)) return false;

                foreach (var node in statement.DescendantNodes())
                {
                    if (node is MemberAccessExpressionSyntax access)
                    {
                        if (access.Expression is IdentifierNameSyntax id && IsComponent(id, components, model))
                        {
                            if (access.Name.Identifier.Text != fieldName) return false;
                            if (access.Parent is MemberAccessExpressionSyntax) return false;
                            continue;
                        }
                        if (access.Expression is ThisExpressionSyntax) continue;
                        return false;
                    }
                    if (node is IdentifierNameSyntax identifier
                        && IsComponent(identifier, components, model)
                        && identifier.Parent is not MemberAccessExpressionSyntax) return false;
                }
            }
            return true;
        }

        private static bool TryGetScalarFieldIndex(ExpressionStatementSyntax statement,
            List<IParameterSymbol> components, SemanticModel model, out int index)
        {
            index = -1;
            var assignment = (AssignmentExpressionSyntax)statement.Expression;
            if (assignment.Left is not MemberAccessExpressionSyntax target
                || target.Expression is not IdentifierNameSyntax targetParameter
                || !IsWritableComponent(targetParameter, components, model)) return false;

            foreach (var node in statement.DescendantNodes())
            {
                if (node is MemberAccessExpressionSyntax access)
                {
                    if (access.Expression is IdentifierNameSyntax id && IsComponent(id, components, model))
                    {
                        if (access.Parent is MemberAccessExpressionSyntax) return false;
                        int fieldIndex = FieldIndex(id, access.Name.Identifier.Text, model);
                        if (fieldIndex < 0) return false;
                        if (index == -1) index = fieldIndex;
                        else if (index != fieldIndex) return false;
                        continue;
                    }
                    if (access.Expression is ThisExpressionSyntax) continue;
                    return false;
                }
                if (node is IdentifierNameSyntax identifier
                    && IsComponent(identifier, components, model)
                    && identifier.Parent is not MemberAccessExpressionSyntax) return false;
            }
            return index >= 0;
        }

        /// <summary>
        /// 语句的规范化文本：组件字段标识符换成 <c>#</c>、数值常量换成 <c>@</c>，其余按 token 原文。
        /// 同时按出现顺序收集数值常量节点，供摊平时按槽取不同常量。
        /// </summary>
        private static string Canonicalize(ExpressionStatementSyntax statement, List<IParameterSymbol> components,
            SemanticModel model, out List<LiteralExpressionSyntax> literals)
        {
            literals = new List<LiteralExpressionSyntax>();
            var canonical = new StringBuilder();
            foreach (var token in statement.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.NumericLiteralToken))
                {
                    canonical.Append('@');
                    if (token.Parent is LiteralExpressionSyntax literal) literals.Add(literal);
                    continue;
                }
                if (token.IsKind(SyntaxKind.IdentifierToken)
                    && token.Parent is IdentifierNameSyntax identifier
                    && model.GetSymbolInfo(identifier).Symbol is IFieldSymbol field
                    && components.Any(p => SymbolEqualityComparer.Default.Equals(field.ContainingType, p.Type)))
                {
                    canonical.Append('#');
                    continue;
                }
                canonical.Append(token.Text);
            }
            return canonical.ToString();
        }

        /// <summary>只放行纯值语句：赋值、字段访问、算术/位运算、逻辑与条件表达式。</summary>
        private static bool IsPureStatement(StatementSyntax statement, SemanticModel model)
        {
            foreach (var node in statement.DescendantNodesAndSelf())
            {
                switch (node.Kind())
                {
                    case SyntaxKind.IfStatement:
                    case SyntaxKind.ForStatement:
                    case SyntaxKind.ForEachStatement:
                    case SyntaxKind.WhileStatement:
                    case SyntaxKind.DoStatement:
                    case SyntaxKind.SwitchStatement:
                    case SyntaxKind.TryStatement:
                    case SyntaxKind.ReturnStatement:
                    case SyntaxKind.BreakStatement:
                    case SyntaxKind.ContinueStatement:
                    case SyntaxKind.ThrowStatement:
                    case SyntaxKind.GotoStatement:
                    case SyntaxKind.YieldReturnStatement:
                    case SyntaxKind.ElementAccessExpression:
                    case SyntaxKind.ObjectCreationExpression:
                    case SyntaxKind.AwaitExpression:
                    case SyntaxKind.SimpleLambdaExpression:
                    case SyntaxKind.ParenthesizedLambdaExpression:
                    case SyntaxKind.ConditionalAccessExpression:
                    case SyntaxKind.RefExpression:
                        return false;
                    case SyntaxKind.InvocationExpression:
                        if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol method || !IsPureMathCall(method)) return false;
                        break;
                }
            }
            return true;
        }

        private static bool IsPureMathCall(IMethodSymbol method)
        {
            if (!method.IsStatic) return false;
            string type = method.ContainingType?.ToDisplayString() ?? "";
            return type is "System.Math" or "System.MathF" or "EntJoy.Mathematics.math";
        }

        private static bool IsComponent(IdentifierNameSyntax identifier, List<IParameterSymbol> components,
            SemanticModel model)
            => model.GetSymbolInfo(identifier).Symbol is IParameterSymbol parameter && components.Contains(parameter);

        private static bool IsWritableComponent(IdentifierNameSyntax identifier, List<IParameterSymbol> components,
            SemanticModel model)
            => model.GetSymbolInfo(identifier).Symbol is IParameterSymbol parameter
                && components.Contains(parameter)
                && parameter.RefKind is RefKind.Ref or RefKind.Out;

        private static int FieldIndex(IdentifierNameSyntax parameter, string fieldName, SemanticModel model)
        {
            if (model.GetSymbolInfo(parameter).Symbol is not IParameterSymbol symbol) return -1;
            var fields = symbol.Type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
            for (int i = 0; i < fields.Count; i++)
                if (fields[i].Name == fieldName) return i;
            return -1;
        }

        private static bool IsSimpleCompoundAssignment(SyntaxKind kind)
            => kind is SyntaxKind.SimpleAssignmentExpression
                or SyntaxKind.AddAssignmentExpression
                or SyntaxKind.SubtractAssignmentExpression
                or SyntaxKind.MultiplyAssignmentExpression
                or SyntaxKind.DivideAssignmentExpression
                or SyntaxKind.ModuloAssignmentExpression
                or SyntaxKind.AndAssignmentExpression
                or SyntaxKind.OrAssignmentExpression
                or SyntaxKind.ExclusiveOrAssignmentExpression;

        /// <summary>
        /// 单字段向量组件的布局：字段名、标量元素名、槽数。不是该形态则返回 false。
        /// </summary>
        internal static bool TryGetVectorField(INamedTypeSymbol type, out string fieldName, out string scalarType, out int slots)
        {
            fieldName = null;
            scalarType = null;
            slots = 0;
            var fields = type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
            if (fields.Count != 1) return false;
            slots = VectorArity(fields[0].Type.Name, out scalarType);
            if (slots == 0) return false;
            fieldName = fields[0].Name;
            return true;
        }

        /// <summary>
        /// N 个同类型标量字段的组件布局：字段名按声明顺序、标量元素名、槽数。不是该形态则返回 false。
        /// </summary>
        internal static bool TryGetScalarFields(INamedTypeSymbol type, out List<string> fieldNames,
            out string scalarType, out int slots)
        {
            fieldNames = null;
            scalarType = null;
            slots = 0;
            var fields = type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
            if (fields.Count < 2) return false;
            string element = ScalarElement(fields[0].Type);
            if (element == null || fields.Any(f => ScalarElement(f.Type) != element)) return false;
            fieldNames = fields.Select(f => f.Name).ToList();
            scalarType = element;
            slots = fields.Count;
            return true;
        }

        private static int VectorArity(string typeName, out string element)
        {
            element = null;
            if (typeName.Length < 2) return 0;
            char arity = typeName[typeName.Length - 1];
            if (arity is not ('2' or '3' or '4')) return 0;
            element = typeName.Substring(0, typeName.Length - 1) switch
            {
                "float" => "float",
                "int" => "int",
                "uint" => "unsigned int",
                _ => null,
            };
            return element == null ? 0 : arity - '0';
        }

        private static string ScalarElement(ITypeSymbol type)
            => type.SpecialType switch
            {
                SpecialType.System_Single => "float",
                SpecialType.System_Int32 => "int",
                SpecialType.System_UInt32 => "unsigned int",
                _ => null,
            };
    }
}

