using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.ECS
{
    /// <summary>
    /// EntityManager 的 Shared Component 支持（per-chunk 存储，对齐 Unity DOTS）。
    ///
    /// 双类型策略：
    /// - blittable shared：值内联存储于 Chunk 内存块 Shared values 区。
    /// - managed shared：值存于 EntityManager 扁平数组（去重索引），Chunk 槽位只存 int 索引。
    ///   值只增不减，World.Dispose 时整体清空 → GC 回收。
    ///
    /// 核心不变式：同一 Chunk 的所有实体共享相同的 SharedComponent 值组合。
    /// 改变 shared 值 → 实体在 Archetype 内部移动到持有目标值的 Chunk（无 Archetype 变更）。
    /// </summary>
    public unsafe partial class EntityManager
    {
        // ======================== managed shared 值存储（简化版：无 refcount，值只增不减） ========================

        /// <summary>managed shared 值本体（全局去重数组）。World.Dispose 时清空。</summary>
        private object[] _managedSharedValues = new object[16];
        private int _managedSharedValueCount;

        /// <summary>per-type 查找表：typeId → (value → globalIndex)。Dictionary 简洁可靠，值种类通常 &lt;200。</summary>
        private readonly Dictionary<int, Dictionary<object, int>> _managedLookup = new();

        /// <summary>
        /// per-value 最近使用缓存：shared 值 → 最近一次命中的 chunk 索引（Archetype 内）。
        /// key = (Archetype, 组件索引, 值)。managed 的值为全局 index（int box）；blittable 为 boxed 值。
        /// 目的：SetSharedComponent 移动 / NewEntity 带 shared 的高频路径避免 O(chunks) 全量扫描。
        /// 失效策略：lazy 验证 —— 命中后验证 chunk 未满且值仍匹配；chunk 被回收（swap-pop）/就地改值
        /// 导致验证失败时移除条目并回退全扫描。删除路径零维护。
        /// </summary>
        private readonly Dictionary<(Archetype, int, object), int> _lastChunkPerSharedValue = new();

        /// <summary>查找或添加 managed shared 值（O(1)，去重）。index 只增不减。</summary>
        private int FindOrAddManagedValue(int typeId, object value)
        {
            if (!_managedLookup.TryGetValue(typeId, out var dict))
            {
                dict = new Dictionary<object, int>();
                _managedLookup[typeId] = dict;
            }
            if (dict.TryGetValue(value, out int idx))
                return idx;

            idx = _managedSharedValueCount++;
            if (idx >= _managedSharedValues.Length)
                Array.Resize(ref _managedSharedValues, _managedSharedValues.Length * 2);
            _managedSharedValues[idx] = value;
            dict[value] = idx;
            return idx;
        }

        // ======================== 读取 / 写入 API ========================

        /// <summary>获取实体的共享组件值（blittable 读 chunk 内存块内联值；managed 读索引 → 值数组）。</summary>
        public T GetSharedComponent<T>(Entity entity) where T : ISharedComponentData
        {
            CheckDisposed();
            ValidateEntity(entity);
            var info = GetEntityInfoRef(entity.Id);
            var arch = info.Archetype;
            int compIdx = arch.GetComponentTypeIndex(ComponentTypeManager.GetComponentType(typeof(T)));
            var chunk = arch.ChunkList[info.ChunkIndex];
            var ct = arch.Types[compIdx];

            if (ct.IsManagedShared)
            {
                int idx = chunk.GetSharedValueIndex(compIdx);
                return (T)_managedSharedValues[idx];
            }
            return (T)ReadBlittableSharedBoxed(chunk, compIdx, ct.Type);
        }

        /// <summary>
        /// 设置实体的共享组件值（per-chunk 语义，对齐 Unity DOTS）：
        /// 1. 所在 chunk 单实体 → 就地改值（无移动）。
        /// 2. 多实体且值不同 → Archetype 内找/建目标值 chunk，swap-pop 移动实体。
        /// 3. 值相同 → 无操作。
        /// </summary>
        public void SetSharedComponent<T>(Entity entity, T value) where T : ISharedComponentData
        {
            CheckDisposed();
            CompleteActiveJobs();
            lock (_structuralLock)
            {
                ValidateEntity(entity);
                ref var info = ref GetEntityInfoRef(entity.Id);
                var arch = info.Archetype;
                int compIdx = arch.GetComponentTypeIndex(ComponentTypeManager.GetComponentType(typeof(T)));
                var ct = arch.Types[compIdx];
                int srcChunkIndex = info.ChunkIndex;
                var srcChunk = arch.ChunkList[srcChunkIndex];

                if (ct.IsManagedShared)
                {
                    int oldIdx = srcChunk.GetSharedValueIndex(compIdx);
                    int newIdx = FindOrAddManagedValue(ct.Id, value);

                    // 值相同 → 无操作
                    if (oldIdx == newIdx) return;

                    // 单实体 chunk → 就地改索引
                    if (srcChunk.EntityCount == 1)
                    {
                        srcChunk.SetSharedValueIndex(compIdx, newIdx);
                        srcChunk.MarkEntityChanged(info.SlotInChunk);
                        return;
                    }

                    // 多实体：按完整 shared 组合找/建目标 chunk（否则其他 shared 组件值丢失）
                    MoveEntityWithShared(arch, entity, srcChunkIndex, compIdx, newIdx, null);
                }
                else
                {
                    // blittable shared
                    object curVal = ReadBlittableSharedBoxed(srcChunk, compIdx, ct.Type);
                    if (EqualityComparer<object>.Default.Equals(curVal, value)) return;

                    if (srcChunk.EntityCount == 1)
                    {
                        WriteBlittableSharedBoxed(srcChunk, compIdx, value, ct.Size);
                        srcChunk.MarkEntityChanged(info.SlotInChunk);
                        return;
                    }

                    MoveEntityWithShared(arch, entity, srcChunkIndex, compIdx, null, value);
                }
            }
        }

        /// <summary>在 Archetype 内查找持有指定 managed index 的未满 chunk；无返回 -1。
        /// 缓存优先（O(1) 期望），lazy 验证失败回退全扫描。</summary>
        private int FindChunkWithManagedValue(Archetype arch, int compIdx, int index)
        {
            var list = arch.ChunkList;
            var key = (arch, compIdx, (object)index);

            // 缓存命中路径：验证 chunk 仍存在、未满、值匹配。
            // 越界（chunk 被回收收缩）或验证失败均移除条目，避免残留导致永久失去缓存命中。
            if (_lastChunkPerSharedValue.TryGetValue(key, out int cached))
            {
                if (cached < list.Count)
                {
                    var cachedChunk = list[cached];
                    if (cachedChunk.EntityCount < cachedChunk.Capacity && cachedChunk.GetSharedValueIndex(compIdx) == index)
                        return cached;
                }
                _lastChunkPerSharedValue.Remove(key);
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].EntityCount < list[i].Capacity && list[i].GetSharedValueIndex(compIdx) == index)
                {
                    _lastChunkPerSharedValue[key] = i;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>在 Archetype 内查找持有指定 boxed blittable 值的未满 chunk；无返回 -1。
        /// 缓存优先（O(1) 期望），lazy 验证失败回退全扫描。</summary>
        private int FindChunkWithBlittableBoxed(Archetype arch, int compIdx, object value, Type compType)
        {
            var list = arch.ChunkList;
            var key = (arch, compIdx, value);

            // 缓存命中路径：验证 chunk 仍存在、未满、值匹配。
            // 越界（chunk 被回收收缩）或验证失败均移除条目，避免残留导致永久失去缓存命中。
            if (_lastChunkPerSharedValue.TryGetValue(key, out int cached))
            {
                if (cached < list.Count)
                {
                    var cachedChunk = list[cached];
                    if (cachedChunk.EntityCount < cachedChunk.Capacity &&
                        EqualityComparer<object>.Default.Equals(ReadBlittableSharedBoxed(cachedChunk, compIdx, compType), value))
                        return cached;
                }
                _lastChunkPerSharedValue.Remove(key);
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].EntityCount >= list[i].Capacity) continue;
                object existing = ReadBlittableSharedBoxed(list[i], compIdx, compType);
                if (EqualityComparer<object>.Default.Equals(existing, value))
                {
                    _lastChunkPerSharedValue[key] = i;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 将实体从 srcChunk 移动到 arch 内的 dstChunk（swap-pop，组件全量复制）。
        /// </summary>
        private void MoveEntityToChunk(Entity entity, Archetype arch, int srcChunkIndex, int dstChunkIndex)
        {
            ref var info = ref GetEntityInfoRef(entity.Id);
            int srcSlot = info.SlotInChunk;

            var dstChunk = arch.ChunkList[dstChunkIndex];
            int dstSlot = dstChunk.EntityCount;
            arch.AddEntityToChunk(entity, dstChunkIndex);

            arch.CopyComponentsTo(srcChunkIndex, srcSlot, arch, dstChunkIndex, dstSlot);

            arch.Remove(srcChunkIndex, srcSlot, out var movedId, out var movedSlot, out var compactedIdx);
            if (movedId >= 0)
                UpdateEntityLocation(movedId, arch, srcChunkIndex, movedSlot);
            if (compactedIdx >= 0)
                RefreshChunkEntityIndices(arch, compactedIdx);

            UpdateEntityLocation(entity.Id, arch, dstChunkIndex, dstSlot);
            structuralVersion++;
        }

        /// <summary>按完整 shared 组合移动实体（SetSharedComponent 多实体路径）：收集源实体全部 shared 值，
        /// 更新单个组件后找/建持有完整组合的目标 chunk，避免其他 shared 组件值丢失。</summary>
        private void MoveEntityWithShared(Archetype arch, Entity entity, int srcChunkIndex, int compIdx, int? newManagedIdx, object? newBlittableValue)
        {
            var types = arch.Types;
            bool[] slotSet = new bool[arch.ComponentCount];
            int[] managedIdx = new int[arch.ComponentCount];
            Array.Fill(managedIdx, -1);
            object?[] slotValue = new object?[arch.ComponentCount];

            var srcChunk = arch.ChunkList[srcChunkIndex];
            for (int c = 0; c < arch.ComponentCount; c++)
            {
                var t = types[c];
                if (!t.IsShared) continue;
                slotSet[c] = true;
                if (t.IsManagedShared) managedIdx[c] = srcChunk.GetSharedValueIndex(c);
                else slotValue[c] = ReadBlittableSharedBoxed(srcChunk, c, t.Type);
            }
            if (newManagedIdx.HasValue) managedIdx[compIdx] = newManagedIdx.Value;
            else slotValue[compIdx] = newBlittableValue;

            int targetChunk = FindExistingChunkForShared(arch, slotSet, managedIdx, slotValue);
            if (targetChunk < 0)
            {
                targetChunk = arch.CreateChunk();
                var newChunk = arch.ChunkList[targetChunk];
                for (int c = 0; c < arch.ComponentCount; c++)
                {
                    if (!slotSet[c]) continue;
                    if (types[c].IsManagedShared) newChunk.SetSharedValueIndex(c, managedIdx[c]);
                    else WriteBlittableSharedBoxed(newChunk, c, slotValue[c]!, types[c].Size);
                }
            }
            MoveEntityToChunk(entity, arch, srcChunkIndex, targetChunk);
            arch.ChunkList[targetChunk].MarkEntityChanged(arch.ChunkList[targetChunk].EntityCount - 1);
        }

        // ======================== 创建带初始共享值的实体 ========================

        /// <summary>
        /// 创建实体并指定初始共享值（per-chunk 分组）。
        /// <paramref name="sharedValues"/>：每个元素为 (shared 组件类型, 值)；类型必须是 ISharedComponentData。
        /// 实体进入持有相同值组合的未满 chunk；不存在则新建 chunk（写入初始值）。
        /// </summary>
        public Entity NewEntity(Span<ComponentType> types, params (Type type, object value)[] sharedValues)
        {
            CheckDisposed();
            CompleteActiveJobs();
            lock (_structuralLock)
            {
                var targetArch = GetOrCreateArchetype(types);

                if (sharedValues == null || sharedValues.Length == 0)
                {
                    var e = AllocateEntityId();
                    targetArch.AddEntity(e, out var cIdx, out var slot);
                    UpdateEntityLocation(e.Id, targetArch, cIdx, slot);
                    GetEntityInfoRef(e.Id).Version = e.Version;
                    structuralVersion++;
                    return e;
                }

                // 解析每个 shared 槽位的目标值
                int[] managedIdx = new int[targetArch.ComponentCount];
                Array.Fill(managedIdx, -1);
                object[] slotValue = new object[targetArch.ComponentCount];
                bool[] slotSet = new bool[targetArch.ComponentCount];

                for (int s = 0; s < sharedValues.Length; s++)
                {
                    var (stype, svalue) = sharedValues[s];
                    if (svalue == null)
                        throw new ArgumentNullException(nameof(sharedValues), $"Shared value for {stype} cannot be null.");

                    int compIdx = targetArch.GetComponentTypeIndex(ComponentTypeManager.GetComponentType(stype));
                    var ct = targetArch.Types[compIdx];
                    if (!ct.IsShared)
                        throw new ArgumentException(
                            $"Type {stype.FullName} is not an ISharedComponentData. Only shared components can take an initial value.",
                            nameof(sharedValues));
                    slotSet[compIdx] = true;
                    slotValue[compIdx] = svalue;

                    if (ct.IsManagedShared)
                        managedIdx[compIdx] = FindOrAddManagedValue(ct.Id, svalue);
                }

                // 找已有 chunk：所有槽位匹配且未满
                int chunkIdx = FindExistingChunkForShared(targetArch, slotSet, managedIdx, slotValue);

                if (chunkIdx == -1)
                {
                    // 新建 chunk：写入所有槽位
                    chunkIdx = targetArch.CreateChunk();
                    var newChunk = targetArch.ChunkList[chunkIdx];
                    for (int i = 0; i < targetArch.ComponentCount; i++)
                    {
                        if (!slotSet[i]) continue;
                        if (targetArch.Types[i].IsManagedShared)
                            newChunk.SetSharedValueIndex(i, managedIdx[i]);
                        else
                            WriteBlittableSharedBoxed(newChunk, i, slotValue[i], targetArch.Types[i].Size);
                    }
                }

                var entity = AllocateEntityId();
                targetArch.AddEntityToChunk(entity, chunkIdx);
                UpdateEntityLocation(entity.Id, targetArch, chunkIdx, targetArch.ChunkList[chunkIdx].EntityCount - 1);
                GetEntityInfoRef(entity.Id).Version = entity.Version;
                structuralVersion++;
                return entity;
            }
        }

        /// <summary>查找所有 slotSet 槽位与目标值匹配的未满 chunk；无返回 -1。
        /// 单 shared 列走缓存路径（复用 FindChunkWithManagedValue/BlittableBoxed，O(1) 期望）；
        /// 多列组合保持线性扫描（组合 key 无法复用单值缓存，且场景低频）。</summary>
        private int FindExistingChunkForShared(Archetype arch, bool[] slotSet, int[] managedIdx, object?[] slotValue)
        {
            // 统计已设置的 shared 列数；单列时走缓存路径
            int setCount = 0, singleIdx = -1;
            for (int c = 0; c < arch.ComponentCount; c++)
            {
                if (slotSet[c])
                {
                    setCount++;
                    singleIdx = c;
                }
            }

            if (setCount == 1)
            {
                var singleCt = arch.Types[singleIdx];
                return singleCt.IsManagedShared
                    ? FindChunkWithManagedValue(arch, singleIdx, managedIdx[singleIdx])
                    : FindChunkWithBlittableBoxed(arch, singleIdx, slotValue[singleIdx], singleCt.Type);
            }

            var list = arch.ChunkList;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].EntityCount >= list[i].Capacity) continue;
                bool match = true;
                for (int c = 0; c < arch.ComponentCount; c++)
                {
                    if (!slotSet[c]) continue;
                    if (arch.Types[c].IsManagedShared)
                    {
                        if (list[i].GetSharedValueIndex(c) != managedIdx[c]) { match = false; break; }
                    }
                    else
                    {
                        object existing = ReadBlittableSharedBoxed(list[i], c, arch.Types[c].Type);
                        if (!EqualityComparer<object>.Default.Equals(existing, slotValue[c])) { match = false; break; }
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        // ======================== 工具方法 ========================

        /// <summary>读取 chunk 槽位的 blittable shared 值（boxed，非热路径用）。</summary>
        private object ReadBlittableSharedBoxed(Chunk chunk, int compIdx, Type compType)
        {
            var ptr = chunk.GetSharedValuePointer(compIdx);
            if (ptr == nint.Zero) return null;
            return Marshal.PtrToStructure(ptr, compType);
        }

        /// <summary>将 boxed blittable shared 值写入 chunk 槽位（GCHandle pin，非热路径用）。</summary>
        private void WriteBlittableSharedBoxed(Chunk chunk, int compIdx, object value, int size)
        {
            var ptr = chunk.GetSharedValuePointer(compIdx);
            if (ptr == nint.Zero) return;
            var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                Buffer.MemoryCopy((void*)handle.AddrOfPinnedObject(), (void*)ptr, size, size);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>读取 chunk 槽位的 blittable shared 值（boxed，查询过滤用）。</summary>
        internal object ReadBlittableShared(Chunk chunk, int compIdx, Type compType)
        {
            return ReadBlittableSharedBoxed(chunk, compIdx, compType);
        }

        /// <summary>按 typeId + index 读取 managed shared 值（查询过滤用）。</summary>
        internal object GetManagedSharedValueById(int typeId, int index)
        {
            if (index >= 0 && index < _managedSharedValueCount)
                return _managedSharedValues[index];
            return null;
        }

        /// <summary>
        /// SharedComponent chunk 级过滤（单一真值来源）：
        /// 无过滤器恒 true；有则 chunk 的 shared 值与过滤值相等才通过。
        /// EntityQuery（托管查询）与 ChunkJobCollector（Job 收集）共用。
        /// </summary>
        internal bool MatchesSharedFilter(QueryBuilder query, Chunk chunk)
        {
            if (!query.HasSharedFilter) return true;
            var arch = chunk.Archetype;
            int compIdx = arch.GetComponentTypeIndex(query.SharedFilterType);
            var ct = arch.Types[compIdx];
            if (ct.IsManagedShared)
            {
                int idx = chunk.GetSharedValueIndex(compIdx);
                if (idx < 0) return false;
                object value = GetManagedSharedValueById(ct.Id, idx);
                return value != null && Equals(value, query.SharedFilterValue);
            }
            return Equals(ReadBlittableShared(chunk, compIdx, ct.Type), query.SharedFilterValue);
        }

        /// <summary>
        /// Change Tracking chunk 级过滤（单一真值来源）：
        /// 无 ChangedComponents 恒 true；有则 chunk 内任何实体被标记即通过。
        /// EntityQuery（托管查询）与 ChunkJobCollector（Job 收集）共用。
        /// </summary>
        internal bool MatchesChangedFilter(QueryBuilder query, Chunk chunk)
        {
            if (query.ChangedComponents == null || query.ChangedComponents.Length == 0) return true;
            return chunk.HasAnyEntityChanged();
        }

        /// <summary>分配实体 id（复用回收队列或递增）。</summary>
        private Entity AllocateEntityId()
        {
            var newEntity = new Entity();
            if (recycleEntities.TryDequeue(out var recycledEnt))
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
            return newEntity;
        }
    }
}