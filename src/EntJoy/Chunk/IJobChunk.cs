using System.Runtime.CompilerServices;

namespace EntJoy.JobSystem
{
    public interface IJobChunk
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask);
    }
}
