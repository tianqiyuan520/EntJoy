#include "Exports.h"
#include "JobSystem.h"
#include "ChunkJobData.h"
#include "EntityBatchData.h"
#include "JobProfiler.h"

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

    void JobSystem_Shutdown()
    {
        JobSystem::Scheduler::Shutdown();
    }

    void JobSystem_PrewakeWorkers()
    {
        JobSystem::Scheduler::PrewakeWorkers();
    }

    void JobSystem_KeepWorkersWarm(int microseconds)
    {
        JobSystem::Scheduler::KeepWorkersWarm(microseconds);
    }

    void JobSystem_FlushScheduledJobs()
    {
        JobSystem::Scheduler::FlushScheduledJobs();
    }

    void* JobSystem_Schedule(JobFunc func, void* context, ContextCleanupFunc cleanup, void* dependency)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto handle = JobSystem::Scheduler::Schedule(func, context, cleanup, dep);
        return toHandle(handle);
    }

    void* JobSystem_ScheduleParallelFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup,
        int length, int batchSize, void* dependency)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto handle = JobSystem::Scheduler::ScheduleParallelFor(func, context, length, batchSize, cleanup, dep);
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
        // 委托给 JobSystem.cpp 中的完整实现（包含 taskflow 头文件）
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
        int rangeSize)
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
        auto handle = JobSystem::Scheduler::ScheduleChunks(func, context, cleanup, chunks, chunkCount, dep, mode, workerCap, rangeSize);
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
        int rangeSize)
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
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(func, context, cleanup, chunks, chunkCount, dep, mode, workerCap, rangeSize);
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
        int rangeSize)
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
        auto handle = JobSystem::Scheduler::ScheduleEntityBatches(func, context, cleanup, batches, batchCount, dep, mode, workerCap, rangeSize);
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
        int rangeSize)
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
        auto handle = JobSystem::Scheduler::ScheduleEntityBatches(func, context, cleanup, batches, batchCount, dep, mode, workerCap, rangeSize);
        handle.Complete();
        return toHandle(handle);
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
    }

    void JobSystem_ResetStats()
    {
        JobSystem::ResetStatsSnapshot();
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
