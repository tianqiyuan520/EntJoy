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
    public unsafe partial class EntityManager
    {
        // Chunk 碎片整理

        /// <summary>
        /// 合并所有 Archetype 中共享值组合一致的瘦 Chunk（利用率 &lt; thresholdPercent）。
        /// 减少 Chunk 数量，提升查询遍历效率；组件搬移采用 move 语义转移所有权。
        /// 结构性 API（需持有锁 + 先 CompleteActiveJobs），主线程调用。
        /// </summary>
        public void CompactChunks(float thresholdPercent = 30f)
        {
            CheckDisposed();
            CompleteActiveJobs();
            using (EnterStructural())
            {
                try
                {
                    for (int i = 0; i < archetypeCount; i++)
                    {
                        var arch = allArchetypes[i];
                        if (arch != null && arch.ChunkCount > 1)
                        {
                            // 先回退游标，覆盖块交换后的空位和部分完成的压缩。
                            arch.ResetChunkRoomSearch();
                            CompactArchetype(arch, thresholdPercent);
                        }
                    }
                }
                finally
                {
                    // 共享值比较可抛出；此前完成的块移除仍使已有借用失效。
                    InvalidateStructuralQueries();
                }
            }
        }

        private unsafe void CompactArchetype(Archetype arch, float thresholdPercent)
        {
            var span = CollectionsMarshal.AsSpan(arch.ChunkList);
            // 从后往前遍历；每次增删块后重新取 span，避免索引失效。
            for (int i = span.Length - 1; i >= 0 && span.Length > 1; i--)
            {
                ref var thin = ref span[i];
                if (thin.EntityCount == 0)
                {
                    // 空块由这里统一移除并归还内存（销毁路径不再即时回收）。
                    RemoveEmptyChunkAt(arch, i);
                    span = CollectionsMarshal.AsSpan(arch.ChunkList);
                    continue;
                }
                if (thin.EntityCount * 100f >= thin.Capacity * thresholdPercent) continue;

                int targetIdx = FindFirstChunkWithRoom(span, i);
                if (targetIdx < 0) continue;
                // thin 前面的可用空间可能不足（MoveEntitiesTo 会提前停）：
                // 没搬空就不能移除该块，否则会静默丢实体并跳过它们的 IDisposable 钩子。
                if (!MoveEntitiesTo(arch, span, i, targetIdx)) continue;

                RemoveEmptyChunkAt(arch, i);
                span = CollectionsMarshal.AsSpan(arch.ChunkList);
            }
        }

        // 在 limit 之前找第一个还能放下实体的块；没有则返回 -1。
        private int FindFirstChunkWithRoom(Span<Chunk> span, int limit)
        {
            for (int j = 0; j < limit; j++)
                if (span[j].EntityCount < span[j].Capacity && SharedValuesMatch(span[limit], span[j])) return j;
            return -1;
        }

        private bool SharedValuesMatch(Chunk source, Chunk target)
        {
            var types = source.Archetype.Types;
            for (int c = 0; c < types.Length; c++)
            {
                var type = types[c];
                if (!type.IsShared) continue;
                if (type.IsManagedShared)
                {
                    if (source.GetSharedValueIndex(c) != target.GetSharedValueIndex(c)) return false;
                }
                else if (!EqualityComparer<object>.Default.Equals(
                    ReadBlittableSharedBoxed(source, c, type.Type), ReadBlittableSharedBoxed(target, c, type.Type)))
                    return false;
            }
            return true;
        }

        /// <summary>把 thin 里的实体搬到前面的未满 Chunk；返回 thin 是否已搬空。</summary>
        private unsafe bool MoveEntitiesTo(Archetype arch, Span<Chunk> span, int thinIdx, int startTargetIdx)
        {
            ref var thin = ref span[thinIdx];
            int targetIdx = startTargetIdx;

            while (thin.EntityCount > 0)
            {
                // 只搬入共享值组合一致的未满 Chunk，目标限制在 thin 前面。
                while (targetIdx < thinIdx && (span[targetIdx].EntityCount >= span[targetIdx].Capacity
                    || !SharedValuesMatch(thin, span[targetIdx])))
                    targetIdx++;
                if (targetIdx >= thinIdx)
                    break;  // 前面没有可用目标，无法继续合并

                ref var target = ref span[targetIdx];
                int lastSlot = thin.EntityCount - 1;
                Entity e = thin.GetEntity(lastSlot);

                target.AddEntity(e);
                int targetSlot = target.EntityCount - 1;

                // move 语义（IDisposable 组件转移所有权，普通组件位拷贝）
                arch.CopyComponentsTo(thinIdx, lastSlot, arch, targetIdx, targetSlot);

                // 复制 enableable 位（AddEntity 默认设为启用，需还原源实体的 enable 状态）
                for (int comp = 0; comp < thin.Meta.ComponentCount; comp++)
                {
                    if (thin.Meta.EnableBitOffsets[comp] == -1) continue;
                    target.SetComponentEnabled(comp, targetSlot, thin.GetComponentEnabled(comp, lastSlot));
                }

                // 移除源（最后一个，无 swap-pop；源组件已被 move 清空，Dispose no-op）
                thin.RemoveEntity(lastSlot, continueOnDisposeError: false, skipDisposal: true);

                UpdateEntityLocation(e.Id, arch, targetIdx, targetSlot);
            }

            return thin.EntityCount == 0;
        }

        private void RemoveEmptyChunkAt(Archetype arch, int chunkIndex)
        {
            var list = arch.ChunkList;
            int lastIndex = list.Count - 1;
            // 空 chunk 的内存块（swap 前记录：swap 后 chunkIndex 位置被 last 覆盖）
            nint freedChunk = list[chunkIndex].MemoryBlock;
            if (chunkIndex != lastIndex)
            {
                list[chunkIndex] = list[lastIndex];
            }
            list.RemoveAt(lastIndex);
            arch.ReleaseChunkMemory(freedChunk);  // 复用空洞 / 归还空 slab
            if (chunkIndex < list.Count)
                RefreshChunkEntityIndices(arch, chunkIndex);
        }

    }
}
