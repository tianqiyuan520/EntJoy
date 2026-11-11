using System;
using System.Collections.Generic;
using System.IO;
using EntJoy.ECS.JobSystem;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        // 校验一条 source 行的全部关系槽：空槽必须规范、目标必须存活、同类型内不得重复、
        // 独占关系不得有第二个持有者；有环约束的关系同时记下边供后续检测。
        private void ValidateSnapshotRow(ComponentType type, Entity source, RelationSlot* targets, RelationIndex index,
            Dictionary<int, Dictionary<Entity, Entity>> acyclic)
        {
            int count = Math.Max(1, type.MultiRelationMaxSlots);
            for (int i = 0; i < count; i++)
            {
                var target = targets[i];
                if (!target.IsValid)
                {
                    if (!target.Matches(RelationSlot.Default))
                        throw new InvalidDataException("Snapshot contains a noncanonical empty relationship.");
                    continue;
                }
                if (type.IsMultiRelation && type.MultiRelationMaxSlots == 0)
                    throw new InvalidDataException("A managed relationship column must remain empty; its edges belong to the side table.");
                for (int prior = 0; prior < i; prior++)
                    if (targets[prior].Matches(target))
                        throw new InvalidDataException("Snapshot contains duplicate relationship targets.");
                if (!IsAlive(target.ToEntity()))
                    throw new InvalidDataException("Snapshot relationship target is missing or stale.");
                var sources = index.PrepareAdd(type.Id, target.ToEntity());
                if ((type.IsExclusiveRelation || type.IsExclusiveTarget) && sources.Count != 0)
                    throw new InvalidDataException("Snapshot contains conflicting exclusive relationships.");
                sources.Add(source);
                if (!type.IsAcyclicRelation) continue;
                if (!acyclic.TryGetValue(type.Id, out var edges)) acyclic[type.Id] = edges = new();
                edges.Add(source, target.ToEntity());
            }
        }

        private RelationIndex ValidateSnapshotRelationships()
        {
            var index = new RelationIndex();
            var acyclic = new Dictionary<int, Dictionary<Entity, Entity>>();
            for (int a = 0; a < archetypeCount; a++)
            {
                var arch = allArchetypes[a];
                foreach (var chunk in arch.ChunkList)
                    for (int slot = 0; slot < chunk.EntityCount; slot++)
                    {
                        var source = chunk.GetEntity(slot);
                        for (int c = 0; c < arch.ComponentCount; c++)
                        {
                            var type = arch.Types[c];
                            if (!type.IsRelation) continue;
                            ValidateSnapshotComponent(type);
                            var targets = (RelationSlot*)((byte*)chunk.GetComponentArrayPointer(c) + slot * type.Size);
                            ValidateSnapshotRow(type, source, targets, index, acyclic);
                        }
                    }
            }
            foreach (var entry in _relationListStore.Entries)
            {
                var type = ComponentTypeManager.GetComponentType(entry.TypeId);
                if (!IsAlive(entry.Source)) throw new InvalidDataException("Managed relationship source is missing or stale.");
                var seen = new HashSet<Entity>();
                foreach (var target in entry.Targets)
                {
                    if (!target.IsValid || !IsAlive(target.ToEntity()) || !seen.Add(target.ToEntity()))
                        throw new InvalidDataException("Managed relationship target is missing, stale or duplicated.");
                    var sources = index.PrepareAdd(type.Id, target.ToEntity());
                    if (type.IsExclusiveTarget && sources.Count != 0) throw new InvalidDataException("Managed relationship has conflicting exclusive owners.");
                    sources.Add(entry.Source);
                }
            }
            foreach (var edges in acyclic.Values)
                if (HasRelationCycle(edges)) throw new InvalidDataException("Snapshot contains a cycle in an acyclic relationship.");
            return index;
        }

        private static bool HasRelationCycle(Dictionary<Entity, Entity> edges)
        {
            var complete = new HashSet<Entity>();
            var path = new HashSet<Entity>();
            foreach (var source in edges.Keys)
            {
                if (complete.Contains(source)) continue;
                path.Clear();
                var current = source;
                while (!complete.Contains(current) && edges.TryGetValue(current, out var target))
                {
                    if (!path.Add(current)) return true;
                    current = target;
                }
                complete.UnionWith(path);
            }
            return false;
        }

        private void RestorePreparedSnapshot(WorldSnapshot snapshot)
        {
            CheckDisposed();
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Data == null) throw new InvalidDataException("Snapshot data is missing.");
            using var stream = new MemoryStream(snapshot.Data, writable: false);
            using var reader = new BinaryReader(stream);
            var (signatures, entityData, managed) = ReadSnapshotHeader(stream, reader);

            CompleteActiveJobs();
            // 先装进临时管理器并自检，通过后才把存储整体换过来；失败时旧数据仍在。
            using var prepared = new EntityManager();
            stream.Position = entityData;
            LoadSnapshotRows(stream, reader, prepared, signatures);
            prepared.LoadManagedRelations(managed);
            prepared._relationIndex = prepared.ValidateSnapshotRelationships();
            CheckRelationshipPreparation("restore-prepared");

            using (EnterStructural())
            {
                ChunkJobScheduler.ClearRawChunkScheduleCaches(this);
                SwapStorageWith(prepared);
                InvalidateStructuralQueries();
                archetypeSetVersion++;
                InvalidateRelationshipQueries();
            }
            DisposeReplacedStorage(prepared);
        }

        // 签名表、实体数据起点、托管关系边都要在动存储之前读完并校验。
        private (ComponentType[][] Signatures, long EntityData, List<ManagedSnapshotEdges> Managed) ReadSnapshotHeader(
            Stream stream, BinaryReader reader)
        {
            try
            {
                var signatures = ReadSnapshotSignatures(reader);
                long entityData = stream.Position;
                ValidateSnapshotPayload(reader, signatures);
                return (signatures, entityData, ReadManagedRelations(reader));
            }
            catch (Exception error) when (error is EndOfStreamException || error is FormatException)
            {
                throw new InvalidDataException("Snapshot is malformed or truncated.", error);
            }
        }

        private void LoadSnapshotRows(Stream stream, BinaryReader reader, EntityManager prepared, ComponentType[][] signatures)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var entity = new Entity { Id = reader.ReadInt32(), Version = reader.ReadInt32() };
                var types = signatures[reader.ReadInt32()];
                var arch = prepared.GetOrCreateArchetype(types);
                if (entity.Id >= prepared.entities.Length)
                    prepared.ResizeEntityTable(Math.Max(checked(prepared.entities.Length * 2), checked(entity.Id + 1)));
                arch.AddEntity(entity, out int chunk, out int slot);
                prepared.UpdateEntityLocation(entity.Id, arch, chunk, slot);
                prepared.GetEntityInfoRef(entity.Id).Version = entity.Version;
                prepared.SetLocateBVersion(entity.Id, entity.Version);
                prepared.entityCount = Math.Max(prepared.entityCount, entity.Id + 1);
                foreach (var type in types)
                {
                    int column = arch.GetComponentTypeIndex(type);
                    byte* value = (byte*)arch.ChunkList[chunk].GetComponentArrayPointer(column) + slot * type.Size;
                    stream.ReadExactly(new Span<byte>(value, type.Size));
                }
                CheckRelationshipPreparation("restore-row-loaded");
            }
        }

        // 两边存储逐项互换，prepared 随后持有旧数据并负责释放。
        private void SwapStorageWith(EntityManager prepared)
        {
            (archetypeMap, prepared.archetypeMap) = (prepared.archetypeMap, archetypeMap);
            (allArchetypes, prepared.allArchetypes) = (prepared.allArchetypes, allArchetypes);
            (archetypeCount, prepared.archetypeCount) = (prepared.archetypeCount, archetypeCount);
            (_nextArchetypeId, prepared._nextArchetypeId) = (prepared._nextArchetypeId, _nextArchetypeId);
            (entities, prepared.entities) = (prepared.entities, entities);
            (entityCount, prepared.entityCount) = (prepared.entityCount, entityCount);
            var oldLocate = _locateB; _locateB = prepared._locateB; prepared._locateB = oldLocate;
            (_locateBCapacity, prepared._locateBCapacity) = (prepared._locateBCapacity, _locateBCapacity);
            var oldRecycle = _recycleStack; _recycleStack = prepared._recycleStack; prepared._recycleStack = oldRecycle;
            (_recycleCount, prepared._recycleCount) = (prepared._recycleCount, _recycleCount);
            (_recycleCapacity, prepared._recycleCapacity) = (prepared._recycleCapacity, _recycleCapacity);
            (_relationIndex, prepared._relationIndex) = (prepared._relationIndex, _relationIndex);
            (_relationListStore, prepared._relationListStore) = (prepared._relationListStore, _relationListStore);
            (_managedLookup, prepared._managedLookup) = (prepared._managedLookup, _managedLookup);
            (_managedSharedValues, prepared._managedSharedValues) = (prepared._managedSharedValues, _managedSharedValues);
            (_managedSharedValueCount, prepared._managedSharedValueCount) = (prepared._managedSharedValueCount, _managedSharedValueCount);
            (_lastChunkPerSharedValue, prepared._lastChunkPerSharedValue) = (prepared._lastChunkPerSharedValue, _lastChunkPerSharedValue);
            for (int i = 0; i < archetypeCount; i++) allArchetypes[i].Owner = this;
            for (int i = 0; i < prepared.archetypeCount; i++) prepared.allArchetypes[i].Owner = prepared;
        }

        // 旧存储的实体已随互换搬到 prepared，逐个释放；单个失败不阻断其余。
        private static void DisposeReplacedStorage(EntityManager prepared)
        {
            for (int i = 0; i < prepared.archetypeCount; i++)
            {
                try { prepared.allArchetypes[i].Dispose(); }
                catch (Exception error) { try { Console.Error.WriteLine($"Snapshot replaced storage disposal failed: {error.Message}"); } catch { } }
            }
        }
    }
}
