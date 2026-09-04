using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EntJoy.ECS;

namespace EntJoy.ECS.JobSystem
{
    /// <summary>
    /// 共同收集器：archetype 扫描 + chunk 收集 + 三种 payload 填充。
    /// 托管路径（轻量）/ Native/ISPC 路径（全指针）/ Cpp entity batch 路径 都从这里构建。
    /// 与 ChunkJobScheduler（调度编排）和 ChunkJobCallbacks（回调层）分离。
    /// </summary>
    internal static unsafe class ChunkJobCollector
    {
        private static int CheckedBytes(long count, int elementSize)
        {
            if (count < 0 || count > int.MaxValue / (long)elementSize)
                throw new OverflowException("Chunk job payload exceeds Int32.MaxValue.");
            return (int)(count * elementSize);
        }

        // ─── 共享缓冲 ───
        [ThreadStatic] private static Chunk[] s_chunkBuffer = new Chunk[64];
        [ThreadStatic] private static List<Archetype> s_archetypeBuffer = new();

        // ─── 核心：archetype 扫描 + chunk 收集 ───
        /// <summary>
        /// 扫描所有 archetype，收集匹配 query 的非空 chunk 到 s_chunkBuffer，返回 chunk 数量。
        /// </summary>
        private static int CollectMatchingChunks(EntityManager entityManager, QueryBuilder query, List<Archetype> matchingArchetypes)
        {
            if (s_chunkBuffer.Length < 64) s_chunkBuffer = new Chunk[64];
            int count = 0;
            for (int i = 0; i < entityManager.ArchetypeCount; i++)
            {
                var arch = entityManager.Archetypes[i];
                if (arch != null && arch.IsMatch(query))
                {
                    matchingArchetypes.Add(arch);
                    foreach (var c in arch.ChunkSpan)
                        if (c.EntityCount > 0 && entityManager.MatchesSharedFilter(query, c) && entityManager.MatchesChangedFilter(query, c))
                        {
                            if (count >= s_chunkBuffer.Length)
                                Array.Resize(ref s_chunkBuffer, s_chunkBuffer.Length * 2);
                            s_chunkBuffer[count++] = c;
                        }
                }
            }
            return count;
        }

        // ─── 托管路径：轻量 payload（entityCount + componentCount + enableBitMaps + chunkId） ───
        /// <summary>
        /// 构建托管路径的 ChunkJobData* 表：只填 entityCount、componentCount、enableBitMaps、chunkHandle(=chunkId)。
        /// 组件数组指针/大小/类型索引/required 均 null——组件数据走 Chunk 对象（不跨边界）。
        /// </summary>
        internal static ChunkJobData* BuildManagedPayload(Chunk[] chunks, int count, bool fillBitmaps, out Chunk[] chunkArray)
        {
            chunkArray = chunks;
            var tablePtr = (ChunkJobData*)Marshal.AllocHGlobal(CheckedBytes(count, sizeof(ChunkJobData)));
            Unsafe.InitBlockUnaligned(tablePtr, 0, (uint)CheckedBytes(count, sizeof(ChunkJobData)));
            try
            {
                for (int ci = 0; ci < count; ci++)
                {
                    var chunk = chunks[ci];
                    int compCount = chunk.ComponentCount;
                    void** bitmaps = null;
                    if (fillBitmaps)
                    {
                        bitmaps = (void**)Marshal.AllocHGlobal(CheckedBytes(compCount, sizeof(void*)));
                        for (int c = 0; c < compCount; c++)
                            bitmaps[c] = chunk.GetEnableBitMapPointer(c);
                    }
                    tablePtr[ci] = new ChunkJobData
                    {
                        entityArray = null, entityCount = chunk.EntityCount,
                        componentCount = compCount, componentArrays = null, componentSizes = null,
                        enableBitMaps = bitmaps, componentTypeIndices = null,
                        chunkHandle = (IntPtr)ci,
                        requiredComponentArrays = null, requiredComponentCount = 0
                    };
                }
                return tablePtr;
            }
            catch
            {
                FreeChunkJobDataPayload(tablePtr, count);
                throw;
            }
        }

