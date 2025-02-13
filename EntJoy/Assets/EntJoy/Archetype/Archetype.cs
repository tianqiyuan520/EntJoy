using System;
using System.Collections.Generic;

namespace EntJoy
{
    public class Archetype
    {
        private ComponentType[] types;
        public ReadOnlySpan<ComponentType> Types => types;
        private StructArray[] structArrays;
        private StructArray<Entity> entities;
        private Dictionary<ComponentType, int> componentTypeToArrayIndices;
        public int ComponentCount { get; private set; }
        public int EntityCount { get; private set; }
        
        internal Archetype(ComponentType[] ts)
        {
            types = ts;
            ComponentCount = ts.Length;
            structArrays = new StructArray[ts.Length];
            entities = new StructArray<Entity>(8);
            componentTypeToArrayIndices = new Dictionary<ComponentType, int>(ts.Length);
            for (int i = 0; i < ComponentCount; i++)
            {
                componentTypeToArrayIndices.Add(types[i], i);
                var genericType = typeof(StructArray<>).MakeGenericType(types[i].Type);
                var instance = Activator.CreateInstance(genericType, new object[] { 8 });
                var array = (StructArray)instance;
                structArrays[i] = array;
            }
        }

        public bool HasAllOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (!componentTypeToArrayIndices.ContainsKey(spanTypes[i]))
                {
                    return false;
                }
            }


            return true;
        }

        public bool HasAnyOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (componentTypeToArrayIndices.ContainsKey(spanTypes[i]))
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasNoneOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (componentTypeToArrayIndices.ContainsKey(spanTypes[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public bool Has(Type type)
        {
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == type)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 添加实体
        /// </summary>
        public void AddEntity(Entity entity, out int slotInArchetype)
        {
            entities.Add(entity);
            slotInArchetype = EntityCount;
            EntityCount++;
        }
        
        /// <summary>
        /// 删除实体和对应组件
        /// </summary>
        public void Remove(int index, out int bePushEntityId, out int bePushEntityNewIndexInArchetype)
        {
            // 用最后一个有效实体去覆盖被移除的位置
            if (EntityCount >= 2)
            {
                int desireBeMoveIndex = EntityCount - 1;
                bePushEntityId = entities.GetRef(desireBeMoveIndex).Id;
                bePushEntityNewIndexInArchetype = index;
                entities.Move(desireBeMoveIndex, index);
                for (int i = 0; i < ComponentCount; i++)
                {
                    var array = structArrays[i];
                    array.Move(desireBeMoveIndex, index);
                }
            }
            // 如果这是最后一个实体了, 那就只要设置为默认 
            else
            {
                bePushEntityId = -1;
                bePushEntityNewIndexInArchetype = -1;
                entities.SetDefault(index);
                for (int i = 0; i < ComponentCount; i++)
                {
                    var array = structArrays[i];
                    array.SetDefault(index);
                }
            }
        }

        /// <summary>
        /// 设置指定位置的组件值
        /// </summary>
        public void Set<T>(int index, T value) where T : struct
        {
            var arrayIndex = componentTypeToArrayIndices[typeof(T)];
            var array = (StructArray<T>)structArrays[arrayIndex];
            array.SetValue(index, value);
        }

        public void CopyComponentsTo(int sourceIndex, Archetype target, int destinationIndex)
        {
            for (int i = 0; i < ComponentCount; i++)
            {
                // 当前要复制的组件类型
                var array = structArrays[i];
                var arrayType = array.GetType();

                // 计算目标原型中,是否包含此组件类型
                if (!target.Has(arrayType))
                {
                    continue;
                }
                
                // 找到目标原型中, 该类型组件是第几行第几列
                // 然后把组件的值复制过去
                var targetCow = target.componentTypeToArrayIndices[arrayType];
                var targetColumn = destinationIndex;
                var targetArray = target.structArrays[targetCow];
                array.CopyTo(sourceIndex, targetArray, targetColumn);
            }
        }

        public Entity GetEntity(int index)
        {
            return entities.GetRef(index);
        }
        
        public ArchetypeQuery<T> GetQuery<T>() where T : struct
        {
            var indexOfT = componentTypeToArrayIndices[typeof(T)];
            var array = structArrays[indexOfT];
            return new ArchetypeQuery<T>((StructArray<T>)array);
        }
    }
}