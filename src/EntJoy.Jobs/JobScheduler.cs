using System;
using EntJoy.JobSystem.Managed;

namespace EntJoy.JobSystem
{
    /// <summary>
    /// 统一作业调度器：自动选择 Native（C++ Chase-Lev）或 Managed（纯 C# Chase-Lev）执行。
    /// 所有作业调度都通过本类，无需关心后端实现。
    /// </summary>
    public static class JobScheduler
    {
        // ─── 后端选择 ───
        internal static bool UseNative { get; private set; }

        /// <summary>
        /// 初始化调度器。优先使用 NativeDll（C++ Chase-Lev）；若不可用，自动回退 ManagedJobScheduler（纯 C#）。
        /// </summary>
        public static void Initialize(int numThreads = 0)
        {
            if (numThreads == 0)
            {
                string? env = Environment.GetEnvironmentVariable("ENTJOY_JOB_WORKERS");
                if (int.TryParse(env, out int w) && w >= 0)
                    numThreads = w;
            }
            try
            {
                NativeJobCore.JobSystem_Initialize(numThreads);
                UseNative = true;
                NativeJobScheduler.RegisterPersistentAllocator();
                NativeJobCore.ValidateStatsLayout();
                NativeJobCore.RegisterCurrentBatchIdCallback();
                if (NativeJobScheduler.TilesPerWorker > 0)
                    NativeJobCore.JobSystem_ConfigureTilesPerWorker(NativeJobScheduler.TilesPerWorker);
                NativeJobCore.JobSystem_SetJobCostCacheEnabled(NativeJobScheduler.JobCostCacheEnabled ? 1 : 0);
            }
            catch
            {
                UseNative = false;
                ManagedJobScheduler.Initialize(
                    numThreads <= 0 ? Math.Max(1, Environment.ProcessorCount - 1) : numThreads);
            }
        }

        public static void Shutdown()
        {
            if (UseNative) NativeJobScheduler.Shutdown();
            else ManagedJobScheduler.Shutdown();
        }

        // ─── IJob ───
        public static JobHandle Schedule<T>(ref T job, JobHandle dependsOn = default) where T : struct, IJob
        {
            if (UseNative) return new JobHandle(NativeJobScheduler.Schedule(ref job, dependsOn._nativeHandle));
            if (dependsOn._managedHandle.Completion != null) return new JobHandle(ManagedJobScheduler.Schedule(ref job, dependsOn._managedHandle));
            return new JobHandle(ManagedJobScheduler.Schedule(ref job));
        }

        // ─── IJobParallelFor ───
        public static JobHandle ScheduleParallelFor<T>(ref T job, int length, int innerBatchCount,
            JobHandle dependsOn = default) where T : struct, IJobParallelFor
        {
            if (UseNative) return new JobHandle(NativeJobScheduler.ScheduleParallelFor(ref job, length, innerBatchCount, dependsOn._nativeHandle));
            if (dependsOn._managedHandle.Completion != null) return new JobHandle(ManagedJobScheduler.Schedule(ref job, length, innerBatchCount, dependsOn._managedHandle));
            return new JobHandle(ManagedJobScheduler.Schedule(ref job, length, innerBatchCount));
        }

        // ─── IJobFor ───
        /// <summary>调度 IJobFor（串行 for 循环）。</summary>
        public static JobHandle ScheduleFor<T>(ref T job, int length,
            JobHandle dependsOn = default) where T : struct, IJobFor
        {
            if (UseNative) return new JobHandle(NativeJobScheduler.ScheduleFor(ref job, length, dependsOn._nativeHandle));
            // 托管路径：顺序包装器，避免 ManagedJobScheduler 的 IJobParallelFor 约束冲突
            var wrapper = new SequentialForJob<T> { Job = job, Length = length };
            return new JobHandle(ManagedJobScheduler.Schedule(ref wrapper));
        }

        // ─── IJobParallelForBatch ───
        public static JobHandle ScheduleBatch<T>(ref T job, int arrayLength, int batchSize,
            JobHandle dependsOn = default) where T : struct, IJobParallelForBatch
        {
            if (UseNative) return new JobHandle(NativeJobScheduler.ScheduleParallelForBatch(ref job, arrayLength, batchSize, dependsOn._nativeHandle));
            var wrapper = new SequentialBatchJob<T> { Job = job, Length = arrayLength, BatchSize = batchSize };
            return new JobHandle(ManagedJobScheduler.Schedule(ref wrapper));
        }

        // ─── 托管回退：顺序包装器（IJob 包装，避免 ManagedJobScheduler 泛型约束冲突） ───
        private struct SequentialForJob<T> : IJob where T : struct, IJobFor
        {
            public T Job; public int Length;
            public void Execute() { for (int i = 0; i < Length; i++) Job.Execute(i); }
        }
        private struct SequentialBatchJob<T> : IJob where T : struct, IJobParallelForBatch
        {
            public T Job; public int Length, BatchSize;
            public void Execute() { for (int b = 0; b < Length; b += BatchSize) Job.Execute(b, Math.Min(BatchSize, Length - b)); }
        }

        // ─── 状态查询 ───
        public static int WorkerCount => UseNative ? NativeJobScheduler.JobWorkerCount : Environment.ProcessorCount - 1;
        public static void PrewakeWorkersOnce() { if (UseNative) NativeJobScheduler.PrewakeWorkersOnce(); }
    }
}