        // ─── Native/ISPC 路径：完整 payload（所有组件指针） ───
        internal static ChunkJobData* BuildNativePayload(Chunk[] chunks, int count, int[]? requiredIds, GCHandle[]? gcHandles)
        {
            var tablePtr = (ChunkJobData*)Marshal.AllocHGlobal(CheckedBytes(count, sizeof(ChunkJobData)));
            Unsafe.InitBlockUnaligned(tablePtr, 0, (uint)CheckedBytes(count, sizeof(ChunkJobData)));
            try
            {
                for (int ci = 0; ci < count; ci++)
                {
                    var chunk = chunks[ci];
                var arch = chunk.Archetype;
                int compCount = chunk.ComponentCount;
                var compPtrs = (void**)Marshal.AllocHGlobal(CheckedBytes(compCount, sizeof(void*)));
                var compSizes = (int*)Marshal.AllocHGlobal(CheckedBytes(compCount, sizeof(int)));
                var bitmaps = (void**)Marshal.AllocHGlobal(CheckedBytes(compCount, sizeof(void*)));
                var typeIndices = (int*)Marshal.AllocHGlobal(CheckedBytes(compCount, sizeof(int)));
                int reqCount = requiredIds?.Length ?? 0;
                void** reqArrays = reqCount > 0 ? (void**)Marshal.AllocHGlobal(CheckedBytes(reqCount, sizeof(void*))) : null;
                if (reqArrays != null) for (int r = 0; r < reqCount; r++) reqArrays[r] = null;
                for (int c = 0; c < compCount; c++)
                {
                    compPtrs[c] = (void*)chunk.GetComponentArrayPointer(c);
                    compSizes[c] = arch.Types[c].Size;
                    bitmaps[c] = chunk.GetEnableBitMapPointer(c);
                    typeIndices[c] = arch.Types[c].Id;
                }
                if (reqArrays != null)
                {
                    for (int r = 0; r < reqCount; r++)
                    {
                        int reqId = requiredIds![r];
                        for (int c = 0; c < compCount; c++)
                            if (typeIndices[c] == reqId) { reqArrays[r] = compPtrs[c]; break; }
                    }
                }
                // SharedComponent blittable 值指针（per-chunk，非 per-entity）
                int sharedCount = 0;
                void** sharedPtrs = null;
                if (chunk.HasSharedValues)
                {
                    for (int c = 0; c < compCount; c++)
                        if (arch.Types[c].IsShared && !arch.Types[c].IsManagedShared)
                            sharedCount++;
                    if (sharedCount > 0)
                    {
                        sharedPtrs = (void**)Marshal.AllocHGlobal(CheckedBytes(sharedCount, sizeof(void*)));
                        int si = 0;
                        for (int c = 0; c < compCount; c++)
                        {
                            if (!arch.Types[c].IsShared || arch.Types[c].IsManagedShared) continue;
                            sharedPtrs[si++] = (void*)chunk.GetSharedValuePointer(c);
                        }
                    }
                }
                    tablePtr[ci] = new ChunkJobData
                    {
                    entityArray = (void*)chunk.GetEntityPointer(),
                    entityCount = chunk.EntityCount, componentCount = compCount,
                    componentArrays = compPtrs, componentSizes = compSizes,
                    enableBitMaps = bitmaps, componentTypeIndices = typeIndices,
                    chunkHandle = gcHandles != null ? (IntPtr)gcHandles[ci] : IntPtr.Zero,
                    requiredComponentArrays = reqArrays, requiredComponentCount = reqCount,
                    sharedValuePtrs = sharedPtrs, sharedValueCount = sharedCount
                    };
                }
                return tablePtr;
            }
            catch
            {
                FreeChunkJobDataPayload(tablePtr, count);
                throw;
            }
        }

