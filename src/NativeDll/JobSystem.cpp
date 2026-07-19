#include "JobSystem.h"
#include "ChunkJobData.h"
#include "EntityBatchData.h"
#include "JobProfiler.h"
#include "NativeWorkerPool.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "../../external/cpp-taskflow/taskflow/taskflow.hpp"

#include <algorithm>
#include <array>
#include <chrono>
#include <cctype>
#include <cstdlib>
#include <deque>
#include <memory>
#include <mutex>
#include <limits>
#include <thread>
#include <string>
#include <utility>
#include <vector>

#if defined(__linux__)
#include <sched.h>
#endif

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#include <windows.h>
#endif

namespace JobSystem
{
    constexpr size_t kMaxPooledStates = 4096;
    constexpr size_t kMaxPooledTaskflows = 1024;
    constexpr size_t kMaxPooledBatchStorage = 256;
    constexpr int kSyncExecutionLengthThreshold = 512;
    constexpr int kSyncWithCompletedDepThreshold = 4096;

    // ---------- Globals ----------
    std::mutex g_executorMutex;
    std::shared_ptr<tf::Executor> g_executor;
    std::unique_ptr<NativeWorkerPool> g_nativeWorkerPool;
    ExecutionBackend g_executionBackend{ ExecutionBackend::Taskflow };
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
    std::atomic<uint64_t> g_activeWorkers{ 0 };
    std::atomic<uint64_t> g_workerTargetTotal{ 0 };
    std::atomic<uint64_t> g_totalTilesPublished{ 0 };
    std::atomic<uint64_t> g_localTiles{ 0 };
    std::atomic<uint64_t> g_stolenTiles{ 0 };
    std::atomic<uint64_t> g_assistTiles{ 0 };
    std::atomic<uint64_t> g_stealAttempts{ 0 };
    std::atomic<uint64_t> g_stealSuccesses{ 0 };
    std::atomic<uint64_t> g_victimScans{ 0 };
    std::atomic<uint64_t> g_stealEmptyExits{ 0 };
    std::atomic<uint64_t> g_batchStorageCreated{ 0 };
    std::atomic<uint64_t> g_batchStorageReused{ 0 };
    std::atomic<uint64_t> g_batchStorageReturned{ 0 };
    std::atomic<uint64_t> g_batchStorageDropped{ 0 };
    std::atomic<uint64_t> g_submitToFirstWorkerEwmaNs{ 0 };
    std::atomic<uint64_t> g_workerStartSpreadEwmaNs{ 0 };
    std::atomic<uint64_t> g_lastTileToTopologyDoneEwmaNs{ 0 };
    std::atomic<uint64_t> g_completeWakeToReturnEwmaNs{ 0 };
    std::atomic<uint64_t> g_taskflowBatches{ 0 };
    std::atomic<uint64_t> g_nativeBatches{ 0 };
    std::atomic<uint64_t> g_invalidBackendSelections{ 0 };
    std::atomic<int64_t> g_wakeLatencyEwmaNs{ 300'000 };
    std::atomic<uint64_t> g_publishToCompletionEwmaNs{ 0 };
    std::atomic<uint64_t> g_perRangeExecEwmaNs{ 0 };
    std::atomic<uint64_t> g_nextDiagnosticBatchId{ 0 };
    std::atomic<bool> g_shuttingDown{ false };
    std::atomic<bool> g_timingDiagnosticsEnabled{ false };

    constexpr size_t kBatchTimingSampleCapacity = 2048;
    struct BatchTimingSample
    {
        uint64_t batchId{ 0 };
        uint64_t batchTotalNs{ 0 };
        uint64_t submitToFirstWorkerNs{ 0 };
        uint64_t workerStartSpreadNs{ 0 };
        uint64_t executionSpanNs{ 0 };
        uint64_t maxRangeNs{ 0 };
        uint64_t slowRangeThreadCpuNs{ 0 };
        uint64_t slowRangeThreadCycles{ 0 };
        uint64_t minRangeThreadCycles{ 0 };
        uint64_t averageRangeThreadCycles{ 0 };
        uint64_t coreMigrations{ 0 };
        uint64_t assistTiles{ 0 };
        int32_t slowRangeIndex{ -1 };
        int32_t slowRangeWorker{ -1 };
        int32_t slowRangeStartLogicalCore{ -1 };
        int32_t slowRangeEndLogicalCore{ -1 };
        int32_t slowRangeStartPhysicalCore{ -1 };
        int32_t slowRangeEndPhysicalCore{ -1 };
    };

    std::mutex g_batchTimingMutex;
    std::array<BatchTimingSample, kBatchTimingSampleCapacity> g_batchTimingSamples{};
    size_t g_batchTimingSampleCount{ 0 };
    uint64_t g_batchTimingSamplesDropped{ 0 };
    BatchTimingSample g_slowestBatch{};

    static void RecordBatchTiming(const BatchTimingSample& sample) noexcept
    {
        std::lock_guard<std::mutex> lock(g_batchTimingMutex);
        if (g_batchTimingSampleCount < g_batchTimingSamples.size())
            g_batchTimingSamples[g_batchTimingSampleCount++] = sample;
        else
            ++g_batchTimingSamplesDropped;

        if (sample.batchTotalNs >= g_slowestBatch.batchTotalNs)
            g_slowestBatch = sample;
    }

    template <typename Selector>
    static void PopulateTimingPercentiles(
        Selector selector,
        uint64_t& p50,
        uint64_t& p95,
        uint64_t& p99,
        uint64_t& maximum)
    {
        if (g_batchTimingSampleCount == 0) return;
        std::vector<uint64_t> values;
        values.reserve(g_batchTimingSampleCount);
        for (size_t i = 0; i < g_batchTimingSampleCount; ++i)
            values.push_back(selector(g_batchTimingSamples[i]));
        std::sort(values.begin(), values.end());

        const size_t last = values.size() - 1;
        const auto percentileIndex = [last](size_t percentile) {
            return (last * percentile + 99) / 100;
        };
        p50 = values[percentileIndex(50)];
        p95 = values[percentileIndex(95)];
        p99 = values[percentileIndex(99)];
        maximum = values.back();
    }

    static void PopulateBatchTimingSnapshot(JobSystemStatsSnapshot* stats) noexcept
    {
        try
        {
            std::lock_guard<std::mutex> lock(g_batchTimingMutex);
            stats->timingSampleCount = static_cast<uint64_t>(g_batchTimingSampleCount);
            stats->timingSamplesDropped = g_batchTimingSamplesDropped;
            PopulateTimingPercentiles(
                [](const BatchTimingSample& sample) { return sample.batchTotalNs; },
                stats->batchTotalP50Ns, stats->batchTotalP95Ns,
                stats->batchTotalP99Ns, stats->batchTotalMaxNs);
            PopulateTimingPercentiles(
                [](const BatchTimingSample& sample) { return sample.submitToFirstWorkerNs; },
                stats->submitToFirstWorkerP50Ns, stats->submitToFirstWorkerP95Ns,
                stats->submitToFirstWorkerP99Ns, stats->submitToFirstWorkerMaxNs);
            PopulateTimingPercentiles(
                [](const BatchTimingSample& sample) { return sample.workerStartSpreadNs; },
                stats->workerStartSpreadP50Ns, stats->workerStartSpreadP95Ns,
                stats->workerStartSpreadP99Ns, stats->workerStartSpreadMaxNs);
            PopulateTimingPercentiles(
                [](const BatchTimingSample& sample) { return sample.executionSpanNs; },
                stats->executionSpanP50Ns, stats->executionSpanP95Ns,
                stats->executionSpanP99Ns, stats->executionSpanMaxNs);
            PopulateTimingPercentiles(
                [](const BatchTimingSample& sample) { return sample.maxRangeNs; },
                stats->maxRangeP50Ns, stats->maxRangeP95Ns,
                stats->maxRangeP99Ns, stats->maxRangeMaxNs);

            stats->slowBatchId = g_slowestBatch.batchId;
            stats->slowBatchTotalNs = g_slowestBatch.batchTotalNs;
            stats->slowSubmitToFirstWorkerNs = g_slowestBatch.submitToFirstWorkerNs;
            stats->slowWorkerStartSpreadNs = g_slowestBatch.workerStartSpreadNs;
            stats->slowExecutionSpanNs = g_slowestBatch.executionSpanNs;
            stats->slowMaxRangeNs = g_slowestBatch.maxRangeNs;
            stats->slowRangeThreadCpuNs = g_slowestBatch.slowRangeThreadCpuNs;
            stats->slowRangeThreadCycles = g_slowestBatch.slowRangeThreadCycles;
            stats->slowBatchMinRangeThreadCycles = g_slowestBatch.minRangeThreadCycles;
            stats->slowBatchAverageRangeThreadCycles = g_slowestBatch.averageRangeThreadCycles;
            stats->slowCoreMigrations = g_slowestBatch.coreMigrations;
            stats->slowAssistTiles = g_slowestBatch.assistTiles;
            stats->slowRangeIndex = g_slowestBatch.slowRangeIndex;
            stats->slowRangeWorker = g_slowestBatch.slowRangeWorker;
            stats->slowRangeStartLogicalCore = g_slowestBatch.slowRangeStartLogicalCore;
            stats->slowRangeEndLogicalCore = g_slowestBatch.slowRangeEndLogicalCore;
            stats->slowRangeStartPhysicalCore = g_slowestBatch.slowRangeStartPhysicalCore;
            stats->slowRangeEndPhysicalCore = g_slowestBatch.slowRangeEndPhysicalCore;
        }
        catch (...)
        {
            // Stats collection must never affect job completion.
        }
    }

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

    static uint64_t MonotonicNowNs() noexcept
    {
        return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count());
    }

