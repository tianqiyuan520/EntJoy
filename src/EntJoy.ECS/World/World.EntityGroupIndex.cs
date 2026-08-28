using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// Group 反向索引：Entity → 匹配的 EntityQuery 集合。
    ///
    /// 设计：惰性构建，不在结构变更路径上增量维护（结构变更零额外成本）。
    /// 索引结构 = Archetype → List&lt;EntityQuery&gt;（Archetype 匹配哪些查询）。
    /// 反向查询 = 实体定位 Archetype（O(1)，EntityManager 正向索引）+ Archetype→查询查表。
    ///
    /// 刷新时机：注册新查询或查询匹配集合变化时重建（结构变更罕见，重建成本可忽略）。
    /// </summary>
    public partial class World
    {
        // Archetype → 匹配该 Archetype 的查询集合（Group 正向索引，反向查询的中间层）
        private Dictionary<Archetype, List<EntityQuery>> _archetypeToQueries;
        // 反向索引缓存版本：_queryCache 的变更计数（判断是否需要重建）
        private int _groupIndexVersion = -1;

        /// <summary>
        /// 获取实体所属的查询集合（反向索引查询）。
        /// 返回只读视图（内部列表，零拷贝）；调用方不得修改元素。
        /// </summary>
        public IReadOnlyList<EntityQuery> GetGroupsOf(Entity entity)
        {
            var em = _entityManager;
            if ((uint)entity.Id >= (uint)em.EntityCount)
                return EmptyGroups;

            ref var info = ref em.GetEntityInfoRef(entity.Id);
            if (info.Archetype == null)
                return EmptyGroups;

            RebuildGroupIndexIfDirty();
            if (_archetypeToQueries == null || !_archetypeToQueries.TryGetValue(info.Archetype, out var queries))
                return EmptyGroups;

            return queries;
        }

        /// <summary>空列表单例（避免每次查询分配）。</summary>
        private static readonly List<EntityQuery> EmptyGroups = new(0);

        /// <summary>
        /// 反向索引脏检查：查询注册表结构变化（新查询注册/查询集合重建）时重建 Archetype→Query 映射。
        /// 使用 EntityQuery.StructuralVersion 的聚合判断，避免每次全量重建。
        /// </summary>
        private void RebuildGroupIndexIfDirty()
        {
            int currentVersion = ComputeGroupIndexVersion();
            if (currentVersion == _groupIndexVersion && _archetypeToQueries != null)
                return;

            var map = new Dictionary<Archetype, List<EntityQuery>>();
            foreach (var query in _queryCache.Values)
            {
                query.EnsureUpToDate(); // 确保匹配集合为最新
                foreach (var arch in query.MatchingArchetypes)
                {
                    if (!map.TryGetValue(arch, out var list))
                    {
                        list = new List<EntityQuery>();
                        map[arch] = list;
                    }
                    list.Add(query);
                }
            }
            _archetypeToQueries = map;
            _groupIndexVersion = currentVersion;
        }

        /// <summary>
        /// 聚合版本：所有注册查询的 StructuralVersion 之和 + 查询数量。
        /// 任一查询集合变化或新查询注册 → 值变化 → 重建。
        /// </summary>
        private int ComputeGroupIndexVersion()
        {
            int v = _queryCache.Count;
            foreach (var query in _queryCache.Values)
                v += query.StructuralVersion;
            return v;
        }
    }
}
