using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using EntJoy.Collections;
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
        /// <summary>
        /// 实体 Id 回收栈（**非托管 LIFO**，P0-4c）。
        /// 原先用 <c>Queue&lt;Entity&gt;</c>：1M 级销毁/重生会把托管队列扩到 ~1M 项
        /// （实测批量销毁 100k 时回放期分配 **3.2MB**，全部来自队列扩容）。
        /// 换成非托管栈后销毁/创建路径**零托管分配**；语义由 FIFO 变 LIFO（最近销毁的 Id 优先复用），
        /// 与 GPU 版实体池（freeList 栈）一致。
        /// </summary>
        private unsafe Entity* _recycleStack;
        private int _recycleCount;
        private int _recycleCapacity;

        private unsafe bool TryPopRecycled(out Entity entity)
        {
            if (_recycleCount > 0)
            {
                entity = _recycleStack[--_recycleCount];
                return true;
            }
            entity = default;
            return false;
        }

        private unsafe void PushRecycled(Entity entity)
        {
            EnsureRecycleCapacity(_recycleCount + 1);
            _recycleStack[_recycleCount++] = entity;
        }

        private void ClearRecycled() => _recycleCount = 0;

        private unsafe void EnsureRecycleCapacity(int needed)
        {
            if (needed <= _recycleCapacity) return;
            int newCapacity = _recycleCapacity > 0 ? _recycleCapacity : 1024;
            while (newCapacity < needed) newCapacity *= 2;
            var next = (Entity*)System.Runtime.InteropServices.Marshal.AllocHGlobal(newCapacity * sizeof(Entity));
            if (_recycleStack != null)
            {
                Buffer.MemoryCopy(_recycleStack, next, (long)newCapacity * sizeof(Entity), (long)_recycleCount * sizeof(Entity));
                System.Runtime.InteropServices.Marshal.FreeHGlobal((IntPtr)_recycleStack);
            }
            _recycleStack = next;
            _recycleCapacity = newCapacity;
        }

        /// <summary>实体索引数组（直接索引访问）</summary>
        private EntityIndexInWorld[] entities;  // 实体索引数组

        /// <summary>
        /// **blittable 实体定位表**（索引 = 实体 Id），与 <see cref="entities"/> **在同一点更新**
        /// （<see cref="UpdateEntityLocation"/> / <see cref="RefreshChunkEntityIndices"/>）：
        /// 每项记录 chunk 数据块基址 + 该 Archetype 的非托管列偏移表 + slot + version。
        /// 用途：让并行 job 与 NativeTranspile 生成的 C++ 内核做**跨 chunk 随机访问**
        /// （托管 <see cref="EntityIndexInWorld"/> 含托管 Archetype 引用，原生读不到）。
        /// 容量随 <see cref="entities"/> 扩容（见 <see cref="EnsureLocateCapacity"/>）。
        /// </summary>
        private unsafe EntityLocateB* _locateB;

        /// <summary><see cref="_locateB"/> 的容量（= entities.Length；0 表示尚未分配）。</summary>
        private int _locateBCapacity;

        /// <summary>下一个 Archetype 编号（<see cref="Archetype.ArchetypeId"/> 的分配源）。</summary>
        private int _nextArchetypeId;

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
        // Native SendEvent 异步 drain 待处理列表（contextPtr, jobType, world）——记录 world 归属，
        // 避免多 World 下依赖全局 DefaultWorld 导致事件写错 EventStream 或丢失。
        internal readonly List<(IntPtr contextPtr, Type jobType, World world)> _pendingNativeEvents = new();
        internal readonly object _pendingNativeEventsLock = new();

        // 系统间依赖跟踪：组件类型 Id → 最后写入它的 Job（帧内跨系统传播，DOTS EntityDependencyManager 语义）。
        // 由 SystemRunner 在每个系统结束后按 [Write] 声明维护；CompleteActiveJobs（全量）后清空。
        private readonly Dictionary<int, JobHandle> _lastWritePerComponent = new();

        // 关系反向索引（target.Id → sources），Add/Remove/级联删除同步维护
        private RelationIndex _relationIndex = new();
        // 多实例关系（[MultiRelation]）正向存储（source → targets 列表）；反向复用 _relationIndex
        private readonly RelationListStore _relationListStore = new();


        public EntityManager()
        {
            _recycleCapacity = 0;
            _recycleCount = 0;
            _recycleStack = (Entity*)System.Runtime.InteropServices.Marshal.AllocHGlobal(1024 * sizeof(Entity));
            _recycleCapacity = 1024;
            entities = new EntityIndexInWorld[32];  // 初始实体数组
            _locateB = NativeLocateStorage.AllocateLocate(entities.Length);
            _locateBCapacity = entities.Length;
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
            archetype.ArchetypeId = _nextArchetypeId++;
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
            var result = new Archetype[archetypeCount];
            Array.Copy(allArchetypes, result, archetypeCount);
            return result;
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
            ClearRecycled();
            if (_recycleStack != null)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal((IntPtr)_recycleStack);
                _recycleStack = null;
                _recycleCapacity = 0;
                _recycleCount = 0;
            }
            _lastChunkPerSharedValue.Clear();
            ChunkJobScheduler.ClearRawChunkScheduleCaches(this);
            _observers?.Clear();
            _observerCount = 0;
            // 用普通 new 分配的数组，直接丢弃即可
            entities = Array.Empty<EntityIndexInWorld>();
            NativeLocateStorage.Free(_locateB);
            _locateB = null;
            _locateBCapacity = 0;
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

            // 全量等待后所有 Job 完成，系统间依赖表随之失效。
            _lastWritePerComponent.Clear();

            JobHandle[] jobs;
            lock (_activeJobLock)
            {
                if (_activeJobs.Count == 0)
                {
                    // 无在飞 Job 时也要 drain 遗留事件（如 schedule 后立即 dispose 的边界）
                    DrainPendingNativeEvents();
                    return;
                }
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

            // 关键顺序：先等所有 Job 完成，再 drain/free 事件 buffer——
            // 否则 worker 仍在写 buffer 时主线程已读计数并释放 dataPtr/countPtr → use-after-free。
            DrainPendingNativeEvents();

            pending?.Throw();
        }

        /// <summary>drain 本 World 的 Native SendEvent buffers（job 完成后事件落盘）。</summary>
        private void DrainPendingNativeEvents()
        {
            List<(IntPtr contextPtr, Type jobType, World world)> pending;
            lock (_pendingNativeEventsLock)
            {
                if (_pendingNativeEvents.Count == 0) return;
                pending = new List<(IntPtr contextPtr, Type jobType, World world)>(_pendingNativeEvents);
                _pendingNativeEvents.Clear();
            }
            Exception first = null;
            foreach (var (contextPtr, jobType, world) in pending)
            {
                try { JobSystem.ChunkJobScheduler.DrainAndFreeEventBuffers(contextPtr, world, jobType); }
                catch (Exception ex) { first ??= ex; }
            }
            if (first != null) throw first;
        }

        /// <summary>记录某组件类型的最后写入 Job（系统结束时由 SystemRunner 调用；空句柄即清除）。</summary>
        internal void SetLastWriter(ComponentType componentType, JobHandle handle)
        {
            if (handle.IsNull) _lastWritePerComponent.Remove(componentType.Id);
            else _lastWritePerComponent[componentType.Id] = handle;
        }

        /// <summary>查询某组件类型的最后写入 Job（无记录返回空句柄）。</summary>
        internal JobHandle GetLastWriter(ComponentType componentType)
        {
            return _lastWritePerComponent.TryGetValue(componentType.Id, out var h) ? h : default;
        }

        /// <summary>
        /// 结构变更前等待访问该实体相关 Archetype 的在飞 Job。extra 为组件迁移目标 Archetype
        /// （Add/Remove 组件时新旧 Archetype 都需等待）；实体已销毁或越界则退化为全量等待。
        /// </summary>
        private void CompleteEntityJobs(Entity entity, Archetype? extra = null)
        {
            if ((uint)entity.Id >= (uint)entities.Length)
            {
                CompleteActiveJobs();
                return;
            }
            ref var info = ref GetEntityInfoRef(entity.Id);
            if (info.Archetype == null)
            {
                CompleteActiveJobs();
                return;
            }
            if (extra == null || extra == info.Archetype)
            {
                // 零分配单元素重载（P0-4c）：原先这里每次 new[] { archetype } ⇒ 32B/实体，
                // 批量销毁 100k 实测 3.2MB 分配全出在这一行。
                Archetype single = info.Archetype;
                CompleteArchetypeJobs(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref single, 1));
            }
            else
            {
                CompleteArchetypeJobs(new[] { info.Archetype, extra });   // 罕见路径（迁移/双 archetype）
            }
        }

        /// <summary>从 Archetype 移除实体槽位，并修正 swap-pop 搬移实体与空 chunk 压缩后的位置索引。</summary>
        private void RemoveAndFixup(Archetype arch, int chunkIndex, int slotInChunk)
        {
            var result = arch.Remove(chunkIndex, slotInChunk);
            if (result.MovedEntityId >= 0)
                UpdateEntityLocation(result.MovedEntityId, arch, chunkIndex, result.MovedEntitySlot);
            if (result.CompactedChunkIndex >= 0)
                RefreshChunkEntityIndices(arch, result.CompactedChunkIndex);
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
        internal void CompleteArchetypeJobs(ReadOnlySpan<Archetype> affectedArchetypes, ComponentType[]? affectedComponentTypes = null)
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
            // blittable 镜像：与托管表同一处更新（这里 + RefreshChunkEntityIndices 是仅有的两个写入点）
            // ⚠ 惰性扩容守卫：**不依赖调用方是否记得同步扩容定位表**（实测踩过：SharedComponent.cs 里
            //   还有第 5 个 Array.Resize(ref entities) 站点漏了同步 ⇒ 超出旧容量的实体读到越界/零值）。
            if ((uint)entityId >= (uint)_locateBCapacity) EnsureLocateCapacity(entityId + 1);
            {
                Chunk chunk = archetype.ChunkList[chunkIndex];
                ref var loc = ref _locateB[entityId];
                loc.ChunkMemory = (void*)chunk.MemoryBlock;
                loc.ChunkOffsets = archetype.GetChunkOffsetsNative();
                loc.SlotInChunk = slotInChunk;
            }
        }

        /// <summary>把实体版本号写进 blittable 定位表（版本号在 <see cref="UpdateEntityLocation"/> 之后单独写入）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetLocateBVersion(int entityId, int version)
        {
            if ((uint)entityId >= (uint)_locateBCapacity) EnsureLocateCapacity(entityId + 1);
            _locateB[entityId].Version = version;
        }

        /// <summary>清空某实体在 blittable 定位表中的项（销毁路径）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClearLocateB(int entityId)
        {
            if ((uint)entityId < (uint)_locateBCapacity)
            {
                ref var loc = ref _locateB[entityId];
                loc.ChunkMemory = null;
                loc.ChunkOffsets = null;
                loc.SlotInChunk = -1;
            }
        }

        /// <summary>
        /// 扩容托管实体表（**唯一入口**）：与 blittable 定位表**同步**扩容。
        /// 纪律：任何地方都不应直接 <c>Array.Resize(ref entities, …)</c> —— 漏掉定位表同步会让
        /// 超出旧容量的实体在定位表里读到越界/零值（实测症状：VerifyLocateTable 报不一致，
        /// 且不一致项的 id 恒大于 <see cref="LocateCapacity"/>）。
        /// </summary>
        private void ResizeEntityTable(int newLength)
        {
            if (newLength <= entities.Length) return;
            Array.Resize(ref entities, newLength);
            EnsureLocateCapacity(entities.Length);
        }

        /// <summary>blittable 定位表随实体数组一起扩容（4 处 Array.Resize(ref entities, …) 之后调用）。</summary>
        private void EnsureLocateCapacity(int needed)
        {
            if (needed <= _locateBCapacity) return;
            int newCapacity = _locateBCapacity > 0 ? _locateBCapacity : 32;
            while (newCapacity < needed) newCapacity *= 2;
            _locateB = NativeLocateStorage.GrowLocate(_locateB, _locateBCapacity, newCapacity);
            _locateBCapacity = newCapacity;
        }

        // ======================== blittable 定位表的对外访问（job / 原生内核） ========================

        /// <summary>blittable 定位表首址（索引 = 实体 Id）；长度 = <see cref="LocateCapacity"/>。</summary>
        public unsafe EntityLocateB* GetEntityLocatePtr() => _locateB;

        /// <summary>blittable 定位表容量（实体 Id 上界）。</summary>
        public int LocateCapacity => _locateBCapacity;

        /// <summary>
        /// 构造 job-safe 的组件随机访问句柄（值语义，可作为 job 字段传给并行 job / 原生内核）。
        /// </summary>
        /// <param name="archetype">目标组件所在的 Archetype（用于预解析组件列下标）。</param>
        public unsafe NativeComponentLookup<T> CreateNativeLookup<T>(Archetype archetype) where T : unmanaged
        {
            return new NativeComponentLookup<T>
            {
                Locate = _locateB,
                ComponentIndex = archetype.GetComponentTypeIndex<T>(),
                Length = _locateBCapacity,
            };
        }

        /// <summary>构造 job-safe 的位置查询句柄（实体 → chunk 基址 / slot / version）。</summary>
        public unsafe NativeEntityLookup CreateNativeEntityLookup()
            => new NativeEntityLookup { Locate = _locateB, Length = _locateBCapacity };

        /// <summary>
        /// **探针**：逐实体比对 blittable 定位表 vs 托管 <see cref="EntityIndexInWorld"/>，
        /// 返回不一致项数（0 = 一致）。遍历全部 Archetype/Chunk/Slot，覆盖
        /// "结构变更（NewEntity/DestroyEntity/Add/Remove/空 chunk 压缩）之后"的一致性判据。
        /// </summary>
        public unsafe long VerifyLocateTable(out int checkedEntities)
        {
            long mismatch = 0;
            int seen = 0;
            for (int a = 0; a < archetypeCount; a++)
            {
                var arch = allArchetypes[a];
                if (arch == null) continue;
                int* offsets = arch.GetChunkOffsetsNative();
                for (int c = 0; c < arch.ChunkList.Count; c++)
                {
                    var chunk = arch.ChunkList[c];
                    for (int slot = 0; slot < chunk.EntityCount; slot++)
                    {
                        int id = chunk.GetEntity(slot).Id;
                        if ((uint)id >= (uint)_locateBCapacity) { mismatch++; continue; }
                        ref var loc = ref _locateB[id];
                        var info = entities[id];
                        seen++;
                        if (loc.ChunkMemory != (void*)chunk.MemoryBlock) mismatch++;
                        else if (loc.ChunkOffsets != offsets) mismatch++;
                        else if (loc.SlotInChunk != slot) mismatch++;
                        else if (info.Archetype != arch || info.ChunkIndex != c || info.SlotInChunk != slot) mismatch++;
                        else if (loc.Version != info.Version) mismatch++;
                    }
                }
            }
            checkedEntities = seen;
            return mismatch;
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
                bool isRecycled = TryPopRecycled(out var recycledEnt);  // 尝试从回收栈获取

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
                        EnsureLocateCapacity(entities.Length);
                    }
                }

                var targetArch = GetOrCreateArchetype(types);
                targetArch.AddEntity(newEntity, out var chunkIndex, out var slotInChunk);  // 在该实体对应的原型中添加实体
                structuralVersion++;

                // 更新该实体索引
                UpdateEntityLocation(newEntity.Id, targetArch, chunkIndex, slotInChunk);
                // 存储实体版本号用于悬垂引用检测
                GetEntityInfoRef(newEntity.Id).Version = newEntity.Version;
                SetLocateBVersion(newEntity.Id, newEntity.Version);

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
        /// **批量创建的零分配版本**（P1-8）：与 <see cref="CreateEntities(int, ComponentType[])"/> 同一路径，
        /// 但把新实体写进调用方提供的**非托管缓冲**而非托管 <c>Entity[]</c>。
        /// 用途：每步生成大批单位（本项目 65,536/步）时避免 512KB/步 的托管分配，并给 ECB 批量回放用。
        /// </summary>
        /// <returns>实际创建数（= min(count, outputCapacity)）。</returns>
        public unsafe int CreateEntities(int count, ComponentType[] types, Entity* output, int outputCapacity)
        {
            CheckDisposed();
            if (count <= 0 || output == null || outputCapacity <= 0 || types == null) return 0;
            if (count > outputCapacity) count = outputCapacity;
            CompleteActiveJobs();
            lock (_structuralLock)
            {
                var targetArch = GetOrCreateArchetype(types);
                int created = 0;
                for (int i = 0; i < count; i++)
                {
                    var newEntity = new Entity();
                    if (TryPopRecycled(out var recycledEnt))
                    {
                        newEntity.Id = recycledEnt.Id;
                        newEntity.Version = recycledEnt.Version + 1;
                    }
                    else
                    {
                        newEntity.Id = entityCount++;
                        if (newEntity.Id >= entities.Length)
                        {
                            Array.Resize(ref entities, entities.Length * 2);
                            EnsureLocateCapacity(entities.Length);
                        }
                    }
                    targetArch.AddEntity(newEntity, out var chunkIndex, out var slotInChunk);
                    UpdateEntityLocation(newEntity.Id, targetArch, chunkIndex, slotInChunk);
                    // ⚠ 两张表必须同点更新：托管表的 Version 也在这里写（漏了它会让 Id 复用的实体
                    //   在 GetComponent/DestroyEntity 的悬垂校验里报 "stale reference (version mismatch)"）。
                    GetEntityInfoRef(newEntity.Id).Version = newEntity.Version;
                    SetLocateBVersion(newEntity.Id, newEntity.Version);
                    output[created++] = newEntity;
                }
                structuralVersion++;
                // ⚠ 与托管版不同：非托管路径**不做 Observer Added 派发**（需要托管 Entity[] 才能批量派发）。
                //    需要 Observer 的调用方请用托管版本 CreateEntities。
                return created;
            }
        }

        /// <summary>
        /// **单实体组件写入**（P1-9）：经 blittable 定位表直落组件列，无反射/无装箱。
        /// 与批量路径 <see cref="WriteComponentRange"/> 同源（都走 <see cref="TryGetComponentPointer"/> 的解析规则）。
        /// </summary>
        public unsafe void SetComponent<T>(Entity entity, T value) where T : struct
        {
            CheckDisposed();
            int typeId = ComponentTypeManager.GetComponentType(typeof(T)).Id;
            if (!TryGetComponentPointer(entity, typeId, sizeof(T), out void* ptr))
                throw new InvalidOperationException(
                    $"SetComponent<{typeof(T).Name}> 失败：实体 {entity.Id} 无效/已销毁/版本不匹配，或该实体没有该组件列。");
            *(T*)ptr = value;
        }

        /// <summary>
        /// 经定位表解析"某实体某组件列的可写指针"（单实体路径）。
        /// 返回 false 的四种情形：Id 越界 / 未分配或已销毁 / 版本不匹配（悬垂）/ Archetype 无该组件列。
        /// </summary>
        internal unsafe bool TryGetComponentPointer(Entity entity, int componentTypeId, int elemSize, out void* ptr)
        {
            ptr = null;
            if ((uint)entity.Id >= (uint)_locateBCapacity) return false;
            ref var loc = ref _locateB[entity.Id];
            if (loc.ChunkMemory == null || loc.SlotInChunk < 0) return false;
            if (loc.Version != entity.Version) return false;
            if ((uint)entity.Id >= (uint)entities.Length) return false;
            var arch = entities[entity.Id].Archetype;
            if (arch == null) return false;
            var ct = new ComponentType(componentTypeId);
            if (!arch.HasComponent(ct)) return false;
            int compIdx = arch.GetComponentTypeIndex(ct);
            ptr = (byte*)loc.ChunkMemory + loc.ChunkOffsets[compIdx] + loc.SlotInChunk * elemSize;
            return true;
        }

        /// <summary>
        /// 把一段**连续取值**批量写进给定实体的某组件列（P0-4/P0-5 用，ECB 回放零反射路径）。
        /// 逐实体经 blittable 定位表解析列基址（见 <see cref="EntityLocateB"/>），不做任何反射/装箱。
        /// </summary>
        /// <param name="componentTypeId">目标组件类型 Id。</param>
        /// <param name="elemSize">组件元素字节数（= sizeof(T)）。</param>
        /// <param name="srcEntities">实体列表（长度 ≥ count）。</param>
        /// <param name="srcValues">取值缓冲（长度 ≥ count × elemSize，与 srcEntities 一一对应）。</param>
        /// <param name="count">元素数。</param>
        /// <returns>实际写入数（实体无效/无该组件列的项被跳过）。</returns>
        internal unsafe int WriteComponentRange(int componentTypeId, int elemSize, Entity* srcEntities, byte* srcValues, int count)
        {
            if (count <= 0 || elemSize <= 0 || srcEntities == null || srcValues == null) return 0;
            if (_locateB == null) return 0;
            int written = 0;
            int cachedArchetypeId = -1;
            int compIdx = -1;
            for (int i = 0; i < count; i++)
            {
                int id = srcEntities[i].Id;
                if ((uint)id >= (uint)_locateBCapacity) continue;
                ref var loc = ref _locateB[id];
                if (loc.ChunkMemory == null || loc.SlotInChunk < 0) continue;
                if ((uint)id >= (uint)entities.Length) continue;
                var arch = entities[id].Archetype;
                if (arch == null) continue;
                // 同一批通常同 archetype：只在切换时重解析组件列下标（字典查询）
                if (arch.ArchetypeId != cachedArchetypeId)
                {
                    cachedArchetypeId = arch.ArchetypeId;
                    var ct = new ComponentType(componentTypeId);
                    // 该 archetype 没有这个组件列 → 整批跳过（GetComponentTypeIndex 是字典索引器，缺失会抛）
                    compIdx = arch.HasComponent(ct) ? arch.GetComponentTypeIndex(ct) : -1;
                }
                if (compIdx < 0) continue;
                byte* dst = (byte*)loc.ChunkMemory + loc.ChunkOffsets[compIdx] + loc.SlotInChunk * elemSize;
                Buffer.MemoryCopy(srcValues + (long)i * elemSize, dst, elemSize, elemSize);
                written++;
            }
            return written;
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
                    bool isRecycled = TryPopRecycled(out var recycledEnt);
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
                            EnsureLocateCapacity(entities.Length);
                    }

                    targetArch.AddEntity(newEntity, out var chunkIndex, out var slotInChunk);
                    UpdateEntityLocation(newEntity.Id, targetArch, chunkIndex, slotInChunk);
                    GetEntityInfoRef(newEntity.Id).Version = newEntity.Version;
                    SetLocateBVersion(newEntity.Id, newEntity.Version);
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

        /// <summary>
        /// **批量销毁**（P0-4b）：一次 <c>CompleteActiveJobs</c> + 一次锁处理整批，
        /// 避免"逐实体 DestroyEntity"时每条命令都等一遍在飞 job。
        /// 无效/已销毁/版本不匹配的项被静默跳过（返回实际销毁数）。
        /// </summary>
        public unsafe int DestroyEntities(Entity* targets, int count)
        {
            CheckDisposed();
            if (targets == null || count <= 0) return 0;
            CompleteActiveJobs();
            lock (_structuralLock)
            {
                int destroyed = 0;
                for (int i = 0; i < count; i++)
                {
                    int id = targets[i].Id;
                    if ((uint)id >= (uint)entities.Length) continue;
                    ref var info = ref entities[id];
                    if (info.Archetype == null) continue;
                    if (info.Version != targets[i].Version) continue;
                    DestroyEntityInternal(targets[i]);   // 锁内版本：含 Observer 与关系索引清理
                    destroyed++;
                }
                return destroyed;
            }
        }

        /// <summary>
        /// **清空某 Archetype 的全部实体**（P0-4b，ClearAll 快路径）：代价 O(Chunk 数) 而非 O(实体数)——
        /// 逐实体只做"清两表 + Id 回池"，Chunk/slab 整批释放（不做 swap-pop 与空 chunk 压缩）。
        ///
        /// ⚠ 含**关系列**的 Archetype 会自动退回逐实体 <see cref="DestroyEntityInternal"/> 路径
        /// （关系反向索引需要逐实体清理），此时退化为 O(实体数)。
        /// </summary>
        public unsafe long DestroyAllInArchetype(Archetype archetype)
        {
            CheckDisposed();
            if (archetype == null) return 0;
            CompleteActiveJobs();
            lock (_structuralLock)
            {
                // 关系列存在时不能用快路径（反向索引/多值关系需要逐实体清理）
                bool hasRelation = false;
                var types = archetype.Types;
                for (int i = 0; i < types.Length; i++)
                    if (types[i].IsRelation) { hasRelation = true; break; }

                long destroyed = 0;
                var chunks = archetype.ChunkList;
                if (hasRelation)
                {
                    for (int c = 0; c < chunks.Count; c++)
                    {
                        var chunk = chunks[c];
                        int ec = chunk.EntityCount;
                        for (int slot = 0; slot < ec; slot++)
                        {
                            var e = chunk.GetEntity(slot);
                            if ((uint)e.Id >= (uint)entities.Length) continue;
                            ref var info = ref entities[e.Id];
                            if (info.Archetype == null || info.Version != e.Version) continue;
                            DestroyEntityInternal(e);
                            destroyed++;
                        }
                    }
                    return destroyed;
                }

                for (int c = 0; c < chunks.Count; c++)
                {
                    var chunk = chunks[c];
                    int ec = chunk.EntityCount;
                    for (int slot = 0; slot < ec; slot++)
                    {
                        var e = chunk.GetEntity(slot);
                        if ((uint)e.Id >= (uint)entities.Length) continue;
                        ref var info = ref entities[e.Id];
                        info.Archetype = null;
                        info.ChunkIndex = -1;
                        info.SlotInChunk = -1;
                        ClearLocateB(e.Id);
                        PushRecycled(e);   // Id 回池，后续 Spawn 复用（Version+1 防悬垂）
                        destroyed++;
                    }
                }
                archetype.ClearAllEntities();
                structuralVersion++;
                return destroyed;
            }
        }

        public void DestroyEntity(Entity entity)
        {
            CheckDisposed();
            // 声明式级联（[OnTargetDeleted(Cascade=true)]）会销毁其他 archetype 的 sources → 需全量等待 jobs
            bool hasCascadeSources = (uint)entity.Id < (uint)entities.Length
                && _relationIndex.TryGetSources(entity.Id, out var cascadeByType)
                && HasCascadeDeclaredSources(cascadeByType);

            // 声明式级联会销毁其他 archetype 的 sources → 需全量等待 jobs；否则只等实体所属 Archetype。
            if (hasCascadeSources)
                CompleteActiveJobs();
            else
                CompleteEntityJobs(entity);
            lock (_structuralLock)
            {
                // 锁内二次确认后执行声明式级联（target 销毁 → 级联销毁其 sources，递归防环）
                if (hasCascadeSources &&
                    _relationIndex.TryGetSources(entity.Id, out var byType) &&
                    HasCascadeDeclaredSources(byType))
                {
                    var visited = new HashSet<int>();
                    var toDestroy = new List<Entity>();
                    CollectDeclaredCascade(entity, visited, toDestroy);
                    foreach (var e in toDestroy)
                    {
                        if (e.Id != entity.Id)   // entity 本身由下方 DestroyEntityCore 统一处理
                            DestroyEntityInternal(e);
                    }
                }
                DestroyEntityCore(entity);
            }
        }

        /// <summary>是否存在声明了 [OnTargetDeleted(Cascade=true)] 的关系类型指向该实体。</summary>
        private static bool HasCascadeDeclaredSources(Dictionary<int, HashSet<Entity>> byType)
        {
            foreach (var kv in byType)
            {
                if (ComponentTypeManager.GetCascadeOnTargetDeleted(kv.Key)) return true;
            }
            return false;
        }

        /// <summary>DFS 收集声明级联子树（仅沿 CascadeOnTargetDeleted 关系类型向下，防环）。</summary>
        private void CollectDeclaredCascade(Entity entity, HashSet<int> visited, List<Entity> toDestroy)
        {
            if (!visited.Add(entity.Id)) return;
            toDestroy.Add(entity);
            if (!_relationIndex.TryGetSources(entity.Id, out var byType)) return;
            foreach (var kv in byType)
            {
                if (!ComponentTypeManager.GetCascadeOnTargetDeleted(kv.Key)) continue;   // 仅沿声明级联的关系类型
                foreach (var source in kv.Value)
                {
                    if (!IsAlive(source)) continue;
                    if (!StillPointsToRelation(source, entity, kv.Key)) continue;   // 槽位校验：防索引滞后误伤
                    CollectDeclaredCascade(source, visited, toDestroy);
                }
            }
        }

        /// <summary>校验 source 是否通过指定关系类型 relTypeId 指向 target（列或多值列表）。</summary>
        private bool StillPointsToRelation(Entity source, Entity target, int relTypeId)
        {
            if ((uint)source.Id >= (uint)entities.Length) return false;
            ref var info = ref GetEntityInfoRef(source.Id);
            if (info.Archetype == null || info.Version != source.Version) return false;

            var type = ComponentTypeManager.GetTypeByComponentType(relTypeId);
            var compType = ComponentTypeManager.GetComponentType(type);

            // 定长多槽列（[MultiRelation(MaxSlots=N)]）：逐槽校验
            if (compType.MultiRelationMaxSlots >= 2)
            {
                if (!info.Archetype.Has(type)) return false;
                int compIdx = info.Archetype.GetComponentTypeIndex(compType);
                var chunk = info.Archetype.ChunkList[info.ChunkIndex];
                var basePtr = (byte*)chunk.GetComponentArrayPointer(compIdx) + info.SlotInChunk * compType.Size;
                for (int i = 0; i < compType.MultiRelationMaxSlots; i++)
                {
                    var slot = ((RelationSlot*)(basePtr + i * 8))[0];
                    if (slot.TargetId == target.Id && slot.TargetVersion == target.Version)
                        return true;
                }
                return false;
            }

            // 托管列表模式（[MultiRelation] 无 MaxSlots）：查正向列表
            if (compType.IsMultiRelation)
                return _relationListStore.Has(relTypeId, source, RelationSlot.From(target));

            // 单值列关系：校验列槽位
            if (!info.Archetype.Has(type)) return false;
            int compIdx1 = info.Archetype.GetComponentTypeIndex(compType);
            var chunk1 = info.Archetype.ChunkList[info.ChunkIndex];
            var slot1 = chunk1.GetComponent<RelationSlot>(info.SlotInChunk, compIdx1);
            return slot1.TargetId == target.Id && slot1.TargetVersion == target.Version;
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
            // 清理本实体作为 source 的多值关系（[MultiRelation] 正向列表 + 反向索引）
            CleanupMultiSourceRelations(entity);
            // 清理本实体作为 target 的反向索引条目（否则已销毁 target 的索引残留 → 泄漏 + 失效关系误返回）
            _relationIndex.ClearTarget(entity.Id);

            // Observer：Destroyed（销毁前快照已订阅组件值；派发在销毁后，实体仍可解析）
            List<(int typeId, IntPtr snapshotPtr)>? destroyedSnapshots = null;
            if (_observerCount > 0 && _observers != null && oldArchetype != null)
            {
                destroyedSnapshots = new List<(int, IntPtr)>();
                foreach (var t in oldArchetype.Types)
                {
                    // IDisposable 组件持有原生内存，销毁后其快照会悬垂（缓冲区已释放），跳过快照
                    if (t.IsDisposable) continue;
                    if (_observers.TryGetValue(t.Id, out var reg) && reg.Destroyed.Count > 0)
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

            RemoveAndFixup(archetype, entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk);

            entityInfoRef.Archetype = null;
            entityInfoRef.ChunkIndex = -1;
            entityInfoRef.SlotInChunk = -1;
            ClearLocateB(entity.Id);
            PushRecycled(entity);
            structuralVersion++;

            // Observer：派发 Destroyed（销毁后派发，实体仍可解析；用销毁前快照）
            if (destroyedSnapshots != null)
            {
                foreach (var (typeId, snapshotPtr) in destroyedSnapshots)
                {
                    try
                    {
                        if (_observers!.TryGetValue(typeId, out var reg) && reg.Destroyed.Count > 0)
                            DispatchRemoved(reg.Destroyed, &entity, (void*)snapshotPtr, 1);
                    }
                    finally
                    {
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(snapshotPtr);
                    }
                }
            }
        }

    }

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
        /// 添加组件并写入**原始字节**值（ECB 回放去反射路径：无 <c>Marshal.PtrToStructure</c>、无装箱）。
        /// 组件已存在时等价于原地写列（与 <see cref="WriteComponentRange"/> 同源）。
        /// </summary>
        public unsafe void AddComponentRaw(Entity entity, int componentTypeId, byte* value, int elemSize)
        {
            CheckDisposed();
            var componentType = ComponentTypeManager.GetTypeByComponentType(componentTypeId);
            Archetype? targetArch = null;
            if ((uint)entity.Id < (uint)entities.Length)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                if (info.Archetype != null && !info.Archetype.Has(componentType))
                    targetArch = info.Archetype.GetAddEdge(ComponentTypeManager.GetComponentType(componentType));
            }
            CompleteEntityJobs(entity, targetArch);
            lock (_structuralLock)
            {
                AddComponentRawCore(entity, componentType, null, value, elemSize);
            }
        }

        /// <summary>
        /// 添加组件（非泛型版本，核心实现）
        /// </summary>
        public unsafe void AddComponentRaw(Entity entity, Type componentType, object value)
        {
            CheckDisposed();
            // 组件不存在时才解析迁移目标 Archetype（存在时原地 SetRaw，只等实体所属 Archetype）。
            Archetype? targetArch = null;
            if ((uint)entity.Id < (uint)entities.Length)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                if (info.Archetype != null && !info.Archetype.Has(componentType))
                    targetArch = info.Archetype.GetAddEdge(ComponentTypeManager.GetComponentType(componentType));
            }
            CompleteEntityJobs(entity, targetArch);
            lock (_structuralLock)
            {
                AddComponentRawCore(entity, componentType, value);
            }
        }

        /// <summary>
        /// 把**原始字节**写进某实体所在 chunk 的组件列（结构变更后的落值步骤，无反射/无装箱）。
        /// </summary>
        private static unsafe void WriteColumnBytes(Archetype arch, int chunkIndex, int slotInChunk, Type componentType, byte* src, int elemSize)
        {
            int compIdx = arch.GetComponentTypeIndex(ComponentTypeManager.GetComponentType(componentType));
            byte* colBase = (byte*)arch.ChunkList[chunkIndex].GetComponentArrayPointer(compIdx);
            Buffer.MemoryCopy(src, colBase + (long)slotInChunk * elemSize, elemSize, elemSize);
        }

        /// <summary>
        /// 添加组件核心（调用方必须已持有 _structuralLock）。
        /// <paramref name="rawValue"/> 非 null 时走**原始字节**写入（值以字节为准，忽略 <paramref name="value"/>）。
        /// </summary>
        private unsafe void AddComponentRawCore(Entity entity, Type componentType, object value, byte* rawValue = null, int rawSize = 0)
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            if (entityInfoRef.Archetype == null)
                throw new InvalidOperationException($"Entity {entity} has been destroyed.");
            if (entityInfoRef.Version != entity.Version)
                throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");
            var oldArch = entityInfoRef.Archetype;
            if (oldArch.Has(componentType))
            {
                if (rawValue != null)
                    WriteColumnBytes(oldArch, entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, componentType, rawValue, rawSize);
                else
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

            // 从旧原型移除（含 swap-pop 搬移实体与空 chunk 压缩的位置修正）
            RemoveAndFixup(oldArch, entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk);

            UpdateEntityLocation(entity.Id, targetArch, chunkIndex, slotInChunk);
            if (rawValue != null)
                WriteColumnBytes(targetArch, chunkIndex, slotInChunk, componentType, rawValue, rawSize);
            else
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
            // 组件存在时才解析迁移目标 Archetype（不存在时核心直接 return，只等实体所属 Archetype）。
            Archetype? targetArch = null;
            if ((uint)entity.Id < (uint)entities.Length)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                if (info.Archetype != null && info.Archetype.Has(componentType))
                    targetArch = info.Archetype.GetRemoveEdge(ComponentTypeManager.GetComponentType(componentType));
            }
            CompleteEntityJobs(entity, targetArch);
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
                    // IDisposable 组件持有原生内存，移除后其快照会悬垂（缓冲区已释放），跳过快照
                    if (!compTypeForObs.IsDisposable &&
                        _observers.TryGetValue(compTypeForObs.Id, out var reg) &&
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
                RemoveAndFixup(oldArch, entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk);

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

        // ======================== Chunk 碎片整理 ========================

        /// <summary>
        /// 合并所有 Archetype 的瘦 Chunk（利用率 &lt; thresholdPercent）。
        /// 减少 Chunk 数量，提升查询遍历效率；搬移走 move 语义（零分配，不泄漏、不 double-free）。
        /// 结构性 API（需持有锁 + 先 CompleteActiveJobs），主线程调用。
        /// </summary>
        public void CompactChunks(float thresholdPercent = 30f)
        {
            CheckDisposed();
            CompleteActiveJobs();
            lock (_structuralLock)
            {
                for (int i = 0; i < archetypeCount; i++)
                {
                    var arch = allArchetypes[i];
                    if (arch != null && arch.ChunkCount > 1)
                        CompactArchetype(arch, thresholdPercent);
                }
                structuralVersion++;
            }
        }

        private unsafe void CompactArchetype(Archetype arch, float thresholdPercent)
        {
            var span = CollectionsMarshal.AsSpan(arch.ChunkList);

            // 从后往前遍历：搬移 + 移除空 Chunk 后重新取 span，避免索引失效
            for (int i = span.Length - 1; i >= 0; i--)
            {
                if (span.Length <= 1) break;

                ref var thin = ref span[i];
                if (thin.EntityCount == 0) continue;
                // 利用率 < 阈值才算瘦 Chunk
                if (thin.EntityCount * 100f >= thin.Capacity * thresholdPercent) continue;

                // 找目标：前面第一个未满的 Chunk
                int targetIdx = -1;
                for (int j = 0; j < i; j++)
                {
                    if (span[j].EntityCount < span[j].Capacity)
                    {
                        targetIdx = j;
                        break;
                    }
                }
                if (targetIdx < 0) continue;

                // 搬移 thin 的所有实体到 target
                MoveEntitiesTo(arch, span, i, targetIdx);

                // 移除空 Chunk（swap 到末尾 + RemoveAt + 刷新被 swap Chunk 的索引）
                RemoveEmptyChunkAt(arch, i);

                span = CollectionsMarshal.AsSpan(arch.ChunkList);
            }
        }

        private unsafe void MoveEntitiesTo(Archetype arch, Span<Chunk> span, int thinIdx, int startTargetIdx)
        {
            ref var thin = ref span[thinIdx];
            int targetIdx = startTargetIdx;

            while (thin.EntityCount > 0)
            {
                // 目标满了就前进到下一个未满 Chunk（限制在 thin 前面，不越过）
                while (targetIdx < thinIdx && span[targetIdx].EntityCount >= span[targetIdx].Capacity)
                    targetIdx++;
                if (targetIdx >= thinIdx)
                    break;  // 前面没有可用目标，无法继续合并

                ref var target = ref span[targetIdx];
                int lastSlot = thin.EntityCount - 1;
                Entity e = thin.GetEntity(lastSlot);

                target.AddEntity(e);
                int targetSlot = target.EntityCount - 1;

                // move 语义（IDisposable 组件转移所有权，普通组件位拷贝）
                arch.CopyComponentsTo(thinIdx, lastSlot, arch, targetIdx, targetSlot);

                // 复制 enableable 位（AddEntity 默认设为启用，需还原源实体的 enable 状态）
                for (int comp = 0; comp < thin.Meta.ComponentCount; comp++)
                {
                    if (thin.Meta.EnableBitOffsets[comp] == -1) continue;
                    target.SetComponentEnabled(comp, targetSlot, thin.GetComponentEnabled(comp, lastSlot));
                }

                // 移除源（最后一个，无 swap-pop；源组件已被 move 清空，Dispose no-op）
                thin.RemoveEntity(lastSlot);

                UpdateEntityLocation(e.Id, arch, targetIdx, targetSlot);
            }
        }

        private void RemoveEmptyChunkAt(Archetype arch, int chunkIndex)
        {
            var list = arch.ChunkList;
            int lastIndex = list.Count - 1;
            // 空 chunk 的内存块（swap 前记录：swap 后 chunkIndex 位置被 last 覆盖）
            nint freedChunk = list[chunkIndex].MemoryBlock;
            if (chunkIndex != lastIndex)
            {
                list[chunkIndex] = list[lastIndex];
            }
            list.RemoveAt(lastIndex);
            arch.ReleaseChunkMemory(freedChunk);  // 复用空洞 / 归还空 slab
            if (chunkIndex < list.Count)
                RefreshChunkEntityIndices(arch, chunkIndex);
        }

        // ======================== 内存分析 ========================

        /// <summary>
        /// 生成内存分析报告（纯观测快照）：原生分配/释放/泄漏、Chunk 数、碎片率、slab 占用。
        /// </summary>
        public MemoryReport GetMemoryReport(float thinThresholdPercent = 30f)
        {
            var alloc = PersistentAllocator.GetStats();
            var report = new MemoryReport
            {
                NativeAllocs = alloc.Allocs,
                NativeFrees = alloc.Frees,
                NativeHits = alloc.Hits,
                NativeMisses = alloc.Misses,
                NativeForeign = alloc.Foreign,
                LeakedContainers = DisposeSentinel.LeakedCount,
                TotalEntityCount = entityCount,
                Archetypes = new List<ArchetypeMemoryInfo>(),
            };

            for (int i = 0; i < archetypeCount; i++)
            {
                var arch = allArchetypes[i];
                if (arch == null) continue;

                report.TotalChunkCount += arch.ChunkCount;
                report.TotalSlabBytes += arch.SlabBytes;

                // 统计瘦 Chunk（碎片信号）
                foreach (var chunk in arch.ChunkList)
                {
                    if (chunk.EntityCount > 0 && chunk.EntityCount * 100f < chunk.Capacity * thinThresholdPercent)
                        report.ThinChunkCount++;
                }

                // Archetype 明细
                var names = new string[arch.ComponentCount];
                for (int c = 0; c < arch.ComponentCount; c++)
                    names[c] = arch.Types[c].Type.Name;

                report.Archetypes.Add(new ArchetypeMemoryInfo
                {
                    TypeSignature = string.Join(", ", names),
                    ChunkCount = arch.ChunkCount,
                    EntityCount = arch.EntityCount,
                    Capacity = arch.ChunkCount > 0 ? arch.ChunkList[0].Capacity : 0,
                    SlabCount = arch.SlabCount,
                    SlabBytes = arch.SlabBytes,
                });
            }

            return report;
        }

        // ======================== Prefab 实例化 ========================

        /// <summary>
        /// 从 Prefab 模板实体复制创建 count 个实例（对齐 Unity EntityManager.Instantiate）。
        /// 复制 blittable 组件值（prefab 与实例为独立副本），实例不含 Prefab 标记；
        /// 含持有原生资源（IDisposable）的组件无法复制，抛异常。
        /// </summary>
        public unsafe Entity[] SpawnFrom(Entity prefabEntity, int count)
        {
            CheckDisposed();
            if (count <= 0) return Array.Empty<Entity>();
            CompleteActiveJobs();
            lock (_structuralLock)
            {
                ref var prefabInfo = ref GetEntityInfoRef(prefabEntity.Id);
                if (prefabInfo.Archetype == null)
                    throw new InvalidOperationException($"Entity {prefabEntity} has been destroyed.");
                if (prefabInfo.Version != prefabEntity.Version)
                    throw new InvalidOperationException($"Entity {prefabEntity} is a stale reference (version mismatch).");
                var prefabArch = prefabInfo.Archetype;
                var prefabType = ComponentTypeManager.GetComponentType(typeof(Prefab));
                if (!prefabArch.HasComponent(prefabType))
                    throw new InvalidOperationException($"Entity {prefabEntity} is not a Prefab.");

                // 实例 archetype = prefab 去掉 Prefab 标记
                var instanceTypes = new ComponentType[prefabArch.ComponentCount - 1];
                int idx = 0;
                foreach (var t in prefabArch.Types)
                    if (t.Id != prefabType.Id)
                        instanceTypes[idx++] = t;

                // 持有原生资源且无复制钩子的组件无法复制（有 ICopyable 钩子的组件走 OnCopy）
                for (int i = 0; i < instanceTypes.Length; i++)
                    if (instanceTypes[i].IsDisposable && !instanceTypes[i].IsCopyable)
                        throw new InvalidOperationException(
                            $"Prefab 含持有原生资源且无复制钩子的组件 '{instanceTypes[i].Type.Name}'，无法复制。");

                var instanceArch = GetOrCreateArchetype(instanceTypes);

                var prefabChunk = prefabArch.ChunkList[prefabInfo.ChunkIndex];
                int prefabSlot = prefabInfo.SlotInChunk;
                var result = new Entity[count];

                for (int i = 0; i < count; i++)
                {
                    var newEntity = new Entity();
                    bool isRecycled = TryPopRecycled(out var recycledEnt);
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
                            EnsureLocateCapacity(entities.Length);
                    }

                    instanceArch.AddEntity(newEntity, out var chunkIndex, out var slotInChunk);
                    UpdateEntityLocation(newEntity.Id, instanceArch, chunkIndex, slotInChunk);
                    GetEntityInfoRef(newEntity.Id).Version = newEntity.Version;
                    SetLocateBVersion(newEntity.Id, newEntity.Version);

                    // 复制组件值（ICopyable 走 OnCopy 如 SharedBlob refcount++；普通走位拷贝）
                    var instanceChunk = instanceArch.ChunkList[chunkIndex];
                    for (int c = 0; c < instanceTypes.Length; c++)
                    {
                        var t = instanceTypes[c];
                        int prefabCompIdx = prefabArch.GetComponentTypeIndex(t);
                        int instCompIdx = instanceArch.GetComponentTypeIndex(t);
                        byte* src = (byte*)prefabChunk.GetComponentArrayPointer(prefabCompIdx) + prefabSlot * t.Size;
                        byte* dst = (byte*)instanceChunk.GetComponentArrayPointer(instCompIdx) + slotInChunk * t.Size;
                        ComponentTypeManager.CopyComponentValue(t, src, dst);
                    }

                    result[i] = newEntity;
                }

                structuralVersion++;

                if (_observerCount > 0 && _observers != null)
                    DispatchCreateEntitiesAdded(instanceArch, instanceTypes, result, count);

                return result;
            }
        }

        // ======================== 数据导航（调试 dump） ========================

        /// <summary>打印单个实体的所有组件字段值（用组件元数据，非反射）。</summary>
        public unsafe string DumpEntity(Entity entity)
        {
            CheckDisposed();
            ref var info = ref GetEntityInfoRef(entity.Id);
            if (info.Archetype == null)
                return "<destroyed entity>";
            if (info.Version != entity.Version)
                return "<stale entity>";
            return DumpEntityInChunk(info.Archetype, info.Archetype.ChunkList[info.ChunkIndex], info.SlotInChunk, entity);
        }

        /// <summary>打印指定组件签名 Archetype 的所有实体。</summary>
        public string DumpArchetype(params ComponentType[] types)
        {
            CheckDisposed();
            Archetype? arch = null;
            for (int i = 0; i < archetypeCount; i++)
            {
                var a = allArchetypes[i];
                if (a != null && a.Types.SequenceEqual(types))
                {
                    arch = a;
                    break;
                }
            }
            if (arch == null)
                return $"<archetype not found: {string.Join(", ", types.Select(t => t.Type.Name))}>";

            var sb = new StringBuilder();
            sb.AppendLine($"Archetype [{string.Join(", ", arch.Types.ToArray().Select(t => t.Type.Name))}] chunk={arch.ChunkCount} 实体={arch.EntityCount}");
            foreach (var chunk in arch.ChunkList)
            {
                for (int slot = 0; slot < chunk.EntityCount; slot++)
                    sb.Append(DumpEntityInChunk(arch, chunk, slot, chunk.GetEntity(slot)));
            }
            return sb.ToString();
        }

        /// <summary>打印所有 Archetype 的概览（组件签名 + 实体数 + chunk 数 + slab）。</summary>
        public string DumpWorld()
        {
            CheckDisposed();
            var sb = new StringBuilder();
            sb.AppendLine($"World: {entityCount} 实体, archetype 数={archetypeCount}");
            for (int i = 0; i < archetypeCount; i++)
            {
                var arch = allArchetypes[i];
                if (arch == null) continue;
                string sig = string.Join(", ", arch.Types.ToArray().Select(t => t.Type.Name));
                sb.AppendLine($"  [{sig}] 实体={arch.EntityCount}, chunk={arch.ChunkCount}, slab={arch.SlabBytes / 1024}KB");
            }
            return sb.ToString();
        }

        private unsafe string DumpEntityInChunk(Archetype arch, Chunk chunk, int slot, Entity entity)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  Entity {entity.Id}:{entity.Version}");
            for (int i = 0; i < arch.ComponentCount; i++)
            {
                var type = arch.Types[i];
                var meta = ComponentMetaRegistry.Get(type.Id);
                byte* compPtr = (byte*)chunk.GetComponentArrayPointer(i) + slot * type.Size;

                if (meta.TypeName == null || meta.Fields == null || meta.Fields.Length == 0)
                {
                    sb.AppendLine($"    {type.Type.Name}: <无字段元数据>");
                    continue;
                }

                sb.Append($"    {meta.TypeName}: ");
                foreach (var f in meta.Fields)
                    sb.Append($"{f.Name}={ReadFieldValue(compPtr + f.Offset, f.Kind)} ");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static unsafe string ReadFieldValue(void* ptr, FieldKind kind)
        {
            return kind switch
            {
                FieldKind.Int8 => (*(sbyte*)ptr).ToString(),
                FieldKind.Int16 => (*(short*)ptr).ToString(),
                FieldKind.Int32 => (*(int*)ptr).ToString(),
                FieldKind.Int64 => (*(long*)ptr).ToString(),
                FieldKind.UInt8 => (*(byte*)ptr).ToString(),
                FieldKind.UInt16 => (*(ushort*)ptr).ToString(),
                FieldKind.UInt32 => (*(uint*)ptr).ToString(),
                FieldKind.UInt64 => (*(ulong*)ptr).ToString(),
                FieldKind.Float32 => (*(float*)ptr).ToString(),
                FieldKind.Float64 => (*(double*)ptr).ToString(),
                FieldKind.Bool => (*(bool*)ptr).ToString(),
                FieldKind.Char => (*(char*)ptr).ToString(),
                FieldKind.Decimal => (*(decimal*)ptr).ToString(),
                _ => "?",
            };
        }

        // ======================== World 快照（零拷贝序列化） ========================

        /// <summary>序列化当前 World 状态为字节快照（组件值零拷贝 memcpy）。</summary>
        public unsafe WorldSnapshot TakeSnapshot()
        {
            CheckDisposed();
            using var ms = new System.IO.MemoryStream();
            using var w = new System.IO.BinaryWriter(ms);
            w.Write("ENTJOY".ToCharArray());  // magic

            // 收集非空 archetype
            var archetypes = new List<Archetype>();
            for (int i = 0; i < archetypeCount; i++)
                if (allArchetypes[i] != null && allArchetypes[i].EntityCount > 0)
                    archetypes.Add(allArchetypes[i]);

            w.Write(archetypes.Count);
            foreach (var arch in archetypes)
            {
                w.Write(arch.ComponentCount);
                foreach (var t in arch.Types)
                {
                    w.Write(t.Id);
                    w.Write(t.Size);
                }
            }

            int totalEntities = 0;
            foreach (var arch in archetypes) totalEntities += arch.EntityCount;
            w.Write(totalEntities);

            for (int archIdx = 0; archIdx < archetypes.Count; archIdx++)
            {
                var arch = archetypes[archIdx];
                foreach (var chunk in arch.ChunkList)
                {
                    for (int slot = 0; slot < chunk.EntityCount; slot++)
                    {
                        Entity e = chunk.GetEntity(slot);
                        w.Write(e.Id);
                        w.Write(e.Version);
                        w.Write(archIdx);
                        for (int c = 0; c < arch.ComponentCount; c++)
                        {
                            var t = arch.Types[c];
                            byte* compPtr = (byte*)chunk.GetComponentArrayPointer(c) + slot * t.Size;
                            w.Write(new ReadOnlySpan<byte>(compPtr, t.Size));
                        }
                    }
                }
            }

            w.Flush();
            return new WorldSnapshot(ms.ToArray());
        }

        /// <summary>从快照恢复 World 状态（清空当前内容后反序列化重建）。</summary>
        public unsafe void Restore(WorldSnapshot snapshot)
        {
            CheckDisposed();
            using var ms = new System.IO.MemoryStream(snapshot.Data);
            using var r = new System.IO.BinaryReader(ms);

            char[] magic = r.ReadChars(6);

            ClearWorldInternal();

            int archCount = r.ReadInt32();
            var signatures = new ComponentType[archCount][];
            for (int i = 0; i < archCount; i++)
            {
                int compCount = r.ReadInt32();
                var types = new ComponentType[compCount];
                for (int c = 0; c < compCount; c++)
                {
                    int typeId = r.ReadInt32();
                    int size = r.ReadInt32();
                    types[c] = new ComponentType(typeId, size);
                }
                signatures[i] = types;
            }

            int entCount = r.ReadInt32();
            for (int i = 0; i < entCount; i++)
            {
                int id = r.ReadInt32();
                int version = r.ReadInt32();
                int archIdx = r.ReadInt32();

                var arch = GetOrCreateArchetype(signatures[archIdx]);
                if (id >= entities.Length)
                    Array.Resize(ref entities, Math.Max(entities.Length * 2, id + 1));
                    EnsureLocateCapacity(entities.Length);
                if (id >= entityCount) entityCount = id + 1;

                var entity = new Entity { Id = id, Version = version };
                arch.AddEntity(entity, out var chunkIndex, out var slotInChunk);
                UpdateEntityLocation(id, arch, chunkIndex, slotInChunk);
                GetEntityInfoRef(id).Version = version;

                for (int c = 0; c < arch.ComponentCount; c++)
                {
                    var t = arch.Types[c];
                    byte* compPtr = (byte*)arch.ChunkList[chunkIndex].GetComponentArrayPointer(c) + slotInChunk * t.Size;
                    r.Read(new Span<byte>(compPtr, t.Size));
                }
            }

            structuralVersion++;
            RebuildRelationIndexes();
        }

        private void ClearWorldInternal()
        {
            CompleteActiveJobs();
            for (int i = 0; i < archetypeCount; i++)
            {
                allArchetypes[i]?.Dispose();
                allArchetypes[i] = null;
            }
            archetypeMap.Clear();
            ClearRecycled();
            _relationIndex.Clear();
            _relationListStore.Clear();
            _managedLookup.Clear();
            _managedSharedValues = new object[16];
            _managedSharedValueCount = 0;
            _lastChunkPerSharedValue.Clear();
            entities = new EntityIndexInWorld[Math.Max(256, entities.Length)];
            // blittable 定位表随实体表一起重建（旧内容全部作废：Archetype 已全部清空）
            NativeLocateStorage.Free(_locateB);
            _locateB = NativeLocateStorage.AllocateLocate(entities.Length);
            _locateBCapacity = entities.Length;
            archetypeCount = 0;
            entityCount = 0;
            structuralVersion++;
        }

        /// <summary>重建关系反向索引（Restore 恢复实体后调用，遍历所有关系列槽位）。</summary>
        private unsafe void RebuildRelationIndexes()
        {
            _relationIndex.Clear();
            _relationListStore.Clear();
            for (int i = 0; i < archetypeCount; i++)
            {
                var arch = allArchetypes[i];
                if (arch == null) continue;
                foreach (var chunk in arch.ChunkList)
                {
                    int entityCount = chunk.EntityCount;
                    for (int slot = 0; slot < entityCount; slot++)
                    {
                        var source = chunk.GetEntity(slot);
                        for (int c = 0; c < arch.ComponentCount; c++)
                        {
                            var ct = arch.Types[c];
                            if (!ct.IsRelation) continue;
                            var basePtr = (byte*)chunk.GetComponentArrayPointer(c) + slot * ct.Size;
                            if (ct.MultiRelationMaxSlots >= 2)
                            {
                                for (int s = 0; s < ct.MultiRelationMaxSlots; s++)
                                {
                                    var relSlot = ((RelationSlot*)(basePtr + s * 8))[0];
                                    if (relSlot.IsValid) _relationIndex.Add(ct.Id, source, relSlot.ToEntity());
                                }
                            }
                            else if (!ct.IsMultiRelation)
                            {
                                var relSlot = ((RelationSlot*)basePtr)[0];
                                if (relSlot.IsValid) _relationIndex.Add(ct.Id, source, relSlot.ToEntity());
                            }
                            // 托管列表模式（IsMultiRelation && MaxSlots < 2）：RelationListStore 不随快照序列化，跳过
                        }
                    }
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
            CompleteEntityJobs(entity);
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
            CompleteEntityJobs(entity);
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
