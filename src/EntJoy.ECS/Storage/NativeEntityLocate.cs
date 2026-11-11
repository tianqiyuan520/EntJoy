using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// blittable 实体定位条目：原生内核与 job 直接读的「实体 → 组件列」定位信息，可按值传给并行 job 只读共享。
    /// 布局 24B、<c>LayoutKind.Sequential</c>，与 <c>src/NativeDll/NativeEntityLookup.h</c> 一一对应：
    /// +0 ChunkMemory、+8 ChunkOffsets、+16 SlotInChunk、+20 Version。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EntityLocateB
    {
        public void* ChunkMemory;
        public int* ChunkOffsets;
        public int SlotInChunk;
        public int Version;

        /// <summary>槽位是否被占用（未分配或已销毁为 false）。null 基址与 -1 slot 都判为未占用。</summary>
        public bool IsAllocated => ChunkMemory != null && SlotInChunk >= 0;
    }

    /// <summary>
    /// 非托管定位表/列偏移表的分配与释放（框架内部）。
    /// 用 <see cref="Marshal.AllocHGlobal"/>：表在 World 生命周期内长期存在，且必须能被原生内核直接读。
    /// </summary>
    public static unsafe class NativeLocateStorage
    {
        /// <summary>分配容量为 <paramref name="capacity"/> 的定位表，全部初始化为"未分配"。</summary>
        public static EntityLocateB* AllocateLocate(int capacity)
        {
            if (capacity <= 0) return null;
            var ptr = (EntityLocateB*)Marshal.AllocHGlobal(capacity * sizeof(EntityLocateB));
            InitRange(ptr, 0, capacity);
            return ptr;
        }

        /// <summary>扩容定位表：拷贝旧内容，新区域初始化为"未分配"。旧的块被释放。</summary>
        public static EntityLocateB* GrowLocate(EntityLocateB* old, int oldCapacity, int newCapacity)
        {
            if (newCapacity <= oldCapacity) return old;
            var ptr = (EntityLocateB*)Marshal.AllocHGlobal(newCapacity * sizeof(EntityLocateB));
            if (old != null && oldCapacity > 0)
                Buffer.MemoryCopy(old, ptr, (long)newCapacity * sizeof(EntityLocateB), (long)oldCapacity * sizeof(EntityLocateB));
            InitRange(ptr, oldCapacity, newCapacity);
            if (old != null) Marshal.FreeHGlobal((IntPtr)old);
            return ptr;
        }

        /// <summary>把托管偏移数组镜像成非托管一份（每个 Archetype 一份，生命周期同 Archetype）。</summary>
        public static int* AllocateOffsets(int[] offsets)
        {
            if (offsets == null || offsets.Length == 0) return null;
            var ptr = (int*)Marshal.AllocHGlobal(offsets.Length * sizeof(int));
            for (int i = 0; i < offsets.Length; i++) ptr[i] = offsets[i];
            return ptr;
        }

        public static void Free(void* ptr)
        {
            if (ptr != null) Marshal.FreeHGlobal((IntPtr)ptr);
        }

        private static void InitRange(EntityLocateB* ptr, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                ptr[i].ChunkMemory = null;
                ptr[i].ChunkOffsets = null;
                ptr[i].SlotInChunk = -1;
                ptr[i].Version = 0;
            }
        }
    }

    /// <summary>
    /// job-safe 的组件随机访问句柄（值语义，可作 job 字段被多线程只读共享）：跨 chunk 随机访问，
    /// 两次依赖加载加地址算术；带 archetype/chunk 缓存与版本校验的便利句柄是 <see cref="ComponentLookup{T}"/>。
    /// 组件地址 = <c>(byte*)locate.ChunkMemory + locate.ChunkOffsets[ComponentIndex] + locate.SlotInChunk * sizeof(T)</c>。
    /// </summary>
    public unsafe struct NativeComponentLookup<T> where T : unmanaged
    {
        /// <summary>定位表首址（索引 = 实体 Id）。</summary>
        public EntityLocateB* Locate;
        /// <summary>目标组件在该 Archetype 内的列下标（宿主用 <c>Archetype.GetComponentTypeIndex&lt;T&gt;()</c> 预解析）。</summary>
        public int ComponentIndex;
        /// <summary>定位表容量（实体 Id 上界）；越界 Id 一律视为不存在。</summary>
        public int Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool InRange(int entityId) => (uint)entityId < (uint)Length && Locate != null;

        /// <summary>该实体是否已分配（不校验版本）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAllocated(int entityId) => InRange(entityId) && Locate[entityId].IsAllocated;

        /// <summary>无校验解析（调用方保证实体有效）。热路径用。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T* UnsafeResolve(int entityId)
        {
            ref var e = ref Locate[entityId];
            return (T*)((byte*)e.ChunkMemory + e.ChunkOffsets[ComponentIndex] + e.SlotInChunk * sizeof(T));
        }

        /// <summary>解析并检查"已分配"。不校验版本（结构变更后 Id 可能被复用）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryResolve(int entityId, out T* ptr)
        {
            if (IsAllocated(entityId)) { ptr = UnsafeResolve(entityId); return true; }
            ptr = null;
            return false;
        }

        /// <summary>解析并校验版本（防悬垂/防读到复用后的新实体）。慢一档，用于调试与校验路径。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryResolveValidated(int entityId, int version, out T* ptr)
        {
            if (IsAllocated(entityId))
            {
                ref var e = ref Locate[entityId];
                if (e.Version == version) { ptr = UnsafeResolve(entityId); return true; }
            }
            ptr = null;
            return false;
        }
    }

    /// <summary>
    /// job-safe 的位置查询句柄（实体 → chunk 基址 / slotInChunk / version），
    /// 对齐 Unity <c>EntityStorageInfoLookup</c> 的用途（不含托管 Archetype 引用）。
    /// </summary>
    public unsafe struct NativeEntityLookup
    {
        public EntityLocateB* Locate;
        public int Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool InRange(int entityId) => (uint)entityId < (uint)Length && Locate != null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAllocated(int entityId) => InRange(entityId) && Locate[entityId].IsAllocated;

        /// <summary>取出该实体的 chunk 基址、槽位与版本；未分配返回 false。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(int entityId, out void* chunkMemory, out int slotInChunk, out int version)
        {
            if (IsAllocated(entityId))
            {
                ref var e = ref Locate[entityId];
                chunkMemory = e.ChunkMemory;
                slotInChunk = e.SlotInChunk;
                version = e.Version;
                return true;
            }
            chunkMemory = null;
            slotInChunk = -1;
            version = 0;
            return false;
        }
    }

    /// <summary>
    /// 静态解析原语，仅供托管 job 使用：诊断 NT004 只放行与被转译 job 同程序集的静态方法，
    /// 而 EntJoy.ECS 是被引用程序集，原生内核不能调用本类或句柄实例方法。
    /// 原生 job 在体内直接内联字段运算，<c>Stride</c> 由宿主以 int 字段传入。
    /// </summary>
    public static unsafe class NativeLookupOps
    {
        /// <summary>实体槽位是否已分配（越界一律 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAllocated<T>(NativeComponentLookup<T> lookup, int entityId) where T : unmanaged
            => (uint)entityId < (uint)lookup.Length && lookup.Locate != null && lookup.Locate[entityId].IsAllocated;

        /// <summary>无校验解析（调用方保证实体有效）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T* Resolve<T>(NativeComponentLookup<T> lookup, int entityId) where T : unmanaged
        {
            ref var e = ref lookup.Locate[entityId];
            return (T*)((byte*)e.ChunkMemory + e.ChunkOffsets[lookup.ComponentIndex] + e.SlotInChunk * sizeof(T));
        }

        /// <summary>解析并校验版本。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryResolveValidated<T>(NativeComponentLookup<T> lookup, int entityId, int version, out T* ptr)
            where T : unmanaged
        {
            if (IsAllocated(lookup, entityId) && lookup.Locate[entityId].Version == version)
            {
                ptr = Resolve(lookup, entityId);
                return true;
            }
            ptr = null;
            return false;
        }

        /// <summary>位置查询：取出 chunk 基址 / 槽位 / 版本。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetLocation(NativeEntityLookup lookup, int entityId,
            out void* chunkMemory, out int slotInChunk, out int version)
        {
            if ((uint)entityId < (uint)lookup.Length && lookup.Locate != null && lookup.Locate[entityId].IsAllocated)
            {
                ref var e = ref lookup.Locate[entityId];
                chunkMemory = e.ChunkMemory;
                slotInChunk = e.SlotInChunk;
                version = e.Version;
                return true;
            }
            chunkMemory = null;
            slotInChunk = -1;
            version = 0;
            return false;
        }
    }
}
