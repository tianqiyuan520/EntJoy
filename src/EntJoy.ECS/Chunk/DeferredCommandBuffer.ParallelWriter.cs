using System;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 单个并行 writer 的非托管状态。<c>internal</c>：只经 <see cref="ParallelWriter"/>（以 <c>void*</c> 持有）
    /// 间接使用，不对外暴露可变内部状态。
    /// </summary>
    internal unsafe struct EcbWriterState
    {
        public byte* Staging;
        public int Offset;
        public int Capacity;
        public Entity* Destroys;
        public int DestroyCount;
        public int DestroyCapacity;
        public bool DestroyRunOpen;
        public int DestroyRunStart;
        public int DestroyCountFieldOffset;
        /// <summary>首次使用该 writer 的托管线程 Id（0 = 未使用）。用于**契约守卫**：
        /// 一个 writer 同一时刻只能被一个线程使用 —— 违反时抛错，而不是静默写坏非托管内存
        /// （实测：把一个 job 切多 tile 分发到多 worker 时，共享 writer 会把堆写坏 0xC0000374）。</summary>
        public int OwnerThread;
        /// <summary>1 = **跨 tile 共享** writer（<c>CreateSharedParallelWriter</c>）：追加走原子占位、不扩容、
        /// 不做线程独占守卫（DOTS <c>EntityCommandBuffer.ParallelWriter</c> 语义）。0 = 单线程 writer。</summary>
        public int Shared;
    }

    /// <summary>
    /// 并行记录句柄（P1-9）：值语义，只含一个非托管指针 ⇒ **可直接作为 job 字段**。
    ///
    /// 与主线程记录（<see cref="DeferredCommandBuffer"/> 的 <c>lock(_sync)</c> 单实例）的区别：
    ///   - 每个 writer 拥有**自己的非托管 staging 区与自己的销毁表** ⇒ 记录期**完全无锁**；
    ///     约定：一个 writer 索引同一时刻只被一个 worker/lane 使用。
    ///   - 回放由主线程按 writer 索引**顺序**进行（确定性），见 <c>PlaybackParallel</c>。
    ///
    /// ⚠ 覆盖范围（诚实声明）：并行 writer 只支持**自包含**的两类命令 ——
    ///   <see cref="DestroyEntity"/>（进该 writer 的销毁表）与 <see cref="SetComponent{T}"/>（值内联在命令里）。
    ///   需要"共享类型集合池 / 本轮新建实体表"的 `CreateEntitiesRange` + `SetComponentRange` **仍限主线程**
    ///   （DOTS 的并行 create 也依赖专门的 placeholder 机制）。本次百万单位改造的实际用法正是：
    ///   主线程做生成/结构变更，并行 job 只做"标记死亡 + 写组件"。
    /// </summary>
    public unsafe struct ParallelWriter
    {
        /// <summary>内部状态指针（<c>EcbWriterState*</c>；以 <c>void*</c> 暴露，避免把实现细节变成公开类型）。</summary>
        public void* State;

        private bool Ready => State != null;

        /// <summary>记录一次销毁（同一 writer 内连续的销毁会在回放期合并成一次批量销毁）。
        /// 共享 writer（<see cref="DeferredCommandBuffer.CreateSharedParallelWriter"/>）下走**原子占位**：
        /// 每条命令 = 一个销毁槽（回放期把连续槽合并成批量销毁）。</summary>
        public void DestroyEntity(Entity entity)
        {
            if (!Ready) throw new InvalidOperationException("ParallelWriter 未初始化（请用 DeferredCommandBuffer.CreateParallelWriter 获取）。");
            ref var st = ref *(EcbWriterState*)State;
            if (st.Shared != 0)
            {
                st.DestroyEntityShared(entity);
                return;
            }
            st.ClaimThread();
            if (!st.DestroyRunOpen)
            {
                st.EnsureCapacity(sizeof(int) * 3);
                *(int*)(st.Staging + st.Offset) = DeferredCommandBuffer.OP_DESTROY_RANGE;
                st.Offset += sizeof(int);
                st.DestroyRunStart = st.DestroyCount;
                *(int*)(st.Staging + st.Offset) = st.DestroyRunStart;
                st.Offset += sizeof(int);
                st.DestroyCountFieldOffset = st.Offset;
                *(int*)(st.Staging + st.Offset) = 0;
                st.Offset += sizeof(int);
                st.DestroyRunOpen = true;
            }
            st.EnsureDestroyCapacity(st.DestroyCount + 1);
            st.Destroys[st.DestroyCount++] = entity;
            *(int*)(st.Staging + st.DestroyCountFieldOffset) = st.DestroyCount - st.DestroyRunStart;
        }

        /// <summary>记录一次单实体组件写入（值内联进命令 ⇒ 自包含、并行安全）。</summary>
        public void SetComponent<T>(Entity entity, T value) where T : unmanaged
        {
            if (!Ready) throw new InvalidOperationException("ParallelWriter 未初始化（请用 DeferredCommandBuffer.CreateParallelWriter 获取）。");
            ref var st = ref *(EcbWriterState*)State;
            if (st.Shared != 0)
            {
                st.SetComponentShared(entity, value);
                return;
            }
            st.ClaimThread();
            int elemSize = sizeof(T);
            st.EnsureCapacity(sizeof(int) + sizeof(Entity) + sizeof(int) * 2 + elemSize);
            *(int*)(st.Staging + st.Offset) = DeferredCommandBuffer.OP_SET_COMPONENT_SINGLE;
            st.Offset += sizeof(int);
            *(Entity*)(st.Staging + st.Offset) = entity;
            st.Offset += sizeof(Entity);
            *(int*)(st.Staging + st.Offset) = ComponentTypeManager.GetComponentType(typeof(T)).Id;
            st.Offset += sizeof(int);
            *(int*)(st.Staging + st.Offset) = elemSize;
            st.Offset += sizeof(int);
            *(T*)(st.Staging + st.Offset) = value;
            st.Offset += elemSize;
            // 销毁段需闭合：SetComponent 之后的 DestroyEntity 要另起一段（staging 里命令序 = 回放序）
            st.DestroyRunOpen = false;
        }
    }

    internal static unsafe class EcbWriterStateExtensions
    {
        /// <summary>
        /// **契约守卫**（P1-9）：一个 writer 同一时刻只能被一个线程使用。
        /// ⚠ 为什么必须有它：`job.Schedule(n, 0)`（自适应分批）会把**同一个 job 切成多个 tile 分发到多个 worker**，
        /// 若这些 tile 共享同一个 writer，两条线会同时改 `Offset`/`DestroyCount` 并交错写 staging ⇒
        /// 实测直接**堆损坏（0xC0000374）**。有了守卫，违反契约会得到明确异常而不是静默写坏内存。
        /// 正确做法：一个 writer = 一个 job = 一次单 tile 调度（batch 传整段），或每个 tile 各分配一个 writer。
        /// DOTS 里 `ParallelWriter` 之所以能跨 tile 共享，是因为它的追加走原子占位（本实现未做，见文档 TODO）。
        /// </summary>
        public static void ClaimThread(this ref EcbWriterState st)
        {
            int tid = Environment.CurrentManagedThreadId;
            if (st.OwnerThread == 0) { st.OwnerThread = tid; return; }
            if (st.OwnerThread != tid)
                throw new InvalidOperationException(
                    $"ParallelWriter 被多线程同时使用（首次线程 {st.OwnerThread}，当前 {tid}）："
                    + "一个 writer 只能被一个线程使用。请把 job 的 batch 设为整个范围（单 tile），或为每个 tile 分配独立 writer。");
        }

        public static void EnsureCapacity(this ref EcbWriterState st, int additionalBytes)
        {
            if (additionalBytes <= 0) return;
            int required = st.Offset + additionalBytes;
            if (required <= st.Capacity) return;
            int newCapacity = st.Capacity > 0 ? st.Capacity : 4096;
            while (newCapacity < required) newCapacity *= 2;
            var next = (byte*)Marshal.AllocHGlobal(newCapacity);
            if (st.Staging != null)
            {
                Buffer.MemoryCopy(st.Staging, next, newCapacity, st.Offset);
                Marshal.FreeHGlobal((IntPtr)st.Staging);
            }
            st.Staging = next;
            st.Capacity = newCapacity;
        }

        public static void EnsureDestroyCapacity(this ref EcbWriterState st, int needed)
        {
            if (needed <= st.DestroyCapacity) return;
            int newCapacity = st.DestroyCapacity > 0 ? st.DestroyCapacity : 256;
            while (newCapacity < needed) newCapacity *= 2;
            var next = (Entity*)Marshal.AllocHGlobal(newCapacity * sizeof(Entity));
            if (st.Destroys != null)
            {
                Buffer.MemoryCopy(st.Destroys, next, (long)newCapacity * sizeof(Entity), (long)st.DestroyCount * sizeof(Entity));
                Marshal.FreeHGlobal((IntPtr)st.Destroys);
            }
            st.Destroys = next;
            st.DestroyCapacity = newCapacity;
        }
        /// <summary>
        /// **原子占位追加**（跨 tile 共享 writer 的写入路径）：<c>Interlocked.Add</c> 抢一段 staging 空间，
        /// 抢到的空间只由本线程写 ⇒ 记录期无锁、无需线程独占。
        ///
        /// 为什么共享 writer **不能扩容**：扩容要搬移整块 staging 并换指针，多个 lane 同时在写时无法安全完成；
        /// 因此共享 writer 的容量必须在创建时给足，超限直接响亮失败（而不是静默写坏内存）。
        /// </summary>
        public static int ReserveShared(this ref EcbWriterState st, int bytes)
        {
            int start = System.Threading.Interlocked.Add(ref st.Offset, bytes) - bytes;
            if (start + bytes > st.Capacity)
                throw new InvalidOperationException(
                    $"共享 ParallelWriter staging 容量不足（需要 {start + bytes} B，容量 {st.Capacity} B）："
                    + "共享 writer 不支持扩容（扩容会破坏并发安全）⇒ 用 CreateSharedParallelWriter 预分配更大的容量。");
            return start;
        }

        /// <summary>共享 writer 的销毁记录：原子占一个销毁槽 + 一条「该槽、count=1」的批量销毁命令。
        /// 回放期把**连续槽**合并回一次批量销毁（见 <c>DeferredCommandBuffer.ReplayWriterBuffer</c>）。</summary>
        public static void DestroyEntityShared(this ref EcbWriterState st, Entity entity)
        {
            int slot = System.Threading.Interlocked.Increment(ref st.DestroyCount) - 1;
            if (slot >= st.DestroyCapacity)
                throw new InvalidOperationException(
                    $"共享 ParallelWriter 销毁表容量不足（需要 {slot + 1}，容量 {st.DestroyCapacity}）"
                    + "⇒ 用 CreateSharedParallelWriter 预分配更大的 destroyCapacity。");
            st.Destroys[slot] = entity;
            int start = st.ReserveShared(sizeof(int) * 3);
            *(int*)(st.Staging + start) = DeferredCommandBuffer.OP_DESTROY_RANGE;
            *(int*)(st.Staging + start + sizeof(int)) = slot;
            *(int*)(st.Staging + start + sizeof(int) * 2) = 1;
        }

        /// <summary>共享 writer 的组件写入：值内联进命令（自包含）⇒ 只需原子占位，命令内不含共享状态。</summary>
        public static void SetComponentShared<T>(this ref EcbWriterState st, Entity entity, T value) where T : unmanaged
        {
            int elemSize = sizeof(T);
            int size = sizeof(int) + sizeof(Entity) + sizeof(int) * 2 + elemSize;
            int start = st.ReserveShared(size);
            byte* p = st.Staging + start;
            *(int*)p = DeferredCommandBuffer.OP_SET_COMPONENT_SINGLE;
            p += sizeof(int);
            *(Entity*)p = entity;
            p += sizeof(Entity);
            *(int*)p = ComponentTypeManager.GetComponentType(typeof(T)).Id;
            p += sizeof(int);
            *(int*)p = elemSize;
            p += sizeof(int);
            *(T*)p = value;
        }
    }
}
