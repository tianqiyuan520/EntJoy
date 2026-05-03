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
        private const string AttributeName = "NativeTranspile";
        private const string AttributeNamespace = "NativeTranspiler";

        private static readonly HashSet<string> SkipTranspileTypeNames = new()
        {
            "EntJoy.Mathematics.math",
            "EntJoy.Collections.UnsafeUtility"
        };

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(ctx =>
                ctx.AddSource($"{AttributeName}Attribute.g.cs", GenerateAttributeCode()));

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
                var ispcFiles = new List<(string fileName, NativeTranspiler.IspcMathLib mathLib)>();
                var attrSymbol = ctx.Compilation.GetTypeByMetadataName($"{AttributeNamespace}.{AttributeName}Attribute");

                // 收集被标记的方法和 Job 结构体
                var validMarkedMethods = ctx.MethodSymbols.Where(m => m != null).Cast<IMethodSymbol>();
                var validJobs = ctx.JobStructSymbols.Where(j => j != null).Cast<INamedTypeSymbol>();

                // 收集用户自定义结构体（用于生成 ISPC 头文件）
                var userStructs = CollectUserStructTypes(validMarkedMethods, validJobs, ctx.Compilation);

                bool anyIspc = ctx.MethodSymbols.Any(m => m != null && GetBackendTarget(m, attrSymbol) == NativeTranspiler.BackendTarget.Ispc)
                             || ctx.JobStructSymbols.Any(j => j != null && GetBackendTarget(j, attrSymbol) == NativeTranspiler.BackendTarget.Ispc);

                // 为用户自定义结构体生成 C++ 头文件和 ISPC 头文件
                foreach (var userStruct in userStructs)
                {
                    var headerName = NativeTranspiler.GetStructHeaderFileName(userStruct);
                    var cppHeaderPath = Path.Combine(outputDir, $"{headerName}.h");
                    WriteAllTextWithRetry(cppHeaderPath, NativeTranspiler.GenerateCppStructDefinition(userStruct));
                }

                if (anyIspc)
                {
                    var commonIspcPath = Path.Combine(outputDir, "EntJoyCommon.ispc");
                    WriteAllTextWithRetry(commonIspcPath, GenerateCommonIspcHeader());

                    // 为用户自定义结构体生成 ISPC 头文件
                    foreach (var userStruct in userStructs)
                    {
                        var headerName = NativeTranspiler.GetStructHeaderFileName(userStruct);
                        var ispcStructPath = Path.Combine(outputDir, $"{headerName}.ispc");
                        WriteAllTextWithRetry(ispcStructPath, NativeTranspiler.GenerateIspcStructDefinition(userStruct));
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

                        bool disabledAutoRefresh = GetDisabledAutoRefresh(method, attrSymbol);
                        bool fileExists = File.Exists(ispcSrcPath) || File.Exists(wrapperCppPath);

                        if (!disabledAutoRefresh || !fileExists)
                        {
                            WriteAllTextWithRetry(ispcSrcPath, ispcSource);
                            WriteAllTextWithRetry(wrapperCppPath, cppWrapper);
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
                                WriteAllTextWithRetry(mtIspcPath, mtIspcSource);
                                WriteAllTextWithRetry(mtWrapperPath, mtCppWrapper);
                            }
                            ispcFiles.Add(($"{baseName}_mt.ispc", mathLib));
                            cppFiles.Add($"{baseName}_mt_wrapper.cpp");
                        }
                    }
                    else
                    {
                        var header = CppGenerator.GenerateHeader(method);
                        var impl = CppGenerator.GenerateImplementation(method, ctx.Compilation, userStructs);

                        string hPath = Path.Combine(outputDir, $"{baseName}.h");
                        string cppPath = Path.Combine(outputDir, $"{baseName}.cpp");

                        bool disabledAutoRefresh = GetDisabledAutoRefresh(method, attrSymbol);
                        bool fileExists = File.Exists(hPath) || File.Exists(cppPath);

                        if (!disabledAutoRefresh || !fileExists)
                        {
                            WriteAllTextWithRetry(hPath, header);
                            WriteAllTextWithRetry(cppPath, impl);
                        }
                        cppFiles.Add(baseName + ".cpp");
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
                        DeleteIfExists(Path.Combine(outputDir, $"{plainBase}.h"));
                        DeleteIfExists(Path.Combine(outputDir, $"{plainBase}.cpp"));

                        var ispcSource = IspcGenerator.GenerateIspcSource(job, ctx.Compilation, userStructs);
                        var cppWrapper = IspcGenerator.GenerateCppWrapper(job, ctx.Compilation);

                        string ispcSrcPath = Path.Combine(outputDir, $"{ispcBase}.ispc");
                        string wrapperCppPath = Path.Combine(outputDir, $"{ispcBase}_wrapper.cpp");

                        bool disabledAutoRefresh = GetDisabledAutoRefresh(job, attrSymbol);
                        bool fileExists = File.Exists(ispcSrcPath) || File.Exists(wrapperCppPath);

                        if (!disabledAutoRefresh || !fileExists)
                        {
                            WriteAllTextWithRetry(ispcSrcPath, ispcSource);
                            WriteAllTextWithRetry(wrapperCppPath, cppWrapper);
                        }

                        ispcFiles.Add(($"{ispcBase}.ispc", mathLib));
                        cppFiles.Add($"{ispcBase}_wrapper.cpp");

                        if (HasUseISPC_MT(job, attrSymbol))
                        {
                            var mtIspcSource = IspcGenerator.GenerateIspcMTSource(job, ctx.Compilation, userStructs);
                            var mtCppWrapper = IspcGenerator.GenerateCppWrapperMT(job, ctx.Compilation);

                            string mtIspcPath = Path.Combine(outputDir, $"{ispcBase}_mt.ispc");
                            string mtWrapperPath = Path.Combine(outputDir, $"{ispcBase}_mt_wrapper.cpp");

                            if (!disabledAutoRefresh || !File.Exists(mtIspcPath))
                            {
                                WriteAllTextWithRetry(mtIspcPath, mtIspcSource);
                                WriteAllTextWithRetry(mtWrapperPath, mtCppWrapper);
                            }
                            ispcFiles.Add(($"{ispcBase}_mt.ispc", mathLib));
                            cppFiles.Add($"{ispcBase}_mt_wrapper.cpp");
                        }
                    }
                    else
                    {
                        DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}.ispc"));
                        DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}_wrapper.cpp"));
                        DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}_mt.ispc"));
                        DeleteIfExists(Path.Combine(outputDir, $"{ispcBase}_mt_wrapper.cpp"));

                        var header = CppJobGenerator.GenerateJobHeader(job, ctx.Compilation);
                        var impl = CppJobGenerator.GenerateJobImplementation(job, ctx.Compilation);

                        string hPath = Path.Combine(outputDir, $"{plainBase}.h");
                        string cppPath = Path.Combine(outputDir, $"{plainBase}.cpp");

                        bool disabledAutoRefresh = GetDisabledAutoRefresh(job, attrSymbol);
                        bool fileExists = File.Exists(hPath) || File.Exists(cppPath);

                        if (!disabledAutoRefresh || !fileExists)
                        {
                            WriteAllTextWithRetry(hPath, header);
                            WriteAllTextWithRetry(cppPath, impl);
                        }
                        cppFiles.Add(plainBase + ".cpp");
                    }
                }

                // 生成 run_ispc.bat
                if (ispcFiles.Count > 0)
                {
                    var batPath = Path.Combine(outputDir, "run_ispc.bat");
                    var batContent = new StringBuilder();
                    batContent.AppendLine("@echo off");
                    batContent.AppendLine("set ISPC=E:/Code/ispc-v1.30.0-windows/bin/ispc.exe");
                    batContent.AppendLine("if not exist \"%ISPC%\" (");
                    batContent.AppendLine("    echo ISPC not found at %ISPC%");
                    batContent.AppendLine("    exit /b 1");
                    batContent.AppendLine(")");
                    batContent.AppendLine("cd /d \"%~dp0\"");
                    batContent.AppendLine("if not exist build mkdir build");
                    foreach (var (ispc, mathLib) in ispcFiles)
                    {
                        string baseName = Path.GetFileNameWithoutExtension(ispc);
                        string mathLibStr = mathLib.ToString().ToLowerInvariant();
                        batContent.AppendLine($"echo Compiling {ispc}...");
                        batContent.AppendLine($"\"%ISPC%\" \"{ispc}\" -o \"build\\{baseName}.obj\" -h \"{baseName}_ispc.h\" --target=avx512skx-i32x16 --math-lib={mathLibStr} --opt=disable-fma");
                        batContent.AppendLine("if errorlevel 1 (");
                        batContent.AppendLine($"    echo Failed to compile {ispc}");
                        batContent.AppendLine("    exit /b 1");
                        batContent.AppendLine(")");
                    }
                    batContent.AppendLine("echo All ISPC files compiled successfully.");
                    WriteAllTextWithRetry(batPath, batContent.ToString());
                }

                if (cppFiles.Count > 0 || ispcFiles.Count > 0)
                {
                    string solutionBinDir = Path.GetFullPath(Path.Combine(ctx.GetProjectDirectory(), "..", "..", "bin"));
                    string nativeDllDir = Path.GetFullPath(Path.Combine(ctx.GetProjectDirectory(), "..", "NativeDll"));
                    var ispcFileNames = ispcFiles.Select(x => x.fileName).ToList();
                    var cmakeContent = GenerateCMakeLists(cppFiles, ispcFileNames, outputDir, solutionBinDir, nativeDllDir);
                    WriteAllTextWithRetry(Path.Combine(outputDir, "CMakeLists.txt"), cmakeContent);
                }

                var bindingsCode = BindingsGenerator.GenerateBindingsClass(validMarkedMethods, validJobs, ctx.Compilation);
                spc.AddSource("NativeTranspiler.Bindings.g.cs", bindingsCode);
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

        private static bool GetDisabledAutoRefresh(ISymbol symbol, INamedTypeSymbol? attrSymbol)
            => AttributeHelper.GetDisabledAutoRefresh(symbol, attrSymbol);

        private static void CollectMethodDependencies(
            IMethodSymbol method, Compilation compilation,
            HashSet<IMethodSymbol> collected, List<Diagnostic> allErrors)
        {
            var containingTypeFullName = method.ContainingType?.ToDisplayString();
            if (containingTypeFullName != null && SkipTranspileTypeNames.Contains(containingTypeFullName))
                return;
            if (method.Name == "Execute" && method.ContainingType?.AllInterfaces.Any(i =>
                i.Name == "IJob" || i.Name == "IJobParallelFor" || i.Name == "IJobFor") == true)
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
                var executeMethod = job.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
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


        private static void WriteAllTextWithRetry(string path, string contents, int maxRetries = 5)
        {
            int retryCount = 0;
            while (true)
            {
                try
                {
                    File.WriteAllText(path, contents);
                    break;
                }
                catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { throw; }
                catch (IOException) when (retryCount < maxRetries) { retryCount++; Thread.Sleep(50 * retryCount); }
                catch (UnauthorizedAccessException) when (retryCount < maxRetries) { retryCount++; Thread.Sleep(50 * retryCount); }
            }
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private static IMethodSymbol? GetMethodSymbol(GeneratorSyntaxContext ctx, CancellationToken ct)
        {
            var methodDecl = (MethodDeclarationSyntax)ctx.Node;
            var methodSymbol = ctx.SemanticModel.GetDeclaredSymbol(methodDecl, ct);
            if (methodSymbol == null) return null;
            var attrSymbol = ctx.SemanticModel.Compilation.GetTypeByMetadataName($"{AttributeNamespace}.{AttributeName}Attribute");
            return attrSymbol != null && methodSymbol.GetAttributes().Any(ad =>
                SymbolEqualityComparer.Default.Equals(ad.AttributeClass, attrSymbol)) ? methodSymbol : null;
        }

        private static INamedTypeSymbol? GetJobStructSymbol(GeneratorSyntaxContext ctx, CancellationToken ct)
        {
            var structDecl = (StructDeclarationSyntax)ctx.Node;
            var structSymbol = ctx.SemanticModel.GetDeclaredSymbol(structDecl, ct);
            if (structSymbol == null) return null;
            var attrSymbol = ctx.SemanticModel.Compilation.GetTypeByMetadataName($"{AttributeNamespace}.{AttributeName}Attribute");
            return attrSymbol != null && structSymbol.GetAttributes().Any(ad =>
                SymbolEqualityComparer.Default.Equals(ad.AttributeClass, attrSymbol)) ? structSymbol : null;
        }

        private static string GenerateAttributeCode() => $@"
using System;
namespace {AttributeNamespace}
{{
    public enum BackendTarget
    {{
        Cpp,
        Ispc
    }}

    public enum IspcMathLib
    {{
        system,
        fast,
        @default
    }}

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Struct)]
    public sealed class {AttributeName}Attribute : Attribute
    {{
        public BackendTarget Target {{ get; set; }} = BackendTarget.Cpp;
        public bool DisabledAutoRefresh {{ get; set; }} = false;
        public bool UseISPC_MT {{ get; set; }} = false;
        public IspcMathLib MathLib {{ get; set; }} = IspcMathLib.fast;
    }}
}}
";

        private static string GenerateCommonIspcHeader()
        {
            return @"
// NativeMath.ispc – ISPC compatible math library
struct float2 { float x; float y; };
struct int2   { int x; int y; };
struct uint2  { unsigned int x; unsigned int y; };

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

        private static string GenerateCMakeLists(List<string> cppFiles, List<string> ispcFiles,
                                  string outputDir, string outputBinDir, string nativeDllDir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("cmake_minimum_required(VERSION 3.10)");
            sb.AppendLine("project(NativeTranspiler_Generated LANGUAGES CXX)");
            sb.AppendLine();
            sb.AppendLine("set(CMAKE_CXX_STANDARD 20)");
            sb.AppendLine("set(CMAKE_CXX_STANDARD_REQUIRED ON)");
            sb.AppendLine();
            sb.AppendLine("include_directories(${CMAKE_CURRENT_SOURCE_DIR})");
            sb.AppendLine($"include_directories(\"{nativeDllDir.Replace("\\", "/")}\")");
            sb.AppendLine();
            sb.AppendLine("# No explicit task system defined; tasksys.cpp will pick the best one for the platform");

            string tasksysPath = Path.Combine(nativeDllDir, "tasksys.cpp").Replace("\\", "/");
            sb.AppendLine($"if(EXISTS \"{tasksysPath}\")");
            sb.AppendLine($"    set(TASKSYS_SRC \"{tasksysPath}\")");
            sb.AppendLine("else()");
            sb.AppendLine("    set(TASKSYS_SRC \"\")");
            sb.AppendLine("    message(WARNING \"tasksys.cpp not found at " + tasksysPath + "\")");
            sb.AppendLine("endif()");
            sb.AppendLine();

            sb.AppendLine("add_library(NativeTranspiler_Generated SHARED");
            foreach (var file in cppFiles)
            {
                sb.AppendLine($"    {file}");
            }
            sb.AppendLine("    ${TASKSYS_SRC}");
            sb.AppendLine(")");
            sb.AppendLine();

            if (ispcFiles.Count > 0)
            {
                sb.AppendLine("if(TASKSYS_SRC)");
                sb.AppendLine("    set_source_files_properties(${TASKSYS_SRC} PROPERTIES COMPILE_FLAGS \"/arch:AVX\")");
                sb.AppendLine("endif()");
                sb.AppendLine();

                sb.AppendLine("find_program(ISPC_EXECUTABLE ispc)");
                sb.AppendLine("if(NOT ISPC_EXECUTABLE)");
                sb.AppendLine("    set(ISPC_EXECUTABLE \"E:/Code/ispc-v1.30.0-windows/bin/ispc.exe\")");
                sb.AppendLine("endif()");
                sb.AppendLine();
                sb.AppendLine("add_custom_command(TARGET NativeTranspiler_Generated PRE_BUILD");
                sb.AppendLine("    COMMAND \"${CMAKE_CURRENT_SOURCE_DIR}/run_ispc.bat\"");
                sb.AppendLine("    WORKING_DIRECTORY \"${CMAKE_CURRENT_SOURCE_DIR}\"");
                sb.AppendLine("    COMMENT \"Running ISPC compiler for generated .ispc files\"");
                sb.AppendLine(")");
                sb.AppendLine();
                sb.AppendLine("set(ISPC_OBJECTS");
                foreach (var ispc in ispcFiles)
                {
                    string baseName = Path.GetFileNameWithoutExtension(ispc);
                    sb.AppendLine($"    \"${{CMAKE_CURRENT_BINARY_DIR}}/{baseName}.obj\"");
                }
                sb.AppendLine(")");
                sb.AppendLine("target_link_libraries(NativeTranspiler_Generated PRIVATE ${ISPC_OBJECTS})");
                sb.AppendLine();
            }

            sb.AppendLine("if(MSVC)");
            sb.AppendLine("    target_compile_options(NativeTranspiler_Generated PRIVATE /std:c++20 /O2 /Ob2 /Oi /Ot /GL /arch:AVX2 /Qpar)");
            sb.AppendLine("    target_compile_definitions(NativeTranspiler_Generated PRIVATE NDEBUG NOMINMAX)");
            sb.AppendLine("    set_target_properties(NativeTranspiler_Generated PROPERTIES INTERPROCEDURAL_OPTIMIZATION TRUE)");
            sb.AppendLine("else()");
            sb.AppendLine("    target_compile_options(NativeTranspiler_Generated PRIVATE -O3 -march=native -mtune=native -ffast-math -ffp-contract=fast -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer)");
            sb.AppendLine("    target_compile_definitions(NativeTranspiler_Generated PRIVATE NDEBUG)");
            sb.AppendLine("endif()");
            sb.AppendLine();

            var binPath = outputBinDir.Replace("\\", "/");
            sb.AppendLine($"set_target_properties(NativeTranspiler_Generated PROPERTIES");
            sb.AppendLine($"    RUNTIME_OUTPUT_DIRECTORY \"{binPath}\"");
            sb.AppendLine($"    LIBRARY_OUTPUT_DIRECTORY \"{binPath}\"");
            sb.AppendLine($"    ARCHIVE_OUTPUT_DIRECTORY \"{binPath}\"");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine($"foreach(OUTPUTCONFIG ${{CMAKE_CONFIGURATION_TYPES}})");
            sb.AppendLine($"    string(TOUPPER ${{OUTPUTCONFIG}} OUTPUTCONFIG)");
            sb.AppendLine($"    set_target_properties(NativeTranspiler_Generated PROPERTIES");
            sb.AppendLine($"        RUNTIME_OUTPUT_DIRECTORY_${{OUTPUTCONFIG}} \"{binPath}\"");
            sb.AppendLine($"        LIBRARY_OUTPUT_DIRECTORY_${{OUTPUTCONFIG}} \"{binPath}\"");
            sb.AppendLine($"        ARCHIVE_OUTPUT_DIRECTORY_${{OUTPUTCONFIG}} \"{binPath}\"");
            sb.AppendLine($"    )");
            sb.AppendLine($"endforeach()");
            sb.AppendLine();
            sb.AppendLine("if(WIN32)");
            sb.AppendLine("    set_target_properties(NativeTranspiler_Generated PROPERTIES SUFFIX \".dll\")");
            sb.AppendLine("endif()");
            return sb.ToString();
        }
    }
}
