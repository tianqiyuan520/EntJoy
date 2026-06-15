#pragma once

#ifdef _WIN32
#ifdef JOB_SYSTEM_EXPORT
#define JOB_API __declspec(dllexport)
#else
#define JOB_API __declspec(dllimport)
#endif
#else
#define JOB_API __attribute__((visibility("default")))
#endif

// Forward declarations
struct ChunkJobData;
struct EntityBatchData;
struct ProfilerEntry;
struct JobSystemTuningNative;
struct JobSystemStatsNative;

extern "C" {

    typedef void (*JobFunc)(void* context);
    typedef void (*IndexJobFunc)(void* context, int index);
    typedef void (*BatchJobFunc)(void* context, int startIndex, int count);
    typedef void (*ContextCleanupFunc)(void* context);
    // Chunk 任务回调：context 为 C# 传入的自定义数据，chunkData 为当前 Chunk 的描述块
    typedef void (*ChunkJobFunc)(void* context, const struct ChunkJobData* chunkData);
    typedef void (*ChunkRangeJobFunc)(void* context, const struct ChunkJobData* chunks, int startIndex, int count);
    typedef void (*EntityBatchRangeJobFunc)(void* context, const struct EntityBatchData* batches, int startIndex, int count);

    JOB_API void JobSystem_Initialize(int numThreads);
    JOB_API void JobSystem_Shutdown();
    JOB_API void JobSystem_PrewakeWorkers();
    JOB_API void JobSystem_FlushScheduledJobs();

    JOB_API void* JobSystem_Schedule(JobFunc func, void* context, ContextCleanupFunc cleanup, void* dependency);
    JOB_API void* JobSystem_ScheduleParallelFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup, int length, int batchSize, void* dependency);
    JOB_API void* JobSystem_ScheduleFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup, int length, void* dependency);
    JOB_API void* JobSystem_ScheduleParallelForBatch(BatchJobFunc func, void* context, ContextCleanupFunc cleanup, int length, int batchSize, void* dependency);

    JOB_API void JobSystem_Complete(void* handle);
    JOB_API void JobSystem_CompleteAndRelease(void* handle);
    JOB_API int JobSystem_IsCompleted(void* handle);
    JOB_API void JobSystem_ReleaseHandle(void* handle);
    JOB_API void* JobSystem_CombineDependencies(void** handles, int count);
    
    typedef struct JobSystemTuningNative {
        int spinBeforeWait;
        int assistAfterWaitLoops;
        int assistBurstMax;
        int assistCooldownWaitLoops;
        int minChunkSize;
        int workerPriorityMode; // 0=normal, 1=above_normal
    } JobSystemTuningNative;

    JOB_API void JobSystem_SetTuning(const JobSystemTuningNative* tuning);
    JOB_API void JobSystem_GetTuning(JobSystemTuningNative* tuning);

    typedef struct JobSystemStatsNative {
        unsigned long long completeWaitLoops;
        unsigned long long assistAttempts;
        unsigned long long assistExecuted;
        unsigned long long frameTasksSubmitted;
        unsigned long long frameTasksCompleted;
        unsigned long long workerExecutedRanges;
        unsigned long long mainExecutedRanges;
        unsigned long long stealCount;
        unsigned long long parkWakeCount;
        unsigned long long deferredRuns;
        unsigned long long publishedJobs;
        unsigned long long prewakeCount;
        unsigned long long hotSpinHits;
        unsigned long long waitFallbacks;
        unsigned long long notifiedWorkers;
        unsigned long long scheduleModePublishNoAssist;
        unsigned long long scheduleModePublishAssist;
        unsigned long long scheduleModeDeferTinyOnly;
        unsigned long long scheduleModeImmediateNative;
        unsigned long long scheduleModeDeferredPublish;
        unsigned long long scheduleModeDeferredPublishNoAssist;
        int frameQueueDepthPeak;
    } JobSystemStatsNative;

    JOB_API void JobSystem_GetStats(JobSystemStatsNative* stats);
    JOB_API void JobSystem_ResetStats();

    /** 
     * 调度多个 Chunk 任务，每个 Chunk 并行执行一次 func 回调。
     * @param func        C# 回调函数指针
     * @param context     C# 上下文数据指针（包含 job 拷贝和辅助数据）
     * @param cleanup     所有 Chunk 任务完成后的清理回调
     * @param chunks      ChunkJobData 数组（非托管内存）
     * @param chunkCount  数组长度
     * @param dependency  依赖的 JobHandle 指针（可为 nullptr）
     * @return 新的 JobHandle 指针，表示所有 Chunk 任务完成
     */
    JOB_API void* JobSystem_ScheduleChunkJob(
        ChunkJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const struct ChunkJobData* chunks,
        int chunkCount,
        void* dependency);

    JOB_API void* JobSystem_ScheduleChunkJobEx(
        ChunkJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const struct ChunkJobData* chunks,
        int chunkCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize);

    JOB_API void* JobSystem_ScheduleChunkRangeJobEx(
        ChunkRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const struct ChunkJobData* chunks,
        int chunkCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize);

    JOB_API void* JobSystem_ScheduleEntityBatchJobEx(
        EntityBatchRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const struct EntityBatchData* batches,
        int batchCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize);

    // ======================== Profiler API ========================
    // 启用/禁用 Profiler
    JOB_API void JobProfiler_SetEnabled(int enabled);
    JOB_API int  JobProfiler_IsEnabled();

    // 读取并清空所有 Profiler 记录
    // returns: 实际读取的条目数
    JOB_API int  JobProfiler_ReadAll(struct ProfilerEntry* buffer, int maxCount);

    // 清空 Profiler 缓冲
    JOB_API void JobProfiler_Clear();

} // extern "C"
