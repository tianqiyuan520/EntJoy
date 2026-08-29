using EntJoy.Collections;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 查询规则指纹：唯一标识一组查询条件（All/Any/None/AllEnabled + SharedFilter）。
    /// 相同指纹的查询共享同一个 <see cref="EntityQuery"/> 实例（World 注册表复用）。
    /// 基于 ComponentType.Id 计算，与匹配语义一致（Id 全局唯一映射 Type）。
    /// </summary>
    public readonly struct QueryKey : IEquatable<QueryKey>
    {
        private readonly int[] _allIds;
        private readonly int[] _anyIds;
        private readonly int[] _noneIds;
        private readonly int[] _allEnabledIds;
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

        public QueryKey(QueryBuilder builder)
        {
            _allIds = ExtractIds(builder.All);
            _anyIds = ExtractIds(builder.Any);
            _noneIds = ExtractIds(builder.None);
            _allEnabledIds = ExtractIds(builder.AllEnabled);
            _sharedTypeId = builder.SharedFilterType.Id;
            _hasSharedFilter = builder.HasSharedFilter;
            _sharedFilterValue = builder.SharedFilterValue;
            _sharedFilterValueHash = builder.SharedFilterValue?.GetHashCode() ?? 0;
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

        private static int[] ExtractIds(ComponentType[] types)
        {
            if (types == null || types.Length == 0) return Array.Empty<int>();
            var ids = new int[types.Length];
            for (int i = 0; i < types.Length; i++) ids[i] = types[i].Id;
            // 排序归一：匹配语义与顺序无关（HasAllOf/HasAnyOf/HasNoneOf 均不依赖顺序），
            // 排序后相同集合不同声明顺序 → 同一指纹 → 共享实例。
            Array.Sort(ids);
            return ids;
        }

        private static int HashIds(int[] ids)
        {
            unchecked
            {
                int h = 0;
                for (int i = 0; i < ids.Length; i++) h = h * 31 + ids[i];
                return h;
            }
        }

        private static bool SequenceEquals(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
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
                && _hasRelationshipFilter == other._hasRelationshipFilter
                && _relTypeId == other._relTypeId
                && _relTargetId == other._relTargetId
                && _relTargetVersion == other._relTargetVersion;
        }

        public override bool Equals(object obj) => obj is QueryKey other && Equals(other);

        public override int GetHashCode() => _hash;
    }

    /// <summary>
    /// 实体查询：按规则匹配 Archetype 并收集非空 Chunk。
    /// 支持共享模式（World 注册表按 <see cref="QueryKey"/> 复用实例）：
    /// 结构变更时由 World 统一标记刷新，查询访问时无锁版本检查。
    /// 优化：archetype 签名匹配集合跨版本复用（仅新建 Archetype 时重扫），
    /// 结构变更只重收 chunk，避免每次全量重扫所有 Archetype。
    /// </summary>
    public sealed unsafe class EntityQuery
    {
        private readonly World _world;
        private readonly QueryBuilder _builder;
        private readonly QueryKey _key;
        private readonly List<Archetype> _matchingArchetypes = new();
        private readonly List<Chunk> _chunks = new();
        private int _cachedStructuralVersion = -1;
        // 已扫描过的 Archetype 总数（判定是否需要重扫签名匹配集合）
        private int _scannedArchetypeCount = 0;

        /// <summary>当前查询的规则指纹（共享注册表键）。</summary>
        public QueryKey Key => _key;

        public EntityQuery(World world, QueryBuilder builder)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
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
                EnsureUpToDate();
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

            var entityManager = _world.EntityManager;
            for (int archetypeIndex = 0; archetypeIndex < entityManager.ArchetypeCount; archetypeIndex++)
            {
                var archetype = entityManager.Archetypes[archetypeIndex];
                if (archetype == null || !archetype.IsMatch(_builder))
                {
                    continue;
                }

                _matchingArchetypes.Add(archetype);
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

            _scannedArchetypeCount = entityManager.ArchetypeCount;
            _cachedStructuralVersion = entityManager.StructuralVersion;
        }

        /// <summary>
        /// 增量刷新（结构变更后调用）：若 Archetype 集合无变化（没有新建/删除 Archetype），
        /// 复用签名匹配集合，只重新收集各 Archetype 的 chunk；否则退回全量刷新。
        /// </summary>
        public void RefreshIncremental()
        {
            var entityManager = _world.EntityManager;
            int archCount = entityManager.ArchetypeCount;

            // Archetype 签名集合未变化 → 复用匹配集合，只重收 chunk
            if (archCount == _scannedArchetypeCount)
            {
                _chunks.Clear();
                for (int i = 0; i < _matchingArchetypes.Count; i++)
                {
                    var archetype = _matchingArchetypes[i];
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
            }
            else
            {
                // Archetype 新增/删除 → 全量重扫
                Refresh();
            }

            _cachedStructuralVersion = entityManager.StructuralVersion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureUpToDate()
        {
            // 带变更过滤（WithChanged）的查询必须每次访问都重评 chunk 过滤：
            // 变更位是帧级数据态（Set/SetSharedComponent 就地改值、ClearAllChangedBitMasks
            // 都不 bump 结构版本），仅靠结构版本检查会导致结果陈旧。
            if (_cachedStructuralVersion != _world.EntityManager.StructuralVersion
                || (_builder.ChangedComponents != null && _builder.ChangedComponents.Length > 0))
            {
                RefreshIncremental();
            }
        }

        public int CalculateEntityCount()
        {
            EnsureUpToDate();
            int total = 0;
            for (int i = 0; i < _chunks.Count; i++)
            {
                total += _chunks[i].EntityCount;
            }

            return total;
        }

        public NativeArray<T> ToComponentDataArray<T>(Allocator allocator = Allocator.Persistent) where T : unmanaged
        {
            EnsureUpToDate();
            // 使用 _chunks 列表而非 _matchingArchetypes[].GetChunks() 来确保
            // 与 CalculateEntityCount() 计数一致，避免竞态引发的堆缓冲区溢出
            int total = CalculateEntityCount();
            var result = new NativeArray<T>(total, allocator);
            int dstIndex = 0;
            int elementSize = Unsafe.SizeOf<T>();

            for (int chunkIdx = 0; chunkIdx < _chunks.Count; chunkIdx++)
            {
                var chunk = _chunks[chunkIdx];
                int count = chunk.EntityCount;
                if (count == 0) continue;

                var arch = chunk.Archetype;
                if (!TryGetComponentTypeIndex<T>(arch, out int componentIndex))
                    continue;

                var srcPtr = (byte*)chunk.GetComponentArrayPointer(componentIndex);
                var dstPtr = (byte*)result.GetUnsafePtr() + dstIndex * elementSize;
                Unsafe.CopyBlock(dstPtr, srcPtr, (uint)(count * elementSize));
                dstIndex += count;
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
