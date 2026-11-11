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
    // Component
    public unsafe partial class EntityManager
    {
        /// <summary>
        /// 添加组件（泛型版本，调用 AddComponentRaw）
        /// </summary>
        public unsafe void AddComponent<T0>(Entity entity, T0 t0) where T0 : struct
        {
            // 走字节路径：泛型值直接取地址，不经 object 装箱，也不重复查类型字典。
            AddComponentRaw(entity, ComponentTypeManager.GetComponentType<T0>(), (byte*)&t0, Unsafe.SizeOf<T0>());
        }


        /// <summary>
        /// 移除组件（泛型版本，调用 RemoveComponentRaw）
        /// </summary>
        public void RemoveComponent<T0>(Entity entity) where T0 : struct
        {
            RemoveComponentRaw(entity, ComponentTypeManager.GetComponentType<T0>());
        }

        // 非泛型方法（供 ECB Playback 使用）

        /// <summary>
        /// 添加组件并写入原始字节值（ECB 回放去反射路径：无 <c>Marshal.PtrToStructure</c>、无装箱）。
        /// 组件已存在时等价于原地写列（与 <see cref="WriteComponentRange"/> 同源）。
        /// </summary>
        public unsafe void AddComponentRaw(Entity entity, int componentTypeId, byte* value, int elemSize)
        {
            AddComponentRaw(entity, ComponentTypeManager.GetComponentType(componentTypeId), value, elemSize);
        }

        /// <summary>按已解析的组件元数据添加：泛型入口不必再查类型字典。</summary>
        public unsafe void AddComponentRaw(Entity entity, ComponentType metadata, byte* value, int elemSize)
        {
            CheckDisposed();
            bool relationship = metadata.IsRelation;
            if (relationship) CompleteRelationWriteJobs(entity, metadata.Type);
            using var relationChanges = relationship ? BeginRelationChanges() : default;
            Archetype? targetArch = ResolveAddTarget(entity, metadata);
            if (!JobBookkeepingIdle()) CompleteEntityJobs(entity, targetArch);
            using (EnterStructural())
            {
                AddComponentRawCore(entity, metadata, null, value, elemSize, knownTarget: targetArch);
            }
        }

        /// <summary>
        /// 添加组件（非泛型版本，核心实现）
        /// </summary>
        public unsafe void AddComponentRaw(Entity entity, Type componentType, object value)
        {
            var metadata = ComponentTypeManager.GetComponentType(componentType);
            CheckDisposed();
            bool relationship = metadata.IsRelation;
            if (relationship) CompleteRelationWriteJobs(entity, metadata.Type);
            using var relationChanges = relationship ? BeginRelationChanges() : default;
            Archetype? targetArch = ResolveAddTarget(entity, metadata);
            CompleteEntityJobs(entity, targetArch);
            using (EnterStructural())
            {
                AddComponentRawCore(entity, metadata, value);
            }
        }

        // 组件不存在时才需要迁移目标原型；已存在则原地写值，只等实体所属原型。
        private Archetype? ResolveAddTarget(Entity entity, ComponentType metadata)
        {
            if ((uint)entity.Id >= (uint)entities.Length) return null;
            ref var info = ref GetEntityInfoRef(entity.Id);
            return info.Archetype != null && !info.Archetype.HasComponent(metadata)
                ? info.Archetype.GetAddEdge(metadata)
                : null;
        }

        /// <summary>
        /// 把原始字节写进某实体所在 chunk 的组件列（结构变更后的落值步骤，无反射/无装箱）。
        /// </summary>
        private static unsafe void WriteColumnBytes(Archetype arch, int chunkIndex, int slotInChunk, Type componentType, byte* src, int elemSize)
        {
            WriteColumnBytes(arch, chunkIndex, slotInChunk, ComponentTypeManager.GetComponentType(componentType), src, elemSize);
        }

        private static unsafe void WriteColumnBytes(Archetype arch, int chunkIndex, int slotInChunk, ComponentType componentType, byte* src, int elemSize)
        {
            int compIdx = arch.GetComponentTypeIndex(componentType);
            byte* colBase = (byte*)arch.ChunkList[chunkIndex].GetComponentArrayPointer(compIdx);
            Buffer.MemoryCopy(src, colBase + (long)slotInChunk * elemSize, elemSize, elemSize);
        }

        /// <summary>解析并缓存增删组件后的目标 Archetype。</summary>
        private Archetype GetComponentDestination(Archetype source, ComponentType type, bool adding)
        {
            var target = adding ? source.GetAddEdge(type) : source.GetRemoveEdge(type);
            if (target != null) return target;
            Span<ComponentType> types = stackalloc ComponentType[source.ComponentCount + (adding ? 1 : -1)];
            int count = 0;
            foreach (var item in source.Types)
                if (adding || item.Id != type.Id) types[count++] = item;
            if (adding) types[count] = type;
            target = GetOrCreateArchetype(types);
            if (adding) source.SetAddEdge(type, target);
            else source.SetRemoveEdge(type, target);
            return target;
        }

        /// <summary>
        /// 添加组件核心（调用方必须已持有 _structuralLock）。
        /// <paramref name="rawValue"/> 非 null 时走原始字节写入（值以字节为准，忽略 <paramref name="value"/>）。
        /// </summary>
        private unsafe void AddComponentRawCore(Entity entity, ComponentType metadata, object value, byte* rawValue = null, int rawSize = 0,
            bool relationStorageOnly = false, nint preparedChunk = 0, Archetype knownTarget = null)
        {
            if (metadata.IsRelation && !relationStorageOnly)
            {
                if (rawValue != null) SetSingleRelationCore(entity, metadata, rawValue, rawSize);
                else
                {
                    if (value == null) throw new ArgumentNullException(nameof(value));
                    if (value.GetType() != metadata.Type) throw new ArgumentException("Relation value type does not match its column.", nameof(value));
                    var pinned = GCHandle.Alloc(value, GCHandleType.Pinned);
                    try { SetSingleRelationCore(entity, metadata, (byte*)pinned.AddrOfPinnedObject(), metadata.Size); }
                    finally { pinned.Free(); }
                }
                return;
            }
            ref var entityInfoRef = ref GetAliveInfoRef(entity);
            var oldArch = entityInfoRef.Archetype;
            if (oldArch.HasComponent(metadata))
            {
                if (rawValue != null)
                    WriteColumnBytes(oldArch, entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, metadata, rawValue, rawSize);
                else
                    oldArch.SetStorageRaw(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, metadata, value);
                return;
            }

            // 命中缓存的原型边，跳过重新匹配
            var compType = metadata;
            var targetArch = knownTarget ?? GetComponentDestination(oldArch, compType, adding: true);
            AddToPreparedChunk(targetArch, entity, preparedChunk, out var chunkIndex, out var slotInChunk);

            // 复制组件数据
            oldArch.CopyComponentsTo(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, targetArch, chunkIndex, slotInChunk);

            // 从旧原型移除（含 swap-pop 搬移实体与空 chunk 压缩的位置修正）
            RemoveAndFixup(oldArch, entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, skipDisposal: metadata.IsRelation);

            UpdateEntityLocation(entity.Id, targetArch, chunkIndex, slotInChunk);
            if (rawValue != null)
                WriteColumnBytes(targetArch, chunkIndex, slotInChunk, metadata, rawValue, rawSize);
            else
                targetArch.SetStorageRaw(chunkIndex, slotInChunk, metadata, value);
            InvalidateStructuralQueries();

            // Observer：Added（迁移完成后派发，回调内实体可查、新组件已存在；count=1）
            if (!metadata.IsRelation && _observerCount > 0 && _observers != null &&
                _observers.TryGetValue(compType.Id, out var reg) && reg.Added.Count > 0)
            {
                int compIdx = targetArch.GetComponentTypeIndex(compType);
                void* valPtr = (void*)(targetArch.ChunkList[chunkIndex].GetComponentArrayPointer(compIdx) + slotInChunk * compType.Size);
                DispatchAdded(reg.Added, &entity, valPtr, 1);
            }
        }

        /// <summary>
        /// 移除组件（非泛型版本，核心实现）
        /// </summary>
        public void RemoveComponentRaw(Entity entity, Type componentType)
        {
            RemoveComponentRaw(entity, ComponentTypeManager.GetComponentType(componentType));
        }

        /// <summary>按已解析的组件元数据移除：泛型入口不必再查类型字典。</summary>
        public void RemoveComponentRaw(Entity entity, ComponentType metadata)
        {
            CheckDisposed();
            bool relationship = metadata.IsRelation;
            if (relationship) CompleteActiveJobs();
            using var relationChanges = relationship ? BeginRelationChanges() : default;
            // 组件存在时才解析迁移目标 Archetype（不存在时核心直接 return，只等实体所属 Archetype）。
            Archetype? targetArch = null;
            if ((uint)entity.Id < (uint)entities.Length)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                if (info.Archetype != null && info.Archetype.HasComponent(metadata))
                    targetArch = info.Archetype.GetRemoveEdge(metadata);
            }
            if (!JobBookkeepingIdle()) CompleteEntityJobs(entity, targetArch);
            using (EnterStructural())
            {
                RemoveComponentRawCore(entity, metadata, knownTarget: targetArch);
            }
        }

        /// <summary>
        /// 移除组件核心（调用方必须已持有 _structuralLock）。
        /// </summary>
        private unsafe void RemoveComponentRawCore(Entity entity, ComponentType metadata, bool relationStorageOnly = false, nint preparedChunk = 0, Archetype knownTarget = null)
        {
            if (metadata.IsRelation && !relationStorageOnly)
            {
                RemoveRelationStorage(entity, metadata);
                return;
            }

            ref var entityInfoRef = ref GetAliveInfoRef(entity);
            var oldArch = entityInfoRef.Archetype;
            if (!oldArch.HasComponent(metadata)) return;

            // 快照必须在迁移前取：迁移后旧值就从原位置搬走了。
            byte* oldValueSnapshot = SnapshotRemovedValue(metadata, oldArch, entityInfoRef);
            var targetArch = knownTarget ?? GetComponentDestination(oldArch, metadata, adding: false);
            AddToPreparedChunk(targetArch, entity, preparedChunk, out var chunkIndex, out var slotInChunk);
            oldArch.CopyComponentsTo(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, targetArch, chunkIndex, slotInChunk);
            RemoveAndFixup(oldArch, entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, skipDisposal: metadata.IsRelation);
            UpdateEntityLocation(entity.Id, targetArch, chunkIndex, slotInChunk);
            targetArch.ChunkList[chunkIndex].MarkEntityChanged(slotInChunk);
            InvalidateStructuralQueries();
            DispatchRemovedValue(metadata, entity, oldValueSnapshot);
        }

        // 关系列的移除走各自的关系 API：多值关系先清列表/列，单值关系交给 RemoveRelationCore。
        private void RemoveRelationStorage(Entity entity, ComponentType metadata)
        {
            if (!metadata.IsMultiRelation)
            {
                RemoveRelationCore(entity, metadata);
                return;
            }
            if (!IsAlive(entity)) return;
            var source = GetEntityInfoRef(entity.Id).Archetype;
            bool hasColumn = source.HasComponent(metadata);
            nint destination = hasColumn ? PrepareRelationSlot(GetComponentDestination(source, metadata, false), entity) : 0;
            if (hasColumn) source.PrepareEntityRemoval();
            ClearMultiRelationshipsRaw(entity, metadata);
            if (hasColumn) RemoveComponentRawCore(entity, metadata, relationStorageOnly: true, preparedChunk: destination);
        }

        // 迁移前拷贝一份旧值给 Removed 观察者；IDisposable 组件的快照在移除后悬垂，因此跳过。
        private unsafe byte* SnapshotRemovedValue(ComponentType metadata, Archetype oldArch, in EntityIndexInWorld info)
        {
            if (metadata.IsRelation || metadata.IsDisposable || _observerCount == 0 || _observers == null) return null;
            if (!_observers.TryGetValue(metadata.Id, out var registry) || registry.Removed.Count == 0) return null;
            int compIdx = oldArch.GetComponentTypeIndex(metadata);
            var chunk = oldArch.ChunkList[info.ChunkIndex];
            void* srcPtr = (void*)(chunk.GetComponentArrayPointer(compIdx) + info.SlotInChunk * metadata.Size);
            var snapshot = (byte*)Marshal.AllocHGlobal(metadata.Size);
            Unsafe.CopyBlock(snapshot, srcPtr, (uint)metadata.Size);
            return snapshot;
        }

        // 迁移完成后派发 Removed，回调内实体已在目标原型里可查（count = 1）。
        private unsafe void DispatchRemovedValue(ComponentType metadata, Entity entity, byte* snapshot)
        {
            if (snapshot == null) return;
            try
            {
                if (_observers!.TryGetValue(metadata.Id, out var registry) && registry.Removed.Count > 0)
                    DispatchRemoved(registry.Removed, &entity, snapshot, 1);
            }
            finally
            {
                Marshal.FreeHGlobal((IntPtr)snapshot);
            }
        }
    }
}
