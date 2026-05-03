using EntJoy;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;


internal static class JobScheduler
{
    public static JobHandle Schedule(IJob job, JobHandle dependency)
    {
        Task task;
        var depTask = dependency.GetTask();
        if (depTask != null && depTask != Task.CompletedTask)
            task = depTask.ContinueWith(_ => job.Execute(), TaskContinuationOptions.ExecuteSynchronously);
        else
            task = Task.Run(job.Execute);
        return new JobHandle(task);
    }

    public static JobHandle ScheduleParallelFor(IJobParallelFor job, int arrayLength, int batchCount, JobHandle dependency, ThreadCounter counter)
    {
        // 使用 Partitioner 实现工作窃取和自动负载均衡
        var partitioner = Partitioner.Create(0, arrayLength);
        if (batchCount > 0)
            partitioner = Partitioner.Create(0, arrayLength, batchCount);
        var depTask = dependency.GetTask();

        // 设置并行选项，强制使用所有逻辑核心
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        Action mainAction = () =>
        {
            Parallel.ForEach(partitioner, options, range =>
            {
                // 每个 range 处理前记录一次（线程可能处理多个 range，但次数极少）
                counter?.RecordCurrentThread();
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    job.Execute(i);
                }
            });
        };

        Task mainTask;
        if (depTask != null && depTask != Task.CompletedTask)
            mainTask = depTask.ContinueWith(_ => mainAction(), TaskContinuationOptions.ExecuteSynchronously);
        //TaskContinuationOptions.ExecuteSynchronously 表示如果 depTask 已经完成，延续任务会尝试在调用线程上同步执行，减少调度开销；否则，它会在 depTask 完成后由线程池调度执行
        else
            mainTask = Task.Run(mainAction);

