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
                    var type = ComponentTypeManager.GetComponentType(entry.TypeId);
                    foreach (var target in entry.Targets)
                    {
                        var edge = CaptureMultiNotification(entity, type, ObserverEvents.Destroyed, target);
                        if (edge.Value != null) (notifications ??= new()).Add(edge);
                    }
                }
            return notifications;
        }


        private sealed class DeletionPlan
        {
            public List<DeletionUnlink> Unlinks = new();
            public List<(Entity Source, Entity Target, int TypeId)> Multi = new();
            public Dictionary<(Entity Source, int TypeId), HashSet<Entity>> BulkLists;
            public List<RelationNotification> Notifications = new();
        }

        // 把入边分成「单值关系要搬去新原型」与「多值关系只需解绑」两类，并预先取好观察者通知。
        private DeletionPlan BuildDeletionPlan(List<Entity> order, List<(Entity Source, Entity Target, int TypeId)> incoming)
        {
            var plan = new DeletionPlan();
            int singleCount = CountSingleRelationEdges(incoming, out bool groupSources);
            var unlinks = new List<DeletionUnlink>(singleCount);
            var sourcePlans = groupSources ? new Dictionary<Entity, int>() : null;
            foreach (var edge in incoming)
            {
                var type = ComponentTypeManager.GetComponentType(edge.TypeId);
                if (type.IsMultiRelation)
                {
                    PlanMultiUnlink(plan, order, edge, type);
                    continue;
                }
                PlanSingleUnlink(plan, unlinks, sourcePlans, edge, type);
            }
            plan.Unlinks = unlinks;
            return plan;
        }

        // 单值边数决定 unlink 列表的初始容量；多个关系类型混在同一批时才需要按 source 去重。
        private static int CountSingleRelationEdges(List<(Entity Source, Entity Target, int TypeId)> incoming, out bool groupSources)
        {
            int singleCount = 0, firstType = -1;
            groupSources = false;
            foreach (var edge in incoming)
            {
                var type = ComponentTypeManager.GetComponentType(edge.TypeId);
                if (type.IsMultiRelation) continue;
                singleCount++;
                if (firstType < 0) firstType = type.Id;
                else if (firstType != type.Id) groupSources = true;
            }
            return singleCount;
        }

        // 多值边只需解绑；托管多值关系的 source 边数超过阈值时改用整批摘除。
        private void PlanMultiUnlink(DeletionPlan plan, List<Entity> order, (Entity Source, Entity Target, int TypeId) edge, ComponentType type)
        {
            plan.Multi.Add(edge);
            if (order.Count > 1 && type.MultiRelationMaxSlots == 0
                && _relationListStore.GetCount(type.Id, edge.Source) > 32)
            {
                plan.BulkLists ??= new();
                var key = (edge.Source, type.Id);
                if (!plan.BulkLists.TryGetValue(key, out var targets)) plan.BulkLists.Add(key, targets = new());
                targets.Add(edge.Target);
            }
            var removed = CaptureMultiNotification(edge.Source, type, ObserverEvents.Removed, RelationSlot.From(edge.Target));
            if (removed.Value != null) plan.Notifications.Add(removed);
        }

        // 单值边要按 source 归组：同一 source 的全部出边要一起搬去新原型。
        private void PlanSingleUnlink(DeletionPlan plan, List<DeletionUnlink> unlinks, Dictionary<Entity, int> sourcePlans,
            (Entity Source, Entity Target, int TypeId) edge, ComponentType type)
        {
            int position;
            if (sourcePlans == null || !sourcePlans.TryGetValue(edge.Source, out position))
            {
                position = unlinks.Count;
                unlinks.Add(new DeletionUnlink { Source = edge.Source });
                sourcePlans?.Add(edge.Source, position);
            }
            ref var unlink = ref CollectionsMarshal.AsSpan(unlinks)[position];
            unlink.AddEdge((type, RelationSlot.From(edge.Target)));
            var notification = CaptureRelationNotification(edge.Source, type, ObserverEvents.Removed, GetRelationValue(edge.Source, type));
            if (notification.Value != null) plan.Notifications.Add(notification);
        }

        // 为每个解绑计划选定目标原型与目标块：优先复用空块，shared 列从源块拷入。
        private Dictionary<(Archetype, nint), DeletionDestination> AssignDeletionDestinations(
            List<DeletionUnlink> unlinks, out HashSet<Archetype> affected)
        {
            affected = new HashSet<Archetype>();
            var destinations = new Dictionary<(Archetype, nint), DeletionDestination>();
            var reservedChunks = new HashSet<nint>();
            var layouts = new Dictionary<Archetype, List<(int[] Removed, Archetype Target)>>();
            for (int planIndex = 0; planIndex < unlinks.Count; planIndex++)
            {
                ref var unlink = ref CollectionsMarshal.AsSpan(unlinks)[planIndex];
                var info = GetEntityInfoRef(unlink.Source.Id);
                var source = info.Archetype;
                if (!layouts.TryGetValue(source, out var choices)) layouts[source] = choices = new();
                Archetype target = null;
                foreach (var choice in choices)
                {
                    if (choice.Removed.Length != unlink.EdgeCount) continue;
                    bool same = true;
                    for (int i = 0; i < unlink.EdgeCount; i++) if (Array.IndexOf(choice.Removed, unlink.EdgeAt(i).Type.Id) < 0) { same = false; break; }
                    if (same) { target = choice.Target; break; }
                }
                if (target == null)
                {
                    var removed = new int[unlink.EdgeCount];
                    for (int i = 0; i < removed.Length; i++) removed[i] = unlink.EdgeAt(i).Type.Id;
                    target = unlink.EdgeCount == 1 ? GetComponentDestination(source, unlink.EdgeAt(0).Type, false)
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
                unlink.Destination = slot;
                affected.Add(source); affected.Add(target);
            }
            return destinations;
        }

        private int CommitPreparedDeletion(List<Entity> order, List<(Entity Source, Entity Target, int TypeId)> incoming)
        {
            var deletion = BuildDeletionPlan(order, incoming);
            var destinations = AssignDeletionDestinations(deletion.Unlinks, out var affected);
            var destroyed = PrepareDestroyedNotifications(order, affected);
            ReserveDeletionNotifications(deletion, destroyed);
            CheckRelationshipPreparation("delete-storage-prepared");

            foreach (var unlink in deletion.Unlinks) MoveUnlinkedSource(unlink);
            RemoveSurvivingEdges(deletion);
            foreach (var notification in deletion.Notifications) CommitRelationNotification(notification);
            for (int i = 0; i < order.Count; i++) DestroyEntityCore(order[i], destroyed[i], notificationsPrepared: true);
            return order.Count;
        }

        // 待删实体的销毁通知先收集，并让它们的原型准备好本轮批量移除。
        private List<RelationNotification>[] PrepareDestroyedNotifications(List<Entity> order, HashSet<Archetype> affected)
        {
            var destroyed = new List<RelationNotification>[order.Count];
            for (int i = 0; i < order.Count; i++)
            {
                destroyed[i] = CaptureDestroyedNotifications(order[i]);
                var info = GetEntityInfoRef(order[i].Id);
                affected.Add(info.Archetype);
                _relationListStore.PrepareSourceRemoval(order[i], _multiRelRemovedBuffer);
            }
            foreach (var arch in affected) { arch.PrepareEntityRemoval(arch.ChunkCount); arch.GetChunkOffsetsNative(); }
            GrowRecycleStack(checked(_recycleCount + order.Count));
            return destroyed;
        }

        private void ReserveDeletionNotifications(DeletionPlan deletion, List<RelationNotification>[] destroyed)
        {
            int total = deletion.Notifications.Count;
            foreach (var list in destroyed) total = checked(total + (list?.Count ?? 0));
            if (total == 0) return;
            _relationNotifications ??= new();
            _relationNotifications.EnsureCapacity(checked(_relationNotifications.Count + total));
        }

        // 集合外的 source 要搬到它的关系列所在原型，再消掉指向待删实体的边。
        private void MoveUnlinkedSource(DeletionUnlink unlink)
        {
            var location = GetEntityInfoRef(unlink.Source.Id);
            var source = location.Archetype;
            var oldChunk = source.ChunkList[location.ChunkIndex];
            var destination = unlink.Destination;
            if (destination.Index >= destination.Archetype.ChunkCount
                || destination.Archetype.ChunkList[destination.Index].MemoryBlock != destination.Memory)
                destination.Index = destination.Archetype.ChunkList.FindIndex(chunk => chunk.MemoryBlock == destination.Memory);
            int slot = destination.Archetype.ChunkList[destination.Index].EntityCount;
            destination.Archetype.AddEntityToChunk(unlink.Source, destination.Index);
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
            UpdateEntityLocation(unlink.Source.Id, destination.Archetype, destination.Index, slot);
            for (int i = 0; i < unlink.EdgeCount; i++)
            {
                var edge = unlink.EdgeAt(i);
                _relationIndex.RemoveRelTypeId(edge.Type.Id, unlink.Source, edge.Target);
            }
            InvalidateStructuralQueries();
            InvalidateRelationshipQueries(unlink.EdgeCount);
        }

        // 存活实体的多值关系边：没进批量列表的逐条解绑，进了的整批摘除。
        private void RemoveSurvivingEdges(DeletionPlan deletion)
        {
            foreach (var edge in deletion.Multi)
                if (deletion.BulkLists == null || !deletion.BulkLists.ContainsKey((edge.Source, edge.TypeId)))
                    UnlinkDeletedTarget(edge.Source, edge.Target, edge.TypeId);
            if (deletion.BulkLists == null) return;
            foreach (var entry in deletion.BulkLists)
            {
                int removed = _relationListStore.RemoveMany(entry.Key.TypeId, entry.Key.Source, entry.Value);
                foreach (var target in entry.Value)
                    _relationIndex.RemoveRelTypeId(entry.Key.TypeId, entry.Key.Source, RelationSlot.From(target));
                ref var location = ref GetEntityInfoRef(entry.Key.Source.Id);
                location.Archetype.NotifyComponentChanged(location.ChunkIndex, location.SlotInChunk);
                InvalidateRelationshipQueries(removed);
            }
        }
    }
}
