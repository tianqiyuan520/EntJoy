using System;
using System.Runtime.CompilerServices;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public unsafe ref struct QueryEnumerable<T0, T1> where T0 : struct where T1 : struct
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;

        internal QueryEnumerable(EntityManager entityManager, QueryBuilder builder)
        {
            _entityManager = entityManager;
            _builder = builder;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryEnumerator<T0, T1> GetEnumerator()
            => new QueryEnumerator<T0, T1>(_entityManager, _builder);
    }

    // 实体级迭代：archetype → chunk → slot。每 chunk 提升组件基址一次（等价 IJobChunk 循环形态），
    // 循环内逐 slot 产出 ref 组件对，密集 OOD 访问零间接、与 class 持平。
    public unsafe ref struct QueryEnumerator<T0, T1>
        where T0 : struct
        where T1 : struct
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;
        private int _archIndex;
        private int _chunkIndex;
        private int _slotIndex;
        private int _count;
        private Archetype _currentArch;
        private Chunk _currentChunk;
        private int _t0Idx;
        private int _t1Idx;
        private T0* _t0Base;
        private T1* _t1Base;

        internal QueryEnumerator(EntityManager entityManager, QueryBuilder builder)
        {
            _entityManager = entityManager;
            _builder = builder;
            _archIndex = 0;
            _chunkIndex = 0;
            _slotIndex = 0;
            _count = 0;
            _currentArch = null;
            _currentChunk = default;
            _t0Idx = -1;
            _t1Idx = -1;
            _t0Base = null;
            _t1Base = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MoveNextArchetype()
        {
            while (_archIndex < _entityManager.ArchetypeCount)
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MoveNextChunk()
        {
            if (_currentArch == null) return false;
            var chunks = _currentArch.ChunkList;
            while (_chunkIndex < chunks.Count)
            {
                var chunk = chunks[_chunkIndex];
                _chunkIndex++;
                if (chunk.EntityCount > 0)
                {
                    _currentChunk = chunk;
                    _count = chunk.EntityCount;
                    _t0Base = (T0*)chunk.GetComponentArrayPointer(_t0Idx);
                    _t1Base = (T1*)chunk.GetComponentArrayPointer(_t1Idx);
                    _slotIndex = 0;
                    return true;
                }
            }
            _currentChunk = default;
            return MoveNextArchetype();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (_currentChunk.MemoryBlock == nint.Zero)
                return MoveNextArchetype();
            _slotIndex++;
            if (_slotIndex < _count)
                return true;
            _currentChunk = default;
            return MoveNextChunk();
        }

        public EntityQueryResult<T0, T1> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (_currentChunk.MemoryBlock == nint.Zero) throw new InvalidOperationException();
                return new EntityQueryResult<T0, T1>(_t0Base + _slotIndex, _t1Base + _slotIndex);
            }
        }
    }

    // 直接持有裸指针(非 ref 字段):避免 readonly ref struct 的 ref 存活跟踪,
    // 让 JIT 能把这 2 个指针放进寄存器、彻底内联进 foreach 循环体。
    public unsafe readonly struct EntityQueryResult<T0, T1> where T0 : struct where T1 : struct
    {
        private readonly T0* _c0;
        private readonly T1* _c1;

        public EntityQueryResult(T0* c0, T1* c1)
        {
            _c0 = c0;
            _c1 = c1;
        }

        public ref T0 Comp0
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref *_c0;
        }

        public ref T1 Comp1
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref *_c1;
        }
    }

}
