using System;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    /// <summary>
    /// 多实例关系（[MultiRelation]）正向存储：source → targets（有序列表，幂等去重）。
    /// 反向复用 EntityManager._relationIndex（target → (relType → sources)），O(1) 查询。
    /// 所有操作在 EntityManager 的 _structuralLock 保护下调用（与关系操作同一锁域）。
    /// </summary>
    public sealed class RelationListStore
    {
        internal sealed class RelationTargets
        {
            public readonly List<RelationSlot> Values = new();
            private HashSet<Entity>? _membership;
            public int Count => Values.Count;
            public List<RelationSlot>.Enumerator GetEnumerator() => Values.GetEnumerator();

            public void Prepare(int additional)
            {
                int capacity = checked(Count + additional);
                Values.EnsureCapacity(capacity);
                if (_membership == null && capacity > 32)
                {
                    var index = new HashSet<Entity>(capacity);
                    foreach (var value in Values) index.Add(value.ToEntity());
                    _membership = index;
                }
                _membership?.EnsureCapacity(capacity);
            }

            public bool Contains(RelationSlot target)
            {
                if (_membership != null) return _membership.Contains(target.ToEntity());
                foreach (var value in Values) if (value.Matches(target)) return true;
                return false;
            }

            public void Add(RelationSlot target)
            {
                _membership?.Add(target.ToEntity());
                Values.Add(target);
            }

            public bool Remove(RelationSlot target)
            {
                if (_membership != null && !_membership.Contains(target.ToEntity())) return false;
                for (int i = 0; i < Count; i++)
                    if (Values[i].Matches(target))
                    {
                        _membership?.Remove(target.ToEntity());
                        Values.RemoveAt(i);
                        return true;
                    }
                return false;
            }

            public int RemoveMany(HashSet<Entity> targets)
            {
                int count = Count, write = 0;
                for (int read = 0; read < count; read++)
                {
                    var value = Values[read];
                    if (targets.Contains(value.ToEntity())) _membership?.Remove(value.ToEntity());
                    else Values[write++] = value;
                }
                Values.RemoveRange(write, count - write);
                return count - write;
            }
        }

        internal void PrepareSourceRemoval(Entity source, List<(int relTypeId, RelationSlot slot)> removed)
        {
            int count = 0;
            foreach (var sources in _forward.Values)
                if (sources.TryGetValue(source, out var values)) count = checked(count + values.Count);
            removed.EnsureCapacity(count);
            _emptyKeysBuffer.EnsureCapacity(_forward.Count);
        }
        // relTypeId → 完整 source 身份 → targets（追加式 List，保持插入顺序）
        private readonly Dictionary<int, Dictionary<Entity, RelationTargets>> _forward = new();

        internal bool HasRelationships
        {
            get
            {
                foreach (var sources in _forward.Values)
                    foreach (var targets in sources.Values) if (targets.Count > 0) return true;
                return false;
            }
        }

        internal bool HasRelationshipsForSource(Entity source)
        {
            foreach (var sources in _forward.Values)
                if (sources.TryGetValue(source, out var targets) && targets.Count > 0) return true;
            return false;
        }

        /// <summary>ClearAllForSource 的复用缓冲，避免每次销毁实体都分配列表。</summary>
        private readonly List<int> _emptyKeysBuffer = new();

        /// <summary>追加关系（幂等：同 target 不重复）。调用方保证 ExclusiveTarget 解绑已处理。</summary>
        internal RelationTargets PrepareAdd(int relTypeId, Entity source, int additional = 1)
        {
            if (!_forward.TryGetValue(relTypeId, out var bySource))
            {
                bySource = new Dictionary<Entity, RelationTargets>();
                _forward[relTypeId] = bySource;
            }
            if (!bySource.TryGetValue(source, out var list))
            {
                list = new RelationTargets();
                bySource[source] = list;
            }
            list.Prepare(additional);
            return list;
        }

        internal void DiscardEmpty(int relTypeId, Entity source)
        {
            if (!_forward.TryGetValue(relTypeId, out var bySource)) return;
            if (bySource.TryGetValue(source, out var targets) && targets.Count == 0) bySource.Remove(source);
            if (bySource.Count == 0) _forward.Remove(relTypeId);
        }

        internal IEnumerable<(int TypeId, Entity Source, List<RelationSlot> Targets)> Entries
        {
            get
            {
                foreach (var type in _forward)
                    foreach (var source in type.Value)
                        if (source.Value.Count > 0) yield return (type.Key, source.Key, source.Value.Values);
            }
        }

        internal IEnumerable<(int TypeId, List<RelationSlot> Targets)> ForSource(Entity source)
        {
            foreach (var entry in _forward)
                if (entry.Value.TryGetValue(source, out var targets) && targets.Count > 0) yield return (entry.Key, targets.Values);
        }

        public void Add(int relTypeId, Entity source, RelationSlot target)
        {
            var list = PrepareAdd(relTypeId, source);
            // 幂等去重（Id + Version 双匹配）
            if (list.Contains(target)) return;
            list.Add(target);
        }

        /// <summary>移除 source 上的指定 target 条目。返回是否移除。</summary>
        public bool Remove(int relTypeId, Entity source, in RelationSlot target)
        {
            if (!_forward.TryGetValue(relTypeId, out var bySource)) return false;
            if (!bySource.TryGetValue(source, out var list)) return false;

            if (list.Remove(target))
            {
                if (list.Count == 0) bySource.Remove(source);
                return true;
            }
            return false;
        }

        /// <summary>source 是否持有指向 target 的关系。</summary>
        public bool Has(int relTypeId, Entity source, in RelationSlot target)
        {
            if (!_forward.TryGetValue(relTypeId, out var bySource)) return false;
            if (!bySource.TryGetValue(source, out var list)) return false;
            return list.Contains(target);
        }

        /// <summary>source 的全部 targets（null = 无）。返回内部列表，调用方不得修改。</summary>
        public List<RelationSlot>? TryGetTargets(int relTypeId, Entity source)
        {
            if (!_forward.TryGetValue(relTypeId, out var bySource)) return null;
            return bySource.TryGetValue(source, out var list) ? list.Values : null;
        }

        internal void CommitAdd(int relTypeId, Entity source, RelationSlot target) => _forward[relTypeId][source].Add(target);

        internal int RemoveMany(int relTypeId, Entity source, HashSet<Entity> targets)
        {
            var bySource = _forward[relTypeId];
            var list = bySource[source];
            int removed = list.RemoveMany(targets);
            if (list.Count == 0) bySource.Remove(source);
            return removed;
        }

        /// <summary>清空 source 在 relTypeId 上的全部关系。返回是否清空。</summary>
        public bool ClearSource(int relTypeId, Entity source)
        {
            if (!_forward.TryGetValue(relTypeId, out var bySource)) return false;
            if (bySource.Remove(source))
            {
                if (bySource.Count == 0) _forward.Remove(relTypeId);
                return true;
            }
            return false;
        }

        /// <summary>统计 source 的关系条数。</summary>
        public int GetCount(int relTypeId, Entity source)
            => _forward.TryGetValue(relTypeId, out var bySource)
               && bySource.TryGetValue(source, out var list) ? list.Count : 0;

        /// <summary>清空整个 store（World 恢复前调用）。</summary>
        public void Clear() => _forward.Clear();

        /// <summary>
        /// 清空 source 在所有关系类型上的全部条目（实体销毁用）。返回被移除的 (relTypeId, slot) 列表，
        /// 供调用方同步维护反向索引；无则返回空列表（复用入参，避免分配）。
        /// </summary>
        public void ClearAllForSource(Entity source, List<(int relTypeId, RelationSlot slot)> removed)
        {
            removed.Clear();
            // 无多值关系数据时直接返回（绝大多数销毁走这里）， 零遍历、零分配
            if (_forward.Count == 0) return;
            // 先收集需删除的 relTypeId（遍历时不得修改 _forward）
            var emptyKeys = _emptyKeysBuffer;
            emptyKeys.Clear();
            foreach (var kv in _forward)
            {
                int relTypeId = kv.Key;
                var bySource = kv.Value;
                if (!bySource.TryGetValue(source, out var list)) continue;
                foreach (var slot in list)
                    removed.Add((relTypeId, slot));
                bySource.Remove(source);
                if (bySource.Count == 0) emptyKeys.Add(relTypeId);
            }
            foreach (var key in emptyKeys)
                _forward.Remove(key);
        }
    }
}
