using EntJoy.Collections;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 查询规则指纹：区分组件条件、共享/关系过滤、变更过滤和数量限制。
    /// 相同指纹的查询共享同一个 <see cref="EntityQuery"/> 实例（EntityManager 注册表复用）。
    /// 基于 ComponentType.Id 计算，与匹配语义一致（Id 全局唯一映射 Type）。
    /// </summary>
    public readonly struct QueryKey : IEquatable<QueryKey>
    {
        private readonly ComponentType[] _allIds;
        private readonly ComponentType[] _anyIds;
        private readonly ComponentType[] _noneIds;
        private readonly ComponentType[] _allEnabledIds;
        private readonly ComponentType[] _changedIds;
        private readonly int _minChangedVersion;
        private readonly int _limitCount;
        private readonly int _sharedTypeId;
        private readonly bool _hasSharedFilter;
        // SharedFilterValue 参与指纹——同类型不同值（Material(2) vs Material(3)）是不同查询
        private readonly object _sharedFilterValue;
        private readonly int _sharedFilterValueHash;
        // 关系过滤（TRel.Id + target.Id + target.Version）
        private readonly int _relTypeId;
        private readonly int _relTargetId;
        private readonly int _relTargetVersion;
        private readonly bool _hasRelationshipFilter;
        private readonly int _hash;

        public QueryKey(QueryBuilder builder) : this(builder, true) { }

        internal QueryKey(QueryBuilder builder, bool snapshot)
        {
            _allIds = snapshot ? Snapshot(builder.All) : builder.All;
            _anyIds = snapshot ? Snapshot(builder.Any) : builder.Any;
            _noneIds = snapshot ? Snapshot(builder.None) : builder.None;
            _allEnabledIds = snapshot ? Snapshot(builder.AllEnabled) : builder.AllEnabled;
            _changedIds = snapshot ? Snapshot(builder.ChangedComponents) : builder.ChangedComponents;
            _minChangedVersion = builder.MinChangedVersion;
            _limitCount = builder.LimitCount;
            _sharedTypeId = builder.SharedFilterType.Id;
            _hasSharedFilter = builder.HasSharedFilter;
            _sharedFilterValue = builder.SharedFilterValue;
            _sharedFilterValueHash = _hasSharedFilter ? builder.SharedFilterValue?.GetHashCode() ?? 0 : 0;
            _relTypeId = builder.RelationshipFilterType.Id;
            _relTargetId = builder.RelationshipFilterTarget.TargetId;
            _relTargetVersion = builder.RelationshipFilterTarget.TargetVersion;
            _hasRelationshipFilter = builder.HasRelationshipFilter;

            unchecked
            {
                int hash = 17;
                hash = hash * 31 + HashIds(_allIds);
                hash = hash * 31 + HashIds(_anyIds);
                hash = hash * 31 + HashIds(_noneIds);
                hash = hash * 31 + HashIds(_allEnabledIds);
                hash = hash * 31 + HashIds(_changedIds);
                hash = hash * 31 + _minChangedVersion;
                hash = hash * 31 + _limitCount;
                hash = hash * 31 + _sharedTypeId;
                hash = hash * 31 + (_hasSharedFilter ? 1 : 0);
                hash = hash * 31 + _sharedFilterValueHash;
                hash = hash * 31 + _relTypeId;
                hash = hash * 31 + _relTargetId;
                hash = hash * 31 + _relTargetVersion;
                hash = hash * 31 + (_hasRelationshipFilter ? 1 : 0);
                _hash = hash;
            }
        }

        private static ComponentType[] Snapshot(ComponentType[] types)
            => types == null || types.Length == 0 ? Array.Empty<ComponentType>() : (ComponentType[])types.Clone();

        private static int HashIds(ComponentType[] types)
        {
            int hash = 0;
            if (types != null)
                foreach (var type in types) hash = unchecked(hash + HashCode.Combine(type.Id));
            return hash;
        }

        private static bool SequenceEquals(ComponentType[] a, ComponentType[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            int length = a?.Length ?? 0;
            if (length != (b?.Length ?? 0)) return false;
            for (int i = 0; i < length; i++)
            {
                if (a[i].Id == b[i].Id) continue;
                int occurrences = 0;
                for (int j = 0; j < length; j++)
                {
                    if (a[j].Id == a[i].Id) occurrences++;
                    if (b[j].Id == a[i].Id) occurrences--;
                }
                if (occurrences != 0) return false;
            }
            return true;
        }

        public bool Equals(QueryKey other)
        {
            return _hasSharedFilter == other._hasSharedFilter
                && _sharedTypeId == other._sharedTypeId
                && (_hasSharedFilter ? Equals(_sharedFilterValue, other._sharedFilterValue) : true)
                && SequenceEquals(_allIds, other._allIds)
                && SequenceEquals(_anyIds, other._anyIds)
                && SequenceEquals(_noneIds, other._noneIds)
                && SequenceEquals(_allEnabledIds, other._allEnabledIds)
                && SequenceEquals(_changedIds, other._changedIds)
                && _minChangedVersion == other._minChangedVersion
                && _limitCount == other._limitCount
                && _hasRelationshipFilter == other._hasRelationshipFilter
                && _relTypeId == other._relTypeId
                && _relTargetId == other._relTargetId
                && _relTargetVersion == other._relTargetVersion;
        }

        public override bool Equals(object obj) => obj is QueryKey other && Equals(other);

        public override int GetHashCode() => _hash;
    }

    /// <summary>
    /// 实体查询：按规则匹配 Archetype 并收集非空 Chunk；EntityManager 按 <see cref="QueryKey"/> 复用实例。
    /// Archetype 匹配集合跨版本复用（仅新建原型时重扫），结构变更只重收 chunk。
    /// 查询访问时检查管理器版本，版本不符即重建。
    /// </summary>
    public sealed unsafe class EntityQuery
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;
        private readonly QueryKey _key;
        private readonly List<Archetype> _matchingArchetypes = new();
        private readonly List<Chunk> _chunks = new();
        private readonly Dictionary<ComponentType[], QueryTraversalPlan> _traversalPlans = new();
        private int _cachedStructuralVersion = -1;
        private long _cachedRelationshipVersion = -1;
        // 已扫描过的 Archetype 总数（判定是否需要重扫签名匹配集合）
        private int _scannedArchetypeCount = 0;
        private int _scannedArchetypeVersion = -1;

        /// <summary>当前查询的规则指纹（共享注册表键）。</summary>
        public QueryKey Key => _key;

        internal QueryTraversalPlan GetTraversalPlan(ComponentType[] columns)
        {
            lock (_traversalPlans)
            {
                if (!_traversalPlans.TryGetValue(columns, out var plan))
                {
                    plan = new QueryTraversalPlan(this, _entityManager, _builder, columns);
                    _traversalPlans.Add(columns, plan);
                }
                plan.Refresh();
                return plan;
            }
        }

        /// <summary>用当前枚举器的共享值筛选候选 Chunk。</summary>
        public bool MatchesSharedValue(Chunk chunk, object value)
        {
            var builder = _builder;
            builder.SharedFilterValue = value;
            return _entityManager.MatchesSharedFilter(builder, chunk);
        }

        /// <summary>用当前枚举器的关系目标筛选候选实体。</summary>
        public bool MatchesRelationshipTarget(Chunk chunk, int slot, RelationSlot target)
        {
            var builder = _builder;
            builder.RelationshipFilterTarget = target;
            return _entityManager.MatchesRelationshipFilter(builder, chunk, slot);
        }

        public EntityQuery(World world, QueryBuilder builder)
            : this((world ?? throw new ArgumentNullException(nameof(world))).EntityManager, builder) { }

        internal EntityQuery(EntityManager entityManager, QueryBuilder builder)
        {
            _entityManager = entityManager;
            _builder = builder;
            _key = new QueryKey(builder);
            Refresh();
        }

        public int StructuralVersion
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                EnsureUpToDate();
                return _cachedStructuralVersion;
            }
        }

        public IReadOnlyList<Archetype> MatchingArchetypes
        {
            get
            {
                _entityManager.CheckDisposed();
                if (_scannedArchetypeVersion != _entityManager.ArchetypeSetVersion)
                    Refresh();
                return _matchingArchetypes;
            }
        }

        public IReadOnlyList<Chunk> Chunks
        {
            get
            {
                EnsureUpToDate();
                return _chunks;
            }
        }

        /// <summary>
        /// 全量刷新：重扫所有 Archetype 重建签名匹配集合 + 收集非空 chunk。
        /// </summary>
        public void Refresh()
        {
            _matchingArchetypes.Clear();
            _chunks.Clear();

            var entityManager = _entityManager;
            for (int archetypeIndex = 0; archetypeIndex < entityManager.ArchetypeCount; archetypeIndex++)
            {
                var archetype = entityManager.Archetypes[archetypeIndex];
                if (archetype == null || !archetype.IsMatch(_builder))
                {
                    continue;
                }

                _matchingArchetypes.Add(archetype);
                CollectChunks(archetype);
            }

            _scannedArchetypeCount = entityManager.ArchetypeCount;
            _scannedArchetypeVersion = entityManager.ArchetypeSetVersion;
            _cachedStructuralVersion = entityManager.StructuralVersion;
            _cachedRelationshipVersion = entityManager.RelationshipVersion;
        }

        // 收集该原型中满足 shared 与变更过滤的非空块。
        private void CollectChunks(Archetype archetype)
        {
            var entityManager = _entityManager;
            foreach (var chunk in archetype.ChunkSpan)
            {
                if (chunk.EntityCount > 0
                    && entityManager.MatchesSharedFilter(_builder, chunk)
                    && entityManager.MatchesChangedFilter(_builder, chunk))
                {
                    _chunks.Add(chunk);
                }
            }
        }

        /// <summary>
        /// 增量刷新（结构变更后调用）：若 Archetype 集合无变化（没有新建/删除 Archetype），
        /// 复用签名匹配集合，只重新收集各 Archetype 的 chunk；否则退回全量刷新。
        /// </summary>
        public void RefreshIncremental()
        {
            var entityManager = _entityManager;
            int archCount = entityManager.ArchetypeCount;

            // 判据必须是 Archetype 集合身份：只比数量时，Restore 后重建或一增一删抵消会复用已释放的
            // Archetype，查询静默返回 0 个实体。
            if (archCount == _scannedArchetypeCount
                && entityManager.ArchetypeSetVersion == _scannedArchetypeVersion)
            {
                _chunks.Clear();
                for (int i = 0; i < _matchingArchetypes.Count; i++)
                    CollectChunks(_matchingArchetypes[i]);
            }
            else
            {
                Refresh();
            }

            _cachedStructuralVersion = entityManager.StructuralVersion;
            _cachedRelationshipVersion = entityManager.RelationshipVersion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureUpToDate()
        {
            // 带变更过滤（WithChanged）的查询必须每次访问都重评 chunk 过滤：
            // 变更位是帧级数据态（Set/SetSharedComponent 就地改值、ClearAllChangedBitMasks
            // 都不 bump 结构版本），仅靠结构版本检查会导致结果陈旧。
            _entityManager.CheckDisposed();
            if (_cachedStructuralVersion != _entityManager.StructuralVersion
                || (_builder.HasRelationshipFilter && _cachedRelationshipVersion != _entityManager.RelationshipVersion)
                || _builder.HasSharedFilter
                || (_builder.ChangedComponents != null && _builder.ChangedComponents.Length > 0))
            {
                RefreshIncremental();
            }
        }

        private bool NeedsEntityFilter => _builder.HasRelationshipFilter || (_builder.AllEnabled != null && _builder.AllEnabled.Length > 0);

        private bool MatchesEntity(Chunk chunk, int slot)
        {
            if (!_entityManager.MatchesRelationshipFilter(_builder, chunk, slot)) return false;
            if (_builder.AllEnabled != null)
                foreach (var type in _builder.AllEnabled)
                    if (!chunk.GetComponentEnabled(chunk.Archetype.GetComponentTypeIndex(type), slot)) return false;
            return true;
        }

        public int CalculateEntityCount()
        {
            EnsureUpToDate();
            int total = 0;
            foreach (var chunk in _chunks)
            {
                if (!NeedsEntityFilter) { total = checked(total + chunk.EntityCount); continue; }
                for (int slot = 0; slot < chunk.EntityCount; slot++)
                    if (MatchesEntity(chunk, slot)) total++;
            }
            return total;
        }

        /// <summary>返回独立副本；关系和 Enabled 条件按实体应用，不暴露原列的写入视图。</summary>
        public NativeArray<T> ToComponentDataArray<T>(Allocator allocator = Allocator.Persistent) where T : unmanaged
        {
            EnsureUpToDate();
            int total = 0;
            foreach (var chunk in _chunks)
            {
                if (!TryGetComponentTypeIndex<T>(chunk.Archetype, out _)) continue;
                if (!NeedsEntityFilter) { total = checked(total + chunk.EntityCount); continue; }
                for (int slot = 0; slot < chunk.EntityCount; slot++)
                    if (MatchesEntity(chunk, slot)) total++;
            }
            var result = new NativeArray<T>(total, allocator);
            int destination = 0;
            int size = Unsafe.SizeOf<T>();
            foreach (var chunk in _chunks)
            {
                if (!TryGetComponentTypeIndex<T>(chunk.Archetype, out int column)) continue;
                byte* source = (byte*)chunk.GetComponentArrayPointer(column);
                byte* target = (byte*)result.GetUnsafePtr();
                if (!NeedsEntityFilter)
                {
                    Unsafe.CopyBlock(target + (long)destination * size, source, (uint)(chunk.EntityCount * size));
                    destination += chunk.EntityCount;
                    continue;
                }
                for (int slot = 0; slot < chunk.EntityCount; slot++)
                {
                    if (!MatchesEntity(chunk, slot)) continue;
                    Unsafe.CopyBlock(target + (long)destination * size, source + (long)slot * size, (uint)size);
                    destination++;
                }
            }
            return result;
        }

        private static bool TryGetComponentTypeIndex<T>(Archetype archetype, out int componentIndex) where T : unmanaged
        {
            var componentType = ComponentTypeManager.GetComponentType(typeof(T));
            var types = archetype.Types;
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i].Id == componentType.Id)
                {
                    componentIndex = i;
                    return true;
                }
            }

            componentIndex = -1;
            return false;
        }
    }
}
