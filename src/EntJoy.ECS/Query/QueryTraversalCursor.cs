using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EntJoy.ECS
{
    internal sealed unsafe class QueryTraversalPlan
    {
        internal readonly EntityQuery Query;
        internal readonly EntityManager Manager;
        internal readonly QueryBuilder Filter;
        internal readonly bool HasEntityFilter;
        // 无实体/Shared/变更过滤：Chunk 全量命中，可按预计算表推进。
        internal readonly bool CanUseChunkTable;
        private readonly ComponentType[] _columns;
        private int _archetypeVersion = -1;
        private ArchetypeColumns[] _archetypes = Array.Empty<ArchetypeColumns>();
        private QueryReadState _storageState;
        // 快照数组按两代轮转复用：每次发布改写上一代，稳态下结构变更不再分配数组。
        // 借用失效后的数组可能被覆盖；读取前由实体枚举器或 Chunk 视图校验借用。
        private const int SnapshotGenerations = 2;
        // 一代快照：数组按 Capacity 分配、只增不减；ChunkCount 是本次有效块数，发布前由 FillSnapshot 写入。
        internal sealed class TraversalSnapshot
        {
            internal static readonly TraversalSnapshot Empty = new(
                Array.Empty<Chunk>(), Array.Empty<nint>(), Array.Empty<QueryChunkEntry>(), Array.Empty<int>(), 0);

            internal readonly Chunk[] Chunks;
            internal readonly nint[] ComponentBases;
            internal readonly QueryChunkEntry[] ChunkTable;
            internal readonly int[] RelationColumns;
            // 本代数组按多少个块分配（只增不减，复用判据）
            internal readonly int Capacity;
            // 本次发布的有效块数；发布线程填完再发布
            internal int ChunkCount;

            internal TraversalSnapshot(Chunk[] chunks, nint[] componentBases, QueryChunkEntry[] chunkTable, int[] relationColumns, int capacity)
            {
                Chunks = chunks;
                ComponentBases = componentBases;
                ChunkTable = chunkTable;
                RelationColumns = relationColumns;
                Capacity = capacity;
                ChunkCount = 0;
            }
        }

        private readonly TraversalSnapshot[] _generations = new TraversalSnapshot[SnapshotGenerations];
        private int _generation;
        private TraversalSnapshot _snapshot = TraversalSnapshot.Empty;
        internal TraversalSnapshot Snapshot => Volatile.Read(ref _snapshot);
        internal int ColumnCount => _columns.Length;

        internal readonly struct ArchetypeColumns
        {
            internal readonly Archetype Archetype;
            internal readonly int[] Offsets;
            internal readonly int RelationColumn;

            internal ArchetypeColumns(Archetype archetype, ComponentType[] columns, QueryBuilder filter)
            {
                Archetype = archetype;
                Offsets = new int[columns.Length];
                bool listRelation = filter.HasRelationshipFilter && filter.RelationshipFilterType.IsMultiRelation
                    && filter.RelationshipFilterType.MultiRelationMaxSlots == 0;
                for (int i = 0; i < columns.Length; i++)
                {
                    int column = listRelation && columns.Length == 2 && i == 1 && columns[i].Id == filter.RelationshipFilterType.Id
                        ? -1 : archetype.GetComponentTypeIndex(columns[i]);
                    Offsets[i] = column < 0 ? -1 : archetype.GetChunkOffsetsNative()[column];
                }
                RelationColumn = filter.HasRelationshipFilter && !listRelation
                    ? archetype.GetComponentTypeIndex(filter.RelationshipFilterType) : -1;
            }
        }

        internal QueryTraversalPlan(EntityQuery query, EntityManager manager, QueryBuilder filter, ComponentType[] columns)
        {
            Query = query;
            Manager = manager;
            Filter = filter;
            HasEntityFilter = filter.HasRelationshipFilter || (filter.AllEnabled != null && filter.AllEnabled.Length > 0);
            CanUseChunkTable = !HasEntityFilter && !filter.HasSharedFilter
                && (filter.ChangedComponents == null || filter.ChangedComponents.Length == 0);
            _columns = columns;
        }

        internal void Refresh()
        {
            Manager.CheckDisposed();
            if (_storageState.IsCurrent) return;
            EnsureArchetypeColumns();
            PublishSnapshot();
        }

        // 原型集合变了才重建列偏移表；单次结构变更不影响列布局。
        private void EnsureArchetypeColumns()
        {
            if (_archetypeVersion == Manager.ArchetypeSetVersion) return;
            var matches = Query.MatchingArchetypes;
            var archetypes = new ArchetypeColumns[matches.Count];
            for (int i = 0; i < matches.Count; i++)
                archetypes[i] = new ArchetypeColumns(matches[i], _columns, Filter);
            _archetypes = archetypes;
            _archetypeVersion = Manager.ArchetypeSetVersion;
        }

        private void PublishSnapshot()
        {
            int capacity = 0;
            foreach (var archetype in _archetypes) capacity = checked(capacity + archetype.Archetype.ChunkCount);
            var snapshot = RentSnapshot(capacity);
            snapshot.ChunkCount = FillSnapshot(snapshot);
            Volatile.Write(ref _snapshot, snapshot);
            _generation = (_generation + 1) % SnapshotGenerations;
            _storageState = new QueryReadState(Manager, trackRelationships: false);
        }

        // 按当前原型集合重写这一代数组；返回有效块数（空块跳过，数组尾部保持旧值不读）。
        private int FillSnapshot(TraversalSnapshot snapshot)
        {
            int count = 0;
            foreach (var archetype in _archetypes)
                foreach (var chunk in archetype.Archetype.ChunkSpan)
                {
                    if (chunk.EntityCount == 0) continue;
                    snapshot.Chunks[count] = chunk;
                    int first = count * ColumnCount;
                    for (int column = 0; column < ColumnCount; column++)
                    {
                        int offset = archetype.Offsets[column];
                        snapshot.ComponentBases[first + column] = offset < 0 ? 0 : chunk.MemoryBlock + offset;
                    }
                    if (CanUseChunkTable)
                    {
                        snapshot.ChunkTable[count] = new QueryChunkEntry(
                            snapshot.ComponentBases[first],
                            ColumnCount > 1 ? snapshot.ComponentBases[first + 1] : 0,
                            ColumnCount > 2 ? snapshot.ComponentBases[first + 2] : 0,
                            chunk.EntityCount);
                    }
                    if (Filter.HasRelationshipFilter) snapshot.RelationColumns[count] = archetype.RelationColumn;
                    count++;
                }
            return count;
        }

        private TraversalSnapshot RentSnapshot(int capacity)
        {
            var cached = _generations[_generation];
            if (cached != null && cached.Capacity >= capacity) return cached;
            var fresh = new TraversalSnapshot(
                new Chunk[capacity],
                new nint[checked(capacity * ColumnCount)],
                CanUseChunkTable ? new QueryChunkEntry[capacity] : Array.Empty<QueryChunkEntry>(),
                Filter.HasRelationshipFilter ? new int[capacity] : Array.Empty<int>(),
                capacity);
            _generations[_generation] = fresh;
            return fresh;
        }
    }

    /// <summary>
    /// 可整块命中的查询的每 Chunk 推进数据：前 3 列基址与实体数，与计划中 Chunk 顺序一致。
    /// 第 4 列起没有内联基址，宽元组走 <see cref="QueryTraversalCursor.Next"/> 路径。
    /// </summary>
    public readonly struct QueryChunkEntry
    {
        public readonly nint Base0;
        public readonly nint Base1;
        public readonly nint Base2;
        public readonly int Count;

        internal QueryChunkEntry(nint base0, nint base1, nint base2, int count)
        {
            Base0 = base0;
            Base1 = base1;
            Base2 = base2;
            Count = count;
        }
    }

    /// <summary>运行时与生成枚举器共用的 Chunk 游标；调用方在推进和读取前校验 QueryReadState。</summary>
    public unsafe struct QueryTraversalCursor
    {
        private readonly QueryTraversalPlan _plan;
        private readonly QueryTraversalPlan.TraversalSnapshot _snapshot;
        private readonly object _sharedValue;
        private readonly RelationSlot _target;
        private readonly bool _hasChangedFilter;
        private readonly bool _hasChunkFilter;
        private readonly bool _targetAlive;
        private int _chunkIndex;
        private int _baseIndex;
        private int _slot;
        private int _count;
        private ulong _enabledWord;
        private int _enabledWordIndex;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryTraversalCursor(EntityManager manager, QueryBuilder builder, ComponentType[] columns)
        {
            _plan = manager.GetArchetypeQuery(builder).GetTraversalPlan(columns);
            // 游标固定迭代构造时的快照：之后别的线程刷新 plan 不会改变本次遍历看到的数据。
            _snapshot = _plan.Snapshot;
            _sharedValue = builder.SharedFilterValue;
            _target = builder.RelationshipFilterTarget;
            _hasChangedFilter = builder.ChangedComponents != null && builder.ChangedComponents.Length > 0;
            _hasChunkFilter = builder.HasSharedFilter || _hasChangedFilter;
            _targetAlive = !builder.HasRelationshipFilter || manager.IsRelationshipTargetAlive(_target);
            _chunkIndex = -1;
            _baseIndex = 0;
            _slot = 0;
            _count = 0;
            _enabledWord = 0;
            _enabledWordIndex = -1;
        }

        public readonly bool HasChunk => _count != 0;
        public readonly int Slot => _slot;
        public readonly int RangeEnd => !HasChunk ? 0 : !_plan.HasEntityFilter ? _count
            : _plan.Filter.HasRelationshipFilter ? _slot + 1 : QueryEntityRange.End(_enabledWord, _slot, _count);
        public readonly Chunk Chunk => HasChunk
            ? _snapshot.Chunks[_chunkIndex] : default;
        public ref readonly Chunk ChunkReference
        {
            get
            {
                if (!HasChunk) throw new InvalidOperationException("游标没有当前 Chunk：需要先 MoveNext 成功。");
                return ref _snapshot.Chunks[_chunkIndex];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly nint ComponentBase(int column)
        {
            if (!HasChunk) return 0;
            return _snapshot.ComponentBases[_baseIndex + column];
        }

        /// <summary>计划能否按预计算表整块推进；为 false 时用 <see cref="Next"/> 逐实体过滤。</summary>
        public readonly bool CanUseChunkTable => _plan.CanUseChunkTable && !_hasChunkFilter;

        public readonly int ChunkCount => _snapshot.ChunkCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref readonly QueryChunkEntry ChunkEntry(int index) => ref _snapshot.ChunkTable[index];

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static QueryTraversalCursor Next(QueryTraversalCursor cursor, int slot)
        {
            cursor._slot = slot;
            if (!cursor._plan.HasEntityFilter)
            {
                if (cursor._count == 0 || slot >= cursor._count) cursor.MoveNextChunk(out _);
            }
            else cursor.Advance();
            return cursor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNextChunk(out Chunk chunk)
        {
            if (_hasChunkFilter) return MoveNextFilteredChunk(out chunk);
            return MoveNextCandidate(out chunk);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MoveNextCandidate(out Chunk chunk)
        {
            if (++_chunkIndex < _snapshot.ChunkCount)
            {
                chunk = _snapshot.Chunks[_chunkIndex];
                _count = chunk.EntityCount;
                _baseIndex = _chunkIndex * _plan.ColumnCount;
                _slot = 0;
                _enabledWord = 0;
                _enabledWordIndex = -1;
                return true;
            }
            _chunkIndex = _snapshot.ChunkCount;
            _count = 0;
            _baseIndex = 0;
            _slot = 0;
            chunk = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool MoveNextFilteredChunk(out Chunk chunk)
        {
            while (MoveNextCandidate(out chunk))
            {
                if (_plan.Filter.HasSharedFilter && !_plan.Query.MatchesSharedValue(chunk, _sharedValue)) continue;
                if (_hasChangedFilter && !chunk.HasAnyEntityChanged()) continue;
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Advance()
        {
            if (!_targetAlive) { _count = 0; _slot = 0; _baseIndex = 0; return; }
            while (true)
            {
                Chunk chunk;
                if (_count == 0 || _slot >= _count)
                {
                    if (!MoveNextChunk(out chunk)) return;
                }
                else chunk = Chunk;
                var enabled = _plan.Filter.AllEnabled;
                if (enabled != null && enabled.Length > 0)
                {
                    while (_slot < _count)
                    {
                        int wordIndex = _slot >> 6;
                        if (wordIndex != _enabledWordIndex)
                        {
                            _enabledWord = QueryEntityRange.EnabledWord(chunk, enabled, wordIndex);
                            _enabledWordIndex = wordIndex;
                        }
                        ulong mask = _enabledWord >> (_slot & 63);
                        if (mask == 0) { _slot = (wordIndex + 1) << 6; continue; }
                        _slot += BitOperations.TrailingZeroCount(mask);
                        if (_slot >= _count) break;
                        if (MatchesRelationship(chunk)) return;
                        _slot++;
                    }
                }
                else
                {
                    while (_slot < _count)
                    {
                        if (MatchesRelationship(chunk)) return;
                        _slot++;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly bool MatchesRelationship(Chunk chunk)
        {
            if (!_plan.Filter.HasRelationshipFilter) return true;
            var type = _plan.Filter.RelationshipFilterType;
            int maxSlots = type.MultiRelationMaxSlots;
            if (type.IsMultiRelation && maxSlots == 0)
                return _plan.Query.MatchesRelationshipTarget(chunk, _slot, _target);
            int column = _snapshot.RelationColumns[_chunkIndex];
            byte* value = (byte*)chunk.GetComponentArrayPointer(column) + _slot * type.Size;
            for (int i = 0; i < Math.Max(1, maxSlots); i++)
                if (((RelationSlot*)value)[i].Matches(_target)) return true;
            return false;
        }
    }
}
