using EntJoy.ECS;
using System.Diagnostics;

namespace EntJoySample.ECS
{
    /// <summary>
    /// 查询缓存基准：验证「共享注册表 + 增量刷新」的收益。
    /// 对比：
    ///   1. GetOrCreateEntityQuery（共享，O(1) 查表） vs CreateEntityQuery（每次全量扫描）
    ///   2. 结构变更后访问共享查询：增量刷新（Archetype 未变化时复用匹配集合）
    /// </summary>
    public static unsafe class EntityQueryCacheBenchmark
    {
        public static void Run()
        {
            Console.WriteLine("=== EntityQuery Cache Benchmark ===\n");

            const int entityCount = 50000;      // 实体数
            const int archetypeCount = 4;       // 不同组件组合的 Archetype 数
            const int warmupIterations = 20;
            const int testIterations = 200;

            using var world = new World("QueryCacheBenchmark");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 构造多 Archetype 数据（接近真实场景）
            var archetypes = new Type[][]
            {
                [typeof(Position), typeof(Velocity)],
                [typeof(Position), typeof(Velocity), typeof(Health)],
                [typeof(Position), typeof(Health)],
                [typeof(Velocity), typeof(Health)],
            };
            for (int i = 0; i < entityCount; i++)
            {
                em.NewEntity(archetypes[i % archetypeCount]);
            }
            Console.WriteLine($"Entities: {entityCount}, Archetypes: {em.ArchetypeCount}\n");

            var rule = new QueryBuilder().WithAll<Position, Velocity>();

            // ===== 1. 查询获取：共享 vs 重复构造 =====
            Console.WriteLine("--- 1. Query acquisition (same rule, repeated) ---");

            // 预热
            for (int i = 0; i < warmupIterations; i++)
            {
                world.GetOrCreateEntityQuery(rule);
                world.CreateEntityQuery(rule);
            }

            // 共享（查表命中）
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < testIterations; i++)
            {
                var q = world.GetOrCreateEntityQuery(rule);
                _ = q.CalculateEntityCount();
            }
            sw.Stop();
            double sharedMs = sw.Elapsed.TotalMilliseconds / testIterations;
            Console.WriteLine($"GetOrCreateEntityQuery (shared) : {sharedMs,10:F4} ms/iter");

            // 重复构造（每次全量扫描）
            sw.Restart();
            for (int i = 0; i < testIterations; i++)
            {
                var q = world.CreateEntityQuery(rule);
                _ = q.CalculateEntityCount();
            }
            sw.Stop();
            double freshMs = sw.Elapsed.TotalMilliseconds / testIterations;
            Console.WriteLine($"CreateEntityQuery      (fresh)  : {freshMs,10:F4} ms/iter");
            Console.WriteLine($"Speedup: {freshMs / sharedMs:F2}x  ({(freshMs > sharedMs ? "shared wins" : "no gain")})\n");

            // ===== 2. 增量刷新 vs 全量重扫（结构变更后） =====
            Console.WriteLine("--- 2. Refresh after structural change ---");

            var sharedQuery = world.GetOrCreateEntityQuery(rule);

            // 每次迭代：创建 100 个实体（结构变更）→ 访问查询（触发刷新）
            const int changePerIter = 100;
            for (int i = 0; i < warmupIterations; i++)
            {
                for (int j = 0; j < changePerIter; j++)
                    em.NewEntity(archetypes[j % archetypeCount]);
                _ = sharedQuery.CalculateEntityCount();
            }

            // 增量刷新（Archetype 集合未变化 → 复用签名匹配集合，只重收 chunk）
            var before = sharedQuery.CalculateEntityCount();
            sw.Restart();
            for (int i = 0; i < testIterations; i++)
            {
                for (int j = 0; j < changePerIter; j++)
                    em.NewEntity(archetypes[j % archetypeCount]);
                _ = sharedQuery.CalculateEntityCount();  // 触发惰性增量刷新
            }
            sw.Stop();
            double incMs = sw.Elapsed.TotalMilliseconds / testIterations;
            var after = sharedQuery.CalculateEntityCount();
            // 4 个 Archetype 中 2 个匹配 Position+Velocity（[P,V] 和 [P,V,H]）→ 每批 100 个中 50 个匹配
            long expectedDelta = changePerIter * testIterations / 2;
            Console.WriteLine($"Incremental refresh ({changePerIter} new entities + query): {incMs,10:F4} ms/iter");
            Console.WriteLine($"Entity count: {before} -> {after} (delta {after - before}, expected {expectedDelta} {(after - before == expectedDelta ? "OK" : "BAD")})\n");

            // ===== 3. Entity Group 反向索引：Entity → 匹配的查询集合 =====
            Console.WriteLine("--- 3. Entity Group reverse index (GetGroupsOf) ---");

            var qPV = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position, Velocity>());
            var qP = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>());
            var qH = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Health>());

            // 选一个 [Position, Velocity] 实体：应匹配 qPV 和 qP，不匹配 qH
            var sampleEntity = default(Entity);
            for (int i = 0; i < em.ArchetypeCount; i++)
            {
                var arch = em.Archetypes[i];
                if (arch == null) continue;
                if (arch.IsMatch(new QueryBuilder().WithAll<Position, Velocity>()))
                {
                    sampleEntity = arch.ChunkList[0].GetEntity(0);
                    break;
                }
            }

            var groups = world.GetGroupsOf(sampleEntity);
            Console.WriteLine($"Entity #{sampleEntity.Id} groups: {string.Join(", ", Enumerable.Select(groups, DescribeQuery))}");
            Console.WriteLine($"  contains qPV: {Contains(groups, qPV)}, contains qP: {Contains(groups, qP)}, contains qH: {Contains(groups, qH)}");

            // 反向索引反映组件增删
            em.AddComponent<Health>(sampleEntity, new Health { Current = 10, Max = 100 });
            var groupsAfterAdd = world.GetGroupsOf(sampleEntity);
            Console.WriteLine($"  after AddComponent<Health> → contains qH: {Contains(groupsAfterAdd, qH)}");

            em.RemoveComponent<Velocity>(sampleEntity);
            var groupsAfterRemove = world.GetGroupsOf(sampleEntity);
            Console.WriteLine($"  after RemoveComponent<Velocity> → contains qPV: {Contains(groupsAfterRemove, qPV)}");

            // 反向查询性能：O(1) 定位 + Archetype→查询查表
            sw.Restart();
            for (int i = 0; i < testIterations * 10; i++)
            {
                _ = world.GetGroupsOf(sampleEntity);
            }
            sw.Stop();
            double groupsMs = sw.Elapsed.TotalMilliseconds / (testIterations * 10) * 1000; // 转 us
            Console.WriteLine($"GetGroupsOf perf: {groupsMs,8:F2} us/iter\n");

            Console.WriteLine("=== End QueryCache Benchmark ===\n");
        }

        private static string DescribeQuery(EntityQuery q)
        {
            // 展示：查询匹配的 archetype 数（演示用，非精确类型名）
            return $"Query[{q.MatchingArchetypes.Count} arch]";
        }

        private static bool Contains(System.Collections.Generic.IReadOnlyList<EntityQuery> list, EntityQuery q)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == q) return true;
            return false;
        }
    }
}