        return new JobHandle(mainTask);
    }

    public static JobHandle ScheduleFor(IJobFor job, int arrayLength, JobHandle dependency)
    {

        var depTask = dependency.GetTask();
        Action action = () =>
        {
            for (int i = 0; i < arrayLength; i++)
                job.Execute(i);
        };

        Task mainTask;
        if (depTask != null && depTask != Task.CompletedTask)
            mainTask = depTask.ContinueWith(_ => action(), TaskContinuationOptions.ExecuteSynchronously);
        else
            mainTask = Task.Run(action);

        return new JobHandle(mainTask);
    }

    public static JobHandle ScheduleParallelForBatch<T>(T job, int arrayLength, int batchSize, JobHandle dependency, ThreadCounter counter)
    where T : struct, IJobParallelForBatch
    {
        // 使用 Partitioner.Create(int fromInclusive, int toExclusive, int rangeSize) 创建固定大小的分区
        var partitioner = Partitioner.Create(0, arrayLength);
        if (batchSize > 0)
            partitioner = Partitioner.Create(0, arrayLength, batchSize);
        //var partitioner = Partitioner.Create(0, arrayLength);
        var depTask = dependency.GetTask();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };

        Action mainAction = () =>
        {
            Parallel.ForEach(partitioner, options, range =>
            {
                counter?.RecordCurrentThread();
                job.Execute(range.Item1, range.Item2 - range.Item1);
            });
        };

        Task mainTask;
        if (depTask != null && depTask != Task.CompletedTask)
            mainTask = depTask.ContinueWith(_ => mainAction(), TaskContinuationOptions.ExecuteSynchronously);
        else
            mainTask = Task.Run(mainAction);

        return new JobHandle(mainTask);
    }

    // 缓存 chunk 列表避免每帧分配
    [ThreadStatic]
    private static List<Chunk> t_cachedChunks;

    public unsafe static JobHandle ScheduleChunkJob<T>(T job, QueryBuilder query, JobHandle dependency, ThreadCounter counter = null)
    where T : struct, IJobChunk
    {
        var world = World.DefaultWorld;
        if (world == null) throw new InvalidOperationException("No active world found.");

        var entityManager = world.EntityManager;

        // 复用缓存的 List，避免每帧 new
        var chunks = t_cachedChunks;
        if (chunks == null)
        {
            chunks = new List<Chunk>(128);
            t_cachedChunks = chunks;
        }
        else
        {
            chunks.Clear();
        }

        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var arch = entityManager.Archetypes[i];
            if (arch != null && arch.IsMatch(query))
            {
                foreach (var chunk in arch.GetChunks())
                {
                    if (chunk.EntityCount > 0)
                        chunks.Add(chunk);
                }
            }
        }

        // 如果没匹配到任何 chunk，直接返回已完成
        if (chunks.Count == 0)
            return new JobHandle(Task.CompletedTask);

        var depTask = dependency.GetTask();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        // 在主线程预处理 enabledIndices（只在主线程做一次，不在并行任务里重复 new）
        int[] enabledIndices = null;
        if (query.AllEnabled != null && query.AllEnabled.Length > 0)
        {
            enabledIndices = ArrayPool<int>.Shared.Rent(query.AllEnabled.Length);
            var arch = chunks[0].Archetype;
            for (int i = 0; i < query.AllEnabled.Length; i++)
                enabledIndices[i] = arch.GetComponentTypeIndex(query.AllEnabled[i]);
        }

        Action mainAction = () =>
        {
            // 每个 chunk 处理的线程内部不再 new List
            var hasEnabled = enabledIndices != null;
            if (hasEnabled)
            {
                Parallel.ForEach(chunks, options, chunk =>
                {
                    counter?.RecordCurrentThread();

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

                    for (int j = 1; j < query.AllEnabled.Length; j++)
                    {
                        int idx = enabledIndices[j];
                        ulong* compMask = chunk.GetEnableBitMapPointer(idx);
                        if (compMask == null) continue;
                        for (int i = 0; i < ulongCount; i++)
                            combinedMask[i] &= compMask[i];
                    }

                    var enabledMask = new ChunkEnabledMask(combinedMask, chunk.EntityCount);
                    job.Execute(new ArchetypeChunk(chunk), enabledMask);
                });
            }
            else
            {
                Parallel.ForEach(chunks, options, chunk =>
                {
                    counter?.RecordCurrentThread();
                    job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
                });
            }
        };

        Task mainTask;
        if (depTask != null && depTask != Task.CompletedTask)
            mainTask = depTask.ContinueWith(_ => mainAction(), TaskContinuationOptions.ExecuteSynchronously);
        else
            mainTask = Task.Run(mainAction);

        if (enabledIndices != null)
            ArrayPool<int>.Shared.Return(enabledIndices);

        return new JobHandle(mainTask);
    }

    //public static JobHandle ScheduleChunkJobBatched<T>(T job, List<Chunk> chunks, int batchSize, JobHandle dependency, ThreadCounter counter = null)
    //where T : struct, IJobChunk
    //{
    //    if (chunks == null || chunks.Count == 0)
    //        return new JobHandle(Task.CompletedTask);

    //    var depTask = dependency.GetTask();
    //    int coreCount = Environment.ProcessorCount;
    //    // 自动调整batchSize：如果batchSize <=0，则根据核心数自动计算（每个核心至少一个batch）
    //    int actualBatchSize = batchSize > 0 ? batchSize : (chunks.Count + coreCount - 1) / coreCount;

    //    var partitioner = Partitioner.Create(chunks, true); // true 表示启用动态分区
    //    var options = new ParallelOptions { MaxDegreeOfParallelism = coreCount };

    //    Action mainAction = () =>
    //    {
    //        Parallel.ForEach(partitioner, options, chunk => job.Execute(new ArchetypeChunk(chunk)));
    //    };

    //    Task mainTask = (depTask != null && depTask != Task.CompletedTask)
    //        ? depTask.ContinueWith(_ => mainAction(), TaskContinuationOptions.ExecuteSynchronously)
    //        : Task.Run(mainAction);

    //    return new JobHandle(mainTask);
    //}


}
