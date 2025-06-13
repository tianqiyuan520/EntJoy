using System;
using System.Collections.Generic;

namespace EntJoy
{
    /// <summary>
    /// 组件类型管理器
    /// </summary>
    public class ComponentTypeManager
    {
        private static int idAllocator = 0;  // ID分配器
        private static readonly Dictionary<Type, ComponentType> ComponentTypeRegistries = new();  // 组件类型到组件类型映射
        public static readonly Dictionary<int, Type> idToTpyeMap = new();  // 记录ID到与组件类型映射

        /// <summary>
        /// 获取该类型对应的组件 
        /// <br>查询该<paramref name="type"/>的对应组件类型，若查询无果则注册该类型</br>
        /// </summary>
        public static ComponentType GetComponentType(Type type)
        {
            if (ComponentTypeRegistries.TryGetValue(type, out var componentType))  // 检查是否已注册
            {
                return componentType;
            }
            //若为注册过该类型
            var newComponentType = new ComponentType(idAllocator);  // 创建新组件类型
            ComponentTypeRegistries.Add(type, newComponentType);  // 添加到类型映射
            idToTpyeMap.Add(idAllocator, type);  // 添加到ID映射
            idAllocator++;  // 递增ID分配器
            return newComponentType;  // 返回新组件类型
        }

        /// <summary>
        /// 通过该组件类型的ID获取对应的类型
        /// </summary>
        public static Type GetTypeByComponentType(int id)
        {
            return idToTpyeMap[id];
        }
    }
}
