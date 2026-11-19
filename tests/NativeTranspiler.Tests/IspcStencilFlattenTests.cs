using System.IO;
using System.Linq;
using Xunit;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// ISPC chunk 摊平允许「读另一列的 [i+k]」：写列与读列组件类型不同即证明不是同一列，
    /// 摊平后槽偏移是 k * slots，仍然一 lane 一槽、访存连续。
    /// </summary>
    public class IspcStencilFlattenTests
    {
        [Fact]
        public void StencilReadOfOtherColumnFlattensToSlotOffset()
        {
            const string source = @"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;
using EntJoy.Mathematics;

public struct StencilPos { public float2 Value; }
public struct StencilVel { public float2 Value; }

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct StencilJob : IJobChunk
{
    public float DeltaTime;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        NativeArray<StencilPos> positions = chunk.GetComponentDataNativeArray<StencilPos>();
        NativeArray<StencilVel> velocities = chunk.GetComponentDataNativeArray<StencilVel>();

        for (int index = 0; index < positions.Length - 1; index++)
        {
            StencilPos position = positions[index];
            position.Value += velocities[index + 1].Value * DeltaTime;
            positions[index] = position;
        }
    }
}";
            var result = GeneratorHarness.EmitFor(source);
            string ispcPath = System.IO.Directory.GetFiles(result.OutputDir, "*StencilJob_Execute.ispc")
                .FirstOrDefault();
            Assert.True(ispcPath != null, GeneratorHarness.Fail(result, "ISPC 内核没有生成 ⇒ 断言是空跑"));
            string text = System.IO.File.ReadAllText(ispcPath).Replace("\r\n", "\n");
            Assert.Contains("foreach_tiled (__flat =", text);
            Assert.Contains("((float*)velocities_ptr)[__flat + 2]", text);
            Assert.DoesNotContain("programIndex != 0", text);
        }

        [Fact]
        public void NegativeStencilOffsetKeepsSerialLoop()
        {
            const string source = @"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;
using EntJoy.Mathematics;

public struct NegStencilPos { public float2 Value; }
public struct NegStencilVel { public float2 Value; }

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct NegStencilJob : IJobChunk
{
    public float DeltaTime;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        NativeArray<NegStencilPos> positions = chunk.GetComponentDataNativeArray<NegStencilPos>();
        NativeArray<NegStencilVel> velocities = chunk.GetComponentDataNativeArray<NegStencilVel>();

        for (int index = 0; index < positions.Length - 1; index++)
        {
            NegStencilPos position = positions[index];
            position.Value += velocities[index - 1].Value * DeltaTime;
            positions[index] = position;
        }
    }
}";
            var result = GeneratorHarness.EmitFor(source);
            string ispcPath = System.IO.Directory.GetFiles(result.OutputDir, "*NegStencilJob_Execute.ispc")
                .FirstOrDefault();
            Assert.True(ispcPath != null, GeneratorHarness.Fail(result, "ISPC 内核没有生成 ⇒ 断言是空跑"));
            string text = System.IO.File.ReadAllText(ispcPath).Replace("\r\n", "\n");
            // 负偏移等价于读列首之前的内存，不进并行/摊平路径，退回串行 uniform 循环。
            Assert.Contains("programIndex != 0", text);
        }
    }
}
