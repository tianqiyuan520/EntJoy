namespace EntJoy.JobSystem
{

/// <summary>
/// 纯 IJob 族（IJob / IJobParallelFor / IJobFor / IJobParallelForBatch）调度扩展。
/// 仅依赖 Jobs/Collections，可单独调度普通作业。
/// 通过 JobScheduler 统一调度，自动选择 Native / Managed 后端。
/// </summary>
public static class JobExtensions
{
    // ======================== IJob 调度 ========================

    /// <summary>调度 IJob（无依赖）</summary>
    public static JobHandle Schedule<T>(this T job) where T : struct, IJob
        => JobScheduler.Schedule(ref job);

    /// <summary>调度 IJob（带依赖）</summary>
    public static JobHandle Schedule<T>(this T job, JobHandle dependsOn) where T : struct, IJob
        => JobScheduler.Schedule(ref job, dependsOn);

    // ======================== IJobParallelFor 调度 ========================

    /// <summary>调度 IJobParallelFor</summary>
    public static JobHandle Schedule<T>(this T job, int arrayLength, int innerBatchCount,
        JobHandle dependsOn = default) where T : struct, IJobParallelFor
        => JobScheduler.ScheduleParallelFor(ref job, arrayLength, innerBatchCount, dependsOn);

    // ======================== IJobFor 调度 ========================

    /// <summary>调度 IJobFor（串行 for 循环）</summary>
    public static JobHandle Schedule<T>(this T job, int arrayLength,
        JobHandle dependsOn = default) where T : struct, IJobFor
        => JobScheduler.ScheduleFor(ref job, arrayLength, dependsOn);

    // ======================== IJobParallelForBatch 调度 ========================

    /// <summary>调度 IJobParallelForBatch</summary>
    public static JobHandle ScheduleBatch<T>(this T job, int arrayLength, int batchSize,
        JobHandle dependsOn = default) where T : struct, IJobParallelForBatch
        => JobScheduler.ScheduleBatch(ref job, arrayLength, batchSize, dependsOn);

    // ======================== ThreadCounter 重载 ========================

    /// <summary>调度 IJobParallelFor（带 ThreadCounter，调试用）</summary>
    public static JobHandle Schedule<T>(this T job, int arrayLength, int innerBatchCount,
        JobHandle dependsOn, ThreadCounter counter) where T : struct, IJobParallelFor
    {
        // 当前 C++ 调度器尚未支持 ThreadCounter，直接调度
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
