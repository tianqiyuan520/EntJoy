// 静态方法的单次调用、多任务入口与 C++ wrapper。
using Microsoft.CodeAnalysis.CSharp;
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
            var includes = CollectMethodIncludes(method, compilation, fields);
            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            if (methodSyntax?.Body == null && methodSyntax?.ExpressionBody == null)
                return $"// {UnsupportedMarkers.Stmt}ISPC_MissingMethodBody";

            bool needResult = method.ReturnType.SpecialType != SpecialType.System_Void;
            string cppReturnType = NativeTranspiler.MapCSharpTypeToCpp(method.ReturnType);
            string ispcReturnType = IspcTypeMapper.FromCpp(cppReturnType);

            string paramList = BuildIspcParamList(fields, false, needResult ? ispcReturnType : null);
            sb.AppendLine($"export void {baseName}_impl({paramList})");
            sb.AppendLine("{");
            sb.AppendLine($"{Indent}if (programIndex != 0) return;");

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var translator = new MethodIspcTranslator(semanticModel, method, needResult: needResult, useUniformVars: true);
            if (methodSyntax.Body != null)
                AppendIspcBody(sb, translator.Translate(methodSyntax.Body));
            else
            {
                string expression = translator.TranslateExpressionToString(methodSyntax.ExpressionBody!.Expression);
                sb.Append(Indent);
                if (needResult) sb.Append("*__result_ptr = ");
                sb.Append(expression).AppendLine(";");
            }
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
                sb.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_MT_NonVoidMethod");
                return sb.ToString();
            }

            var fields = GetFieldsFromMethod(method);
            var methodSyntax = SymbolHelper.GetMethodSyntax(method);
            var includes = CollectMethodIncludes(method, compilation, fields);
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
                        return $"// {UnsupportedMarkers.Stmt}ISPC_MT_RequiresZeroBasedUnitStrideConstantLoop";
                    loops.Add((taskIdx, loop.Value));
                    taskIdx++;
                }
            }

            if (loops.Count == 0)
                return $"// {UnsupportedMarkers.Stmt}ISPC_MT_RequiresTopLevelForLoop";

            string paramList = BuildIspcParamList(fields, false);
            string callArgs = BuildIspcCallArgs(fields, false);

            // 为每个 for 循环生成 task 函数
            foreach (var (idx, loop) in loops)
            {
                string taskFuncName = baseName + "_task" + idx;
                var taskParameters = string.IsNullOrEmpty(paramList) ? "uniform int __startIndex, uniform int __count"
                    : $"uniform int __startIndex, uniform int __count, {paramList}";
                sb.AppendLine($"task void {taskFuncName}({taskParameters})");
                sb.AppendLine("{");
                sb.AppendLine($"{Indent}if (programIndex != 0) return;");
                sb.AppendLine($"{Indent}uniform int n_per_task = max(1, __count / taskCount);");
                sb.AppendLine($"{Indent}uniform int start = __startIndex + taskIndex * n_per_task;");
                sb.AppendLine($"{Indent}uniform int end = (taskIndex == taskCount - 1) ? (__startIndex + __count) : min(start + n_per_task, __startIndex + __count);");
                sb.AppendLine($"{Indent}for (uniform int {loop.IndexName} = start; {loop.IndexName} < end; {loop.IndexName}++) {{");

                var translator = new MethodIspcTranslator(semanticModel, method, initialIndent: 2, needResult: false, useUniformVars: true);
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
            sb.AppendLine($"{Indent}if (programIndex != 0) return;");
            var directTranslator = new MethodIspcTranslator(semanticModel, method, initialIndent: 1, needResult: false, useUniformVars: true);

            // 每个源循环完成后再执行下一条语句，保留跨循环的数据依赖。
            foreach (var stmt in methodSyntax.Body.Statements)
            {
                if (stmt is ForStatementSyntax)
                {
                    var (idx, loop) = loops[nextTaskIdx];
                    var taskArgs = string.IsNullOrEmpty(callArgs) ? $"0, {loop.Limit}" : $"0, {loop.Limit}, {callArgs}";
                    sb.AppendLine($"{Indent}launch[max(1, numTasks)] {baseName}_task{idx}({taskArgs});");
                    sb.AppendLine($"{Indent}sync;");
                    nextTaskIdx++;
                }
                else
                {
                    string stmtCode = directTranslator.TranslateSingleStatement(stmt);
                    sb.Append(stmtCode);
                }
            }

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
            }

            sb.Append(GenerateContextFillCode(fields, isFill: false));
            if (!isVoid) sb.AppendLine("    return __result_temp;");
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

        private struct LoopInfo
        {
            public ForStatementSyntax ForStmt;
            public string IndexName;
            public string Limit;
        }

        //                       内部辅助方法

        private static LoopInfo? ExtractLoopInfo(ForStatementSyntax forStmt, SemanticModel semanticModel)
        {
            if (!IntForLoop.TryAnalyze(forStmt, semanticModel, out var loop)
                || loop.Step != 1 || loop.Comparison != SyntaxKind.LessThanExpression
                || semanticModel.GetConstantValue(loop.Start).Value is not int start || start != 0)
                return null;
            var decl = forStmt.Declaration;
            if (decl.Variables.Count != 1) return null;
            var varDecl = decl.Variables[0];
            string indexName = varDecl.Identifier.Text;
            if (forStmt.Condition is not BinaryExpressionSyntax binExpr ||
                !binExpr.OperatorToken.IsKind(SyntaxKind.LessThanToken)) return null;
            string? limit = TryExtractConstant(loop.Bound, semanticModel);
            if (limit == null) return null;
            return new LoopInfo { ForStmt = forStmt, IndexName = indexName, Limit = limit };
        }

        private static string? TryExtractConstant(ExpressionSyntax expr, SemanticModel semanticModel)
        {
            var symbol = semanticModel.GetSymbolInfo(expr).Symbol;
            if (symbol is IFieldSymbol field && field.HasConstantValue) return field.ConstantValue?.ToString();
            if (symbol is ILocalSymbol local && local.HasConstantValue) return local.ConstantValue?.ToString();
            if (expr is LiteralExpressionSyntax literal)
            {
                if (literal.Token.Value is int intVal) return intVal.ToString();
                if (literal.Token.Value is long longVal) return longVal.ToString();
            }
            return null;
        }

        //               内部类：静态方法 ISPC 翻译器

        private class MethodIspcTranslator : IspcStatementTranslator
        {
            private readonly bool _hasResult;
            public MethodIspcTranslator(SemanticModel semanticModel, IMethodSymbol method,
                int initialIndent = 0, bool needResult = false, bool useUniformVars = false)
                : base(semanticModel, method, null, false, useUniformVars)
            {
                _indentLevel = initialIndent;
                _hasResult = needResult;
            }

            public string TranslateSingleStatement(StatementSyntax stmt)
            {
                int startLen = _builder.Length;
                TranslateStatement(stmt);
                int endLen = _builder.Length;
                string result = _builder.ToString(startLen, endLen - startLen);
                _builder.Length = startLen;
                return result;
            }

            protected override void TranslateIdentifier(IdentifierNameSyntax identifier)
            {
                string name = identifier.Identifier.Text;
                if (_valueParameterNames.Contains(name))
                {
                    _builder.Append($"(*{name}_ptr)");
                    return;
                }
                base.TranslateIdentifier(identifier);
            }

            protected override void TranslateReturnStatement(ReturnStatementSyntax returnStmt)
            {
                if (_hasResult && returnStmt.Expression != null)
                {
                    AppendIndent();
                    _builder.Append("*__result_ptr = ");
                    TranslateExpression(returnStmt.Expression);
                    _builder.AppendLine(";");
                }
                AppendIndent();
                _builder.AppendLine("return;");
            }

            protected override void TranslateMathFunctionCall(IMethodSymbol methodSymbol, InvocationExpressionSyntax invocation)
            {
                string func = methodSymbol.Name switch
                {
                    "Abs" => "abs", "Acos" => "acos", "Asin" => "asin", "Atan" => "atan", "Atan2" => "atan2",
                    "Ceiling" => "ceil", "Cos" => "cos", "Cosh" => "cosh", "Exp" => "exp", "Floor" => "floor",
                    "Log" => "log", "Log10" => "log10", "Max" => "max", "Min" => "min", "Pow" => "pow",
                    "Round" => "round", "Sin" => "sin", "Sinh" => "sinh", "Sqrt" => "sqrt", "Tan" => "tan",
                    "Tanh" => "tanh", "Truncate" => "trunc", _ => null
                };
                if (func != null)
                {
                    _builder.Append(func).Append('(');
                    var args = invocation.ArgumentList.Arguments;
                    for (int i = 0; i < args.Count; i++)
                    {
                        if (i > 0) _builder.Append(", ");
                        TranslateExpression(args[i].Expression);
                    }
                    _builder.Append(')');
                }
                else base.TranslateMathFunctionCall(methodSymbol, invocation);
            }
        }
    }
}
