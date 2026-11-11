using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager : IDisposable
    {
        // 创建带初始共享值的实体

        /// <summary>
        /// 创建实体并指定初始共享值（per-chunk 分组）。
        /// <paramref name="sharedValues"/>：每个元素为 (shared 组件类型, 值)；类型必须是 ISharedComponentData。
        /// 实体进入持有相同值组合的未满 chunk；不存在则新建 chunk（写入初始值）。
        /// </summary>
        public Entity NewEntity(Span<ComponentType> types, params (Type type, object value)[] sharedValues)
        {
            CheckDisposed();
            CompleteActiveJobs();
            using (EnterStructural())
            {
                var targetArch = GetOrCreateArchetype(types);
                if (sharedValues == null || sharedValues.Length == 0)
                    return CreateEntityInChunk(targetArch, -1);

                ResolveSharedSlots(targetArch, sharedValues,
                    out bool[] slotSet, out object[] slotValue, out int[] managedIdx);

                int chunkIdx = FindExistingChunkForShared(targetArch, slotSet, managedIdx, slotValue);
                if (chunkIdx == -1) chunkIdx = CreateSharedChunk(targetArch, slotSet, slotValue, managedIdx);
                return CreateEntityInChunk(targetArch, chunkIdx);
            }
        }

        // 解析每个 shared 槽位的目标值：managed shared 记索引，blittable 记 boxed 值。
        private void ResolveSharedSlots(Archetype arch, (Type type, object value)[] sharedValues,
            out bool[] slotSet, out object[] slotValue, out int[] managedIdx)
        {
            slotSet = new bool[arch.ComponentCount];
            slotValue = new object[arch.ComponentCount];
            managedIdx = new int[arch.ComponentCount];
            Array.Fill(managedIdx, -1);
            for (int s = 0; s < sharedValues.Length; s++)
            {
                var (stype, svalue) = sharedValues[s];
                if (svalue == null)
                    throw new ArgumentNullException(nameof(sharedValues), $"Shared value for {stype} cannot be null.");
                int compIdx = arch.GetComponentTypeIndex(ComponentTypeManager.GetComponentType(stype));
                var ct = arch.Types[compIdx];
                if (!ct.IsShared)
                    throw new ArgumentException(
                        $"Type {stype.FullName} is not an ISharedComponentData. Only shared components can take an initial value.",
                        nameof(sharedValues));
                slotSet[compIdx] = true;
                slotValue[compIdx] = svalue;
                if (ct.IsManagedShared) managedIdx[compIdx] = FindOrAddManagedValue(ct.Id, svalue);
            }
        }

        // 没有匹配的现成块时新建一个，并把初始 shared 值写进去。
        private int CreateSharedChunk(Archetype arch, bool[] slotSet, object[] slotValue, int[] managedIdx)
        {
            int chunkIdx = arch.CreateChunk();
            var newChunk = arch.ChunkList[chunkIdx];
            for (int i = 0; i < arch.ComponentCount; i++)
            {
                if (!slotSet[i]) continue;
                if (arch.Types[i].IsManagedShared) newChunk.SetSharedValueIndex(i, managedIdx[i]);
                else WriteBlittableSharedBoxed(newChunk, i, slotValue[i], arch.Types[i].Size);
            }
            return chunkIdx;
        }

        // 登记新实体的位置、版本与查询失效；chunkIdx 为 -1 时由原型挑一个可容纳的块。
        private Entity CreateEntityInChunk(Archetype arch, int chunkIdx)
        {
            var entity = AllocateEntityId();
            int chunk, slot;
            if (chunkIdx < 0)
            {
                arch.AddEntity(entity, out chunk, out slot);
            }
            else
            {
                arch.AddEntityToChunk(entity, chunkIdx);
                chunk = chunkIdx;
                slot = arch.ChunkList[chunkIdx].EntityCount - 1;
            }
            UpdateEntityLocation(entity.Id, arch, chunk, slot);
            GetEntityInfoRef(entity.Id).Version = entity.Version;
            InvalidateStructuralQueries();
            return entity;
        }

        /// <summary>查找所有 slotSet 槽位与目标值匹配的未满 chunk；无返回 -1。
        /// 单 shared 列走缓存路径（复用 FindChunkWithManagedValue/BlittableBoxed，O(1) 期望）；
        /// 多列组合保持线性扫描（组合 key 无法复用单值缓存，且场景低频）。</summary>
        private int FindExistingChunkForShared(Archetype arch, bool[] slotSet, int[] managedIdx, object?[] slotValue,
            Dictionary<nint, int> reserved = null, nint excluded = 0)
        {
            // 统计已设置的 shared 列数；单列时走缓存路径
            int setCount = 0, singleIdx = -1;
            for (int c = 0; c < arch.ComponentCount; c++)
            {
                if (slotSet[c])
                {
                    setCount++;
                    singleIdx = c;
                }
            }

            if (setCount == 1 && reserved == null && excluded == 0)
            {
                var singleCt = arch.Types[singleIdx];
                return singleCt.IsManagedShared
                    ? FindChunkWithManagedValue(arch, singleIdx, managedIdx[singleIdx])
                    : FindChunkWithBlittableBoxed(arch, singleIdx, slotValue[singleIdx], singleCt.Type);
            }

            var list = arch.ChunkList;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].MemoryBlock == excluded) continue;
                int pending = reserved != null && reserved.TryGetValue(list[i].MemoryBlock, out int reservedCount) ? reservedCount : 0;
                if (list[i].EntityCount + pending >= list[i].Capacity) continue;
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
    }
}
