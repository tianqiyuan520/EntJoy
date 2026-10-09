using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NativeTranspiler.Analyzer;

namespace NativeTranspiler.Tests
{
    /// <summary>一次生成的结果：生成的 C++/ISPC 文本 + 生成的 C# 绑定文本 + 诊断。</summary>
    public sealed class EmitResult
    {
        public string Cpp { get; }
        public string Bindings { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }
        public string OutputDir { get; }

        public EmitResult(string cpp, string bindings, ImmutableArray<Diagnostic> diagnostics, string outputDir)
        {
            Cpp = cpp;
            Bindings = bindings;
            Diagnostics = diagnostics;
            OutputDir = outputDir;
        }

        /// <summary>C++/ISPC 产物 + C# 绑定，测试断言统一用这个（与真实构建里"两份文本都存在"一致）。</summary>
        public string All => Cpp + Environment.NewLine + Bindings;

        public bool HasDiagnostic(string id) => Diagnostics.Any(d => d.Id == id);

        public IEnumerable<Diagnostic> Of(string id) => Diagnostics.Where(d => d.Id == id);

        public string DiagnosticSummary =>
            Diagnostics.Length == 0
                ? "(none)"
                : string.Join(" || ", Diagnostics.Select(d => $"{d.Id}[{d.Severity}] {d.GetMessage()}"));

        /// <summary>完整产物（含文件名分隔），失败信息里用它做现场。</summary>
        public string Dump() => All;
    }

    /// <summary>
    /// 进程内驱动 NativeTranspilerGenerator 的测试夹具。
    ///
    /// 做法：用 CSharpGeneratorDriver 跑一次增量生成器（不是 analyzer），
    ///   · C++/ISPC 产物走 CodeGenIo 落盘 → 从临时 projectdir 的 NativeTranspiler_Generated/ 读回
    ///   · C# 绑定走 spc.AddSource → 从 GeneratorDriverRunResult.GeneratedTrees 读回
    ///   · 诊断（NT005/NT024/NT028…）从 GeneratorDriverRunResult.Diagnostics 读回
    ///
    /// 需要 NativeArray / Job 接口 / [NativeTranspile] 等运行时类型的最小替身（见 <see cref="Stubs"/>），
    /// 因为生成器只看"命名空间 + 名字"。
    /// </summary>
    public static class GeneratorHarness
    {
        private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
        {
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
            return tpa.Split(Path.PathSeparator)
                .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToImmutableArray();
        });

        /// <summary>
        /// 运行时类型替身（EntJoy 侧）。
        ///
        /// ⚠ 这里**不要**定义 NativeTranspiler.NativeTranspileAttribute / 那几个枚举：
        /// 生成器自己用 RegisterPostInitializationOutput 注入它们（RuntimeApi.GenerateAttributeSource），
        /// 而 post-init 源会被并进同一次生成的 compilation。测试里再定义一份 = 同名类型重复定义
        /// ⇒ GetTypeByMetadataName 返回 null（歧义）⇒ 生成器一个 job 都认不出来（实测：产物为空）。
        /// </summary>
        public const string Stubs = @"
using System;
namespace EntJoy.Collections
{
    public unsafe struct NativeArray<T> where T : unmanaged
    {
        private void* _buffer;
        private int _length;
        public int Length => _length;
        public T this[int index] { get { return default; } set { } }
        public void* GetUnsafePtr() => _buffer;
    }
    public unsafe struct NativeList<T> where T : unmanaged
    {
        private void* _buffer;
        private int _length;
        public int Length => _length;
        public T this[int index] { get { return default; } set { } }
    }
}
namespace EntJoy.JobSystem
{
    public interface IJob { }
    public interface IJobFor { }
    public interface IJobParallelFor { }
    public interface IJobParallelForBatch { }
}
namespace EntJoy.ECS
{
    using EntJoy.Collections;
    public struct Entity { public int Id; public int Version; }
    public class World { }   // 生成器自校验用 Config.TypeWorld == EntJoy.ECS.World
    // SendEvent stub: 生成器按 命名空间+名字 识别，这里只需让测试源码能编译出可解析的方法符号
    // （类型实参由泛型推断）。用于 IspcMarkerGateTests 的 ISPC 标记缺口回归。
    public static class SystemAPI { public static void SendEvent<T>(T evt) where T : unmanaged { } }
    public struct ChunkEnabledMask { public unsafe ulong* Bits; }
    public unsafe struct ArchetypeChunk
    {
        private int _count;
        public int Count => _count;
        public NativeArray<T> GetComponentDataNativeArray<T>() where T : unmanaged => default;
        public Span<T> GetComponentDataSpan<T>() where T : unmanaged => default;
        public T* GetComponentDataPtr<T>() where T : unmanaged => null;
    }
    public interface IJobChunk { }
    public interface IJobEntity { }
}
";

