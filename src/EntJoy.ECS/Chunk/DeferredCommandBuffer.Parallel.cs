using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EntJoy.Collections;

namespace EntJoy.ECS
{
    public unsafe partial class DeferredCommandBuffer : IDisposable
    {
        /// <summary>
        /// 预分配 <paramref name="count"/> 个并行 writer（幂等）。必须在任何 <see cref="CreateParallelWriter"/>
        /// 之前、且在任何 job 开始记录之前由主线程调用。
        /// </summary>
        public void EnsureParallelWriters(int count)
        {
            if (count <= 0 || count <= _writerCount) return;
            if (_playingBack) throw new InvalidOperationException("Cannot move ParallelWriter storage during playback.");
            int old = _writerCount;
            var next = (EcbWriterState*)Marshal.AllocHGlobal(CheckedSize((long)count * sizeof(EcbWriterState)));
            if (_writers != null)
            {
                Buffer.MemoryCopy(_writers, next, (long)count * sizeof(EcbWriterState), (long)old * sizeof(EcbWriterState));
                Marshal.FreeHGlobal((IntPtr)_writers);
            }
            for (int i = old; i < count; i++) next[i] = default;
            _writers = next;
            _writerCount = count;
        }

        /// <summary>取第 <paramref name="index"/> 个并行 writer（值语义，可作 job 字段）。</summary>
        public ParallelWriter CreateParallelWriter(int index)
        {
            if (_writers == null || index < 0 || index >= _writerCount)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"并行 writer 索引 {index} 超出范围（已分配 {_writerCount} 个；先调用 EnsureParallelWriters）。");
            return new ParallelWriter { State = _writers + index };
        }

