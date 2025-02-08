using System.Collections.Generic;

namespace EntJoy
{
    public class Archetype
    {
        private ComponentType[] types;
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
            }
        }

        /// <summary>
        /// 添加实体
        /// </summary>
        public void AddEntity(Entity entity)
        {
            entities.Add(entity);
            EntityCount++;
        }
        
        /// <summary>
        /// 删除实体和对应组件
        /// </summary>
        public void Remove(int index, out RemovedEntityInfoInArchetype info)
        {
            info = new RemovedEntityInfoInArchetype();
            // 用最后一个有效实体去覆盖被移除的位置
            if (EntityCount >= 2)
            {
                int desireBeMoveIndex = EntityCount - 1;
                info.IsExecuteMove = true;
                info.NewIndex = desireBeMoveIndex;
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

        public void MoveEntityToAnotherArchetypeByAdd<T>(int index)
        {
            
        }
    }
}