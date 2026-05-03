using System;
using System.Runtime.CompilerServices;

namespace EntJoy
{
    public unsafe ref struct ChunkEnumerable<T0, T1> where T0 : struct where T1 : struct
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;

        internal ChunkEnumerable(EntityManager em, QueryBuilder builder)
        {
            _entityManager = em;
            _builder = builder;
        }

        public ChunkEnumerator<T0, T1> GetEnumerator()
            => new ChunkEnumerator<T0, T1>(_entityManager, _builder);
    }

    public unsafe ref struct ChunkEnumerator<T0, T1> where T0 : struct where T1 : struct
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;
        private int _archIndex;
        private int _chunkIndex;
        private Archetype _currentArch;
        private Chunk _currentChunk;
        private int _t0Idx;
        private int _t1Idx;

        internal ChunkEnumerator(EntityManager em, QueryBuilder builder)
        {
            _entityManager = em;
            _builder = builder;
            _archIndex = 0;
            _chunkIndex = 0;
            _currentArch = null;
            _currentChunk = null;
            _t0Idx = -1;
            _t1Idx = -1;
        }

        private bool MoveNextArchetype()
        {
            while (_archIndex < _entityManager.Archetypes.Length)
            {
                var arch = _entityManager.Archetypes[_archIndex];
                _archIndex++;
                if (arch != null && arch.IsMatch(_builder))
                {
                    _currentArch = arch;
                    _t0Idx = arch.GetComponentTypeIndex<T0>();
                    _t1Idx = arch.GetComponentTypeIndex<T1>();
                    _chunkIndex = 0;
                    return MoveNextChunk();
                }
            }
            return false;
        }

        private bool MoveNextChunk()
        {
            if (_currentArch == null) return false;
            var chunks = _currentArch.ChunkList;
            while (_chunkIndex < chunks.Count)
            {
                _currentChunk = chunks[_chunkIndex];
                _chunkIndex++;
                if (_currentChunk.EntityCount > 0)
                {
                    return true;
                }
            }
            _currentChunk = null;
            return MoveNextArchetype();
        }

        public bool MoveNext()
        {
            if (_currentArch == null)
            {
                return MoveNextArchetype();
            }
            if (_chunkIndex < _currentArch.ChunkList.Count)
            {
                _currentChunk = _currentArch.ChunkList[_chunkIndex];
                _chunkIndex++;
                return _currentChunk.EntityCount > 0;
            }
            return MoveNextArchetype();
        }

        public ChunkResult<T0, T1> Current
        {
            get
            {
                if (_currentChunk == null) throw new InvalidOperationException();
                return new ChunkResult<T0, T1>(_currentChunk, _t0Idx, _t1Idx);
            }
        }


    }

    public unsafe readonly ref struct ChunkResult<T0, T1> where T0 : struct where T1 : struct
    {
        private readonly Chunk _chunk;
        private readonly int _t0Idx;
        private readonly int _t1Idx;

        internal ChunkResult(Chunk chunk, int t0Idx, int t1Idx)
        {
            _chunk = chunk;
            _t0Idx = t0Idx;
            _t1Idx = t1Idx;
        }

        public int Length => _chunk.EntityCount;

        public T0* GetPtr0() => (T0*)_chunk.GetComponentArrayPointer(_t0Idx);
        public T1* GetPtr1() => (T1*)_chunk.GetComponentArrayPointer(_t1Idx);
        public Span<T0> GetSpan0() => new Span<T0>(GetPtr0(), Length);
        public Span<T1> GetSpan1() => new Span<T1>(GetPtr1(), Length);

        /// <summary>
        /// 获取指定 enableable 组件的位图掩码。
        /// </summary>
        /// <typeparam name="T">必须实现 <see cref="IEnableableComponent"/> 的组件类型</typeparam>
        /// <returns>该组件在当前 Chunk 中的位图掩码</returns>
        /// <exception cref="InvalidOperationException">如果组件不可启用或不存在于当前 Archetype 中</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe BitMask GetEnabledMask<T>() where T : struct, IEnableableComponent
        {
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            ulong* ptr = _chunk.GetEnableBitMapPointer(idx);
            if (ptr == null)
                throw new InvalidOperationException($"Component {typeof(T).Name} is not enableable.");
            return new BitMask(ptr, Length);
        }

        /// <summary>
        /// 获取指定实体上 enableable 组件的启用状态。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsComponentEnabled<T>(int entityIndex) where T : struct, IEnableableComponent
        {
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            return _chunk.GetComponentEnabled(idx, entityIndex);
        }

        /// <summary>
        /// 设置指定实体上 enableable 组件的启用状态。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponentEnabled<T>(int entityIndex, bool enabled) where T : struct, IEnableableComponent
        {
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            _chunk.SetComponentEnabled(idx, entityIndex, enabled);
        }
    }


}
