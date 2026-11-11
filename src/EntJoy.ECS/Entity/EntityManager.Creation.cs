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
        public Entity NewEntity(params Type[] componentTypes)
        {
            // 小集合走栈缓冲：这条重载是逐实体创建的热路径，托管数组会变成每实体一次分配。
            Span<ComponentType> componentSpan = componentTypes.Length <= 16
                ? stackalloc ComponentType[componentTypes.Length]
                : new ComponentType[componentTypes.Length];
            for (int i = 0; i < componentTypes.Length; i++)  // 遍历输入的组件类型
            {
                componentSpan[i] = ComponentTypeManager.GetComponentType(componentTypes[i]);  // 获取组件类型
            }
            return NewEntity(componentSpan);  // 调用核心实现
        }

        /// <summary>
        /// 创建新实体（基于ComponentType数组）
        /// </summary>
        public Entity NewEntity(params ComponentType[] types)  // 基于ComponentType创建实体
        {
            return NewEntity(types.AsSpan());  // 调用核心实现
        }

        /// <summary>创建新实体（核心实现）。</summary>
        public unsafe Entity NewEntity(Span<ComponentType> types)
        {
            CheckDisposed();
            // 目标原型要在锁内取，所以有在飞 Job 时先等完。
            if (!JobBookkeepingIdle()) CompleteActiveJobs();
            using (EnterStructural())
            {
                var newEntity = AllocateEntityId();
                var targetArch = GetOrCreateArchetype(types);
                targetArch.AddEntity(newEntity, out var chunkIndex, out var slotInChunk);
                InvalidateStructuralQueries();
                UpdateEntityLocation(newEntity.Id, targetArch, chunkIndex, slotInChunk, newEntity.Version);

                if (_observerCount > 0 && _observers != null)
                {
                    foreach (var t in types)
                    {
                        if (t.IsRelation) continue;
                        if (!_observers.TryGetValue(t.Id, out var reg) || reg.Added.Count == 0) continue;
                        int compIdx = targetArch.GetComponentTypeIndex(t);
                        var chunk = targetArch.ChunkList[chunkIndex];
                        void* valPtr = (void*)(chunk.GetComponentArrayPointer(compIdx) + slotInChunk * t.Size);
                        DispatchAdded(reg.Added, &newEntity, valPtr, 1);
                    }
                }

                return newEntity;
            }
        }

        /// <summary>
        /// 批量创建的零分配版本：与 <see cref="CreateEntities(int, ComponentType[])"/> 同一路径，
        /// 但把新实体写进调用方提供的非托管缓冲而非托管 <c>Entity[]</c>。
        /// 用途：每步生成大批单位（本项目 65,536/步）时避免 512KB/步 的托管分配，并给 ECB 批量回放用。
        /// </summary>
        /// <returns>实际创建数（= min(count, outputCapacity)）。</returns>
        public unsafe int CreateEntities(int count, ComponentType[] types, Entity* output, int outputCapacity)
        {
            CheckDisposed();
            if (count <= 0 || output == null || outputCapacity <= 0 || types == null) return 0;
            if (count > outputCapacity) count = outputCapacity;
            CompleteActiveJobs();
            using (EnterStructural())
            {
                var targetArch = GetOrCreateArchetype(types);
                int created = 0;
                for (int i = 0; i < count; i++)
                {
                    var newEntity = AllocateEntityId();
                    targetArch.AddEntity(newEntity, out var chunkIndex, out var slotInChunk);
                    UpdateEntityLocation(newEntity.Id, targetArch, chunkIndex, slotInChunk, newEntity.Version);
                    output[created++] = newEntity;
                }
                InvalidateStructuralQueries();
                // 非托管路径不派发 Observer Added：批量派发需要托管 Entity[]，需要时用托管重载。
                return created;
            }
        }

        /// <summary>
        /// 单实体组件写入：经 blittable 定位表直落组件列，无反射/无装箱。
        /// 与批量路径 <see cref="WriteComponentRange"/> 同源（都走 <see cref="TryGetComponentPointer"/> 的解析规则）。
        /// </summary>
        public unsafe void SetComponent<T>(Entity entity, T value) where T : struct
        {
            CheckDisposed();
            var componentType = ComponentTypeManager.GetComponentType(typeof(T));
            if (componentType.IsRelation)
            {
                CompleteRelationWriteJobs(entity, componentType);
                using var changes = BeginRelationChanges();
                using (EnterStructural())
                {
                    ValidateEntity(entity);
                    if (!GetEntityInfoRef(entity.Id).Archetype.Has(componentType.Type))
                        throw new InvalidOperationException($"Entity {entity} has no '{componentType.Type}' column.");
                    SetSingleRelationCore(entity, componentType, (byte*)Unsafe.AsPointer(ref value), sizeof(T));
                }
                return;
            }
            CompleteEntityJobs(entity);
            int typeId = componentType.Id;
            if (!TryGetComponentPointer(entity, typeId, sizeof(T), out void* ptr))
                throw new InvalidOperationException(
                    $"SetComponent<{typeof(T).Name}> 失败：实体 {entity.Id} 无效/已销毁/版本不匹配，或该实体没有该组件列。");
            ComponentTypeManager.DestroyComponentValue(componentType, ptr);
            *(T*)ptr = value;
            ref var info = ref entities[entity.Id];
            info.Archetype.NotifyComponentChanged(info.ChunkIndex, info.SlotInChunk);
        }

        /// <summary>
        /// 经定位表解析"某实体某组件列的可写指针"（单实体路径）。
        /// 返回 false 的四种情形：Id 越界 / 未分配或已销毁 / 版本不匹配（悬垂）/ Archetype 无该组件列。
        /// </summary>
        internal unsafe bool TryGetComponentPointer(Entity entity, int componentTypeId, int elemSize, out void* ptr)
        {
            ptr = null;
            if ((uint)entity.Id >= (uint)_locateBCapacity) return false;
            ref var loc = ref _locateB[entity.Id];
            if (loc.ChunkMemory == null || loc.SlotInChunk < 0) return false;
            if (loc.Version != entity.Version) return false;
            if ((uint)entity.Id >= (uint)entities.Length) return false;
            var arch = entities[entity.Id].Archetype;
            if (arch == null) return false;
            var ct = new ComponentType(componentTypeId);
            if (!arch.HasComponent(ct)) return false;
            int compIdx = arch.GetComponentTypeIndex(ct);
            ptr = (byte*)loc.ChunkMemory + loc.ChunkOffsets[compIdx] + loc.SlotInChunk * elemSize;
            return true;
        }

        /// <summary>
        /// 把一段连续取值批量写进给定实体的某组件列（用，ECB 回放零反射路径）。
        /// 逐实体经 blittable 定位表解析列基址（见 <see cref="EntityLocateB"/>），不做任何反射/装箱。
        /// </summary>
        /// <param name="componentTypeId">目标组件类型 Id。</param>
        /// <param name="elemSize">组件元素字节数（= sizeof(T)）。</param>
        /// <param name="srcEntities">实体列表（长度 ≥ count）。</param>
        /// <param name="srcValues">取值缓冲（长度 ≥ count × elemSize，与 srcEntities 一一对应）。</param>
        /// <param name="count">元素数。</param>
        /// <returns>实际写入数（实体无效/无该组件列的项被跳过）。</returns>
        internal unsafe int WriteComponentRange(int componentTypeId, int elemSize, Entity* srcEntities, byte* srcValues, int count)
        {
            if (count <= 0 || elemSize <= 0 || srcEntities == null || srcValues == null) return 0;
            if (_locateB == null) return 0;
            var metadata = ComponentTypeManager.GetComponentType(componentTypeId);
            if (metadata.IsRelation) return WriteRelationRange(metadata, elemSize, srcEntities, srcValues, count);

            int written = 0;
            int cachedArchetypeId = -1;
            int compIdx = -1;
            for (int i = 0; i < count; i++)
            {
                int id = srcEntities[i].Id;
                if ((uint)id >= (uint)_locateBCapacity) continue;
                ref var loc = ref _locateB[id];
                if (loc.ChunkMemory == null || loc.SlotInChunk < 0) continue;
                if (loc.Version != srcEntities[i].Version) continue;
                if ((uint)id >= (uint)entities.Length) continue;
                var arch = entities[id].Archetype;
                if (arch == null) continue;
                // 同一批通常同原型：只在切换时重解析列下标（这是字典查询）。
                if (arch.ArchetypeId != cachedArchetypeId)
                {
                    cachedArchetypeId = arch.ArchetypeId;
                    var ct = new ComponentType(componentTypeId);
                    compIdx = arch.HasComponent(ct) ? arch.GetComponentTypeIndex(ct) : -1;
                }
                if (compIdx < 0) continue;
                byte* dst = (byte*)loc.ChunkMemory + loc.ChunkOffsets[compIdx] + loc.SlotInChunk * elemSize;
                Buffer.MemoryCopy(srcValues + (long)i * elemSize, dst, elemSize, elemSize);
                ref var info = ref entities[id];
                arch.NotifyComponentChanged(info.ChunkIndex, info.SlotInChunk);
                written++;
            }
            return written;
        }

        // 关系列必须走关系 API：逐条按单值关系语义设置，槽位可能因此发生结构变更。
        private unsafe int WriteRelationRange(ComponentType metadata, int elemSize, Entity* srcEntities, byte* srcValues, int count)
        {
            if (metadata.Size != elemSize) throw new ArgumentException("Relationship value size does not match its column.");
            CompleteActiveJobs();
            using var changes = BeginRelationChanges();
            using (EnterStructural())
            {
                int updated = 0;
                for (int i = 0; i < count; i++)
                {
                    if (!IsAlive(srcEntities[i]) || !GetEntityInfoRef(srcEntities[i].Id).Archetype.Has(metadata.Type)) continue;
                    SetSingleRelationCore(srcEntities[i], metadata, srcValues + (long)i * elemSize, elemSize);
                    updated++;
                }
                return updated;
            }
        }

        /// <summary>
        /// 批量创建实体：一次原型查找、一次批量添加、一次 CompleteActiveJobs。
        /// 相比逐个 NewEntity，省去了每次调用的锁与作业同步开销。
        /// </summary>
        public unsafe Entity[] CreateEntities(int count, params ComponentType[] types)
        {
            CheckDisposed();
            if (count <= 0) return Array.Empty<Entity>();
            CompleteActiveJobs();
            using (EnterStructural())
            {
                var targetArch = GetOrCreateArchetype(types);
                var result = new Entity[count];

                for (int i = 0; i < count; i++)
                {
                    var newEntity = AllocateEntityId();
                    targetArch.AddEntity(newEntity, out var chunkIndex, out var slotInChunk);
                    UpdateEntityLocation(newEntity.Id, targetArch, chunkIndex, slotInChunk, newEntity.Version);
                    result[i] = newEntity;
                }

                InvalidateStructuralQueries();

                // Observer：Added（批量合并——循环内收集，锁外一次派发）
                if (_observerCount > 0 && _observers != null)
                {
                    DispatchCreateEntitiesAdded(targetArch, types, result, count);
                }
                return result;
            }
        }

        /// <summary>
        /// CreateEntities 批量 Added 派发：按组件类型分桶，每类型一次回调（ReadOnlySpan）。
        /// 调用方保证在主线程、锁外（回调内结构变更需走 ECB）。
        /// </summary>
        private unsafe void DispatchCreateEntitiesAdded(Archetype targetArch, ComponentType[] types, Entity[] result, int count)
        {
            foreach (var t in types)
            {
                if (t.IsRelation) continue;
                if (!_observers!.TryGetValue(t.Id, out var reg) || reg.Added.Count == 0) continue;

                int compIdx = targetArch.GetComponentTypeIndex(t);
                int compSize = t.Size;
                int totalBytes = count * compSize;
                // 值不连续（实体跨块），先拷进连续缓冲再派发。
                if (totalBytes <= StackBufferLimit)
                {
                    byte* stackValues = stackalloc byte[totalBytes == 0 ? 1 : totalBytes];
                    GatherCreatedValues(result, count, compIdx, compSize, stackValues);
                    fixed (Entity* entitiesPtr = result)
                        DispatchAdded(reg.Added, entitiesPtr, stackValues, count);
                    continue;
                }
                DispatchLargeAdded(reg.Added, result, count, compIdx, compSize, totalBytes);
            }
        }

        // 超过栈缓冲上限改用 ArrayPool；pin 必须覆盖拷贝与派发全过程。
        private unsafe void DispatchLargeAdded(List<IComponentObserver> observers, Entity[] result, int count,
            int compIdx, int compSize, int totalBytes)
        {
            var pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(totalBytes);
            try
            {
                fixed (byte* valuesPtr = pooled)
                {
                    GatherCreatedValues(result, count, compIdx, compSize, valuesPtr);
                    fixed (Entity* entitiesPtr = result)
                        DispatchAdded(observers, entitiesPtr, valuesPtr, count);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pooled);
            }
        }

        // 拷贝上限：超过这个字节数改用池化数组，避免大组件数组撑爆栈。
        private const int StackBufferLimit = 4096;

        private unsafe void GatherCreatedValues(Entity[] result, int count, int compIdx, int compSize, byte* destination)
        {
            int write = 0;
            for (int i = 0; i < count; i++)
            {
                var loc = GetEntityInfoRef(result[i].Id);
                var chunk = loc.Archetype!.ChunkList[loc.ChunkIndex];
                void* srcPtr = (void*)(chunk.GetComponentArrayPointer(compIdx) + loc.SlotInChunk * compSize);
                Unsafe.CopyBlock(destination + write, srcPtr, (uint)compSize);
                write += compSize;
            }
        }

    }
}
