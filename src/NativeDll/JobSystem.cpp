#include "JobSystem.h"
#include "ChunkJobData.h"
#include "EntityBatchData.h"
#include "JobProfiler.h"
#include "../../external/cpp-taskflow/taskflow/taskflow.hpp"

#include <algorithm>
#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#endif
#include <thread>
#include <utility>
#include <atomic>
#include <memory>
#include <deque>
#include <condition_variable>
#include <cstdint>
#include <chrono>

// ---------- 跨平台线程优先级 ----------
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <pthread.h>
#include <sched.h>
#include <sys/resource.h>
#endif

namespace JobSystem
{
    // ---------- 可调参数 ----------
    constexpr size_t kMaxPooledStates = 4096;
    constexpr size_t kMaxPooledTaskflows = 1024;

    constexpr int kSyncExecutionLengthThreshold = 512;
    constexpr int kSyncWithCompletedDepThreshold = 4096;
    constexpr int kSpinBeforeWait = 256;
    constexpr int kWorkerIdleSpin = 128;
    constexpr auto kChunkWorkerHotWait = std::chrono::microseconds(2000);

    // 全局资源
    std::mutex g_executorMutex;
    std::shared_ptr<tf::Executor> g_executor;
    int g_numThreads = 0;

    std::mutex g_statePoolMutex;
    std::vector<HandleState*> g_statePool;

    std::mutex g_taskflowPoolMutex;
    std::vector<tf::Taskflow*> g_taskflowPool;

    std::atomic<uint64_t> g_completeWaitLoops{ 0 };
    std::atomic<uint64_t> g_assistAttempts{ 0 };
    std::atomic<uint64_t> g_assistExecuted{ 0 };
    std::atomic<uint64_t> g_frameTasksSubmitted{ 0 };
    std::atomic<uint64_t> g_frameTasksCompleted{ 0 };
    std::atomic<uint64_t> g_workerExecutedRanges{ 0 };
    std::atomic<uint64_t> g_mainExecutedRanges{ 0 };
    std::atomic<uint64_t> g_stealCount{ 0 };
    std::atomic<uint64_t> g_parkWakeCount{ 0 };
    std::atomic<uint64_t> g_deferredRuns{ 0 };
    std::atomic<uint64_t> g_publishedJobs{ 0 };
    std::atomic<uint64_t> g_prewakeCount{ 0 };
    std::atomic<uint64_t> g_hotSpinHits{ 0 };
    std::atomic<uint64_t> g_waitFallbacks{ 0 };
    std::atomic<uint64_t> g_notifiedWorkers{ 0 };
    std::atomic<uint64_t> g_workerClaimedTokens{ 0 };
    std::atomic<uint64_t> g_mainClaimedTokens{ 0 };
    std::atomic<uint64_t> g_coldBatches{ 0 };
    std::atomic<uint64_t> g_activeWorkersPeak{ 0 };
    std::atomic<int64_t> g_wakeLatencyEwmaNs{ 300'000 };
    std::atomic<uint64_t> g_scheduleModePublishNoAssist{ 0 };
    std::atomic<uint64_t> g_scheduleModePublishAssist{ 0 };
    std::atomic<uint64_t> g_scheduleModeDeferTinyOnly{ 0 };
    std::atomic<uint64_t> g_scheduleModeImmediateNative{ 0 };
    std::atomic<uint64_t> g_scheduleModeDeferredPublish{ 0 };
    std::atomic<uint64_t> g_scheduleModeDeferredPublishNoAssist{ 0 };
    std::atomic<int> g_frameQueueDepthPeak{ 0 };
    std::atomic<bool> g_shuttingDown{ false };
    std::atomic<bool> g_frameLowLatencyMode{ false };
    std::atomic<int64_t> g_lastTaskflowScheduleNs{ 0 };

    void UpdateQueueDepthPeak(int value) noexcept
    {
        int observed = g_frameQueueDepthPeak.load(std::memory_order_relaxed);
        while (value > observed &&
            !g_frameQueueDepthPeak.compare_exchange_weak(observed, value, std::memory_order_relaxed))
        {
        }
    }

    void GetStatsSnapshot(JobSystemStatsSnapshot* stats) noexcept
    {
        if (!stats) return;
        stats->completeWaitLoops = g_completeWaitLoops.load(std::memory_order_relaxed);
        stats->assistAttempts = g_assistAttempts.load(std::memory_order_relaxed);
        stats->assistExecuted = g_assistExecuted.load(std::memory_order_relaxed);
        stats->frameTasksSubmitted = g_frameTasksSubmitted.load(std::memory_order_relaxed);
        stats->frameTasksCompleted = g_frameTasksCompleted.load(std::memory_order_relaxed);
        stats->workerExecutedRanges = g_workerExecutedRanges.load(std::memory_order_relaxed);
        stats->mainExecutedRanges = g_mainExecutedRanges.load(std::memory_order_relaxed);
        stats->stealCount = g_stealCount.load(std::memory_order_relaxed);
        stats->parkWakeCount = g_parkWakeCount.load(std::memory_order_relaxed);
        stats->deferredRuns = g_deferredRuns.load(std::memory_order_relaxed);
        stats->publishedJobs = g_publishedJobs.load(std::memory_order_relaxed);
        stats->prewakeCount = g_prewakeCount.load(std::memory_order_relaxed);
        stats->hotSpinHits = g_hotSpinHits.load(std::memory_order_relaxed);
        stats->waitFallbacks = g_waitFallbacks.load(std::memory_order_relaxed);
        stats->notifiedWorkers = g_notifiedWorkers.load(std::memory_order_relaxed);
        stats->workerClaimedTokens = g_workerClaimedTokens.load(std::memory_order_relaxed);
        stats->mainClaimedTokens = g_mainClaimedTokens.load(std::memory_order_relaxed);
        stats->coldBatches = g_coldBatches.load(std::memory_order_relaxed);
        stats->activeWorkersPeak = g_activeWorkersPeak.load(std::memory_order_relaxed);
        stats->wakeLatencyEwmaNs = static_cast<uint64_t>(g_wakeLatencyEwmaNs.load(std::memory_order_relaxed));
        stats->scheduleModePublishNoAssist = g_scheduleModePublishNoAssist.load(std::memory_order_relaxed);
        stats->scheduleModePublishAssist = g_scheduleModePublishAssist.load(std::memory_order_relaxed);
        stats->scheduleModeDeferTinyOnly = g_scheduleModeDeferTinyOnly.load(std::memory_order_relaxed);
        stats->scheduleModeImmediateNative = g_scheduleModeImmediateNative.load(std::memory_order_relaxed);
        stats->scheduleModeDeferredPublish = g_scheduleModeDeferredPublish.load(std::memory_order_relaxed);
        stats->scheduleModeDeferredPublishNoAssist = g_scheduleModeDeferredPublishNoAssist.load(std::memory_order_relaxed);
        stats->frameQueueDepthPeak = g_frameQueueDepthPeak.load(std::memory_order_relaxed);
    }

    void ResetStatsSnapshot() noexcept
    {
        g_completeWaitLoops.store(0, std::memory_order_relaxed);
        g_assistAttempts.store(0, std::memory_order_relaxed);
        g_assistExecuted.store(0, std::memory_order_relaxed);
        g_frameTasksSubmitted.store(0, std::memory_order_relaxed);
        g_frameTasksCompleted.store(0, std::memory_order_relaxed);
        g_workerExecutedRanges.store(0, std::memory_order_relaxed);
        g_mainExecutedRanges.store(0, std::memory_order_relaxed);
        g_stealCount.store(0, std::memory_order_relaxed);
        g_parkWakeCount.store(0, std::memory_order_relaxed);
        g_deferredRuns.store(0, std::memory_order_relaxed);
        g_publishedJobs.store(0, std::memory_order_relaxed);
        g_prewakeCount.store(0, std::memory_order_relaxed);
        g_hotSpinHits.store(0, std::memory_order_relaxed);
        g_waitFallbacks.store(0, std::memory_order_relaxed);
        g_notifiedWorkers.store(0, std::memory_order_relaxed);
        g_workerClaimedTokens.store(0, std::memory_order_relaxed);
        g_mainClaimedTokens.store(0, std::memory_order_relaxed);
        g_coldBatches.store(0, std::memory_order_relaxed);
        g_activeWorkersPeak.store(0, std::memory_order_relaxed);
        g_scheduleModePublishNoAssist.store(0, std::memory_order_relaxed);
        g_scheduleModePublishAssist.store(0, std::memory_order_relaxed);
        g_scheduleModeDeferTinyOnly.store(0, std::memory_order_relaxed);
        g_scheduleModeImmediateNative.store(0, std::memory_order_relaxed);
        g_scheduleModeDeferredPublish.store(0, std::memory_order_relaxed);
        g_scheduleModeDeferredPublishNoAssist.store(0, std::memory_order_relaxed);
        g_frameQueueDepthPeak.store(0, std::memory_order_relaxed);
    }

    // ---------- 内部函数实现（非匿名，供 Exports.cpp 等 TU 使用） ----------

    void RecycleState(HandleState* state) noexcept
    {
        if (!state) return;
#ifdef _DEBUG
        state->inPool.store(true, std::memory_order_relaxed);
#endif
        state->assist.callback.store(nullptr, std::memory_order_release);
        state->assist.context.store(nullptr, std::memory_order_release);
        state->assist.readersDrained.store(nullptr, std::memory_order_release);
        state->assist.readers.store(0, std::memory_order_relaxed);
        state->pendingCallback.store(nullptr, std::memory_order_release);
        state->pendingContext.store(nullptr, std::memory_order_release);
        state->inlineContinuation = {};
        state->continuations.clear();
        state->waiterCount.store(0, std::memory_order_relaxed);
        state->completed.store(false, std::memory_order_relaxed);
        state->refCount.store(1, std::memory_order_relaxed);

        std::lock_guard<std::mutex> lock(g_statePoolMutex);
        if (g_statePool.size() < kMaxPooledStates)
            g_statePool.push_back(state);
        else
            delete state;
    }

    HandleState* CreateState(bool completed)
    {
        HandleState* state = nullptr;
        {
            std::lock_guard<std::mutex> lock(g_statePoolMutex);
            if (!g_statePool.empty())
            {
                state = g_statePool.back();
                g_statePool.pop_back();
            }
        }
        if (!state) state = new HandleState(completed);
        state->refCount.store(1, std::memory_order_relaxed);
        state->completed.store(completed, std::memory_order_relaxed);
        state->waiterCount.store(0, std::memory_order_relaxed);
        state->assist.callback.store(nullptr, std::memory_order_release);
        state->assist.context.store(nullptr, std::memory_order_release);
        state->assist.readersDrained.store(nullptr, std::memory_order_release);
        state->assist.readers.store(0, std::memory_order_relaxed);
        state->pendingCallback.store(nullptr, std::memory_order_release);
        state->pendingContext.store(nullptr, std::memory_order_release);
        state->inlineContinuation = {};
        state->continuations.clear();
#ifdef _DEBUG
        state->inPool.store(false, std::memory_order_relaxed);
        state->generation.fetch_add(1, std::memory_order_relaxed);
#endif
        return state;
    }

    void AcquireState(HandleState* state) noexcept
    {
        if (state) state->refCount.fetch_add(1, std::memory_order_relaxed);
    }

    void ReleaseState(HandleState* state) noexcept
    {
        if (state && state->refCount.fetch_sub(1, std::memory_order_acq_rel) == 1)
            RecycleState(state);
    }

    void CompleteState(HandleState* state)
    {
        if (!state) return;
        std::function<void()> inlineContinuation;
        std::vector<std::function<void()>> continuations;
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            if (state->completed.exchange(true, std::memory_order_acq_rel))
                return;
            state->assist.callback.store(nullptr, std::memory_order_release);
            state->assist.context.store(nullptr, std::memory_order_release);
            state->pendingCallback.store(nullptr, std::memory_order_release);
            state->pendingContext.store(nullptr, std::memory_order_release);
            inlineContinuation = std::move(state->inlineContinuation);
            continuations.swap(state->continuations);
        }

        state->completed.notify_all();

        if (inlineContinuation)
        {
            try { inlineContinuation(); }
            catch (...) {}
        }
        for (auto& cont : continuations)
        {
            if (cont) { try { cont(); } catch (...) {} }
        }
    }

