using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy
{
    /// <summary>
    /// 组件类型管理器
    /// </summary>
    public class ComponentTypeManager
    {
        private static int idAllocator = 0;
        private static readonly Dictionary<Type, ComponentType> ComponentTypeRegistries = new();  // 组件类型到组件类型映射
        public static readonly Dictionary<int, Type> idToTpyeMap = new();
        private static int[] ComponentDataSize = new int[100];  //该原型对应的组件 大小

        private static bool[] ComponentIsEnableable = new bool[100]; // 记录组件是否为 enableable

        // 用于保护所有静态状态的锁。Component 注册是稀有操作，锁开销可忽略。
        private static readonly object _typeLock = new();

        /// <summary>
        /// 获取该类型对应的组件
        /// <br>查询该<paramref name="type"/>的对应组件类型，若查询无果则注册该类型</br>
        /// </summary>
        public static ComponentType GetComponentType(Type type)
        {
            // 先无锁快速路径检查（读多写少的优化）
            if (ComponentTypeRegistries.TryGetValue(type, out var componentType))
                return componentType;

            lock (_typeLock)
            {
                // 二次检查：可能在锁竞争期间被其它线程注册了
                if (ComponentTypeRegistries.TryGetValue(type, out componentType))
                    return componentType;

                // 若未注册过该类型
                int id = idAllocator;
                int size;
                try
                {
                    size = Marshal.SizeOf(type);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException(
                        $"Type {type.FullName} is not blittable. ECS components must be blittable structs.", ex);
                }
                var newComponentType = new ComponentType(id, size);  // 创建新组件类型

                if (id >= ComponentDataSize.Length - 1)
                {
                    Array.Resize(ref ComponentDataSize, ComponentDataSize.Length * 2);
                    Array.Resize(ref ComponentIsEnableable, ComponentIsEnableable.Length * 2);
                }
                // 判断是否为 IEnableableComponent
                ComponentIsEnableable[id] = typeof(IEnableableComponent).IsAssignableFrom(type);

                ComponentDataSize[id] = newComponentType.Size;

                ComponentTypeRegistries.Add(type, newComponentType);
                idToTpyeMap.Add(id, type);
                idAllocator = id + 1;
                return newComponentType;
            }
        }

        /// <summary>
        /// 通过该组件类型的ID获取对应的类型
        /// </summary>
        public static Type GetTypeByComponentType(int id) => idToTpyeMap[id];

        public static bool GetIsEnableable(int id) => ComponentIsEnableable[id];

        public static int GetComponentDataSize()
        {
            return 0;
        }
    }
}
