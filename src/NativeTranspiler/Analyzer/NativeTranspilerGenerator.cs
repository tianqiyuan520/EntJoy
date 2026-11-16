using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    [Generator]
    public partial class NativeTranspilerGenerator : IIncrementalGenerator
    {
        // 不参与发射的类型见 NativeApiSurface.SkipsEmission。

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(ctx =>
                ctx.AddSource($"{RuntimeApi.AttributeName}Attribute.g.cs", RuntimeApi.GenerateAttributeSource()));

            var optionsProvider = context.AnalyzerConfigOptionsProvider;

            var methodProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (n, _) => n is MethodDeclarationSyntax m &&
                                         m.AttributeLists.Count > 0 &&
                                         m.Modifiers.Any(SyntaxKind.StaticKeyword),
                    transform: (ctx, ct) => GetMethodSymbol(ctx, ct))
                .Where(m => m != null).Collect();

            var structProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (n, _) => n is StructDeclarationSyntax s && s.AttributeLists.Count > 0,
                    transform: (ctx, ct) => GetJobStructSymbol(ctx, ct))
                .Where(s => s != null).Collect();

            var combined = context.CompilationProvider
                .Combine(optionsProvider)
                .Combine(methodProvider)
                .Combine(structProvider)
                .Select((tuple, _) => new NativeTranspilerContext(
                    tuple.Left.Left.Left, tuple.Left.Left.Right,
                    tuple.Left.Right, tuple.Right));

            context.RegisterSourceOutput(combined, (spc, ctx) =>
            {
                // 生成器自身异常的诊断出口：
                // 没有这层保护时，任何 NRE 只会变成 Roslyn 的
                //   `CS8785: 生成器"NativeTranspilerGenerator"未能生成源 … NullReferenceException`
                // —— 只有异常类型名、没有行号，定位只能靠二分（实测代价：一整轮）。
                // 这里捕获后上报 NT026（含异常消息 + 调用栈），并落盘一份完整 ToString()。
                try
                {
                //   0) 空集短路
                //   1) Validate  —— 收集依赖 + NativeTranspileValidator 校验，出错即停
                // 消费者基准），故先以文档化阶段标记落地；无行为变更。
                if (ctx.MethodSymbols.IsEmpty && ctx.JobStructSymbols.IsEmpty)
                {
                    // 即使一个 job 都没标属性，也要跑意图检查：`strict` 策略下"整包都忘了属性"正是要抓的形态。
                    JobIntentChecker.Report(spc, ctx.Compilation, ctx.Options.GlobalOptions, ctx.JobStructSymbols);
                    return;
                }

                // NT032：把"漏写 [NativeTranspile]"从静默降级变成可判定的第三种状态（判据与策略见 JobIntentChecker）。
                JobIntentChecker.Report(spc, ctx.Compilation, ctx.Options.GlobalOptions, ctx.JobStructSymbols);

                var methodsToGenerate = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                var allErrors = new List<Diagnostic>();

                foreach (var method in ctx.MethodSymbols)
                {
                    if (method == null) continue;
                    CollectMethodDependencies(method, ctx.Compilation, methodsToGenerate, allErrors);
                }
                // Job Execute 内部的同程序集静态方法调用也要收集依赖（生成其 C++ 定义），
                // 否则调用点引用了不存在的函数（use of undeclared identifier）。
                // 保护（Execute 自身不作为独立函数生成），故单独遍历 Execute 体内的静态调用。
                foreach (var job in ctx.JobStructSymbols)
                {
                    if (job == null) continue;
                    CollectJobExecuteDependencies(job, ctx.Compilation, methodsToGenerate, allErrors);
                }
                foreach (var method in ctx.MethodSymbols)
                {
                    if (method == null) continue;
                    // NT-05：诊断一律收（含 warning）；是否"校验失败"只看 error，见下面 invalidJobs 的判定。
                    NativeTranspileValidator.ValidateMethod(method, ctx.Compilation, out var diags);
                    allErrors.AddRange(diags);
                }
                var invalidJobs = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                int jobLevelErrorCount = 0;
                // B3：AutoSIMD = Enabled 在 IJobParallelFor/IJobFor/IJob 上默认报 error（NT024，实测慢
                // ~10%）。要开必须显式声明"我已量过"：MSBuild 属性 EntJoyAutoSimdMeasured=true
                // （由 EntJoy.Jobs.props 的 CompilerVisibleProperty 传到编译期）。
                bool autoSimdMeasured = ctx.Options.GlobalOptions.TryGetValue("build_property.EntJoyAutoSimdMeasured", out var asmVal)
                    && (string.Equals(asmVal?.Trim(), "true", StringComparison.OrdinalIgnoreCase) || asmVal?.Trim() == "1");
                foreach (var job in ctx.JobStructSymbols)
                {
                    if (job == null) continue;
                    // NT-05：只有 error 才把 job 排除出生成集。
                    // 旧代码用 ValidateJobStruct 的返回值（= diagnostics.Count == 0）判定有效 ⇒
                    // + 误导性 NT028，违反文档「warning 不阻断生成」。
                    NativeTranspileValidator.ValidateJobStruct(job, ctx.Compilation, out var diags, autoSimdMeasured);
                    allErrors.AddRange(diags);
                    int jobErrors = diags.Count(d => d.Severity == DiagnosticSeverity.Error);
                    if (jobErrors > 0)
                    {
                        jobLevelErrorCount += jobErrors;
                        invalidJobs.Add(job);
                    }
                }
                // 诊断一律上报；只有 Error 才终止生成。
                // （NT023/NT024 是"事实告知"类 Warning —— 既不能憋着不报，也不能因为它们而整个项目不生成产物。）
                foreach (var diag in allErrors) spc.ReportDiagnostic(diag);
                // E-1（DX 修复）：单个 job 的校验错误不再终止整批生成。
                // 旧行为是"只要有 job 校验失败 → 整个 NativeTranspiler.Bindings.g.cs 不产出"，
                // 下游表现为几十条 CS0234「找不到 NativeTranspiler.Bindings」，把唯一有用的 NT008
                // （例如漏写 `using EntJoy.JobSystem;` 导致 job 没实现任何接口）埋在噪声里。
                // 现在：把校验失败的 job 排除出生成集，其余 job 照常产出绑定；Error 仍然让编译失败，
                // 但错误信息只剩 NT008/NT028 这一组，且不再有 CS0234 级联。
                int fatalErrorCount = allErrors.Count(d => d.Severity == DiagnosticSeverity.Error) - jobLevelErrorCount;
                if (fatalErrorCount > 0)
                    return;

                var outputDir = Path.Combine(ctx.GetProjectDirectory(), "NativeTranspiler_Generated");
                Directory.CreateDirectory(outputDir);
                // D1：录制本次写出的全部产物文件名（含"内容未变跳过写入"的）
                CodeGenIo.BeginOutputTracking();

                var cppFiles = new List<string>();
                var fastMathCppFiles = new HashSet<string>();
                // AutoSIMD 生成单元：需要 IEEE-754 精确浮点语义（454229d EC2/EC8/E5/E8/E11），
                // 精确单元独立编译为对象库，按名称查找的导出也会完整链接进 NativeTranspiled.dll。
                var autoSimdCppFiles = new HashSet<string>();
                var ispcFiles = new List<(string fileName, NativeTranspiler.IspcMathLib mathLib)>();
                var attrSymbol = ctx.Compilation.GetTypeByMetadataName($"{RuntimeApi.AttributeNamespace}.{RuntimeApi.AttributeName}Attribute");

                // 收集被标记的方法和 Job 结构体（校验失败的 job 已排除，见上）
                var validMarkedMethods = ctx.MethodSymbols.Where(m => m != null).Cast<IMethodSymbol>();
                var validJobs = ctx.JobStructSymbols.Where(j => j != null && !invalidJobs.Contains(j!)).Cast<INamedTypeSymbol>();
                if (invalidJobs.Count > 0)
                    spc.ReportDiagnostic(Diagnostic.Create(NativeTranspileValidator.PartialBindingsWarning,
                        invalidJobs.First().Locations.FirstOrDefault(), invalidJobs.Count,
                        string.Join(", ", invalidJobs.Select(j => j!.Name))));

                // 收集用户自定义结构体（用于生成 ISPC 头文件）
                var userStructs = CollectUserStructTypes(validMarkedMethods, validJobs, ctx.Compilation);

                // SendEvent 元数据：Job 全名 → 事件类型全名列表
                var allJobEventTypes = new Dictionary<string, List<string>>();

                bool anyIspc = ctx.MethodSymbols.Any(m => m != null && GetBackendTarget(m, attrSymbol) == NativeTranspiler.BackendTarget.Ispc)
                             || ctx.JobStructSymbols.Any(j => j != null && GetBackendTarget(j, attrSymbol) == NativeTranspiler.BackendTarget.Ispc);

                // 布局加固（C++ 与 ISPC 共用）：Explicit / Pack<8 的 struct 无法在自然对齐下复现
                // C# 布局（ISPC 不支持 #pragma pack；C++ 生成器也只用自然对齐），
                // 字段会错位 → fail-fast 拒绝生成（NT016），避免 static_assert 用自然尺寸静默放行。
                foreach (var userStruct in userStructs)
                {
                    if (NativeTranspiler.HasUnsupportedIspcLayout(userStruct, out string reason))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(NativeTranspileValidator.UnsupportedStructLayoutError,
                            userStruct.Locations.FirstOrDefault(), userStruct.Name, reason));
                        return;
                    }
                }

                // 为用户自定义结构体生成 C++ 头文件和 ISPC 头文件
                foreach (var userStruct in userStructs)
                {
                    var headerName = NativeTranspiler.GetStructHeaderFileName(userStruct);
                    var cppHeaderPath = Path.Combine(outputDir, $"{headerName}.h");
                    CodeGenIo.WriteAllTextWithRetry(cppHeaderPath, NativeTranspiler.GenerateCppStructDefinition(userStruct));
                }

                if (anyIspc)
                {
                    var commonIspcPath = Path.Combine(outputDir, "EntJoyCommon.ispc");
                    CodeGenIo.WriteAllTextWithRetry(commonIspcPath, IspcCommonSource.Generate());

                    // 为用户自定义结构体生成 ISPC 头文件
                    foreach (var userStruct in userStructs)
                    {
                        var headerName = NativeTranspiler.GetStructHeaderFileName(userStruct);
                        var ispcStructPath = Path.Combine(outputDir, $"{headerName}.ispc");
                        CodeGenIo.WriteAllTextWithRetry(ispcStructPath, NativeTranspiler.GenerateIspcStructDefinition(userStruct));
                    }
                }

                // ISPC helper（lane 可调用）：ISPC job/方法体内调用的静态方法在 ISPC 侧
                // 没有可链接符号，必须在同一翻译单元内提供 ISPC 版本。这些文件只被调用方
                // .ispc 以 #include 引入，不加入 ispcFiles（不单独编译）。
                if (anyIspc)
                {
                    var ispcHelperSeen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                    var ispcHelperMethods = new List<IMethodSymbol>();
                    void CollectHelpers(IEnumerable<IMethodSymbol> deps)
                    {
                        foreach (var dep in deps)
                            if (ispcHelperSeen.Add(dep)) ispcHelperMethods.Add(dep);
                    }

                    foreach (var job in ctx.JobStructSymbols)
                        if (job != null && GetBackendTarget(job, attrSymbol) == NativeTranspiler.BackendTarget.Ispc)
                            CollectHelpers(IspcGenerator.CollectIspcHelperClosure(job, ctx.Compilation));
                    foreach (var method in ctx.MethodSymbols)
                        if (method != null && GetBackendTarget(method, attrSymbol) == NativeTranspiler.BackendTarget.Ispc)
                            CollectHelpers(IspcGenerator.CollectIspcHelperClosure(method, ctx.Compilation));

                    foreach (var helper in ispcHelperMethods)
                        CodeGenIo.WriteAllTextWithRetry(
                            Path.Combine(outputDir, $"{IspcGenerator.GetIspcHelperFileName(helper)}.ispc"),
                            IspcGenerator.GenerateIspcHelperSource(helper, ctx.Compilation));
                }

                // 处理静态方法
                foreach (var method in methodsToGenerate)
                {
                    var target = GetBackendTarget(method, attrSymbol);
                    var baseName = CppGenerator.GetCppFunctionName(method);
                    var mathLib = GetMathLib(method, attrSymbol);

                    if (target == NativeTranspiler.BackendTarget.Ispc)
                    {
                        var ispcSource = IspcGenerator.GenerateIspcSource(method, ctx.Compilation, userStructs);
                        var cppWrapper = IspcGenerator.GenerateCppWrapper(method);

                        string ispcSrcPath = Path.Combine(outputDir, $"{baseName}.ispc");
                        string wrapperCppPath = Path.Combine(outputDir, $"{baseName}_wrapper.cpp");

                        CodeGenIo.WriteAllTextWithRetry(ispcSrcPath, ispcSource);
                        CodeGenIo.WriteAllTextWithRetry(wrapperCppPath, cppWrapper);
                        ispcFiles.Add(($"{baseName}.ispc", mathLib));
                        cppFiles.Add($"{baseName}_wrapper.cpp");

                        if (HasUseISPC_MT(method, attrSymbol))
                        {
                            var mtIspcSource = IspcGenerator.GenerateIspcMTSource(method, ctx.Compilation, userStructs);
                            var mtCppWrapper = IspcGenerator.GenerateCppWrapperMT(method);

                            string mtIspcPath = Path.Combine(outputDir, $"{baseName}_mt.ispc");
                            string mtWrapperPath = Path.Combine(outputDir, $"{baseName}_mt_wrapper.cpp");

                            CodeGenIo.WriteAllTextWithRetry(mtIspcPath, mtIspcSource);
                            CodeGenIo.WriteAllTextWithRetry(mtWrapperPath, mtCppWrapper);
                            ispcFiles.Add(($"{baseName}_mt.ispc", mathLib));
                            cppFiles.Add($"{baseName}_mt_wrapper.cpp");
                        }
                    }
                    else
                    {
                        var header = CppGenerator.GenerateHeader(method);
                        var methodAutoSIMD = AttributeHelper.GetAutoSIMD(method, attrSymbol);
                        var impl = CppGenerator.GenerateImplementation(method, ctx.Compilation, userStructs, methodAutoSIMD);

                        string hPath = Path.Combine(outputDir, $"{baseName}.h");
                        string cppPath = Path.Combine(outputDir, $"{baseName}.cpp");

                        // 纯助手（非 [NativeTranspile] 入口）= 被入口/job 体调用的静态方法。
                        // 它们不需要被 C# 通过 GetExport 解析 ⇒ 不得发 dllexport（clang 对 dllexport 不内联，
                        // 实测 7 处 callq 即使在单 TU 下也保留）⇒ 改为 .h 内 static inline。
                        bool isHelper = !ctx.MethodSymbols.Contains(method)
                            && methodAutoSIMD != NativeTranspiler.AutoSIMD.Enabled
                            && !HasFastCppMathLib(method, attrSymbol);

                        if (isHelper)
                        {
                            var inlineHeader = CppGenerator.GenerateInlineHelperHeader(
                                method, ctx.Compilation, userStructs, methodAutoSIMD);
                            CodeGenIo.WriteAllTextWithRetry(hPath, inlineHeader);
                            // 不再发独立 dllexport TU：删掉陈旧 .cpp（否则它能被旧列表里的路径编进去）
                            if (File.Exists(cppPath)) File.Delete(cppPath);
                        }
                        else
                        {
                            CodeGenIo.WriteAllTextWithRetry(hPath, header);
                            CodeGenIo.WriteAllTextWithRetry(cppPath, impl);
                            var cppFile = baseName + ".cpp";
                            cppFiles.Add(cppFile);
                            if (methodAutoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                                autoSimdCppFiles.Add(cppFile);
                            if (HasFastCppMathLib(method, attrSymbol) || methodAutoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                                fastMathCppFiles.Add(cppFile);
                        }
                    }
                }

                // 处理 Job 结构：每个 job 只产出一个编译单元（实现与 adapter 同文件，批入口才能内联）。
                void EmitIspcJob(INamedTypeSymbol job, string ispcBase, string plainBase, NativeTranspiler.IspcMathLib mathLib)
                {
                    CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{plainBase}.h"));
                    CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{plainBase}.cpp"));

                    bool useIspcMt = HasUseISPC_MT(job, attrSymbol);
                    bool mtProvidesScheduledAdapter = useIspcMt && CppJobGenerator.IsChunkScheduledJob(job);

                    if (!mtProvidesScheduledAdapter)
                    {
                        var ispcSource = IspcGenerator.GenerateIspcSource(job, ctx.Compilation, userStructs);
                        var cppWrapper = IspcGenerator.GenerateCppWrapper(job, ctx.Compilation);

                        // chunk 形态的 adapter 由 chunk wrapper 自带，不重复追加。
                        if (!CppJobGenerator.IsChunkScheduledJob(job))
                        {
                            var (adapterCode, evtTypes) = CppJobGenerator.GenerateJobAdapter(job, ctx.Compilation);
                            cppWrapper += adapterCode;
                            if (evtTypes.Count > 0)
                                allJobEventTypes[job.ToDisplayString()] = evtTypes;
                        }

                        CodeGenIo.WriteAllTextWithRetry(Path.Combine(outputDir, $"{ispcBase}.ispc"), ispcSource);
                        CodeGenIo.WriteAllTextWithRetry(Path.Combine(outputDir, $"{ispcBase}_wrapper.cpp"), cppWrapper);

                        ispcFiles.Add(($"{ispcBase}.ispc", mathLib));
                        cppFiles.Add($"{ispcBase}_wrapper.cpp");
                    }
                    else
                    {
                        CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}.ispc"));
                        CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}_wrapper.cpp"));
                    }

                    if (useIspcMt)
                    {
                        var mtIspcSource = IspcGenerator.GenerateIspcMTSource(job, ctx.Compilation, userStructs);
                        var mtCppWrapper = IspcGenerator.GenerateCppWrapperMT(job, ctx.Compilation);

                        CodeGenIo.WriteAllTextWithRetry(Path.Combine(outputDir, $"{ispcBase}_mt.ispc"), mtIspcSource);
                        CodeGenIo.WriteAllTextWithRetry(Path.Combine(outputDir, $"{ispcBase}_mt_wrapper.cpp"), mtCppWrapper);
                        ispcFiles.Add(($"{ispcBase}_mt.ispc", mathLib));
                        cppFiles.Add($"{ispcBase}_mt_wrapper.cpp");
                    }
                }

                void EmitCppJob(INamedTypeSymbol job, string plainBase, string ispcBase)
                {
                    CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{plainBase}.h"));
                    CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{plainBase}_Adapter.cpp"));
                    CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}.ispc"));
                    CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}_wrapper.cpp"));
                    CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}_mt.ispc"));
                    CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}_mt_wrapper.cpp"));

                    var jobAutoSIMD = AttributeHelper.GetAutoSIMD(job, attrSymbol);
                    bool preciseVectorize = jobAutoSIMD == NativeTranspiler.AutoSIMD.Vectorize
                        && !HasFastCppMathLib(job, attrSymbol);

                    var impl = CppJobGenerator.GenerateJobImplementation(job, ctx.Compilation);
                    var (adapterCode, evtTypes) = CppJobGenerator.GenerateJobAdapter(job, ctx.Compilation);

                    var cppFile = plainBase + ".cpp";
                    CodeGenIo.WriteAllTextWithRetry(Path.Combine(outputDir, cppFile), impl + adapterCode);
                    cppFiles.Add(cppFile);
                    if (jobAutoSIMD == NativeTranspiler.AutoSIMD.Enabled || preciseVectorize)
                        autoSimdCppFiles.Add(cppFile);
                    if (HasFastCppMathLib(job, attrSymbol) || jobAutoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                        fastMathCppFiles.Add(cppFile);

                    // SendEvent: 收集事件类型元数据
                    if (evtTypes.Count > 0)
                        allJobEventTypes[job.ToDisplayString()] = evtTypes;
                }

                foreach (var job in ctx.JobStructSymbols)
                {
                    if (job == null) continue;
                    var ispcBase = IspcGenerator.GetIspcBaseName(job);
                    var plainBase = CppJobGenerator.GetCppJobFunctionName(job);
                    if (GetBackendTarget(job, attrSymbol) == NativeTranspiler.BackendTarget.Ispc)
                        EmitIspcJob(job, ispcBase, plainBase, GetMathLib(job, attrSymbol));
                    else
                        EmitCppJob(job, plainBase, ispcBase);
                }

                const string abiFile = "NativeTranspiledAbi.cpp";
                cppFiles.Add(abiFile);
                CodeGenIo.WriteAllTextWithRetry(Path.Combine(outputDir, abiFile),
                    "#include <cstdint>\n#include \"NativeGeneratedSupport.h\"\n"
                    + "\nGENERATED_API uint32_t CALLINGCONVENTION NativeTranspiled_GetAbiVersion() { return 4u; }\n"
                    + "GENERATED_API int CALLINGCONVENTION NativeTranspiled_GetChunkContextHeaderSize() { return (int)sizeof(__EntJoyChunkContextHeader); }\n");
                // run_ispc.bat 由 CMake 的 ISPC 规则取代：同一条 ispc 命令行不再生成两遍。
                CodeGenIo.DeleteIfExists(Path.Combine(outputDir, "run_ispc.bat"));

                // prebuilt-native 模式：包消费时由 props 注入（NativeDll 预编译 + 头 + tasksys 所在目录）。
                // 提升到方法作用域：CMakeLists 生成与 run_clangcl.bat 生成两处都要用。
                string? prebuiltNativeDir = null;
                if (ctx.Options.GlobalOptions.TryGetValue("build_property.EntJoyPrebuiltNativeDir", out var configuredPrebuilt) && !string.IsNullOrWhiteSpace(configuredPrebuilt))
                {
                    prebuiltNativeDir = Path.GetFullPath(configuredPrebuilt);
                }

                // 只在内容变化时写入 CMakeLists.txt，避免触发 CMake reconfigure
                if (cppFiles.Count > 0 || ispcFiles.Count > 0)
                {
                    var globalOptions = ctx.Options.GlobalOptions;
            string nativeDllDir;
            string solutionBinDir;
            // 优先读宿主 csproj 显式配置的 EntJoyNativeDllDir（源生成器 + 编译任务共用同一属性）
            if (globalOptions.TryGetValue("build_property.EntJoyNativeDllDir", out var configuredDllDir) && !string.IsNullOrWhiteSpace(configuredDllDir))
            {
                nativeDllDir = Path.GetFullPath(configuredDllDir);
                solutionBinDir = Path.GetFullPath(Path.Combine(ctx.GetProjectDirectory(), "..", "..", "bin"));
            }
            else if (prebuiltNativeDir != null)
            {
                nativeDllDir = prebuiltNativeDir;
                solutionBinDir = Path.GetFullPath(Path.Combine(ctx.GetProjectDirectory(), "..", "..", "bin"));
            }
            else
            {
                // 未配置时自动探测仓库根（从输出目录向上找 src/NativeDll/Exports.cpp）
                var repoRoot = CodeGenIo.FindRepoRoot(ctx.GetProjectDirectory());
                if (repoRoot == null)
                    repoRoot = Path.GetFullPath(Path.Combine(ctx.GetProjectDirectory(), "..", ".."));
                solutionBinDir = Path.GetFullPath(Path.Combine(repoRoot, "bin"));
                nativeDllDir = Path.GetFullPath(Path.Combine(repoRoot, "src", "NativeDll"));
            }
                    var relativeNativeDllDir = CodeGenIo.GetRelativePath(outputDir, nativeDllDir).Replace("\\", "/");
                    bool hasFastMath = fastMathCppFiles.Count > 0;
                    string cmakePath = Path.Combine(outputDir, "CMakeLists.txt");
                    // 增量友好排序：保留上一次 CMakeLists.txt 中已有的源文件顺序，新增文件追加到末尾。
                    // 配合 CMake Unity Build（批大小 8），新增 job/method 不会打乱既有批的成员，
                    // 从而 native 侧只需重编新 TU + 末尾批，而不是把所有批重编一遍。
                    var existingCppOrder = CmakeGenerator.ReadExistingCppSourceOrder(cmakePath);
                    var cmakeContent = CmakeGenerator.Generate(cppFiles, ispcFiles, fastMathCppFiles, autoSimdCppFiles, outputDir, solutionBinDir, relativeNativeDllDir, hasFastMath, existingCppOrder, prebuiltNativeDir);
                    // 如果内容未变则不写入，避免时间戳更新触发 CMake 重新 configure
                    if (!File.Exists(cmakePath) || File.ReadAllText(cmakePath) != cmakeContent)
                    {
                        CodeGenIo.WriteAllTextWithRetry(cmakePath, cmakeContent);
                    }
                }

                // D1：清理陈旧生成物（见 PruneStaleGeneratedFiles 注释）
                // "本次产物" 直接取自 CodeGenIo 录制到的写入集合 —— 不靠手写文件名推导，
                // ⚠ 位置必须在 所有 CodeGenIo 写入之后（含 run_clangcl.bat / CMakeLists.txt）。
                var trackedOutputs = CodeGenIo.EndOutputTracking();
                if (trackedOutputs != null && cppFiles.Count + ispcFiles.Count > 0)
                {
                    var expected = new HashSet<string>(trackedOutputs, StringComparer.OrdinalIgnoreCase);
                    foreach (var f in cppFiles) expected.Add(f);
                    foreach (var f in ispcFiles) expected.Add(f.fileName);
                    PruneStaleGeneratedFiles(outputDir, expected);
                }

                // 生成 run_clangcl.bat：用 ClangCL (LLVM 后端) 编译 NativeDll
                {
                    var repoRoot2 = CodeGenIo.FindRepoRoot(ctx.GetProjectDirectory());
                    string solBinDir = repoRoot2 != null
                        ? Path.GetFullPath(Path.Combine(repoRoot2, "bin"))
                        : Path.GetFullPath(Path.Combine(ctx.GetProjectDirectory(), "..", "..", "bin"));
                    var clangBatPath = Path.Combine(outputDir, "run_clangcl.bat");
                    var clangBat = new StringBuilder();
                    clangBat.AppendLine("@echo off");
                    clangBat.AppendLine("cd /d \"%~dp0\"");
                    clangBat.AppendLine("echo Cleaning old build cache...");
                    clangBat.AppendLine("if exist build rmdir /s /q build >nul 2>nul");
                    clangBat.AppendLine("echo Configuring ClangCL (LLVM backend)...");
                    clangBat.AppendLine("cmake -B build -G \"Visual Studio 17 2022\" -T ClangCL -A x64 -DNATIVE_SIMD_LEVEL=AVX2");
                    clangBat.AppendLine("if errorlevel 1 exit /b 1");
                    clangBat.AppendLine("echo Building NativeDll + NativeTranspiled with ClangCL...");
                    clangBat.AppendLine(prebuiltNativeDir == null
                        ? "cmake --build build --config Release --target NativeDll --target NativeTranspiled"
                        : "cmake --build build --config Release --target NativeTranspiled");
                    clangBat.AppendLine("if errorlevel 1 exit /b 1");
                    if (prebuiltNativeDir == null)
                        clangBat.AppendLine("copy /Y build\\Release\\NativeDll.dll \"" + solBinDir + "\"");
                    else
                        clangBat.AppendLine("copy /Y \"" + Path.Combine(prebuiltNativeDir, "NativeDll.dll").Replace("\\", "\\\\") + "\" \"" + solBinDir + "\"");
                    clangBat.AppendLine("copy /Y build\\Release\\NativeTranspiled.dll \"" + solBinDir + "\"");
                    clangBat.AppendLine("echo Done. NativeDll.dll + NativeTranspiled.dll copied to " + solBinDir);
                    // 内容未变则不写：避免时间戳更新触发无关重编/检查
                    string clangBatContent = clangBat.ToString();
                    if (!File.Exists(clangBatPath) || File.ReadAllText(clangBatPath) != clangBatContent)
                        CodeGenIo.WriteAllTextWithRetry(clangBatPath, clangBatContent);
                }

                // 生成器版本戳（F-11）
                // 记录"这批产物是哪个内容的生成器生成的"。NativeCompileTask 用它判定
                // "生成器改了但产物没重新生成"（Roslyn 内容哈希门控会让这条静默发生），并据此报 warning。
                // ⚠ 不要把它加进 native 依赖哈希（内容含时间戳，会让每次构建都重编 CMake）。
                WriteGeneratorStamp(outputDir);

                var bindingsCode = BindingsGenerator.GenerateBindingsClass(validMarkedMethods, validJobs, ctx.Compilation);
                spc.AddSource("NativeTranspiler.Bindings.g.cs", bindingsCode);

                // 不变量自校验：生成物与"是否引用 EntJoy.ECS"必须一致（NT029 / NT030）
                // 背景：bindings 的 ECS 相关发射（using EntJoy.ECS、ChunkJobFuncDelegate、World 形参）
                // 由 job 种类条件化，使只引用 EntJoy.Collections/Jobs 的项目也能用 [NativeTranspile]。
                // 这条不变量把"解耦"从经验保证升级为生成器自校验：
                //   · 有 chunk/entity job 却没有 ECS → NT029（Error，说明 job 种类判定与类型可见性不一致）
                //   · 无 ECS 但生成物仍出现 ECS 符号 → NT030（Warning，生成器新增发射点漏了条件化；
                //     消费者此时本来也会看到 CS0234/CS0246，这条诊断负责把原因指到生成器）
                {
                    bool ecsReferenced = ctx.Compilation.GetTypeByMetadataName(Config.TypeWorld) != null;
                    bool needsEcs = validJobs.Any(CppJobGenerator.IsChunkScheduledJob);
                    if (needsEcs && !ecsReferenced)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(
                            NativeTranspileValidator.EcsRequiredButMissingError, Location.None));
                    }
                    else if (!ecsReferenced)
                    {
                        var leaked = EcsCouplingTokens(bindingsCode);
                        if (leaked.Count > 0)
                        {
                            spc.ReportDiagnostic(Diagnostic.Create(
                                NativeTranspileValidator.GeneratedEcsCouplingWarning, Location.None,
                                string.Join(", ", leaked)));
                        }
                    }
                }

                // 环境变量 ENTJOY_DUMP_BINDINGS=<path> 时把绑定源码落盘。
                // 用途：Unity 工程无法跑 MSBuild 源生成器管线，需要离线 dump 绑定后直接编译
                // 未设该环境变量时完全无副作用（只多一次 GetEnvironmentVariable）。
                try
                {
                    var dumpPath = System.Environment.GetEnvironmentVariable("ENTJOY_DUMP_BINDINGS");
                    if (!string.IsNullOrEmpty(dumpPath))
                    {
                        var dumpDir = Path.GetDirectoryName(dumpPath);
                        if (!string.IsNullOrEmpty(dumpDir)) Directory.CreateDirectory(dumpDir);
                        CodeGenIo.WriteAllTextWithRetry(dumpPath, bindingsCode);
                        Console.WriteLine($"[NativeTranspiler] Bindings dumped to {dumpPath}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[NativeTranspiler] Failed to dump bindings: {ex.Message}");
                }

                // SendEvent 元数据：供 BindingsGenerator 注册到 ChunkJobScheduler
                if (allJobEventTypes.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("using System;");
                    sb.AppendLine("using System.Collections.Generic;");
                    sb.AppendLine("namespace NativeTranspiler.Generated {");
                    sb.AppendLine("    internal static class NativeEventTypes {");
                    sb.AppendLine("        public static readonly Dictionary<string, Type[]> Types = new();");
                    sb.AppendLine("        static NativeEventTypes() {");
                    foreach (var kv in allJobEventTypes)
                    {
                        sb.Append($"            Types[\"{kv.Key}\"] = new Type[] {{ ");
                        for (int i = 0; i < kv.Value.Count; i++)
                        {
                            if (i > 0) sb.Append(", ");
                            sb.Append($"typeof({kv.Value[i]})");
                        }
                        sb.AppendLine(" };");
                    }
                    sb.AppendLine("        }");
                    sb.AppendLine("    }");
                    sb.AppendLine("}");
                    spc.AddSource("NativeTranspiler.EventTypes.g.cs", sb.ToString());
                }

                // ⚠ 不得把时间戳写进生成产物：源生成器的输出是编译器输入，
                //   一旦含 DateTime.UtcNow，每次构建输入都变 ⇒ 增量构建恒定失效、
                //   可复现构建不可能成立、产物 diff 永远非空（实测 EmitSnapshot 里唯一差异就是这一行）。
                spc.AddSource("NativeTranspiler_GeneratedMarker.g.cs",
                    $"// NativeTranspiler generated marker (deterministic)\n// {validMarkedMethods.Count()} methods, {methodsToGenerate.Count - validMarkedMethods.Count()} deps, {validJobs.Count()} jobs transpiled.");
                }
                catch (Exception ex)
                {
                    ReportGeneratorCrash(spc, ex);
                }
            });
        }

        /// <summary>
        /// 生成器崩溃诊断（NT026）：上报异常消息 + 调用栈，并把完整 <c>ToString()</c> 落盘到
        /// <c>%TEMP%/entjoy-native-transpiler-crash.txt</c>（消息受 MSBuild 输出长度限制，
        /// 栈可能被截断 —— 文件是完整版）。
        /// </summary>
        private static void ReportGeneratorCrash(SourceProductionContext spc, Exception ex)
        {
            var detail = ex.ToString();
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "entjoy-native-transpiler-crash.txt"), detail);
            }
            catch
            {
                // 落盘失败不影响诊断上报
            }
            var firstFrames = string.Join(" <- ", detail.Split('\n')
                .Where(l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal))
                .Take(6)
                .Select(l => l.Trim()));
            spc.ReportDiagnostic(Diagnostic.Create(
                NativeTranspileValidator.GeneratorCrashError,
                Location.None,
                ex.GetType().Name,
                ex.Message,
                firstFrames));
        }

        // 辅助方法（委托到 AttributeHelper）
        private static NativeTranspiler.BackendTarget GetBackendTarget(ISymbol symbol, INamedTypeSymbol? attrSymbol)
            => AttributeHelper.GetBackendTarget(symbol, attrSymbol);

        private static bool HasUseISPC_MT(ISymbol symbol, INamedTypeSymbol? attrSymbol)
            => AttributeHelper.HasUseISPC_MT(symbol, attrSymbol);

        private static NativeTranspiler.IspcMathLib GetMathLib(ISymbol symbol, INamedTypeSymbol? attrSymbol)
            => AttributeHelper.GetMathLib(symbol, attrSymbol);

        private static bool HasFastCppMathLib(ISymbol symbol, INamedTypeSymbol? attrSymbol)
            => AttributeHelper.HasFastCppMathLib(symbol, attrSymbol);

        /// <summary>
        /// 收集 Job Execute 方法体内调用的同程序集静态方法（作为依赖生成其 C++ 定义）。
        /// 与 CollectMethodDependencies 的区别：Execute 自身不加入 collected（它不是独立函数），
        /// 只把其调用的静态方法递归收进依赖集。
        /// </summary>
        private static void CollectJobExecuteDependencies(
            INamedTypeSymbol job, Compilation compilation,
            HashSet<IMethodSymbol> collected, List<Diagnostic> allErrors)
        {
            var executeMethod = job.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == Config.Execute);
            if (executeMethod == null) return;
            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var node in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(node);
                if (symbolInfo.Symbol is not IMethodSymbol calledMethod) continue;
                if (!calledMethod.IsStatic) continue;
                if (!SymbolEqualityComparer.Default.Equals(calledMethod.ContainingAssembly, compilation.Assembly))
                    continue;
                CollectMethodDependencies(calledMethod, compilation, collected, allErrors);
            }
        }

        private static void CollectMethodDependencies(
            IMethodSymbol method, Compilation compilation,
            HashSet<IMethodSymbol> collected, List<Diagnostic> allErrors)
        {
            var containingTypeFullName = method.ContainingType?.ToDisplayString();
            if (NativeApiSurface.SkipsEmission(containingTypeFullName))
                return;
            if (method.Name == Config.Execute && method.ContainingType?.AllInterfaces.Any(i =>
                SymbolHelper.IsEntJoyJobInterface(i, Config.IJob) || SymbolHelper.IsEntJoyJobInterface(i, Config.IJobParallelFor) || SymbolHelper.IsEntJoyJobInterface(i, Config.IJobFor) || SymbolHelper.IsEntJoyJobInterface(i, Config.IJobParallelForBatch) || SymbolHelper.IsEntJoyJobInterface(i, Config.IJobChunk) || SymbolHelper.IsEntJoyJobInterface(i, Config.IJobEntity)) == true)
                return;
            if (!collected.Add(method)) return;
            if (!NativeTranspileValidator.ValidateMethod(method, compilation, out var diags))
            {
                allErrors.AddRange(diags); return;
            }
            var methodSyntax = SymbolHelper.GetMethodSyntax(method);
            if (methodSyntax?.Body == null) return;
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var node in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(node);
                if (symbolInfo.Symbol is not IMethodSymbol calledMethod) continue;
                if (!calledMethod.IsStatic) continue;
                if (!SymbolEqualityComparer.Default.Equals(calledMethod.ContainingAssembly, compilation.Assembly))
                    continue;
                CollectMethodDependencies(calledMethod, compilation, collected, allErrors);
            }
        }

        private static HashSet<INamedTypeSymbol> CollectUserStructTypes(
    IEnumerable<IMethodSymbol> methods,
    IEnumerable<INamedTypeSymbol> jobStructs,
    Compilation compilation)
        {
            var structs = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            // 从静态方法参数中收集
            foreach (var method in methods)
            {
                foreach (var param in method.Parameters)
                    CollectFromType(param.Type, structs);
                // 从方法体的局部变量、new 表达式、临时变量等中深度收集
                var methodSyntax = SymbolHelper.GetMethodSyntax(method);
                if (methodSyntax?.Body != null)
                {
                    var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                    foreach (var localDecl in methodSyntax.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                    {
                        var localType = semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
                        if (localType != null)
                            CollectFromType(localType, structs);
                    }
                    // 也收集声明表达式（如 out var x）和 StackAllocArrayCreation 等
                    foreach (var declExpr in methodSyntax.Body.DescendantNodes().OfType<DeclarationExpressionSyntax>())
                    {
                        var declType = semanticModel.GetTypeInfo(declExpr).Type;
                        if (declType != null)
                            CollectFromType(declType, structs);
                    }
                }
            }

            // 从 Job 字段中收集
            foreach (var job in jobStructs)
            {
                foreach (var field in job.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    CollectFromType(field.Type, structs);
                // 也从 Job Execute 方法体中收集局部变量类型
                var executeMethod = job.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
                if (executeMethod != null)
                {
                    var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
                    if (methodSyntax?.Body != null)
                    {
                        var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                        foreach (var localDecl in methodSyntax.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                        {
                            var localType = semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
                            if (localType != null)
                                CollectFromType(localType, structs);
                        }

                        // SendEvent 事件类型收集
                        foreach (var invocation in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            // 判断是否 SendEvent 调用：
                            // 1) xxx.SendEvent<T>(...)：MemberAccess + GenericName
                            // 2) SendEvent(...)：裸调用（using static）
                            bool isSendEvent = false;
                            if (invocation.Expression is MemberAccessExpressionSyntax macSend
                                && macSend.Name.Identifier.Text == Config.SendEvent)
                                isSendEvent = true;
                            if (invocation.Expression is IdentifierNameSyntax idSend
                                && idSend.Identifier.Text == Config.SendEvent)
                                isSendEvent = true;
                            if (!isSendEvent) continue;

                            // 收集泛型参数类型 SendEvent<T>
                            if (invocation.Expression is MemberAccessExpressionSyntax mac2
                                && mac2.Name is GenericNameSyntax gn2)
                            {
                                foreach (var typeArg in gn2.TypeArgumentList.Arguments)
                                {
                                    var taType = semanticModel.GetTypeInfo(typeArg).Type;
                                    if (taType != null) CollectFromType(taType, structs);
                                }
                            }
                            // 收集参数中的 new 表达式类型 SendEvent(new XEvent {...})
                            foreach (var arg in invocation.ArgumentList.Arguments)
                            {
                                if (arg.Expression is ObjectCreationExpressionSyntax objCreate)
                                {
                                    // 同 CollectSendEventTypes：GetTypeInfo(objCreate) 优先，
                                    // 避免 VS/MSBuild Roslyn 对 object-initializer 的 .Type 返回 null
                                    // （嵌套类型 NativeEventJobTest.DeathSignal 场景必现）。
                                    var createdType = semanticModel.GetTypeInfo(objCreate).Type
                                                   ?? semanticModel.GetTypeInfo(objCreate.Type).Type;
                                    if (createdType != null) CollectFromType(createdType, structs);
                                }
                            }
                        }

                        foreach (var chunkComponentType in CppJobGenerator.CollectChunkNativeArrayTypes(job, compilation))
                            CollectFromType(chunkComponentType, structs);
                        // 也收集 SharedComponent 类型（blittable，GetSharedComponent<T>() 用）
                        foreach (var sharedType in CppJobGenerator.CollectSharedComponentTypes(job, compilation))
                            CollectFromType(sharedType, structs);
                    }
                }
            }

            return structs;
        }

        private static void CollectFromType(ITypeSymbol type, HashSet<INamedTypeSymbol> collected)
        {
            if (type is IPointerTypeSymbol ptrType)
            {
                CollectFromType(ptrType.PointedAtType, collected);
                return;
            }

            // 容器的元素类型必须先递归收集：NativeArray<T>/NativeList<T> 自身是 EntJoy 预定义类型
            // （无头文件，下面那个 IsEntJoyPredefinedType 分支本来也会 return），但 T 是用户结构体，
            // job 头会 include T 的头（见 CppJobGenerator 的 AddType 对 TypeArguments 的递归）——
            // 少了这一步就会生成一个 include 不存在文件的 job 头，MSVC 直接报 "file not found"，
            // 且报错落在生成目录里极易被误判为构建缓存问题。
            if (type is INamedTypeSymbol container && NativeTranspiler.IsEntJoyNativeContainerType(container))
            {
                CollectFromType(NativeTranspiler.GetContainerElementType(container), collected);
                return;
            }
            if (NativeTranspiler.IsEntJoyPredefinedType(type))
                return;
            if (type.Name == Config.Span && type.ContainingNamespace?.ToDisplayString() == Config.NamespaceSystem)
                return;

            if (type.IsValueType && !NativeTranspiler.IsBuiltinUnmanaged(type))
            {
                var named = (INamedTypeSymbol)type;
                if (collected.Add(named))
                {
                    // 递归收集字段中的结构体
                    foreach (var field in named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                        CollectFromType(field.Type, collected);
                }
            }
        }


        private static IMethodSymbol? GetMethodSymbol(GeneratorSyntaxContext ctx, CancellationToken ct)
        {
            var methodDecl = (MethodDeclarationSyntax)ctx.Node;
            var methodSymbol = ctx.SemanticModel.GetDeclaredSymbol(methodDecl, ct);
            if (methodSymbol == null) return null;
            var attrSymbol = ctx.SemanticModel.Compilation.GetTypeByMetadataName($"{RuntimeApi.AttributeNamespace}.{RuntimeApi.AttributeName}Attribute");
            return attrSymbol != null && methodSymbol.GetAttributes().Any(ad =>
                SymbolEqualityComparer.Default.Equals(ad.AttributeClass, attrSymbol)) ? methodSymbol : null;
        }

        private static INamedTypeSymbol? GetJobStructSymbol(GeneratorSyntaxContext ctx, CancellationToken ct)
        {
            var structDecl = (StructDeclarationSyntax)ctx.Node;
            var structSymbol = ctx.SemanticModel.GetDeclaredSymbol(structDecl, ct);
            if (structSymbol == null) return null;
            var attrSymbol = ctx.SemanticModel.Compilation.GetTypeByMetadataName($"{RuntimeApi.AttributeNamespace}.{RuntimeApi.AttributeName}Attribute");
            return attrSymbol != null && structSymbol.GetAttributes().Any(ad =>
                SymbolEqualityComparer.Default.Equals(ad.AttributeClass, attrSymbol)) ? structSymbol : null;
        }

        /// <summary>
        /// F-11：写下"本次产物是哪个生成器版本生成的"（生成器程序集内容哈希）。
        /// NativeCompileTask.WarnIfGeneratedArtifactsStale 用它判定"生成器改了但产物没重新生成"：
        /// 用内容哈希而不是时间戳，避免"重编但内容未变"造成的误报。
        /// </summary>
        private static void WriteGeneratorStamp(string outputDir)
        {
            try
            {
                var asm = typeof(NativeTranspilerGenerator).Assembly;
                string hash = "";
                try
                {
                    var location = asm.Location;
                    if (!string.IsNullOrEmpty(location) && File.Exists(location))
                    {
                        using (var md5 = System.Security.Cryptography.MD5.Create())
                        using (var fs = File.OpenRead(location))
                        {
                            var bytes = md5.ComputeHash(fs);
                            var sbHash = new StringBuilder(bytes.Length * 2);
                            foreach (var b in bytes) sbHash.Append(b.ToString("x2"));
                            hash = sbHash.ToString();
                        }
                    }
                }
                catch { /* 单文件发布/无法读取位置时留空，任务侧会跳过校验 */ }

                // 同样不含时间戳：stamp 只被 `generatorHash=` 消费（NativeCompileTask.WarnIfGeneratedArtifactsStale），
                // 带时间戳会让"内容未变"也被判定为变化 ⇒ stamp 每次构建都被重写。
                var text = $"generatorVersion={asm.GetName().Version}\ngeneratorHash={hash}\n";
                var stampPath = Path.Combine(outputDir, "generator.stamp");
                if (!File.Exists(stampPath) || File.ReadAllText(stampPath) != text)
                    CodeGenIo.WriteAllTextWithRetry(stampPath, text);
            }
            catch { /* 版本戳是诊断信息，写失败不影响生成 */ }
        }

        /// <summary>
        /// D1：删除本次未生成、但上一次运行遗留的生成物。
        /// 只处理本生成器自己的命名空间（`SharpNative_*` 前缀的 .cpp/.h/.ispc），
        /// 绝不触碰 build/ 缓存、CMakeLists.txt、*.bat、native_compile.hash 等由编译任务管理的文件。
        /// </summary>
        /// <remarks>
        /// ⚠ ISPC 的 `-h` 头必须按"对应 `.ispc` 还在不在"决定去留，不能按"在不在本次写出集合里"：
        /// `<X>_ispc.h` 是 ispc.exe 的产物（CMake 里是 <c>add_custom_command</c> 的 OUTPUT），
        /// 本生成器从不写它 ⇒ 它永远不在 <paramref name="expected"/> 里。早先按"不在集合就删"处理，
        /// 结果是每次生成都把全部 31 个 `*_ispc.h` 删掉 ⇒ CMake 判定 ISPC 步骤输出缺失、
        /// 全部重跑（实测改一个 job 体 31 次 ispc.exe、约 13 s，占整轮 ~19 s 的绝大部分；
        /// `.ispc` 真没了（job 被删）才连头一起删 —— D1 的原始目的（不留陈旧产物）不受影响。
        /// </remarks>
        private static void PruneStaleGeneratedFiles(string outputDir, HashSet<string> expected)
        {
            string[] exts = { ".cpp", ".h", ".ispc" };
            try
            {
                // ISPC 头（含 `_mt` 多目标变体）与它们的 `.ispc` 源一一对应：源还在就留住头。
                var ispcOwnedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in expected)
                {
                    if (!name.EndsWith(".ispc", StringComparison.OrdinalIgnoreCase)) continue;
                    ispcOwnedHeaders.Add(name.Substring(0, name.Length - ".ispc".Length) + "_ispc.h");
                }

                foreach (var path in Directory.EnumerateFiles(outputDir, "SharpNative_*"))
                {
                    string ext = Path.GetExtension(path);
                    if (Array.IndexOf(exts, ext) < 0) continue;
                    string name = Path.GetFileName(path);
                    if (expected.Contains(name) || ispcOwnedHeaders.Contains(name)) continue;
                    CodeGenIo.DeleteIfExists(path);
                    Console.WriteLine($"[NativeTranspiler] Pruned stale generated file: {name}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NativeTranspiler] Failed to prune stale files: {ex.Message}");
            }
        }


        /// <summary>
        /// 扫描生成物中出现的 ECS 耦合符号（词边界匹配 ⇒ 用户自定义的 MyWorldJob 之类不会误报）。
        /// 仅在编译未引用 EntJoy.ECS 时用于 NT030 自校验。
        /// </summary>
        private static List<string> EcsCouplingTokens(string generatedCode)
        {
            var found = new List<string>();
            foreach (var token in new[] { "EntJoy.ECS", "World", "QueryBuilder", "ArchetypeChunk", "EntityManager", "ChunkJobScheduler", "ChunkJobData", "ChunkEnabledMask" })
            {
                var pattern = token == "EntJoy.ECS"
                    ? System.Text.RegularExpressions.Regex.Escape(token)
                    : "\\b" + System.Text.RegularExpressions.Regex.Escape(token) + "\\b";
                if (System.Text.RegularExpressions.Regex.IsMatch(generatedCode, pattern))
                    found.Add(token);
            }
            return found;
        }

    }
}
