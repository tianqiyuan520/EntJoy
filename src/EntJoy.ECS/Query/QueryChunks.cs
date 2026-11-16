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

        /// <summary>取该 Chunk 上 T 列的有效实体区间；T 不在原型里时 <see cref="Archetype.GetComponentTypeIndex{T}"/> 会抛出。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span<T> Span<T>() where T : struct
        {
            int index = _chunk.Archetype.GetComponentTypeIndex<T>();
            return new Span<T>((T*)_chunk.GetComponentArrayPointer(index), _chunk.EntityCount);
        }
    }

    /// <summary>
    /// 按 Chunk 遍历匹配的原型：每个原型取一次 Chunk 视图，逐块推进。
    /// 不经过遍历计划与快照，运行时只有原型表和块数组两层。
    /// </summary>
    public ref struct QueryChunkEnumerator
    {
        private readonly IReadOnlyList<Archetype> _archetypes;
        private ReadOnlySpan<Chunk> _chunks;
        private int _archetypeIndex;
        private int _chunkIndex;
        private QueryChunk _current;

        internal QueryChunkEnumerator(EntityManager manager, QueryBuilder builder)
        {
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
