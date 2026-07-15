// ============================================================
// IspcGenerator.cs — 统一 ISPC 代码生成器
//   合并了原 IspcMethodGenerator.cs + IspcJobGenerator.cs 的功能，
//   共享 NativeList 上下文结构、类型转换、回调生成等逻辑。
// ============================================================
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// ISPC 代码统一生成器。
    /// 同时支持静态方法（IMethodSymbol）和 Job 结构体（INamedTypeSymbol）的 ISPC 生成。
    /// 消除原 IspcMethodGenerator + IspcJobGenerator 中大量重复的辅助逻辑。
    /// </summary>
    public static class IspcGenerator
    {
        private const string Indent = "    ";

        // ---------- 共享内部类 ----------

        private class TypeSymbolComparer : IEqualityComparer<ITypeSymbol>
        {
            public bool Equals(ITypeSymbol x, ITypeSymbol y) => SymbolEqualityComparer.Default.Equals(x, y);
            public int GetHashCode(ITypeSymbol obj) => SymbolEqualityComparer.Default.GetHashCode(obj);
        }

        private struct LoopInfo
        {
            public ForStatementSyntax ForStmt;
            public string IndexName;
            public string Limit;
        }

        // ---------- 类型映射 ----------

        /// <summary>将 C++ 类型名映射为 ISPC 类型名（除去命名空间前缀）</summary>
        private static string ToIspcType(string cppType) => cppType switch
        {
            "EntJoy::Mathematics::float2" => "float2",
            "EntJoy::Mathematics::int2" => "int2",
            "EntJoy::Mathematics::uint2" => "uint2",
            _ when cppType.Contains("::") => cppType.Substring(cppType.LastIndexOf("::") + 2),
            _ => cppType
        };

        private static bool IsVectorType(string cppType) =>
            cppType == "EntJoy::Mathematics::float2" ||
            cppType == "EntJoy::Mathematics::int2" ||
            cppType == "EntJoy::Mathematics::uint2";

        // ---------- 参数列表构建（共享） ----------

        /// <summary>
        /// 为 ISPC 函数构建参数列表。
        /// 支持两种调用者：静态方法 (IMethodSymbol) 和 Job 结构体 (INamedTypeSymbol)。
        /// </summary>
        private static string BuildIspcParamList(
            IEnumerable<(ITypeSymbol type, string name)> fields,
            bool includeStartCount,
            string? resultType = null)
        {
            var pars = new StringBuilder();
            if (includeStartCount)
                pars.Append("uniform int __startIndex, uniform int __count");

            foreach (var (type, name) in fields)
            {
                if (pars.Length > 0) pars.Append(", ");
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                {
                    if (type.Name == "NativeList")
                    {
                        var elemType = ((INamedTypeSymbol)type).TypeArguments[0];
                        var ispcElem = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                        pars.Append($"uniform UnsafeList_Context_{ispcElem}* uniform {name}");
                    }
                    else
                    {
                        var elemType = ((INamedTypeSymbol)type).TypeArguments[0];
                        var ispcElem = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                        pars.Append($"uniform {ispcElem} {name}_ptr[], uniform int {name}_length");
                    }
                }
                else if (type is IPointerTypeSymbol ptrType)
                {
                    var baseCpp = NativeTranspiler.MapCSharpTypeToCpp(ptrType.PointedAtType);
                    pars.Append($"uniform {ToIspcType(baseCpp)} * uniform {name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                    pars.Append($"uniform {ToIspcType(cppType)} * uniform {name}_ptr");
                }
            }
            if (resultType != null)
            {
                if (pars.Length > 0) pars.Append(", ");
                pars.Append($"uniform {resultType} * uniform __result_ptr");
            }
            return pars.ToString();
        }

        private static string BuildIspcCallArgs(IEnumerable<(ITypeSymbol type, string name)> fields, bool includeStartCount)
        {
            var args = new StringBuilder();
            if (includeStartCount)
                args.Append("__startIndex, __count");
            bool first = !includeStartCount;
            foreach (var (type, name) in fields)
            {
                if (!first) args.Append(", ");
                first = false;
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                    args.Append(type.Name == "NativeList" ? name : $"{name}_ptr, {name}_length");
                else
                    args.Append($"{name}_ptr");
            }
            return args.ToString();
        }

        /// <summary>
        /// 构建 C++ Wrapper 的参数列表（供 wrapper.cpp 使用）。
        /// </summary>
        private static string BuildCppWrapperParamList(
            IEnumerable<(ITypeSymbol type, string name)> fields,
            bool includeStartCount)
        {
            var pars = new StringBuilder();
            if (includeStartCount)
                pars.Append("int __startIndex, int __count");

            foreach (var (type, name) in fields)
            {
                if (pars.Length > 0) pars.Append(", ");
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                {
                    if (type.Name == "NativeList")
                    {
                        var elemType = ((INamedTypeSymbol)type).TypeArguments[0];
                        var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                        pars.Append($"EntJoy::Collections::UnsafeList<{cppElem}>* RESTRICT {name}_listData");
                    }
                    else
                    {
                        var elemType = ((INamedTypeSymbol)type).TypeArguments[0];
                        var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                        pars.Append($"{cppElem}* RESTRICT {name}_ptr, int {name}_length");
                    }
                }
                else if (type is IPointerTypeSymbol)
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                    pars.Append($"{cppType} RESTRICT {name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                    pars.Append($"{cppType}* RESTRICT {name}_ptr");
                }
            }
            return pars.ToString();
        }

        private static string BuildIspcCallArgsForWrapper(
            IEnumerable<(ITypeSymbol type, string name)> fields,
            bool includeStartCount)
        {
            var args = new StringBuilder();
            if (includeStartCount)
                args.Append("__startIndex, __count");
            bool first = !includeStartCount;
            foreach (var (type, name) in fields)
            {
                if (!first) args.Append(", ");
                first = false;
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                {
                    if (type.Name == "NativeList")
                        args.Append($"&{name}_ctx");
                    else
                    {
                        var elemType = ((INamedTypeSymbol)type).TypeArguments[0];
                        var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                        if (IsVectorType(cppElem))
                        {
                            var ispcType = ToIspcType(cppElem);
                            args.Append($"reinterpret_cast<ispc::{ispcType}*>({name}_ptr), {name}_length");
                        }
                        else
                            args.Append($"{name}_ptr, {name}_length");
                    }
                }
                else if (type is IPointerTypeSymbol)
                    args.Append($"{name}_ptr");
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                    if (IsVectorType(cppType))
                    {
                        var ispcType = ToIspcType(cppType);
                        args.Append($"reinterpret_cast<ispc::{ispcType}*>({name}_ptr)");
                    }
                    else
                        args.Append($"{name}_ptr");
                }
            }
            return args.ToString();
        }

        // ---------- 共享 ISPC 结构体生成 ----------

        /// <summary>
        /// 生成 UnsafeList_Context 结构体定义 + include 头文件。
        /// </summary>
        private static void WriteIspcPreamble(StringBuilder sb,
            IEnumerable<(ITypeSymbol type, string name)> fields,
            List<string> extraIncludes)
        {
            sb.AppendLine("#include \"EntJoyCommon.ispc\"");
            foreach (var include in extraIncludes)
                sb.AppendLine($"#include \"{include}.ispc\"");
            sb.AppendLine();

            foreach (var (type, _) in fields)
            {
                if (!NativeTranspiler.IsEntJoyNativeContainerType(type) || type.Name != "NativeList")
                    continue;
                var elemType = ((INamedTypeSymbol)type).TypeArguments[0];
                var ispcElem = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                sb.AppendLine($"struct UnsafeList_Context_{ispcElem} {{");
                sb.AppendLine($"{Indent}void* uniform _data;");
                sb.AppendLine($"{Indent}uniform int _length;");
                sb.AppendLine($"{Indent}uniform int _capacity;");
                sb.AppendLine($"{Indent}uniform int _allocator;");
                sb.AppendLine($"{Indent}void (* uniform ResizeFunc)(void* uniform * uniform _data, uniform int * uniform _length, uniform int * uniform _capacity, uniform int * uniform _allocator, uniform int newSize, uniform bool clear);");
                sb.AppendLine("};");
                sb.AppendLine();
            }
        }

        // ---------- 共享 C++ Resize 回调生成 ----------

        private static void GenerateResizeCallbacks(StringBuilder sb,
            IEnumerable<(ITypeSymbol type, string name)> fields)
        {
            var types = fields
                .Where(f => NativeTranspiler.IsEntJoyNativeContainerType(f.type) && f.type.Name == "NativeList")
                .Select(f => ((INamedTypeSymbol)f.type).TypeArguments[0])
                .Distinct(new TypeSymbolComparer());

            foreach (var elemType in types)
            {
                var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                var ispcElem = ToIspcType(cppElem);
                sb.AppendLine($"static void UnsafeList_Resize_{ispcElem}_callback(void** data, int* length, int* capacity, int* allocator, int newSize, bool clear) {{");
                sb.AppendLine($"    using Alloc = EntJoy::Collections::Allocator;");
                sb.AppendLine($"    EntJoy::Collections::UnsafeList<{cppElem}> tmp;");
                sb.AppendLine($"    tmp.Ptr = static_cast<{cppElem}*>(*data);");
                sb.AppendLine($"    tmp.Length = *length;");
                sb.AppendLine($"    tmp.Capacity = *capacity;");
                sb.AppendLine($"    tmp.Allocator = static_cast<Alloc>(*allocator);");
                sb.AppendLine($"    EntJoy::Collections::NativeArrayOptions opts = clear ? EntJoy::Collections::NativeArrayOptions::ClearMemory : EntJoy::Collections::NativeArrayOptions::UninitializedMemory;");
                sb.AppendLine($"    tmp.Resize(newSize, opts);");
                sb.AppendLine($"    *data = tmp.Ptr;");
                sb.AppendLine($"    *length = tmp.Length;");
                sb.AppendLine($"    *capacity = tmp.Capacity;");
                sb.AppendLine($"    *allocator = static_cast<int>(tmp.Allocator);");
                sb.AppendLine("}");
            }
        }

        /// <summary>
        /// 生成 C++ wrapper 中 NativeList 上下文的填充/更新代码。
        /// </summary>
        private static string GenerateContextFillCode(
            IEnumerable<(ITypeSymbol type, string name)> fields,
            bool isFill) // true = fill, false = update
        {
            var sb = new StringBuilder();
            foreach (var (fieldType, name) in fields)
            {
                if (!NativeTranspiler.IsEntJoyNativeContainerType(fieldType) || fieldType.Name != "NativeList")
                    continue;
                var elemType = ((INamedTypeSymbol)fieldType).TypeArguments[0];
                var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                var ispcElem = ToIspcType(cppElem);

                if (isFill)
                {
                    sb.AppendLine($"    ispc::UnsafeList_Context_{ispcElem} {name}_ctx;");
                    sb.AppendLine($"    {name}_ctx._data = {name}_listData->Ptr;");
                    sb.AppendLine($"    {name}_ctx._length = {name}_listData->Length;");
                    sb.AppendLine($"    {name}_ctx._capacity = {name}_listData->Capacity;");
                    sb.AppendLine($"    {name}_ctx._allocator = static_cast<int>({name}_listData->Allocator);");
                    sb.AppendLine($"    {name}_ctx.ResizeFunc = UnsafeList_Resize_{ispcElem}_callback;");
                }
                else
                {
                    sb.AppendLine($"    {name}_listData->Length = {name}_ctx._length;");
                    sb.AppendLine($"    {name}_listData->Capacity = {name}_ctx._capacity;");
                    sb.AppendLine($"    {name}_listData->Ptr = static_cast<{cppElem}*>({name}_ctx._data);");
                    sb.AppendLine($"    {name}_listData->Allocator = static_cast<EntJoy::Collections::Allocator>({name}_ctx._allocator);");
                }
            }
            return sb.ToString();
        }

        // ---------- 字段提取 & include 收集（共享） ----------

        private static List<(ITypeSymbol type, string name)> GetFieldsFromMethod(IMethodSymbol method)
            => method.Parameters.Select(p => (p.Type, p.Name)).ToList();

        private static List<(ITypeSymbol type, string name)> GetFieldsFromJob(INamedTypeSymbol jobStruct)
            => jobStruct.GetMembers().OfType<IFieldSymbol>()
                .Where(f => !f.IsStatic)
                .Select(f => (f.Type, f.Name))
                .ToList();

        private static HashSet<string> CollectIncludesFromFields(
            IEnumerable<(ITypeSymbol type, string name)> fields)
        {
            var includes = new HashSet<string>();
            foreach (var (type, _) in fields)
                CollectTypeInclude(type, includes);
            return includes;
        }

        private static void CollectTypeInclude(ITypeSymbol type, HashSet<string> includes)
        {
            if (type is IPointerTypeSymbol ptr)
            {
                CollectTypeInclude(ptr.PointedAtType, includes);
                return;
            }
            if (NativeTranspiler.IsEntJoyPredefinedType(type)) return;
            if (type.IsValueType && !NativeTranspiler.IsBuiltinUnmanaged(type))
                includes.Add(NativeTranspiler.GetStructHeaderFileName((INamedTypeSymbol)type));
        }

        // ===================================================================
        //                       公共 API：静态方法
        // ===================================================================

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

            foreach (var stmt in methodSyntax.Body.Statements)
            {
                if (stmt is ForStatementSyntax)
                {
                    var (idx, loop) = loops[nextTaskIdx];
                    sb.AppendLine($"{Indent}launch[numTasks] {baseName}_task{idx}(0, {loop.Limit}, {callArgs});");
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
                sb.AppendLine($"HEAD void CALLINGCONVENTION {baseName}({cppParams})");
            else
                sb.AppendLine($"HEAD {cppReturnType} CALLINGCONVENTION {baseName}({cppParams})");
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

            sb.AppendLine($"HEAD void CALLINGCONVENTION {baseName}_mt({mtParams})");
            sb.AppendLine("{");
            sb.Append(GenerateContextFillCode(fields, isFill: true));

            string ispcCallArgs = BuildIspcCallArgsForWrapper(fields, false);
            string launchArgs = string.IsNullOrEmpty(ispcCallArgs) ? "numTasks" : ispcCallArgs + ", numTasks";
            sb.AppendLine($"    ispc::{baseName}_mt_impl({launchArgs});");
            sb.Append(GenerateContextFillCode(fields, isFill: false));
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ===================================================================
        //                       公共 API：Job 结构体
        // ===================================================================

        /// <summary>判断是否为纯 IJob（非 ParallelFor/For）</summary>
        public static bool IsIJob(INamedTypeSymbol jobStruct) =>
            jobStruct.AllInterfaces.Any(i => i.Name == "IJob") &&
            !CppJobGenerator.IsParallelForJob(jobStruct) &&
            !CppJobGenerator.IsForJob(jobStruct) &&
            !CppJobGenerator.IsChunkJob(jobStruct);

        /// <summary>获取 ISPC 基础函数名</summary>
        public static string GetIspcBaseName(INamedTypeSymbol jobStruct)
        {
            bool isBatch = CppJobGenerator.IsParallelForJob(jobStruct) || CppJobGenerator.IsForJob(jobStruct);
            return CppJobGenerator.GetCppJobFunctionName(jobStruct, isBatch: isBatch);
        }

        /// <summary>生成 Job 的 ISPC 源文件</summary>
        public static string GenerateIspcSource(INamedTypeSymbol jobStruct, Compilation compilation, HashSet<INamedTypeSymbol> userStructs)
        {
            var sb = new StringBuilder();
            var baseName = GetIspcBaseName(jobStruct);
            sb.AppendLine($"// Auto-generated ISPC source for {jobStruct.Name}");

            var fields = GetFieldsFromJob(jobStruct);
            var includes = CollectIncludesFromFields(fields);
            if (CppJobGenerator.IsEntityJob(jobStruct))
            {
                var executeMethodForIncludes = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
                foreach (var parameter in executeMethodForIncludes.Parameters)
                    CollectTypeInclude(parameter.Type, includes);
            }
            if (CppJobGenerator.IsChunkJob(jobStruct))
            {
                foreach (var component in CppJobGenerator.CollectChunkNativeArrayTypes(jobStruct, compilation))
                    includes.Add(NativeTranspiler.GetStructHeaderFileName(component));
            }
            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null)
            {
                sb.AppendLine("// Error: no Execute body found.");
                return sb.ToString();
            }

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            if (CppJobGenerator.IsEntityJob(jobStruct))
            {
                GenerateIspcEntityFunction(sb, baseName + "_impl", jobStruct, semanticModel, methodSyntax);
            }
            else if (CppJobGenerator.IsChunkJob(jobStruct))
            {
                GenerateIspcChunkFunction(sb, baseName + "_impl", jobStruct, compilation, semanticModel, methodSyntax);
            }
            else if (IsIJob(jobStruct))
            {
                // IJob: 无循环、无 __startIndex/__count，单次执行
                GenerateIspcIJobFunction(sb, baseName + "_impl", jobStruct, semanticModel, methodSyntax);
            }
            else
            {
                // IJobParallelFor / IJobFor: 批处理模式，使用 foreach 或 for (uniform int)
                var conditionalFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel);
                var boolFields = conditionalFields.Where(f => f.Type.SpecialType == SpecialType.System_Boolean).ToList();
                string indexName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;

                if (boolFields.Count > 0)
                {
                    // 生成所有 2^n 个 bool 组合变体
                    int totalVariants = 1 << boolFields.Count;
                    for (int mask = 0; mask < totalVariants; mask++)
                    {
                        var values = new List<bool>();
                        for (int i = 0; i < boolFields.Count; i++)
                            values.Add((mask & (1 << i)) != 0);
                        
                        string suffix = "_" + string.Join("_", values.Select(v => v ? "true" : "false")) + "_impl";
                        GenerateIspcFunction(sb, baseName + suffix, jobStruct, semanticModel, methodSyntax, indexName, boolFields, values);
                        if (mask < totalVariants - 1)
                            sb.AppendLine();
                    }
                }
                else
                {
                    GenerateIspcFunction(sb, baseName + "_impl", jobStruct, semanticModel, methodSyntax, indexName, null, null);
                }
            }

            return sb.ToString();
        }

        /// <summary>生成 Job 的 ISPC 多任务源文件</summary>
        public static string GenerateIspcMTSource(INamedTypeSymbol jobStruct, Compilation compilation, HashSet<INamedTypeSymbol> userStructs)
        {
            if (CppJobGenerator.IsEntityJob(jobStruct))
                return GenerateIspcEntityMTSource(jobStruct, compilation);

            if (CppJobGenerator.IsChunkJob(jobStruct))
                return GenerateIspcChunkMTSource(jobStruct, compilation);

            if (!CppJobGenerator.IsParallelForJob(jobStruct) && !CppJobGenerator.IsForJob(jobStruct))
                return "// ISPC MT only supports IJobParallelFor / IJobFor.";

            var sb = new StringBuilder();
            var baseName = GetIspcBaseName(jobStruct);
            sb.AppendLine($"// Auto-generated ISPC MT source for {jobStruct.Name}");

            var fields = GetFieldsFromJob(jobStruct);
            var includes = CollectIncludesFromFields(fields);
            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return "// Error: no Execute body";

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var conditionalFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel);
            var boolField = conditionalFields.FirstOrDefault(f => f.Type.SpecialType == SpecialType.System_Boolean);
            string indexName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;

            if (boolField != null)
            {
                GenerateIspcTaskFunction(sb, baseName + "_true_task", jobStruct, semanticModel, methodSyntax, indexName, boolField.Name, true);
                sb.AppendLine();
                GenerateIspcMTEntry(sb, baseName + "_true_mt_impl", jobStruct, baseName + "_true_task");
                sb.AppendLine();
                GenerateIspcTaskFunction(sb, baseName + "_false_task", jobStruct, semanticModel, methodSyntax, indexName, boolField.Name, false);
                sb.AppendLine();
                GenerateIspcMTEntry(sb, baseName + "_false_mt_impl", jobStruct, baseName + "_false_task");
            }
            else
            {
                GenerateIspcTaskFunction(sb, baseName + "_task", jobStruct, semanticModel, methodSyntax, indexName, null, false);
                sb.AppendLine();
                GenerateIspcMTEntry(sb, baseName + "_mt_impl", jobStruct, baseName + "_task");
            }

            return sb.ToString();
        }

        /// <summary>生成 Job 的 C++ Wrapper（单线程）</summary>
        public static string GenerateCppWrapper(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            if (CppJobGenerator.IsEntityJob(jobStruct))
                return GenerateCppEntityBatchWrapper(jobStruct, compilation);

            if (CppJobGenerator.IsChunkJob(jobStruct))
                return GenerateCppChunkWrapper(jobStruct, compilation);

            var sb = new StringBuilder();
            var ispcBase = GetIspcBaseName(jobStruct);
            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine($"#include \"{ispcBase}_ispc.h\"");
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();

            var fields = GetFieldsFromJob(jobStruct);
            GenerateResizeCallbacks(sb, fields);

            bool hasBatch = CppJobGenerator.IsParallelForJob(jobStruct) || CppJobGenerator.IsForJob(jobStruct);
            string cppParamList = BuildCppWrapperParamList(fields, hasBatch);

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var conditionalFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel);
            var boolFields = conditionalFields.Where(f => f.Type.SpecialType == SpecialType.System_Boolean).ToList();

            void GenWrapper(string suffix, string ispcImplSuffix)
            {
                string funcName = ispcBase + suffix;
                string implName = ispcBase + ispcImplSuffix;
                sb.AppendLine($"HEAD void CALLINGCONVENTION {funcName}({cppParamList})");
                sb.AppendLine("{");
                sb.Append(GenerateContextFillCode(fields, isFill: true));
                sb.AppendLine($"    ispc::{implName}({BuildIspcCallArgsForWrapper(fields, hasBatch)});");
                sb.Append(GenerateContextFillCode(fields, isFill: false));
                sb.AppendLine("}");
                sb.AppendLine();
            }

            if (hasBatch)
            {
                if (boolFields.Count > 0)
                {
                    // 生成所有 2^n 个 bool 组合变体
                    int totalVariants = 1 << boolFields.Count;
                    for (int mask = 0; mask < totalVariants; mask++)
                    {
                        var values = new List<bool>();
                        for (int i = 0; i < boolFields.Count; i++)
                            values.Add((mask & (1 << i)) != 0);
                        
                        string suffix = CppJobGenerator.BuildBoolVariantSuffix(boolFields, values);
                        string ispcSuffix = "_" + string.Join("_", values.Select(v => v ? "true" : "false")) + "_impl";
                        GenWrapper(suffix, ispcSuffix);
                    }
                }
                else
                {
                    GenWrapper("", "_impl");
                }
            }
            else
            {
                GenWrapper("", "_impl");
            }

            return sb.ToString();
        }

        private static void GenerateIspcEntityFunction(StringBuilder sb, string functionName,
            INamedTypeSymbol jobStruct, SemanticModel semanticModel, MethodDeclarationSyntax methodSyntax)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var fields = GetFieldsFromJob(jobStruct);
            var pars = new StringBuilder();

            foreach (var parameter in executeMethod.Parameters)
            {
                if (pars.Length > 0) pars.Append(", ");
                var ispcType = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(parameter.Type));
                pars.Append($"uniform {ispcType} {parameter.Name}_ptr[]");
            }

            if (pars.Length > 0) pars.Append(", ");
            pars.Append("uniform int __entity_count");

            foreach (var (type, name) in fields)
            {
                if (pars.Length > 0) pars.Append(", ");
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                pars.Append($"uniform {ToIspcType(cppType)} * uniform {name}_ptr");
            }

            sb.AppendLine($"export void {functionName}({pars})");
            sb.AppendLine("{");
            AppendUniformVariableDeclarations(sb, jobStruct);
            sb.AppendLine($"{Indent}foreach (__entity_index = 0 ... __entity_count) {{");

            foreach (var parameter in executeMethod.Parameters)
            {
                var ispcType = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(parameter.Type));
                sb.AppendLine($"{Indent}{Indent}{ispcType} {parameter.Name} = {parameter.Name}_ptr[__entity_index];");
            }

            var translator = new IspcStatementTranslator(semanticModel, jobStruct, null, false);
            var bodyCode = translator.Translate(methodSyntax.Body);
            foreach (var line in bodyCode.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
            {
                if (line.Length == 0) continue;
                sb.Append(Indent).Append(line).AppendLine();
            }

            foreach (var parameter in executeMethod.Parameters)
            {
                if (parameter.RefKind == RefKind.Ref)
                    sb.AppendLine($"{Indent}{Indent}{parameter.Name}_ptr[__entity_index] = {parameter.Name};");
            }

            sb.AppendLine($"{Indent}}}");
            sb.AppendLine("}");
        }

        private static string GenerateIspcEntityMTSource(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            var baseName = GetIspcBaseName(jobStruct);
            sb.AppendLine($"// Auto-generated ISPC MT source for {jobStruct.Name}");

            var fields = GetFieldsFromJob(jobStruct);
            var includes = CollectIncludesFromFields(fields);
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            foreach (var parameter in executeMethod.Parameters)
                CollectTypeInclude(parameter.Type, includes);
            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return "// Error: no Execute body";
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            var pars = new StringBuilder();
            foreach (var parameter in executeMethod.Parameters)
            {
                if (pars.Length > 0) pars.Append(", ");
                var ispcType = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(parameter.Type));
                pars.Append($"uniform {ispcType} {parameter.Name}_ptr[]");
            }
            if (pars.Length > 0) pars.Append(", ");
            pars.Append("uniform int __entity_count");
            foreach (var (type, name) in fields)
            {
                if (pars.Length > 0) pars.Append(", ");
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                pars.Append($"uniform {ToIspcType(cppType)} * uniform {name}_ptr");
            }

            sb.AppendLine($"task void {baseName}_task({pars})");
            sb.AppendLine("{");
            sb.AppendLine($"{Indent}uniform int n_per_task = max(1, __entity_count / taskCount);");
            sb.AppendLine($"{Indent}uniform int __task_start = taskIndex * n_per_task;");
            sb.AppendLine($"{Indent}uniform int __task_end = (taskIndex == taskCount - 1) ? __entity_count : min(__task_start + n_per_task, __entity_count);");
            AppendUniformVariableDeclarations(sb, jobStruct);
            sb.AppendLine($"{Indent}foreach (__entity_index = __task_start ... __task_end) {{");
            foreach (var parameter in executeMethod.Parameters)
            {
                var ispcType = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(parameter.Type));
                sb.AppendLine($"{Indent}{Indent}{ispcType} {parameter.Name} = {parameter.Name}_ptr[__entity_index];");
            }

            var translator = new IspcStatementTranslator(semanticModel, jobStruct, null, false);
            var bodyCode = translator.Translate(methodSyntax.Body);
            foreach (var line in bodyCode.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
            {
                if (line.Length == 0) continue;
                sb.Append(Indent).Append(line).AppendLine();
            }
            foreach (var parameter in executeMethod.Parameters)
            {
                if (parameter.RefKind == RefKind.Ref)
                    sb.AppendLine($"{Indent}{Indent}{parameter.Name}_ptr[__entity_index] = {parameter.Name};");
            }
            sb.AppendLine($"{Indent}}}");
            sb.AppendLine("}");
            sb.AppendLine();

            var mtCallArgs = new List<string>();
            foreach (var parameter in executeMethod.Parameters)
                mtCallArgs.Add($"{parameter.Name}_ptr");
            mtCallArgs.Add("__entity_count");
            foreach (var (_, name) in fields)
                mtCallArgs.Add($"{name}_ptr");

            string mtParams = pars.Length == 0 ? "uniform int numTasks" : $"{pars}, uniform int numTasks";
            sb.AppendLine($"export void {baseName}_mt_impl({mtParams})");
            sb.AppendLine("{");
            sb.AppendLine($"{Indent}launch[numTasks] {baseName}_task({string.Join(", ", mtCallArgs)});");
            sb.AppendLine($"{Indent}sync;");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string GenerateIspcChunkMTSource(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            var baseName = GetIspcBaseName(jobStruct);
            sb.AppendLine($"// Auto-generated ISPC MT source for {jobStruct.Name}");

            var fields = GetFieldsFromJob(jobStruct);
            var includes = CollectIncludesFromFields(fields);
            var chunkArrays = CollectChunkNativeArrayLocals(jobStruct, compilation);
            foreach (var component in CppJobGenerator.CollectChunkNativeArrayTypes(jobStruct, compilation))
                includes.Add(NativeTranspiler.GetStructHeaderFileName(component));
            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return "// Error: no Execute body";
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var paramsList = BuildIspcChunkParamList(chunkArrays, fields);
            string entityCountExpr = chunkArrays.Count > 0 ? $"{chunkArrays[0].name}_length" : "__entity_count";
            string taskParams = string.IsNullOrEmpty(paramsList)
                ? "uniform int __entity_count"
                : $"{paramsList}, uniform int __entity_count";

            sb.AppendLine($"task void {baseName}_task({taskParams})");
            sb.AppendLine("{");
            sb.AppendLine($"{Indent}uniform int n_per_task = max(1, {entityCountExpr} / taskCount);");
            sb.AppendLine($"{Indent}uniform int __task_start = taskIndex * n_per_task;");
            sb.AppendLine($"{Indent}uniform int __task_end = (taskIndex == taskCount - 1) ? {entityCountExpr} : min(__task_start + n_per_task, {entityCountExpr});");
            AppendUniformVariableDeclarations(sb, jobStruct);

            var translator = new IspcChunkStatementTranslator(semanticModel, jobStruct, "__task_start", "__task_end");
            sb.Append(translator.Translate(methodSyntax.Body));
            sb.AppendLine("}");
            sb.AppendLine();

            string mtParams = string.IsNullOrEmpty(paramsList)
                ? "uniform int __entity_count, uniform int numTasks"
                : $"{paramsList}, uniform int __entity_count, uniform int numTasks";
            sb.AppendLine($"export void {baseName}_mt_impl({mtParams})");
            sb.AppendLine("{");
            string callArgs = string.IsNullOrEmpty(paramsList) ? "__entity_count" : $"{BuildIspcCallArgsForChunkMT(chunkArrays, fields)}, __entity_count";
            sb.AppendLine($"{Indent}launch[numTasks] {baseName}_task({callArgs});");
            sb.AppendLine($"{Indent}sync;");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string BuildIspcCallArgsForChunkMT(
            List<(INamedTypeSymbol type, string name)> chunkArrays,
            IEnumerable<(ITypeSymbol type, string name)> fields)
        {
            var args = new List<string>();
            foreach (var (_, name) in chunkArrays)
            {
                args.Add($"{name}_ptr");
                args.Add($"{name}_length");
            }
            foreach (var (type, name) in fields)
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                {
                    args.Add(type.Name == "NativeList" ? name : $"{name}_ptr, {name}_length");
                }
                else
                {
                    args.Add($"{name}_ptr");
                }
            }
            return string.Join(", ", args);
        }

        private static string GenerateCppEntityBatchWrapper(INamedTypeSymbol jobStruct, Compilation compilation, bool useMt = false)
        {
            var sb = new StringBuilder();
            var ispcBase = GetIspcBaseName(jobStruct);
            var ispcHeaderBase = useMt ? ispcBase + "_mt" : ispcBase;
            var ispcImplName = useMt ? ispcBase + "_mt_impl" : ispcBase + "_impl";
            var adapterFuncName = CppJobGenerator.GetEntityBatchAdapterFunctionName(jobStruct);
            var adapterGetterName = CppJobGenerator.GetEntityBatchAdapterPtrGetterName(jobStruct);
            var fields = jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");

            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine("#include \"../../NativeDll/EntityBatchData.h\"");
            sb.AppendLine($"#include \"{ispcHeaderBase}_ispc.h\"");
            if (useMt)
                sb.AppendLine("#include <thread>");
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();
            GenerateResizeCallbacks(sb, fields.Select(f => (f.Type, f.Name)));
            sb.AppendLine();
            sb.AppendLine("struct __EntJoyChunkContextHeader");
            sb.AppendLine("{");
            sb.AppendLine("    int chunkCount;");
            sb.AppendLine("    int hasEnabledFilter;");
            sb.AppendLine("    void* queryAllEnabledTypes;");
            sb.AppendLine("    int allEnabledCount;");
            sb.AppendLine("    int gcHandleStartIndex;");
            sb.AppendLine("    void* chunksPtr;");
            sb.AppendLine("    int cleanupInProgress;");
            sb.AppendLine("    void* requiredComponentTypeIds;");
            sb.AppendLine("    int requiredComponentTypeIdCount;");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine($"HEAD void CALLINGCONVENTION {adapterFuncName}(void* context, const EntityBatchData* __batches, int __batch_start, int __batch_count)");
            sb.AppendLine("{");
            sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
            sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
            sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
            sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
            sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
            sb.AppendLine();

            int currentOffset = 0;
            var fieldArgs = new List<string>();
            foreach (var field in fields)
            {
                int offset = CppJobGenerator.CalculateFieldOffset(field, ref currentOffset);
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                var ispcType = ToIspcType(cppType);
                if (IsVectorType(cppType) || cppType.Contains("::"))
                    sb.AppendLine($"    auto* {field.Name}_ptr = reinterpret_cast<ispc::{ispcType}*>(({cppType}*)(__jobContext + {offset}));");
                else
                    sb.AppendLine($"    auto* {field.Name}_ptr = ({cppType}*)(__jobContext + {offset});");
                fieldArgs.Add($"{field.Name}_ptr");
            }

            sb.AppendLine("    const int __batch_end = __batch_start + __batch_count;");
            sb.AppendLine("    for (int __batch_index = __batch_start; __batch_index < __batch_end; ++__batch_index)");
            sb.AppendLine("    {");
            sb.AppendLine("        const EntityBatchData* __batchData = &__batches[__batch_index];");

            var callArgs = new List<string>();
            for (int i = 0; i < executeMethod.Parameters.Length; i++)
            {
                var parameter = executeMethod.Parameters[i];
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(parameter.Type);
                var ispcType = ToIspcType(cppType);
                sb.AppendLine($"        auto* {parameter.Name}_ptr = reinterpret_cast<ispc::{ispcType}*>(__batchData->componentArrays[{i}]);");
                callArgs.Add($"{parameter.Name}_ptr");
            }
            callArgs.Add("__batchData->entityCount");
            callArgs.AddRange(fieldArgs);
            if (useMt)
                callArgs.Add("std::thread::hardware_concurrency()");
            sb.AppendLine($"        ispc::{ispcImplName}({string.Join(", ", callArgs)});");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"HEAD void* CALLINGCONVENTION {adapterGetterName}()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){adapterFuncName};");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string GenerateCppChunkWrapper(INamedTypeSymbol jobStruct, Compilation compilation, bool useMt = false)
        {
            var sb = new StringBuilder();
            var ispcBase = GetIspcBaseName(jobStruct);
            var ispcHeaderBase = useMt ? ispcBase + "_mt" : ispcBase;
            var ispcImplName = useMt ? ispcBase + "_mt_impl" : ispcBase + "_impl";
            var adapterFuncName = CppJobGenerator.GetAdapterFunctionName(jobStruct);
            var adapterGetterName = CppJobGenerator.GetAdapterPtrGetterName(jobStruct);
            var rangeAdapterFuncName = CppJobGenerator.GetRangeAdapterFunctionName(jobStruct);
            var rangeAdapterGetterName = CppJobGenerator.GetRangeAdapterPtrGetterName(jobStruct);
            var chunkArrays = CollectChunkNativeArrayLocals(jobStruct, compilation);
            var fields = jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();

            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine("#include \"../../NativeDll/ChunkJobData.h\"");
            sb.AppendLine("#include \"../../NativeDll/ChunkNativeArray.h\"");
            sb.AppendLine($"#include \"{ispcHeaderBase}_ispc.h\"");
            if (useMt)
                sb.AppendLine("#include <thread>");
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();
            GenerateResizeCallbacks(sb, fields.Select(f => (f.Type, f.Name)));
            sb.AppendLine();
            sb.AppendLine("struct __EntJoyChunkContextHeader");
            sb.AppendLine("{");
            sb.AppendLine("    int chunkCount;");
            sb.AppendLine("    int hasEnabledFilter;");
            sb.AppendLine("    void* queryAllEnabledTypes;");
            sb.AppendLine("    int allEnabledCount;");
            sb.AppendLine("    int gcHandleStartIndex;");
            sb.AppendLine("    void* chunksPtr;");
            sb.AppendLine("    int cleanupInProgress;");
            sb.AppendLine("    void* requiredComponentTypeIds;");
            sb.AppendLine("    int requiredComponentTypeIdCount;");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine($"HEAD void CALLINGCONVENTION {adapterFuncName}(void* context, const ChunkJobData* __chunkData)");
            sb.AppendLine("{");
            sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
            sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
            sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
            sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
            sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
            sb.AppendLine("    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;");
            sb.AppendLine();

            var callArgs = new List<string>();
            for (int i = 0; i < chunkArrays.Count; i++)
            {
                var (type, name) = chunkArrays[i];
                var ispcType = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(type));
                sb.AppendLine($"    auto* {name}_ptr = reinterpret_cast<ispc::{ispcType}*>(__chunkData->requiredComponentArrays[{i}]);");
                sb.AppendLine($"    int {name}_length = __chunkData->entityCount;");
                callArgs.Add($"{name}_ptr");
                callArgs.Add($"{name}_length");
            }

            if (chunkArrays.Count > 0)
            {
                sb.AppendLine();
            }

            int currentOffset = 0;
            foreach (var field in fields)
            {
                int offset = CppJobGenerator.CalculateFieldOffset(field, ref currentOffset);
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (field.Type.Name == "NativeList")
                    {
                        var elemType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                        var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                        var ispcElem = ToIspcType(cppElem);
                        sb.AppendLine($"    auto* {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{cppElem}>**)(__jobContext + {offset});");
                        sb.AppendLine($"    ispc::UnsafeList_Context_{ispcElem} {field.Name}_ctx;");
                        sb.AppendLine($"    {field.Name}_ctx._data = {field.Name}_listData->Ptr;");
                        sb.AppendLine($"    {field.Name}_ctx._length = {field.Name}_listData->Length;");
                        sb.AppendLine($"    {field.Name}_ctx._capacity = {field.Name}_listData->Capacity;");
                        sb.AppendLine($"    {field.Name}_ctx._allocator = static_cast<int>({field.Name}_listData->Allocator);");
                        sb.AppendLine($"    {field.Name}_ctx.ResizeFunc = UnsafeList_Resize_{ispcElem}_callback;");
                        callArgs.Add($"&{field.Name}_ctx");
                    }
                    else
                    {
                        var elemType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                        var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                        var ispcElem = ToIspcType(cppElem);
                        sb.AppendLine($"    auto* {field.Name}_ptr = reinterpret_cast<ispc::{ispcElem}*>(*({cppElem}**)(__jobContext + {offset}));");
                        sb.AppendLine($"    int {field.Name}_length = *(int*)(__jobContext + {offset + 8});");
                        callArgs.Add($"{field.Name}_ptr");
                        callArgs.Add($"{field.Name}_length");
                    }
                }
                else if (field.Type is IPointerTypeSymbol ptrType)
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(ptrType.PointedAtType);
                    var ispcType = ToIspcType(cppType);
                    sb.AppendLine($"    auto* {field.Name}_ptr = reinterpret_cast<ispc::{ispcType}*>(*({cppType}**)(__jobContext + {offset}));");
                    callArgs.Add($"{field.Name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    var ispcType = ToIspcType(cppType);
                    if (IsVectorType(cppType) || cppType.Contains("::"))
                    {
                        sb.AppendLine($"    auto* {field.Name}_ptr = reinterpret_cast<ispc::{ispcType}*>(({cppType}*)(__jobContext + {offset}));");
                    }
                    else
                    {
                        sb.AppendLine($"    auto* {field.Name}_ptr = ({cppType}*)(__jobContext + {offset});");
                    }
                    callArgs.Add($"{field.Name}_ptr");
                }
            }

            sb.AppendLine();
            if (useMt)
            {
                callArgs.Add("__chunkData->entityCount");
                callArgs.Add("std::thread::hardware_concurrency()");
            }
            sb.AppendLine($"    ispc::{ispcImplName}({string.Join(", ", callArgs)});");

            foreach (var field in fields)
            {
                if (!NativeTranspiler.IsEntJoyNativeContainerType(field.Type) || field.Type.Name != "NativeList")
                {
                    continue;
                }

                var elemType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
                sb.AppendLine($"    {field.Name}_listData->Length = {field.Name}_ctx._length;");
                sb.AppendLine($"    {field.Name}_listData->Capacity = {field.Name}_ctx._capacity;");
                sb.AppendLine($"    {field.Name}_listData->Ptr = static_cast<{cppElem}*>({field.Name}_ctx._data);");
                sb.AppendLine($"    {field.Name}_listData->Allocator = static_cast<EntJoy::Collections::Allocator>({field.Name}_ctx._allocator);");
            }

            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"HEAD void* CALLINGCONVENTION {adapterGetterName}()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){adapterFuncName};");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"HEAD void CALLINGCONVENTION {rangeAdapterFuncName}(void* context, const ChunkJobData* __chunks, int __startIndex, int __count)");
            sb.AppendLine("{");
            sb.AppendLine("    const int __endIndex = __startIndex + __count;");
            sb.AppendLine("    for (int __chunkIndex = __startIndex; __chunkIndex < __endIndex; ++__chunkIndex)");
            sb.AppendLine("    {");
            sb.AppendLine($"        {adapterFuncName}(context, &__chunks[__chunkIndex]);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"HEAD void* CALLINGCONVENTION {rangeAdapterGetterName}()");
            sb.AppendLine("{");
            sb.AppendLine($"    return (void*){rangeAdapterFuncName};");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>生成 Job 的 C++ Wrapper（多线程）</summary>
        public static string GenerateCppWrapperMT(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            if (CppJobGenerator.IsEntityJob(jobStruct))
                return GenerateCppEntityBatchWrapper(jobStruct, compilation, useMt: true);

            if (CppJobGenerator.IsChunkJob(jobStruct))
                return GenerateCppChunkWrapper(jobStruct, compilation, useMt: true);

            if (!CppJobGenerator.IsParallelForJob(jobStruct) && !CppJobGenerator.IsForJob(jobStruct))
                return "// IJob does not have MT version.";

            var sb = new StringBuilder();
            var ispcBase = GetIspcBaseName(jobStruct);
            var fields = GetFieldsFromJob(jobStruct);

            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            sb.AppendLine($"#include \"{ispcBase}_mt_ispc.h\"");
            sb.AppendLine("#include <thread>");
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();

            GenerateResizeCallbacks(sb, fields);
            string cppParamList = BuildCppWrapperParamList(fields, true);

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var conditionalFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel);
            var boolField = conditionalFields.FirstOrDefault(f => f.Type.SpecialType == SpecialType.System_Boolean);

            void GenWrapperMT(string suffix, string ispcImplSuffix)
            {
                string funcName = ispcBase + suffix;
                string implName = ispcBase + ispcImplSuffix;
                sb.AppendLine($"HEAD void CALLINGCONVENTION {funcName}({cppParamList})");
                sb.AppendLine("{");
                sb.Append(GenerateContextFillCode(fields, isFill: true));
                sb.AppendLine("    int numTasks = std::thread::hardware_concurrency();");
                sb.AppendLine($"    ispc::{implName}({BuildIspcCallArgsForWrapper(fields, true)}, numTasks);");
                sb.Append(GenerateContextFillCode(fields, isFill: false));
                sb.AppendLine("}");
                sb.AppendLine();
            }

            if (boolField != null)
            {
                GenWrapperMT("_true_mt", "_true_mt_impl");
                GenWrapperMT("_false_mt", "_false_mt_impl");
            }
            else
            {
                GenWrapperMT("_mt", "_mt_impl");
            }

            return sb.ToString();
        }

        // ===================================================================
        //                       内部辅助方法
        // ===================================================================

        private static LoopInfo? ExtractLoopInfo(ForStatementSyntax forStmt, SemanticModel semanticModel)
        {
            if (forStmt.Declaration == null || forStmt.Condition == null) return null;
            var decl = forStmt.Declaration;
            if (decl.Variables.Count != 1) return null;
            var varDecl = decl.Variables[0];
            string indexName = varDecl.Identifier.Text;
            if (forStmt.Condition is not BinaryExpressionSyntax binExpr ||
                !binExpr.OperatorToken.IsKind(SyntaxKind.LessThanToken)) return null;
            string? limit = TryExtractConstant(binExpr.Right, semanticModel);
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

        /// <summary>
        /// 检测 Job 的 Execute 方法中是否有 NativeArray 被同时读写
        /// （即执行 read-modify-write 自修改操作）。
        /// 
        /// 注意：简单的 read-modify-write 模式（如 pos = arr[i]; ... arr[i] = pos;）
        /// 在 foreach 下是安全的，因为每个 SIMD lane 处理不同的 index。
        /// 只有通过 UnsafeUtility.ArrayElementAsRef 获取 ref 并修改时，
        /// 才需要 uniform for 保护（因为 ref 可能指向同一位置）。
        /// </summary>
        private static bool HasSelfModifyingNativeArray(
            INamedTypeSymbol jobStruct, SemanticModel semanticModel)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
            if (executeMethod == null) return false;
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return false;

            // 收集所有 NativeArray 字段名
            var nativeArrayFields = new HashSet<string>();
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type) && field.Type.Name == "NativeArray")
                    nativeArrayFields.Add(field.Name);
            }
            if (nativeArrayFields.Count == 0) return false;

            // 检测方法调用中的 ref 参数传递（如 UnsafeUtility.ArrayElementAsRef）
            // 这是唯一需要 uniform for 的情况，因为 ref 可能指向同一位置
            foreach (var node in methodSyntax.Body.DescendantNodes())
            {
                if (node is InvocationExpressionSyntax inv)
                {
                    var sym = semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                    if (sym != null && sym.Name == "ArrayElementAsRef" &&
                        sym.ContainingType?.ToDisplayString() == "EntJoy.Collections.UnsafeUtility")
                    {
                        if (inv.ArgumentList.Arguments.Count >= 1 &&
                            inv.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax arrId)
                        {
                            string name = arrId.Identifier.Text;
                            if (nativeArrayFields.Contains(name))
                                return true; // ArrayElementAsRef 需要 uniform for
                        }
                    }
                }
            }

            // 简单的 arr[i] = f(arr[i]) 模式在 foreach 下安全
            return false;
        }

        private static void GenerateIspcChunkFunction(StringBuilder sb, string functionName,
            INamedTypeSymbol jobStruct, Compilation compilation, SemanticModel semanticModel,
            MethodDeclarationSyntax methodSyntax)
        {
            var chunkArrays = CollectChunkNativeArrayLocals(jobStruct, compilation);
            var fields = GetFieldsFromJob(jobStruct);
            var paramsList = BuildIspcChunkParamList(chunkArrays, fields);

            sb.AppendLine($"export void {functionName}({paramsList})");
            sb.AppendLine("{");
            AppendUniformVariableDeclarations(sb, jobStruct);

            var translator = new IspcChunkStatementTranslator(semanticModel, jobStruct);
            var bodyCode = translator.Translate(methodSyntax.Body);
            sb.Append(bodyCode);
            sb.AppendLine("}");
        }

        private static string BuildIspcChunkParamList(
            List<(INamedTypeSymbol type, string name)> chunkArrays,
            IEnumerable<(ITypeSymbol type, string name)> fields)
        {
            var pars = new StringBuilder();
            foreach (var (type, name) in chunkArrays)
            {
                if (pars.Length > 0) pars.Append(", ");
                var ispcType = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(type));
                pars.Append($"uniform {ispcType} {name}_ptr[], uniform int {name}_length");
            }

            foreach (var (type, name) in fields)
            {
                if (pars.Length > 0) pars.Append(", ");
                if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                {
                    if (type.Name == "NativeList")
                    {
                        var elemType = ((INamedTypeSymbol)type).TypeArguments[0];
                        var ispcElem = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                        pars.Append($"uniform UnsafeList_Context_{ispcElem}* uniform {name}");
                    }
                    else
                    {
                        var elemType = ((INamedTypeSymbol)type).TypeArguments[0];
                        var ispcElem = ToIspcType(NativeTranspiler.MapCSharpTypeToCpp(elemType));
                        pars.Append($"uniform {ispcElem} {name}_ptr[], uniform int {name}_length");
                    }
                }
                else if (type is IPointerTypeSymbol ptrType)
                {
                    var baseCpp = NativeTranspiler.MapCSharpTypeToCpp(ptrType.PointedAtType);
                    pars.Append($"uniform {ToIspcType(baseCpp)} * uniform {name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(type);
                    pars.Append($"uniform {ToIspcType(cppType)} * uniform {name}_ptr");
                }
            }

            return pars.ToString();
        }

        private static List<(INamedTypeSymbol type, string name)> CollectChunkNativeArrayLocals(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var result = new List<(INamedTypeSymbol type, string name)>();
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
            var methodSyntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return result;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var localDecl in methodSyntax.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
            {
                foreach (var variable in localDecl.Declaration.Variables)
                {
                    if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation)
                        continue;
                    if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
                        continue;
                    if (methodSymbol.ContainingType?.ToDisplayString() != "EntJoy.ArchetypeChunk" ||
                        methodSymbol.Name != "GetComponentDataNativeArray" ||
                        methodSymbol.TypeArguments.Length == 0 ||
                        methodSymbol.TypeArguments[0] is not INamedTypeSymbol componentType)
                    {
                        continue;
                    }

                    result.Add((componentType, variable.Identifier.Text));
                }
            }

            return result;
        }

        private static void GenerateIspcFunction(StringBuilder sb, string functionName,
            INamedTypeSymbol jobStruct, SemanticModel semanticModel,
            MethodDeclarationSyntax methodSyntax, string indexParamName,
            List<IFieldSymbol>? constBoolFields, List<bool>? constBoolValues)
        {
            var fields = GetFieldsFromJob(jobStruct);
            var paramsList = BuildIspcParamList(fields, true);
            sb.AppendLine($"export void {functionName}({paramsList})");
            sb.AppendLine("{");

            var (_, usesReturnValue) = CheckAtomicOperations(jobStruct, semanticModel);
            bool selfModifying = HasSelfModifyingNativeArray(jobStruct, semanticModel);

            bool useUniformLoop = usesReturnValue || selfModifying;

            if (useUniformLoop)
            {
                // 需要标量执行时（原子操作返回值被使用，或 NativeArray 自修改），
                // 用 for (uniform int) 串行循环。
                // foreach 的 SIMD scatter 在 read-modify-write 场景下有 lane 交叉污染。

                // 在 uniform 循环上下文中，从指针解引用的字段也必须标记 uniform
                foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    if (field.Type is IPointerTypeSymbol) continue;
                    if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type)) continue;
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    var ispcType = ToIspcType(cppType);
                    sb.AppendLine($"{Indent}uniform {ispcType} {field.Name} = *{field.Name}_ptr;");
                }

                // 找到第一个 NativeArray 的 _length 作为边界保护
                string endBound = "__startIndex + __count";
                foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type) && field.Type.Name == "NativeArray")
                    {
                        endBound = $"min(__startIndex + __count, {field.Name}_length)";
                        break;
                    }
                }
                // ISPC 中 for (uniform int) 循环体仍被所有 GANG lanes 重复执行。
                // 需要让非 0 lane 提前返回来防止重复处理同一 index。
                sb.AppendLine($"{Indent}// 仅 lane 0 执行 uniform for 循环（防止重复处理同一 ID）");
                sb.AppendLine($"{Indent}if (programIndex != 0) return;");
                sb.AppendLine();
                sb.AppendLine($"{Indent}for (uniform int {indexParamName} = __startIndex; {indexParamName} < {endBound}; {indexParamName}++) {{");
                var translator = new IspcStatementTranslator(semanticModel, jobStruct, constBoolFields, constBoolValues, useUniformVars: true);
                var bodyCode = translator.Translate(methodSyntax.Body);
                sb.Append(bodyCode);
                sb.AppendLine($"{Indent}}}");
            }
            else
            {
                // 无自修改 + 无原子返回值使用时，使用 foreach 获得最佳 SIMD 加速
                // field dereference variables are varying (ISPC default)
                AppendUniformVariableDeclarations(sb, jobStruct);

                // 找到第一个 NativeArray 字段的 _length 名作为边界保护
                string lengthBound = "__startIndex + __count";
                foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type) && field.Type.Name == "NativeArray")
                    {
                        lengthBound = $"min(__startIndex + __count, {field.Name}_length)";
                        break;
                    }
                }
                sb.AppendLine($"{Indent}foreach ({indexParamName} = __startIndex ... {lengthBound}) {{");
                var translator = new IspcStatementTranslator(semanticModel, jobStruct, constBoolFields, constBoolValues);
                var bodyCode = translator.Translate(methodSyntax.Body);
                sb.Append(bodyCode);
                sb.AppendLine($"{Indent}}}");
            }
            sb.AppendLine("}");
        }

        /// <summary>生成 IJob 的 ISPC 函数（无循环、无 __startIndex/__count）</summary>
        private static void GenerateIspcIJobFunction(StringBuilder sb, string functionName,
            INamedTypeSymbol jobStruct, SemanticModel semanticModel,
            MethodDeclarationSyntax methodSyntax)
        {
            var fields = GetFieldsFromJob(jobStruct);
            // IJob: 无 __startIndex/__count，直接传入 field ptrs
            var paramsList = BuildIspcParamList(fields, false);
            sb.AppendLine($"export void {functionName}({paramsList})");
            sb.AppendLine("{");
            AppendUniformVariableDeclarations(sb, jobStruct);

            var translator = new IspcStatementTranslator(semanticModel, jobStruct, null, false);
            var bodyCode = translator.Translate(methodSyntax.Body);
            sb.Append(bodyCode);
            sb.AppendLine("}");
        }

        /// <summary>
        /// 检查 Job 是否使用了原子操作，并且是否**使用了返回值**。
        /// </summary>
        private static (bool hasAtomics, bool usesReturnValue) CheckAtomicOperations(
            INamedTypeSymbol jobStruct, SemanticModel semanticModel)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
            if (executeMethod == null) return (false, false);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return (false, false);

            bool hasAtomics = false;
            bool usesReturnValue = false;

            foreach (var inv in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var sym = semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (sym == null || sym.ContainingType?.ToDisplayString() != "System.Threading.Interlocked")
                    continue;

                hasAtomics = true;

                // 检查 Interlocked.Add/Increment/Decrement 的返回值是否被使用
                // 如果调用是表达式语句（直接分号结尾），则返回值被丢弃 → 盲递增
                // 如果调用在赋值、二元运算、return、或者其他表达式中 → 返回值被使用
                var parent = inv.Parent;
                bool isUsed = parent switch
                {
                    ExpressionStatementSyntax => false,          // x.Add(); → 返回值丢弃
                    EqualsValueClauseSyntax eq when eq.Parent is VariableDeclaratorSyntax => true, // int v = x.Add();
                    AssignmentExpressionSyntax => true,          // v = x.Add();
                    BinaryExpressionSyntax => true,              // x.Add() - 1
                    ReturnStatementSyntax => true,               // return x.Add();
                    ArgumentSyntax => true,                      // Foo(x.Add())
                    PrefixUnaryExpressionSyntax => true,         // -x.Add()
                    PostfixUnaryExpressionSyntax => true,        // x.Add()++
                    ConditionalExpressionSyntax => true,         // x.Add() ? a : b
                    _ => false
                };

                if (isUsed)
                    usesReturnValue = true;
            }

            return (hasAtomics, usesReturnValue);
        }

        private static bool HasAtomicOperations(INamedTypeSymbol jobStruct, SemanticModel semanticModel)
        {
            var (has, _) = CheckAtomicOperations(jobStruct, semanticModel);
            return has;
        }

        private static void GenerateIspcTaskFunction(StringBuilder sb, string functionName,
            INamedTypeSymbol jobStruct, SemanticModel semanticModel,
            MethodDeclarationSyntax methodSyntax, string indexParamName,
            string? constBoolField, bool constBoolValue)
        {
            var fields = GetFieldsFromJob(jobStruct);
            var paramsList = BuildIspcParamList(fields, true);
            sb.AppendLine($"task void {functionName}({paramsList})");
            sb.AppendLine("{");

            // task 函数内使用 for (uniform int) 循环，字段解引用必须标 uniform
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (field.Type is IPointerTypeSymbol) continue;
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type)) continue;
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                var ispcType = ToIspcType(cppType);
                sb.AppendLine($"{Indent}uniform {ispcType} {field.Name} = *{field.Name}_ptr;");
            }

            // 显式转换 __count/taskCount 避免 max/min 重载歧义
            sb.AppendLine($"{Indent}uniform int n_per_task = max(1, (int)(__count / taskCount));");
            sb.AppendLine($"{Indent}uniform int start = __startIndex + taskIndex * n_per_task;");
            sb.AppendLine($"{Indent}uniform int end = (taskIndex == taskCount - 1) ? (__startIndex + __count) : min(start + n_per_task, __startIndex + (int)__count);");
            // ISPC task 函数内 for (uniform int) 也被所有 GANG lanes 重复执行
            sb.AppendLine($"{Indent}// 仅 lane 0 执行 uniform for 循环（防止重复处理同一 ID）");
            sb.AppendLine($"{Indent}if (programIndex != 0) return;");
            sb.AppendLine();
            sb.AppendLine($"{Indent}for (uniform int {indexParamName} = start; {indexParamName} < end; {indexParamName}++) {{");

            var translator = new IspcStatementTranslator(semanticModel, jobStruct, constBoolField, constBoolValue, useUniformVars: true);
            string bodyCode = translator.Translate(methodSyntax.Body);

            using (var reader = new StringReader(bodyCode))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        sb.AppendLine();
                    else
                        sb.AppendLine(Indent + line);
                }
            }

            sb.AppendLine($"{Indent}}}");
            sb.AppendLine("}");
        }

        private static void GenerateIspcMTEntry(StringBuilder sb, string entryName, INamedTypeSymbol jobStruct, string taskName)
        {
            var fields = GetFieldsFromJob(jobStruct);
            var paramsList = BuildIspcParamList(fields, true);
            string mtParams = string.IsNullOrEmpty(paramsList)
                ? "uniform int numTasks"
                : $"{paramsList}, uniform int numTasks";
            sb.AppendLine($"export void {entryName}({mtParams})");
            sb.AppendLine("{");
            string launchArgs = "__startIndex, __count";
            string callArgs = BuildIspcCallArgs(fields, false);
            if (!string.IsNullOrEmpty(callArgs))
                launchArgs += ", " + callArgs;
            sb.AppendLine($"    launch[numTasks] {taskName}({launchArgs});");
            sb.AppendLine("    sync;");
            sb.AppendLine("}");
        }

        private static void AppendUniformVariableDeclarations(StringBuilder sb, INamedTypeSymbol jobStruct, bool forceUniform = false)
        {
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (field.Type is IPointerTypeSymbol) continue;
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type)) continue;
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                sb.AppendLine($"{Indent}{ToIspcType(cppType)} {field.Name} = *{field.Name}_ptr;");
            }
        }

        // ===================================================================
        //               内部类：静态方法 ISPC 翻译器
        // ===================================================================

        private class MethodIspcTranslator : IspcStatementTranslator
        {
            private readonly bool _skipOuterFor;
            private readonly bool _hasResult;

            public MethodIspcTranslator(SemanticModel semanticModel, IMethodSymbol method,
                bool skipOuterFor = false, int initialIndent = 0, bool needResult = false, bool useUniformVars = false)
                : base(semanticModel, method, null, false, useUniformVars)
            {
                _skipOuterFor = skipOuterFor;
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

            protected override void TranslateForStatement(ForStatementSyntax forStmt)
            {
                if (_skipOuterFor)
                {
                    if (forStmt.Statement is BlockSyntax block)
                    {
                        foreach (var stmt2 in block.Statements) TranslateStatement(stmt2);
                    }
                    else TranslateStatement(forStmt.Statement);
                    return;
                }

                // 检测标准 for (int i = 0; i < limit; i++) 模式，转换为 ISPC foreach
                if (forStmt.Declaration != null &&
                    forStmt.Declaration.Variables.Count == 1 &&
                    forStmt.Condition is BinaryExpressionSyntax binExpr &&
                    binExpr.OperatorToken.IsKind(SyntaxKind.LessThanToken) &&
                    forStmt.Incrementors.Count == 1 &&
                    forStmt.Incrementors[0] is PostfixUnaryExpressionSyntax postfix &&
                    postfix.OperatorToken.IsKind(SyntaxKind.PlusPlusToken))
                {
                    var varDecl = forStmt.Declaration.Variables[0];
                    string indexName = varDecl.Identifier.Text;

                    // 验证 increment 的变量名与声明一致
                    if (postfix.Operand is IdentifierNameSyntax incId &&
                        incId.Identifier.Text == indexName &&
                        varDecl.Initializer?.Value is LiteralExpressionSyntax initLit &&
                        initLit.Token.ValueText == "0")
                    {
                        // 验证条件左边也是同一个变量
                        if (binExpr.Left is IdentifierNameSyntax condId &&
                            condId.Identifier.Text == indexName)
                        {
                            // 成功匹配 for (int i = 0; i < limit; i++) 模式
                            AppendIndent();
                            _builder.Append("foreach (");
                            _builder.Append(indexName);
                            _builder.Append(" = 0 ... ");
                            TranslateExpression(binExpr.Right);
                            _builder.AppendLine(")");

                            if (forStmt.Statement is BlockSyntax block)
                                TranslateBlock(block, skipOuterBraces: false);
                            else
                            {
                                _indentLevel++;
                                AppendIndent();
                                TranslateStatement(forStmt.Statement);
                                _indentLevel--;
                            }
                            return;
                        }
                    }
                }

                // 不匹配标准模式，回退到普通 for 循环
                base.TranslateForStatement(forStmt);
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
