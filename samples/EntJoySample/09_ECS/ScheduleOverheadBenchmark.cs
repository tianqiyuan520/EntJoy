using System.Diagnostics;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;

namespace EntJoySample.ECS
{
    /// <summary>
    /// schedule-only 微基准：测量「空 IJobChunk 的 Schedule()+Complete()」的端到端调度开销
    /// （含：收集 → context 构建 → P/Invoke → C++ 实体数衡 tile → Chase-Lev 分发 → 回调）。
    /// 作为 ECS JobSystem 重构（docs/20260826-重构ECS-JobSystem方案.md）每个阶段的回归标尺，
    /// 阈值：重构前后差异 &lt; 5%。
    /// </summary>
    public static unsafe class ScheduleOverheadBenchmark
    {
        public struct EmptyChunkJob : IJobChunk
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask) { }
        }

        public static void Run(int iterations = 1000, int warmup = 100)
        {
            var world = World.DefaultWorld
                ?? throw new InvalidOperationException("ScheduleOverheadBenchmark 需要先存在一个 World（先跑其它基准）");
            var query = new QueryBuilder().WithAll<Position>();
            var job = new EmptyChunkJob();

            // 预热（worker 冷启动 / JobCostCache 收敛）
            for (int i = 0; i < warmup; i++)
                job.Schedule(query).Complete();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                job.Schedule(query).Complete();
            sw.Stop();

            double msPer = sw.Elapsed.TotalMilliseconds / iterations;
            double usPer = msPer * 1000.0;
            Console.WriteLine($"Schedule-only  : {msPer,10:F4} ms/iter ({usPer,8:F2} us) over {iterations} iters, {world.EntityManager.ArchetypeCount} archetypes");
        }
    }
}