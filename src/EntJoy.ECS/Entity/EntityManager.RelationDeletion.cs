using System;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        private int DestroyRelationGraph(ReadOnlySpan<Entity> roots, bool forceCascade = false)
        {
            if (_relationIndex.IsEmpty)
            {
                int count = 0;
                foreach (var entity in roots)
                    if (IsAlive(entity)) { DestroyEntityCore(entity); count++; }
                return count;
            }

            var visited = new HashSet<int>();
            var order = new List<Entity>();
            var stack = new Stack<(Entity Entity, bool Exit)>();
            foreach (var root in roots)
            {
                stack.Push((root, false));
                while (stack.Count > 0)
                {
                    var entry = stack.Pop();
                    if (entry.Exit) { order.Add(entry.Entity); continue; }
                    if (!IsAlive(entry.Entity) || !visited.Add(entry.Entity.Id)) continue;
                    stack.Push((entry.Entity, true));
                    if (!_relationIndex.TryGetSources(entry.Entity, out var byType)) continue;
                    foreach (var edges in byType)
                    {
                        if (!forceCascade && !ComponentTypeManager.GetCascadeOnTargetDeleted(edges.Key)) continue;
                        foreach (var source in edges.Value)
                            if (StillPointsToRelation(source, entry.Entity, edges.Key)) stack.Push((source, false));
                    }
                }
            }

            // Collect before modifying the index; sources may move between chunks as edges are removed.
            var toUnlink = new List<(Entity Source, Entity Target, int TypeId)>();
            foreach (var target in order)
            {
                if (!_relationIndex.TryGetSources(target, out var byType)) continue;
                foreach (var edges in byType)
                    foreach (var source in edges.Value)
                        if (!visited.Contains(source.Id) && StillPointsToRelation(source, target, edges.Key))
                            toUnlink.Add((source, target, edges.Key));
            }
            CheckRelationshipPreparation("delete-closure-prepared");
            return CommitPreparedDeletion(order, toUnlink);
        }

        private void UnlinkDeletedTarget(Entity source, Entity target, int typeId)
        {
            var type = ComponentTypeManager.GetComponentType(ComponentTypeManager.GetTypeByComponentType(typeId));
            if (!type.IsMultiRelation) { RemoveRelationCore(source, type); return; }
            var slot = RelationSlot.From(target);
            if (type.MultiRelationMaxSlots < 2)
            {
                RemoveListTarget(source, typeId, slot);
                return;
            }
            var values = (RelationSlot*)GetRelationValue(source, type);
            for (int i = 0; i < type.MultiRelationMaxSlots; i++)
                if (values[i].Matches(slot)) values[i] = RelationSlot.Default;
            _relationIndex.RemoveRelTypeId(typeId, source, slot);
            ref var info = ref GetEntityInfoRef(source.Id);
            info.Archetype.NotifyComponentChanged(info.ChunkIndex, info.SlotInChunk);
            _relationshipVersion++;
        }
    }
}
