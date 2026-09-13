using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EntJoy.Collections;

namespace EntJoy.ECS
{
    /// <summary>
    /// 手动延迟命令缓冲区（ECB）。
    /// 在 Job 或主线程中记录结构变更命令，帧末在主线程统一 Playback。
    ///
    /// 用法：
    ///   var ecb = new DeferredCommandBuffer();
    ///   ecb.CreateEntity(typeof(Position), typeof(Velocity));
    ///   ecb.DestroyEntity(entity);
    ///   ecb.AddComponent(entity, new Position { X = 1 });
    ///   ecb.RemoveComponent<Velocity>(entity);
    ///   // ... Job 完成后 ...
    ///   ecb.Playback(world.EntityManager);
    ///   ecb.Dispose();
    ///
    /// Observer 集成：Playback 内部调用 EntityManager 主入口（NewEntity/DestroyEntity/AddComponentRaw/
    /// RemoveComponentRaw），主线程结构变更入口统一挂 observer 派发 → ECB Playback 天然触发事件，无需额外扩展。
    /// </summary>
    public unsafe partial class DeferredCommandBuffer : IDisposable
    {
        private byte* _staging;
        private int _stagingOffset;
        private int _stagingCapacity;
        private bool _disposed;
        private readonly object _sync = new();   // 记录/回放锁：多 worker 并发写同一 ECB 时保证不覆盖/交错

        private const int InitialCapacity = 64 * 1024;

        // OpCodes（internal：ParallelWriter 需要引用其中两个，见 DeferredCommandBuffer.ParallelWriter.cs）
        internal const int OP_CREATE_ENTITIES_RANGE = 1;  // 批量创建（P0-4）：[op][typeSetIndex][count][batchId]
        internal const int OP_DESTROY_RANGE = 2;          // 批量销毁（P0-4b，记录期成段）：[op][startIndex][count]
        private const int OP_ADD_COMPONENT = 3;
        private const int OP_REMOVE_COMPONENT = 4;
        private const int OP_SET_COMPONENT_RANGE = 5;     // 批量写组件列（P0-5，零反射）：
                                                          // [op][batchId][typeId][elemSize][elemCount][payload]
        internal const int OP_SET_COMPONENT_SINGLE = 6;   // 单实体写组件（P1-9，ParallelWriter 用）：
                                                          // [op][entity][typeId][elemSize][payload]

        /// <summary>组件类型集合池：记录期存下 <c>ComponentType[]</c>，回放期按下标取用
        /// ⇒ **回放期不再 per-command <c>new ComponentType[]</c>**（P0-5 的去分配关键）。</summary>
        private readonly List<ComponentType[]> _typeSets = new();

        /// <summary>记录期分配的 batch 计数（回放期用同样的序 push 到 <see cref="_batchStarts"/>）。</summary>
        private int _recordedBatchCount;

        /// <summary>回放期产出：每个 create-range 的实体在 <see cref="_createdEntities"/> 中的起止。</summary>
        private readonly List<int> _batchStarts = new();
        private readonly List<int> _batchCounts = new();

        /// <summary>回放期产出：所有被创建实体的非托管表（零托管分配）。</summary>
        private Entity* _createdEntities;
        private int _createdEntityCapacity;
        private int _createdEntityCount;

        /// <summary>销毁合并用的非托管实体表（P0-4b）：记录期 append，回放期一次 <c>DestroyEntities</c>。</summary>
        private Entity* _destroyEntities;
        private int _destroyEntityCapacity;
        private int _destroyEntityCount;
        private bool _destroyRunOpen;
        private int _destroyRunStart;
        private int _destroyCountFieldOffset;

        /// <summary>关闭当前 destroy 连续段（任何非 destroy 命令都要调用）。</summary>
        private void CloseDestroyRun() => _destroyRunOpen = false;

        // ═══ 并行记录（P1-9）：每 writer 独立非托管 staging + 独立销毁表，记录期无锁 ═══
        // 状态类型 EcbWriterState / 句柄类型 ParallelWriter 定义在 DeferredCommandBuffer.ParallelWriter.cs
        private EcbWriterState* _writers;
        private int _writerCount;

        /// <summary>已分配的并行 writer 数量（见 <see cref="EnsureParallelWriters"/>）。</summary>
        public int ParallelWriterCount => _writerCount;