    static int CurrentProcessorIndexForDiagnostics() noexcept
    {
#if defined(_WIN32) && defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
        PROCESSOR_NUMBER processor{};
        ::GetCurrentProcessorNumberEx(&processor);
        return static_cast<int>(processor.Group) * 64 + static_cast<int>(processor.Number);
#elif defined(__linux__)
        return ::sched_getcpu();
#else
        return -1;
#endif
    }

    static uint64_t CurrentThreadCpuTimeNsForDiagnostics() noexcept
    {
#if defined(_WIN32)
        FILETIME creation{}, exit{}, kernel{}, user{};
        if (!::GetThreadTimes(::GetCurrentThread(), &creation, &exit, &kernel, &user))
            return 0;
        ULARGE_INTEGER kernelTime{}, userTime{};
        kernelTime.LowPart = kernel.dwLowDateTime;
        kernelTime.HighPart = kernel.dwHighDateTime;
        userTime.LowPart = user.dwLowDateTime;
        userTime.HighPart = user.dwHighDateTime;
        return (kernelTime.QuadPart + userTime.QuadPart) * 100ull;
#elif defined(__linux__)
        timespec value{};
        if (::clock_gettime(CLOCK_THREAD_CPUTIME_ID, &value) != 0) return 0;
        return static_cast<uint64_t>(value.tv_sec) * 1'000'000'000ull +
            static_cast<uint64_t>(value.tv_nsec);
#else
        return 0;
#endif
    }

    static uint64_t CurrentThreadCyclesForDiagnostics() noexcept
    {
#if defined(_WIN32)
        ULONG64 cycles = 0;
        return ::QueryThreadCycleTime(::GetCurrentThread(), &cycles)
            ? static_cast<uint64_t>(cycles) : 0;
#else
        return 0;
#endif
    }

    static int PhysicalCoreIndexForDiagnostics(int logicalCore) noexcept
    {
#if defined(_WIN32)
        constexpr size_t kLogicalCoreMapCapacity = 4096;
        static const auto logicalToPhysical = []() noexcept {
            std::array<int, kLogicalCoreMapCapacity> result{};
            result.fill(-1);
            DWORD bytes = 0;
            (void)::GetLogicalProcessorInformationEx(RelationProcessorCore, nullptr, &bytes);
            if (bytes == 0) return result;
            auto* buffer = static_cast<unsigned char*>(std::malloc(bytes));
            if (!buffer) return result;
            if (!::GetLogicalProcessorInformationEx(
                RelationProcessorCore,
                reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer),
                &bytes))
            {
                std::free(buffer);
                return result;
            }

            DWORD offset = 0;
            int physicalCore = 0;
            while (offset < bytes)
            {
                auto* info = reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(
                    buffer + offset);
                if (info->Relationship == RelationProcessorCore)
                {
                    const auto& processor = info->Processor;
                    for (WORD groupIndex = 0; groupIndex < processor.GroupCount; ++groupIndex)
                    {
                        const GROUP_AFFINITY& affinity = processor.GroupMask[groupIndex];
                        for (int bit = 0; bit < 64; ++bit)
                        {
                            if ((affinity.Mask & (static_cast<KAFFINITY>(1) << bit)) == 0)
                                continue;
                            const int index = static_cast<int>(affinity.Group) * 64 + bit;
                            if (index >= 0 && static_cast<size_t>(index) < result.size())
                                result[static_cast<size_t>(index)] = physicalCore;
                        }
                    }
                    ++physicalCore;
                }
                if (info->Size == 0) break;
                offset += info->Size;
            }
            std::free(buffer);
            return result;
        }();
        return logicalCore >= 0 && static_cast<size_t>(logicalCore) < logicalToPhysical.size()
            ? logicalToPhysical[static_cast<size_t>(logicalCore)] : -1;
#else
        (void)logicalCore;
        return -1;
#endif
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
        stats->workerTargetTotal = g_workerTargetTotal.load(std::memory_order_relaxed);
        stats->totalTilesPublished = g_totalTilesPublished.load(std::memory_order_relaxed);
        stats->localTiles = g_localTiles.load(std::memory_order_relaxed);
        stats->stolenTiles = g_stolenTiles.load(std::memory_order_relaxed);
        stats->assistTiles = g_assistTiles.load(std::memory_order_relaxed);
        stats->stealAttempts = g_stealAttempts.load(std::memory_order_relaxed);
        stats->stealSuccesses = g_stealSuccesses.load(std::memory_order_relaxed);
        stats->permitsReleased = 0;
        stats->victimScans = g_victimScans.load(std::memory_order_relaxed);
        stats->stealEmptyExits = g_stealEmptyExits.load(std::memory_order_relaxed);
        stats->batchStorageCreated = g_batchStorageCreated.load(std::memory_order_relaxed);
        stats->batchStorageReused = g_batchStorageReused.load(std::memory_order_relaxed);
        stats->batchStorageReturned = g_batchStorageReturned.load(std::memory_order_relaxed);
        stats->batchStorageDropped = g_batchStorageDropped.load(std::memory_order_relaxed);
        stats->submitToFirstWorkerEwmaNs = g_submitToFirstWorkerEwmaNs.load(std::memory_order_relaxed);
        stats->workerStartSpreadEwmaNs = g_workerStartSpreadEwmaNs.load(std::memory_order_relaxed);
        stats->lastTileToTopologyDoneEwmaNs = g_lastTileToTopologyDoneEwmaNs.load(std::memory_order_relaxed);
        stats->completeWakeToReturnEwmaNs = g_completeWakeToReturnEwmaNs.load(std::memory_order_relaxed);
        stats->taskflowBatches = g_taskflowBatches.load(std::memory_order_relaxed);
        stats->nativeBatches = g_nativeBatches.load(std::memory_order_relaxed);
        stats->invalidBackendSelections = g_invalidBackendSelections.load(std::memory_order_relaxed);
        PopulateBatchTimingSnapshot(stats);

