#include "JobSystem.h"
#include "ChunkJobData.h"
#include "EntityBatchData.h"
#include "JobProfiler.h"
#include "NativeWorkerPool.h"
#include "ThreadAffinity.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif

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
#include <timeapi.h>
#pragma comment(lib, "winmm.lib")
#endif

namespace JobSystem
{
    std::atomic<bool> g_workerAffinityEnabled{ false };

    constexpr size_t kMaxPooledStates = 4096;
    constexpr size_t kMaxPooledBatchStorage = 256;
    constexpr int kSyncExecutionLengthThreshold = 512;
    constexpr int kSyncWithCompletedDepThreshold = 4096;

    // ---------- Globals ----------
    std::mutex g_schedulerMutex;
    std::unique_ptr<NativeWorkerPool> g_nativeWorkerPool;
    int g_numThreads = 0;

    // 并行 for 默认 tiles/worker（batchSize=0 时 ResolveChunkSize 使用）。
    // GridSearch A/B 定标：可变代价 job 最优 ~26 tiles/worker；默认 16 为
    // 可变代价(job 受益) 与均匀代价(job 少付 claim 开销) 的折中。env 可覆盖。
    constexpr int kDefaultTilesPerWorker = 16;
    int g_configuredTilesPerWorker = kDefaultTilesPerWorker;

    // Guided（chunk ∝ 剩余工作量）tile 调度（OpenMP schedule(guided) 同族）。
    // 0=off（uniform 现状）；>0=on。on 时 chunk = max(floor, ceil(remaining/(W*k)))，
    // 头部大块（Poisson 平滑、非 straggler）+ 尾部小块（钳 straggler 上界），
    // 总认领数 ~ W*k*ln(N/floor) 少于 uniform k=26。由 JobSystem_ConfigureGuided 设置。
    int g_guidedEnabled = 0;
    int g_guidedK = 2;
    int g_guidedFloor = 16;

    // B1: AssistDependencyChain 连续零工作的墙钟预算。零工作不代表链卡死
    // （workers 可能正在执行祖先、即将提交下一环），所以用有界回访覆盖祖先
    // completion→下一环 submit 的交接窗口；仅在持续零工作超过该预算后交还
    // 调用方的 spin/futex。以墙钟而非 pass 数计：pass 上限与循环内 yield 的
    // 时长耦合，量级太小会过早 park（V-A 退化 / V-D 残余空等）。
    // 预算内每 pass 带 yield 降频，覆盖亚毫秒~毫秒级的交接窗口绰绰有余；
    // 链持续推进时预算随 worked 重置，不会误伤正常链。
    constexpr uint64_t kAssistStallBudgetNs = 10'000'000; // 10ms

    std::mutex g_statePoolMutex;
    std::vector<HandleState*> g_statePool;

    // B2: per-thread state 缓存。命中零锁；满额批量迁移共享池（每 ~64 次回收 1 次锁）。
    // state 单 owner（refCount==0 才回池），跨线程迁移只发生在共享池锁内，无 ABA。
    constexpr size_t kStateCacheCap = 64;
    struct ThreadStateCache
    {
        std::vector<HandleState*> entries;
        ~ThreadStateCache()
        {
            // 线程退出（worker join / 进程 teardown）时把缓存 state 交还共享池，
            // 由 Shutdown 统一清空。全局互斥体在 Shutdown 中始终存活（本对象先于
            // g_statePoolMutex 初始化，按标准后销毁），此处取锁安全。
            if (entries.empty()) return;
            std::lock_guard<std::mutex> lock(g_statePoolMutex);
            for (auto* s : entries)
            {
                if (g_statePool.size() < kMaxPooledStates)
                    g_statePool.push_back(s);
                else
                    delete s;
            }
            entries.clear();
        }
    };
    thread_local ThreadStateCache t_stateCache;

