#include "JobSystem.h"
#include "ChunkJobData.h"
#include "EntityBatchData.h"
#include "JobProfiler.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "../../external/cpp-taskflow/taskflow/taskflow.hpp"

#include <algorithm>
#include <chrono>
#include <deque>
#include <memory>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#include <windows.h>
#endif

namespace JobSystem
{
    constexpr size_t kMaxPooledStates = 4096;
    constexpr size_t kMaxPooledTaskflows = 1024;
    constexpr int kSyncExecutionLengthThreshold = 512;
    constexpr int kSyncWithCompletedDepThreshold = 4096;

    // ---------- Globals ----------
    std::mutex g_executorMutex;
    std::shared_ptr<tf::Executor> g_executor;
    int g_numThreads = 0;

    std::mutex g_statePoolMutex;
    std::vector<HandleState*> g_statePool;

    std::mutex g_taskflowPoolMutex;
    std::vector<tf::Taskflow*> g_taskflowPool;

    // Stats — all counters restored
    std::atomic<uint64_t> g_completeWaitLoops{ 0 };
    std::atomic<uint64_t> g_assistAttempts{ 0 };
    std::atomic<uint64_t> g_assistExecuted{ 0 };
    std::atomic<uint64_t> g_frameTasksSubmitted{ 0 };
    std::atomic<uint64_t> g_workerExecutedRanges{ 0 };
    std::atomic<uint64_t> g_mainExecutedRanges{ 0 };
    std::atomic<uint64_t> g_stealCount{ 0 };
    std::atomic<uint64_t> g_parkWakeCount{ 0 };
    std::atomic<uint64_t> g_publishedJobs{ 0 };
    std::atomic<uint64_t> g_waitFallbacks{ 0 };
    std::atomic<uint64_t> g_notifiedWorkers{ 0 };
    std::atomic<uint64_t> g_workerClaimedTokens{ 0 };
    std::atomic<uint64_t> g_mainClaimedTokens{ 0 };
    std::atomic<uint64_t> g_activeWorkersPeak{ 0 };
    std::atomic<int64_t> g_wakeLatencyEwmaNs{ 300'000 };
    std::atomic<uint64_t> g_publishToCompletionEwmaNs{ 0 };
    std::atomic<uint64_t> g_perRangeExecEwmaNs{ 0 };
    std::atomic<uint64_t> g_nextDiagnosticBatchId{ 0 };
    std::atomic<bool> g_shuttingDown{ false };

    void UpdateUnsignedEwma(std::atomic<uint64_t>& target, uint64_t sample) noexcept
    {
        if (sample == 0) return;
        uint64_t current = target.load(std::memory_order_relaxed);
        while (true)
        {
            uint64_t next = current == 0
                ? sample
                : (sample >= current
                    ? current + (sample - current) / 8
                    : current - (current - sample) / 8);
            if (target.compare_exchange_weak(current, next, std::memory_order_relaxed)) return;
        }
    }

    void GetStatsSnapshot(JobSystemStatsSnapshot* stats) noexcept
    {
        if (!stats) return;
        stats->completeWaitLoops = g_completeWaitLoops.load(std::memory_order_relaxed);
        stats->assistAttempts = g_assistAttempts.load(std::memory_order_relaxed);
        stats->assistExecuted = g_assistExecuted.load(std::memory_order_relaxed);
        stats->frameTasksSubmitted = g_frameTasksSubmitted.load(std::memory_order_relaxed);
        stats->workerExecutedRanges = g_workerExecutedRanges.load(std::memory_order_relaxed);
        stats->mainExecutedRanges = g_mainExecutedRanges.load(std::memory_order_relaxed);
        stats->stealCount = g_stealCount.load(std::memory_order_relaxed);
        stats->parkWakeCount = g_parkWakeCount.load(std::memory_order_relaxed);
        stats->publishedJobs = g_publishedJobs.load(std::memory_order_relaxed);
        stats->waitFallbacks = g_waitFallbacks.load(std::memory_order_relaxed);
        stats->notifiedWorkers = g_notifiedWorkers.load(std::memory_order_relaxed);
        stats->workerClaimedTokens = g_workerClaimedTokens.load(std::memory_order_relaxed);
        stats->mainClaimedTokens = g_mainClaimedTokens.load(std::memory_order_relaxed);
        stats->activeWorkersPeak = g_activeWorkersPeak.load(std::memory_order_relaxed);
        stats->wakeLatencyEwmaNs = static_cast<uint64_t>(
            g_wakeLatencyEwmaNs.load(std::memory_order_relaxed));
        stats->publishToCompletionEwmaNs = g_publishToCompletionEwmaNs.load(std::memory_order_relaxed);
        stats->perRangeExecEwmaNs = g_perRangeExecEwmaNs.load(std::memory_order_relaxed);

        uint64_t attempts = g_assistAttempts.load(std::memory_order_relaxed);
        uint64_t executed = g_assistExecuted.load(std::memory_order_relaxed);
        stats->assistExecPctEwma = attempts > 0 ? (executed * 100 / attempts) : 0;

        uint64_t compUs = stats->publishToCompletionEwmaNs / 1000;
        uint64_t perUs = stats->perRangeExecEwmaNs / 1000;
        stats->completionOverheadUs = compUs > perUs ? compUs - perUs : 0;

        stats->frameTasksCompleted = 0;
        stats->deferredRuns = 0;
        stats->prewakeCount = 0;
        stats->hotSpinHits = 0;
        stats->coldBatches = 0;
        stats->scheduleModePublishNoAssist = 0;
        stats->scheduleModePublishAssist = 0;
        stats->scheduleModeDeferTinyOnly = 0;
        stats->scheduleModeImmediateNative = 0;
        stats->scheduleModeDeferredPublish = 0;
        stats->scheduleModeDeferredPublishNoAssist = 0;
        stats->frameQueueDepthPeak = 0;
        stats->directAssistClaims = 0;
        stats->exhaustedTickets = 0;
        stats->scheduleToPublishEwmaNs = 0;
        stats->publishToFirstMainClaimEwmaNs = 0;
        stats->publishToFirstWorkerClaimEwmaNs = 0;
        stats->queueLockWaitEwmaNs = 0;
    }

    void ResetStatsSnapshot() noexcept
    {
        g_completeWaitLoops.store(0, std::memory_order_relaxed);
        g_assistAttempts.store(0, std::memory_order_relaxed);
        g_assistExecuted.store(0, std::memory_order_relaxed);
        g_frameTasksSubmitted.store(0, std::memory_order_relaxed);
        g_workerExecutedRanges.store(0, std::memory_order_relaxed);
        g_mainExecutedRanges.store(0, std::memory_order_relaxed);
        g_stealCount.store(0, std::memory_order_relaxed);
        g_parkWakeCount.store(0, std::memory_order_relaxed);
        g_publishedJobs.store(0, std::memory_order_relaxed);
        g_waitFallbacks.store(0, std::memory_order_relaxed);
        g_notifiedWorkers.store(0, std::memory_order_relaxed);
        g_workerClaimedTokens.store(0, std::memory_order_relaxed);
        g_mainClaimedTokens.store(0, std::memory_order_relaxed);
        g_activeWorkersPeak.store(0, std::memory_order_relaxed);
        g_publishToCompletionEwmaNs.store(0, std::memory_order_relaxed);
        g_perRangeExecEwmaNs.store(0, std::memory_order_relaxed);
    }

    int CurrentWorkerCount()
    {
        return std::max(1, g_numThreads);
    }

    // ---------- State lifecycle (unchanged) ----------
    void RecycleState(HandleState* state) noexcept
    {
        if (!state) return;
        state->inlineContinuation = {};
        state->continuations.clear();
        state->waiterCount.store(0, std::memory_order_relaxed);
        state->diagnosticBatchId.store(0, std::memory_order_relaxed);
        state->completed.store(false, std::memory_order_relaxed);
        state->refCount.store(1, std::memory_order_relaxed);
        state->assistCallback.store(nullptr, std::memory_order_release);
        state->assistContext.store(nullptr, std::memory_order_release);
        state->assistReaders.store(0, std::memory_order_relaxed);
        state->assistReadersDrained.store(nullptr, std::memory_order_release);
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
            if (!g_statePool.empty()) { state = g_statePool.back(); g_statePool.pop_back(); }
        }
        if (!state) state = new HandleState(completed);
        state->refCount.store(1, std::memory_order_relaxed);
        state->completed.store(completed, std::memory_order_relaxed);
        state->waiterCount.store(0, std::memory_order_relaxed);
        state->diagnosticBatchId.store(0, std::memory_order_relaxed);
        state->inlineContinuation = {};
        state->continuations.clear();
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
            if (state->completed.exchange(true, std::memory_order_acq_rel)) return;
            inlineContinuation = std::move(state->inlineContinuation);
            continuations.swap(state->continuations);
        }
        state->completed.notify_all();
        if (inlineContinuation) { try { inlineContinuation(); } catch (...) {} }
        for (auto& cont : continuations)
            if (cont) { try { cont(); } catch (...) {} }
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
            if (state->completed.load(std::memory_order_acquire)) toRun = std::move(continuation);
            else if (!state->inlineContinuation) state->inlineContinuation = std::move(continuation);
            else state->continuations.emplace_back(std::move(continuation));
        }
        if (toRun) toRun();
    }

    // ---------- Taskflow helpers ----------
    void ReturnTaskflow(tf::Taskflow* taskflow) noexcept
    {
        if (!taskflow) return;
        taskflow->clear();
        std::lock_guard<std::mutex> lock(g_taskflowPoolMutex);
        if (g_taskflowPool.size() < kMaxPooledTaskflows) g_taskflowPool.push_back(taskflow);
        else delete taskflow;
    }

    std::shared_ptr<tf::Taskflow> AcquireTaskflow()
    {
        tf::Taskflow* taskflow = nullptr;
        {
            std::lock_guard<std::mutex> lock(g_taskflowPoolMutex);
            if (!g_taskflowPool.empty()) { taskflow = g_taskflowPool.back(); g_taskflowPool.pop_back(); }
        }
        if (!taskflow) taskflow = new tf::Taskflow();
        return std::shared_ptr<tf::Taskflow>(taskflow, [](tf::Taskflow* ptr) { ReturnTaskflow(ptr); });
    }

    std::shared_ptr<tf::Executor> EnsureExecutor()
    {
        std::lock_guard<std::mutex> lock(g_executorMutex);
        if (!g_executor)
        {
            auto hw = std::thread::hardware_concurrency();
            g_numThreads = (hw > 1) ? static_cast<int>(hw) - 1 : 1;
            g_executor = std::make_shared<tf::Executor>(static_cast<size_t>(g_numThreads));
        }
        return g_executor;
    }

    int ResolveChunkSize(int length, int requestedChunk)
    {
        if (length <= 0) return 1;
        if (requestedChunk > 0) return requestedChunk;
        int wc = std::max(1, g_numThreads);
        return std::max(64, (length + wc * 4 - 1) / (wc * 4));
    }

    // ============================================================
    // Tile + LocalPartition + half-steal
    // ============================================================
    static int ResolveWorkerTarget(int workerCap, int targetCount) noexcept
    {
        if (targetCount <= 0) return 1;
        const int cap = workerCap > 0 ? workerCap : g_numThreads;
        return std::max(1, std::min({ cap, g_numThreads, targetCount }));
    }
    // A Tile is the load-balancing unit — one or more chunks (IJobChunk)
    // or a sub-range of entities (IJobEntity).
    struct ExecutionTile {
        uint32_t firstChunk;    // Index into ChunkJobData array
        uint16_t chunkCount;    // Number of chunks in this tile (1 for IJobChunk)
        uint16_t flags;         // See kTileIsEntitySubRange
        uint32_t firstEntity;   // First entity index (for IJobEntity sub-tiles)
        uint32_t entityCount;   // Number of entities
    };
    constexpr uint16_t kTileIsEntitySubRange = 1;

    // Local partition — stores tile range [front, back) for stealing-capable local execution.
    struct LocalPartition {
        std::atomic<uint32_t> front{ 0 };
        std::atomic<uint32_t> back{ 0 };
        uint32_t initialFront{ 0 };
        uint32_t initialBack{ 0 };
        uint32_t ownerSlot{ 0 };
        uint32_t localTiles{ 0 };
        uint32_t stolenTiles{ 0 };
    };

    // Build partitions: continuous ranges (not round-robin).
    static void BuildPartitions(LocalPartition* parts, int count, int tileCount) noexcept
    {
        for (int s = 0; s < count; s++)
        {
            uint32_t b = static_cast<uint32_t>(static_cast<uint64_t>(tileCount) * s / count);
            uint32_t e = static_cast<uint32_t>(static_cast<uint64_t>(tileCount) * (s + 1) / count);
            parts[s].front.store(b, std::memory_order_relaxed);
            parts[s].back.store(e, std::memory_order_relaxed);
            parts[s].initialFront = b;
            parts[s].initialBack = e;
            parts[s].ownerSlot = static_cast<uint32_t>(s);
        }
    }

    // Worker: claim next tile from its own partition (front → back)
    static bool TryTakeLocal(LocalPartition& part, uint32_t& tileIdx) noexcept
    {
        uint32_t f = part.front.load(std::memory_order_relaxed);
        while (true)
        {
            uint32_t b = part.back.load(std::memory_order_acquire);
            if (f >= b) return false;
            if (part.front.compare_exchange_weak(f, f + 1,
                std::memory_order_acq_rel, std::memory_order_relaxed))
            {
                tileIdx = f;
                return true;
            }
        }
    }

    // Thief: steal half of remaining tiles from victim's back
    static bool TryStealHalf(LocalPartition& victim, uint32_t& stolenStart, uint32_t& stolenEnd) noexcept
    {
        uint32_t b = victim.back.load(std::memory_order_acquire);
        while (true)
        {
            uint32_t f = victim.front.load(std::memory_order_acquire);
            uint32_t remaining = b > f ? b - f : 0;
            if (remaining <= 1) return false;
            uint32_t half = remaining / 2;
            uint32_t nb = b - half;
            if (victim.back.compare_exchange_weak(b, nb,
                std::memory_order_acq_rel, std::memory_order_relaxed))
            {
                stolenStart = nb;
                stolenEnd = b;
                return true;
            }
        }
    }

    // ============================================================
    // BatchState
    // ============================================================
    struct BatchState {
        HandleState* handle{ nullptr };
        void* context{ nullptr };
        void (*cleanup)(void*){ nullptr };

        bool (*executeTile)(void* ctx, const ExecutionTile& tile) noexcept{ nullptr };

        // Old path (nextRange)
        bool (*processRange)(void* ctx, int start, int count) noexcept{ nullptr };
        int rangeCount{ 0 };
        int rangeSize{ 1 };
        int totalItems{ 0 };
        std::atomic<int> nextRange{ 0 };

        // New path (partitions)
        ExecutionTile* tiles{ nullptr };
        uint32_t tileCount{ 0 };
        LocalPartition* partitions{ nullptr };
        uint32_t partitionCount{ 0 };
        std::atomic<uint32_t> completedTiles{ 0 };

        std::atomic<int> completedRanges{ 0 };
        std::atomic<bool> finalized{ false };
        std::atomic<bool> workersFinished{ false };

        uint64_t diagnosticId{ 0 };
    };

    static bool TryExecuteOneRange(BatchState* batch) noexcept
    {
        if (!batch) return false;
        const int index = batch->nextRange.fetch_add(1, std::memory_order_relaxed);
        if (index >= batch->rangeCount) return false;

        PushTraceEvent(TraceEventType::Claim, batch->diagnosticId,
            index, index * batch->rangeSize, batch->rangeSize);
        PushTraceEvent(TraceEventType::ExecuteBegin, batch->diagnosticId,
            index, index * batch->rangeSize, batch->rangeSize);
        g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);
        batch->processRange(batch->context, index, 1);
        PushTraceEvent(TraceEventType::ExecuteEnd, batch->diagnosticId,
            index, index * batch->rangeSize, batch->rangeSize);
        batch->completedRanges.fetch_add(1, std::memory_order_acq_rel);
        return true;
    }

    static bool AssistExecuteOneRange(void* ptr) noexcept
    {
        auto* batch = static_cast<BatchState*>(ptr);
        return TryExecuteOneRange(batch);
    }

    // ============================================================
    // Partition-based execution (Phase 1)
    // ============================================================
    // Process one tile and update completion counter.
    // Returns true if the tile was processed (for assist comptability).
    static bool TryExecuteOneTile(BatchState* batch, uint32_t tileIndex) noexcept
    {
        if (!batch || tileIndex >= batch->tileCount) return false;

        const auto& tile = batch->tiles[tileIndex];
        PushTraceEvent(TraceEventType::Claim, batch->diagnosticId,
            static_cast<int>(tileIndex),
            static_cast<int>(tile.firstChunk),
            static_cast<int>(tile.chunkCount));
        PushTraceEvent(TraceEventType::ExecuteBegin, batch->diagnosticId,
            static_cast<int>(tileIndex),
            static_cast<int>(tile.firstChunk),
            static_cast<int>(tile.chunkCount));
        g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);
        batch->executeTile(batch->context, batch->tiles[tileIndex]);
        PushTraceEvent(TraceEventType::ExecuteEnd, batch->diagnosticId,
            static_cast<int>(tileIndex),
            static_cast<int>(tile.firstChunk),
            static_cast<int>(tile.chunkCount));
        batch->completedTiles.fetch_add(1, std::memory_order_acq_rel);
        return true;
    }

    // Worker loop for partition mode:
    // 1. Process local tiles from partition[slot] front
    // 2. Steal half from victims
    static void WorkerPartitionLoop(BatchState* batch, uint32_t slot) noexcept
    {
        if (!batch || slot >= batch->partitionCount || batch->tileCount == 0) return;

        LocalPartition& mine = batch->partitions[slot];
        uint32_t tileIdx;

        // Phase 1: Local tiles
        while (TryTakeLocal(mine, tileIdx))
        {
            TryExecuteOneTile(batch, tileIdx);
            mine.localTiles++;
        }

        // Phase 2: Half-steal from victims
        for (uint32_t v = 0; v < batch->partitionCount; v++)
        {
            uint32_t victimIdx = (slot + 1 + v) % batch->partitionCount;
            if (victimIdx == slot) continue;

            uint32_t stealStart, stealEnd;
            while (TryStealHalf(batch->partitions[victimIdx], stealStart, stealEnd))
            {
                for (uint32_t t = stealStart; t < stealEnd; t++)
                {
                    TryExecuteOneTile(batch, t);
                    mine.stolenTiles++;
                }
            }
        }
    }

    static bool AssistExecuteOneTile(void* ptr) noexcept
    {
        auto* batch = static_cast<BatchState*>(ptr);
        // In partition mode, assist by finding partition with most remaining
        uint32_t bestVictim = ~0u;
        uint32_t bestRemaining = 0;
        for (uint32_t p = 0; p < batch->partitionCount; p++)
        {
            auto& part = batch->partitions[p];
            uint32_t f = part.front.load(std::memory_order_acquire);
            uint32_t b = part.back.load(std::memory_order_acquire);
            uint32_t rem = b > f ? b - f : 0;
            if (rem > bestRemaining) { bestRemaining = rem; bestVictim = p; }
        }
        if (bestVictim == ~0u || bestRemaining == 0) return false;

        // Try half-steal first (best for load balance)
        uint32_t ss, se;
        if (TryStealHalf(batch->partitions[bestVictim], ss, se))
        {
            for (uint32_t t = ss; t < se; t++)
                TryExecuteOneTile(batch, t);
            return true;
        }

        // Fallback: single remaining tile — take it from front via TryTakeLocal.
        // This handles the case where TryStealHalf cannot split remaining=1.
        uint32_t tileIdx;
        if (TryTakeLocal(batch->partitions[bestVictim], tileIdx))
        {
            TryExecuteOneTile(batch, tileIdx);
            return true;
        }
        return false;
    }

    static void ReleaseBatch(BatchState* batch) noexcept
    {
        if (!batch) return;
        if (batch->partitions) { delete[] batch->partitions; batch->partitions = nullptr; }
        if (batch->tiles)      { delete[] batch->tiles;      batch->tiles = nullptr; }
        delete batch;
    }

    static void TryFinalizeCompletedBatch(HandleState* state) noexcept
    {
        if (!state) return;

        BatchState* batch = nullptr;
        uint64_t diagnosticId = 0;
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            batch = static_cast<BatchState*>(
                state->assistContext.load(std::memory_order_acquire));
            if (!batch ||
                !batch->workersFinished.load(std::memory_order_acquire) ||
                state->assistReaders.load(std::memory_order_acquire) != 0)
            {
                return;
            }

            diagnosticId = batch->diagnosticId;
            state->assistContext.store(nullptr, std::memory_order_release);
            state->assistReadersDrained.store(nullptr, std::memory_order_release);
        }

        if (!batch->finalized.exchange(true, std::memory_order_acq_rel))
        {
            PushTraceEvent(TraceEventType::FinalizeBegin, diagnosticId, -1, 0, 0);
            if (batch->cleanup) batch->cleanup(batch->context);
            // Push HandleComplete before CompleteState so the event is
            // recorded before CompleteState's notify_all() wakes a waiter
            // that could race with TraceSetEnabled(false).
            PushTraceEvent(TraceEventType::HandleComplete, diagnosticId, -1, 0, 0);
            CompleteState(state);
        }
        ReleaseBatch(batch);
    }

    // The last assist reader only requests finalization. Batch memory remains
    // alive until Taskflow has also finished every worker task.
    static void OnAssistReadersDrained(void* handlePtr) noexcept
    {
        TryFinalizeCompletedBatch(static_cast<HandleState*>(handlePtr));
    }

    // Acquire assist reader: returns false if batch is already finalized
    // Submit a BatchState via taskflow
    static void SubmitBatch(BatchState* batch, const std::shared_ptr<tf::Executor>& executor,
        int /*workerCap*/ = 0)
    {
        auto* state = batch->handle;
        auto taskflow = AcquireTaskflow();

        bool (*assistFn)(void*) noexcept = nullptr;
        const int participantCount = std::max(1, static_cast<int>(batch->partitionCount));

        if (batch->partitions)
        {
            // New path: partition-based — pass slot directly, no nextPartitionSlot
            for (int slot = 0; slot < participantCount; ++slot)
            {
                taskflow->emplace([batch, slot]() { WorkerPartitionLoop(batch, static_cast<uint32_t>(slot)); });
            }
            assistFn = &AssistExecuteOneTile;
        }
        else
        {
            // Old path: global nextRange — still capped by participantCount
            for (int slot = 0; slot < participantCount; ++slot)
            {
                taskflow->emplace([batch]() {
                    while (TryExecuteOneRange(batch)) {}
                });
            }
            assistFn = &AssistExecuteOneRange;
        }

        g_frameTasksSubmitted.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);
        g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
        g_notifiedWorkers.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);

        // Register assist callback + readersDrained for Complete()
        state->assistCallback.store(assistFn, std::memory_order_release);
        state->assistContext.store(batch, std::memory_order_release);
        state->assistReadersDrained.store(&OnAssistReadersDrained, std::memory_order_release);

        uint64_t diagId = batch->diagnosticId;
        if (diagId != 0)
        {
            state->diagnosticBatchId.store(diagId, std::memory_order_release);
        }

        AcquireState(state);
        executor->run(*taskflow, [taskflow, state, batch]() mutable {
            // Clear assist to prevent new readers from attaching.
            state->assistCallback.store(nullptr, std::memory_order_release);

            // Cleanup and Batch release must also wait for any Complete()
            // assist callback that is still executing outside Taskflow.
            batch->workersFinished.store(true, std::memory_order_release);
            TryFinalizeCompletedBatch(state);

            ReleaseState(state);
        });
    }

    // ---------- Chunk/Entity adaptors ----------
    struct ChunkBatchContext {
        void (*func)(void*, const ChunkJobData*);
        void (*rangeFunc)(void*, const ChunkJobData*, int, int);
        void (*entityRangeFunc)(void*, const EntityBatchData*, int, int);
        void* originalContext;
        void (*originalCleanup)(void*);
        const ChunkJobData* chunks;
        const EntityBatchData* entityBatches;
        int itemCount;
        int rangeSize;
        int execMode;
    };

    static bool ProcessChunkRange(void* ctx, int start, int count) noexcept
    {
        auto* bc = static_cast<ChunkBatchContext*>(ctx);
        switch (bc->execMode)
        {
        case 0: {
            int firstChunk = start * bc->rangeSize;
            int lastChunk = std::min(firstChunk + bc->rangeSize, bc->itemCount);
            for (int i = firstChunk; i < lastChunk; i++)
                bc->func(bc->originalContext, &bc->chunks[i]);
            break;
        }
        case 1: {
            int first = start * bc->rangeSize;
            int cnt = std::min(bc->rangeSize, bc->itemCount - first);
            bc->rangeFunc(bc->originalContext, bc->chunks, first, cnt);
            break;
        }
        case 2: {
            int first = start * bc->rangeSize;
            int cnt = std::min(bc->rangeSize, bc->itemCount - first);
            bc->entityRangeFunc(bc->originalContext, bc->entityBatches, first, cnt);
            break;
        }
        }
        return true;
    }

    // Tile-based executor for chunk batches (partition path, execMode 0)
    static bool ChunkExecuteTile(void* ctx, const ExecutionTile& tile) noexcept
    {
        auto* bc = static_cast<ChunkBatchContext*>(ctx);
        // Each tile maps to chunks[tile.firstChunk]
        bc->func(bc->originalContext, &bc->chunks[tile.firstChunk]);
        return true;
    }

    static void CleanupChunkContext(void* ctx) noexcept
    {
        auto* bc = static_cast<ChunkBatchContext*>(ctx);
        if (bc->originalCleanup) bc->originalCleanup(bc->originalContext);
        delete bc;
    }

    struct GeneralBatchContext {
        void (*indexFunc)(void*, int);
        void (*batchFunc)(void*, int, int);
        void* originalContext;
        void (*originalCleanup)(void*);
        int length;
        int batchSize;
    };

    static bool ProcessGeneralRange(void* ctx, int start, int count) noexcept
    {
        auto* bc = static_cast<GeneralBatchContext*>(ctx);
        int s = start * bc->batchSize;
        int e = std::min(s + bc->batchSize * count, bc->length);
        if (bc->batchFunc) bc->batchFunc(bc->originalContext, s, e - s);
        else for (int i = s; i < e; i++) bc->indexFunc(bc->originalContext, i);
        return true;
    }

    static void CleanupGeneralContext(void* ctx) noexcept
    {
        auto* bc = static_cast<GeneralBatchContext*>(ctx);
        if (bc->originalCleanup) bc->originalCleanup(bc->originalContext);
        delete bc;
    }

    // ============================================================
    // JobHandle
    // ============================================================
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

    static inline void CpuPause() noexcept
    {
#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
        _mm_pause();
#endif
    }

    void JobHandle::Complete() const
    {
        if (!_state || _state->completed.load(std::memory_order_acquire)) return;

        // Phase 0: Assist — reader count on HandleState (safe, outlives batch)
        _state->assistReaders.fetch_add(1, std::memory_order_acq_rel);
        auto cb = _state->assistCallback.load(std::memory_order_acquire);
        auto ctx = _state->assistContext.load(std::memory_order_acquire);
        if (cb && ctx && !_state->completed.load(std::memory_order_acquire))
        {
            g_assistAttempts.fetch_add(1, std::memory_order_relaxed);
            while (!_state->completed.load(std::memory_order_acquire))
            {
                if (!cb(ctx)) break;
                g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
                g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
            }
        }
        if (_state->assistReaders.fetch_sub(1, std::memory_order_acq_rel) == 1)
        {
            auto drained = _state->assistReadersDrained.load(std::memory_order_acquire);
            if (drained) drained(_state);
        }
        if (_state->completed.load(std::memory_order_acquire)) return;

        // Phase 2: yield + spin
        std::this_thread::yield();
        for (int i = 0; i < 64; i++)
        {
            if (_state->completed.load(std::memory_order_acquire)) return;
            CpuPause();
        }
        if (_state->completed.load(std::memory_order_acquire)) return;

        // Phase 3: blocking wait
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
        std::vector<HandleState*> pending;
        for (const auto& h : handles)
            if (h._state && !h._state->completed.load(std::memory_order_acquire))
                pending.push_back(h._state);
        if (pending.empty()) return JobHandle(CreateState(true));
        auto* cs = CreateState(false);
        auto remaining = std::make_shared<std::atomic<int>>(static_cast<int>(pending.size()));
        for (auto* ds : pending) {
            AcquireState(cs);
            AddContinuationOrRunNow(ds, [cs, remaining]() {
                if (remaining->fetch_sub(1, std::memory_order_acq_rel) == 1)
                    CompleteState(cs);
                ReleaseState(cs);
            });
        }
        return JobHandle(cs);
    }

    // ============================================================
    // Schedule helpers
    // ============================================================
    template <typename WorkBuilder>
    JobHandle ScheduleWithDependency(const JobHandle& dep, WorkBuilder&& builder)
    {
        auto* state = CreateState(false);
        auto exec = EnsureExecutor();
        auto* ds = dep.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { builder(state, exec); return JobHandle(state); }
        AcquireState(state);
        AddContinuationOrRunNow(ds, [state, exec, b = std::forward<WorkBuilder>(builder)]() mutable {
            b(state, exec);
            ReleaseState(state);
        });
        return JobHandle(state);
    }

    template <typename Work>
    void FastPath(Work&& work, void* ctx, void (*cleanup)(void*), HandleState* state,
        const std::shared_ptr<tf::Executor>& exec)
    {
        AcquireState(state);
        exec->silent_async([work = std::forward<Work>(work), state, ctx, cleanup]() {
            try { work(); } catch (...) {}
            if (cleanup) cleanup(ctx);
            CompleteState(state);
            ReleaseState(state);
        });
    }

    template <typename Work>
    JobHandle ScheduleFastPath(Work&& work, void* ctx, void (*cleanup)(void*), const JobHandle& dep)
    {
        auto* state = CreateState(false);
        auto exec = EnsureExecutor();
        auto* ds = dep.State();
        if (!ds || ds->completed.load(std::memory_order_acquire))
        { FastPath(std::forward<Work>(work), ctx, cleanup, state, exec); return JobHandle(state); }
        AcquireState(state);
        AddContinuationOrRunNow(ds, [state, exec, work = std::forward<Work>(work), ctx, cleanup]() mutable {
            FastPath(std::forward<Work>(work), ctx, cleanup, state, exec);
            ReleaseState(state);
        });
        return JobHandle(state);
    }

    // ============================================================
    // Scheduler
    // ============================================================
    void Scheduler::Initialize(int numThreads)
    {
        g_shuttingDown.store(false, std::memory_order_release);
        std::shared_ptr<tf::Executor> oldExec;
        {
            std::lock_guard<std::mutex> lock(g_executorMutex);
            int resolved = numThreads > 0 ? numThreads :
                (g_numThreads > 0 ? g_numThreads :
                    std::max(1, static_cast<int>(std::thread::hardware_concurrency()) - 1));
            if (g_executor && g_numThreads == resolved) return;
            oldExec = std::move(g_executor); g_numThreads = resolved;
            g_executor = std::make_shared<tf::Executor>(static_cast<size_t>(resolved));

#if defined(_WIN32)
            SYSTEM_INFO si; GetSystemInfo(&si);
            DWORD_PTR allCores = static_cast<DWORD_PTR>(si.dwActiveProcessorMask);
            for (int i = 0; i < resolved; i++)
                g_executor->silent_async([i, resolved, allCores]() {
                    if (allCores) {
                        int ci = 0;
                        for (int c = 0; c < (int)(sizeof(DWORD_PTR)*8) && ci <= i; c++)
                            if (allCores & ((DWORD_PTR)1 << c))
                                { if (ci++ == i) { SetThreadAffinityMask(GetCurrentThread(), (DWORD_PTR)1 << c); break; } }
                    }
                    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_NORMAL);
                });
            g_executor->wait_for_all();
#endif
        }
        if (oldExec) oldExec->wait_for_all();
    }

    void Scheduler::Shutdown()
    {
        g_shuttingDown.store(true, std::memory_order_release);
        std::shared_ptr<tf::Executor> exec;
        { std::lock_guard<std::mutex> lock(g_executorMutex); exec = std::move(g_executor); g_numThreads = 0; }
        if (exec) exec->wait_for_all();
        { std::lock_guard<std::mutex> lock(g_statePoolMutex); for (auto* s : g_statePool) delete s; g_statePool.clear(); }
        { std::lock_guard<std::mutex> lock(g_taskflowPoolMutex); for (auto* tf : g_taskflowPool) delete tf; g_taskflowPool.clear(); }
    }

    void Scheduler::PrewakeWorkers()
    {
        auto exec = EnsureExecutor();
        // Submit NOP tasks to wake all workers
        auto taskflow = AcquireTaskflow();
        for (int i = 0; i < g_numThreads; i++)
            taskflow->emplace([]() { });
        exec->run(*taskflow, [taskflow]() mutable { });
    }

    void Scheduler::KeepWorkersWarm(int /*microseconds*/)
    {
        // No-op: taskflow manages its own thread lifecycle
    }

    void Scheduler::SetFrameLowLatencyMode(bool /*enabled*/) {}
    void Scheduler::FlushScheduledJobs() {}

    // ---------- IJob ----------
    JobHandle Scheduler::Schedule(void (*func)(void*), void* context, void (*cleanup)(void*), const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire)) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if (!func) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if (!dependency.State() || dependency.IsCompleted()) { func(context); if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        return ScheduleFastPath([func, context]() { func(context); }, context, cleanup, dependency);
    }

    // ---------- IJobFor ----------
    JobHandle Scheduler::ScheduleFor(void (*func)(void*, int), void* context, int length, void (*cleanup)(void*), const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire)) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if (!func || length <= 0) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        bool depOk = !dependency.State() || dependency.IsCompleted();
        if (length <= kSyncExecutionLengthThreshold || (depOk && length <= kSyncWithCompletedDepThreshold))
        { for (int i = 0; i < length; i++) func(context, i); if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if (length <= 64) return ScheduleFastPath([func, context, length]() { for (int i = 0; i < length; i++) func(context, i); }, context, cleanup, dependency);
        return ScheduleWithDependency(dependency, [func, context, length, cleanup](HandleState* state, auto exec) {
            auto tf = AcquireTaskflow();
            tf->emplace([func, context, length]() { for (int i = 0; i < length; i++) func(context, i); });
            AcquireState(state);
            exec->run(*tf, [tf, state, context, cleanup]() mutable { if (cleanup) cleanup(context); CompleteState(state); ReleaseState(state); });
        });
    }

    // ---------- IJobParallelFor ----------
    JobHandle Scheduler::ScheduleParallelFor(void (*func)(void*, int), void* context, int length, int batchSize, void (*cleanup)(void*), const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire)) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if (!func || length <= 0) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        bool depOk = !dependency.State() || dependency.IsCompleted();
        if (length <= kSyncExecutionLengthThreshold || (depOk && length <= kSyncWithCompletedDepThreshold))
        { for (int i = 0; i < length; i++) func(context, i); if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        int cs = ResolveChunkSize(length, batchSize);
        int rc = (length + cs - 1) / cs;
        if (rc <= 1) return ScheduleFastPath([func, context, length]() { for (int i = 0; i < length; i++) func(context, i); }, context, cleanup, dependency);

        auto* bc = new GeneralBatchContext{ func, nullptr, context, cleanup, length, cs };
        auto* batch = new BatchState();
        auto* state = CreateState(false); batch->handle = state;
        batch->context = bc; batch->cleanup = [](void* ctx) { CleanupGeneralContext(ctx); };
        batch->processRange = &ProcessGeneralRange; batch->rangeCount = rc; batch->rangeSize = cs;
        batch->partitionCount = static_cast<uint32_t>(ResolveWorkerTarget(0, rc));
        batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

        PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

        auto exec = EnsureExecutor();
        auto* ds = dependency.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitBatch(batch, exec); }
        else { AcquireState(state); AddContinuationOrRunNow(ds, [state, batch, exec]() { SubmitBatch(batch, exec); ReleaseState(state); }); }
        return JobHandle(state);
    }

    // ---------- IJobParallelForBatch ----------
    JobHandle Scheduler::ScheduleParallelForBatch
    (void (*func)(void*, int, int), void* context, int length, int batchSize, void (*cleanup)(void*), const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire)) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if (!func || length <= 0) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        bool depOk = !dependency.State() || dependency.IsCompleted();
        bool forceAsync = batchSize < 0; int reqBatch = forceAsync ? -batchSize : batchSize;
        if (!forceAsync && (length <= kSyncExecutionLengthThreshold || (depOk && length <= kSyncWithCompletedDepThreshold)))
        { func(context, 0, length); if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        int cs = std::max(1, reqBatch > 0 ? reqBatch : ResolveChunkSize(length, 0));
        int rc = (length + cs - 1) / cs;
        if (rc <= 1) { func(context, 0, length); if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }

        auto* bc = new GeneralBatchContext{ nullptr, func, context, cleanup, length, cs };
        auto* batch = new BatchState(); auto* state = CreateState(false); batch->handle = state;
        batch->context = bc; batch->cleanup = [](void* ctx) { CleanupGeneralContext(ctx); };
        batch->processRange = &ProcessGeneralRange; batch->rangeCount = rc; batch->rangeSize = cs;
        batch->partitionCount = static_cast<uint32_t>(ResolveWorkerTarget(0, rc));
        batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

        PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

        auto exec = EnsureExecutor();
        auto* ds = dependency.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitBatch(batch, exec); }
        else { AcquireState(state); AddContinuationOrRunNow(ds, [state, batch, exec]() { SubmitBatch(batch, exec); ReleaseState(state); }); }
        return JobHandle(state);
    }

    // ---------- ScheduleChunkBatchCore ----------
    static JobHandle ScheduleChunkBatchCore(
        void (*func)(void*, const ChunkJobData*), void (*rangeFunc)(void*, const ChunkJobData*, int, int),
        void (*entityRangeFunc)(void*, const EntityBatchData*, int, int),
        void* context, void (*cleanup)(void*),
        const ChunkJobData* chunks, const EntityBatchData* batches,
        int itemCount, const JobHandle& dependency,
        ChunkScheduleMode, int workerCap, int rangeSize)
    {
        if (g_shuttingDown.load(std::memory_order_acquire)) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if ((!func && !rangeFunc && !entityRangeFunc) || itemCount <= 0) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }

        int wc = std::max(1, g_numThreads);
        int rs = rangeSize > 0 ? rangeSize : std::max(1, itemCount / (wc * 4 + 1));
        int rc = (itemCount + rs - 1) / rs;

        // Inline for trivial work
        if (rc <= 1 && workerCap <= 1)
        {
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            if (func) { for (int i = 0; i < itemCount; i++) func(context, &chunks[i]); }
            else if (rangeFunc) { rangeFunc(context, chunks, 0, itemCount); }
            else if (entityRangeFunc) { entityRangeFunc(context, batches, 0, itemCount); }
            if (cleanup) cleanup(context);
            return JobHandle(CreateState(true));
        }

        int execMode = func ? 0 : (rangeFunc ? 1 : 2);
        auto* cc = new ChunkBatchContext{ func, rangeFunc, entityRangeFunc, context, cleanup,
            chunks, batches, itemCount, rs, execMode };
        auto* batch = new BatchState();
        auto* state = CreateState(false); batch->handle = state;
        batch->context = cc; batch->cleanup = &CleanupChunkContext;
        batch->rangeCount = rc; batch->rangeSize = rs; batch->totalItems = itemCount;
        batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

        // Phase 1 path: always use partitions + half-steal for IJobChunk
        if (execMode == 0 && func)
        {
            // Build tiles: one tile per chunk
            uint32_t tileCount = static_cast<uint32_t>(itemCount);
            auto* tiles = new ExecutionTile[tileCount];
            for (uint32_t i = 0; i < tileCount; i++)
            {
                tiles[i].firstChunk = i;
                tiles[i].chunkCount = 1;
                tiles[i].flags = 0;
                tiles[i].firstEntity = 0;
                tiles[i].entityCount = 0;
            }

            // Respect workerCap when choosing partition count
            int targetWorkers = ResolveWorkerTarget(workerCap, static_cast<int>(tileCount));
            auto* parts = new LocalPartition[static_cast<size_t>(targetWorkers)]();
            BuildPartitions(parts, targetWorkers, static_cast<int>(tileCount));

            batch->executeTile = &ChunkExecuteTile;
            batch->tiles = tiles;
            batch->tileCount = tileCount;
            batch->partitions = parts;
            batch->partitionCount = static_cast<uint32_t>(targetWorkers);
            batch->completedTiles.store(0, std::memory_order_relaxed);
        }
        else
        {
            // Fallback for range/entity mode: global nextRange
            batch->processRange = &ProcessChunkRange;
            batch->partitionCount = static_cast<uint32_t>(ResolveWorkerTarget(workerCap, rc));
        }

        PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

        auto exec = EnsureExecutor();
        auto* ds = dependency.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitBatch(batch, exec, workerCap); }
        else { AcquireState(state); AddContinuationOrRunNow(ds, [state, batch, exec, workerCap]() { SubmitBatch(batch, exec, workerCap); ReleaseState(state); }); }
        return JobHandle(state);
    }

    JobHandle Scheduler::ScheduleChunks(void (*f)(void*, const ChunkJobData*), void* ctx, void (*cl)(void*),
        const ChunkJobData* chunks, int cc, const JobHandle& dep, ChunkScheduleMode mode, int wc, int rs)
    { return ScheduleChunkBatchCore(f, nullptr, nullptr, ctx, cl, chunks, nullptr, cc, dep, mode, wc, rs); }

    JobHandle Scheduler::ScheduleChunkRanges(void (*f)(void*, const ChunkJobData*, int, int), void* ctx, void (*cl)(void*),
        const ChunkJobData* chunks, int cc, const JobHandle& dep, ChunkScheduleMode mode, int wc, int rs)
    { return ScheduleChunkBatchCore(nullptr, f, nullptr, ctx, cl, chunks, nullptr, cc, dep, mode, wc, rs); }

    JobHandle Scheduler::ScheduleEntityBatches(void (*f)(void*, const EntityBatchData*, int, int), void* ctx, void (*cl)(void*),
        const EntityBatchData* batches, int bc, const JobHandle& dep, ChunkScheduleMode mode, int wc, int rs)
    { return ScheduleChunkBatchCore(nullptr, nullptr, f, ctx, cl, nullptr, batches, bc, dep, mode, wc, rs); }

} // namespace JobSystem
