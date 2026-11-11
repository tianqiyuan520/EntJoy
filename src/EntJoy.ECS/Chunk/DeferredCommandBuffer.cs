using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EntJoy.Collections;

namespace EntJoy.ECS
{
    /// <summary>
    /// 手动延迟命令缓冲区（ECB）：在 Job 或主线程记录结构变更命令，帧末在主线程统一 Playback。
    /// Playback 走 EntityManager 的主入口（NewEntity/DestroyEntity/AddComponentRaw/RemoveComponentRaw），
    /// 因此 observer 派发天然生效，无需额外扩展。
    /// </summary>
    public unsafe partial class DeferredCommandBuffer : IDisposable
    {
        private byte* _staging;
        private int _stagingOffset;
        private int _stagingCapacity;
        private bool _disposed;
        private readonly object _sync = new();   // 记录/回放锁：多 worker 并发写同一 ECB 时保证不覆盖/交错

        private const int InitialCapacity = 64 * 1024;

        // 主线程缓冲与 ParallelWriter 共用的记录协议。
        internal const int OP_CREATE_ENTITIES_RANGE = 1;  // 批量创建：[op][typeSetIndex][count][batchId]
        internal const int OP_DESTROY_RANGE = 2;          // 批量销毁（记录期成段）：[op][startIndex][count]
        private const int OP_ADD_COMPONENT = 3;
        private const int OP_REMOVE_COMPONENT = 4;
        private const int OP_SET_COMPONENT_RANGE = 5;     // 批量写组件列（零反射）：
                                                          // [op][batchId][typeId][elemSize][elemCount][payload]
        internal const int OP_SET_COMPONENT_SINGLE = 6;   // 单实体写组件（ParallelWriter 用）：
                                                          // [op][entity][typeId][elemSize][payload]

        /// <summary>组件类型集合池：记录期存下 <c>ComponentType[]</c>，回放期按下标取用
        /// ， 回放期不再 per-command <c>new ComponentType[]</c>（去分配关键）。</summary>
        private List<ComponentType[]> _typeSets = new();

        /// <summary>记录期分配的 batch 计数（回放期用同样的序 push 到 <see cref="_batchStarts"/>）。</summary>
        private int _recordedBatchCount;

        /// <summary>回放期产出：每个 create-range 的实体在 <see cref="_createdEntities"/> 中的起止。</summary>
        private readonly List<int> _batchStarts = new();
        private readonly List<int> _batchCounts = new();

        /// <summary>回放期产出：所有被创建实体的非托管表（零托管分配）。</summary>
        private Entity* _createdEntities;
        private int _createdEntityCapacity;
        private int _createdEntityCount;

        /// <summary>销毁合并用的非托管实体表：记录期 append，回放期一次 <c>DestroyEntities</c>。</summary>
        private Entity* _destroyEntities;
        private int _destroyEntityCapacity;
        private int _destroyEntityCount;
        private bool _destroyRunOpen;
        private int _destroyRunStart;
        private int _destroyCountFieldOffset;

        /// <summary>关闭当前 destroy 连续段（任何非 destroy 命令都要调用）。</summary>
        private void CloseDestroyRun() => _destroyRunOpen = false;

        // 普通 writer 独占缓冲；共享 writer 统一预留空间后独立写入。
        // 状态类型 EcbWriterState / 句柄类型 ParallelWriter 定义在 DeferredCommandBuffer.ParallelWriter.cs
        private EcbWriterState* _writers;
        private int _writerCount;

        /// <summary>已分配的并行 writer 数量（见 <see cref="EnsureParallelWriters"/>）。</summary>
        public int ParallelWriterCount => _writerCount;


