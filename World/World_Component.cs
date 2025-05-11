using Godot;
using System;
using System.Linq;

namespace EntJoy 
{
    public partial class World
    {
        /// <summary>
        /// 添加组件
        /// </summary>
        public void AddComponent<T0>(Entity entity, T0 t0) where T0 : struct 
        {
            GD.Print("添加组件 ", typeof(T0));
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);  // 获取该实体引用
            var oldArch = entityInfoRef.Archetype;  // 获取该实体对应的原型
            if (oldArch.Has(typeof(T0)))  // 检查是否已有该组件
            {
                oldArch.Set(entityInfoRef.SlotInArchetype, t0);  // 直接设置组件值
                return;
            }

            GD.Print("但是组件不存在: ");
            GD.Print("旧的原型: ", oldArch.GetComponentTypeString());

            //如果不存在组件，说明该实体将移动到其他原型里
            Span<ComponentType> targetComponents = stackalloc ComponentType[oldArch.ComponentCount + 1];  // 创建新组件数组
            oldArch.Types.CopyTo(targetComponents);  // 复制原有组件
            targetComponents[^1] = ComponentTypeManager.GetComponentType(typeof(T0));  // 添加新组件类型
            var targetArch = GetOrCreateArchetype(targetComponents);  // 获取或创建新原型

            GD.Print("新的原型: ", targetArch.GetComponentTypeString());

            targetArch.AddEntity(entity, out var index);  // 添加实体到新原型
            oldArch.CopyComponentsTo(entityInfoRef.SlotInArchetype, targetArch, index);  //复制组件数据

            oldArch.Remove(entityInfoRef.SlotInArchetype, out var bePushEntityId, out var bePushEntityNewIndexInArchetype);  // 从旧原型移除
            if (bePushEntityId >= 0)  // 处理被移动的实体
            {
                ref var bePushEntityInfoRef = ref GetEntityInfoRef(bePushEntityId);  // 获取被移动实体引用
                bePushEntityInfoRef.SlotInArchetype = bePushEntityNewIndexInArchetype;  // 更新索引
            }
            entityInfoRef.SlotInArchetype = index;  // 更新实体索引
            targetArch.Set(index, t0);  // 设置新组件值

            GD.Print("迁移了:", " ", allArchetypes.Count(), " ", allArchetypes[0]?.EntityCount, " ", allArchetypes[1]?.EntityCount);
        }


        public void RemoveComponent<T0>(Entity entity) where T0 : struct
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            var oldArch = entityInfoRef.Archetype; 
            if (!oldArch.Has(typeof(T0)))
            {
                return;
            }

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

            var targetArch = GetOrCreateArchetype(targetComponents);  // 获取或创建新原型
            targetArch.AddEntity(entity, out var index);  // 添加实体到新原型
            oldArch.CopyComponentsTo(entityInfoRef.SlotInArchetype, targetArch, index);  // 复制组件数据
            oldArch.Remove(entityInfoRef.SlotInArchetype, out var bePushEntityId, out var bePushEntityNewIndexInArchetype);  // 从旧原型移除
            if (bePushEntityId >= 0)  // 处理被移动的实体
            {
                ref var bePushEntityInfoRef = ref GetEntityInfoRef(bePushEntityId);  // 获取被移动实体引用
                bePushEntityInfoRef.SlotInArchetype = bePushEntityNewIndexInArchetype;  // 更新索引
            }
            entityInfoRef.SlotInArchetype = index;  // 更新实体索引
        }

        /// <summary>
        /// 设置组件值
        /// </summary>
        public void Set<T>(Entity entity, T t) where T : struct, IComponent
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id); 
            var arch = entityInfoRef.Archetype;  // 获取对应的原型
            arch.Set(entityInfoRef.SlotInArchetype, t);
        }
    }
}
