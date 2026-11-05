using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public partial class IspcStatementTranslator
    {

        // SendEvent 支持（ISPC EventBuffer 写入）
        /// <summary>ISPC 函数中 EventBuffer 指针数组的参数名（null = 未启用 SendEvent）。</summary>
        private string? _eventBufferParamName;

        /// <summary>Execute 中发现的 SendEvent 事件类型（有序，index 对应 eventBufferHeaders 数组）。</summary>
        public List<INamedTypeSymbol> EventTypes { get; } = new();

        /// <summary>托管事件类型错误（编译时报错）。</summary>
        public List<(INamedTypeSymbol eventType, InvocationExpressionSyntax invocation)> ManagedEventErrors { get; } = new();

        /// <summary>设置 EventBuffer 参数名（由外部生成器在检测到 Job 使用 SendEvent 后调用）。</summary>
        public void SetEventBufferParamName(string name) => _eventBufferParamName = name;

        /// <summary>是否启用 SendEvent 翻译（有 EventBuffer 参数）。</summary>
        public bool HasEventBufferSupport => _eventBufferParamName != null;

        // SendEvent 翻译（ISPC EventBuffer 写入）

        /// <summary>
        /// 检测 world.SendEvent&lt;T&gt;(new T { ... }) 调用，生成 ISPC EventBuffer 写入代码。
        /// 返回 true 表示已翻译（调用方应 return）。
        /// </summary>
        private bool TryTranslateSendEvent(InvocationExpressionSyntax invocation)
        {
            // 情况 1：裸调用 SendEvent<T>(...) — Expression 直接是 IdentifierName
            if (invocation.Expression is IdentifierNameSyntax bareName
                && bareName.Identifier.Text == Config.SendEvent)
            {
                if (invocation.ArgumentList?.Arguments.Count == 1)
                {
                    var typeInfo = _semanticModel.GetTypeInfo(bareName);
                    if (typeInfo.Type is INamedTypeSymbol evtType && NativeTranspileValidator.IsUnmanagedType(evtType))
                        return GenerateSendEventIspc(invocation, evtType, typeInfo.Type.ToDisplayString());
                }
            }

            // 情况 2：xxx.SendEvent<T>(...) — MemberAccess 链（SystemAPI.SendEvent / World.SendEvent）
            if (invocation.Expression is MemberAccessExpressionSyntax mac
                && mac.Name is GenericNameSyntax genericName
                && genericName.Identifier.Text == Config.SendEvent
                && genericName.TypeArgumentList?.Arguments.Count == 1)
            {
                var typeArg = genericName.TypeArgumentList.Arguments[0];
                var typeInfo = _semanticModel.GetTypeInfo(typeArg);
                if (typeInfo.Type is INamedTypeSymbol evtType && NativeTranspileValidator.IsUnmanagedType(evtType))
                    return GenerateSendEventIspc(invocation, evtType, typeInfo.Type.ToDisplayString());
                if (typeInfo.Type is INamedTypeSymbol managedEvtType && !NativeTranspileValidator.IsUnmanagedType(managedEvtType))
                {
                    ManagedEventErrors.Add((managedEvtType, invocation));
                    return false;
                }
            }

            // 情况 3：GetSymbolInfo 回退
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol method
                && method.Name == Config.SendEvent && method.IsGenericMethod)
            {
                var eventType = method.TypeArguments[0] as INamedTypeSymbol;
                if (eventType != null && NativeTranspileValidator.IsUnmanagedType(eventType))
                    return GenerateSendEventIspc(invocation, eventType, eventType.ToDisplayString());
                if (eventType != null && !NativeTranspileValidator.IsUnmanagedType(eventType))
                {
                    ManagedEventErrors.Add((eventType, invocation));
                    return false;
                }
            }

            return false;
        }

        /// <summary>生成 SendEvent 的 ISPC EventBuffer 写入代码（per-lane 原子槽分配）。</summary>
        private bool GenerateSendEventIspc(InvocationExpressionSyntax invocation, INamedTypeSymbol eventType, string cppTypeName)
        {
            // 记录事件类型（去重）
            int typeIndex = -1;
            for (int i = 0; i < EventTypes.Count; i++)
            {
                if (SymbolEqualityComparer.Default.Equals(EventTypes[i], eventType))
                {
                    typeIndex = i;
                    break;
                }
            }
            if (typeIndex < 0)
            {
                typeIndex = EventTypes.Count;
                EventTypes.Add(eventType);
            }

            // 编译期 fail-fast：ISPC 事件 struct 必须 4B 对齐（C ABI，与 C# Sequential 一致）。
            // 含 double(8B)/bool(1B)/long 字段的类型会破坏 ISPC int 槽位写入的 stride 一致性 →
            // 生成一个必然编译失败的未定义符号引用（ispc 报 "Undeclared symbol" Error，exit≠0），
            // 让构建在编译期报错并定位到具体类型，而不是静默错位。
            // ⚠ 不要用 1/0：ispc 只报 Warning（"Division by zero is undefined"），不会失败。
            if (!IsIspcEventLayoutSafe(eventType))
            {
                AppendIndent();
                _builder.AppendLine($"// ERROR: event type {eventType.Name} contains non-4B-aligned field(s) " +
                    $"(double/bool/byte/short/long). ISPC EventBuffer requires 4-byte-aligned blittable layout.");
                AppendIndent();
                _builder.AppendLine($"uniform int __ENTJOY_UNALIGNED_EVENT_TYPE_{eventType.Name}_ = __ENTJOY_UNALIGNED_EVENT_TYPE_{eventType.Name}_;");
                return true;
            }

            string cppEventType = NativeTranspiler.MapCSharpTypeToCpp(eventType);
            string ispcEventType = IspcTypeMapper.FromCpp(cppEventType);
            string tempVar = $"__evt_{eventType.Name}_{typeIndex}";
            string bufVar = _eventBufferParamName!;
            // 事件 buffer 访问表达式（内联，避免在 divergent 块内声明 uniform 局部变量 —— ISPC 禁止）
            string bufExpr = $"((uniform __EntJoyEventBuffer* uniform*){bufVar})[{typeIndex}]";

            // ISPC SendEvent 正确性要点（实测验证）：
            // 1) 原子槽分配用 atomic_add_global —— ISPC 的 atomic_add_global 是 fetch-add 语义，
            //     ⚠ 不要用 varying struct 指针（SoA 布局，sizeof = lanes×元素）会越界写坏内存；
            //     ⚠ 不要手写 int 槽位偏移（stride/4 + fieldOffset/4）——那依赖"事件类型全 4B 字段"
            //        + "Entity 恰好两 int"两个脆弱假设，加 double/bool/long 字段即错位。
            // 4) uniform 局部变量不能声明在 divergent（varying 条件）块内 → SendEvent 代码全部内联表达式。
            AppendIndent();
            _builder.AppendLine($"varying int {tempVar}_idx = atomic_add_global({bufExpr}->count, (varying int)1);");

            // 翻译事件对象初始化
            if (invocation.ArgumentList.Arguments.Count > 0)
            {
                var argExpr = invocation.ArgumentList.Arguments[0].Expression;
                if (argExpr is ObjectCreationExpressionSyntax objCreate)
                {
                    // uniform struct 指针：索引步长 = sizeof(uniform T)，编译器按 struct 定义计算对齐。
                    // ISPC 中 "uniform T*" 可从 "uniform __EntJoyEventBuffer*" 的 data（int*）cast 得到。
                    string bufPtr = $"((uniform {ispcEventType}*){bufExpr}->data)";
                    foreach (var init in objCreate.Initializer?.Expressions ?? Enumerable.Empty<ExpressionSyntax>())
                    {
                        if (init is AssignmentExpressionSyntax assign && assign.Left is IdentifierNameSyntax fieldName)
                        {
                            // 用真实 struct 字段名写：buf[idx].Field = rhs;
                            // ISPC 编译器自动处理嵌套 struct（Entity Target → buf[idx].Target.Id / .Version）。
                            // ⚠ 嵌套 struct 字段值（new Inner {...}）不能走 CaptureExpressionText：
                            //   base.TranslateObjectCreation 生成 C++ 风格 "Type()" 构造函数调用，
                            //   ISPC 没有构造函数，也没有 C99 designated initializer / compound literal。
                            //   → 递归展开为逐字段赋值：buf[idx].Field.SubField = value。
                            TranslateIspcNestedFieldWrite(bufPtr, $"{tempVar}_idx", fieldName.Identifier.Text, assign.Right);
                        }
                    }
                }
                else
                {
                    // 非对象创建参数：无法逐字段写（字段名未知），不支持。
                    // ⚠ 必须发 __ENTJOY_UNSUPPORTED 标记，不能只写普通注释：构建期
                    //   NativeCompileTask.CheckGeneratedMarkers 只会让带标记的产物构建失败；
                    //   只写注释 = ISPC 后端静默丢掉这次事件写入（审计"marker 缺口"的最后一处）。
                    _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_SendEventNonObjectCreationArg");
                }
            }

            return true;
        }

        /// <summary>
        /// 检查事件类型是否为 ISPC 可安全表示的布局：所有字段大小都是 4B 的倍数
        /// （int/float/uint/Entity/嵌套全 4B struct）。
        /// ISPC uniform struct 的对齐与 C# Sequential 一致（都遵循 C ABI），但
        /// double(8B)/bool(1B)/byte/short/long 字段会使 stride 计算与 int 槽位假设
        /// 产生不一致风险 → 编译期拒绝（见 GenerateSendEventIspc 的 fail-fast）。
        /// </summary>
        private static bool IsIspcEventLayoutSafe(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named)
            {
                // 显式放行 4B 类型（int/uint/float）
                switch (named.SpecialType)
                {
                    case SpecialType.System_Int32:
                    case SpecialType.System_UInt32:
                    case SpecialType.System_Single:
                        return true;
                    // 显式拒绝非 4B primitive（防止被当作"空 struct"放行）
                    case SpecialType.System_Boolean:
                    case SpecialType.System_Byte:
                    case SpecialType.System_SByte:
                    case SpecialType.System_Int16:
                    case SpecialType.System_UInt16:
                    case SpecialType.System_Char:
                    case SpecialType.System_Int64:
                    case SpecialType.System_UInt64:
                    case SpecialType.System_Double:
                        return false;
                }
                string fullName = named.ToDisplayString();
                if (fullName == "EntJoy.Mathematics.float2" ||
                    fullName == "EntJoy.Mathematics.int2" ||
                    fullName == "EntJoy.Mathematics.uint2")
                    return true;
                if (named.TypeKind == TypeKind.Struct)
                {
                    foreach (var f in named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    {
                        if (!IsIspcEventLayoutSafe(f.Type))
                            return false;
                    }
                    return true;
                }
                // enum（底层 int）安全
                if (named.TypeKind == TypeKind.Enum)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 将 SendEvent 字段赋值右侧翻译为 ISPC 逐字段写入（支持嵌套 struct 对象创建）。
        /// 顶层：buf[idx].Field = rhs
        /// 嵌套：new Inner { a = v1, b = v2 } → buf[idx].Field.a = v1; buf[idx].Field.b = v2;
        /// 原因：ISPC 没有构造函数，不支持 C99 designated initializer（{ f = v }）和
        /// compound literal（(T){ ... }）在赋值 RHS 的写法；逐字段赋值是唯一确定正确的形式。
        /// 带参构造（new float2(1,2)，无 Initializer）→ 落到 CaptureExpressionText 走
        /// TranslateObjectCreation 的 make_float2/make_int2/make_uint2 辅助函数。
        /// </summary>
        private void TranslateIspcNestedFieldWrite(string bufPtr, string idxVar, string fieldPath, ExpressionSyntax expr)
        {
            if (expr is ObjectCreationExpressionSyntax nestedCreate
                && nestedCreate.Initializer != null
                && nestedCreate.Initializer.Expressions.Count > 0)
            {
                foreach (var init in nestedCreate.Initializer.Expressions)
                {
                    if (init is AssignmentExpressionSyntax assign && assign.Left is IdentifierNameSyntax nestedField)
                        TranslateIspcNestedFieldWrite(bufPtr, idxVar, $"{fieldPath}.{nestedField.Identifier.Text}", assign.Right);
                }
                return;
            }
            string rhs = CaptureExpressionText(expr);
            AppendIndent();
            _builder.AppendLine($"{bufPtr}[{idxVar}].{fieldPath} = {rhs};");
        }

    }
}
