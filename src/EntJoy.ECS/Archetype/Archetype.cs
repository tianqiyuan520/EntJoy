using EntJoy.Debugger;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace EntJoy.ECS
{
    // Archetype 主要
    public sealed partial class Archetype
    {
        private ComponentType[] types;
        public ReadOnlySpan<ComponentType> Types => types;
        private Dictionary<ComponentType, int> componentTypeRecorder;
        public int ComponentCount { get; private set; }
        public int EntityCount { get; private set; }

        // Prefab 标记组件类型（查询默认排除用）
        private static readonly ComponentType s_prefabType = typeof(Prefab);

        // Phase 2.1: Archetype Edges — 缓存 Add/Remove 目标 Archetype
        private readonly Dictionary<ComponentType, Archetype> _addEdges = new();
        private readonly Dictionary<ComponentType, Archetype> _removeEdges = new();

        // 全局变更版本号（组件修改/结构变更时递增）
        private int _globalVersion;
        public int GlobalVersion => _globalVersion;

        // AllEnabled 组合位图缓存（按 chunk enableVersion 失效）
        private readonly Dictionary<int, CombinedMaskCache> _maskCache = new();

        public Archetype(ComponentType[] ts)
        {
            types = ts;
            ComponentCount = ts.Length;
            componentTypeRecorder = new Dictionary<ComponentType, int>(ts.Length);
            for (int i = 0; i < ComponentCount; i++)
            {
                componentTypeRecorder.Add(types[i], i);
            }
            _chunkCapacity = CalculateOptimalChunkCapacity(types);
            _sharedMetadata = ChunkMetadata.Create(this, _chunkCapacity, types);
        }

        /// <summary>
        /// 查询匹配检查：是否符合 QueryBuilder 条件
        /// </summary>
        public bool IsMatch(QueryBuilder builder)
        {
            // 默认排除 Prefab 模板实体（对齐 Unity）：除非查询显式 WithAll<Prefab>
            if (componentTypeRecorder.ContainsKey(s_prefabType))
            {
                bool includesPrefab = false;
                if (builder.All != null)
                {
                    for (int i = 0; i < builder.All.Length; i++)
                        if (builder.All[i].Id == s_prefabType.Id) { includesPrefab = true; break; }
                }
                if (!includesPrefab)
                    return false;
            }

            if (builder.All != null && builder.All.Length > 0)
            {
                if (!HasAllOf(builder.All.AsSpan()))
                    return false;
            }
            if (builder.Any != null && builder.Any.Length > 0)
            {
                if (!HasAnyOf(builder.Any.AsSpan()))
                    return false;
            }
            if (builder.None != null && builder.None.Length > 0)
            {
                if (!HasNoneOf(builder.None.AsSpan()))
                    return false;
            }
            if (builder.AllEnabled != null)
            {
                foreach (var ct in builder.AllEnabled)
                {
                    if (!componentTypeRecorder.ContainsKey(ct))
                        return false;
                }
            }
            // 关系过滤要求拥有 TRel 列（不拆 Archetype，仅过滤）
            if (builder.HasRelationshipFilter)
            {
                if (!componentTypeRecorder.ContainsKey(builder.RelationshipFilterType))
                    return false;
            }
            // 共享值过滤要求拥有 SharedFilterType 列（与关系列同策略）。
            // 缺失时 MatchesSharedFilter 的 GetComponentTypeIndex 会字典 miss——三个收集路径
            // （EntityQuery.Refresh / ChunkJobCollector / QueryEnumerator）都依赖此处先行排除。
            if (builder.HasSharedFilter)
            {
                if (!componentTypeRecorder.ContainsKey(builder.SharedFilterType))
                    return false;
            }
            return true;
        }
    }

    // 组件类型判定
    public sealed partial class Archetype
    {
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

        public bool Has(Type type)
        {
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == type)
                    return true;
            }
            return false;
        }
    }

    // Archetype Chunk 管理
    public sealed partial class Archetype : IDisposable
    {
        private readonly List<Chunk> _chunkList = new();
        private readonly int _chunkCapacity;
        private readonly ChunkMetadata _sharedMetadata;
        private const int _chunkHeaderSize = 64;

        // Contiguous memory slab: 64 KB per slab. Multiple chunks are carved
        // from each slab so their component arrays are physically adjacent.
        private const int SLAB_SIZE = 64 * 1024;
        private const int SLAB_ALIGNMENT = 64 * 1024;
        private List<SlabInfo> _slabs = new();
        private SlabInfo _currentSlab;
        private int _currentSlabOffset;
        private int _chunkStride;
        // 已移除 Chunk 的空洞（复用：新 Chunk 优先从这里取，避免 slab 无限增长）
        private readonly List<nint> _freeChunks = new();

        /// <summary>单个 slab 的追踪信息（复用/压缩用）。</summary>
        private sealed class SlabInfo
        {
            public nint RawPtr;       // ChunkMemoryPool.Free 用（未对齐）
            public nint AlignedPtr;   // Chunk 地址基准（对齐后）
            public int ChunkCount;    // 该 slab 已分配的 Chunk 数
            public int ReleasedCount; // 已释放（移除）的 Chunk 数
        }

        public int ChunkCount => _chunkList.Count;
        public ref readonly List<Chunk> ChunkList => ref _chunkList;

        /// <summary>本 Archetype 已分配的 slab 数量（内存分析器用）。</summary>
        public int SlabCount => _slabs.Count;

        /// <summary>本 Archetype 已分配的 slab 总字节数（每 slab 64KB，内存分析器用）。</summary>
        public long SlabBytes => (long)_slabs.Count * SLAB_SIZE;
        /// <summary>
        /// 内部 _chunkList 的零拷贝只读视图（v3 Phase 1.4：调度/查询热路径遍历
        /// 不再经 GetChunks() 每次 new List&lt;Chunk&gt; 拷贝）。
        /// 使用前提：同线程顺序模型——构建快照期间主线程不会并发结构变更；
        /// job 并行执行期间对列表的后续修改不影响已构建的 ChunkJobData/EntityBatchData 快照。
        /// </summary>
        public ReadOnlySpan<Chunk> ChunkSpan
        {
            get
            {
                // .NET 10+ 签名：CollectionsMarshal.AsSpan(List<T>?) 为无 ref 重载
                //（旧 ref 重载在本机 SDK 10 下解析为 CS1615）。
                // 返回 List 底层数组的零拷贝视图（List<T> 为引用类型，别名零拷贝）。
                return CollectionsMarshal.AsSpan(_chunkList);
            }
        }

        private static int CalculateOptimalChunkCapacity(ComponentType[] types)
        {
            // Unity 风格的小物理 Chunk：Chunk 只负责存储和结构变化，
            // Job 的执行粒度由调度器的 BatchRange 独立决定。
            const int cacheLineSize = 64;
            const int targetChunkBytes = 16 * 1024;

            int entitySize = Marshal.SizeOf<Entity>();
            int totalComponentSize = entitySize;
            int enableableCount = 0;
            foreach (var type in types)
            {
                totalComponentSize += type.Size;
                if (type.IsEnableable) enableableCount++;
            }

            int alignmentOverhead = types.Length * cacheLineSize;
            const int bitmapBytesPer64Entities = 8;
            int bitmapOverheadPerEntity = (enableableCount * bitmapBytesPer64Entities + 63) / 64;

            int stride = totalComponentSize + bitmapOverheadPerEntity;
            int capacity = Math.Max(cacheLineSize,
                (targetChunkBytes - alignmentOverhead) / Math.Max(1, stride));

            // Bitmap 和 SIMD 遍历都以 64 个实体为自然边界。向下对齐，避免
            // 因为容量取整反而突破 16 KiB 目标。
            capacity &= ~(cacheLineSize - 1);
            return Math.Clamp(capacity, cacheLineSize, 131072);
        }

        public void AddEntity(Entity entity, out int chunkIndex, out int slotInChunk)
        {
            if (ChunkCount > 0)
            {
                var span = CollectionsMarshal.AsSpan(_chunkList);
                ref var lastChunk = ref span[^1];
                if (lastChunk.EntityCount < lastChunk.Capacity)
                {
                    slotInChunk = lastChunk.EntityCount;
                    lastChunk.AddEntity(entity);
                    chunkIndex = _chunkList.Count - 1;
                    EntityCount++;
                    return;
                }
            }

            // 需要新建 chunk
            nint chunkMem = AllocateFromSlab();
            var newChunk = new Chunk(_sharedMetadata, chunkMem);
            newChunk.AddEntity(entity);
            _chunkList.Add(newChunk);
            slotInChunk = newChunk.EntityCount - 1;
            chunkIndex = _chunkList.Count - 1;
            EntityCount++;
        }

        // ======================== Shared values 支持 ========================

        /// <summary>
        /// chunk 被回收（空 chunk 从列表移除）时的回调。EntityManager 用它释放
        /// 该 chunk 槽位引用的 managed shared 值（refcount 递减）。无 shared 时恒为 null。
        /// </summary>
        /// <summary>SharedChunkRetired callback removed — managed shared values are never released during World lifetime (World.Dispose clears all).</summary>

        /// <summary>创建新 chunk（不添加实体），返回其索引。Shared values 区由调用方写入。</summary>
        public int CreateChunk()
        {
            nint chunkMem = AllocateFromSlab();
            var newChunk = new Chunk(_sharedMetadata, chunkMem);
            _chunkList.Add(newChunk);
            return _chunkList.Count - 1;
        }

        /// <summary>将实体添加到指定 chunk（SharedComponent 路径：目标 chunk 已按 shared 值选定）。</summary>
        public void AddEntityToChunk(Entity entity, int chunkIndex)
        {
            if ((uint)chunkIndex >= (uint)_chunkList.Count)
                throw new IndexOutOfRangeException($"Chunk index {chunkIndex} out of range (count={_chunkList.Count}).");
            ref var chunk = ref CollectionsMarshal.AsSpan(_chunkList)[chunkIndex];
            if (chunk.EntityCount >= chunk.Capacity)
                throw new InvalidOperationException("Chunk is full");
            chunk.AddEntity(entity);
            EntityCount++;
        }

        private int ComputeChunkStride()
        {
            // 单一真值来源：ChunkMetadata.Create 计算 Entity + 组件数组 + enableable 位图 +
            // 变更位掩码 + Shared values 区的完整布局，stride 必须覆盖 TotalSize（64 对齐）。
            // 此前手算只含 Entity + 组件数组，漏了变更位掩码与共享值区（约 128~192B）→
            // 下一个 chunk 的 Entity 数组起点压在本 chunk 的位掩码/共享值区上：
            //   写入新 chunk 的实体 Id 污染上一 chunk 的变更位掩码（WithChanged 假阳性），
            //   共享值写入损坏下一 chunk 的 Entity 数组（实体引用错乱）。
            var meta = ChunkMetadata.Create(this, _chunkCapacity, types);
            return (meta.TotalSize + 63) & ~63;
        }

        private nint AllocateFromSlab()
        {
            // 复用已移除 Chunk 的空洞，避免 slab 无限增长
            if (_freeChunks.Count > 0)
            {
                int last = _freeChunks.Count - 1;
                nint reused = _freeChunks[last];
                _freeChunks.RemoveAt(last);
                return reused;
            }

            if (_chunkStride == 0) _chunkStride = ComputeChunkStride();

            // Ensure slab allocation
            if (_currentSlab == null || _currentSlabOffset + _chunkStride > SLAB_SIZE)
            {
                // ChunkMemoryPool.Allocate 返回 kBlockSize + kOverAlloc（128KB），
                // 下面的对齐计算保证返回的 _currentSlab 满足 SLAB_ALIGNMENT（64KB）。
                nint raw = ChunkMemoryPool.Allocate();
                long addr = raw.ToInt64();
                long aligned = (addr + SLAB_ALIGNMENT - 1) & ~(SLAB_ALIGNMENT - 1);
                _currentSlab = new SlabInfo { RawPtr = raw, AlignedPtr = new nint(aligned) };
                _slabs.Add(_currentSlab);
                _currentSlabOffset = 0;
            }

            nint chunkMem = _currentSlab.AlignedPtr + _currentSlabOffset;
            _currentSlabOffset += _chunkStride;
            _currentSlab.ChunkCount++;
            return chunkMem;
        }

        /// <summary>
        /// 释放 Chunk 内存（复用或压缩）。Chunk 从列表移除时调用。
        /// 空洞进入复用列表；当某个 slab 的所有 Chunk 都释放时，整个 slab 归还（压缩回收）。
        /// </summary>
        internal void ReleaseChunkMemory(nint chunkMem)
        {
            long addr = chunkMem.ToInt64();
            SlabInfo slab = null;
            for (int i = 0; i < _slabs.Count; i++)
            {
                long start = _slabs[i].AlignedPtr.ToInt64();
                if (addr >= start && addr < start + SLAB_SIZE)
                {
                    slab = _slabs[i];
                    break;
                }
            }
            if (slab == null)
                return;

            slab.ReleasedCount++;
            if (slab.ReleasedCount == slab.ChunkCount)
            {
                // 该 slab 所有 Chunk 都已释放 → 归还整个 slab（压缩）
                ChunkMemoryPool.Free(slab.RawPtr);
                _slabs.Remove(slab);
                // 从空洞列表移除该 slab 的 Chunk（已随 slab 归还，不能再复用）
                long start = slab.AlignedPtr.ToInt64();
                _freeChunks.RemoveAll(c =>
                {
                    long a = c.ToInt64();
                    return a >= start && a < start + SLAB_SIZE;
                });
            }
            else
            {
                _freeChunks.Add(chunkMem);
            }
        }

        public void Remove(int chunkIndex, int slotInChunk, out int movedEntityId, out int movedEntitySlot, out int compactedChunkIndex)
        {
            compactedChunkIndex = -1;
            var span = CollectionsMarshal.AsSpan(_chunkList);
            ref var chunk = ref span[chunkIndex];

            if (chunk.EntityCount == 1)
            {
                movedEntityId = -1;
                movedEntitySlot = -1;
                chunk.RemoveEntity(slotInChunk);
                if (chunk.EntityCount == 0 && _chunkList.Count > 1)
                {
                    // 至少保留 1 个 chunk，避免边界场景频繁创建/销毁抖动
                    int lastChunkIndex = _chunkList.Count - 1;
                    // 空 chunk 的内存块（swap 前记录：swap 后 chunkIndex 位置被 last 覆盖）
                    nint freedChunk = chunk.MemoryBlock;
                    if (chunkIndex != lastChunkIndex)
                    {
                        _chunkList[chunkIndex] = _chunkList[lastChunkIndex];
                        compactedChunkIndex = chunkIndex;
                    }
                    _chunkList.RemoveAt(lastChunkIndex);
                    ReleaseChunkMemory(freedChunk);  // 复用空洞 / 归还空 slab
                }
            }
            else
            {
                int lastEntitySlot = chunk.EntityCount - 1;
                if (slotInChunk == lastEntitySlot)
                {
                    movedEntityId = -1;
                    movedEntitySlot = -1;
                }
                else
                {
                    movedEntityId = chunk.GetEntity(lastEntitySlot).Id;
                    movedEntitySlot = slotInChunk;
                }
                chunk.RemoveEntity(slotInChunk);
            }

            EntityCount--;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(int chunkIndex, int slotInChunk) where T : struct
        {
            var componentIndex = componentTypeRecorder[typeof(T)];
            return ref _chunkList[chunkIndex].GetComponent<T>(slotInChunk, componentIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(int chunkIndex, int slotInChunk, T value) where T : struct
        {
            var componentIndex = componentTypeRecorder[typeof(T)];
            _chunkList[chunkIndex].GetComponent<T>(slotInChunk, componentIndex) = value;
            NotifyComponentChanged(chunkIndex, slotInChunk);
        }

        /// <summary>
        /// 设置组件值（非泛型版本，避免反射）
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void SetRaw(int chunkIndex, int slotInChunk, Type componentType, object value)
        {
            var componentIndex = componentTypeRecorder[componentType];
            var chunk = _chunkList[chunkIndex];
            var compType = ComponentTypeManager.GetComponentType(componentType);
            var compSize = compType.Size;
            var compPtr = (byte*)chunk.GetComponentArrayPointer(componentIndex) + slotInChunk * compSize;

            // 覆盖写：先销毁旧值（持有原生资源的组件释放旧资源），再转移新值进来。
            // 非 IDisposable 组件此调用为 no-op。
            ComponentTypeManager.DestroyComponentValue(compType, compPtr);

            var handle = System.Runtime.InteropServices.GCHandle.Alloc(value, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var srcPtr = handle.AddrOfPinnedObject();
                ComponentTypeManager.MoveComponentValue(compType, (void*)srcPtr, compPtr);
            }
            finally
            {
                handle.Free();
            }
            NotifyComponentChanged(chunkIndex, slotInChunk);
        }

        /// <summary>组件写入后维护变更追踪（递增版本号并标记实体）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void NotifyComponentChanged(int chunkIndex, int slotInChunk)
        {
            _chunkList[chunkIndex].IncrementVersion();
            _chunkList[chunkIndex].MarkEntityChanged(slotInChunk);
            Interlocked.Increment(ref _globalVersion);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void CopyComponentsTo(int sourceChunkIndex, int sourceSlot, Archetype target, int targetChunkIndex, int targetSlot)
        {
            var sourceChunk = _chunkList[sourceChunkIndex];
            var targetChunk = target._chunkList[targetChunkIndex];

            foreach (var type in types)
            {
                if (target.componentTypeRecorder.TryGetValue(type, out int targetComponentIndex))
                {
                    int sourceComponentIndex = componentTypeRecorder[type];
                    var sourcePtr = (byte*)sourceChunk.GetComponentArrayPointer(sourceComponentIndex) + sourceSlot * type.Size;
                    var targetPtr = (byte*)targetChunk.GetComponentArrayPointer(targetComponentIndex) + targetSlot * type.Size;
                    // move 转移所有权：IDisposable 组件拷贝后清空源（避免源随后被 RemoveEntity 销毁时
                    // 释放掉目标槽位仍在引用的同一块原生内存）；普通组件纯位拷贝。
                    ComponentTypeManager.MoveComponentValue(type, sourcePtr, targetPtr);
                }
            }
        }

        public List<Chunk> GetChunks()
        {
            // 返回副本，防止外部调用者绕过 Archetype 直接修改 _chunkList
            return new List<Chunk>(_chunkList);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetComponentTypeIndex<T>()
        {
            return componentTypeRecorder[typeof(T)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetComponentTypeIndex(ComponentType componentType)
        {
            return componentTypeRecorder[componentType];
        }

        // ======================== Phase 2.1: Archetype Edges ========================

        /// <summary>
        /// 获取 Add edge：从当前 Archetype 添加 componentType 后的目标 Archetype。
        /// 返回 null 表示未缓存（miss）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Archetype? GetAddEdge(ComponentType componentType)
        {
            _addEdges.TryGetValue(componentType, out var target);
            return target;
        }

        /// <summary>
        /// 写入 Add edge 缓存。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetAddEdge(ComponentType componentType, Archetype target)
        {
            _addEdges[componentType] = target;
        }

        /// <summary>
        /// 获取 Remove edge：从当前 Archetype 移除 componentType 后的目标 Archetype。
        /// 返回 null 表示未缓存（miss）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Archetype? GetRemoveEdge(ComponentType componentType)
        {
            _removeEdges.TryGetValue(componentType, out var target);
            return target;
        }

        /// <summary>
        /// 写入 Remove edge 缓存。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetRemoveEdge(ComponentType componentType, Archetype target)
        {
            _removeEdges[componentType] = target;
        }

        /// <summary>
        /// 检查当前 Archetype 是否包含指定组件。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasComponent(ComponentType componentType)
        {
            return componentTypeRecorder.ContainsKey(componentType);
        }

        // ======================== 变更追踪 ========================

        /// <summary>递增全局版本号（结构变更或组件修改时调用）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncrementGlobalVersion()
        {
            Interlocked.Increment(ref _globalVersion);
        }

        /// <summary>递增所有 Chunk 的组件修改版本号（结构变更时调用）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncrementChunkVersions()
        {
            foreach (var chunk in _chunkList)
            {
                chunk.IncrementVersion();
            }
        }

        /// <summary>清除所有 Chunk 的实体变更标记（帧末调用）。</summary>
        public void ClearAllChangedBitMasks()
        {
            foreach (var chunk in _chunkList)
            {
                chunk.ClearChangedBitMask();
            }
        }

        // ======================== 组合位图缓存 ========================

        /// <summary>
        /// 单组 AllEnabled 组合的位图缓存（pinned 托管数组，指针稳定）。
        /// 结构与 _chunkList 索引一一对应；chunk 增删时整体重建。
        /// </summary>
        private sealed unsafe class CombinedMaskCache : IDisposable
        {
            public int ChunkCount;
            public int[] ChunkEnableVersions = Array.Empty<int>();
            public nint[] Ptrs = Array.Empty<nint>();      // ulong* 以 nint 存储
            public GCHandle[] Handles = Array.Empty<GCHandle>();

            public unsafe ulong* GetPtr(int chunkIndex)
                => Ptrs[chunkIndex] == 0 ? null : (ulong*)Ptrs[chunkIndex];

            public void Dispose()
            {
                foreach (var h in Handles)
                {
                    if (h.IsAllocated) h.Free();
                }
                Handles = Array.Empty<GCHandle>();
                Ptrs = Array.Empty<nint>();
            }
        }

        /// <summary>计算 AllEnabled 组合的哈希键。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComputeAllEnabledHash(ComponentType[] allEnabledTypes)
        {
            if (allEnabledTypes == null || allEnabledTypes.Length == 0)
                return 0;

            int hash = 17;
            foreach (var type in allEnabledTypes)
            {
                hash = hash * 31 + type.Id;
            }
            return hash;
        }

        /// <summary>
        /// 获取指定 Chunk 的组合位图（惰性计算 + 按 enableVersion 缓存）。
        /// 返回 null 表示无交集（没有实体同时启用所有 AllEnabled 组件）。
        /// 仅主线程使用（IJobChunk.Run 同步执行路径），并行调度路径由 ExecuteManagedChunk 独立计算。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe ulong* GetOrComputeCombinedMask(ComponentType[] allEnabledTypes, int chunkIndex, Chunk chunk)
        {
            if (allEnabledTypes == null || allEnabledTypes.Length == 0)
                return null;
            if (chunkIndex < 0 || chunkIndex >= _chunkList.Count)
                return null;

            int key = ComputeAllEnabledHash(allEnabledTypes);

            // 缓存缺失或 chunk 数量变化（结构变更）→ 重建整套缓存
            if (!_maskCache.TryGetValue(key, out var cache) || cache.ChunkCount != _chunkList.Count)
            {
                cache = BuildMaskCache(allEnabledTypes, key);
                _maskCache[key] = cache;
            }

            // chunk 实体/启用状态变化 → 只重算当前 chunk
            if (cache.ChunkEnableVersions[chunkIndex] != chunk.EnableVersion)
                RecomputeChunkMask(cache, allEnabledTypes, chunkIndex, chunk);

            return cache.GetPtr(chunkIndex);
        }

        private unsafe CombinedMaskCache BuildMaskCache(ComponentType[] allEnabledTypes, int key)
        {
            // 释放旧缓存（存在时）
            if (_maskCache.TryGetValue(key, out var old))
                old.Dispose();

            int count = _chunkList.Count;
            var cache = new CombinedMaskCache
            {
                ChunkCount = count,
                ChunkEnableVersions = new int[count],
                Ptrs = new nint[count],
                Handles = new GCHandle[count],
            };

            for (int chunkIdx = 0; chunkIdx < count; chunkIdx++)
            {
                var chunk = _chunkList[chunkIdx];
                if (chunk.EntityCount == 0) continue;
                var full = ComputeForChunk(allEnabledTypes, chunk);
                cache.Ptrs[chunkIdx] = (nint)Pin(full, out cache.Handles[chunkIdx]);
                cache.ChunkEnableVersions[chunkIdx] = chunk.EnableVersion;
            }
            return cache;
        }

        private unsafe void RecomputeChunkMask(CombinedMaskCache cache, ComponentType[] allEnabledTypes, int chunkIndex, Chunk chunk)
        {
            if (cache.Handles[chunkIndex].IsAllocated)
                cache.Handles[chunkIndex].Free();

            if (chunk.EntityCount == 0)
            {
                cache.Ptrs[chunkIndex] = 0;
                cache.ChunkEnableVersions[chunkIndex] = chunk.EnableVersion;
                return;
            }

            var full = ComputeForChunk(allEnabledTypes, chunk);
            cache.Ptrs[chunkIndex] = (nint)Pin(full, out cache.Handles[chunkIndex]);
            cache.ChunkEnableVersions[chunkIndex] = chunk.EnableVersion;
        }

        /// <summary>计算单 chunk 的组合位图；无交集返回 null。</summary>
        private unsafe ulong[]? ComputeForChunk(ComponentType[] allEnabledTypes, Chunk chunk)
        {
            int entityCount = chunk.EntityCount;
            int ulongCount = (entityCount + 63) / 64;
            var combinedMask = new ulong[ulongCount];

            bool first = true;
            foreach (var type in allEnabledTypes)
            {
                if (!componentTypeRecorder.TryGetValue(type, out int compIdx))
                    continue;

                ulong* bitmap = chunk.GetEnableBitMapPointer(compIdx);
                if (bitmap == null) continue;

                if (first)
                {
                    for (int i = 0; i < ulongCount; i++)
                        combinedMask[i] = bitmap[i];
                    first = false;
                }
                else
                {
                    bool hasAny = false;
                    for (int i = 0; i < ulongCount; i++)
                    {
                        combinedMask[i] &= bitmap[i];
                        if (combinedMask[i] != 0) hasAny = true;
                    }
                    if (!hasAny) return null;  // 交集为空，提前退出
                }
            }

            if (first) return null;  // 没有任何 AllEnabled 组件在该 archetype 中

            // 最终校验（单组件场景：第一个组件可能全 0）
            bool any = false;
            for (int i = 0; i < ulongCount; i++)
            {
                if (combinedMask[i] != 0) { any = true; break; }
            }
            return any ? combinedMask : null;
        }

        private static unsafe ulong* Pin(ulong[]? mask, out GCHandle handle)
        {
            if (mask == null)
            {
                handle = default;
                return null;
            }
            handle = GCHandle.Alloc(mask, GCHandleType.Pinned);
            return (ulong*)handle.AddrOfPinnedObject();
        }

        /// <summary>释放所有位图缓存（Archetype 销毁时调用）。</summary>
        public void InvalidateMaskCache()
        {
            foreach (var cache in _maskCache.Values)
                cache.Dispose();
            _maskCache.Clear();
        }

        public void Dispose()
        {
            InvalidateMaskCache();
            // 释放所有存活实体的生命周期组件（持有原生内存的组件在 slab 释放前销毁，避免泄漏）
            DestroyAllEntityComponents();
            foreach (var slab in _slabs)
                ChunkMemoryPool.Free(slab.RawPtr);
            _slabs.Clear();
            _freeChunks.Clear();
            _chunkList.Clear();
        }

        /// <summary>销毁所有存活实体的生命周期组件（Archetype.Dispose 前调用，无 hook 组件 no-op）。</summary>
        private unsafe void DestroyAllEntityComponents()
        {
            var types = Types;
            foreach (var chunk in _chunkList)
            {
                int count = chunk.EntityCount;
                for (int slot = 0; slot < count; slot++)
                {
                    for (int i = 0; i < ComponentCount; i++)
                    {
                        byte* ptr = (byte*)chunk.GetComponentArrayPointer(i) + slot * types[i].Size;
                        ComponentTypeManager.DestroyComponentValue(types[i], ptr);
                    }
                }
            }
        }

        ~Archetype()
        {
            Dispose();
        }
    }

    // debug 部分
    public partial class Archetype
    {
        private IntPtr _cachedAddress;

        public IntPtr GetAddress()
        {
            if (_cachedAddress == IntPtr.Zero)
            {
                _cachedAddress = MemoryAddress.GetAddress(this);
            }
            return _cachedAddress;
        }

        public unsafe string GetMemoryLayoutInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Archetype 内存布局 ===");
            sb.AppendLine($"Archetype Address: {GetAddress().ToInt64():D}");
            sb.AppendLine($"实体数: {EntityCount}, 组件数: {ComponentCount}");

            int chunkCounter = 0;
            foreach (var chunk in _chunkList)
            {
                chunkCounter++;
                sb.AppendLine($"Chunk: {chunkCounter}/{ChunkCount}");
                sb.AppendLine($"实体数: {chunk.EntityCount}, 组件数: {ComponentCount}");
                var entityArray = (Entity*)chunk.MemoryBlock;
                sb.AppendLine($"  Entity Array: {(long)entityArray:D} 每个size:{Marshal.SizeOf<Entity>()} (Type: {typeof(Entity).Name})");
                for (int i = 0; i < ComponentCount; i++)
                {
                    var componentType = types[i].Type;
                    string typeName = componentType.Name;
                    IntPtr componentArrayPtr = chunk.MemoryBlock + chunk.GetComponentOffset(i);
                    sb.AppendLine($"  Component {i} 地址: {componentArrayPtr.ToInt64():D} 每个size:{types[i].Size} (Type: {typeName})");
                }
            }
            return sb.ToString();
        }
    }
}
