#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EntJoy.ECS.SourceGenerator
{
    /// <summary>
    /// [MultiRelation(MaxSlots = N)]（N ≥ 2）→ 注入 Slot1..SlotN-1 字段的生成器。
    /// 定长多槽列：TRel 占 chunk 列，列宽 = N × sizeof(RelationSlot)（如 4 槽 = 32B），
    /// 使 NativeTranspiler（IJobEntity/IJobChunk）按列宽步进直接访问——多值关系进 Job 的关键。
    /// 要求：partial struct + 首字段 RelationSlot Target。
    /// 输出：<c>public partial struct Skill { public RelationSlot Slot1; ... }</c>。
    /// </summary>
    internal sealed class MultiRelationSlotSourceGenerator : IIncrementalGenerator
    {
        private const string Suffix = "_MultiRelationSlots.g.cs";

        private static readonly DiagnosticDescriptor EJ3001 = new(
            "EJ3001", "Multi-relation slot type must be a partial struct",
            "Type '{0}' marked with [MultiRelation(MaxSlots = N)] (N >= 2) must be a partial struct (add the 'partial' keyword) so the slot fields can be injected",
            "EntJoy.ECS.SourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor EJ3002 = new(
            "EJ3002", "Multi-relation slot type must have a RelationSlot Target first field",
            "Type '{0}' marked with [MultiRelation(MaxSlots = N)] (N >= 2) must declare a 'public RelationSlot Target' field as its first field",
            "EntJoy.ECS.SourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor EJ3003 = new(
            "EJ3003", "Multi-relation slot type cannot be generic or nested",
            "Type '{0}' marked with [MultiRelation(MaxSlots = N)] cannot be generic or nested",
            "EntJoy.ECS.SourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var provider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is StructDeclarationSyntax,
                    transform: static (ctx, ct) => Transform(ctx, ct));

            context.RegisterSourceOutput(provider, static (spc, result) =>
            {
                if (result is null) return;
                foreach (var d in result.Diagnostics)
                    spc.ReportDiagnostic(d);
                if (result.Source is not null && result.HintName is not null)
                    spc.AddSource(result.HintName, SourceText.From(result.Source, Encoding.UTF8));
            });
        }

        private static SlotResult? Transform(GeneratorSyntaxContext context, CancellationToken ct)
        {
            if (context.Node is not StructDeclarationSyntax structDecl)
                return null;

            var model = context.SemanticModel;
            if (model.GetDeclaredSymbol(structDecl, ct) is not INamedTypeSymbol typeSymbol)
                return null;

            // 语义确认：[MultiRelation] 特性 + 读取 MaxSlots 参数
            AttributeData? multiAttr = null;
            foreach (var attr in typeSymbol.GetAttributes())
            {
                if (attr.AttributeClass?.Name == "MultiRelationAttribute" &&
                    attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "EntJoy.ECS")
                {
                    multiAttr = attr;
                    break;
                }
            }
            if (multiAttr == null)
                return null;

            int maxSlots = 0;
            foreach (var namedArg in multiAttr.NamedArguments)
            {
                if (namedArg.Key == "MaxSlots" && namedArg.Value.Value is int v)
                {
                    maxSlots = v;
                    break;
                }
            }
            if (maxSlots < 2)
                return null;   // 托管列表模式（MaxSlots=0/1），不注入

            var result = new SlotResult();

            // EJ3003：泛型 / 嵌套
            if (typeSymbol.TypeParameters.Length > 0 || typeSymbol.ContainingType != null)
            {
                result.Diagnostics.Add(Diagnostic.Create(EJ3003, structDecl.Identifier.GetLocation(), typeSymbol.Name));
                return result;
            }

            // EJ3001：非 partial
            if (!structDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                result.Diagnostics.Add(Diagnostic.Create(EJ3001, structDecl.Identifier.GetLocation(), typeSymbol.Name));
                return result;
            }

            // EJ3002：首字段必须是 RelationSlot Target（或已注入过 → 幂等跳过）
            if (!HasTargetFirstField(typeSymbol))
            {
                result.Diagnostics.Add(Diagnostic.Create(EJ3002, structDecl.Identifier.GetLocation(), typeSymbol.Name));
                return result;
            }
            // 已含 Slot1（手写 4 槽 / 已注入）→ 幂等跳过，避免重复注入
            if (typeSymbol.GetMembers().OfType<IFieldSymbol>().Any(f => !f.IsStatic && f.Name == "Slot1"))
                return null;

            result.HintName = $"{SanitizeHintName(typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))}{Suffix}";
            result.Source = Generate(typeSymbol, maxSlots);
            return result;
        }

        /// <summary>校验首字段为 RelationSlot Target；已含 Slot1 视为已注入（幂等）。</summary>
        private static bool HasTargetFirstField(INamedTypeSymbol typeSymbol)
        {
            var fields = typeSymbol.GetMembers().OfType<IFieldSymbol>()
                .Where(f => !f.IsStatic && !f.IsConst)
                .ToList();
            if (fields.Count == 0) return false;
            var first = fields[0];
            if (first.Name != "Target") return false;
            return first.Type.Name == "RelationSlot" &&
                   first.Type.ContainingNamespace?.ToDisplayString() == "EntJoy.ECS";
        }

        private static string SanitizeHintName(string fullName)
        {
            var sb = new StringBuilder(fullName.Length);
            foreach (char c in fullName)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString();
        }

        private static string Generate(INamedTypeSymbol typeSymbol, int maxSlots)
        {
            string accessibility = typeSymbol.DeclaredAccessibility switch
            {
                Accessibility.Public => "public",
                Accessibility.Internal => "internal",
                _ => "public",
            };

            var ns = typeSymbol.ContainingNamespace;
            bool hasNamespace = ns != null && !ns.IsGlobalNamespace;

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();

            if (hasNamespace)
            {
                sb.AppendLine($"namespace {ns!.ToDisplayString()}");
                sb.AppendLine("{");
            }

            string indent = hasNamespace ? "    " : "";
            sb.AppendLine($"{indent}{accessibility} partial struct {typeSymbol.Name}");
            sb.AppendLine($"{indent}{{");
            for (int i = 1; i < maxSlots; i++)
            {
                sb.AppendLine($"{indent}    /// <summary>定长多槽列：槽 {i}（{maxSlots} 槽中的第 {i + 1} 个）。空槽 = RelationSlot.Default。</summary>");
                sb.AppendLine($"{indent}    public global::EntJoy.ECS.RelationSlot Slot{i};");
            }
            sb.AppendLine($"{indent}}}");

            if (hasNamespace)
                sb.AppendLine("}");

            return sb.ToString();
        }

        private sealed class SlotResult
        {
            public string? HintName;
            public string? Source;
            public List<Diagnostic> Diagnostics = new();
        }
    }
}