    void AddContinuationOrRunNow(HandleState* state, std::function<void()> continuation)
    {
        if (!state || state->completed.load(std::memory_order_acquire))
        {
            if (continuation) continuation();
            return;
        }
        std::function<void()> toRun;
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            if (state->completed.load(std::memory_order_acquire))
                toRun = std::move(continuation);
            else if (!state->inlineContinuation)
                state->inlineContinuation = std::move(continuation);
            else
                state->continuations.emplace_back(std::move(continuation));
        }
        if (toRun) toRun();
    }

    std::shared_ptr<tf::Executor> EnsureExecutor()
    {
        std::lock_guard<std::mutex> lock(g_executorMutex);
        if (!g_executor)
        {
            g_numThreads = ResolveWorkerCount(0);
            g_executor = std::make_shared<tf::Executor>(static_cast<size_t>(g_numThreads));
        }
        return g_executor;
    }

    // 统一使用全部逻辑核心
    // 所有进程实例都使用 hardware_concurrency() 个线程
    // 由操作系统调度器负责在多个进程间分配 CPU 时间
    int ResolveWorkerCount(int numThreads)
    {
        if (numThreads > 0) return numThreads;
        const unsigned int hardwareThreads = std::thread::hardware_concurrency();
        // 默认预留 1 个逻辑核给主线程/系统/录屏/音频等后台任务，减少复杂环境下的过度抢占。
        constexpr unsigned int kReservedThreads = 1;
        if (hardwareThreads <= 2) return 1;
        const unsigned int workerThreads = (hardwareThreads > kReservedThreads)
            ? (hardwareThreads - kReservedThreads)
            : 1u;
        return static_cast<int>(std::max(1u, workerThreads));
    }

    int CurrentWorkerCount()
    {
        std::lock_guard<std::mutex> lock(g_executorMutex);
        if (g_numThreads > 0) return g_numThreads;
        return ResolveWorkerCount(0);
    }

    namespace
    {
        using JobSystem::CreateState;
        using JobSystem::AcquireState;
        using JobSystem::ReleaseState;
        using JobSystem::CompleteState;
        using JobSystem::AddContinuationOrRunNow;
        using JobSystem::EnsureExecutor;

        void ReturnTaskflow(tf::Taskflow* taskflow) noexcept
        {
            if (!taskflow) return;
            taskflow->clear();
            std::lock_guard<std::mutex> lock(g_taskflowPoolMutex);
            if (g_taskflowPool.size() < kMaxPooledTaskflows)
                g_taskflowPool.push_back(taskflow);
            else
                delete taskflow;
        }

        std::shared_ptr<tf::Taskflow> AcquireTaskflow()
        {
            tf::Taskflow* taskflow = nullptr;
            {
                std::lock_guard<std::mutex> lock(g_taskflowPoolMutex);
                if (!g_taskflowPool.empty())
                {
                    taskflow = g_taskflowPool.back();
                    g_taskflowPool.pop_back();
                }
            }
            if (!taskflow) taskflow = new tf::Taskflow();
            return std::shared_ptr<tf::Taskflow>(taskflow, [](tf::Taskflow* ptr) { ReturnTaskflow(ptr); });
        }

        struct ChunkBatchState
        {
            void (*func)(void*, const ChunkJobData*) { nullptr };
            void (*rangeFunc)(void*, const ChunkJobData*, int, int) { nullptr };
            void (*entityRangeFunc)(void*, const EntityBatchData*, int, int) { nullptr };
            void* context{ nullptr };
            void (*cleanup)(void*) { nullptr };
            const ChunkJobData* chunks{ nullptr };
            const EntityBatchData* entityBatches{ nullptr };
            int chunkCount{ 0 };
            int rangeSize{ 1 };
            int rangeCount{ 0 };
            bool enableAssist{ false };
            ChunkScheduleMode mode{ ChunkScheduleMode::PublishAssist };
            HandleState* handleState{ nullptr };
            int workerTarget{ 1 };
            int workerCap{ 0 };
            int rangeSizeOverride{ 0 };
            bool coldStart{ false };
            int tokenCount{ 0 };

            std::atomic<int> completedRanges{ 0 };
            std::atomic<int> queueTokens{ 0 };
            std::atomic<int> unclaimedTokens{ 0 };
            std::atomic<int> activeWorkers{ 0 };
            std::atomic<int64_t> publishNs{ 0 };
            std::atomic<int64_t> firstWorkerStartNs{ 0 };
            std::atomic<int64_t> firstAssistShardNs{ 0 };
            std::atomic<int> assistReaders{ 0 };
            std::atomic<int> scheduleState{ 0 }; // 0=pending, 1=claimed by Complete, 2=published to workers
            std::atomic<bool> cleanupDone{ false };
            std::atomic<bool> returnedToPool{ false };
        };

        constexpr size_t kMaxPooledChunkBatches = 1024;
        std::mutex g_chunkBatchPoolMutex;
        std::vector<ChunkBatchState*> g_chunkBatchPool;

        ChunkBatchState* AcquireChunkBatchState() noexcept
        {
            ChunkBatchState* batch = nullptr;
            {
                std::lock_guard<std::mutex> lock(g_chunkBatchPoolMutex);
                if (!g_chunkBatchPool.empty())
                {
                    batch = g_chunkBatchPool.back();
                    g_chunkBatchPool.pop_back();
                }
            }
            if (!batch) batch = new ChunkBatchState();
            batch->func = nullptr;
            batch->rangeFunc = nullptr;
            batch->entityRangeFunc = nullptr;
            batch->context = nullptr;
            batch->cleanup = nullptr;
            batch->chunks = nullptr;
            batch->entityBatches = nullptr;
            batch->chunkCount = 0;
            batch->rangeSize = 1;
            batch->rangeCount = 0;
            batch->enableAssist = false;
            batch->mode = ChunkScheduleMode::PublishAssist;
            batch->handleState = nullptr;
            batch->workerTarget = 1;
            batch->workerCap = 0;
            batch->rangeSizeOverride = 0;
            batch->coldStart = false;
            batch->tokenCount = 0;
            batch->completedRanges.store(0, std::memory_order_relaxed);
            batch->queueTokens.store(0, std::memory_order_relaxed);
            batch->unclaimedTokens.store(0, std::memory_order_relaxed);
            batch->activeWorkers.store(0, std::memory_order_relaxed);
            batch->publishNs.store(0, std::memory_order_relaxed);
            batch->firstWorkerStartNs.store(0, std::memory_order_relaxed);
            batch->firstAssistShardNs.store(0, std::memory_order_relaxed);
            batch->assistReaders.store(0, std::memory_order_relaxed);
            batch->scheduleState.store(0, std::memory_order_relaxed);
            batch->cleanupDone.store(false, std::memory_order_relaxed);
            batch->returnedToPool.store(false, std::memory_order_relaxed);
            return batch;
        }

        void ReleaseChunkBatchState(ChunkBatchState* batch) noexcept
        {
            if (!batch) return;
            if (batch->handleState)
            {
                ReleaseState(batch->handleState);
                batch->handleState = nullptr;
            }
            std::lock_guard<std::mutex> lock(g_chunkBatchPoolMutex);
            if (g_chunkBatchPool.size() < kMaxPooledChunkBatches)
                g_chunkBatchPool.push_back(batch);
            else
                delete batch;
        }

        void TryReleaseChunkBatchState(ChunkBatchState* batch) noexcept
        {
            if (!batch) return;
            if (!batch->cleanupDone.load(std::memory_order_acquire) ||
                batch->queueTokens.load(std::memory_order_acquire) != 0 ||
                batch->assistReaders.load(std::memory_order_acquire) != 0 ||
                (batch->handleState && batch->handleState->assist.readers.load(std::memory_order_acquire) != 0))
            {
                return;
            }

            if (!batch->returnedToPool.exchange(true, std::memory_order_acq_rel))
            {
                ReleaseChunkBatchState(batch);
            }
        }

        void OnChunkAssistReadersDrained(void* rawBatch) noexcept
        {
            TryReleaseChunkBatchState(static_cast<ChunkBatchState*>(rawBatch));
        }

        void FinalizeChunkBatch(ChunkBatchState* batch) noexcept;
        void CancelChunkBatch(ChunkBatchState* batch) noexcept;

        bool RunChunkRangeSpan(ChunkBatchState* batch, int firstRange, int lastRange) noexcept
        {
            if (!batch || (!batch->func && !batch->rangeFunc && !batch->entityRangeFunc)) return false;
            if (firstRange >= lastRange || firstRange >= batch->rangeCount) return false;
            lastRange = std::min(lastRange, batch->rangeCount);

            int completed = 0;
            for (int rangeIndex = firstRange; rangeIndex < lastRange; ++rangeIndex)
            {
                const int begin = rangeIndex * batch->rangeSize;
                const int end = std::min(batch->chunkCount, begin + batch->rangeSize);
                try
                {
                    if (batch->entityRangeFunc)
                    {
                        batch->entityRangeFunc(batch->context, batch->entityBatches, begin, end - begin);
                    }
                    else if (batch->rangeFunc)
                    {
                        batch->rangeFunc(batch->context, batch->chunks, begin, end - begin);
                    }
                    else
                    {
                        for (int chunkIndex = begin; chunkIndex < end; ++chunkIndex)
                        {
                            batch->func(batch->context, &batch->chunks[chunkIndex]);
                        }
                    }
                }
                catch (...)
                {
                    CancelChunkBatch(batch);
                    return false;
                }
                ++completed;
            }

            if (batch->completedRanges.fetch_add(completed, std::memory_order_acq_rel) + completed == batch->rangeCount)
            {
                FinalizeChunkBatch(batch);
            }
            return true;
        }

        struct ChunkQueueToken
        {
            ChunkBatchState* batch{ nullptr };
            int firstRange{ 0 };
            int lastRange{ 0 };
        };

        std::mutex g_chunkWorkerMutex;
        std::condition_variable g_chunkWorkerCv;
        std::deque<ChunkQueueToken> g_chunkRunnableBatches;
        std::atomic<int> g_chunkRunnableCount{ 0 };
        std::mutex g_pendingChunkMutex;
        std::deque<ChunkBatchState*> g_pendingChunkBatches;
        std::vector<std::thread> g_chunkWorkers;
        bool g_chunkWorkersShutdown = false;
        std::atomic<uint64_t> g_chunkPrewakeGeneration{ 0 };
        std::atomic<int64_t> g_chunkHotUntilNs{ 0 };
        std::atomic<int64_t> g_keepWarmUntilNs{ 0 };
        constexpr int64_t kKeepWarmSpinYieldThreshold = 50;
        std::atomic<int64_t> g_lastChunkCompletionNs{ 0 };

        inline void RelaxCpu() noexcept;
        void RunWorkerChunkToken(const ChunkQueueToken& token) noexcept;

        void UpdatePeak(std::atomic<uint64_t>& peak, uint64_t value) noexcept
        {
            uint64_t current = peak.load(std::memory_order_relaxed);
            while (value > current &&
                !peak.compare_exchange_weak(current, value, std::memory_order_relaxed))
            {
            }
        }

        void UpdateWakeLatencyEwma(int64_t sampleNs) noexcept
        {
            if (sampleNs <= 0) return;
            int64_t current = g_wakeLatencyEwmaNs.load(std::memory_order_relaxed);
            while (true)
            {
                const int64_t next = current + (sampleNs - current) / 8;
                if (g_wakeLatencyEwmaNs.compare_exchange_weak(
                    current, next, std::memory_order_relaxed))
                {
                    return;
                }
            }
        }

        void ClaimChunkToken(ChunkBatchState* batch, bool worker) noexcept
        {
            if (!batch) return;
            batch->unclaimedTokens.fetch_sub(1, std::memory_order_acq_rel);
            if (worker)
                g_workerClaimedTokens.fetch_add(1, std::memory_order_relaxed);
            else
                g_mainClaimedTokens.fetch_add(1, std::memory_order_relaxed);
        }

        void SetCurrentWorkerPriority() noexcept
        {
#ifdef _WIN32
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
#else
            setpriority(PRIO_PROCESS, 0, -5);
#endif
        }

        void FinishChunkQueueToken(ChunkBatchState* batch) noexcept
        {
            if (!batch) return;
            int previous = batch->queueTokens.fetch_sub(1, std::memory_order_acq_rel);
            if (previous <= 0)
            {
                batch->queueTokens.store(0, std::memory_order_release);
                return;
            }
            if (previous == 1)
            {
                TryReleaseChunkBatchState(batch);
            }
        }

        void FinalizeChunkBatch(ChunkBatchState* batch) noexcept
        {
            if (!batch) return;
            if (!batch->cleanupDone.exchange(true, std::memory_order_acq_rel))
            {
                if (batch->cleanup)
                {
                    try { batch->cleanup(batch->context); }
                    catch (...) {}
                }
                auto* state = batch->handleState;
                const auto now = std::chrono::steady_clock::now();
                g_lastChunkCompletionNs.store(
                    std::chrono::duration_cast<std::chrono::nanoseconds>(now.time_since_epoch()).count(),
                    std::memory_order_relaxed);
                CompleteState(state);
            }
            TryReleaseChunkBatchState(batch);
        }

        void CancelChunkBatch(ChunkBatchState* batch) noexcept
        {
            if (!batch) return;
            if (!batch->cleanupDone.exchange(true, std::memory_order_acq_rel))
            {
                if (batch->cleanup)
                {
                    try { batch->cleanup(batch->context); }
                    catch (...) {}
                }
                auto* state = batch->handleState;
                CompleteState(state);
            }
            TryReleaseChunkBatchState(batch);
        }

        void ChunkWorkerLoop(int workerIndex)
        {
            (void)workerIndex;
            SetCurrentWorkerPriority();
            uint64_t observedPrewake = g_chunkPrewakeGeneration.load(std::memory_order_relaxed);

            while (true)
            {
                ChunkQueueToken token{};

                for (int spin = 0; spin < kWorkerIdleSpin; ++spin)
                {
                    if (g_chunkRunnableCount.load(std::memory_order_acquire) > 0)
                    {
                        std::lock_guard<std::mutex> lock(g_chunkWorkerMutex);
                        if (!g_chunkRunnableBatches.empty())
                        {
                            token = g_chunkRunnableBatches.front();
                            g_chunkRunnableBatches.pop_front();
                            g_chunkRunnableCount.fetch_sub(1, std::memory_order_acq_rel);
                            break;
                        }
                    }
                    RelaxCpu();
                }

                if (!token.batch)
                {
                    // ——— 保温阶段 ———
                    // g_keepWarmUntilNs > 0 时 spin-yield 代替 CV park，
                    // 消除帧间 notify_one 唤醒延迟（~100 μs）。
                    int64_t warmDeadline = g_keepWarmUntilNs.load(std::memory_order_acquire);
                    if (warmDeadline > 0)
                    {
                        bool yielded = false;
                        for (int w = 0; ; ++w)
                        {
                            auto now = std::chrono::steady_clock::now();
                            if (now.time_since_epoch().count() >= warmDeadline) break;
                            if (g_chunkRunnableCount.load(std::memory_order_acquire) > 0)
                            {
                                std::lock_guard<std::mutex> lock(g_chunkWorkerMutex);
                                if (!g_chunkRunnableBatches.empty())
                                {
                                    token = g_chunkRunnableBatches.front();
                                    g_chunkRunnableBatches.pop_front();
                                    g_chunkRunnableCount.fetch_sub(1, std::memory_order_acq_rel);
                                }
                            }
                            if (token.batch) break;
                            if (w < kKeepWarmSpinYieldThreshold) { RelaxCpu(); }
                            else if (!yielded) { yielded = true; std::this_thread::yield(); }
                            else { RelaxCpu(); }
                        }
                        if (token.batch) g_parkWakeCount.fetch_add(1, std::memory_order_relaxed);
                    }

                    if (!token.batch)
                    {
                        std::unique_lock<std::mutex> lock(g_chunkWorkerMutex);
                        g_chunkWorkerCv.wait(lock, [&observedPrewake] {
                            return g_chunkWorkersShutdown ||
                                g_chunkRunnableCount.load(std::memory_order_acquire) > 0 ||
                                g_chunkPrewakeGeneration.load(std::memory_order_relaxed) != observedPrewake;
                        });
                        if (g_chunkWorkersShutdown && g_chunkRunnableBatches.empty()) return;
                        if (!g_chunkRunnableBatches.empty())
                        {
                            token = g_chunkRunnableBatches.front();
                            g_chunkRunnableBatches.pop_front();
                            g_chunkRunnableCount.fetch_sub(1, std::memory_order_acq_rel);
                        }
                        observedPrewake = g_chunkPrewakeGeneration.load(std::memory_order_relaxed);
                        if (token.batch)
                        {
                            g_parkWakeCount.fetch_add(1, std::memory_order_relaxed);
                        }
                    }
                }

                if (token.batch)
                {
                    RunWorkerChunkToken(token);
                }

                const auto hotUntilNs = g_chunkHotUntilNs.load(std::memory_order_relaxed);
                if (hotUntilNs <= 0)
                {
                    continue;
                }

                const auto hotUntil = std::chrono::steady_clock::time_point(std::chrono::nanoseconds(hotUntilNs));
                while (std::chrono::steady_clock::now() < hotUntil)
                {
                    if (g_chunkRunnableCount.load(std::memory_order_acquire) <= 0)
                    {
                        RelaxCpu();
                        continue;
                    }
                    {
                        std::lock_guard<std::mutex> lock(g_chunkWorkerMutex);
                        if (!g_chunkRunnableBatches.empty())
                        {
                            token = g_chunkRunnableBatches.front();
                            g_chunkRunnableBatches.pop_front();
                            g_chunkRunnableCount.fetch_sub(1, std::memory_order_acq_rel);
                        }
                        else if (g_chunkWorkersShutdown)
                        {
                            return;
                        }
                    }
                    if (!token.batch)
                    {
                        RelaxCpu();
                        continue;
                    }

                    g_hotSpinHits.fetch_add(1, std::memory_order_relaxed);
                    RunWorkerChunkToken(token);
                    token = {};
                }
            }
        }

        void EnsureChunkWorkers(int workerCount)
        {
            if (workerCount <= 0) workerCount = 1;
            std::lock_guard<std::mutex> lock(g_chunkWorkerMutex);
            // 无条件重置关闭标志，避免之前关闭后的残留 true 导致新 worker 立即退出
            g_chunkWorkersShutdown = false;
            if (!g_chunkWorkers.empty()) return;

            g_chunkWorkers.reserve(static_cast<size_t>(workerCount));
            for (int i = 0; i < workerCount; ++i)
            {
                g_chunkWorkers.emplace_back([i]() { ChunkWorkerLoop(i); });
            }
        }

        void ShutdownChunkWorkers()
        {
            std::vector<std::thread> workers;
            {
                std::lock_guard<std::mutex> lock(g_chunkWorkerMutex);
                g_chunkWorkersShutdown = true;
                workers.swap(g_chunkWorkers);
            }
            g_chunkWorkerCv.notify_all();
            for (auto& worker : workers)
            {
                if (worker.joinable()) worker.join();
            }
        }

        void EnqueueChunkBatch(ChunkBatchState* batch, int workerCount)
        {
            if (!batch || batch->rangeCount <= 0) return;
            const int requestedTokens = std::max(1, std::min(workerCount,
                std::min(batch->workerTarget, batch->rangeCount)));
            const int rangesPerToken = (batch->rangeCount + requestedTokens - 1) / requestedTokens;
            const int tokenCount = (batch->rangeCount + rangesPerToken - 1) / rangesPerToken;
            batch->tokenCount = tokenCount;
            batch->queueTokens.store(tokenCount, std::memory_order_release);
            batch->unclaimedTokens.store(tokenCount, std::memory_order_release);
            batch->publishNs.store(std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now().time_since_epoch()).count(), std::memory_order_release);

            {
                std::lock_guard<std::mutex> lock(g_chunkWorkerMutex);
                for (int i = 0; i < tokenCount; ++i)
                {
                    const int firstRange = i * rangesPerToken;
                    const int lastRange = std::min(batch->rangeCount, firstRange + rangesPerToken);
                    g_chunkRunnableBatches.push_back({ batch, firstRange, lastRange });
                }
                g_chunkRunnableCount.fetch_add(tokenCount, std::memory_order_release);
                UpdateQueueDepthPeak(static_cast<int>(g_chunkRunnableBatches.size()));
            }
            g_frameTasksSubmitted.fetch_add(static_cast<uint64_t>(tokenCount), std::memory_order_relaxed);
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            g_notifiedWorkers.fetch_add(static_cast<uint64_t>(tokenCount), std::memory_order_relaxed);
            // 单次 notify_all 替代 tokenCount 次 notify_one：避免 7× 内核切换延迟（~80μs 节省）。
            // token 是固定范围的，worker 通过原子 ClaimChunkToken 无竞争领取，
            // 不会出现 thundering herd 导致的额外锁竞争。
            g_chunkWorkerCv.notify_all();
        }

        int ResolveChunkWorkerTarget(const ChunkBatchState* batch, int workerCount) noexcept
        {
            if (!batch) return 1;
            if (batch->workerCap > 0)
            {
                return std::max(1, std::min(std::min(workerCount, batch->rangeCount), batch->workerCap));
            }

            if (batch->mode == ChunkScheduleMode::PublishNoAssist ||
                batch->mode == ChunkScheduleMode::DeferredPublishNoAssist)
            {
                return std::max(1, std::min(workerCount, batch->rangeCount));
            }

            // Tokens own fixed ranges, so using all workers no longer adds
            // contention on a shared allocation cursor.
            constexpr int kAssistWorkerCap = 15;
            return std::max(1, std::min(std::min(workerCount, batch->rangeCount), kAssistWorkerCap));
        }

        void RunWorkerChunkToken(const ChunkQueueToken& token) noexcept
        {
            auto* batch = token.batch;
            if (!batch) return;
            ClaimChunkToken(batch, true);

            const auto start = std::chrono::steady_clock::now();
            const int64_t startNs = std::chrono::duration_cast<std::chrono::nanoseconds>(
                start.time_since_epoch()).count();
            int64_t expected = 0;
            if (batch->firstWorkerStartNs.compare_exchange_strong(
                expected, startNs, std::memory_order_acq_rel))
            {
                if (batch->coldStart)
                {
                    UpdateWakeLatencyEwma(startNs - batch->publishNs.load(std::memory_order_relaxed));
                }
            }

            const int active = batch->activeWorkers.fetch_add(1, std::memory_order_acq_rel) + 1;
            UpdatePeak(g_activeWorkersPeak, static_cast<uint64_t>(active));
            const bool ranAny = RunChunkRangeSpan(batch, token.firstRange, token.lastRange);
            batch->activeWorkers.fetch_sub(1, std::memory_order_acq_rel);
            if (ranAny)
            {
                g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);
                g_stealCount.fetch_add(1, std::memory_order_relaxed);
            }
            FinishChunkQueueToken(batch);
        }

        bool RunOneQueuedChunkToken(void* rawBatch) noexcept
        {
            auto* requestedBatch = static_cast<ChunkBatchState*>(rawBatch);
            if (!requestedBatch) return false;
            requestedBatch->assistReaders.fetch_add(1, std::memory_order_acq_rel);

            ChunkQueueToken token{};
            {
                std::lock_guard<std::mutex> lock(g_chunkWorkerMutex);
                auto it = std::find_if(g_chunkRunnableBatches.begin(), g_chunkRunnableBatches.end(),
                    [requestedBatch](const ChunkQueueToken& candidate) {
                        return candidate.batch == requestedBatch;
                    });
                if (it == g_chunkRunnableBatches.end())
                {
                    const int previous = requestedBatch->assistReaders.fetch_sub(1, std::memory_order_acq_rel);
                    if (previous == 1) TryReleaseChunkBatchState(requestedBatch);
                    return false;
                }
                token = *it;
                g_chunkRunnableBatches.erase(it);
                g_chunkRunnableCount.fetch_sub(1, std::memory_order_acq_rel);
            }

            ClaimChunkToken(token.batch, false);
            const bool executed = RunChunkRangeSpan(token.batch, token.firstRange, token.lastRange);
            FinishChunkQueueToken(token.batch);
            const int previous = token.batch->assistReaders.fetch_sub(1, std::memory_order_acq_rel);
            if (previous == 1) TryReleaseChunkBatchState(token.batch);
            return executed;
        }

        bool RunOneQueuedColdChunkToken(void* rawBatch) noexcept
        {
            return RunOneQueuedChunkToken(rawBatch);
        }

        void RemovePendingChunkBatch(ChunkBatchState* batch)
        {
            if (!batch) return;
            std::lock_guard<std::mutex> lock(g_pendingChunkMutex);
            auto it = std::find(g_pendingChunkBatches.begin(), g_pendingChunkBatches.end(), batch);
            if (it != g_pendingChunkBatches.end())
            {
                g_pendingChunkBatches.erase(it);
            }
        }

        bool PublishChunkBatch(ChunkBatchState* batch)
        {
            if (!batch) return false;
            int expected = 0;
            if (!batch->scheduleState.compare_exchange_strong(expected, 2, std::memory_order_acq_rel))
                return false;

            if (g_shuttingDown.load(std::memory_order_acquire))
            {
                RemovePendingChunkBatch(batch);
                CancelChunkBatch(batch);
                return true;
            }

            const int workerCount = std::max(1, CurrentWorkerCount());
            EnsureChunkWorkers(workerCount);
            batch->workerTarget = ResolveChunkWorkerTarget(batch, workerCount);
            const auto now = std::chrono::steady_clock::now();
            const int64_t nowNs = std::chrono::duration_cast<std::chrono::nanoseconds>(
                now.time_since_epoch()).count();
            const int64_t previousCompletionNs = g_lastChunkCompletionNs.load(std::memory_order_relaxed);
            constexpr int64_t kColdStartGapNs = 5'000'000;
            // A long idle gap means workers are likely parked. Complete() uses a
            // smaller measured assist budget so it covers wake latency without
            // draining the whole batch before workers can participate.
            batch->coldStart = previousCompletionNs > 0 && nowNs - previousCompletionNs >= kColdStartGapNs;
            if (batch->coldStart)
            {
                g_coldBatches.fetch_add(1, std::memory_order_relaxed);
            }
            if (batch->handleState)
            {
                batch->handleState->pendingCallback.store(nullptr, std::memory_order_release);
                batch->handleState->pendingContext.store(nullptr, std::memory_order_release);
                if (batch->enableAssist)
                {
                    batch->handleState->assist.context.store(batch, std::memory_order_release);
                    batch->handleState->assist.readersDrained.store(
                        &OnChunkAssistReadersDrained, std::memory_order_release);
                    batch->handleState->assist.callback.store(
                        batch->coldStart ? &RunOneQueuedColdChunkToken : &RunOneQueuedChunkToken,
                        std::memory_order_release);
                }
            }

            EnqueueChunkBatch(batch, workerCount);
            return true;
        }

        bool CompletePendingChunkBatch(void* rawBatch) noexcept
        {
            auto* batch = static_cast<ChunkBatchState*>(rawBatch);
            if (!batch) return false;

            int expected = 0;
            if (!batch->scheduleState.compare_exchange_strong(expected, 1, std::memory_order_acq_rel))
                return false;

            RemovePendingChunkBatch(batch);
            if (batch->handleState)
            {
                batch->handleState->pendingCallback.store(nullptr, std::memory_order_release);
                batch->handleState->pendingContext.store(nullptr, std::memory_order_release);
                batch->handleState->assist.callback.store(nullptr, std::memory_order_release);
                batch->handleState->assist.context.store(nullptr, std::memory_order_release);
            }

            const int rangeCount = batch->rangeCount;
            const bool ranAny = RunChunkRangeSpan(batch, 0, rangeCount);
            if (ranAny)
            {
                g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
                g_deferredRuns.fetch_add(1, std::memory_order_relaxed);
            }
            return true;
        }

        bool PublishPendingChunkBatch(void* rawBatch) noexcept
        {
            auto* batch = static_cast<ChunkBatchState*>(rawBatch);
            if (!batch) return false;
            RemovePendingChunkBatch(batch);
            PublishChunkBatch(batch);
            return false;
        }

        void AddPendingChunkBatch(ChunkBatchState* batch, AssistStepCallback pendingCallback)
        {
            if (!batch) return;
            {
                std::lock_guard<std::mutex> lock(g_pendingChunkMutex);
                g_pendingChunkBatches.push_back(batch);
            }
            if (batch->handleState)
            {
                batch->handleState->pendingContext.store(batch, std::memory_order_release);
                batch->handleState->pendingCallback.store(pendingCallback, std::memory_order_release);
            }
        }

        int ResolveChunkSize(int length, int requestedChunk)
        {
            if (length <= 0) return 1;
            if (requestedChunk > 0) return requestedChunk;
            const int workerCount = std::max(1, CurrentWorkerCount());
            const int targetBatchCount = workerCount * 4;
            return std::max(64, (length + targetBatchCount - 1) / targetBatchCount);
        }

        struct GeneralBatchState
        {
            HandleState* handle{ nullptr };
            void* context{ nullptr };
            void (*cleanup)(void*){ nullptr };
            void (*indexCallback)(void*, int){ nullptr };
            void (*batchCallback)(void*, int, int){ nullptr };
            int length{ 0 };
            int batchSize{ 1 };
            int batchCount{ 0 };
            std::atomic<int> nextBatch{ 0 };
            std::atomic<int> completedBatches{ 0 };
            std::atomic<bool> finalized{ false };
            std::atomic<bool> taskflowFinished{ false };
            std::atomic<bool> released{ false };
        };

        void TryReleaseGeneralBatch(void* rawBatch) noexcept
        {
            auto* batch = static_cast<GeneralBatchState*>(rawBatch);
            if (!batch || !batch->finalized.load(std::memory_order_acquire) ||
                !batch->taskflowFinished.load(std::memory_order_acquire) ||
                batch->handle->assist.readers.load(std::memory_order_acquire) != 0)
            {
                return;
            }
            if (batch->released.exchange(true, std::memory_order_acq_rel)) return;
            batch->handle->assist.readersDrained.store(nullptr, std::memory_order_release);
            delete batch;
        }

        void FinalizeGeneralBatch(GeneralBatchState* batch) noexcept
        {
            if (batch->finalized.exchange(true, std::memory_order_acq_rel)) return;
            batch->handle->assist.callback.store(nullptr, std::memory_order_release);
            batch->handle->assist.context.store(nullptr, std::memory_order_release);
            if (batch->cleanup)
            {
                try { batch->cleanup(batch->context); }
                catch (...) {}
            }
            CompleteState(batch->handle);
            TryReleaseGeneralBatch(batch);
        }

        bool TryExecuteGeneralBatch(void* rawBatch) noexcept
        {
            auto* batch = static_cast<GeneralBatchState*>(rawBatch);
            const int batchIndex = batch->nextBatch.fetch_add(1, std::memory_order_relaxed);
            if (batchIndex >= batch->batchCount) return false;
            const int start = batchIndex * batch->batchSize;
            const int count = std::min(batch->batchSize, batch->length - start);
            try
            {
                if (batch->batchCallback)
                {
                    batch->batchCallback(batch->context, start, count);
                }
                else
                {
                    for (int index = start; index < start + count; ++index)
                        batch->indexCallback(batch->context, index);
                }
            }
            catch (...) {}
            if (batch->completedBatches.fetch_add(1, std::memory_order_acq_rel) + 1 == batch->batchCount)
                FinalizeGeneralBatch(batch);
            return true;
        }

        inline void RelaxCpu() noexcept
        {
#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
            _mm_pause();
#elif defined(__i386__) || defined(__x86_64__)
            __builtin_ia32_pause();
#else
            std::this_thread::yield();
#endif
        }

        JobHandle MakeCompletedHandle() { return JobHandle(CreateState(true)); }

        // ---------- 调度辅助 ----------
        template <typename WorkBuilder>
        JobHandle ScheduleWithDependency(const JobHandle& dependency, WorkBuilder&& builder)
        {
            auto* state = CreateState(false);
            auto executor = EnsureExecutor();
            auto* depState = dependency.State();

            if (!depState || depState->completed.load(std::memory_order_acquire))
            {
                builder(state, executor);
                return JobHandle(state);
            }

            AcquireState(state);
            auto launch = [state, executor, builder = std::forward<WorkBuilder>(builder)]() mutable {
                builder(state, executor);
                ReleaseState(state);
                };
            AddContinuationOrRunNow(depState, std::move(launch));
            return JobHandle(state);
        }

        JobHandle ScheduleGeneralBatch(
            void (*indexCallback)(void*, int),
            void (*batchCallback)(void*, int, int),
            void* context,
            int length,
            int batchSize,
            void (*cleanup)(void*),
            const JobHandle& dependency)
        {
            return ScheduleWithDependency(dependency, [=](HandleState* state, auto executor)
            {
                auto* batch = new GeneralBatchState();
                batch->handle = state;
                batch->context = context;
                batch->cleanup = cleanup;
                batch->indexCallback = indexCallback;
                batch->batchCallback = batchCallback;
                batch->length = length;
                batch->batchSize = batchSize;
                batch->batchCount = (length + batchSize - 1) / batchSize;

                state->assist.context.store(batch, std::memory_order_release);
                state->assist.readersDrained.store(&TryReleaseGeneralBatch, std::memory_order_release);
                state->assist.callback.store(&TryExecuteGeneralBatch, std::memory_order_release);

                auto taskflow = AcquireTaskflow();
                const int drainCount = std::min(std::max(1, CurrentWorkerCount()), batch->batchCount);
                for (int worker = 0; worker < drainCount; ++worker)
                {
                    taskflow->emplace([batch]
                    {
                        while (TryExecuteGeneralBatch(batch)) {}
                    });
                }

                AcquireState(state);
                executor->run(*taskflow, [taskflow, state, batch]() mutable
                {
                    batch->taskflowFinished.store(true, std::memory_order_release);
                    TryReleaseGeneralBatch(batch);
                    ReleaseState(state);
                });
            });
        }

        template <typename Work>
        void ScheduleFastPath(Work&& work, void* context, void (*cleanup)(void*), HandleState* state, const std::shared_ptr<tf::Executor>& executor)
        {
            AcquireState(state);
            executor->silent_async([work = std::forward<Work>(work), state, context, cleanup]() {
                try { work(); }
                catch (...) {}
                if (cleanup)
                {
                    try { cleanup(context); }
                    catch (...) {}
                }
                CompleteState(state);
                ReleaseState(state);
                });
        }

        template <typename Work>
        JobHandle ScheduleFastPathWithDependency(Work&& work, void* context, void (*cleanup)(void*), const JobHandle& dependency)
        {
            auto* state = CreateState(false);
            auto executor = EnsureExecutor();
            auto* depState = dependency.State();

            if (!depState || depState->completed.load(std::memory_order_acquire))
            {
                ScheduleFastPath(std::forward<Work>(work), context, cleanup, state, executor);
                return JobHandle(state);
            }

            AcquireState(state);
            auto launch = [work = std::forward<Work>(work), context, cleanup, state, executor]() mutable {
                ScheduleFastPath(std::forward<Work>(work), context, cleanup, state, executor);
                ReleaseState(state);
                };
            AddContinuationOrRunNow(depState, std::move(launch));
            return JobHandle(state);
        }

        template <typename TaskflowBuilder>
        void SubmitTaskflow(const std::shared_ptr<tf::Executor>& executor, TaskflowBuilder&& builder,
            HandleState* state, void* context, void (*cleanup)(void*))
        {
            auto taskflow = AcquireTaskflow();
            builder(*taskflow);
            AcquireState(state);
            executor->run(*taskflow, [taskflow, state, context, cleanup]() mutable {
                if (cleanup)
                {
                    try { cleanup(context); }
                    catch (...) {}
                }
                CompleteState(state);
                ReleaseState(state);
                });
        }

        // 同步执行包装
        inline void ExecuteJobSync(void (*func)(void*), void* context) { func(context); }
        inline void ExecuteForSync(void (*func)(void*, int), void* context, int length) {
            for (int i = 0; i < length; ++i) func(context, i);
        }
        inline void ExecuteBatchSync(void (*func)(void*, int, int), void* context, int length) {
            func(context, 0, length);
        }

        struct AssistLease
        {
            AssistState* state{ nullptr };
            AssistStepCallback callback{ nullptr };
            AssistReleaseCallback readersDrained{ nullptr };
            void* context{ nullptr };

            AssistLease() = default;
            AssistLease(const AssistLease&) = delete;
            AssistLease& operator=(const AssistLease&) = delete;
            AssistLease(AssistLease&& other) noexcept
                : state(std::exchange(other.state, nullptr)),
                  callback(other.callback),
                  readersDrained(other.readersDrained),
                  context(other.context)
            {
            }

            ~AssistLease()
            {
                if (!state) return;
                if (state->readers.fetch_sub(1, std::memory_order_acq_rel) == 1 && readersDrained)
                {
                    readersDrained(context);
                }
            }
        };

        AssistLease TryAcquireAssist(HandleState* handle) noexcept
        {
            AssistLease lease;
            auto& assist = handle->assist;
            assist.readers.fetch_add(1, std::memory_order_acq_rel);
            lease.state = &assist;
            lease.callback = assist.callback.load(std::memory_order_acquire);
            lease.context = assist.context.load(std::memory_order_acquire);
            lease.readersDrained = assist.readersDrained.load(std::memory_order_acquire);
            if (!lease.callback || !lease.context || handle->completed.load(std::memory_order_acquire))
            {
                if (assist.readers.fetch_sub(1, std::memory_order_acq_rel) == 1 &&
                    lease.readersDrained && lease.context)
                {
                    lease.readersDrained(lease.context);
                }
                lease.state = nullptr;
                lease.callback = nullptr;
                lease.context = nullptr;
            }
            return lease;
        }

    } // namespace

    // ========== JobHandle 实现 ==========
    JobHandle::JobHandle(HandleState* state, bool addRef) noexcept : _state(state) {
        if (addRef) Acquire(_state);
    }
    JobHandle::JobHandle(const JobHandle& other) noexcept : _state(other._state) { Acquire(_state); }
    JobHandle::JobHandle(JobHandle&& other) noexcept : _state(other._state) { other._state = nullptr; }
    JobHandle& JobHandle::operator=(const JobHandle& other) noexcept {
        if (this != &other) { Acquire(other._state); Release(_state); _state = other._state; }
        return *this;
    }
    JobHandle& JobHandle::operator=(JobHandle&& other) noexcept {
        if (this != &other) { Release(_state); _state = other._state; other._state = nullptr; }
        return *this;
    }
    JobHandle::~JobHandle() { Release(_state); }

    void JobHandle::Acquire(HandleState* state) noexcept {
        if (state) state->refCount.fetch_add(1, std::memory_order_relaxed);
    }
    void JobHandle::Release(HandleState* state) noexcept {
        if (state && state->refCount.fetch_sub(1, std::memory_order_acq_rel) == 1)
            RecycleState(state);
    }

    void JobHandle::Complete() const
    {
        if (!_state) return;
        if (_state->completed.load(std::memory_order_acquire)) return;

        // 先尝试 pendingCallback（延迟发布的 batch）
        auto pendingCallback = _state->pendingCallback.load(std::memory_order_acquire);
        void* pendingContext = _state->pendingContext.load(std::memory_order_acquire);
        if (pendingCallback && pendingContext && pendingCallback(pendingContext))
        {
            return;
        }

        // 确保任何延迟发布的 batch 已发布给 worker
        Scheduler::FlushScheduledJobs();

        {
            auto assist = TryAcquireAssist(_state);
            if (assist.callback && assist.context)
            {
                constexpr auto kThroughputAssistBudget = std::chrono::microseconds(1500);
                const auto deadline = std::chrono::steady_clock::now() + kThroughputAssistBudget;
                do
                {
                    g_assistAttempts.fetch_add(1, std::memory_order_relaxed);
                    if (!assist.callback(assist.context)) break;
                    g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
                    g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
                } while (!_state->completed.load(std::memory_order_acquire) &&
                    std::chrono::steady_clock::now() < deadline);
            }
        }

        if (_state->completed.load(std::memory_order_acquire)) return;

        // Phase 2: Short spin — 等待 worker 完成最后的工作
        if (_state->completed.load(std::memory_order_acquire)) return;
        for (int i = 0; i < kSpinBeforeWait; ++i)
        {
            if (_state->completed.load(std::memory_order_acquire)) return;
            RelaxCpu();
        }

        if (_state->completed.load(std::memory_order_acquire)) return;

        // Phase 3: 阻塞等待（极少发生）
        g_waitFallbacks.fetch_add(1, std::memory_order_relaxed);
        g_completeWaitLoops.fetch_add(1, std::memory_order_relaxed);
        while (!_state->completed.load(std::memory_order_acquire))
            _state->completed.wait(false, std::memory_order_acquire);
    }

    bool JobHandle::IsCompleted() const noexcept {
        return !_state || _state->completed.load(std::memory_order_acquire);
    }

    HandleState* JobHandle::State() const noexcept { return _state; }

    JobHandle JobHandle::CombineDependencies(const std::vector<JobHandle>& handles)
    {
        std::vector<HandleState*> pendingStates;
        pendingStates.reserve(handles.size());
        for (const auto& h : handles)
            if (h._state && !h._state->completed.load(std::memory_order_acquire))
                pendingStates.push_back(h._state);
        if (pendingStates.empty()) return MakeCompletedHandle();

        auto* combinedState = CreateState(false);
        auto remaining = std::make_shared<std::atomic<int>>(static_cast<int>(pendingStates.size()));

        for (const auto& depState : pendingStates) {
            AcquireState(combinedState);
            AddContinuationOrRunNow(depState, [combinedState, remaining]() {
                if (remaining->fetch_sub(1, std::memory_order_acq_rel) == 1)
                    CompleteState(combinedState);
                ReleaseState(combinedState);
                });
        }
        return JobHandle(combinedState);
    }

    // ========== Scheduler 实现 ==========
    void Scheduler::Initialize(int numThreads)
    {
        g_shuttingDown.store(false, std::memory_order_release);
        const int resolved = ResolveWorkerCount(numThreads);
        std::shared_ptr<tf::Executor> oldExecutor;
        bool recreateChunkWorkers = false;
        {
            std::lock_guard<std::mutex> lock(g_executorMutex);
            if (g_executor && g_numThreads == resolved) return;
            recreateChunkWorkers = g_executor != nullptr;
            oldExecutor = std::move(g_executor);
            g_numThreads = resolved;
            g_executor = std::make_shared<tf::Executor>(static_cast<size_t>(g_numThreads));

            // 使用高线程优先级，减少被其他线程抢占导致的抖动
            for (int i = 0; i < g_numThreads; ++i)
            {
                g_executor->silent_async([]() {
#ifdef _WIN32
                    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
#else
                    setpriority(PRIO_PROCESS, 0, -5);
#endif
                    });
            }
            g_executor->wait_for_all();
        }
        if (recreateChunkWorkers) ShutdownChunkWorkers();
        EnsureChunkWorkers(resolved);
        if (oldExecutor) oldExecutor->wait_for_all();
    }

    void Scheduler::Shutdown()
    {
        g_shuttingDown.store(true, std::memory_order_release);
        FlushScheduledJobs();
        ShutdownChunkWorkers();

        std::shared_ptr<tf::Executor> executor;
        {
            std::lock_guard<std::mutex> lock(g_executorMutex);
            executor = std::move(g_executor);
            g_numThreads = 0;
        }
        if (executor) executor->wait_for_all();

        {
            std::lock_guard<std::mutex> lock(g_statePoolMutex);
            for (auto* s : g_statePool) delete s;
            g_statePool.clear();
        }
        {
            std::lock_guard<std::mutex> lock(g_taskflowPoolMutex);
            for (auto* tf : g_taskflowPool) delete tf;
            g_taskflowPool.clear();
        }
    }

    void PrewakeTaskflowWorkers()
    {
        std::shared_ptr<tf::Executor> executor;
        int workerCount = 0;
        {
            std::lock_guard<std::mutex> lock(g_executorMutex);
            executor = g_executor;
            workerCount = g_numThreads;
        }
        if (!executor || workerCount <= 0) return;
        const int wakeCount = std::min(workerCount, 4);
        for (int index = 0; index < wakeCount; ++index)
            executor->silent_async([] {});
    }

    void PrewakeChunkWorkers()
    {
        const int workerCount = std::max(1, CurrentWorkerCount());
        EnsureChunkWorkers(workerCount);
        g_chunkPrewakeGeneration.fetch_add(1, std::memory_order_release);
        const auto now = std::chrono::steady_clock::now();
        const auto hotUntil = now + kChunkWorkerHotWait;
        g_chunkHotUntilNs.store(
            std::chrono::duration_cast<std::chrono::nanoseconds>(hotUntil.time_since_epoch()).count(),
            std::memory_order_release);
        g_chunkWorkerCv.notify_all();
    }

    void AutoPrewakeTaskflowIfNeeded(int length)
    {
        if (length < 1024) return;
        const int64_t nowNs = std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count();
        const int64_t previousNs = g_lastTaskflowScheduleNs.exchange(nowNs, std::memory_order_acq_rel);
        if (previousNs == 0 || nowNs - previousNs >= 1'000'000)
            PrewakeTaskflowWorkers();
    }

    void Scheduler::PrewakeWorkers()
    {
        g_prewakeCount.fetch_add(1, std::memory_order_relaxed);
        PrewakeTaskflowWorkers();
        PrewakeChunkWorkers();
    }

    void Scheduler::KeepWorkersWarm(int microseconds)
    {
        // 仅设 g_keepWarmUntilNs，不涉及 g_chunkHotUntilNs（hot-spin）。
        // worker 在 CV park 前检查此标志并自旋等待，而非深度内核休眠。
        const int64_t deadline = std::chrono::duration_cast<std::chrono::nanoseconds>(
            (std::chrono::steady_clock::now() + std::chrono::microseconds(microseconds)).time_since_epoch()
        ).count();
        g_keepWarmUntilNs.store(deadline, std::memory_order_release);
        g_chunkWorkerCv.notify_all();
    }

    void Scheduler::SetFrameLowLatencyMode(bool enabled)
    {
        g_frameLowLatencyMode.store(enabled, std::memory_order_release);
        if (!enabled)
        {
            g_chunkHotUntilNs.store(0, std::memory_order_release);
        }
    }

    void Scheduler::FlushScheduledJobs()
    {
        std::deque<ChunkBatchState*> pending;
        {
            std::lock_guard<std::mutex> lock(g_pendingChunkMutex);
            pending.swap(g_pendingChunkBatches);
        }

        for (auto* batch : pending)
        {
            PublishChunkBatch(batch);
        }
    }

    JobHandle Scheduler::Schedule(
        void (*func)(void*), void* context,
        void (*cleanup)(void*),
        const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire))
        {
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }
        if (!func) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }

        if (!dependency.State() || dependency.IsCompleted())
        {
            ExecuteJobSync(func, context);
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }

        return ScheduleFastPathWithDependency(
            [func, context]() { ExecuteJobSync(func, context); },
            context, cleanup, dependency);
    }

    JobHandle Scheduler::ScheduleFor(
        void (*func)(void*, int), void* context,
        int length,
        void (*cleanup)(void*),
        const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire))
        {
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }
        if (!func) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }
        if (length <= 0) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }

        bool depCompleted = !dependency.State() || dependency.IsCompleted();

        if (length <= kSyncExecutionLengthThreshold || (depCompleted && length <= kSyncWithCompletedDepThreshold))
        {
            ExecuteForSync(func, context, length);
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }

        if (length <= 64)
        {
            return ScheduleFastPathWithDependency(
                [func, context, length]() { ExecuteForSync(func, context, length); },
                context, cleanup, dependency);
        }

        return ScheduleWithDependency(dependency, [func, context, length, cleanup](HandleState* state, auto executor) {
            SubmitTaskflow(executor, [func, context, length](tf::Taskflow& taskflow) {
                taskflow.emplace([func, context, length]() { ExecuteForSync(func, context, length); });
                }, state, context, cleanup);
            });
    }

    JobHandle Scheduler::ScheduleParallelFor(
        void (*func)(void*, int), void* context,
        int length, int batchSize,
        void (*cleanup)(void*),
        const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire))
        {
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }
        if (!func) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }
        if (length <= 0) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }

        bool depCompleted = !dependency.State() || dependency.IsCompleted();

        if (length <= kSyncExecutionLengthThreshold || (depCompleted && length <= kSyncWithCompletedDepThreshold))
        {
            ExecuteForSync(func, context, length);
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }

        AutoPrewakeTaskflowIfNeeded(length);

        const int chunkSize = ResolveChunkSize(length, batchSize);
        const int chunkCount = (length + chunkSize - 1) / chunkSize;

        if (chunkCount == 1)
        {
            return ScheduleFastPathWithDependency(
                [func, context, length]() { ExecuteForSync(func, context, length); },
                context, cleanup, dependency);
        }

        return ScheduleGeneralBatch(func, nullptr, context, length, chunkSize, cleanup, dependency);
    }

    JobHandle Scheduler::ScheduleParallelForBatch(
        void (*func)(void*, int, int), void* context,
        int length, int batchSize,
        void (*cleanup)(void*),
        const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire))
        {
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }
        if (!func) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }
        if (length <= 0) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }

        const bool forceAsync = batchSize < 0;
        const int requestedBatchSize = forceAsync ? -batchSize : batchSize;
        bool depCompleted = !dependency.State() || dependency.IsCompleted();

        if (!forceAsync && (length <= kSyncExecutionLengthThreshold || (depCompleted && length <= kSyncWithCompletedDepThreshold)))
        {
            ExecuteBatchSync(func, context, length);
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }

        AutoPrewakeTaskflowIfNeeded(length);

        const int safeBatchSize = std::max(1, ResolveChunkSize(length, requestedBatchSize));
        const int batchCount = (length + safeBatchSize - 1) / safeBatchSize;

        if (batchCount == 1)
        {
            return ScheduleFastPathWithDependency(
                [func, context, length]() { ExecuteBatchSync(func, context, length); },
                context, cleanup, dependency);
        }

        return ScheduleGeneralBatch(nullptr, func, context, length, safeBatchSize, cleanup, dependency);
    }

    static JobHandle ScheduleChunkBatchCore(
        void (*func)(void*, const struct ChunkJobData*), void* context,
        void (*rangeFunc)(void*, const struct ChunkJobData*, int, int),
        void (*entityRangeFunc)(void*, const struct EntityBatchData*, int, int),
        void (*cleanup)(void*),
        const struct ChunkJobData* chunks,
        const struct EntityBatchData* entityBatches,
        int chunkCount,
        const JobHandle& dependency,
        ChunkScheduleMode mode,
        int workerCap,
        int rangeSize)
    {
        if (g_shuttingDown.load(std::memory_order_acquire))
        {
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }

        if ((!func && !rangeFunc && !entityRangeFunc) || chunkCount <= 0)
        {
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }

        auto* state = CreateState(false);
        auto executor = EnsureExecutor();
        constexpr int kDeferredMaxRanges = 2;
        const bool enableAssist = mode != ChunkScheduleMode::PublishNoAssist &&
            mode != ChunkScheduleMode::DeferredPublishNoAssist;

        switch (mode)
        {
        case ChunkScheduleMode::PublishNoAssist:
            g_scheduleModePublishNoAssist.fetch_add(1, std::memory_order_relaxed);
            break;
        case ChunkScheduleMode::PublishAssist:
            g_scheduleModePublishAssist.fetch_add(1, std::memory_order_relaxed);
            break;
        case ChunkScheduleMode::DeferTinyOnly:
            g_scheduleModeDeferTinyOnly.fetch_add(1, std::memory_order_relaxed);
            break;
        case ChunkScheduleMode::ImmediateNative:
            g_scheduleModeImmediateNative.fetch_add(1, std::memory_order_relaxed);
            break;
        case ChunkScheduleMode::DeferredPublish:
            g_scheduleModeDeferredPublish.fetch_add(1, std::memory_order_relaxed);
            break;
        case ChunkScheduleMode::DeferredPublishNoAssist:
            g_scheduleModeDeferredPublishNoAssist.fetch_add(1, std::memory_order_relaxed);
            break;
        }

        auto createBatch = [=]() -> ChunkBatchState* {
            auto* batch = AcquireChunkBatchState();
            batch->func = func;
            batch->rangeFunc = rangeFunc;
            batch->entityRangeFunc = entityRangeFunc;
            batch->context = context;
            batch->cleanup = cleanup;
            batch->chunks = chunks;
            batch->entityBatches = entityBatches;
            batch->chunkCount = chunkCount;
            if (rangeSize > 0)
            {
                batch->rangeSize = rangeSize;
            }
            else
            {
                int workerCount = std::max(1, CurrentWorkerCount());
                // Keep enough ranges for late workers and Complete() to rebalance
                // a heavy tail without making every chunk a separate queue item.
                batch->rangeSize = std::max(1, chunkCount / (workerCount * 6 + 1));
            }
            batch->rangeCount = (chunkCount + batch->rangeSize - 1) / batch->rangeSize;
            batch->enableAssist = enableAssist;
            batch->mode = mode;
            batch->handleState = state;
            batch->workerCap = workerCap;
            batch->rangeSizeOverride = rangeSize;
            AcquireState(state);

            return batch;
            };

        if (dependency.State() && !dependency.IsCompleted())
        {
            auto* batch = createBatch();
            AcquireState(state);
            AddContinuationOrRunNow(dependency.State(), [batch, state, mode]() {
                if (mode == ChunkScheduleMode::ImmediateNative)
                {
                    CompletePendingChunkBatch(batch);
                }
                else
                {
                    PublishChunkBatch(batch);
                }
                ReleaseState(state);
            });
        }
        else
        {
            auto* batch = createBatch();
            if (mode == ChunkScheduleMode::ImmediateNative)
            {
                CompletePendingChunkBatch(batch);
            }
            else if (mode == ChunkScheduleMode::DeferTinyOnly && batch->rangeCount <= kDeferredMaxRanges)
            {
                AddPendingChunkBatch(batch, &CompletePendingChunkBatch);
            }
            else if (mode == ChunkScheduleMode::DeferredPublish ||
                mode == ChunkScheduleMode::DeferredPublishNoAssist)
            {
                AddPendingChunkBatch(batch, &PublishPendingChunkBatch);
            }
            else
            {
                PublishChunkBatch(batch);
            }
        }

        return JobHandle(state);
    }

    // ========== ScheduleChunks 实现 ==========
    JobHandle Scheduler::ScheduleChunks(
        void (*func)(void*, const struct ChunkJobData*), void* context,
        void (*cleanup)(void*),
        const struct ChunkJobData* chunks,
        int chunkCount,
        const JobHandle& dependency,
        ChunkScheduleMode mode,
        int workerCap,
        int rangeSize)
    {
        return ScheduleChunkBatchCore(func, context, nullptr, nullptr, cleanup, chunks, nullptr, chunkCount, dependency, mode, workerCap, rangeSize);
    }

    JobHandle Scheduler::ScheduleChunkRanges(
        void (*func)(void*, const struct ChunkJobData*, int, int), void* context,
        void (*cleanup)(void*),
        const struct ChunkJobData* chunks,
        int chunkCount,
        const JobHandle& dependency,
        ChunkScheduleMode mode,
        int workerCap,
        int rangeSize)
    {
        return ScheduleChunkBatchCore(nullptr, context, func, nullptr, cleanup, chunks, nullptr, chunkCount, dependency, mode, workerCap, rangeSize);
    }

    JobHandle Scheduler::ScheduleEntityBatches(
        void (*func)(void*, const struct EntityBatchData*, int, int), void* context,
        void (*cleanup)(void*),
        const struct EntityBatchData* batches,
        int batchCount,
        const JobHandle& dependency,
        ChunkScheduleMode mode,
        int workerCap,
        int rangeSize)
    {
        return ScheduleChunkBatchCore(nullptr, context, nullptr, func, cleanup, nullptr, batches, batchCount, dependency, mode, workerCap, rangeSize);
    }

} // namespace JobSystem
