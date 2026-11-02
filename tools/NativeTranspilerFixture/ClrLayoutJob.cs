using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    public struct ClrFlagData : IComponentData
    {
        public bool Enabled;
        public byte Tag;
        public char Code;
    }

    [NativeTranspile]
    public struct ClrLayoutJob : IJobChunk
    {
        public NativeArray<int> Output;
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var values = chunk.GetComponentDataNativeArray<ClrFlagData>();
            for (int i = 0; i < values.Length; i++)
            {
                var value = values[i];
                Output[value.Tag] = value.Enabled ? (int)value.Code + value.Tag : -(int)value.Code;
            }
        }
    }
}
