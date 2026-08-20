using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using EntJoy;

namespace EntJoy.JobSystem
{
    /// <summary>
    /// NativeJobScheduler 的 ECS 扩展部分（partial class）。
    /// 包含所有依赖 Chunk/ComponentType/EntityManager/QueryBuilder 的调度方法。
    /// 与 NativeJobScheduler 在同一 assembly（ECS）。
    /// </summary>
    public static unsafe partial class NativeJobScheduler
    {
    private static NativeJobHandle TrackEntityJob(EntityManager entityManager, NativeJobHandle handle)
    {
        entityManager?.RegisterActiveJob(handle);
        return handle;
    }

    // ======================== IJobChunk 调度 ========================
    internal static readonly object _rawChunkScheduleCacheLock = new();
    internal static readonly Dictionary<RawChunkScheduleCacheKey, RawChunkScheduleCache> _rawChunkScheduleCaches = new();
    internal static readonly Dictionary<RawChunkScheduleCacheKey, ManagedChunkScheduleCache> _managedChunkScheduleCaches = new();
    internal static readonly Dictionary<RawChunkScheduleCacheKey, EntityBatchScheduleCache> _entityBatchScheduleCaches = new();
    internal static readonly ConcurrentDictionary<IntPtr, GCHandle> _chunkContextLeases = new();

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
            foreach (var pair in _managedChunkScheduleCaches)
            {
                if (pair.Key.Matches(entityManager))
                {
                    keysToRemove.Add(pair.Key);
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
            {
                _managedChunkScheduleCaches.Remove(keysToRemove[i]);
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

    public static NativeJobHandle ScheduleChunk<T>(ref T job, EntityManager entityManager, QueryBuilder query, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleChunkCore(ref job, entityManager, query, IntPtr.Zero, null, dependsOn);

    public static NativeJobHandle ScheduleChunkWithWorkerCap<T>(ref T job, EntityManager entityManager, QueryBuilder query, int workerCap, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleChunkCore(ref job, entityManager, query, IntPtr.Zero, null, dependsOn, workerCap: workerCap);

    public static NativeJobHandle ScheduleChunkRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleChunkCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn);

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

    /// <summary>
    /// IJobEntity ISPC 轻量调度：跳过 entity tracking + query cache，
    /// 直接迭代 archetype chunk 构建 ChunkJobData 并调度。
    /// </summary>
    public static NativeJobHandle ScheduleEntityBatchRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
        where T : struct
        => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize);

    public static NativeJobHandle ScheduleChunkEntityBatchRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize, jobKind: NativeEcsJobKind.Chunk);

    /// <summary>
    /// Schedule + Complete 一步完成，消除一次 P/Invoke 往返和 handle boxing 开销。
    /// 适用于基准测试和一次性同步 job。
    /// </summary>
    public static NativeJobHandle ScheduleAndCompleteEntityBatchRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap = 0, int rangeSize = 0)
        where T : struct
        => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, null, workerCap, rangeSize, useScheduleAndComplete: true);

