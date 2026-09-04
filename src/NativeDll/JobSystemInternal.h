#pragma once

// EntJoy JobSystem 内部共享头：承载跨模块的类型定义、extern 全局与函数原型，
// 使各 TU 可独立编译。
//
// 文件布局：
//   JobSystem.cpp            —— base：全局定义 + 统计快照 + 时钟/CPU 诊断助手
//   JobSystem_State.cpp      —— State：HandleState 生命周期 + 依赖链 + JobHandle
//   JobSystem_Tiles.cpp      —— Tiles：ExecutionTile/BatchState/BatchStorage + 执行循环
//   JobSystem_Scheduler.cpp  —— Scheduler：适配器 + Schedule 系列 + IJob* 调度入口

#include "JobSystem.h"
#include "ChunkJobData.h"
#include "EntityBatchData.h"
#include "JobCostCache.h"
#include "JobProfiler.h"
#include "SparseTileDeque.h"

#include <array>
#include <exception>
#include <limits>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <functional>
#include <limits>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

namespace JobSystem
{
    class ChaseLevScheduler; // forward declare

    // ---- 跨模块常量（inline 保证 ODR，各 TU 一份） ----
    inline constexpr size_t kMaxPooledStates = 4096;
    inline constexpr size_t kMaxPooledBatchStorage = 256;

    // per-thread state 缓存上限。命中零锁；满额批量迁移共享池（每 ~64 次回收 1 次锁）。
    // state 单 owner（refCount==0 才回池），跨线程迁移只发生在共享池锁内，无 ABA。
    inline constexpr size_t kStateCacheCap = 64;

    inline constexpr uint64_t kLongBatchBarrierNs = 800'000;

    // 并行 for 默认 tiles/worker（batchSize=0 时 ResolveChunkSize 使用）。
    // tpw=4 平衡 light/heavy 场景性能，与 ECS kTargetTilesPerWorker=4 一致。
    inline constexpr int kDefaultTilesPerWorker = 4;

    // ceil(a/b)，a>=0, b>0；用 (a-1)/b+1 避免 a+b-1 在 a 接近类型上限时的 signed overflow（UB）。
    template <typename T>
    inline T CeilDiv(T a, T b) noexcept
    {
        if (a <= T(0)) return T(0);
        return (a - T(1)) / b + T(1);
    }

    // per-thread state 缓存：定义在本头使 t_stateCache 可跨 TU extern（State 模块直接读写）。
    // 析构时批量交还共享池；g_statePoolMutex 在 Shutdown 中始终存活（本对象先于其初始化，按标准后销毁），线程退出取锁安全。
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
    extern std::unique_ptr<ChaseLevScheduler> g_chaseLevScheduler;
    extern std::atomic<int> g_numThreads;
    extern std::atomic<int> g_configuredTilesPerWorker;
    extern std::atomic<int> g_guidedEnabled;
    extern std::atomic<int> g_guidedK;
    extern std::atomic<int> g_guidedFloor;
    extern std::atomic<bool> g_mainThreadAssistEnabled;  // 主线程 assist 开关（默认 false，由 API 控制）
    // JobCostCache export flag：用户显式启用（C# JobSystem_SetJobCostCacheEnabled(1)）后，
    // worker 按 per-job 每元素成本 EWMA 自动求解最优 tile 数；热路径 relaxed 读取足够（延迟生效无害）。
    extern std::atomic<bool> g_jobCostCacheEnabled;
    // 提交期"延迟唤醒"深度：>0 时 SubmitBatch 跳过末尾 notify_all，由显式 Flush 在提交窗口结束时统一唤醒。
    // 安全：任务已入注入器，worker 自旋自取；全 park 时由 Flush 的 notify_all 唤醒。
    extern std::atomic<int> g_submitDeferDepth;
    // 隐式批（native 收集）：开启时主线程直接提交的 tile 路径 job 挂入 pending，
    // 由 FlushPendingSubmits（EndFrame/Complete 自动触发）统一提交 + 单次唤醒；
    // 依赖未完成路径（continuation）不受 pending 影响，照常立即提交。
    extern std::atomic<bool> g_implicitBatchEnabled;
    extern std::mutex g_pendingBatchesMutex;
    extern std::vector<BatchState*> g_pendingBatches;
    // 诊断：ENTJOY_JCC_VERBOSE=1 时打印 ResolveChunkSize 决策 + 退役学习快照。
    // 只在 flag 开启时读取；进程启动时从 env 初始化一次，之后只读 → 无竞态。
    extern bool g_jobCostCacheVerbose;
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
    // 主线程 id：Initialize 时记录，Shutdown 校验——worker 线程调用 shutdown 会
    // join 自身导致死锁，故非主线程调用直接拒绝（返回，不执行）。
    extern std::thread::id g_mainThreadId;
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

