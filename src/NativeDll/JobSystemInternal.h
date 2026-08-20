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
#include <chrono>
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
    inline constexpr int kSyncWithCompletedDepThreshold = 4096;

    // per-thread state 缓存上限。命中零锁；满额批量迁移共享池（每 ~64 次回收 1 次锁）。
    // state 单 owner（refCount==0 才回池），跨线程迁移只发生在共享池锁内，无 ABA。
    inline constexpr size_t kStateCacheCap = 64;

    // AssistDependencyChain 连续零工作的墙钟预算。零工作不代表链卡死
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

    // per-thread state 缓存类型。定义在本头使 t_stateCache 可跨 TU extern
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

    // ---- 调试面板 per-worker 实时状态 ----
    inline constexpr int kMaxTrackedWorkers = 64;
    extern std::atomic<uint64_t> g_workerCurrentBatchId[kMaxTrackedWorkers];
    extern std::atomic<uint32_t> g_workerCurrentTile[kMaxTrackedWorkers];
    extern std::atomic<uint32_t> g_workerBatchTileCount[kMaxTrackedWorkers];
    extern std::atomic<bool>     g_workerIsActive[kMaxTrackedWorkers];

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

    // ---- 原生发布活动事件（供 GUI Activity 完整记录微秒级 batch；动态保留全部）----
    struct NativeActivityEvent { uint64_t batchId; uint32_t tiles; double timeMs; };
    extern std::atomic<bool> g_nativeActivityCaptureEnabled;
    extern std::atomic<bool> g_debugPaused; // GUI 暂停时停止记录新段，避免环形缓冲覆盖历史
    void RecordPublishedJob(uint64_t batchId, uint32_t tiles) noexcept;
    int ConsumePublishedJobs(NativeActivityEvent* out, int maxCount, uint64_t* readIndex) noexcept;
    void ClearPublishedJobs() noexcept;
    // 直接调用（不经 JobSystem 调度器，如 ISPC-MT 方法直跑）也记录进 activity，并维护 id→名字表
    void RecordDirectCall(const char* jobName, uint32_t tiles) noexcept;
    // 直调执行窗口：Begin 分配 id + 记发布 + 开执行窗口（当前线程泳道），End 关闭窗口
    //（追加共享时间线段）。由 transpiler 包装器在 native 调用前后成对调用。
    uint64_t BeginDirectCall(const char* jobName, uint32_t tiles) noexcept;
    void EndDirectCall(uint64_t id) noexcept;
    int ResolveNativeJobName(uint64_t batchId, char* buf, int bufLen) noexcept;

    // ---- State 模块（JobSystem_State.cpp）定义的全局 ----
    extern std::mutex g_longBatchBarrierMutex;
    extern std::vector<HandleState*> g_longBatchBarriers;
    extern thread_local HandleState* g_completingBatchState;

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

    // ---- 调试面板：执行窗口上报（事件驱动，非每帧采样）----
    // 每次 job 执行（无论 schedule 路径）在入口记录"开始"事件（压栈 + 时间戳），
    // 在出口记录"结束"事件（把完整窗口 [startMs, endMs] 直接追加进共享时间线历史）。
    // GUI 每帧只渲染共享历史，不做任何状态迁移检测——Job 的 start/end 发生时即被
    // 检测并记录，微秒级 Job（两帧之间跑完）也不会丢失，且无需环形握手缓冲。
    // 有 worker 索引的线程（pool worker，WorkerLoop 预分配）上报到其泳道；无索引的
    // 调用线程（典型为主线程 inline）上报到预留的 M 泳道（index == CurrentWorkerCount()）。
    inline int DebugReportLaneId() noexcept
    {
        const int wi = WorkerIndexManager::GetCurrentIndex();
        if (wi >= 0) return wi;
        const int mc = CurrentWorkerCount();
        return (mc >= 0 && mc < kMaxTrackedWorkers) ? mc : -1;
    }
    inline double DebugNowMs() noexcept
    {
        using namespace std::chrono;
        return duration_cast<duration<double, std::milli>>(
            steady_clock::now().time_since_epoch()).count();
    }

    // ---- 共享时间线历史（base 模块定义；job 执行线程在结束瞬间追加，GUI 只读渲染）----
    constexpr int kDebugSegmentMax = 16384;
    // isDirect：是否为直调方法（transpiler 直跑，非调度式 Job）；GUI 用于把直调标记为 [D]。
    // workers：参与本次执行 / 认领工作的 worker 数（batch 为计划参与数；同步/快速路径为 1；直调为并行度）。
    struct DebugSegment { int lane; uint64_t batchId; double startMs; double endMs; uint32_t tiles; uint32_t workers; bool isDirect; };
    extern DebugSegment g_debugSegments[kDebugSegmentMax];
    extern std::atomic<unsigned int> g_debugSegHead;    // 槽位分配（写者 fetch_add relaxed）
    extern std::atomic<unsigned int> g_debugSegVisible; // 已发布的段数（写者 fetch_add release 发布；读者 acquire）
    // 写者协议：先写槽内容，再 fetch_add(release) 发布计数——保证读者 acquire 读到计数时
    // 槽内容必已可见（无"计数先行、内容滞后"竞态）。读者用 visible 推算槽下标，回绕安全。

    // 每个泳道的嵌套执行栈（job 体内再 inline 调度 job）：保存开始时间戳与 id，
    // 结束时弹栈配对。非原子——仅单写者线程访问（M 泳道罕见多写者时仅可能短暂错配）。
    struct ExecWindowRing
    {
        int depth = 0;
        struct ExecFrame { uint64_t id; double startMs; uint32_t tiles; uint32_t workers; bool isDirect; } stack[8];
    };
    extern ExecWindowRing g_execWindows[kMaxTrackedWorkers];

    inline void DebugBeginExec(uint64_t id, uint32_t tiles, uint32_t workers, bool isDirect) noexcept
    {
        // 零开销守门：仅调试面板开启（g_nativeActivityCaptureEnabled）才采集。
        // 注意：优先级需高于 id==0 判断，确保面板关闭时完全不触碰任何原子。
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
        // 暂停时停止记录新段：不压栈、不写共享历史，环形缓冲不被覆盖、历史得以保留。
        if (g_debugPaused.load(std::memory_order_relaxed)) return;
        const int lane = DebugReportLaneId();
        if (id == 0 || lane < 0) return;
        auto& st = g_execWindows[lane];
        if (st.depth < 8)
            st.stack[st.depth++] = ExecWindowRing::ExecFrame{ id, DebugNowMs(), tiles, workers, isDirect };
        else if (st.depth == 8) // 栈满：覆盖最内层，至少保持"活跃"语义
            st.stack[7] = ExecWindowRing::ExecFrame{ id, DebugNowMs(), tiles, workers, isDirect };
        g_workerCurrentBatchId[lane].store(id, std::memory_order_relaxed);
        g_workerCurrentTile[lane].store(0, std::memory_order_relaxed);
        g_workerBatchTileCount[lane].store(tiles, std::memory_order_relaxed);
        g_workerIsActive[lane].store(true, std::memory_order_release);
    }
    inline void DebugEndExec() noexcept
    {
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
        const int lane = DebugReportLaneId();
        if (lane < 0) return;
        auto& st = g_execWindows[lane];
        if (st.depth > 0)
        {
            const ExecWindowRing::ExecFrame f = st.stack[--st.depth];
            if (g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed) &&
                !g_debugPaused.load(std::memory_order_relaxed))
            {
                // 结束事件：完整窗口直接追加进共享时间线历史（GUI 只读渲染）。
                // 先写槽内容，再 fetch_add(release) 发布计数——读者永不读到未写完的槽。
                const unsigned int h = g_debugSegHead.fetch_add(1, std::memory_order_relaxed);
                g_debugSegments[h % kDebugSegmentMax] = DebugSegment{ lane, f.id, f.startMs, DebugNowMs(), f.tiles, f.workers, f.isDirect };
                g_debugSegVisible.fetch_add(1, std::memory_order_release);
            }
        }
        g_workerIsActive[lane].store(false, std::memory_order_release);
        g_workerCurrentBatchId[lane].store(0, std::memory_order_release);
        g_workerCurrentTile[lane].store(0, std::memory_order_release);
        g_workerBatchTileCount[lane].store(0, std::memory_order_release);
    }
    // 更新当前执行窗口（栈顶）已认领执行的 tile 数（worker 在 batch 执行中累计后调用）。
    // 让 segment.tiles = 该 worker 实际领取的 tile 数，而非整批 tileCount。
    inline void DebugUpdateExecTiles(uint32_t tiles) noexcept
    {
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
        const int lane = DebugReportLaneId();
        if (lane < 0) return;
        auto& st = g_execWindows[lane];
        if (st.depth > 0)
            st.stack[st.depth - 1].tiles = tiles;
    }

    template <typename Fn>
    void RunSyncJob(HandleState* state, Fn&& fn) noexcept
    {
        // 幂等：调用方可能已为 state 预分配诊断 id（inline 路径先报到 published），
        // 复用同一 id 保证 事件 id == 执行窗口 id == handle 的 diagnosticBatchId。
        uint64_t id = state->diagnosticBatchId.load(std::memory_order_acquire);
        if (id == 0) id = AssignStateDiagnosticId(state);
        DebugBeginExec(id, 1, 1, false); // 同步 inline Job：单线程执行
        SetCurrentBatchId(id);
        fn();
        SetCurrentBatchId(0);
        DebugEndExec();
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

    // ---- 实体数衡 tile（定义在 JobSystem_Tiles.cpp） ----
    int UnitEntityCount(const ChunkBatchContext* cc, TileKind kind, int unit) noexcept;
    int ResolveEcsEntityTileTarget(int totalEntities, int workerCount) noexcept;
    int BuildEntityBalancedTiles(ExecutionTile* tiles, const ChunkBatchContext* cc,
        TileKind kind, int itemCount, int targetEntities) noexcept;

    // ---- ISPC MT 任务挂钩（tasksys.cpp 调用，事件驱动显示每个参与 worker 的耗时）----
    // 每个 ISPC 任务在自己的 ConcRT 线程上执行，分配到保留的高位泳道（在 W/M 之后）。
    // DLL 分离：tasksys.cpp（含 ISPCAlloc/Launch/Sync 任务系统）移入 NativeTranspiled.dll，
    // 故 DebugIspcTaskBegin/End 必须从 NativeDll 导出——NativeDll 编译时 JOB_SYSTEM_EXPORT
    // 已定义→dllexport；NativeTranspiled 编译时未定义→dllimport。
#ifdef _WIN32
#ifdef JOB_SYSTEM_EXPORT
#define ENTJOY_ISPC_DEBUG_API __declspec(dllexport)
#else
#define ENTJOY_ISPC_DEBUG_API __declspec(dllimport)
#endif
#else
#define ENTJOY_ISPC_DEBUG_API
#endif
    ENTJOY_ISPC_DEBUG_API uint64_t DebugIspcTaskBegin(const char* name) noexcept;
    ENTJOY_ISPC_DEBUG_API void DebugIspcTaskEnd(uint64_t id) noexcept;
    // 当前已分配的最高 ISPC 泳道数（GUI 据此扩展泳道条数）
    int DebugIspcLaneCount() noexcept;
    constexpr int kIspcLaneBase = kMaxTrackedWorkers - 16; // 预留 16 条高位泳道给 ISPC

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
