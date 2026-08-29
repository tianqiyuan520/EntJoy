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
    /// [ECSComponent] partial struct → 补齐 IComponentData 接口的生成器。
    /// 用户声明 <c>[ECSComponent] public partial struct Position { ... }</c>，
    /// 本生成器输出 <c>public partial struct Position : IComponentData { }</c>，
    /// 使所有要求 IComponentData 约束的 API（Set/GetComponent/DCB.AddComponent）可用。
    /// </summary>
    internal sealed class ECSComponentSourceGenerator : IIncrementalGenerator
    {
        private const string Suffix = "_ECSComponent.g.cs";

        private static readonly DiagnosticDescriptor EJ2001 = new(
            "EJ2001", "ECS component must be a partial struct",
            "Type '{0}' marked with [ECSComponent] must be a partial struct (add the 'partial' keyword)",
            "EntJoy.ECS.SourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor EJ2002 = new(
            "EJ2002", "ECS component must be blittable",
            "Component '{0}' contains managed reference field '{1}'; ECS components must be blittable (no reference type fields)",
            "EntJoy.ECS.SourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor EJ2003 = new(
            "EJ2003", "ECS component cannot be generic",
            "[ECSComponent] cannot be applied to generic struct '{0}'",
            "EntJoy.ECS.SourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var provider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCandidate(node),
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

        /// <summary>语法预筛：struct 声明带 [ECSComponent]（或 [ECSComponentAttribute] / 全限定）。</summary>
        private static bool IsCandidate(SyntaxNode node)
        {
            if (node is not StructDeclarationSyntax s || s.AttributeLists.Count == 0)
                return false;

            foreach (var attrList in s.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    string name = attr.Name.ToString();
                    if (name == "ECSComponent" ||
                        name == "ECSComponentAttribute" ||
                        name.EndsWith(".ECSComponent", StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        private static ComponentResult? Transform(GeneratorSyntaxContext context, CancellationToken ct)
        {
            if (context.Node is not StructDeclarationSyntax structDecl)
                return null;

            var model = context.SemanticModel;
            if (model.GetDeclaredSymbol(structDecl, ct) is not INamedTypeSymbol typeSymbol)
                return null;

            // 语义确认：特性必须是 EntJoy.ECS.ECSComponentAttribute（防同名特性误判）
            bool hasAttr = typeSymbol.GetAttributes().Any(a =>
                a.AttributeClass?.Name == Config.ECSComponentAttribute &&
                a.AttributeClass.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyECS);
            if (!hasAttr)
                return null;

            var result = new ComponentResult();

            // EJ2003：泛型 struct
            if (typeSymbol.TypeParameters.Length > 0)
            {
                result.Diagnostics.Add(Diagnostic.Create(EJ2003, structDecl.Identifier.GetLocation(), typeSymbol.Name));
                return result;
            }

            // 已实现任一 ECS 组件接口 → 跳过（幂等，不重复补接口）
            if (ImplementsAnyComponentInterface(typeSymbol))
                return null;

            // EJ2001：非 partial（不输出补丁，避免 CS0260 噪声）
            if (!structDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                result.Diagnostics.Add(Diagnostic.Create(EJ2001, structDecl.Identifier.GetLocation(), typeSymbol.Name));
                return result;
            }

            // EJ2002：含托管引用字段（非 blittable）
            if (!IsBlittable(typeSymbol, new HashSet<string>(), out string? offending))
            {
                result.Diagnostics.Add(Diagnostic.Create(EJ2002, structDecl.Identifier.GetLocation(), typeSymbol.Name, offending));
                return result;
            }

            result.HintName = $"{typeSymbol.Name}{Suffix}";
            result.Source = Generate(typeSymbol);
            return result;
        }

        /// <summary>已实现任一 ECS 组件标记接口（IComponentData/ISharedComponentData/IRelationComponent/IEnableableComponent）。</summary>
        private static bool ImplementsAnyComponentInterface(INamedTypeSymbol typeSymbol)
        {
            foreach (var iface in typeSymbol.AllInterfaces)
            {
                if (iface.ContainingNamespace?.ToDisplayString() != Config.NamespaceEntJoyECS)
                    continue;
                string name = iface.Name;
                if (name == "IComponentData" || name == "ISharedComponentData" ||
                    name == "IRelationComponent" || name == "IEnableableComponent")
                    return true;
            }
            return false;
        }

        /// <summary>递归判定类型是否 blittable（无托管引用字段）。offending 返回违规字段路径。</summary>
        private static bool IsBlittable(ITypeSymbol type, ISet<string> visited, out string? offending)
        {
            offending = null;

            if (type is IPointerTypeSymbol)
                return true; // 非托管指针
            if (type is ITypeParameterSymbol)
                return false; // 泛型参数（泛型 struct 已拒绝，防御）
            if (type is IArrayTypeSymbol)
            {
                offending = type.ToDisplayString();
                return false;
            }

            if (type.SpecialType != SpecialType.None)
            {
                // 原生类型 + decimal 均 blittable；string/object 除外
                if (type.SpecialType == SpecialType.System_String ||
                    type.SpecialType == SpecialType.System_Object)
                {
                    offending = type.ToDisplayString();
                    return false;
                }
                return true;
            }

            if (type.TypeKind == TypeKind.Enum)
                return true;

            if (type.TypeKind == TypeKind.Struct)
            {
                if (!visited.Add(type.ToDisplayString()))
                    return true; // 防环（理论上 C# 禁止 struct 循环包含）
                foreach (var member in type.GetMembers())
                {
                    if (member is IFieldSymbol field && !field.IsStatic && !field.IsConst)
                    {
                        if (!IsBlittable(field.Type, visited, out offending))
                        {
                            offending = $"{type.Name}.{field.Name}: {offending}";
                            return false;
                        }
                    }
                }
                return true;
            }

            // class / interface / delegate / dynamic / error
            offending = type.ToDisplayString();
            return false;
        }

        private static string Generate(INamedTypeSymbol typeSymbol)
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
            sb.AppendLine($"{indent}{accessibility} partial struct {typeSymbol.Name} : global::EntJoy.ECS.IComponentData");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}}}");

            if (hasNamespace)
                sb.AppendLine("}");

            return sb.ToString();
        }

        private sealed class ComponentResult
        {
            public string? HintName;
            public string? Source;
            public List<Diagnostic> Diagnostics = new();
        }
    }
}
