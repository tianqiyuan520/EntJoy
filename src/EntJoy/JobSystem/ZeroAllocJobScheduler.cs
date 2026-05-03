using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace EntJoy.JobSystem
{
    public static class ZeroAllocJobScheduler
    {
        private static JobHandle CreateJobHandleFromTask(Task task) => new JobHandle(task);
        private static JobHandle CreateCompletedJobHandle() => new JobHandle(Task.CompletedTask);

        // ---------- IJob ----------
        public static JobHandle Schedule<T>(T job, JobHandle dependency) where T : struct, IJob
        {
            var depTask = dependency.GetTask();
            if (depTask == null || depTask == Task.CompletedTask)
            {
                var t = Task.Run(() => job.Execute());
                return new JobHandle(t);
            }

            var wrapper = SimpleJobWrapper<T>.Rent();
            wrapper.Job = job;
            Task task = depTask.ContinueWith(_ => wrapper.Execute(), TaskContinuationOptions.ExecuteSynchronously);
            return new JobHandle(task);
        }

        // ---------- IJobParallelFor ----------
        public static JobHandle ScheduleParallelFor<T>(T job, int arrayLength, int batchCount, JobHandle dependency, ThreadCounter counter)
            where T : struct, IJobParallelFor
        {
            if (arrayLength == 0) return CreateCompletedJobHandle();
            var depTask = dependency.GetTask();
            Action mainAction = () => ExecuteParallelForDirect(job, arrayLength, batchCount, counter);

            Task mainTask = (depTask != null && depTask != Task.CompletedTask)
                ? depTask.ContinueWith(_ => mainAction(), TaskContinuationOptions.ExecuteSynchronously)
                : Task.Run(mainAction);
            return new JobHandle(mainTask);
        }

        // ---------- IJobFor ----------
        public static JobHandle ScheduleFor<T>(T job, int arrayLength, JobHandle dependency)
            where T : struct, IJobFor
        {
            if (arrayLength == 0) return CreateCompletedJobHandle();
            var depTask = dependency.GetTask();
            Action action = () => { for (int i = 0; i < arrayLength; i++) job.Execute(i); };

            Task task = (depTask != null && depTask != Task.CompletedTask)
                ? depTask.ContinueWith(_ => action(), TaskContinuationOptions.ExecuteSynchronously)
                : Task.Run(action);
            return new JobHandle(task);
        }

        // ---------- IJobParallelForBatch ----------
        public static JobHandle ScheduleParallelForBatch<T>(T job, int arrayLength, int batchSize, JobHandle dependency, ThreadCounter counter)
            where T : struct, IJobParallelForBatch
        {
            if (arrayLength == 0) return CreateCompletedJobHandle();
            var depTask = dependency.GetTask();
            Action mainAction = () => ExecuteParallelForBatchDirect(job, arrayLength, batchSize, counter);

            Task mainTask = (depTask != null && depTask != Task.CompletedTask)
                ? depTask.ContinueWith(_ => mainAction(), TaskContinuationOptions.ExecuteSynchronously)
                : Task.Run(mainAction);
            return new JobHandle(mainTask);
        }

        // ---------- IJobChunk ----------
        public unsafe static JobHandle ScheduleChunkJob<T>(T job, QueryBuilder query, JobHandle dependency, ThreadCounter counter = null)
            where T : struct, IJobChunk
        {
            var world = World.DefaultWorld;
            if (world == null) throw new InvalidOperationException("No active world found.");
            var entityManager = world.EntityManager;

            // 直接分配新列表，不与任何池交互，确保生命周期贯穿异步任务
            var chunks = new List<Chunk>(128);
            for (int i = 0; i < entityManager.ArchetypeCount; i++)
            {
                var arch = entityManager.Archetypes[i];
                if (arch != null && arch.IsMatch(query))
                {
                    foreach (var chunk in arch.GetChunks())
                        if (chunk.EntityCount > 0)
                            chunks.Add(chunk);
                }
            }

            if (chunks.Count == 0)
                return CreateCompletedJobHandle();

            var depTask = dependency.GetTask();
            var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

            Action mainAction = () =>
            {
                Parallel.ForEach(chunks, options, chunk =>
                {
                    counter?.RecordCurrentThread();
                    var arch = chunk.Archetype;
                    var enabledIndices = new List<int>(4);  // 每个任务独立的小列表

                    if (query.AllEnabled != null)
                    {
                        foreach (var compType in query.AllEnabled)
                            enabledIndices.Add(arch.GetComponentTypeIndex(compType));
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
                            combinedMask = TempBuffer.GetBuffer(ulongCount);  // 使用已修复的 TempBuffer
                        }

                        int firstIdx = enabledIndices[0];
                        ulong* firstMask = chunk.GetEnableBitMapPointer(firstIdx);
                        Buffer.MemoryCopy(firstMask, combinedMask, ulongCount * 8, ulongCount * 8);

                        for (int j = 1; j < enabledIndices.Count; j++)
                        {
                            ulong* compMask = chunk.GetEnableBitMapPointer(enabledIndices[j]);
                            if (compMask == null) continue;
                            for (int k = 0; k < ulongCount; k++)
                                combinedMask[k] &= compMask[k];
                        }

                        var enabledMask = new ChunkEnabledMask(combinedMask, chunk.EntityCount);
                        job.Execute(new ArchetypeChunk(chunk), enabledMask);
                    }
                    else
                    {
                        job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
                    }
                });
            };

            Task mainTask = (depTask != null && depTask != Task.CompletedTask)
                ? depTask.ContinueWith(_ => mainAction(), TaskContinuationOptions.ExecuteSynchronously)
                : Task.Run(mainAction);

            return new JobHandle(mainTask);
        }

        // ---------- 直接执行方法 ----------
        private static void ExecuteParallelForDirect<T>(T job, int arrayLength, int batchCount, ThreadCounter counter)
            where T : struct, IJobParallelFor
        {
            int numThreads = Environment.ProcessorCount;
            int partitionSize = (arrayLength + numThreads - 1) / numThreads;
            using (var countdown = new CountdownEvent(numThreads))
            {
                for (int threadIndex = 0; threadIndex < numThreads; threadIndex++)
                {
                    int start = threadIndex * partitionSize;
                    int end = Math.Min(start + partitionSize, arrayLength);
                    if (start >= end) { countdown.Signal(); continue; }
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            counter?.RecordCurrentThread();
                            for (int i = start; i < end; i++) job.Execute(i);
                        }
                        finally { countdown.Signal(); }
                    });
                }
                countdown.Wait();
            }
        }

        private static void ExecuteParallelForBatchDirect<T>(T job, int arrayLength, int batchSize, ThreadCounter counter)
            where T : struct, IJobParallelForBatch
        {
            if (batchSize <= 0) batchSize = Math.Max(1, arrayLength / Environment.ProcessorCount);
            int numBatches = (arrayLength + batchSize - 1) / batchSize;
            int numThreads = Math.Min(Environment.ProcessorCount, numBatches);
            int batchesPerThread = (numBatches + numThreads - 1) / numThreads;
            using (var countdown = new CountdownEvent(numThreads))
            {
                for (int t = 0; t < numThreads; t++)
                {
                    int batchStart = t * batchesPerThread;
                    int batchEnd = Math.Min(batchStart + batchesPerThread, numBatches);
                    if (batchStart >= batchEnd) { countdown.Signal(); continue; }
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            counter?.RecordCurrentThread();
                            for (int b = batchStart; b < batchEnd; b++)
                            {
                                int start = b * batchSize;
                                int count = Math.Min(batchSize, arrayLength - start);
                                job.Execute(start, count);
                            }
                        }
                        finally { countdown.Signal(); }
                    });
                }
                countdown.Wait();
            }
        }

        // ---------- 包装器对象池（仅对无状态的 Job 包装）----------
        private class SimpleJobWrapper<T> where T : struct, IJob
        {
            private static readonly ConcurrentBag<SimpleJobWrapper<T>> s_pool = new();
            public T Job;
            public void Execute()
            {
                try { Job.Execute(); }
                finally { Return(this); }
            }
            public static SimpleJobWrapper<T> Rent()
            {
                if (s_pool.TryTake(out var w)) return w;
                return new SimpleJobWrapper<T>();
            }
            private static void Return(SimpleJobWrapper<T> w)
            {
                w.Job = default;
                if (s_pool.Count < 32) s_pool.Add(w);
            }
        }
    }
}