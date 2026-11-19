using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NativeTranspiler.Analyzer.Common;
using System;

namespace NativeTranspiler.Analyzer
{
    public static partial class CppJobGenerator
    {

        // IJobChunk Auto-SIMD: Preprocess AST

        /// <summary>
        /// 预处理 IJobChunk 的 Execute AST 用于 SIMD 生成：
        /// 1. 收集 chunk 数组声明（GetComponentDataNativeArray/GetComponentDataSpan）
        /// 3. SyntaxRewriter: 删除 chunk 数组声明、删除实体 for-loop 头、替换 chunk.Count
        /// </summary>
        private static (List<(string name, string elemType, int compIndex)> chunkArrays,
                        string entityLoopIv,
                        BlockSyntax modifiedBody)
            PreprocessIJobChunkAST(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel,
                                   INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var chunkArrays = new List<(string name, string elemType, int compIndex)>();
            var chunkArrayNames = new HashSet<string>();
            string chunkParamName = methodSyntax.ParameterList.Parameters.Count > 0
                ? methodSyntax.ParameterList.Parameters[0].Identifier.Text
                : "chunk";

            // 1. Scan for GetComponentDataNativeArray / GetComponentDataSpan calls
            var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
            foreach (var localDecl in methodSyntax.Body?.Statements.OfType<LocalDeclarationStatementSyntax>() ?? Enumerable.Empty<LocalDeclarationStatementSyntax>())
            {
                foreach (var variable in localDecl.Declaration.Variables)
                {
                    if (variable.Initializer?.Value is InvocationExpressionSyntax inv)
                    {
                        var symbol = semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                        if (symbol != null &&
                            symbol.ContainingType?.ToDisplayString() == Config.TypeArchetypeChunk &&
                            Config.IsChunkArrayAccess(symbol.Name) &&
                            symbol.TypeArguments.Length > 0)
                        {
                            string varName = variable.Identifier.Text;
                            var compType = symbol.TypeArguments[0] as INamedTypeSymbol;
                            if (compType != null)
                            {
                                int idx = requiredTypes.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, compType));
                                string elemCppType = NativeTranspiler.MapCSharpTypeToCpp(compType);
                                if (Config.IsReadOnlyChunkArrayAccess(symbol.Name)) elemCppType = "const " + elemCppType;
                                chunkArrays.Add((varName, elemCppType, idx));
                                chunkArrayNames.Add(varName);
                            }
                        }
                    }
                }
            }

