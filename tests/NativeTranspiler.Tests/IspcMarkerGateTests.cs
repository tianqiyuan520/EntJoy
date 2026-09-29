using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// ISPC 静默降级"标记缺口"的回归守卫（审计的最后一处）。
    ///
    /// `SendEvent(已有变量)`（实参**不是**对象创建）在 ISPC 侧无法逐字段写入事件槽位：
    /// 旧实现只写一条普通注释 ⇒ 构建通过、这次事件**静默丢失**（与 `unchecked{}`/`histPtr[key]++`
    /// 两起历史事故同类）。现必须写 `__ENTJOY_UNSUPPORTED_STMT__…` 标记，
    /// 由 NativeTranspiler.Tasks 的 NativeCompileTask.CheckGeneratedMarkers 让构建失败。
    ///
    /// 现场：Analyzer/Ispc/IspcStatementTranslator.cs 的 GenerateSendEventIspc（非对象创建实参分支）。
    /// 契约见 docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md。
    /// </summary>
    public class IspcMarkerGateTests
    {
        private const string Marker = "__ENTJOY_UNSUPPORTED_STMT__ISPC_SendEventNonObjectCreationArg";

        private readonly ITestOutputHelper _out;
        public IspcMarkerGateTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void Ispc_SendEventWithExistingVariable_EmitsUnsupportedMarker()
        {
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;
using static EntJoy.ECS.EventBus;

public struct LTag { public float V; }
public struct LIspcEvt { public int Kind; public float Amount; }

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct IspcSendEventVarJob : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var tags = chunk.GetComponentDataNativeArray<LTag>();
        for (int i = 0; i < tags.Length; i++)
        {
            if (tags[i].V > 0.0f)
            {
                var evt = new LIspcEvt { Kind = 1, Amount = tags[i].V };
                SendEvent(evt);
            }
        }
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);

            // ISPC 后端确实产出了 .ispc（否则断言会"因为没生成"而假通过）
            Assert.Contains("#include \"EntJoyCommon.ispc\"", result.Cpp.Replace("\r\n", "\n"));
            Assert.True(result.Cpp.Contains(Marker),
                GeneratorHarness.Fail(result,
                    "ISPC 侧非对象创建实参的 SendEvent 必须写唯一标记（否则事件静默丢失、构建不失败）"));
        }

        [Fact]
        public void Ispc_SendEventWithObjectCreation_DoesNotEmitMarker()
        {
            // 反向守卫：对象创建形态（字段逐条写槽）是**支持**的，不得写标记把构建打红。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;
using static EntJoy.ECS.EventBus;

public struct LTag2 { public float V; }
public struct LIspcEvt2 { public int Kind; public float Amount; }

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct IspcSendEventNewJob : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var tags = chunk.GetComponentDataNativeArray<LTag2>();
        for (int i = 0; i < tags.Length; i++)
        {
            if (tags[i].V > 0.0f)
            {
                SendEvent(new LIspcEvt2 { Kind = 1, Amount = tags[i].V });
            }
        }
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);
            Assert.DoesNotContain(Marker, result.Cpp);
        }
    }
}
