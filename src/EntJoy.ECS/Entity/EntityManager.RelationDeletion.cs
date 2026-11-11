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
            var order = CollectDeletionClosure(roots, forceCascade, visited);
            var toUnlink = CollectIncomingEdges(order, visited);
            CheckRelationshipPreparation("delete-closure-prepared");
            return CommitPreparedDeletion(order, toUnlink);
        }

        // 后序收集待删集合：先推 Exit 标记，再推它的入边 source，出栈顺序即"子先父后"。
        private List<Entity> CollectDeletionClosure(ReadOnlySpan<Entity> roots, bool forceCascade, HashSet<int> visited)
        {
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
            return order;
        }

        // 待删集合之外的、仍指向它们的入边要在删之前收集：撤边过程会让 source 在块间迁移。
        private List<(Entity Source, Entity Target, int TypeId)> CollectIncomingEdges(List<Entity> order, HashSet<int> visited)
        {
            int capacity = 0;
            if (order.Count == 1 && _relationIndex.TryGetSources(order[0], out var incomingTypes))
                foreach (var sources in incomingTypes.Values) capacity = checked(capacity + sources.Count);

            var toUnlink = new List<(Entity Source, Entity Target, int TypeId)>(capacity);
            foreach (var target in order)
            {
                if (!_relationIndex.TryGetSources(target, out var byType)) continue;
                foreach (var edges in byType)
                    foreach (var source in edges.Value)
                        if (!visited.Contains(source.Id) && StillPointsToRelation(source, target, edges.Key))
                            toUnlink.Add((source, target, edges.Key));
            }
            return toUnlink;
        }

        private void UnlinkDeletedTarget(Entity source, Entity target, int typeId)
        {
            var type = ComponentTypeManager.GetComponentType(typeId);
            if (!type.IsMultiRelation) { RemoveRelationCore(source, type); return; }
            CommitRemoveMultiEdge(source, type, RelationSlot.From(target));
        }
    }
}
