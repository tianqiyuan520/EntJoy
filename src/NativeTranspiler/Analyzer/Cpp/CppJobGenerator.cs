using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NativeTranspiler.Analyzer.Common;
using System;

namespace NativeTranspiler.Analyzer
{
    public static partial class CppJobGenerator
    {
        /// <summary>
        /// 获取所有 bool 条件字段列表
        /// 与 CppGenerator.GenerateImplementation 对静态方法的处理对齐。
        /// </summary>
        private static List<IFieldSymbol> GetBoolConditionalFields(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            if (executeMethod == null) return new List<IFieldSymbol>();
            
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax == null) return new List<IFieldSymbol>();
            
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var conditionalFields = NativeTranspileValidator.GetConditionalReadOnlyFields(jobStruct, semanticModel);
            return conditionalFields.Where(f => f.Type.SpecialType == SpecialType.System_Boolean).ToList();
        }

        /// <summary>
        /// 生成所有 bool 条件字段组合的变体函数声明
        /// </summary>
        private static void GenerateBoolVariantDeclarations(INamedTypeSymbol jobStruct, List<IFieldSymbol> boolFields, string baseFuncName, string batchParams, StringBuilder sb)
        {
            if (boolFields.Count == 0)
            {
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {baseFuncName}({batchParams});");
                return;
            }

            int totalVariants = 1 << boolFields.Count; // 2^n
            for (int mask = 0; mask < totalVariants; mask++)
            {
                var values = new List<bool>();
                for (int i = 0; i < boolFields.Count; i++)
                    values.Add((mask & (1 << i)) != 0);
                
                string suffix = BuildBoolVariantSuffix(boolFields, values);
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {baseFuncName}{suffix}({batchParams});");
            }
        }