        /// <summary>建一次"待生成的编译"（替身 + 用户代码）。</summary>
        public static CSharpCompilation CreateCompilation(string userSource, out string projectDir, string extraSource = null)
        {
            projectDir = Path.Combine(Path.GetTempPath(), "entjoy-nt-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projectDir);

            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
            var trees = new List<SyntaxTree>
            {
                CSharpSyntaxTree.ParseText(Stubs, parseOptions, Path.Combine(projectDir, "EntJoyStubs.cs")),
                CSharpSyntaxTree.ParseText(userSource, parseOptions, Path.Combine(projectDir, "UserCode.cs")),
            };
            if (!string.IsNullOrEmpty(extraSource))
                trees.Add(CSharpSyntaxTree.ParseText(extraSource, parseOptions, Path.Combine(projectDir, "Extra.cs")));

            return CSharpCompilation.Create(
                "NativeTranspilerTestAssembly",
                trees,
                References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        }

        public static AnalyzerConfigOptionsProvider CreateOptions(string projectDir, bool autoSimdMeasured = true,
            IReadOnlyDictionary<string, string> extraProperties = null)
            => new TestAnalyzerConfigOptionsProvider(projectDir, autoSimdMeasured, extraProperties);

        /// <summary>调试用：暴露驱动过程的原始对象。</summary>
        public static (CSharpCompilation compilation, GeneratorDriver driver, GeneratorDriverRunResult run, string projectDir)
            RawRun(string userSource, string extraSource = null, bool autoSimdMeasured = true,
                IReadOnlyDictionary<string, string> extraProperties = null)
        {
            var compilation = CreateCompilation(userSource, out var projectDir, extraSource);
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
            var optionsProvider = CreateOptions(projectDir, autoSimdMeasured, extraProperties);

            IIncrementalGenerator generator = new NativeTranspilerGenerator();
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { generator.AsSourceGenerator() },
                additionalTexts: null,
                parseOptions: parseOptions,
                optionsProvider: optionsProvider,
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
            driver = driver.RunGenerators(compilation);
            var run = driver.GetRunResult();
            return (compilation, driver, run, projectDir);
        }

        /// <summary>
        /// 跑一次生成器并返回全部产物文本 + 诊断。
        /// <paramref name="autoSimdMeasured"/> 默认 <c>true</c>：测试夹具的绝大多数用例就是**为验证
        /// AutoSIMD 产物**而存在，等价于"已由本夹具/AutoSIMDVerify 量过"；默认关闭时的行为（NT024 error）
        /// 由 NT05 的专项用例显式传 false 覆盖。
        /// </summary>
        public static EmitResult EmitFor(string userSource, string extraSource = null, bool autoSimdMeasured = true,
            IReadOnlyDictionary<string, string> extraProperties = null)
        {
            var (compilation, _, run, projectDir) = RawRun(userSource, extraSource, autoSimdMeasured, extraProperties);

            var bindings = new StringBuilder();
            foreach (var tree in run.GeneratedTrees)
                bindings.AppendLine(tree.GetText().ToString());

            var outputDir = Path.Combine(projectDir, "NativeTranspiler_Generated");
            var cpp = new StringBuilder();
            if (Directory.Exists(outputDir))
            {
                var files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.Combine("NativeTranspiler_Generated", "build") + Path.DirectorySeparatorChar))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var file in files)
                {
                    cpp.AppendLine($"// ===== {Path.GetFileName(file)} =====");
                    cpp.AppendLine(File.ReadAllText(file));
                }
            }

            return new EmitResult(cpp.ToString(), bindings.ToString(), run.Diagnostics, outputDir);
        }

        /// <summary>
        /// 断言"这次发射确实识别到了 job"，再返回产物。
        ///
        /// 动机（2026-09-30 实测踩过）：源码**漏写/写错 `[NativeTranspile]`** 时生成器**不报任何诊断**
        /// （无属性的 struct 不是 job —— 托管 job 是受支持配置），`EmitFor` 只是返回**空串**
        /// ⇒ 以"某段文本必须存在"为判据的测试会**假绿**，甚至被误读成"生成器静默产出空集"的缺陷。
        /// 凡是要断言**发射面存在**的用例，一律走这个方法。
        /// </summary>
        public static EmitResult EmitForJob(string userSource, string extraSource = null, bool autoSimdMeasured = true)
        {
            var result = EmitFor(userSource, extraSource, autoSimdMeasured);
            if (!result.Cpp.Contains("GENERATED_API"))
                throw new InvalidOperationException(
                    "EmitForJob：没有识别到任何 job —— 源码是否漏写 [NativeTranspile]（或属性名拼错）？" +
                    Environment.NewLine + "诊断: " + result.DiagnosticSummary +
                    Environment.NewLine + "输出目录: " + result.OutputDir);
            return result;
        }

        /// <summary>拼装断言失败信息（把现场文本带上，便于定位）。</summary>
        public static string Fail(EmitResult result, string why)
            => why + Environment.NewLine + "诊断: " + result.DiagnosticSummary + Environment.NewLine + result.Dump();

        private sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
        {
            private readonly TestAnalyzerConfigOptions _options;
            public TestAnalyzerConfigOptionsProvider(string projectDir, bool autoSimdMeasured,
                IReadOnlyDictionary<string, string> extraProperties = null)
            {
                var values = new Dictionary<string, string>
                {
                    ["build_property.projectdir"] = projectDir,
                };
                if (autoSimdMeasured)
                    values["build_property.EntJoyAutoSimdMeasured"] = "true";
                if (extraProperties != null)
                    foreach (var kv in extraProperties)
                        values[kv.Key] = kv.Value;
                _options = new TestAnalyzerConfigOptions(values);
            }

            public override AnalyzerConfigOptions GlobalOptions => _options;
            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;
            public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
        }

        private sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
        {
            private readonly Dictionary<string, string> _values;
            public TestAnalyzerConfigOptions(Dictionary<string, string> values) => _values = values;
            public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value);
        }
    }
}
