using System;

namespace EntJoy
{
    public partial class World
    {
        /// <summary>
        /// 添加组件
        /// </summary>
        public void AddComponent<T0>(Entity entity, T0 t0) where T0 : struct
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);  // 获取该实体引用
            var oldArch = entityInfoRef.Archetype;  // 获取该实体对应的原型
            if (oldArch.Has(typeof(T0)))  // 检查原本的原型是否已有该组件
            {
                oldArch.Set(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, t0);  // 直接设置组件值
                return;
            }

            //如果不存在组件，说明该实体将移动到其他原型里
            Span<ComponentType> targetComponents = stackalloc ComponentType[oldArch.ComponentCount + 1];  // 创建新组件数组
            oldArch.Types.CopyTo(targetComponents);  // 复制原有组件
            targetComponents[^1] = ComponentTypeManager.GetComponentType(typeof(T0));  // 添加新组件类型
            var targetArch = GetOrCreateArchetype(targetComponents);  // 获取或创建新原型

            targetArch.AddEntity(entity, out var chunkIndex, out var slotInChunk);  // 添加实体到新原型
            //将该实体的组件信息拷贝转移
            oldArch.CopyComponentsTo(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, targetArch, chunkIndex, slotInChunk);  //复制组件数据
            oldArch.Remove(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, out var movedEntityID, out var movedEntitySlotInChunk); 
            if (movedEntityID >= 0)  // 处理被移动的实体
            {
                ref var bePushEntityInfoRef = ref GetEntityInfoRef(movedEntityID);  // 获取被移动实体引用
                bePushEntityInfoRef.SlotInChunk = movedEntitySlotInChunk;  // 更新索引
            }
            //刷新索引
            entityInfoRef.ChunkIndex = chunkIndex;
            entityInfoRef.SlotInChunk = slotInChunk;
            // 设置新组件值
            targetArch.Set(chunkIndex, slotInChunk, t0);
        }


        public void RemoveComponent<T0>(Entity entity) where T0 : struct
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            var oldArch = entityInfoRef.Archetype;
            //若旧原型中无该类型，则直接返回
            if (!oldArch.Has(typeof(T0)))
            {
                return;
            }

            //生成 目标"组件类型"
            Span<ComponentType> targetComponents = stackalloc ComponentType[oldArch.ComponentCount - 1];  // 创建新组件数组
            int spanIndex = 0;
            for (int i = 0; i < oldArch.Types.Length; i++)  // 遍历组件类型
            {
                var comType = oldArch.Types[i];  // 获取组件类型
                if (comType.Type == typeof(T0))  // 跳过要移除的组件
                {
                    continue;
                }

                targetComponents[spanIndex++] = comType;  // 添加保留的组件
            }
            // 获取或创建新原型
            var targetArch = GetOrCreateArchetype(targetComponents);
            targetArch.AddEntity(entity, out var chunkIndex, out var slotInChunk);  // 添加实体到新原型

            oldArch.CopyComponentsTo(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, targetArch, chunkIndex, slotInChunk);  //复制组件数据
            oldArch.Remove(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, out var movedEntityID, out var movedEntitySlotInChunk);  // 从旧原型移除
            if (movedEntityID >= 0)  // 处理被移动的实体
            {
                ref var bePushEntityInfoRef = ref GetEntityInfoRef(movedEntityID);  // 获取被移动实体引用
                bePushEntityInfoRef.SlotInChunk = movedEntitySlotInChunk;  // 更新索引
            }
            //刷新索引
            entityInfoRef.ChunkIndex = chunkIndex;
            entityInfoRef.SlotInChunk = slotInChunk;
        }

        /// <summary>
        /// 设置组件值
        /// </summary>
        public void Set<T>(Entity entity, T t) where T : struct, IComponent
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            var arch = entityInfoRef.Archetype;  // 获取对应的原型
            arch.Set(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, t);
        }
    }
}
