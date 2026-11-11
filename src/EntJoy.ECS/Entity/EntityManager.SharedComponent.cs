using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.ECS
{
    /// <summary>
    /// EntityManager 的 Shared Component 支持（per-chunk 存储）。核心不变式：同一块的所有实体共享相同的值组合。
    /// blittable 值内联存在块的 Shared values 区，只增不减，World.Dispose 时整体清空；
    /// managed 值走引用计数与去重表。改变 shared 值会让实体在原型内部换块，不换原型。
    /// </summary>
    public unsafe partial class EntityManager
    {
        // managed shared 值存储（简化版：无 refcount，值只增不减）

        /// <summary>managed shared 值本体（全局去重数组）。World.Dispose 时清空。</summary>
        private object[] _managedSharedValues = new object[16];
        private int _managedSharedValueCount;

        /// <summary>per-type 查找表：typeId → (value → globalIndex)。Dictionary 简洁可靠，值种类通常 &lt;200。</summary>
        private Dictionary<int, Dictionary<object, int>> _managedLookup = new();

        /// <summary>
        /// per-value 最近使用缓存：shared 值 → 最近一次命中的 chunk 索引（Archetype 内）。
        /// key = (Archetype, 组件索引, 值)。managed 的值为全局 index（int box）；blittable 为 boxed 值。
        /// 导致验证失败时移除条目并回退全扫描。删除路径零维护。
        /// </summary>
        private Dictionary<(Archetype, int, object), int> _lastChunkPerSharedValue = new();

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

        // 读取 / 写入 API

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
        /// 3. 值相同 → 无操作。
        /// </summary>
        public void SetSharedComponent<T>(Entity entity, T value) where T : ISharedComponentData
        {
            CheckDisposed();
            CompleteActiveJobs();
            using (EnterStructural())
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
                    if (oldIdx == newIdx) return;
                    if (srcChunk.EntityCount == 1)
                    {
                        srcChunk.SetSharedValueIndex(compIdx, newIdx);
                        CommitInPlaceSharedChange(srcChunk, info.SlotInChunk);
                        return;
                    }
                    // 多实体：按完整 shared 组合找/建目标块，否则其他 shared 组件值会丢。
                    MoveEntityWithShared(arch, entity, srcChunkIndex, compIdx, newIdx, null);
                }
                else
                {
                    object curVal = ReadBlittableSharedBoxed(srcChunk, compIdx, ct.Type);
                    if (EqualityComparer<object>.Default.Equals(curVal, value)) return;
                    if (srcChunk.EntityCount == 1)
                    {
                        WriteBlittableSharedBoxed(srcChunk, compIdx, value, ct.Size);
                        CommitInPlaceSharedChange(srcChunk, info.SlotInChunk);
                        return;
                    }
                    MoveEntityWithShared(arch, entity, srcChunkIndex, compIdx, null, value);
                }
            }
        }

        // 单实体块就地改值：标记实体已变更，并推进结构版本。后者不可省——EntityQuery 与调度缓存
        // 只在结构版本变化时刷新匹配集合，否则 WithShared(旧值) 的查询仍会返回这个块（静默错值）。
        private void CommitInPlaceSharedChange(Chunk chunk, int slotInChunk)
        {
            chunk.MarkEntityChanged(slotInChunk);
            InvalidateStructuralQueries();
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

            // 缓存命中路径同上：验证存活、未满、值匹配，失败则移除该条目。
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

            RemoveAndFixup(arch, srcChunkIndex, srcSlot);

            UpdateEntityLocation(entity.Id, arch, dstChunkIndex, dstSlot);
            InvalidateStructuralQueries();
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


        // 工具方法

        /// <summary>读取 chunk 槽位的 blittable shared 值（boxed，非热路径用）。</summary>
        private object ReadBlittableSharedBoxed(Chunk chunk, int compIdx, Type compType)
        {
            var ptr = chunk.GetSharedValuePointer(compIdx);
            if (ptr == nint.Zero) return null;
            return ManagedValueLayout.ReadBoxed(compType, ptr);
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
        public bool MatchesSharedFilter(QueryBuilder query, Chunk chunk)
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
            return Equals(ReadBlittableSharedBoxed(chunk, compIdx, ct.Type), query.SharedFilterValue);
        }

        /// <summary>
        /// Change Tracking chunk 级过滤（单一真值来源）：
        /// 无 ChangedComponents 恒 true；有则 chunk 内任何实体被标记即通过。
        /// EntityQuery（托管查询）与 ChunkJobCollector（Job 收集）共用。
        /// </summary>
        public bool MatchesChangedFilter(QueryBuilder query, Chunk chunk)
        {
            if (query.ChangedComponents == null || query.ChangedComponents.Length == 0) return true;
            return chunk.HasAnyEntityChanged();
        }

        /// <summary>分配实体 id（复用回收栈或递增）。</summary>
        private Entity AllocateEntityId()
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
                    ResizeEntityTable(entities.Length * 2);
            }
            return newEntity;
        }
    }
}
