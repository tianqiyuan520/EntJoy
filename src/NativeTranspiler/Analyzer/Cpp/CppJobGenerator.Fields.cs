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

        // 局部变量声明：仅保留 NativeList 引用，移除 NativeArray 包装。
        // 标量字段的绑定形式见下方 typeOk 判据；字段用途按符号解析（无语义模型时退回名字兜底）。
        private static void AppendLocalVariableDeclarations(INamedTypeSymbol jobStruct, StringBuilder sb,
            SemanticModel? semanticModel = null, Compilation? compilation = null, bool contextAbi = false,
            IFieldSymbol? zeroMaskField = null,
            HashSet<IFieldSymbol>? stableArrays = null)
        {
            if (contextAbi) AppendContextFieldDeclarations(jobStruct, sb, stableArrays);
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                    {
                        var elementType = NativeTranspiler.GetContainerElementType(field.Type);
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        sb.AppendLine($"    EntJoy::Collections::UnsafeList<{cppElementType}>& {field.Name} = *{field.Name}_listData;");
                    }
                    // NativeArray: nothing
                }
            }

            var loopUse = GetFieldLoopUse(jobStruct, semanticModel ?? TryGetSemanticModel(jobStruct, compilation));

            // 用已引用且未写入的合格字段数限制入口快照；这是保守启发式，
            // 不等同于活跃区间分析或实际寄存器数。最终分配由 C++ 编译器决定。
            const int RegisterBudget = 8;
            int liveScalars = 0;
            foreach (var f in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(x => !x.IsStatic))
                if (ValueBindTypeOk(f.Type) && loopUse.Used.Contains(f.Name) && !loopUse.Written.Contains(f.Name))
                    liveScalars++;
            bool bindByValue = liveScalars <= RegisterBudget;
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type)) continue;
                if (field.Type is IPointerTypeSymbol) continue;
                var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                // 绑定形式：`bindByValue`（整个 job 的判定，见上）∧ 类型合格 ∧ 被引用 ∧ 未被写 ⇒ 按值。
                // 其余一律按引用。形参仍是 `T*` ⇒ 不动 ABI，只是函数体内的局部绑定形式。
                string scalarSrc = $"{field.Name}_ptr";
                bool typeOk = ValueBindTypeOk(field.Type);
                if (SymbolEqualityComparer.Default.Equals(field, zeroMaskField))
                {
                    sb.AppendLine($"    static constexpr {cppType} __ejZeroMaskValue = 0;");
                    string reference = bindByValue ? "" : "&";
                    sb.AppendLine($"    const {cppType}{reference} {field.Name} = __ejZeroMask ? __ejZeroMaskValue : *{scalarSrc};");
                    continue;
                }
                if (bindByValue && typeOk && loopUse.Used.Contains(field.Name) && !loopUse.Written.Contains(field.Name))
                    sb.AppendLine($"    const {cppType} {field.Name} = *{scalarSrc};");
                else
                {
                    string qualifier = loopUse.Written.Contains(field.Name) ? "" : "const ";
                    sb.AppendLine($"    {qualifier}{cppType}& {field.Name} = *{scalarSrc};");
                }
            }
        }

        /// <summary>
        /// 批入口的字段取址：从 job 结构体地址按常量偏移取指针/长度，取代逐字段形参。
        /// 句柄稳定的数组按值缓存指针，其余指针槽按引用绑定。
        /// </summary>
        internal static void AppendContextFieldDeclarations(INamedTypeSymbol jobStruct, StringBuilder sb,
            HashSet<IFieldSymbol>? stableArrays = null)
        {
            int currentOffset = 0;
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                int offset = CalculateFieldOffset(field, ref currentOffset);
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                    {
                        var cppElementType = NativeTranspiler.MapCSharpTypeToCpp(NativeTranspiler.GetContainerElementType(field.Type));
                        sb.AppendLine($"    EntJoy::Collections::UnsafeList<{cppElementType}>*& {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{cppElementType}>**)((char*)__job + {offset});");
                    }
                    else
                    {
                        var cppElementType = GetCppElementType(field.Type);
                        string binding = stableArrays?.Contains(field) == true ? "* const " : "*& ";
                        sb.AppendLine($"    {cppElementType}{binding}{field.Name}_ptr = *({cppElementType}**)((char*)__job + {offset});");
                        sb.AppendLine($"    int& {field.Name}_length = *(int*)((char*)__job + {offset + 8});");
                    }
                }
                else if (field.Type is IPointerTypeSymbol)
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    sb.AppendLine($"    {cppType}& {field.Name}_ptr = *({cppType}*)((char*)__job + {offset});");
                }
                else
                {
                    var cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    sb.AppendLine($"    {cppType}* {field.Name}_ptr = ({cppType}*)((char*)__job + {offset});");
                }
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
        // 取一次的局部常量 ⇒ 行程数成为编译期常量。是否按值由 `bindByValue` 决定（见 AppendLocalVariableDeclarations）。

        /// <summary>某个 job 的字段在循环里的用处分组（按符号解析，不再按名字猜）。</summary>
        private sealed class FieldLoopUse
        {
            /// <summary>参与循环行程数的字段名：出现在 `for` 初值/条件/步进、或 `while`/`do` 条件里。</summary>
            public readonly HashSet<string> TripCount = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>出现在任意循环内（头部或体内）的字段名（判据：这类形参加 `__restrict`）。</summary>
            public readonly HashSet<string> InLoop = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>在 body 里被**直接写**的字段名（赋值 / `++` `--` / `ref`|`out` 实参）。
            /// 被写的字段一律不得按值绑定 —— 按值后写会落到入口副本上，语义直接错。
            /// 写成 `Arr[i] = …` 不算写字段本身。</summary>
            public readonly HashSet<string> Written = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>在 body 里被**引用到**的字段名（含环外引用，按符号解析、屏蔽名不误判）。
            /// 按值绑定要求"确实用到"：被同名局部屏蔽而实际未被引用的字段保持按引用。</summary>
            public readonly HashSet<string> Used = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// 收集字段的循环用途与读写集合：按符号解析，标识符必须解析到本 job 的实例字段 ⇒ 同名局部/形参不误判；
        /// 无语义模型时退回名字兜底（同名声明一律保守跳过）。`TripCount`/`InLoop` 决定形参 `__restrict`
        /// （`ScalarRestrictEnabled`/`ListLengthRestrictEnabled`），`Used`/`Written` 参与按值绑定判据。
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

            // 记录"字段本身被写"（不是写它的元素）：赋值左值 / `++` `--` 的操作数 / `ref`|`out` 实参。
            void TakeWrite(SyntaxNode? node)
            {
                while (true)
                {
                    if (node is ParenthesizedExpressionSyntax parentheses) node = parentheses.Expression;
                    else if (node is MemberAccessExpressionSyntax member && member.Expression is not ThisExpressionSyntax)
                    {
                        if (member.IsKind(SyntaxKind.PointerMemberAccessExpression)) return;
                        node = member.Expression;
                    }
                    else break;
                }
                var id = node as IdentifierNameSyntax;
                if (id == null && node is MemberAccessExpressionSyntax access && access.Expression is ThisExpressionSyntax)
                    id = access.Name as IdentifierNameSyntax;
                if (id == null) return;
                var name = id.Identifier.Text;
                if (!fields.Contains(name)) return;
                if (semanticModel != null)
                {
                    var sym = semanticModel.GetSymbolInfo(id).Symbol;
                    if (sym is IFieldSymbol f && !f.IsStatic
                        && SymbolEqualityComparer.Default.Equals(f.ContainingType, jobStruct))
                        use.Written.Add(name);
                }
                else if (shadowed != null && !shadowed.Contains(name))
                {
                    use.Written.Add(name);
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
                    case AssignmentExpressionSyntax a:
                        TakeWrite(a.Left);
                        break;
                    case PrefixUnaryExpressionSyntax pu:
                        if (pu.IsKind(SyntaxKind.PreIncrementExpression) || pu.IsKind(SyntaxKind.PreDecrementExpression)
                            || pu.IsKind(SyntaxKind.AddressOfExpression)) TakeWrite(pu.Operand);
                        break;
                    case PostfixUnaryExpressionSyntax po:
                        TakeWrite(po.Operand);
                        break;
                    case ArgumentSyntax arg when arg.RefOrOutKeyword.RawKind != 0:
                        TakeWrite(arg.Expression);
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
            //   于是"在源码里找循环"的判据（TripCount / InLoop）对这类 job 永远为空 ⇒ 形参 `__restrict`
            //   （`ScalarRestrictEnabled`）对宿主最重要的 job 类型完全失效（Count / Integrate /
            //   Place / Melee 全是 IJobParallelFor）。实测证据：生成的 `CountCellsJob_Execute.cpp` 里
            //   六个标量全部是 `const T& X = *X_ptr;`，尽管 `Length/CellsW/StateDeath` 每元素都要读。
            //   修法：源码里没有循环时，整个 body 就是循环体 ⇒ 全部字段算"循环内"。
            if (!sawLoop)
            {
                Take(body, use.InLoop);
            }
            use.InLoop.UnionWith(use.TripCount);
            Take(body, use.Used);   // 全 body 的字段引用（含环外）：按值绑定要求"确实用到"
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
                        var elementType = NativeTranspiler.GetContainerElementType(field.Type);
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
                        var elementType = NativeTranspiler.GetContainerElementType(field.Type);
                        var cppElementType = (NativeTranspiler.IsReadOnlyNativeArray(field.Type) ? "const " : "") + NativeTranspiler.MapCSharpTypeToCpp(elementType);
                        // 数组形参不加 `__restrict`：job 的数组字段可能互为别名（读邻居/写自身），
                        // 无别名证据时不能声明 noalias。
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
                else
                    args.Add($"{field.Name}_ptr");
            }
            return string.Join(", ", args);
        }
    }
}
