using EntJoy.JobSystem;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.ECS.JobSystem
{
    public static unsafe partial class ChunkJobScheduler
    {
        // 命中且版本未变时领取租约返回 true；过期则移除旧条目，让调用方重新构建。
        private static bool TryReuseCached<TCache>(Dictionary<RawChunkScheduleCacheKey, TCache> caches,
            RawChunkScheduleCacheKey key, EntityManager entityManager, QueryBuilder query, bool synchronousLease,
            out TCache cache, out IDisposable lease) where TCache : ScheduleCacheLifetime
        {
            lease = null;
            if (!caches.TryGetValue(key, out cache))
            {
                cache = null;
                return false;
            }
            if (cache.StructuralVersion == entityManager.StructuralVersion && !(query.ChangedComponents?.Length > 0))
            {
                if (cache.PayloadCount > 0) lease = cache.RetainLease(synchronousLease);
                return true;
            }
            cache.Dispose();
            caches.Remove(key);
            cache = null;
            return false;
        }

        // 调度缓存
        private static bool TryGetRawChunkScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds, out RawChunkScheduleCache cache, out IDisposable lease, bool synchronousLease = false)
        {
            lease = null;
            // 带关系过滤的查询走直建：匹配集合随关系变化，缓存键无法稳定表达。
            if (query.HasRelationshipFilter)
            {
                cache = BuildRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds);
                if (cache.PayloadCount > 0) lease = cache.RetainLease(synchronousLease);
                cache.Dispose();
                return true;
            }
            var key = new RawChunkScheduleCacheKey(entityManager, query, requiredComponentTypeIds, 0);
            lock (_rawChunkScheduleCacheLock)
            {
                if (TryReuseCached(_rawChunkScheduleCaches, key, entityManager, query, synchronousLease, out cache, out lease))
                    return true;
                var storedKey = key.Capture();
                cache = BuildRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds);
                _rawChunkScheduleCaches[storedKey] = cache;
                if (cache.PayloadCount > 0) lease = cache.RetainLease(synchronousLease);
                return true;
            }
        }
        private static bool TryGetManagedChunkScheduleCache(EntityManager entityManager, QueryBuilder query, out RawChunkScheduleCache cache, out IDisposable lease)
        {
            var key = new RawChunkScheduleCacheKey(entityManager, query, null, 1);
            lock (_rawChunkScheduleCacheLock)
            {
                if (TryReuseCached(_rawChunkScheduleCaches, key, entityManager, query, synchronousLease: false, out cache, out lease))
                    return true;
                var storedKey = key.Capture();
                cache = BuildManagedChunkScheduleCache(entityManager, query);
                _rawChunkScheduleCaches[storedKey] = cache;
                if (cache.PayloadCount > 0) lease = cache.RetainLease();
                return true;
            }
        }
        private static bool TryGetEntityBatchScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds, out EntityBatchScheduleCache cache, out IDisposable lease, bool synchronousLease = false)
        {
            lease = null;
            // 关系过滤或启用位过滤的查询走直建：匹配集合随内容变化。
            if (query.HasRelationshipFilter || query.AllEnabled?.Length > 0)
            {
                cache = BuildEntityBatchScheduleCache(entityManager, query, requiredComponentTypeIds);
                if (cache.PayloadCount > 0) lease = cache.RetainLease(synchronousLease);
                cache.Dispose();
                return true;
            }
            var key = new RawChunkScheduleCacheKey(entityManager, query, requiredComponentTypeIds, 3);
            lock (_rawChunkScheduleCacheLock)
            {
                if (TryReuseCached(_entityBatchScheduleCaches, key, entityManager, query, synchronousLease, out cache, out lease))
                    return true;
                var storedKey = key.Capture();
                cache = BuildEntityBatchScheduleCache(entityManager, query, requiredComponentTypeIds);
                _entityBatchScheduleCaches[storedKey] = cache;
                if (cache.PayloadCount > 0) lease = cache.RetainLease(synchronousLease);
                return true;
            }
        }

        // 收集匹配的块；原型列表仅 Chunk 缓存路径需要，这里丢弃。
        private static List<Chunk> CollectQueryChunks(EntityManager entityManager, QueryBuilder query)
        {
            var chunkList = new List<Chunk>(128);
            CollectMatchingChunks(entityManager, query, chunkList, new List<Archetype>(8));
            return chunkList;
        }

        // 块内按组件 id 找列下标；该块没有这一列时返回 -1。
        private static int FindColumn(Archetype archetype, int componentTypeId)
        {
            for (int c = 0; c < archetype.ComponentCount; c++)
            {
                if (archetype.Types[c].Id == componentTypeId) return c;
            }
            return -1;
        }

        private static EntityBatchScheduleCache BuildEntityBatchScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds)
        {
            var chunkList = CollectQueryChunks(entityManager, query);
            int batchCount = chunkList.Count;
            if (batchCount == 0)
            {
                return new EntityBatchScheduleCache(entityManager.StructuralVersion, null, 0, null, null);
            }

            int requiredCount = requiredComponentTypeIds?.Length ?? 0;
            bool hasEnableFilter = query.AllEnabled != null && query.AllEnabled.Length > 0;
            int enableBitmapCount = hasEnableFilter ? requiredCount : 0;
            // 位图块按 requiredCount 分配：原生内核显式读位图时不能拿到 nullptr；
            // enableBitmapCount 仍只在过滤查询时非 0，保持"按它决定是否过滤"的语义。
            int enableBitmapStride = requiredCount;

            int batchBytes = checked(batchCount * sizeof(EntityBatchData));
            var batchesPtr = (EntityBatchData*)Marshal.AllocHGlobal(batchBytes);
            Unsafe.InitBlockUnaligned(batchesPtr, 0, (uint)batchBytes);
            void* componentArraysBlock = null;
            void* enableBitMapsBlock = null;
            try
            {
                componentArraysBlock = AllocateFlatBlock(batchCount, requiredCount);
                enableBitMapsBlock = AllocateFlatBlock(batchCount, enableBitmapStride);
                FillEntityBatches(batchesPtr, chunkList, query, requiredComponentTypeIds,
                    requiredCount, componentArraysBlock, enableBitMapsBlock, enableBitmapCount, enableBitmapStride);
                return new EntityBatchScheduleCache(entityManager.StructuralVersion, batchesPtr, batchCount, componentArraysBlock, enableBitMapsBlock);
            }
            catch
            {
                new EntityBatchScheduleCache(entityManager.StructuralVersion, batchesPtr, batchCount, componentArraysBlock, enableBitMapsBlock).Dispose();
                throw;
            }
        }

        // 每批 stride 个指针的扁平数组；stride 为 0 时不需要分配。
        private static void* AllocateFlatBlock(int batchCount, int stride)
            => stride > 0 ? (void*)Marshal.AllocHGlobal(checked(batchCount * stride * sizeof(void*))) : null;

        private static unsafe void FillEntityBatches(EntityBatchData* batchesPtr, List<Chunk> chunkList, QueryBuilder query,
            int[] requiredComponentTypeIds, int requiredCount, void* componentArraysBlock, void* enableBitMapsBlock,
            int enableBitmapCount, int enableBitmapStride)
        {
            for (int batchIndex = 0; batchIndex < chunkList.Count; batchIndex++)
            {
                ref var batch = ref batchesPtr[batchIndex];
                var chunk = chunkList[batchIndex];
                var archetype = chunk.Archetype;
                batch.componentArrays = componentArraysBlock == null ? null
                    : FillColumnPointers((void**)((byte*)componentArraysBlock + batchIndex * requiredCount * sizeof(void*)),
                        archetype, chunk, requiredComponentTypeIds, requiredCount);
                batch.enableBitMaps = enableBitMapsBlock == null ? null
                    : FillEnableBitMaps((void**)((byte*)enableBitMapsBlock + batchIndex * enableBitmapStride * sizeof(void*)),
                        archetype, chunk, requiredComponentTypeIds, enableBitmapStride);
                batch.enableBitmapCount = enableBitMapsBlock == null ? 0 : enableBitmapCount;
                batch.entityCount = chunk.EntityCount;
                batch.filter = BuildNativeFilter(chunk, query);
            }
        }

        // 按 required 类型 id 填列指针；该块没有这一列时留 null。
        private static void** FillColumnPointers(void** basePtr, Archetype archetype, Chunk chunk, int[] requiredComponentTypeIds, int requiredCount)
        {
            for (int r = 0; r < requiredCount; r++)
            {
                int column = FindColumn(archetype, requiredComponentTypeIds[r]);
                basePtr[r] = column < 0 ? null : (void*)chunk.GetComponentArrayPointer(column);
            }
            return basePtr;
        }

        private static unsafe void** FillEnableBitMaps(void** basePtr, Archetype archetype, Chunk chunk, int[] requiredComponentTypeIds, int enableBitmapStride)
        {
            for (int e = 0; e < enableBitmapStride; e++)
            {
                int column = FindColumn(archetype, requiredComponentTypeIds[e]);
                basePtr[e] = column < 0 ? null : chunk.GetEnableBitMapPointer(column);
            }
            return basePtr;
        }

        private static RawChunkScheduleCache BuildRawChunkScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds)
        {
            var chunkList = new List<Chunk>(128);
            var matchingArchetypes = new List<Archetype>(8);
            CollectMatchingChunks(entityManager, query, chunkList, matchingArchetypes);

            int chunkCount = chunkList.Count;
            if (chunkCount == 0)
            {
                return new RawChunkScheduleCache(entityManager.StructuralVersion, null, 0);
            }

            var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(checked(chunkCount * sizeof(ChunkJobData)));
            Unsafe.InitBlockUnaligned(chunksPtr, 0, (uint)checked(chunkCount * sizeof(ChunkJobData)));
            var cache = new RawChunkScheduleCache(entityManager.StructuralVersion, chunksPtr, chunkCount, matchingArchetypes: matchingArchetypes.ToArray());
            try
            {
                int requiredCount = requiredComponentTypeIds?.Length ?? 0;

                for (int ci = 0; ci < chunkCount; ci++)
                {
                    chunksPtr[ci] = BuildChunkJobData(chunkList[ci], requiredComponentTypeIds, requiredCount);
                    chunksPtr[ci].filter = BuildNativeFilter(chunkList[ci], query);
                }

                return cache;
            }
            catch { cache.Dispose(); throw; }
        }

        // 一个块的裸数据：逐组件列指针、列类型 id、启用位图，以及按 requiredComponentTypeIds 排好的子集。
        private static ChunkJobData BuildChunkJobData(Chunk chunk, int[] requiredComponentTypeIds, int requiredCount)
        {
            var archetype = chunk.Archetype;
            int componentCount = chunk.ComponentCount;
            var componentArrays = (void**)Marshal.AllocHGlobal(componentCount * sizeof(void*));
            var componentTypeIndices = (int*)Marshal.AllocHGlobal(componentCount * sizeof(int));
            // 原生内核经生成的 C++ 包装把 enableBitMaps 拷进 ChunkData，非可启用列为 null。
            var enableBitmaps = (void**)Marshal.AllocHGlobal(componentCount * sizeof(void*));
            for (int componentIndex = 0; componentIndex < componentCount; componentIndex++)
            {
                componentArrays[componentIndex] = (void*)chunk.GetComponentArrayPointer(componentIndex);
                componentTypeIndices[componentIndex] = archetype.Types[componentIndex].Id;
                enableBitmaps[componentIndex] = chunk.GetEnableBitMapPointer(componentIndex);
            }

            ResolveRequiredColumns(archetype, requiredComponentTypeIds, requiredCount, componentArrays, enableBitmaps,
                out void** requiredArrays, out void** requiredBitmaps);

            return new ChunkJobData
            {
                entityArray = (void*)chunk.GetEntityPointer(),
                entityCount = chunk.EntityCount,
                componentCount = componentCount,
                componentArrays = componentArrays,
                componentSizes = null,
                enableBitMaps = enableBitmaps,
                componentTypeIndices = componentTypeIndices,
                chunkHandle = IntPtr.Zero,
                requiredComponentArrays = requiredArrays,
                requiredComponentCount = requiredCount,
                sharedValuePtrs = FillSharedValuePtrs(chunk, archetype, componentCount),
                sharedValueCount = chunk.HasSharedValues ? CountBlittableShared(archetype, componentCount) : 0,
                requiredEnableBitMaps = requiredBitmaps
            };
        }

        // required 类型 id 对应的列指针与位图指针子集；该块没有这一列时留 null。
        private static void ResolveRequiredColumns(Archetype archetype, int[] requiredComponentTypeIds, int requiredCount,
            void** componentArrays, void** enableBitmaps, out void** requiredArrays, out void** requiredBitmaps)
        {
            requiredArrays = null;
            requiredBitmaps = null;
            if (requiredCount <= 0) return;
            requiredArrays = (void**)Marshal.AllocHGlobal(requiredCount * sizeof(void*));
            requiredBitmaps = (void**)Marshal.AllocHGlobal(requiredCount * sizeof(void*));
            for (int r = 0; r < requiredCount; r++)
            {
                int column = FindColumn(archetype, requiredComponentTypeIds[r]);
                requiredArrays[r] = column < 0 ? null : componentArrays[column];
                requiredBitmaps[r] = column < 0 ? null : enableBitmaps[column];
            }
        }

        private static RawChunkScheduleCache BuildManagedChunkScheduleCache(EntityManager entityManager, QueryBuilder query)
        {
            var chunkList = new List<Chunk>(128);
            var matchingArchetypes = new List<Archetype>(8);
            CollectMatchingChunks(entityManager, query, chunkList, matchingArchetypes);

            int chunkCount = chunkList.Count;
            if (chunkCount == 0)
            {
                return new RawChunkScheduleCache(entityManager.StructuralVersion, null, 0, false);
            }

            // 托管路径：chunkId（整数）+ 直接持有 Chunk[] 引用（cache.ManagedChunkArray，无 GCHandle）
            var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
            var chunkArray = chunkList.ToArray();
            for (int ci = 0; ci < chunkCount; ci++)
            {
                var chunk = chunkArray[ci];
                int componentCount = chunk.ComponentCount;
                var enableBitMaps = (void**)Marshal.AllocHGlobal(componentCount * sizeof(void*));
                for (int c = 0; c < componentCount; c++)
                    enableBitMaps[c] = chunk.GetEnableBitMapPointer(c);

                chunksPtr[ci] = new ChunkJobData
                {
                    entityArray = null,
                    entityCount = chunk.EntityCount,
                    componentCount = componentCount,
                    componentArrays = null,
                    componentSizes = null,
                    enableBitMaps = enableBitMaps,
                    componentTypeIndices = null,
                    chunkHandle = (IntPtr)ci,  // chunkId
                    requiredComponentArrays = null,
                    requiredComponentCount = 0,
                    sharedValuePtrs = FillSharedValuePtrs(chunk, chunk.Archetype, componentCount),
                    sharedValueCount = chunk.HasSharedValues ? CountBlittableShared(chunk.Archetype, componentCount) : 0
                };
            }

            var cache = new RawChunkScheduleCache(entityManager.StructuralVersion, chunksPtr, chunkCount, false, matchingArchetypes.ToArray());
            cache.ManagedChunkArray = chunkArray;  // 直接持有引用（无 GCHandle）
            return cache;
        }

        private static int GetQueryHash(QueryBuilder query)
        {
            var hash = new HashCode();
            AddComponentTypesHash(ref hash, query.All);
            AddComponentTypesHash(ref hash, query.Any);
            AddComponentTypesHash(ref hash, query.None);
            AddComponentTypesHash(ref hash, query.AllEnabled);
            hash.Add(query.LimitCount);

            // Shared 过滤必须进指纹：否则 `.WithShared(v1)` 与 `.WithShared(v2)` 会共享同一缓存条目，
            // 第二条查询直接复用第一条的 Chunk 集合（过滤值被无视）。
            hash.Add(query.HasSharedFilter);
            if (query.HasSharedFilter)
            {
                hash.Add(query.SharedFilterType.Id);
                hash.Add(query.SharedFilterValue?.GetHashCode() ?? 0);
            }

            // 变更追踪过滤同理：ChangedComponents / MinChangedVersion 不同的查询不能共用缓存。
            AddComponentTypesHash(ref hash, query.ChangedComponents);
            hash.Add(query.MinChangedVersion);

            // 关系过滤：进指纹以便区分；注意 job 路径目前不做逐槽位关系匹配
            // （chunk 级只能看到列存在性），该限制记录在 Runtime-Contracts 文档中。
            hash.Add(query.HasRelationshipFilter);
            if (query.HasRelationshipFilter)
            {
                hash.Add(query.RelationshipFilterType.Id);
                hash.Add(query.RelationshipFilterTarget.TargetId);
                hash.Add(query.RelationshipFilterTarget.TargetVersion);
            }
            return hash.ToHashCode();
        }

        private static void AddComponentTypesHash(ref HashCode hash, ComponentType[] types)
        {
            if (types == null)
            {
                hash.Add(0);
                return;
            }

            hash.Add(types.Length);
            for (int i = 0; i < types.Length; i++)
            {
                hash.Add(types[i].Id);
            }
        }

        private static int GetRequiredComponentHash(int[] requiredComponentTypeIds)
        {
            var hash = new HashCode();
            if (requiredComponentTypeIds == null)
            {
                hash.Add(0);
                return hash.ToHashCode();
            }

            hash.Add(requiredComponentTypeIds.Length);
            for (int i = 0; i < requiredComponentTypeIds.Length; i++)
            {
                hash.Add(requiredComponentTypeIds[i]);
            }

            return hash.ToHashCode();
        }

    }
}
