using Xunit;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// AutoSIMD 摊平发射：组件是「单字段向量包装」或「N 个同类型标量字段」时，
    /// 实体循环按标量槽重排成连续访问，不再按结构体步长 gather。
    /// </summary>
    public class FlatSlotChunkEmissionTests
    {
        [Fact]
        public void VectorFieldColumnEmitsFlatSlotLoop()
        {
            const string source = @"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Mathematics;

public struct FlatMovePos { public float2 Value; }
public struct FlatMoveVel { public float2 Value; }

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct FlatVectorJob : IJobChunk
{
    public float DeltaTime;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var positions = chunk.GetComponentDataNativeArray<FlatMovePos>();
        var velocities = chunk.GetComponentDataNativeArray<FlatMoveVel>();

        for (int index = 0; index < positions.Length; index++)
        {
            FlatMovePos position = positions[index];
            position.Value += velocities[index].Value * DeltaTime;
            positions[index] = position;
        }
    }
}";
            var result = GeneratorHarness.EmitFor(source);
            string text = Normalize(result.Cpp);
            Assert.True(text.Contains("FlatVectorJob_Execute"), GeneratorHarness.Fail(result, "内核没有生成 ⇒ 断言是空跑"));
            Assert.True(text.Contains("int __flat_end = __entity_count * 2;"),
                GeneratorHarness.Fail(result, "两个槽的组件应摊成 n*2 个标量槽"));
            Assert.True(text.Contains("((float*)positions_ptr)[__flat]"),
                GeneratorHarness.Fail(result, "连续列应按标量槽直接读写"));
            Assert.False(text.Contains("n_gather_ps"),
                GeneratorHarness.Fail(result, "摊平后不应再有 gather"));
        }

        [Fact]
        public void ScalarFieldColumnEmitsFlatSlotLoop()
        {
            const string source = @"
using NativeTranspiler;
using EntJoy.ECS;

public struct FlatPairPos { public float X; public float Y; }
public struct FlatPairVel { public float X; public float Y; }

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct FlatScalarJob : IJobChunk
{
    public float DeltaTime;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var positions = chunk.GetComponentDataNativeArray<FlatPairPos>();
        var velocities = chunk.GetComponentDataNativeArray<FlatPairVel>();

        for (int index = 0; index < positions.Length; index++)
        {
            FlatPairPos position = positions[index];
            position.X += velocities[index].X * DeltaTime;
            position.Y += velocities[index].Y * DeltaTime;
            positions[index] = position;
        }
    }
}";
            var result = GeneratorHarness.EmitFor(source);
            string text = Normalize(result.Cpp);
            Assert.True(text.Contains("FlatScalarJob_Execute"), GeneratorHarness.Fail(result, "内核没有生成 ⇒ 断言是空跑"));
            Assert.True(text.Contains("int __flat_end = __entity_count * 2;"),
                GeneratorHarness.Fail(result, "两个标量字段应摊成 n*2 个标量槽"));
            Assert.Contains("((float*)positions_ptr)[__flat] += ((float*)velocities_ptr)[__flat] * DeltaTime;", text);
            Assert.False(text.Contains("n_gather_ps"), GeneratorHarness.Fail(result, "摊平后不应再有 gather"));
        }

        [Fact]
        public void ScalarFieldColumnWithPerSlotConstantsEmitsSelect()
        {
            const string source = @"
using NativeTranspiler;
using EntJoy.ECS;

public struct FlatConstPos { public float X; public float Y; }
public struct FlatConstVel { public float X; public float Y; }

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct FlatConstJob : IJobChunk
{
    public float DeltaTime;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var positions = chunk.GetComponentDataNativeArray<FlatConstPos>();
        var velocities = chunk.GetComponentDataNativeArray<FlatConstVel>();

        for (int index = 0; index < positions.Length; index++)
        {
            FlatConstPos position = positions[index];
            position.X += velocities[index].X * DeltaTime * 0.5f;
            position.Y += velocities[index].Y * DeltaTime * 0.25f;
            positions[index] = position;
        }
    }
}";
            var result = GeneratorHarness.EmitFor(source);
            string text = Normalize(result.Cpp);
            Assert.True(text.Contains("FlatConstJob_Execute"), GeneratorHarness.Fail(result, "内核没有生成 ⇒ 断言是空跑"));
            Assert.True(text.Contains("int __flat_end = __entity_count * 2;"),
                GeneratorHarness.Fail(result, "两个标量字段应摊成 n*2 个标量槽"));
            Assert.Contains("__flat % 2 == 0 ? 0.5f", text);
            Assert.Contains("0.25f", text);
            Assert.False(text.Contains("n_gather_ps"), GeneratorHarness.Fail(result, "摊平后不应再有 gather"));
        }

        [Fact]
        public void CrossColumnCopyKeepsGatherPath()
        {
            const string source = @"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Mathematics;

public struct FlatCopySrc { public float2 Value; }
public struct FlatCopyDst { public float2 Value; }
public struct FlatCopyVel { public float2 Value; }

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct FlatCrossColumnJob : IJobChunk
{
    public float DeltaTime;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var source = chunk.GetComponentDataNativeArray<FlatCopySrc>();
        var positions = chunk.GetComponentDataNativeArray<FlatCopyDst>();
        var velocities = chunk.GetComponentDataNativeArray<FlatCopyVel>();

        for (int index = 0; index < positions.Length; index++)
        {
            FlatCopySrc position = source[index];
            position.Value += velocities[index].Value * DeltaTime;
            positions[index] = position;
        }
    }
}";
            var result = GeneratorHarness.EmitFor(source);
            string text = Normalize(result.Cpp);
            Assert.True(text.Contains("FlatCrossColumnJob_Execute"), GeneratorHarness.Fail(result, "内核没有生成 ⇒ 断言是空跑"));
            // 摊平会去掉「从读列取初值」这一步，跨列拷贝必须退回原路径。
            Assert.DoesNotContain("__flat_end", text);
        }

        [Fact]
        public void NonZeroStartLoopKeepsGatherPath()
        {
            const string source = @"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Mathematics;

public struct FlatOffsetPos { public float2 Value; }
public struct FlatOffsetVel { public float2 Value; }

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct FlatOffsetJob : IJobChunk
{
    public float DeltaTime;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var positions = chunk.GetComponentDataNativeArray<FlatOffsetPos>();
        var velocities = chunk.GetComponentDataNativeArray<FlatOffsetVel>();

        for (int index = 1; index < positions.Length; index++)
        {
            FlatOffsetPos position = positions[index];
            position.Value += velocities[index].Value * DeltaTime;
            positions[index] = position;
        }
    }
}";
            var result = GeneratorHarness.EmitFor(source);
            string text = Normalize(result.Cpp);
            Assert.True(text.Contains("FlatOffsetJob_Execute"), GeneratorHarness.Fail(result, "内核没有生成 ⇒ 断言是空跑"));
            // 平坦循环覆盖 [0, n*slots)，从 1 起步的源循环会多处理实体 0 ⇒ 必须退回按结构体访存。
            Assert.DoesNotContain("__flat_end", text);
        }

        [Fact]
        public void ReadOnlyColumnEmitsConstFlatAccess()
        {
            const string source = @"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Mathematics;

public struct FlatRoPos { public float2 Value; }
public struct FlatRoVel { public float2 Value; }

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct FlatReadOnlyJob : IJobChunk
{
    public float DeltaTime;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var positions = chunk.GetComponentDataNativeArray<FlatRoPos>();
        var velocities = chunk.GetComponentReadOnlyDataNativeArray<FlatRoVel>();

        for (int index = 0; index < positions.Length; index++)
        {
            FlatRoPos position = positions[index];
            position.Value += velocities[index].Value * DeltaTime;
            positions[index] = position;
        }
    }
}";
            var result = GeneratorHarness.EmitFor(source);
            string text = Normalize(result.Cpp);
            Assert.True(text.Contains("FlatReadOnlyJob_Execute"), GeneratorHarness.Fail(result, "内核没有生成 ⇒ 断言是空跑"));
            Assert.Contains("((float*)positions_ptr)[__flat]", text);
            Assert.Contains("((const float*)velocities_ptr)[__flat]", text);
        }

        [Fact]
        public void StencilColumnKeepsGatherPath()
        {
            const string source = @"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Mathematics;

public struct FlatStencilPos { public float2 Value; }
public struct FlatStencilVel { public float2 Value; }

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct FlatStencilJob : IJobChunk
{
    public float DeltaTime;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var positions = chunk.GetComponentDataNativeArray<FlatStencilPos>();
        var velocities = chunk.GetComponentDataNativeArray<FlatStencilVel>();

        for (int index = 1; index < positions.Length - 1; index++)
        {
            FlatStencilPos position = positions[index];
            position.Value += positions[index + 1].Value * DeltaTime;
            positions[index] = position;
        }
    }
}";
            var result = GeneratorHarness.EmitFor(source);
            string text = Normalize(result.Cpp);
            Assert.True(text.Contains("FlatStencilJob_Execute"), GeneratorHarness.Fail(result, "内核没有生成 ⇒ 断言是空跑"));
            Assert.DoesNotContain("__flat", text);
        }

        private static string Normalize(string text) => text.Replace("\r\n", "\n");

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int at = 0;
            while ((at = text.IndexOf(needle, at, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }
    }
}
