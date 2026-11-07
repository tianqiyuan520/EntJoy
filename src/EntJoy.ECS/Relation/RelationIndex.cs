using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 关系反向索引（级联删除 target index）。
    /// target 的 ID/Version → (relTypeId → sources)。sources 用 HashSet（增删 O(1)）。
    /// 写入由 EntityManager 的 _structuralLock 保护；只读查询在主线程使用。
    /// </summary>
    public sealed class RelationIndex
    {
        private readonly Dictionary<ulong, Dictionary<int, HashSet<Entity>>> _index = new();
        internal bool IsEmpty => _index.Count == 0;

        private static ulong Key(Entity entity) => ((ulong)(uint)entity.Version << 32) | (uint)entity.Id;

        /// <summary>记录关系：source --TRel--> target。覆盖语义由调用方保证（先 RemoveRelTypeId 旧值）。</summary>
        public void Add(int relTypeId, Entity source, Entity target)
        {
            PrepareAdd(relTypeId, target).Add(source);
        }

        /// <summary>提交前准备索引容量；返回的集合在本次结构锁内有效。</summary>
        internal HashSet<Entity> PrepareAdd(int relTypeId, Entity target)
        {
            ulong key = Key(target);
            if (!_index.TryGetValue(key, out var byType))
            {
                byType = new Dictionary<int, HashSet<Entity>>();
                _index[key] = byType;
            }
            if (!byType.TryGetValue(relTypeId, out var set))
            {
                set = new HashSet<Entity>();
                byType[relTypeId] = set;
            }
            set.EnsureCapacity(set.Count + 1);
            return set;
        }

        /// <summary>
        /// 移除 source 上 TRel 关系（旧槽位 oldTarget 已知，直接从索引删除，O(1)）。
        /// </summary>
        public void RemoveRelTypeId(int relTypeId, Entity source, in RelationSlot oldTarget)
        {
            if (!oldTarget.IsValid) return;
            ulong key = Key(oldTarget.ToEntity());
            if (!_index.TryGetValue(key, out var byType)) return;
            if (!byType.TryGetValue(relTypeId, out var set)) return;

            set.Remove(source);
            if (set.Count == 0)
            {
                byType.Remove(relTypeId);
                if (byType.Count == 0)
                    _index.Remove(key);
            }
        }

        internal void DiscardEmpty(int relTypeId, Entity target)
        {
            ulong key = Key(target);
            if (!_index.TryGetValue(key, out var byType)) return;
            if (byType.TryGetValue(relTypeId, out var sources) && sources.Count == 0) byType.Remove(relTypeId);
            if (byType.Count == 0) _index.Remove(key);
        }

        /// <summary>
        /// 获取指向完整 target 身份的关系源（按关系类型分组）。
        /// 返回的内部 HashSet 不得修改。
        /// </summary>
        public bool TryGetSources(Entity target, out Dictionary<int, HashSet<Entity>> byType)
            => _index.TryGetValue(Key(target), out byType);

        /// <summary>按 target ID 统计跨类型关系条目数（诊断用，不用于实体身份校验）。</summary>
        public int GetSourceCount(int targetId)
        {
            int total = 0;
            foreach (var entry in _index)
                if ((int)(uint)entry.Key == targetId)
                    foreach (var set in entry.Value.Values) total += set.Count;
            return total;
        }

        /// <summary>清理：删除指定 target 的整个索引条目（实体销毁后调用）。</summary>
        public void ClearTarget(Entity target)
        {
            _index.Remove(Key(target));
        }

        /// <summary>清空整个索引（World 恢复/重建前调用）。</summary>
        public void Clear() => _index.Clear();
    }
}
