#include "Exports.h"
#include "JobSystem.h"
#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"
#include "ThreadAffinity.h"
#include "ChunkJobData.h"
#include "EntityBatchData.h"
#include "JobProfiler.h"
#include "JobDebuggerGUI.h"
#include "NativeContainers.h"
#include <cstdio>

// 运行时输出当前 SIMD 配置（DLL 加载时执行）
struct SimdInfo {
    SimdInfo() {
#if defined(__AVX2__)
        std::fprintf(stderr, "[SIMD] AVX2 8-wide\n");
#elif defined(__AVX__)
        std::fprintf(stderr, "[SIMD] AVX 8-wide\n");
#elif defined(__SSE4_2__) || defined(__SSE4_1__) || defined(__SSE__) || defined(_M_X64)
        std::fprintf(stderr, "[SIMD] SSE4 4-wide\n");
#elif defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
        std::fprintf(stderr, "[SIMD] NEON 4-wide\n");
#else
        std::fprintf(stderr, "[SIMD] SCALAR 1-wide\n");
#endif
    }
} g_simdInfo;

static JobSystem::HandleState* fromHandle(void* ptr)
{
    return static_cast<JobSystem::HandleState*>(ptr);
}

static void* toHandle(const JobSystem::JobHandle& handle)
{
    if (auto* state = handle.State())
    {
        JobSystem::JobHandle::Acquire(state);
        return static_cast<void*>(state);
    }
    return nullptr;
}

