using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>双组件查询选择器：<c>world.Query&lt;T0, T1&gt;()</c> 的返回值，可链式追加过滤条件，foreach 兼容。</summary>
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

        /// <summary>直接枚举（无附加过滤）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryEnumerator<T0, T1> GetEnumerator()
            => new QueryEnumerator<T0, T1>(_entityManager, _builder);

        /// <summary>只遍历持有 T2 关系且 target 为指定值的实体；关系仅用于过滤，不占组件位。</summary>
        public QuerySelection<T0, T1> WithRelationship<T2>(Entity target)
            where T2 : struct, IRelationComponent
            => new QuerySelection<T0, T1>(_entityManager, _builder.WithRelationship<T2>(target));

        /// <summary>只遍历同时启用了 T2 的实体。</summary>
        public QuerySelection<T0, T1> WithEnabled<T2>()
            where T2 : struct, IEnableableComponent
            => new QuerySelection<T0, T1>(_entityManager, _builder.WithEnabled<T2>());

        /// <summary>只遍历持有指定 shared 值的 chunk。</summary>
        public QuerySelection<T0, T1> WithShared<T2>(T2 filterValue)
            where T2 : struct, ISharedComponentData
            => new QuerySelection<T0, T1>(_entityManager, _builder.WithShared(filterValue));
    }
}
