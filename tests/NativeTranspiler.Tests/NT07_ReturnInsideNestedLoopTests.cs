using System;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-07（High）：体内**循环里**的 `return;` 在 C++/SIMD 路径被翻成 `break;`，
    /// 而 break 只跳出那个内层循环 ⇒ 该实体的剩余语句照跑（静默错值）。
    ///
    /// ISPC 侧已有先例：Analyzer/Ispc/IspcStatementTranslator.cs:398 写
    /// `__ENTJOY_UNSUPPORTED_STMT__ISPC_ReturnInsideNestedLoopInBatch`，
    /// 由 NativeCompileTask.CheckGeneratedMarkers（认 `__ENTJOY_UNSUPPORTED` 前缀）让构建失败。
    /// C++/SIMD 侧必须同构地写标记，而不是静默错执行。
    /// </summary>
    public class NT07_ReturnInsideNestedLoopTests
    {
        // 与 Analyzer/Common/UnsupportedMarkers.cs 的 Stmt 前缀一致：__ENTJOY_UNSUPPORTED_STMT__<构造>
        private const string Marker = "__ENTJOY_UNSUPPORTED_STMT__Cpp_ReturnInsideNestedLoop";

        private readonly ITestOutputHelper _out;
        public NT07_ReturnInsideNestedLoopTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void EntityAutoSimd_NestedLoopReturn_EmitsUnsupportedMarker()
        {
            // IJobEntity + AutoSIMD.Enabled → CppJobGenerator.GenerateEntityFunctionStandard（:643/:674）
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;

public struct LNest { public float Marker; public float V; }

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct EntityNestedReturnJob : IJobEntity
{
    public void Execute(ref LNest p)
    {
        for (int k = 0; k < 4; k = k + 1)
        {
            if (p.V < 0.0f) return;
            p.V = p.V + 1.0f;
        }
        p.Marker = p.Marker + 1.0f;
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);

            Assert.Contains("EntityNestedReturnJob_Execute", result.Cpp);
            Assert.True(result.Cpp.Contains(Marker),
                GeneratorHarness.Fail(result, "嵌套循环内的 return; 必须写唯一标记（否则 break 只跳出内层循环 ⇒ 静默错值）"));
        }

        [Fact]
        public void EntityVectorize_NestedLoopReturn_EmitsUnsupportedMarker()
        {
            // IJobEntity + AutoSIMD.Vectorize → GenerateEntityFunctionVectorize
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;

public struct LNest2 { public float Marker; public float V; }

[NativeTranspile(AutoSIMD = AutoSIMD.Vectorize)]
public struct VectorizeNestedReturnJob : IJobEntity
{
    public void Execute(ref LNest2 p)
    {
        for (int k = 0; k < 4; k = k + 1)
        {
            if (p.V < 0.0f) return;
            p.V = p.V + 1.0f;
        }
        p.Marker = p.Marker + 1.0f;
    }
}
");
            _out.WriteLine(result.Cpp);
            Assert.True(result.Cpp.Contains(Marker),
                GeneratorHarness.Fail(result, "Vectorize 路径的嵌套循环 return; 也必须写标记"));
        }

        [Fact]
        public void PerLaneSimd_NestedLoopReturn_EmitsUnsupportedMarker()
        {
            // IJobParallelFor + AutoSIMD.Enabled，体内"varying 边界循环"⇒ GeneratePerLaneFullBody
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct PerLaneNestedReturnJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        for (int k = 0; k < index; k = k + 1)
        {
            if (Out[index] < 0.0f) return;
        }
        Out[index] = 1.0f;
    }
}
");
            _out.WriteLine(result.Cpp);
            Assert.True(result.Cpp.Contains(Marker),
                GeneratorHarness.Fail(result, "per-lane SIMD 路径的嵌套循环 return; 也必须写标记"));
        }

        [Fact]
        public void EntityPlain_NestedLoopReturn_EmitsUnsupportedMarker()
        {
            // IJobEntity（AutoSIMD 默认关闭）→ GenerateEntityChunkFunctionStandard：
            // 与 AutoSIMD.Enabled 路径同一缺陷类（break 只跳内层循环），必须同构处理。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;

public struct LNest3 { public float Marker; public float V; }

[NativeTranspile]
public struct PlainNestedReturnJob : IJobEntity
{
    public void Execute(ref LNest3 p)
    {
        for (int k = 0; k < 4; k = k + 1)
        {
            if (p.V < 0.0f) return;
            p.V = p.V + 1.0f;
        }
        p.Marker = p.Marker + 1.0f;
    }
}
");
            _out.WriteLine(result.Cpp);
            Assert.True(result.Cpp.Contains(Marker),
                GeneratorHarness.Fail(result, "无 AutoSIMD 的 IJobEntity 路径也必须写标记"));
        }

        [Fact]
        public void EntityPlain_IndexLevelReturn_DegradesToBreakInsideDoWhile()
        {
            // 反向守卫：index 层级的 `return;`（不在循环里）是**支持**的：do{...}while(false) + break。
            // 不得留下裸 `return;`（那会退出整条实体循环所在函数），也不得写标记把构建打红。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;

public struct LGuard { public float Marker; public float V; }

[NativeTranspile]
public struct PlainEarlyExitEntityJob : IJobEntity
{
    public void Execute(ref LGuard p)
    {
        if (p.V < 0.0f) return;
        p.Marker = p.Marker + 1.0f;
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");
            // 定位**函数定义**（CMakeLists/头文件里也含函数名，不能用裸 IndexOf）
            const string def = "GENERATED_API void CALLINGCONVENTION SharpNative_Job__global_namespace__PlainEarlyExitEntityJob_Execute(";
            int fn = text.IndexOf(def, StringComparison.Ordinal);
            Assert.True(fn >= 0, GeneratorHarness.Fail(result, "找不到产物函数定义"));
            int end = text.IndexOf("\n}", fn, StringComparison.Ordinal);
            string body = end > fn ? text.Substring(fn, end - fn) : text.Substring(fn);

            Assert.Contains("do {", body);
            Assert.Contains("} while(false);", body);
            Assert.Contains("break;", body);
            Assert.DoesNotContain("return;", body);
            Assert.DoesNotContain(Marker, body);
        }

        [Fact]
        public void IndexLevelReturn_DoesNotEmitMarker()
        {
            // 反向守卫：index 层级的 return; 是**支持**的（do-while + break），不得写标记把构建打红。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile]
public struct PlainEarlyExitJob : IJobParallelFor
{
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        if (Out[index] < 0) return;
        Out[index] = 1.0f;
    }
}
");
            Assert.DoesNotContain(Marker, result.Cpp);
        }
    }
}
