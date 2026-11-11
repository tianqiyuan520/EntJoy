using System;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        private static void RequireMultiRelation(ComponentType type)
        {
            if (!type.IsRelation || !type.IsMultiRelation)
                throw new ArgumentException($"Relation '{type.Type}' requires a multi-target type.");
        }

        private RelationNotification CaptureMultiNotification(Entity source, ComponentType type, ObserverEvents kind, RelationSlot target)
        {
            if (_observerCount == 0) return default;
            byte[] large = type.Size > 1024 ? new byte[type.Size] : null;
            byte* small = stackalloc byte[type.Size <= 1024 ? type.Size : 0];
            fixed (byte* heap = large)
            {
                byte* value = heap != null ? heap : small;
                for (int i = 0; i < Math.Max(1, type.MultiRelationMaxSlots); i++) ((RelationSlot*)value)[i] = RelationSlot.Default;
                *(RelationSlot*)value = target;
                return CaptureRelationNotification(source, type, kind, value);
            }
        }

        private bool HasMultiEdge(Entity source, ComponentType type, RelationSlot target)
        {
            if (!IsAlive(source)) return false;
            if (type.MultiRelationMaxSlots == 0) return _relationListStore.Has(type.Id, source, target);
            if (!GetEntityInfoRef(source.Id).Archetype.Has(type.Type)) return false;
            var values = (RelationSlot*)GetRelationValue(source, type);
            for (int i = 0; i < type.MultiRelationMaxSlots; i++) if (values[i].Matches(target)) return true;
            return false;
        }

        internal void AddMultiRelationshipRaw(Entity source, Entity target, ComponentType type)
        {
            CheckDisposed(); RequireMultiRelation(type);
            CompleteActiveJobs();
            using var changes = BeginRelationChanges();
            using (EnterStructural()) AddMultiRelationCore(source, target, type);
        }

        private void AddMultiRelationCore(Entity source, Entity target, ComponentType type)
        {
            ValidateRelationshipEntity(source, type, "source");
            ValidateRelationshipEntity(target, type, "target");
            var slot = RelationSlot.From(target);
            if (HasMultiEdge(source, type, slot)) return;

            Entity previous = default;
            bool displaced = type.IsExclusiveTarget
                && FindDisplacedOwner(source, type, slot, out previous);
            var notification = CaptureMultiNotification(source, type, ObserverEvents.Added, slot);
            var removed = displaced ? CaptureMultiNotification(previous, type, ObserverEvents.Removed, slot) : default;

            var arch = GetEntityInfoRef(source.Id).Archetype;
            bool column = type.MultiRelationMaxSlots > 0;
            bool hasColumn = arch.Has(type.Type);
            int empty = column && hasColumn ? FindEmptyColumnSlot(source, type) : 0;
            nint prepared = column && !hasColumn ? PrepareNewRelationColumn(arch, source, type) : 0;

            HashSet<Entity> reverse = PrepareMultiRelationStorage(source, target, type, column,
                out RelationListStore.RelationTargets list);
            WriteMultiEdge(source, type, slot, column, hasColumn, empty, prepared, list);
            reverse.Add(source);

            if (displaced) CommitRemoveMultiEdge(previous, type, slot);
            ref var location = ref GetEntityInfoRef(source.Id);
            location.Archetype.NotifyComponentChanged(location.ChunkIndex, location.SlotInChunk);
            InvalidateRelationshipQueries();
            CommitRelationNotification(removed);
            CommitRelationNotification(notification);
        }

        // 入边唯一约束：找出当前持有该 target 的其他 source；出现多个说明索引已损坏。
        private bool FindDisplacedOwner(Entity source, ComponentType type, RelationSlot slot, out Entity previous)
        {
            previous = default;
            var owners = _relationIndex.GetSourcesOfType(slot.ToEntity(), type.Id);
            if (owners == null) return false;
            bool displaced = false;
            foreach (var owner in owners)
            {
                if (owner.Equals(source) || !HasMultiEdge(owner, type, slot)) continue;
                if (displaced) throw new InvalidOperationException("Exclusive target has multiple owners.");
                previous = owner;
                displaced = true;
            }
            return displaced;
        }

        // 定长多槽列里第一个空槽；列满时直接失败，不做静默覆盖。
        private int FindEmptyColumnSlot(Entity source, ComponentType type)
        {
            var values = (RelationSlot*)GetRelationValue(source, type);
            int empty = 0;
            while (empty < type.MultiRelationMaxSlots && values[empty].IsValid) empty++;
            if (empty == type.MultiRelationMaxSlots)
                throw new InvalidOperationException($"Multi-relation '{type.Type}' source=({source.Id}, v{source.Version}) is full (MaxSlots={type.MultiRelationMaxSlots}).");
            return empty;
        }

        // source 首次获得定长多槽列：预留槽位并让原型准备好本轮移除。
        private nint PrepareNewRelationColumn(Archetype arch, Entity source, ComponentType type)
        {
            nint prepared = PrepareRelationSlot(GetComponentDestination(arch, type, true), source);
            arch.PrepareEntityRemoval();
            return prepared;
        }

        // 预留列表条目与反向索引条目；任一失败都丢弃刚建的空桶，保持两侧一致。
        private HashSet<Entity> PrepareMultiRelationStorage(Entity source, Entity target, ComponentType type, bool column,
            out RelationListStore.RelationTargets list)
        {
            list = null;
            try
            {
                if (!column) list = _relationListStore.PrepareAdd(type.Id, source);
                var reverse = _relationIndex.PrepareAdd(type.Id, target);
                CheckRelationshipPreparation("multi-relation-prepared");
                return reverse;
            }
            catch
            {
                _relationListStore.DiscardEmpty(type.Id, source);
                _relationIndex.DiscardEmpty(type.Id, target);
                throw;
            }
        }

        // 三条写入路径：托管列表追加、定长列的空槽、首次建列（整列初始化为空再写首槽）。
        private void WriteMultiEdge(Entity source, ComponentType type, RelationSlot slot,
            bool column, bool hasColumn, int empty, nint prepared, RelationListStore.RelationTargets list)
        {
            if (!column) { list.Add(slot); return; }
            if (hasColumn) { ((RelationSlot*)GetRelationValue(source, type))[empty] = slot; return; }

            byte[] large = type.Size > 1024 ? new byte[type.Size] : null;
            byte* small = stackalloc byte[large == null ? type.Size : 0];
            fixed (byte* heap = large)
            {
                var values = (RelationSlot*)(large != null ? heap : small);
                for (int i = 0; i < type.MultiRelationMaxSlots; i++) values[i] = RelationSlot.Default;
                values[0] = slot;
                AddComponentRawCore(source, type.Type, null, (byte*)values, type.Size, relationStorageOnly: true, preparedChunk: prepared);
            }
        }

        private void CommitRemoveMultiEdge(Entity source, ComponentType type, RelationSlot target)
        {
            if (type.MultiRelationMaxSlots == 0) _relationListStore.Remove(type.Id, source, target);
            else
            {
                var values = (RelationSlot*)GetRelationValue(source, type);
                for (int i = 0; i < type.MultiRelationMaxSlots; i++)
                    if (values[i].Matches(target)) { values[i] = RelationSlot.Default; break; }
            }
            _relationIndex.RemoveRelTypeId(type.Id, source, target);
            ref var location = ref GetEntityInfoRef(source.Id);
            location.Archetype.NotifyComponentChanged(location.ChunkIndex, location.SlotInChunk);
            InvalidateRelationshipQueries();
        }

        internal void RemoveMultiRelationshipRaw(Entity source, Entity target, ComponentType type)
        {
            CheckDisposed(); RequireMultiRelation(type); CompleteActiveJobs();
            using var changes = BeginRelationChanges();
            using (EnterStructural())
            {
                var slot = RelationSlot.From(target);
                if (!HasMultiEdge(source, type, slot)) return;
                var notification = CaptureMultiNotification(source, type, ObserverEvents.Removed, slot);
                CheckRelationshipPreparation("multi-unlink-prepared");
                CommitRemoveMultiEdge(source, type, slot);
                CommitRelationNotification(notification);
            }
        }

        internal void ClearMultiRelationshipsRaw(Entity source, ComponentType type)
        {
            CheckDisposed(); RequireMultiRelation(type); CompleteActiveJobs();
            using var changes = BeginRelationChanges();
            using (EnterStructural())
            {
                if (!IsAlive(source)) return;
                var targets = GetMultiTargets(source, type);
                if (targets.Length == 0) return;
                var notifications = _observerCount == 0 ? null : new RelationNotification[targets.Length];
                if (notifications != null)
                {
                    for (int i = 0; i < targets.Length; i++) notifications[i] = CaptureMultiNotification(source, type, ObserverEvents.Removed, targets[i]);
                    EnsureRelationNotificationCapacity(notifications.Length);
                }
                CheckRelationshipPreparation("multi-clear-prepared");
                foreach (var target in targets) _relationIndex.RemoveRelTypeId(type.Id, source, target);
                if (type.MultiRelationMaxSlots == 0) _relationListStore.ClearSource(type.Id, source);
                else
                {
                    var slots = (RelationSlot*)GetRelationValue(source, type);
                    for (int i = 0; i < type.MultiRelationMaxSlots; i++) slots[i] = RelationSlot.Default;
                }
                ref var location = ref GetEntityInfoRef(source.Id);
                location.Archetype.NotifyComponentChanged(location.ChunkIndex, location.SlotInChunk);
                InvalidateRelationshipQueries(targets.Length);
                if (notifications != null) foreach (var notification in notifications) CommitRelationNotification(notification);
            }
        }

        private void EnsureRelationNotificationCapacity(int additional)
        {
            if (_observerCount == 0) return;
            _relationNotifications ??= new List<RelationNotification>();
            _relationNotifications.EnsureCapacity(checked(_relationNotifications.Count + additional));
        }

        private RelationSlot[] GetMultiTargets(Entity source, ComponentType type)
        {
            if (type.MultiRelationMaxSlots == 0) return _relationListStore.TryGetTargets(type.Id, source)?.ToArray() ?? Array.Empty<RelationSlot>();
            if (!GetEntityInfoRef(source.Id).Archetype.Has(type.Type)) return Array.Empty<RelationSlot>();
            var result = new List<RelationSlot>();
            var values = (RelationSlot*)GetRelationValue(source, type);
            for (int i = 0; i < type.MultiRelationMaxSlots; i++) if (values[i].IsValid) result.Add(values[i]);
            return result.ToArray();
        }
    }
}
