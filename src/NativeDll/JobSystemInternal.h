#pragma once

// EntJoy JobSystem 内部共享头。
// JobSystem.cpp 已按 State / Tiles / Scheduler 三模块拆分，本头承载跨模块的
// 类型定义、extern 全局与函数原型，使各 TU 可独立编译。
//
// 拆分后文件布局：
//   JobSystem.cpp            —— base：全局定义 + 统计快照 + 时钟/CPU 诊断助手
//   JobSystem_State.cpp      —— State：HandleState 生命周期 + 依赖链 + JobHandle
//   JobSystem_Tiles.cpp      —— Tiles：ExecutionTile/BatchState/BatchStorage + 执行循环
//   JobSystem_Scheduler.cpp  —— Scheduler：适配器 + Schedule 系列 + IJob* 调度入口

#include "JobSystem.h"
#include "ChunkJobData.h"
#include "EntityBatchData.h"
#include "JobProfiler.h"
#include "NativeWorkerPool.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <functional>
#include <limits>
#include <memory>
#include <mutex>
#include <vector>

namespace JobSystem
{
    // ---- 跨模块常量（inline 保证 ODR，各 TU 一份） ----
    inline constexpr size_t kMaxPooledStates = 4096;
    inline constexpr size_t kMaxPooledBatchStorage = 256;
    inline constexpr int kSyncExecutionLengthThreshold = 512;
    inline constexpr int kSyncWithCompletedDepThreshold = 4096;

    // B2: per-thread state 缓存上限。命中零锁；满额批量迁移共享池（每 ~64 次回收 1 次锁）。
    // state 单 owner（refCount==0 才回池），跨线程迁移只发生在共享池锁内，无 ABA。
    inline constexpr size_t kStateCacheCap = 64;

    // B1: AssistDependencyChain 连续零工作的墙钟预算。零工作不代表链卡死
    // （workers 可能正在执行祖先、即将提交下一环），所以用有界回访覆盖祖先
    // completion→下一环 submit 的交接窗口；仅在持续零工作超过该预算后交还
    // 调用方的 spin/futex。以墙钟而非 pass 数计：pass 上限与循环内 yield 的
    // 时长耦合，量级太小会过早 park（V-A 退化 / V-D 残余空等）。
    // 预算内每 pass 带 yield 降频，覆盖亚毫秒~毫秒级的交接窗口绰绰有余；
    // 链持续推进时预算随 worked 重置，不会误伤正常链。
    inline constexpr uint64_t kAssistStallBudgetNs = 10'000'000; // 10ms

    inline constexpr uint64_t kLongBatchBarrierNs = 800'000;

    // 并行 for 默认 tiles/worker（batchSize=0 时 ResolveChunkSize 使用）。
    // GridSearch A/B 定标：可变代价 job 最优 ~26 tiles/worker；默认 16 为
    // 可变代价(job 受益) 与均匀代价(job 少付 claim 开销) 的折中。env 可覆盖。
    inline constexpr int kDefaultTilesPerWorker = 16;

