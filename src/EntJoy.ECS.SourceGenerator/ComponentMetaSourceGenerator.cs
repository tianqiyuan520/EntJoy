#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EntJoy.ECS.SourceGenerator
{
    /// <summary>
    /// 组件元数据生成器：扫描 IComponentData / [ECSComponent] 组件的字段（递归展开嵌套 struct），
    /// 生成 ComponentMeta 注册代码（[ModuleInitializer] 自动注册，Unsafe.ByteOffset 算偏移，AOT 安全无反射）。
    /// 供序列化 / 数据导航 / 调试共用。
    /// </summary>
    internal sealed class ComponentMetaSourceGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var provider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCandidate(node),
                    transform: static (ctx, ct) => Transform(ctx, ct));

            context.RegisterSourceOutput(provider, static (spc, result) =>
            {
                if (result is null) return;
                spc.AddSource(result.HintName, SourceText.From(result.Source, Encoding.UTF8));
            });
        }

        private static bool IsCandidate(SyntaxNode node)
        {
            return node is StructDeclarationSyntax s &&
                   (s.BaseList != null || s.AttributeLists.Count > 0);
        }

        private static MetaResult? Transform(GeneratorSyntaxContext context, System.Threading.CancellationToken ct)
        {
            if (context.Node is not StructDeclarationSyntax)
                return null;
            var model = context.SemanticModel;
            if (model.GetDeclaredSymbol(context.Node, ct) is not INamedTypeSymbol typeSymbol)
                return null;

            // 是 ECS 组件？（实现 IComponentData 或带 [ECSComponent]）
            bool isComponent =
                typeSymbol.AllInterfaces.Any(i =>
                    i.Name == Config.IComponentData && i.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyECS) ||
                typeSymbol.GetAttributes().Any(a =>
                    a.AttributeClass?.Name == Config.ECSComponentAttribute &&
                    a.AttributeClass.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyECS);
            if (!isComponent)
                return null;

            // 跳过非 public 组件（嵌套 private 类型等，生成的顶层代码无法访问）
            if (typeSymbol.DeclaredAccessibility != Accessibility.Public)
                return null;

            // 递归收集叶子字段（内置类型 / enum；嵌套 struct 展开）
            var entries = new List<FieldEntry>();
            CollectFields(typeSymbol, "", entries, new HashSet<string>());
            if (entries.Count == 0)
                return null;

            string fullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return new MetaResult
            {
                HintName = $"{typeSymbol.Name}_Meta.g.cs",
                Source = Generate(typeSymbol, fullName, entries),
            };
        }

        private static void CollectFields(ITypeSymbol type, string prefix, List<FieldEntry> entries, HashSet<string> visited)
        {
            if (!visited.Add(type.ToDisplayString()))
                return;  // 防环（C# 值类型禁止循环包含，防御）

            foreach (var member in type.GetMembers())
            {
                if (member is not IFieldSymbol field || field.IsStatic || field.IsConst)
                    continue;

                // 跳过非 public 字段（如 NativeArray 的 private _buffer/_length，生成的顶层代码无法访问）
                if (field.DeclaredAccessibility != Accessibility.Public)
                    continue;

                string path = prefix.Length == 0 ? field.Name : prefix + "." + field.Name;

                // 指针字段（如 NativeArray._buffer）不可序列化，跳过
                if (field.Type is IPointerTypeSymbol)
                    continue;

                // enum → 底层整数类型
                if (field.Type.TypeKind == TypeKind.Enum)
                {
                    var underlying = ((INamedTypeSymbol)field.Type).EnumUnderlyingType;
                    if (TryMap(underlying, out var kind, out var keyword))
                        entries.Add(new FieldEntry { Path = path, TypeKeyword = keyword, Kind = kind });
                    continue;
                }

                // 内置值类型
                if (field.Type.SpecialType != SpecialType.None && TryMap(field.Type, out var sk, out var skw))
                {
                    entries.Add(new FieldEntry { Path = path, TypeKeyword = skw, Kind = sk });
                    continue;
                }

                // 嵌套 struct → 递归展开
                if (field.Type.TypeKind == TypeKind.Struct)
                {
                    CollectFields(field.Type, path, entries, visited);
                }
                // 其他（class/interface/delegate）→ blittable 校验已拦，跳过
            }
        }

        private static bool TryMap(ITypeSymbol type, out string kind, out string keyword)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean: kind = "Bool"; keyword = "bool"; return true;
                case SpecialType.System_Byte: kind = "UInt8"; keyword = "byte"; return true;
                case SpecialType.System_SByte: kind = "Int8"; keyword = "sbyte"; return true;
                case SpecialType.System_Int16: kind = "Int16"; keyword = "short"; return true;
                case SpecialType.System_UInt16: kind = "UInt16"; keyword = "ushort"; return true;
                case SpecialType.System_Int32: kind = "Int32"; keyword = "int"; return true;
                case SpecialType.System_UInt32: kind = "UInt32"; keyword = "uint"; return true;
                case SpecialType.System_Int64: kind = "Int64"; keyword = "long"; return true;
                case SpecialType.System_UInt64: kind = "UInt64"; keyword = "ulong"; return true;
                case SpecialType.System_Single: kind = "Float32"; keyword = "float"; return true;
                case SpecialType.System_Double: kind = "Float64"; keyword = "double"; return true;
                case SpecialType.System_Char: kind = "Char"; keyword = "char"; return true;
                case SpecialType.System_Decimal: kind = "Decimal"; keyword = "decimal"; return true;
                default: kind = "Int32"; keyword = "int"; return false;
            }
        }

        private static string Generate(INamedTypeSymbol typeSymbol, string fullName, List<FieldEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using System.Runtime.CompilerServices;");
            sb.AppendLine("using EntJoy.ECS;");
            sb.AppendLine();
            sb.AppendLine($"internal static class {typeSymbol.Name}_Meta");
            sb.AppendLine("{");
            sb.AppendLine("    [ModuleInitializer]");
            sb.AppendLine("    internal static void Register()");
            sb.AppendLine("    {");
            sb.AppendLine("        ComponentMetaRegistry.Register(Create());");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private static ComponentMeta Create()");
            sb.AppendLine("    {");
            sb.AppendLine($"        var def = default({fullName});");
            sb.AppendLine($"        var fields = new ComponentFieldMeta[{entries.Count}];");
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.AppendLine($"        fields[{i}] = new ComponentFieldMeta");
                sb.AppendLine("        {");
                sb.AppendLine($"            Name = \"{e.Path}\",");
                sb.AppendLine($"            Offset = (int)Unsafe.ByteOffset(ref Unsafe.As<{fullName}, byte>(ref def), ref Unsafe.As<{e.TypeKeyword}, byte>(ref def.{e.Path})),");
                sb.AppendLine($"            Size = sizeof({e.TypeKeyword}),");
                sb.AppendLine($"            Kind = FieldKind.{e.Kind},");
                sb.AppendLine("        };");
            }
            sb.AppendLine("        return new ComponentMeta");
            sb.AppendLine("        {");
            sb.AppendLine($"            TypeId = ComponentTypeManager.GetComponentType(typeof({fullName})).Id,");
            sb.AppendLine($"            TypeName = nameof({fullName}),");
            sb.AppendLine($"            Size = Unsafe.SizeOf<{fullName}>(),");
            sb.AppendLine("            Fields = fields,");
            sb.AppendLine("        };");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private sealed class FieldEntry
        {
            public string Path = "";
            public string TypeKeyword = "";
            public string Kind = "";
        }

        private sealed class MetaResult
        {
            public string HintName = "";
            public string Source = "";
        }
    }
}