        public static string GenerateJobHeader(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine();
            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            if (IsChunkScheduledJob(jobStruct))
            {
                sb.AppendLine("#include \"ChunkJobData.h\"");
                sb.AppendLine("#include \"ChunkNativeArray.h\"");
            }
            foreach (var include in CollectJobStructIncludes(jobStruct, compilation))
                sb.AppendLine($"#include \"{include}.h\"");
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();
            sb.AppendLine(CodeTemplates.GenerateAtomicMacros());
            // SendEvent: __EntJoyChunkContextHeader 前向声明（指针参数）
            sb.AppendLine("#ifndef __EntJoyChunkContextHeader_FWD");
            sb.AppendLine("#define __EntJoyChunkContextHeader_FWD");
            sb.AppendLine("struct __EntJoyChunkContextHeader;");
            sb.AppendLine("#endif");
            sb.AppendLine();

            // IJobEntity: 无独立 Execute 函数，循环体内联到 Adapter 中
            if (IsChunkJob(jobStruct))
            {
                bool usesSendEvent = JobUsesSendEvent(jobStruct, compilation);
                var chunkParams = BuildChunkJobParameters(jobStruct, includeHeader: usesSendEvent);
                var singleFuncName = GetCppJobFunctionName(jobStruct);
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams});");
            }
            else if (IsEntityJob(jobStruct))
            {
                // IJobEntity：始终生成独立的 Chunk 级 Execute 函数声明（不含 __requiredComponentTypeIds）
                var chunkParams = BuildChunkJobParameters(jobStruct, includeTypeIds: false);
                var singleFuncName = GetCppJobFunctionName(jobStruct);
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams});");
            }
            else if (IsRangeScheduledJob(jobStruct))
            {
                var batchParams = BuildBatchJobParameters(jobStruct);
                var baseFuncName = GetCppJobFunctionName(jobStruct, isBatch: true);
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);
                GenerateBoolVariantDeclarations(jobStruct, boolFields, baseFuncName, batchParams, sb);
            }
            else
            {
                var singleParams = BuildJobParameters(jobStruct);
                var singleFuncName = GetCppJobFunctionName(jobStruct);
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({singleParams});");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 收集 Job Execute 直接/间接调用的同程序集静态方法（用于生成 #include）。
        /// </summary>
        private static HashSet<IMethodSymbol> CollectAllCalledStaticMethods(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var result = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == Config.Execute);
            if (executeMethod == null) return result;

            var queue = new Queue<IMethodSymbol>();
            foreach (var m in CppGenerator.CollectCalledStaticMethods(executeMethod, compilation))
                queue.Enqueue(m);
            while (queue.Count > 0)
            {
                var m = queue.Dequeue();
                if (!result.Add(m)) continue;
                foreach (var d in CppGenerator.CollectCalledStaticMethods(m, compilation))
                    queue.Enqueue(d);
            }
            return result;
        }

        public static string GenerateJobImplementation(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            var baseFuncName = GetCppJobFunctionName(jobStruct);
            var attrSymbol = compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute");
            bool useFastMath = AttributeHelper.HasFastCppMathLib(jobStruct, attrSymbol);
            var autoSIMD = AttributeHelper.GetAutoSIMD(jobStruct, attrSymbol);
            var simdMathPrecision = AttributeHelper.GetMathPrecision(jobStruct, attrSymbol);
            sb.AppendLine($"#include \"{baseFuncName}.h\"");
            // 依赖的静态方法头文件（Execute 内调用的同程序集静态方法，含传递依赖）
            // 缺失会导致调用点引用未声明的函数（use of undeclared identifier）。
            foreach (var dep in CollectAllCalledStaticMethods(jobStruct, compilation))
                sb.AppendLine($"#include \"{CppGenerator.GetCppFunctionName(dep)}.h\"");
            sb.AppendLine("#include <algorithm>");
            sb.AppendLine("#include <cmath>");
            sb.AppendLine("#include <cstdio>");
            // 逐 job 精度覆盖（C1）
            // CMake 的 NATIVE_SIMD_MATH_PRECISION 是全库一刀切；NativeSIMD_math.h 用
            //   3 = IEEE （逐通道标量，与 C# 逐位一致）
            sb.AppendLine($"#define SIMD_MATH_PRECISION {(int)simdMathPrecision + 1}");
            sb.AppendLine("#include \"NativeSIMD.h\"");
            sb.AppendLine("#include \"SimdValue.h\"");

            // Event Buffer POD struct（SendEvent 生成的代码依赖）
            sb.AppendLine("#ifndef __EntJoyEventBuffer_DEFINED");
            sb.AppendLine("#define __EntJoyEventBuffer_DEFINED");
            sb.AppendLine("struct __EntJoyEventBuffer {");
            sb.AppendLine("    void* data;");
            sb.AppendLine("    int* count;");
            sb.AppendLine("    int capacity;");
            sb.AppendLine("    int elementSize;");
            sb.AppendLine("};");
            sb.AppendLine("#endif");
            sb.AppendLine();

            // ChunkContextHeader 完整定义（SendEvent 需要解引用 __header->eventBufferHeaders）
            if (JobUsesSendEvent(jobStruct, compilation))
            {
                sb.AppendLine("#ifndef __EntJoyChunkContextHeader_DEFINED");
                sb.AppendLine("#define __EntJoyChunkContextHeader_DEFINED");
                sb.AppendLine("struct __EntJoyChunkContextHeader");
                sb.AppendLine("{");
                sb.AppendLine("    int chunkCount;");
                sb.AppendLine("    int hasEnabledFilter;");
                sb.AppendLine("    void* queryAllEnabledTypes;");
                sb.AppendLine("    int allEnabledCount;");
                sb.AppendLine("    int gcHandleStartIndex;");
                sb.AppendLine("    void* chunksPtr;");
                sb.AppendLine("    int cleanupInProgress;");
                sb.AppendLine("    int ownsChunkData;");
                sb.AppendLine("    void* requiredComponentTypeIds;");
                sb.AppendLine("    int requiredComponentTypeIdCount;");
                sb.AppendLine("    int jobIsBoxed;");
                sb.AppendLine("    void* chunkArrayHandle;");
                sb.AppendLine("    // Event Buffer");
                sb.AppendLine("    int eventBufferCount;");
                sb.AppendLine("    void* eventBufferHeaders;");
                sb.AppendLine("    void* eventWorldHandle;");
                sb.AppendLine("};");
                sb.AppendLine("#endif");
                sb.AppendLine();
            }

            // IJobChunk: 生成独立 Execute 函数
            if (IsChunkJob(jobStruct))
            {
                if (autoSIMD == NativeTranspiler.AutoSIMD.Vectorize)
                    GenerateChunkFunctionVectorize(jobStruct, compilation, sb, useFastMath);
                else if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                    GenerateChunkFunctionSIMD(jobStruct, compilation, sb, useFastMath, simdMathPrecision);
                else
                    GenerateChunkFunctionStandard(jobStruct, compilation, sb, useFastMath);
            }
            // IJobEntity：生成独立的 Chunk 级 Execute 函数（与 IJobChunk 一致）
            else if (IsEntityJob(jobStruct))
            {
                if (autoSIMD == NativeTranspiler.AutoSIMD.Vectorize)
                    GenerateEntityFunctionVectorize(jobStruct, compilation, sb, useFastMath);
                else if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                    GenerateEntityFunctionStandard(jobStruct, compilation, sb, useFastMath);
                else
                    GenerateEntityChunkFunctionStandard(jobStruct, compilation, sb, useFastMath);
            }
            else if (IsParallelForJob(jobStruct) || IsForJob(jobStruct) || IsParallelForBatchJob(jobStruct))
            {
                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
                var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
                if (methodSyntax == null)
                {
                    sb.AppendLine("// Error: Could not find method syntax");
                    return sb.ToString();
                }

                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);
                // IJobParallelForBatch：Execute(startIndex, count) 自身就是一段区间，C++ 侧不再包 index 循环。
                bool isRangeJob = IsParallelForBatchJob(jobStruct);

                if (boolFields.Count > 0)
                {
                    // 生成所有 2^n 个 bool 组合变体
                    int totalVariants = 1 << boolFields.Count;
                    for (int mask = 0; mask < totalVariants; mask++)
                    {
                        var values = new List<bool>();
                        for (int i = 0; i < boolFields.Count; i++)
                            values.Add((mask & (1 << i)) != 0);
                        GenerateBatchFunctionVariant(jobStruct, boolFields, values, semanticModel, methodSyntax, sb, useFastMath, autoSIMD, simdMathPrecision, isRangeJob);
                    }
                }
                else
                {
                    GenerateBatchFunctionStandard(jobStruct, semanticModel, methodSyntax, sb, useFastMath, autoSIMD, simdMathPrecision, isRangeJob);
                }
            }
            else
            {
                GenerateSingleFunctionStandard(jobStruct, compilation, sb, useFastMath, autoSIMD, simdMathPrecision);
            }

            return sb.ToString();
        }

        // 局部变量声明：仅保留 NativeList 引用，移除 NativeArray 包装。
        // 标量字段的绑定形式见下方 typeOk 判据；字段用途按符号解析（无语义模型时退回名字兜底）。
        private static void AppendLocalVariableDeclarations(INamedTypeSymbol jobStruct, StringBuilder sb,
            SemanticModel? semanticModel = null, Compilation? compilation = null)
        {
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                    {
                        var elementType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        sb.AppendLine($"    EntJoy::Collections::UnsafeList<{cppElementType}>& {field.Name} = *{field.Name}_listData;");
                    }
                    // NativeArray: nothing
                }
            }

            var loopUse = GetFieldLoopUse(jobStruct, semanticModel ?? TryGetSemanticModel(jobStruct, compilation));
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type)) continue;
                if (field.Type is IPointerTypeSymbol) continue;
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                // 绑定形式：参与循环行程数的字段按值，其余按引用。按值后行程数成为入口常量，
                // 不再是"每轮重载 + 无法 unroll/向量化"；其余字段的载入本可折进操作数，按值只多一次拷贝。
                // 类型判据（ValueBindTypeOk）：只认已证 ≤16 B 的平凡可拷贝值类型；引用类型/容器/指针按引用。
                string scalarSrc = $"{field.Name}_ptr";
                bool tripCount = loopUse.TripCount.Contains(field.Name);
                bool typeOk = ValueBindTypeOk(field.Type);
                if (tripCount && typeOk)
                    sb.AppendLine($"    const {cppType} {field.Name} = *{scalarSrc};");
                else
                    sb.AppendLine($"    const {cppType}& {field.Name} = *{scalarSrc};");
            }
        }

        /// <summary>取 job 的 `Execute` 所在语法树的语义模型（拿不到就返回 null，退回名字兜底）。</summary>
        private static SemanticModel? TryGetSemanticModel(INamedTypeSymbol jobStruct, Compilation? compilation)
        {
            if (compilation == null) return null;
            try
            {
                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>()
                    .FirstOrDefault(m => m.Name == Config.Execute);
                var syntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
                return syntax == null ? null : compilation.GetSemanticModel(syntax.SyntaxTree);
            }
            catch { return null; }
        }

        // 标量绑定：`const T& X = *X_ptr;` 的对象可经同函数其它指针被写（`X_ptr` 是非 const 的 `T*`）
        // ⇒ 依赖它的循环行程数在编译期不可知（每轮重载 + 无法 unroll/向量化）。按值绑定后 `X` 是入口
        // 取一次的局部常量 ⇒ 行程数成为编译期常量。绑定形式见 AppendLocalVariableDeclarations 的 typeOk 判据。

        /// <summary>某个 job 的字段在循环里的用处分组（按符号解析，不再按名字猜）。</summary>
        private sealed class FieldLoopUse
        {
            /// <summary>参与循环行程数的字段名：出现在 `for` 初值/条件/步进、或 `while`/`do` 条件里。</summary>
            public readonly HashSet<string> TripCount = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>出现在任意循环内（头部或体内）的字段名（判据：这类形参加 `__restrict`）。</summary>
            public readonly HashSet<string> InLoop = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// 收集字段的循环用途（行程数 / 循环内）：按符号解析，标识符必须解析到本 job 的实例字段 ⇒ 同名
        /// 局部/形参不造成误判；无语义模型时退回名字兜底（同名声明一律保守跳过）。行程数那批按值绑定
        /// （见 AppendLocalVariableDeclarations），循环内那批改用形参 `__restrict`（见 `ScalarRestrictEnabled`）。
        /// </summary>
        private static FieldLoopUse GetFieldLoopUse(INamedTypeSymbol jobStruct, SemanticModel? semanticModel)
        {
            var use = new FieldLoopUse();
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == Config.Execute);
            var body = executeMethod == null ? null : SymbolHelper.GetMethodBody(executeMethod);
            if (body == null) return use;

            var fields = new HashSet<string>(
                jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Select(f => f.Name),
                StringComparer.Ordinal);
            if (fields.Count == 0) return use;

            HashSet<string>? shadowed = null;
            if (semanticModel == null)
            {
                shadowed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in executeMethod!.Parameters) shadowed.Add(p.Name);
                foreach (var v in body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                    shadowed.Add(v.Identifier.Text);
            }

            void Take(SyntaxNode? node, HashSet<string> sink)
            {
                if (node == null) return;
                foreach (var id in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    var name = id.Identifier.Text;
                    if (!fields.Contains(name)) continue;
                    if (semanticModel != null)
                    {
                        var sym = semanticModel.GetSymbolInfo(id).Symbol;
                        if (sym is IFieldSymbol f && !f.IsStatic
                            && SymbolEqualityComparer.Default.Equals(f.ContainingType, jobStruct))
                            sink.Add(name);
                    }
                    else if (shadowed != null && !shadowed.Contains(name))
                    {
                        sink.Add(name);
                    }
                }
            }

            // 循环体内的条件（if / 三元）里出现的字段不再单列一桶：唯一消费者（按值绑定的
            // `=4` 臂）已删除。

            bool sawLoop = false;
            foreach (var node in body.DescendantNodes())
            {
                switch (node)
                {
                    case ForStatementSyntax f:
                        sawLoop = true;
                        if (f.Declaration != null)
                            foreach (var v in f.Declaration.Variables) Take(v.Initializer?.Value, use.TripCount);
                        foreach (var e in f.Initializers) Take(e, use.TripCount);
                        Take(f.Condition, use.TripCount);
                        foreach (var e in f.Incrementors) Take(e, use.TripCount);
                        Take(f.Statement, use.InLoop);
                        break;
                    case WhileStatementSyntax w:
                        sawLoop = true;
                        Take(w.Condition, use.TripCount);
                        Take(w.Statement, use.InLoop);
                        break;
                    case DoStatementSyntax d:
                        sawLoop = true;
                        Take(d.Condition, use.TripCount);
                        Take(d.Statement, use.InLoop);
                        break;
                    case ForEachStatementSyntax fe:
                        sawLoop = true;
                        // foreach 的行程数由集合给（容器长度）；集合与元素都算"循环内"。
                        // ⚠ C++ 路径目前不支持 foreach 转译（会走 NT0xx 拒绝），此分支只为向前兼容。
                        Take(fe.Expression, use.InLoop);
                        Take(fe.Statement, use.InLoop);
                        break;
                }
            }
            // 判据必须考虑"transpiler 自己会合成循环"这件事。
            //   `IJobParallelFor`/`IJob` 的 C# 是逐元素形态（`Execute(int index)`），源码里没有循环；
            //   合成循环的是 transpiler（`_Execute_Batch` 里包一层 `for (index = __startIndex; ...)`）。
            //   于是此前在源码里找循环的判据（TripCount）对这类 job 永远为空 ⇒ 按值绑定与
            //   `SCALAR_RESTRICT` 这两条优化对宿主最重要的 job 类型完全失效（Count / Integrate /
            //   Place / Melee 全是 IJobParallelFor）。实测证据：生成的 `CountCellsJob_Execute.cpp` 里
            //   六个标量全部是 `const T& X = *X_ptr;`，尽管 `Length/CellsW/StateDeath` 每元素都要读。
            //   修法：源码里没有循环时，整个 body 就是循环体 ⇒ 全部字段算"循环内"。
            if (!sawLoop)
            {
                Take(body, use.InLoop);
            }
            use.InLoop.UnionWith(use.TripCount);
            return use;
        }



        /// <summary>该字段类型是否属于"已证 ≤ 16 B 的托管值类型"（可按值绑定）。</summary>
        private static bool ValueBindTypeOk(ITypeSymbol t)
        {
            switch (t.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Char:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                    return true;
            }
            if (t is INamedTypeSymbol nt
                && nt.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyMathematics
                && (nt.Name == Config.Float2 || nt.Name == Config.Int2 || nt.Name == Config.UInt2))
                return true;
            return false;
        }

        /// <summary>纯值字段形参是否加 `__restrict`：窄档 —— 只给出现在循环内的字段加。
        /// 机理：只给指向 job 结构体成员 的形参加，别名判定成立（与任何数组数据不可能同址），
        /// 且不像按值绑定那样引入入口拷贝/栈溢出代价。数组/分量指针一律不加。
        ///
        /// clang-cl /O2 微实验确认机制本身有效：`int* __restrict len_ptr` + `for (i &lt; len)`
        /// 会 hoist 行程数并完全向量化，而 `const int& len = *len_ptr` 每轮 `movslq`；
        /// 加在局部引用上无效（与不加逐字节相同）⇒ 必须加在形参上。
        /// 语义中立：restrict 只排除"经其它指针访问"，不排除"经同一指针写"——
        /// 内联或不可见调用只要通过同一指针写长度，行程数仍逐轮重载。
        ///
        /// 为什么是"窄"：把所有纯值字段都加（历史全字段臂）实测退化 ⇒ 只动真正挡住向量化的那批。</summary>
        private static bool ScalarRestrictEnabled(string fieldName, FieldLoopUse loopUse)
        {
            return loopUse.InLoop.Contains(fieldName);
        }

        /// <summary>
        /// 守卫折叠的 A/B 开关：构建期读 `ENTJOY_GUARD_FOLD`（`=0` 关闭 ⇒ 逐位回到未折叠行为）。
        /// 动机：折叠是生成期变换，A/B 必须"同一源码、两次构建"，否则差异里会混进别的提交。
        /// </summary>
        private static readonly bool GuardFoldEnabled =
            System.Environment.GetEnvironmentVariable("ENTJOY_GUARD_FOLD") != "0";

        /// <summary>
        /// G（守卫折叠，；）：
        /// 把"每元素一次"的 `index &lt; Length` 合取项折进合成循环的上界。
        ///
        /// 这里只做检测（AST + 语义）；剥离在生成文本上做（见 <see cref="StripFirstGuardConjunct"/>）——
        /// ⚠ 不能用 `ReplaceNode` 重写后再翻译：重写树是游离的，Roslyn 的 `CheckSyntaxNode` 会沿父链
        /// 回溯到根、发现不是本树 ⇒ `ArgumentException: 语法节点不在语法树中`（本实现第一版就这么炸的）。
        ///
        /// 识别形状（按语法形状，不按 job 名）：Execute 体内存在一个无 else 的
        /// `if (index &lt; &lt;job 的 int 字段&gt; &amp;&amp; …) { … }`，第一个合取项左侧是 index 形参、
        /// 右侧解析为本 job 的非静态 int 字段（宿主里的 `Length` 这类）。命中 ⇒ 输出字段名，返回 true。
        /// 未命中（形状不符 / 字段不是 int / 没有这样的 if）⇒ 返回 false，调用方逐位不变。
        /// 只认第一个命中的 if（宿主全文只命中 Count/Place 两处）。
        /// </summary>
        private static bool TryDetectIndexLengthGuard(
            MethodDeclarationSyntax methodSyntax, string indexParamName, SemanticModel semanticModel,
            out string foldedField)
        {
            foldedField = null;
            var body = methodSyntax?.Body;
            if (body == null || semanticModel == null) return false;

            foreach (var ifStmt in body.DescendantNodes().OfType<IfStatementSyntax>())
            {
                if (ifStmt.Else != null) continue;            // 设计约束：无 else
                if (ifStmt.Condition == null) continue;

                // 沿 `&&` 左脊收集合取项（Roslyn 把 a && b && c 解析成 ((a && b) && c)）。
                var conj = new System.Collections.Generic.List<ExpressionSyntax>();
                var cur = ifStmt.Condition;
                while (cur is BinaryExpressionSyntax be && be.IsKind(SyntaxKind.LogicalAndExpression))
                {
                    conj.Insert(0, be.Right);
                    cur = be.Left;
                }
                conj.Insert(0, cur);
                // 必须还有其它合取项：若守卫是唯一条件，剥离后 `if ()` 无意义 ⇒ 不折（逐位不变）。
                if (conj.Count < 2) continue;

                if (!(conj[0] is BinaryExpressionSyntax less) || !less.IsKind(SyntaxKind.LessThanExpression))
                    continue;
                if (!(less.Left is IdentifierNameSyntax leftId) || leftId.Identifier.Text != indexParamName)
                    continue;

                string fieldName = null;
                if (less.Right is IdentifierNameSyntax rightId) fieldName = rightId.Identifier.Text;
                else if (less.Right is MemberAccessExpressionSyntax ma && ma.Expression is ThisExpressionSyntax)
                    fieldName = ma.Name.Identifier.Text;
                if (fieldName == null) continue;

                // 右侧必须解析为本 job 的非静态 int 字段（排除局部变量/形参/其它符号）。
                var sym = semanticModel.GetSymbolInfo(less.Right).Symbol as IFieldSymbol;
                if (sym == null || sym.IsStatic || sym.ContainingType == null) continue;
                if (sym.Type.SpecialType != SpecialType.System_Int32) continue;

                foldedField = fieldName;
                return true;
            }
            return false;
        }

        /// <summary>
        /// G 的剥离一步：在已翻译的 C++ 体里把首个 `if (index &lt; Field &amp;&amp; …)` 的首个合取项去掉。
        /// 只在模式实际命中时返回 true（模式要求后面还有 `&amp;&amp;` ⇒ 不会产出 `if ()`）。
        /// 模式用 AST 已确认过的 index 形参名与字段名构造 ⇒ 不会误伤别的比较。
        /// </summary>
        private static bool StripFirstGuardConjunct(ref string code, string indexParamName, string fieldName)
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(fieldName)) return false;
            var pattern = @"(\bif\s*\(\s*)" + Regex.Escape(indexParamName) + @"\s*<\s*"
                + Regex.Escape(fieldName) + @"\s*&&\s*";
            var rx = new Regex(pattern);
            var m = rx.Match(code);
            if (!m.Success) return false;
            code = code.Substring(0, m.Index) + m.Groups[1].Value + code.Substring(m.Index + m.Length);
            return true;
        }

        private static void GenerateBatchFunctionStandard(INamedTypeSymbol jobStruct, SemanticModel semanticModel, MethodDeclarationSyntax methodSyntax, StringBuilder sb, bool useFastMath, NativeTranspiler.AutoSIMD autoSIMD = NativeTranspiler.AutoSIMD.Disabled, NativeTranspiler.SimdMathPrecision simdMathPrecision = NativeTranspiler.SimdMathPrecision.Fastest, bool isRangeJob = false)
        {
            string funcName = GetCppJobFunctionName(jobStruct, isBatch: true);
            string paramsStr = BuildBatchJobParameters(jobStruct);
            // 无条件把 batch 入口内联进它的 Adapter。
            // 对齐档 cs=1 时每个元素多付「一帧 + 一次 26 实参搬迁」。
            // Melee 1.0509（8/9）、Integrate 1.0726（6/9）；默认档 JCC=0 1.013、JCC=1 1.022（各 3 轮…
            // 该档每轮散布 ±7%，故只作"非负"证据）。属性写在声明符之后：clang 拒绝放在
            // `extern "C" __declspec(dllexport)` 之前。调用次数不变 ⇒ 契约不受影响。
            // 两个编译器都覆盖 —— clang(-cl) 用声明符之后的
            // `__attribute__((always_inline))`（实测路径）；MSVC 用声明说明符位的 `__forceinline`
            // 之前 MSVC 分支宏为空 ⇒ 该编译器下这个优化等于不存在（不报错，但也不生效）。
            sb.AppendLine("#ifndef EJ_BATCH_FORCEINLINE_PRE");
            sb.AppendLine("#  if defined(_MSC_VER) && !defined(__clang__)");
            sb.AppendLine("#    define EJ_BATCH_FORCEINLINE_PRE __forceinline");
            sb.AppendLine("#  else");
            sb.AppendLine("#    define EJ_BATCH_FORCEINLINE_PRE");
            sb.AppendLine("#  endif");
            sb.AppendLine("#endif");
            sb.AppendLine("#ifndef EJ_BATCH_ALWAYS_INLINE");
            sb.AppendLine("#  if defined(__clang__)");
            sb.AppendLine("#    define EJ_BATCH_ALWAYS_INLINE __attribute__((always_inline))");
            sb.AppendLine("#  else");
            sb.AppendLine("#    define EJ_BATCH_ALWAYS_INLINE");
            sb.AppendLine("#  endif");
            sb.AppendLine("#endif");
            sb.AppendLine($"GENERATED_API EJ_BATCH_FORCEINLINE_PRE void CALLINGCONVENTION {funcName}({paramsStr}) EJ_BATCH_ALWAYS_INLINE");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, semanticModel: semanticModel);
            var indexParamName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;

            // IJobParallelForBatch：Execute(startIndex, count) 就是一次区间调用 ⇒ 不生成 index 循环，
            // 两个形参分别映射到 C++ 形参 `__startIndex` / `__count`（`__count` 由 BuildBatchJobParameters 声明）。
            if (isRangeJob)
            {
                string countParamName = methodSyntax.ParameterList.Parameters.Count > 1
                    ? methodSyntax.ParameterList.Parameters[1].Identifier.Text
                    : null;
                var rangeTranslator = new CppBatchStatementTranslator(semanticModel, jobStruct,
                    indexParamName, "__startIndex", useFastMath, false,
                    countParamName, "__count");
                sb.Append(rangeTranslator.Translate(methodSyntax.Body));
                sb.AppendLine("}");
                sb.AppendLine();
                return;
            }

            // 先用标量翻译器翻译 body（余量循环需要标量体）
            // 体首指针别名声明的两轮"作用域收窄"（①单点下沉 ②按使用块复制）都已实测为负
            // （②覆盖 18/28，Melee 比值仍 1.0402）⇒ 别再试；寄存器压力来自同时存活约 22 个值。
            var scalarTranslator = new CppBatchStatementTranslator(semanticModel, jobStruct, indexParamName, indexParamName, useFastMath, /* scalar body, no SIMD */ false);
            var scalarBody = scalarTranslator.Translate(methodSyntax.Body);

            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
            {
                // Per-lane SIMD: gather queries once, extract to scalar for each lane
                var simdGen = new OuterSimdGenerator(methodSyntax, semanticModel, indexParamName, jobStruct: jobStruct, simdMathPrecision: simdMathPrecision);
                var simdCode = simdGen.Generate(scalarBody);
                sb.Append(simdCode);
                sb.AppendLine("}");
                sb.AppendLine();
                return;
            }

            // 回退标量路径
            // 批循环把整段 [__startIndex, __startIndex+__count) 放进同一个函数体，而 C# 的 `return;`
            // （Execute 内）只结束本次 index。裸 `return;` 在 C++ 里会退出整个函数 ⇒ 静默跳过本批
            // 剩余下标（实测：1024 元素单批、index 0 处 return ⇒ 只有 index 0 被处理）。
            // ⚠ do-while 包裹不能解决这个问题（它只重定向 `break`，`return` 照样穿出去）——
            //   必须用一个立即调用的 lambda 包住体，`return;` 就变成"结束本次迭代"。
            bool bodyHasReturn = scalarBody.Contains("return;");
            // G（守卫折叠）：AST 只做检测，剥离在已翻译文本上做（未命中 ⇒ 原样，逐位不变）。
            string scalarLoopEnd = "__startIndex + __count";
            if (GuardFoldEnabled
                && TryDetectIndexLengthGuard(methodSyntax, indexParamName, semanticModel, out var guardField)
                && StripFirstGuardConjunct(ref scalarBody, indexParamName, guardField))
            {
                scalarLoopEnd = $"std::min(__startIndex + __count, {guardField})";
                bodyHasReturn = scalarBody.Contains("return;");
            }
            sb.AppendLine($"    for (int {indexParamName} = __startIndex; {indexParamName} < {scalarLoopEnd}; ++{indexParamName})");
            sb.AppendLine("    {");
            if (bodyHasReturn) sb.AppendLine("        [&]() {");
            sb.Append(scalarBody);
            if (bodyHasReturn) sb.AppendLine("        }();");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }


        private static void GenerateBatchFunctionVariant(INamedTypeSymbol jobStruct, List<IFieldSymbol> boolFields, List<bool> values, SemanticModel semanticModel, MethodDeclarationSyntax methodSyntax, StringBuilder sb, bool useFastMath, NativeTranspiler.AutoSIMD autoSIMD = NativeTranspiler.AutoSIMD.Disabled, NativeTranspiler.SimdMathPrecision simdMathPrecision = NativeTranspiler.SimdMathPrecision.Fastest, bool isRangeJob = false)
        {
            string suffix = BuildBoolVariantSuffix(boolFields, values);
            string funcName = GetCppJobFunctionName(jobStruct, isBatch: true) + suffix;
            string paramsStr = BuildBatchJobParameters(jobStruct);
            // 无条件把 batch 入口内联进它的 Adapter。
            // 对齐档 cs=1 时每个元素多付「一帧 + 一次 26 实参搬迁」。
            // Melee 1.0509（8/9）、Integrate 1.0726（6/9）；默认档 JCC=0 1.013、JCC=1 1.022（各 3 轮…
            // 该档每轮散布 ±7%，故只作"非负"证据）。属性写在声明符之后：clang 拒绝放在
            // `extern "C" __declspec(dllexport)` 之前。调用次数不变 ⇒ 契约不受影响。
            // 两个编译器都覆盖 —— clang(-cl) 用声明符之后的
            // `__attribute__((always_inline))`（实测路径）；MSVC 用声明说明符位的 `__forceinline`
            // 之前 MSVC 分支宏为空 ⇒ 该编译器下这个优化等于不存在（不报错，但也不生效）。
            sb.AppendLine("#ifndef EJ_BATCH_FORCEINLINE_PRE");
            sb.AppendLine("#  if defined(_MSC_VER) && !defined(__clang__)");
            sb.AppendLine("#    define EJ_BATCH_FORCEINLINE_PRE __forceinline");
            sb.AppendLine("#  else");
            sb.AppendLine("#    define EJ_BATCH_FORCEINLINE_PRE");
            sb.AppendLine("#  endif");
            sb.AppendLine("#endif");
            sb.AppendLine("#ifndef EJ_BATCH_ALWAYS_INLINE");
            sb.AppendLine("#  if defined(__clang__)");
            sb.AppendLine("#    define EJ_BATCH_ALWAYS_INLINE __attribute__((always_inline))");
            sb.AppendLine("#  else");
            sb.AppendLine("#    define EJ_BATCH_ALWAYS_INLINE");
            sb.AppendLine("#  endif");
            sb.AppendLine("#endif");
            sb.AppendLine($"GENERATED_API EJ_BATCH_FORCEINLINE_PRE void CALLINGCONVENTION {funcName}({paramsStr}) EJ_BATCH_ALWAYS_INLINE");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, semanticModel: semanticModel);
            var indexParamName = methodSyntax.ParameterList.Parameters[0].Identifier.Text;

            // IJobParallelForBatch：与 GenerateBatchFunctionStandard 的 isRangeJob 分支同构（不生成 index 循环）。
            if (isRangeJob)
            {
                string countParamName = methodSyntax.ParameterList.Parameters.Count > 1
                    ? methodSyntax.ParameterList.Parameters[1].Identifier.Text
                    : null;
                var rangeTranslator = new CppBatchStatementTranslator(semanticModel, jobStruct,
                    indexParamName, "__startIndex", useFastMath, false,
                    countParamName, "__count");
                sb.Append(rangeTranslator.Translate(methodSyntax.Body));
                sb.AppendLine("}");
                sb.AppendLine();
                return;
            }

            // 先用标量翻译器翻译 body + 替换 bool 常量
            bool scalar_noSIMD = false;
            var translator = new CppBatchStatementTranslator(semanticModel, jobStruct, indexParamName, indexParamName, useFastMath, scalar_noSIMD);
            var bodyCode = translator.Translate(methodSyntax.Body);
            for (int i = 0; i < boolFields.Count; i++)
            {
                string constantLiteral = values[i] ? "true" : "false";
                string pattern = $@"{Regex.Escape(boolFields[i].Name)}";
                bodyCode = Regex.Replace(bodyCode, pattern, constantLiteral);
            }

            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
            {
                // Per-lane SIMD: gather queries once, extract to scalar for each lane
                // Works for any Job body — no eligibility check needed.
                var boolFieldValues = new System.Collections.Generic.Dictionary<string, string>();
                for (int i_ = 0; i_ < boolFields.Count; i_++)
                    boolFieldValues[boolFields[i_].Name] = values[i_] ? "true" : "false";
                var simdGen = new OuterSimdGenerator(methodSyntax, semanticModel, indexParamName, boolFieldValues, jobStruct, simdMathPrecision);
                var simdCode = simdGen.Generate(bodyCode);
                sb.Append(simdCode);
                sb.AppendLine("}");
                sb.AppendLine();
                return;
            }

            // 标量回退（同上：体内有 `return;` 时用立即调用 lambda 包住，保证它只结束本次 index）
            // G（守卫折叠）：AST 只做检测，剥离在已翻译文本上做（未命中 ⇒ 原样，逐位不变）。
            string variantLoopEnd = "__startIndex + __count";
            if (GuardFoldEnabled
                && TryDetectIndexLengthGuard(methodSyntax, indexParamName, semanticModel, out var guardField2)
                && StripFirstGuardConjunct(ref bodyCode, indexParamName, guardField2))
            {
                variantLoopEnd = $"std::min(__startIndex + __count, {guardField2})";
            }
            bool variantHasReturn = bodyCode.Contains("return;");
            sb.AppendLine($"    for (int {indexParamName} = __startIndex; {indexParamName} < {variantLoopEnd}; ++{indexParamName})");
            sb.AppendLine("    {");
            if (variantHasReturn) sb.AppendLine("        [&]() {");
            sb.Append(bodyCode);
            if (variantHasReturn) sb.AppendLine("        }();");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine();
        }

        private static void GenerateSingleFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath,
            NativeTranspiler.AutoSIMD autoSIMD = NativeTranspiler.AutoSIMD.Disabled,
            NativeTranspiler.SimdMathPrecision simdMathPrecision = NativeTranspiler.SimdMathPrecision.Fastest)
        {
            var singleParams = BuildJobParameters(jobStruct);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({singleParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

                if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                {
                    // IJob/static: no OuterSimdGenerator batch wrapper, use SimdControlFlowGenerator directly
                    // Inner for-loops, if-else, gather-blend all become SIMD via mask management.
                    // Output writes with varying index → per-lane scatter; uniform index → extract lane 0.
                    var varAnalyzer = new SimdVariableAnalyzer(semanticModel, jobStruct, "");
                    var variables = varAnalyzer.Analyze(methodSyntax);
                    var simdGen = new SimdControlFlowGenerator(
                        semanticModel, jobStruct, variables, varAnalyzer,
                        indexParamName: "", simdIndexVar: "v_i",
                        // 单 IJob：没有生成器可知的批循环 ⇒ 偏移未知。先前传字面量 "0" 等于断言
                        // "v_i 相对基址从 0 开始"，而用户循环起点未知 ⇒ 现用哨兵值让
                        // EmitElementStore 退回逐 lane scatter（NT-01(b) 同类陷阱封死）。
                        batchOffsetVar: SimdControlFlowGenerator.UnknownBatchOffset,
                        simdMathPrecision: simdMathPrecision);
                    var simdBody = simdGen.Generate(methodSyntax.Body);
                    sb.Append(simdBody);
                }
                else
                {
                    var translator = new CppPointerStatementTranslator(semanticModel, jobStruct, useFastMath);
                    var bodyCode = translator.Translate(methodSyntax.Body);
                    sb.Append(bodyCode);
                }
            }
            else
            {
                sb.AppendLine("    // (empty Execute body)");
            }
            sb.AppendLine("}");
        }

        private static void GenerateChunkFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
        {
            bool usesSendEvent = JobUsesSendEvent(jobStruct, compilation);
            var chunkParams = BuildChunkJobParameters(jobStruct, includeHeader: usesSendEvent);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
                var sharedTypes = CollectSharedComponentTypes(jobStruct, compilation);
                var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, sharedTypes, useFastMath);
                var bodyCode = translator.Translate(methodSyntax.Body);
                sb.Append(bodyCode);
            }
            else
            {
                sb.AppendLine("    // (empty IJobChunk Execute body)");
            }
            sb.AppendLine("}");
        }

        private static void GenerateChunkFunctionVectorize(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
        {
            // Generate auto-vectorizable scalar loop — Clang generates @llvm.sin.v8f32 / cos.v8f32
            bool usesSendEvent = JobUsesSendEvent(jobStruct, compilation);
            var chunkParams = BuildChunkJobParameters(jobStruct, includeHeader: usesSendEvent);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
                var sharedTypes = CollectSharedComponentTypes(jobStruct, compilation);
                // Use same chunk translator as standard mode — produces scalar ptr[index].field
                var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, sharedTypes, useFastMath);
                var bodyCode = translator.Translate(methodSyntax.Body);
                // Insert auto-vectorize pragma before the entity for-loop
                string pragma = "#ifdef __clang__\n#pragma clang loop vectorize(enable) interleave(enable)\n#endif\n";
                int forPos = bodyCode.IndexOf("for (");
                if (forPos >= 0)
                    bodyCode = bodyCode.Insert(forPos, pragma);
                sb.Append(bodyCode);
            }
            else
            {
                sb.AppendLine("    // (empty IJobChunk Execute body)");
            }
            sb.AppendLine("}");
        }

        private static void GenerateEntityFunctionVectorize(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
        {
            // Flat scalar loop for IJobEntity — compiler auto-vectorizes across entities
            var chunkParams = BuildChunkJobParameters(jobStruct, includeTypeIds: false);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            for (int i = 0; i < executeMethod.Parameters.Length; i++)
            {
                var param = executeMethod.Parameters[i];
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                sb.AppendLine($"    auto* __entity_param_{i}_ptr = reinterpret_cast<{cppType}*>(__chunkData->componentArrays[{i}]);");
                sb.AppendLine($"    (void)((intptr_t)__entity_param_{i}_ptr % 64 == 0);");
            }

            // Pre-translate scalar body
            string scalarBody = "";
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var translator = new CppEntityStatementTranslator(semanticModel, jobStruct, useFastMath);
                scalarBody = translator.Translate(methodSyntax.Body);
            }

            // Replace param.Name references with array indexing
            for (int i = 0; i < executeMethod.Parameters.Length; i++)
            {
                var param = executeMethod.Parameters[i];
                scalarBody = scalarBody.Replace(param.Name + ".", $"__entity_param_{i}_ptr[__entity_index].");
            }

            // NT-02（Critical）：旧实现直接 `scalarBody.Replace("return;", "")` ⇒ 无括号写法
            //   `if (p.X < 0) return; p.Marker += 1;` 变成 `if (p.X < 0) p.Marker += 1;`
            //   ——控制流反转、静默错值。
            // NT-07：嵌在循环里的 `return;` 降级为 `break;` 只跳出内层循环 ⇒ 必须写唯一标记
            //   让构建期（NativeCompileTask.CheckGeneratedMarkers）失败，而不是静默错执行。
            // index 层级则用 do-while + `break;` 表达"结束本次实体"。
            bool hasReturn = scalarBody.Contains("return;");
            if (hasReturn)
                scalarBody = ReturnStatementRewriter.Rewrite(scalarBody, methodSyntax?.Body, "break;");

            // Flat scalar loop with auto-vectorize pragma (must be directly before the for)
            sb.AppendLine("    int __entity_count = __chunkData->entityCount;");
            sb.AppendLine("#ifdef __clang__");
            sb.AppendLine("#pragma clang loop vectorize(enable) interleave(enable)");
            sb.AppendLine("#endif");
            sb.AppendLine("    for (int __entity_index = 0; __entity_index < __entity_count; ++__entity_index)");
            sb.AppendLine("    {");
            if (hasReturn) sb.AppendLine("        do {");
            string bodyIndent = hasReturn ? "            " : "        ";
            foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (line.Length == 0) continue;
                sb.Append(bodyIndent).AppendLine(line);
            }
            if (hasReturn) sb.AppendLine("        } while(false);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }

        private static void GenerateEntityFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
        {
            var chunkParams = BuildChunkJobParameters(jobStruct, includeTypeIds: false);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            for (int i = 0; i < executeMethod.Parameters.Length; i++)
            {
                var param = executeMethod.Parameters[i];
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                sb.AppendLine($"    auto* __entity_param_{i}_ptr = reinterpret_cast<{cppType}*>(__chunkData->componentArrays[{i}]);");
                sb.AppendLine($"    (void)((intptr_t)__entity_param_{i}_ptr % 64 == 0);");
            }

            // Pre-translate scalar body
            string scalarBody = "";
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var translator = new CppEntityStatementTranslator(semanticModel, jobStruct, useFastMath);
                scalarBody = translator.Translate(methodSyntax.Body);
            }

            // NT-02/NT-07：index 层级的 `return;` 配合外层 do-while 降级为 `break;`（结束本次实体）；
            // 嵌在循环里的 `return;` 不能降级为 break（只跳内层循环），改写为唯一标记 ⇒ 构建期失败。
            bool hasReturn = scalarBody.Contains("return;");
            if (hasReturn)
                scalarBody = ReturnStatementRewriter.Rewrite(scalarBody, methodSyntax?.Body, "break;");

            // Generate per-lane SIMD wrapper + remainder loop
            sb.AppendLine("    int __entity_count = __chunkData->entityCount;");
            sb.AppendLine("    int __simd_end = (__entity_count / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine("    if (__simd_end > 0)");
            sb.AppendLine("    {");
            sb.AppendLine("        for (int si = 0; si < __simd_end; si += NSIMD_WIDTH)");
            sb.AppendLine("        {");
            sb.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
            sb.AppendLine("            {");
            sb.AppendLine("                int __entity_index = si + lane;");
            foreach (var param in executeMethod.Parameters.Select((p, i) => (p, i)))
            {
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.p.Type);
                string constPrefix = param.p.RefKind == RefKind.In ? "const " : "";
                sb.AppendLine($"                {constPrefix}{cppType}& {param.p.Name} = __entity_param_{param.i}_ptr[__entity_index];");
            }
            if (hasReturn)
            {
                sb.AppendLine("                do {");
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append("                    ").AppendLine(line);
                }
                sb.AppendLine("                } while(false);");
            }
            else
            {
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append("                ").AppendLine(line);
                }
            }
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            // Remainder scalar loop
            sb.AppendLine("    for (int __entity_index = __simd_end; __entity_index < __entity_count; ++__entity_index)");
            sb.AppendLine("    {");
            foreach (var param in executeMethod.Parameters.Select((p, i) => (p, i)))
            {
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.p.Type);
                string constPrefix = param.p.RefKind == RefKind.In ? "const " : "";
                sb.AppendLine($"        {constPrefix}{cppType}& {param.p.Name} = __entity_param_{param.i}_ptr[__entity_index];");
            }
            if (hasReturn)
            {
                sb.AppendLine("        do {");
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append("            ").AppendLine(line);
                }
                sb.AppendLine("        } while(false);");
            }
            else
            {
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    if (line.Length == 0) continue;
                    sb.Append("        ").AppendLine(line);
                }
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }

        /// <summary>
        /// 生成 IJobEntity 的独立 C++ 函数（对标 GenerateChunkFunctionStandard）。
        /// 函数签名：
        /// void Execute(const ChunkData* __chunkData, ... field_ptrs ...)
        /// 使用 ChunkData 轻量结构（不含 __requiredComponentTypeIds），componentArrays 直接索引。
        /// </summary>
        private static void GenerateEntityChunkFunctionStandard(INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb, bool useFastMath)
        {
            var chunkParams = BuildChunkJobParameters(jobStruct, includeTypeIds: false);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);

            // 从 __chunkData->componentArrays 提取组件数组指针（Entity 参数不占组件列，需独立计数）
            int compIdx = 0;
            for (int i = 0; i < executeMethod.Parameters.Length; i++)
            {
                var param = executeMethod.Parameters[i];
                if (NativeTranspiler.IsEntityType(param.Type)) continue;
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(param.Type);
                string constPrefix = param.RefKind == RefKind.In ? "const " : "";
                sb.AppendLine($"    {constPrefix}auto* __entity_param_{i}_ptr = reinterpret_cast<{constPrefix}{cppType}*>(__chunkData->componentArrays[{compIdx}]);");
                sb.AppendLine($"    (void)((intptr_t)__entity_param_{i}_ptr % 64 == 0);");
                compIdx++;
            }

            // 预翻译标量 body
            string scalarBody = "";
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                var translator = new CppEntityStatementTranslator(semanticModel, jobStruct, useFastMath);
                scalarBody = translator.Translate(methodSyntax.Body);
            }

            // NT-02/NT-07：index 层级的 `return;` 配合外层 do-while 降级为 `break;`；
            // 嵌在循环里的 `return;` 降级为 break 只会跳内层循环 ⇒ 改写为唯一标记让构建期失败。
            bool hasReturn = scalarBody.Contains("return;");
            if (hasReturn)
                scalarBody = ReturnStatementRewriter.Rewrite(scalarBody, methodSyntax?.Body, "break;");

            // Entity 参数：用函数内局部结构体（{ int Id; int Version; } 对齐 C# Entity）。
            // 不用 EntJoy.ECS.Entity 结构头：Entity 是 IJobEntity 的注入参数，不是 chunk 组件列，
            // CollectChunkNativeArrayTypes 已把它跳过（见该函数的 IsEntityType 过滤），
            // 局部结构体让生成的批函数自包含，不依赖该框架类型是否被别的 job 带进 userStructs。
            bool hasEntityParam = executeMethod.Parameters.Any(p => NativeTranspiler.IsEntityType(p.Type));
            if (hasEntityParam)
                sb.AppendLine("    struct __EntJoyEntity { int Id; int Version; };");

            // 实体循环
            sb.AppendLine();
            sb.AppendLine("    int __entity_count = __chunkData->entityCount;");
            sb.AppendLine("    for (int __entity_index = 0; __entity_index < __entity_count; ++__entity_index)");
            sb.AppendLine("    {");
            foreach (var (p, i) in executeMethod.Parameters.Select((p, i) => (p, i)))
            {
                if (NativeTranspiler.IsEntityType(p.Type))
                {
                    sb.AppendLine($"        __EntJoyEntity {p.Name} = ((__EntJoyEntity*)__chunkData->entityArray)[__entity_index];");
                    continue;
                }
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(p.Type);
                string constPrefix = p.RefKind == RefKind.In ? "const " : "";
                sb.AppendLine($"        {constPrefix}{cppType}& {p.Name} = __entity_param_{i}_ptr[__entity_index];");
            }

            if (hasReturn)
            {
                sb.AppendLine("        do {");
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    sb.AppendLine($"            {trimmed}");
                }
                sb.AppendLine("        } while(false);");
            }
            else
            {
                foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    sb.AppendLine($"        {trimmed}");
                }
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        private static string BuildJobParameters(INamedTypeSymbol jobStruct)
        {
            var parameters = new List<string>();
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            if (executeMethod.Parameters.Length == 1 && executeMethod.Parameters[0].Type.SpecialType == SpecialType.System_Int32)
                parameters.Add($"int {executeMethod.Parameters[0].Name}");
            AppendFieldParameters(jobStruct, parameters);
            return string.Join(", ", parameters);
        }

        private static string BuildChunkJobParameters(INamedTypeSymbol jobStruct, bool includeTypeIds = true, bool includeHeader = false)
        {
            // includeTypeIds=false → 轻量 ChunkData 路径（IJobEntity/Chunk 无需类型匹配）
            // includeTypeIds=true → 完整 ChunkJobData 路径（需要 __requiredComponentTypeIds）
            var chunkType = includeTypeIds ? "ChunkJobData" : "ChunkData";
            var parameters = new List<string> { $"const {chunkType}* __chunkData" };
            if (includeTypeIds)
                parameters.Add("const int* __requiredComponentTypeIds");
            if (includeHeader)
                parameters.Add("const __EntJoyChunkContextHeader* __header");
            AppendFieldParameters(jobStruct, parameters);
            return string.Join(", ", parameters);
        }

        // 标量形参打包（`ENTJOY_PACK_SCALARS`）已删除
        // 原意：把"纯值字段"收进一个结构体、以单个指针传参，把形参数从 70+ 降到 (数组字段 + 1)。
        // 删除理由：闸门实测"形参数降到 ~19 后打包已无收益"，且该改造需覆盖批处理/非批处理/ISPC
        // 三条发射路径，后两者开启时构建失败 ⇒ 始终默认关、从未真正可用。
        // 注：为 `IJobEntity` 判定 ISPC 后端的原逻辑随之一并移除。

        /// <summary>
        /// 抽离复用 —— 生成原生 adapter 的"字段解包"代码。
        /// 批形 adapter（`(void*, int start, int count)`）与 `IJobFor` 的 index 形 adapter
        /// （`(void*, int index)`）字段解包逐字相同，只有前缀实参不同
        /// （`__startIndex, __count` ↔ `__index, 1`）⇒ 解包部分收敛到这里，避免两处各写一遍。
        /// 调用方负责先把前缀实参放进 <paramref name="callArgs"/>，本方法追加字段实参。
        /// </summary>
        private static void BuildAdapterFieldAccess(INamedTypeSymbol jobStruct, StringBuilder fieldReads, List<string> callArgs)
        {
            int currentOffset = 0;
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                int offset = CalculateFieldOffset(field, ref currentOffset);

                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                    {
                        // NativeList: _listData 在偏移 0（指针）
                        fieldReads.AppendLine($"    auto* {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(field.Type)}>**)((char*)context + {offset});");
                        callArgs.Add($"{field.Name}_listData");
                    }
                    else // NativeArray
                    {
                        // NativeArray: _buffer 在偏移 0, _length 在偏移 8
                        var cppElemType = GetCppElementType(field.Type);
                        fieldReads.AppendLine($"    auto* {field.Name}_ptr = *({cppElemType}**)((char*)context + {offset});");
                        fieldReads.AppendLine($"    int {field.Name}_length = *(int*)((char*)context + {offset + 8});");
                        callArgs.Add($"{field.Name}_ptr, {field.Name}_length");
                    }
                }
                else if (field.Type is IPointerTypeSymbol)
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    fieldReads.AppendLine($"    auto* {field.Name}_ptr = *({cppType}*)((char*)context + {offset});");
                    callArgs.Add($"{field.Name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    fieldReads.AppendLine($"    auto* {field.Name}_ptr = ({cppType}*)((char*)context + {offset});");
                    callArgs.Add($"{field.Name}_ptr");
                }
            }
        }

        /// <summary>`IJobFor` 的 index 形 adapter 函数名（与批形 adapter 区分开）。</summary>
        // 名字规则统一放在 CppJobNames.cs 的 GetIndexAdapterFunctionName（同一个 partial class）。

        /// <summary>批函数的形参：数组/指针字段逐个传。
        /// 参数顺序必须与适配器侧的调用参数顺序一致（适配器由同一份字段遍历生成）。</summary>
        private static string BuildBatchJobParameters(INamedTypeSymbol jobStruct)
        {
            var parameters = new List<string> { "int __startIndex", "int __count" };
            AppendFieldParameters(jobStruct, parameters);
            return string.Join(", ", parameters);
        }

        private static void AppendFieldParameters(INamedTypeSymbol jobStruct, List<string> parameters)
        {
            // 形参构造点通常没有语义模型 ⇒ 用名字兜底。仅影响 `__restrict` 限定符，误判方向无害
            // （多一个限定符 vs 少一个优化），且语义安全性由下面的论证独立成立。
            var loopUse = GetFieldLoopUse(jobStruct, null);
            bool restrictList = ListLengthRestrictEnabled(jobStruct, loopUse);
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                    {
                        var elementType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        // NativeList 的 `_listData` 形参：当该表的长度决定某个循环的行程数时加 `__restrict`。
                        // 机理（clang-cl /O2 微实验，）：`UnsafeList<T>& L = *L_listData;` +
                        // `for (i = 0; i < L.length(); i++)` 无法 hoist（微实验：每轮 `movslq 0x8(%rdx)`），
                        // 而 `__restrict` 加在形参上即可 hoist + 向量化（`movslq` 提到循环外 + `movdqu`）；
                        // 加在局部引用上无效（实测与不加逐字节相同）。
                        // 语义中立：restrict 只断言"该对象不经其它指针访问"，不断言不被写 ——
                        // 凡是通过同一个指针写（`L.Add()` 内联或不可见调用）的循环，行程数仍逐轮重载
                        // （微实验已验证 inlined/opaque 两种写法）。真正被排除的只有"另一指针也在写它"。
                        string lr = restrictList && loopUse.TripCount.Contains(field.Name) ? "__restrict " : "";
                        parameters.Add($"EntJoy::Collections::UnsafeList<{cppElementType}>* {lr}{field.Name}_listData");
                    }
                    else // NativeArray
                    {
                        var elementType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        parameters.Add($"{cppElementType}* {field.Name}_ptr, int {field.Name}_length");
                    }
                }
                else if (field.Type is IPointerTypeSymbol)
                {
                    // 修改：不再添加多余的 *，MapCSharpTypeToCpp 已包含 *
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    parameters.Add($"{cppType} {field.Name}_ptr");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    // 纯值字段形参：可选 `__restrict`（只给这一类别名判定成立的地方加，见
                    // ScalarRestrictEnabled 的机理注释；数组/分量指针仍不加）。
                    // （MSVC/clang-cl/GCC 都认），于是默认档发射面逐字不变。
                    string restrict = ScalarRestrictEnabled(field.Name, loopUse) ? "__restrict " : "";
                    parameters.Add($"{cppType}* {restrict}{field.Name}_ptr");
                }
            }
        }

        /// <summary>NativeList 形参是否加 `__restrict`。
        /// 只作用于长度决定循环行程数的表，且该 job 只有一个 NativeList 字段 ——
        /// 两个 NativeList 字段可能指向同一份 `UnsafeList`，此时对两者都加 restrict 就是说谎（UB）。</summary>
        private static bool ListLengthRestrictEnabled(INamedTypeSymbol jobStruct, FieldLoopUse loopUse)
        {
            int lists = 0;
            foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                if (NativeTranspiler.IsEntJoyContainerNamed(f.Type, Config.NativeList)) lists++;
            return lists == 1 && loopUse.TripCount.Count > 0;
        }

        public static List<INamedTypeSymbol> CollectChunkNativeArrayTypes(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var result = new List<INamedTypeSymbol>();
            if (IsEntityJob(jobStruct))
            {
                var execute = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
                if (execute != null)
                {
                    foreach (var parameter in execute.Parameters)
                    {
                        if (NativeTranspiler.IsEntityType(parameter.Type))
                            continue;   // Entity 参数不是组件列
                        if (parameter.Type is INamedTypeSymbol componentType &&
                            !result.Any(t => SymbolEqualityComparer.Default.Equals(t, componentType)))
                        {
                            result.Add(componentType);
                        }
                    }
                }
                return result;
            }

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            var methodSyntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return result;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var invocation in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
                    continue;
                if (methodSymbol.ContainingType?.ToDisplayString() != Config.TypeArchetypeChunk ||
                    (methodSymbol.Name != Config.GetComponentDataNativeArray && methodSymbol.Name != Config.GetComponentDataSpan
                     && methodSymbol.Name != Config.GetEnableBitMapPtr))
                    continue;
                if (methodSymbol.TypeArguments.Length == 0 || methodSymbol.TypeArguments[0] is not INamedTypeSymbol componentType)
                    continue;
                if (!result.Any(t => SymbolEqualityComparer.Default.Equals(t, componentType)))
                    result.Add(componentType);
            }
            return result;
        }

        /// <summary>
        /// 收集 IJobChunk Execute 中 ArchetypeChunk.GetSharedComponent&lt;T&gt;() 调用的 shared 类型。
        /// 仅限 blittable（managed shared 不允许在 NativeTranspile job 中访问，由 validator 拦截）。
        /// </summary>
        /// <summary>检测 Job 的 Execute 是否调用 SendEvent（决定是否生成 __header 参数）。</summary>
        public static bool JobUsesSendEvent(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            var methodSyntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return false;

            foreach (var invocation in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // xxx.SendEvent / 裸 SendEvent
                if (invocation.Expression is MemberAccessExpressionSyntax mac
                    && mac.Name.Identifier.Text == Config.SendEvent)
                    return true;
                if (invocation.Expression is IdentifierNameSyntax idn
                    && idn.Identifier.Text == Config.SendEvent)
                    return true;
            }
            return false;
        }

        public static List<INamedTypeSymbol> CollectSharedComponentTypes(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var result = new List<INamedTypeSymbol>();
            if (IsEntityJob(jobStruct)) return result;  // IJobEntity 不通过 chunk 参数读 shared（走 job 字段）

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            var methodSyntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return result;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var invocation in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
                    continue;
                if (methodSymbol.ContainingType?.ToDisplayString() != Config.TypeArchetypeChunk ||
                    methodSymbol.Name != Config.GetSharedComponent ||
                    methodSymbol.TypeArguments.Length != 1)
                    continue;
                if (methodSymbol.TypeArguments[0] is not INamedTypeSymbol sharedType)
                    continue;
                if (!result.Any(t => SymbolEqualityComparer.Default.Equals(t, sharedType)))
                    result.Add(sharedType);
            }
            return result;
        }

        internal static List<string> CollectJobStructIncludes(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var includes = new HashSet<string>();
            void AddType(ITypeSymbol type)
            {
                if (type is IPointerTypeSymbol ptr)
                {
                    AddType(ptr.PointedAtType);
                    return;
                }
                // 泛型：先递归模板实参（EntJoy 侧泛型如 NativeComponentLookup<T> 自身由框架头提供，
                // 但实参是用户结构体时必须有头文件 —— 例如体内 `T* p = …` 会写全限定名 T*）。
                if (type is INamedTypeSymbol generic && generic.IsGenericType)
                {
                    foreach (var arg in generic.TypeArguments)
                        AddType(arg);
                    if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                        return;
                }
                if (type is INamedTypeSymbol namedType &&
                    type.TypeKind == TypeKind.Struct &&
                    !NativeTranspiler.IsBuiltinUnmanaged(type) &&
                    !NativeTranspiler.IsEntJoyPredefinedType(type))
                {
                    includes.Add(NativeTranspiler.GetStructHeaderFileName(namedType));
                }
            }

            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                AddType(field.Type);
            foreach (var type in CollectChunkNativeArrayTypes(jobStruct, compilation))
                AddType(type);
            // SharedComponent 类型头文件（blittable，GetSharedComponent<T>() 用）
            foreach (var type in CollectSharedComponentTypes(jobStruct, compilation))
                AddType(type);
            // SendEvent 事件类型头文件
            foreach (var type in CollectSendEventTypes(jobStruct, compilation))
                AddType(type);

            return includes.OrderBy(x => x).ToList();
        }

        /// <summary>收集 Execute 中 SendEvent 用到的事件类型。</summary>
        public static List<INamedTypeSymbol> CollectSendEventTypes(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var result = new List<INamedTypeSymbol>();
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            var methodSyntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return result;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var invocation in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // 判断是否 SendEvent
                bool isSendEvent = invocation.Expression is MemberAccessExpressionSyntax macS && macS.Name.Identifier.Text == Config.SendEvent;
                if (!isSendEvent && invocation.Expression is IdentifierNameSyntax idS && idS.Identifier.Text == Config.SendEvent)
                    isSendEvent = true;
                if (!isSendEvent) continue;

                // 显式泛型参数 SendEvent<T>
                if (invocation.Expression is MemberAccessExpressionSyntax mac2 && mac2.Name is GenericNameSyntax gn)
                {
                    foreach (var typeArg in gn.TypeArgumentList.Arguments)
                    {
                        var taType = semanticModel.GetTypeInfo(typeArg).Type;
                        // 用 IsStructBlittable 替代 IsUnmanagedType：对嵌套 struct 也正确判断
                        if (taType is INamedTypeSymbol named && IsStructBlittable(named))
                            result.Add(named);
                    }
                }
                // new 表达式参数 SendEvent(new XEvent {...})
                foreach (var arg in invocation.ArgumentList.Arguments)
                {
                    if (arg.Expression is ObjectCreationExpressionSyntax objCreate)
                    {
                        // 注意：不能只查 GetTypeInfo(objCreate.Type)。VS/MSBuild 的 Roslyn 对
                        // object-initializer 的 .Type（QualifiedNameSyntax/IdentifierNameSyntax）
                        // 始终返回表达式类型（两种引擎都可靠）。dotnet CLI 两者都行，VS 只有后者行。
                        var createdType = semanticModel.GetTypeInfo(objCreate).Type
                                       ?? semanticModel.GetTypeInfo(objCreate.Type).Type;
                        // 用 IsStructBlittable 代替 IsUnmanagedType：后者对嵌套 struct 会返回 false
                        // （Roslyn known issue：ContainingType 是 class 时内部 struct 即使全 unmanaged 也判 false）
                        if (createdType is INamedTypeSymbol named2 && IsStructBlittable(named2))
                            result.Add(named2);
                    }
                }
            }
            var distinct = new HashSet<INamedTypeSymbol>(result, SymbolEqualityComparer.Default);
            return distinct.ToList();
        }

        /// <summary>
        /// 手动递归检查 struct 是否 blittable（所有字段都是 unmanaged 类型）。
        /// 替代 IsUnmanagedType：对嵌套 struct 也能正确判断（Roslyn IsUnmanagedType 对嵌套类型会返回 false）。
        /// </summary>
        private static bool IsStructBlittable(INamedTypeSymbol type)
        {
            return IsStructBlittable(type, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default));
        }

        private static bool IsStructBlittable(INamedTypeSymbol type, HashSet<INamedTypeSymbol> visited)
        {
            // 排除引用类型
            if (type.IsReferenceType) return false;
            // 必须有 layout 信息的 struct
            if (type.TypeKind != TypeKind.Struct) return false;

            // 简单快速路径：如果 Roslyn 自身判定为 unmanaged，直接通过
            if (type.IsUnmanagedType) return true;

            // 防止自递归 / 循环引用
            if (!visited.Add(type)) return true; // 已访问过，认为 blittable（结构体自身引用只可能通过指针）

            // 兜底：递归检查所有字段都是 blittable
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic) continue;
                if (!IsTypeBlittable(field.Type, visited)) return false;
            }
            return true;
        }

        private static bool IsTypeBlittable(ITypeSymbol type)
        {
            return IsTypeBlittable(type, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default));
        }

        private static bool IsTypeBlittable(ITypeSymbol type, HashSet<INamedTypeSymbol> visited)
        {
            if (type.IsReferenceType) return false;
            if (type.TypeKind == TypeKind.Pointer) return true;
            if (type is IArrayTypeSymbol) return false;
            if (type.TypeKind == TypeKind.Enum) return true; // enum 的 underlying type 必须 unmanaged
            if (type is INamedTypeSymbol named)
            {
                if (named.IsGenericType) return false; // 泛型可能含引用类型
                if (named.TypeKind == TypeKind.Struct) return IsStructBlittable(named, visited);
                // 基本数值类型都是 blittable
                switch (named.SpecialType)
                {
                    case SpecialType.System_Boolean:
                    case SpecialType.System_Byte:
                    case SpecialType.System_SByte:
                    case SpecialType.System_Int16:
                    case SpecialType.System_UInt16:
                    case SpecialType.System_Int32:
                    case SpecialType.System_UInt32:
                    case SpecialType.System_Int64:
                    case SpecialType.System_UInt64:
                    case SpecialType.System_Single:
                    case SpecialType.System_Double:
                    case SpecialType.System_Char:
                    case SpecialType.System_IntPtr:
                    case SpecialType.System_UIntPtr:
                        return true;
                }
                // EntJoy 自己的 unmanaged 类型
                if (named.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyECS &&
                    named.Name == "Entity")
                    return true;
                // EntJoy.Mathematics.* 下所有 blittable 数学类型（float2/int2/uint2 等）
                if (named.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyMathematics &&
                    named.IsValueType)
                    return true;
            }
            return false;
        }


        /// <summary>
        /// 生成适配函数代码（C++），用于消除 C# 委托桥接。
        /// 适配函数签名匹配 BatchJobFunc(void* context, int startIndex, int count)，
        /// 内部从 context 中按偏移量读取字段，调用实际的 Batch 函数。
        /// </summary>
        public static (string code, List<string> eventTypes) GenerateJobAdapter(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var sb = new StringBuilder();
            var discoveredEventTypes = new List<string>();
            var baseFuncName = GetCppJobFunctionName(jobStruct);
            var adapterFuncName = baseFuncName + "_Adapter";

            // 检查是否为 ISPC job 和 auto-SIMD（include 部分需要 autoSIMD 判断 SimdValue.h）
            var attrSymbol = AttributeHelper.GetAttributeSymbol(compilation);
            var autoSIMD = attrSymbol != null
                ? AttributeHelper.GetAutoSIMD(jobStruct, attrSymbol)
                : NativeTranspiler.AutoSIMD.Disabled;
            bool isIspcJob = attrSymbol != null &&
                AttributeHelper.GetBackendTarget(jobStruct, attrSymbol) == NativeTranspiler.BackendTarget.Ispc;

            sb.AppendLine("#include \"NativeMath.h\"");
            sb.AppendLine("#include \"NativeContainers.h\"");
            if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                sb.AppendLine("#include \"SimdValue.h\"");
            if (IsChunkScheduledJob(jobStruct))
            {
                sb.AppendLine("#include \"ChunkJobData.h\"");
                sb.AppendLine("#include \"EntityBatchData.h\"");
            }
            // 字段里 NativeArray<用户结构体> 的元素类型头文件：adapter/wrapper 的形参列表
            // 直接写 CPUBattle::OrcaLine* 这类限定名，缺头文件就会 use of undeclared identifier。
            foreach (var include in CollectJobStructIncludes(jobStruct, compilation))
                sb.AppendLine($"#include \"{include}.h\"");
            sb.AppendLine(CodeTemplates.GenerateExportMacros());
            sb.AppendLine();

            if (isIspcJob)
            {
                // ISPC job: 声明 wrapper 函数为 extern（在 wrapper.cpp 中实现）
                bool isp_batch = IsParallelForJob(jobStruct) || IsForJob(jobStruct);
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);

                if (isp_batch)
                {
                    var batchFuncName = GetCppJobFunctionName(jobStruct, isBatch: true);
                    var batchParams = BuildBatchJobParameters(jobStruct);
                    sb.AppendLine($"// ISPC wrapper function (defined in wrapper.cpp)");
                    GenerateBoolVariantDeclarations(jobStruct, boolFields, batchFuncName, batchParams, sb);
                }
                else
                {
                    // IJob: non-batch wrapper
                    var singleFuncName = GetCppJobFunctionName(jobStruct, isBatch: false);
                    var singleParams = BuildJobParameters(jobStruct);
                    sb.AppendLine($"// ISPC wrapper function (defined in wrapper.cpp)");
                    sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({singleParams});");
                }
                sb.AppendLine();
            }
            else
            {
                // C++ job: 包含 header 文件
                sb.AppendLine($"#include \"{baseFuncName}.h\"");
                sb.AppendLine();
            }

            bool isChunkJob = IsChunkScheduledJob(jobStruct);
            bool isParallelFor = IsRangeScheduledJob(jobStruct);

            if (isChunkJob)
            {
                sb.AppendLine("#ifndef __EntJoyChunkContextHeader_DEFINED");
                sb.AppendLine("#define __EntJoyChunkContextHeader_DEFINED");
                sb.AppendLine("struct __EntJoyChunkContextHeader");
                sb.AppendLine("{");
                sb.AppendLine("    int chunkCount;");
                sb.AppendLine("    int hasEnabledFilter;");
                sb.AppendLine("    void* queryAllEnabledTypes;");
                sb.AppendLine("    int allEnabledCount;");
                sb.AppendLine("    int gcHandleStartIndex;");
                sb.AppendLine("    void* chunksPtr;");
                sb.AppendLine("    int cleanupInProgress;");
                sb.AppendLine("    int ownsChunkData;");
                sb.AppendLine("    void* requiredComponentTypeIds;");
                sb.AppendLine("    int requiredComponentTypeIdCount;");
                sb.AppendLine("    int jobIsBoxed;");
                sb.AppendLine("    void* chunkArrayHandle;");
                sb.AppendLine("    // Event Buffer");
                sb.AppendLine("    int eventBufferCount;");
                sb.AppendLine("    void* eventBufferHeaders;");
                sb.AppendLine("    void* eventWorldHandle;");
                sb.AppendLine("};");
                sb.AppendLine("#endif");
                sb.AppendLine();
                sb.AppendLine("#ifndef __EntJoyEventBuffer_DEFINED");
                sb.AppendLine("#define __EntJoyEventBuffer_DEFINED");
                sb.AppendLine("struct __EntJoyEventBuffer {");
                sb.AppendLine("    void* data;");
                sb.AppendLine("    int* count;");
                sb.AppendLine("    int capacity;");
                sb.AppendLine("    int elementSize;");
                sb.AppendLine("};");
                sb.AppendLine("#endif");
                sb.AppendLine();

                bool isEntityJob = IsEntityJob(jobStruct);
                bool useFastMath = AttributeHelper.HasFastCppMathLib(jobStruct, attrSymbol);

                // IJobEntity 和 IJobChunk 统一走 ChunkAdapter 路径
                {
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {adapterFuncName}(void* context, const ChunkJobData* __chunkData)");
                sb.AppendLine("{");
                sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
                sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
                sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
                sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
                sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
                sb.AppendLine("    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;");

                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
                var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);

                // IJobEntity：轻量 ChunkData 路径（将 ChunkJobData 转换为 ChunkData，跳过冗余字段）
                if (isEntityJob)
                {
                    sb.AppendLine("    // 轻量 ChunkData：只保留 Execute 实际需要的字段");
                    sb.AppendLine("    ChunkData __chunkDataLite;");
                    sb.AppendLine("    __chunkDataLite.componentArrays = __chunkData->requiredComponentArrays;");
                    sb.AppendLine("    __chunkDataLite.entityCount = __chunkData->entityCount;");
                    sb.AppendLine("    __chunkDataLite.requiredComponentCount = __chunkData->requiredComponentCount;");
                    sb.AppendLine("    __chunkDataLite.enableBitMaps = __chunkData->requiredEnableBitMaps != nullptr ? __chunkData->requiredEnableBitMaps : __chunkData->enableBitMaps;   // 逐组件 enable 位图（优先与 componentArrays 同序的 required 版）");
                    sb.AppendLine("    __chunkDataLite.enableBitmapCount = __chunkData->requiredEnableBitMaps != nullptr ? __chunkData->requiredComponentCount : __chunkData->componentCount;");
                    sb.AppendLine("    // 位图下标与 componentArrays 同序（required 序）；读写原语见 src/NativeDll/NativeEnableMask.h");
                    // 仅当 Execute 声明 Entity 参数时才传实体数组（没有就不传，避免无谓拷贝/解引用）
                    if (executeMethod.Parameters.Any(p => NativeTranspiler.IsEntityType(p.Type)))
                        sb.AppendLine("    __chunkDataLite.entityArray = __chunkData->entityArray;");
                }

                // IJobEntity 和 IJobChunk：解包作业字段到局部变量（指针）
                    int currentOffset = 0;
                    foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    {
                        int offset = CalculateFieldOffset(field, ref currentOffset);

                        if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                        {
                            if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                            {
                                sb.AppendLine($"    auto* {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(field.Type)}>**)(__jobContext + {offset});");
                            }
                            else
                            {
                                var cppElemType = GetCppElementType(field.Type);
                                sb.AppendLine($"    auto* {field.Name}_ptr = *({cppElemType}**)(__jobContext + {offset});");
                                sb.AppendLine($"    int {field.Name}_length = *(int*)(__jobContext + {offset + 8});");
                            }
                        }
                        else if (field.Type is IPointerTypeSymbol)
                        {
                            var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                            sb.AppendLine($"    auto* {field.Name}_ptr = *({cppType}*)(__jobContext + {offset});");
                        }
                        else
                        {
                            var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                            sb.AppendLine($"    auto* {field.Name}_ptr = ({cppType}*)(__jobContext + {offset});");
                        }
                    }

                    // 将字段指针解引用为局部变量引用（原独立 Execute 函数中由 AppendLocalVariableDeclarations 完成）
                    foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    {
                        if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                        {
                            if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                            {
                                var elementType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                                var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                                sb.AppendLine($"    EntJoy::Collections::UnsafeList<{cppElementType}>& {field.Name} = *{field.Name}_listData;");
                            }
                            // NativeArray: nothing
                        }
                    }
                    foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    {
                        if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type)) continue;
                        if (field.Type is IPointerTypeSymbol) continue;
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                        sb.AppendLine($"    const {cppType}& {field.Name} = *{field.Name}_ptr;");
                    }

                    // IJobEntity 或 Auto-SIMD: 调用独立函数而非内联
                    // IJobEntity 使用轻量 ChunkData 路径（&__chunkDataLite）
                    if (isEntityJob || autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                    {
                        string funcName = GetCppJobFunctionName(jobStruct);
                        bool usesSendEvent = JobUsesSendEvent(jobStruct, compilation);
                        string callArgs = isEntityJob
                            ? BuildLiteChunkExecuteCallArgs(jobStruct, includeHeader: usesSendEvent)
                            : BuildChunkExecuteCallArgs(jobStruct, includeHeader: usesSendEvent);
                        sb.AppendLine($"    {funcName}({callArgs});");
                    }
                    else
                    {
                        // 内联 Execute 函数体（如同 IJobEntity 的做法）
                        if (methodSyntax?.Body != null)
                        {
                            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                            var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
                            var sharedTypes = CollectSharedComponentTypes(jobStruct, compilation);
                            var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, sharedTypes, useFastMath);
                            var bodyCode = translator.Translate(methodSyntax.Body);

                            foreach (var line in bodyCode.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                            {
                                if (line.Length == 0) continue;
                                sb.Append("    ").AppendLine(line);
                            }

                            // SendEvent: 生成 EventBuffer 变量声明（在 __header 作用域内）
                            if (translator.EventTypes.Count > 0)
                            {
                                sb.AppendLine($"    // Event Buffer declarations ({translator.EventTypes.Count} types)");
                                for (int ei = 0; ei < translator.EventTypes.Count; ei++)
                                {
                                    string evtName = translator.EventTypes[ei].Name;
                                    sb.AppendLine($"    auto* __evtBuf_{evtName}_{ei} = ((__EntJoyEventBuffer**)__header->eventBufferHeaders)[{ei}];");
                                }
                            }

                            // 收集 SendEvent 发现的事件类型
                            foreach (var evtType in translator.EventTypes)
                                discoveredEventTypes.Add(evtType.ToDisplayString());

                            // 托管事件类型错误：报告编译器诊断
                            if (translator.ManagedEventErrors.Count > 0)
                            {
                                foreach (var (evtSym, invoc) in translator.ManagedEventErrors)
                                {
                                    var diag = Diagnostic.Create(
                                        new DiagnosticDescriptor(
                                            "NT015",
                                            "SendEvent requires unmanaged type",
                                            $"SendEvent<{evtSym.Name}>: event type must be unmanaged (blittable). " +
                                            $"Managed types (string, class, Dictionary) are not supported. " +
                                            $"Use a blittable signal struct.",
                                            "NativeTranspiler",
                                            DiagnosticSeverity.Error,
                                            isEnabledByDefault: true),
                                        invoc.GetLocation());
                                    // 通过 Compilation 添加诊断
                                    // 注意：此处无法直接修改 compilation，诊断在后续验证阶段统一报告
                                }
                                // 标记有错误，不生成 SendEvent 代码
                                discoveredEventTypes.Clear();
                            }
                        }
                    }

                sb.AppendLine("}");
                sb.AppendLine();

                sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{adapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){adapterFuncName};");
                sb.AppendLine("}");
                sb.AppendLine();

                var rangeAdapterFuncName = GetRangeAdapterFunctionName(jobStruct);
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {rangeAdapterFuncName}(void* context, const ChunkJobData* __chunks, int __startIndex, int __count)");
                sb.AppendLine("{");
                // 内联 Adapter：将 header + job 字段提至循环外
                sb.AppendLine("    auto* __header = (__EntJoyChunkContextHeader*)context;");
                sb.AppendLine("    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);");
                sb.AppendLine("    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);");
                sb.AppendLine("    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);");
                sb.AppendLine("    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;");
                sb.AppendLine("    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;");
                // job field 指针
                int rOff = 0;
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    int off = CalculateFieldOffset(f, ref rOff);
                    if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type))
                    {
                        if (NativeTranspiler.IsEntJoyContainerNamed(f.Type, Config.NativeList))
                            sb.AppendLine($"    auto* {f.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(f.Type)}>**)(__jobContext + {off});");
                        else
                        {
                            var e = GetCppElementType(f.Type);
                            sb.AppendLine($"    auto* {f.Name}_ptr = *({e}**)(__jobContext + {off});");
                            sb.AppendLine($"    int {f.Name}_length = *(int*)(__jobContext + {off + 8});");
                        }
                    }
                    else if (f.Type is IPointerTypeSymbol)
                    {
                        var t = NativeTranspiler.MapCSharpTypeToCpp(f.Type);
                        sb.AppendLine($"    auto* {f.Name}_ptr = *({t}*)(__jobContext + {off});");
                    }
                    else
                    {
                        var t = NativeTranspiler.MapCSharpTypeToCpp(f.Type);
                        sb.AppendLine($"    auto* {f.Name}_ptr = ({t}*)(__jobContext + {off});");
                    }
                }
                // field refs
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    if (NativeTranspiler.IsEntJoyContainerNamed(f.Type, Config.NativeList))
                    { var e = ((INamedTypeSymbol)f.Type).TypeArguments[0]; var c = NativeTranspiler.MapCSharpTypeToCpp(e); sb.AppendLine($"    EntJoy::Collections::UnsafeList<{c}>& {f.Name} = *{f.Name}_listData;"); }
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type)) continue;
                    if (f.Type is IPointerTypeSymbol) continue;
                    sb.AppendLine($"    const {NativeTranspiler.MapCSharpTypeToCpp(f.Type)}& {f.Name} = *{f.Name}_ptr;");
                }
                sb.AppendLine("    const int __endIndex = __startIndex + __count;");
                sb.AppendLine("    for (int __chunkIndex = __startIndex; __chunkIndex < __endIndex; ++__chunkIndex)");
                sb.AppendLine("    {");
                sb.AppendLine("        auto* __chunkData = &__chunks[__chunkIndex];");
                // inline the adapter body into range loop
                if (isEntityJob)
                {
                    // IJobEntity：走轻量 ChunkData 路径（转换 ChunkJobData → ChunkData）
                    sb.AppendLine("        ChunkData __chunkDataLite;");
                    sb.AppendLine("        __chunkDataLite.componentArrays = __chunkData->requiredComponentArrays;");
                    sb.AppendLine("        __chunkDataLite.entityCount = __chunkData->entityCount;");
                    sb.AppendLine("        __chunkDataLite.requiredComponentCount = __chunkData->requiredComponentCount;");
                    sb.AppendLine("        __chunkDataLite.enableBitMaps = __chunkData->requiredEnableBitMaps != nullptr ? __chunkData->requiredEnableBitMaps : __chunkData->enableBitMaps;   // 逐组件 enable 位图");
                    sb.AppendLine("        __chunkDataLite.enableBitmapCount = __chunkData->requiredEnableBitMaps != nullptr ? __chunkData->requiredComponentCount : __chunkData->componentCount;");
                    // 仅当 Execute 声明 Entity 参数时才传实体数组
                    if (executeMethod.Parameters.Any(p => NativeTranspiler.IsEntityType(p.Type)))
                        sb.AppendLine("        __chunkDataLite.entityArray = __chunkData->entityArray;");
                    string funcName = GetCppJobFunctionName(jobStruct);
                    string fieldArgs = BuildChunkExecuteFieldArgs(jobStruct);
                    string rangeCallArgs = string.IsNullOrEmpty(fieldArgs)
                        ? $"&__chunkDataLite"
                        : $"&__chunkDataLite, {fieldArgs}";
                    sb.AppendLine($"        {funcName}({rangeCallArgs});");
                }
                else
                {
                    // IJobChunk: Range adapter inline Execute body
                    if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                    {
                        string funcName = GetCppJobFunctionName(jobStruct);
                        string fieldArgs = BuildChunkExecuteFieldArgs(jobStruct);
                        // usesSendEvent：Execute 签名多一个 __header 参数（SendEvent 需要）
                        bool rangeUsesSendEvent = JobUsesSendEvent(jobStruct, compilation);
                        string headerArg = rangeUsesSendEvent ? ", __header" : "";
                        string rangeCallArgs = string.IsNullOrEmpty(fieldArgs)
                            ? $"&__chunks[__chunkIndex], __requiredComponentTypeIds{headerArg}"
                            : $"&__chunks[__chunkIndex], __requiredComponentTypeIds, {fieldArgs}{headerArg}";
                        sb.AppendLine($"        {funcName}({rangeCallArgs});");
                    }
                    else
                    {
                        if (methodSyntax?.Body != null)
                        {
                            var sm = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                            var rt = CollectChunkNativeArrayTypes(jobStruct, compilation);
                            var st = CollectSharedComponentTypes(jobStruct, compilation);
                            var tr = new CppChunkStatementTranslator(sm, jobStruct, rt, st, useFastMath);
                            foreach (var l in tr.Translate(methodSyntax.Body).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                                if (l.Length > 0) sb.Append("        ").AppendLine(l);
                        }
                    }
                }
                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{rangeAdapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){rangeAdapterFuncName};");
                sb.AppendLine("}");

                // Unity 风格 EntityBatch 适配器（IJobChunk 专用，IJobEntity 已走 ChunkRangeRaw）
                // EntityBatchAdapter 无法支持 shared components（shared 值是 per-chunk 的，
                // ISPC jobs: ISPC wrapper (IspcGenerator) 已生成 EntityBatch 函数，跳过 C++ 适配器。
                var sharedTypesForBatch = CollectSharedComponentTypes(jobStruct, compilation);
                if (!isEntityJob && sharedTypesForBatch.Count == 0 && !isIspcJob)
                {
                // 接收 EntityBatchData* 而非 ChunkJobData*，消除 requiredComponentArrays 指针追访
                // EntityBatchData 只含 componentArrays + entityCount，共 16 字节
                // 比 ChunkJobData（72 字节）更紧凑，cache 效率更高
                var entityBatchAdapterFuncName = GetEntityBatchAdapterFunctionName(jobStruct);
                var entityBatchHeader = $@"GENERATED_API void CALLINGCONVENTION {entityBatchAdapterFuncName}(void* context, const EntityBatchData* __batches, int __startIndex, int __count)
{{
    auto* __header = (__EntJoyChunkContextHeader*)context;
    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);
    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);
    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);
    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;";
                sb.Append(entityBatchHeader);
                sb.AppendLine();
                // job field 指针（从 RangeAdapter 复制）
                rOff = 0;
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    int off = CalculateFieldOffset(f, ref rOff);
                    if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type))
                    {
                        if (NativeTranspiler.IsEntJoyContainerNamed(f.Type, Config.NativeList))
                            sb.AppendLine($"    auto* {f.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(f.Type)}>**)(__jobContext + {off});");
                        else
                        {
                            var e = GetCppElementType(f.Type);
                            sb.AppendLine($"    auto* {f.Name}_ptr = *({e}**)(__jobContext + {off});");
                            sb.AppendLine($"    int {f.Name}_length = *(int*)(__jobContext + {off + 8});");
                        }
                    }
                    else if (f.Type is IPointerTypeSymbol)
                    {
                        var t = NativeTranspiler.MapCSharpTypeToCpp(f.Type);
                        sb.AppendLine($"    auto* {f.Name}_ptr = *({t}*)(__jobContext + {off});");
                    }
                    else
                    {
                        var t = NativeTranspiler.MapCSharpTypeToCpp(f.Type);
                        sb.AppendLine($"    auto* {f.Name}_ptr = ({t}*)(__jobContext + {off});");
                    }
                }
                // field refs
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    if (NativeTranspiler.IsEntJoyContainerNamed(f.Type, Config.NativeList))
                    { var e = ((INamedTypeSymbol)f.Type).TypeArguments[0]; var c = NativeTranspiler.MapCSharpTypeToCpp(e); sb.AppendLine($"    EntJoy::Collections::UnsafeList<{c}>& {f.Name} = *{f.Name}_listData;"); }
                foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    if (NativeTranspiler.IsEntJoyNativeContainerType(f.Type)) continue;
                    if (f.Type is IPointerTypeSymbol) continue;
                    sb.AppendLine($"    const {NativeTranspiler.MapCSharpTypeToCpp(f.Type)}& {f.Name} = *{f.Name}_ptr;");
                }
                sb.AppendLine("    const int __endIndex = __startIndex + __count;");
                sb.AppendLine("    for (int __batchIndex = __startIndex; __batchIndex < __endIndex; ++__batchIndex)");
                sb.AppendLine("    {");
                sb.AppendLine("        const EntityBatchData* __batchData = &__batches[__batchIndex];");

                if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled && methodSyntax?.Body != null)
                {
                    // AutoSIMD: SimdControlFlowGenerator 真 SIMD
                    // 与 GenerateChunkFunctionSIMD 相同的预处理（PreprocessIJobChunkAST 内含
                    // SIMD 生成写入临时 simdSb，成功后才 append 到 sb —— 失败时无半截代码残留。
                    var sm = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                    var simdSb = new StringBuilder();
                    try
                    {
                        // SendEvent 无法在 SimdControlFlowGenerator 中翻译 → throw 走标量回退
                        if (JobUsesSendEvent(jobStruct, compilation))
                            throw new InvalidOperationException("EntityBatch AutoSIMD: SendEvent not supported in SimdControlFlowGenerator, falling back to scalar.");
                        var (chunkArrays, entityLoopIv, modifiedBody) =
                            PreprocessIJobChunkAST(methodSyntax, sm, jobStruct, compilation);

                        if (string.IsNullOrEmpty(entityLoopIv) || chunkArrays.Count == 0)
                            throw new InvalidOperationException("EntityBatch AutoSIMD: no component arrays or entity loop found.");

                        // 组件数组指针声明（batch 级）
                        foreach (var (name, elemType, compIdx) in chunkArrays)
                        {
                            simdSb.AppendLine($"        auto* {name}_ptr = reinterpret_cast<{elemType}*>(__batchData->componentArrays[{compIdx}]);");
                            simdSb.AppendLine($"        (void)((intptr_t){name}_ptr % 64 == 0);");
                            simdSb.AppendLine($"        int {name}_length = __batchData->entityCount;");
                        }
                        simdSb.AppendLine("        int __entity_count = __batchData->entityCount;");

                        // 假方法（加 entityIdx 参数）供 SimdVariableAnalyzer
                        var newParams = methodSyntax.ParameterList.AddParameters(
                            SyntaxFactory.Parameter(SyntaxFactory.Identifier(entityLoopIv))
                                .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))));
                        var fakeMethod = methodSyntax.WithParameterList(newParams).WithBody(modifiedBody);

                        var varAnalyzer = new SimdVariableAnalyzer(sm, jobStruct, entityLoopIv);
                        var variables = varAnalyzer.Analyze(fakeMethod);
                        var nativeArrayParams = BuildChunkArrayNativeArrayParams(chunkArrays);
                        var _attrSym = AttributeHelper.GetAttributeSymbol(compilation);
                        var simdMathPrecision = _attrSym != null ? AttributeHelper.GetMathPrecision(jobStruct, _attrSym) : NativeTranspiler.SimdMathPrecision.Fastest;

                        // SIMD batch loop
                        simdSb.AppendLine("        int __simd_end = (__entity_count / NSIMD_WIDTH) * NSIMD_WIDTH;");
                        simdSb.AppendLine("        if (__simd_end > 0)");
                        simdSb.AppendLine("        {");
                        simdSb.AppendLine("            simd_value<int> v_base = simd_value<int>::sequence(0);");
                        simdSb.AppendLine("            for (int si = 0; si < __simd_end; si += NSIMD_WIDTH)");
                        simdSb.AppendLine("            {");
                        simdSb.AppendLine("                simd_value<int> v_i = v_base + si;");

                        var simdGen = new SimdControlFlowGenerator(
                            sm, jobStruct, variables, varAnalyzer,
                            indexParamName: entityLoopIv,
                            simdIndexVar: "v_i",
                            // NT-01(b)：偏移必须是外层批循环的当前位置 `si`（v_i = v_base + si）。
                            // 旧值 "0" 让每个 simd 组都重写 [0,W) ⇒ 元素 ≥W 永远写不到（静默错值）。
                            batchOffsetVar: "si",
                            batchLoopVar: "",
                            nativeArrayParams: nativeArrayParams,
                            simdMathPrecision: simdMathPrecision);
                        string simdBody = simdGen.Generate(modifiedBody);
                        foreach (var line in simdBody.Split('\n'))
                            if (!string.IsNullOrWhiteSpace(line))
                                simdSb.AppendLine($"                {line.TrimEnd()}");

                        simdSb.AppendLine("                __simd_exit: ;");
                        simdSb.AppendLine("            }");
                        simdSb.AppendLine("        }");

                        // 标量余量循环（__chunkData → __batchData 替换）
                        var remTr = new CppChunkStatementTranslator(sm, jobStruct,
                            CollectChunkNativeArrayTypes(jobStruct, compilation),
                            CollectSharedComponentTypes(jobStruct, compilation), useFastMath);
                        string remBody = remTr.Translate(methodSyntax.Body);
                        remBody = remBody.Replace("__chunkData->requiredComponentArrays", "__batchData->componentArrays");
                        remBody = remBody.Replace("__chunkData->requiredEnableBitMaps", "__batchData->enableBitMaps");
                        remBody = remBody.Replace("__chunkData->entityCount", "__batchData->entityCount");
                        // 移除 SIMD prelude 已声明的 ptr/length/entityCount
                        remBody = Regex.Replace(remBody, @"auto\* \w+_ptr = reinterpret_cast<[^>]+>\(__batchData->componentArrays\[\d+\]\);\r?\n?", "");
                        remBody = Regex.Replace(remBody, @"int \w+_length = __batchData->entityCount;\r?\n?", "");
                        remBody = Regex.Replace(remBody, @"int __entity_count = __batchData->entityCount;\r?\n?", "");
                        // 实体循环从 __simd_end 开始
                        string loopPattern = $"for (int {entityLoopIv} = 0; {entityLoopIv} <";
                        string loopReplacement = $"for (int {entityLoopIv} = __simd_end; {entityLoopIv} <";
                        remBody = remBody.Replace(loopPattern, loopReplacement);
                        foreach (var l in remBody.Split('\n'))
                            if (!string.IsNullOrWhiteSpace(l))
                                simdSb.AppendLine($"        {l.TrimEnd()}");

                        // 全部成功 → 合并到 sb
                        sb.Append(simdSb);
                    }
                    catch
                    {
                        // SIMD 生成失败 → 回退 per-lane 标量（simdSb 丢弃，无半截残留）。
                        // 此处直接输出即可，不得再包外层 __entity_index 循环——否则双重循环，
                        // 每个实体的副作用（如 SendEvent）被执行 entity_count 次
                        var fbTr = new CppChunkStatementTranslator(sm, jobStruct,
                            CollectChunkNativeArrayTypes(jobStruct, compilation),
                            CollectSharedComponentTypes(jobStruct, compilation), useFastMath);
                        string fbBody = fbTr.Translate(methodSyntax.Body);
                        fbBody = fbBody.Replace("__chunkData->requiredComponentArrays", "__batchData->componentArrays");
                        fbBody = fbBody.Replace("__chunkData->requiredEnableBitMaps", "__batchData->enableBitMaps");
                        fbBody = fbBody.Replace("__chunkData->entityCount", "__batchData->entityCount");
                        foreach (var l in fbBody.Split('\n'))
                            if (!string.IsNullOrWhiteSpace(l))
                                sb.AppendLine($"        {l.TrimEnd()}");
                    }
                }
                else if (methodSyntax?.Body != null)
                {
                    // 标量路径（原有逻辑）
                    var sm = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                    var rt = CollectChunkNativeArrayTypes(jobStruct, compilation);
                    var st = CollectSharedComponentTypes(jobStruct, compilation);
                    var tr = new CppChunkStatementTranslator(sm, jobStruct, rt, st, useFastMath);
                    var bodyCode = tr.Translate(methodSyntax.Body);
                    bodyCode = bodyCode.Replace("__chunkData->requiredComponentArrays", "__batchData->componentArrays");
                    bodyCode = bodyCode.Replace("__chunkData->requiredEnableBitMaps", "__batchData->enableBitMaps");
                    bodyCode = bodyCode.Replace("__chunkData->entityCount", "__batchData->entityCount");
                    foreach (var l in bodyCode.Split(new[] { "\n" }, StringSplitOptions.None))
                    {
                        if (l.Length > 0) sb.Append("        ").AppendLine(l);
                        string trimmed = l.TrimStart();
                        if (trimmed.StartsWith("auto*") && trimmed.Contains("reinterpret_cast<"))
                        {
                            string varName = trimmed.Split('=')[0].Trim().Split(' ').Last();
                            sb.AppendLine($"        (void)((intptr_t){varName} % 64 == 0);");
                        }
                    }
                }
                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{entityBatchAdapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){entityBatchAdapterFuncName};");
                sb.AppendLine("}");

                } // end if (!isEntityJob)

                }
            }
            else if (isParallelFor)
            {
                var boolFields = GetBoolConditionalFields(jobStruct, compilation);

                // 生成适配函数
                // 纯值字段打包：结构体定义必须在本 TU 可见（适配器是独立 TU）⇒ 放在函数之前。
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {adapterFuncName}(void* context, int __startIndex, int __count)");
                sb.AppendLine("{");
                
                // 生成字段读取代码
                // 抽成 BuildAdapterFieldAccess 复用 —— 批形 adapter
                // 与（IJobFor 专用的）index 形 adapter 的字段解包逐字相同，只是前缀实参不同。
                var fieldReads = new StringBuilder();
                var callArgs = new List<string> { "__startIndex", "__count" };
                BuildAdapterFieldAccess(jobStruct, fieldReads, callArgs);
                sb.Append(fieldReads);
                sb.AppendLine();

                // 调用 Batch 函数（根据所有 bool 条件字段的值选择变体）
                string batchFuncName = GetCppJobFunctionName(jobStruct, isBatch: true);
                if (boolFields.Count > 0)
                {
                    // 读取所有 bool 字段的值
                    var boolValues = new List<string>();
                    foreach (var bf in boolFields)
                    {
                        int boolOffset = GetBoolFieldOffset(jobStruct, bf.Name);
                        string varName = $"__{bf.Name}";
                        sb.AppendLine($"    bool {varName} = *(bool*)((char*)context + {boolOffset});");
                        boolValues.Add(varName);
                    }
                    sb.AppendLine();

                    // 使用 if-else 链选择正确的变体
                    // 生成 2^n 个 if-else 分支
                    int totalVariants = 1 << boolFields.Count;
                    for (int mask = 0; mask < totalVariants; mask++)
                    {
                        var values = new List<bool>();
                        for (int i = 0; i < boolFields.Count; i++)
                            values.Add((mask & (1 << i)) != 0);
                        
                        string suffix = BuildBoolVariantSuffix(boolFields, values);
                        string condition = string.Join(" && ", boolValues.Select((v, i) => values[i] ? v : $"!{v}"));
                        
                        if (mask == 0)
                            sb.AppendLine($"    if ({condition})");
                        else if (mask == totalVariants - 1)
                            sb.AppendLine("    else");
                        else
                            sb.AppendLine($"    else if ({condition})");
                        
                        sb.AppendLine($"        {batchFuncName}{suffix}({string.Join(", ", callArgs)});");
                    }
                }
                else
                {
                    sb.AppendLine($"    {batchFuncName}({string.Join(", ", callArgs)});");
                }
                
                sb.AppendLine("}");
                sb.AppendLine();

                // 生成 Get_XXX_AdapterPtr 导出函数
                sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{adapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){adapterFuncName};");
                sb.AppendLine("}");

                // `IJobFor` 专用的 index 形 adapter
                // 动机：`IJobFor` 的调度语义是单线程串行（原生 `Scheduler::ScheduleFor`：
                // 做法：复用同一套字段解包（BuildAdapterFieldAccess），只把前缀实参换成 `(__index, 1)`
                //   调同一个批内核（内核本就按 [start, start+count) 循环 ⇒ count=1 即"只跑该 index"；
                //   两者同在一个 unity TU 里，会被内联）。
                // 只为 `IJobFor` 发射；`IJobParallelFor`/`Batch` 没有 index 形入口，不需要。
                if (IsForJob(jobStruct))
                {
                    string indexAdapterFuncName = GetIndexAdapterFunctionName(jobStruct);
                    sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {indexAdapterFuncName}(void* context, int __index)");
                    sb.AppendLine("{");
                    var idxFieldReads = new StringBuilder();
                    var idxCallArgs = new List<string> { "__index", "1" };
                    BuildAdapterFieldAccess(jobStruct, idxFieldReads, idxCallArgs);
                    sb.Append(idxFieldReads);
                    sb.AppendLine();
                    sb.AppendLine($"    {GetCppJobFunctionName(jobStruct, isBatch: true)}({string.Join(", ", idxCallArgs)});");
                    sb.AppendLine("}");
                    sb.AppendLine();
                    sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{indexAdapterFuncName}Ptr()");
                    sb.AppendLine("{");
                    sb.AppendLine($"    return (void*){indexAdapterFuncName};");
                    sb.AppendLine("}");
                }
            }
            else
            {
                // IJob（非 ParallelFor）：适配函数签名匹配 JobFunc(void* context)
                // 同样生成适配函数
                sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {adapterFuncName}(void* context)");
                sb.AppendLine("{");
                
                var fieldReads = new StringBuilder();
                var callArgs = new List<string>();
                int currentOffset = 0;
                
                foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    int offset = CalculateFieldOffset(field, ref currentOffset);
                    
                    if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                    {
                        if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                        {
                            fieldReads.AppendLine($"    auto* {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{GetCppElementType(field.Type)}>**)((char*)context + {offset});");
                            callArgs.Add($"{field.Name}_listData");
                        }
                        else
                        {
                            var cppElemType = GetCppElementType(field.Type);
                            fieldReads.AppendLine($"    auto* {field.Name}_ptr = *({cppElemType}**)((char*)context + {offset});");
                            fieldReads.AppendLine($"    int {field.Name}_length = *(int*)((char*)context + {offset + 8});");
                            callArgs.Add($"{field.Name}_ptr, {field.Name}_length");
                        }
                    }
                    else if (field.Type is IPointerTypeSymbol)
                    {
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                        fieldReads.AppendLine($"    auto* {field.Name}_ptr = *({cppType}*)((char*)context + {offset});");
                        callArgs.Add($"{field.Name}_ptr");
                    }
                    else
                    {
                        var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                        fieldReads.AppendLine($"    auto* {field.Name}_ptr = ({cppType}*)((char*)context + {offset});");
                        callArgs.Add($"{field.Name}_ptr");
                    }
                }

                sb.Append(fieldReads);
                sb.AppendLine();
                
                string singleFuncName = GetCppJobFunctionName(jobStruct);
                sb.AppendLine($"    {singleFuncName}({string.Join(", ", callArgs)});");
                sb.AppendLine("}");
                sb.AppendLine();

                // 生成 Get_XXX_AdapterPtr 导出函数
                sb.AppendLine($"GENERATED_API void* CALLINGCONVENTION Get_{adapterFuncName}Ptr()");
                sb.AppendLine("{");
                sb.AppendLine($"    return (void*){adapterFuncName};");
                sb.AppendLine("}");

                // SendEvent: 事件类型查询导出函数
                if (discoveredEventTypes.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"// Event Buffer metadata ({discoveredEventTypes.Count} types)");
                    sb.AppendLine($"GENERATED_API int CALLINGCONVENTION Get_{baseFuncName}_EventBufferCount()");
                    sb.AppendLine("{");
                    sb.AppendLine($"    return {discoveredEventTypes.Count};");
                    sb.AppendLine("}");
                    sb.AppendLine();
                    sb.AppendLine($"GENERATED_API int CALLINGCONVENTION Get_{baseFuncName}_EventBufferElementSize(int index)");
                    sb.AppendLine("{");
                    for (int ei = 0; ei < discoveredEventTypes.Count; ei++)
                    {
                        string cppType = NativeTranspiler.MapCSharpTypeToCpp(
                            compilation.GetTypeByMetadataName(discoveredEventTypes[ei]));
                        sb.AppendLine($"    if (index == {ei}) return sizeof({cppType});");
                    }
                    sb.AppendLine("    return 0;");
                    sb.AppendLine("}");
                }
            }

            return (sb.ToString(), discoveredEventTypes);
        }

        // IJobChunk Auto-SIMD: Preprocess AST

        /// <summary>
        /// 预处理 IJobChunk 的 Execute AST 用于 SIMD 生成：
        /// 1. 收集 chunk 数组声明（GetComponentDataNativeArray/GetComponentDataSpan）
        /// 3. SyntaxRewriter: 删除 chunk 数组声明、删除实体 for-loop 头、替换 chunk.Count
        /// </summary>
        private static (List<(string name, string elemType, int compIndex)> chunkArrays,
                        string entityLoopIv,
                        BlockSyntax modifiedBody)
            PreprocessIJobChunkAST(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel,
                                   INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var chunkArrays = new List<(string name, string elemType, int compIndex)>();
            var chunkArrayNames = new HashSet<string>();
            string chunkParamName = methodSyntax.ParameterList.Parameters.Count > 0
                ? methodSyntax.ParameterList.Parameters[0].Identifier.Text
                : "chunk";

            // 1. Scan for GetComponentDataNativeArray / GetComponentDataSpan calls
            var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
            foreach (var localDecl in methodSyntax.Body?.DescendantNodes().OfType<LocalDeclarationStatementSyntax>() ?? Enumerable.Empty<LocalDeclarationStatementSyntax>())
            {
                foreach (var variable in localDecl.Declaration.Variables)
                {
                    if (variable.Initializer?.Value is InvocationExpressionSyntax inv)
                    {
                        var symbol = semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                        if (symbol != null &&
                            symbol.ContainingType?.ToDisplayString() == Config.TypeArchetypeChunk &&
                            (symbol.Name == Config.GetComponentDataNativeArray || symbol.Name == Config.GetComponentDataSpan) &&
                            symbol.TypeArguments.Length > 0)
                        {
                            string varName = variable.Identifier.Text;
                            var compType = symbol.TypeArguments[0] as INamedTypeSymbol;
                            if (compType != null)
                            {
                                int idx = requiredTypes.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, compType));
                                string elemCppType = NativeTranspiler.MapCSharpTypeToCpp(compType);
                                chunkArrays.Add((varName, elemCppType, idx));
                                chunkArrayNames.Add(varName);
                            }
                        }
                    }
                }
            }

            // 2. Find entity for-loop
            string entityLoopIv = "";
            StatementSyntax? loopBody = null;
            if (methodSyntax.Body != null)
            {
                foreach (var stmt in methodSyntax.Body.Statements)
                {
                    if (stmt is ForStatementSyntax forStmt &&
                        forStmt.Declaration?.Variables.Count == 1)
                    {
                        var decl = forStmt.Declaration.Variables[0];
                        string ivName = decl.Identifier.Text;

                        // Check if this loop's body has element access on chunk arrays
                        bool hasChunkAccess = forStmt.DescendantNodes()
                            .OfType<ElementAccessExpressionSyntax>()
                            .Any(ea => ea.Expression is IdentifierNameSyntax id
                                      && chunkArrayNames.Contains(id.Identifier.Text));

                        if (hasChunkAccess)
                        {
                            entityLoopIv = ivName;
                            loopBody = forStmt.Statement;
                            break;
                        }
                    }
                }
            }

            // 3. Apply SyntaxRewriter (remove chunk decls, for-loop header, replace chunk.Count)
            var rewriter = new IJobChunkSimdRewriter(chunkArrayNames, chunkParamName, entityLoopIv);
            var afterFirstPass = methodSyntax.Body != null
                ? (BlockSyntax)rewriter.Visit(methodSyntax.Body)
                : methodSyntax.Body;

            // 4. Decompose struct read-modify-write pattern (ISPC-style: eliminate intermediate struct locals)
            //    Detects: StructType temp = array[idx]; temp.Field += ...; array[idx] = temp;
            //    Rewrites to: array[idx].Field += ...;
            var decomposedBody = DecomposeStructLocals(afterFirstPass!, chunkArrayNames, entityLoopIv);

            return (chunkArrays, entityLoopIv, decomposedBody);
        }

        /// <summary>
        /// Decompose struct read-modify-write pattern into direct field access.
        /// Replaces:
        /// StructType temp = array[idx]; → removed
        /// temp.Field += rhs; → array[idx].Field += rhs
        /// array[idx] = temp; → removed
        /// This enables SimdControlFlowGenerator to handle struct field access
        /// directly via field-level gather/scatter (ISPC-style).
        /// </summary>
        private static BlockSyntax DecomposeStructLocals(BlockSyntax body, HashSet<string> chunkArrayNames, string entityLoopIv)
        {
            if (body == null) return body;

            // First, flatten any nested blocks (e.g., from for-loop body extraction)
            var flatStatements = FlattenBlockStatements(body);

            var newStatements = new List<StatementSyntax>();
            int i = 0;
            var statements = flatStatements.ToArray();

            while (i < statements.Length)
            {
                var stmt = statements[i];

                // Detect: StructType temp = array[idx]; (local declaration with element access initializer)
                if (stmt is LocalDeclarationStatementSyntax localDecl
                    && localDecl.Declaration.Variables.Count == 1)
                {
                    var varDecl = localDecl.Declaration.Variables[0];
                    string tempName = varDecl.Identifier.Text;

                    if (varDecl.Initializer?.Value is ElementAccessExpressionSyntax initEA
                        && initEA.Expression is IdentifierNameSyntax initArrId
                        && chunkArrayNames.Contains(initArrId.Identifier.Text))
                    {
                        string arrName = initArrId.Identifier.Text;
                        string idxText = initEA.ArgumentList?.Arguments.Count > 0
                            ? initEA.ArgumentList.Arguments[0].ToString()
                            : "0";

                        // Find write-back: array[idx] = tempName (within next few statements)
                        int writeBackIdx = -1;
                        for (int j = i + 1; j < statements.Length; j++)
                        {
                            if (statements[j] is ExpressionStatementSyntax es
                                && es.Expression is AssignmentExpressionSyntax ae
                                && ae.IsKind(SyntaxKind.SimpleAssignmentExpression)
                                && ae.Left is ElementAccessExpressionSyntax wbEA
                                && wbEA.Expression is IdentifierNameSyntax wbArrId
                                && wbArrId.Identifier.Text == arrName
                                && wbEA.ArgumentList?.Arguments.Count > 0
                                && wbEA.ArgumentList.Arguments[0].ToString() == idxText
                                && ae.Right is IdentifierNameSyntax rhsId
                                && rhsId.Identifier.Text == tempName)
                            {
                                writeBackIdx = j;
                                break;
                            }
                        }

                        if (writeBackIdx >= 0)
                        {
                            // Rewrite mutation statements between decl and write-back.
                            for (int k = i + 1; k < writeBackIdx; k++)
                            {
                                var mutationStmt = statements[k];
                                var rewritten = RewriteTempFieldRefs(mutationStmt, tempName, arrName, idxText);
                                if (rewritten != null)
                                    newStatements.Add(rewritten);
                            }
                            i = writeBackIdx + 1;
                            continue;
                        }

                    }
                }

                newStatements.Add(stmt);
                i++;
            }

            return SyntaxFactory.Block(newStatements);
        }

        /// <summary>
        /// Flatten nested blocks into a single list of statements.
        /// </summary>
        private static List<StatementSyntax> FlattenBlockStatements(BlockSyntax block)
        {
            var result = new List<StatementSyntax>();
            foreach (var stmt in block.Statements)
            {
                if (stmt is BlockSyntax nestedBlock)
                    result.AddRange(FlattenBlockStatements(nestedBlock));
                else
                    result.Add(stmt);
            }
            return result;
        }

        /// <summary>
        /// Replace tempName.Field with arrName[idxText].Field in a statement.
        /// Returns null if no replacement needed (keep original).
        /// </summary>
        private static StatementSyntax? RewriteTempFieldRefs(StatementSyntax stmt, string tempName, string arrName, string idxText)
        {
            // Build the replacement expression: arrName[idxText]
            var arrayAccess = SyntaxFactory.ElementAccessExpression(
                SyntaxFactory.IdentifierName(arrName))
                .WithArgumentList(SyntaxFactory.BracketedArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(SyntaxFactory.ParseExpression(idxText)))));

            // Walk the statement tree and replace tempName.Field with arrName[idxText].Field
            var rewriter = new TempFieldRewriter(tempName, arrayAccess);
            return (StatementSyntax)rewriter.Visit(stmt);
        }

        /// <summary>Rewriter that replaces tempName.Field with arrExpr.Field in member access expressions.</summary>
        private sealed class TempFieldRewriter : CSharpSyntaxRewriter
        {
            private readonly string _tempName;
            private readonly ExpressionSyntax _replacementExpr;

            public TempFieldRewriter(string tempName, ExpressionSyntax replacementExpr)
            {
                _tempName = tempName;
                _replacementExpr = replacementExpr;
            }

            public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                // tempName.Field → replacementExpr.Field
                if (node.Expression is IdentifierNameSyntax id
                    && id.Identifier.Text == _tempName)
                {
                    return SyntaxFactory.MemberAccessExpression(
                        node.Kind(),
                        _replacementExpr,
                        node.Name)
                        .WithTriviaFrom(node);
                }
                return base.VisitMemberAccessExpression(node);
            }
        }

        /// <summary>
        /// ISPC-style struct field decomposition rewriter.
        /// Detects the read-modify-write pattern on struct locals from chunk arrays:
        /// StructType temp = array[idx]; // local copy
        /// temp.Field += ...; // field mutation
        /// array[idx] = temp; // write back
        /// Rewrites to direct field access:
        /// array[idx].Field += ...;
        /// This enables SimdControlFlowGenerator to handle struct field access
        /// via n_gather_ps<sizeof(T)> with struct stride (matching ISPC behavior).
        /// </summary>
        

        /// <summary>
        /// SyntaxRewriter for IJobChunk SIMD preprocessing:
        /// - Removes chunk array local declarations
        /// - Replaces entity for-loop with its body (keeps body, removes for-header)
        /// </summary>
        private sealed class IJobChunkSimdRewriter : CSharpSyntaxRewriter
        {
            private readonly HashSet<string> _chunkArrayNames;
            private readonly string _chunkParamName;
            private readonly string _entityLoopIvName;

            public IJobChunkSimdRewriter(HashSet<string> chunkArrayNames, string chunkParamName, string entityLoopIvName)
            {
                _chunkArrayNames = chunkArrayNames;
                _chunkParamName = chunkParamName;
                _entityLoopIvName = entityLoopIvName;
            }

            public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
            {
                foreach (var variable in node.Declaration.Variables)
                {
                    if (_chunkArrayNames.Contains(variable.Identifier.Text))
                        return null; // Remove: this is a chunk array declaration
                }
                return base.VisitLocalDeclarationStatement(node);
            }

            public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                // chunk.Count → __entityCount
                if (node.Name.Identifier.Text == "Count"
                    && node.Expression is IdentifierNameSyntax id
                    && id.Identifier.Text == _chunkParamName)
                {
                    return SyntaxFactory.IdentifierName("__entityCount");
                }
                return base.VisitMemberAccessExpression(node);
            }

            public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
            {
                if (node.Declaration?.Variables.Count == 1)
                {
                    string ivName = node.Declaration.Variables[0].Identifier.Text;
                    if (ivName == _entityLoopIvName)
                    {
                        // Remove for-header, keep body
                        return node.Statement;
                    }
                }
                return base.VisitForStatement(node);
            }
        }

        /// <summary>
        /// 从 chunkArrayInfo 构建 _nativeArrayParams 字典
        /// (SimdControlFlowGenerator 用这个来识别 NativeArray 访问)
        /// </summary>
        private static Dictionary<string, string> BuildChunkArrayNativeArrayParams(
            List<(string name, string elemType, int compIndex)> chunkArrays)
        {
            var result = new Dictionary<string, string>();
            foreach (var (name, elemType, _) in chunkArrays)
                result[name] = elemType;
            return result;
        }

        // IJobChunk Auto-SIMD: Generate SIMD Code

        /// <summary>
        /// 生成 IJobChunk 的 Register-Level SIMD Execute 函数体。
        /// 流程：
        /// 1. 生成 C++ prelude（_ptr / _length 声明）
        /// 2. 生成外层 batch loop（for si; v_i = v_base + si）
        /// 3. SimdControlFlowGenerator on 修改后的 body（无 for-loop 头）
        /// 4. 标量 remainder 循环
        /// </summary>
        private static void GenerateChunkFunctionSIMD(
            INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb,
            bool useFastMath, NativeTranspiler.SimdMathPrecision simdMathPrecision)
        {
            // Output function signature (same as GenerateChunkFunctionStandard)
            bool usesSendEvent = JobUsesSendEvent(jobStruct, compilation);
            var chunkParams = BuildChunkJobParameters(jobStruct, includeHeader: usesSendEvent);
            var singleFuncName = GetCppJobFunctionName(jobStruct);
            sb.AppendLine($"GENERATED_API void CALLINGCONVENTION {singleFuncName}({chunkParams})");
            sb.AppendLine("{");
            AppendLocalVariableDeclarations(jobStruct, sb, compilation: compilation);

            try
            {
                var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
                var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
                if (methodSyntax?.Body == null) return;
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

                // 1. Preprocess AST
                var (chunkArrays, entityLoopIv, modifiedBody) =
                    PreprocessIJobChunkAST(methodSyntax, semanticModel, jobStruct, compilation);

                if (string.IsNullOrEmpty(entityLoopIv) || chunkArrays.Count == 0 || usesSendEvent)
                {
                    // Fallback: use scalar translator
                    // （usesSendEvent：SendEvent 无法在 SimdControlFlowGenerator 中翻译，
                    //   必须用 CppChunkStatementTranslator 标量路径，它完整支持 SendEvent 拦截）
                    var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
                    var sharedTypes = CollectSharedComponentTypes(jobStruct, compilation);
                    var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, sharedTypes, useFastMath);
                    sb.Append(translator.Translate(methodSyntax.Body));
                    // 闭合函数体（fallback 提前 return，需手动补结尾括号）
                    sb.AppendLine("}");
                    return;
                }

                                // 2. Generate C++ prelude
                foreach (var (name, elemType, compIdx) in chunkArrays)
                {
                    sb.AppendLine($"    auto* {name}_ptr = reinterpret_cast<{elemType}*>(__chunkData->requiredComponentArrays[{compIdx}]);");
                    sb.AppendLine($"    int {name}_length = __chunkData->entityCount;");
                }
                sb.AppendLine("    int __entityCount = __chunkData->entityCount;");

                // 3. Build fake method with virtual int i parameter
                var newParamList = methodSyntax.ParameterList.AddParameters(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier(entityLoopIv))
                        .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))));
                var fakeMethod = methodSyntax.WithParameterList(newParamList).WithBody(modifiedBody);

                // 4. Variable analysis
                var varAnalyzer = new SimdVariableAnalyzer(semanticModel, jobStruct, entityLoopIv);
                var variables = varAnalyzer.Analyze(fakeMethod);
                var nativeArrayParams = BuildChunkArrayNativeArrayParams(chunkArrays);

                                // 5. Generate SIMD batch loop
                sb.AppendLine("    int __simd_end = (__entityCount / NSIMD_WIDTH) * NSIMD_WIDTH;");
                sb.AppendLine("    if (__simd_end > 0)");
                sb.AppendLine("    {");
                sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0);");
                sb.AppendLine("        for (int si = 0; si < __simd_end; si += NSIMD_WIDTH)");
                sb.AppendLine("        {");
                sb.AppendLine("            simd_value<int> v_i = v_base + si;");

            // 6. SimdControlFlowGenerator on modified body
            var simdGen = new SimdControlFlowGenerator(
                semanticModel, jobStruct, variables, varAnalyzer,
                indexParamName: entityLoopIv,
                simdIndexVar: "v_i",
                // NT-01(b)：同上 —— 连续向量 store 的偏移是外层批循环变量 `si`，不是常量 0。
                batchOffsetVar: "si",
                batchLoopVar: "",
                nativeArrayParams: nativeArrayParams,
                simdMathPrecision: simdMathPrecision);

            string simdBody = simdGen.Generate(modifiedBody);
            foreach (var line in simdBody.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine($"            {line.TrimEnd()}");

            sb.AppendLine("        __simd_exit: ;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            // 7. Scalar remainder loop
            GenerateChunkFunctionRemainder(jobStruct, compilation, sb, useFastMath, chunkArrays, entityLoopIv);
            }
            catch
            {
                // Catch block: gracefully close the function with scalar body
                try
                {
                    sb.AppendLine("        __simd_exit: ;");
                    sb.AppendLine("        }");
                    sb.AppendLine("    }");
                    var em = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
                    var ms = em != null ? SymbolHelper.GetMethodSyntax(em) : null;
                    if (ms?.Body != null)
                    {
                        var sm = compilation.GetSemanticModel(ms.SyntaxTree);
                        var rt = CollectChunkNativeArrayTypes(jobStruct, compilation);
                        var st = CollectSharedComponentTypes(jobStruct, compilation);
                        var tr = new CppChunkStatementTranslator(sm, jobStruct, rt, st, useFastMath);
                        string scalarBody = tr.Translate(ms.Body);
                        // Remove duplicate pointer/length declarations
                        try { scalarBody = Regex.Replace(scalarBody, @"auto\* \w+_ptr = reinterpret_cast<[^>]+>\(__chunkData->requiredComponentArrays\[\d+\]\);\r?\n?", ""); } catch { }
                        try { scalarBody = Regex.Replace(scalarBody, @"int \w+_length = __chunkData->entityCount;\r?\n?", ""); } catch { }
                        try { scalarBody = Regex.Replace(scalarBody, @"int __entityCount = __chunkData->entityCount;\r?\n?", ""); } catch { }
                        scalarBody = scalarBody.Replace("#pragma loop(ivdep)\r\n", "").Replace("#pragma loop(ivdep)\n", "");
                        scalarBody = scalarBody.Replace("#pragma loop(vector)\r\n", "").Replace("#pragma loop(vector)\n", "");
                        scalarBody = scalarBody.Replace("#pragma unroll(4)\r\n", "").Replace("#pragma unroll(4)\n", "");
                        sb.Append(scalarBody);
                    }
                }
                catch (Exception ex)
                {
                    // 此处若不捕获而静默吞掉，语义是"连标量兜底也失败了"——
                    //   再吞就会发射一个没有函数体的 job（静默 do-nothing），比直接失败危险得多
                    //   （生成码能编译、但什么都不做）。故改为显式失败：当前语料里不触发，
                    //   对现有输入零行为变化，只把"将来真走到这里"从静默错变成响亮错。
                    throw new InvalidOperationException(
                        $"[NativeTranspiler] chunk 函数生成失败：SIMD 路径与标量兜底路径都抛异常（job='{jobStruct.Name}'）。" +
                        "原实现会在此静默吞掉并发射一个无函数体的 job，已改为显式失败。", ex);
                }
            }

            sb.AppendLine("}");
        }

        /// <summary>
        /// 生成 IJobChunk SIMD 的 scalar remainder 循环。
        /// 复用 CppChunkStatementTranslator 的标量输出，仅修改实体循环起始值为 __simd_end。
        /// </summary>
        private static void GenerateChunkFunctionRemainder(
            INamedTypeSymbol jobStruct, Compilation compilation, StringBuilder sb,
            bool useFastMath,
            List<(string name, string elemType, int compIndex)> chunkArrays,
            string entityLoopIv)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == Config.Execute);
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return;
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            var requiredTypes = CollectChunkNativeArrayTypes(jobStruct, compilation);
            var sharedTypes = CollectSharedComponentTypes(jobStruct, compilation);
            var translator = new CppChunkStatementTranslator(semanticModel, jobStruct, requiredTypes, sharedTypes, useFastMath);
            string scalarBody = translator.Translate(methodSyntax.Body);

            // Remove prelude declarations (already emitted by SIMD generator)
            foreach (var (name, _, _) in chunkArrays)
            {
                try
                {
                    string ptrDecl = $"auto* {name}_ptr = reinterpret_cast<";
                    int idx = scalarBody.IndexOf(ptrDecl);
                    if (idx >= 0)
                    {
                        int semiEnd = scalarBody.IndexOf(';', idx);
                        if (semiEnd >= 0)
                        {
                            int lineEnd = scalarBody.IndexOf('\n', semiEnd);
                            if (lineEnd >= 0)
                                scalarBody = scalarBody.Remove(idx, lineEnd - idx + 1);
                            else
                                scalarBody = scalarBody.Remove(idx);
                        }
                    }

                    string lenDecl = $"int {name}_length =";
                    idx = scalarBody.IndexOf(lenDecl);
                    if (idx >= 0)
                    {
                        int lineEnd = scalarBody.IndexOf('\n', idx);
                        if (lineEnd >= 0)
                            scalarBody = scalarBody.Remove(idx, lineEnd - idx + 1);
                        else
                            scalarBody = scalarBody.Remove(idx);
                    }
                }
                catch { }
            }

            // Remove __entityCount declaration if present (already emitted)
            string ecDecl = "int __entityCount = ";
            int ecIdx = scalarBody.IndexOf(ecDecl);
            if (ecIdx >= 0)
            {
                int ecEnd = scalarBody.IndexOf('\n', ecIdx);
                if (ecEnd >= 0)
                    scalarBody = scalarBody.Remove(ecIdx, ecEnd - ecIdx + 1);
                else
                    scalarBody = scalarBody.Remove(ecIdx);
            }

            // Remove pragma hints (already in SIMD loop or not needed)
            scalarBody = scalarBody.Replace("#pragma loop(ivdep)\r\n", "");
            scalarBody = scalarBody.Replace("#pragma loop(ivdep)\n", "");
            scalarBody = scalarBody.Replace("#pragma loop(vector)\r\n", "");
            scalarBody = scalarBody.Replace("#pragma loop(vector)\n", "");
            scalarBody = scalarBody.Replace("#pragma unroll(4)\r\n", "");
            scalarBody = scalarBody.Replace("#pragma unroll(4)\n", "");

            // Change entity loop start from 0 to __simd_end
            string loopPattern = $"for (int {entityLoopIv} = 0; {entityLoopIv} <";
            string loopReplacement = $"for (int {entityLoopIv} = __simd_end; {entityLoopIv} <";
            scalarBody = scalarBody.Replace(loopPattern, loopReplacement);

            sb.Append(scalarBody);
        }

        /// <summary>
        /// 生成调用独立 IJobChunk Execute 函数的实参列表。
        /// 用于适配器中替代内联 Execute 体。
        /// </summary>
        private static string BuildChunkExecuteCallArgs(INamedTypeSymbol jobStruct, bool includeTypeIds = true, bool includeHeader = false)
        {
            var fieldArgs = BuildChunkExecuteFieldArgs(jobStruct);
            var parts = new List<string>();
            if (includeTypeIds)
            {
                parts.Add("__chunkData");
                parts.Add("__requiredComponentTypeIds");
            }
            else
            {
                parts.Add("__chunkData");
            }
            if (includeHeader)
                parts.Add("__header");
            if (!string.IsNullOrEmpty(fieldArgs))
                parts.Add(fieldArgs);
            return string.Join(", ", parts);
        }

        /// <summary>
        /// 为 IJobEntity 生成轻量 ChunkData 调用的实参列表。
        /// 用 &amp;__chunkDataLite 替代 __chunkData（ChunkJobData* → ChunkData*），不含 __requiredComponentTypeIds。
        /// </summary>
        private static string BuildLiteChunkExecuteCallArgs(INamedTypeSymbol jobStruct, bool includeHeader = false)
        {
            var fieldArgs = BuildChunkExecuteFieldArgs(jobStruct);
            var parts = new List<string> { "&__chunkDataLite" };
            if (includeHeader)
                parts.Add("__header");
            if (!string.IsNullOrEmpty(fieldArgs))
                parts.Add(fieldArgs);
            return string.Join(", ", parts);
        }

        /// <summary>
        /// 仅生成字段参数部分（不含 __chunkData 和 __requiredComponentTypeIds）
        /// </summary>
        private static string BuildChunkExecuteFieldArgs(INamedTypeSymbol jobStruct)
        {
            var args = new List<string>();
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                        args.Add($"{field.Name}_listData");
                    else
                    {
                        args.Add($"{field.Name}_ptr");
                        args.Add($"{field.Name}_length");
                    }
                }
                else if (field.Type is IPointerTypeSymbol)
                    args.Add($"{field.Name}_ptr");
                else
                    args.Add($"{field.Name}_ptr");
            }
            return string.Join(", ", args);
        }

        /// <summary>
        /// 为 IJobEntity 的实体循环生成 per-lane SIMD 包装代码。
        /// 将 "for (int __entity_index = 0; ... < ... ; ...)" 替换为
        /// per-lane batch + remainder 循环。
        ///
        /// ⚠ 无调用者（历史遗留，勿直接启用）：内部对翻译产物做逐行
        /// `Replace("return;", "break;")`，而 `break;` 在体内内层循环里的 `return;` 上只跳出
        /// 内层循环 ⇒ 静默错值（NT-07）。要启用必须先改走
        /// <see cref="ReturnStatementRewriter.Rewrite"/>（do-while + break，嵌套循环写
        /// `__ENTJOY_UNSUPPORTED_STMT__` 标记）。用 `[Obsolete(error: true)]` 把"误用"变成编译错误。
        /// </summary>
        [System.Obsolete("无调用者（历史遗留）：启用前必须改走 ReturnStatementRewriter.Rewrite(...)，"
            + "否则内层循环里的 return 会被换成只跳出内层循环的 break（NT-07 静默错值）。", error: true)]
        private static string WrapEntityLoopSIMD(string scalarBodyWithLoop, string entityIndexVar, string entityCountExpr)
        {
            string loopStartPattern = $"for (int {entityIndexVar} = 0; {entityIndexVar} <";
            string loopEndPattern = $"; ++{entityIndexVar})";

            int loopIdx = scalarBodyWithLoop.IndexOf(loopStartPattern);
            if (loopIdx < 0)
            {
                // Try alternate increment pattern
                loopStartPattern = $"for (int {entityIndexVar} = 0; {entityIndexVar} <";
                loopEndPattern = $"; {entityIndexVar}++)";
                loopIdx = scalarBodyWithLoop.IndexOf(loopStartPattern);
                if (loopIdx < 0)
                    return scalarBodyWithLoop; // fallback: no entity loop found
            }

            // Find the bounds expression (between "<" and ";")
            int condStart = scalarBodyWithLoop.IndexOf('<', loopIdx);
            int semiPos = scalarBodyWithLoop.IndexOf(';', condStart);
            string boundExpr = scalarBodyWithLoop.Substring(condStart + 1, semiPos - condStart - 1).Trim();

            // Find the loop body boundaries
            int openBrace = scalarBodyWithLoop.IndexOf('{', loopIdx);
            if (openBrace < 0) return scalarBodyWithLoop;
            int depth = 1;
            int closeBrace = -1;
            for (int i = openBrace + 1; i < scalarBodyWithLoop.Length; i++)
            {
                if (scalarBodyWithLoop[i] == '{') depth++;
                else if (scalarBodyWithLoop[i] == '}')
                {
                    depth--;
                    if (depth == 0) { closeBrace = i; break; }
                }
            }
            if (closeBrace < 0) return scalarBodyWithLoop;

            // Extract the loop body content (without braces)
            string loopBody = scalarBodyWithLoop.Substring(openBrace + 1, closeBrace - openBrace - 1);

            // Remove #pragma lines from body
            loopBody = Regex.Replace(loopBody, @"#pragma\s+\w+\([^)]*\)\s*\r?\n?", "");
            loopBody = Regex.Replace(loopBody, @"#pragma\s+unroll\s*\(\s*\d+\s*\)\s*\r?\n?", "");

            bool hasReturn = loopBody.Contains("return;");

            // Build per-lane SIMD section
            var simdSection = new StringBuilder();
            simdSection.AppendLine($"    int __simd_end = ({boundExpr} / NSIMD_WIDTH) * NSIMD_WIDTH;");
            simdSection.AppendLine("    if (__simd_end > 0)");
            simdSection.AppendLine("    {");
            simdSection.AppendLine("        for (int si = 0; si < __simd_end; si += NSIMD_WIDTH)");
            simdSection.AppendLine("        {");
            simdSection.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
            simdSection.AppendLine("            {");
            simdSection.AppendLine($"                int {entityIndexVar} = si + lane;");

            if (hasReturn)
            {
                simdSection.AppendLine("                do {");
                foreach (var line in loopBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    trimmed = trimmed.Replace("return;", "break;");
                    simdSection.AppendLine($"                    {trimmed}");
                }
                simdSection.AppendLine("                } while(false);");
            }
            else
            {
                foreach (var line in loopBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    simdSection.AppendLine($"                {trimmed}");
                }
            }
            simdSection.AppendLine("            }");
            simdSection.AppendLine("        }");
            simdSection.AppendLine("    }");

            // Build remainder loop
            simdSection.AppendLine($"    for (int {entityIndexVar} = __simd_end; {entityIndexVar} < {boundExpr}; ++{entityIndexVar})");
            simdSection.AppendLine("    {");
            if (hasReturn)
            {
                simdSection.AppendLine("        do {");
                foreach (var line in loopBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    trimmed = trimmed.Replace("return;", "break;");
                    simdSection.AppendLine($"            {trimmed}");
                }
                simdSection.AppendLine("        } while(false);");
            }
            else
            {
                foreach (var line in loopBody.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    simdSection.AppendLine($"            {trimmed}");
                }
            }
            simdSection.AppendLine("    }");

            // Replace the original for-loop with per-lane SIMD section
            string beforeLoop = scalarBodyWithLoop.Substring(0, loopIdx);
            string afterLoop = scalarBodyWithLoop.Substring(closeBrace + 1);
            return beforeLoop + simdSection.ToString() + afterLoop;
        }
    }
}
