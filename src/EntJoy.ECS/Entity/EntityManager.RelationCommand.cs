using System;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        internal void SetRelationshipTargetRaw(Entity source, Entity target, ComponentType type)
        {
            CheckDisposed();
            if (!type.IsRelation || type.IsMultiRelation) throw new ArgumentException("A target-set command requires a single-target relationship.");
            CompleteRelationWriteJobs(source, type);
            using var changes = BeginRelationChanges();
            using (EnterStructural())
            {
                ValidateRelationshipEntity(source, type, "source");
                ValidateRelationshipEntity(target, type, "target");
                byte* previous = GetEntityInfoRef(source.Id).Archetype.Has(type.Type) ? GetRelationValue(source, type) : null;
                if (previous != null && ((RelationSlot*)previous)->Matches(target)) return;
                byte* small = stackalloc byte[type.Size <= 1024 ? type.Size : 0];
                byte[] large = type.Size > 1024 ? new byte[type.Size] : null;
                fixed (byte* largePtr = large)
                {
                    byte* value = largePtr != null ? largePtr : small;
                    if (previous == null) new Span<byte>(value, type.Size).Clear();
                    else Buffer.MemoryCopy(previous, value, type.Size, type.Size);
                    *(RelationSlot*)value = RelationSlot.From(target);
                    SetSingleRelationCore(source, type, value, type.Size);
                }
            }
        }

        internal void RemoveRelationshipRaw(Entity source, ComponentType type)
        {
            CheckDisposed();
            if (!type.IsRelation || type.IsMultiRelation) throw new ArgumentException("An unlink command requires a single-target relationship.");
            CompleteActiveJobs();
            using var changes = BeginRelationChanges();
            using (EnterStructural()) RemoveRelationCore(source, type);
        }
    }
}
