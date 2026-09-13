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
        private static readonly HashSet<string> SkipTranspileTypeNames = new()
        {
            "EntJoy.Mathematics.math",
            "EntJoy.Collections.UnsafeUtility",
            "EntJoy.Hint"
        };

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
                // =====================================================================
                // CodeGenPipeline（阶段化编排；历史遗留内联于 RegisterSourceOutput）
                //   0) 空集短路
                //   1) Validate  —— 收集依赖 + NativeTranspileValidator 校验，出错即停
                //   2) Resolve   —— 收集用户结构体、后端选择、输出目录、公共头
                //   3) Methods   —— 静态方法与 Job 的 C++/ISPC 源 + MT + wrapper 写出
                //   4) Adapters  —— C++ 包装 + 实体批量适配生成
                //   5) BuildArtifacts —— CMakeLists.txt + clang 编译 .bat + ISPC 编译 .bat
                //   6) Bindings  —— 生成 .g.cs 绑定 + 生成标记
                // 说明：完整抽成独立 CodeGenPipeline 类需配行为对拍（本机 SDK 损坏无法跑
                // 消费者基准），故先以文档化阶段标记落地；无行为变更。
                // =====================================================================
                if (ctx.MethodSymbols.IsEmpty && ctx.JobStructSymbols.IsEmpty) return;

                var methodsToGenerate = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                var allErrors = new List<Diagnostic>();

                foreach (var method in ctx.MethodSymbols)
                {
                    if (method == null) continue;
                    CollectMethodDependencies(method, ctx.Compilation, methodsToGenerate, allErrors);
                }
                // Job Execute 内部的同程序集静态方法调用也要收集依赖（生成其 C++ 定义），
                // 否则调用点引用了不存在的函数（use of undeclared identifier）。
                // 注意：不能直接对 Execute 调 CollectMethodDependencies —— 该方法对 Execute 有早退
                // 保护（Execute 自身不作为独立函数生成），故单独遍历 Execute 体内的静态调用。
                foreach (var job in ctx.JobStructSymbols)
                {
                    if (job == null) continue;
                    CollectJobExecuteDependencies(job, ctx.Compilation, methodsToGenerate, allErrors);
                }
                foreach (var method in ctx.MethodSymbols)
                {
                    if (method == null) continue;
                    if (!NativeTranspileValidator.ValidateMethod(method, ctx.Compilation, out var diags))
                        allErrors.AddRange(diags);
                }
                foreach (var job in ctx.JobStructSymbols)
                {
                    if (job == null) continue;
                    if (!NativeTranspileValidator.ValidateJobStruct(job, ctx.Compilation, out var diags))
                        allErrors.AddRange(diags);
                }
                // 诊断一律上报；只有 Error 才终止生成。
                // （NT023/NT024 是"事实告知"类 Warning —— 既不能憋着不报，也不能因为它们而整个项目不生成产物。）
                foreach (var diag in allErrors) spc.ReportDiagnostic(diag);
                if (allErrors.Any(d => d.Severity == DiagnosticSeverity.Error))
                    return;

                var outputDir = Path.Combine(ctx.GetProjectDirectory(), "NativeTranspiler_Generated");
                Directory.CreateDirectory(outputDir);
                // D1：录制本次写出的全部产物文件名（含"内容未变跳过写入"的）
                CodeGenIo.BeginOutputTracking();

                var cppFiles = new List<string>();
                var fastMathCppFiles = new HashSet<string>();
                // AutoSIMD 生成单元：需要 IEEE-754 精确浮点语义（454229d EC2/EC8/E5/E8/E11），
                // 全局 NativeTranspiled 恢复 fast-math 提速，但这些文件编译进独立 precise 静态库
                //（无 fast-math），再链回 NativeTranspiled.dll。
                var autoSimdCppFiles = new HashSet<string>();
                var ispcFiles = new List<(string fileName, NativeTranspiler.IspcMathLib mathLib)>();
                var attrSymbol = ctx.Compilation.GetTypeByMetadataName($"{RuntimeApi.AttributeNamespace}.{RuntimeApi.AttributeName}Attribute");

                // 收集被标记的方法和 Job 结构体
                var validMarkedMethods = ctx.MethodSymbols.Where(m => m != null).Cast<IMethodSymbol>();
                var validJobs = ctx.JobStructSymbols.Where(j => j != null).Cast<INamedTypeSymbol>();

                // 收集用户自定义结构体（用于生成 ISPC 头文件）
                var userStructs = CollectUserStructTypes(validMarkedMethods, validJobs, ctx.Compilation);

                // ─── SendEvent 元数据：Job 全名 → 事件类型全名列表 ───
                var allJobEventTypes = new Dictionary<string, List<string>>();

                bool anyIspc = ctx.MethodSymbols.Any(m => m != null && GetBackendTarget(m, attrSymbol) == NativeTranspiler.BackendTarget.Ispc)
                             || ctx.JobStructSymbols.Any(j => j != null && GetBackendTarget(j, attrSymbol) == NativeTranspiler.BackendTarget.Ispc);

                // ★ 布局加固（C++ 与 ISPC 共用）：Explicit / Pack<8 的 struct 无法在自然对齐下复现
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
                    CodeGenIo.WriteAllTextWithRetry(commonIspcPath, GenerateCommonIspcHeader());

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

                        bool disabledAutoRefresh = GetDisableAutoRefresh(method, attrSymbol);
                        bool fileExists = File.Exists(ispcSrcPath) || File.Exists(wrapperCppPath);

                        if (!disabledAutoRefresh || !fileExists)
                        {
                            CodeGenIo.WriteAllTextWithRetry(ispcSrcPath, ispcSource);
                            CodeGenIo.WriteAllTextWithRetry(wrapperCppPath, cppWrapper);
                        }
                        ispcFiles.Add(($"{baseName}.ispc", mathLib));
                        cppFiles.Add($"{baseName}_wrapper.cpp");

                        if (HasUseISPC_MT(method, attrSymbol))
                        {
                            var mtIspcSource = IspcGenerator.GenerateIspcMTSource(method, ctx.Compilation, userStructs);
                            var mtCppWrapper = IspcGenerator.GenerateCppWrapperMT(method);

                            string mtIspcPath = Path.Combine(outputDir, $"{baseName}_mt.ispc");
                            string mtWrapperPath = Path.Combine(outputDir, $"{baseName}_mt_wrapper.cpp");

                            if (!disabledAutoRefresh || !File.Exists(mtIspcPath))
                            {
                                CodeGenIo.WriteAllTextWithRetry(mtIspcPath, mtIspcSource);
                                CodeGenIo.WriteAllTextWithRetry(mtWrapperPath, mtCppWrapper);
                            }
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

                        bool disabledAutoRefresh = GetDisableAutoRefresh(method, attrSymbol);
                        bool fileExists = File.Exists(hPath) || File.Exists(cppPath);

                        if (!disabledAutoRefresh || !fileExists)
                        {
                            CodeGenIo.WriteAllTextWithRetry(hPath, header);
                            CodeGenIo.WriteAllTextWithRetry(cppPath, impl);
                        }
                        var cppFile = baseName + ".cpp";
                        cppFiles.Add(cppFile);
                        if (methodAutoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                            autoSimdCppFiles.Add(cppFile);
                        if (HasFastCppMathLib(method, attrSymbol) || methodAutoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                            fastMathCppFiles.Add(cppFile);
                    }
                }

                // 处理 Job 结构
                foreach (var job in ctx.JobStructSymbols)
                {
                    if (job == null) continue;
                    var target = GetBackendTarget(job, attrSymbol);

                    var ispcBase = IspcGenerator.GetIspcBaseName(job);
                    var plainBase = CppJobGenerator.GetCppJobFunctionName(job);
                    var mathLib = GetMathLib(job, attrSymbol);

                    if (target == NativeTranspiler.BackendTarget.Ispc)
                    {
                        CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{plainBase}.h"));
                        CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{plainBase}.cpp"));

                        bool disabledAutoRefresh = GetDisableAutoRefresh(job, attrSymbol);
                        bool useIspcMt = HasUseISPC_MT(job, attrSymbol);
                        bool mtProvidesScheduledAdapter = useIspcMt && CppJobGenerator.IsChunkScheduledJob(job);

                        if (!mtProvidesScheduledAdapter)
                        {
                            var ispcSource = IspcGenerator.GenerateIspcSource(job, ctx.Compilation, userStructs);
                            var cppWrapper = IspcGenerator.GenerateCppWrapper(job, ctx.Compilation);

                            string ispcSrcPath = Path.Combine(outputDir, $"{ispcBase}.ispc");
                            string wrapperCppPath = Path.Combine(outputDir, $"{ispcBase}_wrapper.cpp");
                            bool fileExists = File.Exists(ispcSrcPath) || File.Exists(wrapperCppPath);

                            if (!disabledAutoRefresh || !fileExists)
                            {
                                CodeGenIo.WriteAllTextWithRetry(ispcSrcPath, ispcSource);
                                CodeGenIo.WriteAllTextWithRetry(wrapperCppPath, cppWrapper);
                            }

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

                            string mtIspcPath = Path.Combine(outputDir, $"{ispcBase}_mt.ispc");
                            string mtWrapperPath = Path.Combine(outputDir, $"{ispcBase}_mt_wrapper.cpp");

                            if (!disabledAutoRefresh || !File.Exists(mtIspcPath))
                            {
                                CodeGenIo.WriteAllTextWithRetry(mtIspcPath, mtIspcSource);
                                CodeGenIo.WriteAllTextWithRetry(mtWrapperPath, mtCppWrapper);
                            }
                            ispcFiles.Add(($"{ispcBase}_mt.ispc", mathLib));
                            cppFiles.Add($"{ispcBase}_mt_wrapper.cpp");
                        }
                    }
                    else
                    {
                        CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}.ispc"));
                        CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}_wrapper.cpp"));
                        CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}_mt.ispc"));
                        CodeGenIo.DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}_mt_wrapper.cpp"));

                        var header = CppJobGenerator.GenerateJobHeader(job, ctx.Compilation);
                        var impl = CppJobGenerator.GenerateJobImplementation(job, ctx.Compilation);

                        string hPath = Path.Combine(outputDir, $"{plainBase}.h");
                        string cppPath = Path.Combine(outputDir, $"{plainBase}.cpp");

                        bool disabledAutoRefresh = GetDisableAutoRefresh(job, attrSymbol);
                        bool fileExists = File.Exists(hPath) || File.Exists(cppPath);

                        if (!disabledAutoRefresh || !fileExists)
                        {
                            CodeGenIo.WriteAllTextWithRetry(hPath, header);
                            CodeGenIo.WriteAllTextWithRetry(cppPath, impl);
                        }
                        var cppFile = plainBase + ".cpp";
                        cppFiles.Add(cppFile);
                        var jobAutoSIMD = AttributeHelper.GetAutoSIMD(job, attrSymbol);
                        if (jobAutoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                            autoSimdCppFiles.Add(cppFile);
                        if (HasFastCppMathLib(job, attrSymbol) || jobAutoSIMD == NativeTranspiler.AutoSIMD.Enabled)
                            fastMathCppFiles.Add(cppFile);
                    }

                    bool adapterProvidedByIspcChunkWrapper = target == NativeTranspiler.BackendTarget.Ispc &&
                                                             CppJobGenerator.IsChunkScheduledJob(job);
                    if (!adapterProvidedByIspcChunkWrapper)
                    {
                        // 为 NativeTranspile Job 生成适配函数（消除 C# 委托桥接）。
                        // ISPC IJobChunk 的 adapter 由 ISPC wrapper 生成，否则会重复导出同名符号。
                        var (adapterCode, evtTypes) = CppJobGenerator.GenerateJobAdapter(job, ctx.Compilation);
                        string adapterPath = Path.Combine(outputDir, $"{plainBase}_Adapter.cpp");
                        bool adapterDisabledAutoRefresh = GetDisableAutoRefresh(job, attrSymbol);
                        bool adapterFileExists = File.Exists(adapterPath);
                        if (!adapterDisabledAutoRefresh || !adapterFileExists)
                        {
                            CodeGenIo.WriteAllTextWithRetry(adapterPath, adapterCode);
                        }
                        cppFiles.Add($"{plainBase}_Adapter.cpp");

                        // ─── SendEvent: 收集事件类型元数据 ───
                        if (evtTypes.Count > 0)
                        {
                            string jobFullName = job.ToDisplayString();
                            allJobEventTypes[jobFullName] = evtTypes;
                        }
                    }
                }

                // 生成 run_ispc.bat：增量检测 + 并行编译
                if (ispcFiles.Count > 0)
                {
                    var batPath = Path.Combine(outputDir, "run_ispc.bat");
                    var batContent = new StringBuilder();
                    batContent.AppendLine("@echo off");
                    batContent.AppendLine("cd /d \"%~dp0\"");
                    batContent.AppendLine("if not exist build mkdir build");
                    batContent.AppendLine("setlocal enabledelayedexpansion");
                    batContent.AppendLine("set ISPC=E:/Code/ispc-v1.30.0-windows/bin/ispc.exe");
                    batContent.AppendLine("where ispc.exe >nul 2>nul");
                    batContent.AppendLine("if not errorlevel 1 set ISPC=ispc.exe");
                    batContent.AppendLine("if not exist \"%ISPC%\" (");
                    batContent.AppendLine("    echo ISPC not found. Put ispc.exe in PATH or at E:/Code/ispc-v1.30.0-windows/bin/ispc.exe");
                    batContent.AppendLine("    exit /b 1");
                    batContent.AppendLine(")");
                    batContent.AppendLine("set MAXCONCURRENT=%NUMBER_OF_PROCESSORS%");
                    batContent.AppendLine("if \"%MAXCONCURRENT%\"==\"\" set MAXCONCURRENT=8");
                    batContent.AppendLine("set FAILED=0");
                    batContent.AppendLine();

                    // 为每个 ispc 文件生成并行编译块
                    foreach (var (ispc, mathLib) in ispcFiles)
                    {
                        string baseName = Path.GetFileNameWithoutExtension(ispc);
                        string mathLibStr = mathLib.ToString().ToLowerInvariant();

                        // 增量检测
                        // 等待有空闲槽位
                        batContent.AppendLine($":wait_{baseName}");
                        batContent.AppendLine("set RUNNING=0");
                        batContent.AppendLine("for /f %%p in ('tasklist /fi \"imagename eq ispc.exe\" 2^>nul ^| find /c \"ispc.exe\"') do set RUNNING=%%p");
                        batContent.AppendLine("if !RUNNING! GEQ !MAXCONCURRENT! (");
                        batContent.AppendLine("    >nul timeout /t 1 /nobreak");
                        batContent.AppendLine($"    goto :wait_{baseName}");
                        batContent.AppendLine(")");
                        batContent.AppendLine();

                        // 并行编译：后台启动 ispc，输出重定向到日志
                        string ispcExtraOpts = mathLib == NativeTranspiler.IspcMathLib.fast ? "" : " --opt=disable-fma";
                        batContent.AppendLine($"echo Compiling {ispc}... ({mathLibStr})");
                        batContent.AppendLine($"start /b /min \"ISPC_{baseName}\" \"%ISPC%\" \"{ispc}\" -O3 -o \"build\\{baseName}.obj\" -h \"{baseName}_ispc.h\" --target=avx2-i32x8 --math-lib={mathLibStr}{ispcExtraOpts} > \"build\\{baseName}.log\" 2>&1");
                        batContent.AppendLine();
                        batContent.AppendLine($":skip_{baseName}");
                        batContent.AppendLine();
                    }

                    // 等待所有 ISPC 编译完成
                    // #23：tasklist | find /c 的计数文本依赖区域设置（中文输出"找到 N 个"），
                    // 改用 findstr 判断进程是否存在（errorlevel 区域无关）：仍存在则 errorlevel=0。
                    batContent.AppendLine(":wait_all");
                    batContent.AppendLine("tasklist /fi \"imagename eq ispc.exe\" 2>nul | findstr /i \"ispc.exe\" >nul 2>nul");
                    batContent.AppendLine("if not errorlevel 1 (");
                    batContent.AppendLine("    >nul timeout /t 1 /nobreak");
                    batContent.AppendLine("    goto :wait_all");
                    batContent.AppendLine(")");
                    batContent.AppendLine();

                    // 检查所有文件是否编译成功（检查 .obj 存在且非空）
                    foreach (var (ispc, mathLib) in ispcFiles)
                    {
                        string baseName = Path.GetFileNameWithoutExtension(ispc);
                        batContent.AppendLine($"if not exist \"build\\{baseName}.obj\" set FAILED=1");
                        batContent.AppendLine($"if exist \"build\\{baseName}.obj\" if %%~z\"build\\{baseName}.obj\"==0 set FAILED=1");
                    }
                    batContent.AppendLine();

                    batContent.AppendLine("if \"%FAILED%\"==\"1\" (");
                    batContent.AppendLine("    echo One or more ISPC files failed to compile. Check .log files for details.");
                    batContent.AppendLine("    exit /b 1");
                    batContent.AppendLine(")");
                    batContent.AppendLine("echo All ISPC files compiled successfully.");
                    CodeGenIo.WriteAllTextWithRetry(batPath, batContent.ToString());
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
                    var existingCppOrder = ReadExistingCppSourceOrder(cmakePath);
                    var cmakeContent = GenerateCMakeLists(cppFiles, ispcFiles, fastMathCppFiles, autoSimdCppFiles, outputDir, solutionBinDir, relativeNativeDllDir, hasFastMath, existingCppOrder);
                    // 如果内容未变则不写入，避免时间戳更新触发 CMake 重新 configure
                    if (!File.Exists(cmakePath) || File.ReadAllText(cmakePath) != cmakeContent)
                    {
                        CodeGenIo.WriteAllTextWithRetry(cmakePath, cmakeContent);
                    }
                }

                // ─── D1：清理陈旧生成物（见 PruneStaleGeneratedFiles 注释）───
                // "本次产物" 直接取自 CodeGenIo 录制到的写入集合 —— 不靠手写文件名推导，
                // 避免漏掉 job 的 .h / 依赖头文件而误删（实测踩过两次）。
                // ⚠ 位置必须在 **所有 CodeGenIo 写入之后**（含 run_clangcl.bat / CMakeLists.txt）。
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
                    clangBat.AppendLine("cmake --build build --config Release --target NativeDll --target NativeTranspiled");
                    clangBat.AppendLine("if errorlevel 1 exit /b 1");
                    clangBat.AppendLine("copy /Y build\\Release\\NativeDll.dll \"" + solBinDir + "\"");
                    clangBat.AppendLine("copy /Y build\\Release\\NativeTranspiled.dll \"" + solBinDir + "\"");
                    clangBat.AppendLine("echo Done. NativeDll.dll + NativeTranspiled.dll copied to " + solBinDir);
                    // 内容未变则不写（#22）：避免时间戳更新触发无关重编/检查
                    string clangBatContent = clangBat.ToString();
                    if (!File.Exists(clangBatPath) || File.ReadAllText(clangBatPath) != clangBatContent)
                        CodeGenIo.WriteAllTextWithRetry(clangBatPath, clangBatContent);
                }

                // ─── 生成器版本戳（F-11）───
                // 记录"这批产物是哪个内容的生成器生成的"。NativeCompileTask 用它判定
                // "生成器改了但产物没重新生成"（Roslyn 内容哈希门控会让这条静默发生），并据此报 warning。
                // ⚠ 不要把它加进 native 依赖哈希（内容含时间戳，会让每次构建都重编 CMake）。
                WriteGeneratorStamp(outputDir);

                var bindingsCode = BindingsGenerator.GenerateBindingsClass(validMarkedMethods, validJobs, ctx.Compilation);
                spc.AddSource("NativeTranspiler.Bindings.g.cs", bindingsCode);

                // 环境变量 ENTJOY_DUMP_BINDINGS=<path> 时把绑定源码落盘。
                // 用途：Unity 工程无法跑 MSBuild 源生成器管线，需要离线 dump 绑定后直接编译
                // （tools/UnityJobBenchNative 的 build.ps1 依赖这个开关）。
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

                // ─── SendEvent 元数据：供 BindingsGenerator 注册到 ChunkJobScheduler ───
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

                spc.AddSource("NativeTranspiler_GeneratedMarker.g.cs",
                    $"// Generated at {DateTime.UtcNow}\n// {validMarkedMethods.Count()} methods, {methodsToGenerate.Count - validMarkedMethods.Count()} deps, {validJobs.Count()} jobs transpiled.");
            });
        }

        // ----- 辅助方法（委托到 AttributeHelper） -----
        private static NativeTranspiler.BackendTarget GetBackendTarget(ISymbol symbol, INamedTypeSymbol? attrSymbol)
            => AttributeHelper.GetBackendTarget(symbol, attrSymbol);

        private static bool HasUseISPC_MT(ISymbol symbol, INamedTypeSymbol? attrSymbol)
            => AttributeHelper.HasUseISPC_MT(symbol, attrSymbol);

        private static NativeTranspiler.IspcMathLib GetMathLib(ISymbol symbol, INamedTypeSymbol? attrSymbol)
            => AttributeHelper.GetMathLib(symbol, attrSymbol);

        private static bool HasFastCppMathLib(ISymbol symbol, INamedTypeSymbol? attrSymbol)
            => AttributeHelper.HasFastCppMathLib(symbol, attrSymbol);

        private static bool GetDisableAutoRefresh(ISymbol symbol, INamedTypeSymbol? attrSymbol)
            => AttributeHelper.GetDisableAutoRefresh(symbol, attrSymbol);

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
            if (containingTypeFullName != null && SkipTranspileTypeNames.Contains(containingTypeFullName))
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

                        // ─── SendEvent 事件类型收集 ───
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
                foreach (var arg in container.TypeArguments)
                    CollectFromType(arg, collected);
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
        /// Roslyn 的 CoreCompile 内容哈希门控会让这种情况静默发生（改了生成器，跑的却是上一版产物）。
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

                var text = $"generatorVersion={asm.GetName().Version}\ngeneratorHash={hash}\nwrittenUtc={DateTime.UtcNow:O}\n";
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
        private static void PruneStaleGeneratedFiles(string outputDir, HashSet<string> expected)
        {
            string[] exts = { ".cpp", ".h", ".ispc" };
            try
            {
                foreach (var path in Directory.EnumerateFiles(outputDir, "SharpNative_*"))
                {
                    string ext = Path.GetExtension(path);
                    if (Array.IndexOf(exts, ext) < 0) continue;
                    string name = Path.GetFileName(path);
                    if (expected.Contains(name)) continue;
                    CodeGenIo.DeleteIfExists(path);
                    Console.WriteLine($"[NativeTranspiler] Pruned stale generated file: {name}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NativeTranspiler] Failed to prune stale files: {ex.Message}");
            }
        }

        private static string GenerateCommonIspcHeader()
        {
            // include guard：job 的 .ispc 与它 #include 的 helper .ispc 都会引入本文件，
            // 无 guard 会在同一翻译单元内重复定义 make_*/operator* → ISPC 报重定义。
            return "#ifndef __ENTJOY_ISPC_COMMON_DEFINED\n#define __ENTJOY_ISPC_COMMON_DEFINED\n" + @"
// NativeMath.ispc – ISPC compatible math library
struct float2 { float x; float y; };
struct int2   { int x; int y; };
struct uint2  { unsigned int x; unsigned int y; };

// ---------- EventBuffer POD（SendEvent 生成的 ISPC 代码依赖） ----------
// 注意：ISPC 中 uniform void* 非法（void 不能带 uniform 限定），data 用 uniform int*（uniform→uniform cast 合法，
// 且 varying→uniform 指针 cast 被禁止，裸 void* 是 varying 指针无法 cast 到 uniform T*）。
// count 保持 uniform int*（atomic 需要 uniform 指针）。
struct __EntJoyEventBuffer {
    uniform int* data;
    uniform int* count;
    uniform int capacity;
    uniform int elementSize;
};

// ---------- helpers (static to avoid duplicate symbols) ----------
static struct float2 make_float2(float x, float y) {
    struct float2 r; r.x = x; r.y = y; return r;
}
static struct float2 make_float2(float v) { return make_float2(v, v); }
static struct int2 make_int2(int x, int y) {
    struct int2 r; r.x = x; r.y = y; return r;
}
static struct int2 make_int2(int v) { return make_int2(v, v); }
static struct uint2 make_uint2(unsigned int x, unsigned int y) {
    struct uint2 r; r.x = x; r.y = y; return r;
}
static struct uint2 make_uint2(unsigned int v) { return make_uint2(v, v); }
static uniform struct float2 make_uniform_float2(uniform float x, uniform float y) {
    uniform struct float2 r; r.x = x; r.y = y; return r;
}
static uniform struct int2 make_uniform_int2(uniform int x, uniform int y) {
    uniform struct int2 r; r.x = x; r.y = y; return r;
}
static uniform struct uint2 make_uniform_uint2(uniform unsigned int x, uniform unsigned int y) {
    uniform struct uint2 r; r.x = x; r.y = y; return r;
}

// type conversions
static struct float2 float2_from_int2(struct int2 v) { return make_float2(v.x, v.y); }
static struct int2 int2_from_float2(struct float2 v) { return make_int2((int)v.x, (int)v.y); }

// ---------- float2 operators ----------
static struct float2 operator-(struct float2 a) {
    return make_float2(-a.x, -a.y);
}
static struct float2 operator+(struct float2 a, struct float2 b) {
    struct float2 r; r.x = a.x + b.x; r.y = a.y + b.y; return r;
}
static struct float2 operator-(struct float2 a, struct float2 b) {
    struct float2 r; r.x = a.x - b.x; r.y = a.y - b.y; return r;
}
static struct float2 operator*(struct float2 a, struct float2 b) {
    struct float2 r; r.x = a.x * b.x; r.y = a.y * b.y; return r;
}
static struct float2 operator/(struct float2 a, struct float2 b) {
    struct float2 r; r.x = a.x / b.x; r.y = a.y / b.y; return r;
}
static struct float2 operator*(struct float2 v, float s) {
    struct float2 r; r.x = v.x * s; r.y = v.y * s; return r;
}
static struct float2 operator*(float s, struct float2 v) { return v * s; }
static struct float2 operator/(struct float2 v, float s) {
    struct float2 r; r.x = v.x / s; r.y = v.y / s; return r;
}
// ★ uniform 变体：uniform 标量循环（for (uniform int) + if (programIndex!=0) return）里
//   所有局部都是 uniform，`uniform float2 * uniform float` 若只匹配上面的 varying 重载，
//   结果会是 varying struct，无法赋给 uniform 局部（探针 _probe5 实证）。
//   ISPC 允许仅靠 uniform/varying 区分重载；varying 场景传 uniform 实参会自动 splat，故两者可共存。
static uniform struct float2 operator*(uniform struct float2 v, uniform float s) {
    uniform struct float2 r; r.x = v.x * s; r.y = v.y * s; return r;
}
static uniform struct float2 operator*(uniform float s, uniform struct float2 v) { return v * s; }
static uniform struct float2 operator/(uniform struct float2 v, uniform float s) {
    uniform struct float2 r; r.x = v.x / s; r.y = v.y / s; return r;
}
static uniform struct float2 operator+(uniform struct float2 a, uniform struct float2 b) {
    uniform struct float2 r; r.x = a.x + b.x; r.y = a.y + b.y; return r;
}
static uniform struct float2 operator-(uniform struct float2 a, uniform struct float2 b) {
    uniform struct float2 r; r.x = a.x - b.x; r.y = a.y - b.y; return r;
}

// ---------- int2 operators ----------
static struct int2 operator-(struct int2 a) {
    return make_int2(-a.x, -a.y);
}
static struct int2 operator+(struct int2 a, struct int2 b) {
    struct int2 r; r.x = a.x + b.x; r.y = a.y + b.y; return r;
}
static struct int2 operator-(struct int2 a, struct int2 b) {
    struct int2 r; r.x = a.x - b.x; r.y = a.y - b.y; return r;
}
static struct int2 operator*(struct int2 a, struct int2 b) {
    struct int2 r; r.x = a.x * b.x; r.y = a.y * b.y; return r;
}
static struct int2 operator/(struct int2 a, struct int2 b) {
    struct int2 r; r.x = a.x / b.x; r.y = a.y / b.y; return r;
}
static struct int2 operator*(struct int2 v, int s) {
    struct int2 r; r.x = v.x * s; r.y = v.y * s; return r;
}
static struct int2 operator*(int s, struct int2 v) { return v * s; }
static struct int2 operator+(struct int2 a, int b) {
    struct int2 r; r.x = a.x + b; r.y = a.y + b; return r;
}
static struct int2 operator-(struct int2 a, int b) {
    struct int2 r; r.x = a.x - b; r.y = a.y - b; return r;
}

// ---------- uint2 operators ----------
static struct uint2 operator+(struct uint2 a, struct uint2 b) {
    struct uint2 r; r.x = a.x + b.x; r.y = a.y + b.y; return r;
}
static struct uint2 operator-(struct uint2 a, struct uint2 b) {
    struct uint2 r; r.x = a.x - b.x; r.y = a.y - b.y; return r;
}
static struct uint2 operator*(struct uint2 a, struct uint2 b) {
    struct uint2 r; r.x = a.x * b.x; r.y = a.y * b.y; return r;
}
static struct uint2 operator*(struct uint2 v, unsigned int s) {
    struct uint2 r; r.x = v.x * s; r.y = v.y * s; return r;
}

// ---------- math functions ----------
static float dot(struct float2 a, struct float2 b) { return a.x * b.x + a.y * b.y; }
static float lengthsq(struct float2 v) { return dot(v, v); }
static float length(struct float2 v) { return sqrt(lengthsq(v)); }
static struct float2 normalize(struct float2 v) {
    float l = length(v);
    if (l > 0.f)
        return v * (1.f / l);
    else
        return make_float2(0.f, 0.f);
}
static struct float2 abs(struct float2 v) {
    struct float2 r; r.x = abs(v.x); r.y = abs(v.y); return r;
}
static struct int2 abs(struct int2 v) {
    struct int2 r; r.x = abs(v.x); r.y = abs(v.y); return r;
}
static struct float2 min(struct float2 a, struct float2 b) {
    struct float2 r; r.x = (a.x < b.x ? a.x : b.x); r.y = (a.y < b.y ? a.y : b.y); return r;
}
static struct int2 min(struct int2 a, struct int2 b) {
    struct int2 r; r.x = (a.x < b.x ? a.x : b.x); r.y = (a.y < b.y ? a.y : b.y); return r;
}
static struct float2 max(struct float2 a, struct float2 b) {
    struct float2 r; r.x = (a.x > b.x ? a.x : b.x); r.y = (a.y > b.y ? a.y : b.y); return r;
}
static struct int2 max(struct int2 a, struct int2 b) {
    struct int2 r; r.x = (a.x > b.x ? a.x : b.x); r.y = (a.y > b.y ? a.y : b.y); return r;
}
static struct float2 clamp(struct float2 v, struct float2 lo, struct float2 hi) {
    return min(max(v, lo), hi);
}
static struct int2 clamp(struct int2 v, struct int2 lo, struct int2 hi) {
    return min(max(v, lo), hi);
}
static struct float2 floor(struct float2 v) {
    struct float2 r; r.x = floor(v.x); r.y = floor(v.y); return r;
}
static struct float2 ceil(struct float2 v) {
    struct float2 r; r.x = ceil(v.x); r.y = ceil(v.y); return r;
}
static float distancesq(struct float2 a, struct float2 b) { return lengthsq(b - a); }
static float lerp(float a, float b, float t) { return a + (b - a) * t; }
static struct float2 lerp(struct float2 a, struct float2 b, float t) {
    return a + (b - a) * t;
}

// ---------- Interlocked 宏（ISPC 侧） ----------
// 基类 StatementTranslator.TranslateInterlockedCall 会吐 C++ 的 INTERLOCKED_* 宏。
// ISPC 的静态方法 lane-callable helper（IspcGenerator.Helper）若把 C++ 宏直接落进 .ispc，
// 会报 Undeclared symbol INTERLOCKED_EXCHANGE32。这里给 ISPC 等价定义：
// ISPC 的 atomic_add_global 是 fetch-add（返回旧值），故 ADD/INCREMENT 系补回增量以对齐 C# 的 add-fetch 语义。
// 已知限制：atomic_add_global 只接受 32 位 load/store（64 位原子在 ISPC 不可用）。
" + IspcAtomicMacros + @"
" + "\n#endif // __ENTJOY_ISPC_COMMON_DEFINED\n";
        }

        /// <summary>
        /// ISPC 侧 Interlocked 宏定义（拼进 EntJoyCommon.ispc）。
        /// 单独做成普通字符串常量：C# 逐字字符串里写 <c>#</c> 开头会被当成 C# 预处理指令（CS1032），
        /// 而 <c>\u0023</c> 在逐字字符串里不转义（CS1056）。
        /// </summary>
        private const string IspcAtomicMacros =
            "#define INTERLOCKED_FETCH_ADD32(ptr, val)       atomic_add_global((ptr), (val))\n" +
            "#define INTERLOCKED_FETCH_SUB32(ptr, val)       atomic_subtract_global((ptr), (val))\n" +
            "#define INTERLOCKED_EXCHANGE32(ptr, val)        atomic_swap_global((ptr), (val))\n" +
            "#define INTERLOCKED_ADD_AND_FETCH32(ptr, val)   (atomic_add_global((ptr), (val)) + (val))\n" +
            "#define INTERLOCKED_INCREMENT_AND_FETCH32(ptr)  (atomic_add_global((ptr), 1) + 1)\n" +
            "#define INTERLOCKED_DECREMENT_AND_FETCH32(ptr)  (atomic_subtract_global((ptr), 1) - 1)\n" +
            "#define INTERLOCKED_COMPARE_EXCHANGE32(ptr, oldVal, newVal) atomic_compare_exchange_global((ptr), (oldVal), (newVal))\n";

        private static string GenerateCMakeLists(List<string> cppFiles, List<(string fileName, NativeTranspiler.IspcMathLib mathLib)> ispcFiles, HashSet<string> fastMathCppFiles, HashSet<string> autoSimdCppFiles,
                                  string outputDir, string outputBinDir, string relativeNativeDllDir, bool hasFastMath,
                                  List<string>? existingCppOrder = null)
        {
            var sb = new StringBuilder();
            // 跨盘符时 GetRelativePath 返回绝对路径，再拼 ${CMAKE_CURRENT_SOURCE_DIR}/ 会得到
            // 无效路径（.../NativeTranspiler_Generated/C:/Users/.../.nuget/...）。绝对路径直接用。
            string cmakeNativeDllDir = Path.IsPathRooted(relativeNativeDllDir)
                ? relativeNativeDllDir
                : "${CMAKE_CURRENT_SOURCE_DIR}/" + relativeNativeDllDir;
            sb.AppendLine("cmake_minimum_required(VERSION 3.10)");
            sb.AppendLine("set(CMAKE_INSTALL_PREFIX \"${CMAKE_CURRENT_BINARY_DIR}/install\" CACHE PATH \"Install prefix\" FORCE)");
            sb.AppendLine("project(NativeDll LANGUAGES CXX)");
            sb.AppendLine();
            sb.AppendLine("set(CMAKE_CXX_STANDARD 20)");
            sb.AppendLine("set(CMAKE_CXX_STANDARD_REQUIRED ON)");
            sb.AppendLine();
            // Unity Build: merge multiple .cpp batches → 减少 ClangCL 启动开销的同时
            // 保留并行度。BATCH_SIZE=0（单 TU）在生成代码量大时（EntJoySample 207 cpp +
            // 40 ispc）单 TU ClangCL 编译串行成为瓶颈（全量 ~33s）；拆批后
            // `cmake --build --parallel` 并行编译多 TU，增量也只重编变化的 TU。
            //
            // 但拆批有**运行期**代价：job 批函数调用的静态帮助函数（CpuOrca/CpuFlow/CpuObstacle/
            // CpuScan 的 static 方法）各自是独立 .cpp，拆批会把它们与调它的 job 分到不同 TU
            // ⇒ 跨 TU 调用**无法内联**（帮助函数还带 GENERATED_API=dllexport）。
            // 实测（百万单位 Melee，1s 交替 A/B 配对、每配置 2 轮）：
            //   批 8：C++ 内核比 C# 内核 **慢** 2.9±2.3ms/步；单 TU：**快** 6.3±2.3ms/步（≈10% Melee）
            //   —— 托管 JIT 会把同样的平凡帮助函数内联，拆批相当于让 C++ 侧白吃亏。
            // 编译时间差距在小规模下可忽略（63 文件：单 TU 27s vs 批 8 25s）⇒ 小规模默认单 TU。
            sb.AppendLine("set(CMAKE_UNITY_BUILD ON)");
            int unityBatch = cppFiles.Count + ispcFiles.Count <= 96 ? 0 : 8;
            sb.AppendLine($"set(CMAKE_UNITY_BUILD_BATCH_SIZE {unityBatch})"
                + (unityBatch == 0
                    ? "   # 0 = 单 TU：让 job 与它调用的静态帮助函数同 TU 可内联（见上方实测）"
                    : "   # 大规模：保留并行/增量编译，代价是跨 TU 调用不可内联"));
            sb.AppendLine("add_definitions(-DIMGUI_DEFINE_MATH_OPERATORS)");
            sb.AppendLine();
            sb.AppendLine("include_directories(${CMAKE_CURRENT_SOURCE_DIR})");
            sb.AppendLine($"include_directories(\"{cmakeNativeDllDir}\")");
            sb.AppendLine();
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# CPU architecture detection");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("if(CMAKE_SYSTEM_PROCESSOR MATCHES \"^(x86_64|amd64|AMD64|x64)$\")");
            sb.AppendLine("    set(NATIVE_ARCH \"x86_64\")");
            sb.AppendLine("elseif(CMAKE_SYSTEM_PROCESSOR MATCHES \"^(aarch64|arm64|ARM64|AARCH64)$\")");
            sb.AppendLine("    set(NATIVE_ARCH \"arm64\")");
            sb.AppendLine("elseif(CMAKE_SYSTEM_PROCESSOR MATCHES \"^(armv[7-9]|arm|ARM)$\")");
            sb.AppendLine("    set(NATIVE_ARCH \"arm\")");
            sb.AppendLine("else()");
            sb.AppendLine("    set(NATIVE_ARCH \"unknown\")");
            sb.AppendLine("endif()");
            sb.AppendLine();
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# SIMD level: AUTO or user override (-DNATIVE_SIMD_LEVEL=AVX2|SSE4|NEON|SCALAR)");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("if(NOT DEFINED NATIVE_SIMD_LEVEL)");
            sb.AppendLine("    set(NATIVE_SIMD_LEVEL \"AUTO\" CACHE STRING \"SIMD: AUTO/AVX2/AVX/SSE4/NEON/SCALAR\")");
            sb.AppendLine("endif()");
            sb.AppendLine("if(NATIVE_SIMD_LEVEL STREQUAL \"AUTO\")");
            sb.AppendLine("    if(NATIVE_ARCH MATCHES \"x86_64|x86\")");
            sb.AppendLine("        set(NATIVE_SIMD_LEVEL \"AVX2\")");
            sb.AppendLine("    elseif(NATIVE_ARCH MATCHES \"arm64|arm\")");
            sb.AppendLine("        set(NATIVE_SIMD_LEVEL \"NEON\")");
            sb.AppendLine("    else()");
            sb.AppendLine("        set(NATIVE_SIMD_LEVEL \"SCALAR\")");
            sb.AppendLine("    endif()");
            sb.AppendLine("endif()");
            sb.AppendLine("message(STATUS \"NativeDll: arch=${NATIVE_ARCH}, SIMD=${NATIVE_SIMD_LEVEL}\")");
            sb.AppendLine();

            sb.AppendLine("# No explicit task system defined; tasksys.cpp will pick the best one for the platform");
            sb.AppendLine($"if(EXISTS \"{cmakeNativeDllDir}/tasksys.cpp\")");
            sb.AppendLine($"    set(TASKSYS_SRC \"{cmakeNativeDllDir}/tasksys.cpp\")");
            sb.AppendLine("else()");
            sb.AppendLine("    set(TASKSYS_SRC \"\")");
            sb.AppendLine($"    message(WARNING \"tasksys.cpp not found at {cmakeNativeDllDir}/tasksys.cpp\")");
            sb.AppendLine("endif()");
            sb.AppendLine();

            // ============================================================
            // DLL 分离：NativeDll.dll（核心 runtime）+ NativeTranspiled.dll（生成代码）
            //   - NativeDll：JobSystem / WorkerPool / Profiler / Debugger / imgui / tasksys
            //   - NativeTranspiled：transpiled job wrappers + ISPC objects，链接 NativeDll
            //     生成代码只依赖 NativeDll 的 header-only 类型（模板/POD/inline）与
            //     JOB_API 导出函数，跨 DLL 边界通过链接 NativeDll import lib 解析。
            // ============================================================
            // NativeDll 核心源文件（按目录 glob，TU 拆分/新增时免维护漏列）。
            // 曾硬编码 JobSystem.cpp 单文件，模块化拆分为 State/Tiles/Scheduler 后漏列
            // 三个新 TU → 链接期 LNK2019（Scheduler/JobHandle 未定义）。glob 从根上消除该类回归。
            sb.AppendLine("# Core runtime (NativeDll.dll)");
            sb.AppendLine("add_library(NativeDll SHARED");
            var nativeDllAbsDir = Path.GetFullPath(Path.Combine(outputDir, relativeNativeDllDir));
            var nativeDllCppFiles = Directory.Exists(nativeDllAbsDir)
                ? Directory.GetFiles(nativeDllAbsDir, "*.cpp")
                    .Select(f => Path.GetFileName(f))
                    .Where(f => !string.Equals(f, "tasksys.cpp", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
            foreach (var f in nativeDllCppFiles)
                sb.AppendLine($"    \"{cmakeNativeDllDir}/{f}\"");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("# Generated job wrappers + ISPC runtime (NativeTranspiled.dll)");
            sb.AppendLine("#   tasksys.cpp（ISPCAlloc/ISPCLaunch/ISPCSync 任务系统）必须与 ISPC 编译产物");
            sb.AppendLine("#   同 DLL：ISPC .obj 以普通符号引用 ISPCLaunch 等，MSVC 无法从另一 DLL");
            sb.AppendLine("#   自动导入普通符号（非 __imp_），故 tasksys + ISPC objects + 生成代码要同库。");
            sb.AppendLine("add_library(NativeTranspiled SHARED");
            foreach (var file in OrderCppSourcesStable(cppFiles, existingCppOrder))
            {
                // AutoSIMD 单元改由 NativeTranspiledPrecise 静态库编译（无 fast-math），
                // 此处必须排除，避免同一符号在 fast-math 与 precise 两处重复定义（LNK2005）。
                if (autoSimdCppFiles.Contains(file)) continue;
                sb.AppendLine($"    {file}");
            }
            sb.AppendLine("    ${TASKSYS_SRC}");
            sb.AppendLine(")");
            sb.AppendLine("target_link_libraries(NativeTranspiled PRIVATE NativeDll)");
            sb.AppendLine();

            // ============================================================
            // AutoSIMD precise 静态库（fast-math OFF — IEEE-754 NaN/±0）
            //   454229d EC2/EC8/E5/E8/E11：AutoSIMD batch 控制流依赖 NaN/±0 精确语义，
            //   全局 /fp:fast / -ffast-math 会破坏它。global NativeTranspiled 恢复
            //   fast-math 提速（GridSearch 构建/查询热路径），这里把这些文件单独编进
            //   无 fast-math 的静态库，再链回同一个 NativeTranspiled.dll，使 AutoSIMD
            //   导出符号仍从该 DLL 导出、被托管绑定 P/Invoke。
            //   Unity Build 无法按源文件区分编译 flag，故独立静态库是可靠做法。
            //   precise 库同样继承 CMAKE_UNITY_BUILD（批 8），与主库同构，
            //   改一个 AutoSIMD 文件只重编其所在批 → 增量编译友好。
            // ============================================================
            if (autoSimdCppFiles.Count > 0)
            {
                // 确定性排序：precise 库的 unity 批成员稳定，改一个 AutoSIMD 文件只重编其所在批。
                var autoSimdSorted = autoSimdCppFiles.OrderBy(f => f, StringComparer.Ordinal).ToList();
                sb.AppendLine("# --- AutoSIMD precise static lib (fast-math OFF) ---");
                sb.AppendLine("set(AUTOSIMD_SOURCES");
                foreach (var file in autoSimdSorted)
                    sb.AppendLine($"    {file}");
                sb.AppendLine(")");
                sb.AppendLine("add_library(NativeTranspiledPrecise STATIC ${AUTOSIMD_SOURCES})");
                sb.AppendLine("target_compile_options(NativeTranspiledPrecise PRIVATE");
                sb.AppendLine("    $<$<CXX_COMPILER_ID:MSVC>:/O2 /Ob2 /Oi /Ot /Qpar /MP>");      // MSVC default, no /fp:fast
                // ClangCL: 禁用自动 FMA 融合（/clang: 前缀是 clang-cl 正确语法；裸 -ffp-contract 被忽略）。
                // 自动融合 a*a-3 → fmsub 单次舍入 vs C# 两次舍入，差异经除法放大可达 13 ULP
                // （FZ1 实测，ulp_probe 复现 strict=0x3D7A45A2 vs fmsub=0x3D7A45AF）→ 禁自动融合保 bit-exact。
                // 生成器显式 n_fmadd_ps（增量 9）是 intrinsic 调用，不受 fp-contract 影响，性能保留。
                sb.AppendLine("    $<$<CXX_COMPILER_ID:Clang>:/O2 /Ob2 /Oi /Ot /Qpar /MP /clang:-ffp-contract=off>");
                sb.AppendLine("    $<$<NOT:$<CXX_COMPILER_ID:MSVC,Clang>>:-O3 -march=native -mtune=native -ffp-contract=off -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer>");
                sb.AppendLine(")");
                sb.AppendLine("target_compile_definitions(NativeTranspiledPrecise PRIVATE NDEBUG NOMINMAX GENERATED_EXPORTS)");
                sb.AppendLine("target_link_libraries(NativeTranspiled PRIVATE NativeTranspiledPrecise)");
                sb.AppendLine();
            }

            // ---- 调试面板：Dear ImGui 集成（Windows + D3D11 后端） ----
            // 源码位于 NativeDll/thirdParty/imgui。Windows 上编译 imgui 核心 + Win32 + D3D11。
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# Dear ImGui debug panel (Windows / D3D11)");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("if(WIN32)");
            sb.AppendLine($"    set(IMGUI_DIR   \"{cmakeNativeDllDir}/thirdParty/imgui\")");
            sb.AppendLine("    set(IMGUI_BACK  \"${IMGUI_DIR}/backends\")");
            sb.AppendLine("    target_include_directories(NativeDll PRIVATE ${IMGUI_DIR} ${IMGUI_BACK})");
            sb.AppendLine("    target_sources(NativeDll PRIVATE");
            sb.AppendLine("        ${IMGUI_DIR}/imgui.cpp");
            sb.AppendLine("        ${IMGUI_DIR}/imgui_draw.cpp");
            sb.AppendLine("        ${IMGUI_DIR}/imgui_tables.cpp");
            sb.AppendLine("        ${IMGUI_DIR}/imgui_widgets.cpp");
            sb.AppendLine("        ${IMGUI_BACK}/imgui_impl_win32.cpp");
            sb.AppendLine("        ${IMGUI_BACK}/imgui_impl_dx11.cpp");
            sb.AppendLine("    )");
            sb.AppendLine("    target_link_libraries(NativeDll PRIVATE d3d11 dxgi)");
            sb.AppendLine("    target_compile_definitions(NativeDll PRIVATE ENTJOY_IMGUI_ENABLED=1)");
            sb.AppendLine("endif()");
            sb.AppendLine();

            // SIMD arch flags + defines (after add_library)
            // 生成代码（NativeTranspiled）与核心（NativeDll）都 include Native Containes/Math
            // 头（含 SIMD），须应用相同的 SIMD 级别/数学精度/sentinel 布局，保证跨 DLL 一致。
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# SIMD arch flags + defines（NativeDll + NativeTranspiled 一致）");
            sb.AppendLine("# ============================================================");
            string simdTgtList = autoSimdCppFiles.Count > 0
                ? "NativeDll NativeTranspiled NativeTranspiledPrecise"
                : "NativeDll NativeTranspiled";
            sb.AppendLine($"foreach(SIMD_TGT {simdTgtList})");
            sb.AppendLine("    if(NATIVE_SIMD_LEVEL STREQUAL \"AVX2\")");
            sb.AppendLine("        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_AVX2 NSIMD_WIDTH=8)");
            sb.AppendLine("        if(MSVC)");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE /arch:AVX2)");
            sb.AppendLine("        else()");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE -mavx2 -mbmi2 -mfma)");
            sb.AppendLine("        endif()");
            sb.AppendLine("    elseif(NATIVE_SIMD_LEVEL STREQUAL \"AVX\")");
            sb.AppendLine("        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_AVX NSIMD_WIDTH=8)");
            sb.AppendLine("        if(MSVC)");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE /arch:AVX)");
            sb.AppendLine("        else()");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE -mavx)");
            sb.AppendLine("        endif()");
            sb.AppendLine("    elseif(NATIVE_SIMD_LEVEL STREQUAL \"SSE4\")");
            sb.AppendLine("        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_SSE4 NSIMD_WIDTH=4)");
            sb.AppendLine("        if(NOT MSVC)");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE -msse4.2)");
            sb.AppendLine("        endif()");
            sb.AppendLine("    elseif(NATIVE_SIMD_LEVEL STREQUAL \"NEON\")");
            sb.AppendLine("        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_NEON NSIMD_WIDTH=4)");
            sb.AppendLine("        if(NOT MSVC)");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE -march=armv8-a+simd)");
            sb.AppendLine("        endif()");
            sb.AppendLine("    else()");
            sb.AppendLine("        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_SCALAR NSIMD_WIDTH=1)");
            sb.AppendLine("    endif()");
            sb.AppendLine("    # SIMD math precision: 1=Fastest(~3.5ULP) 2=High(~1.0ULP) 3=IEEE(exact)");
            sb.AppendLine("    if(NOT DEFINED NATIVE_SIMD_MATH_PRECISION)");
            sb.AppendLine("        set(NATIVE_SIMD_MATH_PRECISION \"1\" CACHE STRING \"SIMD math precision level: 1=Fastest 2=High 3=IEEE\")");
            sb.AppendLine("    endif()");
            sb.AppendLine("    target_compile_definitions(${SIMD_TGT} PRIVATE SIMD_MATH_PRECISION=${NATIVE_SIMD_MATH_PRECISION})");
            sb.AppendLine("endforeach()");
            sb.AppendLine();

            // Sentinel: match C# #if DEBUG DisposeSentinel container layout.
            // C# Debug 编译下 NativeArray=40B / NativeList=32B（带 sentinel），Release=32/24B。
            // 原生侧以 -DENTJOY_ENABLE_SENTINEL=ON 编译时 C++ 容器模板加 8B sentinel 字段对齐，
            // 否则 C# Debug + 原生适配器的 mirror static_assert 会失败（fail-fast）。Release 保持默认 OFF。
            sb.AppendLine("# Sentinel layout: match C# #if DEBUG DisposeSentinel (native templates add 8B sentinel field)");
            sb.AppendLine("option(ENTJOY_ENABLE_SENTINEL \"Match C# #if DEBUG DisposeSentinel container layout\" OFF)");
            sb.AppendLine("if(ENTJOY_ENABLE_SENTINEL)");
            sb.AppendLine("    target_compile_definitions(NativeDll PRIVATE ENTJOY_ENABLE_SENTINEL)");
            sb.AppendLine("    target_compile_definitions(NativeTranspiled PRIVATE ENTJOY_ENABLE_SENTINEL)");
            if (autoSimdCppFiles.Count > 0)
                sb.AppendLine("    target_compile_definitions(NativeTranspiledPrecise PRIVATE ENTJOY_ENABLE_SENTINEL)");
            sb.AppendLine("    message(STATUS \"NativeDll/NativeTranspiled: ENTJOY_ENABLE_SENTINEL ON (NativeArray=40B / NativeList=32B)\")");
            sb.AppendLine("endif()");
            sb.AppendLine();

            // ISPC: x86 only, optional
            if (ispcFiles.Count > 0)
            {
                sb.AppendLine("# ============================================================");
                sb.AppendLine("# ISPC: x86 only, optional");
                sb.AppendLine("# ============================================================");
                sb.AppendLine("find_program(ISPC_EXECUTABLE ispc)");
                sb.AppendLine("if(ISPC_EXECUTABLE AND NATIVE_ARCH MATCHES \"x86_64|x86\")");
                sb.AppendLine("    set(HAS_ISPC TRUE)");
                sb.AppendLine("    message(STATUS \"NativeDll: ISPC found\")");
                sb.AppendLine("else()");
                sb.AppendLine("    set(HAS_ISPC FALSE)");
                sb.AppendLine("endif()");
                sb.AppendLine();

                sb.AppendLine("if(HAS_ISPC)");
                sb.AppendLine("    set(ISPC_OBJECTS");
                foreach (var (ispc, _) in ispcFiles)
                {
                    string baseName = Path.GetFileNameWithoutExtension(ispc);
                    sb.AppendLine($"        \"${{CMAKE_CURRENT_BINARY_DIR}}/{baseName}.obj\"");
                }
                sb.AppendLine("    )");
                sb.AppendLine();
                foreach (var (ispc, mathLib) in ispcFiles)
                {
                    string baseName = Path.GetFileNameWithoutExtension(ispc);
                    string sourcePath = "${CMAKE_CURRENT_SOURCE_DIR}/" + ispc.Replace("\\", "/");
                    string objectPath = "${CMAKE_CURRENT_BINARY_DIR}/" + baseName + ".obj";
                    string headerPath = "${CMAKE_CURRENT_SOURCE_DIR}/" + baseName + "_ispc.h";
                    string mathLibStr = mathLib.ToString().ToLowerInvariant();
                    string fmaOpt = mathLib == NativeTranspiler.IspcMathLib.fast ? "" : " --opt=disable-fma";
                    sb.AppendLine("    add_custom_command(");
                    sb.AppendLine($"        OUTPUT \"{objectPath}\" \"{headerPath}\"");
                    sb.AppendLine($"        COMMAND \"${{ISPC_EXECUTABLE}}\" \"{sourcePath}\" -O3 -o \"{objectPath}\" -h \"{headerPath}\" --target=avx2-i32x8 --math-lib={mathLibStr}{fmaOpt}");
                    sb.AppendLine($"        DEPENDS \"{sourcePath}\"");
                    sb.AppendLine("        WORKING_DIRECTORY \"${CMAKE_CURRENT_SOURCE_DIR}\"");
                    sb.AppendLine($"        COMMENT \"Compiling ISPC {ispc}\"");
                    sb.AppendLine("        VERBATIM");
                    sb.AppendLine("    )");
                }
                sb.AppendLine("    set_source_files_properties(${ISPC_OBJECTS} PROPERTIES EXTERNAL_OBJECT TRUE GENERATED TRUE)");
                // ISPC 对象由生成代码 wrapper 调用 → 链接进 NativeTranspiled？
                // 否——ISPC _impl 被生成代码 wrapper 直接函数调用，须在同一 DLL。
                // ISPC 编译产物是纯 avx 目标代码，与 NativeTranspiled 一起链接。
                sb.AppendLine("    target_sources(NativeTranspiled PRIVATE ${ISPC_OBJECTS})");
                sb.AppendLine();
                sb.AppendLine("    if(TASKSYS_SRC)");
                sb.AppendLine("        set_source_files_properties(${TASKSYS_SRC} PROPERTIES COMPILE_FLAGS \"/arch:AVX\")");
                sb.AppendLine("    endif()");
                sb.AppendLine("endif()");
                sb.AppendLine();
            }

            sb.AppendLine("# ============================================================");
            sb.AppendLine("# Global compiler flags");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("if(MSVC)");
            sb.AppendLine("    # 源文件为 UTF-8 无 BOM（含中文注释）。不加 /utf-8 时 MSVC 按本地代码页");
            sb.AppendLine("    # （中文系统=936/GBK）读取：UTF-8 汉字字节序列在 GBK 下可能被误判为含");
            sb.AppendLine("    # 0x5C 反斜杠 → 注释内触发行拼接吃掉下一行 → C4819 + C2065/C2447 级联解析错误。");
            sb.AppendLine("    if(CMAKE_CXX_COMPILER_ID STREQUAL \"Clang\")");
            sb.AppendLine("        # ClangCL (LLVM backend — faster SIMD than MSVC)");
            sb.AppendLine("        # /MP：Unity Build 拆批后同 project 内的多个 TU 并行编译");
            sb.AppendLine("        #（--parallel 只并行 project 间；缺 /MP 时拆批反而串行更慢）");
            sb.AppendLine("        target_compile_options(NativeDll PRIVATE /utf-8 /std:c++20 /O2 /Oi /fp:fast /MP)");
            // NativeTranspiled keeps /fp:fast for performance (gridsearch build/query hot paths).
            // AutoSIMD files are compiled separately WITHOUT /fp:fast in NativeTranspiledPrecise
            // (see above), preserving 454229d's IEEE-754 NaN/±0 semantics (EC2/EC8/E5/E8/E11).
            sb.AppendLine("        target_compile_options(NativeTranspiled PRIVATE /utf-8 /std:c++20 /O2 /Oi /fp:fast /MP)");
            sb.AppendLine("    else()");
            sb.AppendLine("        # MSVC (default)");
            sb.AppendLine("        target_compile_options(NativeDll PRIVATE /utf-8 /std:c++20 /O2 /Ob2 /Oi /Ot /Qpar /MP /fp:fast)");
            sb.AppendLine("        target_compile_options(NativeTranspiled PRIVATE /utf-8 /std:c++20 /O2 /Ob2 /Oi /Ot /Qpar /MP /fp:fast)");
            sb.AppendLine("    endif()");
            sb.AppendLine("    target_compile_definitions(NativeDll PRIVATE NDEBUG NOMINMAX NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)");
            sb.AppendLine("    # NativeTranspiled 导出生成代码 wrapper/adapter（GENERATED_API → dllexport）");
            sb.AppendLine("    target_compile_definitions(NativeTranspiled PRIVATE NDEBUG NOMINMAX GENERATED_EXPORTS)");
            sb.AppendLine("else()");
            sb.AppendLine("    target_compile_options(NativeDll PRIVATE -O3 -march=native -mtune=native -ffast-math -ffp-contract=fast -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer)");
            // NativeTranspiled keeps -ffast-math for performance; AutoSIMD precise lib (above)
            // compiles without -ffast-math to preserve 454229d's IEEE-754 semantics.
            sb.AppendLine("    target_compile_options(NativeTranspiled PRIVATE -O3 -march=native -mtune=native -ffast-math -ffp-contract=fast -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer)");
            sb.AppendLine("    target_compile_definitions(NativeDll PRIVATE NDEBUG NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)");
            sb.AppendLine("    target_compile_definitions(NativeTranspiled PRIVATE NDEBUG GENERATED_EXPORTS)");
            sb.AppendLine("endif()");
            sb.AppendLine();
            // 注：曾试过对 NativeTranspiled 开 INTERPROCEDURAL_OPTIMIZATION（/GL+/LTCG、-flto）
            // 来解决同一个"跨 TU 不可内联"问题 —— **实测无收益**（ΔMelee 从 −3.8±4.6 变到 −2.5±2.6ms，
            // 即在噪声内），而且 /GL 会拖慢链接。真正的解法是上面的 unity 批大小（单 TU），故此处不开 IPO。

            sb.AppendLine("# ============================================================");
            sb.AppendLine("# Platform-specific output suffix");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("if(WIN32)");
            sb.AppendLine("    set_target_properties(NativeDll PROPERTIES SUFFIX \".dll\")");
            sb.AppendLine("    set_target_properties(NativeTranspiled PROPERTIES SUFFIX \".dll\")");
            sb.AppendLine("elseif(APPLE)");
            sb.AppendLine("    set_target_properties(NativeDll PROPERTIES SUFFIX \".dylib\")");
            sb.AppendLine("    set_target_properties(NativeTranspiled PROPERTIES SUFFIX \".dylib\")");
            sb.AppendLine("else()");
            sb.AppendLine("    set_target_properties(NativeDll PROPERTIES SUFFIX \".so\")");
            sb.AppendLine("    set_target_properties(NativeTranspiled PROPERTIES SUFFIX \".so\")");
            sb.AppendLine("endif()");
            sb.AppendLine();
            sb.AppendLine("set_target_properties(NativeDll PROPERTIES");
            sb.AppendLine("    RUNTIME_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
            sb.AppendLine("    LIBRARY_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
            sb.AppendLine("    ARCHIVE_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
            sb.AppendLine(")");
            sb.AppendLine("set_target_properties(NativeTranspiled PROPERTIES");
            sb.AppendLine("    RUNTIME_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
            sb.AppendLine("    LIBRARY_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
            sb.AppendLine("    ARCHIVE_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
            sb.AppendLine(")");
            return sb.ToString();
        }

        /// <summary>
        /// 读取上一次生成的 CMakeLists.txt 中 add_library(NativeTranspiled ...) 块内的源文件列表
        ///（保留原有顺序）。文件不存在或解析失败返回 null → 调用方回退到字典序。
        /// </summary>
        private static List<string>? ReadExistingCppSourceOrder(string cmakePath)
        {
            if (!File.Exists(cmakePath))
                return null;
            try
            {
                var order = new List<string>();
                bool inNativeTranspiled = false;
                foreach (var rawLine in File.ReadAllLines(cmakePath))
                {
                    var line = rawLine.Trim();
                    if (inNativeTranspiled)
                    {
                        if (line == ")") break;
                        // 形如 "    SharpNative_Job_xxx_Execute.cpp"，排除 ${TASKSYS_SRC} 等变量
                        if (line.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) && !line.Contains("${"))
                            order.Add(line);
                    }
                    else if (line.StartsWith("add_library(NativeTranspiled", StringComparison.OrdinalIgnoreCase))
                    {
                        inNativeTranspiled = true;
                    }
                }
                return order.Count > 0 ? order : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 增量友好的源列表排序：保留 existingOrder（上一次 CMakeLists 的顺序）中仍存在于
        /// cppFiles 的文件，新文件按字典序追加到末尾，绝不打乱既有文件的相对顺序。
        /// 这样 Unity Build 的既有批成员不变，新增 job/method 只让最后一个批变化，
        /// native 侧只需重编新 TU + 末尾批。
        /// </summary>
        private static List<string> OrderCppSourcesStable(List<string> cppFiles, List<string>? existingCppOrder)
        {
            if (existingCppOrder == null || existingCppOrder.Count == 0)
                return cppFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            var ordered = new List<string>();
            var remaining = new HashSet<string>(cppFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var prev in existingCppOrder)
            {
                if (remaining.Remove(prev))
                    ordered.Add(prev);
            }
            // 新增文件（之前不存在）追加到末尾，避免插入中间打乱既有 Unity 批
            if (remaining.Count > 0)
                ordered.AddRange(remaining.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return ordered;
        }
    }
}