using System;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-02（Critical）：IJobEntity + AutoSIMD.Vectorize 路径把 `return;` **删掉**，
    /// 无括号 `if (c) return;` 后面那句就被"并进" if 真分支 ⇒ 控制流反转、静默错值。
    ///
    /// 现场：CppJobGenerator.GenerateEntityFunctionVectorize
    ///   `scalarBody = scalarBody.Replace("return;", "");`
    /// ⇒ `if (p.X &lt; 0) return; p.Marker += 1;` 变成
    ///   `if (p.X &lt; 0) p.Marker += 1;`（语义完全反过来）。
    /// </summary>
    public class NT02_EntityVectorizeReturnTests
    {
        private readonly ITestOutputHelper _out;
        public NT02_EntityVectorizeReturnTests(ITestOutputHelper output) => _out = output;

        private const string Source = @"
using NativeTranspiler;
using EntJoy.ECS;

public struct LPose { public float X; public float Marker; }

[NativeTranspile(AutoSIMD = AutoSIMD.Vectorize)]
public struct VectorizeEarlyExitJob : IJobEntity
{
    public void Execute(ref LPose p)
    {
        if (p.X < 0) return;
        p.Marker = p.Marker + 1.0f;
    }
}
";

        [Fact]
        public void VectorizePath_EarlyReturn_StillSkipsRestOfBody()
        {
            var result = GeneratorHarness.EmitFor(Source);
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);

            Assert.Contains("SharpNative_Job__global_namespace__VectorizeEarlyExitJob_Execute", result.Cpp);
            Assert.False(result.HasDiagnostic("NT026"), GeneratorHarness.Fail(result, "生成器崩溃"));

            string text = result.Cpp.Replace("\r\n", "\n");

            // 前置：guard 条件确实进了产物
            int guard = text.IndexOf("X < 0", StringComparison.Ordinal);
            Assert.True(guard >= 0, GeneratorHarness.Fail(result, "产物里找不到 guard 条件 `X < 0`"));

            // 缺陷点：guard 之后必须先把"本次实体结束"表达出来（break/continue/do-while），
            // 且必须发生在 Marker 自增之前；旧产物里 return; 被删掉，Marker 自增直接变成 if 的 body。
            string window = text.Substring(guard, Math.Min(400, text.Length - guard));
            int exitIdx = window.IndexOf("break;", StringComparison.Ordinal);
            int continueIdx = window.IndexOf("continue;", StringComparison.Ordinal);
            int markerIdx = window.IndexOf("Marker =", StringComparison.Ordinal);
            if (exitIdx < 0 || (continueIdx >= 0 && continueIdx < exitIdx)) exitIdx = continueIdx;

            Assert.True(exitIdx >= 0,
                GeneratorHarness.Fail(result, "guard 之后没有任何 `break;`/`continue;` —— 早退出语句被删掉了"));

            Assert.True(markerIdx < 0 || exitIdx < markerIdx,
                GeneratorHarness.Fail(result, "guard 之后 Marker 自增发生在退出语句之前 ⇒ 控制流反转（原 if 体变成整段 body）"));

            // 不得出现"裸 if 头后面直接跟下一条语句"（即 return 被删后留下的空 if）
            Assert.False(Regex.IsMatch(window, @"if \([^\n]*\)\s*\n\s*(?!break;|continue;|\{)\S"),
                GeneratorHarness.Fail(result, "出现没有 body 的裸 if（return; 被删后的残留）"));
        }

        [Fact]
        public void VectorizePath_WithBracedIf_KeepsGuard()
        {
            // 有括号写法是既有可达形态，改动后必须保持正确（回归守卫）。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;

public struct LPose2 { public float X; public float Marker; }

[NativeTranspile(AutoSIMD = AutoSIMD.Vectorize)]
public struct BracedEarlyExitJob : IJobEntity
{
    public void Execute(ref LPose2 p)
    {
        if (p.X < 0) { return; }
        p.Marker = p.Marker + 1.0f;
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            Assert.Contains("SharpNative_Job__global_namespace__BracedEarlyExitJob_Execute", result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");
            int guard = text.IndexOf("X < 0", StringComparison.Ordinal);
            string window = text.Substring(guard, Math.Min(400, text.Length - guard));
            int exitIdx = window.IndexOf("break;", StringComparison.Ordinal);
            int markerIdx = window.IndexOf("Marker =", StringComparison.Ordinal);
            Assert.True(exitIdx >= 0, GeneratorHarness.Fail(result, "有括号 if 的 return; 也必须降级为 break"));
            Assert.True(markerIdx < 0 || exitIdx < markerIdx,
                GeneratorHarness.Fail(result, "有括号 if 的 guard 之后 Marker 自增先于退出语句"));
        }
    }
}
