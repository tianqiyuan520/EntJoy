using System;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ChunkEnumerator<T0, T1> GetEnumerator()
            => new ChunkEnumerator<T0, T1>(_entityManager, _builder);
    }

    public unsafe ref struct ChunkEnumerator<T0, T1> where T0 : struct where T1 : struct
    {
        private static readonly ComponentType[] Columns = ComponentTypes<T0, T1>.Share;
        private QueryTraversalCursor _cursor;
        private readonly QueryReadState _readState;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ChunkEnumerator(EntityManager manager, QueryBuilder builder)
        {
            if (builder.HasRelationshipFilter)
                throw new NotSupportedException("Chunk queries do not expose per-entity relationship masks; use an entity query.");
            _cursor = new QueryTraversalCursor(manager, builder, Columns);
            _readState = new QueryReadState(manager, ComponentTypeManager.IsRelationType<T0>() || ComponentTypeManager.IsRelationType<T1>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            _readState.ValidateTracked();
            return _cursor.MoveNextChunk(out _);
        }

        public ChunkResult<T0, T1> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                _readState.ValidateTracked();
                if (!_cursor.HasChunk) throw new InvalidOperationException();
                return new ChunkResult<T0, T1>(in _cursor.ChunkReference, _cursor.ComponentBase(0), _cursor.ComponentBase(1), _readState);
            }
        }
    }

    public unsafe readonly ref struct ChunkResult<T0, T1> where T0 : struct where T1 : struct
    {
        private readonly ref readonly Chunk _chunk;
        private readonly nint _base0;
        private readonly nint _base1;
        private readonly QueryReadState _readState;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ChunkResult(in Chunk chunk, nint base0, nint base1, QueryReadState readState)
        {
            _chunk = ref chunk;
            _base0 = base0;
            _base1 = base1;
            _readState = readState;
        }

        public int Length { [MethodImpl(MethodImplOptions.AggressiveInlining)] get { _readState.ValidateTracked(); return _chunk.EntityCount; } }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T0* GetPtr0() { _readState.ValidateTracked(); return (T0*)_base0; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T1* GetPtr1() { _readState.ValidateTracked(); return (T1*)_base1; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T0> GetSpan0()
        {
            ComponentTypeManager.CheckWritableAccess<T0>(_readState);
            return new Span<T0>((void*)_base0, _chunk.EntityCount);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<T0> GetReadOnlySpan0()
        {
            _readState.ValidateTracked();
            return new ReadOnlySpan<T0>((void*)_base0, _chunk.EntityCount);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T1> GetSpan1()
        {
            ComponentTypeManager.CheckWritableAccess<T1>(_readState);
            return new Span<T1>((void*)_base1, _chunk.EntityCount);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<T1> GetReadOnlySpan1()
        {
            _readState.ValidateTracked();
            return new ReadOnlySpan<T1>((void*)_base1, _chunk.EntityCount);
        }

        /// <summary>
        /// 获取指定 enableable 组件的位图掩码。
        /// </summary>
        /// <typeparam name="T">必须实现 <see cref="IEnableableComponent"/> 的组件类型</typeparam>
        /// <returns>该组件在当前 Chunk 中的位图掩码</returns>
        /// <exception cref="InvalidOperationException">如果组件不可启用或不存在于当前 Archetype 中</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe BitMask GetEnabledMask<T>() where T : struct, IEnableableComponent
        {
            _readState.ValidateTracked();
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
            _readState.ValidateTracked();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            return _chunk.GetComponentEnabled(idx, entityIndex);
        }

        /// <summary>
        /// 设置指定实体上 enableable 组件的启用状态。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponentEnabled<T>(int entityIndex, bool enabled) where T : struct, IEnableableComponent
        {
            _readState.ValidateTracked();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            _chunk.SetComponentEnabled(idx, entityIndex, enabled);
        }
    }


}
