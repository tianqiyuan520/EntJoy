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
            targetArch.AddEntity(newEntity);
            return newEntity;
        }

        public void KillEntity(Entity entity)
        {
            recycleEntities.Enqueue(entity);
        }
    }
}