    public static void RunChunkRawImmediate<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds)
        where T : struct, IJobChunk
    {
        var handle = ScheduleChunkCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, null, ChunkScheduleMode.ImmediateNative);
        Complete(ref handle);
    }

    public static void RunEntityRawImmediate<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds)
        where T : struct
    {
        var handle = ScheduleNativeChunkRawImmediateCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds);
        Complete(ref handle);
    }

    private static NativeJobHandle ScheduleChunkCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, ChunkScheduleMode? forcedMode = null, int workerCap = 0, int rangeSize = 0)
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
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                IntPtr h1268 = JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, mode, workerCap, rangeSize);
                RegisterScheduledJobName(h1268, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(h1268));
            }
            catch { ChunkCleanup(rawContextBlock); throw; }
        }

        bool jobHasManagedReferences = JobHasManagedReferences<T>();

        if (funcPtr == IntPtr.Zero &&
            !jobHasManagedReferences &&
            TryGetManagedChunkScheduleCache(entityManager, query, out var csharpRawCache, out var csharpRawCacheLease) &&
            csharpRawCache.ChunkCount > 0)
        {
            var csharpRawContextBlock = CreateChunkContextBlock(ref job, csharpRawCache.ChunksPtr, csharpRawCache.ChunkCount, hasEnabledFilter, allEnabledTypes, -1, false, null, csharpRawCacheLease);
            try
            {
                var cache = GetOrCreateDelegateCache<T, ChunkRangeJobFuncDelegate>(() => CreateChunkRangeCallback<T>());
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                IntPtr h1285 = JobSystem_ScheduleChunkRangeJobEx(cache.FuncPtr, csharpRawContextBlock, _chunkCleanupPtr, csharpRawCache.ChunksPtr, csharpRawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
                RegisterScheduledJobName(h1285, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(h1285));
            }
            catch { ChunkCleanup(csharpRawContextBlock); throw; }
        }

        if (funcPtr == IntPtr.Zero &&
            TryGetManagedChunkArrayCache(entityManager, query, out var managedCache) &&
            managedCache.Chunks.Length > 0)
        {
            var managedContextBlock = jobHasManagedReferences
                ? AllocManagedChunkBatchContext(ref job, managedCache.Chunks, allEnabledTypes)
                : AllocRawChunkBatchContext(ref job, managedCache.Chunks, allEnabledTypes);
            try
            {
                var cache = GetOrCreateDelegateCache<T, BatchJobFunc>(() => CreateChunkArrayBatchCallback<T>());
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                IntPtr h1301 = JobSystem_ScheduleParallelForBatch(cache.FuncPtr, managedContextBlock, jobHasManagedReferences ? _managedCleanupPtr : _rawChunkBatchCleanupPtr, managedCache.Chunks.Length, -1, dependencyLease.Handle);
                RegisterScheduledJobName(h1301, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(h1301));
            }
            catch
            {
                if (jobHasManagedReferences) ManagedCleanup(managedContextBlock);
                else RawChunkBatchCleanup(managedContextBlock);
                throw;
            }
        }

        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var arch = entityManager.Archetypes[i];
            if (arch != null && arch.IsMatch(query))
            {
                foreach (var c in arch.GetChunks())
                    if (c.EntityCount > 0) chunkList.Add(c);
            }
        }

        int chunkCount = chunkList.Count;
        if (chunkCount == 0) return default;

        var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));

        // 原生 adapter 回调只读 ChunkJobData 原始指针，无需恢复托管 Chunk 对象 → 不装箱。
        // 仅托管回调（funcPtr == 0）需要 GCHandle 恢复 chunk 对象。
        bool nativeCallback = funcPtr != IntPtr.Zero;
        var gcHandles = nativeCallback ? null : new GCHandle[chunkCount];
        int gcHandleStartIndex = -1;
        if (!nativeCallback)
        {
            // 先预分配所有 GCHandle（无锁安全），再原子性加入列表
            for (int ci = 0; ci < chunkCount; ci++)
                gcHandles![ci] = GCHandle.Alloc(chunkList[ci], GCHandleType.WeakTrackResurrection);
            lock (_chunkGCHandlesLock)
            {
                gcHandleStartIndex = _chunkGCHandles.Count;
                for (int ci = 0; ci < chunkCount; ci++)
                    _chunkGCHandles.Add(gcHandles[ci]);
            }
        }

        var contextBlock = IntPtr.Zero;
        try
        {
            for (int ci = 0; ci < chunkCount; ci++)
            {
                var chunk = chunkList[ci];
                var arch = chunk.Archetype;

                int compCount = chunk.ComponentCount;
                var compPtrs = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
                var compSizes = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
                var bitmaps = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
                var typeIndices = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
                void** requiredArrays = null;
                int requiredCount = requiredComponentTypeIds?.Length ?? 0;
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
                        int requiredTypeId = requiredComponentTypeIds[r];
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
                    chunkHandle = nativeCallback ? IntPtr.Zero : (IntPtr)gcHandles![ci],
                    requiredComponentArrays = requiredArrays,
                    requiredComponentCount = requiredCount
                };
            }

            contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, true, requiredComponentTypeIds);

            IntPtr callbackPtr = funcPtr;
            if (callbackPtr == IntPtr.Zero)
            {
                var cache = GetOrCreateDelegateCache<T, ChunkJobFuncDelegate>(() => CreateChunkCallback<T>());
                callbackPtr = cache.FuncPtr;
            }
            var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            IntPtr h1415 = JobSystem_ScheduleChunkJobEx(callbackPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, mode, workerCap, rangeSize);
            RegisterScheduledJobName(h1415, typeof(T).Name);
            return TrackEntityJob(entityManager, new NativeJobHandle(h1415));
        }
        catch
        {
            if (contextBlock != IntPtr.Zero)
            {
                ChunkCleanup(contextBlock);
            }
            else
            {
                // 分配循环未完成：部分清理 per-chunk 分配和 chunksPtr
                if (chunksPtr != null)
                {
                    for (int ci = 0; ci < chunkCount; ci++)
                    {
                        var cd = chunksPtr[ci];
                        if (cd.componentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.componentArrays);
                        if (cd.componentSizes != null) Marshal.FreeHGlobal((IntPtr)cd.componentSizes);
                        if (cd.enableBitMaps != null) Marshal.FreeHGlobal((IntPtr)cd.enableBitMaps);
                        if (cd.componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)cd.componentTypeIndices);
                        if (cd.requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.requiredComponentArrays);
                    }
                    Marshal.FreeHGlobal((IntPtr)chunksPtr);
                }
                if (gcHandles != null)
                    foreach (var gch in gcHandles)
                        if (gch.IsAllocated) gch.Free();
                // 注：GCHandle 已释放，但对应 slot 仍在 _chunkGCHandles 中。
                // 异常路径罕见，孤立条目可接受；正常路径的尾压实可回收尾部段落。
            }
            throw;
        }
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
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                IntPtr h1465 = JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
                RegisterScheduledJobName(h1465, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(h1465));
            }
            catch { ChunkCleanup(rawContextBlock); throw; }
        }

        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var arch = entityManager.Archetypes[i];
            if (arch != null && arch.IsMatch(query))
            {
                foreach (var c in arch.GetChunks())
                    if (c.EntityCount > 0) chunkList.Add(c);
            }
        }

        int chunkCount = chunkList.Count;
        if (chunkCount == 0) return default;

        var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
        // 原生 adapter 回调只读 ChunkJobData 原始指针，无需恢复托管 Chunk 对象 → 不装箱（无 GCHandle）。
        const int gcHandleStartIndex = -1;

        for (int ci = 0; ci < chunkCount; ci++)
        {
            var chunk = chunkList[ci];
            var arch = chunk.Archetype;

            int compCount = chunk.ComponentCount;
            var compPtrs = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var compSizes = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
            var bitmaps = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var typeIndices = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
            void** requiredArrays = null;
            int requiredCount = requiredComponentTypeIds?.Length ?? 0;
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
                    int requiredTypeId = requiredComponentTypeIds[r];
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
                chunkHandle = IntPtr.Zero,
                requiredComponentArrays = requiredArrays,
                requiredComponentCount = requiredCount
            };
        }

        var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, true, requiredComponentTypeIds);
        try
        {
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            IntPtr h1549 = JobSystem_ScheduleChunkJobEx(funcPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
            RegisterScheduledJobName(h1549, typeof(T).Name);
            return TrackEntityJob(entityManager, new NativeJobHandle(h1549));
        }
        catch { ChunkCleanup(contextBlock); throw; }
    }

    private static NativeJobHandle ScheduleNativeChunkRangeRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize)
        where T : struct
    {
        if (funcPtr == IntPtr.Zero)
            throw new ArgumentException("Native chunk range raw scheduling requires a function pointer.", nameof(funcPtr));

        var allEnabledTypes = query.AllEnabled;
        bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            if (!hasEnabledFilter &&
            TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) &&
            rawCache.ChunkCount > 0)
        {
            var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease);
            try
            {
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleChunkRangeJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize)));
            }
            catch { ChunkCleanup(rawContextBlock); throw; }
        }

        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var arch = entityManager.Archetypes[i];
            if (arch != null && arch.IsMatch(query))
            {
                foreach (var c in arch.GetChunks())
                    if (c.EntityCount > 0) chunkList.Add(c);
            }
        }

        int chunkCount = chunkList.Count;
        if (chunkCount == 0) return default;

        var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
        // 原生 adapter 回调只读 ChunkJobData 原始指针，无需恢复托管 Chunk 对象 → 不装箱（无 GCHandle）。
        const int gcHandleStartIndex = -1;

        for (int ci = 0; ci < chunkCount; ci++)
        {
            var chunk = chunkList[ci];
            var arch = chunk.Archetype;

            int compCount = chunk.ComponentCount;
            var compPtrs = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var compSizes = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
            var bitmaps = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var typeIndices = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
            void** requiredArrays = null;
            int requiredCount = requiredComponentTypeIds?.Length ?? 0;
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
                    int requiredTypeId = requiredComponentTypeIds[r];
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
                chunkHandle = IntPtr.Zero,
                requiredComponentArrays = requiredArrays,
                requiredComponentCount = requiredCount
            };
        }

        var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, true, requiredComponentTypeIds);
        try
        {
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleChunkRangeJobEx(funcPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize)));
        }
        catch { ChunkCleanup(contextBlock); throw; }
    }

    private static NativeJobHandle ScheduleNativeEntityBatchRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize, bool useScheduleAndComplete = false, NativeEcsJobKind jobKind = NativeEcsJobKind.Entity)
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
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            var handle = useScheduleAndComplete
                ? JobSystem_ScheduleAndCompleteEntityBatchJobEx(funcPtr, contextBlock, _chunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, jobKind)
                : JobSystem_ScheduleEntityBatchJobEx(funcPtr, contextBlock, _chunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, jobKind);
            RegisterScheduledJobName(handle, typeof(T).Name);
            return TrackEntityJob(entityManager, new NativeJobHandle(handle));
        }
        catch { ChunkCleanup(contextBlock); throw; }
    }

    private static NativeJobHandle ScheduleNativeChunkRawImmediateCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds)
        where T : struct
    {
        if (funcPtr == IntPtr.Zero)
            throw new ArgumentException("Native chunk raw immediate requires a function pointer.", nameof(funcPtr));

        if (!TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) ||
            rawCache.ChunkCount == 0)
            return default;

        var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease);
        try
        {
            IntPtr h1699 = JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, IntPtr.Zero, ChunkScheduleMode.ImmediateNative);
            RegisterScheduledJobName(h1699, typeof(T).Name);
            return TrackEntityJob(entityManager, new NativeJobHandle(h1699));
        }
        catch { ChunkCleanup(rawContextBlock); throw; }
    }

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

    private static bool TryGetManagedChunkArrayCache(EntityManager entityManager, QueryBuilder query, out ManagedChunkScheduleCache cache)
    {
        var key = new RawChunkScheduleCacheKey(entityManager, GetQueryHash(query), 0, 2);
        lock (_rawChunkScheduleCacheLock)
        {
            if (_managedChunkScheduleCaches.TryGetValue(key, out cache))
            {
                if (cache.StructuralVersion == entityManager.StructuralVersion)
                {
                    return true;
                }

                _managedChunkScheduleCaches.Remove(key);
            }

            cache = BuildManagedChunkArrayCache(entityManager, query);
            _managedChunkScheduleCaches[key] = cache;
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
                foreach (var chunk in archetype.GetChunks())
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

        // 三次分配替代 per-chunk × N 次分配：
        // 1) EntityBatchData 数组
        var batchesPtr = (EntityBatchData*)Marshal.AllocHGlobal(batchCount * sizeof(EntityBatchData));
        // 2) 所有 componentArrays 指针（连续存储）
        void* componentArraysBlock = null;
        if (requiredCount > 0)
            componentArraysBlock = (void*)Marshal.AllocHGlobal(batchCount * requiredCount * sizeof(void*));
        // 3) 所有 enableBitMaps 指针（连续存储，可选）
        void* enableBitMapsBlock = null;
        if (enableBitmapCount > 0)
            enableBitMapsBlock = (void*)Marshal.AllocHGlobal(batchCount * enableBitmapCount * sizeof(void*));

        for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var chunk = chunkList[batchIndex];
            var archetype = chunk.Archetype;

            // 用偏移量填充连续块，而非每次分配
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

    private static ManagedChunkScheduleCache BuildManagedChunkArrayCache(EntityManager entityManager, QueryBuilder query)
    {
        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var archetype = entityManager.Archetypes[i];
            if (archetype != null && archetype.IsMatch(query))
            {
                foreach (var chunk in archetype.GetChunks())
                {
                    if (chunk.EntityCount > 0)
                    {
                        chunkList.Add(chunk);
                    }
                }
            }
        }

        return new ManagedChunkScheduleCache(entityManager.StructuralVersion, chunkList.ToArray());
    }

    private static RawChunkScheduleCache BuildRawChunkScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds)
    {
        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var archetype = entityManager.Archetypes[i];
            if (archetype != null && archetype.IsMatch(query))
            {
                foreach (var chunk in archetype.GetChunks())
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

        return new RawChunkScheduleCache(entityManager.StructuralVersion, chunksPtr, chunkCount);
    }

    private static RawChunkScheduleCache BuildManagedChunkScheduleCache(EntityManager entityManager, QueryBuilder query)
    {
        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var archetype = entityManager.Archetypes[i];
            if (archetype != null && archetype.IsMatch(query))
            {
                foreach (var chunk in archetype.GetChunks())
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

        var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var chunk = chunkList[chunkIndex];
            var archetype = chunk.Archetype;
            int componentCount = chunk.ComponentCount;
            var componentArrays = (void**)Marshal.AllocHGlobal(componentCount * sizeof(void*));
            var componentSizes = (int*)Marshal.AllocHGlobal(componentCount * sizeof(int));
            var enableBitMaps = (void**)Marshal.AllocHGlobal(componentCount * sizeof(void*));
            var componentTypeIndices = (int*)Marshal.AllocHGlobal(componentCount * sizeof(int));
            var chunkHandle = GCHandle.Alloc(chunk, GCHandleType.Normal);

            for (int componentIndex = 0; componentIndex < componentCount; componentIndex++)
            {
                componentArrays[componentIndex] = (void*)chunk.GetComponentArrayPointer(componentIndex);
                componentSizes[componentIndex] = archetype.Types[componentIndex].Size;
                enableBitMaps[componentIndex] = chunk.GetEnableBitMapPointer(componentIndex);
                componentTypeIndices[componentIndex] = archetype.Types[componentIndex].Id;
            }

            chunksPtr[chunkIndex] = new ChunkJobData
            {
                entityArray = (void*)chunk.GetEntityPointer(),
                entityCount = chunk.EntityCount,
                componentCount = componentCount,
                componentArrays = componentArrays,
                componentSizes = componentSizes,
                enableBitMaps = enableBitMaps,
                componentTypeIndices = componentTypeIndices,
                chunkHandle = GCHandle.ToIntPtr(chunkHandle),
                requiredComponentArrays = null,
                requiredComponentCount = 0
            };
        }

        return new RawChunkScheduleCache(entityManager.StructuralVersion, chunksPtr, chunkCount, true);
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
        private int _leaseCount;
        private int _retired;
        private int _disposed;

        public RawChunkScheduleCache(int structuralVersion, ChunkJobData* chunksPtr, int chunkCount, bool ownsChunkHandles = false)
        {
            StructuralVersion = structuralVersion;
            ChunksPtr = chunksPtr;
            ChunkCount = chunkCount;
            OwnsChunkHandles = ownsChunkHandles;
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

    internal sealed class ManagedChunkScheduleCache
    {
        public readonly int StructuralVersion;
        public readonly Chunk[] Chunks;

        public ManagedChunkScheduleCache(int structuralVersion, Chunk[] chunks)
        {
            StructuralVersion = structuralVersion;
            Chunks = chunks;
        }
    }

    internal sealed class EntityBatchScheduleCache : IDisposable
    {
        public readonly int StructuralVersion;
        public readonly EntityBatchData* BatchesPtr;
        public readonly int BatchCount;
        private void* _componentArraysBlock;  // 批量分配的 componentArrays（可为 null）
        private void* _enableBitMapsBlock;    // 批量分配的 enableBitMaps（可为 null）
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

            // 释放批量分配的块（仅 2-3 次 Free，而非 per-chunk）
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

    // ======================== 内部实现 ========================
    internal static readonly CleanupFunc _chunkCleanup = ChunkCleanup;
    internal static readonly IntPtr _chunkCleanupPtr = Marshal.GetFunctionPointerForDelegate(_chunkCleanup);

    // 显式逐字段写入器注册表：Debug 下 NativeArray 含 GC 引用（DisposeSentinel）→ Job struct 非 blittable，
    // 裸拷贝布局不可靠；NativeTranspiler 为每个 transpiled Job 生成 WriteJobFields_{Job}，静态构造时登记，
    // CreateChunkContextBlock 按类型分发，未登记（非 transpiled / 含不支持字段）回退裸拷贝。
    public unsafe delegate void JobFieldWriter<T>(byte* dst, ref T job) where T : struct;
    internal static readonly Dictionary<Type, Delegate> s_jobFieldWriters = new();

    /// <summary>注册 Job 字段显式写入器（由 NativeTranspiler 生成代码在 NativeExports 静态构造时调用）</summary>
    public static void RegisterJobFieldWriter(Type type, Delegate writer) => s_jobFieldWriters[type] = writer;

    internal static bool TryGetJobFieldWriter(Type type, out Delegate writer) => s_jobFieldWriters.TryGetValue(type, out writer);

    private unsafe static IntPtr CreateChunkContextBlock<T>(ref T job, ChunkJobData* chunksPtr, int chunkCount, bool hasEnabledFilter, ComponentType[] allEnabledTypes, int gcHandleStartIndex, bool ownsChunkData, int[] requiredComponentTypeIds = null, IDisposable cacheLease = null) where T : struct
    {
        int jobSize = Unsafe.SizeOf<T>();
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
        var pooledBlock = ContextPool.Rent(pooledSize);
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
        // 优先走显式逐字段写入器（非 blittable Job struct 裸拷贝布局不可靠）；未登记回退裸拷贝。
        if (TryGetJobFieldWriter(typeof(T), out var __fieldWriter))
        {
            ((JobFieldWriter<T>)__fieldWriter)(jobPtr, ref job);
        }
        else
        {
            Unsafe.CopyBlockUnaligned(jobPtr, Unsafe.AsPointer(ref job), (uint)jobSize);
        }
        if (cacheLease != null)
        {
            _chunkContextLeases[block] = GCHandle.Alloc(cacheLease, GCHandleType.Normal);
        }

        return block;
    }

    private unsafe static void ChunkCleanup(IntPtr contextBlock)
    {
        if (contextBlock == IntPtr.Zero) return;
        var header = (ChunkContextHeader*)contextBlock;
        if (Interlocked.CompareExchange(ref header->cleanupInProgress, 1, 0) != 0) return;
        int chunkCount = header->chunkCount;
        int gcHandleStartIndex = header->gcHandleStartIndex;
        var chunksPtr = (ChunkJobData*)header->chunksPtr;
        bool ownsChunkData = header->ownsChunkData != 0;

        try
        {
            if (chunksPtr != null && gcHandleStartIndex >= 0)
            {
                lock (_chunkGCHandlesLock)
                {
                    for (int i = 0; i < chunkCount && (gcHandleStartIndex + i) < _chunkGCHandles.Count; i++)
                    {
                        int index = gcHandleStartIndex + i;
                        if (_chunkGCHandles[index].IsAllocated) { _chunkGCHandles[index].Free(); _chunkGCHandles[index] = default; }
                    }
                    // 清理尾部连续的 default 条目，防止 _chunkGCHandles 无界增长
                    while (_chunkGCHandles.Count > 0 && !_chunkGCHandles[_chunkGCHandles.Count - 1].IsAllocated)
                        _chunkGCHandles.RemoveAt(_chunkGCHandles.Count - 1);
                    // #24：空洞积累（中间 default 条目被活跃 job 的 gcHandleStartIndex 引用，
                    // 不能移动元素；但底层数组可能远大于 Count）。TrimExcess 收缩容量但不移动
                    // 元素，释放空洞占用的数组空间，不影响索引寻址。仅在大容量时触发避免频繁拷贝。
                    if (_chunkGCHandles.Capacity > 8192 && _chunkGCHandles.Capacity > _chunkGCHandles.Count * 4)
                        _chunkGCHandles.TrimExcess();
                }
            }

            if (ownsChunkData)
            {
                for (int i = 0; i < chunkCount; i++)
                {
                    if (chunksPtr != null)
                    {
                        var cd = chunksPtr[i];
                        if (cd.componentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.componentArrays);
                        if (cd.componentSizes != null) Marshal.FreeHGlobal((IntPtr)cd.componentSizes);
                        if (cd.enableBitMaps != null) Marshal.FreeHGlobal((IntPtr)cd.enableBitMaps);
                        if (cd.componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)cd.componentTypeIndices);
                        if (cd.requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.requiredComponentArrays);
                    }
                }
            }

            if (chunksPtr != null && ownsChunkData) Marshal.FreeHGlobal((IntPtr)chunksPtr);
        }
        finally
        {
            // 兜底（#7/#16）：即使上面释放过程中抛异常（AccessViolation/StackOverflow 等），
            // 也必须复位 cleanupInProgress、释放 _chunkContextLeases 的 IDisposable 并归还池块，
            // 否则 flag 永久置位 + lease 泄漏（chunk 数据 MB 级永不回收）。
            if (_chunkContextLeases.TryRemove(contextBlock, out var leaseHandle))
            {
                try
                {
                    if (leaseHandle.Target is IDisposable lease)
                        lease.Dispose();
                }
                catch { }
                try { leaseHandle.Free(); } catch { }
            }

            try
            {
                var pooledBlock = contextBlock - IntPtr.Size;
                int pooledSize = *(int*)pooledBlock;
                ContextPool.Return(pooledBlock, pooledSize);
            }
            catch { }

            Interlocked.Exchange(ref header->cleanupInProgress, 0);
        }
    }

    // ======================== 回调工厂 ========================
    private unsafe static JobFunc CreateJobCallback<T>() where T : struct, IJob
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        bool managedContext = JobHasManagedReferences<T>();
        return (IntPtr ctx) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(name);
            try
            {
                long start = 0;
                if (JobProfiler.Enabled) start = Stopwatch.GetTimestamp();
                ref var job = ref GetJob<T>(ctx, managedContext);
                job.Execute();
                if (JobProfiler.Enabled) { int threadId = Environment.CurrentManagedThreadId; long end = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, start, end, threadId, 0); }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    private unsafe static IndexJobFunc CreateForCallback<T>() where T : struct, IJobFor
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        bool managedContext = JobHasManagedReferences<T>();
        return (IntPtr ctx, int i) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(name);
            try
            {
                long start = 0;
                if (JobProfiler.Enabled) start = Stopwatch.GetTimestamp();
                ref var job = ref GetJob<T>(ctx, managedContext);
                job.Execute(i);
                if (JobProfiler.Enabled) { int threadId = Environment.CurrentManagedThreadId; long end = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, start, end, threadId, 1); }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    private unsafe static BatchJobFunc CreateParallelForIndexCallback<T>() where T : struct, IJobParallelFor
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        bool managedContext = JobHasManagedReferences<T>();
        return (IntPtr ctx, int start, int count) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(name);
            try
            {
                long startTicks = 0;
                if (JobProfiler.Enabled) startTicks = Stopwatch.GetTimestamp();
                ref var job = ref GetJob<T>(ctx, managedContext);
                int end = start + count;
                for (int i = start; i < end; i++) job.Execute(i);
                if (JobProfiler.Enabled) { int threadId = Environment.CurrentManagedThreadId; long endTicks = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, startTicks, endTicks, threadId, 2); }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    private unsafe static BatchJobFunc CreateParallelForBatchCallback<T>() where T : struct, IJobParallelForBatch
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        bool managedContext = JobHasManagedReferences<T>();
        return (IntPtr ctx, int start, int count) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(name);
            try
            {
                long startTicks = 0;
                if (JobProfiler.Enabled) startTicks = Stopwatch.GetTimestamp();
                ref var job = ref GetJob<T>(ctx, managedContext);
                job.Execute(start, count);
                if (JobProfiler.Enabled) { int threadId = Environment.CurrentManagedThreadId; long endTicks = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, startTicks, endTicks, threadId, 3); }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    private unsafe static ChunkJobFuncDelegate CreateChunkCallback<T>() where T : struct, IJobChunk
    {
        return (IntPtr ctx, ChunkJobData* cd) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(typeof(T).Name);
            try
            {
                var header = (ChunkContextHeader*)ctx;
                int headerSize = Unsafe.SizeOf<ChunkContextHeader>();
                int typesDataSize = header->allEnabledCount * sizeof(int);
                int requiredTypesDataSize = header->requiredComponentTypeIdCount * sizeof(int);
                byte* jobPtr = (byte*)ctx + headerSize + typesDataSize + requiredTypesDataSize;
                ref var job = ref Unsafe.AsRef<T>(jobPtr);

                var chunkHandle = cd->chunkHandle;
                Chunk chunk = null;
                if (chunkHandle != IntPtr.Zero)
                {
                    try
                    {
                        var gch = GCHandle.FromIntPtr(chunkHandle);
                        if (gch.IsAllocated && gch.Target is Chunk c) chunk = c;
                    }
                    catch { }
                }
                if (chunk == null) return;

                if (header->hasEnabledFilter != 0 && header->allEnabledCount > 0)
                {
                    int* typeHashArray = (int*)header->queryAllEnabledTypes;
                    int ulongCount = (cd->entityCount + 63) / 64;
                    ulong* combinedMask = TempBuffer.GetBuffer(ulongCount);

                    bool firstFound = false;
                    for (int j = 0; j < header->allEnabledCount; j++)
                    {
                        int typeHash = typeHashArray[j];
                        var arch = chunk.Archetype;
                        for (int k = 0; k < cd->componentCount; k++)
                        {
                            if (arch.Types[k].GetHashCode() == typeHash)
                            {
                                ulong* bitmap = (ulong*)cd->enableBitMaps[k];
                                if (bitmap != null)
                                {
                                    if (!firstFound) { Buffer.MemoryCopy(bitmap, combinedMask, ulongCount * 8, ulongCount * 8); firstFound = true; }
                                    else { for (int b = 0; b < ulongCount; b++) combinedMask[b] &= bitmap[b]; }
                                }
                                break;
                            }
                        }
                    }

                    if (firstFound) job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(combinedMask, cd->entityCount));
                    else job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
                }
                else job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    [SkipLocalsInit]
    private unsafe static ChunkRangeJobFuncDelegate CreateChunkRangeCallback<T>() where T : struct, IJobChunk
    {
        return (IntPtr ctx, ChunkJobData* chunks, int startIndex, int count) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(typeof(T).Name);
            try
            {
                var header = (ChunkContextHeader*)ctx;
                int headerSize = Unsafe.SizeOf<ChunkContextHeader>();
                int typesDataSize = header->allEnabledCount * sizeof(int);
                int requiredTypesDataSize = header->requiredComponentTypeIdCount * sizeof(int);
                byte* jobPtr = (byte*)ctx + headerSize + typesDataSize + requiredTypesDataSize;
                ref var job = ref Unsafe.AsRef<T>(jobPtr);

                int end = startIndex + count;
                // 快速路径：无 enabled filter，减少调用链
                if (header->hasEnabledFilter == 0 || header->allEnabledCount == 0)
                {
                    for (int index = startIndex; index < end; index++)
                    {
                        var cd = chunks + index;
                        var chunk = ResolveChunk(cd->chunkHandle);
                        if (chunk != null)
                            job.Execute(new ArchetypeChunk(chunk), default);
                    }
                }
                else
                {
                    for (int index = startIndex; index < end; index++)
                    {
                        ExecuteRawChunk(ref job, header, chunks + index);
                    }
                }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Chunk ResolveChunk(IntPtr chunkHandle)
    {
        if (chunkHandle == IntPtr.Zero) return null;
        try
        {
            var gch = GCHandle.FromIntPtr(chunkHandle);
            if (gch.IsAllocated && gch.Target is Chunk c) return c;
        }
        catch { }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static void ExecuteRawChunk<T>(ref T job, ChunkContextHeader* header, ChunkJobData* cd)
        where T : struct, IJobChunk
    {
        var chunkHandle = cd->chunkHandle;
        Chunk chunk = null;
        if (chunkHandle != IntPtr.Zero)
        {
            try
            {
                var gch = GCHandle.FromIntPtr(chunkHandle);
                if (gch.IsAllocated && gch.Target is Chunk c) chunk = c;
            }
            catch { }
        }
        if (chunk == null) return;

        if (header->hasEnabledFilter != 0 && header->allEnabledCount > 0)
        {
            int* typeHashArray = (int*)header->queryAllEnabledTypes;
            int ulongCount = (cd->entityCount + 63) / 64;
            ulong* combinedMask = TempBuffer.GetBuffer(ulongCount);

            bool firstFound = false;
            for (int j = 0; j < header->allEnabledCount; j++)
            {
                int typeHash = typeHashArray[j];
                var arch = chunk.Archetype;
                for (int k = 0; k < cd->componentCount; k++)
                {
                    if (arch.Types[k].GetHashCode() != typeHash) continue;
                    ulong* bitmap = (ulong*)cd->enableBitMaps[k];
                    if (bitmap != null)
                    {
                        if (!firstFound)
                        {
                            Buffer.MemoryCopy(bitmap, combinedMask, ulongCount * 8, ulongCount * 8);
                            firstFound = true;
                        }
                        else
                        {
                            for (int b = 0; b < ulongCount; b++) combinedMask[b] &= bitmap[b];
                        }
                    }
                    break;
                }
            }

            if (firstFound) job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(combinedMask, cd->entityCount));
            else job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
        }
        else
        {
            job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
        }
    }

    private unsafe static BatchJobFunc CreateChunkArrayBatchCallback<T>() where T : struct, IJobChunk
    {
        bool managedContext = JobHasManagedReferences<T>();
        return (IntPtr ctx, int start, int count) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(typeof(T).Name);
            try
            {
                ref var job = ref GetChunkBatchJob<T>(ctx, managedContext, out var chunks, out var allEnabledTypes);
                int end = start + count;
                for (int index = start; index < end; index++)
                {
                    ExecuteManagedChunk(ref job, chunks[index], allEnabledTypes);
                }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static void ExecuteManagedChunk<T>(ref T job, Chunk chunk, ComponentType[] allEnabledTypes) where T : struct, IJobChunk
    {
        if (chunk == null) return;
        if (allEnabledTypes != null && allEnabledTypes.Length > 0)
        {
            int ulongCount = (chunk.EntityCount + 63) / 64;
            ulong* combinedMask = TempBuffer.GetBuffer(ulongCount);

            bool firstFound = false;
            var archetype = chunk.Archetype;
            for (int i = 0; i < allEnabledTypes.Length; i++)
            {
                int componentIndex = archetype.GetComponentTypeIndex(allEnabledTypes[i]);
                if (componentIndex < 0) continue;
                ulong* bitmap = chunk.GetEnableBitMapPointer(componentIndex);
                if (bitmap == null) continue;
                if (!firstFound)
                {
                    Buffer.MemoryCopy(bitmap, combinedMask, ulongCount * 8, ulongCount * 8);
                    firstFound = true;
                }
                else
                {
                    for (int b = 0; b < ulongCount; b++) combinedMask[b] &= bitmap[b];
                }
            }

            if (firstFound) job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(combinedMask, chunk.EntityCount));
            else job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
        }
        else
        {
            job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
        }
    }

    // ======================== 上下文内存池 ========================
    private static class ContextPool
    {
        private const int BucketShift = 6;
        private const int MaxBucket = 64;
        private static readonly ConcurrentStack<IntPtr>[] _buckets = new ConcurrentStack<IntPtr>[MaxBucket];

        private static int GetBucketIndex(int size)
        {
            int idx = (size + (1 << BucketShift) - 1) >> BucketShift;
            return idx >= MaxBucket ? -1 : idx;
        }

        /// <summary>返回桶 idx 对应的分配大小。</summary>
        /// <remarks>
        /// 桶 idx 覆盖 size ∈ (64*idx, 64*(idx+1)]，分配桶上界 64*(idx+1) 即可。
        /// 修复两个历史缺陷：
        ///  1) 旧实现 1L&lt;&lt;(6+idx) 幂次分配：idx=63 时 1L&lt;&lt;69 在 C# long 移位只取低 6 位
        ///     回绕为 1L&lt;&lt;5=32 字节 → 请求 3969-4032 字节却分配 32 → 堆缓冲区溢出；
        ///  2) idx≥26 时 2^(6+idx) ≥ 2^32 → clamp 到 int.MaxValue(~2GB) → 必然 OOM。
        /// 线性上界同时消除溢出、爆炸、与线性分桶不匹配的浪费。
        /// </remarks>
        private static int GetBucketAllocSize(int idx)
        {
            // idx 最大 63 → (63+1)&lt;&lt;6 = 4096，int 无溢出
            return (idx + 1) << BucketShift;
        }

        public static IntPtr Rent(int size)
        {
            int idx = GetBucketIndex(size);
            if (idx < 0) return Marshal.AllocHGlobal(size);
            var bucket = _buckets[idx];
            if (bucket != null && bucket.TryPop(out var ptr)) return ptr;
            return Marshal.AllocHGlobal(GetBucketAllocSize(idx));
        }

        public static void Return(IntPtr ptr, int size)
        {
            if (ptr == IntPtr.Zero) return;
            int idx = GetBucketIndex(size);
            if (idx < 0) { Marshal.FreeHGlobal(ptr); return; }
            var bucket = Volatile.Read(ref _buckets[idx]);
            if (bucket == null)
            {
                bucket = new ConcurrentStack<IntPtr>();
                bucket = Interlocked.CompareExchange(ref _buckets[idx], bucket, null) ?? bucket;
            }
            const int MaxPerBucket = 256;
            if (bucket.Count < MaxPerBucket) bucket.Push(ptr);
            else Marshal.FreeHGlobal(ptr);
        }
    }

    // ======================== 辅助方法 ========================
    internal static DelegateCache GetOrCreateDelegateCache<T, TDelegate>(Func<TDelegate> factory) where TDelegate : Delegate
    {
        // 必须用 GetOrAdd：手写 TryGetValue-创建-赋值在并发首次调度同一 T 时，
        // loser 实例会被覆盖，其委托可能被 GC 回收但函数指针已交原生侧 → 悬空。
        return _delegateCache.GetOrAdd(typeof(T), _ => new DelegateCache(factory()));
    }

    /// <summary>
    /// 自动批处理回调（per 泛型 T 缓存一次）：若 T 同时实现 IJobParallelForBatch，则调度用批回调
    /// （回调内一次 Execute(start,count)），否则退回逐元素 Execute(i)。减少轻任务上逐元素接口调度开销。
    /// 用反射仅在首次调度该类型时做一次 IsAssignableFrom 判定 + 泛型构造，热路径零开销。
    /// </summary>
    private static class AutoParallelForCallback<T>
        where T : struct, IJobParallelFor
    {
        public static readonly DelegateCache Cache = Build();

        private static DelegateCache Build()
        {
            if (typeof(IJobParallelForBatch).IsAssignableFrom(typeof(T)))
            {
                // T 同时是 IJobParallelForBatch → 批回调
                var create = typeof(NativeJobScheduler)
                    .GetMethod(nameof(CreateParallelForBatchCallback), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    .MakeGenericMethod(typeof(T))
                    .Invoke(null, null);
                return new DelegateCache((BatchJobFunc)create!);
            }
            // 退回逐元素回调
            return GetOrCreateDelegateCache<T, BatchJobFunc>(() => CreateParallelForIndexCallback<T>());
        }

        public static DelegateCache GetCache() => Cache;
    }

    // 按 batchId 归集的 Job 异常。Complete(h) 只抛本 batch 的异常；
    // batch 0 为未归属异常（实际不发生，防御兜底），由 Flush 统一抛。
    private static readonly object _exceptionLock = new();
    private static Dictionary<ulong, List<ExceptionDispatchInfo>> _recordedJobExceptions = new();
    // 每 batch 上限而非全局：一个坏 batch 不应饿死其它 batch 的异常上报。
    private const int MaxRecordedJobExceptionsPerBatch = 16;
    // 因每 batch 容量被丢弃的异常数（上报而非静默丢失）。
    private static int _droppedJobExceptionCount;

    private static void RecordJobException(ulong batchId, Exception exception)
    {
        lock (_exceptionLock)
        {
            if (!_recordedJobExceptions.TryGetValue(batchId, out var list))
            {
                list = new List<ExceptionDispatchInfo>();
                _recordedJobExceptions[batchId] = list;
            }
            if (list.Count >= MaxRecordedJobExceptionsPerBatch)
            {
                _droppedJobExceptionCount++;
                return;
            }
            list.Add(ExceptionDispatchInfo.Capture(exception));
        }
    }

    private static void ThrowAll(List<ExceptionDispatchInfo> captured)
    {
        if (captured.Count == 0) return;
        if (captured.Count == 1)
        {
            ExceptionDispatchInfo.Capture(captured[0].SourceException).Throw();
        }

        var exceptions = new List<Exception>(captured.Count);
        foreach (var ei in captured)
            exceptions.Add(ei.SourceException);
        throw new AggregateException("One or more scheduled C# jobs failed.", exceptions);
    }

    /// <summary>
    /// 抛出所有已记录的 Job 异常（跨所有 batch，含未归属的 batch 0）。
    /// 公有接口，可在帧末通过 TempAllocator.Reset() 或自定义检查点调用。
    /// </summary>
    public static void FlushRecordedExceptions()
    {
        List<ExceptionDispatchInfo> all = new();
        int dropped;
        lock (_exceptionLock)
        {
            foreach (var list in _recordedJobExceptions.Values)
                all.AddRange(list);
            _recordedJobExceptions.Clear();
            dropped = _droppedJobExceptionCount;
            _droppedJobExceptionCount = 0;
        }
        if (dropped > 0)
            Console.Error.WriteLine($"[JobSystem] {dropped} job exceptions dropped (per-batch cap {MaxRecordedJobExceptionsPerBatch}).");
        ThrowAll(all);
    }

    private static void ThrowRecordedJobExceptions(ulong batchId)
    {
        List<ExceptionDispatchInfo> captured;
        int dropped;
        lock (_exceptionLock)
        {
            if (!_recordedJobExceptions.TryGetValue(batchId, out captured))
                return;
            _recordedJobExceptions.Remove(batchId);
            dropped = _droppedJobExceptionCount;
            _droppedJobExceptionCount = 0;
        }
        if (dropped > 0)
            Console.Error.WriteLine($"[JobSystem] {dropped} job exceptions dropped (per-batch cap {MaxRecordedJobExceptionsPerBatch}).");
        ThrowAll(captured);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool JobHasManagedReferences<T>() where T : struct
        => RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    private sealed class ManagedJobBox<T> where T : struct
    {
        public T Job;

        public ManagedJobBox(T job)
        {
            Job = job;
        }
    }

    private sealed class RawChunkBatchContext
    {
        public IntPtr JobPtr;
        public Chunk[] Chunks;
        public ComponentType[] AllEnabledTypes;

        public RawChunkBatchContext(IntPtr jobPtr, Chunk[] chunks, ComponentType[] allEnabledTypes)
        {
            JobPtr = jobPtr;
            Chunks = chunks;
            AllEnabledTypes = allEnabledTypes;
        }
    }

    private sealed class ManagedChunkBatchContext<T> where T : struct, IJobChunk
    {
        public T Job;
        public readonly Chunk[] Chunks;
        public readonly ComponentType[] AllEnabledTypes;

        public ManagedChunkBatchContext(T job, Chunk[] chunks, ComponentType[] allEnabledTypes)
        {
            Job = job;
            Chunks = chunks;
            AllEnabledTypes = allEnabledTypes;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static ref T GetJob<T>(IntPtr ctx, bool managedContext) where T : struct
    {
        if (managedContext)
        {
            return ref GetManagedJob<T>(ctx);
        }

        return ref Unsafe.AsRef<T>((void*)ctx);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static ref T GetChunkBatchJob<T>(IntPtr ctx, bool managedContext, out Chunk[] chunks, out ComponentType[] allEnabledTypes)
        where T : struct, IJobChunk
    {
        var handle = GCHandle.FromIntPtr(ctx);
        if (managedContext)
        {
            var context = (ManagedChunkBatchContext<T>)handle.Target;
            chunks = context.Chunks;
            allEnabledTypes = context.AllEnabledTypes;
            return ref context.Job;
        }
        else
        {
            var context = (RawChunkBatchContext)handle.Target;
            chunks = context.Chunks;
            allEnabledTypes = context.AllEnabledTypes;
            return ref Unsafe.AsRef<T>((void*)context.JobPtr);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref T GetManagedJob<T>(IntPtr ctx) where T : struct
    {
        var handle = GCHandle.FromIntPtr(ctx);
        var box = (ManagedJobBox<T>)handle.Target;
        return ref box.Job;
    }

    private static IntPtr AllocManagedContext<T>(ref T job) where T : struct
    {
        var handle = GCHandle.Alloc(new ManagedJobBox<T>(job), GCHandleType.Normal);
        return GCHandle.ToIntPtr(handle);
    }

    private static IntPtr AllocManagedChunkBatchContext<T>(ref T job, Chunk[] chunks, ComponentType[] allEnabledTypes) where T : struct, IJobChunk
    {
        var handle = GCHandle.Alloc(new ManagedChunkBatchContext<T>(job, chunks, allEnabledTypes), GCHandleType.Normal);
        return GCHandle.ToIntPtr(handle);
    }

    private static IntPtr AllocRawChunkBatchContext<T>(ref T job, Chunk[] chunks, ComponentType[] allEnabledTypes) where T : struct, IJobChunk
    {
        IntPtr jobPtr = AllocContext(ref job);
        try
        {
            var handle = GCHandle.Alloc(new RawChunkBatchContext(jobPtr, chunks, allEnabledTypes), GCHandleType.Normal);
            return GCHandle.ToIntPtr(handle);
        }
        catch
        {
            Cleanup(jobPtr);
            throw;
        }
    }

    internal static void ManagedCleanup(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(ctx);
        if (handle.IsAllocated) handle.Free();
    }

    private static void RawChunkBatchCleanup(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(ctx);
        if (handle.IsAllocated)
        {
            if (handle.Target is RawChunkBatchContext context)
            {
                Cleanup(context.JobPtr);
                context.JobPtr = IntPtr.Zero;
            }

            handle.Free();
        }
    }

    private unsafe static IntPtr AllocContext<T>(ref T job) where T : struct
    {
        int size = Unsafe.SizeOf<T>();
        int totalSize = size + sizeof(int);
        IntPtr dataPtr = ContextPool.Rent(totalSize);
        *(int*)dataPtr = size;
        byte* jobPtr = (byte*)dataPtr + sizeof(int);
        Unsafe.CopyBlockUnaligned(jobPtr, Unsafe.AsPointer(ref job), (uint)size);
        return (IntPtr)jobPtr;
    }

    private unsafe static void Cleanup(IntPtr dataPtr)
    {
        if (dataPtr == IntPtr.Zero) return;
        int size = *(int*)((byte*)dataPtr - sizeof(int));
        ContextPool.Return((IntPtr)((byte*)dataPtr - sizeof(int)), size + sizeof(int));
    }
    }
}

