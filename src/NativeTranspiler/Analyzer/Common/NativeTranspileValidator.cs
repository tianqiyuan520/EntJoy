using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public static class NativeTranspileValidator
    {
        public static readonly DiagnosticDescriptor InvalidReturnTypeError = new("NT001", "Invalid return type", "[NativeTranspile] method '{0}' return type '{1}' must be unmanaged or void", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor InvalidParameterTypeError = new("NT002", "Invalid parameter type", "[NativeTranspile] method '{0}' parameter '{1}' type '{2}' must be unmanaged", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor InvalidLocalVariableTypeError = new("NT003", "Invalid local variable type", "[NativeTranspile] method '{0}' local variable '{1}' type '{2}' must be unmanaged. 局部数组（含定长数组）与托管类型不支持：把临时缓冲改成 job 字段传入（NativeArray/UnsafeList），或放进 job 结构体字段后在体内取本地别名。", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor DisallowedMethodCallError = new("NT004", "Disallowed method call", "[NativeTranspile] method '{0}' cannot call '{1}' because its signature contains non‑unmanaged types or it is not a static method in the same assembly", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor ManagedObjectCreationError = new("NT005", "Managed object creation", "[NativeTranspile] method '{0}' cannot create managed object of type '{1}'", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor ReferenceTypeUsageError = new("NT006", "Reference type usage", "[NativeTranspile] method '{0}' uses reference type '{1}' which is not allowed. 引用类型静态字段（如 `static readonly int[]`）、`switch` 表达式、字符串、委托都无法转译：把常量表改成 job 的 NativeArray 字段由宿主传入，`switch` 表达式改成 if/else 或静态查表。", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor InvalidJobTypeError = new("NT007", "Invalid Job type", "[NativeTranspile] can only be applied to structs. '{0}' is not a struct.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor MissingJobInterfaceError = new("NT008", "Missing Job interface", "[NativeTranspile] struct '{0}' must implement IJob, IJobParallelFor, IJobParallelForBatch, IJobFor, IJobChunk, or IJobEntity.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor InvalidJobFieldError = new("NT009", "Invalid Job field", "[NativeTranspile] struct '{0}' field '{1}' type '{2}' must be unmanaged.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor MissingExecuteMethodError = new("NT010", "Missing Execute method", "[NativeTranspile] struct '{0}' must contain an Execute method.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor DisallowedChunkDataAccessError = new("NT012", "Disallowed chunk data access", "[NativeTranspile] IJobChunk method '{0}' cannot call '{1}'. Use ArchetypeChunk.GetComponentDataNativeArray<T>() for native chunk data access.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor InvalidJobEntityError = new("NT013", "Invalid IJobEntity", "[NativeTranspile] IJobEntity struct '{0}' only supports C++/ISPC backend and Execute(ref/in unmanaged component) parameters.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor ManagedSharedComponentError = new("NT014", "Managed shared component access", "[NativeTranspile] GetSharedComponent<{0}>() cannot access managed shared component type. Only blittable shared components can be accessed in [NativeTranspile] jobs. Use C# main thread or job struct field capture instead.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor ManagedEventTypeError = new("NT015", "Managed event type", "[NativeTranspile] SendEvent<{0}>(): event type must be unmanaged (blittable). Managed types are not supported in native jobs. Use a blittable signal struct instead.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor UnsupportedStructLayoutError = new("NT016", "Unsupported struct layout for ISPC", "[NativeTranspile] struct '{0}' uses {1} which ISPC cannot represent (ISPC does not support #pragma pack); NativeArray<{0}> layout would misalign. Use Sequential default layout (no Pack < 8, no Explicit).", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor MultiRelationAccessError = new("NT017", "Multi-relation access in native job", "[NativeTranspile] '{0}' cannot access [MultiRelation] relation type '{1}'. Multi-relation data is stored in managed lists on the main thread and is not readable from native jobs. Use C# main-thread code or a single-value relation column instead.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor VectorizeRequiresChunkOrEntityError = new("NT018", "AutoSIMD.Vectorize requires IJobChunk/IJobEntity", "[NativeTranspile] struct '{0}' sets AutoSIMD = Vectorize but is not an IJobChunk/IJobEntity; the Vectorize path is only implemented for those two, so the flag is silently dropped. Use AutoSIMD = Enabled for IJobParallelFor/IJobFor/IJob.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor AutoSimdRequiresCppBackendError = new("NT019", "AutoSIMD requires Cpp backend", "[NativeTranspile] struct '{0}' sets AutoSIMD = Enabled together with Target = Ispc; the ISPC backend never reads AutoSIMD (it vectorizes via foreach/gang), so the flag is silently dropped. Drop AutoSIMD, or switch Target to Cpp.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor MathPrecisionRequiresCppBackendError = new("NT020", "MathPrecision requires Cpp backend", "[NativeTranspile] struct '{0}' sets MathPrecision but Target is not Cpp; per-job precision is emitted as a '#define SIMD_MATH_PRECISION' only into the C++ translation unit. ISPC precision is controlled by IspcMathLib instead.", "NativeTranspiler", DiagnosticSeverity.Error, true);

        /// <summary>
        /// 生成器不变量：编译中出现了 chunk/entity（ECS）job，却检测不到 EntJoy.ECS 引用。
        /// 正常不可能发生（IJobChunk/IJobEntity 的类型定义在 EntJoy.ECS 内）；一旦出现即说明
        /// job 种类判定与类型可见性不一致，必须修生成器而不是让用户看 CS0234。
        /// </summary>
        public static readonly DiagnosticDescriptor EcsRequiredButMissingError = new("NT029", "ECS required but not referenced", "[NativeTranspile] this compilation contains IJobChunk/IJobEntity jobs, which require a reference to EntJoy.ECS, but EntJoy.ECS was not found. The generator's job-kind detection disagrees with type visibility.", "NativeTranspiler", DiagnosticSeverity.Error, true);

        /// <summary>
        /// 生成器不变量："Jobs-only 解耦"的自校验。编译未引用 EntJoy.ECS，但生成的 bindings
        /// 仍出现 ECS 符号 ⇒ 某个 ECS 相关发射点漏了条件化（新增代码路径的常见回归）。
        /// 消费者此时本来也会撞上 CS0234/CS0246，本诊断把原因直接指向生成器。
        /// 若这些名字是用户自己的类型，可改名或忽略（词边界匹配，MyWorldJob 之类不会误报）。
        /// </summary>
        public static readonly DiagnosticDescriptor GeneratedEcsCouplingWarning = new("NT030", "Generated bindings couple to EntJoy.ECS", "NativeTranspiler generated bindings reference ECS symbols ({0}) while this compilation does not reference EntJoy.ECS. Either a generator emission point lost its conditional guard, or your own types use these names.", "NativeTranspiler", DiagnosticSeverity.Warning, true);
        public static readonly DiagnosticDescriptor CppMathLibRequiresCppBackendError = new("NT021", "CppMathLib requires Cpp backend", "[NativeTranspile] struct '{0}' sets CppMathLib = fast but Target is not Cpp; that flag only controls the /fp:fast compilation unit split for C++ jobs.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor UseIspcMtRequiresIspcBackendError = new("NT022", "UseISPC_MT requires Ispc backend", "[NativeTranspile] struct '{0}' sets UseISPC_MT but Target is not Ispc; the multi-task ISPC variant is only generated for the ISPC backend.", "NativeTranspiler", DiagnosticSeverity.Error, true);

        /// <summary>F-6：SimdMathPrecision.High 没有实现（产物与 IEEE 逐字相同），不能让人以为拿到了更快的 1.0ULP 路径。</summary>
        public static readonly DiagnosticDescriptor SimdMathPrecisionHighUnimplementedWarning = new("NT023", "SimdMathPrecision.High has no SIMD implementation", "[NativeTranspile] struct '{0}' sets MathPrecision = High, but only Fastest has a SIMD implementation (the Sleef polynomials were removed): the emitted code is identical to MathPrecision = IEEE. Use Fastest for the AVX2/AVX512 inline polynomial, or IEEE to state the intent explicitly.", "NativeTranspiler", DiagnosticSeverity.Warning, true);

        /// <summary>F-2：AutoSIMD.Enabled 在 IJobParallelFor 上实测无收益（更慢），且多数 job 会整体退回标量。</summary>
        public static readonly DiagnosticDescriptor AutoSimdNoMeasuredGainWarning = new("NT024", "AutoSIMD has no measured gain on IJobParallelFor", "[NativeTranspile] struct '{0}' sets AutoSIMD = Enabled on an IJobParallelFor/IJobFor/IJob: measured end-to-end it is ~10% slower than the scalar baseline, and a body containing Interlocked / UnsafeUtility.ArrayElementAsRef / user static helpers falls back to a per-lane scalar loop (no SIMD at all). Keep AutoSIMD = Disabled unless your own measurement says otherwise.", "NativeTranspiler", DiagnosticSeverity.Warning, true);

        /// <summary>F-5：IJobParallelForBatch 目前只有 Cpp 后端 + 标量代码生成路径（ISPC/AutoSIMD 未实现）。</summary>
        public static readonly DiagnosticDescriptor ParallelForBatchRequiresCppBackendError = new("NT025", "IJobParallelForBatch requires the Cpp backend without AutoSIMD", "[NativeTranspile] struct '{0}' implements IJobParallelForBatch, which is only implemented for Target = Cpp with AutoSIMD = Disabled (the ISPC/AutoSIMD paths only know the per-index Execute(int) shape). Drop Target = Ispc / AutoSIMD, or use IJobParallelFor instead.", "NativeTranspiler", DiagnosticSeverity.Error, true);

        /// <summary>
        /// E-1：**部分生成**告知。某个 job 校验失败（如 NT008）时，生成器不再中止整批产物，
        /// 而是把该 job 排除后继续为其余 job 产出 Bindings.g.cs；本 Warning 说明"哪些 job 没有产物"。
        /// 目的：把"缺绑定"这件事**明确说出来**。
        /// </summary>
        public static readonly DiagnosticDescriptor PartialBindingsWarning = new("NT028", "Bindings generated without some jobs", "{0} 个 [NativeTranspile] job 未通过校验、已从本批绑定生成中排除：{1}。其余 job 的绑定照常产出；上述 job 不会生成 Schedule 绑定（调用点会报 CS0103，而不是整包 CS0234）。", "NativeTranspiler", DiagnosticSeverity.Warning, true);

        /// <summary>
        /// 定位只能靠二分（实测代价：一整轮）。本诊断给出异常消息 + 调用栈前 6 帧，
        /// 完整 ToString() 落盘到 <c>%TEMP%/entjoy-native-transpiler-crash.txt</c>。
        /// </summary>
        public static readonly DiagnosticDescriptor GeneratorCrashError = new("NT026", "NativeTranspiler generator crashed", "[NativeTranspile] 生成器抛出 {0}: {1}｜调用栈（前 6 帧；完整版见 %TEMP%/entjoy-native-transpiler-crash.txt）：{2}", "NativeTranspiler", DiagnosticSeverity.Error, true);

        /// <summary>
        /// P0-5b：原生 job 不支持 `ref` 局部（`ref T x = ref expr;`）。
        /// 而在可空注解上下文（`Nullable=enable`）下更会因类型解析返回 null 而打崩生成器。
        /// 正确写法：**指针局部** `T* p = &amp;arr[i];`。
        /// </summary>
        public static readonly DiagnosticDescriptor RefLocalNotSupportedError = new("NT027", "ref local element type cannot be resolved", "[NativeTranspile] 方法 '{0}' 的 `ref` 局部（`ref T x = ref …`）**元素类型无法解析**：无法生成 `T& x = …`。请显式写出元素类型（避免 `ref var`），或改用指针局部 `T* p = &arr[i];`。", "NativeTranspiler", DiagnosticSeverity.Error, true);

        /// <summary>
        /// 解析局部声明的类型（P0-5b 修复）：优先类型语法节点的语义类型；**为 null 时回退到声明符号的类型**
        /// —— `ref T x = ref expr;` 在 `Nullable=enable` 下 `GetTypeInfo(Type).Type` 返回 null，
        /// </summary>
        private static ITypeSymbol? ResolveLocalType(SemanticModel model, LocalDeclarationStatementSyntax localDecl)
        {
            var t = model.GetTypeInfo(localDecl.Declaration.Type).Type;
            if (t != null) return t;
            foreach (var v in localDecl.Declaration.Variables)
                if (model.GetDeclaredSymbol(v) is ILocalSymbol ls) return ls.Type;
            return null;
        }

        /// <summary>是否为 `ref` 局部（初始化器是 `ref expr`）。</summary>
        private static bool IsRefLocal(LocalDeclarationStatementSyntax localDecl)
        {
            foreach (var v in localDecl.Declaration.Variables)
                if (v.Initializer?.Value is RefExpressionSyntax) return true;
            return false;
        }

        // 预定义的系统 API 白名单
        private static readonly HashSet<string> AllowedStaticMethods = new()
        {
            "System.Math.Abs", "System.MathF.Abs",
            "System.Math.Acos", "System.MathF.Acos",
            "System.Math.Asin", "System.MathF.Asin",
            "System.Math.Atan", "System.MathF.Atan",
            "System.Math.Atan2", "System.MathF.Atan2",
            "System.Math.Ceiling", "System.MathF.Ceiling",
            "System.Math.Clamp", "System.MathF.Clamp",
            "System.Math.Cos", "System.MathF.Cos",
            "System.Math.Cosh", "System.MathF.Cosh",
            "System.Math.Exp", "System.MathF.Exp",
            "System.Math.Floor", "System.MathF.Floor",
            "System.Math.Log", "System.MathF.Log",
            "System.Math.Log10", "System.MathF.Log10",
            "System.Math.Max", "System.MathF.Max",
            "System.Math.Min", "System.MathF.Min",
            "System.Math.Pow", "System.MathF.Pow",
            "System.Math.Round", "System.MathF.Round",
            "System.Math.Sin", "System.MathF.Sin",
            "System.Math.Sinh", "System.MathF.Sinh",
            "System.Math.Sqrt", "System.MathF.Sqrt",
            "System.Math.Tan", "System.MathF.Tan",
            "System.Math.Tanh", "System.MathF.Tanh",
            "System.Math.Truncate", "System.MathF.Truncate",
            "System.Single.IsNaN", "System.Double.IsNaN",
            "System.Single.IsInfinity", "System.Double.IsInfinity",
            // ToDisplayString() 对 C# 关键字别名返回 "float"/"double"（非 System.Single/Double）
            "float.IsNaN", "double.IsNaN",
            "float.IsInfinity", "double.IsInfinity",
            "System.Threading.Interlocked.Increment",
            "System.Threading.Interlocked.Decrement",
            "System.Threading.Interlocked.Add",
            "System.Threading.Interlocked.Exchange",
            "System.Threading.Interlocked.CompareExchange",
            "System.Threading.Interlocked.Read",
            "EntJoy.Mathematics.math.dot",
            "EntJoy.Mathematics.math.lengthsq",
            "EntJoy.Mathematics.math.length",
            "EntJoy.Mathematics.math.normalize",
            "EntJoy.Mathematics.math.abs",
            "EntJoy.Mathematics.math.min",
            "EntJoy.Mathematics.math.max",
            "EntJoy.Mathematics.math.clamp",
            "EntJoy.Mathematics.math.lerp",
            "EntJoy.Mathematics.math.floor",
            "EntJoy.Mathematics.math.ceil",
            "EntJoy.Mathematics.math.distancesq",
            "EntJoy.Collections.UnsafeUtility.ArrayElementAsRef",
            "EntJoy.Hint.Likely",
            "EntJoy.Hint.Unlikely",
        };

        public static bool ValidateMethod(IMethodSymbol method, Compilation compilation, out List<Diagnostic> diagnostics)
        {
            diagnostics = new List<Diagnostic>();

            var attrSym = compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute");
            if (attrSym != null)
            {
                // GPU/CUDA 后端已拆分至 feature/gpu-offload 分支，此处仅校验 Cpp/Ispc。
            }

            if (!IsUnmanagedTypeOrVoid(method.ReturnType))
                diagnostics.Add(Diagnostic.Create(InvalidReturnTypeError, method.Locations.FirstOrDefault(), method.Name, method.ReturnType.ToDisplayString()));

            foreach (var p in method.Parameters)
                if (!IsUnmanagedType(p.Type))
                    diagnostics.Add(Diagnostic.Create(InvalidParameterTypeError, p.Locations.FirstOrDefault(), method.Name, p.Name, p.Type.ToDisplayString()));

            var methodSyntax = SymbolHelper.GetMethodSyntax(method);
            if (methodSyntax?.Body == null)
                return diagnostics.Count == 0;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            foreach (var node in methodSyntax.Body.DescendantNodes())
            {
                switch (node)
                {
                    case LocalDeclarationStatementSyntax localDecl:
                        if (IsRefLocal(localDecl) && ResolveLocalType(semanticModel, localDecl) == null)
                        {
                            diagnostics.Add(Diagnostic.Create(RefLocalNotSupportedError, localDecl.GetLocation(), method.Name));
                            break;
                        }
                        var localType = ResolveLocalType(semanticModel, localDecl);
                        if (localType != null && !IsUnmanagedType(localType))
                            foreach (var v in localDecl.Declaration.Variables)
                                diagnostics.Add(Diagnostic.Create(InvalidLocalVariableTypeError, v.GetLocation(), method.Name, v.Identifier.Text, localType.ToDisplayString()));
                        break;

                    case InvocationExpressionSyntax invocation:
                        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                        if (symbolInfo.Symbol is IMethodSymbol calledMethod)
                        {
                            if (!IsAllowedMethodCall(calledMethod, compilation))
                                diagnostics.Add(Diagnostic.Create(DisallowedMethodCallError, invocation.GetLocation(), method.Name, calledMethod.ToDisplayString()));
                        }
                        break;

                    case ObjectCreationExpressionSyntax objCreation:
                        // GetTypeInfo(objCreation) 优先：VS/MSBuild 的 Roslyn 对嵌套类型
                        // （如 NativeEventJobTest.DeathSignal）+ object-initializer 的 .Type
                        // 解析会返回 null，导致漏检 managed 类型；对整个表达式取类型两种引擎都可靠。
                        var createdType = semanticModel.GetTypeInfo(objCreation).Type
                                       ?? semanticModel.GetTypeInfo(objCreation.Type).Type;
                        if (createdType != null && !IsUnmanagedType(createdType))
                            diagnostics.Add(Diagnostic.Create(ManagedObjectCreationError, objCreation.GetLocation(), method.Name, createdType.ToDisplayString()));
                        break;

                    case IdentifierNameSyntax identifier:
                        var typeInfo = semanticModel.GetTypeInfo(identifier);
                        var idSymbolInfo = semanticModel.GetSymbolInfo(identifier);
                        if (idSymbolInfo.Symbol is ITypeSymbol || idSymbolInfo.Symbol is IMethodSymbol)
                            break;
                        if (typeInfo.Type != null && typeInfo.Type.IsReferenceType && typeInfo.Type.SpecialType != SpecialType.System_String)
                        {
                            // SendEvent 链中的任何标识符都特放（World.DefaultWorld.SendEvent 等）
                            if (IsInSendEventChain(identifier))
                                break;
                            diagnostics.Add(Diagnostic.Create(ReferenceTypeUsageError, identifier.GetLocation(), method.Name, typeInfo.Type.ToDisplayString()));
                        }
                        break;
                }
            }

            return diagnostics.Count == 0;
        }

        public static bool ValidateJobStruct(INamedTypeSymbol structSymbol, Compilation compilation, out List<Diagnostic> diagnostics)
        {
            diagnostics = new List<Diagnostic>();

            if (!structSymbol.IsValueType)
                diagnostics.Add(Diagnostic.Create(InvalidJobTypeError, structSymbol.Locations.FirstOrDefault(), structSymbol.Name));

            bool isChunkJob = structSymbol.AllInterfaces.Any(i => SymbolHelper.IsEntJoyJobInterface(i, Config.IJobChunk));
            bool isEntityJob = structSymbol.AllInterfaces.Any(i => SymbolHelper.IsEntJoyJobInterface(i, Config.IJobEntity));
            bool isBatchJob = structSymbol.AllInterfaces.Any(i => SymbolHelper.IsEntJoyJobInterface(i, Config.IJobParallelForBatch));
            bool implementsJob = structSymbol.AllInterfaces.Any(i =>
                SymbolHelper.IsEntJoyJobInterface(i, Config.IJob) || SymbolHelper.IsEntJoyJobInterface(i, Config.IJobParallelFor) ||
                SymbolHelper.IsEntJoyJobInterface(i, Config.IJobFor) || SymbolHelper.IsEntJoyJobInterface(i, Config.IJobParallelForBatch) ||
                SymbolHelper.IsEntJoyJobInterface(i, Config.IJobChunk) ||
                SymbolHelper.IsEntJoyJobInterface(i, Config.IJobEntity));
            if (!implementsJob)
                diagnostics.Add(Diagnostic.Create(MissingJobInterfaceError, structSymbol.Locations.FirstOrDefault(), structSymbol.Name));

            foreach (var field in structSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (!IsUnmanagedType(field.Type))
                    diagnostics.Add(Diagnostic.Create(InvalidJobFieldError, field.Locations.FirstOrDefault(), structSymbol.Name, field.Name, field.Type.ToDisplayString()));
            }

            var executeMethod = structSymbol.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            if (executeMethod == null)
            {
                diagnostics.Add(Diagnostic.Create(MissingExecuteMethodError, structSymbol.Locations.FirstOrDefault(), structSymbol.Name));
                return diagnostics.Count == 0;
            }

            if (isEntityJob)
            {
                var attrSymbol = compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute");
                var target = AttributeHelper.GetBackendTarget(structSymbol, attrSymbol);
                if ((target != NativeTranspiler.BackendTarget.Cpp && target != NativeTranspiler.BackendTarget.Ispc) ||
                    executeMethod.Parameters.Length == 0 ||
                    executeMethod.Parameters.Any(p => IsInvalidEntityParam(p)))
                {
                    diagnostics.Add(Diagnostic.Create(InvalidJobEntityError, executeMethod.Locations.FirstOrDefault(), structSymbol.Name));
                }
                // 托管列表模式多值关系（[MultiRelation] 无 MaxSlots）不占 chunk 列 → IJobEntity 无法读取；
                // 定长多槽列模式（[MultiRelation(MaxSlots=N)]）占列，Job 可读，不拦截。
                foreach (var p in executeMethod.Parameters)
                {
                    if (IsManagedMultiRelationType(p.Type, compilation))
                        diagnostics.Add(Diagnostic.Create(MultiRelationAccessError, p.Locations.FirstOrDefault(), executeMethod.Name, p.Type.ToDisplayString()));
                }
            }
            else
            {
                // ─── 属性组合校验（C2）───
                // 生成器只对 IJobChunk / IJobEntity 实现 Vectorize 路径；IJobParallelFor/IJobFor/IJob
                // 落到 else if (IsParallelForJob || IsForJob) / 兜底分支，Vectorize 被**静默丢弃**，
                // 用户以为开了向量化其实没有。这里直接报错而不是放任。
                var attrSymbol = compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute");
                var target = AttributeHelper.GetBackendTarget(structSymbol, attrSymbol);
                var autoSimd = AttributeHelper.GetAutoSIMD(structSymbol, attrSymbol);

                if (autoSimd == NativeTranspiler.AutoSIMD.Vectorize && !isChunkJob && !isEntityJob)
                {
                    diagnostics.Add(Diagnostic.Create(VectorizeRequiresChunkOrEntityError,
                        structSymbol.Locations.FirstOrDefault(), structSymbol.Name));
                }
                // AutoSIMD.Enabled 是 Cpp 后端专有：ISPC 路径用 foreach/gang 自带向量化，
                // 该标志在 ISPC 下无任何读取点（静默丢弃）。
                if (autoSimd == NativeTranspiler.AutoSIMD.Enabled && target == NativeTranspiler.BackendTarget.Ispc)
                {
                    diagnostics.Add(Diagnostic.Create(AutoSimdRequiresCppBackendError,
                        structSymbol.Locations.FirstOrDefault(), structSymbol.Name));
                }
                // 逐 job 精度覆盖只写进 C++ 单元（#define SIMD_MATH_PRECISION），ISPC 侧不读。
                if (AttributeHelper.GetMathPrecision(structSymbol, attrSymbol) != NativeTranspiler.SimdMathPrecision.Fastest
                    && target != NativeTranspiler.BackendTarget.Cpp)
                {
                    diagnostics.Add(Diagnostic.Create(MathPrecisionRequiresCppBackendError,
                        structSymbol.Locations.FirstOrDefault(), structSymbol.Name));
                }
                // CppMathLib.fast 只对 Cpp 后端生效（控制 /fp:fast 单元分组）。
                if (AttributeHelper.HasFastCppMathLib(structSymbol, attrSymbol)
                    && target != NativeTranspiler.BackendTarget.Cpp)
                {
                    diagnostics.Add(Diagnostic.Create(CppMathLibRequiresCppBackendError,
                        structSymbol.Locations.FirstOrDefault(), structSymbol.Name));
                }
                // UseISPC_MT 只对 ISPC 后端生效。
                if (AttributeHelper.HasUseISPC_MT(structSymbol, attrSymbol)
                    && target != NativeTranspiler.BackendTarget.Ispc)
                {
                    diagnostics.Add(Diagnostic.Create(UseIspcMtRequiresIspcBackendError,
                        structSymbol.Locations.FirstOrDefault(), structSymbol.Name));
                }
                // F-6：High 没有实现（NativeSIMD_math.h 的 == 2 分支为空）⇒ 产物与 IEEE 相同。
                // 只警告不报错：行为不错，但名字会误导（有人以为它比 Fastest 更精确且仍向量化）。
                if (AttributeHelper.GetMathPrecision(structSymbol, attrSymbol) == NativeTranspiler.SimdMathPrecision.High
                    && target == NativeTranspiler.BackendTarget.Cpp)
                {
                    diagnostics.Add(Diagnostic.Create(SimdMathPrecisionHighUnimplementedWarning,
                        structSymbol.Locations.FirstOrDefault(), structSymbol.Name));
                }
                // F-2：AutoSIMD.Enabled 在本 job 形态上实测无收益（详见描述），显式告知。
                if (autoSimd == NativeTranspiler.AutoSIMD.Enabled && !isChunkJob && !isEntityJob)
                {
                    diagnostics.Add(Diagnostic.Create(AutoSimdNoMeasuredGainWarning,
                        structSymbol.Locations.FirstOrDefault(), structSymbol.Name));
                }
                // F-5：IJobParallelForBatch 的代码生成只有 Cpp 标量一条路径。
                if (isBatchJob && (target != NativeTranspiler.BackendTarget.Cpp || autoSimd != NativeTranspiler.AutoSIMD.Disabled))
                {
                    diagnostics.Add(Diagnostic.Create(ParallelForBatchRequiresCppBackendError,
                        structSymbol.Locations.FirstOrDefault(), structSymbol.Name));
                }
            }

            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

                foreach (var node in methodSyntax.Body.DescendantNodes())
                {
                    switch (node)
                    {
                        case LocalDeclarationStatementSyntax localDecl:
                            if (IsRefLocal(localDecl) && ResolveLocalType(semanticModel, localDecl) == null)
                            {
                                diagnostics.Add(Diagnostic.Create(RefLocalNotSupportedError, localDecl.GetLocation(), executeMethod.Name));
                                break;
                            }
                            var localType = ResolveLocalType(semanticModel, localDecl);
                            if (localType != null && !IsUnmanagedType(localType) && !(isChunkJob && IsAllowedChunkSpanLocal(localDecl, semanticModel)))
                                foreach (var v in localDecl.Declaration.Variables)
                                    diagnostics.Add(Diagnostic.Create(InvalidLocalVariableTypeError, v.GetLocation(), executeMethod.Name, v.Identifier.Text, localType.ToDisplayString()));
                            break;

                        case InvocationExpressionSyntax invocation:
                            var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                            if (symbolInfo.Symbol is IMethodSymbol calledMethod)
                            {
                                if (isChunkJob && IsDisallowedChunkDataAccess(calledMethod))
                                    diagnostics.Add(Diagnostic.Create(DisallowedChunkDataAccessError, invocation.GetLocation(), executeMethod.Name, calledMethod.ToDisplayString()));
                                else if (isChunkJob && calledMethod.Name == Config.GetSharedComponent && calledMethod.TypeArguments.Length == 1)
                                {
                                    // Managed shared component 在 NativeTranspile job 中不允许访问（validator 编译期拦截）
                                    var sharedType = calledMethod.TypeArguments[0];
                                    bool isManagedShared = sharedType.IsReferenceType || (sharedType.TypeKind == TypeKind.Struct && !IsUnmanagedType(sharedType));
                                    if (isManagedShared)
                                        diagnostics.Add(Diagnostic.Create(ManagedSharedComponentError, invocation.GetLocation(), sharedType.ToDisplayString()));
                                }
                                else if (isChunkJob && calledMethod.IsGenericMethod && calledMethod.TypeArguments.Length == 1 &&
                                         (calledMethod.Name == Config.GetComponentDataSpan || calledMethod.Name == Config.GetComponentDataNativeArray || calledMethod.Name == Config.GetComponentDataPtr) &&
                                         IsManagedMultiRelationType(calledMethod.TypeArguments[0], compilation))
                                {
                                    // 托管列表模式多值关系不占 chunk 列 → IJobChunk 无法按列读取；定长多槽列模式可读，不拦截
                                    diagnostics.Add(Diagnostic.Create(MultiRelationAccessError, invocation.GetLocation(), executeMethod.Name, calledMethod.TypeArguments[0].ToDisplayString()));
                                }
                                else if (!IsAllowedMethodCall(calledMethod, compilation, isChunkJob))
                                    diagnostics.Add(Diagnostic.Create(DisallowedMethodCallError, invocation.GetLocation(), executeMethod.Name, calledMethod.ToDisplayString()));
                            }
                            break;

                        case ObjectCreationExpressionSyntax objCreation:
                            // GetTypeInfo(objCreation) 优先（同 ValidateMethod 的注释）
                            var createdType = semanticModel.GetTypeInfo(objCreation).Type
                                           ?? semanticModel.GetTypeInfo(objCreation.Type).Type;
                            if (createdType != null && !IsUnmanagedType(createdType))
                                diagnostics.Add(Diagnostic.Create(ManagedObjectCreationError, objCreation.GetLocation(), executeMethod.Name, createdType.ToDisplayString()));
                            break;

                        case IdentifierNameSyntax identifier:
                            var typeInfo = semanticModel.GetTypeInfo(identifier);
                            var idSymbolInfo = semanticModel.GetSymbolInfo(identifier);
                            if (idSymbolInfo.Symbol is ITypeSymbol || idSymbolInfo.Symbol is IMethodSymbol)
                                break;
                            if (typeInfo.Type != null && typeInfo.Type.IsReferenceType && typeInfo.Type.SpecialType != SpecialType.System_String)
                                diagnostics.Add(Diagnostic.Create(ReferenceTypeUsageError, identifier.GetLocation(), executeMethod.Name, typeInfo.Type.ToDisplayString()));
                            break;
                    }
                }
            }

            return diagnostics.Count == 0;
        }

        /// <summary>IJobEntity Execute 参数校验：Entity 参数（DOTS 式，按值传）豁免 ref/in 要求。</summary>
        private static bool IsInvalidEntityParam(IParameterSymbol p)
        {
            if (NativeTranspiler.IsEntityType(p.Type))
                return false;   // Entity 参数按值传合法
            return (p.RefKind != RefKind.Ref && p.RefKind != RefKind.In) || !IsUnmanagedType(p.Type);
        }

        public static bool IsUnmanagedType(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol)
                return true;
            if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                return true;

            var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (fullName == "EntJoy.Mathematics.float2" ||
                fullName == "EntJoy.Mathematics.int2" ||
                fullName == "EntJoy.Mathematics.uint2")
                return true;

            if (type.IsValueType && !type.IsReferenceType)
            {
                if (type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char or
                    SpecialType.System_SByte or SpecialType.System_Byte or
                    SpecialType.System_Int16 or SpecialType.System_UInt16 or
                    SpecialType.System_Int32 or SpecialType.System_UInt32 or
                    SpecialType.System_Int64 or SpecialType.System_UInt64 or
                    SpecialType.System_Single or SpecialType.System_Double or
                    SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
                    return true;

                if (type.TypeKind == TypeKind.Enum)
                    return true;

                if (type.TypeKind == TypeKind.Struct)
                {
                    var namedType = (INamedTypeSymbol)type;
                    foreach (var field in namedType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    {
                        if (!IsUnmanagedType(field.Type))
                            return false;
                    }
                    return true;
                }
            }
            return false;
        }

        public static bool IsUnmanagedTypeOrVoid(ITypeSymbol type) =>
            type.SpecialType == SpecialType.System_Void || IsUnmanagedType(type);

        /// <summary>
        /// 判断类型是否为 [MultiRelation] 关系组件（实现 EntJoy.ECS.IRelationComponent 且标记 MultiRelationAttribute）。
        /// 分析器不引用 EntJoy.ECS 程序集，通过 MetadataName + 特性名匹配识别。
        /// </summary>
        public static bool IsMultiRelationType(ITypeSymbol type, Compilation compilation)
        {
            if (type is not INamedTypeSymbol namedType || namedType.TypeKind != TypeKind.Struct) return false;
            if (!namedType.AllInterfaces.Any(i => i.ToDisplayString() == Config.TypeIRelationComponent)) return false;
            return namedType.GetAttributes().Any(ad => ad.AttributeClass?.ToDisplayString() == Config.TypeMultiRelationAttribute);
        }

        /// <summary>
        /// 判断是否为「托管列表模式」多值关系（[MultiRelation] 且未指定 MaxSlots 或 MaxSlots &lt; 2）。
        /// 托管模式数据在 EntityManager 托管列表，不进 chunk 列 → Job 不可读，需 NT017 拦截。
        /// 定长多槽列模式（MaxSlots ≥ 2）占 chunk 列，Job 可读，不拦截。
        /// </summary>
        public static bool IsManagedMultiRelationType(ITypeSymbol type, Compilation compilation)
        {
            if (!IsMultiRelationType(type, compilation)) return false;
            foreach (var attr in type.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() != Config.TypeMultiRelationAttribute) continue;
                foreach (var namedArg in attr.NamedArguments)
                {
                    if (namedArg.Key == "MaxSlots" && namedArg.Value.Value is int v && v >= 2)
                        return false;   // 定长多槽列模式 → Job 可读
                }
                return true;   // MaxSlots 缺失或 < 2 → 托管模式
            }
            return true;
        }

        /// <summary>
        /// 检查方法调用是否被允许。
        /// </summary>
        private static bool IsAllowedMethodCall(IMethodSymbol method, Compilation compilation, bool allowChunkMethods = false)
        {
            // 0. SendEvent<T> 特放：NativeTranspiler 翻译为 C++ EventBuffer 写入
            if (method.Name == Config.SendEvent && method.IsGenericMethod)
            {
                var containingType = method.ContainingType;
                if (containingType != null)
                {
                    // 允许：World.SendEvent / EntityManager.SendEvent / EventBus.SendEvent
                    if (SymbolEqualityComparer.Default.Equals(containingType, compilation.GetTypeByMetadataName(Config.TypeWorld)) ||
                        SymbolEqualityComparer.Default.Equals(containingType, compilation.GetTypeByMetadataName(Config.TypeEntityManager)) ||
                        SymbolEqualityComparer.Default.Equals(containingType, compilation.GetTypeByMetadataName(Config.TypeEventBus)))
                        return true;
                }
            }

            // 1. 允许对容器类型的实例方法调用 (NativeList, NativeArray)
            if (!method.IsStatic)
            {
                var containingType = method.ContainingType;
                if (containingType != null && NativeTranspiler.IsEntJoyNativeContainerType(containingType))
                    return true;
                if (allowChunkMethods && SymbolEqualityComparer.Default.Equals(containingType, compilation.GetTypeByMetadataName(Config.TypeArchetypeChunk)) &&
                    (method.Name == Config.GetComponentDataNativeArray || method.Name == Config.GetComponentDataSpan || method.Name == Config.GetEnableBitMapPtr))
                    return true;
                return false;
            }

            // 2. 专门放行 System.Runtime.CompilerServices.Unsafe 类的所有静态方法
            var containingTypeName = method.ContainingType?.ToDisplayString();
            if (containingTypeName == "System.Runtime.CompilerServices.Unsafe")
                return true;

            // 3. 系统白名单（静态方法）
            var fullName = method.ContainingType.ToDisplayString() + "." + method.Name;
            if (AllowedStaticMethods.Contains(fullName))
                return true;

            // 4. 标记了 [NativeTranspile] 的方法
            if (method.GetAttributes().Any(ad => ad.AttributeClass?.Name == Config.NativeTranspileAttribute))
                return true;

            // 5. 同一程序集中的用户定义静态方法，且签名符合非托管要求
            if (SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, compilation.Assembly))
            {
                if (!IsUnmanagedTypeOrVoid(method.ReturnType))
                    return false;
                foreach (var p in method.Parameters)
                {
                    if (!IsUnmanagedType(p.Type))
                        return false;
                }
                return true;
            }

            return false;
        }

        private static bool IsDisallowedChunkDataAccess(IMethodSymbol method)
        {
            if (method.ContainingType?.ToDisplayString() != Config.TypeArchetypeChunk)
                return false;
            return method.Name == Config.GetComponentDataPtr;
        }

        private static bool IsAllowedChunkSpanLocal(LocalDeclarationStatementSyntax localDecl, SemanticModel semanticModel)
        {
            var localType = semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
            if (localType?.Name != Config.Span || localType.ContainingNamespace?.ToDisplayString() != "System")
                return false;

            foreach (var variable in localDecl.Declaration.Variables)
            {
                if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation)
                    return false;
                if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                    return false;
                if (method.ContainingType?.ToDisplayString() != Config.TypeArchetypeChunk || method.Name != Config.GetComponentDataSpan)
                    return false;
            }

            return true;
        }

        public static List<IFieldSymbol> GetConditionalReadOnlyFields(INamedTypeSymbol jobStruct, SemanticModel semanticModel)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            if (executeMethod == null)
                return new List<IFieldSymbol>();

            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null)
                return new List<IFieldSymbol>();

            var fields = jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();

            var assignedFields = new HashSet<IFieldSymbol>();
            foreach (var node in methodSyntax.Body.DescendantNodes())
            {
                ISymbol? assignedSymbol = null;
                if (node is AssignmentExpressionSyntax assignment)
                    assignedSymbol = semanticModel.GetSymbolInfo(assignment.Left).Symbol;
                else if (node is PostfixUnaryExpressionSyntax postfix &&
                         (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
                    assignedSymbol = semanticModel.GetSymbolInfo(postfix.Operand).Symbol;
                else if (node is PrefixUnaryExpressionSyntax prefix &&
                         (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
                    assignedSymbol = semanticModel.GetSymbolInfo(prefix.Operand).Symbol;

                if (assignedSymbol is IFieldSymbol field && fields.Contains(field))
                    assignedFields.Add(field);
            }

            var conditionalReadFields = new HashSet<IFieldSymbol>();
            foreach (var node in methodSyntax.Body.DescendantNodes())
            {
                if (node is IdentifierNameSyntax id)
                {
                    var symbol = semanticModel.GetSymbolInfo(id).Symbol;
                    if (symbol is IFieldSymbol field && fields.Contains(field))
                    {
                        if (IsInConditionContext(id))
                            conditionalReadFields.Add(field);
                    }
                }
            }

            return fields.Where(f => !assignedFields.Contains(f) && conditionalReadFields.Contains(f)).ToList();
        }

        private static bool IsInConditionContext(SyntaxNode node)
        {
            var parent = node.Parent;
            while (parent != null)
            {
                if (parent is IfStatementSyntax ifStmt && ifStmt.Condition.Contains(node)) return true;
                if (parent is WhileStatementSyntax whileStmt && whileStmt.Condition.Contains(node)) return true;
                if (parent is DoStatementSyntax doStmt && doStmt.Condition.Contains(node)) return true;
                if (parent is ForStatementSyntax forStmt && forStmt.Condition != null && forStmt.Condition.Contains(node)) return true;
                if (parent is ConditionalExpressionSyntax cond && cond.Condition.Contains(node)) return true;
                if (parent is BinaryExpressionSyntax binary &&
                    (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression)))
                    return true;
                parent = parent.Parent;
            }
            return false;
        }

        /// <summary>检查标识符是否在 SendEvent 调用链中（如 World.DefaultWorld.SendEvent / 裸 SendEvent）。</summary>
        private static bool IsInSendEventChain(IdentifierNameSyntax identifier)
        {
            // 情况 1：裸调用 SendEvent(...) — identifier 本身就是被调用的方法名
            if (identifier.Identifier.Text == Config.SendEvent)
            {
                if (identifier.Parent is InvocationExpressionSyntax inv && inv.Expression == identifier)
                    return true;
            }

            // 情况 2：MemberAccess 链（World.DefaultWorld.SendEvent / ECS.SendEvent）
            SyntaxNode? current = identifier;
            while (current != null)
            {
                if (current is MemberAccessExpressionSyntax mac
                    && mac.Name.Identifier.Text == Config.SendEvent)
                    return true;
                current = current.Parent;
            }
            return false;
        }
    }
}
