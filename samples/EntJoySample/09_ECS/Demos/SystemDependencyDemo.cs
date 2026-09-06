using System;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;

namespace EntJoySample.ECS
{
    // ─── 系统 Job 自动依赖（DOTS SystemState.Dependency）示例 ───
    // 隐式路径：系统内 job.Schedule(query) 未传 dependsOn 时，自动继承执行上下文 Dependency，
    // SystemRunner 按 [Read]/[Write] 声明合并前序系统的冲突依赖（读等写、写等写，读读不冲突）。

    /// <summary>写 Position = 42 的 chunk job。</summary>
    public struct DepWriteJob : IJobChunk
    {
        public float Value;
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
        {
            var span = chunk.GetComponentDataSpan<Position>();
            for (int i = 0; i < span.Length; i++) { span[i].X = Value; span[i].Y = Value; }
        }
    }

    /// <summary>读 Position 的 chunk job，记录首个实体值到静态字段。</summary>
    public struct DepReadJob : IJobChunk
    {
        public static float Observed;
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
        {
            var span = chunk.GetComponentDataSpan<Position>();
            if (span.Length > 0) Observed = span[0].X;
        }
    }

    [Write(typeof(Position))]
    [Order(0)]
    public struct DepProducerSystem : ISystem
    {
        public void OnUpdate()
        {
            var job = new DepWriteJob { Value = 42f };
            job.Schedule(new QueryBuilder().WithAll<Position>());  // 自动入队，回写 Dependency
        }
    }

    [Read(typeof(Position))]
    [Order(1)]
    public struct DepConsumerSystem : ISystem
    {
        public void OnUpdate()
        {
            var job = new DepReadJob();
            job.Schedule(new QueryBuilder().WithAll<Position>());  // 自动依赖 Producer 的 job
        }
    }

    // 显式路径：ISystemWithState 手动读写 state.Dependency（对齐 DOTS OnUpdate(ref SystemState)）。
    [Write(typeof(Position))]
    [Order(0)]
    public struct ExplicitStateProducerSystem : ISystem, ISystemWithState
    {
        public void OnUpdate(ref SystemState state)
        {
            var job = new DepWriteJob { Value = 99f };
            state.Dependency = job.Schedule(new QueryBuilder().WithAll<Position>(), dependsOn: state.Dependency);
        }
    }

    public static class SystemDependencyDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== SystemDependency Demo (SystemState.Dependency) ===\n");

            TestImplicitDependency();
            TestExplicitState();

            Console.WriteLine("=== End SystemDependency Demo ===\n");
        }

        private static void TestImplicitDependency()
        {
            Console.WriteLine("--- 隐式自动依赖：Consumer 自动等待 Producer ---");
            using var world = new World("SystemDependencyImplicit");
            World.DefaultWorld = world;
            world.EntityManager.NewEntity(typeof(Position));

            var runner = new SystemRunner(world);
            runner.RegisterSystem<DepProducerSystem>();
            runner.RegisterSystem<DepConsumerSystem>();

            DepReadJob.Observed = -1f;
            runner.Update();  // 帧末屏障等待所有 job

            Console.WriteLine($"  Consumer observed = {DepReadJob.Observed} (expect 42)");
            Console.WriteLine($"  {(DepReadJob.Observed == 42f ? "OK" : "BAD")}\n");
        }

        private static void TestExplicitState()
        {
            Console.WriteLine("--- 显式 SystemState.Dependency：手动合并依赖调度 ---");
            using var world = new World("SystemDependencyExplicit");
            World.DefaultWorld = world;
            world.EntityManager.NewEntity(typeof(Position));

            var runner = new SystemRunner(world);
            runner.RegisterSystem<ExplicitStateProducerSystem>();
            runner.RegisterSystem<DepConsumerSystem>();

            DepReadJob.Observed = -1f;
            runner.Update();

            Console.WriteLine($"  Consumer observed = {DepReadJob.Observed} (expect 99)");
            Console.WriteLine($"  {(DepReadJob.Observed == 99f ? "OK" : "BAD")}\n");
        }
    }
}
