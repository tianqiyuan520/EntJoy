// IspcGenerator.cs — 统一 ISPC 代码生成器
//   合并了原 IspcMethodGenerator.cs + IspcJobGenerator.cs 的功能，
//   共享 NativeList 上下文结构、类型转换、回调生成等逻辑。
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer.Common
{
    public static partial class IspcGenerator
    {

        //                       公共 API：静态方法
        // (isoc 方法级入口已拆分至 IspcGenerator.Method.cs)
        //                       公共 API：Job 结构体

        /// <summary>判断是否为纯 IJob（非 ParallelFor/For）</summary>
        public static bool IsIJob(INamedTypeSymbol jobStruct) =>
            jobStruct.AllInterfaces.Any(i => SymbolHelper.IsEntJoyJobInterface(i, Config.IJob)) &&
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
            var includes = CollectMethodIncludes(jobStruct.GetMembers().OfType<IMethodSymbol>().First(method => method.Name == Config.Execute), compilation, fields);
            if (CppJobGenerator.IsChunkJob(jobStruct))
            {
                foreach (var component in CppJobGenerator.CollectChunkNativeArrayTypes(jobStruct, compilation))
                    includes.Add(NativeTranspiler.GetStructHeaderFileName(component));
            }
            // SendEvent: 事件类型 include（ISPC 侧 SendEvent 生成的代码引用事件类型结构）
            foreach (var evtType in CppJobGenerator.CollectSendEventTypes(jobStruct, compilation))
                includes.Add(NativeTranspiler.GetStructHeaderFileName(evtType));
            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null)
            {
                sb.AppendLine("// Error: no Execute body found.");
                return sb.ToString();
            }

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            if (CppJobGenerator.IsEntityJob(jobStruct) || CppJobGenerator.IsChunkJob(jobStruct))
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
                    foreach (var (mask, values) in BoolVariants.Enumerate(boolFields.Count))
                    {

                        string suffix = BoolVariants.Suffix(values) + "_impl";
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

            if (CppJobGenerator.IsChunkJob(jobStruct))
                return GenerateIspcChunkMTSource(jobStruct, compilation);

            if (!CppJobGenerator.IsParallelForJob(jobStruct) && !CppJobGenerator.IsForJob(jobStruct))
                return "// ISPC MT only supports IJobParallelFor / IJobFor.";

            var sb = new StringBuilder();
            var baseName = GetIspcBaseName(jobStruct);
            sb.AppendLine($"// Auto-generated ISPC MT source for {jobStruct.Name}");

            var fields = GetFieldsFromJob(jobStruct);
            var includes = CollectMethodIncludes(jobStruct.GetMembers().OfType<IMethodSymbol>().First(method => method.Name == Config.Execute), compilation, fields);
            WriteIspcPreamble(sb, fields, includes.OrderBy(x => x).ToList());

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return "// Error: no Execute body";

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var boolFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel)
                .Where(field => field.Type.SpecialType == SpecialType.System_Boolean).ToList();
            string indexName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;
            foreach (var (_, values) in BoolVariants.Enumerate(boolFields.Count))
            {
                string suffix = BoolVariants.Suffix(values);
                GenerateIspcTaskFunction(sb, baseName + suffix + "_task", jobStruct, semanticModel,
                    methodSyntax, indexName, boolFields, values);
                sb.AppendLine();
                GenerateIspcMTEntry(sb, baseName + suffix + "_mt_impl", jobStruct, baseName + suffix + "_task");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>生成 Job 的 C++ Wrapper（单线程）</summary>
        public static string GenerateCppWrapper(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            if (CppJobGenerator.IsEntityJob(jobStruct))
            {
                // IJobEntity + ISPC: 使用 chunk 级 wrapper（绕过 EntityBatch 开销）
                var attrSym = AttributeHelper.GetAttributeSymbol(compilation);
                bool isIspc = attrSym != null && AttributeHelper.GetBackendTarget(jobStruct, attrSym) == NativeTranspiler.BackendTarget.Ispc;
                if (isIspc)
                    return GenerateCppChunkWrapper(jobStruct, compilation);
                return GenerateCppEntityBatchWrapper(jobStruct, compilation);
            }

            if (CppJobGenerator.IsChunkJob(jobStruct))
                return GenerateCppChunkWrapper(jobStruct, compilation);

            var sb = new StringBuilder();
            var ispcBase = GetIspcBaseName(jobStruct);
            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            // 形参列表直接写 CPUBattle::OrcaLine* 这类限定名 → 元素类型头文件必须可见
            foreach (var include in CppJobGenerator.CollectJobStructIncludes(jobStruct, compilation))
                sb.AppendLine($"#include \"{include}.h\"");
            sb.AppendLine($"#include \"{ispcBase}_ispc.h\"");
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();

            var fields = GetFieldsFromJob(jobStruct);
            GenerateResizeCallbacks(sb, fields);

            bool hasBatch = CppJobGenerator.IsParallelForJob(jobStruct) || CppJobGenerator.IsForJob(jobStruct);
            string cppParamList = hasBatch
                ? "void* __job, int __startIndex, int __count"
                : BuildCppWrapperParamList(fields, hasBatch);

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var conditionalFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel);
            var boolFields = conditionalFields.Where(f => f.Type.SpecialType == SpecialType.System_Boolean).ToList();

            void GenWrapper(string suffix, string ispcImplSuffix)
            {
                string funcName = ispcBase + suffix;
                string implName = ispcBase + ispcImplSuffix;
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {funcName}({cppParamList})");
                sb.AppendLine("{");
                if (hasBatch) CppJobGenerator.AppendContextFieldDeclarations(jobStruct, sb);
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
                    foreach (var (_, values) in BoolVariants.Enumerate(boolFields.Count))
                    {

                        string suffix = BoolVariants.Suffix(values);
                        string ispcSuffix = BoolVariants.Suffix(values) + "_impl";
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

        /// <summary>生成 Job 的 C++ Wrapper（多线程）</summary>
        public static string GenerateCppWrapperMT(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            if (CppJobGenerator.IsEntityJob(jobStruct))
            {
                var attrSym = AttributeHelper.GetAttributeSymbol(compilation);
                bool isIspc = attrSym != null && AttributeHelper.GetBackendTarget(jobStruct, attrSym) == NativeTranspiler.BackendTarget.Ispc;
                if (isIspc)
                    return GenerateCppChunkWrapper(jobStruct, compilation, useMt: true);
                return GenerateCppEntityBatchWrapper(jobStruct, compilation, useMt: true);
            }

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
            string cppParamList = "void* __job, int __startIndex, int __count";

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var boolFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel)
                .Where(field => field.Type.SpecialType == SpecialType.System_Boolean).ToList();

            void GenWrapperMT(string suffix, string ispcImplSuffix)
            {
                string funcName = ispcBase + suffix;
                string implName = ispcBase + ispcImplSuffix;
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {funcName}({cppParamList})");
                sb.AppendLine("{");
                CppJobGenerator.AppendContextFieldDeclarations(jobStruct, sb);
                sb.Append(GenerateContextFillCode(fields, isFill: true));
                sb.AppendLine("    int numTasks = std::thread::hardware_concurrency();");
                sb.AppendLine($"    ispc::{implName}({BuildIspcCallArgsForWrapper(fields, true)}, numTasks);");
                sb.Append(GenerateContextFillCode(fields, isFill: false));
                sb.AppendLine("}");
                sb.AppendLine();
            }

            foreach (var (_, values) in BoolVariants.Enumerate(boolFields.Count))
            {
                string suffix = BoolVariants.Suffix(values);
                GenWrapperMT(suffix + "_mt", suffix + "_mt_impl");
            }

            return sb.ToString();
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
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            if (executeMethod == null) return false;
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return false;

            // 收集所有 NativeArray 字段名
            var nativeArrayFields = new HashSet<string>();
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeArray))
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
                    if (sym != null && sym.Name == Config.ArrayElementAsRef &&
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

            bool useUniformLoop = usesReturnValue || selfModifying || HasMutableValueFields(jobStruct, semanticModel, methodSyntax);

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
                    var ispcType = IspcTypeMapper.FromCpp(cppType);
                    sb.AppendLine($"{Indent}uniform {ispcType} {field.Name} = *{field.Name}_ptr;");
                }

                string endBound = "__startIndex + __count";
                // ISPC 中 for (uniform int) 循环体仍被所有 GANG lanes 重复执行。
                // 需要让非 0 lane 提前返回来防止重复处理同一 index。
                sb.AppendLine($"{Indent}// 仅 lane 0 执行 uniform for 循环（防止重复处理同一 ID）");
                sb.AppendLine($"{Indent}if (programIndex != 0) return;");
                sb.AppendLine();
                sb.AppendLine($"{Indent}for (uniform int {indexParamName} = __startIndex; {indexParamName} < {endBound}; {indexParamName}++) {{");
                var translator = new IspcStatementTranslator(semanticModel, jobStruct, constBoolFields, constBoolValues, useUniformVars: true);
                // 批索引循环上下文：体内 index 层级的 `return;` 要降级成 `continue;`（否则退出整个导出函数）
                translator.SetInsideBatchIndexLoop(true);
                var bodyCode = translator.Translate(methodSyntax.Body);
                AppendIspcBody(sb, bodyCode, 2);
                sb.AppendLine($"{Indent}}}");
            }
            else
            {
                // Execute 的索引由调度区间确定；内层循环保留逐索引的执行顺序。
                AppendUniformVariableDeclarations(sb, jobStruct);
                sb.AppendLine($"{Indent}foreach ({indexParamName} = __startIndex ... __startIndex + __count) {{");
                var translator = new IspcStatementTranslator(semanticModel, jobStruct, constBoolFields, constBoolValues);
                translator.SetInsideForeach(true);
                translator.SetInsideBatchIndexLoop(true);
                var bodyCode = translator.Translate(methodSyntax.Body);
                AppendIspcBody(sb, bodyCode, 2);
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
            sb.AppendLine($"{Indent}if (programIndex != 0) return;");
            AppendUniformVariableDeclarations(sb, jobStruct);

            var translator = new IspcStatementTranslator(semanticModel, jobStruct, null, false, useUniformVars: true);
            var bodyCode = translator.Translate(methodSyntax.Body);
            AppendIspcBody(sb, bodyCode);
            sb.AppendLine("}");
        }

        /// <summary>
        /// 检查 Job 是否使用了原子操作，并且是否使用了返回值。
        /// </summary>
        private static bool HasMutableValueFields(INamedTypeSymbol job, SemanticModel model, MethodDeclarationSyntax method)
        {
            bool IsValueField(ExpressionSyntax expression)
            {
                while (expression is ParenthesizedExpressionSyntax parentheses) expression = parentheses.Expression;
                if (expression is MemberAccessExpressionSyntax member)
                {
                    if (member.IsKind(SyntaxKind.PointerMemberAccessExpression)) return false;
                    if (member.Expression is not ThisExpressionSyntax) return IsValueField(member.Expression);
                }
                return model.GetSymbolInfo(expression).Symbol is IFieldSymbol field && !field.IsStatic
                    && SymbolEqualityComparer.Default.Equals(field.ContainingType, job)
                    && field.Type is not IPointerTypeSymbol && !NativeTranspiler.IsEntJoyNativeContainerType(field.Type);
            }
            return method.Body != null && method.Body.DescendantNodes().Any(node => node switch
            {
                AssignmentExpressionSyntax assignment => IsValueField(assignment.Left),
                PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression)
                    || prefix.IsKind(SyntaxKind.PreDecrementExpression) || prefix.IsKind(SyntaxKind.AddressOfExpression) => IsValueField(prefix.Operand),
                PostfixUnaryExpressionSyntax postfix => IsValueField(postfix.Operand),
                ArgumentSyntax argument when argument.RefOrOutKeyword.RawKind != 0 => IsValueField(argument.Expression),
                _ => false
            });
        }

        private static (bool hasAtomics, bool usesReturnValue) CheckAtomicOperations(
            INamedTypeSymbol jobStruct, SemanticModel semanticModel)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
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

        private static void GenerateIspcTaskFunction(StringBuilder sb, string functionName,
            INamedTypeSymbol jobStruct, SemanticModel semanticModel,
            MethodDeclarationSyntax methodSyntax, string indexParamName,
            List<IFieldSymbol> constBoolFields, List<bool> constBoolValues)
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
                var ispcType = IspcTypeMapper.FromCpp(cppType);
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

            var translator = new IspcStatementTranslator(semanticModel, jobStruct, constBoolFields, constBoolValues, useUniformVars: true);
            // 同上：task 函数里的批索引循环，体内 index 层级 `return;` 降级为 `continue;`
            translator.SetInsideBatchIndexLoop(true);
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
            sb.AppendLine($"    launch[max(1, numTasks)] {taskName}({launchArgs});");
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
                // 标量字段（float/int/bool 等）和向量类型（float2/int2/uint2）声明为 uniform，
                // 避免 ISPC 每 lane 一份冗余拷贝和 read-broadcast 开销。
                bool isScalarOrVec = field.Type.SpecialType != SpecialType.None ||
                    field.Type.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyMathematics;
                string uniform = isScalarOrVec || forceUniform ? "uniform " : "";
                sb.AppendLine($"{Indent}{uniform}{IspcTypeMapper.FromCpp(cppType)} {field.Name} = *{field.Name}_ptr;");
            }
        }

    }
}
