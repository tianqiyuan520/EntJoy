using System;
using System.Collections.Generic;

namespace EntJoy
{
    public class ComponentTypeRegistry
    {
        private static Dictionary<Type, ComponentType> comTypeMap = new Dictionary<Type, ComponentType>();
        private static int idForAllocate;
        
        public static ComponentType Get(Type type)
        {
            if (comTypeMap.TryGetValue(type, out var result))
            {
                return result;
            }

            var newRes = new ComponentType(idForAllocate++);
            comTypeMap.Add(type, newRes);
            return newRes;
        }
    }
}