using System;
using System.Collections.Generic;
using Unity.Collections;

namespace EntJoy
{
    public unsafe partial class World
    {
        private Dictionary<uint, Archetype> allArchetypes = new();
        private NativeArray<Entity> entities;
        private NativeArray<ArchetypeLocate> entityInfos;
        private int idAllocator;
        private int entityCount;

        public World()
        {
            entities = new NativeArray<Entity>(32, Allocator.Persistent);
            entityInfos = new NativeArray<ArchetypeLocate>(32, Allocator.Persistent);
        }

        ~World()
        {
            entities.Dispose();
            entityInfos.Dispose();
        }
        
        public Entity CreateEntity()
        {
            var newEntity = new Entity
            {
                Id = idAllocator++
            };

            entities[newEntity.Id] = newEntity;
            entityCount++;
            return newEntity;
        }

        public void Add<T>(Entity entity, T t) where T : struct, IComponent
        {
            int id = entity.Id;
            var sourceLocate = entityInfos[id];
            var sourceArch = sourceLocate.Archetype;
            Span<ComponentType> newComponents = stackalloc ComponentType[sourceArch.Types.Length + 1];
            newComponents[0] = typeof(T);
            for (int i = 1; i < sourceArch.Types.Length; i++)
            {
                newComponents[i] = sourceArch.Types[i];
            }

            var destArch = GetOrCreateArchetype(newComponents);
            sourceArch.MoveComponentsByAdd(ref sourceLocate, destArch);
            destArch.Set(sourceLocate.ComponentInArray, t);
        }

        public void Remove<T>(Entity entity) where T : struct, IComponent
        {
            
        }
        
        public Archetype GetOrCreateArchetype(Span<ComponentType> types)
        {
            fixed (ComponentType* ptr = types)
            {
                return GetOrCreateArchetype(ptr, types.Length);
            }
        }

        private Archetype GetOrCreateArchetype(ComponentType* componentTypes, int length)
        {
            var hash = UnsafeTool.CalculateHash(componentTypes, length);
            if (allArchetypes.TryGetValue(hash, out var archetype))
            {
                return archetype;
            }
            
            ComponentType[] types = new ComponentType[length];
            for (int i = 0; i < length; i++)
            {
                types[i] = componentTypes[i];
            }

            var newArchetype = new Archetype(types);
            return newArchetype;
        }
    }
}