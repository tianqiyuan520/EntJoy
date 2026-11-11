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

        /// <summary>释放单个块的裸数据：过滤器与各列指针数组都由调度方分配。</summary>
        internal static unsafe void FreeChunkJobData(in ChunkJobData data)
        {
            FreeNativeFilter(data.filter);
            if (data.componentArrays != null) Marshal.FreeHGlobal((IntPtr)data.componentArrays);
            if (data.componentSizes != null) Marshal.FreeHGlobal((IntPtr)data.componentSizes);
            if (data.enableBitMaps != null) Marshal.FreeHGlobal((IntPtr)data.enableBitMaps);
            if (data.componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)data.componentTypeIndices);
            if (data.requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)data.requiredComponentArrays);
            if (data.requiredEnableBitMaps != null) Marshal.FreeHGlobal((IntPtr)data.requiredEnableBitMaps);
            if (data.sharedValuePtrs != null) Marshal.FreeHGlobal((IntPtr)data.sharedValuePtrs);
        }
    }
}
