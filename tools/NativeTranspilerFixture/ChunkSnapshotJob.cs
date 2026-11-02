using EntJoy.Collections;
using EntJoy.ECS;
using NativeTranspiler;

namespace NativeTranspilerFixture;

public struct SnapshotValue : IComponentData
{
    public int Slot;
    public int Number;
}

[NativeTranspile]
public struct ChunkSnapshotJob : IJobChunk
{
    public NativeColumn<int> Source;
    public NativeArray<int> Destination;
    public int Bias;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var values = chunk.GetComponentDataNativeArray<SnapshotValue>();
        for (int i = 0; i < values.Length; i++)
        {
            SnapshotValue value = values[i];
            Destination[value.Slot] = Source[value.Slot] + Bias;
            value.Number = Destination[value.Slot];
            values[i] = value;
        }
    }
}
