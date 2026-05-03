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
                sb.AppendLine($"    ispc::{baseName}_impl({ispcCallArgs});");
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
            !CppJobGenerator.IsForJob(jobStruct);

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
            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Execute");
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null)
            {
                sb.AppendLine("// Error: no Execute body found.");
                return sb.ToString();
            }

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            if (IsIJob(jobStruct))
            {
                // IJob: 无循环、无 __startIndex/__count，单次执行
                GenerateIspcIJobFunction(sb, baseName + "_impl", jobStruct, semanticModel, methodSyntax);
            }
            else
            {
                // IJobParallelFor / IJobFor: 批处理模式，使用 foreach 或 for (uniform int)
                var conditionalFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel);
                var boolField = conditionalFields.FirstOrDefault(f => f.Type.SpecialType == SpecialType.System_Boolean);
                string indexName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;

                if (boolField != null)
                {
                    GenerateIspcFunction(sb, baseName + "_true_impl", jobStruct, semanticModel, methodSyntax, indexName, boolField.Name, true);
                    sb.AppendLine();
                    GenerateIspcFunction(sb, baseName + "_false_impl", jobStruct, semanticModel, methodSyntax, indexName, boolField.Name, false);
                }
                else
                {
                    GenerateIspcFunction(sb, baseName + "_impl", jobStruct, semanticModel, methodSyntax, indexName, null, false);
                }
            }

            return sb.ToString();
        }

        /// <summary>生成 Job 的 ISPC 多任务源文件</summary>
        public static string GenerateIspcMTSource(INamedTypeSymbol jobStruct, Compilation compilation, HashSet<INamedTypeSymbol> userStructs)
        {
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
            var boolField = conditionalFields.FirstOrDefault(f => f.Type.SpecialType == SpecialType.System_Boolean);

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
                if (boolField != null)
                {
                    GenWrapper("_true", "_true_impl");
                    GenWrapper("_false", "_false_impl");
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

        /// <summary>生成 Job 的 C++ Wrapper（多线程）</summary>
        public static string GenerateCppWrapperMT(INamedTypeSymbol jobStruct, Compilation compilation)
        {
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
        /// 对 foreach 的 SIMD scatter 不安全，必须使用 uniform for 循环。
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

            // 收集所有被读取的 NativeArray 字段名
            var readArrays = new HashSet<string>();
            var writtenArrays = new HashSet<string>();

            // 遍历表达式树：
            // 元素访问 Position[index] 称为读
            // 赋值左边 Position[index] = ... 称为写
            foreach (var node in methodSyntax.Body.DescendantNodes())
            {
                // 检测元素访问：arr[i] → 读/写
                if (node is ElementAccessExpressionSyntax elemAccess)
                {
                    if (elemAccess.Expression is IdentifierNameSyntax id)
                    {
                        string name = id.Identifier.Text;
                        if (nativeArrayFields.Contains(name))
                        {
                            // 检查是否在赋值左边（如 Positions[index] = pos）
                            bool isLeftOfAssign = elemAccess.Parent is AssignmentExpressionSyntax assign &&
                                                  assign.Left == elemAccess;
                            if (isLeftOfAssign)
                                writtenArrays.Add(name);
                            else
                                readArrays.Add(name);   // 在赋值右边或任何其他位置 → 读
                        }
                    }
                }

                // 检测方法调用中的 ref 参数传递（如 UnsafeUtility.ArrayElementAsRef）
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
                            {
                                // ArrayElementAsRef 返回 ref，通常用于写入
                                writtenArrays.Add(name);
                                readArrays.Add(name);
                            }
                        }
                    }
                }
            }

            // 检查交集：如果任一阵列既读又写 → read-modify-write
            foreach (var name in nativeArrayFields)
            {
                if (readArrays.Contains(name) && writtenArrays.Contains(name))
                    return true;
            }
            return false;
        }

        private static void GenerateIspcFunction(StringBuilder sb, string functionName,
            INamedTypeSymbol jobStruct, SemanticModel semanticModel,
            MethodDeclarationSyntax methodSyntax, string indexParamName,
            string? constBoolField, bool constBoolValue)
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
                var translator = new IspcStatementTranslator(semanticModel, jobStruct, constBoolField, constBoolValue, useUniformVars: true);
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
                var translator = new IspcStatementTranslator(semanticModel, jobStruct, constBoolField, constBoolValue);
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
