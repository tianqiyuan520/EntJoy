using System;
using System.Buffers;
using System.Collections.Generic;

namespace EntJoy
{
    /// <summary>
    /// 世界实体的定位和更新
    /// </summary>
    public partial class World
    {
        public string Name { get; private set; }
        private Queue<Entity> recycleEntities;
        private EntityIndexInWorld[] entities;
        private int entityCount;

        public World(string worldName)
        {
            Name = worldName;
            recycleEntities = new Queue<Entity>();
            entities = ArrayPool<EntityIndexInWorld>.Shared.Rent(32);
        }

        public ref EntityIndexInWorld GetEntityInfoRef(int index) {
            return ref entities[index];
        }
        
        public Entity NewEntity(Span<ComponentType> types)
        {
            var newEntity = new Entity();
            if (recycleEntities.TryDequeue(out var ent))
            {
                newEntity.Id = ent.Id;
                newEntity.Version = ent.Version + 1;
            }
            else
            {
                newEntity.Id = entityCount;
            }
            entityCount++;
            var targetArch = GetOrCreateArchetype(types);
            targetArch.AddEntity(newEntity, out var index);
            entities[ent.Id] = new EntityIndexInWorld() {
                Archetype = targetArch,
                Entity = newEntity,
                SlotInArchetype = index,
            };
            
            return newEntity;
        }

        public void KillEntity(Entity entity)
        {
            recycleEntities.Enqueue(entity);
        }
    }
}