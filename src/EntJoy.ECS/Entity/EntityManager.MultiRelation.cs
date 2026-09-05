using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 多实例关系（[MultiRelation]）操作：双模式路由。
    /// - 托管列表模式（[MultiRelation]，MaxSlots=0）：RelationListStore（无界，主线程，NT017 拦截 Job）。
    /// - 定长多槽列模式（[MultiRelation(MaxSlots=N)]，N≥2）：chunk 列（宽 N×8B），Job 可读。
    /// 两种模式共用反向索引（RelationIndex，target→sources O(1)）。
    /// 叠加 [ExclusiveTarget] 时 target 侧唯一（背包：物品唯一持有者）。
    /// </summary>
    public unsafe partial class EntityManager
    {
        // ======================== 定长多槽列 读写辅助 ========================
        // TRel 首 N 个字段为连续 RelationSlot（源生成器注入 Slot1..SlotN-1，Sequential 布局），
        // Unsafe.As<TRel, RelationSlot> 取槽 0 引用，Unsafe.Add 按 8B 步进访问槽 i。

        /// <summary>读 TRel 的槽 i（0..N-1）。</summary>
        private static RelationSlot GetSlot<TRel>(ref TRel value, int i) where TRel : struct
            => Unsafe.Add(ref Unsafe.As<TRel, RelationSlot>(ref value), i);

        /// <summary>写 TRel 的槽 i。</summary>
        private static void SetSlot<TRel>(ref TRel value, int i, RelationSlot slot) where TRel : struct
            => Unsafe.Add(ref Unsafe.As<TRel, RelationSlot>(ref value), i) = slot;

        /// <summary>实体列中是否存在指向 target 的槽（幂等去重）。</summary>
        private bool ColumnHasTarget<TRel>(ref TRel value, int maxSlots, in RelationSlot target) where TRel : struct
        {
            for (int i = 0; i < maxSlots; i++)
            {
                if (GetSlot(ref value, i).Matches(target)) return true;
            }
            return false;
        }

        /// <summary>找首个空槽索引；-1 = 槽满。空槽 = RelationSlot.Default（TargetId = -1）；
        /// 注意不能用 IsValid（TargetId ≥ 0）判空——default(TRel) 的槽位是 {0,0} 而非 Default。</summary>
        private int FindEmptySlot<TRel>(ref TRel value, int maxSlots) where TRel : struct
        {
            for (int i = 0; i < maxSlots; i++)
            {
                if (GetSlot(ref value, i).TargetId < 0) return i;
            }
            return -1;
        }

        // ======================== Add（核心，锁内调用） ========================

        /// <summary>
        /// 多值关系追加核心（锁内调用）。幂等去重；[ExclusiveTarget] 时先解绑旧 source 的该 target 条目。
        /// 按 MaxSlots 路由：≥2 定长列（槽满抛异常），否则托管列表。
        /// </summary>
        private void AddMultiRelationshipCore<TRel>(Entity entity, Entity target, int relTypeId)
            where TRel : struct, IRelationComponent
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            if (entityInfoRef.Archetype == null)
                throw new InvalidOperationException($"Entity {entity} has been destroyed.");
            if (entityInfoRef.Version != entity.Version)
                throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");

            var compType = ComponentTypeManager.GetComponentType(typeof(TRel));
            var slotValue = RelationSlot.From(target);
            int maxSlots = compType.MultiRelationMaxSlots;
            bool isColumn = maxSlots >= 2;

            // [ExclusiveTarget]：target 已被其他 source 持有 → 解绑旧 source 的该 target 条目（背包语义）
            if (compType.IsExclusiveTarget &&
                _relationIndex.TryGetSources(target.Id, out var byTypeEx) &&
                byTypeEx.TryGetValue(relTypeId, out var setEx))
            {
                foreach (var oldSource in setEx)
                {
                    if (oldSource.Id == entity.Id && oldSource.Version == entity.Version) continue;
                    if (!IsAlive(oldSource)) continue;
                    if (isColumn)
                        RemoveColumnTarget<TRel>(oldSource, relTypeId, maxSlots, in slotValue);
                    else
                        RemoveListTarget(oldSource, relTypeId, in slotValue);
                }
            }

            bool added;
            if (isColumn)
                added = AddColumnTarget<TRel>(entity, ref entityInfoRef, compType, maxSlots, in slotValue);
            else
                added = AddListTarget(entity, relTypeId, in slotValue);

            _relationIndex.Add(relTypeId, entity, target);

            // Observer：Added（对齐单值关系；多值关系值 = RelationSlot）
            if (added && _observerCount > 0 && _observers != null &&
                _observers.TryGetValue(relTypeId, out var reg) && reg.Added.Count > 0)
            {
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(slotValue, System.Runtime.InteropServices.GCHandleType.Pinned);
                try { DispatchAdded(reg.Added, &entity, (void*)handle.AddrOfPinnedObject(), 1); }
                finally { handle.Free(); }
            }
        }

        /// <summary>定长列追加：写首个空槽。返回是否真正新增（幂等去重返回 false）。槽满抛异常。</summary>
        private bool AddColumnTarget<TRel>(Entity entity, ref EntityIndexInWorld info, ComponentType compType, int maxSlots, in RelationSlot target)
            where TRel : struct, IRelationComponent
        {
            var arch = info.Archetype!;
            if (!arch.Has(compType.Type))
            {
                // 首次：AddComponentRawCore 建列。所有槽显式初始化为 Default（-1）——
                // default(TRel) 的槽位是 {0,0}，会被 IsValid 误判为有效关系。
                var relValue = default(TRel);
                for (int i = 0; i < maxSlots; i++)
                    SetSlot(ref relValue, i, RelationSlot.Default);
                SetSlot(ref relValue, 0, target);
                AddComponentRawCore(entity, compType.Type, relValue);
                return true;
            }

            int compIdx = arch.GetComponentTypeIndex(compType);
            var chunk = arch.ChunkList[info.ChunkIndex];
            ref var col = ref chunk.GetComponent<TRel>(info.SlotInChunk, compIdx);

            if (ColumnHasTarget(ref col, maxSlots, in target))
                return false;   // 幂等

            int empty = FindEmptySlot(ref col, maxSlots);
            if (empty < 0)
                throw new InvalidOperationException(
                    $"Multi-relation '{typeof(TRel).Name}' column full (MaxSlots={maxSlots}). "
                    + $"Entity {entity} cannot hold more targets. Increase MaxSlots or remove existing targets first.");
            SetSlot(ref col, empty, target);
            arch.NotifyComponentChangedPublic(info.ChunkIndex, info.SlotInChunk);   // 变更追踪
            return true;
        }

        /// <summary>托管列表追加：RelationListStore 幂等去重。</summary>
        private bool AddListTarget(Entity entity, int relTypeId, in RelationSlot target)
        {
            bool added = !_relationListStore.Has(relTypeId, entity, in target);
            _relationListStore.Add(relTypeId, entity, target);
            return added;
        }

        /// <summary>定长列移除：清空匹配槽为 Default。返回是否移除。</summary>
        private bool RemoveColumnTarget<TRel>(Entity entity, int relTypeId, int maxSlots, in RelationSlot target)
            where TRel : struct, IRelationComponent
        {
            if (!IsAlive(entity)) return false;
            ref var info = ref GetEntityInfoRef(entity.Id);
            var arch = info.Archetype!;
            var compType = ComponentTypeManager.GetComponentType(typeof(TRel));
            if (!arch.Has(compType.Type)) return false;

            int compIdx = arch.GetComponentTypeIndex(compType);
            var chunk = arch.ChunkList[info.ChunkIndex];
            ref var col = ref chunk.GetComponent<TRel>(info.SlotInChunk, compIdx);

            for (int i = 0; i < maxSlots; i++)
            {
                if (GetSlot(ref col, i).Matches(target))
                {
                    SetSlot(ref col, i, RelationSlot.Default);
                    arch.NotifyComponentChangedPublic(info.ChunkIndex, info.SlotInChunk);
                    _relationIndex.RemoveRelTypeId(relTypeId, entity, in target);
                    return true;
                }
            }
            return false;
        }

        /// <summary>托管列表移除。</summary>
        private bool RemoveListTarget(Entity entity, int relTypeId, in RelationSlot target)
        {
            if (!IsAlive(entity)) return false;
            if (_relationListStore.Remove(relTypeId, entity, in target))
            {
                _relationIndex.RemoveRelTypeId(relTypeId, entity, in target);
                return true;
            }
            return false;
        }

        // ======================== 多值关系专用 API ========================

        /// <summary>多值关系：移除 entity 指向 target 的条目（无则 no-op）。同步维护反向索引。</summary>
        public void RemoveRelationship<TRel>(Entity entity, Entity target)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            var compType = ComponentTypeManager.GetComponentType(typeof(TRel));
            if (!compType.IsMultiRelation)
                throw new InvalidOperationException(
                    $"RemoveRelationship(entity, target) is only for [MultiRelation] types. '{typeof(TRel).Name}' is not marked [MultiRelation].");

            CompleteActiveJobs();
            lock (_structuralLock)
            {
                if (!IsAlive(entity)) return;
                var slotValue = RelationSlot.From(target);
                if (compType.MultiRelationMaxSlots >= 2)
                    RemoveColumnTarget<TRel>(entity, compType.Id, compType.MultiRelationMaxSlots, in slotValue);
                else
                    RemoveListTarget(entity, compType.Id, in slotValue);
            }
        }

        /// <summary>多值关系：entity 是否指向 target。</summary>
        public bool HasRelationship<TRel>(Entity entity, Entity target)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            var compType = ComponentTypeManager.GetComponentType(typeof(TRel));
            if (!compType.IsMultiRelation)
                throw new InvalidOperationException(
                    $"HasRelationship(entity, target) is only for [MultiRelation] types. '{typeof(TRel).Name}' is not marked [MultiRelation].");
            if (!IsAlive(entity)) return false;
            var slotValue = RelationSlot.From(target);

            if (compType.MultiRelationMaxSlots >= 2)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                var arch = info.Archetype!;
                if (!arch.Has(compType.Type)) return false;
                int compIdx = arch.GetComponentTypeIndex(compType);
                var chunk = arch.ChunkList[info.ChunkIndex];
                ref var col = ref chunk.GetComponent<TRel>(info.SlotInChunk, compIdx);
                return ColumnHasTarget(ref col, compType.MultiRelationMaxSlots, in slotValue);
            }
            return _relationListStore.Has(compType.Id, entity, in slotValue);
        }

        /// <summary>多值关系：entity 的全部 targets（存活 + 版本校验过滤，新分配数组）。</summary>
        public Entity[] GetRelationships<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            var compType = ComponentTypeManager.GetComponentType(typeof(TRel));
            if (!compType.IsMultiRelation)
                throw new InvalidOperationException(
                    $"GetRelationships(entity) is only for [MultiRelation] types. '{typeof(TRel).Name}' is not marked [MultiRelation].");
            if (!IsAlive(entity)) return Array.Empty<Entity>();

            if (compType.MultiRelationMaxSlots >= 2)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                var arch = info.Archetype!;
                if (!arch.Has(compType.Type)) return Array.Empty<Entity>();
                int compIdx = arch.GetComponentTypeIndex(compType);
                var chunk = arch.ChunkList[info.ChunkIndex];
                ref var col = ref chunk.GetComponent<TRel>(info.SlotInChunk, compIdx);

                int maxSlots = compType.MultiRelationMaxSlots;
                var temp = new Entity[maxSlots];
                int count = 0;
                for (int i = 0; i < maxSlots; i++)
                {
                    var slot = GetSlot(ref col, i);
                    if (!slot.IsValid) continue;
                    if ((uint)slot.TargetId >= (uint)entities.Length) continue;
                    var targetInfo = GetEntityInfoRef(slot.TargetId);
                    if (targetInfo.Archetype != null && targetInfo.Version == slot.TargetVersion)
                        temp[count++] = slot.ToEntity();
                }
                if (count == 0) return Array.Empty<Entity>();
                var result = new Entity[count];
                Array.Copy(temp, result, count);
                return result;
            }

            var list = _relationListStore.TryGetTargets(compType.Id, entity);
            if (list == null || list.Count == 0) return Array.Empty<Entity>();

            var result2 = new Entity[list.Count];
            int i2 = 0;
            foreach (var slot in list)
            {
                if ((uint)slot.TargetId >= (uint)entities.Length) continue;
                var targetInfo = GetEntityInfoRef(slot.TargetId);
                if (targetInfo.Archetype != null && targetInfo.Version == slot.TargetVersion)
                    result2[i2++] = slot.ToEntity();
            }
            if (i2 != result2.Length) Array.Resize(ref result2, i2);
            return result2;
        }

        /// <summary>多值关系：entity 的有效关系条数（定长列 = 有效槽数；托管 = 列表长度）。</summary>
        public int GetRelationshipCount<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            var compType = ComponentTypeManager.GetComponentType(typeof(TRel));
            if (!compType.IsMultiRelation)
                throw new InvalidOperationException(
                    $"GetRelationshipCount(entity) is only for [MultiRelation] types. '{typeof(TRel).Name}' is not marked [MultiRelation].");
            if (!IsAlive(entity)) return 0;

            if (compType.MultiRelationMaxSlots >= 2)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                var arch = info.Archetype!;
                if (!arch.Has(compType.Type)) return 0;
                int compIdx = arch.GetComponentTypeIndex(compType);
                var chunk = arch.ChunkList[info.ChunkIndex];
                ref var col = ref chunk.GetComponent<TRel>(info.SlotInChunk, compIdx);
                int count = 0;
                for (int i = 0; i < compType.MultiRelationMaxSlots; i++)
                {
                    if (GetSlot(ref col, i).IsValid) count++;
                }
                return count;
            }
            return _relationListStore.GetCount(compType.Id, entity);
        }

        /// <summary>多值关系：清空 entity 的全部条目。同步维护反向索引。</summary>
        public void ClearRelationships<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            var compType = ComponentTypeManager.GetComponentType(typeof(TRel));
            if (!compType.IsMultiRelation)
                throw new InvalidOperationException(
                    $"ClearRelationships(entity) is only for [MultiRelation] types. '{typeof(TRel).Name}' is not marked [MultiRelation].");

            CompleteActiveJobs();
            lock (_structuralLock)
            {
                if (!IsAlive(entity)) return;

                if (compType.MultiRelationMaxSlots >= 2)
                {
                    ref var info = ref GetEntityInfoRef(entity.Id);
                    var arch = info.Archetype!;
                    if (!arch.Has(compType.Type)) return;
                    int compIdx = arch.GetComponentTypeIndex(compType);
                    var chunk = arch.ChunkList[info.ChunkIndex];
                    ref var col = ref chunk.GetComponent<TRel>(info.SlotInChunk, compIdx);
                    for (int i = 0; i < compType.MultiRelationMaxSlots; i++)
                    {
                        var slot = GetSlot(ref col, i);
                        if (slot.IsValid)
                        {
                            _relationIndex.RemoveRelTypeId(compType.Id, entity, in slot);
                            SetSlot(ref col, i, RelationSlot.Default);
                        }
                    }
                    arch.NotifyComponentChangedPublic(info.ChunkIndex, info.SlotInChunk);
                    return;
                }

                var list = _relationListStore.TryGetTargets(compType.Id, entity);
                if (list == null) return;
                foreach (var slot in list)
                    _relationIndex.RemoveRelTypeId(compType.Id, entity, in slot);
                _relationListStore.ClearSource(compType.Id, entity);
            }
        }

        // ======================== 销毁清理集成 ========================
        // 定长列模式：TRel 占 chunk 列，DestroyEntityCore 的 CleanupSourceRelations（列路径）自动清理反向索引；
        // 托管模式：走 CleanupMultiSourceRelations（列表路径）。

        /// <summary>实体销毁时清理其全部托管模式多值关系（正向列表 + 反向索引）。锁内调用。
        /// 定长列模式不需要——列清理走 CleanupSourceRelations。</summary>
        private void CleanupMultiSourceRelations(Entity entity)
        {
            var removed = _multiRelRemovedBuffer;
            _relationListStore.ClearAllForSource(entity, removed);
            foreach (var (relTypeId, slot) in removed)
                _relationIndex.RemoveRelTypeId(relTypeId, entity, in slot);
        }

        // 复用缓冲：销毁路径的 (relTypeId, slot) 收集（避免每实体分配）
        private readonly List<(int relTypeId, RelationSlot slot)> _multiRelRemovedBuffer = new();
    }
}
