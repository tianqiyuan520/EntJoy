#pragma once

#include <cstdint>

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
namespace JobSystem { struct TraceEvent; }

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
    JOB_API void JobSystem_KeepWorkersWarm(int microseconds);
    JOB_API void JobSystem_FlushScheduledJobs();

    JOB_API void* JobSystem_Schedule(JobFunc func, void* context, ContextCleanupFunc cleanup, void* dependency);
    JOB_API void* JobSystem_ScheduleParallelFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup, int length, int batchSize, void* dependency);
    JOB_API void* JobSystem_ScheduleFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup, int length, void* dependency);
    JOB_API void* JobSystem_ScheduleParallelForBatch(BatchJobFunc func, void* context, ContextCleanupFunc cleanup, int length, int batchSize, void* dependency);

    JOB_API void JobSystem_Complete(void* handle);
    JOB_API void JobSystem_CompleteAndRelease(void* handle);
    JOB_API void JobSystem_RetainHandle(void* handle);
    JOB_API int JobSystem_IsCompleted(void* handle);
    JOB_API void JobSystem_ReleaseHandle(void* handle);
    JOB_API void* JobSystem_CombineDependencies(void** handles, int count);
    // Combined Schedule+Complete: 调度后立即 inline assist，消除 P/Invoke 往返
    // 返回已完成的 handle
    JOB_API void* JobSystem_ScheduleAndCompleteEntityBatchJobEx(
        EntityBatchRangeJobFunc func, void* context, ContextCleanupFunc cleanup,
        const struct EntityBatchData* batches, int batchCount, void* dependency,
        int scheduleMode, int workerCap, int rangeSize);
    
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
        unsigned long long workerClaimedTokens;
        unsigned long long mainClaimedTokens;
        unsigned long long coldBatches;
        unsigned long long activeWorkersPeak;
        unsigned long long wakeLatencyEwmaNs;
        unsigned long long scheduleModePublishNoAssist;
        unsigned long long scheduleModePublishAssist;
        unsigned long long scheduleModeDeferTinyOnly;
        unsigned long long scheduleModeImmediateNative;
        unsigned long long scheduleModeDeferredPublish;
        unsigned long long scheduleModeDeferredPublishNoAssist;
        int frameQueueDepthPeak;
        unsigned long long directAssistClaims;
        unsigned long long exhaustedTickets;
        unsigned long long scheduleToPublishEwmaNs;
        unsigned long long publishToFirstMainClaimEwmaNs;
        unsigned long long publishToFirstWorkerClaimEwmaNs;
        unsigned long long publishToCompletionEwmaNs;
        unsigned long long queueLockWaitEwmaNs;
        unsigned long long perRangeExecEwmaNs;
        unsigned long long assistExecPctEwma;
        unsigned long long completionOverheadUs;
        // Appended Tile/partition fields; keep order in sync with C#.
        unsigned long long workerTargetTotal;
        unsigned long long totalTilesPublished;
        unsigned long long localTiles;
        unsigned long long stolenTiles;
        unsigned long long assistTiles;
        unsigned long long stealAttempts;
        unsigned long long stealSuccesses;
        unsigned long long permitsReleased;
        unsigned long long victimScans;
        unsigned long long stealEmptyExits;
        unsigned long long batchStorageCreated;
        unsigned long long batchStorageReused;
        unsigned long long batchStorageReturned;
        unsigned long long batchStorageDropped;
        unsigned long long submitToFirstWorkerEwmaNs;
        unsigned long long workerStartSpreadEwmaNs;
        unsigned long long lastTileToTopologyDoneEwmaNs;
        unsigned long long completeWakeToReturnEwmaNs;
        unsigned long long taskflowBatches;
        unsigned long long nativeBatches;
        unsigned long long invalidBackendSelections;
        // Appended exact per-batch timing distribution; keep order in sync with C#.
        unsigned long long timingSampleCount;
        unsigned long long timingSamplesDropped;
        unsigned long long batchTotalP50Ns;
        unsigned long long batchTotalP95Ns;
        unsigned long long batchTotalP99Ns;
        unsigned long long batchTotalMaxNs;
        unsigned long long submitToFirstWorkerP50Ns;
        unsigned long long submitToFirstWorkerP95Ns;
        unsigned long long submitToFirstWorkerP99Ns;
        unsigned long long submitToFirstWorkerMaxNs;
        unsigned long long workerStartSpreadP50Ns;
        unsigned long long workerStartSpreadP95Ns;
        unsigned long long workerStartSpreadP99Ns;
        unsigned long long workerStartSpreadMaxNs;
        unsigned long long executionSpanP50Ns;
        unsigned long long executionSpanP95Ns;
        unsigned long long executionSpanP99Ns;
        unsigned long long executionSpanMaxNs;
        unsigned long long maxRangeP50Ns;
        unsigned long long maxRangeP95Ns;
        unsigned long long maxRangeP99Ns;
        unsigned long long maxRangeMaxNs;
        unsigned long long slowBatchId;
        unsigned long long slowBatchTotalNs;
        unsigned long long slowSubmitToFirstWorkerNs;
        unsigned long long slowWorkerStartSpreadNs;
        unsigned long long slowExecutionSpanNs;
        unsigned long long slowMaxRangeNs;
        unsigned long long slowCoreMigrations;
        unsigned long long slowAssistTiles;
        unsigned long long slowRangeThreadCpuNs;
        unsigned long long slowRangeThreadCycles;
        unsigned long long slowBatchMinRangeThreadCycles;
        unsigned long long slowBatchAverageRangeThreadCycles;
        int slowRangeIndex;
        int slowRangeWorker;
        int slowRangeStartLogicalCore;
        int slowRangeEndLogicalCore;
        int slowRangeStartPhysicalCore;
        int slowRangeEndPhysicalCore;
    } JobSystemStatsNative;

    JOB_API void JobSystem_GetStats(JobSystemStatsNative* stats);
    JOB_API void JobSystem_ResetStats();
    JOB_API void JobSystem_SetTimingDiagnostics(int enabled);

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

    JOB_API void Trace_SetEnabled(int enabled);
    JOB_API int Trace_IsEnabled();
    JOB_API int Trace_ReadAll(JobSystem::TraceEvent* buffer, int maxCount);
    JOB_API uint64_t Trace_DroppedEvents();
    JOB_API void Trace_Clear();

} // extern "C"
