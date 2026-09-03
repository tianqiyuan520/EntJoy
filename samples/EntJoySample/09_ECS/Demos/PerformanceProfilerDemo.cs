using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// 性能分析器示例：跑两个 System（一快一慢）若干帧，打印各 System 耗时 + slab 复用统计。
    /// </summary>
    public static class PerformanceProfilerDemo
    {
        public struct FastSystem : ISystem
        {
            public static int Executions;
            public void OnUpdate() => Executions++;
        }

        public struct SlowSystem : ISystem
        {
            public static long TotalSum;   // 静态，防止累加被优化
            public void OnUpdate()
            {
                long sum = 0;
                foreach (var r in World.DefaultWorld.Query<Position, Velocity>())
                    sum += (long)r.Comp0.X;
                TotalSum = sum;
            }
        }

        public static void Run()
        {
            Console.WriteLine("=== 性能分析器 Demo ===\n");
            var world = new World("PerfDemo");
            world.CreateEntities(5000, typeof(Position), typeof(Velocity));

            var runner = new SystemRunner(world);
            runner.RegisterSystem<FastSystem>();
            runner.RegisterSystem<SlowSystem>();

            // 跑 60 帧
            for (int f = 0; f < 60; f++)
                runner.Update();

            var report = runner.GetPerformanceReport();

            Console.WriteLine("--- System 耗时 ---");
            foreach (var t in report.SystemTimings)
                Console.WriteLine($"  {t.SystemName}: 总={t.TotalMs:F3}ms, 均={t.AvgMs:F4}ms, 最大={t.MaxMs:F4}ms, 帧={t.FrameCount}");

            Console.WriteLine("--- slab 复用（ChunkPool）---");
            Console.WriteLine($"  alloc={report.ChunkPoolAllocs}, free={report.ChunkPoolFrees}, hits={report.ChunkPoolHits}, misses={report.ChunkPoolMisses}");

            Console.WriteLine("--- 内存 ---");
            Console.WriteLine($"  chunk={report.Memory.TotalChunkCount}, 实体={report.Memory.TotalEntityCount}, slab={report.Memory.TotalSlabBytes / 1024}KB");

            world.Dispose();
            Console.WriteLine("\n=== 性能分析器 Demo Complete ===\n");
        }
    }
}
