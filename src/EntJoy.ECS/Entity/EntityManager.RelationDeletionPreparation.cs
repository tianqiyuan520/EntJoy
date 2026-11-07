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

        private sealed class DeletionUnlink
        {
            public Entity Source;
            public List<(ComponentType Type, RelationSlot Target)> Edges = new();
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
                if (type.IsRelation && !((RelationSlot*)value)->IsValid) continue;
                var notification = CaptureRelationNotification(entity, type, ObserverEvents.Destroyed, value);
                if (notification.Value != null) (notifications ??= new()).Add(notification);
            }
            return notifications;
        }

        private int CommitPreparedDeletion(List<Entity> order, List<(Entity Source, Entity Target, int TypeId)> incoming)
        {
            var unlinks = new Dictionary<Entity, DeletionUnlink>();
            var multi = new List<(Entity Source, Entity Target, int TypeId)>();
            var notifications = new List<RelationNotification>();
            foreach (var edge in incoming)
            {
                var type = ComponentTypeManager.GetComponentType(ComponentTypeManager.GetTypeByComponentType(edge.TypeId));
                if (type.IsMultiRelation) { multi.Add(edge); continue; }
                if (!unlinks.TryGetValue(edge.Source, out var plan)) unlinks[edge.Source] = plan = new DeletionUnlink { Source = edge.Source };
                plan.Edges.Add((type, RelationSlot.From(edge.Target)));
                var notification = CaptureRelationNotification(edge.Source, type, ObserverEvents.Removed, GetRelationValue(edge.Source, type));
                if (notification.Value != null) notifications.Add(notification);
            }
            var destinations = new Dictionary<(Archetype, nint), DeletionDestination>();
            var reservedChunks = new HashSet<nint>();
            var layouts = new Dictionary<Archetype, List<(int[] Removed, Archetype Target)>>();
            var affected = new HashSet<Archetype>();
            foreach (var plan in unlinks.Values)
            {
                var info = GetEntityInfoRef(plan.Source.Id);
                var source = info.Archetype;
                if (!layouts.TryGetValue(source, out var choices)) layouts[source] = choices = new();
                Archetype target = null;
                foreach (var choice in choices)
                {
                    if (choice.Removed.Length != plan.Edges.Count) continue;
                    bool same = true;
                    foreach (var edge in plan.Edges) if (Array.IndexOf(choice.Removed, edge.Type.Id) < 0) { same = false; break; }
                    if (same) { target = choice.Target; break; }
                }
                if (target == null)
                {
                    var removed = plan.Edges.Select(edge => edge.Type.Id).ToArray();
                    target = plan.Edges.Count == 1 ? GetComponentDestination(source, plan.Edges[0].Type, false)
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
            foreach (var plan in unlinks.Values)
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
                foreach (var edge in plan.Edges) _relationIndex.RemoveRelTypeId(edge.Type.Id, plan.Source, edge.Target);
                structuralVersion++;
                _relationshipVersion += plan.Edges.Count;
            }
            foreach (var edge in multi) UnlinkDeletedTarget(edge.Source, edge.Target, edge.TypeId);
            foreach (var notification in notifications) CommitRelationNotification(notification);
            for (int i = 0; i < order.Count; i++) DestroyEntityCore(order[i], destroyed[i], notificationsPrepared: true);
            return order.Count;
        }
    }
}
