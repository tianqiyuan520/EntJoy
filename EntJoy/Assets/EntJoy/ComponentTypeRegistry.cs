using System;
using System.Collections.Generic;

namespace EntJoy
{
    public class ComponentTypeRegistry
    {
        private static Dictionary<Type, ComponentType> typeToComType = new();
        private static Dictionary<int, Type> idToType = new();
        private static int idForAllocate;
        
        public static ComponentType Get(Type type)
        {
            if (typeToComType.TryGetValue(type, out var result))
            {
                return result;
            }

            var newRes = new ComponentType(idForAllocate++);
            typeToComType.Add(type, newRes);
            idToType.Add(idForAllocate, type);
            return newRes;
        }

        public static Type GetType(ComponentType componentType)
        {
            return idToType[componentType.Id];
        }
    }
}