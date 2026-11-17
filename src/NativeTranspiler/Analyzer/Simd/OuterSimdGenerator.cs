using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativeTranspiler.Analyzer
{
    public class OuterSimdGenerator
    {
        private readonly MethodDeclarationSyntax _methodSyntax;
        private readonly SemanticModel _semanticModel;
        private readonly string _idx;
        private readonly Dictionary<string, string> _boolFields;
        private readonly INamedTypeSymbol? _jobStruct;
        private readonly NativeTranspiler.SimdMathPrecision _simdMathPrecision;

        public OuterSimdGenerator(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel, string indexVarName,
            Dictionary<string, string>? boolFieldValues = null,
            INamedTypeSymbol? jobStruct = null,
            NativeTranspiler.SimdMathPrecision simdMathPrecision = NativeTranspiler.SimdMathPrecision.Fastest)
        {
            _methodSyntax = methodSyntax;
            _semanticModel = semanticModel;
            _idx = indexVarName;
            _boolFields = boolFieldValues ?? new Dictionary<string, string>();
            _jobStruct = jobStruct;
            _simdMathPrecision = simdMathPrecision;
        }

        public string Generate(string scalarBody)
        {
            string simdResult = GenerateFullSIMDFromAST(scalarBody);
            if (!string.IsNullOrEmpty(simdResult))
                return simdResult;
            return GeneratePerLane(scalarBody);
        }

        /// <summary>
        /// 作业字段上的容器即 native array：元素类型要按 MapCSharpTypeToCpp 建表，
        /// 否则翻译器认不出数组元素、元素读退化成 lane0 标量读。
        /// </summary>
        private static Dictionary<string, string> BuildNativeArrayParams(INamedTypeSymbol? jobStruct)
        {
            var result = new Dictionary<string, string>();
            if (jobStruct == null) return result;
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, "NativeArray")
                    && field.Type is INamedTypeSymbol container)
                    result[field.Name] = NativeTranspiler.MapCSharpTypeToCpp(
                        NativeTranspiler.GetContainerElementType(container));
            return result;
        }

        private string GenerateFullSIMDFromAST(string scalarBody)
        {
            if (_jobStruct == null || _methodSyntax.Body == null) return "";

                var varAnalyzer = new SimdVariableAnalyzer(_semanticModel, _jobStruct, _idx);
                var variables = varAnalyzer.Analyze(_methodSyntax);
                if (!variables.ContainsKey(_idx) || variables.Count == 0) return "";

                var sb = new StringBuilder();
                sb.AppendLine("    // Universal Full-SIMD (ISPC-style)");
                sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / g_simdWidthInt) * g_simdWidthInt;");
                sb.AppendLine("    if (simd_end_ > __startIndex)");
                sb.AppendLine("    {");
                sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0, g_simdWidthInt);");
                sb.AppendLine("        for (int si = __startIndex; si < simd_end_; si += g_simdWidthInt)");
                sb.AppendLine("        {");
                sb.AppendLine("            simd_value<int> v_i = v_base + si;");

                var cfGenerator = new SimdControlFlowGenerator(
                    _semanticModel, _jobStruct, variables, varAnalyzer,
                    indexParamName: _idx, simdIndexVar: "v_i",
                    boolFields: _boolFields,
                    simdMathPrecision: _simdMathPrecision,
                    nativeArrayParams: BuildNativeArrayParams(_jobStruct),
                    batchLoopVar: "si");   // 启用连续 load/store 优化（v_i → ptr+si）

                string simdBody = cfGenerator.Generate(_methodSyntax.Body);

                foreach (var line in simdBody.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.Append("            ").AppendLine(line);

                sb.AppendLine("        __simd_exit: ;");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                bool covered = EmitOverlapTail(sb, simdBody);
            if (!string.IsNullOrEmpty(scalarBody))
                sb.Append(RemainderLoop(scalarBody, narrowOnly: covered));
                return sb.ToString();
        }

        /// <summary>尾部用一个与主循环重叠的完整向量迭代覆盖：偏移取总末端减宽度，
        /// 重叠部分重算出同样的值，因此不需要掩码访问。返回是否覆盖了尾部。</summary>
        private bool EmitOverlapTail(StringBuilder sb, string simdBody)
        {
            if (!BodyIsIdempotent()) return false;

            // 尾部是同一段向量体的第二份拷贝：内部所有合成标签都要改名，避免重定义
            string tailBody = Regex.Replace(simdBody, @"\bgoto\s+(__[A-Za-z0-9_]+)", m => "goto " + m.Groups[1].Value + "_tail");
            tailBody = Regex.Replace(tailBody, @"(?m)^(\s*)(__[A-Za-z0-9_]+)\s*:", m => m.Groups[1].Value + m.Groups[2].Value + "_tail:");
            sb.AppendLine("    int total_end_ = __startIndex + __count;");
            sb.AppendLine("    if (__count >= g_simdWidthInt && total_end_ > simd_end_)");
            sb.AppendLine("    {");
            sb.AppendLine("        int si = total_end_ - g_simdWidthInt;");
            sb.AppendLine("        simd_value<int> v_i = simd_value<int>::sequence(0, g_simdWidthInt) + si;");
            foreach (var line in tailBody.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    sb.Append("        ").AppendLine(line.TrimEnd());
            sb.AppendLine("        __simd_exit_tail: ;");
            sb.AppendLine("    }");
            return true;
        }

        /// <summary>重叠尾部的前提：每个写入的数组元素只依赖自身下标，且该数组不被读取；
        /// 这样重算重叠 lane 得到同一结果，重复写入无副作用。</summary>
        private bool BodyIsIdempotent()
        {
            if (_jobStruct == null || _methodSyntax.Body == null) return false;

            var written = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var assign in _methodSyntax.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assign.Left is not ElementAccessExpressionSyntax target) return false;
                // 复合赋值（|=、+= 等）读同一元素，重算重叠 lane 会重复累积
                if (assign.Kind() != SyntaxKind.SimpleAssignmentExpression) return false;
                if (target.ArgumentList.Arguments.Count != 1) return false;
                if (target.ArgumentList.Arguments[0].Expression.ToString() != _idx) return false;
                var sym = _semanticModel.GetSymbolInfo(target.Expression).Symbol;
                if (sym == null) return false;
                written.Add(sym);
            }
            if (written.Count == 0) return false;
            foreach (var unary in _methodSyntax.Body.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                         .Concat<ExpressionSyntax>(_methodSyntax.Body.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()))
            {
                if (unary is PrefixUnaryExpressionSyntax pre && pre.Operand is ElementAccessExpressionSyntax) return false;
                if (unary is PostfixUnaryExpressionSyntax post && post.Operand is ElementAccessExpressionSyntax) return false;
            }

            foreach (var access in _methodSyntax.Body.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
            {
                if (IsAssignmentTarget(access)) continue;
                var sym = _semanticModel.GetSymbolInfo(access.Expression).Symbol;
                if (sym != null && written.Contains(sym)) return false;
            }
            return true;
        }

        private static bool IsAssignmentTarget(ElementAccessExpressionSyntax access)
            => access.Parent is AssignmentExpressionSyntax assign && assign.Left == access;
        private string GeneratePerLane(string scalarBody)
        {
            string body = scalarBody;

            bool hr = body.Contains("return;");
            // per-lane 回退循环固定声明 `int index`，因此实体体里出现的 index 形参名
            //   （Execute(int tid) 的 `tid`）必须一并改名为 `index` —— 否则生成 `tid` 未声明。
            if (!string.IsNullOrEmpty(_idx) && _idx != "index")
                body = Regex.Replace(body, $@"\b{Regex.Escape(_idx)}\b", "index");
            var sb = new StringBuilder();
            sb.AppendLine("    // Outer SIMD: per-lane");
            sb.AppendLine("    int simd_end_=__startIndex+((__count)/g_simdWidthInt)*g_simdWidthInt;");
            sb.AppendLine("    if(simd_end_>__startIndex){");
            sb.AppendLine("        simd_value<int> v_base=simd_value<int>::sequence(0);");
            sb.AppendLine("        for(int si=__startIndex;si<simd_end_;si+=g_simdWidthInt){");
            sb.AppendLine("            for(int __ej_lane=0;__ej_lane<g_simdWidthInt;__ej_lane++){");
            sb.AppendLine("                int index=si+__ej_lane;");
            if (hr) sb.AppendLine("                do{");
            foreach (var line in body.Split('\n'))
            {
                var x = line.TrimEnd();
                if (string.IsNullOrEmpty(x)) continue;
                if (hr) x = x.Replace("return;", "break;");
                sb.Append("                ").AppendLine(x);
            }
            if (hr) sb.AppendLine("                }while(false);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    for(int index=simd_end_;index<__startIndex+__count;++index){");
            if (hr) sb.AppendLine("    do{");
            foreach (var line in body.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var x = line.TrimEnd();
                if (hr) x = x.Replace("return;", "break;");
                sb.Append("    ").AppendLine(x);
            }
            if (hr) sb.AppendLine("    }while(false);");
            sb.AppendLine("    }");
            return sb.ToString();
        }

        private string RemainderLoop(string scalarBody, bool narrowOnly = false)
        {
            string substituted = scalarBody;
            var sb = new StringBuilder();
            string narrowGuard = narrowOnly ? "__count < g_simdWidthInt && " : "";
            sb.AppendLine($"    for (int {_idx} = simd_end_; {narrowGuard}{_idx} < __startIndex + __count; ++{_idx})");
            sb.AppendLine("    {");
            bool hr = substituted.Contains("return;");
            string doneLabel = $"__scalar_returned_{_idx}";
            foreach (var line in substituted.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var l = line.TrimEnd();
                // Fix: use goto to exit the j-loop and skip the default store,
                //   instead of break which only exits the k-loop (EC5/FZ4 bug:
                //   break exits k-loop → j-loop continues → default store overwrites).
                l = l.Replace("return;", $"goto {doneLabel};");
                sb.Append("    ").AppendLine(l);
            }
            if (hr)
            {
                sb.AppendLine($"    {doneLabel}: ;");
            }
            sb.AppendLine("    }");
            return sb.ToString();
        }
    }
}