extern "C"
{
    void JobSystem_Initialize(int numThreads)
    {
        JobSystem::Scheduler::Initialize(numThreads);
    }

    void JobDebuggerGUI_Launch()
    {
        JobSystem::JobDebuggerGUI::Launch();
    }

    int JobSystem_GetWorkerCount()
    {
        return JobSystem::CurrentWorkerCount();
    }

    void JobSystem_Shutdown()
    {
        JobSystem::Scheduler::Shutdown();
    }

    void JobSystem_PrewakeWorkers()
    {
        JobSystem::Scheduler::PrewakeWorkers();
    }

    void JobSystem_ConfigureTilesPerWorker(int tilesPerWorker)
    {
        JobSystem::Scheduler::ConfigureTilesPerWorker(tilesPerWorker);
    }

    void JobSystem_ConfigureGuided(int enabled, int k, int floor)
    {
        JobSystem::Scheduler::ConfigureGuided(enabled, k, floor);
    }

    void JobSystem_SetJobCostCacheEnabled(int enabled)
    {
        JobSystem::g_jobCostCacheEnabled.store(enabled != 0, std::memory_order_release);
    }

    void JobSystem_RegisterPersistentAllocator(PersistentAllocCallback alloc, PersistentFreeCallback free)
    {
        EntJoy::Collections::RegisterPersistentAllocator(alloc, free);
    }

    // 托管 Persistent 回调槽的单一存储（唯一归属 NativeDll.dll）。
    // DLL 分离后，NativeTranspiled.dll 的生成代码经这两个导出访问器读写同一槽，
    // 避免 inline+static 导致跨 DLL 各持一份副本（回退 malloc/free → 堆损坏）。
    // 定义显式带 ENTJOY_PERSISTENT_ALLOC_API（NativeDll 编译时=extern "C" dllexport，
    // 与 NativeContainers.h 声明一致），确保符号进入 NativeDll.dll 导出表。
    namespace {
        PersistentAllocCallback g_persistentAlloc = nullptr;
        PersistentFreeCallback  g_persistentFree  = nullptr;
    }
    ENTJOY_PERSISTENT_ALLOC_API PersistentAllocCallback* EntJoy_GetPersistentAllocRef() { return &g_persistentAlloc; }
    ENTJOY_PERSISTENT_ALLOC_API PersistentFreeCallback*  EntJoy_GetPersistentFreeRef()  { return &g_persistentFree; }

    void JobSystem_RegisterCurrentBatchId(CurrentBatchIdCallback cb)
    {
        JobSystem::RegisterCurrentBatchIdCallback(cb);
    }

    // batchId→Job名 解析器（C# 注册，供 ImGui Timeline 用）。GUI-only，存静态指针。
    namespace { BatchJobNameResolver g_batchNameResolver = nullptr; BatchJobNameClear g_batchNameClear = nullptr; }

    void JobSystem_RegisterNameResolver(BatchJobNameResolver cb, BatchJobNameClear clearCb)
    {
        g_batchNameResolver = cb;
        g_batchNameClear = clearCb;
    }

    const BatchJobNameResolver& JobSystem_GetNameResolver()
    {
        return g_batchNameResolver;
    }

    void JobSystem_ClearNameResolver()
    {
        if (g_batchNameClear) g_batchNameClear();
    }

    void JobSystem_RecordDirectCall(const char* jobName, unsigned int tiles)
    {
        JobSystem::RecordDirectCall(jobName, tiles);
    }

    uint64_t JobSystem_BeginDirectCall(const char* jobName, unsigned int tiles)
    {
        return JobSystem::BeginDirectCall(jobName, tiles);
    }

    void JobSystem_EndDirectCall(uint64_t id)
    {
        JobSystem::EndDirectCall(id);
    }

    void* JobSystem_Schedule(JobFunc func, void* context, ContextCleanupFunc cleanup, void* dependency)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto handle = JobSystem::Scheduler::Schedule(func, context, cleanup, dep);
        return toHandle(handle);
    }

    void* JobSystem_ScheduleFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup,
        int length, void* dependency)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto handle = JobSystem::Scheduler::ScheduleFor(func, context, length, cleanup, dep);
        return toHandle(handle);
    }

    void* JobSystem_ScheduleParallelForBatch(BatchJobFunc func, void* context, ContextCleanupFunc cleanup,
        int length, int batchSize, void* dependency)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(func, context, length, batchSize, cleanup, dep);
        return toHandle(handle);
    }

    void JobSystem_Complete(void* handle)
    {
        // 仅等待任务完成，不改变引用计数
        if (handle)
            JobSystem::JobHandle(fromHandle(handle), true).Complete();
    }

    uint64_t JobSystem_GetDiagnosticBatchId(void* handle)
    {
        // 读 handle 的 diagnosticBatchId。调用方须在 Complete 之后、Release 之前
        // 调用（此时 batch 必已 submit、id 已设置，且调用方引用使 state 存活）。
        if (!handle) return 0;
        return fromHandle(handle)->diagnosticBatchId.load(std::memory_order_acquire);
    }

    int JobSystem_GetWorkerSnapshots(WorkerSnapshot* buffer, int maxCount)
    {
        if (!buffer || maxCount <= 0) return 0;
        const int workerCount = JobSystem::CurrentWorkerCount();
        const int count = (maxCount < workerCount) ? maxCount : workerCount;
        for (int i = 0; i < count; ++i)
        {
            auto& snap = buffer[i];
            snap.workerIndex = i;
            snap.currentBatchId = JobSystem::g_workerCurrentBatchId[i].load(std::memory_order_relaxed);
            snap.currentTile = JobSystem::g_workerCurrentTile[i].load(std::memory_order_relaxed);
            snap.tileCount = JobSystem::g_workerBatchTileCount[i].load(std::memory_order_relaxed);
            snap.isActive = JobSystem::g_workerIsActive[i].load(std::memory_order_relaxed);
        }
        return count;
    }

    void JobSystem_CompleteAndRelease(void* handle)
    {
        // 接管调用方持有的引用，等待完成后自动释放
        if (handle)
        {
            JobSystem::JobHandle jobHandle(fromHandle(handle), false); // 不增加引用
            jobHandle.Complete();
        } // 析构时 Release(state)
    }

    void JobSystem_RetainHandle(void* handle)
    {
        if (handle)
            JobSystem::JobHandle::Acquire(fromHandle(handle));
    }

    int JobSystem_IsCompleted(void* handle)
    {
        if (!handle) return 1;
        return fromHandle(handle)->completed.load(std::memory_order_acquire) ? 1 : 0;
    }

    void JobSystem_ReleaseHandle(void* handle)
    {
        if (!handle) return;
        JobSystem::JobHandle::Release(fromHandle(handle));
    }

    void* JobSystem_CombineDependencies(void** handles, int count)
    {
        if (count <= 0 || !handles)
            return nullptr;
        std::vector<JobSystem::JobHandle> vec;
        vec.reserve(count);
        for (int i = 0; i < count; ++i)
        {
            if (handles[i])
                vec.emplace_back(fromHandle(handles[i]), true);
        }
        auto combined = JobSystem::JobHandle::CombineDependencies(vec);
        return toHandle(combined);
    }

    void* JobSystem_ScheduleChunkJob(
        ChunkJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const ChunkJobData* chunks,
        int chunkCount,
        void* dependency)
    {
        // 委托给 JobSystem.cpp 中的完整实现
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto handle = JobSystem::Scheduler::ScheduleChunks(func, context, cleanup, chunks, chunkCount, dep, JobSystem::ChunkScheduleMode::PublishAssist, 0, 0);
        return toHandle(handle);
    }

    void* JobSystem_ScheduleChunkJobEx(
        ChunkJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const ChunkJobData* chunks,
        int chunkCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        uint32_t unitGeneration)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto mode = JobSystem::ChunkScheduleMode::PublishAssist;
        if (scheduleMode == 0)
            mode = JobSystem::ChunkScheduleMode::PublishNoAssist;
        else if (scheduleMode == 2)
            mode = JobSystem::ChunkScheduleMode::DeferTinyOnly;
        else if (scheduleMode == 3)
            mode = JobSystem::ChunkScheduleMode::ImmediateNative;
        else if (scheduleMode == 4)
            mode = JobSystem::ChunkScheduleMode::DeferredPublish;
        else if (scheduleMode == 5)
            mode = JobSystem::ChunkScheduleMode::DeferredPublishNoAssist;
        auto handle = JobSystem::Scheduler::ScheduleChunks(func, context, cleanup, chunks, chunkCount, dep, mode, workerCap, rangeSize, unitGeneration);
        return toHandle(handle);
    }

    void* JobSystem_ScheduleChunkRangeJobEx(
        ChunkRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const ChunkJobData* chunks,
        int chunkCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        uint32_t unitGeneration)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto mode = JobSystem::ChunkScheduleMode::PublishAssist;
        if (scheduleMode == 0)
            mode = JobSystem::ChunkScheduleMode::PublishNoAssist;
        else if (scheduleMode == 2)
            mode = JobSystem::ChunkScheduleMode::DeferTinyOnly;
        else if (scheduleMode == 3)
            mode = JobSystem::ChunkScheduleMode::ImmediateNative;
        else if (scheduleMode == 4)
            mode = JobSystem::ChunkScheduleMode::DeferredPublish;
        else if (scheduleMode == 5)
            mode = JobSystem::ChunkScheduleMode::DeferredPublishNoAssist;
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(func, context, cleanup, chunks, chunkCount, dep, mode, workerCap, rangeSize, unitGeneration);
        return toHandle(handle);
    }

    void* JobSystem_ScheduleEntityBatchJobEx(
        EntityBatchRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const EntityBatchData* batches,
        int batchCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        int jobKind,
        uint32_t unitGeneration)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto mode = JobSystem::ChunkScheduleMode::PublishAssist;
        if (scheduleMode == 0)
            mode = JobSystem::ChunkScheduleMode::PublishNoAssist;
        else if (scheduleMode == 2)
            mode = JobSystem::ChunkScheduleMode::DeferTinyOnly;
        else if (scheduleMode == 3)
            mode = JobSystem::ChunkScheduleMode::ImmediateNative;
        else if (scheduleMode == 4)
            mode = JobSystem::ChunkScheduleMode::DeferredPublish;
        else if (scheduleMode == 5)
            mode = JobSystem::ChunkScheduleMode::DeferredPublishNoAssist;
        const auto kind = jobKind == 0
            ? JobSystem::EcsJobKind::Chunk
            : JobSystem::EcsJobKind::Entity;
        auto handle = JobSystem::Scheduler::ScheduleEntityBatches(func, context, cleanup, batches, batchCount, dep, mode, workerCap, rangeSize, kind, unitGeneration);
        return toHandle(handle);
    }

    void* JobSystem_ScheduleAndCompleteEntityBatchJobEx(
        EntityBatchRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const EntityBatchData* batches,
        int batchCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        int jobKind,
        uint32_t unitGeneration)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto mode = JobSystem::ChunkScheduleMode::PublishAssist;
        if (scheduleMode == 0)
            mode = JobSystem::ChunkScheduleMode::PublishNoAssist;
        else if (scheduleMode == 2)
            mode = JobSystem::ChunkScheduleMode::DeferTinyOnly;
        else if (scheduleMode == 3)
            mode = JobSystem::ChunkScheduleMode::ImmediateNative;
        else if (scheduleMode == 4)
            mode = JobSystem::ChunkScheduleMode::DeferredPublish;
        else if (scheduleMode == 5)
            mode = JobSystem::ChunkScheduleMode::DeferredPublishNoAssist;
        // 一步完成 Schedule+Complete，消除 P/Invoke 往返
        // workers 还在上下文切换中，主线程已经进入 assist
        const auto kind = jobKind == 0
            ? JobSystem::EcsJobKind::Chunk
            : JobSystem::EcsJobKind::Entity;
        auto handle = JobSystem::Scheduler::ScheduleEntityBatches(func, context, cleanup, batches, batchCount, dep, mode, workerCap, rangeSize, kind, unitGeneration);
        handle.Complete();
        return toHandle(handle);
    }

    uint32_t JobSystem_GetStatsSize()
    {
        // 布局防御：必须与 C# NativeJobSystemStats（Marshal.SizeOf）相等。
        // 新增统计字段时若不同步，C# GetStats 会越界写 → 堆损坏。
        return static_cast<uint32_t>(sizeof(JobSystemStatsNative));
    }

    void JobSystem_GetStats(JobSystemStatsNative* stats)
    {
        if (!stats) return;
        JobSystem::JobSystemStatsSnapshot snapshot{};
        JobSystem::GetStatsSnapshot(&snapshot);
        stats->completeWaitLoops = snapshot.completeWaitLoops;
        stats->assistAttempts = snapshot.assistAttempts;
        stats->assistExecuted = snapshot.assistExecuted;
        stats->frameTasksSubmitted = snapshot.frameTasksSubmitted;
        stats->frameTasksCompleted = snapshot.frameTasksCompleted;
        stats->workerExecutedRanges = snapshot.workerExecutedRanges;
        stats->mainExecutedRanges = snapshot.mainExecutedRanges;
        stats->stealCount = snapshot.stealCount;
        stats->parkWakeCount = snapshot.parkWakeCount;
        stats->deferredRuns = snapshot.deferredRuns;
        stats->publishedJobs = snapshot.publishedJobs;
        stats->prewakeCount = snapshot.prewakeCount;
        stats->hotSpinHits = snapshot.hotSpinHits;
        stats->waitFallbacks = snapshot.waitFallbacks;
        stats->notifiedWorkers = snapshot.notifiedWorkers;
        stats->workerClaimedTokens = snapshot.workerClaimedTokens;
        stats->mainClaimedTokens = snapshot.mainClaimedTokens;
        stats->coldBatches = snapshot.coldBatches;
        stats->activeWorkersPeak = snapshot.activeWorkersPeak;
        stats->wakeLatencyEwmaNs = snapshot.wakeLatencyEwmaNs;
        stats->scheduleModePublishNoAssist = snapshot.scheduleModePublishNoAssist;
        stats->scheduleModePublishAssist = snapshot.scheduleModePublishAssist;
        stats->scheduleModeDeferTinyOnly = snapshot.scheduleModeDeferTinyOnly;
        stats->scheduleModeImmediateNative = snapshot.scheduleModeImmediateNative;
        stats->scheduleModeDeferredPublish = snapshot.scheduleModeDeferredPublish;
        stats->scheduleModeDeferredPublishNoAssist = snapshot.scheduleModeDeferredPublishNoAssist;
        stats->frameQueueDepthPeak = snapshot.frameQueueDepthPeak;
        stats->directAssistClaims = snapshot.directAssistClaims;
        stats->exhaustedTickets = snapshot.exhaustedTickets;
        stats->scheduleToPublishEwmaNs = snapshot.scheduleToPublishEwmaNs;
        stats->publishToFirstMainClaimEwmaNs = snapshot.publishToFirstMainClaimEwmaNs;
        stats->publishToFirstWorkerClaimEwmaNs = snapshot.publishToFirstWorkerClaimEwmaNs;
        stats->publishToCompletionEwmaNs = snapshot.publishToCompletionEwmaNs;
        stats->queueLockWaitEwmaNs = snapshot.queueLockWaitEwmaNs;
        stats->perRangeExecEwmaNs = snapshot.perRangeExecEwmaNs;
        stats->assistExecPctEwma = snapshot.assistExecPctEwma;
        stats->completionOverheadUs = snapshot.completionOverheadUs;
        stats->workerTargetTotal = snapshot.workerTargetTotal;
        stats->totalTilesPublished = snapshot.totalTilesPublished;
        stats->localTiles = snapshot.localTiles;
        stats->stolenTiles = snapshot.stolenTiles;
        stats->assistTiles = snapshot.assistTiles;
        stats->stealAttempts = snapshot.stealAttempts;
        stats->stealSuccesses = snapshot.stealSuccesses;
        stats->permitsReleased = snapshot.permitsReleased;
        stats->victimScans = snapshot.victimScans;
        stats->stealEmptyExits = snapshot.stealEmptyExits;
        stats->batchStorageCreated = snapshot.batchStorageCreated;
        stats->batchStorageReused = snapshot.batchStorageReused;
        stats->batchStorageReturned = snapshot.batchStorageReturned;
        stats->batchStorageDropped = snapshot.batchStorageDropped;
        stats->submitToFirstWorkerEwmaNs = snapshot.submitToFirstWorkerEwmaNs;
        stats->workerStartSpreadEwmaNs = snapshot.workerStartSpreadEwmaNs;
        stats->lastTileToTopologyDoneEwmaNs = snapshot.lastTileToTopologyDoneEwmaNs;
        stats->completeWakeToReturnEwmaNs = snapshot.completeWakeToReturnEwmaNs;
        stats->nativeBatches = snapshot.nativeBatches;
        stats->invalidBackendSelections = snapshot.invalidBackendSelections;
        stats->timingSampleCount = snapshot.timingSampleCount;
        stats->timingSamplesDropped = snapshot.timingSamplesDropped;
        stats->batchTotalP50Ns = snapshot.batchTotalP50Ns;
        stats->batchTotalP95Ns = snapshot.batchTotalP95Ns;
        stats->batchTotalP99Ns = snapshot.batchTotalP99Ns;
        stats->batchTotalMaxNs = snapshot.batchTotalMaxNs;
        stats->submitToFirstWorkerP50Ns = snapshot.submitToFirstWorkerP50Ns;
        stats->submitToFirstWorkerP95Ns = snapshot.submitToFirstWorkerP95Ns;
        stats->submitToFirstWorkerP99Ns = snapshot.submitToFirstWorkerP99Ns;
        stats->submitToFirstWorkerMaxNs = snapshot.submitToFirstWorkerMaxNs;
        stats->workerStartSpreadP50Ns = snapshot.workerStartSpreadP50Ns;
        stats->workerStartSpreadP95Ns = snapshot.workerStartSpreadP95Ns;
        stats->workerStartSpreadP99Ns = snapshot.workerStartSpreadP99Ns;
        stats->workerStartSpreadMaxNs = snapshot.workerStartSpreadMaxNs;
        stats->executionSpanP50Ns = snapshot.executionSpanP50Ns;
        stats->executionSpanP95Ns = snapshot.executionSpanP95Ns;
        stats->executionSpanP99Ns = snapshot.executionSpanP99Ns;
        stats->executionSpanMaxNs = snapshot.executionSpanMaxNs;
        stats->maxRangeP50Ns = snapshot.maxRangeP50Ns;
        stats->maxRangeP95Ns = snapshot.maxRangeP95Ns;
        stats->maxRangeP99Ns = snapshot.maxRangeP99Ns;
        stats->maxRangeMaxNs = snapshot.maxRangeMaxNs;
        stats->slowBatchId = snapshot.slowBatchId;
        stats->slowBatchTotalNs = snapshot.slowBatchTotalNs;
        stats->slowSubmitToFirstWorkerNs = snapshot.slowSubmitToFirstWorkerNs;
        stats->slowWorkerStartSpreadNs = snapshot.slowWorkerStartSpreadNs;
        stats->slowExecutionSpanNs = snapshot.slowExecutionSpanNs;
        stats->slowMaxRangeNs = snapshot.slowMaxRangeNs;
        stats->slowCoreMigrations = snapshot.slowCoreMigrations;
        stats->slowAssistTiles = snapshot.slowAssistTiles;
        stats->slowRangeThreadCpuNs = snapshot.slowRangeThreadCpuNs;
        stats->slowRangeThreadCycles = snapshot.slowRangeThreadCycles;
        stats->slowBatchMinRangeThreadCycles = snapshot.slowBatchMinRangeThreadCycles;
        stats->slowBatchAverageRangeThreadCycles = snapshot.slowBatchAverageRangeThreadCycles;
        stats->slowRangeIndex = snapshot.slowRangeIndex;
        stats->slowRangeWorker = snapshot.slowRangeWorker;
        stats->slowRangeStartLogicalCore = snapshot.slowRangeStartLogicalCore;
        stats->slowRangeEndLogicalCore = snapshot.slowRangeEndLogicalCore;
        stats->slowRangeStartPhysicalCore = snapshot.slowRangeStartPhysicalCore;
        stats->slowRangeEndPhysicalCore = snapshot.slowRangeEndPhysicalCore;
    }

    void JobSystem_ResetStats()
    {
        JobSystem::ResetStatsSnapshot();
    }

    void JobSystem_SetTimingDiagnostics(int enabled)
    {
        JobSystem::SetTimingDiagnosticsEnabled(enabled != 0);
    }

    // 主线程 assist 运行时开关。默认关闭，由 API 控制。
    // g_mainThreadAssistEnabled 声明于 JobSystemInternal.h（namespace JobSystem 内）
    void JobSystem_SetMainThreadAssist(int enabled)
    {
        JobSystem::g_mainThreadAssistEnabled = enabled != 0;
    }

    // CPU 亲和性运行时开关：立即应用到主线程 + 所有 worker。
    void JobSystem_SetWorkerAffinity(int enabled)
    {
        JobSystem::g_workerAffinityEnabled.store(
            enabled != 0, std::memory_order_relaxed);
        // worker：遍历已启动线程设置/清除亲和性
        if (JobSystem::g_chaseLevScheduler)
            JobSystem::g_chaseLevScheduler->ApplyAffinity(enabled != 0);
        // 主线程：绑定核心 0 或清除
#if defined(_WIN32)
        if (enabled)
            JobSystem::BindCurrentThreadToLogicalProcessor(0);
        else
            JobSystem::ClearCurrentThreadAffinity();
#endif
    }

    // ======================== Profiler API ========================

    void JobProfiler_SetEnabled(int enabled)
    {
        g_profilerEnabled.store(enabled != 0, std::memory_order_release);
        if (!enabled) {
            g_profilerBuffer.Clear();
        }
    }

    int JobProfiler_IsEnabled()
    {
        return g_profilerEnabled.load(std::memory_order_acquire) ? 1 : 0;
    }

    int JobProfiler_ReadAll(struct ProfilerEntry* buffer, int maxCount)
    {
        if (!buffer || maxCount <= 0) return 0;
        return static_cast<int>(g_profilerBuffer.ReadAll(static_cast<size_t>(maxCount), buffer));
    }

    void JobProfiler_Clear()
    {
        g_profilerBuffer.Clear();
    }

    void Trace_SetEnabled(int enabled)
    {
        JobSystem::TraceSetEnabled(enabled != 0);
    }

    int Trace_IsEnabled()
    {
        return JobSystem::TraceIsEnabled() ? 1 : 0;
    }

    int Trace_ReadAll(JobSystem::TraceEvent* buffer, int maxCount)
    {
        return JobSystem::TraceReadAll(buffer, maxCount);
    }

    uint64_t Trace_DroppedEvents()
    {
        return JobSystem::TraceDroppedEvents();
    }

    void Trace_Clear()
    {
        JobSystem::TraceClear();
    }

} // extern "C"
