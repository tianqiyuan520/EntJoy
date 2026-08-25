using System;
using System.Diagnostics;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;

namespace EntJoySample.ECS
{
    /// <summary>非 NativeTranspile 的 IJobEntity：adapter 生成。工作负载 = 读 X 累加（与 Query/Job 公平对比）。</summary>
    public unsafe struct SumEntityJob : IJobEntity
    {
        public long* SumPtr;

        public void Execute(ref Position position)
        {
            *SumPtr += (int)position.X;
        }
    }

    /// <summary>
    /// IJobEntity.Run enabled 开关对比（同工作量：读 Position.X 累加）：
    /// 关 = 遍历全部 100K；开 = adapter 内联 BitOperations 位图跳转（只处理 33K）。
    /// </summary>
    public static unsafe class IJobEntityEnabledBenchmark
    {
        public static void Run()
        {
            Console.WriteLine("=== IJobEntity.Run Enabled Comparison ===\n");

            const int entityCount = 100000;
            const int warmupIterations = 10;
            const int testIterations = 100;

            var world = new World();
            World.DefaultWorld = world;
            var em = world.EntityManager;

            for (int i = 0; i < entityCount; i++)
            {
                var entity = em.NewEntity(new ComponentType[] { typeof(Position), typeof(Velocity), typeof(ActiveComponent) });
                em.Set(entity, new Position { X = i, Y = 0 });
                em.Set(entity, new Velocity { X = 1, Y = 1 });
                em.SetComponentEnabled<ActiveComponent>(entity, (i % 3 == 0));
            }

            var queryNoFilter = new QueryBuilder().WithAll<Position>();
            var queryEnabled = new QueryBuilder().WithAll<Position>().WithEnabled<ActiveComponent>();

            // 预期累加值
            long expectedAll = 0, expectedEnabled = 0;
            for (int i = 0; i < entityCount; i++) expectedAll += i;
            for (int i = 0; i < entityCount; i += 3) expectedEnabled += i;

            // 预热
            for (int i = 0; i < warmupIterations; i++)
            {
                long w = 0;
                new SumEntityJob { SumPtr = &w }.Run(queryNoFilter);
                w = 0;
                new SumEntityJob { SumPtr = &w }.Run(queryEnabled);
            }

            // 正确性
            long gain = 0;
            new SumEntityJob { SumPtr = &gain }.Run(queryEnabled);
            bool okFilter = gain == expectedEnabled;
            Console.WriteLine($"Enabled filter  : sum={gain} (expect {expectedEnabled}) => {(okFilter ? "PASS" : "FAIL")}\n");

            // OFF（100K 全遍历）
            Console.WriteLine("--- IJobEntity.Run (enabled OFF, all 100K) ---");
            long sumOff = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < testIterations; i++)
            {
                sumOff = 0;
                new SumEntityJob { SumPtr = &sumOff }.Run(queryNoFilter);
            }
            sw.Stop();
            double offMs = sw.Elapsed.TotalMilliseconds / testIterations;
            Console.WriteLine($"  Time: {offMs:F4} ms  sum={sumOff} {(sumOff == expectedAll ? "OK" : "BAD")}\n");

            // ON（33K BitOps 过滤）
            Console.WriteLine("--- IJobEntity.Run (enabled ON, 33K) ---");
            long sumOn = 0;
            sw.Restart();
            for (int i = 0; i < testIterations; i++)
            {
                sumOn = 0;
                new SumEntityJob { SumPtr = &sumOn }.Run(queryEnabled);
            }
            sw.Stop();
            double onMs = sw.Elapsed.TotalMilliseconds / testIterations;
            Console.WriteLine($"  Time: {onMs:F4} ms  sum={sumOn} {(sumOn == expectedEnabled ? "OK" : "BAD")}\n");

            // Query reference（同工作量）
            Console.WriteLine("--- Query<Position>.WithEnabled<ActiveComponent> (reference) ---");
            long qSum = 0;
            sw.Restart();
            for (int i = 0; i < testIterations; i++)
            {
                qSum = 0;
                foreach (var r in world.Query<Position>().WithEnabled<ActiveComponent>()) qSum += (int)r.Comp0.X;
            }
            sw.Stop();
            double queryMs = sw.Elapsed.TotalMilliseconds / testIterations;
            Console.WriteLine($"  Time: {queryMs:F4} ms  sum={qSum} {(qSum == expectedEnabled ? "OK" : "BAD")}\n");

            Console.WriteLine("=== Summary ===\n");
            Console.WriteLine($"{"Method",40} {"ms/iter",10} {"WorkXCount",12}");
            Console.WriteLine(new string('-', 64));
            Console.WriteLine($"{"IJobEntity.Run (enabled OFF)",40} {offMs,10:F4} {entityCount,12}");
            Console.WriteLine($"{"IJobEntity.Run (enabled ON)",40} {onMs,10:F4} {entityCount / 3 + 1,12}");
            Console.WriteLine($"{"Query.WithEnabled (reference)",40} {queryMs,10:F4} {entityCount / 3 + 1,12}");
            Console.WriteLine();
        }
    }
}