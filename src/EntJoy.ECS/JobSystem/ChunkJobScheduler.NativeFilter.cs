using System;
using System.Runtime.InteropServices;

namespace EntJoy.ECS.JobSystem
{
    public static unsafe partial class ChunkJobScheduler
    {
        internal static EntityFilterData BuildNativeFilter(Chunk chunk, QueryBuilder query)
        {
            EntityFilterData filter = default;
            try
            {
                if (query.HasRelationshipFilter)
                {
                    int words = (chunk.EntityCount + 63) / 64;
                    Span<ulong> scratch = words <= 64 ? stackalloc ulong[words] : new ulong[words];
                    var mask = ComputeChunkMask(chunk, null, query, scratch);
                    filter.relationshipBits = (ulong*)Marshal.AllocHGlobal(checked(words * sizeof(ulong)));
                    mask.Bits.CopyTo(new Span<ulong>(filter.relationshipBits, words));
                    chunk.Archetype.Owner.CheckRelationshipPreparation("job-filter-bits");
                }
                filter.enabledCount = query.AllEnabled?.Length ?? 0;
                if (filter.enabledCount > 0)
                {
                    filter.enabledBitMaps = (void**)Marshal.AllocHGlobal(checked(filter.enabledCount * sizeof(void*)));
                    for (int i = 0; i < filter.enabledCount; i++)
                    {
                        int column = chunk.Archetype.GetComponentTypeIndex(query.AllEnabled[i]);
                        if (column < 0) throw new InvalidOperationException("Enabled query component is missing from its candidate Chunk.");
                        filter.enabledBitMaps[i] = chunk.GetEnableBitMapPointer(column);
                    }
                    chunk.Archetype.Owner.CheckRelationshipPreparation("job-filter-enabled");
                }
                return filter;
            }
            catch { FreeNativeFilter(filter); throw; }
        }

        internal static void FreeNativeFilter(EntityFilterData filter)
        {
            if (filter.relationshipBits != null) Marshal.FreeHGlobal((IntPtr)filter.relationshipBits);
            if (filter.enabledBitMaps != null) Marshal.FreeHGlobal((IntPtr)filter.enabledBitMaps);
        }
    }
}
