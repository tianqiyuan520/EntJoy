using System;

namespace EntJoy
{
    public partial class World
    {
        public void AddComponent<T0>(Entity entity, T0 t0) where T0 : struct
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            var oldArch = entityInfoRef.Archetype;
            if (oldArch.Has(typeof(T0)))
            {
                oldArch.Set(entityInfoRef.SlotInArchetype, t0);
                return;
            }
            
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
            targetArch.Set(index, t0);
        }

        public void RemoveComponent<T0>(Entity entity) where T0 : struct
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            var oldArch = entityInfoRef.Archetype;
            if (!oldArch.Has(typeof(T0)))
            {
                return;
            }
            
            Span<ComponentType> targetComponents = stackalloc ComponentType[oldArch.ComponentCount - 1];
            int spanIndex = 0;
            for (int i = 0; i < oldArch.Types.Length; i++)
            {
                var comType = oldArch.Types[i];
                if (comType.Type == typeof(T0))
                {
                    continue;
                }

                targetComponents[spanIndex++] = comType;
            }

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

        public void Set<T>(Entity entity, T t) where T : struct
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            var arch = entityInfoRef.Archetype;
            arch.Set(entityInfoRef.SlotInArchetype, t);
        }
    }
}