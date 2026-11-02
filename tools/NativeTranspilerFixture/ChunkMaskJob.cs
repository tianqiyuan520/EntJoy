using EntJoy.ECS;
using NativeTranspiler;

namespace NativeTranspilerFixture;

public struct MaskValue : IComponentData
{
    public int Number;
}

[NativeTranspile]
public struct ChunkMaskJob : IJobChunk
{
    public uint Flags;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var values = chunk.GetComponentDataNativeArray<MaskValue>();
        for (int i = 0; i < values.Length; i++)
        {
            int n = 0;
            if ((Flags & 1u) == 0) n += i;
            if ((Flags & 2u) == 0) n += 10;
            if ((Flags & 4u) == 0) n += 100;
            MaskValue value = values[i];
            value.Number = n;
            values[i] = value;
        }
    }
}
