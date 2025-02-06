using System;
using System.Collections.Generic;

namespace EntJoy
{
    public unsafe partial class EntityComponentStorage
    {
        private Dictionary<uint, Archetype> allArchetypes = new();
        
        public Entity CreateEntity(params ComponentType[] componentTypes)
        {
            return new Entity();
        }
        
        public Archetype GetOrCreateArchetype(params ComponentType[] componentTypes)
        {
            fixed (ComponentType* ptr = componentTypes)
            {
                return GetOrCreateArchetype(ptr, componentTypes.Length);
            }
        }

        private Archetype GetOrCreateArchetype(ComponentType* componentTypes, int length)
        {
            var hash = UnsafeTool.CalculateHash(componentTypes, length);
            if (allArchetypes.TryGetValue(hash, out var archetype))
            {
                return archetype;
            }

            return new Archetype();
        }
    }
}