        // 勿标 noexcept：用户回调可能抛异常，须经 TryExecuteOneTile 的 try/catch 记录，
        // 否则 C++ 直接 terminate。
        bool (*executeTile)(void* ctx, const ExecutionTile& tile){ nullptr };

        // Unified lightweight BatchRange path. Physical ECS chunks remain
        // storage boundaries; tiles are contiguous descriptor/index ranges.
        ExecutionTile* tiles{ nullptr };
        uint32_t tileCount{ 0 };
        std::atomic<uint32_t> nextTile{ 0 };
        uint32_t workerCount{ 0 };
        std::atomic<uint32_t> workerSlotsEntered{ 0 };
        // 逻辑完成由 tile 完成驱动，而非任务退役：公共 JobHandle 已完成后，
        // 慢 worker 槽可能仍在退出窃取循环。
        std::atomic<uint32_t> tilesRemaining{ 0 };
        std::atomic<bool> logicalCompleted{ false };
        // ---- Chase-Lev：在飞任务计数（防 use-after-free）----
        // SubmitBatch 时 = 任务数，每个任务执行完 fetch_sub(1)。
        // 退役须满足 tilesRemaining==0 && pendingTasks==0：tilesRemaining=0 仅代表 tile 执行完，
        // deque 中可能仍有已 pop 未执行的任务（task.batch 引用本 storage），须等其全部完成才能 ReleaseBatch。
        // 由"最后者"（tile 完成者或 task 完成者）执行退役。
        std::atomic<uint32_t> pendingTasks{ 0 };

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

        // 物理退役与逻辑完成刻意分离：最后回调结束后，worker 槽与 Complete() 协助读取者
        // 仍引用调度器元数据。
        std::atomic<bool> cleanupStarted{ false };
        std::atomic<bool> finalized{ false };
        std::atomic<bool> workersFinished{ false };

        uint64_t diagnosticId{ 0 };

