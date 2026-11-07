using System;
using System.Collections.Generic;
using System.IO;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        private const string SnapshotMagic = "EJSNAP03";

        private static void ValidateSnapshotComponent(ComponentType type)
        {
            if (type.Size <= 0 || type.Size != ComponentTypeManager.GetComponentType(type.Type).Size)
                throw new InvalidDataException($"Snapshot layout is incompatible with {type.Type}.");
            // These states need ownership/side-table serialization, not raw bytes.
            if (type.IsShared || type.IsEnableable
                || type.IsDisposable || type.IsCopyable)
                throw new NotSupportedException($"Snapshots cannot serialize shared, enabled or owned-resource state for {type.Type}.");
        }

        private static ComponentType[][] ReadSnapshotSignatures(BinaryReader reader)
        {
            if (new string(reader.ReadChars(SnapshotMagic.Length)) != SnapshotMagic)
                throw new InvalidDataException("Snapshot format is incompatible.");
            if (reader.ReadInt32() != IntPtr.Size)
                throw new InvalidDataException("Snapshot pointer width is incompatible.");
            int count = reader.ReadInt32();
            if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / sizeof(int))
                throw new InvalidDataException("Invalid snapshot archetype count.");
            var signatures = new ComponentType[count][];
            for (int i = 0; i < count; i++)
            {
                int components = reader.ReadInt32();
                if (components < 0 || components > (reader.BaseStream.Length - reader.BaseStream.Position) / (2 * sizeof(int)))
                    throw new InvalidDataException("Invalid snapshot component count.");
                var types = new ComponentType[components];
                var seen = new HashSet<int>();
                for (int c = 0; c < components; c++)
                {
                    int id = reader.ReadInt32(), size = reader.ReadInt32();
                    if (!seen.Add(id) || !ComponentTypeManager.TryGetTypeById(id, out var runtimeType))
                        throw new InvalidDataException("Unknown or duplicate snapshot component.");
                    var type = ComponentTypeManager.GetComponentType(runtimeType);
                    if (size != type.Size || reader.ReadString() != runtimeType.AssemblyQualifiedName)
                        throw new InvalidDataException($"Snapshot layout is incompatible with {runtimeType}.");
                    byte[] module = reader.ReadBytes(16);
                    if (module.Length != 16 || new Guid(module) != runtimeType.Module.ModuleVersionId)
                        throw new InvalidDataException($"Snapshot assembly is incompatible with {runtimeType}.");
                    ValidateSnapshotComponent(type);
                    types[c] = type;
                }
                signatures[i] = types;
            }
            return signatures;
        }

        private static void ValidateSnapshotPayload(BinaryReader reader, ComponentType[][] signatures)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / (3 * sizeof(int)))
                throw new InvalidDataException("Invalid snapshot entity count.");
            var seen = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                int id = reader.ReadInt32();
                reader.ReadInt32(); // Entity versions use the full int range.
                int archetype = reader.ReadInt32();
                if (id < 0 || id >= Array.MaxLength || !seen.Add(id)
                    || (uint)archetype >= (uint)signatures.Length)
                    throw new InvalidDataException("Invalid snapshot entity identity.");
                long bytes = 0;
                foreach (var type in signatures[archetype]) bytes += type.Size;
                if (bytes > reader.BaseStream.Length - reader.BaseStream.Position)
                    throw new InvalidDataException("Truncated snapshot component data.");
                reader.BaseStream.Position += bytes;
            }
        }
    }
}