        /// <summary>
        /// 预分配 <paramref name="count"/> 个并行 writer（幂等）。必须在任何 <see cref="CreateParallelWriter"/>
        /// 之前、且在任何 job 开始记录之前由主线程调用。
        /// </summary>
        public void EnsureParallelWriters(int count)
        {
            if (count <= 0 || count <= _writerCount) return;
            int old = _writerCount;
            var next = (EcbWriterState*)Marshal.AllocHGlobal(count * sizeof(EcbWriterState));
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
        /// 取第 <paramref name="index"/> 个**跨 tile 共享** writer（P1-9 补完，对齐 DOTS 语义）：
        /// 同一个 job 被切成多个 tile 分发到多个 worker 时可以**共享同一个 writer** ——
        /// 追加走 <c>Interlocked</c> 原子占位，不需要"一个 writer 一个线程"的约定。
        ///
        /// 与 <see cref="CreateParallelWriter"/> 的差别：
        ///   - 记录期允许并发（无 <c>ClaimThread</c> 守卫）；
        ///   - staging / 销毁表**必须一次给足容量**（并发下不能扩容）⇒ 超限抛明确异常；
        ///   - 同一 writer 内跨 lane 的**命令顺序不确定**（销毁之间无序、销毁与写组件之间以回放序为准）。
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
            st.Staging = (byte*)Marshal.AllocHGlobal(stagingCapacityBytes);
            st.Capacity = stagingCapacityBytes;
            st.Offset = 0;
            st.Destroys = (Entity*)Marshal.AllocHGlobal(destroyCapacity * sizeof(Entity));
            st.DestroyCapacity = destroyCapacity;
            st.DestroyCount = 0;
            st.Shared = 1;
            return new ParallelWriter { State = _writers + index };
        }

        /// <summary>主线程回放全部并行 writer（**按索引顺序** ⇒ 确定性），回放后清空各 writer。</summary>
        public void PlaybackParallel(EntityManager entityManager)
        {
            if (_writers == null) return;
            for (int i = 0; i < _writerCount; i++)
            {
                ref var st = ref _writers[i];
                if (st.Offset > 0) ReplayWriterBuffer(entityManager, ref st);
                st.Offset = 0;
                st.DestroyCount = 0;
                st.DestroyRunOpen = false;
                st.OwnerThread = 0;   // 释放线程占用，供下一轮记录复用
            }
        }

