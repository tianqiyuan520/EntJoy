namespace EntJoy.ECS
{
    /// <summary>
    /// 单组件查询选择器：作为 world.Query&lt;T0&gt;() 的返回值，
    /// 支持链式附加过滤条件（如 WithEnabled&lt;TEnableable&gt;）。
    /// 
    /// 使用示例：
    ///   foreach (var result in world.Query&lt;Position&gt;().WithEnabled&lt;ActiveComponent&gt;())
    ///   {
    ///       ref var pos = ref result.Comp0;
    ///   }
    /// </summary>
    public readonly struct QuerySelection<T0>
        where T0 : struct
    {
        private readonly EntityManager _entityManager;

        internal QuerySelection(EntityManager entityManager)
        {
            _entityManager = entityManager;
        }

        /// <summary>
        /// 附加 Enableable 组件过滤：只遍历同时启用了 T1 的实体。
        /// 使用 SIMD + 提前退出优化的组合位图遍历（复用 QueryEnumerable）。
        /// </summary>
        public QueryEnumerable<T0, T1> WithEnabled<T1>()
            where T1 : struct, IEnableableComponent
            => new QueryEnumerable<T0, T1>(
                _entityManager,
                new QueryBuilder().WithAll<T0>().WithEnabled<T1>());

        /// <summary>
        /// 附加关系过滤：只遍历持有 T1 关系且 target == <paramref name="target"/> 的实体。
        /// T1 是关系组件（IRelationComponent），遍历结果中 Comp1 为该关系槽位值（一般无需读取）。
        /// </summary>
        public QueryEnumerable<T0, T1> WithRelationship<T1>(Entity target)
            where T1 : struct, IRelationComponent
            => new QueryEnumerable<T0, T1>(
                _entityManager,
                new QueryBuilder().WithAll<T0>().WithRelationship<T1>(target));

        /// <summary>
        /// 附加 SharedComponent 过滤：只遍历持有指定 shared 值的 chunk。
        /// chunk 级过滤（per-chunk 共享值），EntityQuery/Job/枚举三路径统一。
        /// </summary>
        public QueryEnumerable<T0, T1> WithShared<T1>(T1 filterValue)
            where T1 : struct, ISharedComponentData
            => new QueryEnumerable<T0, T1>(
                _entityManager,
                new QueryBuilder().WithAll<T0>().WithShared(filterValue));
    }
}