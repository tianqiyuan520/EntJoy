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
        /// <summary>首次使用该 writer 的托管线程 Id（0 = 未使用）。用于契约守卫：
        /// 一个 writer 同一时刻只能被一个线程使用 —— 违反时抛错，而不是静默写坏非托管内存
        /// （实测：把一个 job 切多 tile 分发到多 worker 时，共享 writer 会把堆写坏 0xC0000374）。</summary>
        public int OwnerThread;
        /// <summary>1 = 跨 tile 共享 writer（<c>CreateSharedParallelWriter</c>）：预留区短临界区、不扩容、
        /// 不做线程独占守卫（DOTS <c>EntityCommandBuffer.ParallelWriter</c> 语义）。0 = 单线程 writer。</summary>
        public int Shared;
        public int ReservationGate;
        public bool HasRelationshipCommands;
    }

    /// <summary>
    /// 非托管记录句柄，可作为 Job 字段。支持销毁、组件写入及单目标关系设置/解除。
    /// 普通 writer 线程独占；共享 writer 在短临界区预留空间后独立写入。
    /// 关系命令按 sortKey 回放，普通命令的 key 为 0；创建与批次写入由主线程 ECB 记录。
    /// </summary>
    public unsafe struct ParallelWriter
    {
        /// <summary>内部状态指针（<c>EcbWriterState*</c>；以 <c>void*</c> 暴露，避免把实现细节变成公开类型）。</summary>
        public void* State;

        private bool Ready => State != null;

        /// <summary>按 sortKey 回放；相同 key 的跨 writer 冲突不承诺胜者。普通命令的 key 为 0。</summary>
        public void SetRelationshipTarget<TRel>(int sortKey, Entity source, Entity target) where TRel : struct, IRelationComponent
            => RecordRelationship(DeferredCommandBuffer.OP_SET_RELATIONSHIP_TARGET,
                DeferredCommandBuffer.SingleRelationTypeId<TRel>(), sortKey, source, target);

        public void RemoveRelationship<TRel>(int sortKey, Entity source) where TRel : struct, IRelationComponent
            => RecordRelationship(DeferredCommandBuffer.OP_REMOVE_RELATIONSHIP,
                DeferredCommandBuffer.SingleRelationTypeId<TRel>(), sortKey, source, default);

        private void RecordRelationship(int opcode, int typeId, int sortKey, Entity source, Entity target)
        {
            if (!Ready) throw new InvalidOperationException("ParallelWriter is not initialized.");
            ref var state = ref *(EcbWriterState*)State;
            int offset;
            if (state.Shared != 0) offset = state.ReserveShared(DeferredCommandBuffer.RelationshipCommandSize);
            else
            {
                state.ClaimThread();
                state.EnsureCapacity(DeferredCommandBuffer.RelationshipCommandSize);
                offset = state.Offset;
                state.Offset += DeferredCommandBuffer.RelationshipCommandSize;
            }
            DeferredCommandBuffer.WriteRelationship(state.Staging + offset, opcode, sortKey, typeId, source, target);
            state.HasRelationshipCommands = true;
            state.DestroyRunOpen = false;
        }

        /// <summary>记录一次销毁（同一 writer 内连续的销毁会在回放期合并成一次批量销毁）。
        /// 共享 writer（<see cref="DeferredCommandBuffer.CreateSharedParallelWriter"/>）下统一预留命令和销毁槽位：
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
            st.EnsureDestroyCapacity(checked(st.DestroyCount + 1));
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
            int typeId = ComponentTypeManager.GetComponentType(typeof(T)).Id;
            st.EnsureCapacity(checked(sizeof(int) + sizeof(Entity) + sizeof(int) * 2 + elemSize));
            *(int*)(st.Staging + st.Offset) = DeferredCommandBuffer.OP_SET_COMPONENT_SINGLE;
            st.Offset += sizeof(int);
            *(Entity*)(st.Staging + st.Offset) = entity;
            st.Offset += sizeof(Entity);
            *(int*)(st.Staging + st.Offset) = typeId;
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
        /// <summary>普通 writer 每轮绑定一个线程；共享记录使用 CreateSharedParallelWriter。</summary>
        public static void ClaimThread(this ref EcbWriterState st)
        {
            int tid = Environment.CurrentManagedThreadId;
            int owner = System.Threading.Interlocked.CompareExchange(ref st.OwnerThread, tid, 0);
            if (owner != 0 && owner != tid)
                throw new InvalidOperationException(
                    $"ParallelWriter 被多线程同时使用（首次线程 {owner}，当前 {tid}）："
                    + "一个 writer 只能被一个线程使用。请把 job 的 batch 设为整个范围（单 tile），或为每个 tile 分配独立 writer。");
        }

        public static void EnsureCapacity(this ref EcbWriterState st, int additionalBytes)
        {
            if (additionalBytes < 0) throw new ArgumentOutOfRangeException(nameof(additionalBytes));
            if (st.Offset < 0 || st.Capacity < 0 || st.Offset > st.Capacity) throw new InvalidOperationException("Invalid writer byte cursor.");
            int required = checked(st.Offset + additionalBytes);
            if (required <= st.Capacity && st.Staging != null) return;
            int newCapacity = st.Capacity > 0 ? st.Capacity : 4096;
            while (newCapacity < required) newCapacity = newCapacity > int.MaxValue / 2 ? required : newCapacity * 2;
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
            if (needed < 0) throw new ArgumentOutOfRangeException(nameof(needed));
            if (needed <= st.DestroyCapacity && st.Destroys != null) return;
            int newCapacity = st.DestroyCapacity > 0 ? st.DestroyCapacity : 256;
            while (newCapacity < needed) newCapacity = newCapacity > int.MaxValue / 2 ? needed : newCapacity * 2;
            var next = (Entity*)Marshal.AllocHGlobal(checked(newCapacity * sizeof(Entity)));
            if (st.Destroys != null)
            {
                Buffer.MemoryCopy(st.Destroys, next, (long)newCapacity * sizeof(Entity), (long)st.DestroyCount * sizeof(Entity));
                Marshal.FreeHGlobal((IntPtr)st.Destroys);
            }
            st.Destroys = next;
            st.DestroyCapacity = newCapacity;
        }
        // Only reservation counters share this gate. Payload writes target disjoint
        // ranges and playback starts after every producer has completed.
        private static void EnterReservation(ref EcbWriterState st)
        {
            var spin = new System.Threading.SpinWait();
            while (System.Threading.Interlocked.CompareExchange(ref st.ReservationGate, 1, 0) != 0) spin.SpinOnce();
        }

        private static void ExitReservation(ref EcbWriterState st)
            => System.Threading.Volatile.Write(ref st.ReservationGate, 0);

        private static int ReserveBytesLocked(ref EcbWriterState st, int bytes)
        {
            int current = st.Offset;
            if (bytes <= 0 || current < 0 || current > st.Capacity || bytes > st.Capacity - current)
                throw new InvalidOperationException("Shared ParallelWriter staging capacity exceeded.");
            if (st.Staging == null) st.Staging = (byte*)Marshal.AllocHGlobal(st.Capacity);
            st.Offset = current + bytes;
            return current;
        }

        public static int ReserveShared(this ref EcbWriterState st, int bytes)
        {
            EnterReservation(ref st);
            try { return ReserveBytesLocked(ref st, bytes); }
            finally { ExitReservation(ref st); }
        }

        public static void DestroyEntityShared(this ref EcbWriterState st, Entity entity)
        {
            int slot, start;
            EnterReservation(ref st);
            try
            {
                if (st.DestroyCount < 0 || st.DestroyCount >= st.DestroyCapacity)
                    throw new InvalidOperationException("Shared ParallelWriter destroy capacity exceeded.");
                if (st.Destroys == null) st.Destroys = (Entity*)Marshal.AllocHGlobal(checked(st.DestroyCapacity * sizeof(Entity)));
                // Reserve both resources together. Failure consumes neither cursor.
                start = ReserveBytesLocked(ref st, sizeof(int) * 3);
                slot = st.DestroyCount++;
            }
            finally { ExitReservation(ref st); }
            st.Destroys[slot] = entity;
            *(int*)(st.Staging + start) = DeferredCommandBuffer.OP_DESTROY_RANGE;
            *(int*)(st.Staging + start + sizeof(int)) = slot;
            *(int*)(st.Staging + start + sizeof(int) * 2) = 1;
        }

        /// <summary>共享 writer 的组件写入：值内联进命令，预留范围后由该生产者独立写入。</summary>
        public static void SetComponentShared<T>(this ref EcbWriterState st, Entity entity, T value) where T : unmanaged
        {
            int elemSize = sizeof(T);
            int size = checked(sizeof(int) + sizeof(Entity) + sizeof(int) * 2 + elemSize);
            int typeId = ComponentTypeManager.GetComponentType(typeof(T)).Id;
            int start = st.ReserveShared(size);
            byte* p = st.Staging + start;
            *(int*)p = DeferredCommandBuffer.OP_SET_COMPONENT_SINGLE;
            p += sizeof(int);
            *(Entity*)p = entity;
            p += sizeof(Entity);
            *(int*)p = typeId;
            p += sizeof(int);
            *(int*)p = elemSize;
            p += sizeof(int);
            *(T*)p = value;
        }
    }
}
