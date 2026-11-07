using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    /// <summary>组件类型元数据（每组件类型一份，按 ComponentType.Id 索引）。</summary>
    internal unsafe struct ComponentTypeInfo
    {
        public bool IsEnableable;
        public bool IsShared;
        public bool IsRelation;
        public bool IsExclusiveRelation;
        public bool IsMultiRelation;
        public int MultiRelationMaxSlots;
        public bool IsExclusiveTarget;
        public bool CascadeOnTargetDeleted;
        public bool IsAcyclicRelation;
        public bool IsDisposable;
        public bool IsCopyable;
        public delegate*<void*, void> DisposeFn;
        public delegate*<void*, void*, void> CopyFn;
    }

    /// <summary>
    /// 组件类型管理器
    /// </summary>
    public unsafe class ComponentTypeManager
    {
        private static int idAllocator = 0;
        private static readonly ConcurrentDictionary<Type, ComponentType> ComponentTypeRegistries = new();
        public static readonly ConcurrentDictionary<int, Type> idToTpyeMap = new();
        private static ComponentTypeInfo[] _componentTypeInfos = new ComponentTypeInfo[100];

        // 用于保护所有静态状态的锁。Component 注册是稀有操作，锁开销可忽略。
        private static readonly object _typeLock = new();
        [ThreadStatic] private static int _disposalDepth;
        internal static bool IsDisposingComponent => _disposalDepth != 0;
        [ThreadStatic] private static int _copyDepth;
        internal static bool IsCopyingComponent => _copyDepth != 0;
        internal readonly struct CopyPreparationScope : IDisposable
        {
            public void Dispose() => _copyDepth--;
        }
        internal static CopyPreparationScope BeginCopyPreparation() { _copyDepth++; return default; }

        private static class Access<T> where T : struct
        {
            internal static readonly bool IsRelation = typeof(IRelationComponent).IsAssignableFrom(typeof(T));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsRelationType<T>() where T : struct => Access<T>.IsRelation;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CheckWritableAccess<T>() where T : struct
        {
            if (IsCopyingComponent) throw new InvalidOperationException("Writable ECS views are not allowed inside component copy hooks.");
            if (Access<T>.IsRelation)
                throw new InvalidOperationException($"Relationship '{typeof(T)}' requires read-only access; use EntityManager or DeferredCommandBuffer to change it.");
        }

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

                int id = idAllocator;
                bool isShared = typeof(ISharedComponentData).IsAssignableFrom(type);
                bool isRelation = typeof(IRelationComponent).IsAssignableFrom(type);
                if (isRelation)
                    ValidateRelationDefinition(type);
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

                if (id >= _componentTypeInfos.Length)
                    Array.Resize(ref _componentTypeInfos, _componentTypeInfos.Length * 2);

                bool isMultiRelation = isRelation && Attribute.IsDefined(type, typeof(MultiRelationAttribute));

                _componentTypeInfos[id] = new ComponentTypeInfo
                {
                    IsEnableable = typeof(IEnableableComponent).IsAssignableFrom(type),
                    IsShared = isShared,
                    IsRelation = isRelation,
                    IsExclusiveRelation = isRelation && Attribute.IsDefined(type, typeof(ExclusiveRelationAttribute)),
                    IsMultiRelation = isMultiRelation,
                    // 定长槽位数：[MultiRelation(MaxSlots=N)]，N≥2 走定长多槽列；0 = 托管列表模式
                    MultiRelationMaxSlots = isMultiRelation
                        ? ((MultiRelationAttribute)Attribute.GetCustomAttribute(type, typeof(MultiRelationAttribute)))!.MaxSlots
                        : 0,
                    // 入边唯一约束（[ExclusiveTarget]，背包场景）
                    IsExclusiveTarget = isMultiRelation && Attribute.IsDefined(type, typeof(ExclusiveTargetAttribute)),
                    // 声明级联（[OnTargetDeleted(Cascade=true)]，target 销毁时级联销毁 sources）
                    CascadeOnTargetDeleted = isRelation
                        && Attribute.IsDefined(type, typeof(OnTargetDeletedAttribute))
                        && ((OnTargetDeletedAttribute)Attribute.GetCustomAttribute(type, typeof(OnTargetDeletedAttribute)))!.Cascade,
                    IsAcyclicRelation = isRelation && Attribute.IsDefined(type, typeof(AcyclicRelationAttribute)),
                    IsDisposable = typeof(IDisposable).IsAssignableFrom(type),
                    IsCopyable = typeof(ICopyable).IsAssignableFrom(type),
                };

                var newComponentType = new ComponentType(id, size);
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
        /// 使用 C# 的无引用值类型布局，关系组件允许携带关系数据。
        /// 要求首个字段必须是 RelationSlot Target（偏移 0，LayoutKind.Sequential），
        /// 使列前 8B 重解释为 RelationSlot 的正确性与 GetComponentDataSpan&lt;TRel&gt; 步长一致。
        /// 注册时校验；否则抛异常（防空 struct / 缺 Target 字段误用导致越界或步长错位）。
        /// 普通组件与 typed span、sizeof(T) 和原生转译使用相同的步长。
        /// </summary>
        private static int ComputeComponentSize(Type type)
        {
            if (typeof(IRelationComponent).IsAssignableFrom(type))
            {
                var target = type.GetField("Target", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (target == null || target.FieldType != typeof(RelationSlot))
                    throw new InvalidOperationException($"Relation component '{type.FullName}' requires a RelationSlot Target field.");
                // 校验 Target 首字段（偏移 0）。Marshal.OffsetOf 对缺失字段抛 ArgumentException。
                IntPtr targetOffset;
                try
                {
                    targetOffset = Marshal.OffsetOf(type, "Target");
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException(
                        $"Relation component '{type.FullName}' must contain a field named 'Target' of type RelationSlot as its first field "
                        + "(e.g. 'public struct ChildOf : IRelationComponent { public RelationSlot Target; }').", ex);
                }
                if (targetOffset != IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"Relation component '{type.FullName}' must have its RelationSlot 'Target' field at offset 0 "
                        + $"(currently at {targetOffset}). Use LayoutKind.Sequential and declare Target first.");
            }
            return ManagedValueLayout.SizeOf(type);
        }

        private static void ValidateRelationDefinition(Type type)
        {
            bool multi = Attribute.IsDefined(type, typeof(MultiRelationAttribute));
            bool exclusive = Attribute.IsDefined(type, typeof(ExclusiveRelationAttribute));
            bool exclusiveTarget = Attribute.IsDefined(type, typeof(ExclusiveTargetAttribute));
            bool acyclic = Attribute.IsDefined(type, typeof(AcyclicRelationAttribute));
            bool copyable = false;
            foreach (var implemented in type.GetInterfaces())
                if (implemented.IsGenericType && implemented.GetGenericTypeDefinition() == typeof(ICopyable<>))
                    copyable = true;
            if (!type.IsLayoutSequential || typeof(ISharedComponentData).IsAssignableFrom(type)
                || typeof(IEnableableComponent).IsAssignableFrom(type) || typeof(IDisposable).IsAssignableFrom(type)
                || copyable)
                throw new InvalidOperationException($"Relation '{type.FullName}' must be a sequential unmanaged value without shared, enableable or resource hooks.");
            if ((multi && (exclusive || acyclic)) || (!multi && exclusiveTarget))
                throw new InvalidOperationException($"Relation '{type.FullName}' has incompatible relationship attributes.");

            if (!multi) return;
            int slots = ((MultiRelationAttribute)Attribute.GetCustomAttribute(type, typeof(MultiRelationAttribute)))!.MaxSlots;
            if (slots < 0 || slots == 1)
                throw new InvalidOperationException($"Relation '{type.FullName}' MaxSlots must be zero or at least two.");
            int expected = slots == 0 ? sizeof(RelationSlot) : checked(slots * sizeof(RelationSlot));
            if (ManagedValueLayout.SizeOf(type) != expected)
                throw new InvalidOperationException($"Relation '{type.FullName}' requires exactly {expected} bytes for its slots.");
            for (int i = 1; i < slots; i++)
            {
                string name = $"Slot{i}";
                var field = type.GetField(name, System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (field?.FieldType != typeof(RelationSlot) || Marshal.OffsetOf(type, name).ToInt64() != i * sizeof(RelationSlot))
                    throw new InvalidOperationException($"Relation '{type.FullName}' requires RelationSlot {name} at byte {i * sizeof(RelationSlot)}.");
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
                return ManagedValueLayout.SizeOf(type);
            return sizeof(int);  // managed shared：chunk 槽位只存 int 索引
        }

        /// <summary>判断是否能按 C# 内存布局保存为无托管引用的值类型。</summary>
        public static bool IsBlittable(Type type)
            => ManagedValueLayout.IsUnmanaged(type);

        /// <summary>该组件类型是否为 Shared（ISharedComponentData）。</summary>
        public static bool GetIsShared(int id) => _componentTypeInfos[id].IsShared;

        /// <summary>该组件类型是否为关系组件（IRelationComponent）。</summary>
        public static bool GetIsRelation(int id) => _componentTypeInfos[id].IsRelation;

        public static bool GetIsAcyclicRelation(int id) => _componentTypeInfos[id].IsAcyclicRelation;

        /// <summary>该关系组件类型是否标记 [ExclusiveRelation]（真 1:1，入边唯一）。</summary>
        public static bool GetIsExclusiveRelation(int id) => _componentTypeInfos[id].IsExclusiveRelation;

        /// <summary>该关系组件类型是否标记 [MultiRelation]（出边多值，1:N / M:N）。</summary>
        public static bool GetIsMultiRelation(int id) => _componentTypeInfos[id].IsMultiRelation;

        /// <summary>[MultiRelation(MaxSlots=N)] 的定长槽位数；0 = 托管列表模式。</summary>
        public static int GetMultiRelationMaxSlots(int id) => _componentTypeInfos[id].MultiRelationMaxSlots;

        /// <summary>该多值关系是否标记 [ExclusiveTarget]（入边唯一，背包语义）。</summary>
        public static bool GetIsExclusiveTarget(int id) => _componentTypeInfos[id].IsExclusiveTarget;

        /// <summary>该关系是否声明级联（[OnTargetDeleted(Cascade=true)]，target 销毁时级联销毁 sources）。</summary>
        public static bool GetCascadeOnTargetDeleted(int id) => _componentTypeInfos[id].CascadeOnTargetDeleted;

        /// <summary>该组件类型是否实现 IDisposable（持有原生资源）。</summary>
        public static bool GetIsDisposable(int id) => _componentTypeInfos[id].IsDisposable;

        /// <summary>该组件类型是否实现 ICopyable（复制时需调用 OnCopy）。</summary>
        public static bool GetIsCopyable(int id) => _componentTypeInfos[id].IsCopyable;

        /// <summary>通过该组件类型的ID获取对应的类型</summary>
        public static Type GetTypeByComponentType(int id) => idToTpyeMap[id];

        public static bool GetIsEnableable(int id) => _componentTypeInfos[id].IsEnableable;

        /// <summary>
        /// 显式注册持有原生资源组件的 Dispose 函数指针（对齐 Flecs <c>ecs_set_hooks</c> 的 dtor）。
        /// 组件实现 <see cref="IDisposable"/> 后须调用一次；AOT 安全（泛型实例化，无反射）。
        /// </summary>
        public static void RegisterDisposable<T>() where T : unmanaged, IDisposable
        {
            int id = GetComponentType(typeof(T)).Id;
            _componentTypeInfos[id].DisposeFn = DisposableHooks<T>.Dispose;
        }

        /// <summary>
        /// 显式注册组件复制钩子的 Copy 函数指针（复制组件值时调用 OnCopy，如 SharedBlob 的 refcount++）。
        /// 组件实现 <see cref="ICopyable{T}"/> 后须调用一次；AOT 安全（泛型实例化，无反射）。
        /// </summary>
        public static void RegisterCopyable<T>() where T : unmanaged, ICopyable<T>
        {
            int id = GetComponentType(typeof(T)).Id;
            _componentTypeInfos[id].CopyFn = CopyableHooks<T>.Copy;
        }

        /// <summary>
        /// 转移组件所有权（move，零分配）：
        /// IDisposable 组件 → 位拷贝 dst←src 后清空 src（转移指针，避免双所有权/悬垂）；
        /// 用于跨 archetype 迁移（CopyComponentsTo）。
        /// </summary>
        public static void MoveComponentValue(ComponentType type, void* src, void* dst)
        {
            Unsafe.CopyBlock(dst, src, (uint)type.Size);
            if (_componentTypeInfos[type.Id].DisposeFn != null)
                Unsafe.InitBlock(src, 0, (uint)type.Size);
        }

        /// <summary>
        /// 销毁组件值（释放原生资源）：IDisposable 组件 → 调 Dispose；普通 → no-op。
        /// 若实现 IDisposable 但未注册 → 抛错（fail-fast，提示补注册）。
        /// </summary>
        public static void DestroyComponentValue(ComponentType type, void* ptr)
        {
            var info = _componentTypeInfos[type.Id];
            if (info.DisposeFn != null)
            {
                _disposalDepth++;
                try { info.DisposeFn(ptr); }
                finally { _disposalDepth--; }
            }
            else if (info.IsDisposable)
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
            var info = _componentTypeInfos[type.Id];
            if (info.CopyFn != null)
            {
                using var preparation = BeginCopyPreparation();
                info.CopyFn(src, dst);
            }
            else if (info.IsCopyable)
            {
                throw new InvalidOperationException(
                    $"Component '{type.Type.FullName}' implements ICopyable but its Copy hook is not registered. "
                    + "Call ComponentTypeManager.RegisterCopyable<T>() before use.");
            }
            else
            {
                Unsafe.CopyBlock(dst, src, (uint)type.Size);
            }
        }

        internal static void ValidateCopySupport(ComponentType type)
        {
            if (type.Size <= 0 || type.Size != GetComponentType(type.Type).Size)
                throw new InvalidOperationException($"Clone source layout is incompatible with '{type.Type}'.");
            var info = _componentTypeInfos[type.Id];
            if (info.IsDisposable && (!info.IsCopyable || info.DisposeFn == null))
                throw new InvalidOperationException($"Component '{type.Type}' requires registered copy and disposal hooks to clone.");
            if (info.IsCopyable && info.CopyFn == null)
                throw new InvalidOperationException($"Component '{type.Type}' requires a registered copy hook.");
            if (type.IsShared && (info.IsCopyable || info.IsDisposable))
                throw new NotSupportedException("Cloning shared components with ownership hooks is not supported.");
        }

        /// <summary>按 id 反查类型（布局指纹登记时要拿 owner 程序集；找不到返回 false）。</summary>
        public static bool TryGetTypeById(int id, out Type type) => idToTpyeMap.TryGetValue(id, out type);
    }
}
