namespace EntJoy.ECS
{
    /// <summary>单组件查询选择器：<c>world.Query&lt;T0&gt;()</c> 的返回值，可链式追加一个过滤条件后 foreach。</summary>
    public readonly struct QuerySelection<T0>
        where T0 : struct
    {
        private readonly EntityManager _entityManager;

        internal QuerySelection(EntityManager entityManager)
        {
            _entityManager = entityManager;
        }

        /// <summary>只遍历同时启用了 T1 的实体（SIMD 位图遍历）。</summary>
        public QueryEnumerable<T0, T1> WithEnabled<T1>()
            where T1 : struct, IEnableableComponent
            => new QueryEnumerable<T0, T1>(
                _entityManager,
                new QueryBuilder().WithAll<T0>().WithEnabled<T1>());

        /// <summary>只遍历持有 T1 关系且 target 为指定值的实体；关系仅用于过滤，不占组件位。</summary>
        public QueryEnumerable<T0, T1> WithRelationship<T1>(Entity target)
            where T1 : struct, IRelationComponent
            => new QueryEnumerable<T0, T1>(
                _entityManager,
                new QueryBuilder().WithAll<T0>().WithRelationship<T1>(target));

        /// <summary>只遍历持有指定 shared 值的 chunk；三条件路径（查询/Job/枚举）共用同一过滤。</summary>
        public QueryEnumerable<T0, T1> WithShared<T1>(T1 filterValue)
            where T1 : struct, ISharedComponentData
            => new QueryEnumerable<T0, T1>(
                _entityManager,
                new QueryBuilder().WithAll<T0>().WithShared(filterValue));
    }
}
