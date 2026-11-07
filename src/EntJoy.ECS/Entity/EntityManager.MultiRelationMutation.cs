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
            lock (_structuralLock) AddMultiRelationCore(source, target, type);
        }

        private void AddMultiRelationCore(Entity source, Entity target, ComponentType type)
        {
            ValidateRelationshipEntity(source, type, "source");
            ValidateRelationshipEntity(target, type, "target");
            var slot = RelationSlot.From(target);
            if (HasMultiEdge(source, type, slot)) return;
            Entity previous = default;
            bool displaced = false;
            if (type.IsExclusiveTarget && _relationIndex.TryGetSources(target, out var types)
                && types.TryGetValue(type.Id, out var owners))
                foreach (var owner in owners)
                    if (!owner.Equals(source) && HasMultiEdge(owner, type, slot))
                    {
                        if (displaced) throw new InvalidOperationException("Exclusive target has multiple owners.");
                        previous = owner; displaced = true;
                    }

            var notification = CaptureMultiNotification(source, type, ObserverEvents.Added, slot);
            var removed = displaced ? CaptureMultiNotification(previous, type, ObserverEvents.Removed, slot) : default;
            var arch = GetEntityInfoRef(source.Id).Archetype;
            bool column = type.MultiRelationMaxSlots > 0;
            bool hasColumn = arch.Has(type.Type);
            int empty = 0;
            if (column && hasColumn)
            {
                var values = (RelationSlot*)GetRelationValue(source, type);
                while (empty < type.MultiRelationMaxSlots && values[empty].IsValid) empty++;
                if (empty == type.MultiRelationMaxSlots)
                    throw new InvalidOperationException($"Multi-relation '{type.Type}' source=({source.Id}, v{source.Version}) is full (MaxSlots={type.MultiRelationMaxSlots}).");
            }
            nint prepared = column && !hasColumn ? PrepareRelationSlot(GetComponentDestination(arch, type, true), source) : 0;
            if (column && !hasColumn) arch.PrepareEntityRemoval();
            byte[] large = column && !hasColumn && type.Size > 1024 ? new byte[type.Size] : null;
            RelationListStore.RelationTargets list = null;
            HashSet<Entity> reverse;
            try
            {
                if (!column) list = _relationListStore.PrepareAdd(type.Id, source);
                reverse = _relationIndex.PrepareAdd(type.Id, target);
                CheckRelationshipPreparation("multi-relation-prepared");
            }
            catch
            {
                _relationListStore.DiscardEmpty(type.Id, source);
                _relationIndex.DiscardEmpty(type.Id, target);
                throw;
            }
            byte* small = stackalloc byte[column && !hasColumn && type.Size <= 1024 ? type.Size : 0];
            fixed (byte* heap = large)
            {
                if (!column) list.Add(slot);
                else if (hasColumn) ((RelationSlot*)GetRelationValue(source, type))[empty] = slot;
                else
                {
                    var values = (RelationSlot*)(heap != null ? heap : small);
                    for (int i = 0; i < type.MultiRelationMaxSlots; i++) values[i] = RelationSlot.Default;
                    values[0] = slot;
                    AddComponentRawCore(source, type.Type, null, (byte*)values, type.Size, relationStorageOnly: true, preparedChunk: prepared);
                }
            }
            reverse.Add(source);
            if (displaced) CommitRemoveMultiEdge(previous, type, slot);
            ref var location = ref GetEntityInfoRef(source.Id);
            location.Archetype.NotifyComponentChanged(location.ChunkIndex, location.SlotInChunk);
            _relationshipVersion++;
            CommitRelationNotification(removed);
            CommitRelationNotification(notification);
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
            _relationshipVersion++;
        }

        internal void RemoveMultiRelationshipRaw(Entity source, Entity target, ComponentType type)
        {
            CheckDisposed(); RequireMultiRelation(type); CompleteActiveJobs();
            using var changes = BeginRelationChanges();
            lock (_structuralLock)
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
            lock (_structuralLock)
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
                _relationshipVersion += targets.Length;
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
