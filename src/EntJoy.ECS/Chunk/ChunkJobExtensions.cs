using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using System;

namespace EntJoy.ECS.JobSystem
{
    /// <summary>
    /// IJobChunk 的调度扩展：依赖 World/EntityManager/QueryBuilder，因此留在 EntJoy.ECS；
    /// 纯 IJob 族的调度见 EntJoy.Jobs 的 JobExtensions。
    /// </summary>
    public static class ChunkJobExtensions
    {
        /// <summary>调度 IJobChunk（world 默认 <see cref="World.DefaultWorld"/>）。</summary>
        public static JobHandle Schedule<T>(this T job, QueryBuilder query,
            World world = null,
            JobHandle dependsOn = default,
            ComponentType[]? writtenComponents = null) where T : struct, IJobChunk
            => ScheduleCore(ref job, query, world, dependsOn, writtenComponents, workerCap: 0);

        /// <summary>调度 IJobChunk 并限制 worker 数（world 默认 <see cref="World.DefaultWorld"/>）。</summary>
        public static JobHandle ScheduleWithWorkerCap<T>(this T job, QueryBuilder query, int workerCap,
            World world = null,
            JobHandle dependsOn = default,
            ComponentType[]? writtenComponents = null) where T : struct, IJobChunk
            => ScheduleCore(ref job, query, world, dependsOn, writtenComponents, workerCap);

        // 未显式传 dependsOn 时继承系统执行上下文的依赖（对齐 DOTS SystemState.Dependency），
        // 提交后再把本 Job 写回上下文，后续系统才能串在前面。
        private static JobHandle ScheduleCore<T>(ref T job, QueryBuilder query, World world,
            JobHandle dependsOn, ComponentType[]? writtenComponents, int workerCap) where T : struct, IJobChunk
        {
            world ??= World.DefaultWorld;
            if (world == null) throw new InvalidOperationException("No active World found.");
            if (dependsOn.IsNull && SystemExecutionContext.IsActive)
                dependsOn = SystemExecutionContext.Dependency;

            var result = workerCap > 0
                ? ChunkJobScheduler.ScheduleChunkWithWorkerCap(ref job, world.EntityManager, query, workerCap, dependsOn, writtenComponents: writtenComponents)
                : ChunkJobScheduler.ScheduleChunk(ref job, world.EntityManager, query, dependsOn, writtenComponents: writtenComponents);

            if (SystemExecutionContext.IsActive)
                SystemExecutionContext.Dependency = result;
            return result;
        }

        /// <summary>同步执行 IJobChunk（无调度开销）。</summary>
        public static unsafe void Run<T>(this T job, QueryBuilder query, World world = null) where T : struct, IJobChunk
        {
            ChunkJobCollector.ValidateJobQuery(query);
            world ??= World.DefaultWorld;
            if (world == null) throw new InvalidOperationException("No active World found.");
            world.EntityManager.CompleteActiveJobs();
            ChunkJobScheduler.ExecuteOnQuery(ref job, world.EntityManager, query);
        }
    }
}
