using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// blittable 实体定位条目：**原生内核与 job 可直接读**的"实体 → 组件列"定位信息。
    ///
    /// 设计要点（与托管 <see cref="EntityIndexInWorld"/> 的关键区别）：
    ///   1. 只有裸指针与整数，**无托管引用** ⇒ 可进 NativeTranspile 生成的 C++ 内核；
    ///   2. 每个实体携带**自己的 chunk 基址与所属 Archetype 的列偏移表**（而不是 chunk 目录下标）
    ///      ⇒ chunk 的空闲/压缩/swap-back（<c>Archetype.Remove</c> 的空 chunk 回收）不会让表项失效，
    ///      因为表项与托管位置表在**同一处**（<c>EntityManager.UpdateEntityLocation</c> /
    ///      <c>RefreshChunkEntityIndices</c>）被一起更新；
    ///   3. 无内部可变缓存 ⇒ 可按值传给并行 job 并被多线程只读共享（对齐 Unity DOTS 的
    ///      <c>ComponentLookup&lt;T&gt;</c> job 用法）；<see cref="ComponentLookup{T}"/> 那份
    ///      带 single-archetype 缓存、自述 main-thread only，两者用途不同。
    ///
    /// 布局（24B，<c>LayoutKind.Sequential</c>，与 <c>src/NativeDll/NativeEntityLookup.h</c> 一一对应）：
    ///   +0   void* ChunkMemory   chunk 数据块首址（null = 未分配 / 已销毁）
    ///   +8   int*  ChunkOffsets  该 Archetype 的"组件列字节偏移"非托管镜像，按 componentIndex 索引
    ///   +16  int   SlotInChunk
    ///   +20  int   Version
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
    /// job-safe 的**组件随机访问句柄**（值语义，可直接作为 job 字段，可被多线程只读共享）。
    ///
    /// 用法：宿主在主线程构造（<c>EntityManager.CreateNativeLookup&lt;T&gt;(archetype)</c>），
    /// 作为 job 字段传入；job 内 <c>lookup.UnsafeResolve(entityId)</c> 得到 <c>T*</c>：
    /// <code>
    /// T* p = (T*)((byte*)locate.ChunkMemory + locate.ChunkOffsets[ComponentIndex] + locate.SlotInChunk * sizeof(T));
    /// </code>
    ///
    /// ⚠ 与 <see cref="ComponentLookup{T}"/> 的分工：
    ///   - 本结构：跨 chunk 随机访问、**并行 job 与原生内核可用**（2 次依赖加载 + 地址算术）；
    ///   - <see cref="ComponentLookup{T}"/>：主线程便利句柄，带 archetype/chunk 缓存与版本校验。
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
    /// job-safe 的**位置查询句柄**（实体 → chunk 基址 / slotInChunk / version），
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
    /// **静态**解析原语 —— 供**托管** job 使用。
    ///
    /// ⚠⚠ 2026-09-13 实测（P0-3b）：`[NativeTranspile]` 原生内核**不能**调用本类，也不能调用句柄上的实例方法。
    /// 诊断 **NT004** 的确切行为（实测，两种写法都报）：
    ///   - 实例方法：<c>cannot call 'NativeComponentLookup&lt;T&gt;.UnsafeResolve(int)' … or it is not a static method in the same assembly</c>
    ///   - **跨程序集静态方法**：<c>cannot call 'NativeLookupOps.IsAllocated&lt;T&gt;(NativeComponentLookup&lt;T&gt;, int)' …</c>
    /// ⇒ 规则是「**调用目标必须与被转译的 job 在同一个程序集**」。EntJoy.ECS 是被引用程序集，
    ///   所以框架只能提供**数据结构**，不能提供可调用助手。
    ///
    /// **原生 job 的正确写法 = 在 job 体内直接内联字段运算**（不调用任何方法、不访问任何属性）：
    /// <code>
    /// ref EntityLocateB e = ref Lookup.Locate[id];
    /// if (e.ChunkMemory == null || e.SlotInChunk &lt; 0) continue;
    /// PPos* p = (PPos*)((byte*)e.ChunkMemory + e.ChunkOffsets[Lookup.ComponentIndex] + e.SlotInChunk * Stride);
    /// </code>
    /// （`Stride` 由宿主以 int 字段传入；见 tools/EntityLookupNativeProbe/LookupConsumeJob.cs 的实测样例。）
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
