using EntJoy.JobSystem;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.ECS.JobSystem
{

    public static unsafe class ChunkJobScheduler
    {


        // ======================== Job 名登记 / 实体跟踪 ========================
        private static NativeJobHandle TrackEntityJob(EntityManager entityManager, NativeJobHandle handle, Archetype[]? matchingArchetypes = null, ComponentType[]? writtenComponents = null)
        {
            entityManager?.TrackEntityJob(handle, matchingArchetypes, writtenComponents);
            return handle;
        }

        // ======================== Chunk 调度缓存（归属 ECS，与 engine 解耦） ========================
        internal static readonly object _rawChunkScheduleCacheLock = new();
        internal static readonly Dictionary<RawChunkScheduleCacheKey, RawChunkScheduleCache> _rawChunkScheduleCaches = new();
        internal static readonly Dictionary<RawChunkScheduleCacheKey, EntityBatchScheduleCache> _entityBatchScheduleCaches = new();

        // 托管回调层的 Chunk[] 引用表：context block 指针 → Chunk[]（无 GCHandle，无泄漏）
        internal static readonly ConcurrentDictionary<IntPtr, Chunk[]> ChunkArrayTable = new();

        public static void ClearRawChunkScheduleCaches(EntityManager entityManager)
        {
            if (entityManager == null) return;

            lock (_rawChunkScheduleCacheLock)
            {
                var keysToRemove = new List<RawChunkScheduleCacheKey>();
                foreach (var pair in _rawChunkScheduleCaches)
                {
                    if (pair.Key.Matches(entityManager))
                    {
                        pair.Value.Dispose();
                        keysToRemove.Add(pair.Key);
                    }
                }

                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    _rawChunkScheduleCaches.Remove(keysToRemove[i]);
                }

                keysToRemove.Clear();
                foreach (var pair in _entityBatchScheduleCaches)
                {
                    if (pair.Key.Matches(entityManager))
                    {
                        pair.Value.Dispose();
                        keysToRemove.Add(pair.Key);
                    }
                }

                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    _entityBatchScheduleCaches.Remove(keysToRemove[i]);
                }
            }
        }

        // ======================== IJobChunk 调度 ========================
        public static NativeJobHandle ScheduleChunk<T>(ref T job, EntityManager entityManager, QueryBuilder query, NativeJobHandle? dependsOn = null, ComponentType[]? writtenComponents = null)
            where T : struct, IJobChunk
            => ScheduleChunkCore(ref job, entityManager, query, IntPtr.Zero, null, dependsOn, writtenComponents: writtenComponents);

        public static NativeJobHandle ScheduleChunkWithWorkerCap<T>(ref T job, EntityManager entityManager, QueryBuilder query, int workerCap, NativeJobHandle? dependsOn = null, ComponentType[]? writtenComponents = null)
            where T : struct, IJobChunk
            => ScheduleChunkCore(ref job, entityManager, query, IntPtr.Zero, null, dependsOn, workerCap: workerCap, writtenComponents: writtenComponents);

        public static NativeJobHandle ScheduleChunkRangeRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr rangeFuncPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn = null)
            where T : struct
            => ScheduleNativeChunkRangeRawCore(
                ref job, entityManager, query, rangeFuncPtr,
                requiredComponentTypeIds, dependsOn, workerCap: 0, rangeSize: 0);

        public static NativeJobHandle ScheduleChunkRawWithWorkerCap<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, NativeJobHandle? dependsOn = null)
            where T : struct, IJobChunk
            => ScheduleChunkCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap: workerCap);

        public static NativeJobHandle ScheduleChunkRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
            where T : struct, IJobChunk
            => ScheduleChunkCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap: workerCap, rangeSize: rangeSize);

        public static NativeJobHandle ScheduleEntityRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
            where T : struct
            => ScheduleNativeChunkRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize);

        public static NativeJobHandle ScheduleEntityRangeRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
            where T : struct
            => ScheduleNativeChunkRangeRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize);

        public static NativeJobHandle ScheduleChunkEntityBatchRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
            where T : struct, IJobChunk
            => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize, jobKind: NativeEcsJobKind.Chunk);

        private static NativeJobHandle ScheduleChunkCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, ChunkScheduleMode? forcedMode = null, int workerCap = 0, int rangeSize = 0, ComponentType[]? writtenComponents = null)
            where T : struct, IJobChunk
        {
            var allEnabledTypes = query.AllEnabled;
            bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            bool canUseRawCache = funcPtr != IntPtr.Zero &&
                                  !hasEnabledFilter;
            if (canUseRawCache &&
                TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) &&
                rawCache.ChunkCount > 0)
            {
                var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
                var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease);
                try
                {
                    using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                    IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, NativeChunkJobs.ChunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, mode, workerCap, rangeSize);
                    NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                    return TrackEntityJob(entityManager, new NativeJobHandle(handle), rawCache.MatchingArchetypes, writtenComponents);
                }
                catch { NativeChunkJobs.ChunkCleanup(rawContextBlock); throw; }
            }

            bool jobHasManagedReferences = NativeJobCore.JobHasManagedReferences<T>();

            // 托管路径（唯一）：区间回调，blittable 走 job blob、托管引用 job 走 GCHandle box
            if (funcPtr == IntPtr.Zero &&
                TryGetManagedChunkScheduleCache(entityManager, query, out var managedCache, out var managedCacheLease) &&
                managedCache.ChunkCount > 0)
            {
                var managedContextBlock = CreateChunkContextBlock(ref job, managedCache.ChunksPtr, managedCache.ChunkCount, hasEnabledFilter, allEnabledTypes, -1, false, null, managedCacheLease, jobBoxed: jobHasManagedReferences, chunkArray: managedCache.ManagedChunkArray);
                try
                {
                    var cache = NativeJobCore.GetOrCreateDelegateCache<T, ChunkRangeJobFuncDelegate>(() => ChunkJobCallbacks.CreateChunkRangeCallback<T>());
                    using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                    IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkRangeJobEx(cache.FuncPtr, managedContextBlock, NativeChunkJobs.ChunkCleanupPtr, managedCache.ChunksPtr, managedCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
                    NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                    return TrackEntityJob(entityManager, new NativeJobHandle(handle), managedCache.MatchingArchetypes, writtenComponents);
                }
                catch { NativeChunkJobs.ChunkCleanup(managedContextBlock); throw; }
            }

            return default;
        }

        private static NativeJobHandle ScheduleNativeChunkRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize)
            where T : struct
        {
            if (funcPtr == IntPtr.Zero)
                throw new ArgumentException("Native chunk raw scheduling requires a function pointer.", nameof(funcPtr));

            var allEnabledTypes = query.AllEnabled;
            bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            if (!hasEnabledFilter &&
                TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) &&
                rawCache.ChunkCount > 0)
            {
                var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease);
                try
                {
                    using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                    IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, NativeChunkJobs.ChunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
                    NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                    return TrackEntityJob(entityManager, new NativeJobHandle(handle), rawCache.MatchingArchetypes);
                }
                catch { NativeChunkJobs.ChunkCleanup(rawContextBlock); throw; }
            }

            var chunkList = new List<Chunk>(128);
            var fallbackMatchingArchetypes = new List<Archetype>(8);
            CollectMatchingChunks(entityManager, query, chunkList, fallbackMatchingArchetypes);

            int chunkCount = chunkList.Count;
            if (chunkCount == 0) return default;

            var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
            const int gcHandleStartIndex = -1;

            FillChunkJobDataList(chunksPtr, chunkList, requiredComponentTypeIds, gcHandles: null);

            var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, true, requiredComponentTypeIds);
            try
            {
                using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
                NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(handle), fallbackMatchingArchetypes.ToArray());
            }
            catch { NativeChunkJobs.ChunkCleanup(contextBlock); throw; }
        }

        private static void CollectMatchingChunks(EntityManager entityManager, QueryBuilder query, List<Chunk> chunkList, List<Archetype> matchingArchetypes)
        {
            for (int i = 0; i < entityManager.ArchetypeCount; i++)
            {
                var arch = entityManager.Archetypes[i];
                if (arch != null && arch.IsMatch(query))
                {
                    matchingArchetypes.Add(arch);
                    foreach (var c in arch.ChunkSpan)
                        if (c.EntityCount > 0) chunkList.Add(c);
                }
            }
        }

        /// <summary>
        /// 填充 ChunkJobData 数组：组件指针/大小/位图/类型索引 + requiredComponentTypeIds 对应指针。
        /// gcHandles 为 null 时 chunkHandle 置 IntPtr.Zero（纯原生回调）；否则从 GCHandle[] 取句柄（托管回调）。
        /// 调用方负责释放各 chunk 分配的 compPtrs/compSizes/bitmaps/typeIndices/requiredArrays。
        /// </summary>
        private unsafe static void FillChunkJobDataList(ChunkJobData* chunksPtr, List<Chunk> chunkList, int[]? requiredComponentTypeIds, GCHandle[]? gcHandles)
        {
            for (int ci = 0; ci < chunkList.Count; ci++)
            {
                var chunk = chunkList[ci];
                var arch = chunk.Archetype;
                int compCount = chunk.ComponentCount;
                var compPtrs = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
                var compSizes = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
                var bitmaps = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
                var typeIndices = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));

                int requiredCount = requiredComponentTypeIds?.Length ?? 0;
                void** requiredArrays = null;
                if (requiredCount > 0)
                {
                    requiredArrays = (void**)Marshal.AllocHGlobal(requiredCount * sizeof(void*));
                    for (int r = 0; r < requiredCount; r++) requiredArrays[r] = null;
                }

                for (int c = 0; c < compCount; c++)
                {
                    compPtrs[c] = (void*)chunk.GetComponentArrayPointer(c);
                    compSizes[c] = arch.Types[c].Size;
                    bitmaps[c] = chunk.GetEnableBitMapPointer(c);
                    typeIndices[c] = arch.Types[c].Id;
                }

                if (requiredArrays != null)
                {
                    for (int r = 0; r < requiredCount; r++)
                    {
                        int requiredTypeId = requiredComponentTypeIds![r];
                        for (int c = 0; c < compCount; c++)
                        {
                            if (typeIndices[c] == requiredTypeId)
                            {
                                requiredArrays[r] = compPtrs[c];
                                break;
                            }
                        }
                    }
                }

                chunksPtr[ci] = new ChunkJobData
                {
                    entityArray = (void*)chunk.GetEntityPointer(),
                    entityCount = chunk.EntityCount,
                    componentCount = compCount,
                    componentArrays = compPtrs,
                    componentSizes = compSizes,
                    enableBitMaps = bitmaps,
                    componentTypeIndices = typeIndices,
                    chunkHandle = gcHandles != null ? (IntPtr)gcHandles[ci] : IntPtr.Zero,
                    requiredComponentArrays = requiredArrays,
                    requiredComponentCount = requiredCount
                };
            }
        }

        private static NativeJobHandle ScheduleNativeChunkRangeRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize, ChunkScheduleMode? forcedMode = null)
            where T : struct
        {
            if (funcPtr == IntPtr.Zero)
                throw new ArgumentException("Native chunk range raw scheduling requires a function pointer.", nameof(funcPtr));

            var allEnabledTypes = query.AllEnabled;
            bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
            if (!hasEnabledFilter &&
                TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) &&
                rawCache.ChunkCount > 0)
            {
                var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease);
                try
                {
                    using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                    return TrackEntityJob(entityManager, new NativeJobHandle(NativeChunkJobs.JobSystem_ScheduleChunkRangeJobEx(funcPtr, rawContextBlock, NativeChunkJobs.ChunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, mode, workerCap, rangeSize)), rawCache.MatchingArchetypes);
                }
                catch { NativeChunkJobs.ChunkCleanup(rawContextBlock); throw; }
            }

            var chunkList = new List<Chunk>(128);
            var fallbackMatchingArchetypes = new List<Archetype>(8);
            CollectMatchingChunks(entityManager, query, chunkList, fallbackMatchingArchetypes);

            int chunkCount = chunkList.Count;
            if (chunkCount == 0) return default;

            var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
            const int gcHandleStartIndex = -1;

            FillChunkJobDataList(chunksPtr, chunkList, requiredComponentTypeIds, gcHandles: null);

            var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, true, requiredComponentTypeIds);
            try
            {
                using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                return TrackEntityJob(entityManager, new NativeJobHandle(NativeChunkJobs.JobSystem_ScheduleChunkRangeJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, mode, workerCap, rangeSize)), fallbackMatchingArchetypes.ToArray());
            }
            catch { NativeChunkJobs.ChunkCleanup(contextBlock); throw; }
        }

        private static NativeJobHandle ScheduleNativeEntityBatchRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize, bool useScheduleAndComplete = false, ChunkScheduleMode? forcedMode = null, NativeEcsJobKind jobKind = NativeEcsJobKind.Entity)
            where T : struct
        {
            if (funcPtr == IntPtr.Zero)
                throw new ArgumentException("Native entity batch raw scheduling requires a function pointer.", nameof(funcPtr));

            var allEnabledTypes = query.AllEnabled;
            bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            if (hasEnabledFilter)
                throw new NotSupportedException("Native IJobEntity DirectEntityBatch does not support AllEnabled filters yet.");

            if (!TryGetEntityBatchScheduleCache(entityManager, query, requiredComponentTypeIds, out var cache, out var cacheLease) ||
                cache.BatchCount == 0)
                return default;

            var contextBlock = CreateChunkContextBlock(ref job, null, cache.BatchCount, false, null, -1, false, requiredComponentTypeIds, cacheLease);
            try
            {
                using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
                var handle = useScheduleAndComplete
                    ? NativeChunkJobs.JobSystem_ScheduleAndCompleteEntityBatchJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, mode, workerCap, rangeSize, jobKind)
                    : NativeChunkJobs.JobSystem_ScheduleEntityBatchJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, mode, workerCap, rangeSize, jobKind);
                NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(handle));
            }
            catch { NativeChunkJobs.ChunkCleanup(contextBlock); throw; }
        }

        /// <summary>
        /// 同步执行 [NativeTranspile] IJobChunk：以 ImmediateNative 模式提交（C++ 侧主线程直接执行，
        /// 零 worker 唤醒），并一步完成（无需显式 Complete）。等价于 Run 的零调度开销版本。
        /// </summary>
        public static void RunChunkImmediate<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds)
            where T : struct
        {
            ScheduleNativeEntityBatchRawCore(
                ref job, entityManager, query, funcPtr, requiredComponentTypeIds,
                null, workerCap: 0, rangeSize: 0,
                useScheduleAndComplete: true, forcedMode: ChunkScheduleMode.ImmediateNative,
                jobKind: NativeEcsJobKind.Chunk);
        }

        /// <summary>
        /// 同步执行 [NativeTranspile] IJobChunk（ISPC 后端）：走 ChunkRange 路径的 ImmediateNative 提交。
        /// </summary>
        public static void RunChunkRangeImmediate<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds)
            where T : struct
        {
            ScheduleNativeChunkRangeRawCore(
                ref job, entityManager, query, funcPtr, requiredComponentTypeIds,
                null, workerCap: 0, rangeSize: 0,
                forcedMode: ChunkScheduleMode.ImmediateNative);
        }

        // ======================== 调度缓存 ========================
        private static bool TryGetRawChunkScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds, out RawChunkScheduleCache cache, out IDisposable lease)
        {
            lease = null;
            var key = new RawChunkScheduleCacheKey(entityManager, GetQueryHash(query), GetRequiredComponentHash(requiredComponentTypeIds), 0);
            lock (_rawChunkScheduleCacheLock)
            {
                if (_rawChunkScheduleCaches.TryGetValue(key, out cache))
                {
                    if (cache.StructuralVersion == entityManager.StructuralVersion)
                    {
                        if (cache.ChunkCount > 0)
                        {
                            lease = cache.RetainLease();
                        }

                        return true;
                    }

                    cache.Dispose();
                    _rawChunkScheduleCaches.Remove(key);
                }

                cache = BuildRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds);
                _rawChunkScheduleCaches[key] = cache;
                if (cache.ChunkCount > 0)
                {
                    lease = cache.RetainLease();
                }

                return true;
            }
        }

        private static bool TryGetManagedChunkScheduleCache(EntityManager entityManager, QueryBuilder query, out RawChunkScheduleCache cache, out IDisposable lease)
        {
            lease = null;
            var key = new RawChunkScheduleCacheKey(entityManager, GetQueryHash(query), 0, 1);
            lock (_rawChunkScheduleCacheLock)
            {
                if (_rawChunkScheduleCaches.TryGetValue(key, out cache))
                {
                    if (cache.StructuralVersion == entityManager.StructuralVersion)
                    {
                        if (cache.ChunkCount > 0)
                        {
                            lease = cache.RetainLease();
                        }

                        return true;
                    }

                    cache.Dispose();
                    _rawChunkScheduleCaches.Remove(key);
                }

                cache = BuildManagedChunkScheduleCache(entityManager, query);
                _rawChunkScheduleCaches[key] = cache;
                if (cache.ChunkCount > 0)
                {
                    lease = cache.RetainLease();
                }

                return true;
            }
        }

        private static bool TryGetEntityBatchScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds, out EntityBatchScheduleCache cache, out IDisposable lease)
        {
            lease = null;
            var key = new RawChunkScheduleCacheKey(entityManager, GetQueryHash(query), GetRequiredComponentHash(requiredComponentTypeIds), 3);
            lock (_rawChunkScheduleCacheLock)
            {
                if (_entityBatchScheduleCaches.TryGetValue(key, out cache))
                {
                    if (cache.StructuralVersion == entityManager.StructuralVersion)
                    {
                        if (cache.BatchCount > 0)
                        {
                            lease = cache.RetainLease();
                        }

                        return true;
                    }

                    cache.Dispose();
                    _entityBatchScheduleCaches.Remove(key);
                }

                cache = BuildEntityBatchScheduleCache(entityManager, query, requiredComponentTypeIds);
                _entityBatchScheduleCaches[key] = cache;
                if (cache.BatchCount > 0)
                {
                    lease = cache.RetainLease();
                }

                return true;
            }
        }

        private static EntityBatchScheduleCache BuildEntityBatchScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds)
        {
            var chunkList = new List<Chunk>(128);
            for (int i = 0; i < entityManager.ArchetypeCount; i++)
            {
                var archetype = entityManager.Archetypes[i];
                if (archetype != null && archetype.IsMatch(query))
                {
                    foreach (var chunk in archetype.ChunkSpan)
                    {
                        if (chunk.EntityCount > 0)
                        {
                            chunkList.Add(chunk);
                        }
                    }
                }
            }

            int batchCount = chunkList.Count;
            if (batchCount == 0)
            {
                return new EntityBatchScheduleCache(entityManager.StructuralVersion, null, 0, null, null);
            }

            int requiredCount = requiredComponentTypeIds?.Length ?? 0;
            bool hasEnableFilter = query.AllEnabled != null && query.AllEnabled.Length > 0;
            int enableBitmapCount = hasEnableFilter ? requiredCount : 0;

            var batchesPtr = (EntityBatchData*)Marshal.AllocHGlobal(batchCount * sizeof(EntityBatchData));
            void* componentArraysBlock = null;
            if (requiredCount > 0)
                componentArraysBlock = (void*)Marshal.AllocHGlobal(batchCount * requiredCount * sizeof(void*));
            void* enableBitMapsBlock = null;
            if (enableBitmapCount > 0)
                enableBitMapsBlock = (void*)Marshal.AllocHGlobal(batchCount * enableBitmapCount * sizeof(void*));

            for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                var chunk = chunkList[batchIndex];
                var archetype = chunk.Archetype;

                if (componentArraysBlock != null)
                {
                    void** arraysBase = (void**)componentArraysBlock + batchIndex * requiredCount;
                    for (int r = 0; r < requiredCount; r++)
                    {
                        arraysBase[r] = null;
                        int requiredTypeId = requiredComponentTypeIds[r];
                        for (int componentIndex = 0; componentIndex < chunk.ComponentCount; componentIndex++)
                        {
                            if (archetype.Types[componentIndex].Id == requiredTypeId)
                            {
                                arraysBase[r] = (void*)chunk.GetComponentArrayPointer(componentIndex);
                                break;
                            }
                        }
                    }

                    batchesPtr[batchIndex].componentArrays = arraysBase;
                }
                else
                {
                    batchesPtr[batchIndex].componentArrays = null;
                }

                if (enableBitMapsBlock != null)
                {
                    void** bitmapsBase = (void**)enableBitMapsBlock + batchIndex * enableBitmapCount;
                    batchesPtr[batchIndex].enableBitMaps = bitmapsBase;
                    batchesPtr[batchIndex].enableBitmapCount = enableBitmapCount;
                    for (int e = 0; e < enableBitmapCount; e++)
                    {
                        bitmapsBase[e] = null;
                        int requiredTypeId = requiredComponentTypeIds[e];
                        for (int componentIndex = 0; componentIndex < chunk.ComponentCount; componentIndex++)
                        {
                            if (archetype.Types[componentIndex].Id == requiredTypeId)
                            {
                                bitmapsBase[e] = chunk.GetEnableBitMapPointer(componentIndex);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    batchesPtr[batchIndex].enableBitMaps = null;
                    batchesPtr[batchIndex].enableBitmapCount = 0;
                }

                batchesPtr[batchIndex].entityCount = chunk.EntityCount;
            }

            return new EntityBatchScheduleCache(entityManager.StructuralVersion, batchesPtr, batchCount, componentArraysBlock, enableBitMapsBlock);
        }

        private static RawChunkScheduleCache BuildRawChunkScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds)
        {
            var chunkList = new List<Chunk>(128);
            var matchingArchetypes = new List<Archetype>(8);
            for (int i = 0; i < entityManager.ArchetypeCount; i++)
            {
                var archetype = entityManager.Archetypes[i];
                if (archetype != null && archetype.IsMatch(query))
                {
                    matchingArchetypes.Add(archetype);
                    foreach (var chunk in archetype.ChunkSpan)
                    {
                        if (chunk.EntityCount > 0)
                        {
                            chunkList.Add(chunk);
                        }
                    }
                }
            }

            int chunkCount = chunkList.Count;
            if (chunkCount == 0)
            {
                return new RawChunkScheduleCache(entityManager.StructuralVersion, null, 0);
            }

            var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
            int requiredCount = requiredComponentTypeIds?.Length ?? 0;

            for (int ci = 0; ci < chunkCount; ci++)
            {
                var chunk = chunkList[ci];
                var archetype = chunk.Archetype;
                int componentCount = chunk.ComponentCount;
                var componentArrays = (void**)Marshal.AllocHGlobal(componentCount * sizeof(void*));
                var componentTypeIndices = (int*)Marshal.AllocHGlobal(componentCount * sizeof(int));
                void** requiredArrays = null;

                if (requiredCount > 0)
                {
                    requiredArrays = (void**)Marshal.AllocHGlobal(requiredCount * sizeof(void*));
                    for (int r = 0; r < requiredCount; r++) requiredArrays[r] = null;
                }

                for (int componentIndex = 0; componentIndex < componentCount; componentIndex++)
                {
                    componentArrays[componentIndex] = (void*)chunk.GetComponentArrayPointer(componentIndex);
                    componentTypeIndices[componentIndex] = archetype.Types[componentIndex].Id;
                }

                if (requiredArrays != null)
                {
                    for (int r = 0; r < requiredCount; r++)
                    {
                        int requiredTypeId = requiredComponentTypeIds[r];
                        for (int componentIndex = 0; componentIndex < componentCount; componentIndex++)
                        {
                            if (componentTypeIndices[componentIndex] == requiredTypeId)
                            {
                                requiredArrays[r] = componentArrays[componentIndex];
                                break;
                            }
                        }
                    }
                }

                chunksPtr[ci] = new ChunkJobData
                {
                    entityArray = (void*)chunk.GetEntityPointer(),
                    entityCount = chunk.EntityCount,
                    componentCount = componentCount,
                    componentArrays = componentArrays,
                    componentSizes = null,
                    enableBitMaps = null,
                    componentTypeIndices = componentTypeIndices,
                    chunkHandle = IntPtr.Zero,
                    requiredComponentArrays = requiredArrays,
                    requiredComponentCount = requiredCount
                };
            }

            return new RawChunkScheduleCache(entityManager.StructuralVersion, chunksPtr, chunkCount, matchingArchetypes: matchingArchetypes.ToArray());
        }

        private static RawChunkScheduleCache BuildManagedChunkScheduleCache(EntityManager entityManager, QueryBuilder query)
        {
            var chunkList = new List<Chunk>(128);
            var matchingArchetypes = new List<Archetype>(8);
            for (int i = 0; i < entityManager.ArchetypeCount; i++)
            {
                var archetype = entityManager.Archetypes[i];
                if (archetype != null && archetype.IsMatch(query))
                {
                    matchingArchetypes.Add(archetype);
                    foreach (var chunk in archetype.ChunkSpan)
                    {
                        if (chunk.EntityCount > 0)
                        {
                            chunkList.Add(chunk);
                        }
                    }
                }
            }

            int chunkCount = chunkList.Count;
            if (chunkCount == 0)
            {
                return new RawChunkScheduleCache(entityManager.StructuralVersion, null, 0, false);
            }

            // 托管路径：chunkId（整数）+ 单 GCHandle 保活 Chunk[]，不给每 chunk GCHandle
            var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
            var chunkArray = chunkList.ToArray();
            var chunkArrayGCHandle = GCHandle.Alloc(chunkArray, GCHandleType.Normal);
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
                    requiredComponentCount = 0
                };
            }

            var cache = new RawChunkScheduleCache(entityManager.StructuralVersion, chunksPtr, chunkCount, false, matchingArchetypes.ToArray());
            cache.ManagedChunkArray = chunkArray;  // 直接持有引用（无 GCHandle）
            return cache;

            return new RawChunkScheduleCache(entityManager.StructuralVersion, chunksPtr, chunkCount, true, matchingArchetypes: matchingArchetypes.ToArray());
        }

        private static int GetQueryHash(QueryBuilder query)
        {
            var hash = new HashCode();
            AddComponentTypesHash(ref hash, query.All);
            AddComponentTypesHash(ref hash, query.Any);
            AddComponentTypesHash(ref hash, query.None);
            AddComponentTypesHash(ref hash, query.AllEnabled);
            hash.Add(query.LimitCount);
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

        internal readonly struct RawChunkScheduleCacheKey : IEquatable<RawChunkScheduleCacheKey>
        {
            private readonly EntityManager _entityManager;
            private readonly int _managerHash;
            private readonly int _queryHash;
            private readonly int _requiredHash;
            private readonly int _mode;

            public RawChunkScheduleCacheKey(EntityManager entityManager, int queryHash, int requiredHash, int mode)
            {
                _entityManager = entityManager;
                _managerHash = RuntimeHelpers.GetHashCode(entityManager);
                _queryHash = queryHash;
                _requiredHash = requiredHash;
                _mode = mode;
            }

            public bool Equals(RawChunkScheduleCacheKey other)
                => ReferenceEquals(_entityManager, other._entityManager) &&
                   _queryHash == other._queryHash &&
                   _requiredHash == other._requiredHash &&
                   _mode == other._mode;

            public bool Matches(EntityManager entityManager)
                => ReferenceEquals(_entityManager, entityManager);

            public override bool Equals(object obj)
                => obj is RawChunkScheduleCacheKey other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(_managerHash, _queryHash, _requiredHash, _mode);
        }

        internal sealed class RawChunkScheduleCache : IDisposable
        {
            public readonly int StructuralVersion;
            public readonly ChunkJobData* ChunksPtr;
            public readonly int ChunkCount;
            public readonly bool OwnsChunkHandles;
            public readonly Archetype[]? MatchingArchetypes;
            internal Chunk[]? ManagedChunkArray;  // 托管路径：直接持有 Chunk[]（无 GCHandle）
            private int _leaseCount;
            private int _retired;
            private int _disposed;

            public RawChunkScheduleCache(int structuralVersion, ChunkJobData* chunksPtr, int chunkCount, bool ownsChunkHandles = false, Archetype[]? matchingArchetypes = null)
            {
                StructuralVersion = structuralVersion;
                ChunksPtr = chunksPtr;
                ChunkCount = chunkCount;
                OwnsChunkHandles = ownsChunkHandles;
                MatchingArchetypes = matchingArchetypes;
            }

            ~RawChunkScheduleCache()
            {
                Dispose();
            }

            public IDisposable RetainLease()
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(RawChunkScheduleCache));
                }

                Interlocked.Increment(ref _leaseCount);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    ReleaseLease();
                    throw new ObjectDisposedException(nameof(RawChunkScheduleCache));
                }

                return new CacheLease(this);
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _retired, 1);
                GC.SuppressFinalize(this);
                TryDisposeNow();
            }

            private void ReleaseLease()
            {
                if (Interlocked.Decrement(ref _leaseCount) == 0)
                {
                    TryDisposeNow();
                }
            }

            private void TryDisposeNow()
            {
                if (ChunksPtr == null)
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    return;
                }

                if (Volatile.Read(ref _retired) == 0 ||
                    Volatile.Read(ref _leaseCount) != 0 ||
                    Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                for (int i = 0; i < ChunkCount; i++)
                {
                    var chunkData = ChunksPtr[i];
                    if (chunkData.componentArrays != null) Marshal.FreeHGlobal((IntPtr)chunkData.componentArrays);
                    if (chunkData.componentSizes != null) Marshal.FreeHGlobal((IntPtr)chunkData.componentSizes);
                    if (chunkData.enableBitMaps != null) Marshal.FreeHGlobal((IntPtr)chunkData.enableBitMaps);
                    if (chunkData.componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)chunkData.componentTypeIndices);
                    if (chunkData.requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)chunkData.requiredComponentArrays);
                    if (OwnsChunkHandles && chunkData.chunkHandle != IntPtr.Zero)
                    {
                        var handle = GCHandle.FromIntPtr(chunkData.chunkHandle);
                        if (handle.IsAllocated) handle.Free();
                    }
                }

                Marshal.FreeHGlobal((IntPtr)ChunksPtr);
                // ManagedChunkArray 无 GCHandle，由引用计数 GC 管理
            }

            internal sealed class CacheLease : IDisposable
            {
                private RawChunkScheduleCache _owner;

                public CacheLease(RawChunkScheduleCache owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    var owner = Interlocked.Exchange(ref _owner, null);
                    owner?.ReleaseLease();
                }
            }
        }

        internal sealed class EntityBatchScheduleCache : IDisposable
        {
            public readonly int StructuralVersion;
            public readonly EntityBatchData* BatchesPtr;
            public readonly int BatchCount;
            private void* _componentArraysBlock;
            private void* _enableBitMapsBlock;
            private int _leaseCount;
            private int _retired;
            private int _disposed;

            public EntityBatchScheduleCache(int structuralVersion, EntityBatchData* batchesPtr, int batchCount, void* componentArraysBlock, void* enableBitMapsBlock)
            {
                StructuralVersion = structuralVersion;
                BatchesPtr = batchesPtr;
                BatchCount = batchCount;
                _componentArraysBlock = componentArraysBlock;
                _enableBitMapsBlock = enableBitMapsBlock;
            }

            ~EntityBatchScheduleCache()
            {
                Dispose();
            }

            public IDisposable RetainLease()
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(EntityBatchScheduleCache));
                }

                Interlocked.Increment(ref _leaseCount);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    ReleaseLease();
                    throw new ObjectDisposedException(nameof(EntityBatchScheduleCache));
                }

                return new CacheLease(this);
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _retired, 1);
                GC.SuppressFinalize(this);
                TryDisposeNow();
            }

            private void ReleaseLease()
            {
                if (Interlocked.Decrement(ref _leaseCount) == 0)
                {
                    TryDisposeNow();
                }
            }

            private void TryDisposeNow()
            {
                if (BatchesPtr == null)
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    return;
                }

                if (Volatile.Read(ref _retired) == 0 ||
                    Volatile.Read(ref _leaseCount) != 0 ||
                    Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                if (_componentArraysBlock != null)
                    Marshal.FreeHGlobal((IntPtr)_componentArraysBlock);
                if (_enableBitMapsBlock != null)
                    Marshal.FreeHGlobal((IntPtr)_enableBitMapsBlock);
                Marshal.FreeHGlobal((IntPtr)BatchesPtr);
            }

            internal sealed class CacheLease : IDisposable
            {
                private EntityBatchScheduleCache _owner;

                public CacheLease(EntityBatchScheduleCache owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    var owner = Interlocked.Exchange(ref _owner, null);
                    owner?.ReleaseLease();
                }
            }
        }

        // ======================== 上下文块创建 / 清理 ========================
        private unsafe static IntPtr CreateChunkContextBlock<T>(ref T job, ChunkJobData* chunksPtr, int chunkCount, bool hasEnabledFilter, ComponentType[] allEnabledTypes, int gcHandleStartIndex, bool ownsChunkData, int[] requiredComponentTypeIds = null, IDisposable cacheLease = null, bool jobBoxed = false, Chunk[]? chunkArray = null) where T : struct
        {
            int jobSize = jobBoxed ? sizeof(IntPtr) : Unsafe.SizeOf<T>();
            int headerSize = Unsafe.SizeOf<ChunkContextHeader>();
            int typesDataSize = 0;
            int[] typeHashes = null;
            if (hasEnabledFilter && allEnabledTypes != null)
            {
                typeHashes = new int[allEnabledTypes.Length];
                for (int i = 0; i < allEnabledTypes.Length; i++) typeHashes[i] = allEnabledTypes[i].GetHashCode();
                typesDataSize = allEnabledTypes.Length * sizeof(int);
            }
            int requiredTypesDataSize = requiredComponentTypeIds != null ? requiredComponentTypeIds.Length * sizeof(int) : 0;
            int totalSize = headerSize + typesDataSize + requiredTypesDataSize + jobSize;
            int pooledSize = IntPtr.Size + totalSize;
            var pooledBlock = NativeJobCore.ContextPool.Rent(pooledSize);
            var block = pooledBlock + IntPtr.Size;
            *(int*)pooledBlock = pooledSize;
            Unsafe.InitBlockUnaligned((void*)block, 0, (uint)totalSize);
            var header = (ChunkContextHeader*)block;
            header->chunkCount = chunkCount;
            header->hasEnabledFilter = hasEnabledFilter ? 1 : 0;
            header->gcHandleStartIndex = gcHandleStartIndex;
            header->ownsChunkData = ownsChunkData ? 1 : 0;
            header->chunksPtr = (IntPtr)chunksPtr;
            header->cleanupInProgress = 0;
            header->jobIsBoxed = jobBoxed ? 1 : 0;
            // 托管回调的 Chunk[] 通过 ChunkArrayTable 存储（按 context 指针索引，零 GCHandle）
            header->chunkArrayHandle = IntPtr.Zero;
            if (chunkArray != null) ChunkArrayTable[(IntPtr)block] = chunkArray;
            if (hasEnabledFilter && typeHashes != null)
            {
                var typeHashPtr = (int*)((byte*)block + headerSize);
                for (int i = 0; i < typeHashes.Length; i++) typeHashPtr[i] = typeHashes[i];
                header->allEnabledCount = typeHashes.Length;
                header->queryAllEnabledTypes = (IntPtr)typeHashPtr;
            }
            else { header->allEnabledCount = 0; header->queryAllEnabledTypes = IntPtr.Zero; }
            if (requiredComponentTypeIds != null && requiredComponentTypeIds.Length > 0)
            {
                var requiredTypePtr = (int*)((byte*)block + headerSize + typesDataSize);
                for (int i = 0; i < requiredComponentTypeIds.Length; i++) requiredTypePtr[i] = requiredComponentTypeIds[i];
                header->requiredComponentTypeIdCount = requiredComponentTypeIds.Length;
                header->requiredComponentTypeIds = (IntPtr)requiredTypePtr;
            }
            else { header->requiredComponentTypeIdCount = 0; header->requiredComponentTypeIds = IntPtr.Zero; }
            byte* jobPtr = (byte*)block + headerSize + typesDataSize + requiredTypesDataSize;
            if (jobBoxed)
            {
                *(IntPtr*)jobPtr = GCHandle.ToIntPtr(GCHandle.Alloc(new ManagedJobBox<T> { Job = job }, GCHandleType.Normal));
            }
            else if (NativeJobScheduler.TryGetJobFieldWriter(typeof(T), out var __fieldWriter))
            {
                ((NativeJobScheduler.JobFieldWriter<T>)__fieldWriter)(jobPtr, ref job);
            }
            else
            {
                Unsafe.CopyBlockUnaligned(jobPtr, Unsafe.AsPointer(ref job), (uint)jobSize);
            }
            if (cacheLease != null)
            {
                NativeChunkJobs.ChunkContextLeases[block] = GCHandle.Alloc(cacheLease, GCHandleType.Normal);
            }

            return block;
        }

        // ======================== Run 路径（主线程同步执行） ========================
        internal static unsafe void ExecuteOnQuery<T>(ref T job, EntityManager entityManager, QueryBuilder query)
            where T : struct, IJobChunk
        {
            var allEnabledTypes = query.AllEnabled;
            bool hasFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            for (int archIdx = 0; archIdx < entityManager.ArchetypeCount; archIdx++)
            {
                var archetype = entityManager.Archetypes[archIdx];
                if (archetype == null || !archetype.IsMatch(query)) continue;
                var chunks = archetype.ChunkSpan;
                for (int ci = 0; ci < chunks.Length; ci++)
                {
                    var chunk = chunks[ci];
                    if (chunk.EntityCount == 0) continue;
                    var mask = hasFilter ? ComputeChunkMask(chunk, allEnabledTypes) : default;
                    job.Execute(new ArchetypeChunk(chunk), mask);
                }
            }
        }

        // ======================== Phase C：共享 mask 工具（单一真值来源） ========================
        /// <summary>
        /// 对 chunk 中 enabled 组件的位图做 AND，返回组合掩码（null = 全禁用或无过滤）。
        /// 单组件零拷贝（直传位图），多组件走逐 ulong AND。
        /// </summary>
        internal static unsafe ChunkEnabledMask ComputeChunkMask(Chunk chunk, ComponentType[] allEnabledTypes)
        {
            if (allEnabledTypes == null || allEnabledTypes.Length == 0) return default;
            var archetype = chunk.Archetype;
            int entityCount = chunk.EntityCount;
            int ulongCount = (entityCount + 63) / 64;
            if (allEnabledTypes.Length == 1)
            {
                int idx = archetype.GetComponentTypeIndex(allEnabledTypes[0]);
                if (idx < 0) return default;
                ulong* bitmap = chunk.GetEnableBitMapPointer(idx);
                if (bitmap == null) return default;
                return new ChunkEnabledMask(bitmap, entityCount);
            }
            ulong* combined = TempBuffer.GetBuffer(ulongCount);
            bool first = false;
            for (int i = 0; i < allEnabledTypes.Length; i++)
            {
                int idx = archetype.GetComponentTypeIndex(allEnabledTypes[i]);
                if (idx < 0) continue;
                ulong* bitmap = chunk.GetEnableBitMapPointer(idx);
                if (bitmap == null) continue;
                if (!first) { Buffer.MemoryCopy(bitmap, combined, ulongCount * 8, ulongCount * 8); first = true; }
                else { for (int b = 0; b < ulongCount; b++) combined[b] &= bitmap[b]; }
            }
            return first ? new ChunkEnabledMask(combined, entityCount) : default;
        }

        internal static unsafe ComponentType[] ResolveEnabledTypes(ChunkContextHeader* header, Chunk chunk)
        {
            int count = header->allEnabledCount;
            if (count == 0) return Array.Empty<ComponentType>();
            int* hashes = (int*)header->queryAllEnabledTypes;
            var archTypes = chunk.Archetype.Types;
            var result = new ComponentType[count];
            for (int i = 0; i < count; i++)
            {
                int h = hashes[i];
                for (int k = 0; k < archTypes.Length; k++)
                {
                    if (archTypes[k].GetHashCode() == h) { result[i] = archTypes[k]; break; }
                }
            }
            return result;
        }

    }
}
