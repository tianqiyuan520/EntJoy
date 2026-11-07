using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 实体关系操作（单实例 SoA 列）+ 级联删除。
    /// 关系值首字段为 RelationSlot（target + version），后续字段保存业务数据。
    /// 反向索引（RelationIndex）：Add/Remove/覆盖时同步维护，DestroyEntityCascade 时 O(1) 查索引。
    /// </summary>
    public unsafe partial class EntityManager
    {
        // P1：遍历 API 分配消除
        // 每次调用的 List/HashSet 分配。约束：主线程单线程使用、不可重入（遍历中不得再调用
        // 遍历 API——当前 API 无回调，天然满足）；返回值数组独立 new，调用方可安全持有。

        private readonly List<Entity> _relBufferA = new();   // 结果暂存（GetAncestors/Siblings/RelationsOfAll）
        private readonly List<Entity> _relBufferB = new();   // BFS frontier
        private readonly List<Entity> _relBufferC = new();   // BFS next（与 B 交换）
        private readonly HashSet<int> _relVisited = new();   // 防环（存 Id）

        /// <summary>
        /// 建立关系：entity --TRel--> target。
        /// 首次（实体无 TRel 列）触发结构变更（AddComponentRaw）；已有关系直接覆盖（SetRaw，零结构变更）。
        /// 同步维护反向索引（覆盖先去旧 target 条目）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void AddRelationship<TRel>(Entity entity, Entity target)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            var type = ComponentTypeManager.GetComponentType(typeof(TRel));
            if (!type.IsMultiRelation) { SetRelationshipTargetRaw(entity, target, type); return; }
            CompleteRelationWriteJobs(entity, type);
            using var changes = BeginRelationChanges();
            lock (_structuralLock)
            {
                ValidateRelationshipEntity(entity, type, "source");
                ValidateRelationshipEntity(target, type, "target");
                AddMultiRelationshipCore<TRel>(entity, target, type.Id);
            }
        }

        /// <summary>移除单目标关系列；多目标类型清空全部边。</summary>
        public void RemoveRelationship<TRel>(Entity entity) where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            var type = ComponentTypeManager.GetComponentType(typeof(TRel));
            if (type.IsMultiRelation) { ClearRelationships<TRel>(entity); return; }
            RemoveRelationshipRaw(entity, type);
        }

        /// <summary>
        /// 读取关系 target。无 TRel 组件 → default(Entity)；
        /// 有组件但槽位无效 / target 已销毁 / Id 复用版本不匹配 → default(Entity)。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Entity GetRelationship<TRel>(Entity entity) where TRel : struct, IRelationComponent
            => TryGetRelationship<TRel>(entity, out var target) ? target : default;

        /// <summary>读取有效目标，同时检查两端完整身份。</summary>
        public bool TryGetRelationship<TRel>(Entity entity, out Entity target) where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            var type = ComponentTypeManager.GetComponentType(typeof(TRel));
            if (type.IsMultiRelation)
                throw new InvalidOperationException($"Use GetRelationships for multi-relation '{type.Type}'.");
            target = default;
            if (!IsAlive(entity) || !GetEntityInfoRef(entity.Id).Archetype.Has(type.Type)) return false;
            var slot = *(RelationSlot*)GetRelationValue(entity, type);
            if (!slot.IsValid || !IsAlive(slot.ToEntity())) return false;
            target = slot.ToEntity();
            return true;
        }

        public bool HasRelationship<TRel>(Entity entity) where TRel : struct, IRelationComponent
            => TryGetRelationship<TRel>(entity, out _);

        /// <summary>
        /// 获取所有与 target 建立 TRel 关系的 source 实体（target ←TRel-- sources）。
        /// 走反向索引 O(1)，不扫描任何 chunk；同类型的 source 去重，顺序不保证。
        /// 返回数组（新分配，调用方可安全持有）。
        /// </summary>
        public Entity[] GetRelationsOf<TRel>(Entity target)
            where TRel : struct, IRelationComponent
        {
            var sources = GetRelationSources<TRel>(target);
            if (sources == null || sources.Count == 0) return Array.Empty<Entity>();
            var result = new Entity[sources.Count];
            int count = GetRelationsOf<TRel>(target, result.AsSpan());
            if (count != result.Length) Array.Resize(ref result, count);
            return result;
        }

        /// <summary>获取所有指向 target 的关系源（跨所有关系类型）。O(1) 索引查表。
        /// 复用容器（P1）：结果 List 复用，返回值数组独立 new。</summary>
        public Entity[] GetRelationsOfAll(Entity target)
        {
            CheckDisposed();
            if (!IsAlive(target) || !_relationIndex.TryGetSources(target, out var byType))
                return Array.Empty<Entity>();

            var result = _relBufferA;
            result.Clear();
            _relVisited.Clear();
            foreach (var typed in byType)
            {
                foreach (var source in typed.Value)
                {
                    if (StillPointsToRelation(source, target, typed.Key) && _relVisited.Add(source.Id))
                        result.Add(source);
                }
            }
            return result.ToArray();
        }

        /// <summary>实体是否存活（Id 范围内 + Archetype 非空 + 版本匹配）。</summary>
        private bool IsAlive(Entity e)
        {
            if ((uint)e.Id >= (uint)entities.Length) return false;
            ref var info = ref GetEntityInfoRef(e.Id);
            return info.Archetype != null && info.Version == e.Version;
        }

        // 关系遍历 API（借鉴 Bevy iter_ancestors/descendants/siblings）

        /// <summary>
        /// 尝试读取 entity 的 TRel 关系 target（存活 + 有效槽位 + 版本匹配）。
        /// 与 <see cref="GetRelationship{TRel}"/> 不同：无关系时返回 false（而非 default(Entity)，
        /// 后者的 Id=0 会与真实实体 0 冲突，遍历场景必须用本方法）。
        /// </summary>
        private bool TryGetRelationshipTarget<TRel>(Entity entity, out Entity target)
            where TRel : struct, IRelationComponent => TryGetRelationship<TRel>(entity, out target);

        /// <summary>
        /// 获取 entity 的全部祖先（沿 TRel 链向上）：最近的祖先在前，根在后。
        /// 单实例语义（每实体每关系类型最多 1 target），链式向上；visited 防环（含起始实体）。
        /// 复用容器（P1）：内部 List/HashSet 复用，返回值数组独立 new。
        /// </summary>
        public Entity[] GetAncestors<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            var result = _relBufferA;
            result.Clear();
            var visited = _relVisited;
            visited.Clear();
            visited.Add(entity.Id);  // 含起始实体：环闭合时立即终止

            var current = entity;
            while (IsAlive(current))
            {
                if (!TryGetRelationshipTarget<TRel>(current, out var parent)) break;  // 无关系/失效 → 链尾
                if (!visited.Add(parent.Id)) break;  // 环：父已访问
                result.Add(parent);
                current = parent;
            }
            return result.ToArray();
        }

        /// <summary>
        /// 获取 entity 的全部后代（沿 TRel 链向下，BFS 广度优先）：直接子在前，孙层次随深度。
        /// 走反向索引 O(1) 逐层查 sources，不扫描 chunk；visited 防环（含起始实体）。
        /// 复用容器（P1）：frontier/next 双缓冲 + visited 复用，返回值数组独立 new。
        /// </summary>
        public Entity[] GetDescendants<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            if (!IsAlive(entity)) return Array.Empty<Entity>();
            var result = _relBufferA;
            result.Clear();
            var visited = _relVisited;
            visited.Clear();
            visited.Add(entity.Id);  // 含起始实体：环闭合时立即终止

            var frontier = _relBufferB;
            frontier.Clear();
            frontier.Add(entity);

            var relTypeId = ComponentTypeManager.GetComponentType(typeof(TRel)).Id;

            while (frontier.Count > 0)
            {
                // 双缓冲交换：next = 非当前 frontier 的缓冲（清空后作为新 frontier 的写入面）
                var next = frontier == _relBufferB ? _relBufferC : _relBufferB;
                next.Clear();
                foreach (var node in frontier)
                {
                    // 查反向索引：所有 --TRel--> node 的 sources（直接子）
                    if (!_relationIndex.TryGetSources(node, out var byType) ||
                        !byType.TryGetValue(relTypeId, out var set))
                        continue;

                    foreach (var source in set)
                    {
                        if (!StillPointsToRelation(source, node, relTypeId)) continue;
                        if (!visited.Add(source.Id)) continue; // 防环/防重复
                        result.Add(source);
                        next.Add(source);
                    }
                }
                frontier = next;
            }
            return result.ToArray();
        }

        /// <summary>
        /// 获取 entity 的全部兄弟：与 entity 共享同一 TRel target 的其他实体（不含自身）。
        /// entity 无 TRel 关系 → 空数组。
        /// 复用容器（P1）：结果 List 复用，返回值数组独立 new。
        /// </summary>
        public Entity[] GetSiblings<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            if (!IsAlive(entity)) return Array.Empty<Entity>();
            if (!TryGetRelationshipTarget<TRel>(entity, out var parent)) return Array.Empty<Entity>();

            // 父的所有其他子
            var relTypeId = ComponentTypeManager.GetComponentType(typeof(TRel)).Id;
            if (!_relationIndex.TryGetSources(parent, out var byType) ||
                !byType.TryGetValue(relTypeId, out var set))
                return Array.Empty<Entity>();

            var result = _relBufferA;
            result.Clear();
            foreach (var source in set)
            {
                if (source.Id == entity.Id) continue;    // 排除自身
                if (!StillPointsToRelation(source, parent, relTypeId)) continue;
                result.Add(source);
            }
            return result.ToArray();
        }

        // 级联删除

        /// <summary>
        /// 级联销毁实体：销毁 entity 及所有关系指向它的实体（递归，整棵子树）。
        /// 走反向索引 O(1) 查 sources，不扫描所有关系列。
        /// 递归防环：已销毁实体 Archetype=null，天然终止；visited 集合防止环状关系重复入队。
        /// </summary>
        public void DestroyEntityCascade(Entity entity)
        {
            CheckDisposed();
            CompleteActiveJobs();
            using var changes = BeginRelationChanges();
            lock (_structuralLock)
            {
                ValidateEntityForDestruction(entity);
                if (!IsAlive(entity)) return;
                DestroyRelationGraph(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref entity, 1), forceCascade: true);
            }
        }

        /// <summary>清理 source 的关系列对应的反向条目。</summary>
        private void CleanupSourceRelations(Entity entity, in EntityIndexInWorld info, Archetype archetype)
        {
            foreach (var t in archetype.Types)
            {
                if (!t.IsRelation) continue;
                int compIdx = archetype.GetComponentTypeIndex(t);
                var chunk = archetype.ChunkList[info.ChunkIndex];

                if (t.MultiRelationMaxSlots >= 2)
                {
                    // 定长多槽列：逐槽清理反向索引（每个有效槽都维护了 target→source 条目）
                    int stride = t.Size;
                    var basePtr = (byte*)chunk.GetComponentArrayPointer(compIdx) + info.SlotInChunk * stride;
                    for (int i = 0; i < t.MultiRelationMaxSlots; i++)
                    {
                        var slot = ((RelationSlot*)(basePtr + i * 8))[0];
                        if (slot.IsValid)
                            _relationIndex.RemoveRelTypeId(t.Id, entity, in slot);
                    }
                    continue;
                }

                var slot0 = chunk.GetComponentReadOnly<RelationSlot>(info.SlotInChunk, compIdx);
                _relationIndex.RemoveRelTypeId(t.Id, entity, in slot0);
            }
        }

    }
}
