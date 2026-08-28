using System;
using System.Diagnostics;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;

namespace EntJoySample.ECS
{
    /// <summary>
    /// 统一工作负载 = 读取每个（满足条件的）实体的 Position.X 并累加。
    /// 所有方案做等量的实体级内存访问，公平对比遍历机制本身。
    /// </summary>
    public unsafe struct SumEnabledJob : IJobChunk
    {
        public long* SumPtr;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            // TryGetNextRange：范围遍历（IJobChunk 官方用法）
            var positions = chunk.GetComponentDataSpan<Position>();
            int start = 0;
            while (enabledMask.TryGetNextRange(ref start, out int rangeStart, out int rangeEnd))
            {
                for (int i = rangeStart; i < rangeEnd; i++)
                    *SumPtr += (int)positions[i].X;
            }
        }
    }

    /// <summary>无过滤：遍历 Chunk 全部实体，同样读取 X 累加（与过滤方案同工作量）。</summary>
    public unsafe struct SumAllJob : IJobChunk
    {
        public long* SumPtr;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var positions = chunk.GetComponentDataSpan<Position>();
            for (int i = 0; i < positions.Length; i++)
                *SumPtr += (int)positions[i].X;
        }
    }

    /// <summary>
    /// 公平对比：Query(foreach) vs IJobChunk.Run（TryGetNextRange/全遍历），
    /// 均读取 Position.X 累加；每组分开对比「无过滤(100K)」与「启用过滤(33K)」。
    /// </summary>
    public static unsafe class EnabledComparisonBenchmark
    {
        public static void Run()
        {
            Console.WriteLine("=== Fair Comparison (read Position.X per entity) ===\n");

            const int entityCount = 100000;
            const int warmupIterations = 10;
            const int testIterations = 100;

            Console.WriteLine($"Entity Count: {entityCount}, Iterations: {testIterations}\n");

            var world = new World();
            World.DefaultWorld = world;
            var entityManager = world.EntityManager;

            for (int i = 0; i < entityCount; i++)
            {
                var entity = entityManager.NewEntity(
                    new ComponentType[] { typeof(Position), typeof(Velocity), typeof(ActiveComponent) });
                entityManager.Set(entity, new Position { X = i, Y = 0 });
                entityManager.Set(entity, new Velocity { X = 1, Y = 1 });
                entityManager.SetComponentEnabled<ActiveComponent>(entity, (i % 3 == 0));
            }

            var queryNoFilter = new QueryBuilder().WithAll<Position>();
            var queryEnabled = new QueryBuilder().WithAll<Position>().WithEnabled<ActiveComponent>();

            // 预期累加值（验证工作量一致）
            long expectedAll = SumRange(0, entityCount);
            long expectedEnabled = ExpectedEnabledSum(entityCount);
            Console.WriteLine($"expected: all={expectedAll}, enabled={expectedEnabled}\n");

            // 预热
            for (int i = 0; i < warmupIterations; i++)
            {
                foreach (var r in world.Query<Position, Velocity>()) { _ = r.Comp0.X; }
                foreach (var r in world.Query<Position>().WithEnabled<ActiveComponent>()) { _ = r.Comp0.X; }
            }

            // ===== 无过滤（100K） =====
            Console.WriteLine("=== No Filter (all 100K) ===");

            // Query foreach（读 X）
            long qAll = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < testIterations; i++)
            {
                qAll = 0;
                foreach (var r in world.Query<Position, Velocity>()) qAll += (long)r.Comp0.X;
            }
            sw.Stop();
            double qAllMs = sw.Elapsed.TotalMilliseconds / testIterations;
            Console.WriteLine($"Query foreach       : {qAllMs,8:F4} ms  sum={qAll} {(qAll == expectedAll ? "OK" : "BAD")}");

            // IJobChunk.Run 全遍历
            long jAll = 0;
            sw.Restart();
            for (int i = 0; i < testIterations; i++)
            {
                jAll = 0;
                new SumAllJob { SumPtr = &jAll }.Run(queryNoFilter);
            }
            sw.Stop();
            double jAllMs = sw.Elapsed.TotalMilliseconds / testIterations;
            Console.WriteLine($"IJobChunk.Run (all)  : {jAllMs,8:F4} ms  sum={jAll} {(jAll == expectedAll ? "OK" : "BAD")}");
            Console.WriteLine();

            // ===== 启用过滤（33K） =====
            Console.WriteLine("=== Enabled Filter (33K) ===");

            // Query.WithEnabled
            long qEn = 0;
            sw.Restart();
            for (int i = 0; i < testIterations; i++)
            {
                qEn = 0;
                foreach (var r in world.Query<Position>().WithEnabled<ActiveComponent>()) qEn += (int)r.Comp0.X;
            }
            sw.Stop();
            double qEnMs = sw.Elapsed.TotalMilliseconds / testIterations;
            Console.WriteLine($"Query.WithEnabled    : {qEnMs,8:F4} ms  sum={qEn} {(qEn == expectedEnabled ? "OK" : "BAD")}");

            // IJobChunk.Run TryGetNextRange
            long jEn = 0;
            sw.Restart();
            for (int i = 0; i < testIterations; i++)
            {
                jEn = 0;
                new SumEnabledJob { SumPtr = &jEn }.Run(queryEnabled);
            }
            sw.Stop();
            double jEnMs = sw.Elapsed.TotalMilliseconds / testIterations;
            Console.WriteLine($"IJobChunk.Run (range): {jEnMs,8:F4} ms  sum={jEn} {(jEn == expectedEnabled ? "OK" : "BAD")}");
            Console.WriteLine();

            // ===== 汇总（同工作量） =====
            Console.WriteLine("=== Summary ===\n");
            Console.WriteLine($"{"Method",28} {"ms/iter",10} {"vs Query",10} {"WorkXCount",12}");
            Console.WriteLine(new string('-', 62));
            Console.WriteLine($"{"Query (all 100K)",28} {qAllMs,10:F4} {"1.00x",10} {entityCount,12}");
            Console.WriteLine($"{"IJobChunk.Run (all)",28} {jAllMs,10:F4} {qAllMs / jAllMs,10:F2}x {entityCount,12}");
            Console.WriteLine($"{"Query (enabled 33K)",28} {qEnMs,10:F4} {qAllMs / qEnMs,10:F2}x {entityCount / 3 + 1,12}");
            Console.WriteLine($"{"IJobChunk.Run (range)",28} {jEnMs,10:F4} {qAllMs / jEnMs,10:F2}x {entityCount / 3 + 1,12}");
            Console.WriteLine();
        }

        private static long SumRange(int from, int count)
        {
            long s = 0;
            for (int i = from; i < count; i++) s += i;
            return s;
        }

        private static long ExpectedEnabledSum(int count)
        {
            // i % 3 == 0：0,3,6,...,count-1（最后取 <= count-1 的 3 的倍数）
            long s = 0;
            for (int i = 0; i < count; i += 3) s += i;
            return s;
        }
    }
}