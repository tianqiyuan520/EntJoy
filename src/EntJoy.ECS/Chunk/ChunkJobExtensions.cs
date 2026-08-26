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
        /// <summary>调度 IJobChunk</summary>
        public static JobHandle Schedule<T>(this T job, QueryBuilder query,
            JobHandle dependsOn = default,
            ComponentType[]? writtenComponents = null) where T : struct, IJobChunk
        {
            var world = World.DefaultWorld;
            if (world == null) throw new InvalidOperationException("No active World found.");
            NativeJobHandle? nativeDep = dependsOn._nativeHandle;
            return ChunkJobScheduler.ScheduleChunk(ref job, world.EntityManager, query, nativeDep, writtenComponents: writtenComponents);
        }

        /// <summary>调度 IJobChunk（带 workerCap）</summary>
        public static JobHandle ScheduleWithWorkerCap<T>(this T job, QueryBuilder query, int workerCap,
            JobHandle dependsOn = default,
            ComponentType[]? writtenComponents = null) where T : struct, IJobChunk
        {
            var world = World.DefaultWorld;
            if (world == null) throw new InvalidOperationException("No active World found.");
            NativeJobHandle? nativeDep = dependsOn._nativeHandle;
            return ChunkJobScheduler.ScheduleChunkWithWorkerCap(ref job, world.EntityManager, query, workerCap, nativeDep, writtenComponents: writtenComponents);
        }

        /// <summary>Run IJobChunk：同步执行（无调度开销），由 ChunkJobScheduler 直接遍历执行。</summary>
        public static unsafe void Run<T>(this T job, QueryBuilder query) where T : struct, IJobChunk
        {
            var world = World.DefaultWorld;
            if (world == null) throw new InvalidOperationException("No active World found.");
            world.EntityManager.CompleteActiveJobs();
            ChunkJobScheduler.ExecuteOnQuery(ref job, world.EntityManager, query);
        }

    }
}
