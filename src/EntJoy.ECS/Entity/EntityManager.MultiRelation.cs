using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 多实例关系（[MultiRelation]）操作：双模式路由。
    /// - 托管列表模式（[MultiRelation]，MaxSlots=0）：RelationListStore（无界，主线程，NT017 拦截 Job）。
    /// 叠加 [ExclusiveTarget] 时 target 侧唯一（背包：物品唯一持有者）。
    /// </summary>
    public unsafe partial class EntityManager
    {
        private static RelationSlot GetSlot<TRel>(ref TRel value, int index) where TRel : struct
            => Unsafe.Add(ref Unsafe.As<TRel, RelationSlot>(ref value), index);

        private bool ColumnHasTarget<TRel>(ref TRel value, int count, in RelationSlot target) where TRel : struct
        {
            for (int i = 0; i < count; i++) if (GetSlot(ref value, i).Matches(target)) return true;
            return false;
        }

        // 多值关系专用 API

        /// <summary>多值关系：移除 entity 指向 target 的条目（无则 no-op）。同步维护反向索引。</summary>
        public void RemoveRelationship<TRel>(Entity entity, Entity target)
            where TRel : struct, IRelationComponent
        {
            RemoveMultiRelationshipRaw(entity, target, ComponentTypeManager.GetComponentType(typeof(TRel)));
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
            if (!IsAlive(entity) || !IsAlive(target)) return false;
            var slotValue = RelationSlot.From(target);

            if (compType.MultiRelationMaxSlots >= 2)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                var arch = info.Archetype!;
                if (!arch.Has(compType.Type)) return false;
                int compIdx = arch.GetComponentTypeIndex(compType);
                var chunk = arch.ChunkList[info.ChunkIndex];
                ref var col = ref chunk.GetComponentStorage<TRel>(info.SlotInChunk, compIdx);
                return ColumnHasTarget(ref col, compType.MultiRelationMaxSlots, in slotValue);
            }
            return _relationListStore.Has(compType.Id, entity, in slotValue);
        }

        /// <summary>多值关系：entity 的全部 targets（存活 + 版本校验过滤，新分配数组）。</summary>
        public Entity[] GetRelationships<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            int count = GetRelationships<TRel>(entity, Span<Entity>.Empty);
            if (count == 0) return Array.Empty<Entity>();
            var result = new Entity[count];
            GetRelationships<TRel>(entity, result.AsSpan());
            return result;
        }

        /// <summary>有效目标数量，校验完整实体身份。</summary>
        public int GetRelationshipCount<TRel>(Entity entity) where TRel : struct, IRelationComponent
            => GetRelationships<TRel>(entity, Span<Entity>.Empty);

        /// <summary>多值关系：清空 entity 的全部条目。同步维护反向索引。</summary>
        public void ClearRelationships<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            ClearMultiRelationshipsRaw(entity, ComponentTypeManager.GetComponentType(typeof(TRel)));
        }

        // 销毁清理集成
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
