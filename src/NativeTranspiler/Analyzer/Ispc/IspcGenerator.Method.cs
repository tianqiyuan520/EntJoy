// ============================================================
// IspcGenerator.Method.cs — ISPC 代码生成的「静态方法」入口
//   拆分自 IspcGenerator（static partial），仅含 IMethodSymbol 入口：
//   方法级 ISPC 源 / MT 源 / C++ 单线程 Wrapper / C++ 多线程 Wrapper。
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
        /// <summary>生成单线程 ISPC 源文件</summary>
        public static string GenerateIspcSource(IMethodSymbol method, Compilation compilation, HashSet<INamedTypeSymbol> userStructs)
        {
            var sb = new StringBuilder();
            var baseName = CppGenerator.GetCppFunctionName(method);
            sb.AppendLine($"// Auto-generated ISPC source for {method.Name}");

            var fields = GetFieldsFromMethod(method);
            var methodSyntax = SymbolHelper.GetMethodSyntax(method);
            var includes = CollectIncludesFromFields(fields);
            // 从方法体中收集额外的结构体 include
            if (methodSyntax?.Body != null)
            {
                var localModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                foreach (var localDecl in methodSyntax.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                {
                    var localType = localModel.GetTypeInfo(localDecl.Declaration.Type).Type;
                    if (localType != null) CollectTypeInclude(localType, includes);
                }
            }
            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            if (methodSyntax?.Body == null) return "// Error: no method body";

            bool needResult = method.ReturnType.SpecialType != SpecialType.System_Void;
            string cppReturnType = NativeTranspiler.MapCSharpTypeToCpp(method.ReturnType);
            string ispcReturnType = ToIspcType(cppReturnType);

            string paramList = BuildIspcParamList(fields, false, needResult ? ispcReturnType : null);
            sb.AppendLine($"export void {baseName}_impl({paramList})");
            sb.AppendLine("{");

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var translator = new MethodIspcTranslator(semanticModel, method, needResult: needResult);
            var bodyCode = translator.Translate(methodSyntax.Body);
            sb.Append(bodyCode);
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>生成多线程 ISPC 源文件（将 for 循环映射为 ISPC task）</summary>
        public static string GenerateIspcMTSource(IMethodSymbol method, Compilation compilation, HashSet<INamedTypeSymbol> userStructs)
        {
            var sb = new StringBuilder();
            var baseName = CppGenerator.GetCppFunctionName(method);
            sb.AppendLine($"// Auto-generated ISPC MT source for {method.Name}");

            if (method.ReturnType.SpecialType != SpecialType.System_Void)
            {
                sb.AppendLine("// Error: ISPC MT does not support non-void return value.");
                return sb.ToString();
            }

            var fields = GetFieldsFromMethod(method);
            var methodSyntax = SymbolHelper.GetMethodSyntax(method);
            var includes = CollectIncludesFromFields(fields);
            if (methodSyntax?.Body != null)
            {
                var localModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                foreach (var localDecl in methodSyntax.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                {
                    var localType = localModel.GetTypeInfo(localDecl.Declaration.Type).Type;
                    if (localType != null) CollectTypeInclude(localType, includes);
                }
            }
            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            if (methodSyntax?.Body == null) return "// Error: no body";
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            // 收集所有可并行的 for 循环
            var loops = new List<(int index, LoopInfo loopInfo)>();
            int taskIdx = 0;
            foreach (var stmt in methodSyntax.Body.Statements)
            {
                if (stmt is ForStatementSyntax forStmt)
                {
                    var loop = ExtractLoopInfo(forStmt, semanticModel);
                    if (loop == null)
                        return "// Error: Could not determine loop upper bound constant for a for loop.";
                    loops.Add((taskIdx, loop.Value));
                    taskIdx++;
                }
            }

            if (loops.Count == 0)
                return "// Error: No parallelizable for loop found.";

            string paramList = BuildIspcParamList(fields, false);
            string callArgs = BuildIspcCallArgs(fields, false);

            // 为每个 for 循环生成 task 函数
            foreach (var (idx, loop) in loops)
            {
                string taskFuncName = baseName + "_task" + idx;
                sb.AppendLine($"task void {taskFuncName}(uniform int __startIndex, uniform int __count, {paramList})");
                sb.AppendLine("{");
                sb.AppendLine($"{Indent}uniform int n_per_task = max(1, __count / taskCount);");
                sb.AppendLine($"{Indent}uniform int start = __startIndex + taskIndex * n_per_task;");
                sb.AppendLine($"{Indent}uniform int end = (taskIndex == taskCount - 1) ? (__startIndex + __count) : min(start + n_per_task, __startIndex + __count);");
                sb.AppendLine($"{Indent}for (uniform int {loop.IndexName} = start; {loop.IndexName} < end; {loop.IndexName}++) {{");

                var translator = new MethodIspcTranslator(semanticModel, method, skipOuterFor: true, initialIndent: 2, needResult: false, useUniformVars: true);
                if (loop.ForStmt.Statement is BlockSyntax block)
                {
                    foreach (var bodyStmt in block.Statements)
                        sb.Append(translator.TranslateSingleStatement(bodyStmt));
                }
                else
                {
                    sb.Append(translator.TranslateSingleStatement(loop.ForStmt.Statement));
                }

                sb.AppendLine($"{Indent}}}");
                sb.AppendLine("}");
                sb.AppendLine();
            }

            // 生成 _mt_impl 入口函数
            string mtFuncName = baseName + "_mt_impl";
            string mtEntryParams = string.IsNullOrEmpty(paramList)
                ? "uniform int numTasks"
                : $"{paramList}, uniform int numTasks";
            sb.AppendLine($"export void {mtFuncName}({mtEntryParams})");
            sb.AppendLine("{");

            int nextTaskIdx = 0;
            var directTranslator = new MethodIspcTranslator(semanticModel, method, initialIndent: 1, needResult: false);

            // 并行化：连续 for 循环批量 launch，末尾统一 sync（ISPC sync 等待本函数内
            // 所有已 launch 任务）→ 相邻无依赖循环真正并行，消除逐个 launch+sync 的串行退化。
            // 依赖规则：相邻循环若操作共享可变数据，须由用户保证无读写依赖（生成器不跨循环
            // 分析依赖）；循环间出现非循环语句时先 sync（该语句在已 launch 循环完成后执行）。
            int pendingLaunches = 0;
            foreach (var stmt in methodSyntax.Body.Statements)
            {
                if (stmt is ForStatementSyntax)
                {
                    var (idx, loop) = loops[nextTaskIdx];
                    sb.AppendLine($"{Indent}launch[numTasks] {baseName}_task{idx}(0, {loop.Limit}, {callArgs});");
                    ++pendingLaunches;
                    nextTaskIdx++;
                }
                else
                {
                    if (pendingLaunches > 0)
                    {
                        sb.AppendLine($"{Indent}sync;");
                        pendingLaunches = 0;
                    }
                    string stmtCode = directTranslator.TranslateSingleStatement(stmt);
                    sb.Append(stmtCode);
                }
            }
            if (pendingLaunches > 0)
                sb.AppendLine($"{Indent}sync;");

            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>生成 C++ Wrapper（单线程 ISPC 调用）</summary>
        public static string GenerateCppWrapper(IMethodSymbol method)
        {
            var sb = new StringBuilder();
            var baseName = CppGenerator.GetCppFunctionName(method);
            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine($"#include \"{baseName}_ispc.h\"");
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();

            var fields = GetFieldsFromMethod(method);
            GenerateResizeCallbacks(sb, fields);

            string cppReturnType = NativeTranspiler.MapCSharpTypeToCpp(method.ReturnType);
            bool isVoid = method.ReturnType.SpecialType == SpecialType.System_Void;
            string cppParams = BuildCppWrapperParamList(fields, false);

            if (isVoid)
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {baseName}({cppParams})");
            else
                sb.AppendLine($"GENERATED_API {cppReturnType} CALLINGCONVENTION {baseName}({cppParams})");
            sb.AppendLine("{");

            sb.Append(GenerateContextFillCode(fields, isFill: true));

            string ispcCallArgs = BuildIspcCallArgsForWrapper(fields, false);
            if (isVoid)
                sb.AppendLine($"    ispc::{baseName}_impl({ispcCallArgs});");
            else
            {
                sb.AppendLine($"    {cppReturnType} __result_temp;");
                // ISPC _impl 函数签名中需要 __result_ptr 输出参数
                string resultArg = string.IsNullOrEmpty(ispcCallArgs) ? "&__result_temp" : $", &__result_temp";
                sb.AppendLine($"    ispc::{baseName}_impl({ispcCallArgs}{resultArg});");
                sb.AppendLine("    return __result_temp;");
            }

            sb.Append(GenerateContextFillCode(fields, isFill: false));
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>生成 C++ Wrapper（多线程 ISPC 调用）</summary>
        public static string GenerateCppWrapperMT(IMethodSymbol method)
        {
            var sb = new StringBuilder();
            var baseName = CppGenerator.GetCppFunctionName(method);
            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine($"#include \"{baseName}_mt_ispc.h\"");
            sb.AppendLine("#include <thread>");
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();

            var fields = GetFieldsFromMethod(method);
            GenerateResizeCallbacks(sb, fields);

            string cppParams = BuildCppWrapperParamList(fields, false);
            string mtParams = string.IsNullOrEmpty(cppParams) ? "int numTasks" : cppParams + ", int numTasks";

            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {baseName}_mt({mtParams})");
            sb.AppendLine("{");
            sb.Append(GenerateContextFillCode(fields, isFill: true));

            string ispcCallArgs = BuildIspcCallArgsForWrapper(fields, false);
            string launchArgs = string.IsNullOrEmpty(ispcCallArgs) ? "numTasks" : ispcCallArgs + ", numTasks";
            sb.AppendLine($"    ispc::{baseName}_mt_impl({launchArgs});");
            sb.Append(GenerateContextFillCode(fields, isFill: false));
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
