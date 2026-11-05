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
                            (symbol.Name == Config.GetComponentDataNativeArray || symbol.Name == Config.GetComponentDataSpan) &&
                            symbol.TypeArguments.Length > 0)
                        {
                            string varName = variable.Identifier.Text;
                            var compType = symbol.TypeArguments[0] as INamedTypeSymbol;
                            if (compType != null)
                            {
                                int idx = requiredTypes.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, compType));
                                string elemCppType = NativeTranspiler.MapCSharpTypeToCpp(compType);
                                chunkArrays.Add((varName, elemCppType, idx));
                                chunkArrayNames.Add(varName);
                            }
                        }
                    }
                }
            }

            // 2. Find entity for-loop
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
                if (type != "int" && type != "unsigned int" && type != "float"
                    && type != "EntJoy::Mathematics::float2" && type != "EntJoy::Mathematics::int2") return false;
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

            sb.Append(scalarBody);
        }
    }
}
