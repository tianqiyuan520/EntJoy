// ============================================================
// IspcGenerator.Helper.cs — ISPC 内部 helper（lane 可调用）生成
//
// 背景：job 的 Execute 体内调用的同程序集静态方法（如 CpuOrca.Solve）在 C++ 后端
// 会各自生成独立 .cpp，调用点按函数名直接链接即可。ISPC 后端不同：ISPC 无法调用
// 外部 C++ 符号，必须在同一翻译单元内提供 ISPC 版本，因此这里为这些依赖方法生成
// 「非 export 的 lane 可调用」ISPC 函数，由调用方 .ispc 以 #include 引入。
//
// 参数约定（必须能接受 foreach lane 内的 varying 实参，已用 ispc v1.30 探针验证）：
//   ref/out T x → varying T * uniform x_ptr   实参是 &局部变量（varying 数据 + 常量地址）
//   T* p        → uniform T * varying p_ptr   实参是 (T*)NativeArray_ptr
//   值参数 T x  → T x                         默认 varying，uniform 实参可隐式提升
// 若沿用 export 版的 "uniform T * uniform"，varying 实参无法匹配 → overload 不匹配。
// ============================================================
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer.Common
{
    public static partial class IspcGenerator
    {
        /// <summary>helper 源文件名（不含扩展名）</summary>
        internal static string GetIspcHelperFileName(IMethodSymbol method)
            => CppGenerator.GetCppFunctionName(method) + "_helper";

        /// <summary>
        /// 收集从 <paramref name="root"/> 出发可达的同程序集静态方法闭包。
        /// 这些方法必须额外生成 ISPC helper 版本，否则 ISPC 调用点找不到符号。
        /// </summary>
        internal static List<IMethodSymbol> CollectIspcHelperClosure(IMethodSymbol root, Compilation compilation)
        {
            var ordered = new List<IMethodSymbol>();
            var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var queue = new Queue<IMethodSymbol>();
            foreach (var m in CppGenerator.CollectCalledStaticMethods(root, compilation))
                queue.Enqueue(m);

            while (queue.Count > 0)
            {
                var m = queue.Dequeue();
                if (!seen.Add(m)) continue;
                ordered.Add(m);
                foreach (var dep in CppGenerator.CollectCalledStaticMethods(m, compilation))
                    queue.Enqueue(dep);
            }
            return ordered;
        }

        /// <summary>收集某个 job 的 Execute 体内静态调用的传递闭包。</summary>
        internal static List<IMethodSymbol> CollectIspcHelperClosure(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var execute = jobStruct.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == Config.Execute);
            return execute == null
                ? new List<IMethodSymbol>()
                : CollectIspcHelperClosure(execute, compilation);
        }

        /// <summary>生成 ISPC 内部 helper 源（仅作为 #include 头使用，不单独编译）。</summary>
        internal static string GenerateIspcHelperSource(IMethodSymbol method, Compilation compilation)
        {
            var sb = new StringBuilder();
            var baseName = CppGenerator.GetCppFunctionName(method);
            sb.AppendLine($"// Auto-generated ISPC helper (lane-callable) for {method.Name}");

            string guard = "__ENTJOY_ISPC_HELPER_" + SymbolHelper.Sanitize(baseName).ToUpperInvariant() + "_DEFINED";
            sb.AppendLine($"#ifndef {guard}");
            sb.AppendLine($"#define {guard}");
            sb.AppendLine();

            var fields = GetFieldsFromMethod(method);
            var includes = CollectIncludesFromFields(fields);
            var methodSyntax = SymbolHelper.GetMethodSyntax(method);

            if (methodSyntax?.Body != null)
            {
                var localModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                foreach (var localDecl in methodSyntax.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                {
                    var localType = localModel.GetTypeInfo(localDecl.Declaration.Type).Type;
                    if (localType != null) CollectTypeInclude(localType, includes);
                }
            }
            // 依赖的其它 helper（同目录 {name}_helper.ispc）
            foreach (var dep in CollectIspcHelperClosure(method, compilation))
                includes.Add(GetIspcHelperFileName(dep));

            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            if (methodSyntax == null)
            {
                sb.AppendLine("// Error: no method syntax");
                sb.AppendLine("#endif");
                return sb.ToString();
            }

            string ispcReturn = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(method.ReturnType));
            sb.AppendLine($"static {ispcReturn} {baseName}({BuildIspcHelperParamList(method)})");
            sb.AppendLine("{");

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var translator = new HelperIspcTranslator(semanticModel, method);

            if (methodSyntax.Body != null)
            {
                sb.Append(translator.Translate(methodSyntax.Body));
            }
            else if (methodSyntax.ExpressionBody != null)
            {
                // 表达式体方法（`=> expr`）没有 BlockSyntax Body，必须补 return
                sb.Append("    ");
                if (method.ReturnType.SpecialType != SpecialType.System_Void)
                    sb.Append("return ");
                sb.Append(translator.TranslateExpressionToString(methodSyntax.ExpressionBody.Expression));
                sb.AppendLine(";");
            }
            else
            {
                sb.AppendLine("    // (empty method body)");
            }

            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("#endif");
            return sb.ToString();
        }

        /// <summary>
        /// 构建 lane 可调用 helper 的参数列表（约定见文件头）。
        /// </summary>
        private static string BuildIspcHelperParamList(IMethodSymbol method)
        {
            var pars = new List<string>();
            foreach (var p in method.Parameters)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(p.Type))
                {
                    var elemType = ((INamedTypeSymbol)p.Type).TypeArguments[0];
                    var ispcElem = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                    if (NativeTranspiler.IsEntJoyContainerNamed(p.Type, Config.NativeList))
                        pars.Add($"uniform UnsafeList_Context_{ispcElem}* uniform {p.Name}");
                    else
                        pars.Add($"uniform {ispcElem} {p.Name}_ptr[], uniform int {p.Name}_length");
                }
                else if (p.Type is IPointerTypeSymbol ptrType)
                {
                    var baseIspc = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(ptrType.PointedAtType));
                    // ISPC 不接受 "uniform void"（void 不能带 uniform 限定）→ void* 单独处理
                    pars.Add(baseIspc == "void"
                        ? $"void * varying {p.Name}_ptr"
                        : $"uniform {baseIspc} * varying {p.Name}_ptr");
                }
                else if (p.RefKind == RefKind.Ref || p.RefKind == RefKind.Out)
                {
                    var ispcType = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(p.Type));
                    pars.Add($"varying {ispcType} * uniform {p.Name}_ptr");
                }
                else
                {
                    var ispcType = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(p.Type));
                    pars.Add($"{ispcType} {p.Name}");
                }
            }
            return string.Join(", ", pars);
        }

        /// <summary>
        /// helper 体翻译器：ref/out 参数以 <c>(*name_ptr)</c> 就地读写（ISPC 无引用，
        /// 也不能靠「函数末尾回写」——body 里存在提前 return）。
        /// 值参数按值直接引用；指针参数在基类已映射为 <c>name_ptr</c>。
        /// </summary>
        private sealed class HelperIspcTranslator : IspcStatementTranslator
        {
            private readonly HashSet<string> _refParamNames;

            public HelperIspcTranslator(SemanticModel semanticModel, IMethodSymbol method)
                : base(semanticModel, method, null, false, false)
            {
                _refParamNames = new HashSet<string>(method.Parameters
                    .Where(p => p.RefKind == RefKind.Ref || p.RefKind == RefKind.Out)
                    .Select(p => p.Name));
            }

            protected override void TranslateIdentifier(IdentifierNameSyntax identifier)
            {
                if (_refParamNames.Contains(identifier.Identifier.Text))
                {
                    _builder.Append("(*").Append(identifier.Identifier.Text).Append("_ptr)");
                    return;
                }
                base.TranslateIdentifier(identifier);
            }

            protected override void TranslateAssignment(AssignmentExpressionSyntax assignment)
            {
                if (assignment.Left is IdentifierNameSyntax id && _refParamNames.Contains(id.Identifier.Text))
                {
                    string op = assignment.OperatorToken.Text;
                    _builder.Append("(*").Append(id.Identifier.Text).Append("_ptr)");
                    if (op == "+=" || op == "-=" || op == "*=" || op == "/=")
                    {
                        // ISPC struct 不支持复合赋值 → 展开为 a = a op b
                        _builder.Append(" = (*").Append(id.Identifier.Text).Append("_ptr) ")
                                .Append(op[0]).Append(' ');
                        TranslateExpression(assignment.Right);
                        return;
                    }
                    _builder.Append(' ').Append(op).Append(' ');
                    TranslateExpression(assignment.Right);
                    return;
                }
                base.TranslateAssignment(assignment);
            }
        }
    }
}
