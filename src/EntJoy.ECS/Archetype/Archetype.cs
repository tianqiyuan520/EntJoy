using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    public unsafe sealed partial class Archetype : IDisposable
    {
        private readonly List<Chunk> _chunkList = new();
        // 此索引之前的块已满；移除实体时回退，新增时向前扫描。
        private int _firstChunkWithRoom;
        private readonly int _chunkCapacity;
        private readonly ChunkMetadata _sharedMetadata;
        private readonly ChunkCacheLayout _cacheLayout;

        /// <summary>本 Archetype 在 EntityManager 中的稳定编号（由 EntityManager 分配；blittable 定位表/原生内核用）。</summary>
        public int ArchetypeId;

        /// <summary>
        /// 组件列字节偏移的非托管镜像（按 componentIndex 索引）：原生内核拿到 ChunkMemory 后
        /// 直接算出列基址，不经托管 int[]。生命周期 = Archetype，内容不可变。
        /// </summary>
        private int* _chunkOffsetsNative;

        /// <summary>非托管列偏移表首址（只读；与 <see cref="ChunkMetadata.ComponentOffsets"/> 逐项一致）。</summary>
        public int* GetChunkOffsetsNative() => _chunkOffsetsNative;

        // slab 为 64 KB 连续内存，多个 Chunk 从中切出，使各组件列物理相邻。
        private const int SLAB_SIZE = 64 * 1024;
        private const int SLAB_ALIGNMENT = 64 * 1024;
        private List<SlabInfo> _slabs = new();
        private SlabInfo _currentSlab;
        private int _currentSlabOffset;
        private int _chunkStride;
        // 已移除 Chunk 的空洞：新 Chunk 优先从这里取，避免 slab 无限增长
        private readonly List<nint> _freeChunks = new();

        /// <summary>
        /// 显式指定所有 Archetype 的 Chunk 容量（0 = 自动值），用于需要单块连续列的 SoA 视图。
        /// 必须在创建 Archetype 之前设置；容量过大时 stride 超过 SLAB_SIZE，每块独占一个 slab。
        /// </summary>
        public static int ChunkCapacityOverride;

        /// <summary>单个 slab 的追踪信息（复用/压缩用）。</summary>
        private sealed class SlabInfo
        {
            public nint RawPtr;       // ChunkMemoryPool.Free 用（未对齐）
            public nint AlignedPtr;   // Chunk 地址基准（对齐后）
            public int Bytes;         // 本 slab 的可用字节数（≥ SLAB_SIZE；超大 stride 时按 stride 给）
            public int ChunkCount;    // 该 slab 已分配的 Chunk 数
            public int ReleasedCount; // 已释放（移除）的 Chunk 数
        }

        /// <summary>找出包含该地址的 slab；不属于任何现存 slab 时返回 null。</summary>
        private SlabInfo FindSlab(long addr)
        {
            for (int i = 0; i < _slabs.Count; i++)
            {
                long start = _slabs[i].AlignedPtr.ToInt64();
                if (addr >= start && addr < start + _slabs[i].Bytes) return _slabs[i];
            }
            return null;
        }

        /// <summary>
        /// 自检 slab 账本（诊断/测试用，返回违例数，0 = 健康）：每个存活块的 <c>MemoryBlock</c> 落在某个现存 slab 内，
        /// 每个 slab 的 <c>ReleasedCount</c> 等于 <c>ChunkCount</c> 减存活数且不超过它。
        /// 任一条不成立，归还整块 slab 时可能连带别人仍在用的内存。
        /// </summary>
        internal int VerifySlabInvariants()
        {
            int violations = 0;

            for (int c = 0; c < _chunkList.Count; c++)
            {
                if (FindSlab(_chunkList[c].MemoryBlock.ToInt64()) == null) violations++;
            }

            for (int i = 0; i < _slabs.Count; i++)
            {
                SlabInfo slab = _slabs[i];
                long start = slab.AlignedPtr.ToInt64();
                long end = start + slab.Bytes;
                int live = 0;
                for (int c = 0; c < _chunkList.Count; c++)
                {
                    long addr = _chunkList[c].MemoryBlock.ToInt64();
                    if (addr >= start && addr < end) live++;
                }
                if (slab.ReleasedCount != slab.ChunkCount - live) violations++;
                if (slab.ReleasedCount > slab.ChunkCount) violations++;
            }

            return violations;
        }

        public int ChunkCount => _chunkList.Count;



        public ref readonly List<Chunk> ChunkList => ref _chunkList;

        /// <summary>本 Archetype 已分配的 slab 数量（内存分析器用）。</summary>
        public int SlabCount => _slabs.Count;

        /// <summary>本 Archetype 已分配的 slab 总字节数（内存分析器用）。</summary>
        public long SlabBytes
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _slabs.Count; i++) total += _slabs[i].Bytes;
                return total;
            }
        }
        /// <summary>
        /// _chunkList 的零拷贝只读视图，供调度与查询热路径遍历（不再每次 new List 拷贝）。
        /// 前提是同线程顺序模型：构建快照期间主线程不做结构变更。
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

        private int CalculateOptimalChunkCapacity(ComponentType[] types)
        {
            // Unity 风格的小物理 Chunk：Chunk 只负责存储和结构变化，
            // Job 的执行粒度由调度器的 BatchRange 独立决定。
            const int entityBlockSize = 64;
            const int targetChunkBytes = 16 * 1024;

            int entitySize = Unsafe.SizeOf<Entity>();
            int totalComponentSize = entitySize;
            int enableableCount = 0;
            foreach (var type in types)
            {
                totalComponentSize += type.Size;
                if (type.IsEnableable) enableableCount++;
            }

            const int bitmapBytesPer64Entities = 8;
            int bitmapOverheadPerEntity = (enableableCount * bitmapBytesPer64Entities + 63) / 64;

            int stride = totalComponentSize + bitmapOverheadPerEntity;
            int capacity = Math.Max(entityBlockSize,
                targetChunkBytes / Math.Max(1, stride));

            // 先按纯字节和取容量，再用完整布局复核并下调，不夹到固定实体数。
            // 按 64 个实体向下对齐并核算完整布局，较大的组件仍可降低容量。
            capacity &= ~(entityBlockSize - 1);
            capacity = Math.Max(capacity, entityBlockSize);
            // 复用完整布局校验，把列间隔、位图和 shared 区计入 16 KiB 目标；最少保留 64 个实体。
            while (capacity > entityBlockSize
                && ChunkMetadata.Create(this, capacity, types, true, _cacheLayout).TotalSize > targetChunkBytes)
                capacity -= entityBlockSize;
            return capacity;
        }


        internal void PrepareEntityRemoval(int count = 1)
            => _freeChunks.EnsureCapacity(checked(_freeChunks.Count + count));

        public RemovalResult Remove(int chunkIndex, int slotInChunk)
            => Remove(chunkIndex, slotInChunk, continueOnDisposeError: false);

        internal RemovalResult Remove(int chunkIndex, int slotInChunk, bool continueOnDisposeError, bool skipDisposal = false)
        {
            int movedEntityId = -1, movedEntitySlot = -1;
            var span = CollectionsMarshal.AsSpan(_chunkList);
            ref var chunk = ref span[chunkIndex];

            // 空 Chunk 留在列表里：搬移 Chunk 会让它里面每个实体都要重写位置索引。
            // 内存与空洞由 EntityManager.CompactChunks 显式回收。
            int lastEntitySlot = chunk.EntityCount - 1;
            chunk.RemoveEntity(slotInChunk, continueOnDisposeError, skipDisposal);
            _firstChunkWithRoom = Math.Min(_firstChunkWithRoom, chunkIndex);
            if (chunk.EntityCount > 0 && slotInChunk != lastEntitySlot)
            {
                movedEntityId = chunk.GetEntity(lastEntitySlot).Id;
                movedEntitySlot = slotInChunk;
            }

            EntityCount--;
            return new RemovalResult(movedEntityId, movedEntitySlot);
        }


        public List<Chunk> GetChunks()
        {
            // 返回副本，防止外部调用者绕过 Archetype 直接修改 _chunkList
            return new List<Chunk>(_chunkList);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetComponentTypeIndex<T>()
        {
            return GetComponentTypeIndex(ComponentTypeCache<T>.Type);
        }

        private static class ComponentTypeCache<T>
        {
            internal static readonly ComponentType Type = ComponentTypeManager.GetComponentType(typeof(T));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetComponentTypeIndex(ComponentType componentType)
        {
            return TryGetComponentTypeIndex(componentType, out int column) ? column : throw new KeyNotFoundException($"Component type {componentType.Id} is not in this archetype.");
        }

        /// <summary>按 id 查列索引；不在本原型时返回 false，不抛异常。</summary>
        public bool TryGetComponentTypeIndex(ComponentType componentType, out int column)
        {
            int id = componentType.Id;
            if ((uint)id < (uint)_componentColumns.Length)
            {
                column = _componentColumns[id] - 1;
                if (column >= 0) return true;
            }
            return componentTypeRecorder.TryGetValue(componentType, out column);
        }

        // 增删组件的目标原型缓存：按组件 id 索引，null 表示未缓存。

        /// <summary>按组件 id 写入边表，超出容量时扩容。</summary>
        private static void SetEdgeSlot(ref Archetype[] edges, int id, Archetype target)
        {
            if ((uint)id >= (uint)edges.Length)
                Array.Resize(ref edges, Math.Max(id + 1, edges.Length * 2));
            edges[id] = target;
        }
        /// <summary>
        /// 获取 Add edge：从当前 Archetype 添加 componentType 后的目标 Archetype。
        /// 返回 null 表示未缓存（miss）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Archetype? GetAddEdge(ComponentType componentType)
        {
            var edges = _addEdgeByTypeId;
            return (uint)componentType.Id < (uint)edges.Length ? edges[componentType.Id] : null;
        }

        /// <summary>
        /// 写入 Add edge 缓存。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetAddEdge(ComponentType componentType, Archetype target)
        {
            SetEdgeSlot(ref _addEdgeByTypeId, componentType.Id, target);
        }

        /// <summary>
        /// 获取 Remove edge：从当前 Archetype 移除 componentType 后的目标 Archetype。
        /// 返回 null 表示未缓存（miss）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Archetype? GetRemoveEdge(ComponentType componentType)
        {
            var edges = _removeEdgeByTypeId;
            return (uint)componentType.Id < (uint)edges.Length ? edges[componentType.Id] : null;
        }

        /// <summary>
        /// 写入 Remove edge 缓存。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetRemoveEdge(ComponentType componentType, Archetype target)
        {
            SetEdgeSlot(ref _removeEdgeByTypeId, componentType.Id, target);
        }

    }
}
