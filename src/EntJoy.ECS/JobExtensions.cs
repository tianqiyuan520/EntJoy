using EntJoy;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
namespace EntJoy.JobSystem
{

public static partial class JobExtensions
{
    // ======================== IJob 调度 ========================

    /// <summary>调度 IJob（无依赖）</summary>
    public static JobHandle Schedule<T>(this T job) where T : struct, IJob
    {
        return new JobHandle(NativeJobScheduler.Schedule(ref job));
    }

    /// <summary>调度 IJob（带依赖）</summary>
    public static JobHandle Schedule<T>(this T job, JobHandle dependsOn) where T : struct, IJob
    {
        NativeJobHandle? nativeDep = dependsOn.GetNativeDependency();
        return new JobHandle(NativeJobScheduler.Schedule(ref job, nativeDep));
    }

    // ======================== IJobParallelFor 调度 ========================

    /// <summary>调度 IJobParallelFor</summary>
    public static JobHandle Schedule<T>(this T job, int arrayLength, int innerBatchCount,
        JobHandle dependsOn = default) where T : struct, IJobParallelFor
    {
        NativeJobHandle? nativeDep = dependsOn.GetNativeDependency();
        return new JobHandle(
            NativeJobScheduler.ScheduleParallelFor(ref job, arrayLength, innerBatchCount, nativeDep));
    }

    // ======================== IJobFor 调度 ========================

    /// <summary>调度 IJobFor（串行 for 循环）</summary>
    public static JobHandle Schedule<T>(this T job, int arrayLength,
        JobHandle dependsOn = default) where T : struct, IJobFor
    {
        NativeJobHandle? nativeDep = dependsOn.GetNativeDependency();
        return new JobHandle(
            NativeJobScheduler.ScheduleFor(ref job, arrayLength, nativeDep));
    }

    // ======================== IJobParallelForBatch 调度 ========================

    /// <summary>调度 IJobParallelForBatch</summary>
    public static JobHandle ScheduleBatch<T>(this T job, int arrayLength, int batchSize,
        JobHandle dependsOn = default) where T : struct, IJobParallelForBatch
    {
        NativeJobHandle? nativeDep = dependsOn.GetNativeDependency();
        return new JobHandle(
            NativeJobScheduler.ScheduleParallelForBatch(ref job, arrayLength, batchSize, nativeDep));
    }

    // ======================== ThreadCounter 重载 ========================

    /// <summary>调度 IJobParallelFor（带 ThreadCounter，调试用）</summary>
    public static JobHandle Schedule<T>(this T job, int arrayLength, int innerBatchCount,
        JobHandle dependsOn, ThreadCounter counter) where T : struct, IJobParallelFor
    {
        // 当前 C++ 调度器尚未支持 ThreadCounter，直接调度
        // 如果需要计数，可以在 C++ 端或回调中统计
        NativeJobHandle? nativeDep = dependsOn.GetNativeDependency();
        return new JobHandle(
            NativeJobScheduler.ScheduleParallelFor(ref job, arrayLength, innerBatchCount, nativeDep));
    }

    /// <summary>调度 IJobParallelForBatch（带 ThreadCounter，调试用）</summary>
    public static JobHandle ScheduleBatch<T>(this T job, int arrayLength, int batchSize,
        JobHandle dependsOn, ThreadCounter counter) where T : struct, IJobParallelForBatch
    {
        NativeJobHandle? nativeDep = dependsOn.GetNativeDependency();
        return new JobHandle(
            NativeJobScheduler.ScheduleParallelForBatch(ref job, arrayLength, batchSize, nativeDep));
    }

    // ======================== IJobChunk 调度 ========================

    /// <summary>调度 IJobChunk</summary>
    public static JobHandle Schedule<T>(this T job, QueryBuilder query,
        JobHandle dependsOn = default) where T : struct, IJobChunk
    {
        var world = World.DefaultWorld;
        if (world == null) throw new InvalidOperationException("No active World found.");
        NativeJobHandle? nativeDep = dependsOn._nativeHandle;
        return new JobHandle(NativeEcsScheduler.ScheduleChunk(ref job, world.EntityManager, query, nativeDep));
    }

    /// <summary>调度 IJobChunk（带 workerCap）</summary>
    public static JobHandle ScheduleWithWorkerCap<T>(this T job, QueryBuilder query, int workerCap,
        JobHandle dependsOn = default) where T : struct, IJobChunk
    {
        var world = World.DefaultWorld;
        if (world == null) throw new InvalidOperationException("No active World found.");
        NativeJobHandle? nativeDep = dependsOn._nativeHandle;
        return new JobHandle(NativeEcsScheduler.ScheduleChunkWithWorkerCap(ref job, world.EntityManager, query, workerCap, nativeDep));
    }

    // ======================== Run 方法（主线程执行，调试用） ========================

    public static void Run(this IJob job) => job.Execute();

    public static void Run(this IJobParallelFor job, int arrayLength)
    {
        for (int i = 0; i < arrayLength; i++) job.Execute(i);
    }

    public static void Run(this IJobFor job, int arrayLength)
    {
        for (int i = 0; i < arrayLength; i++) job.Execute(i);
    }

}
}