        // ---- JobCostCache：per-job 自动 batch ----
        // funcHash：Schedule 入口设置，退役时按此更新 per-job 每元素成本 EWMA；0 = 未标记（不参与自动 batch）。
        uint32_t funcHash{ 0 };
        // jccFine：本次分块是否由 JCC 公式（细粒度）产出（ResolveChunkSize 设置）。
        // 退役时据此把学习样本归为细/粗（粗 = tpw 兜底/mem-bound/显式 batchSize）。
        bool jccFine{ false };
        // totalElements：Schedule 入口设置（IJobParallelFor 的 length）。
        // 退役时 perElemNs = (topologyDoneAt - publishedAt) / totalElements。
        uint32_t totalElements{ 0 };
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
        // JobCostCache：Schedule 入口设置的 funcPtr hash（0 = 未标记）。
        uint32_t funcHash{ 0 };
    };

    // ---- base 模块助手（定义在 JobSystem.cpp） ----
    inline void SetCurrentBatchId(uint64_t id) noexcept
    {
        auto cb = g_currentBatchIdCallback.load(std::memory_order_acquire);
        if (cb) cb(id);
    }
    uint64_t AssignStateDiagnosticId(HandleState* state) noexcept;

    // ---- 调试面板：执行窗口上报（事件驱动，非每帧采样）----
    // 入口记录"开始"事件（压栈 + 时间戳），出口把完整窗口 [startMs, endMs] 追加进共享历史；
    // GUI 只读渲染共享历史，微秒级 Job（两帧之间跑完）也不丢失。
    // 有 worker 索引的线程上报其泳道；无索引的调用线程（主线程 inline）上报 M 泳道（index == CurrentWorkerCount()）。
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
    extern std::atomic<unsigned int> g_debugSegVisible; // 已发布段数（写者 release 发布；读者 acquire）
    // 每槽 seqlock 代次：奇=写中、偶=写完。修复并发写者乱序完成时，读者凭 visible
    // 定位到"已发布但尚未写完"的槽（visible 计数先行、槽内容滞后的撕裂）。
    extern std::atomic<uint64_t> g_debugSegSeq[kDebugSegmentMax];

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
        // 零开销守门：面板关闭时不触碰任何原子（此判断须先于 id==0 判断）。
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
        // 暂停时停止记录新段，环形缓冲不被覆盖、历史得以保留。
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
                const unsigned int slot = h % kDebugSegmentMax;
                g_debugSegSeq[slot].fetch_add(1, std::memory_order_acquire);  // 奇：写中
                g_debugSegments[slot] = DebugSegment{ lane, f.id, f.startMs, DebugNowMs(), f.tiles, f.workers, f.isDirect };
                g_debugSegSeq[slot].fetch_add(1, std::memory_order_release);  // 偶：写完
                g_debugSegVisible.fetch_add(1, std::memory_order_release);
            }
        }
        g_workerIsActive[lane].store(false, std::memory_order_release);
        g_workerCurrentBatchId[lane].store(0, std::memory_order_release);
        g_workerCurrentTile[lane].store(0, std::memory_order_release);
        g_workerBatchTileCount[lane].store(0, std::memory_order_release);
    }
    // 尝试读一个已完成的 debug segment（跳过写中的槽）。seqlock：seq 奇=写中。
    // 返回 false 表示该槽正在写或读期间被写，调用方应跳过。
    inline bool DebugTryReadSegment(unsigned int slot, DebugSegment& out) noexcept
    {
        const uint64_t s1 = g_debugSegSeq[slot].load(std::memory_order_acquire);
        if (s1 & 1) return false;
        out = g_debugSegments[slot];
        const uint64_t s2 = g_debugSegSeq[slot].load(std::memory_order_acquire);
        return s1 == s2;
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
        // C++ 异常协议：inline/同步路径（小 job 直跑）异常也捕获到 handle state，
        // 由 Complete() 统一重抛（与批量路径一致；noexcept 下若不捕获会 terminate）。
        try
        {
            fn();
        }
        catch (...)
        {
            RecordStateException(state, std::current_exception());
        }
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
    // 提交异步 state-owned 操作：即使操作在正常尾声前抛异常，state 引用也由后端包装器释放。
    bool SubmitBackendAsync(
        std::function<void()> work,
        HandleState* state = nullptr,
        void (*failureCleanup)(void*) = nullptr,
        void* failureContext = nullptr) noexcept;
    int ResolveChunkSize(int length, int requestedChunk);
    // 带 funcHash 的重载：flag 开启且有 per-job 成本数据时按 perElem EWMA 自动求解最优 tile 数，否则 tpw=4 兜底；funcHash=0 等价两参版本。
    // outJccFine（可空）：分块是否由 JCC 公式（细粒度）产出，退役时据此归细/粗（公式可能产出 < tpw 的 tiles，tile 数比较会误判）。
    int ResolveChunkSize(int length, int requestedChunk, uint32_t funcHash,
        bool* outJccFine = nullptr);

    // ---- 实体数衡 tile（定义在 JobSystem_Tiles.cpp） ----
    int UnitEntityCount(const ChunkBatchContext* cc, TileKind kind, int unit) noexcept;
    int ResolveEcsEntityTileTarget(int64_t totalEntities, int workerCount) noexcept;
    int BuildEntityBalancedTiles(ExecutionTile* tiles, const ChunkBatchContext* cc,
        TileKind kind, int itemCount, int targetEntities) noexcept;

    // ---- shutdown 残留 batch 强制退役（定义在 JobSystem_Tiles.cpp） ----
    // Shutdown 对每个未退役 batch 执行与正常退役 finalized 块等价的
    // cleanup + ReleaseBatch + backendRetired + ReleaseState，但不检查 tilesRemaining/pendingTasks
    // ——shutdown 时 worker 已 join，单线程安全。context 已清理或 finalized 已置位时幂等跳过。
    void ForceFinalizeBatch(BatchState* batch) noexcept;

    // 中止从未到达后端的 batch（分配/生命周期/continuation 失败）：持有初始 BatchStorage 引用，
    // 发布终态 HandleState，不触碰调度器计数器。
    void AbortUnsubmittedBatch(BatchState* batch, std::exception_ptr exception) noexcept;

    // ---- ISPC MT 任务挂钩（tasksys.cpp 调用，事件驱动显示每个参与 worker 的耗时）----
    // 每个 ISPC 任务在自己的 ConcRT 线程上执行，分配到保留的高位泳道（W/M 之后）。
    // tasksys.cpp 位于 NativeTranspiled.dll，故本 API 须从 NativeDll 导出：
    // JOB_SYSTEM_EXPORT 已定义→dllexport；未定义→dllimport。
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
    void ReleaseBatchStorage(BatchStorage* storage) noexcept;
#ifdef ENTJOY_TESTING
    // Test-only allocation fault injection. Production builds do not expose this hook.
    void FailNextBatchStorageAcquireForTests(int count) noexcept;
#endif
    void ClearBatchStoragePool() noexcept;
    void FlushBatchStorageCacheToSharedPool();
    void SubmitBatch(BatchState* batch, int workerCap = 0);
    // 隐式批（native 收集）入口：开关开 → 挂 pending（持 state 引用防悬垂）；否则直接 SubmitBatch。
    void SubmitOrPending(BatchState* batch);
    // 隐式批 force point：defer 窗口内提交全部 pending + 单次唤醒（EndFrame / Complete 自动触发）。
    void FlushPendingSubmits();
    bool ChunkExecuteTile(void* ctx, const ExecutionTile& tile);
    void CleanupChunkContext(void* ctx);
    void DestroyChunkContextWithoutCleanup(void* ctx) noexcept;
    bool GeneralExecuteTile(void* ctx, const ExecutionTile& tile);
    void CleanupGeneralContext(void* ctx);
    void DestroyGeneralContextWithoutCleanup(void* ctx) noexcept;

    // ---- Chase-Lev tile 级窃取（定义在 JobSystem_Tiles.cpp） ----
    // ChaseLevScheduler 回调 trampoline（供 Scheduler::Initialize 传给 ChaseLevScheduler::Start）
    void ChaseLevExecuteTile(BatchState* batch, uint32_t tileIndex) noexcept;
    // ChaseLev 双条件退役：tilesRemaining==0 && pendingTasks==0 时才释放 storage。
    void TryFinalizeChaseLevBatch(BatchState* batch) noexcept;
    // ChaseLev 任务完成回调：pendingTasks--，归零时触发双条件退役检查。
    void ChaseLevTaskDone(BatchState* batch) noexcept;
    // 记录 worker 进入批次的时间（firstWorkerAt/lastWorkerAt），供 timing 诊断。
    void ChaseLevRecordWorkerEntry(BatchState* batch) noexcept;
    // （标准 Chase-Lev 不需要共享注册表追踪）
} // namespace JobSystem
