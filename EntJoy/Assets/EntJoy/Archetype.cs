using System;

namespace EntJoy
{
    public class Archetype
    {
        private readonly StructArray[] components;
        
        // 我需要在传入types的时候, 知道对应的组件数组下标是多少
        // componentId -> 数组下标
        private int[] lookUp;

        private ComponentType[] types;
        
        public Archetype(ComponentType[] types)
        {
            this.types = types;
            components = new StructArray[types.Length];
            CreateLookUp(types);
        }
        
        public ReadOnlySpan<ComponentType> Types => types;

        public void MoveComponentsByAdd(ref ArchetypeLocate locate, Archetype dest)
        {
            for (int i = 0; i < components.Length; i++)
            {
                var array = components[i];
                var type = array.GetType();
                if (!dest.Has(type))
                {
                    continue;
                }

                var targetArrayIndex = dest.lookUp[((ComponentType)type).Id];
                array.CopyComponentTo(locate.ComponentInArray.componentIndex, dest.components[targetArrayIndex], 0);
            }
        }

        public bool Has(ComponentType type)
        {
            if (type.Id < components.Length && lookUp[type.Id] >= 0)
            {
                return true;
            }

            return false;
        }

        public void Set<T0>(ComponentInArray locate, T0 t0) where T0 : struct
        {
            var id = ComponentTypeRegistry.Get(typeof(T0)).Id;
            var componentArray = (StructArray<T0>) components[id];
            componentArray.GetComponentRef(locate.componentIndex) = t0;
        }

        public ref T0 ReadWrite<T0>(ComponentInArray locate) where T0 : struct
        {
            var id = ComponentTypeRegistry.Get(typeof(T0)).Id;
            var componentArray = (StructArray<T0>) components[id];
            return ref componentArray.GetComponentRef(locate.componentIndex);
        }

        private void CreateLookUp(ComponentType[] types)
        {
            // 插入排序 升序
            var span = types.AsSpan();
            for (int i = 1; i < span.Length; i++)
            {
                ComponentType key = span[i];
                int j = i - 1;
                while (j >= 0 && span[j].Id > key.Id)
                {
                    span[j + 1] = span[j];
                    j--;
                }
                span[j + 1] = key;
            }

            lookUp = new int[span[^1].Id + 1];
            for (int i = 0; i < span.Length; i++)
            {
                lookUp[span[i].Id] = i;
            }
        }
    }
}