using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public static class CppGenerator
    {
        private static readonly HashSet<string> SkipIncludeTypeNames = new()
        {
            "EntJoy.Mathematics.math",
            "EntJoy.Collections.UnsafeUtility",
            "EntJoy.Hint"
        };

        public static string GetCppFunctionName(IMethodSymbol method)
        {
            var containingNamespace = method.ContainingNamespace?.ToDisplayString() ?? "";
            var typePath = SymbolHelper.BuildFullTypePath(method.ContainingType);
            var methodName = method.Name;
            var safeNamespace = SymbolHelper.Sanitize(containingNamespace);
            var safeTypePath = SymbolHelper.Sanitize(typePath);
            var safeMethod = SymbolHelper.Sanitize(methodName);
            return $"SharpNative_{safeNamespace}_{safeTypePath}_{safeMethod}";
        }

        public static string GenerateHeader(IMethodSymbol method)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine();
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include <cstddef>");
            // 签名中用到的用户自定义结构体（如 out/ref 参数、返回值）必须 include 其定义头，
            // 否则 unity build 的 include 顺序一旦变化就会报 use of undeclared identifier。
            foreach (var t in CollectSignatureStructTypes(method))
                sb.AppendLine($"#include \"{NativeTranspiler.GetStructHeaderFileName(t)}.h\"");
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateAtomicMacros());
            sb.AppendLine();
            sb.AppendLine(GenerateCppFunctionSignature(method, fullyQualified: true) + ";");
            return sb.ToString();
        }

        /// <summary>收集方法签名（参数/返回值）中出现的用户自定义结构体类型。</summary>
        internal static IEnumerable<INamedTypeSymbol> CollectSignatureStructTypes(IMethodSymbol method)
        {
            var result = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var queue = new Queue<ITypeSymbol>();
            foreach (var p in method.Parameters) queue.Enqueue(p.Type);
            queue.Enqueue(method.ReturnType);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (t is IPointerTypeSymbol ptr) { queue.Enqueue(ptr.PointedAtType); continue; }
                if (t is IArrayTypeSymbol arr) { queue.Enqueue(arr.ElementType); continue; }
                if (t is not INamedTypeSymbol named) continue;
                if (named.IsGenericType)
                {
                    foreach (var ta in named.TypeArguments) queue.Enqueue(ta);
                    continue;
                }
                if (named.TypeKind != TypeKind.Struct) continue;
                if (named.ContainingAssembly == null) continue;
                // EntJoy 内建数学类型由 NativeMath.h 提供，无需用户结构体头
                var ns = named.ContainingNamespace?.ToDisplayString();
                if (ns == "EntJoy.Mathematics") continue;
                if (NativeTranspiler.IsEntJoyNativeContainerType(named)) continue;
                if (named.SpecialType != SpecialType.None) continue;
                if (result.Add(named)) { }
            }
            return result;
        }

    /// <summary>
    /// 助手（非入口）的**头内联**发射：函数体直接进 .h 并标 `static inline`，不再发 dllexport 独立 TU。
    /// 动机（实测，见 `docs/gridsearch/07 §7x(j)(h)`）：助手原为 `EXTERNC __declspec(dllexport)`，
    /// clang 对 dllexport 函数**不内联**（单 TU 也不行——反汇编实测 7 处 `callq`）；仅加 `inline` 无效。
    /// 改为头内联后调用点可内联，且**不依赖 UNITY_BUILD**：需要助手体的 TU 只需 include 本头。
    /// 入口方法（`[NativeTranspile]` 标记的）仍走 `GenerateHeader`/`GenerateImplementation`（dllexport）。
    /// </summary>
    public static string GenerateInlineHelperHeader(IMethodSymbol method, Compilation compilation,
        HashSet<INamedTypeSymbol>? userStructs = null,
        NativeTranspiler.AutoSIMD autoSIMD = NativeTranspiler.AutoSIMD.Disabled)
    {
        var functionName = GetCppFunctionName(method);
        var exportSig = GenerateCppFunctionSignature(method, fullyQualified: true);
        var inlineSig = "static inline " + exportSig
            .Replace("GENERATED_API ", string.Empty)
            .Replace("CALLINGCONVENTION ", string.Empty);
        var body = GenerateImplementation(method, compilation, userStructs, autoSIMD);
        // 去掉对自身 .h 的 include（体已在本头内）；其余 include 原样保留（体依赖它们）。
        body = body.Replace("#include \"" + functionName + ".h\"\r\n", string.Empty)
                   .Replace("#include \"" + functionName + ".h\"\n", string.Empty)
                   .Replace(exportSig, inlineSig);
        return "#pragma once\n" + body;
    }

    public static string GenerateImplementation(IMethodSymbol method, Compilation compilation,
        HashSet<INamedTypeSymbol>? userStructs = null,
        NativeTranspiler.AutoSIMD autoSIMD = NativeTranspiler.AutoSIMD.Disabled)
        {
            var sb = new StringBuilder();
            var functionName = GetCppFunctionName(method);
            sb.AppendLine($"#include \"{functionName}.h\"");

            var dependencies = CollectCalledStaticMethods(method, compilation);
            foreach (var dep in dependencies)
            {
                var depFuncName = GetCppFunctionName(dep);
                sb.AppendLine($"#include \"{depFuncName}.h\"");
            }

            // 为用户自定义结构体添加 include
            if (userStructs != null)
            {
                foreach (var us in userStructs)
                {
                    var headerName = NativeTranspiler.GetStructHeaderFileName(us);
                    sb.AppendLine($"#include \"{headerName}.h\"");
                }
            }

            sb.AppendLine("#include <algorithm>");
            sb.AppendLine("#include <cstdio>");

            var methodSyntax = SymbolHelper.GetMethodSyntax(method);

            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
            {
                sb.AppendLine("#include \"NativeSIMD.h\"");
                sb.AppendLine("#include \"SimdValue.h\"");
            }
            else
                sb.AppendLine("#include <cmath>");

            sb.AppendLine();
            sb.AppendLine(GenerateCppFunctionSignature(method, fullyQualified: true));
            sb.AppendLine("{");

            // 1. 仅保留 NativeList 的引用声明，NativeArray 不生成任何局部变量
            foreach (var param in method.Parameters.Where(p => NativeTranspiler.IsEntJoyNativeContainerType(p.Type)))
            {
                if (NativeTranspiler.IsEntJoyContainerNamed(param.Type, Config.NativeList))
                {
                    var elementType = ((INamedTypeSymbol)param.Type).TypeArguments[0];
                    var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                    sb.AppendLine($"    EntJoy::Collections::UnsafeList<{cppElementType}>& {param.Name} = *{param.Name}_listData;");
                }
                // NativeArray: nothing to declare
            }

            // 2. 为 ref/out 参数创建局部引用（值参数按值传递，无需前导；
            //    跳过容器和指针类型），移除 const
            foreach (var param in method.Parameters)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(param.Type)) continue;
                if (param.Type is IPointerTypeSymbol) continue;
                if (param.RefKind != RefKind.Ref && param.RefKind != RefKind.Out) continue;
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                sb.AppendLine($"    {cppType}& {param.Name} = *{param.Name}_ptr;");
            }

            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                bool useFastMath = AttributeHelper.HasFastCppMathLib(method,
                    compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute"));

                if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                {
                    string simdCode = GenerateSimdViaCFG(method, methodSyntax.Body, semanticModel, useFastMath);
                    sb.Append(simdCode);
                }
                else
                {
                    var translator = new CppPointerStatementTranslator(semanticModel, method, useFastMath);
                    var bodyCode = translator.Translate(methodSyntax.Body);
                    sb.Append(bodyCode);
                }
            }
            else if (methodSyntax?.ExpressionBody != null)
            {
                // 表达式体方法（`=> expr`）没有 BlockSyntax Body —— 旧实现直接落到
                // "(empty method body)" 分支，非 void 方法因此缺失 return（C++ 未定义行为，
                // 调用方拿到垃圾值；MSVC 还可能触发 __debugbreak）。此处补 `return expr;`。
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                bool useFastMath = AttributeHelper.HasFastCppMathLib(method,
                    compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute"));
                var translator = new CppPointerStatementTranslator(semanticModel, method, useFastMath);
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
            return sb.ToString();
        }



        private static int GetStrideCoeff(ExpressionSyntax expr, string var)
        {
            if (expr is IdentifierNameSyntax id)
                return id.Identifier.Text == var ? 1 : 0;
            if (expr is LiteralExpressionSyntax) return 0;
            if (expr is BinaryExpressionSyntax bin)
            {
                int left = GetStrideCoeff(bin.Left, var);
                int right = GetStrideCoeff(bin.Right, var);
                if (bin.OperatorToken.Text == "*")
                {
                    if (left > 0 && bin.Right is LiteralExpressionSyntax rLit)
                        return left * (int)(rLit.Token.Value ?? 0);
                    if (right > 0 && bin.Left is LiteralExpressionSyntax lLit)
                        return right * (int)(lLit.Token.Value ?? 0);
                    return left * right;
                }
                if (bin.OperatorToken.Text == "+" || bin.OperatorToken.Text == "-")
                    return bin.OperatorToken.Text == "+" ? left + right : left - right;
            }
            if (expr is ParenthesizedExpressionSyntax paren)
                return GetStrideCoeff(paren.Expression, var);
            if (expr is CastExpressionSyntax cast)
                return GetStrideCoeff(cast.Expression, var);
            return 0;
        }

        /// <summary>Resolve a constant int expression: literal, const field, or const local.</summary>
        private static int? ResolveConstInt(ExpressionSyntax expr, SemanticModel semanticModel)
        {
            if (expr is LiteralExpressionSyntax lit && lit.Token.Value is int iv) return iv;
            if (expr is IdentifierNameSyntax id)
            {
                try
                {
                    var sym = semanticModel.GetSymbolInfo(id).Symbol;
                    if (sym is IFieldSymbol f && f.HasConstantValue) return (int)f.ConstantValue;
                    if (sym is ILocalSymbol l && l.HasConstantValue) return (int)l.ConstantValue;
                }
                catch { }
            }
            return null;
        }

        private static string PickBestVectorVar(List<string> loopVars, BlockSyntax body,
            Dictionary<string, string> nativeArrayParams)
        {
            if (loopVars.Count <= 1) return loopVars.FirstOrDefault();
            var accesses = body.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
                .Where(ea => ea.Expression is IdentifierNameSyntax id
                    && nativeArrayParams.ContainsKey(id.Identifier.Text)).ToList();
            if (accesses.Count == 0) return loopVars[0];

            // Collect variables that hold indirect array results (idx = b[i]; a[idx])
            var indirectVars = new HashSet<string>();
            foreach (var assign in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                if (assign.Left is IdentifierNameSyntax lhs
                    && assign.Right is ElementAccessExpressionSyntax)
                    indirectVars.Add(lhs.Identifier.Text);
            foreach (var decl in body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                foreach (var v in decl.Declaration.Variables)
                    if (v.Initializer?.Value is ElementAccessExpressionSyntax)
                        indirectVars.Add(v.Identifier.Text);

            // Check for indirect access: direct a[b[i]] OR idx = b[i]; a[idx]
            bool hasIndirect = accesses.Any(ea =>
            {
                var arg = ea.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                if (arg is ElementAccessExpressionSyntax) return true;
                if (arg is IdentifierNameSyntax id3 && indirectVars.Contains(id3.Identifier.Text))
                    return true;
                return false;
            });
            if (hasIndirect) return loopVars[0];

            var sums = new Dictionary<string, int>();
            foreach (var v in loopVars) sums[v] = 0;
            foreach (var ea in accesses)
            {
                if (ea.ArgumentList == null || ea.ArgumentList.Arguments.Count == 0) continue;
                var firstArg = ea.ArgumentList.Arguments[0].Expression;
                foreach (var v in loopVars)
                    sums[v] += GetStrideCoeff(firstArg, v);
            }
            var valid = sums.Where(kv => kv.Value > 0).ToList();
            if (valid.Count == 0) return loopVars[0];
            return valid.OrderBy(kv => kv.Value).First().Key;
        }

        private static string GenerateSimdViaCFG(IMethodSymbol method, BlockSyntax body,
            SemanticModel semanticModel, bool useFastMath)
        {
            var forStmt = body.Statements.OfType<ForStatementSyntax>().FirstOrDefault();
            if (forStmt == null) return FallbackScalarTranslation(method, body, semanticModel, useFastMath);
            string outerVar = forStmt.Declaration.Variables[0].Identifier.Text;
            var limitExpr = ((BinaryExpressionSyntax)forStmt.Condition).Right;
            string limitStr = limitExpr.GetText().ToString().Trim();
            var nap = new Dictionary<string, string>();
            foreach (var param in method.Parameters)
                if (NativeTranspiler.IsEntJoyContainerNamed(param.Type, Config.NativeArray))
                {
                    var ta = ((INamedTypeSymbol)param.Type).TypeArguments.FirstOrDefault();
                    nap[param.Name] = ta != null ? NativeTranspiler.MapCSharpTypeToCpp(ta) : "float";
                }
            var innerFor = (forStmt.Statement is BlockSyntax ob)
                ? ob.Statements.OfType<ForStatementSyntax>().FirstOrDefault() : null;
            if (innerFor != null)
            {
                string iVar = innerFor.Declaration.Variables[0].Identifier.Text;
                // Resolve inner bound: literal, const field, or const local
                var innerLimitExpr = ((BinaryExpressionSyntax)innerFor.Condition).Right;
                int? innerBound = innerLimitExpr switch
                {
                    LiteralExpressionSyntax lit when lit.Token.Value is int iv => iv,
                    IdentifierNameSyntax id => ResolveConstInt(id, semanticModel),
                    _ => null
                };
                if (innerBound.HasValue)
                {
                    var ibody = innerFor.Statement is BlockSyntax ibs ? ibs
                        : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(new SyntaxList<StatementSyntax>(innerFor.Statement));
                    if (PickBestVectorVar(new List<string>{outerVar,iVar}, ibody, nap) == iVar)
                    {
                        // ★ NT-09：只有**证明得了**"内层体恰好把单个 NativeArray 的原始元素
                        //   min/max 到累加量、累加量初值恰为单位元、随后写回 ra[outerVar]"时才向量化。
                        //   旧实现只要体内出现第一个 `if (x < y)` 就凭空合成"对体内每个数组读的
                        //   原始元素做 min/max"，体内真正的计算（closest-point 的 d = f(arr[i])、
                        //   argmin 写回…）被整段丢弃 ⇒ 静默错值。证明不了就老实退标量。
                        if (TryProveRowRawReduction(forStmt.Statement, innerFor, ibody, outerVar, iVar,
                                innerLimitExpr.GetText().ToString().Trim(), nap,
                                out string reduceFn, out string cmpOp, out string initVal, out string reductionArray))
                            return GenerateVectorizedInnerLoop(forStmt, innerFor, nap,
                                reduceFn, cmpOp, initVal, reductionArray);
                        return FallbackScalarTranslation(method, body, semanticModel, useFastMath);
                    }
                }
            }
            return GenerateBatchLoopSIMD(method, forStmt, nap, semanticModel, useFastMath);
        }

        private static string GenerateBatchLoopSIMD(IMethodSymbol method, ForStatementSyntax forStmt,
            Dictionary<string, string> nap, SemanticModel semanticModel, bool useFastMath = false)
        {
            var sb = new StringBuilder();
            string idx = forStmt.Declaration.Variables[0].Identifier.Text;
            string lim = ((BinaryExpressionSyntax)forStmt.Condition).Right.GetText().ToString().Trim();
            BlockSyntax ib = forStmt.Statement is BlockSyntax bs ? bs
                : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(new SyntaxList<StatementSyntax>(forStmt.Statement));
            var ms = SymbolHelper.GetMethodSyntax(method);
            var va2 = new SimdVariableAnalyzer(semanticModel, null, idx);
            var vars = va2.Analyze(ms);
            if (vars.TryGetValue(idx, out var ii)) ii.Kind = VarKind.Varying;
            else vars[idx] = new SimdVariableInfo { Name = idx, Kind = VarKind.Varying, CppType = "int" };
            var sg = new SimdControlFlowGenerator(semanticModel, null, vars, va2,
                indexParamName: idx, simdIndexVar: "v_i", batchOffsetVar: "si",
                simdMathPrecision: NativeTranspiler.SimdMathPrecision.Fastest,
                nativeArrayParams: nap, batchLoopVar: "si");
            sb.AppendLine(string.Format("    int vec_count = (({0}) / NSIMD_WIDTH) * NSIMD_WIDTH;", lim));
            sb.AppendLine("    simd_value<int> v_base = simd_value<int>::sequence(0);");
            sb.AppendLine("    if (vec_count > 0) {");
            sb.AppendLine("        for (int si = 0; si < vec_count; si += NSIMD_WIDTH) {");
            sb.AppendLine("            simd_value<int> v_i = v_base + si;");
            foreach (var line in sg.Generate(ib).Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine("            " + line.TrimEnd());
            sb.AppendLine("        __simd_exit: ; } }");
            sb.AppendLine();
            // ★ 余数（n % NSIMD_WIDTH）循环体必须走**真正的指针转译器**。
            //   旧实现把 C# 源文本 `stmt.GetText()` 过一张 9 条手写替换表（数组名、MathF.*、float.MaxValue）
            //   就直接交付 ⇒ 真转译器修过的语义原样复活：
            //     · C# `1UL` 是 64 位，C++（Windows/LLP64）的 `unsigned long` 是 32 位
            //       ⇒ `m_ptr[i] |= 1UL << (i & 63)` 位移量 ≥32 = UB（clang 折叠成 &31）⇒ 尾部掩码静默错值
            //       （正是文档 §8.1 记为已修的缺陷）；
            //     · 其它 C#-only 构造（`MathF.Max(`、switch 表达式…）被**原样**漏进 C++。
            //   转译器自带数组字段→`_ptr`、MathF.*→`::fmaxf`、字面量后缀归一化（1UL→1ULL）等映射，
            //   手写表因此删除；循环头仍然只在这里重写（区分 vec/tail 两段）。
            var tailTranslator = new CppPointerStatementTranslator(semanticModel, method, useFastMath);
            // ⚠ 用**原语法树里**的节点：`ib` 在"循环体不是 Block"时是 SyntaxFactory 造的脱离树节点，
            //   喂给语义模型会抛 ArgumentException（NT026）⇒ 单条语句走 TranslateSingleStatement。
            string tailCode = forStmt.Statement is BlockSyntax tailBlock
                ? tailTranslator.Translate(tailBlock)
                : tailTranslator.TranslateSingleStatement(forStmt.Statement);
            sb.AppendLine(string.Format("    for (int {0} = vec_count; {0} < {1}; {0}++)", idx, lim));
            sb.AppendLine("    {");
            foreach (var line in tailCode.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine("        " + line.TrimEnd());
            sb.AppendLine("    }");
            return sb.ToString();
        }

        /// <summary>
        /// ★ NT-09：证明内层循环体**恰好**是"把某个 NativeArray 的原始元素按 min/max 归约到累加量"。
        ///
        /// 成立的全部条件（缺一不可；发射器会硬编码单位元初值、按 <c>base = ov * il</c> 寻址、
        /// 只写一条 <c>ra_ptr[ov] = h</c>，所以这些都必须被证明）：
        /// <list type="number">
        /// <item>内层体是**一条**无 else 的 <c>if</c>；</item>
        /// <item>体内**读**到的 NativeArray 恰好一个；</item>
        /// <item>条件的一侧是<b>该数组的原始元素</b> <c>arr[ov * il + iv]</c>（规范行下标），
        ///       另一侧是普通标识符（累加量）；</item>
        /// <item>then 分支是**单条** <c>acc = arr[同一元素]</c>（只更新累加量，无其它副作用）；</item>
        /// <item>累加量是外层体内、内层循环**之前**声明的局部量，且初值恰为发射器硬编码的单位元
        ///       （否则用 FLT_MAX 顶替用户初值会改语义）；</item>
        /// <item>外层体（内层循环之外）恰好一条 NativeArray 元素写入 <c>ra[outerVar] = acc</c>。</item>
        /// </list>
        /// 任一条不成立即返回 false —— 调用点必须退 <c>FallbackScalarTranslation</c>，
        /// **绝不**从"比较扫描"合成归约。
        /// </summary>
        private static bool TryProveRowRawReduction(
            StatementSyntax outerBodyStmt, ForStatementSyntax innerFor, BlockSyntax ibody,
            string outerVar, string innerVar, string innerLimitText, Dictionary<string, string> nap,
            out string reduceFn, out string cmpOp, out string initVal, out string reductionArray)
        {
            reduceFn = null; cmpOp = null; initVal = null; reductionArray = null;

            // (1) 内层体：一条无 else 的 if
            if (ibody.Statements.Count != 1 || !(ibody.Statements[0] is IfStatementSyntax ifs) || ifs.Else != null)
                return false;
            if (!(ifs.Condition is BinaryExpressionSyntax cond)) return false;
            string opText = cond.OperatorToken.Text;
            if (opText != "<" && opText != ">") return false;

            // (2) 体内读到的 NativeArray 恰好一个
            var readArrays = new HashSet<string>();
            foreach (var ea in ibody.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
            {
                if (ea.Expression is IdentifierNameSyntax rid && nap.ContainsKey(rid.Identifier.Text)
                    && !(ea.Parent is AssignmentExpressionSyntax raes && raes.Left == ea))
                    readArrays.Add(rid.Identifier.Text);
            }
            if (readArrays.Count != 1) return false;
            string arr = readArrays.First();

            // (3) 条件：一侧 = 规范行下标的原始元素，另一侧 = 标识符（累加量）
            ExpressionSyntax elemSide = null;
            string accVar = null;
            if (IsCanonicalRawElement(cond.Left, arr, outerVar, innerVar, innerLimitText)
                && cond.Right is IdentifierNameSyntax accRight)
            { elemSide = cond.Left; accVar = accRight.Identifier.Text; }
            else if (IsCanonicalRawElement(cond.Right, arr, outerVar, innerVar, innerLimitText)
                && cond.Left is IdentifierNameSyntax accLeft)
            { elemSide = cond.Right; accVar = accLeft.Identifier.Text; }
            if (elemSide == null) return false;

            // 方向：`elem < acc` / `acc > elem` ⇒ min；`elem > acc` / `acc < elem` ⇒ max
            bool elemOnLeft = elemSide == cond.Left;
            bool isMin = opText == "<" ? elemOnLeft : !elemOnLeft;
            reduceFn = isMin ? "n_min_ps" : "n_max_ps";
            cmpOp = isMin ? "<" : ">";
            initVal = isMin ? "3.402823466e+38f" : "-3.402823466e+38f";

            // (4) then 分支：单条 `acc = <同一原始元素>`
            var thenStmts = ifs.Statement is BlockSyntax tb
                ? tb.Statements : new SyntaxList<StatementSyntax>(ifs.Statement);
            if (thenStmts.Count != 1) return false;
            if (!(thenStmts[0] is ExpressionStatementSyntax tes)
                || !(tes.Expression is AssignmentExpressionSyntax ta)) return false;
            if (ta.OperatorToken.Text != "=") return false;
            if (!(ta.Left is IdentifierNameSyntax taId) || taId.Identifier.Text != accVar) return false;
            if (ta.Right.GetText().ToString().Trim() != elemSide.GetText().ToString().Trim()) return false;

            // (5) 累加量：外层体内、内层循环之前的局部量，且初值恰为单位元
            if (!(outerBodyStmt is BlockSyntax outerBlock)) return false;
            int innerIdx = outerBlock.Statements.IndexOf(innerFor);
            if (innerIdx < 0) return false;
            string[] acceptedInit = isMin
                ? new[] { "3.402823466e+38f", "float.MaxValue" }
                : new[] { "-3.402823466e+38f", "-float.MaxValue" };
            bool accDeclared = false;
            for (int i = 0; i < innerIdx; i++)
            {
                if (!(outerBlock.Statements[i] is LocalDeclarationStatementSyntax lds)) continue;
                foreach (var v in lds.Declaration.Variables)
                    if (v.Identifier.Text == accVar)
                        accDeclared = v.Initializer != null
                            && System.Array.IndexOf(acceptedInit, v.Initializer.Value.GetText().ToString().Trim()) >= 0;
            }
            if (!accDeclared) return false;

            // (6) 外层体（内层循环之外）恰好一条 NativeArray 元素写入 `ra[outerVar] = acc`
            var stores = new List<AssignmentExpressionSyntax>();
            foreach (var stmt in outerBlock.Statements)
            {
                if (stmt == innerFor) continue;
                foreach (var aes in stmt.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
                    if (aes.Left is ElementAccessExpressionSyntax) stores.Add(aes);
            }
            if (stores.Count != 1) return false;
            var store = stores[0];
            if (!(store.Left is ElementAccessExpressionSyntax sea)
                || !(sea.Expression is IdentifierNameSyntax sId) || !nap.ContainsKey(sId.Identifier.Text)) return false;
            var sIdx = sea.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
            if (sIdx == null || sIdx.GetText().ToString().Trim() != outerVar) return false;
            if (!(store.Right is IdentifierNameSyntax rId) || rId.Identifier.Text != accVar) return false;

            reductionArray = arr;
            return true;
        }

        /// <summary><c>arr[ov * il + iv]</c>（与发射器的 <c>base = ov * il</c> + <c>iv</c> 寻址一致）。</summary>
        private static bool IsCanonicalRawElement(ExpressionSyntax expr, string arr,
            string outerVar, string innerVar, string innerLimitText)
        {
            if (!(expr is ElementAccessExpressionSyntax ea)) return false;
            if (!(ea.Expression is IdentifierNameSyntax id) || id.Identifier.Text != arr) return false;
            var args = ea.ArgumentList?.Arguments;
            if (args == null || args.Value.Count != 1) return false;
            if (!(args.Value[0].Expression is BinaryExpressionSyntax plus) || plus.OperatorToken.Text != "+")
                return false;
            if (!(plus.Right is IdentifierNameSyntax ivId) || ivId.Identifier.Text != innerVar) return false;
            if (!(plus.Left is BinaryExpressionSyntax mul) || mul.OperatorToken.Text != "*") return false;
            if (!(mul.Left is IdentifierNameSyntax ovId) || ovId.Identifier.Text != outerVar) return false;
            return mul.Right.GetText().ToString().Trim() == innerLimitText.Trim();
        }

        private static string GenerateVectorizedInnerLoop(ForStatementSyntax ofs,
            ForStatementSyntax ifs,
            Dictionary<string, string> nap, string reduceFn, string cmpOp, string initVal,
            string reductionArray)
        {
            var sb = new StringBuilder();
            string ov = ofs.Declaration.Variables[0].Identifier.Text;
            string ol = ((BinaryExpressionSyntax)ofs.Condition).Right.GetText().ToString().Trim();
            string iv = ifs.Declaration.Variables[0].Identifier.Text;
            string il = ((BinaryExpressionSyntax)ifs.Condition).Right.GetText().ToString().Trim();
            string arr = reductionArray;
            string elemType = nap.TryGetValue(arr, out var t) ? t : "float";

            // 归约方向/初值/目标数组已由 TryProveRowRawReduction 证明（不再扫描体内 if 猜）。
            sb.AppendLine(string.Format("    for (int {0} = 0; {0} < {1}; {0}++) {{", ov, ol));
            sb.AppendLine(string.Format("        n_float v_best = n_set1_ps({0});", initVal));
            sb.AppendLine(string.Format("        int base = {0} * {1};", ov, il));
            // ★ Align the SIMD loop bound to NSIMD_WIDTH — otherwise the last
            //   n_load_ps reads past the row end (il is often not a multiple of
            //   NSIMD_WIDTH), producing garbage min/max or OOB memory access.
            sb.AppendLine(string.Format("        int __aligned = ({0} / NSIMD_WIDTH) * NSIMD_WIDTH;", il));
            sb.AppendLine(string.Format("        for (int {0} = 0; {0} < __aligned; {0} += NSIMD_WIDTH) {{", iv));
            if (elemType == "int")
                sb.AppendLine(string.Format("            v_best = {0}(v_best, simd_value<float>{{ n_cvtepi32_ps(n_load_epi32({1}_ptr + base + {2})) }});", reduceFn, arr, iv));
            else
                sb.AppendLine(string.Format("            v_best = {0}(v_best, n_load_ps({1}_ptr + base + {2}));", reduceFn, arr, iv));
            sb.AppendLine("        }");
            sb.AppendLine("        float lane[NSIMD_WIDTH]; n_store_ps(lane, v_best);");
            sb.AppendLine("        float h = lane[0];");
            sb.AppendLine("        for (int i = 1; i < NSIMD_WIDTH; i++)");
            sb.AppendLine(string.Format("            if (lane[i] {0} h) h = lane[i];", cmpOp));
            // ★ Tail: reduce the remaining [__aligned, il) elements scalar
            sb.AppendLine(string.Format("        for (int {0} = __aligned; {0} < {1}; {0}++) {{", iv, il));
            string cast = elemType == "int" ? "(float)" : "";
            sb.AppendLine(string.Format("            float __v_{0} = {1}_ptr[base + {2}];", arr, arr, iv));
            sb.AppendLine(string.Format("            if ({0}__v_{1} {2} h) h = __v_{1};", cast, arr, cmpOp));
            sb.AppendLine("        }");
            sb.AppendLine(string.Format("        {0}_ptr[{1}] = h;", arr, ov));
            sb.AppendLine("    }");
            return sb.ToString();
        }


        /// <summary>Fallback: translate entire body as scalar C++.</summary>
        private static string FallbackScalarTranslation(IMethodSymbol method, BlockSyntax body,
            SemanticModel semanticModel, bool useFastMath)
        {
            var translator = new CppPointerStatementTranslator(semanticModel, method, useFastMath);
            return translator.Translate(body);
        }

        private static string GenerateCppFunctionSignature(IMethodSymbol method, bool fullyQualified)
        {
            var returnType = NativeTranspiler.MapCSharpTypeToCpp(method.ReturnType);
            // MapCSharpTypeToCpp 已处理指针类型（追加 *），此处只需处理引用类型返回指针
            if (!(method.ReturnType is IPointerTypeSymbol) &&
                method.ReturnType.SpecialType != SpecialType.System_Void &&
                !method.ReturnType.IsValueType) returnType += "*";

            var funcName = fullyQualified ? GetCppFunctionName(method) : method.Name;
            var parameters = new List<string>();
            foreach (var p in method.Parameters)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(p.Type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(p.Type, Config.NativeList))
                    {
                        var elementType = ((INamedTypeSymbol)p.Type).TypeArguments[0];
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        parameters.Add($"EntJoy::Collections::UnsafeList<{cppElementType}>* {p.Name}_listData");
                    }
                    else // NativeArray
                    {
                        var elementType = ((INamedTypeSymbol)p.Type).TypeArguments[0];
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        parameters.Add($"{cppElementType}* {p.Name}_ptr, int {p.Name}_length");
                    }
                }
                else if (p.Type is IPointerTypeSymbol)
                {
                    // ★ 修改：不再添加多余的 *，MapCSharpTypeToCpp 已返回带 * 的类型
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(p.Type);
                    parameters.Add($"{cppType} {p.Name}_ptr");
                }
                else if (p.RefKind == RefKind.Ref || p.RefKind == RefKind.Out)
                {
                    // ref/out：保持指针 ABI（调用方必须传左值地址）
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(p.Type);
                    parameters.Add($"{cppType}* {p.Name}_ptr");
                }
                else
                {
                    // ★ 按值参数默认按值传递。C# 的值参数语义就是副本，
                    // 旧实现（T* ptr + T& x = *ptr）实为引用语义，既不符合 C# 语义，
                    // 又要求调用点提供左值 —— 字面量/临时量（&0、&false、&(a-b)）直接编译失败。
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(p.Type);
                    parameters.Add($"{cppType} {p.Name}");
                }
            }
            string paramStr = string.Join(", ", parameters);
            if (fullyQualified)
                return $"GENERATED_API {returnType} CALLINGCONVENTION {funcName}({paramStr})";
            else
                return $"{returnType} {funcName}({paramStr})";
        }

        internal static IEnumerable<IMethodSymbol> CollectCalledStaticMethods(IMethodSymbol method, Compilation compilation)
        {
            var calledMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var methodSyntax = SymbolHelper.GetMethodSyntax(method);
            if (methodSyntax == null) return calledMethods;

            // ⚠ 块体与**表达式体**都要走：表达式体（`=> BinKey(...)`）没有 Body，
            //   旧实现直接 return ⇒ 依赖不被收集 ⇒ 该助手的头内联版缺 `#include "<dep>.h"`
            //   （实测报 `use of undeclared identifier 'SharpNative_..._BinKey'`）。
            IEnumerable<SyntaxNode> roots;
            if (methodSyntax.Body != null) roots = new SyntaxNode[] { methodSyntax.Body };
            else if (methodSyntax.ExpressionBody != null) roots = new SyntaxNode[] { methodSyntax.ExpressionBody.Expression };
            else return calledMethods;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var node in roots.SelectMany(r => r.DescendantNodesAndSelf()).OfType<InvocationExpressionSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(node);
                if (symbolInfo.Symbol is IMethodSymbol calledMethod && calledMethod.IsStatic)
                {
                    var containingTypeFullName = calledMethod.ContainingType?.ToDisplayString();
                    if (containingTypeFullName != null && SkipIncludeTypeNames.Contains(containingTypeFullName)) continue;
                    if (SymbolEqualityComparer.Default.Equals(calledMethod.ContainingAssembly, compilation.Assembly))
                        calledMethods.Add(calledMethod);
                }
            }
            return calledMethods;
        }

    }
}
