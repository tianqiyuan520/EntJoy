using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 关系反向索引（级联删除 target index）。
    /// target.Id → (relTypeId → sources)。sources 用 HashSet（增删 O(1)）。
    /// 主动维护：Add/Remove/覆盖更新关系时同步增删；DestroyEntityCascade 时 O(1) 查索引。
    /// 所有操作在 EntityManager 的 _structuralLock 保护下调用（与关系操作同一锁域）。
    /// </summary>
    public sealed class RelationIndex
    {
        // target.Id → (relTypeId → sources)
        private readonly Dictionary<int, Dictionary<int, HashSet<Entity>>> _index = new();

        /// <summary>记录关系：source --TRel--> target。覆盖语义由调用方保证（先 RemoveRelTypeId 旧值）。</summary>
        public void Add(int relTypeId, Entity source, Entity target)
        {
            if (!_index.TryGetValue(target.Id, out var byType))
            {
                byType = new Dictionary<int, HashSet<Entity>>();
                _index[target.Id] = byType;
            }
            if (!byType.TryGetValue(relTypeId, out var set))
            {
                set = new HashSet<Entity>();
                byType[relTypeId] = set;
            }
            set.Add(source);
        }

        /// <summary>
        /// 移除 source 上 TRel 关系（旧槽位 oldTarget 已知，直接从索引删除，O(1)）。
        /// </summary>
        public void RemoveRelTypeId(int relTypeId, Entity source, in RelationSlot oldTarget)
        {
            if (!oldTarget.IsValid) return;
            if (!_index.TryGetValue(oldTarget.TargetId, out var byType)) return;
            if (!byType.TryGetValue(relTypeId, out var set)) return;

            set.Remove(source);
            if (set.Count == 0)
            {
                byType.Remove(relTypeId);
                if (byType.Count == 0)
                    _index.Remove(oldTarget.TargetId);
            }
        }

        /// <summary>
        /// 获取指向 target 的所有关系源（按关系类型分组）。返回 false 表示无关系。
        /// 返回的内部 HashSet 不得修改。
        /// </summary>
        public bool TryGetSources(int targetId, out Dictionary<int, HashSet<Entity>> byType)
            => _index.TryGetValue(targetId, out byType);

        /// <summary>获取直接指向 target 的关系源实体总数（跨类型，诊断/测试用）。</summary>
        public int GetSourceCount(int targetId)
        {
            if (!_index.TryGetValue(targetId, out var byType)) return 0;
            int total = 0;
            foreach (var set in byType.Values) total += set.Count;
            return total;
        }

        /// <summary>清理：删除指定 target 的整个索引条目（实体销毁后调用）。</summary>
        public void ClearTarget(int targetId)
        {
            _index.Remove(targetId);
        }
    }
}
