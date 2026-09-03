using System.Collections.Generic;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    /// <summary>单个 System 的耗时统计。</summary>
    public struct SystemTiming
    {
        public string SystemName;
        public double TotalMs;   // 累计耗时
        public double AvgMs;     // 平均每帧
        public double MaxMs;     // 单帧最大
        public long FrameCount;  // 执行帧数
    }

    /// <summary>性能分析报告（纯观测快照）：System 耗时 + Job 调度 + slab 复用 + 内存布局。</summary>
    public struct PerformanceReport
    {
        public List<SystemTiming> SystemTimings;
        public NativeJobSystemStats JobStats;
        public int ChunkPoolAllocs;
        public int ChunkPoolFrees;
        public int ChunkPoolHits;
        public int ChunkPoolMisses;
        public MemoryReport Memory;
    }
}
