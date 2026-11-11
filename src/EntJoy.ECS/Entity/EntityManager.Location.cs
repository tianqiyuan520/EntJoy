using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using EntJoy.Collections;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager : IDisposable
    {
        /// <summary>按实体 Id 取实体信息引用；越界直接抛 <see cref="IndexOutOfRangeException"/>。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref EntityIndexInWorld GetEntityInfoRef(int index)
        {
            if ((uint)index >= (uint)entities.Length)
                throw new IndexOutOfRangeException($"Entity index {index} is out of range (max {entities.Length - 1}).");
            return ref entities[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateEntityLocation(int entityId, Archetype archetype, int chunkIndex, int slotInChunk, int version = -1)
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entityId);
            entityInfoRef.SetLocation(archetype, chunkIndex, slotInChunk);
            if (version >= 0) entityInfoRef.Version = version;
            // blittable 镜像：与托管表同一处更新（另两个写入点是 RefreshChunkEntityIndices 与扩容）。
            // 定位表按需扩容，不依赖调用方同步：漏扩会让超出旧容量的实体读到越界/零值。
            if ((uint)entityId >= (uint)_locateBCapacity) EnsureLocateCapacity(entityId + 1);
            {
                Chunk chunk = archetype.ChunkList[chunkIndex];
                ref var loc = ref _locateB[entityId];
                loc.ChunkMemory = (void*)chunk.MemoryBlock;
                loc.ChunkOffsets = archetype.GetChunkOffsetsNative();
                loc.SlotInChunk = slotInChunk;
                if (version >= 0) loc.Version = version;
            }
        }

        /// <summary>把实体版本号写进 blittable 定位表（版本号在 <see cref="UpdateEntityLocation"/> 之后单独写入）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetLocateBVersion(int entityId, int version)
        {
            if ((uint)entityId >= (uint)_locateBCapacity) EnsureLocateCapacity(entityId + 1);
            _locateB[entityId].Version = version;
        }

        /// <summary>清空某实体在 blittable 定位表中的项（销毁路径）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClearLocateB(int entityId)
        {
            if ((uint)entityId < (uint)_locateBCapacity)
            {
                ref var loc = ref _locateB[entityId];
                loc.ChunkMemory = null;
                loc.ChunkOffsets = null;
                loc.SlotInChunk = -1;
            }
        }

        /// <summary>
        /// 扩容托管实体表（唯一入口）：与 blittable 定位表同步扩容。
        /// 任何地方都不得直接 <c>Array.Resize(ref entities, …)</c>，漏掉定位表同步会让超出旧容量的实体读到越界值。
        /// </summary>
        private void ResizeEntityTable(int newLength)
        {
            if (newLength <= entities.Length) return;
            Array.Resize(ref entities, newLength);
            EnsureLocateCapacity(entities.Length);
        }

        /// <summary>blittable 定位表随实体数组一起扩容（4 处 Array.Resize(ref entities, …) 之后调用）。</summary>
        private void EnsureLocateCapacity(int needed)
        {
            if (needed <= _locateBCapacity) return;
            int newCapacity = _locateBCapacity > 0 ? _locateBCapacity : 32;
            while (newCapacity < needed) newCapacity *= 2;
            _locateB = NativeLocateStorage.GrowLocate(_locateB, _locateBCapacity, newCapacity);
            _locateBCapacity = newCapacity;
        }

        // blittable 定位表的对外访问（job / 原生内核）

        /// <summary>blittable 定位表首址（索引 = 实体 Id）；长度 = <see cref="LocateCapacity"/>。</summary>
        public unsafe EntityLocateB* GetEntityLocatePtr() => _locateB;

        /// <summary>blittable 定位表容量（实体 Id 上界）。</summary>
        public int LocateCapacity => _locateBCapacity;

        /// <summary>
        /// 构造 job-safe 的组件随机访问句柄（值语义，可作为 job 字段传给并行 job / 原生内核）。
        /// </summary>
        /// <param name="archetype">目标组件所在的 Archetype（用于预解析组件列下标）。</param>
        public unsafe NativeComponentLookup<T> CreateNativeLookup<T>(Archetype archetype) where T : unmanaged
        {
            return new NativeComponentLookup<T>
            {
                Locate = _locateB,
                ComponentIndex = archetype.GetComponentTypeIndex<T>(),
                Length = _locateBCapacity,
            };
        }

        /// <summary>构造 job-safe 的位置查询句柄（实体 → chunk 基址 / slot / version）。</summary>
        public unsafe NativeEntityLookup CreateNativeEntityLookup()
            => new NativeEntityLookup { Locate = _locateB, Length = _locateBCapacity };

        /// <summary>
        /// 探针：逐实体比对 blittable 定位表 vs 托管 <see cref="EntityIndexInWorld"/>，
        /// 返回不一致项数（0 = 一致）。遍历全部 Archetype/Chunk/Slot，覆盖
        /// "结构变更（NewEntity/DestroyEntity/Add/Remove/空 chunk 压缩）之后"的一致性判据。
        /// </summary>
        public unsafe long VerifyLocateTable(out int checkedEntities)
        {
            long mismatch = 0;
            int seen = 0;
            for (int a = 0; a < archetypeCount; a++)
            {
                var arch = allArchetypes[a];
                if (arch == null) continue;
                int* offsets = arch.GetChunkOffsetsNative();
                for (int c = 0; c < arch.ChunkList.Count; c++)
                {
                    var chunk = arch.ChunkList[c];
                    for (int slot = 0; slot < chunk.EntityCount; slot++)
                    {
                        int id = chunk.GetEntity(slot).Id;
                        if ((uint)id >= (uint)_locateBCapacity) { mismatch++; continue; }
                        ref var loc = ref _locateB[id];
                        var info = entities[id];
                        seen++;
                        if (loc.ChunkMemory != (void*)chunk.MemoryBlock) mismatch++;
                        else if (loc.ChunkOffsets != offsets) mismatch++;
                        else if (loc.SlotInChunk != slot) mismatch++;
                        else if (info.Archetype != arch || info.ChunkIndex != c || info.SlotInChunk != slot) mismatch++;
                        else if (loc.Version != info.Version) mismatch++;
                    }
                }
            }
            checkedEntities = seen;
            return mismatch;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ValidateEntity(Entity entity)
        {
            CheckDisposed();
            if ((uint)entity.Id >= (uint)entities.Length)
                throw new InvalidOperationException($"Entity {entity} has an invalid ID.");
            // 存活与版本判定集中在 GetAliveInfoRef，错误文案只有一处。
            GetAliveInfoRef(entity);
        }

        private void RefreshChunkEntityIndices(Archetype archetype, int chunkIndex)
        {
            var chunk = archetype.ChunkList[chunkIndex];
            for (int slot = 0; slot < chunk.EntityCount; slot++)
            {
                UpdateEntityLocation(chunk.GetEntity(slot).Id, archetype, chunkIndex, slot);
            }
        }

    }
}
