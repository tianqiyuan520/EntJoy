using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
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
        private static bool[] ComponentIsShared = new bool[100];     // 记录组件是否为 ISharedComponentData

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
                bool isShared = typeof(ISharedComponentData).IsAssignableFrom(type);
                int size;
                try
                {
                    size = isShared ? ComputeSharedSize(type) : Marshal.SizeOf(type);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException(
                        $"Type {type.FullName} is not blittable. ECS components must be blittable structs "
                        + $"(managed references only allowed in {nameof(ISharedComponentData)}).", ex);
                }
                var newComponentType = new ComponentType(id, size);  // 创建新组件类型

                if (id >= ComponentDataSize.Length - 1)
                {
                    Array.Resize(ref ComponentDataSize, ComponentDataSize.Length * 2);
                    Array.Resize(ref ComponentIsEnableable, ComponentIsEnableable.Length * 2);
                    Array.Resize(ref ComponentIsShared, ComponentIsShared.Length * 2);
                }
                // 判断是否为 IEnableableComponent
                ComponentIsEnableable[id] = typeof(IEnableableComponent).IsAssignableFrom(type);
                // 判断是否为 ISharedComponentData
                ComponentIsShared[id] = isShared;

                ComponentDataSize[id] = newComponentType.Size;

                ComponentTypeRegistries.Add(type, newComponentType);
                idToTpyeMap.Add(id, type);
                idAllocator = id + 1;
                return newComponentType;
            }
        }

        /// <summary>
        /// 计算 shared 组件类型的大小：
        /// blittable struct → 实际大小（内联存储于 chunk 内存块）；
        /// managed（class 或含引用字段的 struct）→ sizeof(int)（chunk 只存索引引用）。
        /// </summary>
        private static int ComputeSharedSize(Type type)
        {
            if (type.IsValueType && IsBlittable(type))
                return Marshal.SizeOf(type);

            // managed shared：chunk 槽位只存 int 索引（指向 EntityManager 哈希桶数组）
            return sizeof(int);
        }

        /// <summary>判断类型是否 blittable（无引用字段的 struct / 原生类型）。</summary>
        public static bool IsBlittable(Type type)
        {
            if (!type.IsValueType) return false;
            try
            {
                // Marshal.SizeOf 对含引用字段的 struct 抛 ArgumentException
                _ = Marshal.SizeOf(type);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>该组件类型是否为 Shared（ISharedComponentData）。</summary>
        public static bool GetIsShared(int id) => ComponentIsShared[id];

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
