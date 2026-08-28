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
                if (allErrors.Any())
                {
                    foreach (var diag in allErrors) spc.ReportDiagnostic(diag);
                    return;
                }

                var outputDir = Path.Combine(ctx.GetProjectDirectory(), "NativeTranspiler_Generated");
                Directory.CreateDirectory(outputDir);

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

                var bindingsCode = BindingsGenerator.GenerateBindingsClass(validMarkedMethods, validJobs, ctx.Compilation);
                spc.AddSource("NativeTranspiler.Bindings.g.cs", bindingsCode);

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

        private static void CollectMethodDependencies(
            IMethodSymbol method, Compilation compilation,
            HashSet<IMethodSymbol> collected, List<Diagnostic> allErrors)
        {
            var containingTypeFullName = method.ContainingType?.ToDisplayString();
            if (containingTypeFullName != null && SkipTranspileTypeNames.Contains(containingTypeFullName))
                return;
            if (method.Name == Config.Execute && method.ContainingType?.AllInterfaces.Any(i =>
                SymbolHelper.IsEntJoyJobInterface(i, Config.IJob) || SymbolHelper.IsEntJoyJobInterface(i, Config.IJobParallelFor) || SymbolHelper.IsEntJoyJobInterface(i, Config.IJobFor) || SymbolHelper.IsEntJoyJobInterface(i, Config.IJobChunk) || SymbolHelper.IsEntJoyJobInterface(i, Config.IJobEntity)) == true)
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

            // 过滤预定义的容器类型
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

        private static string GenerateCommonIspcHeader()
        {
            return @"
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

// ---------- int2 operators ----------
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
";
        }

        private static string GenerateCMakeLists(List<string> cppFiles, List<(string fileName, NativeTranspiler.IspcMathLib mathLib)> ispcFiles, HashSet<string> fastMathCppFiles, HashSet<string> autoSimdCppFiles,
                                  string outputDir, string outputBinDir, string relativeNativeDllDir, bool hasFastMath,
                                  List<string>? existingCppOrder = null)
        {
            var sb = new StringBuilder();
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
            sb.AppendLine("set(CMAKE_UNITY_BUILD ON)");
            sb.AppendLine("set(CMAKE_UNITY_BUILD_BATCH_SIZE 8)");
            sb.AppendLine("add_definitions(-DIMGUI_DEFINE_MATH_OPERATORS)");
            sb.AppendLine();
            sb.AppendLine("include_directories(${CMAKE_CURRENT_SOURCE_DIR})");
            sb.AppendLine($"include_directories(\"${{CMAKE_CURRENT_SOURCE_DIR}}/{relativeNativeDllDir}\")");
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
            sb.AppendLine($"if(EXISTS \"${{CMAKE_CURRENT_SOURCE_DIR}}/{relativeNativeDllDir}/tasksys.cpp\")");
            sb.AppendLine($"    set(TASKSYS_SRC \"${{CMAKE_CURRENT_SOURCE_DIR}}/{relativeNativeDllDir}/tasksys.cpp\")");
            sb.AppendLine("else()");
            sb.AppendLine("    set(TASKSYS_SRC \"\")");
            sb.AppendLine($"    message(WARNING \"tasksys.cpp not found at ${{CMAKE_CURRENT_SOURCE_DIR}}/{relativeNativeDllDir}/tasksys.cpp\")");
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
                sb.AppendLine($"    \"${{CMAKE_CURRENT_SOURCE_DIR}}/{relativeNativeDllDir}/{f}\"");
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
                sb.AppendLine("    $<$<CXX_COMPILER_ID:Clang>:/O2 /Ob2 /Oi /Ot /Qpar /MP>");     // ClangCL, no /fp:fast
                sb.AppendLine("    $<$<NOT:$<CXX_COMPILER_ID:MSVC,Clang>>:-O3 -march=native -mtune=native -ffp-contract=fast -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer>");
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
            sb.AppendLine($"    set(IMGUI_DIR   \"${{CMAKE_CURRENT_SOURCE_DIR}}/{relativeNativeDllDir}/thirdParty/imgui\")");
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
