namespace EntJoy.ECS
{
    /// <summary>
    /// 双组件查询选择器：world.Query&lt;T0, T1&gt;() 的返回值。
    /// 支持链式附加过滤条件（WithRelationship 等），foreach 兼容（委托 QueryEnumerable）。
    ///
    /// 使用示例：
    ///   foreach (var r in world.Query&lt;Position, Velocity&gt;().WithRelationship&lt;ChildOf&gt;(parent))
    ///   {
    ///       ref var pos = ref r.Comp0;   // Position
    ///       ref var vel = ref r.Comp1;   // Velocity（关系仅过滤，不占组件位）
    ///   }
    /// </summary>
    public readonly struct QuerySelection<T0, T1>
        where T0 : struct
        where T1 : struct
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;

        internal QuerySelection(EntityManager entityManager, QueryBuilder builder)
        {
            _entityManager = entityManager;
            _builder = builder;
        }

        /// <summary>直接枚举（无附加过滤）。返回 QueryEnumerator（含 MoveNext/Current，foreach 兼容）。</summary>
        public QueryEnumerator<T0, T1> GetEnumerator()
            => new QueryEnumerator<T0, T1>(_entityManager, _builder);

        /// <summary>
        /// 附加关系过滤：只遍历持有 T2 关系且 target == <paramref name="target"/> 的实体。
        /// T2 是关系组件（IRelationComponent）；过滤通过 QueryBuilder.WithRelationship 实现，
        /// 遍历结果仅含 T0/T1 组件（关系不占位）。返回自身支持继续链式（如 .WithEnabled）。
        /// </summary>
        public QuerySelection<T0, T1> WithRelationship<T2>(Entity target)
            where T2 : struct, IRelationComponent
            => new QuerySelection<T0, T1>(_entityManager, _builder.WithRelationship<T2>(target));

        /// <summary>
        /// 附加 Enableable 组件过滤：只遍历同时启用了 T2 的实体。返回自身支持继续链式。
        /// </summary>
        public QuerySelection<T0, T1> WithEnabled<T2>()
            where T2 : struct, IEnableableComponent
            => new QuerySelection<T0, T1>(_entityManager, _builder.WithEnabled<T2>());

        /// <summary>
        /// 附加 SharedComponent 过滤：只遍历持有指定 shared 值的 chunk。返回自身支持继续链式。
        /// </summary>
        public QuerySelection<T0, T1> WithShared<T2>(T2 filterValue)
            where T2 : struct, ISharedComponentData
            => new QuerySelection<T0, T1>(_entityManager, _builder.WithShared(filterValue));
    }
}
