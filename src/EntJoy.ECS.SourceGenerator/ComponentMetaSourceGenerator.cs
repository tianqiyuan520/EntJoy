#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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

            // 把本次编译的全部组件布局指纹写成清单（`NativeTranspiler_Generated\LayoutFingerprint.json`），
            // 再由 EntJoy.Jobs.targets 拷成 DLL 旁的 `NativeTranspiled.layout.json`。
            var withOptions = context.AnalyzerConfigOptionsProvider.Combine(provider.Collect());
            context.RegisterSourceOutput(withOptions, static (_, pair) => WriteLayoutManifest(pair.Left, pair.Right));
        }

        /// <summary>写布局清单（只在内容变化时落盘：重写会抖动时间戳，触发下游 CMake reconfigure 与重编）。</summary>
        private static void WriteLayoutManifest(
            Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider options,
            System.Collections.Immutable.ImmutableArray<MetaResult?> results)
        {
            try
            {
                var entries = new List<MetaResult>();
                foreach (var r in results)
                    if (r is not null) entries.Add(r);
                // 无组件也要产出（entries:[]）：让"本工程没有组件"成为明确声明，而不是"文件缺失"
                // —— 否则下游分不开这两者，守卫只能要么 fail-open 要么误拒。

                entries.Sort(static (a, b) => string.CompareOrdinal(a.LayoutTypeName, b.LayoutTypeName));
                string assemblyName = entries.Count > 0 ? (entries[0].AssemblyName ?? "") : "";
                if (assemblyName.Length == 0
                    && options.GlobalOptions.TryGetValue("build_property.msbuildprojectname", out var projectName)
                    && !string.IsNullOrEmpty(projectName))
                    assemblyName = projectName;
                var body = new StringBuilder();
                for (int i = 0; i < entries.Count; i++)
                {
                    if (i > 0) body.Append(',');
                    body.Append("{\"type\":\"").Append(entries[i].LayoutTypeName)
                        .Append("\",\"hash\":\"0x").Append(entries[i].LayoutHash.ToString("X16")).Append("\"}");
                }
                string json = "{\"version\":1,\"assembly\":\"" + assemblyName + "\",\"entries\":[" + body + "]}";

                if (!options.GlobalOptions.TryGetValue("build_property.projectdir", out var projectDir)
                    || string.IsNullOrWhiteSpace(projectDir))
                    return;
                string dir = Path.Combine(projectDir, "NativeTranspiler_Generated");
                string path = Path.Combine(dir, "LayoutFingerprint.json");
                // RS1035（分析器禁止文件 IO）有意收窄屏蔽：这份清单是构建产物，与 NativeTranspiler
                // 写 CMakeLists/*.cpp 同一类。内容不变则不落盘。
#pragma warning disable RS1035
                Directory.CreateDirectory(dir);
                if (!File.Exists(path) || File.ReadAllText(path) != json) File.WriteAllText(path, json);
#pragma warning restore RS1035
            }
            catch
            {
                // 生成器不应因写清单失败而打断编译；失败只表现为"清单缺失" ⇒ 重载时由守卫显式拒绝并说明。
            }
        }

        /// <summary>
        /// 递归收集叶子字段（内置类型 / enum；嵌套 struct 展开）。
        /// 只往 <paramref name="entries"/> 写**叶子字段**（`Generate` 会把它发成 `ComponentFieldMeta[]`）。
        /// </summary>
        private static void CollectFields(ITypeSymbol type, string prefix, List<FieldEntry> entries)
        {
            // 无 visited 去重：C# 编译器禁止值类型循环包含（CS0523），不会无限递归；
            // 全局 visited 按类型名去重会误伤「同一组件含两个同类型嵌套 struct 字段」的场景（第二个字段的叶子项被跳过）。
            foreach (var member in type.GetMembers())
            {
                if (member is not IFieldSymbol field || field.IsStatic || field.IsConst)
                    continue;

                // 跳过非 public 字段（如 NativeArray 的 private _buffer/_length，生成的顶层代码无法访问）
                if (field.DeclaredAccessibility != Accessibility.Public)
                    continue;

                string path = prefix.Length == 0 ? field.Name : prefix + "." + field.Name;

                // 指针字段（含 fixed 缓冲）不可序列化，跳过 —— 与既有元数据语义保持一致
                if (field.Type is IPointerTypeSymbol)
                    continue;

                // enum → 底层整数类型（TypeKeyword 用枚举全名，Unsafe.As/sizeof 才能与 ref 字段类型匹配；
                // Kind 仍映射底层类型，保证 FieldKind 语义正确）
                if (field.Type.TypeKind == TypeKind.Enum)
                {
                    var underlying = ((INamedTypeSymbol)field.Type).EnumUnderlyingType;
                    if (underlying is not { } ut || !TryMap(ut, out var kind, out _)) continue;
                    entries.Add(new FieldEntry { Path = path, TypeKeyword = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), Kind = kind });
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
                    CollectFields(field.Type, path, entries);
                }
                // 其他（class/interface/delegate）→ blittable 校验已拦，跳过
            }
        }

        /// <summary>
        /// FNV-1a 64 位。输入 = 类型全名 + 按序的 `字段路径:类型关键字:Kind`。
        /// 这是**变化检测器**（回答"两次构建之间字段布局变了没有"），不追求等价于 CLR 的 offset/size，
        /// 也**看不见** `Pack`/`FieldOffset`/`fixed`/私有字段的变化（已知取舍）。
        /// </summary>
        private static ulong ComputeLayoutHash(string fullName, List<FieldEntry> entries)
        {
            var sb = new StringBuilder(fullName);
            foreach (var e in entries)
                sb.Append('|').Append(e.Path).Append(':').Append(e.TypeKeyword).Append(':').Append(e.Kind);
            ulong h = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(sb.ToString()))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h;
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

            // 泛型组件：生成代码无法引用未定义的 T（CS0246），跳过；[ECSComponent] 路径由 EJ2003 诊断
            if (typeSymbol.TypeParameters.Length > 0)
                return null;

            // 递归收集叶子字段（内置类型 / enum；嵌套 struct 展开）
            var entries = new List<FieldEntry>();
            CollectFields(typeSymbol, "", entries);
            if (entries.Count == 0)
                return null;

            string fullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            ulong layoutHash = ComputeLayoutHash(fullName, entries);
            return new MetaResult
            {
                HintName = $"{SanitizeHintName(fullName)}_Meta.g.cs",
                Source = Generate(typeSymbol, fullName, entries, layoutHash),
                LayoutHash = layoutHash,
                LayoutTypeName = fullName,
                AssemblyName = context.SemanticModel.Compilation.AssemblyName ?? "",
            };
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

        private static string Generate(INamedTypeSymbol typeSymbol, string fullName, List<FieldEntry> entries, ulong layoutHash)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using System.Runtime.CompilerServices;");
            sb.AppendLine("using EntJoy.ECS;");
            sb.AppendLine();
            // 类型名用全限定名 sanitize，避免跨命名空间同名组件生成同名类 → CS0101
            sb.AppendLine($"internal static class {SanitizeHintName(fullName)}_Meta");
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
            sb.AppendLine($"            TypeName = \"{typeSymbol.Name}\",");
            sb.AppendLine($"            Size = Unsafe.SizeOf<{fullName}>(),");
            sb.AppendLine("            Fields = fields,");
            // 布局指纹（**生成期**算好的字面量；不参与运行时哈希 ⇒ 零成本、无分歧）。
            sb.AppendLine($"            LayoutHash = 0x{layoutHash:X16}UL,");
            sb.AppendLine($"            LayoutTypeName = \"{fullName}\",");
            sb.AppendLine("        };");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>把全限定名转成安全的 HintName 片段（非字母数字下划线 → 下划线），消除同名组件跨命名空间的 HintName 冲突。</summary>
        private static string SanitizeHintName(string fullName)
        {
            var sb = new StringBuilder(fullName.Length);
            foreach (char c in fullName)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
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
            /// <summary>布局指纹（生成期算好的字面量）与其清单键（全限定类型名）。</summary>
            public ulong LayoutHash;
            public string LayoutTypeName = "";
            /// <summary>产出该组件的程序集简单名（清单的作用域；与运行时 `Type.Assembly.GetName().Name` 对齐）。</summary>
            public string AssemblyName = "";
        }
    }
}
