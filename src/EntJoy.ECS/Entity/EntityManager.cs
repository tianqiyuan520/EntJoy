using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager : IDisposable
    {
        private readonly Dictionary<int, List<Archetype>> archetypeMap;  // 原型映射表（哈希 -> Archetype 列表，防碰撞）
        private Archetype[] allArchetypes;  // 所有原型数组

        private int archetypeCount;
        public int ArchetypeCount
        {
            get { return archetypeCount; }
            set { archetypeCount = value; }
        }
        public ref readonly Archetype[] Archetypes => ref allArchetypes;

        /// <summary>实体回收队列（对象池）</summary>
        private Queue<Entity> recycleEntities;  // 实体回收队列

        /// <summary>实体索引数组（直接索引访问）</summary>
        private EntityIndexInWorld[] entities;  // 实体索引数组

        /// <summary>当前已创建的实体总数</summary>
        private int entityCount;  // 实体计数器
        public int EntityCount => entityCount;
        private int structuralVersion;
        public int StructuralVersion => structuralVersion;

        private bool _disposed;
        private readonly object _activeJobLock = new();
        private readonly List<JobHandle> _activeJobs = new();
        private readonly Dictionary<int, List<JobHandle>> _archetypeJobs = new(); // Per-Archetype Job Tracking
        private readonly Dictionary<JobHandle, ComponentType[]> _jobWrittenComponents = new(); // Job → written components
        private readonly object _structuralLock = new();  // 结构性操作（NewEntity/DestroyEntity/AddComponent/RemoveComponent）的锁
        // Native SendEvent 异步 drain 待处理列表（contextPtr, jobType）——Complete 后按本 World 的 EventStream 落盘
        internal readonly List<(IntPtr contextPtr, Type jobType)> _pendingNativeEvents = new();

        // 关系反向索引（target.Id → sources），Add/Remove/级联删除同步维护
        private RelationIndex _relationIndex = new();


        public EntityManager()
        {
            recycleEntities = new Queue<Entity>();  // 初始化回收队列
            entities = new EntityIndexInWorld[32];  // 初始实体数组
            archetypeMap = new Dictionary<int, List<Archetype>>();  // 初始化原型映射（哈希 -> Archetype 列表）
            allArchetypes = new Archetype[8];  // 初始原型数组
        }

        /// <summary>
        /// 根据给定的 <see cref="ComponentType"/> 数组 <paramref name="types"/> 获取或创建对应的 <see cref="Archetype"/>
        /// </summary>
        private Archetype GetOrCreateArchetype(Span<ComponentType> types)  // 获取或创建原型方法
        {
            var hash = Utils.CalculateHash(types);
            if (archetypeMap.TryGetValue(hash, out var archetypeList))  // 根据哈希值检查是否已存在
            {
                // 双重校验：用完整组件类型列表验证，防止哈希碰撞
                for (int i = 0; i < archetypeList.Count; i++)
                {
                    if (archetypeList[i].Types.SequenceEqual(types))
                        return archetypeList[i];
                }
            }
            else
            {
                archetypeList = new List<Archetype>(1);
                archetypeMap[hash] = archetypeList;
            }
            // 不存在具体匹配的 Archetype，创建新原型
            var archetype = new Archetype(types.ToArray());
            archetypeList.Add(archetype);
            //检查原型数组容量
            if (archetypeCount >= allArchetypes.Length)
            {
                Array.Resize(ref allArchetypes, allArchetypes.Length * 2);
            }
            // Todo: 如果有移除archetype的操作,空白的数组需要被填充
            allArchetypes[archetypeCount] = archetype;
            archetypeCount++;
            return archetype;
        }

        /// <summary>
        /// 获取所有原型数组
        /// </summary>
        public Archetype[] GetAllArchetypes()
        {
            return allArchetypes;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CompleteActiveJobs();
            _disposed = true;

            for (int i = 0; i < archetypeCount; i++)
            {
                allArchetypes[i]?.Dispose();
                allArchetypes[i] = null;
            }

            archetypeMap.Clear();
            recycleEntities.Clear();
            _lastChunkPerSharedValue.Clear();
            ChunkJobScheduler.ClearRawChunkScheduleCaches(this);
            _observers?.Clear();
            _observerCount = 0;
            // 用普通 new 分配的数组，直接丢弃即可
            entities = Array.Empty<EntityIndexInWorld>();
            allArchetypes = Array.Empty<Archetype>();
            archetypeCount = 0;
            entityCount = 0;
        }

        private void CheckDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(EntityManager));
        }

        internal void RegisterActiveJob(NativeJobHandle nativeHandle)
        {
            if (!nativeHandle.IsValid) return;
            lock (_activeJobLock)
            {
                PruneCompletedJobsNoLock();
                _activeJobs.Add(new JobHandle(nativeHandle));
            }
        }

        internal void CompleteActiveJobs()
        {
            if (NativeJobScheduler.IsExecutingJob)
            {
                throw new InvalidOperationException("Structural changes are not allowed while a scheduled job is executing. Complete the job before modifying entities or components.");
            }

            // Observer 重入保护：回调内再结构变更会死锁（_structuralLock 内 CompleteActiveJobs 等 job）。
            // 约定：回调内结构变更请走 DeferredCommandBuffer。
            if (s_observerDepth > 0)
            {
                throw new InvalidOperationException("Structural changes are not allowed inside an observer callback. Use DeferredCommandBuffer to defer structural changes.");
            }

            DrainPendingNativeEvents();

            JobHandle[] jobs;
            lock (_activeJobLock)
            {
                if (_activeJobs.Count == 0) return;
                jobs = _activeJobs.ToArray();
                _activeJobs.Clear();
            }

            // 即使某个 job 抛异常也必须完成剩余 job——否则它们从 _activeJobs 移除后
            // 仍挂在后台写内存，调用方继续推进会产生数据竞态。统一在全部完成后抛第一个。
            ExceptionDispatchInfo? pending = null;
            for (int i = 0; i < jobs.Length; i++)
            {
                try
                {
                    jobs[i].Complete();
                }
                catch (Exception ex)
                {
                    pending ??= ExceptionDispatchInfo.Capture(ex);
                }
            }
            pending?.Throw();
        }

        /// <summary>drain 本 World 的 Native SendEvent buffers（job 完成后事件落盘）。</summary>
        private void DrainPendingNativeEvents()
        {
            if (_pendingNativeEvents.Count == 0) return;
            foreach (var (contextPtr, jobType) in _pendingNativeEvents)
                JobSystem.ChunkJobScheduler.DrainAndFreeEventBuffers(contextPtr, World.DefaultWorld, jobType);
            _pendingNativeEvents.Clear();
        }

        private void PruneCompletedJobsNoLock()
        {
            for (int i = _activeJobs.Count - 1; i >= 0; i--)
            {
                if (_activeJobs[i].IsCompleted)
                {
                    _activeJobs.RemoveAt(i);
                }
            }
            // 同步清理 per-archetype 列表中的已完成 Job
            foreach (var kvp in _archetypeJobs)
            {
                var list = kvp.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].IsCompleted)
                        list.RemoveAt(i);
                }
            }
        }

        // ======================== Phase 3: Per-Archetype Job Tracking ========================

        /// <summary>
        /// 登记 Job 到全局列表 + per-archetype 列表。
        /// writtenComponents: Job 写了哪些组件（用于 Selective Wait 精度过滤）。
        /// </summary>
        internal void TrackEntityJob(JobHandle handle, Archetype[]? matchingArchetypes, ComponentType[]? writtenComponents = null)
        {
            if (!handle._nativeHandle.IsValid && handle._managedHandle.Completion == null) return;
            lock (_activeJobLock)
            {
                PruneCompletedJobsNoLock();
                _activeJobs.Add(handle);
                if (writtenComponents != null && writtenComponents.Length > 0)
                    _jobWrittenComponents[handle] = writtenComponents;
                if (matchingArchetypes != null)
                {
                    for (int i = 0; i < matchingArchetypes.Length; i++)
                    {
                        int id = matchingArchetypes[i].GetHashCode();
                        if (!_archetypeJobs.TryGetValue(id, out var list))
                        {
                            list = new List<JobHandle>();
                            _archetypeJobs[id] = list;
                        }
                        list.Add(handle);
                    }
                }
            }
        }

        /// <summary>
        /// 只等待访问受影响 Archetype 的 Job（Selective Wait）。
        /// affectedComponentTypes: 如果提供，只等待写入了这些组件的 Job（精确过滤）。
        /// </summary>
        internal void CompleteArchetypeJobs(Archetype[] affectedArchetypes, ComponentType[]? affectedComponentTypes = null)
        {
            if (NativeJobScheduler.IsExecutingJob)
                throw new InvalidOperationException("Structural changes are not allowed while a scheduled job is executing.");

            JobHandle[]? jobsToComplete = null;
            lock (_activeJobLock)
            {
                if (_activeJobs.Count == 0) return;

                // 收集受影响 Archetype 关联的 Job
                var handleSet = new HashSet<JobHandle>();
                for (int i = 0; i < affectedArchetypes.Length; i++)
                {
                    int id = affectedArchetypes[i].GetHashCode();
                    if (_archetypeJobs.TryGetValue(id, out var list))
                    {
                        for (int j = 0; j < list.Count; j++)
                        {
                            var handle = list[j];
                            // 如果指定了 affectedComponentTypes，过滤：只等写入了这些组件的 Job
                            if (affectedComponentTypes != null && affectedComponentTypes.Length > 0)
                            {
                                if (_jobWrittenComponents.TryGetValue(handle, out var written))
                                {
                                    // 检查是否有交集
                                    bool hasOverlap = false;
                                    for (int w = 0; w < written.Length; w++)
                                    {
                                        for (int a = 0; a < affectedComponentTypes.Length; a++)
                                        {
                                            if (written[w] == affectedComponentTypes[a])
                                            {
                                                hasOverlap = true;
                                                break;
                                            }
                                        }
                                        if (hasOverlap) break;
                                    }
                                    if (!hasOverlap) continue; // 没有写交集，跳过
                                }
                                // 没有 writtenComponents 信息的 Job，保守等待
                            }
                            handleSet.Add(handle);
                        }
                    }
                }

                if (handleSet.Count == 0) return;
                jobsToComplete = new JobHandle[handleSet.Count];
                handleSet.CopyTo(jobsToComplete);
            }

            // 执行等待（在锁外）
            ExceptionDispatchInfo? pending = null;
            for (int i = 0; i < jobsToComplete.Length; i++)
            {
                try { jobsToComplete[i].Complete(); }
                catch (Exception ex) { pending ??= ExceptionDispatchInfo.Capture(ex); }
            }
            pending?.Throw();

            // 清理已完成的 Job
            lock (_activeJobLock)
            {
                PruneCompletedJobsNoLock();
            }
        }

    }
    // Entity
    public unsafe partial class EntityManager
    {
        /// <summary>
        /// 给定index，返回实体引用
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref EntityIndexInWorld GetEntityInfoRef(int index)
        {
            if ((uint)index >= (uint)entities.Length)
                throw new IndexOutOfRangeException($"Entity index {index} is out of range (max {entities.Length - 1}).");
            return ref entities[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateEntityLocation(int entityId, Archetype archetype, int chunkIndex, int slotInChunk)
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entityId);
            entityInfoRef.Archetype = archetype;
            entityInfoRef.ChunkIndex = chunkIndex;
            entityInfoRef.SlotInChunk = slotInChunk;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateEntity(Entity entity)
        {
            if ((uint)entity.Id >= (uint)entities.Length)
                throw new InvalidOperationException($"Entity {entity} has an invalid ID.");
            ref var info = ref GetEntityInfoRef(entity.Id);
            if (info.Archetype == null)
                throw new InvalidOperationException($"Entity {entity} has been destroyed.");
            if (info.Version != entity.Version)
                throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");
        }

        private void RefreshChunkEntityIndices(Archetype archetype, int chunkIndex)
        {
            var chunk = archetype.ChunkList[chunkIndex];
            for (int slot = 0; slot < chunk.EntityCount; slot++)
            {
                UpdateEntityLocation(chunk.GetEntity(slot).Id, archetype, chunkIndex, slot);
            }
        }

        /// <summary>
        /// 创建新实体（基于组件类型）
        /// </summary>
        public Entity NewEntity(params Type[] componentTypes)
        {
            var componentSpan = new ComponentType[componentTypes.Length];  // 创建组件类型数组
            for (int i = 0; i < componentTypes.Length; i++)  // 遍历输入的组件类型
            {
                componentSpan[i] = ComponentTypeManager.GetComponentType(componentTypes[i]);  // 获取组件类型
            }
            return NewEntity(componentSpan.AsSpan());  // 调用核心实现
        }

        /// <summary>
        /// 创建新实体（基于ComponentType数组）
        /// </summary>
        public Entity NewEntity(params ComponentType[] types)  // 基于ComponentType创建实体
        {
            return NewEntity(types.AsSpan());  // 调用核心实现
        }

        /// <summary>
        /// 创建新实体核心实现
        /// </summary>
        public unsafe Entity NewEntity(Span<ComponentType> types)  // 创建实体核心方法
        {
            CheckDisposed();
            CompleteActiveJobs();  // NewEntity 需要知道目标 Archetype，但 GetOrCreateArchetype 需在锁内
            lock (_structuralLock)
            {
                var newEntity = new Entity();  // 创建新实体
                bool isRecycled = recycleEntities.TryDequeue(out var recycledEnt);  // 尝试从回收队列获取

                if (isRecycled)  // 使用回收的实体
                {
                    newEntity.Id = recycledEnt.Id;  // 复用ID
                    newEntity.Version = recycledEnt.Version + 1;  // 版本号递增
                }
                else  // 无可复用的实体，则创建新实体
                {
                    newEntity.Id = entityCount++;  // 分配新ID
                    if (newEntity.Id >= entities.Length)  // 检查数组容量
                    {
                        Array.Resize(ref entities, entities.Length * 2);  // 扩容数组
                    }
                }

                var targetArch = GetOrCreateArchetype(types);
                targetArch.AddEntity(newEntity, out var chunkIndex, out var slotInChunk);  // 在该实体对应的原型中添加实体
                structuralVersion++;

                // 更新该实体索引
                UpdateEntityLocation(newEntity.Id, targetArch, chunkIndex, slotInChunk);
                // 存储实体版本号用于悬垂引用检测
                GetEntityInfoRef(newEntity.Id).Version = newEntity.Version;

                // Observer：Added（单实体 NewEntity；新组件槽为初始零值；count=1 批量派发）
                if (_observerCount > 0 && _observers != null)
                {
                    foreach (var t in types)
                    {
                        if (_observers.TryGetValue(t.Id, out var reg) && reg.Added.Count > 0)
                        {
                            int compIdx = targetArch.GetComponentTypeIndex(t);
                            var chunk = targetArch.ChunkList[chunkIndex];
                            void* valPtr = (void*)(chunk.GetComponentArrayPointer(compIdx) + slotInChunk * t.Size);
                            DispatchAdded(reg.Added, &newEntity, valPtr, 1);
                        }
                    }
                }

                return newEntity;  // 返回新实体
            }
        }

        /// <summary>
        /// 批量创建实体：一次 Archetype 查找、一次批量添加、一次 CompleteActiveJobs。
        /// 比逐个 NewEntity 快 N 倍（N = 实体数），因为减少了锁和 CompleteActiveJobs 调用。
        /// </summary>
        public unsafe Entity[] CreateEntities(int count, params ComponentType[] types)
        {
            CheckDisposed();
            if (count <= 0) return Array.Empty<Entity>();
            CompleteActiveJobs();
            lock (_structuralLock)
            {
                var targetArch = GetOrCreateArchetype(types);
                var result = new Entity[count];

                for (int i = 0; i < count; i++)
                {
                    var newEntity = new Entity();
                    bool isRecycled = recycleEntities.TryDequeue(out var recycledEnt);
                    if (isRecycled)
                    {
                        newEntity.Id = recycledEnt.Id;
                        newEntity.Version = recycledEnt.Version + 1;
                    }
                    else
                    {
                        newEntity.Id = entityCount++;
                        if (newEntity.Id >= entities.Length)
                            Array.Resize(ref entities, entities.Length * 2);
                    }

                    targetArch.AddEntity(newEntity, out var chunkIndex, out var slotInChunk);
                    UpdateEntityLocation(newEntity.Id, targetArch, chunkIndex, slotInChunk);
                    GetEntityInfoRef(newEntity.Id).Version = newEntity.Version;
                    result[i] = newEntity;
                }

                structuralVersion++;

                // Observer：Added（批量合并——循环内收集，锁外一次派发）
                if (_observerCount > 0 && _observers != null)
                {
                    DispatchCreateEntitiesAdded(targetArch, types, result, count);
                }
                return result;
            }
        }

        /// <summary>
        /// CreateEntities 批量 Added 派发：按组件类型分桶，每类型一次回调（ReadOnlySpan）。
        /// 调用方保证在主线程、锁外（回调内结构变更需走 ECB）。
        /// </summary>
        private unsafe void DispatchCreateEntitiesAdded(Archetype targetArch, ComponentType[] types, Entity[] result, int count)
        {
            foreach (var t in types)
            {
                if (!_observers!.TryGetValue(t.Id, out var reg) || reg.Added.Count == 0) continue;

                int compIdx = targetArch.GetComponentTypeIndex(t);
                int compSize = t.Size;

                // 值不连续（实体跨 chunk），拷贝到连续缓冲
                byte* valuesPtr = null;
                var pooled = Array.Empty<byte>();
                int totalBytes = count * compSize;
                if (totalBytes <= 4096)
                {
                    byte* stackBuf = stackalloc byte[totalBytes == 0 ? 1 : totalBytes];
                    valuesPtr = stackBuf;
                }
                else
                {
                    pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(totalBytes);
                    fixed (byte* p = pooled)
                        valuesPtr = p;
                }

                try
                {
                    int write = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var e = result[i];
                        var loc = GetEntityInfoRef(e.Id);
                        var chunk = loc.Archetype!.ChunkList[loc.ChunkIndex];
                        void* srcPtr = (void*)(chunk.GetComponentArrayPointer(compIdx) + loc.SlotInChunk * compSize);
                        Unsafe.CopyBlock(valuesPtr + write, srcPtr, (uint)compSize);
                        write += compSize;
                    }

                    fixed (Entity* entitiesPtr = result)
                        DispatchAdded(reg.Added, entitiesPtr, valuesPtr, count);
                }
                finally
                {
                    if (pooled.Length > 0)
                        System.Buffers.ArrayPool<byte>.Shared.Return(pooled);
                }
            }
        }

        /// <summary>清除所有 archetype 所有 chunk 的变更标记（帧末调用）。</summary>
        public void ClearAllChangedBitMasks()
        {
            for (int i = 0; i < archetypeCount; i++)
                allArchetypes[i]?.ClearAllChangedBitMasks();
        }

        public void DestroyEntity(Entity entity)
        {
            CheckDisposed();
            // 确定源 Archetype，只等待该 Archetype 的 Job
            if ((uint)entity.Id < (uint)entities.Length)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                if (info.Archetype != null)
                    CompleteArchetypeJobs(new[] { info.Archetype });
                else
                    CompleteActiveJobs();
            }
            else
            {
                CompleteActiveJobs();
            }
            lock (_structuralLock)
            {
                DestroyEntityCore(entity);
            }
        }

        /// <summary>
        /// 销毁实体核心（调用方必须已持有 _structuralLock）。含 Observer Destroyed 派发。
        /// </summary>
        private unsafe void DestroyEntityCore(Entity entity)
        {
            if ((uint)entity.Id >= (uint)entities.Length)
                throw new InvalidOperationException($"Entity {entity} has an invalid ID.");
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            // 版本不匹配：旧句柄指向已回收再生的实体
            if (entityInfoRef.Version != entity.Version)
                throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");
            var archetype = entityInfoRef.Archetype;
            if (archetype == null)
            {
                return;
            }

            int oldChunkIndex = entityInfoRef.ChunkIndex;
            var oldArchetype = archetype;

            // 清理本实体作为 source 的所有关系索引条目（遍历其关系列）
            CleanupSourceRelations(entity, entityInfoRef, oldArchetype);

            // Observer：Destroyed（销毁前快照已订阅组件值；派发在销毁后，实体仍可解析）
            List<(int typeId, IntPtr snapshotPtr)>? destroyedSnapshots = null;
            if (_observerCount > 0 && _observers != null && oldArchetype != null)
            {
                destroyedSnapshots = new List<(int, IntPtr)>();
                foreach (var t in oldArchetype.Types)
                {
                    if (_observers.TryGetValue(t.Id, out var reg) && reg.Removed.Count > 0)
                    {
                        int compIdx = oldArchetype.GetComponentTypeIndex(t);
                        var chunk = oldArchetype.ChunkList[oldChunkIndex];
                        void* srcPtr = (void*)(chunk.GetComponentArrayPointer(compIdx) + entityInfoRef.SlotInChunk * t.Size);
                        var snapshot = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal(t.Size);
                        System.Runtime.CompilerServices.Unsafe.CopyBlock(snapshot, srcPtr, (uint)t.Size);
                        destroyedSnapshots.Add((t.Id, (IntPtr)snapshot));
                    }
                }
            }

            archetype.Remove(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, out var movedEntityId, out var movedEntitySlotInChunk, out var compactedChunkIndex);

            if (movedEntityId >= 0)
            {
                UpdateEntityLocation(movedEntityId, archetype, oldChunkIndex, movedEntitySlotInChunk);
            }

            if (compactedChunkIndex >= 0)
            {
                RefreshChunkEntityIndices(archetype, compactedChunkIndex);
            }

            entityInfoRef.Archetype = null;
            entityInfoRef.ChunkIndex = -1;
            entityInfoRef.SlotInChunk = -1;
            recycleEntities.Enqueue(entity);
            structuralVersion++;

            // Observer：派发 Destroyed（销毁后派发，实体仍可解析；用销毁前快照）
            if (destroyedSnapshots != null)
            {
                foreach (var (typeId, snapshotPtr) in destroyedSnapshots)
                {
                    try
                    {
                        if (_observers!.TryGetValue(typeId, out var reg) && reg.Removed.Count > 0)
                            DispatchRemoved(reg.Removed, &entity, (void*)snapshotPtr, 1);
                    }
                    finally
                    {
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(snapshotPtr);
                    }
                }
            }
        }

    }

    // Query
    //public unsafe partial class EntityManager
    //{
    //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //    public unsafe void Query<T0>(QueryBuilder builder, ISystem<T0> system)
    //        where T0 : struct
    //    {
    //        int entityCounter = 0; // 记录查询到的实体数量
    //        int limitCount = builder.LimitCount;

    //        for (int i = 0; i < archetypeCount; i++)
    //        {
    //            var archetype = allArchetypes[i];
    //            if (archetype != null && archetype.IsMatch(builder))
    //            {
    //                int t0Index = archetype.GetComponentTypeIndex<T0>();
    //                var chunks = archetype.GetChunks();
    //                var ArchtypeIndex = i;
    //                system.InArchetype(ArchtypeIndex);
    //                for (int j = 0; j < chunks.Count; j++)
    //                {
    //                    var chunk = chunks[j];
    //                    int count = chunk.EntityCount;
    //                    if (count == 0) continue;
    //                    var ChunkIndex = j;
    //                    system.InChunk(ArchtypeIndex, ChunkIndex);
    //                    Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();
    //                    T0* components = (T0*)chunk.GetComponentArrayPointer(t0Index).ToPointer();
    //                    {
    //                        system._execute(entities, components, count, limitCount - entityCounter, ArchtypeIndex, ChunkIndex);
    //                    }
    //                    entityCounter += count;
    //                    if (limitCount != -1 && entityCounter >= limitCount) break;
    //                }
    //            }
    //        }
    //    }

    //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //    public unsafe void Query<T0, T1>(QueryBuilder builder, ISystem<T0, T1> system)
    //        where T0 : struct
    //        where T1 : struct
    //    {
    //        int entityCounter = 0; // 记录查询到的实体数量
    //        int limitCount = builder.LimitCount;
    //        unchecked
    //        {

    //            for (int i = 0; i < archetypeCount; i++)
    //            {
    //                var archetype = allArchetypes[i];
    //                if (archetype != null && archetype.IsMatch(builder))
    //                {

    //                    int t0Index = archetype.GetComponentTypeIndex<T0>();
    //                    int t1Index = archetype.GetComponentTypeIndex<T1>();
    //                    var chunks = archetype.GetChunks();
    //                    var ArchtypeIndex = i;
    //                    system.InArchetype(ArchtypeIndex);
    //                    for (int j = 0; j < chunks.Count; j++)
    //                    {
    //                        var chunk = chunks[j];
    //                        int count = chunk.EntityCount;
    //                        if (count == 0) continue;
    //                        var ChunkIndex = j;
    //                        system.InChunk(ArchtypeIndex, ChunkIndex);
    //                        Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();
    //                        T0* components0 = (T0*)chunk.GetComponentArrayPointer(t0Index).ToPointer();
    //                        T1* components1 = (T1*)chunk.GetComponentArrayPointer(t1Index).ToPointer();
    //                        {
    //                            system._execute(entities, components0, components1, count, limitCount - entityCounter, ArchtypeIndex, ChunkIndex);
    //                        }

    //                        entityCounter += count;
    //                        if (limitCount != -1 && entityCounter >= limitCount) break;
    //                    }
    //                }
    //            }

    //            //allArchetypes[0].Query(system);

    //        }

    //    }



    //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //    public unsafe void MultiQuery<T0>(QueryBuilder builder, ISystem<T0> system)
    //        where T0 : struct
    //    {
    //        static void RunSystem(Chunk chunk, int t0Index, ISystem<T0> system, int LimitCount, int ArchetypeIndex, int ChunkIndex)
    //        {
    //            int count = chunk.EntityCount;
    //            Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();
    //            T0* components0 = (T0*)chunk.GetComponentArrayPointer(t0Index).ToPointer();
    //            system._execute(entities, components0, count, LimitCount, ArchetypeIndex, ChunkIndex);
    //        }

    //        unchecked
    //        {
    //            int entityCounter = 0;
    //            int limitCount = builder.LimitCount;

    //            for (int i = 0; i < archetypeCount; i++)
    //            {
    //                var archetype = allArchetypes[i];
    //                if (archetype != null && archetype.IsMatch(builder))
    //                {
    //                    int t0Index = archetype.GetComponentTypeIndex<T0>();
    //                    List<Task> tasks = new();

    //                    var ArchtypeIndex = i;
    //                    system.InArchetype(ArchtypeIndex);

    //                    var chunks = archetype.GetChunks();
    //                    for (int j = 0; j < chunks.Count; j++)
    //                    {
    //                        var chunk = chunks[j];
    //                        int count = chunk.EntityCount;
    //                        if (count == 0) continue;
    //                        int spareCount = limitCount - entityCounter;
    //                        int ChunkIndex = j;

    //                        system.InChunk(ArchtypeIndex, ChunkIndex);
    //                        Task task = Task.Run(() =>
    //                        {
    //                            RunSystem(chunk, t0Index, system, spareCount, ArchtypeIndex, ChunkIndex);
    //                        }
    //                        );

    //                        tasks.Add(task);
    //                        entityCounter += count;
    //                        if (limitCount != -1 && entityCounter >= limitCount) break;

    //                    }
    //                    Task.WaitAll(tasks.ToArray());
    //                }
    //            }
    //        }
    //    }
    //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //    public unsafe void MultiQuery<T0, T1>(QueryBuilder builder, ISystem<T0, T1> system)
    //        where T0 : struct
    //        where T1 : struct
    //    {
    //        void RunSystem(Chunk chunk, int t0Index, int t1Index, ISystem<T0, T1> system, int LimitCount, int ArchetypeCount, int ChunkIndex)
    //        {
    //            int count = chunk.EntityCount;


    //            Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();
    //            T0* components0 = (T0*)chunk.GetComponentArrayPointer(t0Index).ToPointer();
    //            T1* components1 = (T1*)chunk.GetComponentArrayPointer(t1Index).ToPointer();
    //            system._execute(entities, components0, components1, count, LimitCount, ArchetypeCount, ChunkIndex);
    //        }

    //        unchecked
    //        {
    //            int entityCounter = 0;
    //            int limitCount = builder.LimitCount;

    //            for (int i = 0; i < archetypeCount; i++)
    //            {
    //                var archetype = allArchetypes[i];
    //                if (archetype != null && archetype.IsMatch(builder))
    //                {
    //                    int t0Index = archetype.GetComponentTypeIndex<T0>();
    //                    int t1Index = archetype.GetComponentTypeIndex<T1>();

    //                    List<Task> tasks = new();

    //                    var ArchtypeIndex = i;
    //                    system.InArchetype(ArchtypeIndex);

    //                    var chunks = archetype.GetChunks();
    //                    for (int j = 0; j < chunks.Count; j++)
    //                    {
    //                        var chunk = chunks[j];
    //                        int count = chunk.EntityCount;
    //                        if (count == 0) continue;
    //                        int spareCount = limitCount - entityCounter;

    //                        var ChunkIndex = j;

    //                        system.InChunk(ArchtypeIndex, ChunkIndex);

    //                        Task task = Task.Run(() =>
    //                        {
    //                            RunSystem(chunk, t0Index, t1Index, system, spareCount, ArchtypeIndex, ChunkIndex);
    //                        }
    //                        );

    //                        tasks.Add(task);
    //                        //task.Start();

    //                        entityCounter += count;
    //                        if (limitCount != -1 && entityCounter >= limitCount) break;

    //                    }
    //                    Task.WaitAll(tasks.ToArray());
    //                    //Task.WhenAll(tasks.Select(t => t.AsTask()));
    //                }
    //            }
    //        }
    //    }

    //    //[MethodImpl(MethodImplOptions.AggressiveInlining)]
    //    //private IEnumerable<Archetype> GetMatchingArchetypes(QueryBuilder builder)
    //    //{
    //    //    for (int i = 0; i < archetypeCount; i++)
    //    //    {
    //    //        var arch = allArchetypes[i];
    //    //        if (arch != null && arch.IsMatch(builder))
    //    //        {
    //    //            yield return arch;
    //    //        }
    //    //    }
    //    //}
    //}

    // Component
    public unsafe partial class EntityManager
    {
        /// <summary>
        /// 添加组件（泛型版本，调用 AddComponentRaw）
        /// </summary>
        public void AddComponent<T0>(Entity entity, T0 t0) where T0 : struct
        {
            AddComponentRaw(entity, typeof(T0), t0);
        }


        /// <summary>
        /// 移除组件（泛型版本，调用 RemoveComponentRaw）
        /// </summary>
        public void RemoveComponent<T0>(Entity entity) where T0 : struct
        {
            RemoveComponentRaw(entity, typeof(T0));
        }

        // ======================== 非泛型方法（供 ECB Playback 使用） ========================

        /// <summary>
        /// 添加组件（非泛型版本，核心实现）
        /// </summary>
        public unsafe void AddComponentRaw(Entity entity, Type componentType, object value)
        {
            CheckDisposed();
            if ((uint)entity.Id < (uint)entities.Length)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                if (info.Archetype != null)
                    CompleteArchetypeJobs(new[] { info.Archetype });
                else
                    CompleteActiveJobs();
            }
            else
            {
                CompleteActiveJobs();
            }
            lock (_structuralLock)
            {
                AddComponentRawCore(entity, componentType, value);
            }
        }

        /// <summary>
        /// 添加组件核心（调用方必须已持有 _structuralLock）。
        /// </summary>
        private unsafe void AddComponentRawCore(Entity entity, Type componentType, object value)
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            if (entityInfoRef.Archetype == null)
                throw new InvalidOperationException($"Entity {entity} has been destroyed.");
            if (entityInfoRef.Version != entity.Version)
                throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");
            var oldArch = entityInfoRef.Archetype;
            if (oldArch.Has(componentType))
            {
                oldArch.SetRaw(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, componentType, value);
                return;
            }

            // Phase 2.1: 走 Add Edge 快路径
            var compType = ComponentTypeManager.GetComponentType(componentType);
            var targetArch = oldArch.GetAddEdge(compType);
            if (targetArch == null)
            {
                Span<ComponentType> targetComponents = stackalloc ComponentType[oldArch.ComponentCount + 1];
                oldArch.Types.CopyTo(targetComponents);
                targetComponents[^1] = compType;
                targetArch = GetOrCreateArchetype(targetComponents);
                oldArch.SetAddEdge(compType, targetArch);
            }
            targetArch.AddEntity(entity, out var chunkIndex, out var slotInChunk);

            // 复制组件数据
            oldArch.CopyComponentsTo(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, targetArch, chunkIndex, slotInChunk);

            // 从旧原型移除
            oldArch.Remove(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, out var movedEntityID, out var movedEntitySlotInChunk, out var compactedChunkIndex);

            if (movedEntityID >= 0)
                UpdateEntityLocation(movedEntityID, oldArch, entityInfoRef.ChunkIndex, movedEntitySlotInChunk);

            if (compactedChunkIndex >= 0)
                RefreshChunkEntityIndices(oldArch, compactedChunkIndex);

            UpdateEntityLocation(entity.Id, targetArch, chunkIndex, slotInChunk);
            targetArch.SetRaw(chunkIndex, slotInChunk, componentType, value);
            structuralVersion++;

            // Observer：Added（迁移完成后派发，回调内实体可查、新组件已存在；count=1）
            if (_observerCount > 0 && _observers != null &&
                _observers.TryGetValue(compType.Id, out var reg) && reg.Added.Count > 0)
            {
                int compIdx = targetArch.GetComponentTypeIndex(compType);
                void* valPtr = (void*)(targetArch.ChunkList[chunkIndex].GetComponentArrayPointer(compIdx) + slotInChunk * compType.Size);
                DispatchAdded(reg.Added, &entity, valPtr, 1);
            }
        }

        /// <summary>
        /// 移除组件（非泛型版本，核心实现）
        /// </summary>
        public void RemoveComponentRaw(Entity entity, Type componentType)
        {
            CheckDisposed();
            if ((uint)entity.Id < (uint)entities.Length)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                if (info.Archetype != null)
                    CompleteArchetypeJobs(new[] { info.Archetype });
                else
                    CompleteActiveJobs();
            }
            else
            {
                CompleteActiveJobs();
            }
            lock (_structuralLock)
            {
                RemoveComponentRawCore(entity, componentType);
            }
        }

        /// <summary>
        /// 移除组件核心（调用方必须已持有 _structuralLock）。
        /// </summary>
        private unsafe void RemoveComponentRawCore(Entity entity, Type componentType)
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
                if (entityInfoRef.Archetype == null)
                    throw new InvalidOperationException($"Entity {entity} has been destroyed.");
                if (entityInfoRef.Version != entity.Version)
                    throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");
                var oldArch = entityInfoRef.Archetype;
                if (!oldArch.Has(componentType))
                    return;

                // Observer：Removed（迁移前快照旧值；派发在迁移完成后，回调内实体仍可查）
                byte* oldValueSnapshot = null;
                if (_observerCount > 0 && _observers != null)
                {
                    var compTypeForObs = ComponentTypeManager.GetComponentType(componentType);
                    if (_observers.TryGetValue(compTypeForObs.Id, out var reg) &&
                        (reg.Removed.Count > 0))
                    {
                        int compIdx = oldArch.GetComponentTypeIndex(compTypeForObs);
                        var chunk = oldArch.ChunkList[entityInfoRef.ChunkIndex];
                        void* srcPtr = (void*)(chunk.GetComponentArrayPointer(compIdx) + entityInfoRef.SlotInChunk * compTypeForObs.Size);
                        oldValueSnapshot = (byte*)Marshal.AllocHGlobal(compTypeForObs.Size);
                        Unsafe.CopyBlock(oldValueSnapshot, srcPtr, (uint)compTypeForObs.Size);
                    }
                }

                // Phase 2.1: 走 Remove Edge 快路径
                var compType = ComponentTypeManager.GetComponentType(componentType);
                var targetArch = oldArch.GetRemoveEdge(compType);
                if (targetArch == null)
                {
                    Span<ComponentType> targetComponents = stackalloc ComponentType[oldArch.ComponentCount - 1];
                    int idx = 0;
                    foreach (var t in oldArch.Types)
                    {
                        if (t.Id != compType.Id)
                            targetComponents[idx++] = t;
                    }
                    targetArch = GetOrCreateArchetype(targetComponents);
                    oldArch.SetRemoveEdge(compType, targetArch);
                }
                targetArch.AddEntity(entity, out var chunkIndex, out var slotInChunk);

                oldArch.CopyComponentsTo(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, targetArch, chunkIndex, slotInChunk);
                oldArch.Remove(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, out var movedEntityID, out var movedEntitySlotInChunk, out var compactedChunkIndex);

                if (movedEntityID >= 0)
                    UpdateEntityLocation(movedEntityID, oldArch, entityInfoRef.ChunkIndex, movedEntitySlotInChunk);

                if (compactedChunkIndex >= 0)
                    RefreshChunkEntityIndices(oldArch, compactedChunkIndex);

                UpdateEntityLocation(entity.Id, targetArch, chunkIndex, slotInChunk);
                targetArch.ChunkList[chunkIndex].MarkEntityChanged(slotInChunk);
                structuralVersion++;

                // Observer：Removed 派发（迁移完成后，回调内实体在目标 archetype 可查；count=1）
                if (oldValueSnapshot != null)
                {
                    try
                    {
                        var compTypeForObs = ComponentTypeManager.GetComponentType(componentType);
                        if (_observers!.TryGetValue(compTypeForObs.Id, out var reg) && reg.Removed.Count > 0)
                            DispatchRemoved(reg.Removed, &entity, oldValueSnapshot, 1);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal((IntPtr)oldValueSnapshot);
                    }
                }
            }

        /// <summary>
        /// 设置组件值（泛型版本，调用 SetRaw）
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(Entity entity, T t) where T : struct, IComponentData
        {
            SetRaw(entity, typeof(T), t);
        }

        /// <summary>
        /// 设置组件值（非泛型版本，核心实现）
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void SetRaw(Entity entity, Type componentType, object value)
        {
            CheckDisposed();
            if ((uint)entity.Id < (uint)entities.Length)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                if (info.Archetype != null)
                    CompleteArchetypeJobs(new[] { info.Archetype });
                else
                    CompleteActiveJobs();
            }
            else
            {
                CompleteActiveJobs();
            }
            lock (_structuralLock)
            {
                ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
                if (entityInfoRef.Archetype == null)
                    throw new InvalidOperationException($"Entity {entity} has been destroyed.");
                if (entityInfoRef.Version != entity.Version)
                    throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");
                var arch = entityInfoRef.Archetype;
                arch.SetRaw(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, componentType, value);

                // Observer：Set（写入后派发，NewValue 用写入值；v1 不提供 OldValue；count=1）
                if (_observerCount > 0 && _observers != null)
                {
                    var compTypeForObs = ComponentTypeManager.GetComponentType(componentType);
                    if (_observers.TryGetValue(compTypeForObs.Id, out var reg) && reg.Set.Count > 0)
                    {
                        var valueHandle = GCHandle.Alloc(value, GCHandleType.Pinned);
                        try
                        {
                            // Set 桶复用 DispatchAdded（新值语义）
                            DispatchAdded(reg.Set, &entity, (void*)valueHandle.AddrOfPinnedObject(), 1);
                        }
                        finally
                        {
                            valueHandle.Free();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 读取组件值（返回引用，OOD 稀疏访问面）。
        /// 返回的 ref 在下次结构变更（add/remove/destroy → 实体迁移 chunk）前有效，与 <see cref="EntityIndexInWorld"/> 同一纪律。
        /// 读路径 lock-free（main-thread only 纪律）：结构性 API 先 CompleteActiveJobs 且 IsExecutingJob 时抛异常，
        /// 结构性变更不可能与读并发；多线程场景可定义 ENTJOY_SAFE_ENTITY_READS 恢复锁。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(Entity entity) where T : struct, IComponentData
        {
            CheckDisposed();
#if ENTJOY_SAFE_ENTITY_READS
            lock (_structuralLock)
#endif
            {
                ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
                if (entityInfoRef.Archetype == null)
                    throw new InvalidOperationException($"Entity {entity} has been destroyed.");
                if (entityInfoRef.Version != entity.Version)
                    throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");
                var arch = entityInfoRef.Archetype;
                return ref arch.GetComponent<T>(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk);
            }
        }

        /// <summary>
        /// 创建稀疏随机访问句柄（对齐 Unity ComponentLookup）。普通 struct，可作系统字段；
        /// main-thread only，持有本 EntityManager 强引用。
        /// </summary>
        public unsafe ComponentLookup<T> GetComponentLookup<T>() where T : struct
            => new ComponentLookup<T>(this);
    }

    public unsafe partial class EntityManager
    {
        #region Enableable Components

        /// <summary>
        /// 设置指定实体上 enableable 组件的启用状态。
        /// </summary>
        /// <typeparam name="T">实现了 IEnableableComponent 的组件类型</typeparam>
        /// <param name="entity">目标实体</param>
        /// <param name="enabled">true 为启用，false 为禁用</param>
        /// <exception cref="InvalidOperationException">如果实体不包含该组件，或组件不可 enable</exception>
#pragma warning disable CS0618 // 保留旧接口兼容
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponentEnabled<T>(Entity entity, bool enabled) where T : struct, IEnableableComponent
        {
            CheckDisposed();
            // 确定当前 Archetype，只等待该 Archetype 的 Job
            if ((uint)entity.Id < (uint)entities.Length)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                if (info.Archetype != null)
                    CompleteArchetypeJobs(new[] { info.Archetype });
                else
                    CompleteActiveJobs();
            }
            else
            {
                CompleteActiveJobs();
            }
            lock (_structuralLock)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                var archetype = info.Archetype;

                if (archetype == null)
                    throw new InvalidOperationException($"Entity {entity} has been destroyed.");
                if (info.Version != entity.Version)
                    throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");

                // 检查实体是否拥有该组件
                if (!archetype.Has(typeof(T)))
                    throw new InvalidOperationException($"Entity {entity} does not have component {typeof(T).Name}.");

                int compIdx = archetype.GetComponentTypeIndex<T>();
                var chunk = archetype.ChunkList[info.ChunkIndex];

                chunk.SetComponentEnabled(compIdx, info.SlotInChunk, enabled);
            }
        }

        /// <summary>
        /// 获取指定实体上 enableable 组件的当前启用状态。
        /// </summary>
        /// <typeparam name="T">实现了 IEnableableComponent 的组件类型</typeparam>
        /// <param name="entity">目标实体</param>
        /// <returns>true 表示启用，false 表示禁用</returns>
        /// <exception cref="InvalidOperationException">如果实体不包含该组件，或组件不可 enable</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsComponentEnabled<T>(Entity entity) where T : struct, IEnableableComponent
        {
            CheckDisposed();
#if ENTJOY_SAFE_ENTITY_READS
            lock (_structuralLock)
#endif
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                var archetype = info.Archetype;

                if (archetype == null)
                    throw new InvalidOperationException($"Entity {entity} has been destroyed.");
                if (info.Version != entity.Version)
                    throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");

                if (!archetype.Has(typeof(T)))
                    throw new InvalidOperationException($"Entity {entity} does not have component {typeof(T).Name}.");

                int compIdx = archetype.GetComponentTypeIndex<T>();
                var chunk = archetype.ChunkList[info.ChunkIndex];

                return chunk.GetComponentEnabled(compIdx, info.SlotInChunk);
            }
        }

        #endregion
    }






}
