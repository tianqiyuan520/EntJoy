using System.Runtime.CompilerServices;

namespace EntJoy
{
    public interface IJobChunk
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask);
    }
}