        /// <summary>
        /// 取第 <paramref name="index"/> 个跨 tile 共享 writer：同一个 job 切成多个 tile 分发到多个 worker 时可共享，
        /// 预留在短临界区内完成，不需要「一个 writer 一个线程」的约定。
        /// 记录期允许并发（无 <c>ClaimThread</c> 守卫），因此 staging/销毁表容量必须一次给足，超限抛异常。
        /// </summary>
        public ParallelWriter CreateSharedParallelWriter(int index, int stagingCapacityBytes, int destroyCapacity)
        {
            if (_writers == null || index < 0 || index >= _writerCount)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"并行 writer 索引 {index} 超出范围（已分配 {_writerCount} 个；先调用 EnsureParallelWriters）。");
            if (stagingCapacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(stagingCapacityBytes));
            if (destroyCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(destroyCapacity));
            ref var st = ref _writers[index];
            if (st.Staging != null || st.Destroys != null)
                throw new InvalidOperationException(
                    $"并行 writer {index} 已被占用（共享 writer 需在每轮记录前一次性分配，PlaybackParallel 只清空内容、不释放缓冲）。");
            int destroyBytes = CheckedSize((long)destroyCapacity * sizeof(Entity));
            var staging = (byte*)Marshal.AllocHGlobal(stagingCapacityBytes);
            Entity* destroys;
            try { destroys = (Entity*)Marshal.AllocHGlobal(destroyBytes); }
            catch { Marshal.FreeHGlobal((IntPtr)staging); throw; }
            st.Staging = staging;
            st.Capacity = stagingCapacityBytes;
            st.Offset = 0;
            st.Destroys = destroys;
            st.DestroyCapacity = destroyCapacity;
            st.DestroyCount = 0;
            st.Shared = 1;
            return new ParallelWriter { State = _writers + index };
        }

        /// <summary>回放并消费当前 writer 批次。关系命令按 sortKey 排序，普通命令的 key 为 0。</summary>
        public void PlaybackParallel(EntityManager entityManager)
        {
            if (_playingBack) throw new InvalidOperationException("ECB playback cannot be nested.");
            entityManager.CompleteActiveJobs();
            lock (_sync) PlaybackWriterBatch(entityManager);
        }
        /// <summary>回放仅含普通命令的 writer，保留连续销毁段合并。
        /// <c>OP_DESTROY_RANGE</c> 在连续销毁槽上合并（共享 writer 里每条销毁命令只带一个槽），
        /// 且遇到非销毁命令前先落地，保证命令序不被重排。</summary>
        private void ReplayWriterBuffer(EntityManager entityManager, ref EcbWriterState st, ref int completed)
        {
            int offset = 0;
            int runStart = 0, runCount = 0, pendingCommands = 0;
            while (offset + sizeof(int) <= st.Offset)
            {
                int opCode = *(int*)(st.Staging + offset);
                offset += sizeof(int);

                if (opCode == OP_DESTROY_RANGE)
                {
                    AbsorbDestroyRange(st, ref offset, ref runStart, ref runCount, ref pendingCommands,
                        entityManager, ref completed);
                    continue;
                }

                FlushDestroyRun(entityManager, st, ref runStart, ref runCount, ref pendingCommands, ref completed);
                if (opCode != OP_SET_COMPONENT_SINGLE)
                    throw new InvalidOperationException($"普通 writer 回放路径中出现不支持的 opcode {opCode}。");
                ReplaySetComponentSingle(entityManager, st, ref offset, ref completed);
            }

            FlushDestroyRun(entityManager, st, ref runStart, ref runCount, ref pendingCommands, ref completed);
        }

        // 连续销毁槽合并成一段；与前一段不连续时先把前一段落地。
        private static void AbsorbDestroyRange(in EcbWriterState st, ref int offset, ref int runStart, ref int runCount,
            ref int pendingCommands, EntityManager entityManager, ref int completed)
        {
            int startIndex = *(int*)(st.Staging + offset);
            offset += sizeof(int);
            int count = *(int*)(st.Staging + offset);
            offset += sizeof(int);
            if (count <= 0)
            {
                completed++;
                return;
            }
            if (runCount > 0 && runStart + runCount == startIndex)
            {
                runCount += count;
            }
            else
            {
                FlushDestroyRun(entityManager, st, ref runStart, ref runCount, ref pendingCommands, ref completed);
                runStart = startIndex;
                runCount = count;
            }
            pendingCommands++;
        }

        // 关系列必须走关系 API；普通列直接写目标指针。
        private static void ReplaySetComponentSingle(EntityManager entityManager, in EcbWriterState st, ref int offset, ref int completed)
        {
            Entity entity = *(Entity*)(st.Staging + offset);
            offset += sizeof(Entity);
            int typeId = *(int*)(st.Staging + offset);
            offset += sizeof(int);
            int elemSize = *(int*)(st.Staging + offset);
            offset += sizeof(int);
            byte* payload = st.Staging + offset;
            if (new ComponentType(typeId).IsRelation)
                entityManager.WriteComponentRange(typeId, elemSize, &entity, payload, 1);
            else if (entityManager.TryGetComponentPointer(entity, typeId, elemSize, out void* dst))
                Buffer.MemoryCopy(payload, dst, elemSize, elemSize);
            offset += elemSize;
            completed++;
        }

        // 落地累积的连续销毁段；计数按命令数而非实体数，一条命令可能覆盖多个槽。
        private static void FlushDestroyRun(EntityManager entityManager, in EcbWriterState st,
            ref int runStart, ref int runCount, ref int pendingCommands, ref int completed)
        {
            if (runCount == 0) return;
            entityManager.DestroyEntities(st.Destroys + runStart, runCount);
            completed += pendingCommands;
            pendingCommands = 0;
            runCount = 0;
        }

        private void DisposeParallelWriters()
        {
            if (_writers == null) return;
            for (int i = 0; i < _writerCount; i++)
            {
                ref var st = ref _writers[i];
                if (st.Staging != null) Marshal.FreeHGlobal((IntPtr)st.Staging);
                if (st.Destroys != null) Marshal.FreeHGlobal((IntPtr)st.Destroys);
                st.Staging = null;
                st.Destroys = null;
            }
            Marshal.FreeHGlobal((IntPtr)_writers);
            _writers = null;
            _writerCount = 0;
        }
    }
}
