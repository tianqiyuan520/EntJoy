#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"

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
#include <unordered_map>
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
    // ---------- 调试面板 per-worker 实时状态 ----------
    std::atomic<uint64_t> g_workerCurrentBatchId[kMaxTrackedWorkers]{};
    std::atomic<uint32_t> g_workerCurrentTile[kMaxTrackedWorkers]{};
    std::atomic<uint32_t> g_workerBatchTileCount[kMaxTrackedWorkers]{};
    std::atomic<bool>     g_workerIsActive[kMaxTrackedWorkers]{};
    std::atomic<bool>     g_debugPaused{ false }; // GUI 暂停标志：暂停时停止记录新段
    ExecWindowRing g_execWindows[kMaxTrackedWorkers]{};
    // 共享时间线历史：job 执行线程在结束瞬间追加（DebugEndExec），GUI 线程只读渲染
    DebugSegment g_debugSegments[kDebugSegmentMax]{};
    std::atomic<unsigned int> g_debugSegHead{ 0 };
    std::atomic<unsigned int> g_debugSegVisible{ 0 };
    std::atomic<uint64_t> g_debugSegSeq[kDebugSegmentMax]{};

    std::atomic<bool> g_workerAffinityEnabled{ false };

    // ---------- Globals ----------
    std::mutex g_schedulerMutex;
    std::unique_ptr<ChaseLevScheduler> g_chaseLevScheduler;
    std::atomic<int> g_numThreads{ 0 };

    // 并行 for 默认 tiles/worker（batchSize=0 时 ResolveChunkSize 使用）。
    // 默认 16 为可变代价与均匀代价 job 的折中（GridSearch A/B 定标），env 可覆盖。
    std::atomic<int> g_configuredTilesPerWorker{ kDefaultTilesPerWorker };

    // JobCostCache export flag（State 模块 ResolveChunkSize 与 Tiles 退役路径读取）。
    // 默认开启：per-job 自动 batch 收益显著且压测零回归；C# Initialize 强制同步此值
    //（防 DLL 重载不一致）。关闭 = 纯 tpw=4（冷启动/保守场景）。
    std::atomic<bool> g_jobCostCacheEnabled{ true };

    // 提交期延迟唤醒深度（ChaseLevScheduler::SubmitBatch 尾部读取；defer>0 跳过逐批 notify）
    std::atomic<int> g_submitDeferDepth{ 0 };

    // 隐式批（native 收集）开关 + pending 列表（extern 声明见 JobSystemInternal.h）。
    // 默认关闭：Schedule* 直接提交；开启后 tile 路径 job 挂入 pending，由 FlushPendingSubmits 统一提交 + 单次唤醒。
    std::atomic<bool> g_implicitBatchEnabled{ false };
    std::mutex g_pendingBatchesMutex;
    std::vector<BatchState*> g_pendingBatches;

    // 诊断开关（进程启动时读 env 一次，之后只读）：ENTJOY_JCC_VERBOSE=1
    // 打印 per-job 自动 batch 的决策与学习快照。
    bool g_jobCostCacheVerbose = []() -> bool {
        const char* v = std::getenv("ENTJOY_JCC_VERBOSE");
        return v != nullptr && v[0] == '1';
    }();

    // Guided（chunk ∝ 剩余工作量）tile 调度（OpenMP schedule(guided) 同族）。0=off；>0=on。
    // on 时 chunk = max(floor, ceil(remaining/(W*k)))，头部大块、尾部小块（钳 straggler 上界）。由 JobSystem_ConfigureGuided 设置。
    std::atomic<int> g_guidedEnabled{ 0 };
    std::atomic<int> g_guidedK{ 2 };
    std::atomic<int> g_guidedFloor{ 16 };

    std::mutex g_statePoolMutex;
    std::vector<HandleState*> g_statePool;

    thread_local ThreadStateCache t_stateCache;

    void FlushStateCacheToSharedPool()
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
    std::thread::id g_mainThreadId{};
    std::atomic<bool> g_timingDiagnosticsEnabled{ false };
    // 主线程 assist 开关（Controller API 可运行时切换）。默认关闭（纯 worker 模式）：
    // 实测与开启相当，且释放主线程参与竞争；慢 worker 被 OS 抢占导致尾延迟时，可运行时开启兜底。
    std::atomic<bool> g_mainThreadAssistEnabled{ false };

    // 线程局部"当前 batch"回调。C# 初始化时注册一次；每次 job 执行窗口入口
    // 调 cb(batchId)、出口 cb(0)，托管异常按此绑定到具体 batch。
    std::atomic<void (*)(uint64_t)> g_currentBatchIdCallback{ nullptr };
    void RegisterCurrentBatchIdCallback(void (*cb)(uint64_t)) noexcept
    {
        g_currentBatchIdCallback.store(cb, std::memory_order_release);
    }

    // GUI Activity 用的原生发布事件（动态保留全部历史，不覆盖；调试面板开启时记录）
    std::atomic<bool> g_nativeActivityCaptureEnabled{ false };
    static std::mutex g_nativeActivityMutex;
    static std::vector<NativeActivityEvent> g_nativeActivity;   // 已发布事件（保留）
    static size_t g_nativeActivityTotal = 0;                     // 累计写入数（含被截断者）

    static double NativeNowMs() noexcept
    {
        using namespace std::chrono;
        return duration_cast<duration<double, std::milli>>(
            steady_clock::now().time_since_epoch()).count();
    }

    void RecordPublishedJob(uint64_t batchId, uint32_t tiles) noexcept
    {
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
        std::lock_guard<std::mutex> lock(g_nativeActivityMutex);
        g_nativeActivity.emplace_back(NativeActivityEvent{ batchId, tiles, NativeNowMs() });
        ++g_nativeActivityTotal;
    }

    // GUI 从 readIndex 起读取新增事件（不删除，历史完整保留）。返回读取条数。
    int ConsumePublishedJobs(NativeActivityEvent* out, int maxCount, uint64_t* readIndex) noexcept
    {
        if (out == nullptr || maxCount <= 0) return 0;
        uint64_t startIdx = readIndex ? *readIndex : 0;
        std::lock_guard<std::mutex> lock(g_nativeActivityMutex);
        if (startIdx >= g_nativeActivity.size())
        {
            if (readIndex) *readIndex = static_cast<uint64_t>(g_nativeActivity.size());
            return 0;
        }
        const size_t n = std::min<size_t>(maxCount, g_nativeActivity.size() - static_cast<size_t>(startIdx));
        size_t base = static_cast<size_t>(startIdx);
        for (size_t i = 0; i < n; ++i) out[i] = g_nativeActivity[base + i];
        if (readIndex) *readIndex = startIdx + n;
        return static_cast<int>(n);
    }

    void ClearPublishedJobs() noexcept
    {
        std::lock_guard<std::mutex> lock(g_nativeActivityMutex);
        g_nativeActivity.clear();
        g_nativeActivityTotal = 0;
    }

    // 直接调用（ISPC-MT 等方法直跑，不经调度器）：也记入 published 计数与 activity，并维护 id→名字
    static std::mutex g_nativeJobNameMutex;
    static std::unordered_map<uint64_t, std::string> g_nativeJobNameMap;

    void RecordDirectCall(const char* jobName, uint32_t tiles) noexcept
    {
        // 直调也是一次"发布"：统一计数口径，使 GUI 的 Published Jobs 与 Activity 事件一一对应。
        g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
        const uint64_t id = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;
        if (jobName)
        {
            std::lock_guard<std::mutex> lock(g_nativeJobNameMutex);
            g_nativeJobNameMap[id] = jobName;
        }
        RecordPublishedJob(id, tiles);
    }

    // ISPC MT 任务挂钩（tasksys.cpp 调用）：每个任务在自己的 ConcRT 线程上执行，分配到保留的高位泳道。
    // 注意：g_ispcLaneNext 须为文件级共享宿主（函数级 static 会各自独立，导致分配计数读不到）。
    static std::atomic<int> g_ispcLaneNext{ kIspcLaneBase };

    uint64_t DebugIspcTaskBegin(const char* name) noexcept
    {
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return 0;
        // 每个 ISPC 工作线程分配一条高位泳道（跨线程稳定）
        thread_local int t_ispcLane = -1;
        if (t_ispcLane < 0)
        {
            const int lane = g_ispcLaneNext.fetch_add(1, std::memory_order_relaxed);
            if (lane >= kMaxTrackedWorkers)
                return 0; // 泳道耗尽，放弃记录
            t_ispcLane = lane;
            WorkerIndexManager::SetCurrentIndex(t_ispcLane); // DebugBeginExec 用同一条泳道
        }
        const uint64_t id = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;
        if (name)
        {
            std::lock_guard<std::mutex> lock(g_nativeJobNameMutex);
            g_nativeJobNameMap[id] = std::string("[ISPC]") + name;
        }
        DebugBeginExec(id, 1, 1, true); // isDirect=true，复用直调样式
        return id;
    }

    void DebugIspcTaskEnd(uint64_t id) noexcept
    {
        if (id == 0) return;
        DebugEndExec();
    }

    int DebugIspcLaneCount() noexcept
    {
        const int used = g_ispcLaneNext.load(std::memory_order_relaxed) - kIspcLaneBase;
        return used < 0 ? 0 : (used > 16 ? 16 : used);
    }

    uint64_t BeginDirectCall(const char* jobName, uint32_t tiles) noexcept
    {
        // 直调执行窗口开始：发布计数 + 记 Activity + 开当前线程泳道窗口（事件驱动）。
        // isDirect=true：GUI 将直调标记为 [D]，与调度式 Job 区分（直调不经调度器）。
        g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return 0;
        const uint64_t id = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;
        if (jobName)
        {
            std::lock_guard<std::mutex> lock(g_nativeJobNameMutex);
            g_nativeJobNameMap[id] = jobName;
        }
        RecordPublishedJob(id, tiles);
        const uint32_t workers = tiles > 0 ? tiles : 1u; // 直调并行度 ≈ tile 数（MT 用 CPU 数）
        DebugBeginExec(id, tiles, workers, true);
        return id;
    }

    void EndDirectCall(uint64_t id) noexcept
    {
        if (id == 0) return; // Begin 未推窗口（采集未开）时不可弹栈
        DebugEndExec();
    }

    int ResolveNativeJobName(uint64_t batchId, char* buf, int bufLen) noexcept
    {
        if (buf == nullptr || bufLen <= 0) return 0;
        std::lock_guard<std::mutex> lock(g_nativeJobNameMutex);
        auto it = g_nativeJobNameMap.find(batchId);
        if (it == g_nativeJobNameMap.end()) return 0;
        const int n = std::min<int>(bufLen - 1, static_cast<int>(it->second.size()));
        memcpy(buf, it->second.data(), static_cast<size_t>(n));
        buf[n] = 0;
        return n;
    }
    // 给非 batch 快速路径 job 分配诊断 id（batch 路径由 SubmitBatch 从
    // batch->diagnosticId 设置），保证 Complete(h) 按 id 抛对应异常。
    uint64_t AssignStateDiagnosticId(HandleState* state) noexcept
    {
        const uint64_t id = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;
        if (state) state->diagnosticBatchId.store(id, std::memory_order_relaxed);
        return id;
    }

    constexpr size_t kBatchTimingSampleCapacity = 2048;

    std::mutex g_batchTimingMutex;
    std::array<BatchTimingSample, kBatchTimingSampleCapacity> g_batchTimingSamples{};
    size_t g_batchTimingSampleCount{ 0 };
    uint64_t g_batchTimingSamplesDropped{ 0 };
    BatchTimingSample g_slowestBatch{};

    void RecordBatchTiming(const BatchTimingSample& sample) noexcept
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

    uint64_t MonotonicNowNs() noexcept
    {
        return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count());
    }

    int CurrentProcessorIndexForDiagnostics() noexcept
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

    uint64_t CurrentThreadCpuTimeNsForDiagnostics() noexcept
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

    uint64_t CurrentThreadCyclesForDiagnostics() noexcept
    {
#if defined(_WIN32)
        ULONG64 cycles = 0;
        return ::QueryThreadCycleTime(::GetCurrentThread(), &cycles)
            ? static_cast<uint64_t>(cycles) : 0;
#else
        return 0;
#endif
    }

    int PhysicalCoreIndexForDiagnostics(int logicalCore) noexcept
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
        // parkWake/hotSpin 由 Chase-Lev（g_parkWakeCount）统计。
        stats->parkWakeCount = g_parkWakeCount.load(std::memory_order_relaxed);
        stats->hotSpinHits = 0;
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
        return std::max(1, g_numThreads.load(std::memory_order_relaxed));
    }

} // namespace JobSystem
