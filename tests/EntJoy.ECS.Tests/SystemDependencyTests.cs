using System;
using System.Diagnostics;
using System.Threading;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // Producer：忙等制造确定性的执行窗口，结束后写 42。
    public struct SlowProducerJob : IJobChunk
    {
        public static int Started;
        public static int Finished;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            Interlocked.Increment(ref Started);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 100) Thread.SpinWait(16);
            Interlocked.Increment(ref Finished);
            var span = chunk.GetComponentDataSpan<Position>();
            for (int i = 0; i < span.Length; i++)
            {
                span[i].X = 42f;
                span[i].Y = 42f;
            }
        }
    }

    // Consumer：若在 Producer 完成前执行（依赖缺失），检测到脏读/未完成标记。
    public struct CheckConsumerJob : IJobChunk
    {
        public static int Violations;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            if (Volatile.Read(ref SlowProducerJob.Finished) < Volatile.Read(ref SlowProducerJob.Started))
                Interlocked.Increment(ref Violations);
            var span = chunk.GetComponentDataSpan<Position>();
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i].X != 42f) Interlocked.Increment(ref Violations);
            }
        }
    }

    [Write(typeof(Position))]
    [Order(0)]
    public struct SlowProducerSystem : ISystem
    {
        public void OnUpdate()
        {
            var job = new SlowProducerJob();
            job.Schedule(new QueryBuilder().WithAll<Position>());
        }
    }

    [Read(typeof(Position))]
    [Order(1)]
    public struct CheckConsumerSystem : ISystem
    {
        public void OnUpdate()
        {
            var job = new CheckConsumerJob();
            job.Schedule(new QueryBuilder().WithAll<Position>());
        }
    }

    // 显式 SystemState.Dependency：系统手动合并依赖并调度（DOTS 风格）。
    [Write(typeof(Position))]
    [Order(0)]
    public struct ExplicitProducerSystem : ISystem, ISystemWithState
    {
        public void OnUpdate(ref SystemState state)
        {
            var job = new SlowProducerJob();
            state.Dependency = job.Schedule(new QueryBuilder().WithAll<Position>(), dependsOn: state.Dependency);
        }
    }

    public class SystemDependencyTests
    {
        static SystemDependencyTests()
        {
            JobScheduler.Initialize(4);
            // 等待 worker 线程启动并进入循环，避免主线程 TryAssistOne 串行执行掩盖并发竞争
            Thread.Sleep(300);
        }

        [Fact]
        public void ConsumerSystem_WaitsForProducerJob()
        {
            var world = new World("Dep");
            // 单实体 → 单 chunk：Producer 忙等期间，空闲 worker 会立刻执行 Consumer 的 chunk。
            world.EntityManager.NewEntity(typeof(Position));

            var runner = new SystemRunner(world);
            runner.RegisterSystem<SlowProducerSystem>();
            runner.RegisterSystem<CheckConsumerSystem>();

            SlowProducerJob.Started = 0;
            SlowProducerJob.Finished = 0;
            CheckConsumerJob.Violations = 0;

            runner.Update();

            // 若依赖传播失效，Consumer 会在 Producer 忙等期间执行，检测到未完成标记并读到 0。
            Assert.Equal(0, CheckConsumerJob.Violations);

            world.Dispose();
        }

        [Fact]
        public void ExplicitStateSystem_DependencyPropagatesToNextSystem()
        {
            var world = new World("Dep2");
            world.EntityManager.NewEntity(typeof(Position));

            var runner = new SystemRunner(world);
            runner.RegisterSystem<ExplicitProducerSystem>();
            runner.RegisterSystem<CheckConsumerSystem>();

            SlowProducerJob.Started = 0;
            SlowProducerJob.Finished = 0;
            CheckConsumerJob.Violations = 0;

            runner.Update();

            // 显式 state.Dependency 出站后，隐式 Consumer 应等待它完成。
            Assert.Equal(0, CheckConsumerJob.Violations);

            world.Dispose();
        }
    }
}
