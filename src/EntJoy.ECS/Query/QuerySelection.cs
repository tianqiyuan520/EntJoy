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
    }
}