using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        private int _relationChangeDepth;
        private List<RelationNotification> _relationNotifications;
        private long _relationshipVersion;
        public long RelationshipVersion => _relationshipVersion;

        public void ValidateQueryReadState(int structuralVersion, long relationshipVersion)
        {
            CheckDisposed();
            if (structuralVersion != StructuralVersion)
                throw new InvalidOperationException("Structural change invalidated the query or read-only view.");
            if (relationshipVersion >= 0 && relationshipVersion != _relationshipVersion)
                throw new InvalidOperationException("Relationship change invalidated the query or read-only view.");
        }

        private readonly struct RelationNotification
        {
            public readonly Entity Entity;
            public readonly int TypeId;
            public readonly ObserverEvents Event;
            public readonly byte[] Value;
            public readonly Entity[] Batch;

            public RelationNotification(Entity entity, int typeId, ObserverEvents kind, byte[] value, Entity[] batch = null)
            {
                Entity = entity;
                TypeId = typeId;
                Event = kind;
                Value = value;
                Batch = batch;
            }
        }

        private readonly struct RelationChangeScope : IDisposable
        {
            private readonly EntityManager _manager;
            public RelationChangeScope(EntityManager manager)
            {
                _manager = manager;
                manager._relationChangeDepth++;
            }
            public void Dispose() => _manager?.EndRelationChanges();
        }

        private RelationChangeScope BeginRelationChanges() => new(this);

        private void CompleteRelationWriteJobs(Entity source, ComponentType type)
        {
            if (!type.IsMultiRelation && !type.IsExclusiveRelation && IsAlive(source)
                && GetEntityInfoRef(source.Id).Archetype.Has(type.Type))
                CompleteEntityJobs(source);
            else
                CompleteActiveJobs();
        }

        private RelationNotification CaptureRelationNotification(Entity entity, ComponentType type,
            ObserverEvents kind, void* value)
        {
            if (_observerCount == 0 || _observers == null || kind == ObserverEvents.None
                || !_observers.TryGetValue(type.Id, out var registry)) return default;
            var observers = kind switch
            {
                ObserverEvents.Added => registry.Added,
                ObserverEvents.Set => registry.Set,
                ObserverEvents.Removed => registry.Removed,
                _ => registry.Destroyed
            };
            if (observers.Count == 0) return default;
            _relationNotifications ??= new List<RelationNotification>();
            _relationNotifications.EnsureCapacity(_relationNotifications.Count + 2);
            var bytes = new byte[type.Size];
            Marshal.Copy((IntPtr)value, bytes, 0, bytes.Length);
            return new RelationNotification(entity, type.Id, kind, bytes);
        }

        private void CommitRelationNotification(in RelationNotification notification)
        {
            if (notification.Value != null) _relationNotifications.Add(notification);
        }

        private void EndRelationChanges()
        {
            if (--_relationChangeDepth != 0 || _relationNotifications == null) return;
            var notifications = _relationNotifications;
            _relationNotifications = null;
            try
            {
                foreach (var notification in notifications)
                {
                    if (_observers == null || !_observers.TryGetValue(notification.TypeId, out var registry)) continue;
                    var observers = notification.Event switch
                    {
                        ObserverEvents.Added => registry.Added,
                        ObserverEvents.Set => registry.Set,
                        ObserverEvents.Removed => registry.Removed,
                        _ => registry.Destroyed
                    };
                    Entity entity = notification.Entity;
                    fixed (Entity* batch = notification.Batch)
                    fixed (byte* value = notification.Value)
                    {
                        Entity* items = batch != null ? batch : &entity;
                        int count = notification.Batch?.Length ?? 1;
                        if (notification.Event == ObserverEvents.Added || notification.Event == ObserverEvents.Set)
                            DispatchAdded(observers, items, value, count);
                        else
                            DispatchRemoved(observers, items, value, count);
                    }
                }
            }
            finally
            {
                notifications.Clear();
                _relationNotifications ??= notifications;
            }
        }

        private byte* GetRelationValue(Entity source, ComponentType type)
        {
            ref var info = ref GetEntityInfoRef(source.Id);
            int index = info.Archetype.GetComponentTypeIndex(type);
            return (byte*)info.Archetype.ChunkList[info.ChunkIndex].GetComponentArrayPointer(index)
                + info.SlotInChunk * type.Size;
        }

        private void ValidateRelationshipEntity(Entity entity, ComponentType type, string role)
        {
            if (!IsAlive(entity))
                throw new InvalidOperationException($"Relation '{type.Type}' {role}=({entity.Id}, v{entity.Version}) is invalid, destroyed or stale.");
        }

        private void ValidateRelationTarget(Entity source, ComponentType type, in RelationSlot target)
        {
            if (!target.IsValid)
            {
                if (!target.Matches(RelationSlot.Default))
                    throw new InvalidOperationException($"Relation '{type.Type}' requires a live target or RelationSlot.Default.");
                return;
            }
            ValidateRelationshipEntity(target.ToEntity(), type, "target");
            if (!type.IsAcyclicRelation) return;
            _relVisited.Clear();
            Entity current = target.ToEntity();
            while (IsAlive(current))
            {
                if (current.Id == source.Id || !_relVisited.Add(current.Id))
                    throw new InvalidOperationException($"Relation '{type.Type}' from ({source.Id}, v{source.Version}) to {target} would form a cycle.");
                if (!GetEntityInfoRef(current.Id).Archetype.Has(type.Type)) break;
                var next = *(RelationSlot*)GetRelationValue(current, type);
                if (!next.IsValid) break;
                current = next.ToEntity();
            }
        }

        // A prepared chunk may move within the list during swap-pop; its native address stays stable.
        private static bool HasSharedColumns(Archetype archetype)
        {
            foreach (var type in archetype.Types) if (type.IsShared) return true;
            return false;
        }

        private nint PrepareRelationSlot(Archetype target, Entity source, nint excluded = 0)
        {
            var location = GetEntityInfoRef(source.Id);
            var sourceArch = location.Archetype;
            if (HasSharedColumns(target))
            {
                var sourceChunk = sourceArch.ChunkList[location.ChunkIndex];
                var set = new bool[target.ComponentCount];
                var indices = new int[target.ComponentCount];
                var values = new object[target.ComponentCount];
                for (int c = 0; c < target.ComponentCount; c++)
                {
                    var type = target.Types[c];
                    if (!type.IsShared || !sourceArch.Has(type.Type)) continue;
                    set[c] = true;
                    int from = sourceArch.GetComponentTypeIndex(type);
                    if (type.IsManagedShared) indices[c] = sourceChunk.GetSharedValueIndex(from);
                    else values[c] = ReadBlittableSharedBoxed(sourceChunk, from, type.Type);
                }
                int index = FindExistingChunkForShared(target, set, indices, values, excluded: excluded);
                if (index < 0)
                {
                    index = target.CreateChunk();
                    var chunk = target.ChunkList[index];
                    for (int c = 0; c < target.ComponentCount; c++)
                        if (set[c])
                        {
                            int from = sourceArch.GetComponentTypeIndex(target.Types[c]);
                            Buffer.MemoryCopy((void*)sourceChunk.GetSharedValuePointer(from), (void*)chunk.GetSharedValuePointer(c), target.Types[c].Size, target.Types[c].Size);
                        }
                }
                return target.ChunkList[index].MemoryBlock;
            }
            var chunks = target.ChunkList;
            for (int i = chunks.Count - 1; i >= 0; i--)
                if (chunks[i].MemoryBlock != excluded && chunks[i].EntityCount < chunks[i].Capacity)
                    return chunks[i].MemoryBlock;
            chunks.EnsureCapacity(chunks.Count + 1);
            return chunks[target.CreateChunk()].MemoryBlock;
        }

        private void AddToPreparedChunk(Archetype target, Entity entity, nint prepared,
            out int chunkIndex, out int slot)
        {
            if (prepared == 0 && HasSharedColumns(target)) prepared = PrepareRelationSlot(target, entity);
            if (prepared == 0) { target.AddEntity(entity, out chunkIndex, out slot); return; }
            var chunks = target.ChunkList;
            for (int offset = 0; offset < chunks.Count; offset++)
            {
                int i = chunks.Count - 1 - offset;
                if (chunks[i].MemoryBlock != prepared) continue;
                chunkIndex = i;
                slot = chunks[i].EntityCount;
                target.AddEntityToChunk(entity, i);
                return;
            }
            throw new InvalidOperationException("Prepared relationship storage is no longer available.");
        }

        private void SetSingleRelationCore(Entity source, ComponentType type, byte* value, int size)
        {
            ValidateRelationshipEntity(source, type, "source");
            if (type.IsMultiRelation)
                throw new InvalidOperationException($"Use the multi-relationship API for '{type.Type}'.");
            if (value == null || size != type.Size)
                throw new ArgumentException($"Relation '{type.Type}' requires its complete {type.Size}-byte value.");
            var target = *(RelationSlot*)value;
            ValidateRelationTarget(source, type, target);

            ref var sourceInfo = ref GetEntityInfoRef(source.Id);
            var sourceArch = sourceInfo.Archetype;
            bool hadColumn = sourceArch.Has(type.Type);
            var old = hadColumn ? *(RelationSlot*)GetRelationValue(source, type) : RelationSlot.Default;
            bool hadEdge = old.IsValid && IsAlive(old.ToEntity());
            Entity previousOwner = default;
            bool hasPreviousOwner = false;
            if (target.IsValid && type.IsExclusiveRelation
                && _relationIndex.TryGetSources(target.ToEntity(), out var byType)
                && byType.TryGetValue(type.Id, out var sources))
            {
                foreach (var candidate in sources)
                {
                    if (candidate.Id == source.Id || !StillPointsToRelation(candidate, target.ToEntity(), type.Id)) continue;
                    if (hasPreviousOwner) throw new InvalidOperationException("Exclusive relationship index contains multiple owners.");
                    previousOwner = candidate;
                    hasPreviousOwner = true;
                }
            }

            var eventKind = hadEdge ? (target.IsValid ? ObserverEvents.Set : ObserverEvents.Removed)
                : (target.IsValid ? ObserverEvents.Added : ObserverEvents.None);
            var notification = CaptureRelationNotification(source, type, eventKind,
                eventKind == ObserverEvents.Removed ? GetRelationValue(source, type) : value);
            var ownerNotification = hasPreviousOwner
                ? CaptureRelationNotification(previousOwner, type, ObserverEvents.Removed, GetRelationValue(previousOwner, type))
                : default;
            nint sourceSlot = hadColumn ? 0 : PrepareRelationSlot(GetComponentDestination(sourceArch, type, true), source);
            if (!hadColumn) sourceArch.PrepareEntityRemoval();
            nint ownerSlot = 0;
            if (hasPreviousOwner)
            {
                var ownerArch = GetEntityInfoRef(previousOwner.Id).Archetype;
                ownerArch.PrepareEntityRemoval();
                var ownerDestination = GetComponentDestination(ownerArch, type, false);
                nint excluded = !hadColumn && ownerDestination == sourceArch
                    ? sourceArch.ChunkList[sourceInfo.ChunkIndex].MemoryBlock : 0;
                ownerSlot = PrepareRelationSlot(ownerDestination, previousOwner, excluded);
            }
            HashSet<Entity> newSources;
            try
            {
                newSources = target.IsValid ? _relationIndex.PrepareAdd(type.Id, target.ToEntity()) : null;
                CheckRelationshipPreparation("relation-prepared");
            }
            catch
            {
                if (target.IsValid) _relationIndex.DiscardEmpty(type.Id, target.ToEntity());
                throw;
            }

            AddComponentRawCore(source, type.Type, null, value, size, relationStorageOnly: true, preparedChunk: sourceSlot);
            if (hasPreviousOwner)
                RemoveComponentRawCore(previousOwner, type.Type, relationStorageOnly: true, preparedChunk: ownerSlot);
            if (!old.Matches(target)) _relationIndex.RemoveRelTypeId(type.Id, source, old);
            newSources?.Add(source);
            if (hasPreviousOwner) _relationIndex.RemoveRelTypeId(type.Id, previousOwner, target);
            ref var updated = ref GetEntityInfoRef(source.Id);
            updated.Archetype.NotifyComponentChanged(updated.ChunkIndex, updated.SlotInChunk);
            _relationshipVersion++;
            CommitRelationNotification(ownerNotification);
            CommitRelationNotification(notification);
        }

        private void RemoveRelationCore(Entity source, ComponentType type)
        {
            if (!IsAlive(source)) return;
            var arch = GetEntityInfoRef(source.Id).Archetype;
            if (!arch.Has(type.Type)) return;
            byte* value = GetRelationValue(source, type);
            var old = *(RelationSlot*)value;
            var notification = !type.IsMultiRelation && old.IsValid && IsAlive(old.ToEntity())
                ? CaptureRelationNotification(source, type, ObserverEvents.Removed, value) : default;
            int count = Math.Max(1, type.MultiRelationMaxSlots);
            Span<RelationSlot> slots = count <= 64 ? stackalloc RelationSlot[count] : new RelationSlot[count];
            for (int i = 0; i < count; i++) slots[i] = ((RelationSlot*)value)[i];
            nint prepared = PrepareRelationSlot(GetComponentDestination(arch, type, false), source);
            arch.PrepareEntityRemoval();
            CheckRelationshipPreparation("unlink-prepared");
            RemoveComponentRawCore(source, type.Type, relationStorageOnly: true, preparedChunk: prepared);
            foreach (var target in slots) _relationIndex.RemoveRelTypeId(type.Id, source, target);
            _relationshipVersion++;
            CommitRelationNotification(notification);
        }
    }
}
