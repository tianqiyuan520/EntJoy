using System;
using System.Collections.Generic;

namespace EntJoy
{
    public partial class World
    {
        private readonly Dictionary<int, Archetype> archetypeMap;
        private Archetype[] allArchetypes;
        private int archetypeCnt;
        
        private Archetype GetOrCreateArchetype(Span<ComponentType> types)
        {
            var hash = Utils.CalculateHash(types);
            if (archetypeMap.TryGetValue(hash, out Archetype archetype))
            {
                return archetype;
            }

            archetype = new Archetype(types.ToArray());
            archetypeMap.Add(hash, archetype);
            // Todo: 如果有移除archetype的操作,空白的数组需要被填充
            allArchetypes[archetypeCnt] = archetype;
            archetypeCnt++;
            return archetype;
        }
    }
}