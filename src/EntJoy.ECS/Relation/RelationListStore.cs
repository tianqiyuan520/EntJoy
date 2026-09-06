using System;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    /// <summary>
    /// 多实例关系（[MultiRelation]）正向存储：source → targets（有序列表，幂等去重）。
    /// 反向复用 EntityManager._relationIndex（target → (relType → sources)），O(1) 查询。
    /// 存储的是 RelationSlot（含 target version），防 ID 回收：target 销毁后槽位失效。
    /// 所有操作在 EntityManager 的 _structuralLock 保护下调用（与关系操作同一锁域）。
    /// </summary>
    public sealed class RelationListStore
    {
        // relTypeId → sourceId → targets（追加式 List，保持插入顺序）
        private readonly Dictionary<int, Dictionary<int, List<RelationSlot>>> _forward = new();

        /// <summary>追加关系（幂等：同 target 不重复）。调用方保证 ExclusiveTarget 解绑已处理。</summary>
        public void Add(int relTypeId, Entity source, RelationSlot target)
        {
            if (!_forward.TryGetValue(relTypeId, out var bySource))
            {
                bySource = new Dictionary<int, List<RelationSlot>>();
                _forward[relTypeId] = bySource;
            }
            if (!bySource.TryGetValue(source.Id, out var list))
            {
                list = new List<RelationSlot>();
                bySource[source.Id] = list;
            }
            // 幂等去重（Id + Version 双匹配）
            foreach (var slot in list)
            {
                if (slot.Matches(target)) return;
            }
            list.Add(target);
        }

        /// <summary>移除 source 上的指定 target 条目。返回是否移除。</summary>
        public bool Remove(int relTypeId, Entity source, in RelationSlot target)
        {
            if (!_forward.TryGetValue(relTypeId, out var bySource)) return false;
            if (!bySource.TryGetValue(source.Id, out var list)) return false;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Matches(target))
                {
                    list.RemoveAt(i);
                    if (list.Count == 0) bySource.Remove(source.Id);
                    return true;
                }
            }
            return false;
        }

        /// <summary>source 是否持有指向 target 的关系。</summary>
        public bool Has(int relTypeId, Entity source, in RelationSlot target)
        {
            if (!_forward.TryGetValue(relTypeId, out var bySource)) return false;
            if (!bySource.TryGetValue(source.Id, out var list)) return false;
            foreach (var slot in list)
            {
                if (slot.Matches(target)) return true;
            }
            return false;
        }

        /// <summary>source 的全部 targets（null = 无）。返回内部列表，调用方不得修改。</summary>
        public List<RelationSlot>? TryGetTargets(int relTypeId, Entity source)
        {
            if (!_forward.TryGetValue(relTypeId, out var bySource)) return null;
            return bySource.TryGetValue(source.Id, out var list) ? list : null;
        }

        /// <summary>清空 source 在 relTypeId 上的全部关系。返回是否清空。</summary>
        public bool ClearSource(int relTypeId, Entity source)
        {
            if (!_forward.TryGetValue(relTypeId, out var bySource)) return false;
            if (bySource.Remove(source.Id))
            {
                if (bySource.Count == 0) _forward.Remove(relTypeId);
                return true;
            }
            return false;
        }

        /// <summary>统计 source 的关系条数。</summary>
        public int GetCount(int relTypeId, Entity source)
            => _forward.TryGetValue(relTypeId, out var bySource)
               && bySource.TryGetValue(source.Id, out var list) ? list.Count : 0;

        /// <summary>清空整个 store（World 恢复前调用）。</summary>
        public void Clear() => _forward.Clear();

        /// <summary>
        /// 清空 source 在所有关系类型上的全部条目（实体销毁用）。返回被移除的 (relTypeId, slot) 列表，
        /// 供调用方同步维护反向索引；无则返回空列表（复用入参，避免分配）。
        /// </summary>
        public void ClearAllForSource(Entity source, List<(int relTypeId, RelationSlot slot)> removed)
        {
            removed.Clear();
            // 先收集需删除的 relTypeId（遍历时不得修改 _forward）
            var emptyKeys = new List<int>();
            foreach (var kv in _forward)
            {
                int relTypeId = kv.Key;
                var bySource = kv.Value;
                if (!bySource.TryGetValue(source.Id, out var list)) continue;
                foreach (var slot in list)
                    removed.Add((relTypeId, slot));
                bySource.Remove(source.Id);
                if (bySource.Count == 0) emptyKeys.Add(relTypeId);
            }
            foreach (var key in emptyKeys)
                _forward.Remove(key);
        }
    }
}