        private unsafe void EnsureDestroyCapacity(int needed)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (needed <= _destroyEntityCapacity) return;
            int newCapacity = _destroyEntityCapacity > 0 ? _destroyEntityCapacity : 4096;
            while (newCapacity < needed)
            {
                if (newCapacity > int.MaxValue / 2) throw new OverflowException("ECB entity capacity exceeds Int32.MaxValue.");
                newCapacity *= 2;
            }
            var next = (Entity*)Marshal.AllocHGlobal(CheckedSize((long)newCapacity * sizeof(Entity)));
            if (_destroyEntities != null)
            {
                Buffer.MemoryCopy(_destroyEntities, next, (long)newCapacity * sizeof(Entity), (long)_destroyEntityCount * sizeof(Entity));
                Marshal.FreeHGlobal((IntPtr)_destroyEntities);
            }
            _destroyEntities = next;
            _destroyEntityCapacity = newCapacity;
        }

        public int CommandCount { get; private set; }

        /// <summary>本轮回放创建的实体表首址（长度 = <see cref="CreatedEntityCount"/>）。</summary>
        public unsafe Entity* CreatedEntities => _createdEntities;

        /// <summary>本轮回放创建的实体总数。</summary>
        public int CreatedEntityCount => _createdEntityCount;

        /// <summary>已记录的 <see cref="DestroyEntity"/> 实体条数。
        /// 与 <see cref="CommandCount"/> 不同：连续的 destroy 会合并成一个连续段 = 一条命令
        /// （见 <see cref="DestroyEntity"/>），故 3200 次并发 `DestroyEntity` 时
        /// `DestroyedEntityCount = 3200` 而 `CommandCount = 1`。要断言"写得没丢"用本属性。</summary>
        public int DestroyedEntityCount => _destroyEntityCount;

