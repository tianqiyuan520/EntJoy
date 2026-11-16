using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 查询结果的一个 Chunk：按组件类型取列 Span，调用方在 Span 上写循环。
    /// 不带泛型参数，任意组件组合共用一个类型。
    /// </summary>
    public readonly ref struct QueryChunk
    {
        private readonly ref readonly Chunk _chunk;

        internal QueryChunk(in Chunk chunk) => _chunk = ref chunk;

        public int Count => _chunk.EntityCount;

        /// <summary>该块内的实体句柄，下标与 <see cref="Span{T}"/> 的槽位一一对应。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe ReadOnlySpan<Entity> Entities()
            => new ReadOnlySpan<Entity>((Entity*)_chunk.GetEntityPointer(), _chunk.EntityCount);

        /// <summary>取该 Chunk 上 T 列的有效实体区间；T 不在原型里时 <see cref="Archetype.GetComponentTypeIndex{T}"/> 会抛出。
        /// shared 组件每块只有一份值，没有逐实体列，取 Span 直接拒绝。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span<T> Span<T>() where T : struct
        {
            int index = _chunk.Archetype.GetComponentTypeIndex<T>();
            if (_chunk.Archetype.Types[index].IsShared)
                throw new NotSupportedException("shared 组件每块一份值，没有逐实体列，不能用 Span<T> 读取。");
            return new Span<T>((T*)_chunk.GetComponentArrayPointer(index), _chunk.EntityCount);
        }
    }

    /// <summary>
    /// 按 Chunk 遍历匹配的原型：每个原型取一次 Chunk 视图，逐块推进。
    /// 不经过遍历计划与快照，运行时只有原型表和块数组两层。
    /// 块级过滤（shared）决定哪些块进入结果；逐实体过滤（关系目标、启用位）在块视图里无法表达，构造时拒绝。
    /// </summary>
    public ref struct QueryChunkEnumerator
    {
        private readonly IReadOnlyList<Archetype> _archetypes;
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _filter;
        private ReadOnlySpan<Chunk> _chunks;
        private int _archetypeIndex;
        private int _chunkIndex;
        private QueryChunk _current;

        internal QueryChunkEnumerator(EntityManager manager, QueryBuilder builder)
        {
            if (builder.HasRelationshipFilter || (builder.AllEnabled != null && builder.AllEnabled.Length > 0))
                throw new NotSupportedException("Chunk 视图不筛实体（关系目标、启用位）；用 Entities() 逐实体自行判定，或改用 foreach。");
            _entityManager = manager;
            _filter = builder;
            _archetypes = manager.GetMatchingArchetypes(builder);
            _chunks = default;
            _archetypeIndex = -1;
            _chunkIndex = 0;
            _current = default;
        }

        public QueryChunk Current => _current;

        public bool MoveNext()
        {
            while (true)
            {
                while (_chunkIndex < _chunks.Length)
                {
                    ref readonly var chunk = ref _chunks[_chunkIndex++];
                    if (chunk.EntityCount == 0) continue;
                    if (_filter.HasSharedFilter && !_entityManager.MatchesSharedFilter(_filter, chunk)) continue;
                    _current = new QueryChunk(in chunk);
                    return true;
                }
                if (++_archetypeIndex >= _archetypes.Count) return false;
                _chunks = _archetypes[_archetypeIndex].ChunkSpan;
                _chunkIndex = 0;
            }
        }
    }

    /// <summary>Chunk 级查询视图：<c>world.Query&lt;A,B&gt;().Chunks()</c>。</summary>
    public readonly ref struct QueryChunks
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;

        internal QueryChunks(EntityManager entityManager, QueryBuilder builder)
        {
            _entityManager = entityManager;
            _builder = builder;
        }

        public QueryChunkEnumerator GetEnumerator() => new QueryChunkEnumerator(_entityManager, _builder);
    }
}
