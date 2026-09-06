using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using System;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS.JobSystem
{

    /// <summary>
    /// IJobChunk（依赖 ECS）调度扩展。因依赖 World/EntityManager/QueryBuilder/NativeEcsScheduler，
    /// 必须留在 EntJoy.ECS；纯 IJob 族调度见 EntJoy.Jobs 的 JobExtensions。
    /// 与 JobExtensions 同命名空间、不同类名，调用方通过 using 与泛型约束自动解析。
    /// </summary>
    public static class ChunkJobExtensions
    {
        /// <summary>调度 IJobChunk（world 默认 DefaultWorld，多 World 可显式传入）。
        /// 未显式传 dependsOn 时自动继承系统执行上下文的 Dependency（DOTS SystemState.Dependency 语义）。</summary>
        public static JobHandle Schedule<T>(this T job, QueryBuilder query,
            World world = null,
            JobHandle dependsOn = default,
            ComponentType[]? writtenComponents = null) where T : struct, IJobChunk
        {
            world ??= World.DefaultWorld;
            if (world == null) throw new InvalidOperationException("No active World found.");
            if (dependsOn.IsNull && SystemExecutionContext.IsActive)
                dependsOn = SystemExecutionContext.Dependency;
            var result = ChunkJobScheduler.ScheduleChunk(ref job, world.EntityManager, query, dependsOn, writtenComponents: writtenComponents);
            if (SystemExecutionContext.IsActive)
                SystemExecutionContext.Dependency = result;
            return result;
        }

        /// <summary>调度 IJobChunk（带 workerCap，world 默认 DefaultWorld）。依赖注入语义同 <see cref="Schedule{T}"/>。</summary>
        public static JobHandle ScheduleWithWorkerCap<T>(this T job, QueryBuilder query, int workerCap,
            World world = null,
            JobHandle dependsOn = default,
            ComponentType[]? writtenComponents = null) where T : struct, IJobChunk
        {
            world ??= World.DefaultWorld;
            if (world == null) throw new InvalidOperationException("No active World found.");
            if (dependsOn.IsNull && SystemExecutionContext.IsActive)
                dependsOn = SystemExecutionContext.Dependency;
            var result = ChunkJobScheduler.ScheduleChunkWithWorkerCap(ref job, world.EntityManager, query, workerCap, dependsOn, writtenComponents: writtenComponents);
            if (SystemExecutionContext.IsActive)
                SystemExecutionContext.Dependency = result;
            return result;
        }

        /// <summary>Run IJobChunk：同步执行（无调度开销），由 ChunkJobScheduler 直接遍历执行。</summary>
        public static unsafe void Run<T>(this T job, QueryBuilder query, World world = null) where T : struct, IJobChunk
        {
            world ??= World.DefaultWorld;
            if (world == null) throw new InvalidOperationException("No active World found.");
            world.EntityManager.CompleteActiveJobs();
            ChunkJobScheduler.ExecuteOnQuery(ref job, world.EntityManager, query);
        }

    }
}