    // B2: per-thread state 缓存类型。定义在本头使 t_stateCache 可跨 TU extern
    // （State 模块 RecycleState/CreateState 直接读写）。析构把缓存 state 批量
    // 交还共享池；全局互斥体在 Shutdown 中始终存活（本对象先于 g_statePoolMutex
    // 初始化，按标准后销毁），线程退出取锁安全。
    extern std::mutex g_statePoolMutex;
    extern std::vector<HandleState*> g_statePool;
    struct ThreadStateCache
    {
        std::vector<HandleState*> entries;
        ~ThreadStateCache()
        {
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

    // ---- base 模块（JobSystem.cpp）定义的全局 ----
    extern std::atomic<bool> g_workerAffinityEnabled;
    extern std::mutex g_schedulerMutex;
    extern std::unique_ptr<NativeWorkerPool> g_nativeWorkerPool;
    extern int g_numThreads;
    extern int g_configuredTilesPerWorker;
    extern int g_guidedEnabled;
    extern int g_guidedK;
    extern int g_guidedFloor;
    extern thread_local ThreadStateCache t_stateCache;

    // 统计计数器（base 定义；Tiles 递增 / base GetStatsSnapshot 读取）。
    extern std::atomic<uint64_t> g_completeWaitLoops;
    extern std::atomic<uint64_t> g_assistAttempts;
    extern std::atomic<uint64_t> g_assistExecuted;
    extern std::atomic<uint64_t> g_frameTasksSubmitted;
    extern std::atomic<uint64_t> g_workerExecutedRanges;
    extern std::atomic<uint64_t> g_mainExecutedRanges;
    extern std::atomic<uint64_t> g_stealCount;
    extern std::atomic<uint64_t> g_parkWakeCount;
    extern std::atomic<uint64_t> g_publishedJobs;
    extern std::atomic<uint64_t> g_waitFallbacks;
    extern std::atomic<uint64_t> g_notifiedWorkers;
    extern std::atomic<uint64_t> g_workerClaimedTokens;
    extern std::atomic<uint64_t> g_mainClaimedTokens;
    extern std::atomic<uint64_t> g_activeWorkersPeak;
    extern std::atomic<uint64_t> g_activeWorkers;
    extern std::atomic<uint64_t> g_workerTargetTotal;
    extern std::atomic<uint64_t> g_totalTilesPublished;
    extern std::atomic<uint64_t> g_localTiles;
    extern std::atomic<uint64_t> g_stolenTiles;
    extern std::atomic<uint64_t> g_assistTiles;
    extern std::atomic<uint64_t> g_stealAttempts;
    extern std::atomic<uint64_t> g_stealSuccesses;
    extern std::atomic<uint64_t> g_victimScans;
    extern std::atomic<uint64_t> g_stealEmptyExits;
    extern std::atomic<uint64_t> g_batchStorageCreated;
    extern std::atomic<uint64_t> g_batchStorageReused;
    extern std::atomic<uint64_t> g_batchStorageReturned;
    extern std::atomic<uint64_t> g_batchStorageDropped;
    extern std::atomic<uint64_t> g_submitToFirstWorkerEwmaNs;
    extern std::atomic<uint64_t> g_workerStartSpreadEwmaNs;
    extern std::atomic<uint64_t> g_lastTileToTopologyDoneEwmaNs;
    extern std::atomic<uint64_t> g_completeWakeToReturnEwmaNs;
    extern std::atomic<uint64_t> g_nativeBatches;
    extern std::atomic<uint64_t> g_invalidBackendSelections;
    extern std::atomic<int64_t> g_wakeLatencyEwmaNs;
    extern std::atomic<uint64_t> g_publishToCompletionEwmaNs;
    extern std::atomic<uint64_t> g_perRangeExecEwmaNs;
    extern std::atomic<uint64_t> g_nextDiagnosticBatchId;
    extern std::atomic<bool> g_shuttingDown;
    extern std::atomic<bool> g_timingDiagnosticsEnabled;
    extern std::atomic<void (*)(uint64_t)> g_currentBatchIdCallback;
    extern std::atomic<uint32_t> g_backendBatchesOutstanding;

    // ---- State 模块（JobSystem_State.cpp）定义的全局 ----
    extern std::mutex g_longBatchBarrierMutex;
    extern std::vector<HandleState*> g_longBatchBarriers;
    extern thread_local HandleState* g_completingBatchState;
    extern std::atomic<bool> g_useFineRangesForNextEcsBatch;

    // ---- 跨模块类型 ----

    // Tile 是负载均衡单位 —— 一个或多个 chunk（IJobChunk）或 entity 子区间（IJobEntity）。
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

    struct ChunkBatchContext {
        void (*func)(void*, const ChunkJobData*);
        void (*rangeFunc)(void*, const ChunkJobData*, int, int);
        void (*entityRangeFunc)(void*, const EntityBatchData*, int, int);
        void* originalContext;
        void (*originalCleanup)(void*);
        const ChunkJobData* chunks;
        const EntityBatchData* entityBatches;
    };

    struct GeneralBatchContext {
        void (*indexFunc)(void*, int);
        void (*batchFunc)(void*, int, int);
        void* originalContext;
        void (*originalCleanup)(void*);
    };

    // ---- base 模块助手（定义在 JobSystem.cpp） ----
    inline void SetCurrentBatchId(uint64_t id) noexcept
    {
        auto cb = g_currentBatchIdCallback.load(std::memory_order_acquire);
        if (cb) cb(id);
    }
    uint64_t AssignStateDiagnosticId(HandleState* state) noexcept;
    template <typename Fn>
    void RunSyncJob(HandleState* state, Fn&& fn) noexcept
    {
        const uint64_t id = AssignStateDiagnosticId(state);
        SetCurrentBatchId(id);
        fn();
        SetCurrentBatchId(0);
    }
    void RecordBatchTiming(const BatchTimingSample& sample) noexcept;
    uint64_t MonotonicNowNs() noexcept;
    int CurrentProcessorIndexForDiagnostics() noexcept;
    uint64_t CurrentThreadCpuTimeNsForDiagnostics() noexcept;
    uint64_t CurrentThreadCyclesForDiagnostics() noexcept;
    int PhysicalCoreIndexForDiagnostics(int logicalCore) noexcept;
    void FlushStateCacheToSharedPool();
    void ConsumeLongBatchBarriers() noexcept;

    // ---- State 模块（定义在 JobSystem_State.cpp） ----
    void RetainDependency(HandleState* state, HandleState* dep) noexcept;
    void RegisterLongBatchBarrier(HandleState* state) noexcept;
    void SubmitBackendAsync(std::function<void()> work);
    int ResolveChunkSize(int length, int requestedChunk);

    // ---- Tiles 模块（定义在 JobSystem_Tiles.cpp） ----
    int ResolveWorkerTarget(int workerCap, int targetCount) noexcept;
    int ResolveEcsBatchRangeSize(int itemCount, int workerCount) noexcept;
    int GuidedTileCount(int length, int workerCount, int k, int floor) noexcept;
    int BuildGuidedTiles(ExecutionTile* tiles, int length, int workerCount,
        int k, int floor, TileKind kind = TileKind::GeneralRange) noexcept;
    BatchStorage* AcquireBatchStorage(uint32_t tileCapacity);
    void ClearBatchStoragePool() noexcept;
    void FlushBatchStorageCacheToSharedPool();
    void SubmitBatch(BatchState* batch, int workerCap = 0);
    bool ChunkExecuteTile(void* ctx, const ExecutionTile& tile) noexcept;
    void CleanupChunkContext(void* ctx) noexcept;
    bool GeneralExecuteTile(void* ctx, const ExecutionTile& tile) noexcept;
    void CleanupGeneralContext(void* ctx) noexcept;
} // namespace JobSystem