        /// <summary>回放单个 writer 的 staging（只认并行支持的两类命令；其它 opcode 直接抛错）。
        /// <c>OP_DESTROY_RANGE</c> 在**连续销毁槽**上合并（共享 writer 里每条销毁命令只带一个槽），
        /// 且遇到非销毁命令前先落地，保证命令序不被重排。</summary>
        private void ReplayWriterBuffer(EntityManager entityManager, ref EcbWriterState st)
        {
            int offset = 0;
            int runStart = 0, runCount = 0;
            while (offset + sizeof(int) <= st.Offset)
            {
                int opCode = *(int*)(st.Staging + offset);
                offset += sizeof(int);

                if (opCode == OP_DESTROY_RANGE)
                {
                    int startIndex = *(int*)(st.Staging + offset);
                    offset += sizeof(int);
                    int count = *(int*)(st.Staging + offset);
                    offset += sizeof(int);
                    if (count > 0)
                    {
                        if (runCount > 0 && runStart + runCount == startIndex)
                        {
                            runCount += count;
                        }
                        else
                        {
                            if (runCount > 0) entityManager.DestroyEntities(st.Destroys + runStart, runCount);
                            runStart = startIndex;
                            runCount = count;
                        }
                    }
                    continue;
                }

                if (runCount > 0)
                {
                    entityManager.DestroyEntities(st.Destroys + runStart, runCount);
                    runCount = 0;
                }

                switch (opCode)
                {
                    case OP_SET_COMPONENT_SINGLE:
                    {
                        Entity entity = *(Entity*)(st.Staging + offset);
                        offset += sizeof(Entity);
                        int typeId = *(int*)(st.Staging + offset);
                        offset += sizeof(int);
                        int elemSize = *(int*)(st.Staging + offset);
                        offset += sizeof(int);
                        byte* payload = st.Staging + offset;
                        if (entityManager.TryGetComponentPointer(entity, typeId, elemSize, out void* dst))
                            Buffer.MemoryCopy(payload, dst, elemSize, elemSize);
                        offset += elemSize;
                        break;
                    }
                    default:
                        throw new InvalidOperationException(
                            $"并行 writer 缓冲中出现不支持的 opcode {opCode}：并行只支持 DestroyEntity / SetComponent<T>。");
                }
            }

            if (runCount > 0)
                entityManager.DestroyEntities(st.Destroys + runStart, runCount);
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

        private unsafe void EnsureDestroyCapacity(int needed)
        {
            if (needed <= _destroyEntityCapacity) return;
            int newCapacity = _destroyEntityCapacity > 0 ? _destroyEntityCapacity : 4096;
            while (newCapacity < needed) newCapacity *= 2;
            var next = (Entity*)Marshal.AllocHGlobal(newCapacity * sizeof(Entity));
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
            if (additionalBytes < 0 || additionalBytes > int.MaxValue - _stagingOffset)
                throw new OverflowException("DeferredCommandBuffer size exceeds Int32.MaxValue.");
            int required = _stagingOffset + additionalBytes;
            if (required > _stagingCapacity)
            {
                int newCapacity = _stagingCapacity;
                while (newCapacity < required)
                {
                    if (newCapacity > int.MaxValue / 2)
                        throw new OverflowException("DeferredCommandBuffer size exceeds Int32.MaxValue.");
                    newCapacity *= 2;
                }
                var newStaging = (byte*)Marshal.AllocHGlobal(newCapacity);
                Buffer.MemoryCopy(_staging, newStaging, newCapacity, _stagingOffset);
                Marshal.FreeHGlobal((IntPtr)_staging);
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

        /// <summary>记录 CreateEntity 命令（单实体；等价于 <c>CreateEntitiesRange(1, types)</c>）。</summary>
        public void CreateEntity(params ComponentType[] componentTypes)
        {
            CreateEntitiesRange(1, componentTypes);
        }

        /// <summary>
        /// **批量创建**命令（P0-4）：一次记录 N 个同 archetype 实体，回放期走
        /// <see cref="EntityManager.CreateEntities(int, ComponentType[], Entity*, int)"/> 的零分配批量路径
        /// （一次 <c>CompleteActiveJobs</c> + 一次锁 + 无逐实体托管分配）。
        /// </summary>
        /// <returns>batchId（供 <see cref="SetComponentRange{T}"/> 引用本批实体）。</returns>
        public int CreateEntitiesRange(int count, params ComponentType[] types)
        {
            if (count <= 0 || types == null || types.Length == 0) return -1;
            lock (_sync)
            {
                CloseDestroyRun();
                int typeSetIndex = GetOrAddTypeSet(types);
                int batchId = _recordedBatchCount++;
                EnsureCapacity(sizeof(int) * 4);
                *(int*)(_staging + _stagingOffset) = OP_CREATE_ENTITIES_RANGE;
                _stagingOffset += sizeof(int);
                *(int*)(_staging + _stagingOffset) = typeSetIndex;
                _stagingOffset += sizeof(int);
                *(int*)(_staging + _stagingOffset) = count;
                _stagingOffset += sizeof(int);
                *(int*)(_staging + _stagingOffset) = batchId;
                _stagingOffset += sizeof(int);
                CommandCount++;
                return batchId;
            }
        }

        /// <summary>
        /// **批量写组件列**命令（P0-5）：把 <paramref name="values"/> 逐元素写进第 <paramref name="batchId"/>
        /// 批创建的实体的该组件列。取值在**记录期**就拷进 staging（调用方缓冲随后可自由释放/复用），
        /// 回放期经 blittable 定位表直接落列 ⇒ **无反射、无装箱、无 per-command 分配**。
        /// </summary>
        public unsafe void SetComponentRange<T>(int batchId, NativeArray<T> values) where T : unmanaged
        {
            if (batchId < 0) return;
            int elemSize = Unsafe.SizeOf<T>();
            int n = values.Length;
            if (n <= 0) return;
            long payload = (long)n * elemSize;
            int total = CheckedSize(sizeof(int) * 5 + payload);
            lock (_sync)
            {
                CloseDestroyRun();
                EnsureCapacity(total);
                *(int*)(_staging + _stagingOffset) = OP_SET_COMPONENT_RANGE;
                _stagingOffset += sizeof(int);
                *(int*)(_staging + _stagingOffset) = batchId;
                _stagingOffset += sizeof(int);
                *(int*)(_staging + _stagingOffset) = ComponentTypeManager.GetComponentType(typeof(T)).Id;
                _stagingOffset += sizeof(int);
                *(int*)(_staging + _stagingOffset) = elemSize;
                _stagingOffset += sizeof(int);
                *(int*)(_staging + _stagingOffset) = n;
                _stagingOffset += sizeof(int);
                Buffer.MemoryCopy(values.GetUnsafePtr(), _staging + _stagingOffset, payload, payload);
                _stagingOffset += (int)payload;
                CommandCount++;
            }
        }

        /// <summary>取某 batch 在 <see cref="CreatedEntities"/> 中的区间（回放期产出）。</summary>
        public void GetBatch(int batchId, out int start, out int count)
        {
            if (batchId >= 0 && batchId < _batchStarts.Count)
            {
                start = _batchStarts[batchId];
                count = _batchCounts[batchId];
                return;
            }
            start = -1;
            count = 0;
        }

        /// <summary>组件类型集合去重池（集合种类很少，线性比较足够）。</summary>
        private int GetOrAddTypeSet(ComponentType[] types)
        {
            for (int i = 0; i < _typeSets.Count; i++)
            {
                var set = _typeSets[i];
                if (set.Length != types.Length) continue;
                bool same = true;
                for (int k = 0; k < set.Length; k++)
                    if (set[k].Id != types[k].Id) { same = false; break; }
                if (same) return i;
            }
            _typeSets.Add(types);
            return _typeSets.Count - 1;
        }

        private unsafe void EnsureCreatedEntityCapacity(int needed)
        {
            if (needed <= _createdEntityCapacity) return;
            int newCapacity = _createdEntityCapacity > 0 ? _createdEntityCapacity : 1024;
            while (newCapacity < needed) newCapacity *= 2;
            var next = (Entity*)Marshal.AllocHGlobal(newCapacity * sizeof(Entity));
            if (_createdEntities != null)
            {
                Buffer.MemoryCopy(_createdEntities, next, (long)newCapacity * sizeof(Entity), (long)_createdEntityCount * sizeof(Entity));
                Marshal.FreeHGlobal((IntPtr)_createdEntities);
            }
            _createdEntities = next;
            _createdEntityCapacity = newCapacity;
        }

        /// <summary>
        /// 记录 DestroyEntity 命令。**连续的 destroy 会在回放期合并成一次批量销毁**（P0-4b）：
        /// 记录期把实体 append 到非托管表，staging 里只留 `[OP_DESTROY_RANGE][startIndex][count]` 一条
        /// （count 随记录就地更新）⇒ 回放期无需扫描/拷贝 staging，直接一次
        /// <c>EntityManager.DestroyEntities</c>（每批一次 job 等待 + 一次锁）。
        /// </summary>
        public void DestroyEntity(Entity entity)
        {
            lock (_sync)
            {
                if (!_destroyRunOpen)
                {
                    EnsureCapacity(sizeof(int) * 3);
                    *(int*)(_staging + _stagingOffset) = OP_DESTROY_RANGE;
                    _stagingOffset += sizeof(int);
                    _destroyRunStart = _destroyEntityCount;
                    *(int*)(_staging + _stagingOffset) = _destroyRunStart;
                    _stagingOffset += sizeof(int);
                    _destroyCountFieldOffset = _stagingOffset;
                    *(int*)(_staging + _stagingOffset) = 0;
                    _stagingOffset += sizeof(int);
                    _destroyRunOpen = true;
                    CommandCount++;   // 一条命令 = 一个连续段
                }
                EnsureDestroyCapacity(_destroyEntityCount + 1);
                _destroyEntities[_destroyEntityCount++] = entity;
                *(int*)(_staging + _destroyCountFieldOffset) = _destroyEntityCount - _destroyRunStart;
            }
        }

        /// <summary>记录 AddComponent 命令</summary>
        public void AddComponent<T>(Entity entity, T value) where T : struct, IComponentData
        {
            lock (_sync)
            {
                CloseDestroyRun();
                int compSize = Unsafe.SizeOf<T>();
                int totalSize = CheckedSize((long)sizeof(int) * 3 + sizeof(Entity) + compSize);
                EnsureCapacity(totalSize);

                *(int*)(_staging + _stagingOffset) = OP_ADD_COMPONENT;
                _stagingOffset += sizeof(int);
                *(Entity*)(_staging + _stagingOffset) = entity;
                _stagingOffset += sizeof(Entity);
                *(int*)(_staging + _stagingOffset) = ComponentTypeManager.GetComponentType(typeof(T)).Id;
                _stagingOffset += sizeof(int);
                *(int*)(_staging + _stagingOffset) = compSize;
                _stagingOffset += sizeof(int);
                Unsafe.CopyBlock(_staging + _stagingOffset, &value, (uint)compSize);
                _stagingOffset += compSize;
                CommandCount++;
            }
        }

        /// <summary>记录 RemoveComponent 命令</summary>
        public void RemoveComponent<T>(Entity entity) where T : struct
        {
            lock (_sync)
            {
                CloseDestroyRun();
                int totalSize = sizeof(int) + sizeof(Entity) + sizeof(int);
                EnsureCapacity(totalSize);

                *(int*)(_staging + _stagingOffset) = OP_REMOVE_COMPONENT;
                _stagingOffset += sizeof(int);
                *(Entity*)(_staging + _stagingOffset) = entity;
                _stagingOffset += sizeof(Entity);
                *(int*)(_staging + _stagingOffset) = ComponentTypeManager.GetComponentType(typeof(T)).Id;
                _stagingOffset += sizeof(int);
                CommandCount++;
            }
        }

        /// <summary>
        /// 主线程回放所有命令。无需注册，直接调用 EntityManager 的非泛型方法。
        /// </summary>
        public unsafe void Playback(EntityManager entityManager)
        {
            lock (_sync)
            {
                _batchStarts.Clear();
                _batchCounts.Clear();
                _createdEntityCount = 0;
                int offset = 0;
                while (offset < _stagingOffset)
                {
                int opCode = *(int*)(_staging + offset);
                offset += sizeof(int);

                switch (opCode)
                {
                    case OP_CREATE_ENTITIES_RANGE:
                    {
                        int typeSetIndex = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        int count = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        int batchId = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        EnsureCreatedEntityCapacity(_createdEntityCount + count);
                        int created = entityManager.CreateEntities(count, _typeSets[typeSetIndex],
                            _createdEntities + _createdEntityCount, _createdEntityCapacity - _createdEntityCount);
                        // batch 表按记录顺序 push；batchId 由记录期分配、与推入顺序一致
                        _batchStarts.Add(_createdEntityCount);
                        _batchCounts.Add(created);
                        _createdEntityCount += created;
                        break;
                    }
                    case OP_SET_COMPONENT_RANGE:
                    {
                        int batchId = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        int typeId = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        int elemSize = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        int elemCount = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        byte* payload = _staging + offset;
                        if (elemSize > 0 && elemCount > 0
                            && batchId >= 0 && batchId < _batchStarts.Count && batchId < _batchCounts.Count)
                        {
                            int start = _batchStarts[batchId];
                            int have = _batchCounts[batchId];
                            int n = elemCount < have ? elemCount : have;
                            if (n > 0)
                                entityManager.WriteComponentRange(typeId, elemSize, _createdEntities + start, payload, n);
                        }
                        offset += elemCount * elemSize;
                        break;
                    }
                    case OP_DESTROY_RANGE:
                    {
                        int startIndex = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        int count = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        if (count > 0)
                            entityManager.DestroyEntities(_destroyEntities + startIndex, count);
                        break;
                    }
                    case OP_ADD_COMPONENT:
                    {
                        var entity = *(Entity*)(_staging + offset);
                        offset += sizeof(Entity);
                        int typeId = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        int compSize = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        
                        // 去反射（P0-5 收尾）：直接按原始字节落列（无 PtrToStructure / 无装箱 / 无 Type 解析）
                        entityManager.AddComponentRaw(entity, typeId, _staging + offset, compSize);
                        
                        offset += compSize;
                        break;
                    }
                    case OP_REMOVE_COMPONENT:
                    {
                        var entity = *(Entity*)(_staging + offset);
                        offset += sizeof(Entity);
                        int typeId = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        
                        // 获取组件类型
                        var compType = ComponentTypeManager.GetTypeByComponentType(typeId);
                        entityManager.RemoveComponentRaw(entity, compType);
                        break;
                    }
                }
            }

            _stagingOffset = 0;
            CommandCount = 0;
            _recordedBatchCount = 0;
            _destroyEntityCount = 0;
            _destroyRunOpen = false;
            }
        }

        public void Dispose()
        {
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
