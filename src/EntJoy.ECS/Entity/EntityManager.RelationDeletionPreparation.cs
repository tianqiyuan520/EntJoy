using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        private sealed class DeletionDestination
        {
            public Archetype Archetype;
            public nint Memory;
            public int Index, Reserved;
        }

        private struct DeletionUnlink
        {
            public Entity Source;
            private (ComponentType Type, RelationSlot Target) _first;
            private List<(ComponentType Type, RelationSlot Target)> _additional;
            public int EdgeCount { get; private set; }
            public void AddEdge((ComponentType Type, RelationSlot Target) edge)
            {
                if (EdgeCount == 0) _first = edge;
                else (_additional ??= new()).Add(edge);
                EdgeCount++;
            }
            public (ComponentType Type, RelationSlot Target) EdgeAt(int index) => index == 0 ? _first : _additional[index - 1];
            public DeletionDestination Destination;
        }

        private List<RelationNotification> CaptureDestroyedNotifications(Entity entity)
        {
            List<RelationNotification> notifications = null;
            if (_observerCount == 0 || _observers == null) return notifications;
            var info = GetEntityInfoRef(entity.Id);
            var arch = info.Archetype;
            foreach (var type in arch.Types)
            {
                if (type.IsDisposable || type.IsManagedShared) continue;
                int column = arch.GetComponentTypeIndex(type);
                var chunk = arch.ChunkList[info.ChunkIndex];
                byte* value = type.IsShared ? (byte*)chunk.GetSharedValuePointer(column)
                    : (byte*)chunk.GetComponentArrayPointer(column) + info.SlotInChunk * type.Size;
                if (type.IsMultiRelation)
                {
                    if (type.MultiRelationMaxSlots == 0) continue;
                    foreach (var target in GetMultiTargets(entity, type))
                    {
                        var edge = CaptureMultiNotification(entity, type, ObserverEvents.Destroyed, target);
                        if (edge.Value != null) (notifications ??= new()).Add(edge);
                    }
                    continue;
                }
                if (type.IsRelation && !((RelationSlot*)value)->IsValid) continue;
                var notification = CaptureRelationNotification(entity, type, ObserverEvents.Destroyed, value);
                if (notification.Value != null) (notifications ??= new()).Add(notification);
            }
            if (_relationListStore.HasRelationshipsForSource(entity))
                foreach (var entry in _relationListStore.ForSource(entity))
                {
                    var type = ComponentTypeManager.GetComponentType(ComponentTypeManager.GetTypeByComponentType(entry.TypeId));
                    foreach (var target in entry.Targets)
                    {
                        var edge = CaptureMultiNotification(entity, type, ObserverEvents.Destroyed, target);
                        if (edge.Value != null) (notifications ??= new()).Add(edge);
                    }
                }
            return notifications;
        }

        private int CommitPreparedDeletion(List<Entity> order, List<(Entity Source, Entity Target, int TypeId)> incoming)
        {
            int singleCount = 0, firstType = -1;
            bool groupSources = false;
            foreach (var edge in incoming)
            {
                var type = ComponentTypeManager.GetComponentType(ComponentTypeManager.GetTypeByComponentType(edge.TypeId));
                if (type.IsMultiRelation) continue;
                singleCount++;
                if (firstType < 0) firstType = type.Id;
                else if (firstType != type.Id) groupSources = true;
            }
            var unlinks = new List<DeletionUnlink>(singleCount);
            var sourcePlans = groupSources ? new Dictionary<Entity, int>() : null;
            var multi = new List<(Entity Source, Entity Target, int TypeId)>();
            Dictionary<(Entity Source, int TypeId), HashSet<Entity>> bulkLists = null;
            var notifications = new List<RelationNotification>();
            foreach (var edge in incoming)
            {
                var type = ComponentTypeManager.GetComponentType(ComponentTypeManager.GetTypeByComponentType(edge.TypeId));
                if (type.IsMultiRelation)
                {
                    multi.Add(edge);
                    if (order.Count > 1 && type.MultiRelationMaxSlots == 0
                        && _relationListStore.GetCount(type.Id, edge.Source) > 32)
                    {
                        bulkLists ??= new();
                        var key = (edge.Source, type.Id);
                        if (!bulkLists.TryGetValue(key, out var targets)) bulkLists.Add(key, targets = new());
                        targets.Add(edge.Target);
                    }
                    var removed = CaptureMultiNotification(edge.Source, type, ObserverEvents.Removed, RelationSlot.From(edge.Target));
                    if (removed.Value != null) notifications.Add(removed);
                    continue;
                }
                int position;
                if (sourcePlans == null || !sourcePlans.TryGetValue(edge.Source, out position))
                {
                    position = unlinks.Count;
                    unlinks.Add(new DeletionUnlink { Source = edge.Source });
                    sourcePlans?.Add(edge.Source, position);
                }
                ref var plan = ref CollectionsMarshal.AsSpan(unlinks)[position];
                plan.AddEdge((type, RelationSlot.From(edge.Target)));
                var notification = CaptureRelationNotification(edge.Source, type, ObserverEvents.Removed, GetRelationValue(edge.Source, type));
                if (notification.Value != null) notifications.Add(notification);
            }
            var destinations = new Dictionary<(Archetype, nint), DeletionDestination>();
            var reservedChunks = new HashSet<nint>();
            var layouts = new Dictionary<Archetype, List<(int[] Removed, Archetype Target)>>();
            var affected = new HashSet<Archetype>();
            for (int planIndex = 0; planIndex < unlinks.Count; planIndex++)
            {
                ref var plan = ref CollectionsMarshal.AsSpan(unlinks)[planIndex];
                var info = GetEntityInfoRef(plan.Source.Id);
                var source = info.Archetype;
                if (!layouts.TryGetValue(source, out var choices)) layouts[source] = choices = new();
                Archetype target = null;
                foreach (var choice in choices)
                {
                    if (choice.Removed.Length != plan.EdgeCount) continue;
                    bool same = true;
                    for (int i = 0; i < plan.EdgeCount; i++) if (Array.IndexOf(choice.Removed, plan.EdgeAt(i).Type.Id) < 0) { same = false; break; }
                    if (same) { target = choice.Target; break; }
                }
                if (target == null)
                {
                    var removed = new int[plan.EdgeCount];
                    for (int i = 0; i < removed.Length; i++) removed[i] = plan.EdgeAt(i).Type.Id;
                    target = plan.EdgeCount == 1 ? GetComponentDestination(source, plan.EdgeAt(0).Type, false)
                        : GetOrCreateArchetype(source.Types.ToArray().Where(type => Array.IndexOf(removed, type.Id) < 0).ToArray());
                    choices.Add((removed, target));
                }
                var types = target.Types;
                var sourceChunk = source.ChunkList[info.ChunkIndex];
                nint shared = HasSharedColumns(target) ? sourceChunk.MemoryBlock : 0;
                var key = (target, shared);
                if (!destinations.TryGetValue(key, out var slot) || slot.Reserved >= target.ChunkList[slot.Index].Capacity)
                {
                    int index = target.ChunkList.FindIndex(chunk => chunk.EntityCount == 0 && !reservedChunks.Contains(chunk.MemoryBlock));
                    if (index < 0) index = target.CreateChunk();
                    var chunk = target.ChunkList[index];
                    reservedChunks.Add(chunk.MemoryBlock);
                    foreach (var type in types)
                        if (type.IsShared)
                        {
                            int from = source.GetComponentTypeIndex(type), to = target.GetComponentTypeIndex(type);
                            Buffer.MemoryCopy((void*)sourceChunk.GetSharedValuePointer(from), (void*)chunk.GetSharedValuePointer(to), type.Size, type.Size);
                        }
                    destinations[key] = slot = new DeletionDestination { Archetype = target, Index = index, Memory = chunk.MemoryBlock };
                }
                slot.Reserved++;
                plan.Destination = slot;
                affected.Add(source); affected.Add(target);
            }
            var destroyed = new List<RelationNotification>[order.Count];
            for (int i = 0; i < order.Count; i++)
            {
                destroyed[i] = CaptureDestroyedNotifications(order[i]);
                var info = GetEntityInfoRef(order[i].Id);
                affected.Add(info.Archetype);
                _relationListStore.PrepareSourceRemoval(order[i], _multiRelRemovedBuffer);
            }
            foreach (var arch in affected) { arch.PrepareEntityRemoval(arch.ChunkCount); arch.GetChunkOffsetsNative(); }
            EnsureRecycleCapacity(checked(_recycleCount + order.Count));
            int total = notifications.Count;
            foreach (var list in destroyed) total = checked(total + (list?.Count ?? 0));
            if (total > 0)
            {
                _relationNotifications ??= new();
                _relationNotifications.EnsureCapacity(checked(_relationNotifications.Count + total));
            }
            CheckRelationshipPreparation("delete-storage-prepared");
            foreach (var plan in unlinks)
            {
                var location = GetEntityInfoRef(plan.Source.Id);
                var source = location.Archetype;
                var oldChunk = source.ChunkList[location.ChunkIndex];
                var destination = plan.Destination;
                if (destination.Index >= destination.Archetype.ChunkCount
                    || destination.Archetype.ChunkList[destination.Index].MemoryBlock != destination.Memory)
                    destination.Index = destination.Archetype.ChunkList.FindIndex(chunk => chunk.MemoryBlock == destination.Memory);
                int slot = destination.Archetype.ChunkList[destination.Index].EntityCount;
                destination.Archetype.AddEntityToChunk(plan.Source, destination.Index);
                ref var chunk = ref CollectionsMarshal.AsSpan(destination.Archetype.ChunkList)[destination.Index];
                for (int c = 0; c < destination.Archetype.ComponentCount; c++)
                {
                    var type = destination.Archetype.Types[c];
                    if (type.IsShared) continue;
                    int from = source.GetComponentTypeIndex(type);
                    ComponentTypeManager.MoveComponentValue(type,
                        (byte*)oldChunk.GetComponentArrayPointer(from) + location.SlotInChunk * type.Size,
                        (byte*)chunk.GetComponentArrayPointer(c) + slot * type.Size);
                    if (type.IsEnableable) chunk.SetComponentEnabled(c, slot, oldChunk.GetComponentEnabled(from, location.SlotInChunk));
                }
                RemoveAndFixup(source, location.ChunkIndex, location.SlotInChunk, skipDisposal: true);
                UpdateEntityLocation(plan.Source.Id, destination.Archetype, destination.Index, slot);
                for (int i = 0; i < plan.EdgeCount; i++)
                {
                    var edge = plan.EdgeAt(i);
                    _relationIndex.RemoveRelTypeId(edge.Type.Id, plan.Source, edge.Target);
                }
                structuralVersion++;
                _relationshipVersion += plan.EdgeCount;
            }
            foreach (var edge in multi)
                if (bulkLists == null || !bulkLists.ContainsKey((edge.Source, edge.TypeId)))
                    UnlinkDeletedTarget(edge.Source, edge.Target, edge.TypeId);
            if (bulkLists != null)
                foreach (var entry in bulkLists)
                {
                    int removed = _relationListStore.RemoveMany(entry.Key.TypeId, entry.Key.Source, entry.Value);
                    foreach (var target in entry.Value)
                        _relationIndex.RemoveRelTypeId(entry.Key.TypeId, entry.Key.Source, RelationSlot.From(target));
                    ref var location = ref GetEntityInfoRef(entry.Key.Source.Id);
                    location.Archetype.NotifyComponentChanged(location.ChunkIndex, location.SlotInChunk);
                    _relationshipVersion += removed;
                }
            foreach (var notification in notifications) CommitRelationNotification(notification);
            for (int i = 0; i < order.Count; i++) DestroyEntityCore(order[i], destroyed[i], notificationsPrepared: true);
            return order.Count;
        }
    }
}
