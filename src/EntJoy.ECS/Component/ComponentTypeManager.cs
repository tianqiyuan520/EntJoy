using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
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
        private static ComponentType[] _componentTypesById = new ComponentType[100];

        // 用于保护所有静态状态的锁。Component 注册是稀有操作，锁开销可忽略。
        private static readonly object _typeLock = new();
        [ThreadStatic] private static int _disposalDepth;
        internal static bool IsDisposingComponent => _disposalDepth != 0;
        [ThreadStatic] private static int _copyDepth;
        private static int _activeCopyScopes;
        internal static bool IsCopyingComponent
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref _activeCopyScopes) != 0 && IsCurrentThreadCopying();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsCurrentThreadCopying() => _copyDepth != 0;

        internal readonly struct CopyPreparationScope : IDisposable
        {
            public void Dispose()
            {
                _copyDepth--;
                Interlocked.Decrement(ref _activeCopyScopes);
            }
        }
        internal static CopyPreparationScope BeginCopyPreparation()
        {
            // 先登记全局作用域，再进入本线程；无复制时的组件访问只读全局计数。
            Interlocked.Increment(ref _activeCopyScopes);
            _copyDepth++;
            return default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsRelationType<T>() where T : struct => default(T) is IRelationComponent;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CheckWritableAccess<T>() where T : struct
        {
            if (IsRelationType<T>() || (Volatile.Read(ref _activeCopyScopes) != 0 && _copyDepth != 0))
                throw WritableAccessException<T>();
        }

        /// <summary>在返回查询的可写引用前校验借用和当前线程的写权限。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CheckWritableAccess<T>(QueryReadState state) where T : struct
        {
            if (IsRelationType<T>() || (Volatile.Read(ref _activeCopyScopes) != 0 && _copyDepth != 0))
                ThrowTrackedWritableAccess<T>(state);
            state.ValidateTracked();
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowTrackedWritableAccess<T>(QueryReadState state) where T : struct
        {
            state.ValidateTracked();
            throw WritableAccessException<T>();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static InvalidOperationException WritableAccessException<T>() where T : struct
        {
            if (IsCopyingComponent) return new InvalidOperationException("Writable ECS views are not allowed inside component copy hooks.");
            return new InvalidOperationException($"Relationship '{typeof(T)}' requires read-only access; use EntityManager or DeferredCommandBuffer to change it.");
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

        /// <summary>泛型入口：按 T 静态缓存组件类型，每次调用不再查字典。</summary>
        public static ComponentType GetComponentType<T>() where T : struct => ComponentTypeByType<T>.Value;

        private static class ComponentTypeByType<T> where T : struct
        {
            internal static readonly ComponentType Value = GetComponentType(typeof(T));
        }

        /// <summary>取该类型对应的组件；未注册则在此注册并分配 id。</summary>
        public static ComponentType GetComponentType(Type type)
        {
            // 读多写少：命中时无锁。
            if (ComponentTypeRegistries.TryGetValue(type, out var registered))
                return registered;

            lock (_typeLock)
            {
                // 二次检查：可能在等锁期间被其它线程注册。
                if (ComponentTypeRegistries.TryGetValue(type, out registered))
                    return registered;
                return RegisterComponentTypeLocked(type);
            }
        }

        private static ComponentType RegisterComponentTypeLocked(Type type)
        {
            int id = idAllocator;
            var info = DescribeType(type);
            var componentType = new ComponentType(id, info.Size);

            int required = id + 1;
            if (required > _componentTypeInfos.Length)
                Array.Resize(ref _componentTypeInfos, Math.Max(required, _componentTypeInfos.Length * 2));
            if (required > _componentTypesById.Length)
                Array.Resize(ref _componentTypesById, Math.Max(required, _componentTypesById.Length * 2));

            _componentTypeInfos[id] = info.Info;
            _componentTypesById[id] = componentType;
            idToTpyeMap[id] = type;
            // 先发布 id 反查，再发布类型注册表：读到注册表项的线程必须能立刻反查出类型。
            ComponentTypeRegistries[type] = componentType;
            idAllocator = required;
            return componentType;
        }

        /// <summary>注册时算出的组件元数据与字节宽度。</summary>
        private readonly struct TypeDescription
        {
            public readonly ComponentTypeInfo Info;
            public readonly int Size;
            public TypeDescription(ComponentTypeInfo info, int size) { Info = info; Size = size; }
        }

        private static TypeDescription DescribeType(Type type)
        {
            bool isShared = typeof(ISharedComponentData).IsAssignableFrom(type);
            bool isRelation = typeof(IRelationComponent).IsAssignableFrom(type);
            if (isRelation) ValidateRelationDefinition(type);

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

            var multi = (MultiRelationAttribute)Attribute.GetCustomAttribute(type, typeof(MultiRelationAttribute));
            var cascade = (OnTargetDeletedAttribute)Attribute.GetCustomAttribute(type, typeof(OnTargetDeletedAttribute));

            var info = new ComponentTypeInfo
            {
                IsEnableable = typeof(IEnableableComponent).IsAssignableFrom(type),
                IsShared = isShared,
                IsRelation = isRelation,
                IsExclusiveRelation = isRelation && Attribute.IsDefined(type, typeof(ExclusiveRelationAttribute)),
                IsMultiRelation = isRelation && multi != null,
                MultiRelationMaxSlots = isRelation && multi != null ? multi.MaxSlots : 0,
                IsExclusiveTarget = isRelation && multi != null && Attribute.IsDefined(type, typeof(ExclusiveTargetAttribute)),
                CascadeOnTargetDeleted = isRelation && cascade != null && cascade.Cascade,
                IsAcyclicRelation = isRelation && Attribute.IsDefined(type, typeof(AcyclicRelationAttribute)),
                IsDisposable = typeof(IDisposable).IsAssignableFrom(type),
                IsCopyable = typeof(ICopyable).IsAssignableFrom(type),
            };
            return new TypeDescription(info, size);
        }

        /// <summary>
        /// 组件字节宽度。关系组件要求首个字段是偏移 0 的 RelationSlot Target：
        /// 列前 8 字节会被重解释为 RelationSlot，target 不在首位会让步长错位。
        /// </summary>
        private static int ComputeComponentSize(Type type)
        {
            if (typeof(IRelationComponent).IsAssignableFrom(type))
            {
                var target = type.GetField("Target", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (target == null || target.FieldType != typeof(RelationSlot))
                    throw new InvalidOperationException($"Relation component '{type.FullName}' requires a RelationSlot Target field.");
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

        /// <summary>按组件 id 直接取组件类型元数据（数组索引，不查字典）。</summary>
        public static ComponentType GetComponentType(int id) => _componentTypesById[id];

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
