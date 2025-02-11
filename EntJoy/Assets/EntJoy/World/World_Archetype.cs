using System;
using System.Collections.Generic;

namespace EntJoy
{
    public partial class World
    {
        private readonly Dictionary<int, Archetype> allArchetypes = new();
        
        private Archetype GetOrCreateArchetype(Span<ComponentType> types)
        {
            var hash = Utils.CalculateHash(types);
            if (allArchetypes.TryGetValue(hash, out Archetype archetype))
            {
                return archetype;
            }

            archetype = new Archetype(types.ToArray());
            allArchetypes.Add(hash, archetype);
            return archetype;
        }
    }
}