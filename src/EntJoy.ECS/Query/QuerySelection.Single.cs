using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>单组件查询选择器：<c>world.Query&lt;T0&gt;()</c> 的返回值，可直接 foreach，也可链式追加一个过滤条件。</summary>
    public readonly struct QuerySelection<T0>
        where T0 : struct
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;

        internal QuerySelection(EntityManager entityManager)
            : this(entityManager, new QueryBuilder().WithAll<T0>())
        {
        }

        internal QuerySelection(EntityManager entityManager, QueryBuilder builder)
        {
            _entityManager = entityManager;
            _builder = builder;
        }

        /// <summary>直接枚举（无附加过滤）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryEnumerator<T0> GetEnumerator()
            => new QueryEnumerator<T0>(_entityManager, _builder);

        /// <summary>块循环 + 内联回调：按 Chunk 取列后逐实体调用，循环内没有逐实体游标状态。</summary>
        public void ForEach<TAction>(ref TAction action)
            where TAction : struct, IQueryForeach<T0>
            => new QueryEnumerable<T0>(_entityManager, _builder).ForEach(ref action);

        /// <summary>按 Chunk 遍历：每步只推进 Chunk，循环体由调用方写，不构造逐实体结果对象。</summary>
        public QueryChunks Chunks()
            => new QueryChunks(_entityManager, _builder);

        /// <summary>只遍历同时启用了 T1 的实体（SIMD 位图遍历）。</summary>
        public QueryEnumerable<T0, T1> WithEnabled<T1>()
            where T1 : struct, IEnableableComponent
            => new QueryEnumerable<T0, T1>(_entityManager, _builder.WithEnabled<T1>());

        /// <summary>只遍历持有 T1 关系且 target 为指定值的实体；关系仅用于过滤，不占组件位。</summary>
        public QueryEnumerable<T0, T1> WithRelationship<T1>(Entity target)
            where T1 : struct, IRelationComponent
            => new QueryEnumerable<T0, T1>(_entityManager, _builder.WithRelationship<T1>(target));

        /// <summary>只遍历持有指定 shared 值的 chunk；三条件路径（查询/Job/枚举）共用同一过滤。</summary>
        public QueryEnumerable<T0, T1> WithShared<T1>(T1 filterValue)
            where T1 : struct, ISharedComponentData
            => new QueryEnumerable<T0, T1>(_entityManager, _builder.WithShared(filterValue));
    }
}
