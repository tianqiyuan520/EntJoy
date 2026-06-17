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
#define NOMINMAX
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
    constexpr int kInitialAssistBurst = 4;
    constexpr int kAssistDrainBurst = 32;
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
    std::atomic<uint64_t> g_scheduleModePublishNoAssist{ 0 };
    std::atomic<uint64_t> g_scheduleModePublishAssist{ 0 };
    std::atomic<uint64_t> g_scheduleModeDeferTinyOnly{ 0 };
    std::atomic<uint64_t> g_scheduleModeImmediateNative{ 0 };
    std::atomic<uint64_t> g_scheduleModeDeferredPublish{ 0 };
    std::atomic<uint64_t> g_scheduleModeDeferredPublishNoAssist{ 0 };
    std::atomic<int> g_frameQueueDepthPeak{ 0 };
    std::atomic<bool> g_shuttingDown{ false };

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
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            state->assistStep = {};
        }
        state->assistCallback.store(nullptr, std::memory_order_release);
        state->assistContext.store(nullptr, std::memory_order_release);
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
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            state->assistStep = {};
        }
        state->assistCallback.store(nullptr, std::memory_order_release);
        state->assistContext.store(nullptr, std::memory_order_release);
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
            state->assistCallback.store(nullptr, std::memory_order_release);
            state->assistContext.store(nullptr, std::memory_order_release);
            state->pendingCallback.store(nullptr, std::memory_order_release);
            state->pendingContext.store(nullptr, std::memory_order_release);
            state->assistStep = {};
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
            int rangeSize{ 8 };
            int rangeCount{ 0 };
            bool enableAssist{ false };
            ChunkScheduleMode mode{ ChunkScheduleMode::PublishAssist };
            HandleState* handleState{ nullptr };
            int workerTarget{ 1 };
            int workerCap{ 0 };
            int rangeSizeOverride{ 0 };

            std::atomic<int> nextRange{ 0 };
            std::atomic<int> completedRanges{ 0 };
            std::atomic<int> queueTokens{ 0 };
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
            batch->rangeSize = 8;
            batch->rangeCount = 0;
            batch->enableAssist = false;
            batch->mode = ChunkScheduleMode::PublishAssist;
            batch->handleState = nullptr;
            batch->workerTarget = 1;
            batch->workerCap = 0;
            batch->rangeSizeOverride = 0;
            batch->nextRange.store(0, std::memory_order_relaxed);
            batch->completedRanges.store(0, std::memory_order_relaxed);
            batch->queueTokens.store(0, std::memory_order_relaxed);
            batch->scheduleState.store(0, std::memory_order_relaxed);
            batch->cleanupDone.store(false, std::memory_order_relaxed);
            batch->returnedToPool.store(false, std::memory_order_relaxed);
            return batch;
        }

        void ReleaseChunkBatchState(ChunkBatchState* batch) noexcept
        {
            if (!batch) return;
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
                batch->queueTokens.load(std::memory_order_acquire) != 0)
            {
                return;
            }

            if (!batch->returnedToPool.exchange(true, std::memory_order_acq_rel))
            {
                ReleaseChunkBatchState(batch);
            }
        }

        void FinalizeChunkBatch(ChunkBatchState* batch) noexcept;
        void CancelChunkBatch(ChunkBatchState* batch) noexcept;

        bool RunOneChunkRange(void* rawBatch) noexcept
        {
            auto* batch = static_cast<ChunkBatchState*>(rawBatch);
            if (!batch || (!batch->func && !batch->rangeFunc && !batch->entityRangeFunc)) return false;

            const int rangeIndex = batch->nextRange.fetch_add(1, std::memory_order_relaxed);
            if (rangeIndex >= batch->rangeCount) return false;

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

            if (batch->completedRanges.fetch_add(1, std::memory_order_acq_rel) + 1 == batch->rangeCount)
            {
                FinalizeChunkBatch(batch);
            }
            return true;
        }

        std::mutex g_chunkWorkerMutex;
        std::condition_variable g_chunkWorkerCv;
        std::deque<ChunkBatchState*> g_chunkRunnableBatches;
        std::mutex g_pendingChunkMutex;
        std::deque<ChunkBatchState*> g_pendingChunkBatches;
        std::vector<std::thread> g_chunkWorkers;
        bool g_chunkWorkersShutdown = false;
        std::atomic<uint64_t> g_chunkPrewakeGeneration{ 0 };
        std::atomic<int64_t> g_chunkHotUntilNs{ 0 };

        inline void RelaxCpu() noexcept;

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
                batch->handleState = nullptr;
                CompleteState(state);
                ReleaseState(state);
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
                batch->handleState = nullptr;
                CompleteState(state);
                ReleaseState(state);
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
                ChunkBatchState* batch = nullptr;

                for (int spin = 0; spin < kWorkerIdleSpin; ++spin)
                {
                    {
                        std::lock_guard<std::mutex> lock(g_chunkWorkerMutex);
                        if (!g_chunkRunnableBatches.empty())
                        {
                            batch = g_chunkRunnableBatches.front();
                            g_chunkRunnableBatches.pop_front();
                            break;
                        }
                        if (g_chunkWorkersShutdown) return;
                    }
                    RelaxCpu();
                }

                if (!batch)
                {
                    std::unique_lock<std::mutex> lock(g_chunkWorkerMutex);
                    g_chunkWorkerCv.wait(lock, [&observedPrewake] {
                        return g_chunkWorkersShutdown ||
                            !g_chunkRunnableBatches.empty() ||
                            g_chunkPrewakeGeneration.load(std::memory_order_relaxed) != observedPrewake;
                    });
                    if (g_chunkWorkersShutdown && g_chunkRunnableBatches.empty()) return;
                    if (!g_chunkRunnableBatches.empty())
                    {
                        batch = g_chunkRunnableBatches.front();
                        g_chunkRunnableBatches.pop_front();
                    }
                    observedPrewake = g_chunkPrewakeGeneration.load(std::memory_order_relaxed);
                    if (batch)
                    {
                        g_parkWakeCount.fetch_add(1, std::memory_order_relaxed);
                    }
                }

                if (batch)
                {
                    bool ranAny = false;
                    while (RunOneChunkRange(batch))
                    {
                        ranAny = true;
                        g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);
                    }
                    if (ranAny) g_stealCount.fetch_add(1, std::memory_order_relaxed);
                    FinishChunkQueueToken(batch);
                }

                const auto hotUntilNs = g_chunkHotUntilNs.load(std::memory_order_relaxed);
                if (hotUntilNs <= 0)
                {
                    continue;
                }

                const auto hotUntil = std::chrono::steady_clock::time_point(std::chrono::nanoseconds(hotUntilNs));
                while (std::chrono::steady_clock::now() < hotUntil)
                {
                    {
                        std::lock_guard<std::mutex> lock(g_chunkWorkerMutex);
                        if (!g_chunkRunnableBatches.empty())
                        {
                            batch = g_chunkRunnableBatches.front();
                            g_chunkRunnableBatches.pop_front();
                        }
                        else if (g_chunkWorkersShutdown)
                        {
                            return;
                        }
                    }
                    if (!batch)
                    {
                        RelaxCpu();
                        continue;
                    }

                    g_hotSpinHits.fetch_add(1, std::memory_order_relaxed);
                    bool ranAny = false;
                    while (RunOneChunkRange(batch))
                    {
                        ranAny = true;
                        g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);
                    }
                    if (ranAny) g_stealCount.fetch_add(1, std::memory_order_relaxed);
                    FinishChunkQueueToken(batch);
                    batch = nullptr;
                }
            }
        }

        void EnsureChunkWorkers(int workerCount)
        {
            if (workerCount <= 0) workerCount = 1;
            std::lock_guard<std::mutex> lock(g_chunkWorkerMutex);
            if (!g_chunkWorkers.empty()) return;

            g_chunkWorkersShutdown = false;
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
            const int tokenCount = std::max(1, std::min(workerCount, std::min(batch->workerTarget, batch->rangeCount)));
            batch->queueTokens.store(tokenCount, std::memory_order_release);

            {
                std::lock_guard<std::mutex> lock(g_chunkWorkerMutex);
                for (int i = 0; i < tokenCount; ++i)
                {
                    g_chunkRunnableBatches.push_back(batch);
                }
                UpdateQueueDepthPeak(static_cast<int>(g_chunkRunnableBatches.size()));
            }
            g_frameTasksSubmitted.fetch_add(static_cast<uint64_t>(tokenCount), std::memory_order_relaxed);
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            g_notifiedWorkers.fetch_add(static_cast<uint64_t>(tokenCount), std::memory_order_relaxed);
            for (int i = 0; i < tokenCount; ++i)
            {
                g_chunkWorkerCv.notify_one();
            }
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

            // NativeTranspile light IJobChunk benefits from fewer workers: less wake/sync traffic and less memory-bandwidth contention.
            constexpr int kNativeAssistWorkerCap = 12;
            return std::max(1, std::min(std::min(workerCount, batch->rangeCount), kNativeAssistWorkerCap));
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
            if (batch->handleState)
            {
                batch->handleState->pendingCallback.store(nullptr, std::memory_order_release);
                batch->handleState->pendingContext.store(nullptr, std::memory_order_release);
                if (batch->enableAssist)
                {
                    batch->handleState->assistContext.store(batch, std::memory_order_release);
                    batch->handleState->assistCallback.store(&RunOneChunkRange, std::memory_order_release);
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
                batch->handleState->assistCallback.store(&RunOneChunkRange, std::memory_order_release);
                batch->handleState->assistContext.store(batch, std::memory_order_release);
            }

            bool ranAny = false;
            while (RunOneChunkRange(batch))
            {
                ranAny = true;
                g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
            }
            if (ranAny) g_deferredRuns.fetch_add(1, std::memory_order_relaxed);
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
            return std::max(64, length / (workerCount * 2));
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

        // Unity-style deferred schedule: 如果 Schedule 后立刻 Complete，直接在当前线程执行 pending chunk batch，
        // 避免唤醒 worker 和 condition_variable 往返。
        auto pendingCallback = _state->pendingCallback.load(std::memory_order_acquire);
        void* pendingContext = _state->pendingContext.load(std::memory_order_acquire);
        if (pendingCallback && pendingContext && pendingCallback(pendingContext))
        {
            return;
        }

        // If this handle depends on another deferred job, completing this handle must
        // publish the whole pending batch set. This mirrors Unity's Complete behavior:
        // pending scheduled work is made runnable before the caller blocks.
        Scheduler::FlushScheduledJobs();

        auto assistSome = [this](int burst) -> bool {
            auto assistCallback = _state->assistCallback.load(std::memory_order_acquire);
            void* assistContext = _state->assistContext.load(std::memory_order_acquire);
            if (!assistCallback || !assistContext) return false;

            bool assisted = false;
            for (int i = 0; i < burst; ++i) {
                if (_state->completed.load(std::memory_order_acquire)) return assisted;
                g_assistAttempts.fetch_add(1, std::memory_order_relaxed);
                if (!assistCallback(assistContext)) break;
                g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
                g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
                assisted = true;
            }
            return assisted;
        };

        // 阶段 1: Complete 立即帮助执行少量 range，模拟 Unity 等待时主线程参与执行。
        if (assistSome(kInitialAssistBurst) &&
            _state->completed.load(std::memory_order_acquire))
        {
            return;
        }

        // 阶段 2: 有限自旋，并穿插 assist，覆盖 worker 已被预唤醒但尚未取到任务的窗口。
        for (int i = 0; i < kSpinBeforeWait; ++i) {
            if (_state->completed.load(std::memory_order_acquire)) return;
            if ((i & 31) == 31 && assistSome(1) &&
                _state->completed.load(std::memory_order_acquire))
            {
                return;
            }
            RelaxCpu();
        }

        // 阶段 3: 主线程协作执行；无可执行工作时再阻塞等待。
        while (!_state->completed.load(std::memory_order_acquire)) {
            if (assistSome(kAssistDrainBurst)) {
                continue;
            }

            std::function<bool()> assistStep;
            {
                std::lock_guard<std::mutex> lock(_state->mtx);
                assistStep = _state->assistStep;
            }
            if (assistStep && assistStep()) {
                g_assistAttempts.fetch_add(1, std::memory_order_relaxed);
                g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
                continue;
            }
            g_completeWaitLoops.fetch_add(1, std::memory_order_relaxed);
            g_waitFallbacks.fetch_add(1, std::memory_order_relaxed);
            _state->completed.wait(false, std::memory_order_acquire);
        }
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

    void Scheduler::PrewakeWorkers()
    {
        const int workerCount = std::max(1, CurrentWorkerCount());
        EnsureChunkWorkers(workerCount);
        g_prewakeCount.fetch_add(1, std::memory_order_relaxed);
        g_chunkPrewakeGeneration.fetch_add(1, std::memory_order_release);
        const auto hotUntil = std::chrono::steady_clock::now() + kChunkWorkerHotWait;
        g_chunkHotUntilNs.store(
            std::chrono::duration_cast<std::chrono::nanoseconds>(hotUntil.time_since_epoch()).count(),
            std::memory_order_release);

        std::shared_ptr<tf::Executor> executor;
        int taskflowWorkerCount = 0;
        {
            std::lock_guard<std::mutex> lock(g_executorMutex);
            executor = g_executor;
            taskflowWorkerCount = g_numThreads;
        }
        if (!executor || taskflowWorkerCount <= 0) return;

        const int taskflowWakeCount = std::min(taskflowWorkerCount, 4);
        for (int i = 0; i < taskflowWakeCount; ++i)
        {
            executor->silent_async([] {});
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

        const int chunkSize = ResolveChunkSize(length, batchSize);
        const int chunkCount = (length + chunkSize - 1) / chunkSize;
        const int workerCount = std::max(1, CurrentWorkerCount());

        if (chunkCount == 1)
        {
            return ScheduleFastPathWithDependency(
                [func, context, length]() { ExecuteForSync(func, context, length); },
                context, cleanup, dependency);
        }

        return ScheduleWithDependency(dependency, [=](HandleState* state, auto executor) {
            // 当 chunk 数较少时，使用 silent_async 直接提交每个 chunk
            // 避免 taskflow 图构建开销（taskflow 的 emplace + run 对于少量 task 开销占比大）
            if (chunkCount <= workerCount * 2)
            {
                auto remaining = std::make_shared<std::atomic<int>>(chunkCount);
                for (int i = 0; i < chunkCount; ++i)
                {
                    int begin = i * chunkSize;
                    int end = std::min(length, begin + chunkSize);
                    AcquireState(state);
                    executor->silent_async([=]() {
                        try
                        {
                            for (int j = begin; j < end; ++j) func(context, j);
                        }
                        catch (...) {}
                        if (--*remaining == 0)
                        {
                            if (cleanup)
                            {
                                try { cleanup(context); }
                                catch (...) {}
                            }
                            CompleteState(state);
                        }
                        ReleaseState(state);
                        });
                }
            }
            else
            {
                auto nextChunk = std::make_shared<std::atomic<int>>(0);
                auto completedChunks = std::make_shared<std::atomic<int>>(0);
                auto finalized = std::make_shared<std::atomic<bool>>(false);
                auto finalizeParallelFor = [=]() {
                    if (!finalized->exchange(true, std::memory_order_acq_rel))
                    {
                        if (cleanup)
                        {
                            try { cleanup(context); }
                            catch (...) {}
                        }
                        CompleteState(state);
                    }
                };
                auto runOneChunk = [=]() -> bool {
                    const int chunkIdx = nextChunk->fetch_add(1, std::memory_order_relaxed);
                    if (chunkIdx >= chunkCount) return false;
                    const int begin = chunkIdx * chunkSize;
                    const int end = std::min(length, begin + chunkSize);
                    try
                    {
                        for (int i = begin; i < end; ++i) func(context, i);
                    }
                    catch (...) {}
                    if (completedChunks->fetch_add(1, std::memory_order_acq_rel) + 1 == chunkCount)
                    {
                        finalizeParallelFor();
                    }
                    return true;
                };
                {
                    std::lock_guard<std::mutex> lock(state->mtx);
                    state->assistStep = runOneChunk;
                }

                auto taskflow = AcquireTaskflow();
                for (int w = 0; w < workerCount; ++w)
                {
                    taskflow->emplace([=]() {
                        while (runOneChunk()) {}
                    });
                }

                AcquireState(state);
                executor->run(*taskflow, [taskflow, state]() mutable {
                    ReleaseState(state);
                });
            }
            });
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

        const int safeBatchSize = std::max(1, ResolveChunkSize(length, requestedBatchSize));
        const int batchCount = (length + safeBatchSize - 1) / safeBatchSize;
        const int workerCount = std::max(1, CurrentWorkerCount());

        if (batchCount == 1)
        {
            return ScheduleFastPathWithDependency(
                [func, context, length]() { ExecuteBatchSync(func, context, length); },
                context, cleanup, dependency);
        }

        return ScheduleWithDependency(dependency, [=](HandleState* state, auto executor) {
            // 当 batch 数较少时，使用 silent_async 直接提交每个 batch
            // 避免 taskflow 图构建开销
            if (batchCount <= workerCount * 2)
            {
                auto remaining = std::make_shared<std::atomic<int>>(batchCount);
                for (int i = 0; i < batchCount; ++i)
                {
                    int start = i * safeBatchSize;
                    int count = std::min(safeBatchSize, length - start);
                    AcquireState(state);
                    executor->silent_async([=]() {
                        try { func(context, start, count); }
                        catch (...) {}
                        if (--*remaining == 0)
                        {
                            if (cleanup)
                            {
                                try { cleanup(context); }
                                catch (...) {}
                            }
                            CompleteState(state);
                        }
                        ReleaseState(state);
                        });
                }
            }
            else
            {
                auto nextBatch = std::make_shared<std::atomic<int>>(0);
                auto completedBatches = std::make_shared<std::atomic<int>>(0);
                auto finalized = std::make_shared<std::atomic<bool>>(false);
                auto finalizeBatchJob = [=]() {
                    if (!finalized->exchange(true, std::memory_order_acq_rel))
                    {
                        if (cleanup)
                        {
                            try { cleanup(context); }
                            catch (...) {}
                        }
                        CompleteState(state);
                    }
                };
                auto runOneBatch = [=]() -> bool {
                    const int batchIdx = nextBatch->fetch_add(1, std::memory_order_relaxed);
                    if (batchIdx >= batchCount) return false;
                    const int start = batchIdx * safeBatchSize;
                    const int count = std::min(safeBatchSize, length - start);
                    try { func(context, start, count); }
                    catch (...) {}
                    if (completedBatches->fetch_add(1, std::memory_order_acq_rel) + 1 == batchCount)
                    {
                        finalizeBatchJob();
                    }
                    return true;
                };
                {
                    std::lock_guard<std::mutex> lock(state->mtx);
                    state->assistStep = runOneBatch;
                }

                auto taskflow = AcquireTaskflow();
                for (int w = 0; w < workerCount; ++w)
                {
                    taskflow->emplace([=]() {
                        while (runOneBatch()) {}
                    });
                }

                AcquireState(state);
                executor->run(*taskflow, [taskflow, state]() mutable {
                    ReleaseState(state);
                });
            }
            });
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
            batch->rangeSize = rangeSize > 0 ? rangeSize : 32;
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
            // DeferredPublish: 所有 C++/ISPC Job 统一走 worker 并行执行
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
