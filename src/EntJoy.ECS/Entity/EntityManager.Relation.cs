using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 实体关系操作（单实例 SoA 列）+ 级联删除。
    /// 关系类型 = 组件类型（IRelationComponent 空 struct），列值 = RelationSlot（target + version）。
    /// Add = 首次 AddComponentRaw（结构变更，走 Archetype Edges）+ 已有则 SetRaw 写 8B（零结构变更）。
    /// Remove = RemoveComponentRaw（edge 快路径）；无组件 no-op。
    /// Get/Has = 直接列读 + Version 校验（防 ID 回收）。
    /// 反向索引（RelationIndex）：Add/Remove/覆盖时同步维护，DestroyEntityCascade 时 O(1) 查索引。
    /// </summary>
    public unsafe partial class EntityManager
    {
        // ======================== P1：遍历 API 分配消除 ========================
        // 复用容器（实例字段）：消除 GetAncestors/GetDescendants/GetSiblings/GetRelationsOfAll
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
        public void AddRelationship<TRel>(Entity entity, Entity target)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            // 结构变更路径需 CompleteArchetypeJobs + 锁（与 AddComponentRaw 同纪律）
            if ((uint)entity.Id < (uint)entities.Length)
            {
                ref var info = ref GetEntityInfoRef(entity.Id);
                if (info.Archetype != null)
                    CompleteArchetypeJobs(new[] { info.Archetype });
                else
                    CompleteActiveJobs();
            }
            else
            {
                CompleteActiveJobs();
            }

            lock (_structuralLock)
            {
                ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
                if (entityInfoRef.Archetype == null)
                    throw new InvalidOperationException($"Entity {entity} has been destroyed.");
                if (entityInfoRef.Version != entity.Version)
                    throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");

                var compType = ComponentTypeManager.GetComponentType(typeof(TRel));
                int relTypeId = compType.Id;
                var arch = entityInfoRef.Archetype;

                // 覆盖语义：已有关系 → 先移除旧索引条目，再 SetRaw 写新值
                if (arch.Has(compType.Type))
                {
                    int compIdx = arch.GetComponentTypeIndex(compType);
                    var chunk = arch.ChunkList[entityInfoRef.ChunkIndex];
                    var oldSlot = chunk.GetComponent<RelationSlot>(entityInfoRef.SlotInChunk, compIdx);
                    _relationIndex.RemoveRelTypeId(relTypeId, entity, in oldSlot);
                    arch.SetRaw(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, compType.Type, RelationSlot.From(target));
                }
                else
                {
                    // 首次：复用组件添加核心（锁内调用，含 edge 快路径 + 迁移 + Observer）
                    AddComponentRawCore(entity, compType.Type, RelationSlot.From(target));
                }

                // 维护反向索引：source → target
                _relationIndex.Add(relTypeId, entity, target);
            }
        }

        /// <summary>
        /// 移除关系：entity 上的 TRel 列（无则 no-op）。同步维护反向索引。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveRelationship<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            if ((uint)entity.Id >= (uint)entities.Length) return;
            ref var info = ref GetEntityInfoRef(entity.Id);
            if (info.Archetype == null) return;
            if (info.Version != entity.Version) return;

            var compType = ComponentTypeManager.GetComponentType(typeof(TRel));
            if (!info.Archetype.Has(compType.Type)) return;

            lock (_structuralLock)
            {
                // 二次确认（锁内重读）
                ref var info2 = ref GetEntityInfoRef(entity.Id);
                if (info2.Archetype == null) return;
                if (info2.Version != entity.Version) return;
                var arch = info2.Archetype;
                if (!arch.Has(compType.Type)) return;

                int compIdx = arch.GetComponentTypeIndex(compType);
                var chunk = arch.ChunkList[info2.ChunkIndex];
                var oldSlot = chunk.GetComponent<RelationSlot>(info2.SlotInChunk, compIdx);
                _relationIndex.RemoveRelTypeId(compType.Id, entity, in oldSlot);

                // 锁内复用移除核心（含 edge 快路径 + 迁移 + Observer）
                RemoveComponentRawCore(entity, compType.Type);
            }
        }

        /// <summary>
        /// 读取关系 target。无 TRel 组件 → default(Entity)；
        /// 有组件但槽位无效 / target 已销毁 / Id 复用版本不匹配 → default(Entity)。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Entity GetRelationship<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            ref var info = ref GetEntityInfoRef(entity.Id);
            if (info.Archetype == null) return default;
            if (info.Version != entity.Version) return default;  // 自身句柄已过期

            var arch = info.Archetype;
            if (!arch.Has(typeof(TRel))) return default;

            int compIdx = arch.GetComponentTypeIndex<TRel>();
            var chunk = arch.ChunkList[info.ChunkIndex];
            var slot = chunk.GetComponent<RelationSlot>(info.SlotInChunk, compIdx);
            if (!slot.IsValid) return default;

            // 存活 + 版本校验：target 销毁（Archetype=null）或 Id 复用（version+1）均视为无效关系
            if ((uint)slot.TargetId >= (uint)entities.Length) return default;
            var targetInfo = GetEntityInfoRef(slot.TargetId);
            return (targetInfo.Archetype != null && targetInfo.Version == slot.TargetVersion)
                ? slot.ToEntity()
                : default;
        }

        /// <summary>
        /// 是否持有有效关系（有 TRel 列 且 槽位匹配 target 的当前 Id+Version）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasRelationship<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            ref var info = ref GetEntityInfoRef(entity.Id);
            if (info.Archetype == null) return false;
            if (info.Version != entity.Version) return false;

            var arch = info.Archetype;
            if (!arch.Has(typeof(TRel))) return false;

            int compIdx = arch.GetComponentTypeIndex<TRel>();
            var chunk = arch.ChunkList[info.ChunkIndex];
            var slot = chunk.GetComponent<RelationSlot>(info.SlotInChunk, compIdx);
            if (!slot.IsValid) return false;

            // 版本校验：target 实体仍存活且版本一致（防 Id 回收后误命中）
            if ((uint)slot.TargetId >= (uint)entities.Length) return false;
            var targetInfo = GetEntityInfoRef(slot.TargetId);
            return targetInfo.Archetype != null && targetInfo.Version == slot.TargetVersion;
        }

        // ======================== 反向查询 GetRelationsOf（利用关系索引 O(1)） ========================

        /// <summary>
        /// 获取所有与 target 建立 TRel 关系的 source 实体（target ←TRel-- sources）。
        /// 走反向索引 O(1)，不扫描任何 chunk。结果按插入顺序去重。
        /// 返回数组（新分配，调用方可安全持有）。
        /// </summary>
        public Entity[] GetRelationsOf<TRel>(Entity target)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            var relTypeId = ComponentTypeManager.GetComponentType(typeof(TRel)).Id;
            if (!_relationIndex.TryGetSources(target.Id, out var byType) ||
                !byType.TryGetValue(relTypeId, out var set) || set.Count == 0)
                return Array.Empty<Entity>();

            var result = new Entity[set.Count];
            int i = 0;
            foreach (var source in set)
            {
                // 防御性过滤：source 已销毁（Archetype=null）或句柄过期 → 跳过
                if (IsAlive(source))
                    result[i++] = source;
            }
            if (i != result.Length)
                Array.Resize(ref result, i);
            return result;
        }

        /// <summary>获取所有指向 target 的关系源（跨所有关系类型）。O(1) 索引查表。
        /// 复用容器（P1）：结果 List 复用，返回值数组独立 new。</summary>
        public Entity[] GetRelationsOfAll(Entity target)
        {
            CheckDisposed();
            if (!_relationIndex.TryGetSources(target.Id, out var byType))
                return Array.Empty<Entity>();

            var result = _relBufferA;
            result.Clear();
            foreach (var set in byType.Values)
            {
                foreach (var source in set)
                {
                    if (IsAlive(source))
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

        // ======================== 关系遍历 API（借鉴 Bevy iter_ancestors/descendants/siblings） ========================

        /// <summary>
        /// 尝试读取 entity 的 TRel 关系 target（存活 + 有效槽位 + 版本匹配）。
        /// 与 <see cref="GetRelationship{TRel}"/> 不同：无关系时返回 false（而非 default(Entity)，
        /// 后者的 Id=0 会与真实实体 0 冲突，遍历场景必须用本方法）。
        /// </summary>
        private bool TryGetRelationshipTarget<TRel>(Entity entity, out Entity target)
            where TRel : struct, IRelationComponent
        {
            target = default;
            if ((uint)entity.Id >= (uint)entities.Length) return false;
            ref var info = ref GetEntityInfoRef(entity.Id);
            if (info.Archetype == null || info.Version != entity.Version) return false;

            var arch = info.Archetype;
            if (!arch.Has(typeof(TRel))) return false;

            int compIdx = arch.GetComponentTypeIndex<TRel>();
            var chunk = arch.ChunkList[info.ChunkIndex];
            var slot = chunk.GetComponent<RelationSlot>(info.SlotInChunk, compIdx);
            if (!slot.IsValid) return false;

            // target 存活 + 版本校验
            if ((uint)slot.TargetId >= (uint)entities.Length) return false;
            var targetInfo = GetEntityInfoRef(slot.TargetId);
            if (targetInfo.Archetype == null || targetInfo.Version != slot.TargetVersion) return false;

            target = slot.ToEntity();
            return true;
        }

        /// <summary>
        /// 获取 entity 的全部祖先（沿 TRel 链向上）：最近的祖先在前，根在后。
        /// 单实例语义（每实体每关系类型最多 1 target），链式向上；visited 防环（含起始实体）。
        /// 空数组 = entity 无 TRel 关系或无祖先。
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
        /// 不包含 entity 自身。
        /// 复用容器（P1）：frontier/next 双缓冲 + visited 复用，返回值数组独立 new。
        /// </summary>
        public Entity[] GetDescendants<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
        {
            CheckDisposed();
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
                    if (!_relationIndex.TryGetSources(node.Id, out var byType) ||
                        !byType.TryGetValue(relTypeId, out var set))
                        continue;

                    foreach (var source in set)
                    {
                        if (!IsAlive(source)) continue;        // 防御：已销毁
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
            if (!_relationIndex.TryGetSources(parent.Id, out var byType) ||
                !byType.TryGetValue(relTypeId, out var set))
                return Array.Empty<Entity>();

            var result = _relBufferA;
            result.Clear();
            foreach (var source in set)
            {
                if (source.Id == entity.Id) continue;    // 排除自身
                if (!IsAlive(source)) continue;
                result.Add(source);
            }
            return result.ToArray();
        }

        // ======================== 级联删除 ========================

        /// <summary>
        /// 级联销毁实体：销毁 entity 及所有关系指向它的实体（递归，整棵子树）。
        /// 走反向索引 O(1) 查 sources，不扫描所有关系列。
        /// 递归防环：已销毁实体 Archetype=null，天然终止；visited 集合防止环状关系重复入队。
        /// </summary>
        public void DestroyEntityCascade(Entity entity)
        {
            CheckDisposed();
            // 先等所有 Job（级联可能跨多个 Archetype）
            CompleteActiveJobs();
            lock (_structuralLock)
            {
                if ((uint)entity.Id >= (uint)entities.Length)
                    throw new InvalidOperationException($"Entity {entity} has an invalid ID.");
                ref var info = ref GetEntityInfoRef(entity.Id);
                if (info.Archetype == null) return;  // 已销毁
                if (info.Version != entity.Version)
                    throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");

                // 收集待销毁集合（DFS，防环）
                var toDestroy = new List<Entity>();
                var visited = new HashSet<int>();
                CollectCascade(entity, toDestroy, visited);

                // 按"子实体先于父实体"销毁（子实体可能是父实体的 target，先销毁子避免悬垂）
                foreach (var e in toDestroy)
                {
                    DestroyEntityInternal(e);
                }
            }
        }

        /// <summary>收集级联销毁集合（DFS：实体 + 所有关系指向它的实体）。</summary>
        private void CollectCascade(Entity entity, List<Entity> toDestroy, HashSet<int> visited)
        {
            if (!visited.Add(entity.Id)) return;  // 防环
            toDestroy.Add(entity);

            // 查反向索引：所有指向本实体的 sources
            if (_relationIndex.TryGetSources(entity.Id, out var byType))
            {
                foreach (var kv in byType)
                {
                    foreach (var source in kv.Value)
                    {
                        // 槽位校验：source 仍指向本实体（防止索引滞后误伤）
                        if (StillPointsTo(source, entity))
                            CollectCascade(source, toDestroy, visited);
                    }
                }
            }
        }

        /// <summary>校验 source 实体的任一关系列仍指向 target（防止索引滞后/实体已改关系）。</summary>
        private bool StillPointsTo(Entity source, Entity target)
        {
            if ((uint)source.Id >= (uint)entities.Length) return false;
            ref var info = ref GetEntityInfoRef(source.Id);
            if (info.Archetype == null) return false;   // source 已销毁，跳过
            if (info.Version != source.Version) return false;

            foreach (var t in info.Archetype.Types)
            {
                if (!t.IsRelation) continue;
                int compIdx = info.Archetype.GetComponentTypeIndex(t);
                var chunk = info.Archetype.ChunkList[info.ChunkIndex];
                var slot = chunk.GetComponent<RelationSlot>(info.SlotInChunk, compIdx);
                if (slot.TargetId == target.Id && slot.TargetVersion == target.Version)
                    return true;
            }
            return false;
        }

        /// <summary>内部销毁（锁内调用，不重新上锁/等待；含 Observer + 索引清理）。</summary>
        private void DestroyEntityInternal(Entity entity)
        {
            if ((uint)entity.Id >= (uint)entities.Length) return;
            ref var info = ref GetEntityInfoRef(entity.Id);
            if (info.Archetype == null) return;
            if (info.Version != entity.Version) return;

            DestroyEntityCore(entity);
        }

        /// <summary>清理实体的关系索引条目（作为 source 的所有 TRel 列）。锁内调用。</summary>
        private void CleanupSourceRelations(Entity entity, in EntityIndexInWorld info, Archetype archetype)
        {
            foreach (var t in archetype.Types)
            {
                if (!t.IsRelation) continue;
                int compIdx = archetype.GetComponentTypeIndex(t);
                var chunk = archetype.ChunkList[info.ChunkIndex];
                var slot = chunk.GetComponent<RelationSlot>(info.SlotInChunk, compIdx);
                _relationIndex.RemoveRelTypeId(t.Id, entity, in slot);
            }
        }

        /// <summary>内部：实体当前是否有某组件列（无锁读取，仅供同锁上下文使用）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HasComponentInternal(Entity entity, Type componentType)
        {
            if ((uint)entity.Id >= (uint)entities.Length) return false;
            ref var info = ref GetEntityInfoRef(entity.Id);
            return info.Archetype != null && info.Archetype.Has(componentType);
        }
    }
}
