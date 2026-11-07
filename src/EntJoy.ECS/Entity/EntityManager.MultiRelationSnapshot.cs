using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        private readonly record struct ManagedSnapshotEdges(ComponentType Type, Entity Source, RelationSlot[] Targets);

        private void WriteManagedRelations(BinaryWriter writer)
        {
            var groups = _relationListStore.Entries.GroupBy(entry => entry.TypeId).ToArray();
            writer.Write(groups.Length);
            foreach (var group in groups)
            {
                var type = ComponentTypeManager.GetComponentType(ComponentTypeManager.GetTypeByComponentType(group.Key));
                writer.Write(type.Id); writer.Write(type.Size);
                writer.Write(type.Type.AssemblyQualifiedName);
                writer.Write(type.Type.Module.ModuleVersionId.ToByteArray());
                writer.Write(group.Count());
                foreach (var entry in group)
                {
                    writer.Write(entry.Source.Id); writer.Write(entry.Source.Version); writer.Write(entry.Targets.Count);
                    foreach (var target in entry.Targets) { writer.Write(target.TargetId); writer.Write(target.TargetVersion); }
                }
            }
        }

        private static List<ManagedSnapshotEdges> ReadManagedRelations(BinaryReader reader)
        {
            int types = ReadSnapshotCount(reader, sizeof(int) * 3);
            var result = new List<ManagedSnapshotEdges>();
            var seenTypes = new HashSet<int>();
            for (int t = 0; t < types; t++)
            {
                int id = reader.ReadInt32(), size = reader.ReadInt32();
                if (!seenTypes.Add(id) || !ComponentTypeManager.TryGetTypeById(id, out var runtimeType))
                    throw new InvalidDataException("Unknown or duplicate managed relationship type.");
                var type = ComponentTypeManager.GetComponentType(runtimeType);
                if (!type.IsMultiRelation || type.MultiRelationMaxSlots != 0 || size != type.Size
                    || reader.ReadString() != runtimeType.AssemblyQualifiedName)
                    throw new InvalidDataException("Managed relationship snapshot layout is incompatible.");
                var module = reader.ReadBytes(16);
                if (module.Length != 16 || new Guid(module) != runtimeType.Module.ModuleVersionId)
                    throw new InvalidDataException("Managed relationship snapshot assembly is incompatible.");
                int sources = ReadSnapshotCount(reader, sizeof(int) * 3);
                var seenSources = new HashSet<Entity>();
                for (int s = 0; s < sources; s++)
                {
                    var source = new Entity { Id = reader.ReadInt32(), Version = reader.ReadInt32() };
                    int count = ReadSnapshotCount(reader, sizeof(int) * 2);
                    if (!seenSources.Add(source) || count == 0) throw new InvalidDataException("Duplicate or empty managed relationship source.");
                    var targets = new RelationSlot[count];
                    for (int i = 0; i < count; i++) targets[i] = new RelationSlot { TargetId = reader.ReadInt32(), TargetVersion = reader.ReadInt32() };
                    result.Add(new ManagedSnapshotEdges(type, source, targets));
                }
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Unexpected trailing snapshot data.");
            return result;
        }

        private static int ReadSnapshotCount(BinaryReader reader, int minimumBytes)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / minimumBytes)
                throw new InvalidDataException("Invalid relationship snapshot count.");
            return count;
        }

        private void LoadManagedRelations(List<ManagedSnapshotEdges> entries)
        {
            foreach (var entry in entries)
            {
                if (!IsAlive(entry.Source)) throw new InvalidDataException("Managed relationship source is missing or stale.");
                var list = _relationListStore.PrepareAdd(entry.Type.Id, entry.Source, entry.Targets.Length);
                var seen = new HashSet<Entity>();
                foreach (var target in entry.Targets)
                {
                    if (!target.IsValid || !IsAlive(target.ToEntity()) || !seen.Add(target.ToEntity()))
                        throw new InvalidDataException("Invalid or duplicate managed relationship target.");
                    list.Add(target);
                }
            }
        }
    }
}
