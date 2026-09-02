using System.Collections.Generic;

namespace EntJoy.ECS
{
    /// <summary>ECS 内存分析报告（纯观测快照，不改变内存行为）。</summary>
    public struct MemoryReport
    {
        /// <summary>累计原生内存分配次数（PersistentAllocator）。</summary>
        public int NativeAllocs;
        /// <summary>累计原生内存释放次数。</summary>
        public int NativeFrees;
        /// <summary>free-list 命中次数（同尺寸复用）。</summary>
        public int NativeHits;
        /// <summary>free-list 未命中次数（直通 OS 分配）。</summary>
        public int NativeMisses;
        /// <summary>外来块释放次数（>0 提示 double-free 或跨堆释放异常）。</summary>
        public int NativeForeign;
        /// <summary>未释放 NativeContainer 数量（仅 Debug 注册；Release 恒 0）。</summary>
        public int LeakedContainers;
        /// <summary>所有 Archetype 的 Chunk 总数。</summary>
        public int TotalChunkCount;
        /// <summary>瘦 Chunk 数（利用率 &lt; 阈值，碎片信号）。</summary>
        public int ThinChunkCount;
        /// <summary>实体总数。</summary>
        public int TotalEntityCount;
        /// <summary>slab 总字节数。</summary>
        public long TotalSlabBytes;
        /// <summary>每 Archetype 明细。</summary>
        public List<ArchetypeMemoryInfo> Archetypes;

        /// <summary>原生内存泄漏估算（Allocs - Frees）。</summary>
        public int NativeLeakEstimate => NativeAllocs - NativeFrees;
    }

    /// <summary>单个 Archetype 的内存明细。</summary>
    public struct ArchetypeMemoryInfo
    {
        /// <summary>组件签名，如 "Position, Velocity"。</summary>
        public string TypeSignature;
        public int ChunkCount;
        public int EntityCount;
        /// <summary>每个 Chunk 的实体容量。</summary>
        public int Capacity;
        public int SlabCount;
        public long SlabBytes;

        /// <summary>平均 Chunk 利用率（0~1）。无 Chunk 时为 0。</summary>
        public float Utilization => ChunkCount > 0 && Capacity > 0
            ? (float)EntityCount / (ChunkCount * Capacity)
            : 0f;
    }
}
