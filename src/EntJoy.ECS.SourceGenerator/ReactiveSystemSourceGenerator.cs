#nullable enable
using System;
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
    /// Reactive System 生成器：扫描带 [Reactive(ObserverEvents)] 的 struct，
    /// 从静态 Execute(in ReadOnlySpan&lt;Entity&gt;, in ReadOnlySpan&lt;TComponent&gt;) 推导组件类型，
    /// 生成 <c>ReactiveSystemRegistry.RegisterAll(World)</c> 自动注册 Observer 订阅，
    /// 消除手写 world.AddObserver&lt;T&gt;(...) 样板。
    /// </summary>
    internal sealed class ReactiveSystemSourceGenerator : IIncrementalGenerator
    {
        private const string OutputHintName = "ReactiveSystemRegistry.g.cs";

        private static readonly DiagnosticDescriptor EJ2011 = new(
            "EJ2011", "Reactive handler must define a static Execute method",
            "Type '{0}' marked with [Reactive] must define a static Execute method",
            "EntJoy.ECS.SourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor EJ2012 = new(
            "EJ2012", "Reactive Execute must have the signature (in ReadOnlySpan<Entity>, in ReadOnlySpan<TComponent>)",
            "Type '{0}' Execute must have the signature 'static void Execute(in ReadOnlySpan<Entity>, in ReadOnlySpan<TComponent>)'",
            "EntJoy.ECS.SourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var provider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCandidate(node),
                    transform: static (ctx, ct) => Transform(ctx, ct))
                .Where(static r => r != null);

            var collected = provider.Collect();

            context.RegisterSourceOutput(collected, static (spc, results) =>
            {
                var registrations = new List<(string Handler, string Component, string Events)>();
                foreach (var result in results)
                {
                    if (result is null) continue;
                    foreach (var d in result.Diagnostics)
                        spc.ReportDiagnostic(d);
                    registrations.AddRange(result.Registrations);
                }

                if (registrations.Count == 0)
                    return;

                var sorted = registrations
                    .OrderBy(r => r.Handler, StringComparer.Ordinal)
                    .ThenBy(r => r.Events, StringComparer.Ordinal)
                    .ToList();

                spc.AddSource(OutputHintName, SourceText.From(Generate(sorted), Encoding.UTF8));
            });
        }

        /// <summary>语法预筛：struct 声明带 [Reactive]（或 [ReactiveAttribute] / 全限定）。</summary>
        private static bool IsCandidate(SyntaxNode node)
        {
            if (node is not StructDeclarationSyntax s || s.AttributeLists.Count == 0)
                return false;

            foreach (var attrList in s.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    string name = attr.Name.ToString();
                    if (name == "Reactive" ||
                        name == "ReactiveAttribute" ||
                        name.EndsWith(".Reactive", StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        private static ReactiveResult? Transform(GeneratorSyntaxContext context, CancellationToken ct)
        {
            if (context.Node is not StructDeclarationSyntax structDecl)
                return null;

            var model = context.SemanticModel;
            if (model.GetDeclaredSymbol(structDecl, ct) is not INamedTypeSymbol typeSymbol)
                return null;

            // 收集语义确认的 [Reactive] 特性（防同名特性误判）
            var reactiveAttrs = typeSymbol.GetAttributes()
                .Where(a => a.AttributeClass?.Name == Config.ReactiveAttribute &&
                            a.AttributeClass.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyECS)
                .ToList();
            if (reactiveAttrs.Count == 0)
                return null;

            var result = new ReactiveResult();

            // 静态 Execute 方法
            var execute = typeSymbol.GetMembers(Config.Execute)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.IsStatic);
            if (execute == null)
            {
                result.Diagnostics.Add(Diagnostic.Create(EJ2011, structDecl.Identifier.GetLocation(), typeSymbol.Name));
                return result;
            }

            // 签名验证：(ReadOnlySpan<Entity>, ReadOnlySpan<TComponent>) → void
            if (!TryGetExecuteComponentType(execute, out string? componentFullName))
            {
                result.Diagnostics.Add(Diagnostic.Create(EJ2012, execute.Locations.FirstOrDefault() ?? structDecl.Identifier.GetLocation(), typeSymbol.Name));
                return result;
            }

            string handlerFullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            foreach (var attr in reactiveAttrs)
            {
                // 特性参数：ObserverEvents 枚举常量 → 事件位 int
                if (attr.ConstructorArguments.Length == 0 ||
                    attr.ConstructorArguments[0].Value is not int eventsValue)
                    continue;

                string eventsExpr = BuildEventsExpression(eventsValue);
                result.Registrations.Add((handlerFullName, componentFullName!, eventsExpr));
            }

            return result;
        }

        /// <summary>解析 Execute 第二个参数 ReadOnlySpan&lt;T&gt; 的 T（组件类型），并验证签名形态。</summary>
        private static bool TryGetExecuteComponentType(IMethodSymbol execute, out string? componentFullName)
        {
            componentFullName = null;

            if (execute.Parameters.Length != 2 || !execute.ReturnsVoid)
                return false;

            if (!IsReadOnlySpanOf(execute.Parameters[0].Type, "Entity"))
                return false;

            if (execute.Parameters[1].Type is not INamedTypeSymbol spanType ||
                spanType.Name != Config.ReadOnlySpan ||
                spanType.TypeArguments.Length != 1)
                return false;

            var componentType = spanType.TypeArguments[0];
            if (componentType is IErrorTypeSymbol)
                return false;

            componentFullName = componentType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return true;
        }

        private static bool IsReadOnlySpanOf(ITypeSymbol type, string elementName)
        {
            return type is INamedTypeSymbol named &&
                   named.Name == Config.ReadOnlySpan &&
                   named.TypeArguments.Length == 1 &&
                   named.TypeArguments[0].Name == elementName;
        }

        /// <summary>int 事件位 → ObserverEvents 枚举表达式（支持组合，如 Added | Removed）。</summary>
        private static string BuildEventsExpression(int eventsValue)
        {
            if (eventsValue == 0)
                return "ObserverEvents.None";

            var parts = new List<string>();
            // 按位分解：Added=1, Removed=2, Set=4, Destroyed=8
            foreach (var (bit, name) in new[] { (1, "Added"), (2, "Removed"), (4, "Set"), (8, "Destroyed") })
            {
                if ((eventsValue & bit) != 0)
                    parts.Add($"ObserverEvents.{name}");
            }
            return string.Join(" | ", parts);
        }

        private static string Generate(List<(string Handler, string Component, string Events)> registrations)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using EntJoy.ECS;");
            sb.AppendLine();
            sb.AppendLine("/// <summary>Reactive 处理器注册入口（由 ReactiveSystemSourceGenerator 生成）。</summary>");
            sb.AppendLine("public static class ReactiveSystemRegistry");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>注册本程序集内所有 [Reactive] 处理器的 Observer 订阅。</summary>");
            sb.AppendLine("    public static void RegisterAll(World world)");
            sb.AppendLine("    {");
            foreach (var r in registrations)
            {
                sb.AppendLine($"        world.AddObserver<{r.Component}>({r.Events}, (entities, values) =>");
                sb.AppendLine($"            {r.Handler}.Execute(entities, values));");
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private sealed class ReactiveResult
        {
            public List<Diagnostic> Diagnostics = new();
            public List<(string Handler, string Component, string Events)> Registrations = new();
        }
    }
}