            // 2. Find entity for-loop
            if (methodSyntax.Body?.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier =>
                semanticModel.GetSymbolInfo(identifier).Symbol is IParameterSymbol parameter
                && parameter.Type.ToDisplayString() == Config.TypeChunkEnabledMask) == true)
                return (chunkArrays, "", methodSyntax.Body);
            string entityLoopIv = "";
            ForStatementSyntax? entityLoop = null;
            if (methodSyntax.Body != null)
            {
                foreach (var stmt in methodSyntax.Body.Statements)
                {
                    if (stmt is ForStatementSyntax forStmt &&
                        forStmt.Declaration?.Variables.Count == 1)
                    {
                        var decl = forStmt.Declaration.Variables[0];
                        string ivName = decl.Identifier.Text;

                        // Check if this loop's body has element access on chunk arrays
                        bool hasChunkAccess = forStmt.DescendantNodes()
                            .OfType<ElementAccessExpressionSyntax>()
                            .Any(ea => ea.Expression is IdentifierNameSyntax id
                                      && chunkArrayNames.Contains(id.Identifier.Text));

                        if (hasChunkAccess)
                        {
                            entityLoopIv = ivName;
                            entityLoop = forStmt;
                            break;
                        }
                    }
                }
            }

            if (entityLoop == null || !CanVectorizeChunkEntityLoop(methodSyntax, entityLoop, semanticModel, chunkArrays, chunkParamName))
                return (chunkArrays, "", methodSyntax.Body!);

            var origins = SourceSyntax.Annotate(methodSyntax.Body!);
            var names = entityLoop.Statement.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>()
                .Select(variable => (Symbol: semanticModel.GetDeclaredSymbol(variable), Name: $"__ej_{variable.Identifier.ValueText}_{variable.SpanStart}"))
                .Where(local => local.Symbol != null).ToDictionary(local => local.Symbol!, local => local.Name, SymbolEqualityComparer.Default);
            var renamed = (BlockSyntax)new ChunkLocalNameRewriter(semanticModel, names).Visit(origins)!;

            // 3. Apply SyntaxRewriter (remove chunk decls, for-loop header, replace chunk.Count)
            var rewriter = new IJobChunkSimdRewriter(chunkArrayNames, chunkParamName, entityLoopIv);
            var afterFirstPass = methodSyntax.Body != null
                ? (BlockSyntax)rewriter.Visit(renamed)
                : methodSyntax.Body;

            // 4. 组件副本保持为局部字段值，在源写回位置提交。
            var decomposedBody = DecomposeStructLocals(afterFirstPass!, chunkArrayNames, semanticModel);

            return (chunkArrays, entityLoopIv, decomposedBody);
        }

        // 实体间独立：完整单位步长遍历，仅当前实体的组件列访问；其它逐 Chunk 副作用保留标量入口。
        private static bool CanVectorizeChunkEntityLoop(MethodDeclarationSyntax method, ForStatementSyntax loop,
            SemanticModel model, List<(string name, string elemType, int compIndex)> arrays, string chunkName)
        {
            var columns = arrays.ToDictionary(array => array.name, array => array.compIndex);
            if (!IntForLoop.TryAnalyze(loop, model, out var control) || control.Step != 1
                || control.Comparison != SyntaxKind.LessThanExpression || model.GetConstantValue(control.Start).Value is not int start || start != 0)
                return false;
            if (control.Bound is not MemberAccessExpressionSyntax bound || bound.Expression is not IdentifierNameSyntax owner
                || !(bound.Name.Identifier.Text == "Length" && columns.ContainsKey(owner.Identifier.Text)
                    || bound.Name.Identifier.Text == "Count" && owner.Identifier.Text == chunkName)) return false;
            if (method.Body!.Statements.Any(statement => statement != loop
                && !(statement is LocalDeclarationStatementSyntax declaration
                    && declaration.Declaration.Variables.All(variable => columns.ContainsKey(variable.Identifier.Text))))) return false;

            var counter = model.GetDeclaredSymbol(loop.Declaration!.Variables[0]);
            var nodes = loop.Statement.DescendantNodesAndSelf().ToArray();
            var jobType = model.GetDeclaredSymbol(method)?.ContainingType;
            bool WritesJob(ExpressionSyntax target) => target.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(expression =>
                model.GetSymbolInfo(expression).Symbol is IFieldSymbol field && SymbolEqualityComparer.Default.Equals(field.ContainingType, jobType));
            if (nodes.OfType<AssignmentExpressionSyntax>().Any(assignment => WritesJob(assignment.Left))) return false;
            foreach (var increment in nodes.OfType<ExpressionSyntax>().Where(expression => expression.IsKind(SyntaxKind.PreIncrementExpression)
                || expression.IsKind(SyntaxKind.PreDecrementExpression) || expression.IsKind(SyntaxKind.PostIncrementExpression) || expression.IsKind(SyntaxKind.PostDecrementExpression)))
                if (WritesJob(increment)) return false;
            foreach (var member in nodes.OfType<MemberAccessExpressionSyntax>())
            {
                if (model.GetSymbolInfo(member).Symbol is not IFieldSymbol field || field.IsStatic
                    || SymbolEqualityComparer.Default.Equals(field.ContainingType, jobType)) continue;
                string type = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                // 标量与 2 分量向量（float2/int2/uint2）可向量化；uint2 与 int2 共用寄存器类型。
                if (type != "int" && type != "unsigned int" && type != "float"
                    && !SimdVectorTypes.IsPair(type)) return false;
            }
            foreach (var inner in nodes.OfType<ForStatementSyntax>())
                if (!IntForLoop.TryAnalyze(inner, model, out var controlFlow)
                    || model.GetTypeInfo(controlFlow.Bound).ConvertedType?.SpecialType != SpecialType.System_Int32) return false;
            if (nodes.Any(node => node is ReturnStatementSyntax || node is RefExpressionSyntax || node is SwitchStatementSyntax
                || node.IsKind(SyntaxKind.PointerMemberAccessExpression)
                || node is ArgumentSyntax argument && argument.RefOrOutKeyword.RawKind != 0
                || node is PrefixUnaryExpressionSyntax prefix && (prefix.IsKind(SyntaxKind.AddressOfExpression) || prefix.IsKind(SyntaxKind.PointerIndirectionExpression))))
                return false;
            foreach (var increment in nodes.Where(node => node.IsKind(SyntaxKind.PreIncrementExpression) || node.IsKind(SyntaxKind.PreDecrementExpression)
                || node.IsKind(SyntaxKind.PostIncrementExpression) || node.IsKind(SyntaxKind.PostDecrementExpression)))
                if (increment.Parent is not ExpressionStatementSyntax && increment.Parent is not ForStatementSyntax) return false;
            foreach (var transfer in nodes.Where(node => node is BreakStatementSyntax || node is ContinueStatementSyntax))
                if (transfer.Ancestors().FirstOrDefault(node => node is ForStatementSyntax || node is WhileStatementSyntax || node is DoStatementSyntax) == loop)
                    return false;
            foreach (var access in nodes.OfType<ElementAccessExpressionSyntax>())
                if (access.Expression is not IdentifierNameSyntax array || !columns.ContainsKey(array.Identifier.Text)
                    || access.ArgumentList.Arguments.Count != 1
                    || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.ArgumentList.Arguments[0].Expression).Symbol, counter)) return false;
            foreach (var call in nodes.OfType<InvocationExpressionSyntax>())
            {
                var type = (model.GetSymbolInfo(call).Symbol as IMethodSymbol)?.ContainingType?.ToDisplayString();
                if (type != "EntJoy.Mathematics.math" && type != "System.MathF" && type != "System.Math") return false;
            }
            return ChunkCopiesHaveIsolatedStorage(nodes, columns, model, loop);
        }

        // 结构体副本降为字段访问时，源列在读入与回写之间不可由其它表达式观察或改写。
        private static bool ChunkCopiesHaveIsolatedStorage(SyntaxNode[] nodes, Dictionary<string, int> columns, SemanticModel model, ForStatementSyntax entityLoop)
        {
            foreach (var variable in nodes.OfType<VariableDeclaratorSyntax>())
            {
                if (variable.Initializer?.Value is not ElementAccessExpressionSyntax read
                    || read.Expression is not IdentifierNameSyntax array || !columns.TryGetValue(array.Identifier.Text, out int column)) continue;
                if (variable.Parent is not VariableDeclarationSyntax declaration || declaration.Variables.Count != 1) return false;
                if (variable.Parent?.Parent?.Parent != entityLoop.Statement) return false;
                var local = model.GetDeclaredSymbol(variable);
                foreach (var use in nodes.OfType<IdentifierNameSyntax>().Where(id => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, local)))
                {
                    if (use.Parent is MemberAccessExpressionSyntax member && member.Expression == use
                        && model.GetSymbolInfo(member).Symbol is IFieldSymbol) continue;
                    if (use.Parent is AssignmentExpressionSyntax assignment && assignment.Right == use
                        && assignment.Left is ElementAccessExpressionSyntax && assignment.Parent is ExpressionStatementSyntax) continue;
                    return false;
                }
                bool IsLocal(ExpressionSyntax expression)
                {
                    while (expression is MemberAccessExpressionSyntax member) expression = member.Expression;
                    return SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(expression).Symbol, local);
                }
                bool hasWriteBack = false;
                foreach (var access in nodes.OfType<ElementAccessExpressionSyntax>().Where(access => access.SpanStart > read.SpanStart))
                {
                    if (access.Expression is not IdentifierNameSyntax other || columns[other.Identifier.Text] != column) continue;
                    if (access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access
                        && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && assignment.Right is IdentifierNameSyntax
                        && IsLocal(assignment.Right) && other.Identifier.Text == array.Identifier.Text)
                        hasWriteBack = true;
                    else return false;
                }
                bool mutated = nodes.OfType<AssignmentExpressionSyntax>().Any(assignment => IsLocal(assignment.Left))
                    || nodes.OfType<PrefixUnaryExpressionSyntax>().Any(prefix =>
                        (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)) && IsLocal(prefix.Operand))
                    || nodes.OfType<PostfixUnaryExpressionSyntax>().Any(postfix => IsLocal(postfix.Operand));
                if (mutated && !hasWriteBack) return false;
            }
            return true;
        }

        // 将通过隔离性检查的组件值副本拆成局部字段值；写回只发生在源写回位置。
        private static BlockSyntax DecomposeStructLocals(BlockSyntax body, HashSet<string> arrays, SemanticModel model)
        {
            var statements = FlattenBlockStatements(body);
            var copies = new Dictionary<string, Dictionary<string, string>>();
            var fields = new Dictionary<string, List<(IFieldSymbol Symbol, string Name, bool Written, MemberAccessExpressionSyntax Source)>>();
            foreach (var declaration in statements.OfType<LocalDeclarationStatementSyntax>())
            {
                if (declaration.Declaration.Variables.Count != 1) continue;
                var variable = declaration.Declaration.Variables[0];
                if (variable.Initializer?.Value is not ElementAccessExpressionSyntax read
                    || read.Expression is not IdentifierNameSyntax array || !arrays.Contains(array.Identifier.ValueText)) continue;
                string name = variable.Identifier.ValueText;
                var members = body.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                    .Where(member => member.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == name)
                    .GroupBy(member => member.Name.Identifier.ValueText);
                var slots = new List<(IFieldSymbol Symbol, string Name, bool Written, MemberAccessExpressionSyntax Source)>();
                foreach (var group in members)
                {
                    if (SymbolHelper.GetBoundSymbol(model, group.First()) is not IFieldSymbol field) continue;
                    string localName = $"__ej_copy_{SourceSyntax.Original(model, variable)!.SpanStart}_{field.Name}";
                    bool written = group.Any(member => IsWrittenField(member));
                    slots.Add((field, localName, written, group.First()));
                }
                fields[name] = slots;
                copies[name] = slots.ToDictionary(slot => slot.Symbol.Name, slot => slot.Name);
            }
            var writtenFields = fields.ToDictionary(copy => copy.Key,
                copy => new HashSet<string>(copy.Value.Where(slot => slot.Written).Select(slot => slot.Symbol.Name)));
            var rewrite = new ComponentFieldRewriter(copies, writtenFields);
            var output = new List<StatementSyntax>();
            foreach (var statement in statements)
            {
                if (statement is LocalDeclarationStatementSyntax declaration && declaration.Declaration.Variables.Count == 1
                    && fields.TryGetValue(declaration.Declaration.Variables[0].Identifier.ValueText, out var slots))
                {
                    var read = declaration.Declaration.Variables[0].Initializer!.Value;
                    foreach (var slot in slots)
                    {
                        var access = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, read, SyntaxFactory.IdentifierName(slot.Symbol.Name));
                        output.Add(SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
                            SourceSyntax.WithOrigin(SyntaxFactory.ParseTypeName(slot.Symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)), slot.Source))
                            .AddVariables(SyntaxFactory.VariableDeclarator(slot.Name).WithInitializer(SyntaxFactory.EqualsValueClause(access)))));
                    }
                }
                else output.Add((StatementSyntax)rewrite.Visit(statement)!);
            }
            return SyntaxFactory.Block(output);
        }

        private static bool IsWrittenField(MemberAccessExpressionSyntax member)
        {
            ExpressionSyntax target = member;
            while (target.Parent is MemberAccessExpressionSyntax parent) target = parent;
            return target.Parent is AssignmentExpressionSyntax assignment && assignment.Left == target
                || target.Parent is PrefixUnaryExpressionSyntax prefix && (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression))
                || target.Parent is PostfixUnaryExpressionSyntax;
        }

        private static List<StatementSyntax> FlattenBlockStatements(BlockSyntax block)
            => block.Statements.SelectMany(statement => statement is BlockSyntax nested
                ? FlattenBlockStatements(nested) : new List<StatementSyntax> { statement }).ToList();

        // 不同源局部符号使用不同内部名字，变量表不会合并相邻作用域中的同名变量。
        private sealed class ChunkLocalNameRewriter : CSharpSyntaxRewriter
        {
            private readonly SemanticModel _model;
            private readonly Dictionary<ISymbol, string> _names;
            public ChunkLocalNameRewriter(SemanticModel model, Dictionary<ISymbol, string> names) { _model = model; _names = names; }

            public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node)
            {
                var changed = (VariableDeclaratorSyntax)base.VisitVariableDeclarator(node)!;
                if (SourceSyntax.Original(_model, node) is VariableDeclaratorSyntax source
                    && _model.GetDeclaredSymbol(source) is ISymbol symbol && _names.TryGetValue(symbol, out var name))
                    changed = changed.WithIdentifier(SyntaxFactory.Identifier(name).WithTriviaFrom(node.Identifier));
                return changed;
            }

            public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
                => SymbolHelper.GetBoundSymbol(_model, node) is ISymbol symbol && _names.TryGetValue(symbol, out var name)
                    ? SourceSyntax.WithOrigin(SyntaxFactory.IdentifierName(name).WithTriviaFrom(node), node) : node;
        }

        private sealed class ComponentFieldRewriter : CSharpSyntaxRewriter
        {
            private readonly Dictionary<string, Dictionary<string, string>> _copies;
            private readonly Dictionary<string, HashSet<string>> _written;
            public ComponentFieldRewriter(Dictionary<string, Dictionary<string, string>> copies, Dictionary<string, HashSet<string>> written)
            { _copies = copies; _written = written; }

            public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
            {
                if (node.Expression is AssignmentExpressionSyntax assignment && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && assignment.Left is ElementAccessExpressionSyntax && assignment.Right is IdentifierNameSyntax value
                    && _copies.TryGetValue(value.Identifier.ValueText, out var fields))
                    return SyntaxFactory.Block(_written[value.Identifier.ValueText].Select(field =>
                        SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, assignment.Left, SyntaxFactory.IdentifierName(field)),
                            SyntaxFactory.IdentifierName(fields[field])))));
                return base.VisitExpressionStatement(node);
            }

            public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
                => node.Expression is IdentifierNameSyntax owner && _copies.TryGetValue(owner.Identifier.ValueText, out var fields)
                    && fields.TryGetValue(node.Name.Identifier.ValueText, out var name)
                        ? SourceSyntax.WithOrigin(SyntaxFactory.IdentifierName(name).WithTriviaFrom(node), node)
                        : base.VisitMemberAccessExpression(node);
        }

        /// <summary>
        /// SyntaxRewriter for IJobChunk SIMD preprocessing:
        /// - Removes chunk array local declarations
        /// - Replaces entity for-loop with its body (keeps body, removes for-header)
        /// </summary>
        private sealed class IJobChunkSimdRewriter : CSharpSyntaxRewriter
        {
            private readonly HashSet<string> _chunkArrayNames;
            private readonly string _chunkParamName;
            private readonly string _entityLoopIvName;

            public IJobChunkSimdRewriter(HashSet<string> chunkArrayNames, string chunkParamName, string entityLoopIvName)
            {
                _chunkArrayNames = chunkArrayNames;
                _chunkParamName = chunkParamName;
                _entityLoopIvName = entityLoopIvName;
            }

            public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
            {
                foreach (var variable in node.Declaration.Variables)
                {
                    if (_chunkArrayNames.Contains(variable.Identifier.Text))
                        return null; // Remove: this is a chunk array declaration
                }
                return base.VisitLocalDeclarationStatement(node);
            }

            public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                // 两种 Chunk adapter 共用实体数变量。
                if (node.Name.Identifier.Text == "Count"
                    && node.Expression is IdentifierNameSyntax id
                    && id.Identifier.Text == _chunkParamName)
                {
                    return SourceSyntax.WithOrigin(SyntaxFactory.IdentifierName("__entity_count"), node);
                }
                return base.VisitMemberAccessExpression(node);
            }

            public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
            {
                if (node.Declaration?.Variables.Count == 1)
                {
                    string ivName = node.Declaration.Variables[0].Identifier.Text;
                    if (ivName == _entityLoopIvName)
                    {
                        // Remove for-header, keep body
                        return Visit(node.Statement);
                    }
                }
                return base.VisitForStatement(node);
            }
        }

        /// <summary>
        /// 从 chunkArrayInfo 构建 _nativeArrayParams 字典
        /// (SimdControlFlowGenerator 用这个来识别 NativeArray 访问)
        /// </summary>
        private static Dictionary<string, string> BuildChunkArrayNativeArrayParams(
            List<(string name, string elemType, int compIndex)> chunkArrays)
        {
            var result = new Dictionary<string, string>();
            foreach (var (name, elemType, _) in chunkArrays)
                result[name] = elemType;
            return result;
        }

        // IJobChunk Auto-SIMD: Generate SIMD Code

        /// <summary>
        /// 生成 IJobChunk 的 Register-Level SIMD Execute 函数体。
        /// 流程：
        /// 1. 生成 C++ prelude（_ptr / _length 声明）
        /// 2. 生成外层 batch loop（for si; v_i = v_base + si）
        /// 3. SimdControlFlowGenerator on 修改后的 body（无 for-loop 头）
        /// 4. 标量 remainder 循环
        /// </summary>
        private static void GenerateChunkFunctionSIMD(
            INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb,
            bool useFastMath, NativeTranspiler.SimdMathPrecision simdMathPrecision)
        {
            // Output function signature (same as GenerateChunkFunctionStandard)
            bool usesSendEvent = JobUsesSendEvent(jobStruct, compilation);
            var chunkParams = BuildChunkJobParameters(jobStruct, includeHeader: usesSendEvent);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);
            sb.AppendLine("    EntityMatchMask __queryMask(__chunkData->filter, __chunkData->entityCount);");
            sb.AppendLine("    if (!__queryMask.HasMatches()) return;");

                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
                var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
                if (methodSyntax?.Body == null) throw new InvalidOperationException("Chunk SIMD Execute requires a block body.");
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

                // 1. Preprocess AST
                var (chunkArrays, entityLoopIv, modifiedBody) =
                    PreprocessIJobChunkAST(methodSyntax, semanticModel, jobStruct, compilation);

                if (string.IsNullOrEmpty(entityLoopIv) || chunkArrays.Count == 0 || usesSendEvent)
                {
                    sb.AppendLine("    // Scalar chunk body: preserve source control flow and semantic binding.");
                    // Fallback: use scalar translator
                    // （usesSendEvent：SendEvent 无法在 SimdControlFlowGenerator 中翻译，
                    //   必须用 CppChunkStatementTranslator 标量路径，它完整支持 SendEvent 拦截）
                    var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
                    var sharedTypes = CollectSharedComponentTypes(jobStruct, compilation);
                    var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, sharedTypes, useFastMath);
                    sb.Append(translator.Translate(methodSyntax.Body));
                    // 闭合函数体（fallback 提前 return，需手动补结尾括号）
                    sb.AppendLine("}");
                    return;
                }

                                // 2. Generate C++ prelude
                foreach (var (name, elemType, compIdx) in chunkArrays)
                {
                    sb.AppendLine($"    auto* {name}_ptr = reinterpret_cast<{elemType}*>(__chunkData->requiredComponentArrays[{compIdx}]);");
                    sb.AppendLine($"    int {name}_length = __chunkData->entityCount;");
                }
                sb.AppendLine("    int __entity_count = __chunkData->entityCount;");

                // 连续列 + 单向量字段的实体体：按标量槽摊平，整宽连续读写。
                // 结构体步长下的逐字段取数只能编成 gather，而这里每个实体恰好占 slots 个连续标量。
                if (TryEmitFlattenedSlotLoop(sb, methodSyntax, entityLoopIv, chunkArrays,
                        useFastMath, jobStruct, compilation, semanticModel))
                {
                    sb.AppendLine("}");
                    return;
                }

                // 3. Build fake method with virtual int i parameter
                var newParamList = methodSyntax.ParameterList.AddParameters(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier(entityLoopIv))
                        .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))));
                var fakeMethod = methodSyntax.WithParameterList(newParamList).WithBody(modifiedBody);

                // 4. Variable analysis
                var varAnalyzer = new SimdVariableAnalyzer(semanticModel, jobStruct, entityLoopIv);
                var variables = varAnalyzer.Analyze(fakeMethod);
                var nativeArrayParams = BuildChunkArrayNativeArrayParams(chunkArrays);

                                // 5. Generate SIMD batch loop
                sb.AppendLine("    int __simd_end = (__entity_count / NSIMD_WIDTH) * NSIMD_WIDTH;");
                sb.AppendLine("    if (__simd_end > 0)");
                sb.AppendLine("    {");
                sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0);");
                sb.AppendLine("        for (int si = 0; si < __simd_end; si += NSIMD_WIDTH)");
                sb.AppendLine("        {");
                sb.AppendLine("            simd_value<int> v_i = v_base + si;");

            // 6. SimdControlFlowGenerator on modified body
            var simdGen = new SimdControlFlowGenerator(
                semanticModel, jobStruct, variables, varAnalyzer,
                indexParamName: entityLoopIv,
                simdIndexVar: "v_i",
                // NT-01(b)：同上 —— 连续向量 store 的偏移是外层批循环变量 `si`，不是常量 0。
                batchOffsetVar: "si",
                batchLoopVar: "",
                nativeArrayParams: nativeArrayParams,
                simdMathPrecision: simdMathPrecision);

            string simdBody = simdGen.Generate(modifiedBody);
            foreach (var line in simdBody.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine($"            {line.TrimEnd()}");

            sb.AppendLine("        __simd_exit: ;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            // 7. Scalar remainder loop
            GenerateChunkFunctionRemainder(jobStruct, compilation, sb, useFastMath, chunkArrays, entityLoopIv);

            sb.AppendLine("}");
        }


        /// <summary>
        /// 摊平发射：实体体只对同一单字段向量组件做逐槽运算时，按标量槽重排成连续访问。
        /// 结构体步长下的逐字段取数只能编成 gather；这里每个实体恰好占 slots 个连续标量，整宽读写即可。
        /// </summary>
        private static bool TryEmitFlattenedSlotLoop(
            StringBuilder sb, MethodDeclarationSyntax methodSyntax, string entityLoopIv,
            List<(string name, string elemType, int compIndex)> chunkArrays,
            bool useFastMath, INamedTypeSymbol jobStruct, Compilation compilation, SemanticModel semanticModel)
        {
            if (!TryBuildFlattenedSlotBody(methodSyntax, entityLoopIv, chunkArrays, useFastMath, jobStruct, compilation,
                    semanticModel, out var statements, out var assertions, out int slots)) return false;
            foreach (var assertion in assertions) sb.AppendLine("    " + assertion);
            sb.AppendLine($"    int __flat_end = __entity_count * {slots};");
            sb.AppendLine("    for (int __flat = 0; __flat < __flat_end; ++__flat)");
            sb.AppendLine("    {");
            foreach (var statement in statements) sb.AppendLine("        " + statement);
            sb.AppendLine("    }");
            return true;
        }

        /// <summary>
        /// 判定并生成摊平后的循环体语句。两个后端（chunk SIMD / EntityBatch）共用同一判据与重写。
        /// </summary>
        private static bool TryBuildFlattenedSlotBody(
            MethodDeclarationSyntax methodSyntax, string entityLoopIv,
            List<(string name, string elemType, int compIndex)> chunkArrays,
            bool useFastMath, INamedTypeSymbol jobStruct, Compilation compilation, SemanticModel semanticModel,
            out List<string> statements, out List<string> assertions, out int slots)
        {
            statements = null;
            assertions = null;
            slots = 0;
            if (chunkArrays.Count == 0 || string.IsNullOrEmpty(entityLoopIv)) return false;

            var columnTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
            string fieldName = null, scalarType = null;
            List<string> scalarFieldNames = null;
            foreach (var (_, _, compIndex) in chunkArrays)
            {
                if (compIndex < 0 || compIndex >= columnTypes.Count) return false;
                INamedTypeSymbol columnType = columnTypes[compIndex];
                if (fieldName == null && scalarFieldNames == null)
                {
                    // 第一列决定形态：单字段向量包装，或 N 个同类型标量字段。
                    if (FlatShape.TryGetVectorField(columnType, out string field, out string scalar, out int arity))
                    { fieldName = field; scalarType = scalar; slots = arity; }
                    else if (FlatShape.TryGetScalarFields(columnType, out List<string> names, out string element, out int count))
                    { scalarFieldNames = names; scalarType = element; slots = count; }
                    else return false;
                    continue;
                }
                if (fieldName != null)
                {
                    if (!FlatShape.TryGetVectorField(columnType, out string field, out string scalar, out int arity)
                        || field != fieldName || scalar != scalarType || arity != slots) return false;
                }
                else if (!FlatShape.TryGetScalarFields(columnType, out List<string> names, out string element, out int count)
                    || element != scalarType || count != slots || !names.SequenceEqual(scalarFieldNames)) return false;
            }
            if (slots <= 1) return false;
            bool scalarLayout = scalarFieldNames != null;

            var arrays = new HashSet<string>(chunkArrays.Select(array => array.name));
            bool IsColumnElementAccess(ElementAccessExpressionSyntax access)
                => access.Expression is IdentifierNameSyntax owner
                    && arrays.Contains(owner.Identifier.Text)
                    && access.ArgumentList?.Arguments.Count == 1
                    && access.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax index
                    && index.Identifier.Text == entityLoopIv;
            bool IsColumnField(MemberAccessExpressionSyntax access)
                => access.Expression is ElementAccessExpressionSyntax element
                    && IsColumnElementAccess(element)
                    && (scalarLayout ? scalarFieldNames.Contains(access.Name.Identifier.Text) : access.Name.Identifier.Text == fieldName);
            bool TryGetColumn(ExpressionSyntax expression, out string column)
            {
                column = null;
                switch (expression)
                {
                    case ElementAccessExpressionSyntax access when IsColumnElementAccess(access):
                        column = ((IdentifierNameSyntax)access.Expression).Identifier.Text;
                        return true;
                    case MemberAccessExpressionSyntax access when IsColumnField(access):
                        column = ((IdentifierNameSyntax)((ElementAccessExpressionSyntax)access.Expression).Expression).Identifier.Text;
                        return true;
                    default:
                        return false;
                }
            }
            bool IsLocalTarget(ExpressionSyntax expression, string local)
                => expression is IdentifierNameSyntax identifier && identifier.Identifier.Text == local
                    || expression is MemberAccessExpressionSyntax access
                        && access.Expression is IdentifierNameSyntax owner
                        && owner.Identifier.Text == local;

            // 预处理后的体是拷贝形态：`T copy = X[i]…; …改 copy…; X[i] = copy;`。
            // 判据与翻译都落在**原始语法树**上：预处理器重写出来的节点不在树里，取类型信息会抛。
            var entityLoop = methodSyntax.Body?.Statements.OfType<ForStatementSyntax>()
                .FirstOrDefault(loop => loop.Declaration?.Variables.FirstOrDefault()?.Identifier.Text == entityLoopIv);
            if (entityLoop?.Statement is not BlockSyntax loopBody) return false;
            // 平坦循环按 [0, n*slots) 覆盖全部实体：源循环必须从 0 起步，否则会多处理源循环跳过的实体。
            if (entityLoop.Declaration?.Variables.Count != 1
                || semanticModel.GetConstantValue(entityLoop.Declaration.Variables[0].Initializer?.Value).Value is not int start
                || start != 0) return false;

            var flat = new List<StatementSyntax>();
            FlattenStatements(loopBody, flat);
            string localName = null, sourceColumn = null, targetColumn = null;
            bool copyForm = flat.Count >= 2 && flat[0] is LocalDeclarationStatementSyntax;
            if (copyForm)
            {
                // 拷贝形态：`T copy = X[i]…; …改 copy 的各槽…; X[i] = copy;`。
                // 判据与翻译都落在**原始语法树**上：预处理器重写出来的节点不在树里，取类型信息会抛。
                if (flat[0] is not LocalDeclarationStatementSyntax declaration
                    || declaration.Declaration.Variables.Count != 1
                    || declaration.Declaration.Variables[0].Initializer?.Value is not ExpressionSyntax initializer) return false;
                localName = declaration.Declaration.Variables[0].Identifier.Text;
                if (!TryGetColumn(initializer, out sourceColumn)) return false;
                if (flat[flat.Count - 1] is not ExpressionStatementSyntax storeStatement
                    || storeStatement.Expression is not AssignmentExpressionSyntax storeAssignment
                    || storeAssignment.Right is not IdentifierNameSyntax storedValue
                    || storedValue.Identifier.Text != localName
                    || !TryGetColumn(storeAssignment.Left, out targetColumn)) return false;
                // 局部副本必须读自写往的同一列：`B[i] = A[i] + …` 摊平后副本就是写列的那一个槽，
                // 读列只在声明里出现，去掉声明就把读丢了。
                if (sourceColumn != targetColumn) return false;

                var mutatedFields = new HashSet<string>();
                for (int i = 1; i < flat.Count - 1; i++)
                {
                    if (flat[i] is not ExpressionStatementSyntax mutate
                        || mutate.Expression is not AssignmentExpressionSyntax mutation) return false;
                    // 中间语句只许改这个局部副本：改列或改 job 字段都不是这一形态。
                    if (!IsLocalTarget(mutation.Left, localName)) return false;
                    if (scalarLayout)
                    {
                        if (mutation.Left is not MemberAccessExpressionSyntax localField
                            || !scalarFieldNames.Contains(localField.Name.Identifier.Text)
                            || !mutatedFields.Add(localField.Name.Identifier.Text)) return false;
                    }
                    if (!IsFlatPureStatement(flat[i], IsColumnField, IsColumnElementAccess, localName)) return false;
                }
                if (scalarLayout && mutatedFields.Count != slots) return false;
            }
            else
            {
                // 直接形态：每个槽一条赋值语句，字段各覆盖一次。
                if (!scalarLayout || flat.Count != slots) return false;
                var covered = new HashSet<string>();
                foreach (var statement in flat)
                {
                    if (statement is not ExpressionStatementSyntax expressionStatement
                        || expressionStatement.Expression is not AssignmentExpressionSyntax assignment
                        || assignment.Left is not MemberAccessExpressionSyntax target
                        || !IsColumnField(target)
                        || !covered.Add(target.Name.Identifier.Text)) return false;
                    if (!IsFlatPureStatement(statement, IsColumnField, IsColumnElementAccess, null)) return false;
                }
            }

            var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
            var sharedTypes = CollectSharedComponentTypes(jobStruct, compilation);
            var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, sharedTypes, useFastMath);
            // 翻译整块（原始语法树）再切出循环体：合成块会让子节点脱离语法树，翻译器取符号会抛。
            string translated = translator.Translate(methodSyntax.Body!);
            string loopText = ExtractLoopBody(translated, entityLoopIv);
            if (loopText == null) return false;
            translated = loopText;
            System.Text.RegularExpressions.Regex declarationPattern = null, storePattern = null;
            string flatTarget = null;
            if (localName != null)
            {
                declarationPattern = new System.Text.RegularExpressions.Regex($@"^[\w:<>]+\s+{localName}\s*=");
                storePattern = new System.Text.RegularExpressions.Regex($@"^[^=]*=\s*{localName}\s*;\s*$");
                flatTarget = $"(({scalarType}*){targetColumn}_ptr)[__flat]";
            }
            // 只读列保持 const：摊平只读 const 指针再强转成可写会编译失败。
            var flatCasts = new Dictionary<string, string>();
            foreach (var (name, elemType, _) in chunkArrays)
                flatCasts[name] = elemType.StartsWith("const ", StringComparison.Ordinal)
                    ? $"((const {scalarType}*){name}_ptr)"
                    : $"(({scalarType}*){name}_ptr)";
            var fieldNames = scalarLayout ? scalarFieldNames : new List<string> { fieldName };
            var emitted = new List<string>();
            foreach (var rawLine in translated.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                if (declarationPattern != null && (declarationPattern.IsMatch(rawLine) || storePattern.IsMatch(rawLine))) continue;
                if (rawLine.Contains("_ptr = reinterpret_cast<") || rawLine.Contains("_length = __chunkData->entityCount;")) continue;
                if (rawLine.Contains("__chunk_entity_count =")) continue;
                string line = rawLine;
                foreach (var pair in flatCasts)
                {
                    foreach (var name in fieldNames)
                        line = line.Replace($"{pair.Key}_ptr[{entityLoopIv}].{name}", $"{pair.Value}[__flat]");
                    line = line.Replace($"{pair.Key}_ptr[{entityLoopIv}]", $"{pair.Value}[__flat]");
                }
                if (localName != null)
                {
                    foreach (var name in fieldNames)
                        line = System.Text.RegularExpressions.Regex.Replace(line, $@"\b{localName}\.{name}\b", flatTarget);
                    line = System.Text.RegularExpressions.Regex.Replace(line, $@"\b{localName}\b", flatTarget);
                }
                if (System.Text.RegularExpressions.Regex.IsMatch(line, $@"\b{System.Text.RegularExpressions.Regex.Escape(entityLoopIv)}\b"))
                    return false;
                emitted.Add(line.Trim());
            }
            if (scalarLayout)
            {
                // 各槽语句必须同构：字段名与列名归一后文本一致，才能用一条语句跑遍所有槽。
                // 只差数值常量时，常量按 `__flat % slots` 取对应槽的值。
                string canonical = null;
                bool identical = true;
                var literalsPerSlot = new List<List<string>>();
                foreach (var line in emitted)
                {
                    var literals = new List<string>();
                    string skeleton = System.Text.RegularExpressions.Regex.Replace(line, NumberLiteralPattern,
                        match => { literals.Add(match.Value); return "@"; });
                    if (canonical == null) canonical = skeleton;
                    else if (canonical != skeleton) return false;
                    if (line != emitted[0]) identical = false;
                    literalsPerSlot.Add(literals);
                }
                if (!identical)
                {
                    if (emitted.Count != slots) return false;
                    int literalCount = literalsPerSlot[0].Count;
                    if (literalsPerSlot.Any(literals => literals.Count != literalCount)) return false;
                    var builder = new System.Text.StringBuilder();
                    int cursor = 0;
                    for (int index = 0; index < canonical.Length; index++)
                    {
                        if (canonical[index] != '@') { builder.Append(canonical[index]); continue; }
                        // 逐槽取值：`(__flat % slots == 1 ? a1 : a0)`，槽 0 作为兜底。
                        builder.Append("(__flat % ").Append(slots).Append(" == 0 ? ")
                            .Append(literalsPerSlot[0][cursor]);
                        for (int slot = 1; slot < slots; slot++)
                        {
                            builder.Append(" : (__flat % ").Append(slots).Append(" == ").Append(slot)
                                .Append(" ? ").Append(literalsPerSlot[slot][cursor]).Append(')');
                        }
                        builder.Append(')');
                        cursor++;
                    }
                    emitted.Clear();
                    emitted.Add(builder.ToString());
                }
                else if (emitted.Count > 1)
                {
                    emitted.RemoveRange(1, emitted.Count - 1);
                }
            }
            if (emitted.Count == 0)
                emitted.Add($"{flatTarget} = (({scalarType}*){sourceColumn}_ptr)[__flat];");

            statements = emitted;
            assertions = new List<string>();
            foreach (var (_, elemType, _) in chunkArrays)
                assertions.Add($"static_assert(sizeof({elemType}) == {slots} * sizeof({scalarType}), \"flat slot layout mismatch\");");
            return true;
        }

        /// <summary>从翻译后的函数体文本里取出实体循环的循环体，按花括号配对切分。</summary>
        private static string ExtractLoopBody(string translated, string entityLoopIv)
        {
            var lines = translated.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int start = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains($"for (int {entityLoopIv} = 0;")) { start = i; break; }
            }
            if (start < 0) return null;
            var body = new System.Text.StringBuilder();
            int depth = 0;
            bool opened = false;
            for (int i = start; i < lines.Length; i++)
            {
                int opens = lines[i].Count(c => c == '{');
                int closes = lines[i].Count(c => c == '}');
                if (!opened)
                {
                    depth += opens - closes;
                    if (opens > 0) { opened = true; if (depth <= 0) return body.ToString(); }
                    continue;
                }
                if (depth == 1 && opens == 0 && closes == 1) return body.ToString();
                body.AppendLine(lines[i]);
                depth += opens - closes;
            }
            return null;
        }

        /// <summary>摊平语句里的数值字面量（含 `f`/`u` 后缀与小数点）；标识符里的数字不算。</summary>
        private const string NumberLiteralPattern = @"(?<![\w.])\d+(?:\.\d+)?(?:[eE][-+]?\d+)?[fFuUlL]*(?![\w.])";

        /// <summary>摊平语句只许由列访问、局部副本、算术与字面量组成。</summary>
        private static bool IsFlatPureStatement(StatementSyntax statement,
            Func<MemberAccessExpressionSyntax, bool> isColumnField,
            Func<ElementAccessExpressionSyntax, bool> isColumnElementAccess,
            string localName)
        {
            foreach (var node in statement.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case MemberAccessExpressionSyntax access:
                        if (isColumnField(access))
                        {
                            // `.字段.分量` 不是整槽运算，摊平后语义不同。
                            if (access.Parent is MemberAccessExpressionSyntax) return false;
                            continue;
                        }
                        if (access.Expression is ThisExpressionSyntax) continue;
                        if (localName != null && access.Expression is IdentifierNameSyntax localOwner
                            && localOwner.Identifier.Text == localName) continue;
                        return false;
                    case ElementAccessExpressionSyntax access:
                        if (!isColumnElementAccess(access)) return false;
                        continue;
                    case InvocationExpressionSyntax:
                        return false;
                    case IfStatementSyntax:
                    case ForStatementSyntax:
                    case ForEachStatementSyntax:
                    case WhileStatementSyntax:
                    case SwitchStatementSyntax:
                    case ReturnStatementSyntax:
                    case BreakStatementSyntax:
                    case ContinueStatementSyntax:
                        return false;
                }
            }
            return true;
        }

        /// <summary>把不含声明的裸块就地摊开，便于按语义顺序检查语句序列。</summary>
        private static void FlattenStatements(BlockSyntax block, List<StatementSyntax> into)
        {
            foreach (var statement in block.Statements)
            {
                if (statement is BlockSyntax nested) FlattenStatements(nested, into);
                else into.Add(statement);
            }
        }

        /// <summary>
        /// 生成 IJobChunk SIMD 的 scalar remainder 循环。
        /// 复用 CppChunkStatementTranslator 的标量输出，仅修改实体循环起始值为 __simd_end。
        /// </summary>
        private static void GenerateChunkFunctionRemainder(
            INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb,
            bool useFastMath,
            List<(string name, string elemType, int compIndex)> chunkArrays,
            string entityLoopIv)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return;
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
            var sharedTypes = CollectSharedComponentTypes(jobStruct, compilation);
            var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, sharedTypes, useFastMath);
            string scalarBody = translator.Translate(methodSyntax.Body);

            // Remove prelude declarations (already emitted by SIMD generator)
            foreach (var (name, _, _) in chunkArrays)
            {
                    string ptrDecl = $"auto* {name}_ptr = reinterpret_cast<";
                    int idx = scalarBody.IndexOf(ptrDecl);
                    if (idx >= 0)
                    {
                        int semiEnd = scalarBody.IndexOf(';', idx);
                        if (semiEnd >= 0)
                        {
                            int lineEnd = scalarBody.IndexOf('\n', semiEnd);
                            if (lineEnd >= 0)
                                scalarBody = scalarBody.Remove(idx, lineEnd - idx + 1);
                            else
                                scalarBody = scalarBody.Remove(idx);
                        }
                    }

                    string lenDecl = $"int {name}_length =";
                    idx = scalarBody.IndexOf(lenDecl);
                    if (idx >= 0)
                    {
                        int lineEnd = scalarBody.IndexOf('\n', idx);
                        if (lineEnd >= 0)
                            scalarBody = scalarBody.Remove(idx, lineEnd - idx + 1);
                        else
                            scalarBody = scalarBody.Remove(idx);
                    }
            }

            // Remove __entityCount declaration if present (already emitted)
            string ecDecl = "int __entity_count = ";
            int ecIdx = scalarBody.IndexOf(ecDecl);
            if (ecIdx >= 0)
            {
                int ecEnd = scalarBody.IndexOf('\n', ecIdx);
                if (ecEnd >= 0)
                    scalarBody = scalarBody.Remove(ecIdx, ecEnd - ecIdx + 1);
                else
                    scalarBody = scalarBody.Remove(ecIdx);
            }

            // Remove pragma hints (already in SIMD loop or not needed)
            scalarBody = scalarBody.Replace("#pragma loop(ivdep)\r\n", "");
            scalarBody = scalarBody.Replace("#pragma loop(ivdep)\n", "");
            scalarBody = scalarBody.Replace("#pragma loop(vector)\r\n", "");
            scalarBody = scalarBody.Replace("#pragma loop(vector)\n", "");
            scalarBody = scalarBody.Replace("#pragma unroll(4)\r\n", "");
            scalarBody = scalarBody.Replace("#pragma unroll(4)\n", "");

            // Change entity loop start from 0 to __simd_end
            string loopPattern = $"for (int {entityLoopIv} = 0; {entityLoopIv} <";
            string loopReplacement = $"for (int {entityLoopIv} = __simd_end; {entityLoopIv} <";
            scalarBody = scalarBody.Replace(loopPattern, loopReplacement);

            sb.Append(CodeIndent.Shift(scalarBody, 1));
        }
    }
}

