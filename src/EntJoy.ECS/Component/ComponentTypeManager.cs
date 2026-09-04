using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 组件类型管理器
    /// </summary>
    public unsafe class ComponentTypeManager
    {
        private static int idAllocator = 0;
        private static readonly ConcurrentDictionary<Type, ComponentType> ComponentTypeRegistries = new();
        public static readonly ConcurrentDictionary<int, Type> idToTpyeMap = new();
        private static int[] ComponentDataSize = new int[100];  //该原型对应的组件 大小

        private static bool[] ComponentIsEnableable = new bool[100]; // 记录组件是否为 enableable
        private static bool[] ComponentIsShared = new bool[100];     // 记录组件是否为 ISharedComponentData
        private static bool[] ComponentIsRelation = new bool[100];   // 记录组件是否为 IRelationComponent
        private static bool[] ComponentIsDisposable = new bool[100]; // 记录组件是否实现 IDisposable（持有原生资源）
        private static delegate*<void*, void>[] ComponentDisposeFn = new delegate*<void*, void>[100]; // Dispose 函数指针（null = 无资源）
        private static bool[] ComponentIsCopyable = new bool[100];  // 记录组件是否实现 ICopyable（复制时需 refcount++ 等）
        private static delegate*<void*, void*, void>[] ComponentCopyFn = new delegate*<void*, void*, void>[100]; // Copy 函数指针（null = 位拷贝）

        // 用于保护所有静态状态的锁。Component 注册是稀有操作，锁开销可忽略。
        private static readonly object _typeLock = new();

        /// <summary>泛型 Dispose 函数指针（AOT 安全：泛型实例化，无反射）。</summary>
        private static class DisposableHooks<T> where T : unmanaged, IDisposable
        {
            public static readonly delegate*<void*, void> Dispose = &DisposeHook;
            private static void DisposeHook(void* p) => Unsafe.AsRef<T>(p).Dispose();
        }

        /// <summary>泛型 Copy 函数指针（AOT 安全：泛型实例化，无反射）。</summary>
        private static class CopyableHooks<T> where T : unmanaged, ICopyable<T>
        {
            public static readonly delegate*<void*, void*, void> Copy = &CopyHook;
            private static void CopyHook(void* src, void* dst)
            {
                ref T s = ref Unsafe.AsRef<T>(src);
                ref T d = ref Unsafe.AsRef<T>(dst);
                d.OnCopy(in s, ref d);
            }
        }

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
                    size = isShared ? ComputeSharedSize(type) : ComputeComponentSize(type);
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
                    Array.Resize(ref ComponentIsRelation, ComponentIsRelation.Length * 2);
                    Array.Resize(ref ComponentIsDisposable, ComponentIsDisposable.Length * 2);
                    Array.Resize(ref ComponentIsCopyable, ComponentIsCopyable.Length * 2);
                    // 函数指针数组不能用 Array.Resize<T>（函数指针不可作泛型实参），手动扩容
                    var newDisposeFn = new delegate*<void*, void>[ComponentDisposeFn.Length * 2];
                    Array.Copy(ComponentDisposeFn, newDisposeFn, ComponentDisposeFn.Length);
                    ComponentDisposeFn = newDisposeFn;
                    var newCopyFn = new delegate*<void*, void*, void>[ComponentCopyFn.Length * 2];
                    Array.Copy(ComponentCopyFn, newCopyFn, ComponentCopyFn.Length);
                    ComponentCopyFn = newCopyFn;
                }
                // 判断是否为 IEnableableComponent
                ComponentIsEnableable[id] = typeof(IEnableableComponent).IsAssignableFrom(type);
                // 判断是否为 ISharedComponentData
                ComponentIsShared[id] = isShared;
                // 判断是否为 IRelationComponent
                ComponentIsRelation[id] = typeof(IRelationComponent).IsAssignableFrom(type);
                // 判断是否实现 IDisposable（持有原生资源，销毁/移除时需调用 Dispose）
                ComponentIsDisposable[id] = typeof(IDisposable).IsAssignableFrom(type);
                // 判断是否实现 ICopyable（复制组件值时需调用 OnCopy，如 SharedBlob 的 refcount++）
                ComponentIsCopyable[id] = typeof(ICopyable).IsAssignableFrom(type);

                ComponentDataSize[id] = newComponentType.Size;

                idToTpyeMap[id] = type;
                // Publish the reverse map before the type registry. A reader that observes
                // the registry entry must be able to resolve ComponentType.Type immediately.
                ComponentTypeRegistries[type] = newComponentType;
                idAllocator = id + 1;
                return newComponentType;
            }
        }

        /// <summary>
        /// 计算组件类型的大小：
        /// 关系组件（IRelationComponent）→ 强制 sizeof(RelationSlot)（8B，列值存 target+version）。
        /// 关系类型必须含 RelationSlot Target 字段（Unsafe.SizeOf&lt;TRel&gt; == 8B），
        /// 使 GetComponentDataSpan&lt;TRel&gt;()/IJobEntity 指针步长与列宽一致；
        /// 强制 8B 兜底防御空 struct 误用（防空 struct 注册成 1B 列导致越界）。
        /// 普通 blittable struct → Marshal.SizeOf。
        /// </summary>
        private static int ComputeComponentSize(Type type)
        {
            if (typeof(IRelationComponent).IsAssignableFrom(type))
                return System.Runtime.InteropServices.Marshal.SizeOf<RelationSlot>();
            return Marshal.SizeOf(type);
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

        /// <summary>该组件类型是否为关系组件（IRelationComponent）。</summary>
        public static bool GetIsRelation(int id) => ComponentIsRelation[id];

        /// <summary>该组件类型是否实现 IDisposable（持有原生资源）。</summary>
        public static bool GetIsDisposable(int id) => ComponentIsDisposable[id];

        /// <summary>该组件类型是否实现 ICopyable（复制时需调用 OnCopy）。</summary>
        public static bool GetIsCopyable(int id) => ComponentIsCopyable[id];

        /// <summary>
        /// 通过该组件类型的ID获取对应的类型
        /// </summary>
        public static Type GetTypeByComponentType(int id) => idToTpyeMap[id];

        public static bool GetIsEnableable(int id) => ComponentIsEnableable[id];

        /// <summary>
        /// 显式注册持有原生资源组件的 Dispose 函数指针（对齐 Flecs <c>ecs_set_hooks</c> 的 dtor）。
        /// 组件实现 <see cref="IDisposable"/> 后须调用一次；AOT 安全（泛型实例化，无反射）。
        /// </summary>
        public static void RegisterDisposable<T>() where T : unmanaged, IDisposable
        {
            int id = GetComponentType(typeof(T)).Id;
            ComponentDisposeFn[id] = DisposableHooks<T>.Dispose;
        }

        /// <summary>
        /// 显式注册组件复制钩子的 Copy 函数指针（复制组件值时调用 OnCopy，如 SharedBlob 的 refcount++）。
        /// 组件实现 <see cref="ICopyable{T}"/> 后须调用一次；AOT 安全（泛型实例化，无反射）。
        /// </summary>
        public static void RegisterCopyable<T>() where T : unmanaged, ICopyable<T>
        {
            int id = GetComponentType(typeof(T)).Id;
            ComponentCopyFn[id] = CopyableHooks<T>.Copy;
        }

        /// <summary>
        /// 转移组件所有权（move，零分配）：
        /// IDisposable 组件 → 位拷贝 dst←src 后清空 src（转移指针，避免双所有权/悬垂）；
        /// 普通 blittable 组件 → 位拷贝（源为死槽，清空无意义，省去）。
        /// 用于跨 archetype 迁移（CopyComponentsTo）。
        /// </summary>
        public static void MoveComponentValue(ComponentType type, void* src, void* dst)
        {
            if (ComponentDisposeFn[type.Id] != null)
            {
                Unsafe.CopyBlock(dst, src, (uint)type.Size);
                Unsafe.InitBlock(src, 0, (uint)type.Size);
            }
            else
            {
                Unsafe.CopyBlock(dst, src, (uint)type.Size);
            }
        }

        /// <summary>
        /// 销毁组件值（释放原生资源）：IDisposable 组件 → 调 Dispose；普通 → no-op。
        /// 若实现 IDisposable 但未注册 → 抛错（fail-fast，提示补注册）。
        /// </summary>
        public static void DestroyComponentValue(ComponentType type, void* ptr)
        {
            var fn = ComponentDisposeFn[type.Id];
            if (fn != null)
            {
                fn(ptr);
            }
            else if (ComponentIsDisposable[type.Id])
            {
                throw new InvalidOperationException(
                    $"Component '{type.Type.FullName}' implements IDisposable but its Dispose hook is not registered. "
                    + "Call ComponentTypeManager.RegisterDisposable<T>() before use.");
            }
        }

        /// <summary>
        /// 复制组件值：ICopyable 组件 → 调 OnCopy（如 SharedBlob 的 refcount++）；普通 → 位拷贝。
        /// 用于「真复制」场景（SpawnFrom 等），区别于 move（转移所有权）。
        /// </summary>
        public static void CopyComponentValue(ComponentType type, void* src, void* dst)
        {
            var fn = ComponentCopyFn[type.Id];
            if (fn != null)
            {
                fn(src, dst);
            }
            else
            {
                if (ComponentIsCopyable[type.Id])
                    throw new InvalidOperationException(
                        $"Component '{type.Type.FullName}' implements ICopyable but its Copy hook is not registered. "
                        + "Call ComponentTypeManager.RegisterCopyable<T>() before use.");
                Unsafe.CopyBlock(dst, src, (uint)type.Size);
            }
        }

    }
}
