#pragma once

#include <cstdint>
#include "WorkerSnapshot.h"

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
    // 强制启动 Dear ImGui 调试面板并开始监听（不依赖 ENTJOY_DEBUG 环境变量，幂等）。
    // 由 C# NativeJobScheduler.LaunchDebuggerGUI() 调用。
    JOB_API void JobDebuggerGUI_Launch();
    // Unity JobsUtility.JobWorkerCount equivalent: the number of persistent
    // job workers selected when the scheduler was initialized.
    JOB_API int JobSystem_GetWorkerCount();
    JOB_API void JobSystem_Shutdown();
    JOB_API void JobSystem_PrewakeWorkers();
    JOB_API void JobSystem_ConfigureTilesPerWorker(int tilesPerWorker);
    JOB_API void JobSystem_ConfigureGuided(int enabled, int k, int floor);
    // 启用/关闭 per-job 自动 batch（JobCostCache）。0=关闭（默认，纯 tpw=4），
    // 1=启用（worker 按 per-job 每元素成本 EWMA 自动求解最优 tile 数）。
    JOB_API void JobSystem_SetJobCostCacheEnabled(int enabled);

    // 注册托管 Persistent 分配器回调（NativeContainers.h 的 UnsafeList 扩容/释放走托管侧，
    // 杜绝原生 free 内部指针导致的堆损坏）。alloc/free 参数为 C# 侧函数指针（cdecl）。
    typedef void* (*PersistentAllocCallback)(int32_t size);
    typedef void  (*PersistentFreeCallback)(void* ptr);
    JOB_API void JobSystem_RegisterPersistentAllocator(PersistentAllocCallback alloc, PersistentFreeCallback free);

    // 注册"当前 batch"回调。每次 job 执行窗口入口调 cb(batchId)、出口 cb(0)，
    // C# 异常按 batch 归属。
    typedef void (*CurrentBatchIdCallback)(uint64_t batchId);
    JOB_API void JobSystem_RegisterCurrentBatchId(CurrentBatchIdCallback cb);

    // 注册"batch→Job 名"解析回调，供 Dear ImGui Timeline 显示 Job 名。
    // C# 侧维护 batchId→名字映射；GUI 线程按需查询。cb 返回写入 buf 的名长（0=无映射）。
    // 第二个参数为"清空"回调：GUI 线程退出时由 native 调用，让 C# 清空累积的字典并关闭捕获。
    typedef int (*BatchJobNameResolver)(uint64_t batchId, char* buf, int bufLen);
    typedef void (*BatchJobNameClear)();
    JOB_API void JobSystem_RegisterNameResolver(BatchJobNameResolver cb, BatchJobNameClear clearCb);
    // 供 ImGui GUI 读取已注册的解析器（无则返回 nullptr）
    JOB_API const BatchJobNameResolver& JobSystem_GetNameResolver();
    // 供 ImGui GUI 在退出时触发 C# 清理
    JOB_API void JobSystem_ClearNameResolver();
    // C# 直接调用（ISPC-MT 等方法直跑）时报告一次"发布"，计入 published + Activity
    JOB_API void JobSystem_RecordDirectCall(const char* jobName, unsigned int tiles);
    // 直调执行窗口（transpiler 包装器在 native 调用前后成对调用，事件驱动开/关泳道窗口）
    JOB_API uint64_t JobSystem_BeginDirectCall(const char* jobName, unsigned int tiles);
    JOB_API void JobSystem_EndDirectCall(uint64_t id);

    JOB_API void* JobSystem_Schedule(JobFunc func, void* context, ContextCleanupFunc cleanup, void* dependency);
    JOB_API void* JobSystem_ScheduleFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup, int length, void* dependency);
    JOB_API void* JobSystem_ScheduleParallelForBatch(BatchJobFunc func, void* context, ContextCleanupFunc cleanup, int length, int batchSize, void* dependency);

    // ── 显式批提交（BatchScope.Submit 用）：一次 P/Invoke 提交一组 job，句柄回写 outHandles ──
    // kind: 0=IJob(JobFunc) 1=IJobFor(IndexJobFunc, length) 2=IJobParallelFor(BatchJobFunc, length+batchSize)
    // 内部 deferNotify 窗口：全部 submit 后统一唤醒一次（依赖未完成路径由 continuation 照常执行）。
    // 失败槽位 outHandles[i]=nullptr；返回成功提交数。
    typedef struct JobBatchDesc
    {
        unsigned char kind;
        unsigned char reserved[3];
        void* func;
        void* context;
        ContextCleanupFunc cleanup;
        void* dependency;
        int length;
        int batchSize;
    } JobBatchDesc;
    JOB_API int JobSystem_ScheduleBatch(const JobBatchDesc* descs, int count, void** outHandles);

    JOB_API void JobSystem_Complete(void* handle);
    JOB_API uint64_t JobSystem_CompleteAndRelease(void* handle);

    // ── 提交窗口延迟唤醒（deferNotify）：批提交期 Bump 打开 defer（SubmitBatch 跳过逐批
    //    notify），Flush 关闭并统一唤醒一次。支持嵌套（深度计数）；Flush 在归零时广播。 ──
    JOB_API void JobSystem_SubmitDeferBump(void);
    JOB_API void JobSystem_SubmitDeferFlush(void);
    JOB_API void JobSystem_RetainHandle(void* handle);
    JOB_API int JobSystem_IsCompleted(void* handle);
    JOB_API void JobSystem_ReleaseHandle(void* handle);
    JOB_API void* JobSystem_CombineDependencies(void** handles, int count);
    // 读 handle 的 diagnosticBatchId（Complete 后 batch 必已 submit，id 已设置）。
    JOB_API uint64_t JobSystem_GetDiagnosticBatchId(void* handle);

    // ---- 调试面板：实时 Worker 状态快照 ----
    // 读取所有 worker 的实时状态快照，写入 buffer（最多 maxCount 条）。
    // 返回实际写入的条目数。WorkerSnapshot 定义在 WorkerSnapshot.h。
    JOB_API int JobSystem_GetWorkerSnapshots(struct WorkerSnapshot* buffer, int maxCount);

    // Combined Schedule+Complete: 调度后立即 inline assist，消除 P/Invoke 往返
    // 返回已完成的 handle
    JOB_API void* JobSystem_ScheduleAndCompleteEntityBatchJobEx(
        EntityBatchRangeJobFunc func, void* context, ContextCleanupFunc cleanup,
        const struct EntityBatchData* batches, int batchCount, void* dependency,
        int scheduleMode, int workerCap, int rangeSize, int jobKind, uint32_t unitGeneration);
    
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
    JOB_API void JobSystem_SetMainThreadAssist(int enabled);
    JOB_API void JobSystem_SetWorkerAffinity(int enabled);

    /** 布局防御：返回 C++ JobSystemStatsNative 结构体字节数。
     *  C# 侧 Marshal.SizeOf&lt;NativeJobSystemStats&gt; 必须与之相等，
     *  否则 GetStats 会越界写（新增字段未同步 → 堆损坏）。*/
    JOB_API uint32_t JobSystem_GetStatsSize();

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
        int rangeSize,
        uint32_t unitGeneration);

    JOB_API void* JobSystem_ScheduleChunkRangeJobEx(
        ChunkRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const struct ChunkJobData* chunks,
        int chunkCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        uint32_t unitGeneration);

    JOB_API void* JobSystem_ScheduleEntityBatchJobEx(
        EntityBatchRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const struct EntityBatchData* batches,
        int batchCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        int jobKind,
        uint32_t unitGeneration);

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
