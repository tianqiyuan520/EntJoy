using System;
using System.Threading;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>探针：统计被 job 访问到的实体总数（由测试读/重置）。</summary>
    public struct SharedFilterProbeJob : IJobChunk
    {
        public static int Visited;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var span = chunk.GetComponentDataSpan<Position>();
            Interlocked.Add(ref Visited, span.Length);
        }
    }

    /// <summary>
    /// 契约：`QueryBuilder` 的 **Shared / Changed 过滤必须对 job 路径同样生效**
    /// （`job.Run(query)` 与 `job.Schedule(query)` 的四条 chunk 采集路径）。
    ///
    /// 缺陷形状（审计 B14 / R3）：四条 job 采集路径只调用 `Archetype.IsMatch(query)`
    /// （仅覆盖 All/Any/None/AllEnabled）与 `EntityCount > 0`，从不调用
    /// `EntityManager.MatchesSharedFilter` / `MatchesChangedFilter`；缓存指纹 `GetQueryHash`
    /// 也完全不含 shared 值 / changed / relationship 维度。于是
    /// `new QueryBuilder().WithAll&lt;Position, Material&gt;().WithShared(new Material(2))`
    /// 作为 job 运行时会处理**所有** Position+Material 的 chunk（含 Material(1)），静默错值。
    /// </summary>
    public class JobQueryFilterTests
    {
        [Fact]
        public void JobWithSharedFilter_MustOnlyVisitMatchingChunks()
        {
            TestBackend.EnsureInitialized();

            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64;
            try
            {
                using var world = new World("SharedJob" + Guid.NewGuid().ToString("N"));
                World.DefaultWorld = world;
                var em = world.EntityManager;
                var types = new ComponentType[] { typeof(Position), typeof(Material) };

                const int perValue = 8;
                for (int i = 0; i < perValue; i++)
                    em.NewEntity(types, (typeof(Material), (object)new Material(1)));
                for (int i = 0; i < perValue; i++)
                    em.NewEntity(types, (typeof(Material), (object)new Material(2)));

                SharedFilterProbeJob.Visited = 0;
                var query = new QueryBuilder().WithAll<Position, Material>().WithShared(new Material(2));

                // 先确认「已知正确路径」（EntityQuery）看到了 8 个实体，再据此判断 job 路径是否漏掉了过滤
                var entityQuery = world.GetOrCreateEntityQuery(query);
                Assert.Equal(perValue, entityQuery.CalculateEntityCount());

                var job = new SharedFilterProbeJob();
                job.Schedule(query).Complete();

                Assert.Equal(perValue, SharedFilterProbeJob.Visited);
            }
            finally
            {
                Archetype.ChunkCapacityOverride = savedCapacity;
            }
        }

        [Fact]
        public void JobWithSharedFilter_DifferentValues_DoNotShareCacheEntry()
        {
            TestBackend.EnsureInitialized();

            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64;
            try
            {
                using var world = new World("SharedJob2" + Guid.NewGuid().ToString("N"));
                World.DefaultWorld = world;
                var em = world.EntityManager;
                var types = new ComponentType[] { typeof(Position), typeof(Material) };

                // ⚠ 两组实体数**必须不同**（6 vs 3）：若两组同数，缓存指纹缺失时复用第一组的 Chunk 集合
                //    仍会得到相同计数 ⇒ 测试空转（独立验收 agent 用 6-vs-3 探针实测：
                //    去掉缓存指纹后期望 3 实得 6，而同数版本照样"通过"）。
                const int firstValueCount = 6;
                const int secondValueCount = 3;
                for (int i = 0; i < firstValueCount; i++)
                    em.NewEntity(types, (typeof(Material), (object)new Material(1)));
                for (int i = 0; i < secondValueCount; i++)
                    em.NewEntity(types, (typeof(Material), (object)new Material(2)));

                // 两次查询只差 shared 值：若缓存指纹不含 shared 值，第二次会复用第一次的 Chunk 集合
                SharedFilterProbeJob.Visited = 0;
                var j1 = new SharedFilterProbeJob();
                j1.Schedule(new QueryBuilder().WithAll<Position, Material>().WithShared(new Material(1))).Complete();
                Assert.Equal(firstValueCount, SharedFilterProbeJob.Visited);

                SharedFilterProbeJob.Visited = 0;
                var j2 = new SharedFilterProbeJob();
                j2.Schedule(new QueryBuilder().WithAll<Position, Material>().WithShared(new Material(2))).Complete();
                Assert.Equal(secondValueCount, SharedFilterProbeJob.Visited);
            }
            finally
            {
                Archetype.ChunkCapacityOverride = savedCapacity;
            }
        }
    }
}
