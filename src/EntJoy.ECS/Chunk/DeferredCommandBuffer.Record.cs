using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EntJoy.Collections;

namespace EntJoy.ECS
{
    public unsafe partial class DeferredCommandBuffer : IDisposable
    {
        /// <summary>记录 CreateEntity 命令（单实体；等价于 <c>CreateEntitiesRange(1, types)</c>）。</summary>
        public void CreateEntity(params ComponentType[] componentTypes)
        {
            CreateEntitiesRange(1, componentTypes);
        }

        /// <summary>批量创建：一次记录 N 个同原型实体，回放期走零分配批量路径。
        /// 返回 batchId，供 <see cref="SetComponentRange{T}"/> 引用本批实体。</summary>
        public int CreateEntitiesRange(int count, params ComponentType[] types)
        {
            if (count <= 0 || types == null || types.Length == 0) return -1;
            lock (_sync)
            {
                CloseDestroyRun();
                int typeSetIndex = GetOrAddTypeSet(types);
                int batchId = _recordedBatchCount++;
                EnsureCapacity(sizeof(int) * 4);
                WriteInt(OP_CREATE_ENTITIES_RANGE);
                WriteInt(typeSetIndex);
                WriteInt(count);
                WriteInt(batchId);
                CommandCount++;
                return batchId;
            }
        }

        /// <summary>批量写组件列：把 values 逐元素写进第 batchId 批实体的该列，记录期就拷进 staging，
        /// 回放期经定位表直接落列（无反射、无装箱）。</summary>
        public unsafe void SetComponentRange<T>(int batchId, NativeArray<T> values) where T : unmanaged
        {
            if (batchId < 0) return;
            int elemSize = Unsafe.SizeOf<T>();
            int n = values.Length;
            if (n <= 0) return;
            long payload = (long)n * elemSize;
            int total = CheckedSize(sizeof(int) * 5 + payload);
            int typeId = ComponentTypeManager.GetComponentType(typeof(T)).Id;
            lock (_sync)
            {
                CloseDestroyRun();
                EnsureCapacity(total);
                WriteInt(OP_SET_COMPONENT_RANGE);
                WriteInt(batchId);
                WriteInt(typeId);
                WriteInt(elemSize);
                WriteInt(n);
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

        // 往 staging 追写一个 int 并前移游标；调用方保证容量已预留。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteInt(int value)
        {
            *(int*)(_staging + _stagingOffset) = value;
            _stagingOffset += sizeof(int);
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
            while (newCapacity < needed)
            {
                if (newCapacity > int.MaxValue / 2) throw new OverflowException("ECB entity capacity exceeds Int32.MaxValue.");
                newCapacity *= 2;
            }
            var next = (Entity*)Marshal.AllocHGlobal(CheckedSize((long)newCapacity * sizeof(Entity)));
            if (_createdEntities != null)
            {
                Buffer.MemoryCopy(_createdEntities, next, (long)newCapacity * sizeof(Entity), (long)_createdEntityCount * sizeof(Entity));
                Marshal.FreeHGlobal((IntPtr)_createdEntities);
            }
            _createdEntities = next;
            _createdEntityCapacity = newCapacity;
        }

        /// <summary>
        /// 记录 DestroyEntity 命令。连续的 destroy 会在回放期合并成一次批量销毁：
        /// 记录期把实体 append 到非托管表，staging 里只留 `[OP_DESTROY_RANGE][startIndex][count]` 一条
        /// <c>EntityManager.DestroyEntities</c>（每批一次 job 等待 + 一次锁）。
        /// </summary>
        public void DestroyEntity(Entity entity)
        {
            lock (_sync)
            {
                EnsureDestroyCapacity(checked(_destroyEntityCount + 1));
                if (!_destroyRunOpen)
                {
                    EnsureCapacity(sizeof(int) * 3);
                    WriteInt(OP_DESTROY_RANGE);
                    _destroyRunStart = _destroyEntityCount;
                    WriteInt(_destroyRunStart);
                    _destroyCountFieldOffset = _stagingOffset;
                    WriteInt(0);
                    _destroyRunOpen = true;
                    CommandCount++;   // 一条命令 = 一个连续段
                }
                _destroyEntities[_destroyEntityCount++] = entity;
                *(int*)(_staging + _destroyCountFieldOffset) = _destroyEntityCount - _destroyRunStart;
            }
        }

        /// <summary>记录 AddComponent 命令</summary>
        public void AddComponent<T>(Entity entity, T value) where T : struct, IComponentData
        {
            int typeId = ComponentTypeManager.GetComponentType(typeof(T)).Id;
            lock (_sync)
            {
                CloseDestroyRun();
                int compSize = Unsafe.SizeOf<T>();
                int totalSize = CheckedSize((long)sizeof(int) * 3 + sizeof(Entity) + compSize);
                EnsureCapacity(totalSize);

                WriteInt(OP_ADD_COMPONENT);
                *(Entity*)(_staging + _stagingOffset) = entity;
                _stagingOffset += sizeof(Entity);
                WriteInt(typeId);
                WriteInt(compSize);
                Unsafe.CopyBlock(_staging + _stagingOffset, &value, (uint)compSize);
                _stagingOffset += compSize;
                CommandCount++;
            }
        }

        /// <summary>记录 RemoveComponent 命令</summary>
        public void RemoveComponent<T>(Entity entity) where T : struct
        {
            int typeId = ComponentTypeManager.GetComponentType(typeof(T)).Id;
            lock (_sync)
            {
                CloseDestroyRun();
                int totalSize = sizeof(int) + sizeof(Entity) + sizeof(int);
                EnsureCapacity(totalSize);

                WriteInt(OP_REMOVE_COMPONENT);
                *(Entity*)(_staging + _stagingOffset) = entity;
                _stagingOffset += sizeof(Entity);
                WriteInt(typeId);
                CommandCount++;
            }
        }

    }
}
