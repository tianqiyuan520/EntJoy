using System;
using System.Threading;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>探针 A：统计收到的 chunk 实体数。</summary>
    public struct AliasProbeJobA : IJobChunk
    {
        public static int Count;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
            => Interlocked.Add(ref Count, chunk.Count);
    }

    /// <summary>探针 B：立即统计——它的作用是**触发第二次采集**，从而覆盖复用的采集缓冲。</summary>
    public struct AliasProbeJobB : IJobChunk
    {
        public static int Count;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
            => Interlocked.Add(ref Count, chunk.Count);
    }

    /// <summary>
    /// 托管 chunk 采集的两条不变式（审计 B15 / B16c）：
    ///
    /// **B15**：托管后端「Schedule 一律异步」，而采集用的是 `[ThreadStatic] Chunk[] s_chunkBuffer` 复用缓冲。
    /// 若把它**直接**当 job 载荷（`ManagedChunkParallelJob.Chunks = s_chunkBuffer`），
    /// 同线程下一次 `Schedule` 的采集会原地覆盖它的前 count 项 ⇒ 在飞 job 遍历到**别的查询**的 chunk（静默错值）。
    ///
    /// **B16c**：托管回退只需要 `Chunk[]`，此前却照建 `ChunkJobData` payload（HGlobal 表 + 每 chunk 位图块）
    /// 并把 `out ptr` 丢掉 ⇒ 每次调度泄漏一块非托管内存。
    /// </summary>
    public class ManagedChunkCollectionTests
    {
        [Fact]
        public unsafe void AsyncChunkJobs_MustNotAliasTheSharedCollectionBuffer()
        {
            TestBackend.EnsureInitialized();

            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64;   // 两个 archetype 各 1 个 chunk（count 是 **chunk 数**，不是实体数）
            try
            {
                using var world = new World("Alias" + Guid.NewGuid().ToString("N"));
                World.DefaultWorld = world;
                var em = world.EntityManager;

                // archetype1 = Position（8 个实体，1 chunk）
                for (int i = 0; i < 8; i++) em.NewEntity(typeof(Position));
                // archetype2 = Position + Velocity（3 个实体，1 chunk）
                for (int i = 0; i < 3; i++) em.NewEntity(typeof(Position), typeof(Velocity));

                // ★ 判据用**采集层快照独立性**（确定性，不依赖调度时序）：
                //   连续两次采集 = 两次 Schedule 的采集步骤，必须各自返回独立的 Chunk[] 快照。
                //   只要第二次原地覆盖了第一次的数组，在飞 job 就会遍历到**别的查询**的 chunk（B15 静默错值）。
                //   ⚠ 旧版本靠"占满所有 worker"制造别名窗口 ⇒ 依赖 worker 数量与调度时序，
                //     在 223 项全量里出现过一次 ~6m43s 的假失败（测试自身的稳定性缺陷），已废弃该设计。
                ChunkJobCollector.CollectAndBuildManaged(em, new QueryBuilder().WithAll<Position>().WithNone<Velocity>(),
                    fillBitmaps: true, hasEnabledFilter: true,
                    out _, out var chunksA, out int countA, out _, buildPayload: false);
                Assert.Equal(1, countA);
                var archetypeA0 = chunksA[0].Archetype;   // 第二次采集前的现场

                ChunkJobCollector.CollectAndBuildManaged(em, new QueryBuilder().WithAll<Position, Velocity>(),
                    fillBitmaps: true, hasEnabledFilter: true,
                    out _, out var chunksB, out int countB, out _, buildPayload: false);
                Assert.Equal(1, countB);

                Assert.False(ReferenceEquals(chunksA, chunksB),
                    "两次采集返回了同一个 Chunk[] 实例：ThreadStatic 复用缓冲被直接当载荷，" +
                    "在飞 job 会遍历到别的查询的 chunk（B15 静默错值）");
                Assert.Same(archetypeA0, chunksA[0].Archetype);   // 第一次的快照内容不得被第二次覆盖

                // 端到端：连续两次 Schedule 各自统计到自己的 chunk（不再依赖占满 worker）
                AliasProbeJobA.Count = 0;
                AliasProbeJobB.Count = 0;
                var handleA = new AliasProbeJobA().Schedule(new QueryBuilder().WithAll<Position>().WithNone<Velocity>());
                var handleB = new AliasProbeJobB().Schedule(new QueryBuilder().WithAll<Position, Velocity>());
                handleA.Complete();
                handleB.Complete();
                Assert.Equal(8, AliasProbeJobA.Count);
                Assert.Equal(3, AliasProbeJobB.Count);
            }
            finally
            {
                Archetype.ChunkCapacityOverride = savedCapacity;
            }
        }

        [Fact]
        public unsafe void CollectWithoutPayload_MustNotAllocateChunkJobData()
        {
            TestBackend.EnsureInitialized();

            using var world = new World("NoPayload" + Guid.NewGuid().ToString("N"));
            World.DefaultWorld = world;
            var em = world.EntityManager;
            em.NewEntity(typeof(Position));

            ChunkJobCollector.CollectAndBuildManaged(em, new QueryBuilder().WithAll<Position>(),
                fillBitmaps: true, hasEnabledFilter: true,
                out var ptr, out var chunks, out var count, out var archetypes, buildPayload: false);

            Assert.True(count > 0);
            Assert.Equal(count, chunks.Length);
            Assert.True(ptr == null,
                "托管回退只需要 Chunk[]；不应分配 ChunkJobData payload（构建了又丢弃 = 每次调度泄漏 HGlobal）");
            Assert.True(archetypes.Length > 0);
        }
    }
}
