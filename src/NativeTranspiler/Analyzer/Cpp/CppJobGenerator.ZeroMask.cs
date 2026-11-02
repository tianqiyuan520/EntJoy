using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NativeTranspiler.Analyzer.Common;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    public static partial class CppJobGenerator
    {
        // 稳定的整数或枚举字段控制多个位开关时，为零值发一个特化；非零值仍走原始体。
        // 每个 job 最多选一个字段，不枚举位组合，不改变 ABI 或调度方式。
        private static IFieldSymbol? FindZeroMaskField(INamedTypeSymbol jobStruct,
            SemanticModel model, MethodDeclarationSyntax method)
        {
            if (method.Body == null) return null;
            var nodes = method.Body.DescendantNodes().ToArray();
            if (!nodes.Any(n => n is ForStatementSyntax || n is WhileStatementSyntax || n is DoStatementSyntax))
                return null;
            if (HasIndirectJobFieldWrites(jobStruct, model, nodes))
                return null;

            var use = GetFieldLoopUse(jobStruct, model);
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic || field.IsVolatile || !IsZeroMaskInteger(field.Type)
                    || use.Written.Contains(field.Name)) continue;
                var aliases = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { field };
                foreach (var local in nodes.OfType<VariableDeclaratorSyntax>())
                    if (local.Initializer != null
                        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(local.Initializer.Value).Symbol, field)
                        && model.GetDeclaredSymbol(local) is ILocalSymbol symbol)
                        aliases.Add(symbol);

                var masks = new HashSet<ulong>();
                foreach (var bitAnd in nodes.OfType<BinaryExpressionSyntax>().Where(b => b.IsKind(SyntaxKind.BitwiseAndExpression)))
                {
                    var value = model.GetConstantValue(bitAnd.Right);
                    var symbol = model.GetSymbolInfo(bitAnd.Left).Symbol;
                    if (symbol == null || !aliases.Contains(symbol) || !value.HasValue) continue;
                    ulong mask = value.Value switch
                    {
                        sbyte v => unchecked((ulong)v), byte v => v,
                        short v => unchecked((ulong)v), ushort v => v,
                        int v => unchecked((ulong)v), uint v => v,
                        long v => unchecked((ulong)v), ulong v => v,
                        char v => v,
                        _ => 0
                    };
                    if (mask != 0) masks.Add(mask);
                }
                // 单个判断的特化不值得复制循环；多组开关才有明显的消分支空间。
                if (masks.Count >= 3) return field;
            }
            return null;
        }

        private static string MaskAdapterName(string name, IFieldSymbol? mask)
            => mask == null ? name : name + "_MaskImpl";

        private static void AppendMaskAdapterHeader(StringBuilder sb, string name, string parameters, IFieldSymbol? mask)
        {
            if (mask != null) sb.AppendLine("template<bool __ejZeroMask>");
            sb.AppendLine($"{(mask == null ? "GENERATED_API" : "static")} void CALLINGCONVENTION {MaskAdapterName(name, mask)}({parameters})");
        }

        private static void AppendMaskFieldBinding(StringBuilder sb, IFieldSymbol field, IFieldSymbol? mask)
        {
            string type = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
            if (SymbolEqualityComparer.Default.Equals(field, mask))
            {
                sb.AppendLine($"    static constexpr {type} __ejZeroMaskValue{{}};");
                sb.AppendLine($"    const {type}& {field.Name} = __ejZeroMask ? __ejZeroMaskValue : *{field.Name}_ptr;");
            }
            else sb.AppendLine($"    const {type}& {field.Name} = *{field.Name}_ptr;");
        }

        // Dispatch once per adapter call; the original signature and nonzero path remain available.
        private static void AppendMaskAdapterDispatch(StringBuilder sb, INamedTypeSymbol job, IFieldSymbol? mask,
            string name, string parameters, string arguments)
        {
            if (mask == null) return;
            int offset = 0, current = 0;
            foreach (var field in job.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                offset = CalculateFieldOffset(field, ref current);
                if (SymbolEqualityComparer.Default.Equals(field, mask)) break;
            }
            string type = NativeTranspiler.MapCSharpTypeToCpp(mask.Type);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {name}({parameters})");
            sb.AppendLine("{");
            sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
            sb.AppendLine("    const char* __job = (const char*)context + sizeof(__EntJoyChunkContextHeader) + (__header->allEnabledCount + __header->requiredComponentTypeIdCount) * sizeof(int);");
            sb.AppendLine($"    if (*reinterpret_cast<const {type}*>(__job + {offset}) == static_cast<{type}>(0))");
            sb.AppendLine($"        {MaskAdapterName(name, mask)}<true>({arguments});");
            sb.AppendLine("    else");
            sb.AppendLine($"        {MaskAdapterName(name, mask)}<false>({arguments});");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        private static bool IsZeroMaskInteger(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol { EnumUnderlyingType: { } underlying }) type = underlying;
            return type.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte
                or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64
                or SpecialType.System_IntPtr or SpecialType.System_UIntPtr;
        }

        // 实例方法、属性或传出 this 可能间接修改 job 字段。
        private static bool HasIndirectJobFieldWrites(INamedTypeSymbol jobStruct, SemanticModel model, SyntaxNode[] nodes)
            => nodes.Any(n => n is RefExpressionSyntax)
                || nodes.OfType<ThisExpressionSyntax>().Any(t => t.Parent is not MemberAccessExpressionSyntax)
                || nodes.OfType<InvocationExpressionSyntax>().Any(i =>
                    model.GetSymbolInfo(i).Symbol is IMethodSymbol m && !m.IsStatic
                    && SymbolEqualityComparer.Default.Equals(m.ContainingType, jobStruct))
                || nodes.OfType<IdentifierNameSyntax>().Any(i =>
                    model.GetSymbolInfo(i).Symbol is IPropertySymbol p && !p.IsStatic
                    && SymbolEqualityComparer.Default.Equals(p.ContainingType, jobStruct));
    }
}