        const uint64_t workerTiles =
            g_workerExecutedRanges.load(std::memory_order_relaxed);
        const uint64_t assistTiles =
            g_mainExecutedRanges.load(std::memory_order_relaxed);
        const uint64_t totalTiles = workerTiles + assistTiles;
        stats->assistExecPctEwma = totalTiles > 0
            ? (assistTiles * 100 / totalTiles)
            : 0;

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
        g_activeWorkers.store(0, std::memory_order_relaxed);
        g_workerTargetTotal.store(0, std::memory_order_relaxed);
        g_totalTilesPublished.store(0, std::memory_order_relaxed);
        g_localTiles.store(0, std::memory_order_relaxed);
        g_stolenTiles.store(0, std::memory_order_relaxed);
        g_assistTiles.store(0, std::memory_order_relaxed);
        g_stealAttempts.store(0, std::memory_order_relaxed);
        g_stealSuccesses.store(0, std::memory_order_relaxed);
        g_victimScans.store(0, std::memory_order_relaxed);
        g_stealEmptyExits.store(0, std::memory_order_relaxed);
        g_batchStorageCreated.store(0, std::memory_order_relaxed);
        g_batchStorageReused.store(0, std::memory_order_relaxed);
        g_batchStorageReturned.store(0, std::memory_order_relaxed);
        g_batchStorageDropped.store(0, std::memory_order_relaxed);
        g_submitToFirstWorkerEwmaNs.store(0, std::memory_order_relaxed);
        g_workerStartSpreadEwmaNs.store(0, std::memory_order_relaxed);
        g_lastTileToTopologyDoneEwmaNs.store(0, std::memory_order_relaxed);
        g_completeWakeToReturnEwmaNs.store(0, std::memory_order_relaxed);
        g_taskflowBatches.store(0, std::memory_order_relaxed);
        g_nativeBatches.store(0, std::memory_order_relaxed);
        g_invalidBackendSelections.store(0, std::memory_order_relaxed);
        g_publishToCompletionEwmaNs.store(0, std::memory_order_relaxed);
        g_perRangeExecEwmaNs.store(0, std::memory_order_relaxed);
        {
            std::lock_guard<std::mutex> lock(g_batchTimingMutex);
            g_batchTimingSampleCount = 0;
            g_batchTimingSamplesDropped = 0;
            g_slowestBatch = {};
        }
    }

    void SetTimingDiagnosticsEnabled(bool enabled) noexcept
    {
        g_timingDiagnosticsEnabled.store(enabled, std::memory_order_release);
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

    struct BackendAsyncContext
    {
        std::function<void()> work;
    };

    static void RunBackendAsync(void* raw, uint32_t) noexcept
    {
        auto* context = static_cast<BackendAsyncContext*>(raw);
        try { context->work(); } catch (...) {}
    }

    static void CompleteBackendAsync(void* raw) noexcept
    {
        delete static_cast<BackendAsyncContext*>(raw);
    }

    static void SubmitBackendAsync(std::function<void()> work)
    {
        auto* context = new BackendAsyncContext{ std::move(work) };
        if (g_executionBackend == ExecutionBackend::NativeWorkerPoolExperimental)
        {
            if (!g_nativeWorkerPool || !g_nativeWorkerPool->Submit(
                context, 1, &RunBackendAsync, &CompleteBackendAsync))
            {
                RunBackendAsync(context, 0);
                CompleteBackendAsync(context);
            }
            return;
        }

        auto executor = EnsureExecutor();
        executor->silent_async([context]
        {
            RunBackendAsync(context, 0);
            CompleteBackendAsync(context);
        });
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
    enum class TileKind : uint8_t
    {
        ChunkCallbacks,
        ChunkRange,
        EntityBatchRange
    };

    struct ExecutionTile {
        uint32_t firstItem;
        uint32_t itemCount;
        TileKind kind;
    };

    // Local partition — stores tile range [front, back) for stealing-capable local execution.
    struct alignas(hardware_destructive_interference_size) LocalPartition {
        std::atomic<uint32_t> front{ 0 };
        std::atomic<uint32_t> back{ 0 };
        uint32_t initialFront{ 0 };
        uint32_t initialBack{ 0 };
        uint32_t ownerSlot{ 0 };
    };
    static_assert(alignof(LocalPartition) >= hardware_destructive_interference_size);
    static_assert(sizeof(LocalPartition) % hardware_destructive_interference_size == 0);

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
        g_stealAttempts.fetch_add(1, std::memory_order_relaxed);
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
                g_stealSuccesses.fetch_add(1, std::memory_order_relaxed);
                g_stealCount.fetch_add(1, std::memory_order_relaxed);
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
        struct BatchStorage* storage{ nullptr };
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
        std::atomic<uint32_t> workerSlotsEntered{ 0 };

        std::atomic<uint64_t> publishedAt{ 0 };
        std::atomic<uint64_t> firstWorkerAt{ 0 };
        std::atomic<uint64_t> lastWorkerAt{ 0 };
        std::atomic<uint64_t> firstTileAt{ 0 };
        std::atomic<uint64_t> lastTileAt{ 0 };
        std::atomic<uint64_t> topologyDoneAt{ 0 };
        std::atomic<uint64_t> maxRangeDurationNs{ 0 };
        std::atomic<uint64_t> minRangeThreadCycles{ (std::numeric_limits<uint64_t>::max)() };
        std::atomic<uint64_t> totalRangeThreadCycles{ 0 };
        std::atomic<uint64_t> measuredRangeThreadCycles{ 0 };
        std::atomic_flag slowRangeLock = ATOMIC_FLAG_INIT;
        uint64_t slowRangeThreadCpuNs{ 0 };
        uint64_t slowRangeThreadCycles{ 0 };
        int32_t slowRangeIndex{ -1 };
        int32_t slowRangeWorker{ -1 };
        int32_t slowRangeStartLogicalCore{ -1 };
        int32_t slowRangeEndLogicalCore{ -1 };
        int32_t slowRangeStartPhysicalCore{ -1 };
        int32_t slowRangeEndPhysicalCore{ -1 };
        std::atomic<uint64_t> coreMigrations{ 0 };
        std::atomic<uint64_t> batchAssistTiles{ 0 };

        std::atomic<int> completedRanges{ 0 };
        std::atomic<bool> finalized{ false };
        std::atomic<bool> workersFinished{ false };

        uint64_t diagnosticId{ 0 };
    };

    static void AtomicMinNonZero(std::atomic<uint64_t>& target, uint64_t value) noexcept
    {
        if (value == 0) return;
        uint64_t current = target.load(std::memory_order_relaxed);
        while (value < current && !target.compare_exchange_weak(
            current, value, std::memory_order_relaxed)) {}
    }

    static void RecordRangeExecutionDiagnostics(
        BatchState* batch,
        int rangeIndex,
        uint64_t wallNs,
        uint64_t threadCpuNs,
        uint64_t threadCycles,
        int startLogicalCore,
        int endLogicalCore) noexcept
    {
        AtomicMinNonZero(batch->minRangeThreadCycles, threadCycles);
        if (threadCycles != 0)
        {
            batch->totalRangeThreadCycles.fetch_add(threadCycles, std::memory_order_relaxed);
            batch->measuredRangeThreadCycles.fetch_add(1, std::memory_order_relaxed);
        }
        while (batch->slowRangeLock.test_and_set(std::memory_order_acquire))
            std::this_thread::yield();
        if (wallNs > batch->maxRangeDurationNs.load(std::memory_order_relaxed))
        {
            batch->maxRangeDurationNs.store(wallNs, std::memory_order_relaxed);
            batch->slowRangeThreadCpuNs = threadCpuNs;
            batch->slowRangeThreadCycles = threadCycles;
            batch->slowRangeIndex = rangeIndex;
            batch->slowRangeWorker = WorkerIndexManager::GetCurrentIndex();
            batch->slowRangeStartLogicalCore = startLogicalCore;
            batch->slowRangeEndLogicalCore = endLogicalCore;
            batch->slowRangeStartPhysicalCore =
                PhysicalCoreIndexForDiagnostics(startLogicalCore);
            batch->slowRangeEndPhysicalCore =
                PhysicalCoreIndexForDiagnostics(endLogicalCore);
        }
        batch->slowRangeLock.clear(std::memory_order_release);
    }

    struct BatchStorage
    {
        BatchState batch;
        ExecutionTile* tileBuffer{ nullptr };
        uint32_t tileCapacity{ 0 };
        LocalPartition* partitionBuffer{ nullptr };
        uint32_t partitionCapacity{ 0 };

        BatchStorage() noexcept { batch.storage = this; }
        ~BatchStorage()
        {
            delete[] partitionBuffer;
            delete[] tileBuffer;
        }
    };

    std::mutex g_batchStoragePoolMutex;
    std::vector<BatchStorage*> g_batchStoragePool;

    static BatchStorage* AcquireBatchStorage(
        uint32_t tileCapacity,
        uint32_t partitionCapacity)
    {
        BatchStorage* storage = nullptr;
        {
            std::lock_guard<std::mutex> lock(g_batchStoragePoolMutex);
            auto best = g_batchStoragePool.end();
            for (auto it = g_batchStoragePool.begin();
                it != g_batchStoragePool.end(); ++it)
            {
                if ((*it)->tileCapacity < tileCapacity ||
                    (*it)->partitionCapacity < partitionCapacity)
                    continue;
                if (best == g_batchStoragePool.end() ||
                    (*it)->tileCapacity < (*best)->tileCapacity)
                    best = it;
            }
            if (best != g_batchStoragePool.end())
            {
                storage = *best;
                g_batchStoragePool.erase(best);
            }
        }

        if (storage)
            g_batchStorageReused.fetch_add(1, std::memory_order_relaxed);
        else
        {
            storage = new BatchStorage();
            g_batchStorageCreated.fetch_add(1, std::memory_order_relaxed);
        }

        if (storage->tileCapacity < tileCapacity)
        {
            auto* replacement = new ExecutionTile[tileCapacity];
            delete[] storage->tileBuffer;
            storage->tileBuffer = replacement;
            storage->tileCapacity = tileCapacity;
        }
        if (storage->partitionCapacity < partitionCapacity)
        {
            auto* replacement = new LocalPartition[partitionCapacity]();
            delete[] storage->partitionBuffer;
            storage->partitionBuffer = replacement;
            storage->partitionCapacity = partitionCapacity;
        }

        storage->batch.storage = storage;
        storage->batch.tiles = tileCapacity > 0 ? storage->tileBuffer : nullptr;
        storage->batch.partitions = partitionCapacity > 0
            ? storage->partitionBuffer
            : nullptr;
        return storage;
    }

    static void ReleaseBatchStorage(BatchStorage* storage) noexcept
    {
        if (!storage) return;
        std::destroy_at(&storage->batch);
        std::construct_at(&storage->batch);
        storage->batch.storage = storage;
        g_batchStorageReturned.fetch_add(1, std::memory_order_relaxed);

        bool pooled = false;
        {
            std::lock_guard<std::mutex> lock(g_batchStoragePoolMutex);
            if (g_batchStoragePool.size() < kMaxPooledBatchStorage)
            {
                g_batchStoragePool.push_back(storage);
                pooled = true;
            }
        }
        if (!pooled)
        {
            g_batchStorageDropped.fetch_add(1, std::memory_order_relaxed);
            delete storage;
        }
    }

    static void ClearBatchStoragePool() noexcept
    {
        std::vector<BatchStorage*> idle;
        {
            std::lock_guard<std::mutex> lock(g_batchStoragePoolMutex);
            idle.swap(g_batchStoragePool);
        }
        for (auto* storage : idle) delete storage;
    }

    struct VictimSnapshot
    {
        uint32_t index{ ~0u };
        uint32_t remaining{ 0 };
    };

    static VictimSnapshot SelectHeaviestVictim(
        BatchState* batch,
        uint32_t excludedSlot,
        uint32_t minimumRemaining) noexcept
    {
        VictimSnapshot best{};
        if (!batch || !batch->partitions) return best;

        for (uint32_t slot = 0; slot < batch->partitionCount; ++slot)
        {
            if (slot == excludedSlot) continue;
            g_victimScans.fetch_add(1, std::memory_order_relaxed);
            auto& part = batch->partitions[slot];
            const uint32_t front = part.front.load(std::memory_order_acquire);
            const uint32_t back = part.back.load(std::memory_order_acquire);
            const uint32_t remaining = back > front ? back - front : 0;
            if (remaining >= minimumRemaining && remaining > best.remaining)
            {
                best.index = slot;
                best.remaining = remaining;
            }
        }
        return best;
    }

    static bool TryExecuteOneRange(BatchState* batch, bool assist) noexcept
    {
        if (!batch) return false;
        const int index = batch->nextRange.fetch_add(1, std::memory_order_relaxed);
        if (index >= batch->rangeCount) return false;

        PushTraceEvent(TraceEventType::Claim, batch->diagnosticId,
            index, index * batch->rangeSize, batch->rangeSize);
        PushTraceEvent(TraceEventType::ExecuteBegin, batch->diagnosticId,
            index, index * batch->rangeSize, batch->rangeSize);
        const bool timingEnabled = g_timingDiagnosticsEnabled.load(std::memory_order_relaxed);
        const uint64_t rangeStartedAt = timingEnabled ? MonotonicNowNs() : 0;
        const uint64_t threadCpuStartedAt = timingEnabled
            ? CurrentThreadCpuTimeNsForDiagnostics() : 0;
        const uint64_t threadCyclesStartedAt = timingEnabled
            ? CurrentThreadCyclesForDiagnostics() : 0;
        const int rangeStartLogicalCore = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        if (timingEnabled)
        {
            uint64_t empty = 0;
            batch->firstTileAt.compare_exchange_strong(
                empty, rangeStartedAt, std::memory_order_release, std::memory_order_relaxed);
        }
        batch->processRange(batch->context, index, 1);
        const int rangeEndLogicalCore = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        const uint64_t threadCyclesFinishedAt = timingEnabled
            ? CurrentThreadCyclesForDiagnostics() : 0;
        const uint64_t threadCpuFinishedAt = timingEnabled
            ? CurrentThreadCpuTimeNsForDiagnostics() : 0;
        const uint64_t rangeFinishedAt = timingEnabled ? MonotonicNowNs() : 0;
        if (timingEnabled && rangeFinishedAt >= rangeStartedAt)
        {
            RecordRangeExecutionDiagnostics(
                batch,
                index,
                rangeFinishedAt - rangeStartedAt,
                threadCpuFinishedAt >= threadCpuStartedAt
                    ? threadCpuFinishedAt - threadCpuStartedAt : 0,
                threadCyclesFinishedAt >= threadCyclesStartedAt
                    ? threadCyclesFinishedAt - threadCyclesStartedAt : 0,
                rangeStartLogicalCore,
                rangeEndLogicalCore);
        }
        if (assist)
        {
            g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
            g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
            batch->batchAssistTiles.fetch_add(1, std::memory_order_relaxed);
        }
        else
        {
            g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);
        }
        PushTraceEvent(TraceEventType::ExecuteEnd, batch->diagnosticId,
            index, index * batch->rangeSize, batch->rangeSize);
        if (batch->completedRanges.fetch_add(1, std::memory_order_acq_rel) + 1 ==
            batch->rangeCount)
            batch->lastTileAt.store(MonotonicNowNs(), std::memory_order_release);
        return true;
    }

    static bool AssistExecuteOneRange(void* ptr) noexcept
    {
        auto* batch = static_cast<BatchState*>(ptr);
        return TryExecuteOneRange(batch, true);
    }

    // ============================================================
    // Partition-based execution (Phase 1)
    // ============================================================
    // Process one tile and update completion counter.
    // Returns true if the tile was processed (for assist comptability).
    enum class TileExecutionSource : uint8_t
    {
        WorkerLocal,
        WorkerStolen,
        Assist
    };

    static bool TryExecuteOneTile(
        BatchState* batch,
        uint32_t tileIndex,
        TileExecutionSource source) noexcept
    {
        if (!batch || tileIndex >= batch->tileCount) return false;

        const auto& tile = batch->tiles[tileIndex];
        PushTraceEvent(TraceEventType::Claim, batch->diagnosticId,
            static_cast<int>(tileIndex),
            static_cast<int>(tile.firstItem),
            static_cast<int>(tile.itemCount));
        PushTraceEvent(TraceEventType::ExecuteBegin, batch->diagnosticId,
            static_cast<int>(tileIndex),
            static_cast<int>(tile.firstItem),
            static_cast<int>(tile.itemCount));
        const bool timingEnabled = g_timingDiagnosticsEnabled.load(std::memory_order_relaxed);
        const uint64_t rangeStartedAt = timingEnabled ? MonotonicNowNs() : 0;
        const uint64_t threadCpuStartedAt = timingEnabled
            ? CurrentThreadCpuTimeNsForDiagnostics() : 0;
        const uint64_t threadCyclesStartedAt = timingEnabled
            ? CurrentThreadCyclesForDiagnostics() : 0;
        const int rangeStartLogicalCore = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        if (timingEnabled)
        {
            uint64_t empty = 0;
            batch->firstTileAt.compare_exchange_strong(
                empty, rangeStartedAt, std::memory_order_release, std::memory_order_relaxed);
        }
        batch->executeTile(batch->context, batch->tiles[tileIndex]);
        const int rangeEndLogicalCore = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        const uint64_t threadCyclesFinishedAt = timingEnabled
            ? CurrentThreadCyclesForDiagnostics() : 0;
        const uint64_t threadCpuFinishedAt = timingEnabled
            ? CurrentThreadCpuTimeNsForDiagnostics() : 0;
        const uint64_t rangeFinishedAt = timingEnabled ? MonotonicNowNs() : 0;
        if (timingEnabled && rangeFinishedAt >= rangeStartedAt)
        {
            RecordRangeExecutionDiagnostics(
                batch,
                static_cast<int>(tileIndex),
                rangeFinishedAt - rangeStartedAt,
                threadCpuFinishedAt >= threadCpuStartedAt
                    ? threadCpuFinishedAt - threadCpuStartedAt : 0,
                threadCyclesFinishedAt >= threadCyclesStartedAt
                    ? threadCyclesFinishedAt - threadCyclesStartedAt : 0,
                rangeStartLogicalCore,
                rangeEndLogicalCore);
        }
        if (source == TileExecutionSource::Assist)
        {
            g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
            g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
            g_assistTiles.fetch_add(1, std::memory_order_relaxed);
            batch->batchAssistTiles.fetch_add(1, std::memory_order_relaxed);
        }
        else
        {
            g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);
            if (source == TileExecutionSource::WorkerLocal)
                g_localTiles.fetch_add(1, std::memory_order_relaxed);
            else
                g_stolenTiles.fetch_add(1, std::memory_order_relaxed);
        }
        PushTraceEvent(TraceEventType::ExecuteEnd, batch->diagnosticId,
            static_cast<int>(tileIndex),
            static_cast<int>(tile.firstItem),
            static_cast<int>(tile.itemCount));
        if (batch->completedTiles.fetch_add(1, std::memory_order_acq_rel) + 1 ==
            batch->tileCount)
            batch->lastTileAt.store(MonotonicNowNs(), std::memory_order_release);
        return true;
    }

    static void RecordWorkerEntry(BatchState* batch) noexcept
    {
        const uint64_t now = MonotonicNowNs();
        uint64_t empty = 0;
        batch->firstWorkerAt.compare_exchange_strong(
            empty, now, std::memory_order_acq_rel, std::memory_order_relaxed);
        if (batch->workerSlotsEntered.fetch_add(1, std::memory_order_acq_rel) + 1 ==
            batch->partitionCount)
            batch->lastWorkerAt.store(now, std::memory_order_release);
    }

    // Worker loop for partition mode:
    // 1. Process local tiles from partition[slot] front
    // 2. Steal half from victims
    static void WorkerPartitionLoop(BatchState* batch, uint32_t slot) noexcept
    {
        if (!batch || slot >= batch->partitionCount || batch->tileCount == 0) return;
        RecordWorkerEntry(batch);

        const uint64_t active =
            g_activeWorkers.fetch_add(1, std::memory_order_acq_rel) + 1;
        uint64_t peak = g_activeWorkersPeak.load(std::memory_order_relaxed);
        while (active > peak && !g_activeWorkersPeak.compare_exchange_weak(
            peak, active, std::memory_order_relaxed)) {}
        g_workerClaimedTokens.fetch_add(1, std::memory_order_relaxed);

        LocalPartition& mine = batch->partitions[slot];
        uint32_t tileIdx;

        // Phase 1: Local tiles
        while (TryTakeLocal(mine, tileIdx))
        {
            TryExecuteOneTile(batch, tileIdx, TileExecutionSource::WorkerLocal);
        }

        // Phase 2: repeatedly target only the heaviest visible victim. A
        // failed CAS gets one fresh selection instead of an all-victim loop.
        uint32_t consecutiveFailures = 0;
        while (consecutiveFailures < 2)
        {
            const VictimSnapshot victim = SelectHeaviestVictim(batch, slot, 2);
            if (victim.index == ~0u)
            {
                g_stealEmptyExits.fetch_add(1, std::memory_order_relaxed);
                break;
            }

            uint32_t stealStart, stealEnd;
            if (!TryStealHalf(batch->partitions[victim.index], stealStart, stealEnd))
            {
                ++consecutiveFailures;
                continue;
            }

            consecutiveFailures = 0;
            for (uint32_t t = stealStart; t < stealEnd; t++)
                TryExecuteOneTile(batch, t, TileExecutionSource::WorkerStolen);
        }
        g_activeWorkers.fetch_sub(1, std::memory_order_acq_rel);
    }

    static bool AssistExecuteOneTile(void* ptr) noexcept
    {
        auto* batch = static_cast<BatchState*>(ptr);
        const VictimSnapshot victim = SelectHeaviestVictim(batch, ~0u, 1);
        if (victim.index == ~0u) return false;

        // Try half-steal first (best for load balance)
        uint32_t ss, se;
        if (victim.remaining > 1 &&
            TryStealHalf(batch->partitions[victim.index], ss, se))
        {
            for (uint32_t t = ss; t < se; t++)
                TryExecuteOneTile(batch, t, TileExecutionSource::Assist);
            return true;
        }

        // Fallback: single remaining tile — take it from front via TryTakeLocal.
        // This handles the case where TryStealHalf cannot split remaining=1.
        uint32_t tileIdx;
        if (TryTakeLocal(batch->partitions[victim.index], tileIdx))
        {
            TryExecuteOneTile(batch, tileIdx, TileExecutionSource::Assist);
            return true;
        }
        return false;
    }

    static void RecordTopologyCompletion(BatchState* batch) noexcept
    {
        const uint64_t now = MonotonicNowNs();
        batch->topologyDoneAt.store(now, std::memory_order_release);
        const uint64_t published = batch->publishedAt.load(std::memory_order_acquire);
        const uint64_t firstWorker = batch->firstWorkerAt.load(std::memory_order_acquire);
        const uint64_t lastWorker = batch->lastWorkerAt.load(std::memory_order_acquire);
        const uint64_t lastTile = batch->lastTileAt.load(std::memory_order_acquire);
        if (published != 0 && firstWorker >= published)
            UpdateUnsignedEwma(g_submitToFirstWorkerEwmaNs,
                std::max<uint64_t>(1, firstWorker - published));
        if (firstWorker != 0 && lastWorker >= firstWorker)
            UpdateUnsignedEwma(g_workerStartSpreadEwmaNs,
                std::max<uint64_t>(1, lastWorker - firstWorker));
        if (lastTile != 0 && now >= lastTile)
            UpdateUnsignedEwma(g_lastTileToTopologyDoneEwmaNs,
                std::max<uint64_t>(1, now - lastTile));

    }

    static void RecordFinalizedBatchTiming(BatchState* batch) noexcept
    {
        if (!g_timingDiagnosticsEnabled.load(std::memory_order_relaxed)) return;

        const uint64_t now = MonotonicNowNs();
        const uint64_t published = batch->publishedAt.load(std::memory_order_acquire);
        const uint64_t firstWorker = batch->firstWorkerAt.load(std::memory_order_acquire);
        const uint64_t lastWorker = batch->lastWorkerAt.load(std::memory_order_acquire);
        const uint64_t firstTile = batch->firstTileAt.load(std::memory_order_acquire);
        const uint64_t lastTile = batch->lastTileAt.load(std::memory_order_acquire);

        BatchTimingSample sample{};
        sample.batchId = batch->diagnosticId;
        sample.batchTotalNs = published != 0 && now >= published
            ? now - published : 0;
        sample.submitToFirstWorkerNs = published != 0 && firstWorker >= published
            ? firstWorker - published : 0;
        sample.workerStartSpreadNs = firstWorker != 0 && lastWorker >= firstWorker
            ? lastWorker - firstWorker : 0;
        sample.executionSpanNs = firstTile != 0 && lastTile >= firstTile
            ? lastTile - firstTile : 0;
        sample.maxRangeNs = batch->maxRangeDurationNs.load(std::memory_order_relaxed);
        sample.slowRangeThreadCpuNs = batch->slowRangeThreadCpuNs;
        sample.slowRangeThreadCycles = batch->slowRangeThreadCycles;
        const uint64_t minCycles = batch->minRangeThreadCycles.load(std::memory_order_relaxed);
        sample.minRangeThreadCycles = minCycles == (std::numeric_limits<uint64_t>::max)()
            ? 0 : minCycles;
        const uint64_t measuredCycles =
            batch->measuredRangeThreadCycles.load(std::memory_order_relaxed);
        sample.averageRangeThreadCycles = measuredCycles > 0
            ? batch->totalRangeThreadCycles.load(std::memory_order_relaxed) / measuredCycles
            : 0;
        sample.coreMigrations = batch->coreMigrations.load(std::memory_order_relaxed);
        sample.assistTiles = batch->batchAssistTiles.load(std::memory_order_relaxed);
        sample.slowRangeIndex = batch->slowRangeIndex;
        sample.slowRangeWorker = batch->slowRangeWorker;
        sample.slowRangeStartLogicalCore = batch->slowRangeStartLogicalCore;
        sample.slowRangeEndLogicalCore = batch->slowRangeEndLogicalCore;
        sample.slowRangeStartPhysicalCore = batch->slowRangeStartPhysicalCore;
        sample.slowRangeEndPhysicalCore = batch->slowRangeEndPhysicalCore;
        RecordBatchTiming(sample);
    }

    static void ReleaseBatch(BatchState* batch) noexcept
    {
        if (!batch) return;
        ReleaseBatchStorage(batch->storage);
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
            RecordFinalizedBatchTiming(batch);
            PushTraceEvent(TraceEventType::FinalizeBegin, diagnosticId, -1, 0, 0);
            if (batch->cleanup) batch->cleanup(batch->context);
            // Push HandleComplete before CompleteState so the event is
            // recorded before CompleteState's notify_all() wakes a waiter
            // that could race with TraceSetEnabled(false).
            PushTraceEvent(TraceEventType::HandleComplete, diagnosticId, -1, 0, 0);
            // Completion is the public ownership boundary. Return scheduler
            // metadata before publishing completed=true so a caller that
            // leaves Complete() can immediately observe reconciled pool stats.
            ReleaseBatch(batch);
            CompleteState(state);
        }
    }

    // The last assist reader only requests finalization. Batch memory remains
    // alive until Taskflow has also finished every worker task.
    static void OnAssistReadersDrained(void* handlePtr) noexcept
    {
        TryFinalizeCompletedBatch(static_cast<HandleState*>(handlePtr));
    }

    // Acquire assist reader: returns false if batch is already finalized
    // Submit a BatchState via taskflow
    static void ExecuteBatchSlot(void* raw, uint32_t slot) noexcept
    {
        auto* batch = static_cast<BatchState*>(raw);
        const bool timingEnabled = g_timingDiagnosticsEnabled.load(std::memory_order_relaxed);
        const int startProcessor = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        if (WorkerIndexManager::GetCurrentIndex() < 0)
            WorkerIndexManager::SetCurrentIndex(WorkerIndexManager::AllocateIndex());
        if (batch->partitions)
            WorkerPartitionLoop(batch, slot);
        else
        {
            RecordWorkerEntry(batch);
            while (TryExecuteOneRange(batch, false)) {}
        }
        const int endProcessor = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        if (startProcessor >= 0 && endProcessor >= 0 && startProcessor != endProcessor)
            batch->coreMigrations.fetch_add(1, std::memory_order_relaxed);
    }

    static void CompleteBackendBatch(void* raw) noexcept
    {
        auto* batch = static_cast<BatchState*>(raw);
        auto* state = batch->handle;
        RecordTopologyCompletion(batch);
        state->assistCallback.store(nullptr, std::memory_order_release);
        batch->workersFinished.store(true, std::memory_order_release);
        TryFinalizeCompletedBatch(state);
        ReleaseState(state);
    }

    static void SubmitBatch(BatchState* batch, int /*workerCap*/ = 0)
    {
        auto* state = batch->handle;
        bool (*assistFn)(void*) noexcept = batch->partitions
            ? &AssistExecuteOneTile
            : &AssistExecuteOneRange;
        const int participantCount = std::max(1, static_cast<int>(batch->partitionCount));

        g_frameTasksSubmitted.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);
        g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
        g_workerTargetTotal.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);
        g_totalTilesPublished.fetch_add(
            batch->partitions ? static_cast<uint64_t>(batch->tileCount)
                              : static_cast<uint64_t>(batch->rangeCount),
            std::memory_order_relaxed);

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
        batch->publishedAt.store(MonotonicNowNs(), std::memory_order_release);
        if (g_executionBackend == ExecutionBackend::NativeWorkerPoolExperimental)
        {
            g_nativeBatches.fetch_add(1, std::memory_order_relaxed);
            if (!g_nativeWorkerPool || !g_nativeWorkerPool->Submit(
                batch,
                static_cast<uint32_t>(participantCount),
                &ExecuteBatchSlot,
                &CompleteBackendBatch))
            {
                for (int slot = 0; slot < participantCount; ++slot)
                    ExecuteBatchSlot(batch, static_cast<uint32_t>(slot));
                CompleteBackendBatch(batch);
            }
            return;
        }

        // Native batches return above without touching Taskflow. Constructing the
        // graph here also guarantees every acquired graph reaches its completion
        // callback and is returned to the graph pool.
        auto taskflow = AcquireTaskflow();
        if (batch->partitions)
        {
            for (int slot = 0; slot < participantCount; ++slot)
            {
                taskflow->emplace([batch, slot]() {
                    if (WorkerIndexManager::GetCurrentIndex() < 0)
                        WorkerIndexManager::SetCurrentIndex(WorkerIndexManager::AllocateIndex());
                    WorkerPartitionLoop(batch, static_cast<uint32_t>(slot));
                });
            }
        }
        else
        {
            for (int slot = 0; slot < participantCount; ++slot)
            {
                taskflow->emplace([batch]() {
                    if (WorkerIndexManager::GetCurrentIndex() < 0)
                        WorkerIndexManager::SetCurrentIndex(WorkerIndexManager::AllocateIndex());
                    RecordWorkerEntry(batch);
                    while (TryExecuteOneRange(batch, false)) {}
                });
            }
        }

        g_taskflowBatches.fetch_add(1, std::memory_order_relaxed);
        auto executor = EnsureExecutor();
        executor->run(*taskflow, [taskflow, batch]() mutable
        {
            CompleteBackendBatch(batch);
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

    // Unified Tile executor for Chunk callbacks, Chunk ranges and Entity ranges.
    static bool ChunkExecuteTile(void* ctx, const ExecutionTile& tile) noexcept
    {
        auto* bc = static_cast<ChunkBatchContext*>(ctx);
        switch (tile.kind)
        {
        case TileKind::ChunkCallbacks:
            for (uint32_t i = 0; i < tile.itemCount; ++i)
                bc->func(bc->originalContext, &bc->chunks[tile.firstItem + i]);
            break;
        case TileKind::ChunkRange:
            bc->rangeFunc(bc->originalContext, bc->chunks,
                static_cast<int>(tile.firstItem), static_cast<int>(tile.itemCount));
            break;
        case TileKind::EntityBatchRange:
            bc->entityRangeFunc(bc->originalContext, bc->entityBatches,
                static_cast<int>(tile.firstItem), static_cast<int>(tile.itemCount));
            break;
        }
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
        if (!_state) return;

        const uint64_t diagnosticId =
            _state->diagnosticBatchId.load(std::memory_order_acquire);
        if (diagnosticId != 0)
            PushTraceEvent(TraceEventType::CompleteEnter, diagnosticId, -1, 0, 0);

        if (_state->completed.load(std::memory_order_acquire)) return;

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
                g_mainClaimedTokens.fetch_add(1, std::memory_order_relaxed);
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
        const uint64_t completeWakeAt = MonotonicNowNs();
        const uint64_t completeReturnAt = MonotonicNowNs();
        if (completeReturnAt >= completeWakeAt)
            UpdateUnsignedEwma(
                g_completeWakeToReturnEwmaNs,
                std::max<uint64_t>(1, completeReturnAt - completeWakeAt));
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
        auto* ds = dep.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { builder(state); return JobHandle(state); }
        AcquireState(state);
        AddContinuationOrRunNow(ds, [state, b = std::forward<WorkBuilder>(builder)]() mutable {
            b(state);
            ReleaseState(state);
        });
        return JobHandle(state);
    }

    template <typename Work>
    void FastPath(Work&& work, void* ctx, void (*cleanup)(void*), HandleState* state)
    {
        AcquireState(state);
        SubmitBackendAsync([work = std::forward<Work>(work), state, ctx, cleanup]() {
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
        auto* ds = dep.State();
        if (!ds || ds->completed.load(std::memory_order_acquire))
        { FastPath(std::forward<Work>(work), ctx, cleanup, state); return JobHandle(state); }
        AcquireState(state);
        AddContinuationOrRunNow(ds, [state, work = std::forward<Work>(work), ctx, cleanup]() mutable {
            FastPath(std::forward<Work>(work), ctx, cleanup, state);
            ReleaseState(state);
        });
        return JobHandle(state);
    }

    // ============================================================
    // Scheduler
    // ============================================================
    static ExecutionBackend ResolveConfiguredBackend() noexcept
    {
        std::string value;
#if defined(_WIN32)
        char* raw = nullptr;
        std::size_t rawLength = 0;
        if (_dupenv_s(&raw, &rawLength, "ENTJOY_JOB_BACKEND") != 0)
            raw = nullptr;
        if (raw)
        {
            value.assign(raw);
            std::free(raw);
        }
#else
        const char* raw = std::getenv("ENTJOY_JOB_BACKEND");
        if (raw)
            value.assign(raw);
#endif
        if (value.empty()) return ExecutionBackend::Taskflow;
        std::transform(value.begin(), value.end(), value.begin(),
            [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
        if (value == "taskflow") return ExecutionBackend::Taskflow;
        if (value == "native") return ExecutionBackend::NativeWorkerPoolExperimental;
        g_invalidBackendSelections.fetch_add(1, std::memory_order_relaxed);
        return ExecutionBackend::Taskflow;
    }

    void Scheduler::Initialize(int numThreads)
    {
        g_shuttingDown.store(false, std::memory_order_release);
        {
            std::lock_guard<std::mutex> lock(g_executorMutex);
            int resolved = numThreads > 0 ? numThreads :
                (g_numThreads > 0 ? g_numThreads :
                    std::max(1, static_cast<int>(std::thread::hardware_concurrency()) - 1));
            if (g_executor || (g_nativeWorkerPool && g_nativeWorkerPool->IsRunning()))
                return;
            g_numThreads = resolved;
            g_executionBackend = ResolveConfiguredBackend();
            if (g_executionBackend == ExecutionBackend::NativeWorkerPoolExperimental)
            {
                g_nativeWorkerPool = std::make_unique<NativeWorkerPool>();
                g_nativeWorkerPool->Start(static_cast<uint32_t>(resolved));
                return;
            }
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
    }

    void Scheduler::Shutdown()
    {
        g_shuttingDown.store(true, std::memory_order_release);
        std::shared_ptr<tf::Executor> exec;
        std::unique_ptr<NativeWorkerPool> nativePool;
        {
            std::lock_guard<std::mutex> lock(g_executorMutex);
            exec = std::move(g_executor);
            nativePool = std::move(g_nativeWorkerPool);
            g_numThreads = 0;
        }
        if (exec) exec->wait_for_all();
        if (nativePool) nativePool->Stop();
        ClearBatchStoragePool();
        { std::lock_guard<std::mutex> lock(g_statePoolMutex); for (auto* s : g_statePool) delete s; g_statePool.clear(); }
        { std::lock_guard<std::mutex> lock(g_taskflowPoolMutex); for (auto* tf : g_taskflowPool) delete tf; g_taskflowPool.clear(); }
    }

    void Scheduler::PrewakeWorkers()
    {
        if (g_executionBackend == ExecutionBackend::NativeWorkerPoolExperimental)
            return;
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
        return ScheduleWithDependency(dependency, [func, context, length, cleanup](HandleState* state) {
            AcquireState(state);
            SubmitBackendAsync([func, context, length, cleanup, state]() {
                for (int i = 0; i < length; i++) func(context, i);
                if (cleanup) cleanup(context);
                CompleteState(state);
                ReleaseState(state);
            });
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
        auto* storage = AcquireBatchStorage(0, 0);
        auto* batch = &storage->batch;
        auto* state = CreateState(false); batch->handle = state;
        batch->context = bc; batch->cleanup = [](void* ctx) { CleanupGeneralContext(ctx); };
        batch->processRange = &ProcessGeneralRange; batch->rangeCount = rc; batch->rangeSize = cs;
        batch->partitionCount = static_cast<uint32_t>(ResolveWorkerTarget(0, rc));
        batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

        PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

        auto* ds = dependency.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitBatch(batch); }
        else { AcquireState(state); AddContinuationOrRunNow(ds, [state, batch]() { SubmitBatch(batch); ReleaseState(state); }); }
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
        auto* storage = AcquireBatchStorage(0, 0);
        auto* batch = &storage->batch; auto* state = CreateState(false); batch->handle = state;
        batch->context = bc; batch->cleanup = [](void* ctx) { CleanupGeneralContext(ctx); };
        batch->processRange = &ProcessGeneralRange; batch->rangeCount = rc; batch->rangeSize = cs;
        batch->partitionCount = static_cast<uint32_t>(ResolveWorkerTarget(0, rc));
        batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

        PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

        auto* ds = dependency.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitBatch(batch); }
        else { AcquireState(state); AddContinuationOrRunNow(ds, [state, batch]() { SubmitBatch(batch); ReleaseState(state); }); }
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
        const uint32_t tileCount = static_cast<uint32_t>(func ? itemCount : rc);
        const int targetWorkers = ResolveWorkerTarget(
            workerCap, static_cast<int>(tileCount));
        auto* storage = AcquireBatchStorage(
            tileCount, static_cast<uint32_t>(targetWorkers));
        auto* batch = &storage->batch;
        auto* state = CreateState(false); batch->handle = state;
        batch->context = cc; batch->cleanup = &CleanupChunkContext;
        batch->rangeCount = rc; batch->rangeSize = rs; batch->totalItems = itemCount;
        batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

        // Every Chunk/Entity entry point uses the same Tile/partition protocol.
        {
            const TileKind tileKind = func
                ? TileKind::ChunkCallbacks
                : (rangeFunc ? TileKind::ChunkRange : TileKind::EntityBatchRange);
            auto* tiles = storage->tileBuffer;
            for (uint32_t i = 0; i < tileCount; i++)
            {
                const uint32_t first = func
                    ? i
                    : i * static_cast<uint32_t>(rs);
                tiles[i].firstItem = first;
                tiles[i].itemCount = func
                    ? 1u
                    : std::min(static_cast<uint32_t>(rs),
                        static_cast<uint32_t>(itemCount) - first);
                tiles[i].kind = tileKind;
            }

            // Respect workerCap when choosing partition count
            auto* parts = storage->partitionBuffer;
            BuildPartitions(parts, targetWorkers, static_cast<int>(tileCount));

            batch->executeTile = &ChunkExecuteTile;
            batch->tiles = tiles;
            batch->tileCount = tileCount;
            batch->partitions = parts;
            batch->partitionCount = static_cast<uint32_t>(targetWorkers);
            batch->completedTiles.store(0, std::memory_order_relaxed);
        }

        PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

        auto* ds = dependency.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitBatch(batch, workerCap); }
        else { AcquireState(state); AddContinuationOrRunNow(ds, [state, batch, workerCap]() { SubmitBatch(batch, workerCap); ReleaseState(state); }); }
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