    static void FlushStateCacheToSharedPool()
    {
        if (t_stateCache.entries.empty()) return;
        std::lock_guard<std::mutex> lock(g_statePoolMutex);
        for (auto* s : t_stateCache.entries)
        {
            if (g_statePool.size() < kMaxPooledStates)
                g_statePool.push_back(s);
            else
                delete s;
        }
        t_stateCache.entries.clear();
    }

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
    std::atomic<uint64_t> g_nativeBatches{ 0 };
    std::atomic<uint64_t> g_invalidBackendSelections{ 0 };
    std::atomic<int64_t> g_wakeLatencyEwmaNs{ 300'000 };
    std::atomic<uint64_t> g_publishToCompletionEwmaNs{ 0 };
    std::atomic<uint64_t> g_perRangeExecEwmaNs{ 0 };
    std::atomic<uint64_t> g_nextDiagnosticBatchId{ 0 };
    std::atomic<bool> g_shuttingDown{ false };
    std::atomic<bool> g_timingDiagnosticsEnabled{ false };

    // B5: 线程局部"当前 batch"回调。C# 初始化时注册一次；每次 job 执行窗口入口
    // 调 cb(batchId)、出口 cb(0)，托管异常按此绑定到具体 batch（修 V-B）。
    std::atomic<void (*)(uint64_t)> g_currentBatchIdCallback{ nullptr };
    void RegisterCurrentBatchIdCallback(void (*cb)(uint64_t)) noexcept
    {
        g_currentBatchIdCallback.store(cb, std::memory_order_release);
    }
    static inline void SetCurrentBatchId(uint64_t id) noexcept
    {
        auto cb = g_currentBatchIdCallback.load(std::memory_order_acquire);
        if (cb) cb(id);
    }
    // 给非 batch 快速路径 job 分配诊断 id（batch 路径由 SubmitBatch 从
    // batch->diagnosticId 设置），保证 Complete(h) 按 id 抛对应异常。
    static uint64_t AssignStateDiagnosticId(HandleState* state) noexcept
    {
        const uint64_t id = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;
        if (state) state->diagnosticBatchId.store(id, std::memory_order_relaxed);
        return id;
    }

    // B5: 内联同步执行窗口包装。分配诊断 id 并在 C# func 执行期间 set/clear
    // 当前-batch，使内联 job 抛出的异常也能按本 job 归属（而非记到 batch 0
    // 只在 Flush 时抛，避免 Complete(h) 单异常 rethrow 语义回归）。
    template <typename Fn>
    static void RunSyncJob(HandleState* state, Fn&& fn) noexcept
    {
        const uint64_t id = AssignStateDiagnosticId(state);
        SetCurrentBatchId(id);
        fn();
        SetCurrentBatchId(0);
    }

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

    static void WaitForBackendBatches() noexcept;

    void GetStatsSnapshot(JobSystemStatsSnapshot* stats) noexcept
    {
        if (!stats) return;
        WaitForBackendBatches();
        stats->completeWaitLoops = g_completeWaitLoops.load(std::memory_order_relaxed);
        stats->assistAttempts = g_assistAttempts.load(std::memory_order_relaxed);
        stats->assistExecuted = g_assistExecuted.load(std::memory_order_relaxed);
        stats->frameTasksSubmitted = g_frameTasksSubmitted.load(std::memory_order_relaxed);
        stats->workerExecutedRanges = g_workerExecutedRanges.load(std::memory_order_relaxed);
        stats->mainExecutedRanges = g_mainExecutedRanges.load(std::memory_order_relaxed);
        stats->stealCount = g_stealCount.load(std::memory_order_relaxed);
        if (g_nativeWorkerPool)
        {
            uint64_t parkWake = 0, hotSpin = 0;
            g_nativeWorkerPool->GetCounters(&parkWake, &hotSpin);
            stats->parkWakeCount = parkWake;
            stats->hotSpinHits = hotSpin;
        }
        else
        {
            stats->parkWakeCount = g_parkWakeCount.load(std::memory_order_relaxed);
            stats->hotSpinHits = 0;
        }
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

    static void ConsumeLongBatchBarriers() noexcept;
    std::atomic<uint32_t> g_backendBatchesOutstanding{ 0 };

    static void WaitForBackendBatches() noexcept
    {
        uint32_t outstanding =
            g_backendBatchesOutstanding.load(std::memory_order_acquire);
        while (outstanding != 0)
        {
            g_backendBatchesOutstanding.wait(
                outstanding, std::memory_order_relaxed);
            outstanding =
                g_backendBatchesOutstanding.load(std::memory_order_acquire);
        }
    }

    void ResetStatsSnapshot() noexcept
    {
        ConsumeLongBatchBarriers();
        WaitForBackendBatches();
        g_completeWaitLoops.store(0, std::memory_order_relaxed);
        g_assistAttempts.store(0, std::memory_order_relaxed);
        g_assistExecuted.store(0, std::memory_order_relaxed);
        g_frameTasksSubmitted.store(0, std::memory_order_relaxed);
        g_workerExecutedRanges.store(0, std::memory_order_relaxed);
        g_mainExecutedRanges.store(0, std::memory_order_relaxed);
        g_stealCount.store(0, std::memory_order_relaxed);
        g_parkWakeCount.store(0, std::memory_order_relaxed);
        if (g_nativeWorkerPool)
            g_nativeWorkerPool->ResetCounters();
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

    // ---------- State lifecycle ----------
    // 无锁 continuation 节点：fn 完整构造后才 CAS 入原子槽（无发布竞态）。
    // CompleteState 摘取后执行并 delete。槽位 ≤1 节点，CAS 只对 nullptr 比较，
    // 无 Treiber 栈的 ABA 问题（不会拿陈旧节点指针做比较）。
    struct ContinuationNode {
        std::function<void()> fn;
        ContinuationNode* next{ nullptr };
    };

    // 执行并释放一条 continuation 链（含单个节点）。异常吞掉，与旧行为一致。
    static void RunContinuationChain(ContinuationNode* head) noexcept
    {
        while (head)
        {
            ContinuationNode* next = head->next;
            if (head->fn) { try { head->fn(); } catch (...) {} }
            delete head;
            head = next;
        }
    }

    // 兜底取回 state 上可能残留的 continuation（正常路径 CompleteState 已摘尽；
    // 仅供 RecycleState 防泄漏）。
    static void DrainContinuationSlot(HandleState* state) noexcept
    {
        if (auto* leftover = state->continuationSlot.exchange(nullptr, std::memory_order_acq_rel))
            RunContinuationChain(leftover);
    }

    void RecycleState(HandleState* state) noexcept
    {
        if (!state) return;
        // B1: 释放依赖链持有引用（依赖 state 可能仍被自身 batch 持有，不会悬垂）。
        if (state->dependency)
        {
            auto* dep = state->dependency;
            state->dependency = nullptr;
            ReleaseState(dep);
        }
        for (auto* dep : state->dependencies)
            ReleaseState(dep);
        state->dependencies.clear();
        DrainContinuationSlot(state);
        state->hasExtraContinuations.store(false, std::memory_order_relaxed);
        state->continuations.clear();
        state->waiterCount.store(0, std::memory_order_relaxed);
        state->diagnosticBatchId.store(0, std::memory_order_relaxed);
        state->completed.store(false, std::memory_order_relaxed);
        state->backendRetired.store(true, std::memory_order_relaxed);
        state->refCount.store(1, std::memory_order_relaxed);
        state->assistCallback.store(nullptr, std::memory_order_release);
        state->assistContext.store(nullptr, std::memory_order_release);
        state->assistReaders.store(0, std::memory_order_relaxed);
        state->assistReadersDrained.store(nullptr, std::memory_order_release);
        // B2: 先入 per-thread 缓存；满额时一次性迁移共享池（一次锁 / 64 次回收）。
        if (t_stateCache.entries.size() < kStateCacheCap)
        {
            t_stateCache.entries.push_back(state);
            return;
        }
        FlushStateCacheToSharedPool();
        t_stateCache.entries.push_back(state);
    }

    HandleState* CreateState(bool completed)
    {
        HandleState* state = nullptr;
        if (!t_stateCache.entries.empty())
        {
            state = t_stateCache.entries.back();
            t_stateCache.entries.pop_back();
        }
        else
        {
            // B2: 从共享池批量补满线程缓存（一次锁 / 64 次创建），池空则 new。
            std::lock_guard<std::mutex> lock(g_statePoolMutex);
            const size_t available = std::min(g_statePool.size(), kStateCacheCap);
            if (available > 0)
            {
                state = g_statePool.back();
                g_statePool.pop_back();
                for (size_t i = 1; i < available; ++i)
                {
                    t_stateCache.entries.push_back(g_statePool.back());
                    g_statePool.pop_back();
                }
            }
        }
        if (!state) state = new HandleState(completed);
        state->refCount.store(1, std::memory_order_relaxed);
        state->completed.store(completed, std::memory_order_relaxed);
        state->backendRetired.store(true, std::memory_order_relaxed);
        state->waiterCount.store(0, std::memory_order_relaxed);
        state->diagnosticBatchId.store(0, std::memory_order_relaxed);
        state->continuationSlot.store(nullptr, std::memory_order_relaxed);
        state->hasExtraContinuations.store(false, std::memory_order_relaxed);
        state->continuations.clear();
        state->dependency = nullptr;
        state->dependencies.clear();
        return state;
    }

    // B1: 把依赖 state 挂到被依赖 state 上并持引用，保证传递协助链不会悬垂。
    // 释放点在 RecycleState（refcount 归零时）。仅在依赖未完成（需要等）时调用。
    static void RetainDependency(HandleState* state, HandleState* dep) noexcept
    {
        if (!state || !dep) return;
        AcquireState(dep);
        state->dependency = dep;
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

    constexpr uint64_t kLongBatchBarrierNs = 800'000;
    std::mutex g_longBatchBarrierMutex;
    std::vector<HandleState*> g_longBatchBarriers;
    thread_local HandleState* g_completingBatchState = nullptr;
    std::atomic<bool> g_useFineRangesForNextEcsBatch{ false };

    static void RegisterLongBatchBarrier(HandleState* state) noexcept
    {
        if (!state || state->backendRetired.load(std::memory_order_acquire))
            return;
        AcquireState(state);
        std::lock_guard<std::mutex> lock(g_longBatchBarrierMutex);
        g_longBatchBarriers.push_back(state);
    }

    static void ConsumeLongBatchBarriers() noexcept
    {
        std::vector<HandleState*> barriers;
        std::vector<HandleState*> deferred;
        bool waitedForBarrier = false;
        {
            std::lock_guard<std::mutex> lock(g_longBatchBarrierMutex);
            barriers.swap(g_longBatchBarriers);
        }
        for (auto* state : barriers)
        {
            if (state == g_completingBatchState)
            {
                deferred.push_back(state);
                continue;
            }
            while (!state->backendRetired.load(std::memory_order_acquire))
                state->backendRetired.wait(false, std::memory_order_relaxed);
            waitedForBarrier = true;
            ReleaseState(state);
        }
        if (!deferred.empty())
        {
            std::lock_guard<std::mutex> lock(g_longBatchBarrierMutex);
            g_longBatchBarriers.insert(
                g_longBatchBarriers.end(), deferred.begin(), deferred.end());
        }
        if (waitedForBarrier)
            g_useFineRangesForNextEcsBatch.store(true, std::memory_order_release);
    }

    void CompleteState(HandleState* state)
    {
        if (!state) return;
        if (state->completed.exchange(true, std::memory_order_acq_rel)) return;

        // 无锁快路径：原子摘取 continuation 槽（≤1 节点）。completed 先置位再摘取，
        // 保证 AddContinuationOrRunNow 的 G2 重检能看到本摘取已发生或未发生。
        ContinuationNode* node =
            state->continuationSlot.exchange(nullptr, std::memory_order_acq_rel);
        state->completed.notify_all();
        if (node) RunContinuationChain(node);

        // 多 continuation（同 handle 扇出）溢出到 mtx + vector。hasExtra 原子跳过空
        // 路径，使单 continuation 的常见完成路径零 mutex。
        if (state->hasExtraContinuations.exchange(false, std::memory_order_acq_rel))
        {
            std::vector<std::function<void()>> extra;
            {
                std::lock_guard<std::mutex> lock(state->mtx);
                extra.swap(state->continuations);
            }
            for (auto& cont : extra)
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
        // 无锁快路径：单 continuation 直接 CAS 入原子槽。fn 先完整 move 进节点再发布，
        // 无数据竞态；CAS 失败时 move 回调用方走慢路径。
        auto* node = new ContinuationNode{ {}, nullptr };
        node->fn.swap(continuation);
        ContinuationNode* expected = nullptr;
        if (state->continuationSlot.compare_exchange_strong(
            expected, node, std::memory_order_acq_rel, std::memory_order_relaxed))
        {
            // 发布后已完成：Completer 可能已摘取本节点（正常执行），也可能漏掉
            // （摘取早于本 CAS）——此时自己取回并执行，保证每节点恰执行一次。
            if (state->completed.load(std::memory_order_acquire))
            {
                if (auto* mine = state->continuationSlot.exchange(nullptr, std::memory_order_acq_rel))
                    RunContinuationChain(mine);
            }
            return;
        }
        continuation.swap(node->fn);
        delete node;

        // 慢路径：槽已占（第 2+ 个 continuation）。mtx 内判 completed，完成后不再入列。
        std::function<void()> toRun;
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            if (state->completed.load(std::memory_order_acquire)) toRun = std::move(continuation);
            else state->continuations.emplace_back(std::move(continuation));
        }
        if (toRun) { toRun(); return; }
        // 已入列。若 CompleteState 的 hasExtra 摘取早于本发布而漏检（completed 已置位），
        // 取回自己的条目执行；向量已空说明被 Completer 取走，不会重复。
        state->hasExtraContinuations.store(true, std::memory_order_release);
        if (state->completed.load(std::memory_order_acquire))
        {
            std::function<void()> mine;
            {
                std::lock_guard<std::mutex> lock(state->mtx);
                if (!state->continuations.empty())
                {
                    mine = std::move(state->continuations.back());
                    state->continuations.pop_back();
                    if (state->continuations.empty())
                        state->hasExtraContinuations.store(false, std::memory_order_release);
                }
            }
            if (mine) { try { mine(); } catch (...) {} }
        }
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
        if (!g_nativeWorkerPool || !g_nativeWorkerPool->Submit(
            context, 1, &RunBackendAsync, &CompleteBackendAsync))
        {
            RunBackendAsync(context, 0);
            CompleteBackendAsync(context);
        }
    }

    int ResolveChunkSize(int length, int requestedChunk)
    {
        if (length <= 0) return 1;
        if (requestedChunk > 0) return requestedChunk;
        int wc = std::max(1, g_numThreads);
        // 默认 g_configuredTilesPerWorker 个 tile/worker（可调，默认 16），
        // 比 Unity 默认 4/worker 更细：可变代价 job 的负载均衡收益 > claim 开销。
        // batch = N/(W*k) 随 N 自动缩放，无需每 job 标代价。
        return std::max(16, (length + wc * g_configuredTilesPerWorker - 1) / (wc * g_configuredTilesPerWorker));
    }

    // ============================================================
    // Unified execution tiles + dynamic atomic range claiming
    // ============================================================
    static int ResolveWorkerTarget(int workerCap, int targetCount) noexcept
    {
        if (targetCount <= 0) return 1;
        // Match Unity-style worker configuration: by default every job can use
        // the full persistent worker cohort (logical CPU count minus one).
        // An explicit per-job workerCap remains authoritative.
        const int cap = workerCap > 0 ? workerCap : g_numThreads;
        return std::max(1, std::min({ cap, g_numThreads, targetCount }));
    }

    static int ResolveEcsBatchRangeSize(
        int itemCount,
        int workerCount) noexcept
    {
        // Keep enough independently claimable ranges to absorb worker skew,
        // without paying one atomic claim/callback for every physical chunk.
        constexpr int kTargetTilesPerWorker = 4;
        constexpr int kMinChunksPerTile = 4;
        constexpr int kMaxChunksPerTile = 32;
        const int targetTiles = std::max(
            1, workerCount * kTargetTilesPerWorker);
        const int chunksPerTile =
            (itemCount + targetTiles - 1) / targetTiles;
        return std::clamp(
            chunksPerTile,
            kMinChunksPerTile,
            kMaxChunksPerTile);
    }
    // A Tile is the load-balancing unit — one or more chunks (IJobChunk)
    // or a sub-range of entities (IJobEntity).
    enum class TileKind : uint8_t
    {
        GeneralRange,
        ChunkCallbacks,
        ChunkRange,
        EntityBatchRange
    };

    struct ExecutionTile {
        uint32_t firstItem;
        uint32_t itemCount;
        TileKind kind;
    };

    // Guided（OpenMP schedule(guided) 同族）tile 大小：chunk = ceil(remaining/(W*k))，
    // 头部大块（Poisson 平滑、非 straggler）、尾部递减到 floor（钳 straggler 上界）。
    // 总认领数 ~ W*k*ln(N/floor)，少于 uniform k=26 的同时尾部更细。
    // k/floor 由 JobSystem_ConfigureGuided 配置。返回实际 tile 数。
    static int GuidedTileCount(int length, int workerCount, int k, int floor) noexcept
    {
        const int denom = std::max(1, workerCount) * std::max(1, k);
        const int f = std::max(1, floor);
        int offset = 0;
        int count = 0;
        while (offset < length)
        {
            const int remaining = length - offset;
            int size = (remaining + denom - 1) / denom;   // ceil(remaining/denom)
            if (size < f) size = f;                        // floor 兜底
            if (size > remaining) size = remaining;
            offset += size;
            ++count;
        }
        return count;
    }

    static int BuildGuidedTiles(ExecutionTile* tiles, int length, int workerCount,
        int k, int floor, TileKind kind = TileKind::GeneralRange) noexcept
    {
        const int denom = std::max(1, workerCount) * std::max(1, k);
        const int f = std::max(1, floor);
        int offset = 0;
        int i = 0;
        while (offset < length)
        {
            const int remaining = length - offset;
            int size = (remaining + denom - 1) / denom;   // ceil(remaining/denom)
            if (size < f) size = f;                        // floor 兜底
            if (size > remaining) size = remaining;
            tiles[i] = { static_cast<uint32_t>(offset),
                static_cast<uint32_t>(size), kind };
            offset += size;
            ++i;
        }
        return i;   // 实际 tile 数
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

        // Unified lightweight BatchRange path. Physical ECS chunks remain
        // storage boundaries; tiles are contiguous descriptor/index ranges.
        ExecutionTile* tiles{ nullptr };
        uint32_t tileCount{ 0 };
        std::atomic<uint32_t> nextTile{ 0 };
        uint32_t workerCount{ 0 };
        std::atomic<uint32_t> workerSlotsEntered{ 0 };
        // Logical completion is driven by finished tiles, not by participant
        // task retirement.  Slow/late worker slots may still be unwinding the
        // steal loop after the public JobHandle is already complete.
        std::atomic<uint32_t> tilesRemaining{ 0 };
        std::atomic<bool> logicalCompleted{ false };

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

        // Physical retirement is deliberately separate from logical
        // completion because worker slots and Complete() assist readers still
        // reference the scheduler metadata after the last callback finishes.
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

        BatchStorage() noexcept { batch.storage = this; }
        ~BatchStorage()
        {
            delete[] tileBuffer;
        }
    };

    // 近无锁：batch storage per-thread 缓存。命中零锁；共享池仅在缓存空/满时批量
    // 迁移（一次锁 / ~8 次 acquire 或 release）。弃用原 O(n) 最佳适配扫描——buffer
    // 在 AcquireBatchStorage 按需增长，缓存里任何 storage 都可用，扫描纯属全局锁竞争点。
    std::mutex g_batchStoragePoolMutex;
    std::vector<BatchStorage*> g_batchStoragePool;

    constexpr size_t kBatchStorageCacheCap = 8;
    struct ThreadBatchStorageCache
    {
        std::vector<BatchStorage*> entries;
        ~ThreadBatchStorageCache()
        {
            // 线程退出（worker join / 进程 teardown）时把缓存 storage 交还共享池或释放
            // （池满）。全局互斥体在 Shutdown 中始终存活（本对象先于 g_batchStoragePoolMutex
            // 初始化，按标准后销毁），此处取锁安全。
            if (entries.empty()) return;
            std::lock_guard<std::mutex> lock(g_batchStoragePoolMutex);
            for (auto* s : entries)
            {
                if (g_batchStoragePool.size() < kMaxPooledBatchStorage)
                    g_batchStoragePool.push_back(s);
                else
                {
                    g_batchStorageDropped.fetch_add(1, std::memory_order_relaxed);
                    delete s;
                }
            }
            entries.clear();
        }
    };
    thread_local ThreadBatchStorageCache t_batchStorageCache;

    static void FlushBatchStorageCacheToSharedPool()
    {
        if (t_batchStorageCache.entries.empty()) return;
        std::lock_guard<std::mutex> lock(g_batchStoragePoolMutex);
        for (auto* s : t_batchStorageCache.entries)
        {
            if (g_batchStoragePool.size() < kMaxPooledBatchStorage)
                g_batchStoragePool.push_back(s);
            else
            {
                g_batchStorageDropped.fetch_add(1, std::memory_order_relaxed);
                delete s;
            }
        }
        t_batchStorageCache.entries.clear();
    }

    static BatchStorage* AcquireBatchStorage(uint32_t tileCapacity)
    {
        BatchStorage* storage = nullptr;
        if (!t_batchStorageCache.entries.empty())
        {
            storage = t_batchStorageCache.entries.back();
            t_batchStorageCache.entries.pop_back();
            g_batchStorageReused.fetch_add(1, std::memory_order_relaxed);
        }
        else
        {
            // 缓存空：一次性从共享池批量补满（一次锁），池空则 new。
            {
                std::lock_guard<std::mutex> lock(g_batchStoragePoolMutex);
                const size_t available =
                    std::min(g_batchStoragePool.size(), kBatchStorageCacheCap);
                if (available > 0)
                {
                    storage = g_batchStoragePool.back();
                    g_batchStoragePool.pop_back();
                    for (size_t i = 1; i < available; ++i)
                    {
                        t_batchStorageCache.entries.push_back(g_batchStoragePool.back());
                        g_batchStoragePool.pop_back();
                    }
                }
            }
            if (storage)
                g_batchStorageReused.fetch_add(1, std::memory_order_relaxed);
            else
            {
                storage = new BatchStorage();
                g_batchStorageCreated.fetch_add(1, std::memory_order_relaxed);
            }
        }

        if (storage->tileCapacity < tileCapacity)
        {
            auto* replacement = new ExecutionTile[tileCapacity];
            delete[] storage->tileBuffer;
            storage->tileBuffer = replacement;
            storage->tileCapacity = tileCapacity;
        }
        storage->batch.storage = storage;
        storage->batch.tiles = tileCapacity > 0 ? storage->tileBuffer : nullptr;
        return storage;
    }

    static void ReleaseBatchStorage(BatchStorage* storage) noexcept
    {
        if (!storage) return;
        std::destroy_at(&storage->batch);
        std::construct_at(&storage->batch);
        storage->batch.storage = storage;
        g_batchStorageReturned.fetch_add(1, std::memory_order_relaxed);

        // 近无锁：先入 per-thread 缓存；满额时整体迁移共享池（一次锁 / ~8 次回收）。
        if (t_batchStorageCache.entries.size() < kBatchStorageCacheCap)
        {
            t_batchStorageCache.entries.push_back(storage);
            return;
        }
        FlushBatchStorageCacheToSharedPool();
        t_batchStorageCache.entries.push_back(storage);
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

    // ============================================================
    // Partition-based execution (Phase 1)
    // ============================================================
    static void TryCompleteLogicalBatch(BatchState* batch) noexcept;

    // Forward declaration for tile prefetch (defined after ChunkBatchContext).
    static void PrefetchNextTileData(void* context, const ExecutionTile& nextTile) noexcept;

    // Process one tile and update completion counter.
    // Returns true if the tile was processed (for assist comptability).
    static bool TryExecuteOneTile(
        BatchState* batch,
        uint32_t tileIndex) noexcept
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

        // Prefetch the next tile's data (delegated to a helper below that
        // has access to the full ChunkBatchContext layout).
        if (tileIndex + 1 < batch->tileCount)
            PrefetchNextTileData(batch->context, batch->tiles[tileIndex + 1]);

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
        PushTraceEvent(TraceEventType::ExecuteEnd, batch->diagnosticId,
            static_cast<int>(tileIndex),
            static_cast<int>(tile.firstItem),
            static_cast<int>(tile.itemCount));

        // Completion follows actual callback completion.  This is the hot-path
        // atomic that replaces the much more expensive requirement that every
        // published participant slot must first enter and retire.
        if (batch->tilesRemaining.fetch_sub(1, std::memory_order_acq_rel) == 1)
        {
            batch->lastTileAt.store(MonotonicNowNs(), std::memory_order_release);
            TryCompleteLogicalBatch(batch);
        }
        return true;
    }

    static void RecordWorkerEntry(BatchState* batch) noexcept
    {
        const uint64_t now = MonotonicNowNs();
        uint64_t empty = 0;
        batch->firstWorkerAt.compare_exchange_strong(
            empty, now, std::memory_order_acq_rel, std::memory_order_relaxed);
        if (batch->workerSlotsEntered.fetch_add(1, std::memory_order_acq_rel) + 1 ==
            batch->workerCount)
            batch->lastWorkerAt.store(now, std::memory_order_release);
    }

    static void WorkerAtomicRangeLoop(BatchState* batch) noexcept
    {
        RecordWorkerEntry(batch);
        const uint64_t active =
            g_activeWorkers.fetch_add(1, std::memory_order_acq_rel) + 1;
        uint64_t peak = g_activeWorkersPeak.load(std::memory_order_relaxed);
        while (active > peak && !g_activeWorkersPeak.compare_exchange_weak(
            peak, active, std::memory_order_relaxed)) {}
        g_workerClaimedTokens.fetch_add(1, std::memory_order_relaxed);

        uint64_t executed = 0;
        while (true)
        {
            const uint32_t tile = batch->nextTile.fetch_add(
                1, std::memory_order_relaxed);
            if (tile >= batch->tileCount) break;
            TryExecuteOneTile(batch, tile);
            ++executed;
        }

        g_workerExecutedRanges.fetch_add(executed, std::memory_order_relaxed);
        g_localTiles.fetch_add(executed, std::memory_order_relaxed);
        g_activeWorkers.fetch_sub(1, std::memory_order_acq_rel);
    }

    static bool AssistExecuteOneTile(void* ptr) noexcept
    {
        auto* batch = static_cast<BatchState*>(ptr);
        if (!batch) return false;
        const uint32_t tile = batch->nextTile.fetch_add(
            1, std::memory_order_relaxed);
        if (tile >= batch->tileCount) return false;
        SetCurrentBatchId(batch->diagnosticId);
        TryExecuteOneTile(batch, tile);
        SetCurrentBatchId(0);
        g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
        g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
        g_assistTiles.fetch_add(1, std::memory_order_relaxed);
        batch->batchAssistTiles.fetch_add(1, std::memory_order_relaxed);
        return true;
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
        // Always retain cheap batch-boundary timing. Per-tile CPU/core/cycle
        // diagnostics remain gated by g_timingDiagnosticsEnabled.
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

    static void TryCompleteLogicalBatch(BatchState* batch) noexcept
    {
        // handle is cleared by construct_at in ReleaseBatchStorage, so a null
        // handle means the batch was already finalized, retired and recycled.
        // Finalization is single-owned by the last tile executor, but keep this
        // guard so a stale duplicate call can never touch a recycled batch's
        // state (would crash on the null-handle dereference below).
        if (!batch || !batch->handle) return;
        if (batch->logicalCompleted.exchange(
            true, std::memory_order_acq_rel)) return;

        auto* state = batch->handle;
        // Stop admitting new assist calls. Readers that already captured the
        // callback can only observe empty partitions at this point.
        state->assistCallback.store(nullptr, std::memory_order_release);

        RecordFinalizedBatchTiming(batch);
        const uint64_t publishedAt =
            batch->publishedAt.load(std::memory_order_acquire);
        const uint64_t lastTileAt =
            batch->lastTileAt.load(std::memory_order_acquire);
        if (publishedAt != 0 && lastTileAt >= publishedAt + kLongBatchBarrierNs)
            RegisterLongBatchBarrier(state);
        auto* previousCompletingState = g_completingBatchState;
        g_completingBatchState = state;
        PushTraceEvent(TraceEventType::FinalizeBegin,
            batch->diagnosticId, -1, 0, 0);
        if (batch->cleanup)
        {
            batch->cleanup(batch->context);
            batch->context = nullptr;
        }
        PushTraceEvent(TraceEventType::HandleComplete,
            batch->diagnosticId, -1, 0, 0);
        CompleteState(state);
        g_completingBatchState = previousCompletingState;
    }

    static void TryRetireCompletedBatch(HandleState* state) noexcept
    {
        if (!state) return;

        BatchState* batch = nullptr;
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            batch = static_cast<BatchState*>(
                state->assistContext.load(std::memory_order_acquire));
            if (!batch ||
                !batch->logicalCompleted.load(std::memory_order_acquire) ||
                !batch->workersFinished.load(std::memory_order_acquire) ||
                state->assistReaders.load(std::memory_order_acquire) != 0)
            {
                return;
            }

            state->assistContext.store(nullptr, std::memory_order_release);
            state->assistReadersDrained.store(nullptr, std::memory_order_release);
        }

        if (!batch->finalized.exchange(true, std::memory_order_acq_rel))
        {
            ReleaseBatch(batch);
            state->backendRetired.store(true, std::memory_order_release);
            state->backendRetired.notify_all();
            g_backendBatchesOutstanding.fetch_sub(
                1, std::memory_order_acq_rel);
            g_backendBatchesOutstanding.notify_all();
        }
    }

    // The last assist reader only requests finalization. Batch memory remains
    // alive until every worker slot has also finished its tile loop.
    static void OnAssistReadersDrained(void* handlePtr) noexcept
    {
        TryRetireCompletedBatch(static_cast<HandleState*>(handlePtr));
    }

    // Acquire assist reader: returns false if batch is already finalized
    static void ExecuteBatchSlot(void* raw, uint32_t slot) noexcept
    {
        auto* batch = static_cast<BatchState*>(raw);
        const bool timingEnabled = g_timingDiagnosticsEnabled.load(std::memory_order_relaxed);
        const int startProcessor = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        if (WorkerIndexManager::GetCurrentIndex() < 0)
            WorkerIndexManager::SetCurrentIndex(WorkerIndexManager::AllocateIndex());
        // B5: worker 整个 slot 只执行这一个 batch —— 窗口一次，覆盖槽内所有 tile。
        SetCurrentBatchId(batch->diagnosticId);
        WorkerAtomicRangeLoop(batch);
        SetCurrentBatchId(0);
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
        // Logical completion is owned exclusively by the last tile executor
        // (TryCompleteLogicalBatch in TryExecuteOneTile), which provably cannot
        // run on a retired/recycled batch (it holds a worker slot or an assist
        // reader, both of which block retirement). The old defensive second
        // call here could double-finalize a batch after it was recycled —
        // removed; tileCount is always >= 1 so no empty-batch path needs it.
        TryRetireCompletedBatch(state);
        ReleaseState(state);
    }

    static void SubmitBatch(BatchState* batch, int /*workerCap*/ = 0)
    {
        auto* state = batch->handle;
        bool (*assistFn)(void*) noexcept = &AssistExecuteOneTile;
        const int participantCount = std::max(1, static_cast<int>(batch->workerCount));

        g_frameTasksSubmitted.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);
        g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
        g_workerTargetTotal.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);
        g_totalTilesPublished.fetch_add(
            static_cast<uint64_t>(batch->tileCount),
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
        state->backendRetired.store(false, std::memory_order_release);
        g_backendBatchesOutstanding.fetch_add(1, std::memory_order_acq_rel);
        const uint64_t publishedAt = MonotonicNowNs();
        batch->publishedAt.store(publishedAt, std::memory_order_release);
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
    };

    // Prefetch data for the next tile. Called from TryExecuteOneTile before
    // executing the current tile, so DRAM reads for the next batch overlap
    // with computation of the current one.
    static void PrefetchNextTileData(void* context, const ExecutionTile& nextTile) noexcept
    {
        auto* cc = static_cast<ChunkBatchContext*>(context);
        if (nextTile.kind == TileKind::EntityBatchRange)
        {
            const auto* nextBatch = &cc->entityBatches[nextTile.firstItem];
            if (nextBatch->componentArrays)
            {
                _mm_prefetch(
                    reinterpret_cast<const char*>(nextBatch->componentArrays[0]),
                    _MM_HINT_NTA);
            }
        }
        else if (nextTile.kind == TileKind::ChunkCallbacks ||
                 nextTile.kind == TileKind::ChunkRange)
        {
            const auto& nextChunk = cc->chunks[nextTile.firstItem];
            if (nextChunk.entityArray)
                _mm_prefetch(
                    reinterpret_cast<const char*>(nextChunk.entityArray),
                    _MM_HINT_NTA);
        }
    }

    // Unified Tile executor for Chunk callbacks, Chunk ranges and Entity ranges.
    static bool ChunkExecuteTile(void* ctx, const ExecutionTile& tile) noexcept
    {
        auto* bc = static_cast<ChunkBatchContext*>(ctx);
        switch (tile.kind)
        {
        case TileKind::GeneralRange:
            return false;
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
    };

    static bool GeneralExecuteTile(void* ctx, const ExecutionTile& tile) noexcept
    {
        auto* bc = static_cast<GeneralBatchContext*>(ctx);
        const int start = static_cast<int>(tile.firstItem);
        const int count = static_cast<int>(tile.itemCount);
        if (bc->batchFunc)
            bc->batchFunc(bc->originalContext, start, count);
        else
            for (int i = start; i < start + count; ++i)
                bc->indexFunc(bc->originalContext, i);
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

    // B1: 协助单个 state —— 认领并执行其 tile 直到无工作或已完成。
    // 调用方被计为该 state 的一个 assistReader（生命周期与 Complete 一致）。
    // 返回是否实际执行了任何 tile。
    static bool AssistState(HandleState* state) noexcept
    {
        if (!state || state->completed.load(std::memory_order_acquire)) return false;
        bool worked = false;
        state->assistReaders.fetch_add(1, std::memory_order_acq_rel);
        auto cb = state->assistCallback.load(std::memory_order_acquire);
        auto ctx = state->assistContext.load(std::memory_order_acquire);
        if (cb && ctx && !state->completed.load(std::memory_order_acquire))
        {
            g_assistAttempts.fetch_add(1, std::memory_order_relaxed);
            // Unlimited assist: 认领 tile 直到无工作剩余，消除 P95 尾部延迟。
            while (!state->completed.load(std::memory_order_acquire))
            {
                if (!cb(ctx)) break;
                worked = true;
                g_mainClaimedTokens.fetch_add(1, std::memory_order_relaxed);
            }
        }
        if (state->assistReaders.fetch_sub(1, std::memory_order_acq_rel) == 1)
        {
            auto drained = state->assistReadersDrained.load(std::memory_order_acquire);
            if (drained) drained(state);
        }
        return worked;
    }

    // B1: 传递依赖链协助。目标 job 未提交（前驱还在跑）时，沿 dependency 链
    // 回溯协助所有未完成祖先执行其 tile，让链推进到目标。worker 内嵌套
    // Complete() 不再 park 空等，而是成为自己依赖链的执行者（消解 V-A 死锁）；
    // 主线程也从空等变干活（修 V-D）。单依赖走 dependency，合并依赖走
    // dependencies 向量；DAG 无环，固定容量栈做安全网。
    //
    // 迭代语义：一次 pass 认领不到 tile 并不代表链卡死 —— workers 可能正在
    // 执行祖先的 tile，即将触发其 continuation 提交下一环（EntJoy 的提交是
    // deferred 的，随依赖完成逐个 submit）。若在第一个零工作 pass 就 break，
    // 调用方会 park，而此时链上其余 worker 正被 gate 在调用方（如嵌套
    // Complete 场景）→ 退化为 V-A 死锁。因此链未完成时持续回访：只要任一
    // pass 推进了链就重置墙钟预算；零工作 pass 加 yield 降频（把 CPU 让给
    // 正在推进的 worker），仅持续零工作超过 kAssistStallBudgetNs 才放弃，
    // 交还调用方的 spin/futex 等待。
    static void AssistDependencyChain(HandleState* target) noexcept
    {
        HandleState* stack[64];
        uint64_t budgetEnd = MonotonicNowNs() + kAssistStallBudgetNs;
        while (!target->completed.load(std::memory_order_acquire))
        {
            bool worked = false;
            int sp = 0;
            if (sp < 64) stack[sp++] = target;
            if (target->dependency && sp < 64) stack[sp++] = target->dependency;
            for (auto* d : target->dependencies)
                if (sp < 64) stack[sp++] = d;
            while (sp > 0 && !target->completed.load(std::memory_order_acquire))
            {
                auto* cur = stack[--sp];
                if (!cur) continue;
                if (AssistState(cur)) worked = true;
                if (cur->dependency && sp < 64) stack[sp++] = cur->dependency;
                for (auto* d : cur->dependencies)
                    if (sp < 64) stack[sp++] = d;
            }
            if (worked)
            {
                // 本 pass 推进了链（认领并执行了 tile）→ 重置墙钟预算。
                budgetEnd = MonotonicNowNs() + kAssistStallBudgetNs;
                continue;
            }
            // 零工作 pass：链可能仍在其他线程上推进。yield 降频 + 有界回访，
            // 覆盖祖先 completion → 下一环 submit 的交接窗口；墙钟预算耗尽后
            // 交还调用方的 spin/futex（正常场景 workers 自行跑完，futex 即醒）。
            if (MonotonicNowNs() >= budgetEnd) break;
            std::this_thread::yield();
        }
    }

    void JobHandle::Complete() const
    {
        if (!_state) return;

        const uint64_t diagnosticId =
            _state->diagnosticBatchId.load(std::memory_order_acquire);
        if (diagnosticId != 0)
            PushTraceEvent(TraceEventType::CompleteEnter, diagnosticId, -1, 0, 0);

        if (_state->completed.load(std::memory_order_acquire)) return;

        // Phase 0: 协助目标 job 自身（reader 计数在 HandleState 上，生命周期长于 batch）
        if (AssistState(_state))
        {
            if (_state->completed.load(std::memory_order_acquire)) return;
        }

        // Phase 0.5 (B1): 目标无 tile 可认领（可能根本没被提交——前驱还在跑），
        // 沿依赖链回溯协助祖先。无依赖的 job 此路径完全不执行，零回归。
        if (!_state->completed.load(std::memory_order_acquire) &&
            (_state->dependency || !_state->dependencies.empty()))
        {
            AssistDependencyChain(_state);
            if (_state->completed.load(std::memory_order_acquire)) return;
        }

        // Phase 2: dense spin first (never yield before we've given the job a
        // chance to complete — yield triggers a full OS context switch).
        for (int i = 0; i < 2048; i++)
        {
            if (_state->completed.load(std::memory_order_acquire)) return;
            CpuPause();
        }
        if (_state->completed.load(std::memory_order_acquire)) return;

        // Brief yield — let other threads run if the job is truly not done.
        std::this_thread::yield();

        // One more short spin after yielding.
        for (int i = 0; i < 256; i++)
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
        // B1: 合成 state 持有每个父依赖的引用，保证传递协助链不悬垂；
        // 在 RecycleState 释放。
        cs->dependencies = pending;
        for (auto* ds : pending) {
            AcquireState(ds);
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
        AssignStateDiagnosticId(state);
        auto* ds = dep.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { builder(state); return JobHandle(state); }
        AcquireState(state);
        RetainDependency(state, ds);
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
            // B5: 非 batch 快速路径异步窗口——work() 即 C# func 执行点，
            // 执行期间 set/clear 当前-batch 使异常按本 job 归属。
            const uint64_t id = state->diagnosticBatchId.load(std::memory_order_acquire);
            if (id != 0) SetCurrentBatchId(id);
            try { work(); } catch (...) {}
            if (id != 0) SetCurrentBatchId(0);
            if (cleanup) cleanup(ctx);
            CompleteState(state);
            ReleaseState(state);
        });
    }

    template <typename Work>
    JobHandle ScheduleFastPath(Work&& work, void* ctx, void (*cleanup)(void*), const JobHandle& dep)
    {
        auto* state = CreateState(false);
        AssignStateDiagnosticId(state);
        auto* ds = dep.State();
        if (!ds || ds->completed.load(std::memory_order_acquire))
        { FastPath(std::forward<Work>(work), ctx, cleanup, state); return JobHandle(state); }
        AcquireState(state);
        RetainDependency(state, ds);
        AddContinuationOrRunNow(ds, [state, work = std::forward<Work>(work), ctx, cleanup]() mutable {
            FastPath(std::forward<Work>(work), ctx, cleanup, state);
            ReleaseState(state);
        });
        return JobHandle(state);
    }

    // ============================================================
    // Scheduler
    // ============================================================
    static bool ResolveWorkerAffinityEnabled() noexcept
    {
        std::string value;
#if defined(_WIN32)
        char* raw = nullptr;
        std::size_t rawLength = 0;
        if (_dupenv_s(&raw, &rawLength, "ENTJOY_WORKER_AFFINITY") == 0 && raw)
        {
            value.assign(raw);
            std::free(raw);
        }
#else
        if (const char* raw = std::getenv("ENTJOY_WORKER_AFFINITY"))
            value.assign(raw);
#endif
        std::transform(value.begin(), value.end(), value.begin(),
            [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
        return value == "1" || value == "true" || value == "on";
    }

    void Scheduler::Initialize(int numThreads)
    {
        g_shuttingDown.store(false, std::memory_order_release);
#if defined(_WIN32)
        // Raise this process above typical background load so worker threads
        // are deprioritized less when competing with the OS and other processes.
        ::SetPriorityClass(::GetCurrentProcess(), ABOVE_NORMAL_PRIORITY_CLASS);
        // Raise timer resolution from the default ~15.6 ms to 1 ms so that
        // semaphore wait/notify and condition-variable timeouts are more
        // responsive.  The OS-wide effect is negligible for a game process.
        ::timeBeginPeriod(1);
#endif
        {
            std::lock_guard<std::mutex> lock(g_schedulerMutex);
            int resolved = numThreads > 0 ? numThreads :
                (g_numThreads > 0 ? g_numThreads :
                    std::max(1, static_cast<int>(std::thread::hardware_concurrency()) - 1));
            if (g_nativeWorkerPool && g_nativeWorkerPool->IsRunning())
                return;
            g_numThreads = resolved;
            g_workerAffinityEnabled.store(
                ResolveWorkerAffinityEnabled(), std::memory_order_relaxed);

            // Pin the calling thread (main thread) to logical core 0 so it
            // is never preempted by a worker that shares its L1/L2 cache.
            if (g_workerAffinityEnabled.load(std::memory_order_relaxed))
                BindCurrentThreadToLogicalProcessor(0);

            g_nativeWorkerPool = std::make_unique<NativeWorkerPool>();
            g_nativeWorkerPool->Start(
                static_cast<uint32_t>(resolved),
                g_workerAffinityEnabled.load(std::memory_order_relaxed));
        }
    }

    void Scheduler::Shutdown()
    {
        g_shuttingDown.store(true, std::memory_order_release);
        std::unique_ptr<NativeWorkerPool> nativePool;
        {
            std::lock_guard<std::mutex> lock(g_schedulerMutex);
            nativePool = std::move(g_nativeWorkerPool);
            g_numThreads = 0;
        }
        if (nativePool) nativePool->Stop();
        ConsumeLongBatchBarriers();
        // 近无锁：先把 main 线程缓存中的 batch storage 交还共享池，再统一清空。
        // worker 已由 nativePool->Stop() join，其 thread_local 缓存已在退出时交还。
        FlushBatchStorageCacheToSharedPool();
        ClearBatchStoragePool();
        // B2: 先把当前线程（main）缓存中的 state 交还共享池，再统一清空。
        // worker 线程已由 nativePool->Stop() join，其 thread_local 缓存已在
        // 退出时交还，故此处清空覆盖全部 state。
        FlushStateCacheToSharedPool();
        { std::lock_guard<std::mutex> lock(g_statePoolMutex); for (auto* s : g_statePool) delete s; g_statePool.clear(); }
    }

    void Scheduler::PrewakeWorkers()
    {
        // C# 初始化经 GetProcAddress 解析此导出，保留为 no-op。
        // NativeWorkerPool 的 worker 常驻 spin/futex，无需显式唤醒。
    }

    void Scheduler::KeepWorkersWarm(int /*microseconds*/)
    {
        // keep-warm 实验已还原（数据：紧循环无效、睡眠模式回归）。no-op。
    }

    void Scheduler::ConfigureTilesPerWorker(int tilesPerWorker)
    {
        // 并行 for 默认粒度（batchSize=0 时 ResolveChunkSize 用）。Initialize 期调用，写后由 job
        // 提交的 release/acquire 对 worker 可见。默认 16，见 kDefaultTilesPerWorker 注释。
        g_configuredTilesPerWorker = std::max(1, tilesPerWorker);
    }

    void Scheduler::ConfigureGuided(int enabled, int k, int floor)
    {
        // guided（chunk ∝ 剩余工作量）tile 调度开关 + 参数。Initialize 期调用，
        // 写后由 job 提交的 release/acquire 对 worker 可见。0=off（uniform 现状）。
        g_guidedEnabled = enabled != 0 ? 1 : 0;
        g_guidedK = std::max(1, k);
        g_guidedFloor = std::max(1, floor);
    }

    void Scheduler::SetFrameLowLatencyMode(bool /*enabled*/) {}
    void Scheduler::FlushScheduledJobs() {}

    // ---------- IJob ----------
    JobHandle Scheduler::Schedule(void (*func)(void*), void* context, void (*cleanup)(void*), const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire)) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if (!func) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if (!dependency.State() || dependency.IsCompleted())
        {
            auto* st = CreateState(true);
            RunSyncJob(st, [func, context]() { func(context); });
            if (cleanup) cleanup(context);
            return JobHandle(st);
        }
        return ScheduleFastPath([func, context]() { func(context); }, context, cleanup, dependency);
    }

    // ---------- IJobFor ----------
    JobHandle Scheduler::ScheduleFor(void (*func)(void*, int), void* context, int length, void (*cleanup)(void*), const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire)) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if (!func || length <= 0) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        bool depOk = !dependency.State() || dependency.IsCompleted();
        // 依赖未完成时绝不 inline —— 必须先等依赖。两条阈值仅在 depOk（无依赖或依赖已完成）下生效。
        if (depOk && (length <= kSyncExecutionLengthThreshold || length <= kSyncWithCompletedDepThreshold))
        {
            auto* st = CreateState(true);
            RunSyncJob(st, [func, context, length]() { for (int i = 0; i < length; i++) func(context, i); });
            if (cleanup) cleanup(context);
            return JobHandle(st);
        }
        if (length <= 64) return ScheduleFastPath([func, context, length]() { for (int i = 0; i < length; i++) func(context, i); }, context, cleanup, dependency);
        return ScheduleWithDependency(dependency, [func, context, length, cleanup](HandleState* state) {
            AcquireState(state);
            SubmitBackendAsync([func, context, length, cleanup, state]() {
                // B5: state 由 ScheduleWithDependency 分配诊断 id，异步窗口同样需要归属。
                const uint64_t id = state->diagnosticBatchId.load(std::memory_order_acquire);
                if (id != 0) SetCurrentBatchId(id);
                for (int i = 0; i < length; i++) func(context, i);
                if (id != 0) SetCurrentBatchId(0);
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
        ConsumeLongBatchBarriers();
        if (!func || length <= 0) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        bool depOk = !dependency.State() || dependency.IsCompleted();
        // 依赖未完成时绝不 inline —— 必须先等依赖。两条阈值仅在 depOk（无依赖或依赖已完成）下生效。
        if (depOk && (length <= kSyncExecutionLengthThreshold || length <= kSyncWithCompletedDepThreshold))
        {
            auto* st = CreateState(true);
            RunSyncJob(st, [func, context, length]() { for (int i = 0; i < length; i++) func(context, i); });
            if (cleanup) cleanup(context);
            return JobHandle(st);
        }
        int cs = ResolveChunkSize(length, batchSize);
        int rc = (length + cs - 1) / cs;
        if (rc <= 1) return ScheduleFastPath([func, context, length]() { for (int i = 0; i < length; i++) func(context, i); }, context, cleanup, dependency);

        const uint32_t targetWorkers = static_cast<uint32_t>(
            ResolveWorkerTarget(0, rc));
        auto* bc = new GeneralBatchContext{ func, nullptr, context, cleanup };
        // guided 只作用于 batchSize=0 的默认路径（用户显式 batchSize 走 uniform）。
        const bool guided = g_guidedEnabled != 0 && batchSize <= 0;
        const int tileCount = guided
            ? GuidedTileCount(length, static_cast<int>(targetWorkers), g_guidedK, g_guidedFloor)
            : rc;
        auto* storage = AcquireBatchStorage(
            static_cast<uint32_t>(tileCount));
        auto* batch = &storage->batch;
        auto* state = CreateState(false); batch->handle = state;
        batch->context = bc; batch->cleanup = [](void* ctx) { CleanupGeneralContext(ctx); };
        batch->executeTile = &GeneralExecuteTile;
        batch->tileCount = static_cast<uint32_t>(tileCount);
        batch->nextTile.store(0, std::memory_order_relaxed);
        batch->tilesRemaining.store(batch->tileCount, std::memory_order_relaxed);
        if (guided)
        {
            BuildGuidedTiles(storage->tileBuffer, length,
                static_cast<int>(targetWorkers), g_guidedK, g_guidedFloor);
        }
        else
        {
            for (uint32_t i = 0; i < batch->tileCount; ++i)
            {
                const uint32_t first = i * static_cast<uint32_t>(cs);
                storage->tileBuffer[i] = {
                    first,
                    std::min(static_cast<uint32_t>(cs),
                        static_cast<uint32_t>(length) - first),
                    TileKind::GeneralRange };
            }
        }
        batch->tiles = storage->tileBuffer;
        batch->workerCount = targetWorkers;
        batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

        PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

        auto* ds = dependency.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitBatch(batch); }
        else { AcquireState(state); RetainDependency(state, ds); AddContinuationOrRunNow(ds, [state, batch]() { SubmitBatch(batch); ReleaseState(state); }); }
        return JobHandle(state);
    }

    // ---------- IJobParallelForBatch ----------
    JobHandle Scheduler::ScheduleParallelForBatch
    (void (*func)(void*, int, int), void* context, int length, int batchSize, void (*cleanup)(void*), const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire)) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        ConsumeLongBatchBarriers();
        if (!func || length <= 0) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        bool depOk = !dependency.State() || dependency.IsCompleted();
        bool forceAsync = batchSize < 0; int reqBatch = forceAsync ? -batchSize : batchSize;
        if (!forceAsync && depOk && (length <= kSyncExecutionLengthThreshold || length <= kSyncWithCompletedDepThreshold))
        {
            auto* st = CreateState(true);
            RunSyncJob(st, [func, context, length]() { func(context, 0, length); });
            if (cleanup) cleanup(context);
            return JobHandle(st);
        }
        int cs = std::max(1, reqBatch > 0 ? reqBatch : ResolveChunkSize(length, 0));
        int rc = (length + cs - 1) / cs;
        if (!forceAsync && depOk && rc <= 1)
        {
            auto* st = CreateState(true);
            RunSyncJob(st, [func, context, length]() { func(context, 0, length); });
            if (cleanup) cleanup(context);
            return JobHandle(st);
        }
        // 依赖未完成或强制异步时不得 inline：走 ScheduleFastPath（按依赖排序的池任务）。
        if (rc <= 1)
            return ScheduleFastPath([func, context, length]() { func(context, 0, length); }, context, cleanup, dependency);

        const uint32_t targetWorkers = static_cast<uint32_t>(
            ResolveWorkerTarget(0, rc));
        auto* bc = new GeneralBatchContext{ nullptr, func, context, cleanup };
        // guided 只作用于 batchSize=0 的默认路径（用户显式 batchSize / forceAsync 走 uniform）。
        const bool guided = g_guidedEnabled != 0 && reqBatch <= 0;
        const int tileCount = guided
            ? GuidedTileCount(length, static_cast<int>(targetWorkers), g_guidedK, g_guidedFloor)
            : rc;
        auto* storage = AcquireBatchStorage(
            static_cast<uint32_t>(tileCount));
        auto* batch = &storage->batch; auto* state = CreateState(false); batch->handle = state;
        batch->context = bc; batch->cleanup = [](void* ctx) { CleanupGeneralContext(ctx); };
        batch->executeTile = &GeneralExecuteTile;
        batch->tileCount = static_cast<uint32_t>(tileCount);
        batch->nextTile.store(0, std::memory_order_relaxed);
        batch->tilesRemaining.store(batch->tileCount, std::memory_order_relaxed);
        if (guided)
        {
            BuildGuidedTiles(storage->tileBuffer, length,
                static_cast<int>(targetWorkers), g_guidedK, g_guidedFloor);
        }
        else
        {
            for (uint32_t i = 0; i < batch->tileCount; ++i)
            {
                const uint32_t first = i * static_cast<uint32_t>(cs);
                storage->tileBuffer[i] = {
                    first,
                    std::min(static_cast<uint32_t>(cs),
                        static_cast<uint32_t>(length) - first),
                    TileKind::GeneralRange };
            }
        }
        batch->tiles = storage->tileBuffer;
        batch->workerCount = targetWorkers;
        batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

        PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

        auto* ds = dependency.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitBatch(batch); }
        else { AcquireState(state); RetainDependency(state, ds); AddContinuationOrRunNow(ds, [state, batch]() { SubmitBatch(batch); ReleaseState(state); }); }
        return JobHandle(state);
    }

    // ---------- ScheduleChunkBatchCore ----------
    static JobHandle ScheduleChunkBatchCore(
        void (*func)(void*, const ChunkJobData*), void (*rangeFunc)(void*, const ChunkJobData*, int, int),
        void (*entityRangeFunc)(void*, const EntityBatchData*, int, int),
        void* context, void (*cleanup)(void*),
        const ChunkJobData* chunks, const EntityBatchData* batches,
        int itemCount, const JobHandle& dependency,
        ChunkScheduleMode, int workerCap, int rangeSize, EcsJobKind jobKind)
    {
        if (g_shuttingDown.load(std::memory_order_acquire)) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        ConsumeLongBatchBarriers();
        // Clear the fine-range flag (no longer used for scheduling decisions,
        // but must consume the stored value to keep the barrier mechanism clean).
        g_useFineRangesForNextEcsBatch.exchange(false, std::memory_order_acq_rel);
        if ((!func && !rangeFunc && !entityRangeFunc) || itemCount <= 0) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        // 依赖未完成时不得 inline —— 小任务也走异步提交（由依赖完成触发）。
        const bool depOk = !dependency.State() || dependency.IsCompleted();

        // Choose the execution range from workload size and worker cohort.
        // Physical 16 KiB chunks remain storage units only.
        const int provisionalWorkers = ResolveWorkerTarget(workerCap, itemCount);
        int rs = rangeSize > 0
            ? rangeSize
            : ResolveEcsBatchRangeSize(itemCount, provisionalWorkers);
        // Native IJobChunk and IJobEntity may both use EntityBatchData. The
        // explicit kind is intentionally retained here for independent policy.
        // useFineRanges deliberately disabled: it doubled tile count without benefit.
        int rc = (itemCount + rs - 1) / rs;

        // Inline for trivial work（依赖已完成/无依赖时；依赖未完成走异步提交）
        if (depOk && rc <= 1 && workerCap <= 1)
        {
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            auto* st = CreateState(true);
            if (func) RunSyncJob(st, [&]() { for (int i = 0; i < itemCount; i++) func(context, &chunks[i]); });
            else if (rangeFunc) RunSyncJob(st, [&]() { rangeFunc(context, chunks, 0, itemCount); });
            else if (entityRangeFunc) RunSyncJob(st, [&]() { entityRangeFunc(context, batches, 0, itemCount); });
            if (cleanup) cleanup(context);
            return JobHandle(st);
        }

        auto* cc = new ChunkBatchContext{ func, rangeFunc, entityRangeFunc, context, cleanup,
            chunks, batches };
        // guided 只作用于 rangeSize=0 的默认路径（用户显式 rangeSize 走 uniform）。
        const bool guided = g_guidedEnabled != 0 && rangeSize <= 0;
        const uint32_t tileCount = guided
            ? static_cast<uint32_t>(GuidedTileCount(itemCount,
                provisionalWorkers, g_guidedK, g_guidedFloor))
            : static_cast<uint32_t>(rc);
        const int targetWorkers = ResolveWorkerTarget(
            workerCap, static_cast<int>(tileCount));
        auto* storage = AcquireBatchStorage(tileCount);
        auto* batch = &storage->batch;
        auto* state = CreateState(false); batch->handle = state;
        batch->context = cc; batch->cleanup = &CleanupChunkContext;
        batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

        // Every Chunk/Entity entry point uses the same Tile/partition protocol.
        {
            const TileKind tileKind = func
                ? TileKind::ChunkCallbacks
                : (rangeFunc ? TileKind::ChunkRange : TileKind::EntityBatchRange);
            auto* tiles = storage->tileBuffer;
            if (guided)
            {
                BuildGuidedTiles(tiles, itemCount, targetWorkers,
                    g_guidedK, g_guidedFloor, tileKind);
            }
            else
            {
                for (uint32_t i = 0; i < tileCount; i++)
                {
                    const uint32_t first = i * static_cast<uint32_t>(rs);
                    tiles[i].firstItem = first;
                    tiles[i].itemCount = std::min(static_cast<uint32_t>(rs),
                        static_cast<uint32_t>(itemCount) - first);
                    tiles[i].kind = tileKind;
                }
            }

            batch->executeTile = &ChunkExecuteTile;
            batch->tiles = tiles;
            batch->tileCount = tileCount;
            batch->nextTile.store(0, std::memory_order_relaxed);
            batch->tilesRemaining.store(tileCount, std::memory_order_relaxed);
            batch->workerCount = static_cast<uint32_t>(targetWorkers);
        }

        PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

        auto* ds = dependency.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitBatch(batch, workerCap); }
        else { AcquireState(state); RetainDependency(state, ds); AddContinuationOrRunNow(ds, [state, batch, workerCap]() { SubmitBatch(batch, workerCap); ReleaseState(state); }); }
        return JobHandle(state);
    }

    JobHandle Scheduler::ScheduleChunks(void (*f)(void*, const ChunkJobData*), void* ctx, void (*cl)(void*),
        const ChunkJobData* chunks, int cc, const JobHandle& dep, ChunkScheduleMode mode, int wc, int rs)
    { return ScheduleChunkBatchCore(f, nullptr, nullptr, ctx, cl, chunks, nullptr, cc, dep, mode, wc, rs, EcsJobKind::Chunk); }

    JobHandle Scheduler::ScheduleChunkRanges(void (*f)(void*, const ChunkJobData*, int, int), void* ctx, void (*cl)(void*),
        const ChunkJobData* chunks, int cc, const JobHandle& dep, ChunkScheduleMode mode, int wc, int rs)
    { return ScheduleChunkBatchCore(nullptr, f, nullptr, ctx, cl, chunks, nullptr, cc, dep, mode, wc, rs, EcsJobKind::Chunk); }

    JobHandle Scheduler::ScheduleEntityBatches(void (*f)(void*, const EntityBatchData*, int, int), void* ctx, void (*cl)(void*),
        const EntityBatchData* batches, int bc, const JobHandle& dep, ChunkScheduleMode mode, int wc, int rs, EcsJobKind jobKind)
    { return ScheduleChunkBatchCore(nullptr, nullptr, f, ctx, cl, nullptr, batches, bc, dep, mode, wc, rs, jobKind); }

} // namespace JobSystem
