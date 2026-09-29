using System;
using System.Threading;
using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// Job 记账的两条不变式（审计 B5 / B6）：
    ///
    /// **B5**：`TrackEntityJob` 允许 `matchingArchetypes: null`（native entity-batch 路径就是这么注册的）。
    /// 这类 Job 对「选择性等待」不可见 —— `CompleteArchetypeJobs` 只查 `_archetypeJobs`，
    /// 于是结构变更（DestroyEntity/AddComponent/CompactChunks…）可以在该 Job 仍在写同一 chunk 时
    /// 释放或搬迁它的内存。修复：archetype 归属未知的 Job 记入 `_unscopedJobs`，结构变更保守等待。
    ///
    /// **B6**：`_jobWrittenComponents`（Job → 写入组件）必须随 Job 完成被清理。
    /// 否则每个调度都留下一条 Dictionary 条目 + 一个被保留的 ComponentType[]，
    /// 且已释放的 native 句柄值被复用时与陈旧组件集串味（选择性等待按错误组件集过滤）。
    /// </summary>
    public class JobTrackingInvariantTests
    {
        private struct SleepThenFlagJob : IJob
        {
            public NativeArray<int> Flags;
            public void Execute()
            {
                Thread.Sleep(150);
                Flags[0] = 1;
            }
        }

        private struct NoopJob : IJob
        {
            public void Execute() { }
        }

        [Fact]
        public void ArchetypeLessJob_IsWaitedByStructuralChange()
        {
            TestBackend.EnsureInitialized();

            using var world = new World("Unscoped" + Guid.NewGuid().ToString("N"));
            var em = world.EntityManager;
            var flags = new NativeArray<int>(1, Allocator.Persistent);
            try
            {
                var entity = em.NewEntity(typeof(Position));

                var job = new SleepThenFlagJob { Flags = flags };
                var handle = JobScheduler.Schedule(ref job);

                // 模拟 native entity-batch 的注册形状：不带 matchingArchetypes
                em.TrackEntityJob(handle, null);

                // 结构变更：修复后必须先等待该 archetype-less Job
                em.DestroyEntity(entity);

                Assert.Equal(1, flags[0]); // 未等待时：job 仍在睡 → 0，或主线程访问被写声明拦下
            }
            finally
            {
                flags.Dispose();
            }
        }

        [Fact]
        public void WrittenComponentsTable_DoesNotGrowWithScheduledJobCount()
        {
            TestBackend.EnsureInitialized();

            using var world = new World("JobTable" + Guid.NewGuid().ToString("N"));
            var em = world.EntityManager;
            em.NewEntity(typeof(Position));

            Archetype? arch = null;
            foreach (var a in em.GetAllArchetypes())
            {
                if (a.ChunkCount > 0) { arch = a; break; }
            }
            Assert.NotNull(arch);

            var posType = ComponentTypeManager.GetComponentType(typeof(Position));
            var archetypes = new[] { arch! };
            var written = new[] { posType };

            const int N = 200;
            for (int i = 0; i < N; i++)
            {
                var job = new NoopJob();
                var handle = JobScheduler.Schedule(ref job);
                // 必须先登记再 Complete：JobHandle.Complete() 会**消费**句柄（native 句柄被释放），
                // 之后再 TrackEntityJob 会被「空句柄」早退，测试就成了空转。
                em.TrackEntityJob(handle, archetypes, written);
                handle.Complete();                       // 让句柄进入「已完成」态，成为可清理对象
            }

            // 记账表不得随调度次数线性增长（修复前 = N）
            Assert.True(em.JobWrittenComponentsCount <= 2,
                $"Job→写入组件 记账表应被清理，实际条目数={em.JobWrittenComponentsCount}（调度 {N} 次）");

            // 同理：archetype 归属未知的登记也必须被清理
            for (int i = 0; i < N; i++)
            {
                var job = new NoopJob();
                var handle = JobScheduler.Schedule(ref job);
                em.TrackEntityJob(handle, null);
                handle.Complete();
            }
            Assert.True(em.UnscopedJobCount <= 2,
                $"archetype-less 在飞 Job 记账应被清理，实际={em.UnscopedJobCount}");
        }
    }
}
