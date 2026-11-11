using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    public unsafe sealed partial class Archetype
    {
        private ComponentType[] types;
        public ReadOnlySpan<ComponentType> Types => types;
        private Dictionary<ComponentType, int> componentTypeRecorder;
        private readonly int[] _componentColumns;
        internal bool HasRelationships { get; }
        internal bool HasSharedColumns { get; private set; }
        internal bool HasDisposableComponents { get; private set; }
        public int ComponentCount { get; private set; }
        public int EntityCount { get; private set; }
        private bool _disposed;
        internal EntityManager Owner { get; set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CheckReadable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Archetype));
        }

        // Prefab 标记组件类型（查询默认排除用）
        private static readonly ComponentType s_prefabType = typeof(Prefab);

        // 按组件 id 缓存的增删目标原型（null = 未缓存）
        private Archetype[] _addEdgeByTypeId = System.Array.Empty<Archetype>();
        private Archetype[] _removeEdgeByTypeId = System.Array.Empty<Archetype>();

        public Archetype(ComponentType[] ts)
            : this(ts, ChunkCacheLayout.Current) { }

        internal Archetype(ComponentType[] ts, ChunkCacheLayout cacheLayout)
        {
            _cacheLayout = cacheLayout;
            types = ts;
            ComponentCount = ts.Length;
            componentTypeRecorder = new Dictionary<ComponentType, int>(ts.Length);
            int maxTypeId = -1;
            for (int i = 0; i < ts.Length; i++) maxTypeId = Math.Max(maxTypeId, ts[i].Id);
            // 全局类型 ID 可能稀疏；密集索引的内存只随当前 Archetype 的列数增长。
            int denseLength = (int)Math.Min((long)maxTypeId + 1, Math.Max(64L, ts.Length * 4L));
            _componentColumns = new int[denseLength];
            for (int i = 0; i < ComponentCount; i++)
            {
                componentTypeRecorder.Add(types[i], i);
                if ((uint)types[i].Id < (uint)_componentColumns.Length)
                    _componentColumns[types[i].Id] = i + 1;
                HasRelationships |= types[i].IsRelation;
                HasSharedColumns |= types[i].IsShared;
                HasDisposableComponents |= types[i].IsDisposable;
            }
            _chunkCapacity = ChunkCapacityOverride > 0
                ? Math.Max(64, ChunkCapacityOverride)   // 位图/遍历以 64 个实体为自然边界。
                : CalculateOptimalChunkCapacity(types);
            EnsureCapacityWithinPackedLimit(_chunkCapacity);
            _sharedMetadata = ChunkMetadata.Create(this, _chunkCapacity, types, true, _cacheLayout);
            _chunkOffsetsNative = NativeLocateStorage.AllocateOffsets(_sharedMetadata.ComponentOffsets);
        }

        // 槽位只有 16 位：容量越界会让 SetLocation 静默截断，之后读写落到错误实体。
        // 校验放在建立容量的入口（构造与扩块），不进逐实体路径。
        internal static void EnsureCapacityWithinPackedLimit(int capacity)
        {
            if (capacity > EntityIndexInWorld.PackedSlotLimit)
                throw new InvalidOperationException(
                    $"Chunk 容量 {capacity} 超过位置表打包上限 {EntityIndexInWorld.PackedSlotLimit}；调小 ChunkCapacityOverride。");
        }

        /// <summary>查询匹配检查：是否符合 QueryBuilder 条件。</summary>
        public bool IsMatch(QueryBuilder builder)
        {
            if (!PassesPrefabRule(builder)) return false;
            if (builder.All != null && builder.All.Length > 0 && !HasAllOf(builder.All.AsSpan())) return false;
            if (builder.Any != null && builder.Any.Length > 0 && !HasAnyOf(builder.Any.AsSpan())) return false;
            if (builder.None != null && builder.None.Length > 0 && !HasNoneOf(builder.None.AsSpan())) return false;
            if (builder.AllEnabled != null)
                foreach (var ct in builder.AllEnabled)
                    if (!componentTypeRecorder.ContainsKey(ct)) return false;
            if (!PassesRelationshipFilter(builder)) return false;
            return PassesSharedFilter(builder);
        }

        // 默认排除 Prefab 模板实体（对齐 Unity）：除非查询显式 WithAll<Prefab>。
        private bool PassesPrefabRule(QueryBuilder builder)
        {
            if (!componentTypeRecorder.ContainsKey(s_prefabType)) return true;
            if (builder.All == null) return false;
            for (int i = 0; i < builder.All.Length; i++)
                if (builder.All[i].Id == s_prefabType.Id) return true;
            return false;
        }

        // 关系过滤要求拥有该关系列；托管多值关系没有列，不参与此项。
        private bool PassesRelationshipFilter(QueryBuilder builder)
        {
            if (!builder.HasRelationshipFilter) return true;
            var type = builder.RelationshipFilterType;
            if (type.IsMultiRelation && type.MultiRelationMaxSlots == 0) return true;
            return componentTypeRecorder.ContainsKey(type);
        }

        // 共享值过滤要求拥有该列。缺失时 MatchesSharedFilter 的列查找会字典 miss，
        // 三个收集路径（EntityQuery.Refresh / ChunkJobCollector / QueryEnumerator）都依赖此处先行排除。
        private bool PassesSharedFilter(QueryBuilder builder)
            => !builder.HasSharedFilter || componentTypeRecorder.ContainsKey(builder.SharedFilterType);

        public bool HasAllOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (!componentTypeRecorder.ContainsKey(spanTypes[i]))
                    return false;
            }
            return true;
        }

        public bool HasAnyOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (componentTypeRecorder.ContainsKey(spanTypes[i]))
                    return true;
            }
            return false;
        }

        public bool HasNoneOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (componentTypeRecorder.ContainsKey(spanTypes[i]))
                    return false;
            }
            return true;
        }

        public bool Has(ComponentType type) => componentTypeRecorder.ContainsKey(type);
    }
}
