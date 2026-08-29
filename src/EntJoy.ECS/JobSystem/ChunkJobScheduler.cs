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
        // ─── Event Buffer 元数据缓存（Job 类型 → 查询结果） ───
        public struct EventBufferMeta
        {
            public int Count;                  // 事件类型数
            public Type[] EventTypes;          // 事件类型数组（与 adapter index 一一对应）
        }
        public static readonly ConcurrentDictionary<Type, EventBufferMeta> EventMetaCache = new();

        // ─── 活跃 EventBuffer 实例（context 指针 → buffer 列表，Complete 后 drain + free） ───
        internal static readonly ConcurrentDictionary<IntPtr, List<EventBufferHeader>> LiveEventBuffers = new();
        internal static readonly ConcurrentDictionary<IntPtr, List<IDisposable>> LiveEventBufferDisposables = new();
        // 事件类型数组（context 指针 → 该 job 的事件类型列表，drain 时需要按类型写回 EventStream）
        internal static readonly ConcurrentDictionary<IntPtr, Type[]> LiveEventBufferTypes = new();

        // ======================== Job 名登�?/ 实体跟踪 ========================
        /// <summary>�?C++ 返回�?IntPtr 构�?JobHandle�?/summary>
        private static JobHandle FromNative(IntPtr handle) => new JobHandle(new NativeJobHandle(handle));

        private static NativeJobHandle TrackEntityJob(EntityManager entityManager, JobHandle handle, Archetype[]? matchingArchetypes = null, ComponentType[]? writtenComponents = null)
        {
            entityManager?.TrackEntityJob(handle, matchingArchetypes, writtenComponents);
            return handle._nativeHandle;
        }

        // ======================== Chunk 调度缓存（归�?ECS，与 engine 解耦） ========================
        internal static readonly object _rawChunkScheduleCacheLock = new();
        internal static readonly Dictionary<RawChunkScheduleCacheKey, RawChunkScheduleCache> _rawChunkScheduleCaches = new();
        internal static readonly Dictionary<RawChunkScheduleCacheKey, EntityBatchScheduleCache> _entityBatchScheduleCaches = new();

        // 托管回调层的 Chunk[] 引用表：context block 指针 �?Chunk[]（无 GCHandle，无泄漏�?
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
        public static JobHandle ScheduleChunk<T>(ref T job, EntityManager entityManager, QueryBuilder query, NativeJobHandle? dependsOn = null, ComponentType[]? writtenComponents = null)
            where T : struct, IJobChunk
            => ScheduleChunkCore(ref job, entityManager, query, IntPtr.Zero, null, dependsOn, writtenComponents: writtenComponents);

        public static JobHandle ScheduleChunkWithWorkerCap<T>(ref T job, EntityManager entityManager, QueryBuilder query, int workerCap, NativeJobHandle? dependsOn = null, ComponentType[]? writtenComponents = null)
            where T : struct, IJobChunk
            => ScheduleChunkCore(ref job, entityManager, query, IntPtr.Zero, null, dependsOn, workerCap: workerCap, writtenComponents: writtenComponents);

        public static NativeJobHandle ScheduleChunkRangeRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr rangeFuncPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn = null, World world = null)
            where T : struct
            => ScheduleNativeChunkRangeRawCore(
                ref job, entityManager, query, rangeFuncPtr,
                requiredComponentTypeIds, dependsOn, workerCap: 0, rangeSize: 0, world: world);

        public static NativeJobHandle ScheduleChunkRawWithWorkerCap<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, NativeJobHandle? dependsOn = null, World world = null)
            where T : struct, IJobChunk
            => ScheduleChunkNativeCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap: workerCap, world: world);

        public static NativeJobHandle ScheduleChunkRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null, World world = null)
            where T : struct, IJobChunk
            => ScheduleChunkNativeCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap: workerCap, rangeSize: rangeSize, world: world);

        public static NativeJobHandle ScheduleEntityRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null, World world = null)
            where T : struct
            => ScheduleNativeChunkRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize, world: world);

        public static NativeJobHandle ScheduleEntityRangeRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null, World world = null)
            where T : struct
            => ScheduleNativeChunkRangeRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize, world: world);

        public static NativeJobHandle ScheduleChunkEntityBatchRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null, World world = null)
            where T : struct, IJobChunk
            => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize, jobKind: NativeEcsJobKind.Chunk, world: world);

        private static JobHandle ScheduleChunkCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, ChunkScheduleMode? forcedMode = null, int workerCap = 0, int rangeSize = 0, ComponentType[]? writtenComponents = null, World world = null)
            where T : struct, IJobChunk
        {
            // ─── Managed fallback（NativeDll 不可用时）：�?C# 路径，无回调 ───
            if (NativeJobScheduler.UseFallback)
                return ScheduleChunkManagedFallback(ref job, entityManager, query, writtenComponents);

            // ─── C++ 路径 ───
            var result = ScheduleChunkNativeCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, forcedMode, workerCap, rangeSize, writtenComponents, world);
            return new JobHandle(result);
        }

        private static NativeJobHandle ScheduleChunkNativeCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, ChunkScheduleMode? forcedMode = null, int workerCap = 0, int rangeSize = 0, ComponentType[]? writtenComponents = null, World world = null)
            where T : struct, IJobChunk
        {
            // 多 World 支持：绑定本次调度的 World
            world ??= World.DefaultWorld;
            // ─── Event Buffer: 按需分配 ───
            Type jobType = typeof(T);
            List<EventBufferHeader>? evtHeaders = null;
            List<IDisposable>? evtDisposables = null;
            if (EventMetaCache.TryGetValue(jobType, out var evtMeta) && evtMeta.Count > 0)
            {
                (evtHeaders, evtDisposables) = AllocateEventBuffers(jobType);
            }

            var allEnabledTypes = query.AllEnabled;
            bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            bool canUseRawCache = funcPtr != IntPtr.Zero &&
                                  !hasEnabledFilter;
            if (canUseRawCache &&
                TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) &&
                rawCache.ChunkCount > 0)
            {
                var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
                var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease, eventBufferHeaders: evtHeaders, world: world);
                if (evtHeaders != null) StoreLiveEventBuffers(rawContextBlock, evtHeaders, evtDisposables,
                    EventMetaCache.TryGetValue(jobType, out var rawMeta) ? rawMeta.EventTypes : null);
                try
                {
                    using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                    IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, NativeChunkJobs.ChunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, mode, workerCap, rangeSize, (uint)rawCache.StructuralVersion);
                    NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                    return TrackEntityJob(entityManager, FromNative(handle), rawCache.MatchingArchetypes, writtenComponents);
                }
                catch { NativeChunkJobs.ChunkCleanup(rawContextBlock); throw; }
            }

            bool jobHasManagedReferences = NativeJobCore.JobHasManagedReferences<T>();

            // 托管路径（唯一）：区间回调，blittable �?job blob、托管引�?job �?GCHandle box
            if (funcPtr == IntPtr.Zero &&
                TryGetManagedChunkScheduleCache(entityManager, query, out var managedCache, out var managedCacheLease) &&
                managedCache.ChunkCount > 0)
            {
                var managedContextBlock = CreateChunkContextBlock(ref job, managedCache.ChunksPtr, managedCache.ChunkCount, hasEnabledFilter, allEnabledTypes, -1, false, null, managedCacheLease, jobBoxed: jobHasManagedReferences, chunkArray: managedCache.ManagedChunkArray);
                try
                {
                    var cache = NativeJobCore.GetOrCreateDelegateCache<T, ChunkRangeJobFuncDelegate>(() => ChunkJobCallbacks.CreateChunkRangeCallback<T>());
                    using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                    IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkRangeJobEx(cache.FuncPtr, managedContextBlock, NativeChunkJobs.ChunkCleanupPtr, managedCache.ChunksPtr, managedCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, (uint)managedCache.StructuralVersion);
                    NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                    return TrackEntityJob(entityManager, FromNative(handle), managedCache.MatchingArchetypes, writtenComponents);
                }
                catch { NativeChunkJobs.ChunkCleanup(managedContextBlock); throw; }
            }

            return default;
        }

        private static NativeJobHandle ScheduleNativeChunkRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize, World world = null)
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
                    IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, NativeChunkJobs.ChunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, (uint)rawCache.StructuralVersion);
                    NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                    return TrackEntityJob(entityManager, FromNative(handle), rawCache.MatchingArchetypes);
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
                IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, 0);
                NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                return TrackEntityJob(entityManager, FromNative(handle), fallbackMatchingArchetypes.ToArray());
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
        /// 填充 ChunkJobData 数组：组件指�?大小/位图/类型索引 + requiredComponentTypeIds 对应指针�?
        /// gcHandles �?null �?chunkHandle �?IntPtr.Zero（纯原生回调）；否则�?GCHandle[] 取句柄（托管回调）�?
        /// 调用方负责释放各 chunk 分配�?compPtrs/compSizes/bitmaps/typeIndices/requiredArrays�?
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
                    requiredComponentCount = requiredCount,
                    sharedValuePtrs = FillSharedValuePtrs(chunk, arch, compCount),
                    sharedValueCount = chunk.HasSharedValues ? CountBlittableShared(arch, compCount) : 0
                };
            }
        }

        // ======================== Shared values 支持（per-chunk） ========================

        /// <summary>统计 arch 中 blittable shared 组件数量（managed shared 不传指针到 C++ 侧）。</summary>
        private static int CountBlittableShared(Archetype arch, int compCount)
        {
            int count = 0;
            for (int c = 0; c < compCount; c++)
                if (arch.Types[c].IsShared && !arch.Types[c].IsManagedShared)
                    count++;
            return count;
        }

        /// <summary>
        /// 收集 blittable shared 组件的 chunk 内联值指针（per-chunk，非 per-entity）。
        /// 返回非 null 的 void** 数组（调用方不负责释放——跟随 ChunkJobData 生命周期由 NativeChunkJobs 释放）。
        /// </summary>
        private static unsafe void** FillSharedValuePtrs(Chunk chunk, Archetype arch, int compCount)
        {
            if (!chunk.HasSharedValues) return null;
            int sharedCount = CountBlittableShared(arch, compCount);
            if (sharedCount == 0) return null;
            var ptrs = (void**)Marshal.AllocHGlobal(sharedCount * sizeof(void*));
            int si = 0;
            for (int c = 0; c < compCount; c++)
            {
                if (!arch.Types[c].IsShared || arch.Types[c].IsManagedShared) continue;
                ptrs[si++] = (void*)chunk.GetSharedValuePointer(c);
            }
            return ptrs;
        }

        private static NativeJobHandle ScheduleNativeChunkRangeRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize, ChunkScheduleMode? forcedMode = null, World world = null)
            where T : struct
        {
            if (funcPtr == IntPtr.Zero)
                throw new ArgumentException("Native chunk range raw scheduling requires a function pointer.", nameof(funcPtr));

            // 多 World 支持：绑定本次调度的 World（drain 写回正确 EventStream）
            world ??= World.DefaultWorld;

            // ─── Event Buffer: 按需分配（ISPC IJobChunk SendEvent 路径） ───
            Type evtJobType = typeof(T);
            List<EventBufferHeader>? evtHeaders = null;
            List<IDisposable>? evtDisposables = null;
            if (EventMetaCache.TryGetValue(evtJobType, out var evtMeta) && evtMeta.Count > 0)
                (evtHeaders, evtDisposables) = AllocateEventBuffers(evtJobType);

            var allEnabledTypes = query.AllEnabled;
            bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
            if (!hasEnabledFilter &&
                TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) &&
                rawCache.ChunkCount > 0)
            {
                var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease, eventBufferHeaders: evtHeaders, world: world);
                if (evtHeaders != null) StoreLiveEventBuffers(rawContextBlock, evtHeaders, evtDisposables,
                    EventMetaCache.TryGetValue(evtJobType, out var rawMeta) ? rawMeta.EventTypes : null);
                try
                {
                    using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                    return TrackEntityJob(entityManager, FromNative(NativeChunkJobs.JobSystem_ScheduleChunkRangeJobEx(funcPtr, rawContextBlock, NativeChunkJobs.ChunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, mode, workerCap, rangeSize, (uint)rawCache.StructuralVersion)), rawCache.MatchingArchetypes);
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

            var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, true, requiredComponentTypeIds, eventBufferHeaders: evtHeaders, world: world);
            if (evtHeaders != null) StoreLiveEventBuffers(contextBlock, evtHeaders, evtDisposables,
                EventMetaCache.TryGetValue(evtJobType, out var fbMeta) ? fbMeta.EventTypes : null);
            try
            {
                using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                return TrackEntityJob(entityManager, FromNative(NativeChunkJobs.JobSystem_ScheduleChunkRangeJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, mode, workerCap, rangeSize, 0)), fallbackMatchingArchetypes.ToArray());
            }
            catch { NativeChunkJobs.ChunkCleanup(contextBlock); throw; }
        }

        private static NativeJobHandle ScheduleNativeEntityBatchRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize, bool useScheduleAndComplete = false, ChunkScheduleMode? forcedMode = null, NativeEcsJobKind jobKind = NativeEcsJobKind.Entity, World world = null)
            where T : struct
        {
            if (funcPtr == IntPtr.Zero)
                throw new ArgumentException("Native entity batch raw scheduling requires a function pointer.", nameof(funcPtr));

            // 多 World 支持：绑定本次调度的 World（drain 写回正确 EventStream）
            world ??= World.DefaultWorld;

            // ── 临时诊断（ENTJOY_DIAG_CSHARP_PHASE=1）：C# 调度侧四段细分计�?──
            bool cDiag = s_csharpPhaseDiag;
            long d0 = cDiag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            long d1 = 0, d2 = 0, d3 = 0;

            var allEnabledTypes = query.AllEnabled;
            bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            if (hasEnabledFilter)
                throw new NotSupportedException("Native IJobEntity DirectEntityBatch does not support AllEnabled filters yet.");

            if (!TryGetEntityBatchScheduleCache(entityManager, query, requiredComponentTypeIds, out var cache, out var cacheLease) ||
                cache.BatchCount == 0)
                return default;
            d1 = cDiag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

            // ─── Event Buffer: 按需分配 ───
            Type evtJobType = typeof(T);
            List<EventBufferHeader>? evtHeaders = null;
            List<IDisposable>? evtDisposables = null;
            if (EventMetaCache.TryGetValue(evtJobType, out var evtMeta) && evtMeta.Count > 0)
                (evtHeaders, evtDisposables) = AllocateEventBuffers(evtJobType);

            var contextBlock = CreateChunkContextBlock(ref job, null, cache.BatchCount, false, null, -1, false, requiredComponentTypeIds, cacheLease, eventBufferHeaders: evtHeaders, world: world);
            if (evtHeaders != null) StoreLiveEventBuffers(contextBlock, evtHeaders, evtDisposables,
                    EventMetaCache.TryGetValue(evtJobType, out var batchMeta) ? batchMeta.EventTypes : null);
            d2 = cDiag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
                var handle = useScheduleAndComplete
                    ? NativeChunkJobs.JobSystem_ScheduleAndCompleteEntityBatchJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, mode, workerCap, rangeSize, jobKind, (uint)cache.StructuralVersion)
                    : NativeChunkJobs.JobSystem_ScheduleEntityBatchJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, mode, workerCap, rangeSize, jobKind, (uint)cache.StructuralVersion);
                d3 = cDiag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                // ─── Event Buffer Drain ───
                if (useScheduleAndComplete && evtHeaders != null)
                {
                    // 同步路径：执行完成后立即 drain
                    DrainAndFreeEventBuffers(contextBlock, world, evtJobType);
                }
                else if (evtHeaders != null)
                {
                    // 异步路径：注册到本 World 的 EntityManager，Complete 时统一 drain
                    entityManager._pendingNativeEvents.Add((contextBlock, evtJobType));
                }
                var ret = TrackEntityJob(entityManager, FromNative(handle));
                if (cDiag && s_csharpPhaseCount++ < 24)
                    Console.WriteLine($"[CPHS] cache+hash={us(d1 - d0):F1} us  contextBlock={us(d2 - d1):F1} us  PInvoke={us(d3 - d2):F1} us  track+return={us(System.Diagnostics.Stopwatch.GetTimestamp() - d3):F1} us");
                return ret;
            }
            catch { NativeChunkJobs.ChunkCleanup(contextBlock); throw; }

            static double us(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static readonly bool s_csharpPhaseDiag =
            Environment.GetEnvironmentVariable("ENTJOY_DIAG_CSHARP_PHASE") == "1";
        private static uint s_csharpPhaseCount = 0;

        /// <summary>
        /// 同步执行 [NativeTranspile] IJobChunk：以 ImmediateNative 模式提交（C++ 侧主线程直接执行�?
        /// �?worker 唤醒），并一步完成（无需显式 Complete）。等价于 Run 的零调度开销版本�?
        /// </summary>
        public static void RunChunkImmediate<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, World world = null)
            where T : struct
        {
            ScheduleNativeEntityBatchRawCore(
                ref job, entityManager, query, funcPtr, requiredComponentTypeIds,
                null, workerCap: 0, rangeSize: 0,
                useScheduleAndComplete: true, forcedMode: ChunkScheduleMode.ImmediateNative,
                jobKind: NativeEcsJobKind.Chunk, world: world);
        }

        /// <summary>
        /// 同步执行 [NativeTranspile] IJobChunk（ISPC 后端）：�?ChunkRange 路径�?ImmediateNative 提交�?
        /// </summary>
        public static void RunChunkRangeImmediate<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, World world = null)
            where T : struct
        {
            ScheduleNativeChunkRangeRawCore(
                ref job, entityManager, query, funcPtr, requiredComponentTypeIds,
                null, workerCap: 0, rangeSize: 0,
                forcedMode: ChunkScheduleMode.ImmediateNative, world: world);
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
                    requiredComponentCount = requiredCount,
                    sharedValuePtrs = FillSharedValuePtrs(chunk, archetype, componentCount),
                    sharedValueCount = chunk.HasSharedValues ? CountBlittableShared(archetype, componentCount) : 0
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

            // 托管路径：chunkId（整数）+ �?GCHandle 保活 Chunk[]，不给每 chunk GCHandle
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
                    requiredComponentCount = 0,
                    sharedValuePtrs = FillSharedValuePtrs(chunk, chunk.Archetype, componentCount),
                    sharedValueCount = chunk.HasSharedValues ? CountBlittableShared(chunk.Archetype, componentCount) : 0
                };
            }

            var cache = new RawChunkScheduleCache(entityManager.StructuralVersion, chunksPtr, chunkCount, false, matchingArchetypes.ToArray());
            cache.ManagedChunkArray = chunkArray;  // 直接持有引用（无 GCHandle�?
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
            internal Chunk[]? ManagedChunkArray;  // 托管路径：直接持�?Chunk[]（无 GCHandle�?
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
                // ManagedChunkArray �?GCHandle，由引用计数 GC 管理
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

        // ======================== Event Buffer 注册 / 分配 / Drain ========================

        /// <summary>
        /// 注册 NativeTranspile Job 的事件类型元数据（由 BindingsGenerator 在启动时调用）。
        /// </summary>
        public static void RegisterEventBufferMeta(Type jobType, Type[] eventTypes)
        {
            EventMetaCache[jobType] = new EventBufferMeta { Count = eventTypes.Length, EventTypes = eventTypes };
        }

        /// <summary>
        /// 为指定 Job 类型分配 EventBuffer 并返回 headers 列表。
        /// </summary>
        internal static (List<EventBufferHeader> headers, List<IDisposable> disposables) AllocateEventBuffers(Type jobType)
        {
            var headers = new List<EventBufferHeader>();
            var disposables = new List<IDisposable>();
            if (!EventMetaCache.TryGetValue(jobType, out var meta) || meta.Count == 0)
                return (headers, disposables);

            for (int i = 0; i < meta.Count; i++)
            {
                int elemSize = System.Runtime.InteropServices.Marshal.SizeOf(meta.EventTypes[i]);
                // ISPC 后端以 int 槽位（4B 对齐）写事件 buffer（stride = sizeof(uniform T)，C ABI 对齐，
                // 与 C# Sequential 一致）。非 4B 对齐的字段（double/bool/long）会破坏 int 槽位前提 →
                // 编译期/运行时立即失败，而不是静默错位。
                if ((elemSize & 3) != 0)
                    throw new InvalidOperationException(
                        $"Event type {meta.EventTypes[i].FullName} has Marshal.SizeOf {elemSize}, " +
                        $"not divisible by 4. ISPC EventBuffer requires 4-byte-aligned blittable layout " +
                        $"(no double/bool/long fields).");
                var dataPtr = Marshal.AllocHGlobal(1024 * elemSize);
                var countPtr = Marshal.AllocHGlobal(sizeof(int));
                *(int*)countPtr = 0;
                headers.Add(new EventBufferHeader
                {
                    dataPtr = dataPtr,
                    countPtr = countPtr,
                    capacity = 1024,
                    elementSize = elemSize
                });
            }
            return (headers, disposables);
        }

        /// <summary>
        /// Drain EventBuffer → EventStream，然后释放非托管内存。
        /// </summary>
        internal static void DrainAndFreeEventBuffers(IntPtr contextPtr, World world, Type jobType)
        {
            LiveEventBufferDisposables.TryRemove(contextPtr, out var _);
            if (!LiveEventBuffers.TryRemove(contextPtr, out var headers)) return;
            if (!EventMetaCache.TryGetValue(jobType, out var meta)) return;

            // world 由参数传入（调度时用户显式指定，或 DefaultWorld）
            if (world == null) world = World.DefaultWorld;
            if (world == null) return;

            // 释放独立分配的指针数组（eventBufferHeaders → __EntJoyEventBuffer*[]）
            unsafe
            {
                var ctxHeader = (ChunkContextHeader*)contextPtr;
                if (ctxHeader->eventBufferHeaders != IntPtr.Zero)
                {
                    var ptrArr = (IntPtr*)ctxHeader->eventBufferHeaders;
                    int bufCount = ctxHeader->eventBufferCount;
                    for (int i = 0; i < bufCount; i++)
                    {
                        if (ptrArr[i] != IntPtr.Zero) Marshal.FreeHGlobal(ptrArr[i]);
                    }
                    Marshal.FreeHGlobal(ctxHeader->eventBufferHeaders);
                    ctxHeader->eventBufferHeaders = IntPtr.Zero;
                }
                // 释放 World GCHandle 并清除，避免 ChunkCleanup 二次 drain/释放
                if (ctxHeader->eventWorldHandle != IntPtr.Zero)
                {
                    try { GCHandle.FromIntPtr(ctxHeader->eventWorldHandle).Free(); } catch { }
                    ctxHeader->eventWorldHandle = IntPtr.Zero;
                }
            }

            for (int i = 0; i < headers.Count && i < meta.Count; i++)
            {
                var hdr = headers[i];
                int count = Math.Min(Volatile.Read(ref *(int*)hdr.countPtr), hdr.capacity);
                if (count > 0 && meta.EventTypes[i] != null)
                {
                    var stream = world.GetEventStream(meta.EventTypes[i]);
                    stream?.DrainFromBuffer((void*)hdr.dataPtr, count, hdr.elementSize);
                }
                if (hdr.dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdr.dataPtr);
                if (hdr.countPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdr.countPtr);
            }
        }

        /// <summary>存储活跃 EventBuffer（调度后、Complete 前，供 drain 释放）。</summary>
        internal static void StoreLiveEventBuffers(IntPtr contextPtr, List<EventBufferHeader>? headers, List<IDisposable>? disposables, Type[]? eventTypes = null)
        {
            if (headers != null && headers.Count > 0)
            {
                LiveEventBuffers[contextPtr] = headers;
                if (eventTypes != null) LiveEventBufferTypes[contextPtr] = eventTypes;
            }
            if (disposables != null && disposables.Count > 0)
                LiveEventBufferDisposables[contextPtr] = disposables;
        }

        /// <summary>
        /// ChunkCleanup 回调调用的自动 drain：C++ 执行完自动回读事件。
        /// 从 LiveEventBufferTypes 取事件类型 → drain 到 World.EventStream → 释放 buffer 内存。
        /// </summary>
        internal static void DrainEventBuffersFromCleanup(IntPtr contextPtr, World world)
        {
            LiveEventBufferDisposables.TryRemove(contextPtr, out var _);
            if (!LiveEventBuffers.TryRemove(contextPtr, out var headers)) return;
            if (!LiveEventBufferTypes.TryRemove(contextPtr, out var eventTypes)) return;

            for (int i = 0; i < headers.Count && i < eventTypes.Length; i++)
            {
                var hdr = headers[i];
                int count = Math.Min(Volatile.Read(ref *(int*)hdr.countPtr), hdr.capacity);
                if (count > 0 && eventTypes[i] != null)
                {
                    var stream = world.GetEventStream(eventTypes[i]);
                    stream?.DrainFromBuffer((void*)hdr.dataPtr, count, hdr.elementSize);
                }
                if (hdr.dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdr.dataPtr);
                if (hdr.countPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdr.countPtr);
            }
        }

        // ======================== 上下文块创建 / 清理 ========================
        private unsafe static IntPtr CreateChunkContextBlock<T>(ref T job, ChunkJobData* chunksPtr, int chunkCount, bool hasEnabledFilter, ComponentType[] allEnabledTypes, int gcHandleStartIndex, bool ownsChunkData, int[] requiredComponentTypeIds = null, IDisposable cacheLease = null, bool jobBoxed = false, Chunk[]? chunkArray = null, List<EventBufferHeader>? eventBufferHeaders = null, World world = null) where T : struct
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
            // 托管回调�?Chunk[] 通过 ChunkArrayTable 存储（按 context 指针索引，零 GCHandle�?
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
            // ─── Event Buffer Headers ───
            if (eventBufferHeaders != null && eventBufferHeaders.Count > 0)
            {
                // C++ 侧期望 eventBufferHeaders 是 __EntJoyEventBuffer*[]（指针数组），
                // 每个元素指向独立的 EventBufferHeader（新分配，避免结构数组布局歧义）。
                int ptrArraySize = eventBufferHeaders.Count * sizeof(IntPtr);
                var ptrArrayBlock = Marshal.AllocHGlobal(ptrArraySize);
                var ptrArr = (IntPtr*)ptrArrayBlock;
                for (int i = 0; i < eventBufferHeaders.Count; i++)
                {
                    var hdrPtr = Marshal.AllocHGlobal(sizeof(EventBufferHeader));
                    *(EventBufferHeader*)hdrPtr = eventBufferHeaders[i];
                    ptrArr[i] = hdrPtr;
                }
                header->eventBufferCount = eventBufferHeaders.Count;
                header->eventBufferHeaders = ptrArrayBlock;
                // 绑定 World GCHandle：cleanup 时自动 drain 到正确的 EventStream
                if (world != null)
                    header->eventWorldHandle = GCHandle.ToIntPtr(GCHandle.Alloc(world, GCHandleType.Normal));
                else
                    header->eventWorldHandle = IntPtr.Zero;
            }
            else
            {
                header->eventBufferCount = 0;
                header->eventBufferHeaders = IntPtr.Zero;
                header->eventWorldHandle = IntPtr.Zero;
            }
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

        // ======================== Managed fallback（NativeDll 不可用时�?========================
        /// <summary>
        /// Managed fallback：收�?chunks �?ManagedChunkParallelJob<T> �?ManagedJobScheduler.ScheduleParallelFor�?
        /// �?C# 路径，无 P/Invoke、无 ChunkJobCallbacks、无 ContextBlock�?
        /// </summary>
        private static JobHandle ScheduleChunkManagedFallback<T>(ref T job, EntityManager entityManager,
            QueryBuilder query, ComponentType[]? writtenComponents)
            where T : struct, IJobChunk
        {
            var allEnabledTypes = query.AllEnabled;
            ChunkJobCollector.CollectAndBuildManaged(entityManager, query, fillBitmaps: true, hasEnabledFilter: true,
                out var ptr, out var chunkArray, out var chunkCount, out var archetypes);
            if (chunkCount == 0) return default;

            var parallelJob = new ManagedChunkParallelJob<T>
            {
                Job = job, Chunks = chunkArray, ChunkCount = chunkCount, AllEnabledTypes = allEnabledTypes
            };
            var mhandle = JobScheduler.ScheduleParallelFor(ref parallelJob, chunkCount, innerBatchCount: 1);
            // 托管路径也需�?TrackEntityJob：用�?Selective Wait（结构变更时只等影响�?archetype �?job�?
            TrackEntityJob(entityManager, mhandle, archetypes, writtenComponents);
            return mhandle;
        }

        /// <summary>托管调度 payload：直接在 ManagedWorker 上执行，�?P/Invoke 回调。实�?IJobParallelFor�?/summary>
        internal unsafe struct ManagedChunkParallelJob<T> : IJobParallelFor where T : struct, IJobChunk
        {
            public T Job;
            public Chunk[] Chunks;
            public int ChunkCount;
            public ComponentType[]? AllEnabledTypes;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Execute(int index)
            {
                if ((uint)index >= (uint)ChunkCount) return;
                var chunk = Chunks[index];
                if (chunk.EntityCount == 0) return;
                var mask = (AllEnabledTypes?.Length > 0)
                    ? ChunkJobScheduler.ComputeChunkMask(chunk, AllEnabledTypes)
                    : default;
                Job.Execute(new ArchetypeChunk(chunk), mask);
            }
        }

        // ======================== Run 路径（主线程同步执行�?========================
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

        // ======================== Phase C：共�?mask 工具（单一真值来源） ========================
        /// <summary>
        /// �?chunk �?enabled 组件的位图做 AND，返回组合掩码（null = 全禁用或无过滤）�?
        /// 单组件零拷贝（直传位图），多组件走�?ulong AND�?
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
