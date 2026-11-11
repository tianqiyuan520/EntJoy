using System;
using System.IO;
using System.Text.Json;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        /// <summary>在宿主线程等待已登记作业后导出调试快照。</summary>
        public byte[] ExportDebugJson()
        {
            CheckDisposed();
            CompleteActiveJobs();
            using (EnterStructural())
            {
                using var stream = new MemoryStream();
                using (var json = new Utf8JsonWriter(stream))
                {
                    json.WriteStartObject();
                    json.WriteNumber("entityCount", LiveEntityCount);
                    json.WriteStartArray("entities");
                    for (int a = 0; a < archetypeCount; a++)
                    {
                        var arch = allArchetypes[a];
                        if (arch == null) continue;
                        foreach (var chunk in arch.ChunkList)
                            for (int slot = 0; slot < chunk.EntityCount; slot++)
                                WriteEntityJson(json, arch, chunk, slot);
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                return stream.ToArray();
            }
        }

        private static void WriteEntityJson(Utf8JsonWriter json, Archetype arch, Chunk chunk, int slot)
        {
            var entity = chunk.GetEntity(slot);
            json.WriteStartObject();
            json.WriteNumber("id", entity.Id);
            json.WriteNumber("version", entity.Version);
            json.WriteStartArray("components");
            for (int c = 0; c < arch.ComponentCount; c++)
                WriteComponentJson(json, arch.Types[c], chunk, c, slot);
            json.WriteEndArray();
            json.WriteEndObject();
        }

        private static void WriteComponentJson(Utf8JsonWriter json, ComponentType type, Chunk chunk, int column, int slot)
        {
            var meta = ComponentMetaRegistry.Get(type.Id);
            json.WriteStartObject();
            json.WriteString("type", type.Type.FullName);
            bool supported = !type.IsManagedShared && !type.IsRelation && meta.Fields != null;
            json.WriteBoolean("fieldsAvailable", supported);
            if (supported)
            {
                byte* ptr = (byte*)chunk.GetComponentArrayPointer(column) + slot * type.Size;
                json.WriteStartObject("fields");
                foreach (var field in meta.Fields)
                {
                    if (field.Offset < 0 || field.Size < 0 || field.Offset > type.Size - field.Size)
                        throw new InvalidOperationException("Component field metadata exceeds its storage.");
                    json.WritePropertyName(field.Name);
                    WriteDebugField(json, ptr + field.Offset, field.Kind);
                }
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }

        private static void WriteDebugField(Utf8JsonWriter json, byte* p, FieldKind kind)
        {
            switch (kind)
            {
                case FieldKind.Int8: json.WriteNumberValue(*(sbyte*)p); break;
                case FieldKind.Int16: json.WriteNumberValue(*(short*)p); break;
                case FieldKind.Int32: json.WriteNumberValue(*(int*)p); break;
                case FieldKind.Int64: json.WriteNumberValue(*(long*)p); break;
                case FieldKind.UInt8: json.WriteNumberValue(*p); break;
                case FieldKind.UInt16: json.WriteNumberValue(*(ushort*)p); break;
                case FieldKind.UInt32: json.WriteNumberValue(*(uint*)p); break;
                case FieldKind.UInt64: json.WriteNumberValue(*(ulong*)p); break;
                case FieldKind.Float32:
                    float f = *(float*)p;
                    if (float.IsFinite(f)) json.WriteNumberValue(f); else json.WriteStringValue(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case FieldKind.Float64:
                    double d = *(double*)p;
                    if (double.IsFinite(d)) json.WriteNumberValue(d); else json.WriteStringValue(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case FieldKind.Bool: json.WriteBooleanValue(*(bool*)p); break;
                case FieldKind.Char: json.WriteStringValue((*(char*)p).ToString()); break;
                case FieldKind.Decimal: json.WriteNumberValue(*(decimal*)p); break;
                default: throw new InvalidOperationException("Unknown component field kind.");
            }
        }
    }
}
