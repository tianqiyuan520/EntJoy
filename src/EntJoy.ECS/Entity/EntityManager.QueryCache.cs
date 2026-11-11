using System.Collections.Generic;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        internal readonly Dictionary<QueryKey, EntityQuery> QueryCache = new();
        internal readonly object QueryCacheLock = new();

        public int QueryCacheCount { get { lock (QueryCacheLock) return QueryCache.Count; } }

        /// <summary>复用 Archetype 匹配条件；动态过滤值留给当前枚举器，避免按目标持续增加缓存。</summary>
        public IReadOnlyList<Archetype> GetMatchingArchetypes(QueryBuilder builder)
            => GetArchetypeQuery(builder).MatchingArchetypes;

        /// <summary>复用静态匹配条件；动态过滤值由当前枚举器保存。</summary>
        public EntityQuery GetArchetypeQuery(QueryBuilder builder)
        {
            builder.LimitCount = -1;
            builder.ChangedComponents = null;
            builder.MinChangedVersion = -1;
            builder.SharedFilterValue = null;
            builder.RelationshipFilterTarget = default;
            return GetOrCreateEntityQuery(builder);
        }

        /// <summary>复用规则相同的查询；查找不分配规则数组，首次注册时保存独立指纹。</summary>
        public EntityQuery GetOrCreateEntityQuery(QueryBuilder builder)
        {
            CheckDisposed();
            var lookup = new QueryKey(builder, false);
            lock (QueryCacheLock)
            {
                if (QueryCache.TryGetValue(lookup, out var query))
                {
                    return query;
                }
                query = new EntityQuery(this, builder);
                QueryCache.Add(query.Key, query);
                return query;
            }
        }
    }
}