        private static void FreeChunkJobDataPayload(ChunkJobData* tablePtr, int count)
        {
            if (tablePtr == null) return;
            for (int i = 0; i < count; i++)
            {
                if (tablePtr[i].componentArrays != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].componentArrays);
                if (tablePtr[i].componentSizes != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].componentSizes);
                if (tablePtr[i].enableBitMaps != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].enableBitMaps);
                if (tablePtr[i].componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].componentTypeIndices);
                if (tablePtr[i].requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].requiredComponentArrays);
                if (tablePtr[i].sharedValuePtrs != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].sharedValuePtrs);
            }
            Marshal.FreeHGlobal((IntPtr)tablePtr);
        }

        // ─── Cpp entity batch 路径：EntityBatchData* ───
        internal static void BuildEntityBatchPayload(Chunk[] chunks, int count, int[]? requiredIds,
            out EntityBatchData* batchesPtr, out int batchCount, out void* compArraysBlock, out void* enableBitMapsBlock)
        {
            enableBitMapsBlock = null;
            if (count == 0) { batchesPtr = null; batchCount = 0; compArraysBlock = null; return; }
            batchCount = count;
            int reqCount = requiredIds?.Length ?? 0;
            batchesPtr = (EntityBatchData*)Marshal.AllocHGlobal(CheckedBytes(count, sizeof(EntityBatchData)));
            compArraysBlock = null;
            try
            {
                Unsafe.InitBlockUnaligned(batchesPtr, 0, (uint)CheckedBytes(count, sizeof(EntityBatchData)));
                compArraysBlock = reqCount > 0 ? (void*)Marshal.AllocHGlobal(CheckedBytes((long)count * reqCount, sizeof(void*))) : null;
                for (int ci = 0; ci < count; ci++)
                {
                    var chunk = chunks[ci];
                    var arch = chunk.Archetype;
                    batchesPtr[ci].entityCount = chunk.EntityCount;
                    if (compArraysBlock != null)
                    {
                        void** arraysBase = (void**)compArraysBlock + ci * reqCount;
                        for (int r = 0; r < reqCount; r++)
                        {
                            int reqId = requiredIds![r];
                            for (int c = 0; c < chunk.ComponentCount; c++)
                                if (arch.Types[c].Id == reqId)
                                {
                                    arraysBase[r] = (void*)chunk.GetComponentArrayPointer(c);
                                    break;
                                }
                        }
                        batchesPtr[ci].componentArrays = arraysBase;
                    }
                }
            }
            catch
            {
                if (compArraysBlock != null) Marshal.FreeHGlobal((IntPtr)compArraysBlock);
                Marshal.FreeHGlobal((IntPtr)batchesPtr);
                batchesPtr = null;
                batchCount = 0;
                throw;
            }
        }

        // ─── 对外接口：收集 + 构建托管 payload ───
        internal static void CollectAndBuildManaged(EntityManager em, QueryBuilder query, bool fillBitmaps, bool hasEnabledFilter,
            out ChunkJobData* ptr, out Chunk[] chunkArray, out int chunkCount, out Archetype[] archetypes)
        {
            s_archetypeBuffer.Clear();
            chunkCount = CollectMatchingChunks(em, query, s_archetypeBuffer);
            if (chunkCount == 0) { ptr = null; chunkArray = Array.Empty<Chunk>(); archetypes = Array.Empty<Archetype>(); return; }
            chunkArray = s_chunkBuffer;
            ptr = BuildManagedPayload(chunkArray, chunkCount, fillBitmaps: hasEnabledFilter && fillBitmaps, out _);
            archetypes = s_archetypeBuffer.ToArray();
        }

        // ─── 对外接口：收集 + 构建 native payload ───
        internal static void CollectAndBuildNative(EntityManager em, QueryBuilder query, int[]? requiredIds,
            out ChunkJobData* ptr, out int chunkCount, out Archetype[] archetypes)
        {
            s_archetypeBuffer.Clear();
            chunkCount = CollectMatchingChunks(em, query, s_archetypeBuffer);
            if (chunkCount == 0) { ptr = null; archetypes = Array.Empty<Archetype>(); return; }
            ptr = BuildNativePayload(s_chunkBuffer, chunkCount, requiredIds, gcHandles: null);
            archetypes = s_archetypeBuffer.ToArray();
        }

        // ─── 对外接口：收集 + 构建 entity batch payload ───
        internal static void CollectAndBuildEntityBatch(EntityManager em, QueryBuilder query, int[]? requiredIds,
            out EntityBatchData* ptr, out int batchCount, out Archetype[] archetypes)
        {
            s_archetypeBuffer.Clear();
            int chunkCount = CollectMatchingChunks(em, query, s_archetypeBuffer);
            if (chunkCount == 0) { ptr = null; batchCount = 0; archetypes = Array.Empty<Archetype>(); return; }
            BuildEntityBatchPayload(s_chunkBuffer, chunkCount, requiredIds, out ptr, out batchCount, out _, out _);
            archetypes = s_archetypeBuffer.ToArray();
        }

    }
}
