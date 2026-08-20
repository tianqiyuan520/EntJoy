using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using EntJoy;

namespace EntJoy.JobSystem
{

// ======================== Chunk 任务数据结构（与 C++ 一一对应） ========================

/// <summary>
/// 跨语言共享的 Chunk 任务数据结构（与 C++ ChunkJobData 一一对应）
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChunkJobData
{
    public void* entityArray;           // Entity 数组首地址
    public int entityCount;             // 实体数量
    public int componentCount;          // 组件种类数
    public void** componentArrays;      // 每个组件数组首地址（长度为 componentCount）
    public int* componentSizes;         // 每个组件大小（字节，长度为 componentCount）
    public void** enableBitMaps;        // 每个 enableable 组件位图指针（可为 null，长度为 componentCount）
    public int* componentTypeIndices;   // 组件类型索引数组
    public IntPtr chunkHandle;          // GCHandle IntPtr，用于在回调中恢复 Chunk 对象
    public void** requiredComponentArrays; // NativeTranspile IJobChunk 所需组件数组指针
    public int requiredComponentCount;     // requiredComponentArrays 数量
}

/// <summary>
/// NativeTranspile 轻量 Chunk 数据结构（与 C++ ChunkData 一一对应）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChunkData
{
    public void** componentArrays;      // 组件数组指针 [requiredCount]
    public int entityCount;             // 实体数量
    public int requiredComponentCount;  // 组件数组数量
    public void** enableBitMaps;        // enable 位图 [enableCount]，无过滤时为 null（预留）
    public int enableBitmapCount;       // enable 位图数量，0 表示无过滤（预留）
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct EntityBatchData
{
    public void** componentArrays;
    public void** enableBitMaps;
    public int entityCount;
    public int enableBitmapCount;
}

/// <summary>
/// Chunk 上下文包的内存布局（非托管），必须 Sequential 以确保布局与指针访问一致。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ChunkContextHeader
{
    public int chunkCount;               // Chunk 数量
    public int hasEnabledFilter;         // 是否有 enable 过滤
    public IntPtr queryAllEnabledTypes;  // int[]（类型哈希数组）指针
    public int allEnabledCount;          // AllEnabled 数组长度
    public int gcHandleStartIndex;       // GCHandle 列表起始索引（-1 = 无 GCHandle）
    public IntPtr chunksPtr;             // ChunkJobData 数组指针（用于 cleanup 回收）
    public int cleanupInProgress;        // 防止重复清理的标志
    public int ownsChunkData;            // 该 context 是否负责释放 chunksPtr + 每 chunk 缓冲区
    public IntPtr requiredComponentTypeIds; // NativeTranspiler IJobChunk 所需组件类型 ID 数组
    public int requiredComponentTypeIdCount; // 所需组件类型 ID 数量
}

internal enum ChunkScheduleMode
{
    PublishNoAssist = 0,
    PublishAssist = 1,
    DeferTinyOnly = 2,
    ImmediateNative = 3,
    DeferredPublish = 4,
    DeferredPublishNoAssist = 5
}

internal enum NativeEcsJobKind
{
    Chunk = 0,
    Entity = 1
}

/// <summary>
/// <see cref="NativeJobScheduler"/> 的 ECS 扩展（独立类）。
/// 包含所有依赖 Chunk/ComponentType/EntityManager/QueryBuilder 的调度方法。
/// 共享的可变状态（委托缓存、上下文池、异常、纯 P/Invoke 指针）由
/// <see cref="NativeJobEngine"/> 独占持有；本类只保留 chunk 专属结构/指针与状态。
/// </summary>
public static unsafe class NativeEcsScheduler
{
    // ======================== Chunk P/Invoke 函数指针 ========================
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, IntPtr> _jobSystem_ScheduleChunkJob;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, IntPtr> _jobSystem_ScheduleChunkJobEx;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, IntPtr> _jobSystem_ScheduleChunkRangeJobEx;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, IntPtr> _jobSystem_ScheduleEntityBatchJobEx;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, IntPtr> _jobSystem_ScheduleAndCompleteEntityBatchJobEx;

    private static readonly object _chunkPointerLoadLock = new();
    private static int _chunkPointersLoaded;

    /// <summary>
    /// 从 <see cref="NativeJobEngine.NativeDllHandle"/> 加载 chunk 专属导出。
    /// 首次 chunk 调度时幂等调用。
    /// </summary>
    internal static void LoadNativeChunkPointers(IntPtr dllHandle)
    {
        _jobSystem_ScheduleChunkJob = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleChunkJob");
        _jobSystem_ScheduleChunkJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleChunkJobEx");
        _jobSystem_ScheduleChunkRangeJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleChunkRangeJobEx");
        _jobSystem_ScheduleEntityBatchJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleEntityBatchJobEx");
        _jobSystem_ScheduleAndCompleteEntityBatchJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleAndCompleteEntityBatchJobEx");
    }

    private static void EnsureChunkPointersLoaded()
    {
        if (Volatile.Read(ref _chunkPointersLoaded) != 0) return;
        lock (_chunkPointerLoadLock)
        {
            if (_chunkPointersLoaded != 0) return;
            LoadNativeChunkPointers(NativeJobEngine.NativeDllHandle);
            Interlocked.Exchange(ref _chunkPointersLoaded, 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IntPtr JobSystem_ScheduleChunkJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, ChunkJobData* chunks, int chunkCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0)
    {
        NativeJobEngine.EnsureNativeLoaded();
        EnsureChunkPointersLoaded();
        return _jobSystem_ScheduleChunkJobEx(funcPtr, context, cleanupPtr, chunks, chunkCount, dependency, (int)mode, workerCap, rangeSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IntPtr JobSystem_ScheduleChunkRangeJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, ChunkJobData* chunks, int chunkCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0)
    {
        NativeJobEngine.EnsureNativeLoaded();
        EnsureChunkPointersLoaded();
        return _jobSystem_ScheduleChunkRangeJobEx(funcPtr, context, cleanupPtr, chunks, chunkCount, dependency, (int)mode, workerCap, rangeSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IntPtr JobSystem_ScheduleEntityBatchJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, EntityBatchData* batches, int batchCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0, NativeEcsJobKind jobKind = NativeEcsJobKind.Entity)
    {
        NativeJobEngine.EnsureNativeLoaded();
        EnsureChunkPointersLoaded();
        return _jobSystem_ScheduleEntityBatchJobEx(funcPtr, context, cleanupPtr, batches, batchCount, dependency, (int)mode, workerCap, rangeSize, (int)jobKind);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IntPtr JobSystem_ScheduleAndCompleteEntityBatchJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, EntityBatchData* batches, int batchCount, IntPtr dependency, ChunkScheduleMode mode = ChunkScheduleMode.PublishAssist, int workerCap = 0, int rangeSize = 0, NativeEcsJobKind jobKind = NativeEcsJobKind.Entity)
    {
        NativeJobEngine.EnsureNativeLoaded();
        EnsureChunkPointersLoaded();
        return _jobSystem_ScheduleAndCompleteEntityBatchJobEx(funcPtr, context, cleanupPtr, batches, batchCount, dependency, (int)mode, workerCap, rangeSize, (int)jobKind);
    }

    // ======================== Chunk 回调委托 ========================
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ChunkJobFuncDelegate(IntPtr context, ChunkJobData* chunkData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ChunkRangeJobFuncDelegate(IntPtr context, ChunkJobData* chunks, int startIndex, int count);

    // ======================== Job 名登记 / 实体跟踪 ========================
    private static NativeJobHandle TrackEntityJob(EntityManager entityManager, NativeJobHandle handle)
    {
        entityManager?.RegisterActiveJob(handle);
        return handle;
    }

    // ======================== Chunk 调度缓存（归属 ECS，与 engine 解耦） ========================
    internal static readonly object _rawChunkScheduleCacheLock = new();
    internal static readonly Dictionary<RawChunkScheduleCacheKey, RawChunkScheduleCache> _rawChunkScheduleCaches = new();
    internal static readonly Dictionary<RawChunkScheduleCacheKey, ManagedChunkScheduleCache> _managedChunkScheduleCaches = new();
    internal static readonly Dictionary<RawChunkScheduleCacheKey, EntityBatchScheduleCache> _entityBatchScheduleCaches = new();
    internal static readonly ConcurrentDictionary<IntPtr, GCHandle> _chunkContextLeases = new();
    internal static readonly object _chunkGCHandlesLock = new();
    internal static readonly List<GCHandle> _chunkGCHandles = new();

    internal static readonly NativeJobEngine.CleanupFunc _chunkCleanup = ChunkCleanup;
    internal static readonly IntPtr _chunkCleanupPtr = Marshal.GetFunctionPointerForDelegate(_chunkCleanup);
    internal static readonly NativeJobEngine.CleanupFunc _rawChunkBatchCleanup = RawChunkBatchCleanup;
    internal static readonly IntPtr _rawChunkBatchCleanupPtr = Marshal.GetFunctionPointerForDelegate(_rawChunkBatchCleanup);

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

    // ======================== IJobChunk 调度 ========================
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

    /// <summary>IJobEntity ISPC 轻量调度：跳过 entity tracking + query cache。</summary>
    public static NativeJobHandle ScheduleEntityBatchRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
        where T : struct
        => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize);

    public static NativeJobHandle ScheduleChunkEntityBatchRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize, jobKind: NativeEcsJobKind.Chunk);

    /// <summary>Schedule + Complete 一步完成，消除一次 P/Invoke 往返和 handle boxing 开销。</summary>
    public static NativeJobHandle ScheduleAndCompleteEntityBatchRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap = 0, int rangeSize = 0)
        where T : struct
        => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, null, workerCap, rangeSize, useScheduleAndComplete: true);

    public static void RunChunkRawImmediate<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds)
        where T : struct, IJobChunk
    {
        var handle = ScheduleChunkCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, null, ChunkScheduleMode.ImmediateNative);
        NativeJobScheduler.Complete(ref handle);
    }

    public static void RunEntityRawImmediate<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds)
        where T : struct
    {
        var handle = ScheduleNativeChunkRawImmediateCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds);
        NativeJobScheduler.Complete(ref handle);
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
                using var dependencyLease = new NativeJobEngine.RetainedNativeDependency(dependsOn);
                IntPtr h1268 = JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, mode, workerCap, rangeSize);
                NativeJobEngine.RegisterScheduledJobName(h1268, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(h1268));
            }
            catch { ChunkCleanup(rawContextBlock); throw; }
        }

        bool jobHasManagedReferences = NativeJobEngine.JobHasManagedReferences<T>();

        if (funcPtr == IntPtr.Zero &&
            !jobHasManagedReferences &&
            TryGetManagedChunkScheduleCache(entityManager, query, out var csharpRawCache, out var csharpRawCacheLease) &&
            csharpRawCache.ChunkCount > 0)
        {
            var csharpRawContextBlock = CreateChunkContextBlock(ref job, csharpRawCache.ChunksPtr, csharpRawCache.ChunkCount, hasEnabledFilter, allEnabledTypes, -1, false, null, csharpRawCacheLease);
            try
            {
                var cache = NativeJobEngine.GetOrCreateDelegateCache<T, ChunkRangeJobFuncDelegate>(() => CreateChunkRangeCallback<T>());
                using var dependencyLease = new NativeJobEngine.RetainedNativeDependency(dependsOn);
                IntPtr h1285 = JobSystem_ScheduleChunkRangeJobEx(cache.FuncPtr, csharpRawContextBlock, _chunkCleanupPtr, csharpRawCache.ChunksPtr, csharpRawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
                NativeJobEngine.RegisterScheduledJobName(h1285, typeof(T).Name);
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
                var cache = NativeJobEngine.GetOrCreateDelegateCache<T, NativeJobEngine.BatchJobFunc>(() => CreateChunkArrayBatchCallback<T>());
                using var dependencyLease = new NativeJobEngine.RetainedNativeDependency(dependsOn);
                IntPtr h1301 = NativeJobEngine.JobSystem_ScheduleParallelForBatch(cache.FuncPtr, managedContextBlock, jobHasManagedReferences ? NativeJobEngine.ManagedCleanupPtr : _rawChunkBatchCleanupPtr, managedCache.Chunks.Length, -1, dependencyLease.Handle);
                NativeJobEngine.RegisterScheduledJobName(h1301, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(h1301));
            }
            catch
            {
                if (jobHasManagedReferences) NativeJobEngine.ManagedCleanup(managedContextBlock);
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

        bool nativeCallback = funcPtr != IntPtr.Zero;
        var gcHandles = nativeCallback ? null : new GCHandle[chunkCount];
        int gcHandleStartIndex = -1;
        if (!nativeCallback)
        {
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
                var cache = NativeJobEngine.GetOrCreateDelegateCache<T, ChunkJobFuncDelegate>(() => CreateChunkCallback<T>());
                callbackPtr = cache.FuncPtr;
            }
            var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
            using var dependencyLease = new NativeJobEngine.RetainedNativeDependency(dependsOn);
            IntPtr h1415 = JobSystem_ScheduleChunkJobEx(callbackPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, mode, workerCap, rangeSize);
            NativeJobEngine.RegisterScheduledJobName(h1415, typeof(T).Name);
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
                using var dependencyLease = new NativeJobEngine.RetainedNativeDependency(dependsOn);
                IntPtr h1465 = JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
                NativeJobEngine.RegisterScheduledJobName(h1465, typeof(T).Name);
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
            using var dependencyLease = new NativeJobEngine.RetainedNativeDependency(dependsOn);
            IntPtr h1549 = JobSystem_ScheduleChunkJobEx(funcPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
            NativeJobEngine.RegisterScheduledJobName(h1549, typeof(T).Name);
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
                using var dependencyLease = new NativeJobEngine.RetainedNativeDependency(dependsOn);
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
            using var dependencyLease = new NativeJobEngine.RetainedNativeDependency(dependsOn);
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
            using var dependencyLease = new NativeJobEngine.RetainedNativeDependency(dependsOn);
            var handle = useScheduleAndComplete
                ? JobSystem_ScheduleAndCompleteEntityBatchJobEx(funcPtr, contextBlock, _chunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, jobKind)
                : JobSystem_ScheduleEntityBatchJobEx(funcPtr, contextBlock, _chunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, jobKind);
            NativeJobEngine.RegisterScheduledJobName(handle, typeof(T).Name);
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
            NativeJobEngine.RegisterScheduledJobName(h1699, typeof(T).Name);
            return TrackEntityJob(entityManager, new NativeJobHandle(h1699));
        }
        catch { ChunkCleanup(rawContextBlock); throw; }
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
        var pooledBlock = NativeJobEngine.ContextPool.Rent(pooledSize);
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
        if (NativeJobScheduler.TryGetJobFieldWriter(typeof(T), out var __fieldWriter))
        {
            ((NativeJobScheduler.JobFieldWriter<T>)__fieldWriter)(jobPtr, ref job);
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
                    while (_chunkGCHandles.Count > 0 && !_chunkGCHandles[_chunkGCHandles.Count - 1].IsAllocated)
                        _chunkGCHandles.RemoveAt(_chunkGCHandles.Count - 1);
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
                NativeJobEngine.ContextPool.Return(pooledBlock, pooledSize);
            }
            catch { }

            Interlocked.Exchange(ref header->cleanupInProgress, 0);
        }
    }

    // ======================== Chunk 回调工厂 ========================
    private unsafe static ChunkJobFuncDelegate CreateChunkCallback<T>() where T : struct, IJobChunk
    {
        return (IntPtr ctx, ChunkJobData* cd) =>
        {
            NativeJobEngine.EnterJobExecution();
            NativeJobEngine.RegisterCurrentBatchJobName(typeof(T).Name);
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
                NativeJobEngine.RecordJobException(NativeJobEngine.CurrentBatchId, exception);
            }
            finally
            {
                NativeJobEngine.ExitJobExecution();
            }
        };
    }

    [SkipLocalsInit]
    private unsafe static ChunkRangeJobFuncDelegate CreateChunkRangeCallback<T>() where T : struct, IJobChunk
    {
        return (IntPtr ctx, ChunkJobData* chunks, int startIndex, int count) =>
        {
            NativeJobEngine.EnterJobExecution();
            NativeJobEngine.RegisterCurrentBatchJobName(typeof(T).Name);
            try
            {
                var header = (ChunkContextHeader*)ctx;
                int headerSize = Unsafe.SizeOf<ChunkContextHeader>();
                int typesDataSize = header->allEnabledCount * sizeof(int);
                int requiredTypesDataSize = header->requiredComponentTypeIdCount * sizeof(int);
                byte* jobPtr = (byte*)ctx + headerSize + typesDataSize + requiredTypesDataSize;
                ref var job = ref Unsafe.AsRef<T>(jobPtr);

                int end = startIndex + count;
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
                NativeJobEngine.RecordJobException(NativeJobEngine.CurrentBatchId, exception);
            }
            finally
            {
                NativeJobEngine.ExitJobExecution();
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

    private unsafe static NativeJobEngine.BatchJobFunc CreateChunkArrayBatchCallback<T>() where T : struct, IJobChunk
    {
        bool managedContext = NativeJobEngine.JobHasManagedReferences<T>();
        return (IntPtr ctx, int start, int count) =>
        {
            NativeJobEngine.EnterJobExecution();
            NativeJobEngine.RegisterCurrentBatchJobName(typeof(T).Name);
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
                NativeJobEngine.RecordJobException(NativeJobEngine.CurrentBatchId, exception);
            }
            finally
            {
                NativeJobEngine.ExitJobExecution();
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

    // ======================== Chunk 批上下文 ========================
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

    private static IntPtr AllocManagedChunkBatchContext<T>(ref T job, Chunk[] chunks, ComponentType[] allEnabledTypes) where T : struct, IJobChunk
    {
        var handle = GCHandle.Alloc(new ManagedChunkBatchContext<T>(job, chunks, allEnabledTypes), GCHandleType.Normal);
        return GCHandle.ToIntPtr(handle);
    }

    private static IntPtr AllocRawChunkBatchContext<T>(ref T job, Chunk[] chunks, ComponentType[] allEnabledTypes) where T : struct, IJobChunk
    {
        IntPtr jobPtr = NativeJobEngine.AllocContext(ref job);
        try
        {
            var handle = GCHandle.Alloc(new RawChunkBatchContext(jobPtr, chunks, allEnabledTypes), GCHandleType.Normal);
            return GCHandle.ToIntPtr(handle);
        }
        catch
        {
            NativeJobEngine.Cleanup(jobPtr);
            throw;
        }
    }

    private static void RawChunkBatchCleanup(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(ctx);
        if (handle.IsAllocated)
        {
            if (handle.Target is RawChunkBatchContext context)
            {
                NativeJobEngine.Cleanup(context.JobPtr);
                context.JobPtr = IntPtr.Zero;
            }

            handle.Free();
        }
    }
}
}
