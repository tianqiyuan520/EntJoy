using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.ECS
{
    /// <summary>
    /// EntityManager 的 Shared Component 支持（per-chunk 存储，对齐 Unity DOTS）。
    ///
    /// 双类型策略：
    /// - blittable shared：值内联存储于 Chunk 内存块 Shared values 区。
    /// - managed shared：值存于 EntityManager 扁平数组（去重索引），Chunk 槽位只存 int 索引。
    ///   值只增不减，World.Dispose 时整体清空 → GC 回收。
    ///
    /// 核心不变式：同一 Chunk 的所有实体共享相同的 SharedComponent 值组合。
    /// 改变 shared 值 → 实体在 Archetype 内部移动到持有目标值的 Chunk（无 Archetype 变更）。
    /// </summary>
    public unsafe partial class EntityManager
    {
        // ======================== managed shared 值存储（简化版：无 refcount，值只增不减） ========================

        /// <summary>managed shared 值本体（全局去重数组）。World.Dispose 时清空。</summary>
        private object[] _managedSharedValues = new object[16];
        private int _managedSharedValueCount;

        /// <summary>per-type 查找表：typeId → (value → globalIndex)。Dictionary 简洁可靠，值种类通常 &lt;200。</summary>
        private readonly Dictionary<int, Dictionary<object, int>> _managedLookup = new();

        /// <summary>查找或添加 managed shared 值（O(1)，去重）。index 只增不减。</summary>
        private int FindOrAddManagedValue(int typeId, object value)
        {
            if (!_managedLookup.TryGetValue(typeId, out var dict))
            {
                dict = new Dictionary<object, int>();
                _managedLookup[typeId] = dict;
            }
            if (dict.TryGetValue(value, out int idx))
                return idx;

            idx = _managedSharedValueCount++;
            if (idx >= _managedSharedValues.Length)
                Array.Resize(ref _managedSharedValues, _managedSharedValues.Length * 2);
            _managedSharedValues[idx] = value;
            dict[value] = idx;
            return idx;
        }

        // ======================== 读取 / 写入 API ========================

        /// <summary>获取实体的共享组件值（blittable 读 chunk 内存块内联值；managed 读索引 → 值数组）。</summary>
        public T GetSharedComponent<T>(Entity entity) where T : ISharedComponentData
        {
            CheckDisposed();
            ValidateEntity(entity);
            var info = GetEntityInfoRef(entity.Id);
            var arch = info.Archetype;
            int compIdx = arch.GetComponentTypeIndex(ComponentTypeManager.GetComponentType(typeof(T)));
            var chunk = arch.ChunkList[info.ChunkIndex];
            var ct = arch.Types[compIdx];

            if (ct.IsManagedShared)
            {
                int idx = chunk.GetSharedValueIndex(compIdx);
                return (T)_managedSharedValues[idx];
            }
            return (T)ReadBlittableSharedBoxed(chunk, compIdx, ct.Type);
        }

        /// <summary>
        /// 设置实体的共享组件值（per-chunk 语义，对齐 Unity DOTS）：
        /// 1. 所在 chunk 单实体 → 就地改值（无移动）。
        /// 2. 多实体且值不同 → Archetype 内找/建目标值 chunk，swap-pop 移动实体。
        /// 3. 值相同 → 无操作。
        /// </summary>
        public void SetSharedComponent<T>(Entity entity, T value) where T : ISharedComponentData
        {
            CheckDisposed();
            CompleteActiveJobs();
            lock (_structuralLock)
            {
                ValidateEntity(entity);
                ref var info = ref GetEntityInfoRef(entity.Id);
                var arch = info.Archetype;
                int compIdx = arch.GetComponentTypeIndex(ComponentTypeManager.GetComponentType(typeof(T)));
                var ct = arch.Types[compIdx];
                int srcChunkIndex = info.ChunkIndex;
                var srcChunk = arch.ChunkList[srcChunkIndex];

                if (ct.IsManagedShared)
                {
                    int oldIdx = srcChunk.GetSharedValueIndex(compIdx);
                    int newIdx = FindOrAddManagedValue(ct.Id, value);

                    // 值相同 → 无操作
                    if (oldIdx == newIdx) return;

                    // 单实体 chunk → 就地改索引
                    if (srcChunk.EntityCount == 1)
                    {
                        srcChunk.SetSharedValueIndex(compIdx, newIdx);
                        srcChunk.MarkEntityChanged(info.SlotInChunk);
                        return;
                    }

                    // 多实体：找持有新值的未满 chunk；无则新建
                    int targetChunk = FindChunkWithManagedValue(arch, compIdx, newIdx);
                    if (targetChunk < 0)
                    {
                        targetChunk = CreateChunkWithSharedIndex(arch, compIdx, newIdx);
                    }
                    MoveEntityToChunk(entity, arch, srcChunkIndex, targetChunk);
                    // 移动后标记新位置变更
                    arch.ChunkList[targetChunk].MarkEntityChanged(arch.ChunkList[targetChunk].EntityCount - 1);
                }
                else
                {
                    // blittable shared
                    object curVal = ReadBlittableSharedBoxed(srcChunk, compIdx, ct.Type);
                    if (EqualityComparer<object>.Default.Equals(curVal, value)) return;

                    if (srcChunk.EntityCount == 1)
                    {
                        WriteBlittableSharedBoxed(srcChunk, compIdx, value, ct.Size);
                        srcChunk.MarkEntityChanged(info.SlotInChunk);
                        return;
                    }

                    int targetChunk = FindChunkWithBlittableBoxed(arch, compIdx, value, ct.Type);
                    if (targetChunk < 0)
                        targetChunk = CreateChunkWithBlittableBoxed(arch, compIdx, value, ct.Type, ct.Size);
                    MoveEntityToChunk(entity, arch, srcChunkIndex, targetChunk);
                    arch.ChunkList[targetChunk].MarkEntityChanged(arch.ChunkList[targetChunk].EntityCount - 1);
                }
            }
        }

        /// <summary>在 Archetype 内查找持有指定 managed index 的未满 chunk；无返回 -1。</summary>
        private int FindChunkWithManagedValue(Archetype arch, int compIdx, int index)
        {
            var list = arch.ChunkList;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].EntityCount < list[i].Capacity && list[i].GetSharedValueIndex(compIdx) == index)
                    return i;
            }
            return -1;
        }

        /// <summary>在 Archetype 内查找持有指定 boxed blittable 值的未满 chunk；无返回 -1。</summary>
        private int FindChunkWithBlittableBoxed(Archetype arch, int compIdx, object value, Type compType)
        {
            var list = arch.ChunkList;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].EntityCount >= list[i].Capacity) continue;
                object existing = ReadBlittableSharedBoxed(list[i], compIdx, compType);
                if (EqualityComparer<object>.Default.Equals(existing, value))
                    return i;
            }
            return -1;
        }

        /// <summary>创建持有指定 managed index 的新 chunk。</summary>
        private int CreateChunkWithSharedIndex(Archetype arch, int compIdx, int index)
        {
            int chunkIdx = arch.CreateChunk();
            arch.ChunkList[chunkIdx].SetSharedValueIndex(compIdx, index);
            return chunkIdx;
        }

        /// <summary>创建持有指定 boxed blittable 值的新 chunk。</summary>
        private int CreateChunkWithBlittableBoxed(Archetype arch, int compIdx, object value, Type compType, int size)
        {
            int chunkIdx = arch.CreateChunk();
            WriteBlittableSharedBoxed(arch.ChunkList[chunkIdx], compIdx, value, size);
            return chunkIdx;
        }

        /// <summary>
        /// 将实体从 srcChunk 移动到 arch 内的 dstChunk（swap-pop，组件全量复制）。
        /// </summary>
        private void MoveEntityToChunk(Entity entity, Archetype arch, int srcChunkIndex, int dstChunkIndex)
        {
            ref var info = ref GetEntityInfoRef(entity.Id);
            int srcSlot = info.SlotInChunk;

            var dstChunk = arch.ChunkList[dstChunkIndex];
            int dstSlot = dstChunk.EntityCount;
            arch.AddEntityToChunk(entity, dstChunkIndex);

            arch.CopyComponentsTo(srcChunkIndex, srcSlot, arch, dstChunkIndex, dstSlot);

            arch.Remove(srcChunkIndex, srcSlot, out var movedId, out var movedSlot, out var compactedIdx);
            if (movedId >= 0)
                UpdateEntityLocation(movedId, arch, srcChunkIndex, movedSlot);
            if (compactedIdx >= 0)
                RefreshChunkEntityIndices(arch, compactedIdx);

            UpdateEntityLocation(entity.Id, arch, dstChunkIndex, dstSlot);
            structuralVersion++;
        }

        // ======================== 创建带初始共享值的实体 ========================

        /// <summary>
        /// 创建实体并指定初始共享值（per-chunk 分组）。
        /// <paramref name="sharedValues"/>：每个元素为 (shared 组件类型, 值)；类型必须是 ISharedComponentData。
        /// 实体进入持有相同值组合的未满 chunk；不存在则新建 chunk（写入初始值）。
        /// </summary>
        public Entity NewEntity(Span<ComponentType> types, params (Type type, object value)[] sharedValues)
        {
            CheckDisposed();
            CompleteActiveJobs();
            lock (_structuralLock)
            {
                var targetArch = GetOrCreateArchetype(types);

                if (sharedValues == null || sharedValues.Length == 0)
                {
                    var e = AllocateEntityId();
                    targetArch.AddEntity(e, out var cIdx, out var slot);
                    UpdateEntityLocation(e.Id, targetArch, cIdx, slot);
                    GetEntityInfoRef(e.Id).Version = e.Version;
                    structuralVersion++;
                    return e;
                }

                // 解析每个 shared 槽位的目标值
                int[] managedIdx = new int[targetArch.ComponentCount];
                Array.Fill(managedIdx, -1);
                object[] slotValue = new object[targetArch.ComponentCount];
                bool[] slotSet = new bool[targetArch.ComponentCount];

                for (int s = 0; s < sharedValues.Length; s++)
                {
                    var (stype, svalue) = sharedValues[s];
                    if (svalue == null)
                        throw new ArgumentNullException(nameof(sharedValues), $"Shared value for {stype} cannot be null.");

                    int compIdx = targetArch.GetComponentTypeIndex(ComponentTypeManager.GetComponentType(stype));
                    var ct = targetArch.Types[compIdx];
                    if (!ct.IsShared)
                        throw new ArgumentException(
                            $"Type {stype.FullName} is not an ISharedComponentData. Only shared components can take an initial value.",
                            nameof(sharedValues));
                    slotSet[compIdx] = true;
                    slotValue[compIdx] = svalue;

                    if (ct.IsManagedShared)
                        managedIdx[compIdx] = FindOrAddManagedValue(ct.Id, svalue);
                }

                // 找已有 chunk：所有槽位匹配且未满
                int chunkIdx = FindExistingChunkForShared(targetArch, slotSet, managedIdx, slotValue);

                if (chunkIdx == -1)
                {
                    // 新建 chunk：写入所有槽位
                    chunkIdx = targetArch.CreateChunk();
                    var newChunk = targetArch.ChunkList[chunkIdx];
                    for (int i = 0; i < targetArch.ComponentCount; i++)
                    {
                        if (!slotSet[i]) continue;
                        if (targetArch.Types[i].IsManagedShared)
                            newChunk.SetSharedValueIndex(i, managedIdx[i]);
                        else
                            WriteBlittableSharedBoxed(newChunk, i, slotValue[i], targetArch.Types[i].Size);
                    }
                }

                var entity = AllocateEntityId();
                targetArch.AddEntityToChunk(entity, chunkIdx);
                UpdateEntityLocation(entity.Id, targetArch, chunkIdx, targetArch.ChunkList[chunkIdx].EntityCount - 1);
                GetEntityInfoRef(entity.Id).Version = entity.Version;
                structuralVersion++;
                return entity;
            }
        }

        /// <summary>查找所有 slotSet 槽位与目标值匹配的未满 chunk；无返回 -1。</summary>
        private int FindExistingChunkForShared(Archetype arch, bool[] slotSet, int[] managedIdx, object[] slotValue)
        {
            var list = arch.ChunkList;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].EntityCount >= list[i].Capacity) continue;
                bool match = true;
                for (int c = 0; c < arch.ComponentCount; c++)
                {
                    if (!slotSet[c]) continue;
                    if (arch.Types[c].IsManagedShared)
                    {
                        if (list[i].GetSharedValueIndex(c) != managedIdx[c]) { match = false; break; }
                    }
                    else
                    {
                        object existing = ReadBlittableSharedBoxed(list[i], c, arch.Types[c].Type);
                        if (!EqualityComparer<object>.Default.Equals(existing, slotValue[c])) { match = false; break; }
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        // ======================== 工具方法 ========================

        /// <summary>读取 chunk 槽位的 blittable shared 值（boxed，非热路径用）。</summary>
        private object ReadBlittableSharedBoxed(Chunk chunk, int compIdx, Type compType)
        {
            var ptr = chunk.GetSharedValuePointer(compIdx);
            if (ptr == nint.Zero) return null;
            return Marshal.PtrToStructure(ptr, compType);
        }

        /// <summary>将 boxed blittable shared 值写入 chunk 槽位（GCHandle pin，非热路径用）。</summary>
        private void WriteBlittableSharedBoxed(Chunk chunk, int compIdx, object value, int size)
        {
            var ptr = chunk.GetSharedValuePointer(compIdx);
            if (ptr == nint.Zero) return;
            var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                Buffer.MemoryCopy((void*)handle.AddrOfPinnedObject(), (void*)ptr, size, size);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>读取 chunk 槽位的 blittable shared 值（boxed，查询过滤用）。</summary>
        internal object ReadBlittableShared(Chunk chunk, int compIdx, Type compType)
        {
            return ReadBlittableSharedBoxed(chunk, compIdx, compType);
        }

        /// <summary>按 typeId + index 读取 managed shared 值（查询过滤用）。</summary>
        internal object GetManagedSharedValueById(int typeId, int index)
        {
            if (index >= 0 && index < _managedSharedValueCount)
                return _managedSharedValues[index];
            return null;
        }

        /// <summary>分配实体 id（复用回收队列或递增）。</summary>
        private Entity AllocateEntityId()
        {
            var newEntity = new Entity();
            if (recycleEntities.TryDequeue(out var recycledEnt))
            {
                newEntity.Id = recycledEnt.Id;
                newEntity.Version = recycledEnt.Version + 1;
            }
            else
            {
                newEntity.Id = entityCount++;
                if (newEntity.Id >= entities.Length)
                    Array.Resize(ref entities, entities.Length * 2);
            }
            return newEntity;
        }
    }
}