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
        // 上下文块创建 / 清理
        private unsafe static IntPtr CreateChunkContextBlock<T>(ref T job, ChunkJobData* chunksPtr, int chunkCount, bool hasEnabledFilter, ComponentType[] allEnabledTypes, int gcHandleStartIndex, bool ownsChunkData, int[] requiredComponentTypeIds = null, IDisposable cacheLease = null, bool jobBoxed = false, Chunk[]? chunkArray = null, List<EventBufferHeader>? eventBufferHeaders = null, World world = null, QueryBuilder query = default) where T : struct
        {
            int jobSize = jobBoxed ? sizeof(IntPtr) : Unsafe.SizeOf<T>();
            int headerSize = Unsafe.SizeOf<ChunkContextHeader>();
            int[] typeHashes = CollectEnabledTypeHashes(hasEnabledFilter, allEnabledTypes);
            int typesDataSize = typeHashes != null ? typeHashes.Length * sizeof(int) : 0;
            int requiredTypesDataSize = requiredComponentTypeIds != null ? requiredComponentTypeIds.Length * sizeof(int) : 0;
            int totalSize = headerSize + typesDataSize + requiredTypesDataSize + jobSize;
            int pooledSize = IntPtr.Size + totalSize;

            // 池块头部存整块字节数，清理时据此归还。
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
            // 托管回调的 Chunk[] 存在 ChunkArrayTable（按 context 指针索引，零 GCHandle）。
            header->chunkArrayHandle = IntPtr.Zero;
            if (chunkArray != null) ChunkArrayTable[(IntPtr)block] = chunkArray;

            WriteEnabledTypeHashes(header, (byte*)block, headerSize, typeHashes);
            WriteRequiredTypeIds(header, (byte*)block, headerSize, typesDataSize, requiredComponentTypeIds);
            AttachEventBufferHeaders(header, eventBufferHeaders, world);

            byte* jobPtr = (byte*)block + headerSize + typesDataSize + requiredTypesDataSize;
            WriteJobPayload(jobPtr, ref job, jobBoxed, query, jobSize);

            if (cacheLease != null)
            {
                NativeChunkJobs.ChunkContextLeases[block] = GCHandle.Alloc(cacheLease, GCHandleType.Normal);
            }

            return block;
        }

        private static int[] CollectEnabledTypeHashes(bool hasEnabledFilter, ComponentType[] allEnabledTypes)
        {
            if (!hasEnabledFilter || allEnabledTypes == null) return null;
            var hashes = new int[allEnabledTypes.Length];
            for (int i = 0; i < allEnabledTypes.Length; i++) hashes[i] = allEnabledTypes[i].GetHashCode();
            return hashes;
        }

        // 启用位过滤的类型哈希表紧跟在 header 之后。
        private static unsafe void WriteEnabledTypeHashes(ChunkContextHeader* header, byte* block, int headerSize, int[] typeHashes)
        {
            if (typeHashes == null)
            {
                header->allEnabledCount = 0;
                header->queryAllEnabledTypes = IntPtr.Zero;
                return;
            }
            var typeHashPtr = (int*)(block + headerSize);
            for (int i = 0; i < typeHashes.Length; i++) typeHashPtr[i] = typeHashes[i];
            header->allEnabledCount = typeHashes.Length;
            header->queryAllEnabledTypes = (IntPtr)typeHashPtr;
        }

        private static unsafe void WriteRequiredTypeIds(ChunkContextHeader* header, byte* block, int headerSize, int typesDataSize, int[] requiredComponentTypeIds)
        {
            if (requiredComponentTypeIds == null || requiredComponentTypeIds.Length == 0)
            {
                header->requiredComponentTypeIdCount = 0;
                header->requiredComponentTypeIds = IntPtr.Zero;
                return;
            }
            var requiredTypePtr = (int*)(block + headerSize + typesDataSize);
            for (int i = 0; i < requiredComponentTypeIds.Length; i++) requiredTypePtr[i] = requiredComponentTypeIds[i];
            header->requiredComponentTypeIdCount = requiredComponentTypeIds.Length;
            header->requiredComponentTypeIds = (IntPtr)requiredTypePtr;
        }

        // C++ 侧要的是 __EntJoyEventBuffer*[]：每个元素独立分配，避免结构数组布局歧义。
        private static unsafe void AttachEventBufferHeaders(ChunkContextHeader* header, List<EventBufferHeader> eventBufferHeaders, World world)
        {
            if (eventBufferHeaders == null || eventBufferHeaders.Count == 0)
            {
                header->eventBufferCount = 0;
                header->eventBufferHeaders = IntPtr.Zero;
                header->eventWorldHandle = IntPtr.Zero;
                return;
            }
            var ptrArrayBlock = Marshal.AllocHGlobal(eventBufferHeaders.Count * sizeof(IntPtr));
            var ptrArr = (IntPtr*)ptrArrayBlock;
            for (int i = 0; i < eventBufferHeaders.Count; i++)
            {
                var hdrPtr = Marshal.AllocHGlobal(sizeof(EventBufferHeader));
                *(EventBufferHeader*)hdrPtr = eventBufferHeaders[i];
                ptrArr[i] = hdrPtr;
            }
            header->eventBufferCount = eventBufferHeaders.Count;
            header->eventBufferHeaders = ptrArrayBlock;
            // 绑定 World 句柄：清理时据此把事件 drain 到正确的 EventStream。
            header->eventWorldHandle = world != null
                ? GCHandle.ToIntPtr(GCHandle.Alloc(world, GCHandleType.Normal))
                : IntPtr.Zero;
        }

        private static unsafe void WriteJobPayload<T>(byte* jobPtr, ref T job, bool jobBoxed, QueryBuilder query, int jobSize) where T : struct
        {
            if (jobBoxed)
            {
                *(IntPtr*)jobPtr = GCHandle.ToIntPtr(GCHandle.Alloc(new ManagedJobBox<T> { Job = job, Query = query }, GCHandleType.Normal));
            }
            else if (NativeJobScheduler.TryGetJobFieldWriter(typeof(T), out var fieldWriter))
            {
                ((NativeJobScheduler.JobFieldWriter<T>)fieldWriter)(jobPtr, ref job);
            }
            else
            {
                Unsafe.CopyBlockUnaligned(jobPtr, Unsafe.AsPointer(ref job), (uint)jobSize);
            }
        }

        // Managed fallback（NativeDll 不可用时）
        /// <summary>
        /// Managed fallback：收集 chunks 到 ManagedChunkParallelJob 再 ManagedJobScheduler.ScheduleParallelFor。
        /// 纯 C# 路径，无 P/Invoke、无 ChunkJobCallbacks、无 ContextBlock。
        /// </summary>
        private static JobHandle ScheduleChunkManagedFallback<T>(ref T job, EntityManager entityManager,
            QueryBuilder query, ComponentType[]? writtenComponents, JobHandle dependsOn, World world)
            where T : struct, IJobChunk
        {
            var allEnabledTypes = query.AllEnabled;
            // buildPayload: false —— 本路径直接遍历 Chunk 对象，不需要 ChunkJobData 表；
            // 之前忽略了 out ptr 又照建 payload ， 每次托管回退调度泄漏一份 HGlobal 表 + 每 chunk 位图块。
            ChunkJobCollector.CollectAndBuildManaged(entityManager, query, fillBitmaps: true, hasEnabledFilter: true,
                out _, out var chunkArray, out var chunkCount, out var archetypes, buildPayload: false);
            if (chunkCount == 0) return default;

            var parallelJob = new ManagedChunkParallelJob<T>
            {
                Job = job, Chunks = chunkArray, ChunkCount = chunkCount, AllEnabledTypes = allEnabledTypes, World = world, Query = query
            };
            var mhandle = JobScheduler.ScheduleParallelFor(ref parallelJob, chunkCount, innerBatchCount: 1, dependsOn);
            // 托管路径也需 TrackEntityJob：用于 Selective Wait（结构变更时只等影响的 archetype 的 job）
            TrackEntityJob(entityManager, mhandle, query.HasRelationshipFilter ? null : archetypes, writtenComponents);
            return mhandle;
        }

        /// <summary>托管调度 payload：直接在 ManagedWorker 上执行，无 P/Invoke 回调。实现 IJobParallelFor。</summary>
        internal unsafe struct ManagedChunkParallelJob<T> : IJobParallelFor where T : struct, IJobChunk
        {
            public T Job;
            public Chunk[] Chunks;
            public int ChunkCount;
            public ComponentType[]? AllEnabledTypes;
            public World World;
            public QueryBuilder Query;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Execute(int index)
            {
                if ((uint)index >= (uint)ChunkCount) return;
                var chunk = Chunks[index];
                if (chunk.EntityCount == 0) return;
                int words = (AllEnabledTypes?.Length > 0 || Query.HasRelationshipFilter) ? (chunk.EntityCount + 63) / 64 : 0;
                Span<ulong> scratch = words <= 64 ? stackalloc ulong[words] : new ulong[words];
                var mask = ComputeChunkMask(chunk, AllEnabledTypes, Query, scratch);
                if (!mask.HasMatches) return;
                // 绑定 job 所属 World：worker 线程内 SystemAPI.SendEvent/DefaultWorld 访问不串写到其他 World
                var prev = World.DefaultWorld;
                if (World != null) World.DefaultWorld = World;
                try
                {
                    Job.Execute(new ArchetypeChunk(chunk), mask);
                }
                finally
                {
                    World.DefaultWorld = prev;
                }
            }
        }

        // Run 路径（主线程同步执行）
        internal static unsafe void ExecuteOnQuery<T>(ref T job, EntityManager entityManager, QueryBuilder query)
            where T : struct, IJobChunk
        {
            ChunkJobCollector.ValidateJobQuery(query);
            var allEnabledTypes = query.AllEnabled;
            bool hasChunkFilters = query.HasSharedFilter || query.ChangedComponents?.Length > 0;
            bool hasEntityFilters = allEnabledTypes?.Length > 0 || query.HasRelationshipFilter;
            Span<ulong> scratch = stackalloc ulong[64];
            // 读令牌按调用借用一次：逐 Chunk 借用会在热路径上重复取令牌与校验。
            QueryReadState plainState = default;
            QueryReadState relationshipState = default;
            bool plainBorrowed = false;
            bool relationshipBorrowed = false;
            for (int archIdx = 0; archIdx < entityManager.ArchetypeCount; archIdx++)
            {
                var archetype = entityManager.Archetypes[archIdx];
                if (archetype == null || !archetype.IsMatch(query)) continue;
                QueryReadState readState;
                if (archetype.HasRelationships)
                {
                    if (!relationshipBorrowed)
                    {
                        relationshipState = new QueryReadState(entityManager, trackRelationships: true);
                        relationshipBorrowed = true;
                    }
                    readState = relationshipState;
                }
                else
                {
                    if (!plainBorrowed)
                    {
                        plainState = new QueryReadState(entityManager, trackRelationships: false);
                        plainBorrowed = true;
                    }
                    readState = plainState;
                }
                var chunks = archetype.ChunkSpan;
                for (int ci = 0; ci < chunks.Length; ci++)
                {
                    var chunk = chunks[ci];
                    if (chunk.EntityCount == 0) continue;
                    // job.Run(query) 同样必须应用 Shared / Changed 过滤
                    if (hasChunkFilters && !MatchesChunkFilters(entityManager, query, chunk)) continue;
                    ChunkEnabledMask mask = default;
                    if (hasEntityFilters)
                    {
                        int words = (chunk.EntityCount + 63) / 64;
                        mask = ComputeChunkMask(chunk, allEnabledTypes, query, words <= scratch.Length ? scratch : new ulong[words]);
                    }
                    if (!mask.HasMatches) continue;
                    job.Execute(new ArchetypeChunk(chunk, readState), mask);
                }
            }
        }

        // Phase C：共享 mask 工具（单一真值来源）
        /// <summary>
        /// 关系条件与 Enabled 位图取交集；default 仅表示无过滤，空结果保留实际长度。
        /// </summary>
        internal static unsafe ChunkEnabledMask ComputeChunkMask(Chunk chunk, ComponentType[] allEnabledTypes, QueryBuilder query, Span<ulong> scratch)
        {
            if ((allEnabledTypes == null || allEnabledTypes.Length == 0) && !query.HasRelationshipFilter) return default;
            var archetype = chunk.Archetype;
            int entityCount = chunk.EntityCount;
            int ulongCount = (entityCount + 63) / 64;
            var combined = scratch.Slice(0, ulongCount);
            combined.Fill(ulong.MaxValue);
            for (int i = 0; i < (allEnabledTypes?.Length ?? 0); i++)
            {
                int idx = archetype.GetComponentTypeIndex(allEnabledTypes[i]);
                if (idx < 0) { combined.Clear(); break; }
                ulong* bitmap = chunk.GetEnableBitMapPointer(idx);
                if (bitmap == null) continue;
                for (int b = 0; b < ulongCount; b++) combined[b] &= bitmap[b];
            }
            if (query.HasRelationshipFilter)
                for (int i = 0; i < entityCount; i++)
                    if (!archetype.Owner.MatchesRelationshipFilter(query, chunk, i))
                        combined[i >> 6] &= ~(1UL << (i & 63));
            if (ulongCount > 0 && (entityCount & 63) != 0)
                combined[ulongCount - 1] &= (1UL << (entityCount & 63)) - 1;
            return new ChunkEnabledMask(combined, entityCount);
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