        public DeferredCommandBuffer()
        {
            _staging = (byte*)Marshal.AllocHGlobal(InitialCapacity);
            _stagingCapacity = InitialCapacity;
            _stagingOffset = 0;
            CommandCount = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacity(int additionalBytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (additionalBytes < 0 || additionalBytes > int.MaxValue - _stagingOffset)
                throw new OverflowException("DeferredCommandBuffer size exceeds Int32.MaxValue.");
            int required = _stagingOffset + additionalBytes;
            if (required > _stagingCapacity)
            {
                int newCapacity = _stagingCapacity > 0 ? _stagingCapacity : InitialCapacity;
                while (newCapacity < required)
                {
                    if (newCapacity > int.MaxValue / 2)
                        throw new OverflowException("DeferredCommandBuffer size exceeds Int32.MaxValue.");
                    newCapacity *= 2;
                }
                var newStaging = (byte*)Marshal.AllocHGlobal(newCapacity);
                if (_staging != null)
                {
                    Buffer.MemoryCopy(_staging, newStaging, newCapacity, _stagingOffset);
                    Marshal.FreeHGlobal((IntPtr)_staging);
                }
                _staging = newStaging;
                _stagingCapacity = newCapacity;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CheckedSize(long size)
        {
            if (size < 0 || size > int.MaxValue)
                throw new OverflowException("DeferredCommandBuffer size exceeds Int32.MaxValue.");
            return (int)size;
        }

        /// <summary>
        /// 主线程回放所有命令。无需注册，直接调用 EntityManager 的非泛型方法。
        /// </summary>
        public unsafe void Playback(EntityManager entityManager)
        {
            if (_playingBack) throw new InvalidOperationException("ECB playback cannot be nested.");
            entityManager.CompleteActiveJobs();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_playingBack) throw new InvalidOperationException("ECB playback cannot be nested.");

                PlaybackBatch batch = DetachBatch();
                _playingBack = true;
                try
                {
                    _batchStarts.Clear();
                    _batchCounts.Clear();
                    _createdEntityCount = 0;
                    ReplayAll(entityManager, batch);
                }
                finally
                {
                    ReturnBatchBuffers(batch);
                }
            }
        }

        // 本批要回放的原始字节与随附数据；取走时缓冲从实例上摘下，避免回放期间被复用。
        private readonly struct PlaybackBatch
        {
            public readonly byte* Staging;
            public readonly int Length;
            public readonly int Capacity;
            public readonly Entity* Destroys;
            public readonly int DestroyCapacity;
            public readonly List<ComponentType[]> TypeSets;
            public readonly int CommandTotal;

            public PlaybackBatch(byte* staging, int length, int capacity, Entity* destroys, int destroyCapacity,
                List<ComponentType[]> typeSets, int commandTotal)
            {
                Staging = staging;
                Length = length;
                Capacity = capacity;
                Destroys = destroys;
                DestroyCapacity = destroyCapacity;
                TypeSets = typeSets;
                CommandTotal = commandTotal;
            }
        }

        private unsafe PlaybackBatch DetachBatch()
        {
            var batch = new PlaybackBatch(_staging, _stagingOffset, _stagingCapacity,
                _destroyEntities, _destroyEntityCapacity, _typeSets, CommandCount);

            _typeSets = _spareTypeSets;
            _spareTypeSets = null;
            _staging = null;
            _stagingOffset = _stagingCapacity = 0;
            _destroyEntities = null;
            _destroyEntityCount = _destroyEntityCapacity = 0;
            _destroyRunOpen = false;
            _recordedBatchCount = 0;
            CommandCount = 0;
            return batch;
        }

        // 回放结束后把缓冲归还实例；整批命令都消费完（offset 归零）才保留，否则直接释放。
        private unsafe void ReturnBatchBuffers(PlaybackBatch batch)
        {
            if (_stagingOffset == 0)
            {
                if (_staging != null) Marshal.FreeHGlobal((IntPtr)_staging);
                _staging = batch.Staging;
                _stagingCapacity = batch.Capacity;
            }
            else if (batch.Staging != null) Marshal.FreeHGlobal((IntPtr)batch.Staging);

            if (_destroyEntityCount == 0)
            {
                if (_destroyEntities != null) Marshal.FreeHGlobal((IntPtr)_destroyEntities);
                _destroyEntities = batch.Destroys;
                _destroyEntityCapacity = batch.DestroyCapacity;
            }
            else if (batch.Destroys != null) Marshal.FreeHGlobal((IntPtr)batch.Destroys);

            batch.TypeSets.Clear();
            _spareTypeSets = batch.TypeSets;
            _playingBack = false;
        }

        private unsafe void ReplayAll(EntityManager entityManager, in PlaybackBatch batch)
        {
            byte* staging = batch.Staging;
            int completed = 0;
            int offset = 0;
            try
            {
                while (offset < batch.Length)
                {
                    int opCode = *(int*)(staging + offset);
                    offset += sizeof(int);
                    offset = opCode switch
                    {
                        OP_SET_RELATIONSHIP_TARGET or OP_REMOVE_RELATIONSHIP or OP_ADD_MULTI_RELATIONSHIP
                            or OP_REMOVE_MULTI_RELATIONSHIP or OP_CLEAR_MULTI_RELATIONSHIPS
                            => ReplayRelationshipCommand(entityManager, staging, offset),
                        OP_CREATE_ENTITIES_RANGE => ReplayCreateEntities(entityManager, batch, staging, offset),
                        OP_SET_COMPONENT_RANGE => ReplaySetComponentRange(entityManager, batch, staging, offset),
                        OP_DESTROY_RANGE => ReplayDestroyRange(entityManager, batch, staging, offset),
                        OP_ADD_COMPONENT => ReplayAddComponent(entityManager, staging, offset),
                        OP_REMOVE_COMPONENT => ReplayRemoveComponent(entityManager, staging, offset),
                        _ => throw new InvalidOperationException("Invalid ECB command opcode.")
                    };
                    completed++;
                }
            }
            catch (Exception cause)
            {
                throw new CommandPlaybackException(completed, Math.Max(0, batch.CommandTotal - completed - 1), -1, cause);
            }
        }

        // 各分支都接收 opcode 之后的偏移，返回下一条命令的偏移。
        private static unsafe int ReplayRelationshipCommand(EntityManager entityManager, byte* staging, int offset)
        {
            ReplayRelationship(entityManager, staging + offset - sizeof(int));
            return offset + RelationshipCommandSize - sizeof(int);
        }

        private unsafe int ReplayCreateEntities(EntityManager entityManager, in PlaybackBatch batch, byte* staging, int offset)
        {
            int typeSetIndex = *(int*)(staging + offset);
            offset += sizeof(int);
            int count = *(int*)(staging + offset);
            offset += sizeof(int);
            offset += sizeof(int);   // batchId：批次下标由推入顺序决定，此处不用
            EnsureCreatedEntityCapacity(checked(_createdEntityCount + count));
            int created = entityManager.CreateEntities(count, batch.TypeSets[typeSetIndex],
                _createdEntities + _createdEntityCount, _createdEntityCapacity - _createdEntityCount);
            _batchStarts.Add(_createdEntityCount);
            _batchCounts.Add(created);
            _createdEntityCount += created;
            return offset;
        }

        private unsafe int ReplaySetComponentRange(EntityManager entityManager, in PlaybackBatch batch, byte* staging, int offset)
        {
            int batchId = *(int*)(staging + offset);
            offset += sizeof(int);
            int typeId = *(int*)(staging + offset);
            offset += sizeof(int);
            int elemSize = *(int*)(staging + offset);
            offset += sizeof(int);
            int elemCount = *(int*)(staging + offset);
            offset += sizeof(int);
            byte* payload = staging + offset;
            if (elemSize > 0 && elemCount > 0
                && batchId >= 0 && batchId < _batchStarts.Count && batchId < _batchCounts.Count)
            {
                int start = _batchStarts[batchId];
                int have = _batchCounts[batchId];
                int n = elemCount < have ? elemCount : have;
                if (n > 0)
                    entityManager.WriteComponentRange(typeId, elemSize, _createdEntities + start, payload, n);
            }
            return offset + elemCount * elemSize;
        }

        private static unsafe int ReplayDestroyRange(EntityManager entityManager, in PlaybackBatch batch, byte* staging, int offset)
        {
            int startIndex = *(int*)(staging + offset);
            offset += sizeof(int);
            int count = *(int*)(staging + offset);
            offset += sizeof(int);
            if (count > 0)
                entityManager.DestroyEntities(batch.Destroys + startIndex, count);
            return offset;
        }

        // 按原始字节直接落列：不装箱、不解析 Type。
        private static unsafe int ReplayAddComponent(EntityManager entityManager, byte* staging, int offset)
        {
            var entity = *(Entity*)(staging + offset);
            offset += sizeof(Entity);
            int typeId = *(int*)(staging + offset);
            offset += sizeof(int);
            int compSize = *(int*)(staging + offset);
            offset += sizeof(int);
            entityManager.AddComponentRaw(entity, typeId, staging + offset, compSize);
            return offset + compSize;
        }

        private static unsafe int ReplayRemoveComponent(EntityManager entityManager, byte* staging, int offset)
        {
            var entity = *(Entity*)(staging + offset);
            offset += sizeof(Entity);
            int typeId = *(int*)(staging + offset);
            offset += sizeof(int);
            entityManager.RemoveComponentRaw(entity, ComponentTypeManager.GetTypeByComponentType(typeId));
            return offset;
        }

        public void Dispose()
        {
            if (_playingBack) throw new InvalidOperationException("Cannot dispose an ECB during playback.");
            if (!_disposed)
            {
                if (_staging != null)
                {
                    Marshal.FreeHGlobal((IntPtr)_staging);
                    _staging = null;
                }
                if (_createdEntities != null)
                {
                    Marshal.FreeHGlobal((IntPtr)_createdEntities);
                    _createdEntities = null;
                    _createdEntityCapacity = 0;
                }
                if (_destroyEntities != null)
                {
                    Marshal.FreeHGlobal((IntPtr)_destroyEntities);
                    _destroyEntities = null;
                    _destroyEntityCapacity = 0;
                }
                DisposeParallelWriters();
                _disposed = true;
            }
        }

        ~DeferredCommandBuffer()
        {
            Dispose();
        }
    }
}
