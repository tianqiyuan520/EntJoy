using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        private sealed class CloneLayout
        {
            public ComponentType[] Types;
            public int[] Offsets, SharedIndices;
            public bool[] Enabled, Shared;
            public object[] SharedValues;
            public int Width, Alignment = 1;
        }

        private struct CloneSource
        {
            public Entity Entity;
            public CloneLayout Layout;
            public int SnapshotOffset;
            public nint SharedKey;
            public List<(ComponentType Type, RelationSlot[] Targets)> Managed;
            public ComponentType[] Types => Layout.Types;
            public int[] Offsets => Layout.Offsets;
            public int[] SharedIndices => Layout.SharedIndices;
            public bool[] Enabled => Layout.Enabled;
            public bool[] Shared => Layout.Shared;
            public object[] SharedValues => Layout.SharedValues;
            public int Width => Layout.Width;
            public int Alignment => Layout.Alignment;
        }

        private struct CloneItem
        {
            public int Source, Offset, ChunkIndex, Slot, CopiedColumns;
            public Entity Entity;
            public Archetype Archetype;
            public nint ChunkMemory;
            public EntityIndexInWorld OldInfo;
            public EntityLocateB OldLocate;
            public bool Added;
        }

        private readonly struct CloneEdge
        {
            public readonly int TypeId;
            public readonly Entity Source, Target;
            public readonly bool Managed;
            public CloneEdge(int type, Entity source, Entity target, bool managed = false) { TypeId = type; Source = source; Target = target; Managed = managed; }
        }

        /// <summary>按输入顺序复制实体，去除 Prefab 标记，重映射集合内部的关系 Target。</summary>
        public Entity[] CloneEntities(ReadOnlySpan<Entity> sources)
        {
            CheckDisposed();
            if (sources.IsEmpty) return Array.Empty<Entity>();
            CompleteActiveJobs();
            using var changes = BeginRelationChanges();
            lock (_structuralLock) return CloneEntitiesCore(sources, 1, requirePrefab: false);
        }

        private static int AlignCloneOffset(int value, int alignment) => checked(value + alignment - 1) & -alignment;

        private Entity[] CloneEntitiesCore(ReadOnlySpan<Entity> sources, int copies, bool requirePrefab)
        {
            using var preparation = ComponentTypeManager.BeginCopyPreparation();
            int total = checked(sources.Length * copies);
            var sourceIndices = new Dictionary<Entity, int>(sources.Length);
            var layouts = new CloneSource[sources.Length];
            var acyclicRoots = new Dictionary<ComponentType, List<Entity>>();
            var layoutTemplates = new Dictionary<Archetype, CloneLayout>();
            int bytes = 0, edgeCount = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                ValidateEntity(sources[i]);
                if (!sourceIndices.TryAdd(sources[i], i)) throw new ArgumentException("Clone sources must be unique.", nameof(sources));
            }
            for (int i = 0; i < sources.Length; i++)
            {
                var entity = sources[i];
                var info = GetEntityInfoRef(entity.Id);
                var arch = info.Archetype;
                if (requirePrefab && !arch.Has(typeof(Prefab))) throw new InvalidOperationException($"Entity {entity} is not a Prefab.");
                bool reusable = true;
                foreach (var type in arch.Types) if (type.IsShared || type.IsEnableable) { reusable = false; break; }
                CloneLayout template = null;
                if (reusable) layoutTemplates.TryGetValue(arch, out template);
                var types = template?.Types ?? arch.Types.ToArray().Where(type => type.Type != typeof(Prefab)).ToArray();
                var layout = new CloneSource
                {
                    Entity = entity,
                    Layout = template ?? new CloneLayout
                    {
                        Types = types, Offsets = new int[types.Length], Enabled = new bool[types.Length],
                        Shared = new bool[types.Length], SharedIndices = new int[types.Length], SharedValues = new object[types.Length]
                    }
                };
                var chunk = arch.ChunkList[info.ChunkIndex];
                for (int c = 0; c < types.Length; c++)
                {
                    var type = types[c];
                    ComponentTypeManager.ValidateCopySupport(type);
                    if (template == null)
                    {
                        int alignment = Math.Min(64, type.Size & -type.Size);
                        layout.Layout.Alignment = Math.Max(layout.Alignment, alignment);
                        layout.Offsets[c] = AlignCloneOffset(layout.Width, alignment);
                        layout.Layout.Width = checked(layout.Offsets[c] + type.Size);
                    }
                    int column = arch.GetComponentTypeIndex(type);
                    layout.Enabled[c] = !type.IsEnableable || chunk.GetComponentEnabled(column, info.SlotInChunk);
                    layout.Shared[c] = type.IsShared;
                    if (type.IsShared)
                    {
                        if (type.IsManagedShared) layout.SharedIndices[c] = chunk.GetSharedValueIndex(column);
                        else layout.SharedValues[c] = ReadBlittableSharedBoxed(chunk, column, type.Type);
                    }
                    if (!type.IsRelation) continue;
                    var slots = (RelationSlot*)GetRelationValue(entity, type);
                    for (int s = 0; s < Math.Max(1, type.MultiRelationMaxSlots); s++)
                    {
                        var target = slots[s];
                        if (!target.IsValid)
                        {
                            if (!target.Matches(RelationSlot.Default)) throw new InvalidOperationException("Clone source has a noncanonical empty relationship.");
                            continue;
                        }
                        ValidateRelationshipEntity(target.ToEntity(), type, "target");
                        edgeCount = checked(edgeCount + copies);
                        if (type.IsAcyclicRelation)
                        {
                            if (!acyclicRoots.TryGetValue(type, out var roots)) acyclicRoots[type] = roots = new();
                            roots.Add(entity);
                        }
                        if ((type.IsExclusiveRelation || type.IsExclusiveTarget) && !sourceIndices.ContainsKey(target.ToEntity()))
                            throw new InvalidOperationException("Cloning an occupied external exclusive target would displace its existing owner.");
                    }
                }
                if (template == null) layout.Layout.Width = AlignCloneOffset(layout.Width, layout.Alignment);
                if (_relationListStore.HasRelationshipsForSource(entity))
                    foreach (var entry in _relationListStore.ForSource(entity))
                    {
                        var type = ComponentTypeManager.GetComponentType(ComponentTypeManager.GetTypeByComponentType(entry.TypeId));
                        var targets = entry.Targets.ToArray();
                        edgeCount = checked(edgeCount + checked(targets.Length * copies));
                        foreach (var target in targets)
                        {
                            ValidateRelationshipEntity(target.ToEntity(), type, "target");
                            if (type.IsExclusiveTarget && !sourceIndices.ContainsKey(target.ToEntity()))
                                throw new InvalidOperationException("Cloning an occupied external exclusive target would displace its existing owner.");
                        }
                        (layout.Managed ??= new()).Add((type, targets));
                    }
                if (reusable && template == null) layoutTemplates.Add(arch, layout.Layout);
                layout.SharedKey = HasSharedColumns(arch) ? chunk.MemoryBlock : 0;
                layout.SnapshotOffset = AlignCloneOffset(bytes, layout.Alignment);
                bytes = checked(layout.SnapshotOffset + layout.Width);
                layouts[i] = layout;
            }
            foreach (var entry in acyclicRoots)
            {
                var edgesToCheck = new Dictionary<Entity, Entity>();
                foreach (var root in entry.Value)
                {
                    var current = root;
                    while (!edgesToCheck.ContainsKey(current) && GetEntityInfoRef(current.Id).Archetype.Has(entry.Key.Type))
                    {
                        var target = *(RelationSlot*)GetRelationValue(current, entry.Key);
                        if (!target.IsValid)
                        {
                            if (!target.Matches(RelationSlot.Default)) throw new InvalidOperationException("Clone path contains a noncanonical empty relationship.");
                            break;
                        }
                        ValidateRelationshipEntity(target.ToEntity(), entry.Key, "target");
                        edgesToCheck.Add(current, target.ToEntity());
                        current = target.ToEntity();
                    }
                }
                if (HasRelationCycle(edgesToCheck)) throw new InvalidOperationException("Clone source contains an acyclic relationship cycle.");
            }

            var items = new CloneItem[total];
            var result = new Entity[total];
            int originalCount = entityCount, originalRecycled = _recycleCount;
            int recycled = Math.Min(total, _recycleCount), nextId = entityCount;
            for (int i = 0; i < total; i++)
            {
                var layout = layouts[i % layouts.Length];
                Entity entity;
                if (i < recycled)
                {
                    var previous = _recycleStack[_recycleCount - 1 - i];
                    entity = new Entity { Id = previous.Id, Version = unchecked(previous.Version + 1) };
                }
                else entity = new Entity { Id = nextId++, Version = 0 };
                if ((uint)entity.Id >= Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(copies), "Clone identity capacity exceeded.");
                if (entity.Id < entities.Length && entities[entity.Id].Archetype != null) throw new InvalidOperationException("Entity allocator offered a live identity.");
                int offset = AlignCloneOffset(bytes, layout.Alignment);
                bytes = checked(offset + layout.Width);
                items[i] = new CloneItem
                {
                    Source = i % layouts.Length, Entity = entity, Offset = offset,
                    OldInfo = entity.Id < entities.Length ? entities[entity.Id] : default,
                    OldLocate = entity.Id < _locateBCapacity ? _locateB[entity.Id] : default
                };
                result[i] = entity;
            }

            var edges = new List<CloneEdge>(edgeCount);
            var destinations = new Dictionary<(int Type, Entity Target), HashSet<Entity>>();
            var rollbackChunks = new Dictionary<nint, int>();
            bool committed = false, storageTouched = false;
            nint allocation = Marshal.AllocHGlobal(checked(bytes + 63));
            byte* values = (byte*)(((nuint)allocation + 63) & ~(nuint)63);
            try
            {
                new Span<byte>(values, bytes).Clear();
                foreach (var layout in layouts)
                {
                    var info = GetEntityInfoRef(layout.Entity.Id);
                    var chunk = info.Archetype.ChunkList[info.ChunkIndex];
                    for (int c = 0; c < layout.Types.Length; c++)
                    {
                        var type = layout.Types[c];
                        int column = info.Archetype.GetComponentTypeIndex(type);
                        byte* source = type.IsShared ? (byte*)chunk.GetSharedValuePointer(column)
                            : (byte*)chunk.GetComponentArrayPointer(column) + info.SlotInChunk * type.Size;
                        Buffer.MemoryCopy(source, values + layout.SnapshotOffset + layout.Offsets[c], type.Size, type.Size);
                    }
                }
                for (int i = 0; i < items.Length; i++)
                {
                    ref var item = ref items[i];
                    var layout = layouts[item.Source];
                    for (int c = 0; c < layout.Types.Length; c++)
                    {
                        var type = layout.Types[c];
                        byte* source = values + layout.SnapshotOffset + layout.Offsets[c];
                        byte* target = values + item.Offset + layout.Offsets[c];
                        item.CopiedColumns = c + 1;
                        if (type.IsShared) Buffer.MemoryCopy(source, target, type.Size, type.Size);
                        else ComponentTypeManager.CopyComponentValue(type, source, target);
                        if (!type.IsRelation) continue;
                        for (int slot = 0; slot < Math.Max(1, type.MultiRelationMaxSlots); slot++)
                        {
                            var relation = (RelationSlot*)target + slot;
                            if (!relation->IsValid) continue;
                            var old = relation->ToEntity();
                            if (sourceIndices.TryGetValue(old, out int mapped))
                                *relation = RelationSlot.From(result[i / layouts.Length * layouts.Length + mapped]);
                            edges.Add(new CloneEdge(type.Id, item.Entity, relation->ToEntity()));
                        }
                    }
                    if (layout.Managed != null)
                        foreach (var group in layout.Managed)
                            foreach (var target in group.Targets)
                            {
                                var mapped = sourceIndices.TryGetValue(target.ToEntity(), out int index)
                                    ? result[i / layouts.Length * layouts.Length + index] : target.ToEntity();
                                edges.Add(new CloneEdge(group.Type.Id, item.Entity, mapped, managed: true));
                            }
                }
                var notifications = PrepareCloneNotifications(layouts, items, result, copies, values);
                if (_observerCount > 0)
                    foreach (var edge in edges)
                        if (edge.Managed)
                        {
                            var type = ComponentTypeManager.GetComponentType(ComponentTypeManager.GetTypeByComponentType(edge.TypeId));
                            var notification = CaptureMultiNotification(edge.Source, type, ObserverEvents.Added, RelationSlot.From(edge.Target));
                            if (notification.Value != null) notifications.Add(notification);
                        }
                EnsureRelationNotificationCapacity(notifications.Count);
                CheckRelationshipPreparation("clone-values-prepared");
                storageTouched = true;
                int capacity = Math.Max(nextId, result.Max(entity => checked(entity.Id + 1)));
                ResizeEntityTable(Math.Max(entities.Length, capacity));
                var reservations = new Dictionary<nint, int>();
                var destinationCache = new Dictionary<(Archetype, nint), int>();
                var arches = new HashSet<Archetype>();
                for (int i = 0; i < items.Length; i++)
                {
                    ref var item = ref items[i];
                    var layout = layouts[item.Source];
                    var arch = GetOrCreateArchetype(layout.Types);
                    var key = (arch, layout.SharedKey);
                    int chunkIndex = -1;
                    if (destinationCache.TryGetValue(key, out int cached))
                    {
                        var candidate = arch.ChunkList[cached];
                        reservations.TryGetValue(candidate.MemoryBlock, out int pending);
                        if (candidate.EntityCount + pending < candidate.Capacity) chunkIndex = cached;
                    }
                    if (chunkIndex < 0) chunkIndex = FindExistingChunkForShared(arch, layout.Shared, layout.SharedIndices, layout.SharedValues, reservations);
                    if (chunkIndex < 0)
                    {
                        chunkIndex = arch.CreateChunk();
                        var chunk = arch.ChunkList[chunkIndex];
                        for (int c = 0; c < layout.Types.Length; c++)
                            if (layout.Shared[c])
                            {
                                if (layout.Types[c].IsManagedShared) chunk.SetSharedValueIndex(c, layout.SharedIndices[c]);
                                else WriteBlittableSharedBoxed(chunk, c, layout.SharedValues[c], layout.Types[c].Size);
                            }
                    }
                    nint memory = arch.ChunkList[chunkIndex].MemoryBlock;
                    reservations.TryGetValue(memory, out int reserved);
                    reservations[memory] = reserved + 1;
                    destinationCache[key] = chunkIndex;
                    item.Archetype = arch; item.ChunkIndex = chunkIndex; item.ChunkMemory = memory;
                    arches.Add(arch);
                    if (layout.Managed != null)
                        foreach (var group in layout.Managed)
                            _relationListStore.PrepareAdd(group.Type.Id, item.Entity, group.Targets.Length);
                }
                foreach (var arch in arches)
                {
                    arch.GetChunkOffsetsNative(); arch.PrepareEntityRemoval(arch.ChunkCount);
                    for (int c = 0; c < arch.ChunkCount; c++) rollbackChunks.Add(arch.ChunkList[c].MemoryBlock, c);
                }
                var reverseCounts = new Dictionary<(int TypeId, Entity Target), int>();
                foreach (var edge in edges)
                {
                    var key = (edge.TypeId, edge.Target);
                    reverseCounts.TryGetValue(key, out int count);
                    reverseCounts[key] = checked(count + 1);
                }
                foreach (var entry in reverseCounts)
                {
                    var set = _relationIndex.PrepareAdd(entry.Key.TypeId, entry.Key.Target);
                    set.EnsureCapacity(checked(set.Count + entry.Value));
                    destinations.Add(entry.Key, set);
                }
                CheckRelationshipPreparation("clone-storage-prepared");
                foreach (ref var item in items.AsSpan())
                {
                    var layout = layouts[item.Source];
                    item.Slot = item.Archetype.ChunkList[item.ChunkIndex].EntityCount;
                    item.Archetype.AddEntityToChunk(item.Entity, item.ChunkIndex);
                    item.Added = true;
                    ref var chunk = ref CollectionsMarshal.AsSpan(item.Archetype.ChunkList)[item.ChunkIndex];
                    for (int c = 0; c < layout.Types.Length; c++)
                    {
                        var type = layout.Types[c];
                        if (type.IsShared) continue;
                        byte* destination = (byte*)chunk.GetComponentArrayPointer(c) + item.Slot * type.Size;
                        Buffer.MemoryCopy(values + item.Offset + layout.Offsets[c], destination, type.Size, type.Size);
                        if (type.IsEnableable) chunk.SetComponentEnabled(c, item.Slot, layout.Enabled[c]);
                    }
                    chunk.MarkEntityChanged(item.Slot);
                    CheckRelationshipPreparation("clone-row-added");
                }
                CheckRelationshipPreparation("clone-before-publish");
                foreach (var item in items)
                {
                    UpdateEntityLocation(item.Entity.Id, item.Archetype, item.ChunkIndex, item.Slot);
                    entities[item.Entity.Id].Version = item.Entity.Version;
                    SetLocateBVersion(item.Entity.Id, item.Entity.Version);
                }
                foreach (var edge in edges)
                {
                    destinations[(edge.TypeId, edge.Target)].Add(edge.Source);
                    if (edge.Managed) _relationListStore.CommitAdd(edge.TypeId, edge.Source, RelationSlot.From(edge.Target));
                }
                CheckRelationshipPreparation("clone-indexed");
                _recycleCount -= recycled;
                entityCount = nextId;
                structuralVersion++;
                if (edges.Count > 0) _relationshipVersion++;
                foreach (var notification in notifications) CommitRelationNotification(notification);
                committed = true;
                return result;
            }
            finally
            {
                try
                {
                    if (!committed)
                    {
                        foreach (var edge in edges)
                        {
                            _relationIndex.RemoveRelTypeId(edge.TypeId, edge.Source, RelationSlot.From(edge.Target));
                            if (edge.Managed)
                            {
                                _relationListStore.Remove(edge.TypeId, edge.Source, RelationSlot.From(edge.Target));
                                _relationListStore.DiscardEmpty(edge.TypeId, edge.Source);
                            }
                        }
                        for (int i = items.Length - 1; i >= 0; i--)
                        {
                            ref var item = ref items[i];
                            if (item.Added)
                            {
                                int chunk = rollbackChunks[item.ChunkMemory];
                                int previousCount = item.Archetype.ChunkCount;
                                RemoveAndFixup(item.Archetype, chunk, item.Slot, skipDisposal: true);
                                if (item.Archetype.ChunkCount < previousCount)
                                {
                                    rollbackChunks.Remove(item.ChunkMemory);
                                    if (chunk < item.Archetype.ChunkCount) rollbackChunks[item.Archetype.ChunkList[chunk].MemoryBlock] = chunk;
                                }
                            }
                            if (storageTouched && item.Entity.Id < entities.Length)
                            { entities[item.Entity.Id] = item.OldInfo; _locateB[item.Entity.Id] = item.OldLocate; }
                            var layout = layouts[item.Source];
                            for (int c = 0; c < item.CopiedColumns; c++)
                                if (layout.Types[c].IsDisposable)
                                    try { ComponentTypeManager.DestroyComponentValue(layout.Types[c], values + item.Offset + layout.Offsets[c]); }
                                    catch (Exception error) { try { Console.Error.WriteLine($"Clone preparation disposal failed: {error.Message}"); } catch { } }
                        }
                        entityCount = originalCount; _recycleCount = originalRecycled;
                        if (storageTouched) structuralVersion++;
                    }
                }
                finally { Marshal.FreeHGlobal(allocation); }

            }
        }

        private List<RelationNotification> PrepareCloneNotifications(CloneSource[] layouts, CloneItem[] items, Entity[] entities, int copies, byte* values)
        {
            var notifications = new List<RelationNotification>();
            if (_observerCount == 0 || _observers == null) return notifications;
            for (int s = 0; s < layouts.Length; s++)
            {
                var layout = layouts[s];
                for (int c = 0; c < layout.Types.Length; c++)
                {
                    var type = layout.Types[c];
                    if (type.IsManagedShared || !_observers.TryGetValue(type.Id, out var registry) || registry.Added.Count == 0) continue;
                    if (type.IsMultiRelation)
                    {
                        for (int copy = 0; copy < copies; copy++)
                        {
                            int i = copy * layouts.Length + s;
                            var slots = (RelationSlot*)(values + items[i].Offset + layout.Offsets[c]);
                            for (int slot = 0; slot < type.MultiRelationMaxSlots; slot++)
                                if (slots[slot].IsValid) notifications.Add(CaptureMultiNotification(entities[i], type, ObserverEvents.Added, slots[slot]));
                        }
                        continue;
                    }
                    if (type.IsRelation && !((RelationSlot*)(values + items[s].Offset + layout.Offsets[c]))->IsValid) continue;
                    var batch = new Entity[copies];
                    var data = new byte[checked(copies * type.Size)];
                    fixed (byte* destination = data)
                        for (int copy = 0; copy < copies; copy++)
                        {
                            int i = copy * layouts.Length + s;
                            batch[copy] = entities[i];
                            Buffer.MemoryCopy(values + items[i].Offset + layout.Offsets[c], destination + copy * type.Size, type.Size, type.Size);
                        }
                    notifications.Add(new RelationNotification(batch[0], type.Id, ObserverEvents.Added, data, batch));
                }
            }
            _relationNotifications ??= new List<RelationNotification>();
            _relationNotifications.EnsureCapacity(checked(_relationNotifications.Count + notifications.Count));
            return notifications;
        }
    }
}
