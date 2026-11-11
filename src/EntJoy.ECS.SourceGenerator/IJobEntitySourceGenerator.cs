#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace EntJoy.ECS.SourceGenerator
{
    /// <summary>
    /// IJobEntity → IJobChunk 适配器生成器（由总入口 <see cref="ECSSourceGenerator"/> 调度加载，不直接注册）。
    /// </summary>
    internal sealed class IJobEntitySourceGenerator : IIncrementalGenerator
    {
        private const string JobAdapterSuffix = "_IJobEntityAdapter.g.cs";
        /// <summary>启用区间达到该长度才整段调用 <c>__EntJoyBody</c>；更短的区间内联执行，避免逐实体调用开销。</summary>
        private const int MaskedRunBodyThreshold = 8;
        private static readonly DiagnosticDescriptor WritableRelationship = new(
            "EJ2021", "Relationship parameters must be read-only",
            "Relationship parameter '{0}' must be passed by value or in; use an ECB to change relationships.",
            "EntJoy.ECS.SourceGenerator", DiagnosticSeverity.Error, true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var jobProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCandidate(node),
                    transform: static (ctx, ct) => GetJob(ctx, ct))
                .Where(static j => j != null);

            var collected = jobProvider.Collect();

            context.RegisterSourceOutput(collected, static (spc, jobs) =>
            {
                foreach (var job in jobs)
                {
                    if (job == null) continue;
                    var writableRelations = job.Execute.Parameters.Where(p => IsRelationParam(p)
                        && (p.RefKind == RefKind.Ref || p.RefKind == RefKind.Out)).ToArray();
                    if (writableRelations.Length > 0)
                    {
                        foreach (var parameter in writableRelations)
                            spc.ReportDiagnostic(Diagnostic.Create(WritableRelationship, parameter.Locations.FirstOrDefault(), parameter.Name));
                        continue;
                    }
                    string source = Generate(job.JobType, job.Execute, job.IsNativeTranspiled, job.DefensiveCopyParams);
                    spc.AddSource(
                        $"{Sanitize(job.JobType.ToDisplayString())}{JobAdapterSuffix}",
                        SourceText.From(source, Encoding.UTF8));
                }
            });
        }

        private static bool IsCandidate(SyntaxNode node)
        {
            return node is StructDeclarationSyntax s &&
                   s.BaseList != null &&
                   s.BaseList.Types.Any(t =>
                       t.Type.ToString().Contains(Config.IJobEntity) ||
                       t.Type.ToString().Contains(Config.IJobChunk));
        }

        private static JobCandidate? GetJob(GeneratorSyntaxContext context, CancellationToken ct)
        {
            if (context.Node is not StructDeclarationSyntax candidate)
                return null;

            var model = context.SemanticModel;
            if (model.GetDeclaredSymbol(candidate, ct) is not INamedTypeSymbol jobType)
                return null;

            bool isEntity = jobType.AllInterfaces.Any(i =>
                i.Name == Config.IJobEntity && i.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyECS);
            bool isChunk = jobType.AllInterfaces.Any(i =>
                i.Name == Config.IJobChunk && i.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyECS);
            if (!isEntity && !isChunk)
                return null;

            // [NativeTranspile] 的 Job 路由到原生路径（NativeExports.Schedule_* / RunImmediate_*）
            // 非 [NativeTranspile] 的 IJobEntity 走托管路径（IJobChunk 适配器）；
            // 非 [NativeTranspile] 的 IJobChunk 无需生成（ChunkJobExtensions 直接可用）。
            var nativeTranspileAttr = jobType.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "NativeTranspileAttribute" &&
                a.AttributeClass?.ContainingNamespace?.ToDisplayString() == "NativeTranspiler");
            bool isNativeTranspiled = nativeTranspileAttr != null;
            if (isChunk && !isNativeTranspiled)
                return null;

            var execute = jobType.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == Config.Execute && m.Parameters.Length > 0 && m.ReturnsVoid);
            if (execute == null)
                return null;

            return new JobCandidate(jobType, execute, isNativeTranspiled, AnalyzeDefensiveCopies(model, execute, ct));
        }

        /// <summary>
        /// 标记需要保留 <c>in</c> 参数防御性副本语义的参数下标：body 经只读参数调用非 readonly
        /// 成员（方法、属性、索引器）时，逐实体必须经 <c>ref readonly</c> 局部访问，副本才由编译器产生。
        /// 纯字段读取不产生副本，不受影响。
        /// </summary>
        private static bool[] AnalyzeDefensiveCopies(SemanticModel model, IMethodSymbol execute, CancellationToken ct)
        {
            var flags = new bool[execute.Parameters.Length];
            if (execute.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is not MethodDeclarationSyntax method)
                return flags;
            var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
            if (body == null) return flags;

            var readonlyParams = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
            for (int i = 0; i < execute.Parameters.Length; i++)
            {
                var parameter = execute.Parameters[i];
                if (IsEntityParam(parameter) || parameter.RefKind == RefKind.None || parameter.RefKind == RefKind.Ref)
                    continue;
                readonlyParams[parameter] = i;
            }
            if (readonlyParams.Count == 0) return flags;

            foreach (var node in body.DescendantNodes())
            {
                ISymbol? member;
                IParameterSymbol? receiver;
                switch (node)
                {
                    case MemberAccessExpressionSyntax access:
                        member = model.GetSymbolInfo(access, ct).Symbol;
                        receiver = ResolveRootParameter(model, access.Expression, ct);
                        break;
                    case ElementAccessExpressionSyntax element:
                        member = model.GetSymbolInfo(element, ct).Symbol;
                        receiver = ResolveRootParameter(model, element.Expression, ct);
                        break;
                    default:
                        continue;
                }
                if (receiver == null || !readonlyParams.TryGetValue(receiver, out int index)) continue;
                bool mutating = member switch
                {
                    IMethodSymbol m => !m.IsReadOnly,
                    IPropertySymbol p => !p.IsReadOnly,
                    _ => false,
                };
                if (mutating) flags[index] = true;
            }
            return flags;
        }

        /// <summary>沿 <c>a.b[c]</c> 链回溯到最内层标识符，返回其参数符号。</summary>
        private static IParameterSymbol? ResolveRootParameter(SemanticModel model, ExpressionSyntax expression, CancellationToken ct)
        {
            while (true)
            {
                switch (expression)
                {
                    case IdentifierNameSyntax:
                        return model.GetSymbolInfo(expression, ct).Symbol as IParameterSymbol;
                    case MemberAccessExpressionSyntax access:
                        expression = access.Expression;
                        break;
                    case ElementAccessExpressionSyntax element:
                        expression = element.Expression;
                        break;
                    case ParenthesizedExpressionSyntax parenthesized:
                        expression = parenthesized.Expression;
                        break;
                    default:
                        return null;
                }
            }
        }

        private static string Generate(INamedTypeSymbol jobType, IMethodSymbol execute, bool isNativeTranspiled, bool[] defensiveCopyParams)
        {
            string jobFullName = jobType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string adapterName = $"__EntJoy_IJobEntityAdapter_{Sanitize(jobType.ToDisplayString())}";
            string namespaceName = jobType.ContainingNamespace?.IsGlobalNamespace == false
                ? jobType.ContainingNamespace.ToDisplayString()
                : string.Empty;
            var entityParams = execute.Parameters.Where(p => IsEntityParam(p)).ToList();
            // 列基址在 Chunk 级取一次，循环内只做索引偏移：逐实体重读 Span 基址会多出两次加载。
            // 只读列在边界处转成可写 ref：方法形参用 ref 传递是热路径性能的必要条件。
            var componentDeclsSb = new StringBuilder();
            var baseParams = new List<string>();
            var baseArgs = new List<string>();
            for (int i = 0; i < execute.Parameters.Length; i++)
            {
                var parameter = execute.Parameters[i];
                if (IsEntityParam(parameter)) continue;
                string type = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                bool readOnly = IsRelationParam(parameter)
                    || parameter.RefKind == RefKind.In
                    || parameter.RefKind == RefKind.RefReadOnlyParameter;
                string method = readOnly ? "GetComponentReadOnlyDataSpan" : "GetComponentDataSpan";
                componentDeclsSb.AppendLine($"            var {parameter.Name} = __chunk.{method}<{type}>();");
                componentDeclsSb.AppendLine(readOnly
                    ? $"            ref {type} __ejBase{i} = ref Unsafe.AsRef(in MemoryMarshal.GetReference({parameter.Name}));"
                    : $"            ref {type} __ejBase{i} = ref MemoryMarshal.GetReference({parameter.Name});");
                baseParams.Add($"ref {type} __ejBase{i}");
                baseArgs.Add($"ref __ejBase{i}");
            }
            string componentDecls = componentDeclsSb.ToString().TrimEnd('\r', '\n');
            string inlinedBody = BuildInlinedBody(jobType, execute, "Job", defensiveCopyParams);
            string methodBody = BuildInlinedBody(jobType, execute, "__job", defensiveCopyParams);

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("using System;");
            sb.AppendLine("using EntJoy.ECS;");
            sb.AppendLine("using EntJoy.ECS.JobSystem;");
            sb.AppendLine("using EntJoy.JobSystem;");
            sb.AppendLine("using System.Runtime.CompilerServices;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine($"namespace {namespaceName}");
                sb.AppendLine("{");
            }

            // 生成 JobExtensions（统一入口）
            sb.AppendLine("public static partial class JobExtensions");
            sb.AppendLine("{");
            if (isNativeTranspiled)
            {
                // [NativeTranspile]：路由到原生路径（world 默认 DefaultWorld，多 World 可显式传入）
                sb.AppendLine($"    public static JobHandle Schedule(this {jobFullName} job, QueryBuilder query, World world = null, JobHandle dependsOn = default)");
                sb.AppendLine("    {");
                sb.AppendLine($"        return NativeTranspiler.Bindings.NativeExports.Schedule_{jobType.Name}(ref job, query, dependsOn, world);");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    public static JobHandle ScheduleWithWorkerCap(this {jobFullName} job, QueryBuilder query, int workerCap, World world = null, JobHandle dependsOn = default)");
                sb.AppendLine("    {");
                sb.AppendLine($"        return NativeTranspiler.Bindings.NativeExports.ScheduleWithWorkerCap_{jobType.Name}(ref job, query, workerCap, dependsOn, world);");
                sb.AppendLine("    }");
                sb.AppendLine();
                // ScheduleWithWorkerCapAndRangeSize：原生后端（Cpp/Ispc）均已在 NativeExports 生成
                // ScheduleWithWorkerCapAndRangeSize_{Name}，此处补齐用户域扩展方法（见 docs EntJoy20260820-ai聊天.md：
                // workerCap + rangeSize 手动指定调度粒度）。
                sb.AppendLine($"    public static JobHandle ScheduleWithWorkerCapAndRangeSize(this {jobFullName} job, QueryBuilder query, int workerCap, int rangeSize, World world = null, JobHandle dependsOn = default)");
                sb.AppendLine("    {");
                sb.AppendLine($"        return NativeTranspiler.Bindings.NativeExports.ScheduleWithWorkerCapAndRangeSize_{jobType.Name}(ref job, query, workerCap, rangeSize, dependsOn, world);");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    public static void Run(this {jobFullName} job, QueryBuilder query, World world = null)");
                sb.AppendLine("    {");
                // RunImmediate_*：C++ ImmediateNative 主线程直接执行（零 worker 唤醒、无 handle 往返）
                sb.AppendLine($"        NativeTranspiler.Bindings.NativeExports.RunImmediate_{jobType.Name}(ref job, query, world);");
                sb.AppendLine("    }");
            }
            else
            {
                // 非 [NativeTranspile]：生成 IJobChunk 适配器 + 托管路径（world 默认 DefaultWorld）
                sb.AppendLine($"    public static JobHandle Schedule(this {jobFullName} job, QueryBuilder query, World world = null, JobHandle dependsOn = default)");
                sb.AppendLine("    {");
                sb.AppendLine($"        return ChunkJobExtensions.Schedule(new {adapterName} {{ Job = job }}, query, world, dependsOn);");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    public static JobHandle ScheduleWithWorkerCap(this {jobFullName} job, QueryBuilder query, int workerCap, World world = null, JobHandle dependsOn = default)");
                sb.AppendLine("    {");
                sb.AppendLine($"        return ChunkJobExtensions.ScheduleWithWorkerCap(new {adapterName} {{ Job = job }}, query, workerCap, world, dependsOn);");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    public static void Run(this {jobFullName} job, QueryBuilder query, World world = null)");
                sb.AppendLine("    {");
                sb.AppendLine($"        ChunkJobExtensions.Run(new {adapterName} {{ Job = job }}, query, world);");
                sb.AppendLine("    }");
            }
            sb.AppendLine("}");
            sb.AppendLine();

            // 只为非 NativeTranspiled 生成 IJobChunk 适配器
            if (!isNativeTranspiled)
            {
                sb.AppendLine($"internal unsafe struct {adapterName} : IJobChunk");
                sb.AppendLine("{");
                sb.AppendLine($"    public {jobFullName} Job;");
                sb.AppendLine();
                sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]");
                sb.AppendLine("    public void Execute(ArchetypeChunk __chunk, in ChunkEnabledMask __enabledMask)");
                sb.AppendLine("    {");
                sb.AppendLine(componentDecls);
                if (entityParams.Count != 0)
                {
                    sb.AppendLine("        var __entities = __chunk.GetEntitySpan();");
                    sb.AppendLine("        ref Entity __ejEntityBase = ref MemoryMarshal.GetReference(__entities);");
                }
                var firstComponent = execute.Parameters.FirstOrDefault(p => !IsEntityParam(p));
                string countExpression = firstComponent != null ? $"{firstComponent.Name}.Length" : "__entities.Length";
                sb.AppendLine($"        int __count = {countExpression};");
                // 无过滤（enabledMask.Length == 0）：整块交给独立方法，循环在方法内递减迭代。
                var bodyCallArgs = new List<string> { "ref Job" };
                bodyCallArgs.AddRange(baseArgs);
                if (entityParams.Count != 0) bodyCallArgs.Add("ref __ejEntityBase");
                bodyCallArgs.Add("__count");
                sb.AppendLine("        if (__enabledMask.Length == 0)");
                sb.AppendLine("        {");
                sb.AppendLine($"            __EntJoyBody({string.Join(", ", bodyCallArgs)});");
                sb.AppendLine("            return;");
                sb.AppendLine("        }");
                // 有过滤：按位图字推进，位图字与基址留在寄存器；长启用区间整段交给 __EntJoyBody。
                var runCallArgs = new List<string> { "ref Job" };
                runCallArgs.AddRange(baseArgs.Select(a => $"ref Unsafe.Add(ref {a.Substring(4)}, __idx)"));
                if (entityParams.Count != 0) runCallArgs.Add("ref Unsafe.Add(ref __ejEntityBase, __idx)");
                runCallArgs.Add("__run");
                sb.AppendLine("        Span<ulong> __bits = __enabledMask.Bits;");
                sb.AppendLine("        int __words = __bits.Length;");
                sb.AppendLine("        for (int __u = 0; __u < __words; __u++)");
                sb.AppendLine("        {");
                sb.AppendLine("            ulong __m = __bits[__u];");
                sb.AppendLine("            if (__m == 0) continue;");
                sb.AppendLine("            int __base = __u << 6;");
                // 全部为孤立置位（交替启用）时无需区间计算，直接逐位推进。
                sb.AppendLine("            if ((__m & (__m >> 1)) == 0)");
                sb.AppendLine("            {");
                sb.AppendLine("                while (__m != 0)");
                sb.AppendLine("                {");
                sb.AppendLine("                    int __idx = __base + System.Numerics.BitOperations.TrailingZeroCount(__m);");
                sb.AppendLine("                    if (__idx >= __count) { __u = __words; break; }");
                if (!string.IsNullOrEmpty(inlinedBody))
                    sb.Append(inlinedBody);
                sb.AppendLine("                    __m &= __m - 1;");
                sb.AppendLine("                }");
                sb.AppendLine("                continue;");
                sb.AppendLine("            }");
                sb.AppendLine("            while (__m != 0)");
                sb.AppendLine("            {");
                sb.AppendLine("                int __off = System.Numerics.BitOperations.TrailingZeroCount(__m);");
                sb.AppendLine("                int __idx = __base + __off;");
                sb.AppendLine("                if (__idx >= __count) { __u = __words; break; }");
                sb.AppendLine("                int __run = System.Numerics.BitOperations.TrailingZeroCount(~(__m >> __off));");
                sb.AppendLine("                if (__run > __count - __idx) __run = __count - __idx;");
                sb.AppendLine($"                if (__run >= {MaskedRunBodyThreshold})");
                sb.AppendLine("                {");
                sb.AppendLine($"                    __EntJoyBody({string.Join(", ", runCallArgs)});");
                sb.AppendLine("                    ulong __clear = __run >= 64 ? ulong.MaxValue : (1UL << __run) - 1;");
                sb.AppendLine("                    __m &= ~(__clear << __off);");
                sb.AppendLine("                    continue;");
                sb.AppendLine("                }");
                if (!string.IsNullOrEmpty(inlinedBody))
                    sb.Append(inlinedBody);
                sb.AppendLine("                __m &= __m - 1;");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine();
            // 逐实体循环独立成非内联方法，递减迭代；内联展开会让热路径变慢，改动前需重测。
                var bodyParams = new List<string> { $"ref {jobFullName} __job" };
                bodyParams.AddRange(baseParams);
                if (entityParams.Count != 0) bodyParams.Add("ref Entity __ejEntityBase");
                bodyParams.Add("int __count");
                sb.AppendLine("    [MethodImpl(MethodImplOptions.NoInlining)]");
                sb.AppendLine($"    private static void __EntJoyBody({string.Join(", ", bodyParams)})");
                sb.AppendLine("    {");
                sb.AppendLine("        for (int __idx = __count - 1; __idx >= 0; __idx--)");
                sb.AppendLine("        {");
                if (!string.IsNullOrEmpty(methodBody))
                    sb.Append(methodBody);
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine("}");
            }
            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        private static string BuildInlinedBody(INamedTypeSymbol jobType, IMethodSymbol execute, string jobRefName, bool[] defensiveCopyParams)
        {
            // 取 Execute 方法体，执行"managed 源码级内联"：
            //   形参 p/v (in/ref 组件)  ->  __c{i}[__idx]
            //   Job 自定义字段 (DeltaTime) -> <jobRefName>.DeltaTime（内联后字段在适配器的 Job 字段里）
            //   this -> <jobRefName>；void return; -> continue; （否则第一实体后整个 Execute 退出，破坏循环）
            if (execute.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not MethodDeclarationSyntax ms)
                return "";
            var body = ms.Body ?? (ms.ExpressionBody != null
                ? SyntaxFactory.Block(ms.ExpressionBody.Expression is ThrowExpressionSyntax thrown
                    ? (StatementSyntax)SyntaxFactory.ThrowStatement(thrown.Expression)
                    : SyntaxFactory.ExpressionStatement(ms.ExpressionBody.Expression))
                : null);
            if (body == null) return "";

            var paramMap = new Dictionary<string, string>();
            for (int i = 0; i < execute.Parameters.Length; i++)
            {
                if (IsEntityParam(execute.Parameters[i])) continue;
                paramMap[execute.Parameters[i].Name] = execute.Parameters[i].RefKind == RefKind.None
                    ? $"__componentValue_{i}"
                    : defensiveCopyParams[i] ? $"__ejRef{i}" : $"Unsafe.Add(ref __ejBase{i}, __idx)";
            }

            var localNames = new HashSet<string>();
            foreach (var node in body.DescendantNodes())
            {
                switch (node)
                {
                    case LocalDeclarationStatementSyntax ld:
                        foreach (var v in ld.Declaration.Variables) localNames.Add(v.Identifier.Text);
                        break;
                    case ForStatementSyntax f when f.Declaration != null:
                        foreach (var v in f.Declaration.Variables) localNames.Add(v.Identifier.Text);
                        break;
                    case ForEachStatementSyntax fe:
                        localNames.Add(fe.Identifier.Text);
                        break;
                }
            }

            var fieldNames = new HashSet<string>(
                jobType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Select(f => f.Name));

            var rewriter = new InlineBodyRewriter(paramMap, localNames, fieldNames, jobRefName);
            var rewritten = (BlockSyntax)rewriter.Visit(body)!;

            var sb = new StringBuilder();
            const string indent = "            "; // 12 空格，位于生成的外层 for 内
            // 形参以 Unsafe.Add(ref __ejBase{i}, __idx) 表达式代入；循环内声明 ref 局部会让热路径变慢。
            // 需要防御性副本语义的只读参数例外，用 ref readonly 局部访问。
            for (int i = 0; i < execute.Parameters.Length; i++)
            {
                var parameter = execute.Parameters[i];
                if (IsEntityParam(parameter))
                    sb.Append(indent).AppendLine($"var {parameter.Name} = Unsafe.Add(ref __ejEntityBase, __idx);");
                else if (defensiveCopyParams[i])
                    sb.Append(indent).AppendLine($"ref readonly {parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} __ejRef{i} = ref Unsafe.Add(ref __ejBase{i}, __idx);");
            }
            for (int i = 0; i < execute.Parameters.Length; i++)
                if (!IsEntityParam(execute.Parameters[i]) && execute.Parameters[i].RefKind == RefKind.None)
                    sb.Append(indent).AppendLine($"var __componentValue_{i} = Unsafe.Add(ref __ejBase{i}, __idx);");
            foreach (var stmt in rewritten.Statements)
            {
                var text = stmt.NormalizeWhitespace().ToFullString();
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                    sb.Append(indent).AppendLine(line);
            }
            return sb.ToString();
        }

        private sealed class InlineBodyRewriter : CSharpSyntaxRewriter
        {
            private readonly Dictionary<string, string> _paramMap;
            private readonly HashSet<string> _localNames;
            private readonly HashSet<string> _fieldNames;
            private readonly string _jobRefName;

            public InlineBodyRewriter(Dictionary<string, string> paramMap, HashSet<string> localNames, HashSet<string> fieldNames, string jobRefName)
            {
                _paramMap = paramMap;
                _localNames = localNames;
                _fieldNames = fieldNames;
                _jobRefName = jobRefName;
            }

            public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
            {
                // 跳过成员访问/绑定/命名实参的"名称"侧，避免把 .Value / 具名实参 误替换
                if (node.Parent is MemberAccessExpressionSyntax mem && ReferenceEquals(mem.Name, node)) return node;
                if (node.Parent is MemberBindingExpressionSyntax) return node;
                if (node.Parent is NameColonSyntax) return node;
                if (node.Identifier.Text == "this")
                    return SyntaxFactory.ParseExpression(_jobRefName).WithTriviaFrom(node);

                if (_localNames.Contains(node.Identifier.Text)) return node;

                if (_paramMap.TryGetValue(node.Identifier.Text, out var repl))
                    return SyntaxFactory.ParseExpression(repl).WithTriviaFrom(node);

                if (_fieldNames.Contains(node.Identifier.Text))
                    return SyntaxFactory.ParseExpression(_jobRefName + "." + node.Identifier.Text).WithTriviaFrom(node);

                return node;
            }

            public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
            {
                if (node.Expression == null)
                    return SyntaxFactory.ContinueStatement().WithTriviaFrom(node);
                return node;
            }
        }

        private static string Sanitize(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        /// <summary>DOTS 式 IJobEntity 的 Entity 参数（按值传，非组件列）。</summary>
        private static bool IsEntityParam(IParameterSymbol p) =>
            p.Type.Name == "Entity" &&
            p.Type.ContainingNamespace?.ToDisplayString() == "EntJoy.ECS";

        private static bool IsRelationParam(IParameterSymbol parameter) =>
            parameter.Type.AllInterfaces.Any(type => type.ToDisplayString() == "EntJoy.ECS.IRelationComponent");
        private sealed class JobCandidate
        {
            public JobCandidate(INamedTypeSymbol jobType, IMethodSymbol execute, bool isNativeTranspiled, bool[] defensiveCopyParams)
            {
                JobType = jobType;
                Execute = execute;
                IsNativeTranspiled = isNativeTranspiled;
                DefensiveCopyParams = defensiveCopyParams;
            }

            public INamedTypeSymbol JobType { get; }
            public IMethodSymbol Execute { get; }
            public bool IsNativeTranspiled { get; }
            public bool[] DefensiveCopyParams { get; }
        }
    }
}
