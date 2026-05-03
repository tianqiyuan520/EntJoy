using EntJoy;
using EntJoy.JobSystem;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

public static class JobExtensions
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
        NativeJobHandle? nativeDep = dependsOn._nativeHandle.IsValid
            ? dependsOn._nativeHandle
            : null;
        return new JobHandle(NativeJobScheduler.Schedule(ref job, nativeDep));
    }

    // ======================== IJobParallelFor 调度 ========================

    /// <summary>调度 IJobParallelFor</summary>
    public static JobHandle Schedule<T>(this T job, int arrayLength, int innerBatchCount,
        JobHandle dependsOn = default) where T : struct, IJobParallelFor
    {
        NativeJobHandle? nativeDep = dependsOn._nativeHandle.IsValid
            ? dependsOn._nativeHandle
            : null;
        return new JobHandle(
            NativeJobScheduler.ScheduleParallelFor(ref job, arrayLength, innerBatchCount, nativeDep));
    }

    // ======================== IJobFor 调度 ========================

    /// <summary>调度 IJobFor（串行 for 循环）</summary>
    public static JobHandle Schedule<T>(this T job, int arrayLength,
        JobHandle dependsOn = default) where T : struct, IJobFor
    {
        NativeJobHandle? nativeDep = dependsOn._nativeHandle.IsValid
            ? dependsOn._nativeHandle
            : null;
        return new JobHandle(
            NativeJobScheduler.ScheduleFor(ref job, arrayLength, nativeDep));
    }

    // ======================== IJobParallelForBatch 调度 ========================

    /// <summary>调度 IJobParallelForBatch</summary>
    public static JobHandle ScheduleBatch<T>(this T job, int arrayLength, int batchSize,
        JobHandle dependsOn = default) where T : struct, IJobParallelForBatch
    {
        NativeJobHandle? nativeDep = dependsOn._nativeHandle.IsValid
            ? dependsOn._nativeHandle
            : null;
        return new JobHandle(
            NativeJobScheduler.ScheduleParallelForBatch(ref job, arrayLength, batchSize, nativeDep));
    }

    // ======================== IJobChunk 调度 ========================

    /// <summary>
    /// 并行调度 IJobChunk，对每个匹配查询的 chunk 执行 Execute。
    /// 使用 C++ JobSystem 的线程池实现真正的并行执行。
    /// </summary>
    public static JobHandle Schedule<T>(this T job, QueryBuilder query,
        JobHandle dependsOn = default) where T : struct, IJobChunk
    {
        var world = World.DefaultWorld;
        if (world == null)
            throw new InvalidOperationException("No active World found.");

        NativeJobHandle? nativeDep = dependsOn._nativeHandle.IsValid
            ? dependsOn._nativeHandle
            : null;

        return new JobHandle(
            NativeJobScheduler.ScheduleChunk(ref job, world.EntityManager, query, nativeDep));
    }

    // ======================== ThreadCounter 重载 ========================

    /// <summary>调度 IJobParallelFor（带 ThreadCounter，调试用）</summary>
    public static JobHandle Schedule<T>(this T job, int arrayLength, int innerBatchCount,
        JobHandle dependsOn, ThreadCounter counter) where T : struct, IJobParallelFor
    {
        // 当前 C++ 调度器尚未支持 ThreadCounter，直接调度
        // 如果需要计数，可以在 C++ 端或回调中统计
        NativeJobHandle? nativeDep = dependsOn._nativeHandle.IsValid
            ? dependsOn._nativeHandle
            : null;
        return new JobHandle(
            NativeJobScheduler.ScheduleParallelFor(ref job, arrayLength, innerBatchCount, nativeDep));
    }

    /// <summary>调度 IJobParallelForBatch（带 ThreadCounter，调试用）</summary>
    public static JobHandle ScheduleBatch<T>(this T job, int arrayLength, int batchSize,
        JobHandle dependsOn, ThreadCounter counter) where T : struct, IJobParallelForBatch
    {
        NativeJobHandle? nativeDep = dependsOn._nativeHandle.IsValid
            ? dependsOn._nativeHandle
            : null;
        return new JobHandle(
            NativeJobScheduler.ScheduleParallelForBatch(ref job, arrayLength, batchSize, nativeDep));
    }

    /// <summary>调度 IJobChunk（带 ThreadCounter，调试用）</summary>
    public static JobHandle Schedule<T>(this T job, QueryBuilder query,
        JobHandle dependsOn, ThreadCounter counter) where T : struct, IJobChunk
    {
        var world = World.DefaultWorld;
        if (world == null)
            throw new InvalidOperationException("No active World found.");

        NativeJobHandle? nativeDep = dependsOn._nativeHandle.IsValid
            ? dependsOn._nativeHandle
            : null;

        return new JobHandle(
            NativeJobScheduler.ScheduleChunk(ref job, world.EntityManager, query, nativeDep));
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

    /// <summary>
    /// 在主线程依次执行每个匹配查询的 chunk（调试用）
    /// </summary>
    public unsafe static void Run<T>(this T job, QueryBuilder query) where T : struct, IJobChunk
    {
        var world = World.DefaultWorld ?? throw new InvalidOperationException("No active world found.");
        var entityManager = world.EntityManager;

        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var arch = entityManager.Archetypes[i];
            if (arch != null && arch.IsMatch(query))
            {
                foreach (var chunk in arch.GetChunks())
                {
                    if (chunk.EntityCount == 0) continue;

                    var enabledIndices = new List<int>();
                    if (query.AllEnabled != null)
                    {
                        foreach (var compType in query.AllEnabled)
                        {
                            int idx = arch.GetComponentTypeIndex(compType);
                            enabledIndices.Add(idx);
                        }
                    }

                    if (enabledIndices.Count > 0)
                    {
                        int ulongCount = (chunk.EntityCount + 63) / 64;
                        const int maxStackAlloc = 256;
                        ulong* combinedMask;

                        if (ulongCount <= maxStackAlloc)
                        {
                            var u = stackalloc ulong[ulongCount];
                            combinedMask = u;
                        }
                        else
                        {
                            combinedMask = TempBuffer.GetBuffer(ulongCount);
                        }

                        int firstIdx = enabledIndices[0];
                        ulong* firstMask = chunk.GetEnableBitMapPointer(firstIdx);
                        Buffer.MemoryCopy(firstMask, combinedMask, ulongCount * 8, ulongCount * 8);

                        for (int j = 1; j < enabledIndices.Count; j++)
                        {
                            int idx = enabledIndices[j];
                            ulong* compMask = chunk.GetEnableBitMapPointer(idx);
                            if (compMask == null) continue;
                            for (int b = 0; b < ulongCount; b++)
                                combinedMask[b] &= compMask[b];
                        }

                        var enabledMask = new ChunkEnabledMask(combinedMask, chunk.EntityCount);
                        job.Execute(new ArchetypeChunk(chunk), enabledMask);
                    }
                    else
                    {
                        job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
                    }
                }
            }
        }
    }
}
