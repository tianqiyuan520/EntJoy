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
        public static JobHandle ScheduleChunk<T>(ref T job, EntityManager entityManager, QueryBuilder query, JobHandle dependsOn = default, ComponentType[]? writtenComponents = null)
            where T : struct, IJobChunk
            => ScheduleChunkCore(ref job, entityManager, query, IntPtr.Zero, null, dependsOn, writtenComponents: writtenComponents);

        public static JobHandle ScheduleChunkWithWorkerCap<T>(ref T job, EntityManager entityManager, QueryBuilder query, int workerCap, JobHandle dependsOn = default, ComponentType[]? writtenComponents = null)
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

        private static JobHandle ScheduleChunkCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, JobHandle dependsOn, ChunkScheduleMode? forcedMode = null, int workerCap = 0, int rangeSize = 0, ComponentType[]? writtenComponents = null, World world = null)
            where T : struct, IJobChunk
        {
            // Managed fallback（NativeDll 不可用时）：纯 C# 路径，无回调
            if (NativeJobScheduler.UseFallback)
            {
                world ??= World.DefaultWorld;
                return ScheduleChunkManagedFallback(ref job, entityManager, query, writtenComponents, dependsOn, world);
            }

            // C++ 路径
            var nativeDep = dependsOn._nativeHandle.IsValid ? (NativeJobHandle?)dependsOn._nativeHandle : null;
            var result = ScheduleChunkNativeCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, nativeDep, forcedMode, workerCap, rangeSize, writtenComponents, world);
            return new JobHandle(result);
        }

        private static NativeJobHandle ScheduleChunkNativeCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, ChunkScheduleMode? forcedMode = null, int workerCap = 0, int rangeSize = 0, ComponentType[]? writtenComponents = null, World world = null)
            where T : struct, IJobChunk
        {
            ChunkJobCollector.ValidateJobQuery(query);
            // 多 World 支持：绑定本次调度的 World
            world ??= World.DefaultWorld;
            // Event Buffer: 按需分配
            Type jobType = typeof(T);
            List<EventBufferHeader>? evtHeaders = null;
            if (EventMetaCache.TryGetValue(jobType, out var evtMeta) && evtMeta.Count > 0)
                evtHeaders = AllocateEventBuffers(jobType);

            var allEnabledTypes = query.AllEnabled;
            bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            bool canUseRawCache = funcPtr != IntPtr.Zero;
            if (canUseRawCache &&
                TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) &&
                rawCache.ChunkCount > 0)
            {
                var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
                var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease, eventBufferHeaders: evtHeaders, world: world);
                if (evtHeaders != null) StoreLiveEventBuffers(rawContextBlock, evtHeaders,
                    EventMetaCache.TryGetValue(jobType, out var rawMeta) ? rawMeta.EventTypes : null);
                try
                {
                    using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                    IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, NativeChunkJobs.ChunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, mode, workerCap, rangeSize, (uint)rawCache.StructuralVersion);
                    if (handle == IntPtr.Zero) { NativeChunkJobs.ChunkCleanup(rawContextBlock); return default; }
                    NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                    return TrackEntityJob(entityManager, FromNative(handle), query.HasRelationshipFilter ? null : rawCache.MatchingArchetypes, writtenComponents);
                }
                catch { NativeChunkJobs.ChunkCleanup(rawContextBlock); throw; }
            }

            bool jobIsBoxed = NativeJobCore.JobHasManagedReferences<T>() || query.HasRelationshipFilter;

            // 托管路径（唯一）：区间回调，blittable → job blob、托管引用 job → GCHandle box
            if (funcPtr == IntPtr.Zero &&
                TryGetManagedChunkScheduleCache(entityManager, query, out var managedCache, out var managedCacheLease) &&
                managedCache.ChunkCount > 0)
            {
                var managedContextBlock = CreateChunkContextBlock(ref job, managedCache.ChunksPtr, managedCache.ChunkCount, hasEnabledFilter, allEnabledTypes, -1, false, null, managedCacheLease, jobBoxed: jobIsBoxed, chunkArray: managedCache.ManagedChunkArray, query: query);
                try
                {
                    var cache = NativeJobCore.GetOrCreateDelegateCache<T, ChunkRangeJobFuncDelegate>(() => ChunkJobCallbacks.CreateChunkRangeCallback<T>());
                    using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                    IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkRangeJobEx(cache.FuncPtr, managedContextBlock, NativeChunkJobs.ChunkCleanupPtr, managedCache.ChunksPtr, managedCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, (uint)managedCache.StructuralVersion);
                    if (handle == IntPtr.Zero) { NativeChunkJobs.ChunkCleanup(managedContextBlock); return default; }
                    NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                    return TrackEntityJob(entityManager, FromNative(handle), query.HasRelationshipFilter ? null : managedCache.MatchingArchetypes, writtenComponents);
                }
                catch { NativeChunkJobs.ChunkCleanup(managedContextBlock); throw; }
            }

            // 无匹配 Chunk：释放提前分配但未接管的事件 buffer（否则 dataPtr/countPtr 泄漏）
            FreeEventBuffers(evtHeaders);
            return default;
        }

        private static NativeJobHandle ScheduleNativeChunkRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize, World world = null)
            where T : struct
        {
            ChunkJobCollector.ValidateJobQuery(query);
            if (funcPtr == IntPtr.Zero)
                throw new ArgumentException("Native chunk raw scheduling requires a function pointer.", nameof(funcPtr));

            var allEnabledTypes = query.AllEnabled;
            bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            if (TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) &&
                rawCache.ChunkCount > 0)
            {
                var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease);
                try
                {
                    using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                    IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, NativeChunkJobs.ChunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, (uint)rawCache.StructuralVersion);
                    if (handle == IntPtr.Zero) { NativeChunkJobs.ChunkCleanup(rawContextBlock); return default; }
                    NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                    return TrackEntityJob(entityManager, FromNative(handle), query.HasRelationshipFilter ? null : rawCache.MatchingArchetypes);
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

            FillChunkJobDataList(chunksPtr, chunkList, requiredComponentTypeIds, gcHandles: null, query: query);

            var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, true, requiredComponentTypeIds);
            try
            {
                using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, 0);
                if (handle == IntPtr.Zero) { NativeChunkJobs.ChunkCleanup(contextBlock); return default; }
                NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                return TrackEntityJob(entityManager, FromNative(handle), fallbackMatchingArchetypes.ToArray());
            }
            catch { NativeChunkJobs.ChunkCleanup(contextBlock); throw; }
        }

        /// <summary>
        /// Chunk 级过滤：<c>Archetype.IsMatch</c> 只覆盖 All/Any/None/AllEnabled，Shared 与 Changed 必须在这里再应用，
        /// 否则 <c>WithShared(v)</c> 的 job 会处理不匹配的块（静默错值）。
        /// 与 EntityQuery / ChunkJobCollector 共用同一对判定函数，保证四条路径一致。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool MatchesChunkFilters(EntityManager entityManager, QueryBuilder query, Chunk chunk)
            => entityManager.MatchesSharedFilter(query, chunk) && entityManager.MatchesChangedFilter(query, chunk);

        private static void CollectMatchingChunks(EntityManager entityManager, QueryBuilder query, List<Chunk> chunkList, List<Archetype> matchingArchetypes)
        {
            for (int i = 0; i < entityManager.ArchetypeCount; i++)
            {
                var arch = entityManager.Archetypes[i];
                if (arch != null && arch.IsMatch(query))
                {
                    matchingArchetypes.Add(arch);
                    foreach (var c in arch.ChunkSpan)
                        if (c.EntityCount > 0 && MatchesChunkFilters(entityManager, query, c)) chunkList.Add(c);
                }
            }
        }

        /// <summary>
        /// 填充 ChunkJobData 数组：组件指针、大小/位图/类型索引 + requiredComponentTypeIds 对应指针。
        /// gcHandles 为 null 时 chunkHandle 为 IntPtr.Zero（纯原生回调）；否则从 GCHandle[] 取句柄（托管回调）。
        /// 调用方负责释放各 chunk 分配的 compPtrs/compSizes/bitmaps/typeIndices/requiredArrays。
        /// </summary>
        private unsafe static void FillChunkJobDataList(ChunkJobData* chunksPtr, List<Chunk> chunkList, int[]? requiredComponentTypeIds, GCHandle[]? gcHandles, QueryBuilder query = default)
        {
            for (int ci = 0; ci < chunkList.Count; ci++)
            {
                IntPtr handle = gcHandles != null ? (IntPtr)gcHandles[ci] : IntPtr.Zero;
                chunksPtr[ci] = BuildChunkJobDataEntry(chunkList[ci], requiredComponentTypeIds, handle, query);
            }
        }

        // 单块的裸数据：逐列指针/大小/位图/类型 id，required 子集，shared 值指针与过滤条件。
        private unsafe static ChunkJobData BuildChunkJobDataEntry(Chunk chunk, int[]? requiredComponentTypeIds, IntPtr chunkHandle, QueryBuilder query)
        {
            var arch = chunk.Archetype;
            int compCount = chunk.ComponentCount;
            var compPtrs = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var compSizes = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
            var bitmaps = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var typeIndices = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
            for (int c = 0; c < compCount; c++)
            {
                compPtrs[c] = (void*)chunk.GetComponentArrayPointer(c);
                compSizes[c] = arch.Types[c].Size;
                bitmaps[c] = chunk.GetEnableBitMapPointer(c);
                typeIndices[c] = arch.Types[c].Id;
            }

            int requiredCount = requiredComponentTypeIds?.Length ?? 0;
            ResolveRequiredSubset(requiredComponentTypeIds, requiredCount, compPtrs, bitmaps, typeIndices, compCount,
                out void** requiredArrays, out void** requiredBitmaps);

            return new ChunkJobData
            {
                entityArray = (void*)chunk.GetEntityPointer(),
                entityCount = chunk.EntityCount,
                componentCount = compCount,
                componentArrays = compPtrs,
                componentSizes = compSizes,
                enableBitMaps = bitmaps,
                componentTypeIndices = typeIndices,
                chunkHandle = chunkHandle,
                requiredComponentArrays = requiredArrays,
                requiredComponentCount = requiredCount,
                sharedValuePtrs = FillSharedValuePtrs(chunk, arch, compCount),
                sharedValueCount = chunk.HasSharedValues ? CountBlittableShared(arch, compCount) : 0,
                requiredEnableBitMaps = requiredBitmaps,
                filter = BuildNativeFilter(chunk, query)
            };
        }

        // required 类型 id 对应的列指针与位图子集；该块没有这一列时留 null。
        private unsafe static void ResolveRequiredSubset(int[]? requiredComponentTypeIds, int requiredCount,
            void** compPtrs, void** bitmaps, int* typeIndices, int compCount,
            out void** requiredArrays, out void** requiredBitmaps)
        {
            requiredArrays = null;
            requiredBitmaps = null;
            if (requiredCount <= 0) return;
            requiredArrays = (void**)Marshal.AllocHGlobal(requiredCount * sizeof(void*));
            requiredBitmaps = (void**)Marshal.AllocHGlobal(requiredCount * sizeof(void*));
            for (int r = 0; r < requiredCount; r++)
            {
                requiredArrays[r] = null;
                requiredBitmaps[r] = null;
                int requiredTypeId = requiredComponentTypeIds![r];
                for (int c = 0; c < compCount; c++)
                {
                    if (typeIndices[c] != requiredTypeId) continue;
                    requiredArrays[r] = compPtrs[c];
                    requiredBitmaps[r] = bitmaps[c];
                    break;
                }
            }
        }

        // Shared values 支持（per-chunk）

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
        /// 返回非 null 的 void 数组（调用方不负责释放——跟随 ChunkJobData 生命周期由 NativeChunkJobs 释放）。
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
            ChunkJobCollector.ValidateJobQuery(query);
            if (funcPtr == IntPtr.Zero)
                throw new ArgumentException("Native chunk range raw scheduling requires a function pointer.", nameof(funcPtr));

            // 多 World 支持：绑定本次调度的 World（drain 写回正确 EventStream）
            world ??= World.DefaultWorld;

            // Event Buffer: 按需分配（ISPC IJobChunk SendEvent 路径）
            Type evtJobType = typeof(T);
            List<EventBufferHeader>? evtHeaders = null;
            if (EventMetaCache.TryGetValue(evtJobType, out var evtMeta) && evtMeta.Count > 0)
                evtHeaders = AllocateEventBuffers(evtJobType);

            var allEnabledTypes = query.AllEnabled;
            bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
            bool synchronousLease = mode == ChunkScheduleMode.ImmediateNative && !dependsOn.HasValue;
            if (TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease, synchronousLease) &&
                rawCache.ChunkCount > 0)
            {
                try
                {
                    var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease, eventBufferHeaders: evtHeaders, world: world);
                    if (evtHeaders != null) StoreLiveEventBuffers(rawContextBlock, evtHeaders,
                        EventMetaCache.TryGetValue(evtJobType, out var rawMeta) ? rawMeta.EventTypes : null);
                    try
                    {
                        using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                        IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkRangeJobEx(funcPtr, rawContextBlock, NativeChunkJobs.ChunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, mode, workerCap, rangeSize, (uint)rawCache.StructuralVersion);
                        if (handle == IntPtr.Zero) { NativeChunkJobs.ChunkCleanup(rawContextBlock); return default; }
                        return TrackEntityJob(entityManager, FromNative(handle), query.HasRelationshipFilter ? null : rawCache.MatchingArchetypes);
                    }
                    catch { NativeChunkJobs.ChunkCleanup(rawContextBlock); throw; }
                }
                finally { if (synchronousLease) rawCache.ReleaseLease(); }
            }

            var chunkList = new List<Chunk>(128);
            var fallbackMatchingArchetypes = new List<Archetype>(8);
            CollectMatchingChunks(entityManager, query, chunkList, fallbackMatchingArchetypes);

            int chunkCount = chunkList.Count;
            if (chunkCount == 0)
            {
                // 无匹配 Chunk：释放提前分配但未接管的事件 buffer
                FreeEventBuffers(evtHeaders);
                return default;
            }

            var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
            const int gcHandleStartIndex = -1;

            FillChunkJobDataList(chunksPtr, chunkList, requiredComponentTypeIds, gcHandles: null, query: query);

            var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, true, requiredComponentTypeIds, eventBufferHeaders: evtHeaders, world: world);
            if (evtHeaders != null) StoreLiveEventBuffers(contextBlock, evtHeaders,
                EventMetaCache.TryGetValue(evtJobType, out var fbMeta) ? fbMeta.EventTypes : null);
            try
            {
                using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                IntPtr handle = NativeChunkJobs.JobSystem_ScheduleChunkRangeJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, mode, workerCap, rangeSize, 0);
                if (handle == IntPtr.Zero) { NativeChunkJobs.ChunkCleanup(contextBlock); return default; }
                return TrackEntityJob(entityManager, FromNative(handle), fallbackMatchingArchetypes.ToArray());
            }
            catch { NativeChunkJobs.ChunkCleanup(contextBlock); throw; }
        }

        private static NativeJobHandle ScheduleNativeEntityBatchRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize, bool useScheduleAndComplete = false, ChunkScheduleMode? forcedMode = null, NativeEcsJobKind jobKind = NativeEcsJobKind.Entity, World world = null)
            where T : struct
        {
            ChunkJobCollector.ValidateJobQuery(query);
            if (funcPtr == IntPtr.Zero)
                throw new ArgumentException("Native entity batch raw scheduling requires a function pointer.", nameof(funcPtr));

            // 多 World 支持：绑定本次调度的 World（drain 写回正确 EventStream）
            world ??= World.DefaultWorld;

            // 诊断（ENTJOY_DIAG_CSHARP_PHASE=1）：C# 调度侧四段细分计时。
            //    采样窗口关闭后连时间戳都不取（Sampling=false）， 诊断对稳态零影响。
            bool cDiag = s_csharpPhaseDiag && CSharpPhaseDiag.Sampling("chunk.cache+hash");
            long d0 = cDiag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            long d1 = 0, d2 = 0, d3 = 0;

            var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
            bool synchronousLease = useScheduleAndComplete && mode == ChunkScheduleMode.ImmediateNative && !dependsOn.HasValue;
            if (!TryGetEntityBatchScheduleCache(entityManager, query, requiredComponentTypeIds, out var cache, out var cacheLease, synchronousLease) ||
                cache.BatchCount == 0)
                return default;
            try
            {
                d1 = cDiag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

                // Event Buffer: 按需分配
                Type evtJobType = typeof(T);
                List<EventBufferHeader>? evtHeaders = null;
                if (EventMetaCache.TryGetValue(evtJobType, out var evtMeta) && evtMeta.Count > 0)
                    evtHeaders = AllocateEventBuffers(evtJobType);

                var contextBlock = CreateChunkContextBlock(ref job, null, cache.BatchCount, false, null, -1, false, requiredComponentTypeIds, cacheLease, eventBufferHeaders: evtHeaders, world: world);
                if (evtHeaders != null) StoreLiveEventBuffers(contextBlock, evtHeaders,
                        EventMetaCache.TryGetValue(evtJobType, out var batchMeta) ? batchMeta.EventTypes : null);
                d2 = cDiag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                try
                {
                    using var dependencyLease = new NativeJobCore.RetainedNativeDependency(dependsOn);
                    var handle = useScheduleAndComplete
                        ? NativeChunkJobs.JobSystem_ScheduleAndCompleteEntityBatchJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, mode, workerCap, rangeSize, jobKind, (uint)cache.StructuralVersion)
                        : NativeChunkJobs.JobSystem_ScheduleEntityBatchJobEx(funcPtr, contextBlock, NativeChunkJobs.ChunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, mode, workerCap, rangeSize, jobKind, (uint)cache.StructuralVersion);
                    if (handle == IntPtr.Zero && !useScheduleAndComplete)
                    {
                        NativeChunkJobs.ChunkCleanup(contextBlock);
                        return default;
                    }
                    d3 = cDiag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    NativeJobCore.RegisterScheduledJobName(handle, typeof(T).Name);
                    // Event Buffer Drain
                    if (useScheduleAndComplete && evtHeaders != null)
                    {
                        // 同步路径：执行完成后立即 drain
                        DrainAndFreeEventBuffers(contextBlock, world, evtJobType);
                    }
                    else if (evtHeaders != null)
                    {
                        // 异步路径：注册到本 World 的 EntityManager，Complete 时统一 drain（记录 world 归属）
                        lock (entityManager._pendingNativeEventsLock)
                        {
                            entityManager._pendingNativeEvents.Add((contextBlock, evtJobType, world));
                            System.Threading.Volatile.Write(ref entityManager._pendingNativeEventsPresent, 1);
                        }
                    }
                    // 注意：这里故意不传 matchingArchetypes（entity-batch 缓存只持有 ptr/count，
                    // 每调度 ToArray 一遍 MatchingArchetypes 会在热路径上产生分配）。
                    // 由 CompleteArchetypeJobs 保守等待，结构变更不会与它并发写同一 chunk。
                    var ret = TrackEntityJob(entityManager, FromNative(handle));
                    if (cDiag)
                    {
                        // 必须经 CSharpPhaseDiag 攒窗口输出：直接 Console.WriteLine 会落进外层计时区，
                        // 把派发读数放大一个量级。
                        CSharpPhaseDiag.Add("chunk.cache+hash", us(d1 - d0));
                        CSharpPhaseDiag.Add("chunk.contextBlock", us(d2 - d1));
                        CSharpPhaseDiag.Add("chunk.PInvoke", us(d3 - d2));
                        CSharpPhaseDiag.Add("chunk.track+return", us(CSharpPhaseDiag.Now() - d3));
                    }
                    return ret;
                }
                catch { NativeChunkJobs.ChunkCleanup(contextBlock); throw; }
            }
            finally { if (synchronousLease) cache.ReleaseLease(); }

            static double us(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static readonly bool s_csharpPhaseDiag =
            Environment.GetEnvironmentVariable("ENTJOY_DIAG_CSHARP_PHASE") == "1";

        /// <summary>
        /// 同步执行 [NativeTranspile] IJobChunk：以 ImmediateNative 模式提交（C++ 侧主线程直接执行，
        /// 无 worker 唤醒），并一步完成（无需显式 Complete）。等价于 Run 的零调度开销版本。
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
        /// 同步执行 [NativeTranspile] IJobChunk（ISPC 后端）：经 ChunkRange 路径以 ImmediateNative 提交。
        /// </summary>
        public static void RunChunkRangeImmediate<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, World world = null)
            where T : struct
        {
            ScheduleNativeChunkRangeRawCore(
                ref job, entityManager, query, funcPtr, requiredComponentTypeIds,
                null, workerCap: 0, rangeSize: 0,
                forcedMode: ChunkScheduleMode.ImmediateNative, world: world);
        }

    }
}
