using System;

namespace EntJoy
{
    public partial class World
    {
        public void AddComponent<T0>(Entity entity, T0 t0)
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            var oldArch = entityInfoRef.Archetype;
            Span<ComponentType> targetComponents = stackalloc ComponentType[oldArch.ComponentCount + 1];
            oldArch.Types.CopyTo(targetComponents);
            targetComponents[^1] = ComponentTypeRegistry.GetComponentType(typeof(T0));
            var targetArch = GetOrCreateArchetype(targetComponents);
            targetArch.AddEntity(entity, out var index);
            oldArch.CopyComponentsTo(entityInfoRef.SlotInArchetype, targetArch, index);
            oldArch.Remove(entityInfoRef.SlotInArchetype, out var bePushEntityId, out var bePushEntityNewIndexInArchetype);
            if (bePushEntityId >= 0)
            {
                ref var bePushEntityInfoRef = ref GetEntityInfoRef(bePushEntityId);
                bePushEntityInfoRef.SlotInArchetype = bePushEntityNewIndexInArchetype;
            }
            entityInfoRef.SlotInArchetype = index;
        }
    }
}