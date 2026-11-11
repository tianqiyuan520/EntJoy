using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EntJoy.ECS;

namespace EntJoy.ECS.JobSystem
{
    /// <summary>共同收集器：archetype 扫描 + chunk 收集 + 三种 payload 填充；
    /// 托管（轻量）/ Native/ISPC（全指针）/ Cpp entity batch 四条路径都从这里构建。</summary>
    public static unsafe partial class ChunkJobCollector
    {
        internal static void ValidateJobQuery(QueryBuilder query)
        {
            if (query.HasRelationshipFilter && (query.RelationshipFilterType.Size < sizeof(RelationSlot)
                || !query.RelationshipFilterType.IsRelation))
                throw new ArgumentException("A relationship query requires a registered relationship type.", nameof(query));
        }

        private static int CheckedBytes(long count, int elementSize)
        {
            if (count < 0 || count > int.MaxValue / (long)elementSize)
                throw new OverflowException("Chunk job payload exceeds Int32.MaxValue.");
            return (int)(count * elementSize);
        }

        // 共享缓冲
        [ThreadStatic] private static Chunk[] s_chunkBuffer;
        [ThreadStatic] private static List<Archetype> s_archetypeBuffer;

        // 核心：archetype 扫描 + chunk 收集
        /// <summary>
        /// 扫描所有 archetype，收集匹配 query 的非空 chunk 到 s_chunkBuffer，返回 chunk 数量。
        /// </summary>
        private static int CollectMatchingChunks(EntityManager entityManager, QueryBuilder query, List<Archetype> matchingArchetypes)
        {
            ValidateJobQuery(query);
            s_chunkBuffer ??= new Chunk[64];
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

        // 托管路径：轻量 payload（entityCount + componentCount + enableBitMaps + chunkId）
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

        // Native/ISPC 路径：完整 payload（所有组件指针）
        internal static ChunkJobData* BuildNativePayload(Chunk[] chunks, int count, int[]? requiredIds, GCHandle[]? gcHandles, QueryBuilder query = default)
        {
            int bytes = CheckedBytes(count, sizeof(ChunkJobData));
            var tablePtr = (ChunkJobData*)Marshal.AllocHGlobal(bytes);
            Unsafe.InitBlockUnaligned(tablePtr, 0, (uint)bytes);
            try
            {
                for (int ci = 0; ci < count; ci++)
                {
                    IntPtr handle = gcHandles != null ? (IntPtr)gcHandles[ci] : IntPtr.Zero;
                    tablePtr[ci] = BuildChunkPayload(chunks[ci], handle, requiredIds, query);
                }
                return tablePtr;
            }
            catch
            {
                FreeChunkJobDataPayload(tablePtr, count);
                throw;
            }
        }

        // 单块 payload：组件列指针/大小/位图/类型 id、required 列、shared 值指针，以及块过滤条件。
        private static ChunkJobData BuildChunkPayload(Chunk chunk, IntPtr chunkHandle, int[]? requiredIds, QueryBuilder query)
        {
            var arch = chunk.Archetype;
            int compCount = chunk.ComponentCount;
            var compPtrs = (void**)Marshal.AllocHGlobal(CheckedBytes(compCount, sizeof(void*)));
            var compSizes = (int*)Marshal.AllocHGlobal(CheckedBytes(compCount, sizeof(int)));
            var bitmaps = (void**)Marshal.AllocHGlobal(CheckedBytes(compCount, sizeof(void*)));
            var typeIndices = (int*)Marshal.AllocHGlobal(CheckedBytes(compCount, sizeof(int)));
            for (int c = 0; c < compCount; c++)
            {
                compPtrs[c] = (void*)chunk.GetComponentArrayPointer(c);
                compSizes[c] = arch.Types[c].Size;
                bitmaps[c] = chunk.GetEnableBitMapPointer(c);
                typeIndices[c] = arch.Types[c].Id;
            }

            int reqCount = requiredIds?.Length ?? 0;
            ResolveRequiredColumns(requiredIds, typeIndices, compPtrs, bitmaps, compCount,
                out void** reqArrays, out void** reqBitmaps);
            ResolveSharedValuePointers(chunk, arch, compCount, out void** sharedPtrs, out int sharedCount);

            var data = new ChunkJobData
            {
                entityArray = (void*)chunk.GetEntityPointer(),
                entityCount = chunk.EntityCount, componentCount = compCount,
                componentArrays = compPtrs, componentSizes = compSizes,
                enableBitMaps = bitmaps, componentTypeIndices = typeIndices,
                chunkHandle = chunkHandle,
                requiredComponentArrays = reqArrays, requiredComponentCount = reqCount,
                requiredEnableBitMaps = reqBitmaps,
                sharedValuePtrs = sharedPtrs, sharedValueCount = sharedCount
            };
            data.filter = ChunkJobScheduler.BuildNativeFilter(chunk, query);
            return data;
        }

        // 把 required 类型 id 映射成对应的列指针与位图指针；类型不在该块里时留 null。
        private static void ResolveRequiredColumns(int[]? requiredIds, int* typeIndices, void** compPtrs, void** bitmaps,
            int compCount, out void** reqArrays, out void** reqBitmaps)
        {
            int reqCount = requiredIds?.Length ?? 0;
            if (reqCount == 0)
            {
                reqArrays = null;
                reqBitmaps = null;
                return;
            }
            reqArrays = (void**)Marshal.AllocHGlobal(CheckedBytes(reqCount, sizeof(void*)));
            reqBitmaps = (void**)Marshal.AllocHGlobal(CheckedBytes(reqCount, sizeof(void*)));
            for (int r = 0; r < reqCount; r++)
            {
                reqArrays[r] = null;
                reqBitmaps[r] = null;
                int reqId = requiredIds![r];
                for (int c = 0; c < compCount; c++)
                    if (typeIndices[c] == reqId) { reqArrays[r] = compPtrs[c]; reqBitmaps[r] = bitmaps[c]; break; }
            }
        }

        // SharedComponent blittable 值指针（per-chunk，非 per-entity）；managed shared 只存索引，不在这里。
        private static void ResolveSharedValuePointers(Chunk chunk, Archetype arch, int compCount,
            out void** sharedPtrs, out int sharedCount)
        {
            sharedPtrs = null;
            sharedCount = 0;
            if (!chunk.HasSharedValues) return;
            for (int c = 0; c < compCount; c++)
                if (arch.Types[c].IsShared && !arch.Types[c].IsManagedShared) sharedCount++;
            if (sharedCount == 0) return;
            sharedPtrs = (void**)Marshal.AllocHGlobal(CheckedBytes(sharedCount, sizeof(void*)));
            int si = 0;
            for (int c = 0; c < compCount; c++)
            {
                if (!arch.Types[c].IsShared || arch.Types[c].IsManagedShared) continue;
                sharedPtrs[si++] = (void*)chunk.GetSharedValuePointer(c);
            }
        }

        internal static void FreeChunkJobDataPayload(ChunkJobData* tablePtr, int count)
        {
            if (tablePtr == null) return;
            for (int i = 0; i < count; i++)
            {
                ChunkJobScheduler.FreeNativeFilter(tablePtr[i].filter);
                if (tablePtr[i].componentArrays != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].componentArrays);
                if (tablePtr[i].componentSizes != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].componentSizes);
                if (tablePtr[i].enableBitMaps != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].enableBitMaps);
                if (tablePtr[i].componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].componentTypeIndices);
                if (tablePtr[i].requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].requiredComponentArrays);
                if (tablePtr[i].requiredEnableBitMaps != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].requiredEnableBitMaps);
                if (tablePtr[i].sharedValuePtrs != null) Marshal.FreeHGlobal((IntPtr)tablePtr[i].sharedValuePtrs);
            }
            Marshal.FreeHGlobal((IntPtr)tablePtr);
        }

        // Cpp entity batch 路径：EntityBatchData*
        internal static void BuildEntityBatchPayload(Chunk[] chunks, int count, int[]? requiredIds,
            out EntityBatchData* batchesPtr, out int batchCount, out void* compArraysBlock, out void* enableBitMapsBlock, QueryBuilder query = default)
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
                    batchesPtr[ci].filter = ChunkJobScheduler.BuildNativeFilter(chunk, query);
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
                for (int i = 0; i < count; i++) ChunkJobScheduler.FreeNativeFilter(batchesPtr[i].filter);
                if (compArraysBlock != null) Marshal.FreeHGlobal((IntPtr)compArraysBlock);
                Marshal.FreeHGlobal((IntPtr)batchesPtr);
                batchesPtr = null;
                batchCount = 0;
                throw;
            }
        }

        /// <summary>收集匹配 chunk 并（可选）构建托管 payload。
        /// `chunkArray` 必须是本次调度的独立快照：`s_chunkBuffer` 是 [ThreadStatic] 复用缓冲，
        /// 下一次收集会原地覆盖它的前 count 项，job 就会遍历到别的查询的 chunk。</summary>
        internal static void CollectAndBuildManaged(EntityManager em, QueryBuilder query, bool fillBitmaps, bool hasEnabledFilter,
            out ChunkJobData* ptr, out Chunk[] chunkArray, out int chunkCount, out Archetype[] archetypes,
            bool buildPayload = true)
        {
            (s_archetypeBuffer ??= new()).Clear();
            int count = CollectMatchingChunks(em, query, s_archetypeBuffer);
            chunkCount = count;
            if (count == 0) { ptr = null; chunkArray = Array.Empty<Chunk>(); archetypes = Array.Empty<Archetype>(); return; }

            chunkArray = new Chunk[count];
            Array.Copy(s_chunkBuffer, chunkArray, count);

            // 只需要 Chunk[] 的调用方（托管回退直接遍历 Chunk 对象）不应构建 payload：
            // 那份 ChunkJobData 表 + 每 chunk 的位图块是 HGlobal，构建了又没人释放就是每调度泄漏。
            ptr = buildPayload
                ? BuildManagedPayload(chunkArray, count, fillBitmaps: hasEnabledFilter && fillBitmaps, out _)
                : null;
            archetypes = s_archetypeBuffer.ToArray();
        }

        // 对外接口：收集 + 构建 native payload
        internal static void CollectAndBuildNative(EntityManager em, QueryBuilder query, int[]? requiredIds,
            out ChunkJobData* ptr, out int chunkCount, out Archetype[] archetypes)
        {
            (s_archetypeBuffer ??= new()).Clear();
            chunkCount = CollectMatchingChunks(em, query, s_archetypeBuffer);
            if (chunkCount == 0) { ptr = null; archetypes = Array.Empty<Archetype>(); return; }
            ptr = BuildNativePayload(s_chunkBuffer, chunkCount, requiredIds, gcHandles: null, query: query);
            archetypes = s_archetypeBuffer.ToArray();
        }

        // 对外接口：收集 + 构建 entity batch payload
        internal static void CollectAndBuildEntityBatch(EntityManager em, QueryBuilder query, int[]? requiredIds,
            out EntityBatchData* ptr, out int batchCount, out Archetype[] archetypes)
        {
            (s_archetypeBuffer ??= new()).Clear();
            int chunkCount = CollectMatchingChunks(em, query, s_archetypeBuffer);
            if (chunkCount == 0) { ptr = null; batchCount = 0; archetypes = Array.Empty<Archetype>(); return; }
            BuildEntityBatchPayload(s_chunkBuffer, chunkCount, requiredIds, out ptr, out batchCount, out _, out _, query);
            archetypes = s_archetypeBuffer.ToArray();
        }

    }
}
