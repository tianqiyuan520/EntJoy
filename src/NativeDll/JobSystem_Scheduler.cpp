#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"
#include "ThreadAffinity.h"
#include "JobDebuggerGUI.h"

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#if defined(_WIN32)
#include <windows.h>
#include <timeapi.h>
#if defined(_MSC_VER)
#pragma comment(lib, "winmm.lib")
#endif
#endif

namespace JobSystem
{
    // 无效/关闭中的提交仍执行调用方提供的 cleanup；异常走异步 job 同一冷路径通道，
    // 避免跨非托管导出边界抛出或终止进程，同时保留调用方持有的 Complete() 语义。
    static JobHandle MakeCompletedAfterCleanup(void (*cleanup)(void*), void* context)
    {
        auto* state = CreateState(true);
        if (cleanup)
        {
            try
            {
                cleanup(context);
            }
            catch (...)
            {
                RecordStateException(state, std::current_exception());
            }
        }
        return JobHandle(state);
    }

    // ============================================================
    // Schedule helpers
    // ============================================================
    // **认领几何的优先级**：
    //   ① 调用点声明（表项第四字段，`kClaimGeomSpread|Adjacent`）—— 显式，最高优先；
    //   ② F6 按 job 学习（仅 General 路；判据 = JCC 学到的每元素成本 + 迟滞）。
    // 两者都缺省时 ⇒ 恒为"不切片"。
    // ⚠ 切片**机制本身**（InitSliceCursors / BatchState::sliceCount 等）必须保留：F6 在用它。
    static bool ResolveClaimSliced(const BatchState* batch, bool allowAdaptive) noexcept
    {
        const uint32_t decl = (batch != nullptr) ? batch->claimGeomOverride : kClaimGeomAuto;
        if (decl == kClaimGeomSpread) return true;
        if (decl == kClaimGeomAdjacent) return false;
        if (!allowAdaptive) return false;   // chunk/entity 路：不切片
        return g_claimAdaptiveEnabled
            ? g_jobCostCache.ClaimSlicedWanted(batch != nullptr ? batch->funcHash : 0u, /*fallback=*/false)
            : false;
    }
    // ── tile 布局缓存：同 key（unitsPtr/itemCount/workerCap/rangeSize/unitGeneration）下划分确定
    //    → 跨 job 共享（同 query 只扫一次）；只存值拷贝不持指针 → 无悬垂。
    namespace {
        struct TileLayoutEntry
        {
            const void* unitsPtr = nullptr;
            int itemCount = 0;
            int workerCap = 0;
            int rangeSize = 0;
            uint32_t unitGeneration = 0; // C# cache StructuralVersion：重建必变 → 防指针地址复用误命中
            int64_t totalEntities = 0;  // 实体总量（JCC 前置判重成本估算用）
            uint32_t tileCount = 0;
            std::vector<uint32_t> bounds; // 长度 tileCount+1：tile i 覆盖 [bounds[i], bounds[i+1])
        };
        struct TileLayoutCache
        {
            std::mutex mtx;
            TileLayoutEntry entries[16];
            int count = 0;
        };
        TileLayoutCache g_tileLayoutCache;

        bool TileLayoutTryGet(const void* unitsPtr, int itemCount, int workerCap, int rangeSize,
            uint32_t unitGeneration, uint32_t& outTileCount, int64_t& outTotalEntities,
            std::vector<uint32_t>& outBounds)
        {
            // unitGeneration==0 = 调用方无缓存身份（fallback 路径）→ 不参与缓存（避免指针复用误命中）。
            if (!unitsPtr || unitGeneration == 0) return false;
            std::lock_guard<std::mutex> lock(g_tileLayoutCache.mtx);
            for (int i = 0; i < g_tileLayoutCache.count; ++i)
            {
                const auto& e = g_tileLayoutCache.entries[i];
                if (e.unitsPtr == unitsPtr && e.itemCount == itemCount &&
                    e.workerCap == workerCap && e.rangeSize == rangeSize &&
                    e.unitGeneration == unitGeneration)
                {
                    outTileCount = e.tileCount;
                    outTotalEntities = e.totalEntities;
                    outBounds = e.bounds;
                    return true;
                }
            }
            return false;
        }

        void TileLayoutStore(const void* unitsPtr, int itemCount, int workerCap, int rangeSize,
            uint32_t unitGeneration, int64_t totalEntities, uint32_t tileCount,
            const std::vector<uint32_t>& bounds)
        {
            if (unitGeneration == 0) return;
            std::lock_guard<std::mutex> lock(g_tileLayoutCache.mtx);
            // 覆盖同 key（重算）或插入；满则简单覆盖 index 0（LRU 近似，8+ 不同 key 罕见）。
            for (int i = 0; i < g_tileLayoutCache.count; ++i)
            {
                auto& e = g_tileLayoutCache.entries[i];
                if (e.unitsPtr == unitsPtr && e.itemCount == itemCount &&
                    e.workerCap == workerCap && e.rangeSize == rangeSize &&
                    e.unitGeneration == unitGeneration)
                {
                    e.totalEntities = totalEntities;
                    e.tileCount = tileCount;
                    e.bounds = bounds;
                    return;
                }
            }
            int slot = g_tileLayoutCache.count < 16 ? g_tileLayoutCache.count++ : 0;
            auto& e = g_tileLayoutCache.entries[slot];
            e.unitsPtr = unitsPtr;
            e.itemCount = itemCount;
            e.workerCap = workerCap;
            e.rangeSize = rangeSize;
            e.unitGeneration = unitGeneration;
            e.totalEntities = totalEntities;
            e.tileCount = tileCount;
            e.bounds = bounds;
        }
    }
    template <typename WorkBuilder>
    JobHandle ScheduleWithDependency(const JobHandle& dep, WorkBuilder&& builder)
    {
        auto* state = CreateState(false);
        AssignStateDiagnosticId(state);
        auto* ds = dep.State();
        if (!ds || ds->completed.load(std::memory_order_acquire))
        {
            try
            {
                builder(state);
            }
            catch (...)
            {
                CompleteStateAfterException(state, std::current_exception());
            }
            return JobHandle(state);
        }
        AcquireState(state);
        RetainDependency(state, ds);
        try
        {
            AddContinuationOrRunNow(ds, [state, b = std::forward<WorkBuilder>(builder)]() mutable {
                try
                {
                    b(state);
                }
                catch (...)
                {
                    CompleteStateAfterException(state, std::current_exception());
                }
                // Balance the continuation's in-flight reference even when
                // the builder or submission path fails.
                ReleaseState(state);
            });
        }
        catch (...)
        {
            CompleteStateAfterException(state, std::current_exception());
            ReleaseState(state); // continuation reference acquired above
        }
        return JobHandle(state);
    }

    template <typename Work>
    void FastPath(Work&& work, void* ctx, void (*cleanup)(void*), HandleState* state)
    {
        AcquireState(state);
        try
        {
            const bool accepted = SubmitBackendAsync([work = std::forward<Work>(work), state, ctx, cleanup]() {
                // 非 batch 快速路径异步窗口——work() 即 C# func 执行点，
                // 执行期间 set/clear 当前-batch 使异常按本 job 归属。
                const uint64_t id = state->diagnosticBatchId.load(std::memory_order_acquire);
                // 调试面板：pool 执行窗口上报到本 worker 泳道（WorkerLoop 已预分配索引）
                DebugBeginExec(id, 1, 1, false); // 快速路径 Job：单线程执行
                if (id != 0) SetCurrentBatchId(id);
                try { work(); }
                catch (...)
                {
                    // C++ 异常协议：快速路径（pool 窗口）异常记录到 handle state，Complete() 统一重抛。
                    RecordStateException(state, std::current_exception());
                }
                if (id != 0) SetCurrentBatchId(0);
                DebugEndExec();
                try
                {
                    if (cleanup) cleanup(ctx);
                }
                catch (...)
                {
                    RecordStateException(state, std::current_exception());
                }
                try { CompleteState(state); } catch (...) { RecordStateException(state, std::current_exception()); }
            }, state, cleanup, ctx);
            (void)accepted; // failure path performs cleanup and terminalization
        }
        catch (...)
        {
            // std::function construction or an unexpected submission failure
            // happened before ownership reached the backend wrapper.
            RecordStateException(state, std::current_exception());
            try
            {
                if (cleanup) cleanup(ctx);
            }
            catch (...)
            {
                RecordStateException(state, std::current_exception());
            }
            try { CompleteState(state); } catch (...) { RecordStateException(state, std::current_exception()); }
            ReleaseState(state);
        }
    }

    template <typename Work>
    JobHandle ScheduleFastPath(Work&& work, void* ctx, void (*cleanup)(void*), const JobHandle& dep)
    {
        auto* state = CreateState(false);
        const uint64_t id = AssignStateDiagnosticId(state);
        // 与 SubmitBatch 同语义：调度即"发布"（pool 执行窗口另由 FastPath 上报泳道）。
        // 纯诊断计数（只被 GetStatsSnapshot/GUI 读取），受 g_statsEnabled 门控。
        if (StatsEnabled())
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
        RecordPublishedJob(id, 1);
        auto* ds = dep.State();
        if (!ds || ds->completed.load(std::memory_order_acquire))
        { FastPath(std::forward<Work>(work), ctx, cleanup, state); return JobHandle(state); }
        AcquireState(state);
        RetainDependency(state, ds);
        try
        {
            AddContinuationOrRunNow(ds, [state, work = std::forward<Work>(work), ctx, cleanup]() mutable {
                try
                {
                    FastPath(std::forward<Work>(work), ctx, cleanup, state);
                }
                catch (...)
                {
                    CompleteStateAfterException(state, std::current_exception());
                }
                ReleaseState(state);
            });
        }
        catch (...)
        {
            RecordStateException(state, std::current_exception());
            try { if (cleanup) cleanup(ctx); }
            catch (...) { RecordStateException(state, std::current_exception()); }
            try { CompleteState(state); } catch (...) { RecordStateException(state, std::current_exception()); }
            ReleaseState(state);
        }
        return JobHandle(state);
    }

    // ============================================================
    // Scheduler
    // ============================================================
    // 注意：worker 线程被显式设为 NORMAL 优先级，进程 PriorityClass 不影响它们。
    static bool ResolveWorkerAffinityEnabled() noexcept
    {        // 默认关闭 CPU 亲和性：worker 交 OS 自由调度（避免 SMT 双线程死绑共享执行单元）。
        // ENTJOY_WORKER_AFFINITY=1 可显式开启（无 SMT / 独占机器场景）。
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
        // 显式 "1"/"true"/"on" 才开启；其余（含未设置/0/off）默认关闭。
        return value == "1" || value == "true" || value == "on";
    }

    bool Scheduler::Initialize(int numThreads)
    {
        std::lock_guard<std::mutex> lifecycleLock(g_schedulerMutex);
        g_shuttingDown.store(false, std::memory_order_release);
        g_mainThreadId = std::this_thread::get_id();
        // E1 忙比诊断：清计数并记起点（`ENTJOY_DIAG_E1=1` 时才真的做，否则空操作）。
        E1::Reset();
#if defined(_WIN32)
        // 提升进程优先级，减少 worker 与 OS/其他进程竞争时被降权。
        ::SetPriorityClass(::GetCurrentProcess(), ABOVE_NORMAL_PRIORITY_CLASS);
#endif
            int resolved;
            int envWorkers = 0;
            // 默认 worker 数 = 逻辑核心-1；SMT 竞争由自适应亲和消化，无需限制 ≤ 物理核心。
            // ENTJOY_JOB_WORKERS>0 显式覆盖。
            {
                    std::string value;
#if defined(_WIN32)
                    char* raw = nullptr;
                    std::size_t rawLength = 0;
                    if (_dupenv_s(&raw, &rawLength, "ENTJOY_JOB_WORKERS") == 0 && raw)
                    {
                        value.assign(raw);
                        std::free(raw);
                    }
#else
                    if (const char* raw = std::getenv("ENTJOY_JOB_WORKERS"))
                        value.assign(raw);
#endif
                    int v = 0;
                    if (!value.empty())
                    {
                        try { v = std::stoi(value); } catch (...) { v = 0; }
                    }
                    if (v > 0) envWorkers = v;
            }
            resolved = numThreads > 0 ? numThreads :
                (envWorkers > 0 ? envWorkers :
                    std::max(1, static_cast<int>(
                        std::thread::hardware_concurrency()) - 1));
            // 诊断数组 kMaxTrackedWorkers=64：超出会导致 GetWorkerSnapshots/DumpState
            // 及 affinity 位运算越界，此处钳制。
            resolved = std::min(resolved, kMaxTrackedWorkers);
            if (auto scheduler = LoadChaseLevScheduler(); scheduler && scheduler->IsRunning()) return true;
            g_numThreads.store(resolved, std::memory_order_relaxed);
            // 物理核数在此刷新（冷路径一次；"小 job 不超订物理核"的上限判定要用它）。
            RefreshPhysicalCoreCount();
            g_workerAffinityEnabled.store(
                ResolveWorkerAffinityEnabled(), std::memory_order_relaxed);

            // 主线程 assist 默认关闭（纯 worker 模式）；JobSystem_SetMainThreadAssist(int) 可运行时开启。

            // 主线程钉到逻辑核 0，避免被共享 L1/L2 的 worker 抢占。
            if (g_workerAffinityEnabled.load(std::memory_order_relaxed))
                BindCurrentThreadToLogicalProcessor(0);

            // Chase-Lev 调度器（唯一路径）：持久 worker 线程 + per-worker deque + MPMC Injector
            // 实例进程内唯一（首次 Initialize 创建，之后复用；Shutdown 只 Stop 不销毁）
            // ⇒ 伴生裸指针永不悬垂，热路径无需 shared_ptr 自旋锁。
            if (!g_chaseLevSchedulerInstance)
                g_chaseLevSchedulerInstance = std::make_shared<ChaseLevScheduler>();
            auto scheduler = g_chaseLevSchedulerInstance;   // 本代 shared_ptr 别名（锚定生命周期）
            if (!scheduler->Start(
                static_cast<uint32_t>(resolved),
                &ChaseLevExecuteTile,
                &ChaseLevTaskDone,
                g_workerAffinityEnabled.load(std::memory_order_relaxed)))
            {
                // worker 创建失败：回滚，避免「无 worker 但仍认为 Native 可用」。
                g_shuttingDown.store(true, std::memory_order_release);
                return false;
            }
            // 先发布 shared_ptr（既有语义），再发布裸指针（热路径读取）。
            std::atomic_store_explicit(&g_chaseLevScheduler, scheduler, std::memory_order_release);
            g_chaseLevSchedulerRaw.store(scheduler.get(), std::memory_order_release);

#if defined(_WIN32)
            // 只在真正创建一代 scheduler 后增加计时器分辨率引用，避免重复 Initialize 泄漏引用。
            ::timeBeginPeriod(1);
#endif

            // 若设置了 ENTJOY_DEBUG=1，启动 Dear ImGui 调试窗口
            JobDebuggerGUI::TryLaunch();
            return true;
    }

    void Scheduler::Shutdown()
    {
        // 线程防护：**只有本调度器的 worker 线程**不能调 Shutdown —— 它会走到
        // ChaseLevScheduler::Stop 的 join 自身 → 永不返回死锁。
        // 任何非 worker 的线程（含 ProcessExit/DomainUnload 回调线程）调用都是安全的。
        if (ChaseLevScheduler_IsWorkerThread())
        {
            std::fprintf(stderr,
                "[JobSystem] Shutdown() called from a scheduler worker thread — rejected (would self-join deadlock).\n");
            return;
        }

        std::lock_guard<std::mutex> lifecycleLock(g_schedulerMutex);
        // ★ 先关掉 ImGui 调试面板并**等它退出**，再拆 worker/状态：
        //   面板线程是 detach 的，且在关停期仍会读 JobSystem 状态；先停面板可保证
        //   "窗口线程不会比 JobSystem 活得更久"（也让关停后可重新 Launch）。
        //   无 ImGui 构建下该调用是空实现（CMake 目标即如此），不影响 CI/测试路径。
        JobDebuggerGUI::Shutdown();
        // Shutdown 幂等；停止/重置期间保持 gate，防止 Initialize 与 teardown 并发发布新一代。
        g_shuttingDown.store(true, std::memory_order_release);
        // 关停前的 worker 池大小快照：下面马上要把它清零（给关停期读者看"无池"），
        // 但末尾的 [JOBPHYS] 诊断行要用它 —— 否则那个字段恒为 0。
        const int g_numThreadsBeforeShutdown = g_numThreads.load(std::memory_order_relaxed);
        g_numThreads.store(0, std::memory_order_relaxed);
        // 关键：在 pending 锁内关闭隐式批，再 flush——否则「Schedule 读到 enabled=true → 暂停 →
        // Shutdown flush 空队列 → 调度线程继续入队」会把 batch 留在无人 flush 的队列，永久悬挂/泄漏。
        {
            std::lock_guard<std::mutex> lock(g_pendingBatchesMutex);
            g_implicitBatchEnabled.store(false, std::memory_order_release);
        }
        // 隐式批排空：执行 pending 中未发布的 job（worker 尚在运行）；未及执行者由 in-flight 兜底，不产生 UAF。
        FlushPendingSubmits();
        // 性能项 5：先清裸指针（此后新读者得 nullptr，与旧实现交换后语义一致），再取本代
        // shared_ptr 别名并 Stop。实例本身由 g_chaseLevSchedulerInstance 持有 ⇒ 即便有读者在
        // 清空之前刚取到指针，其解引用也不会悬垂（对象永不析构）。
        g_chaseLevSchedulerRaw.store(nullptr, std::memory_order_release);
        if (auto scheduler = std::atomic_exchange_explicit(
                &g_chaseLevScheduler, std::shared_ptr<ChaseLevScheduler>{}, std::memory_order_acq_rel))
        {
            scheduler->Stop();
            // 释放 Stop 排空出的未退役 batch（cleanup + ReleaseBatch + ReleaseState），
            // 消除 shutdown 未完成 job 的 context 泄漏。
            for (auto* batch : scheduler->drainedBatches)
                ForceFinalizeBatch(batch);
        }
        ConsumeLongBatchBarriers();
        // 先把 main 线程缓存的 batch storage 交还共享池再清空；worker 已 join，其 thread_local 缓存已交还。
        FlushBatchStorageCacheToSharedPool();
        ClearBatchStoragePool();
        // 批上下文池同理：主线程缓存交还共享池后清空。
        FlushBatchContextCacheToSharedPool();
        ClearBatchContextPool();
        // BackendAsyncContext 池同理（worker 已 join，其 TLS 缓存已交还共享池）。
        FlushAsyncContextCacheToSharedPool();
        ClearAsyncContextPool();
        // 诊断收尾：`ENTJOY_DIAG_NATIVE_PHASE=1` 时打印 Complete 分段（未设该变量时为空操作）。
        DiagPhase::Dump();
        // E1 收尾：`ENTJOY_DIAG_E1=1` 时打印 worker 忙比 / 窗口抖动（未设该变量时为空操作）。
        E1::Dump();
        // 诊断收尾：`ENTJOY_DIAG_NATIVE_SCHED=1` 时打印 Schedule 分段（未设该变量时为空操作）。
        SchedPhase::Dump();
        // 小 job 物理核封顶策略的可观测性（每次 Shutdown 一行）。
        // ⚠ `workerThreads` 必须用**关停前快照**：本函数上方已把 `g_numThreads` 清零，
        //   直接读该字段会恒为 0（诊断撒谎 ⇒ 结论无效）。
        const char* physCapEnv = std::getenv("ENTJOY_PHYSCAP_SMALLJOB");
        std::printf("[JOBPHYS] physicalCores=%d workerThreads=%d smalljobPhysCap=%s cappedJobs=%llu\n",
            g_physicalCores.load(std::memory_order_relaxed),
            g_numThreadsBeforeShutdown,
            (physCapEnv != nullptr && physCapEnv[0] == '0') ? "OFF(=0)" : "ON(default)",
            (unsigned long long)g_physCapApplied.load(std::memory_order_relaxed));
        // 代次校验的生效证据（每次 Shutdown 一行）：
        //   staleSettleDropped = 结算时"令牌代次 != storage 当前代次"被拒的次数；
        //   pendingTasksWrap   = `pendingTasks.fetch_sub(1)` 打在 0 上的次数。
        // 两者都应恒为 0；非 0 即证明"批已回收而令牌仍在飞"确实发生。
        std::printf("[JOBGEN] staleSettleDropped=%llu pendingTasksWrap=%llu\n",
            (unsigned long long)g_staleSettleDropped.load(std::memory_order_relaxed),
            (unsigned long long)g_pendingTasksWrap.load(std::memory_order_relaxed));
        // ⚠ `wakePoll=ON`（默认）时旧的广播守卫整段不执行 ⇒ 该计数恒为 0，
        //   真正的跳过数见下方的 `[JOBWAKEPOLL]`（"skipped=0" 不代表跳过守卫从不触发）。
        // 认领几何学习（`ENTJOY_CLAIM_ADAPT`，默认开）的生效证据：
        //   判据：nosample 若吃掉绝大多数有效调用 ⇒ 学习事实上是死的；flips==0 ⇒ 自适应从未动过。
        {
            const char* adaptEnv = std::getenv("ENTJOY_CLAIM_ADAPT");
            std::printf("[JOBF6] claimAdapt=%s nokey=%llu nosample=%llu sliced=%llu interleaved=%llu flips=%llu\n",
                (adaptEnv != nullptr && adaptEnv[0] == '0') ? "OFF(=0)" : "ON(default)",
                (unsigned long long)g_claimGeomNoKey.load(std::memory_order_relaxed),
                (unsigned long long)g_claimGeomNoSample.load(std::memory_order_relaxed),
                (unsigned long long)g_claimGeomSliced.load(std::memory_order_relaxed),
                (unsigned long long)g_claimGeomInterleaved.load(std::memory_order_relaxed),
                (unsigned long long)g_claimGeomFlips.load(std::memory_order_relaxed));
        }
        // 每-job 分母：把"这个 pass 每步被调用几次、每次多少元素"打出来（与宿主的 [M-19] ms 按 key 对照）
        JobPerKeyDump();
        // 认领几何生效证据（声明值分桶）：`ClaimPolicy`/批表第 4 字段/F6 这条轴的计数
        //（"调用点传几何 ⇒ 静默降级到托管回调"靠这里才能证明修好了）。
        std::printf("[JOBGEOM] declared spread=%llu adjacent=%llu auto=%llu\n",
            (unsigned long long)g_claimGeomDeclSpread.load(std::memory_order_relaxed),
            (unsigned long long)g_claimGeomDeclAdjacent.load(std::memory_order_relaxed),
            (unsigned long long)g_claimGeomDeclAuto.load(std::memory_order_relaxed));
        // 等宽 tile / 每批快路径的生效证据（默认开，受 thinTiles 判据门控）。
        // ⚠ `thinTiles` 用的 `cs <= kClaimSpanThinElems(16)` 恰好等于 `ResolveChunkSize` 五处
        //   `std::max(16, …)` 的硬编码下限（`JobSystem_State.cpp:1183/1203/1259/1281/1342`）
        //   ⇒ 该判据实际等价于"**JCC 顶在下限上**"，与 tile 厚薄无关；且"进不进该 regime"**随进程翻**
        //   ⇒ 这个门既不是薄厚判据、也不是 length 的稳定函数。
        std::printf("[JOBF2F4] uniformTiles=%s applied=%llu | tileFastPath=%s applied=%llu"
                    " (thinTiles 门控: cs <= 16)\n",
            g_uniformTilesEnabled ? "ON" : "OFF",
            (unsigned long long)g_uniformTilesApplied.load(std::memory_order_relaxed),
            g_tileFastPath ? "ON" : "OFF",
            (unsigned long long)g_tileFastApplied.load(std::memory_order_relaxed));
        std::printf("[JOBWAKE] notify_all skipped=%llu (only the legacy broadcast path; inactive while"
                    " wakePoll=ON -- see [JOBWAKEPOLL] for the real skip count)\n",
            (unsigned long long)g_notifySkipped.load(std::memory_order_relaxed));
        std::fflush(stdout);
        // `ENTJOY_WAKE_POLL` 的生效证据（否则"开关没打开"会被读成"改动无效"）：
        // skips = 提交侧一个字节都没写的次数；wakes = 真的 bump+notify_all 的次数。
        {
            // 计数走 thread_local 累加（热路径不写全局原子）⇒ 打印前先合并**当前线程**的尾巴。
            WakePollFlushCurrentThread();
            std::printf("[JOBWAKEPOLL] wakePoll=%s skips=%llu wakes=%llu parkWake=%llu"
                        " | work skips=%llu wakes=%llu | batch skips=%llu wakes=%llu\n",
                WakePollEnabled() ? "ON" : "OFF",
                (unsigned long long)g_wakePollSkips.load(std::memory_order_relaxed),
                (unsigned long long)g_wakePollWakes.load(std::memory_order_relaxed),
                (unsigned long long)g_parkWakeCount.load(std::memory_order_relaxed),
                (unsigned long long)g_wakePollSkipsWork.load(std::memory_order_relaxed),
                (unsigned long long)g_wakePollWakesWork.load(std::memory_order_relaxed),
                (unsigned long long)g_wakePollSkipsBatch.load(std::memory_order_relaxed),
                (unsigned long long)g_wakePollWakesBatch.load(std::memory_order_relaxed));
            std::fflush(stdout);
        }
        // 先交还 main 缓存中的 state 再清空；worker 已 join 交还，故清空覆盖全部 state。
        FlushStateCacheToSharedPool();
        { std::lock_guard<std::mutex> lock(g_statePoolMutex); for (auto* s : g_statePool) delete s; g_statePool.clear(); }
#if defined(_WIN32)
        // 与 Initialize 的 timeBeginPeriod(1) 配对，避免多次 Init/Shutdown 累积系统计时器分辨率引用。
        ::timeEndPeriod(1);
#endif
    }

    void Scheduler::PrewakeWorkers()
    {
        // Chase-Lev worker 常驻 spin/futex，无需显式唤醒 → 本导出为 no-op。
    }

    void Scheduler::ConfigureTilesPerWorker(int tilesPerWorker)
    {
        // 并行 for 默认粒度（batchSize=0 时用）。Initialize 期调用，经 job 提交的 release/acquire 对 worker 可见。
        // `ENTJOY_TILES_PER_WORKER=<n>` 时优先于入参。
        static const int envTpw = [] {
            const char* v = std::getenv("ENTJOY_TILES_PER_WORKER");
            return (v != nullptr) ? std::atoi(v) : 0;
        }();
        const int effective = (envTpw > 0) ? envTpw : tilesPerWorker;
        g_configuredTilesPerWorker.store(std::max(1, effective), std::memory_order_relaxed);
    }

    void Scheduler::ConfigureGuided(int enabled, int k, int floor)
    {
        // guided（chunk ∝ 剩余工作量）开关+参数；Initialize 期调用，经 job 提交的 release/acquire 对 worker 可见。
        g_guidedEnabled.store(enabled != 0 ? 1 : 0, std::memory_order_relaxed);
        g_guidedK.store(std::max(1, k), std::memory_order_relaxed);
        g_guidedFloor.store(std::max(1, floor), std::memory_order_relaxed);
    }

    // ---------- IJob ----------
    // Schedule 一律异步提交（对齐 Unity JobSystem 语义：调用线程只提交，不执行）。
    // 需要同步执行请用 Run()。
    JobHandle Scheduler::Schedule(void (*func)(void*), void* context, void (*cleanup)(void*), const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire))
            return MakeCompletedAfterCleanup(cleanup, context);
        if (!func)
            return MakeCompletedAfterCleanup(cleanup, context);
        return ScheduleFastPath([func, context]() { func(context); }, context, cleanup, dependency);
    }

    // ---------- IJobFor ----------
    // Schedule 一律异步提交（对齐 Unity JobSystem 语义）。
    // IJobFor 语义是串行 for，由单个 worker 执行。
    JobHandle Scheduler::ScheduleFor(void (*func)(void*, int), void* context, int length, void (*cleanup)(void*), const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire))
            return MakeCompletedAfterCleanup(cleanup, context);
        if (!func || length <= 0)
            return MakeCompletedAfterCleanup(cleanup, context);
        if (length <= 64) return ScheduleFastPath([func, context, length]() { for (int i = 0; i < length; i++) func(context, i); }, context, cleanup, dependency);
        return ScheduleWithDependency(dependency, [func, context, length, cleanup](HandleState* state) {
            const uint64_t id = state->diagnosticBatchId.load(std::memory_order_acquire);
            if (StatsEnabled())
                g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            RecordPublishedJob(id, 1);
            AcquireState(state);
            try
            {
                SubmitBackendAsync([func, context, length, cleanup, state]() {
                    // state 由 ScheduleWithDependency 分配诊断 id，异步窗口同样需要归属。
                    const uint64_t id = state->diagnosticBatchId.load(std::memory_order_acquire);
                    DebugBeginExec(id, 1, 1, false); // ScheduleFor（异步单任务）Job：单线程执行
                    if (id != 0) SetCurrentBatchId(id);
                    try
                    {
                        for (int i = 0; i < length; i++) func(context, i);
                    }
                    catch (...)
                    {
                        RecordStateException(state, std::current_exception());
                    }
                    if (id != 0) SetCurrentBatchId(0);
                    DebugEndExec();
                    try
                    {
                        if (cleanup) cleanup(context);
                    }
                    catch (...)
                    {
                        RecordStateException(state, std::current_exception());
                    }
                    try { CompleteState(state); } catch (...) { RecordStateException(state, std::current_exception()); }
                }, state, cleanup, context);
            }
            catch (...)
            {
                // Argument construction can fail before SubmitBackendAsync
                // takes ownership of the acquired reference.
                RecordStateException(state, std::current_exception());
                try { if (cleanup) cleanup(context); }
                catch (...) { RecordStateException(state, std::current_exception()); }
                try { CompleteState(state); } catch (...) { RecordStateException(state, std::current_exception()); }
                ReleaseState(state);
            }
        });
    }

    // ---------- IJobParallelFor ----------
    // Schedule 一律异步提交（对齐 IJob/IJobFor）。
    JobHandle Scheduler::ScheduleParallelFor(void (*func)(void*, int), void* context, int length, int batchSize, void (*cleanup)(void*), const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire))
            return MakeCompletedAfterCleanup(cleanup, context);
        ConsumeLongBatchBarriers();
        if (!func || length <= 0)
            return MakeCompletedAfterCleanup(cleanup, context);
        // JobCostCache：hash 在 ResolveChunkSize 前计算（自适应分支需要）；FastPath 不学成本，batch 路径退役时学。
        // `ENTJOY_FORCE_INNER_BATCH`（默认关）：强制显式内批并跳过 JCC（funcHash=0）。
        const bool forceInner = (g_forceInnerBatch > 0) && (batchSize <= 0);
        const uint32_t funcHash = (g_jobCostCacheEnabled.load(std::memory_order_relaxed) && !forceInner)
            ? HashFuncPtr(reinterpret_cast<void (*)() noexcept>(func)) : 0;
        bool jccFine = false;
        int cs = forceInner ? static_cast<int>(g_forceInnerBatch)
                            : ResolveChunkSize(length, batchSize, funcHash, &jccFine);
        int rc = CeilDiv(length, cs);
        if (rc <= 1) return ScheduleFastPath([func, context, length]() { for (int i = 0; i < length; i++) func(context, i); }, context, cleanup, dependency);

        const uint32_t targetWorkers = static_cast<uint32_t>(
            ResolveWorkerTarget(0, rc));
        auto* bc = AcquireGeneralBatchContext();
        *bc = GeneralBatchContext{ func, nullptr, context, cleanup };
        bc->funcHash = funcHash;
        // General 路径默认"等量 tile"（配合批量认领既均衡又低争用）；g_guidedEnabled 开启时走 guided。
        const bool guided = g_guidedEnabled.load(std::memory_order_relaxed) != 0;   // 开启 guided：按工作量（chunk∝剩余）切 tile，可变代价 job 负载均衡
        const int guidedK = g_guidedK.load(std::memory_order_relaxed);
        const int guidedFloor = g_guidedFloor.load(std::memory_order_relaxed);
        const int tileCount = guided
            ? GuidedTileCount(length, static_cast<int>(targetWorkers), guidedK, guidedFloor)
            : rc;
        BatchStorage* storage = nullptr;
        HandleState* state = nullptr;
        try
        {
            // 只对**薄 tile**（每 tile 元素数 `cs ≤ kClaimSpanThinElems`）启用"不物化 tileBuffer + 每批快照
            // 快路径"；厚 tile 逐位走旧路径（`tiles[]` 带 `PrefetchNextTileData` ⇒ **物化是对的**）。
            // ⚠ 但这条判据**名不副实**：`kClaimSpanThinElems = 16` 恰好等于 `ResolveChunkSize` 各返回路径
            //   `std::max(16, …)` 的硬编码下限 ⇒ `cs <= 16` 实际是"**JCC 顶在下限上**"，与"tile 厚薄"无关，
            //   且"进不进该 regime"随进程翻。当前判定为**良性**（短调度 tileCount 本来就小，O(tileCount) 代价小）；
            //   若要真正的"薄 tile"判据，必须换成**相对**口径（如把 `cs` 与 `length/W` 比较，或用 `rc`）。
            const bool thinTiles = !guided && static_cast<uint32_t>(cs) <= kClaimSpanThinElems;
            const bool uniformTiles = g_uniformTilesEnabled && thinTiles;
            // 等宽时**不申请 tile 缓冲**（只要 batch 对象）。
            storage = AcquireBatchStorage(uniformTiles ? 0u : static_cast<uint32_t>(tileCount));
            auto* batch = &storage->batch;
            state = CreateState(false); batch->handle = state;
            batch->context = bc; batch->cleanup = [](void* ctx) { CleanupGeneralContext(ctx); };
            batch->executeTile = &GeneralExecuteTile;
            // 薄 tile 才生效：把每-tile 的 trace/timing/firstTileAt 固定开销提到每批/每令牌。
            // 厚 tile 下 `AcquireBatchStorage` 快照的 g_tileFastPath 结果被这里覆盖为 false ⇒ 逐位不变。
            batch->tileFast = g_tileFastPath && thinTiles;
            // 本批是否真的拿到"每批快照"快路径（`ENTJOY_TILE_FASTPATH` 的生效证据）。
            if (batch->tileFast) g_tileFastApplied.fetch_add(1, std::memory_order_relaxed);
            // 认领几何按 job 定（判据 = JCC 学到的每元素成本 + 迟滞，见 JobCostCache.h）。
            //   `funcHash == 0`（表/强制档，或 JCC 关）⇒ 无样本 ⇒ 回退全局 env。
            batch->funcHash = funcHash;
            // 每-job 分母：索引在**提交线程**解析一次写进批（worker 只按索引累加 ⇒ 无碰撞混行）。
            // 键必须用 `JobFuncKey`（批表/jobkeys.txt 同一键空间），**不是** `funcHash`
            // （后者在"表 + CLAIM_ADAPT=0"时有意为 0 ⇒ 用它会正好让本仪器在对齐档里失效）。
            int unkeyedReason = -1;
            batch->perKeyIndex = JobPerKeyResolve(reinterpret_cast<void (*)() noexcept>(func), unkeyedReason);
            bc->perKeyIndex = batch->perKeyIndex;   // worker 侧从 **context** 读（不做哈希查找）
            if (batch->perKeyIndex >= 0)
            {
                const uint32_t pi = static_cast<uint32_t>(batch->perKeyIndex);
                g_perKeyBatches[pi].fetch_add(1, std::memory_order_relaxed);
                g_perKeyElems[pi].fetch_add(static_cast<uint64_t>(length), std::memory_order_relaxed);
                g_perKeyTiles[pi].fetch_add(static_cast<uint64_t>(tileCount), std::memory_order_relaxed);
                // 逐键归因薄批来源：该门已证明等价于"length 够短"，故这一列读出的是**短调度分布**
                //（配 `tiles` 可反查每批平均 cs）。
                if (thinTiles) g_perKeyThin[pi].fetch_add(1, std::memory_order_relaxed);
            }
            else if (unkeyedReason >= 0)
            {
                // 未归因：按原因 + 长度量级记账，使"未归因的薄批"可被指名（而不是只看见一个总数）。
                g_unkeyedBatches[unkeyedReason].fetch_add(1, std::memory_order_relaxed);
                g_unkeyedLenBucket[unkeyedReason][Log2Bucket(static_cast<uint64_t>(length < 0 ? 0 : length))]
                    .fetch_add(1, std::memory_order_relaxed);
                if (thinTiles) g_unkeyedThin[unkeyedReason].fetch_add(1, std::memory_order_relaxed);
            }
            batch->jccFine = jccFine;
            batch->totalElements = static_cast<uint32_t>(length);
            batch->tileCount = static_cast<uint32_t>(tileCount);
            batch->nextTile.store(0, std::memory_order_relaxed);
            batch->tilesRemaining.store(batch->tileCount, std::memory_order_relaxed);
            if (uniformTiles)
            {
                batch->uniformTileSize = static_cast<uint32_t>(cs);
                // 本批走了"不物化 tileBuffer"（`ENTJOY_TILES_UNIFORM` 的生效证据，与 F4 同一条 thinTiles 判据）。
                g_uniformTilesApplied.fetch_add(1, std::memory_order_relaxed);
                batch->tiles = nullptr;
            }
            else if (guided)
            {
                BuildGuidedTiles(storage->tileBuffer, length,
                    static_cast<int>(targetWorkers), guidedK, guidedFloor);
                batch->tiles = storage->tileBuffer;
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
                batch->tiles = storage->tileBuffer;
            }
            // 唤醒多少个工作者 = 由预估工作量决定，再按物理核封顶（小 job）；tile 布局保持不变。
            batch->workerCount = static_cast<uint32_t>(
                ApplyPhysCoreCapForSmallJob(
                    ResolveWorkerTarget(0, rc),
                    batch->tileCount, length));
            // 切片认领的段游标必须在 publish 之前初始化（发布后 worker 会立刻执行）。
            InitSliceCursors(batch, ResolveClaimSliced(batch, true));
            JccDiagNoteWorkers(batch->workerCount);
            batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

            PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

            auto* ds = dependency.State();
            if (!ds || ds->completed.load(std::memory_order_acquire))
            {
                try { SubmitOrPending(batch); }
                catch (...) { AbortUnsubmittedBatch(batch, std::current_exception()); }
            }
            else
            {
                AcquireState(state);
                RetainDependency(state, ds);
                try
                {
                    AddContinuationOrRunNow(ds, [state, batch]() {
                        try { SubmitBatch(batch); }
                        catch (...) { AbortUnsubmittedBatch(batch, std::current_exception()); }
                        ReleaseState(state);
                    });
                }
                catch (...)
                {
                    AbortUnsubmittedBatch(batch, std::current_exception());
                    ReleaseState(state);
                }
            }
            return JobHandle(state);
        }
        catch (...)
        {
            // 构造失败时 native 不拥有原始 context；只销毁内部 wrapper，
            // 调用方（C#）负责随后执行一次用户 cleanup。
            if (storage)
            {
                auto* batch = &storage->batch;
                if (batch->handle)
                {
                    batch->context = nullptr;
                    batch->cleanup = nullptr;
                    AbortUnsubmittedBatch(batch, std::current_exception());
                }
                else
                    ReleaseBatchStorage(storage);
            }
            if (state)
                ReleaseState(state);
            DestroyGeneralContextWithoutCleanup(bc);
            throw;
        }
    }

    // ---------- IJobParallelForBatch ----------
    // ============================================================
    // 原生 Schedule 分段诊断（`ENTJOY_DIAG_NATIVE_SCHED=1`）
    //
    // 按段累加纳秒与次数，`Scheduler::Shutdown` 时打印一次（不新增导出、不改协议）；
    // 未启用时每段只有一次静态 bool 读，热路径零成本。
    //
    // 自带插桩税校准：每 entry 额外做一次相邻 Now()/Now() 对。读数是 `steady_clock::now()`
    //（Windows 下 QueryPerformanceCounter，~24 ns），每 entry 16 次调用在 1 µs 量级上不可忽略；
    // **净账**（raw − 16×calib）才是各段真实占比。
    // ============================================================
    namespace SchedPhase
    {
        std::atomic<uint64_t> g_sumNs[Count];
        std::atomic<uint64_t> g_calls[Count];
        std::atomic<uint64_t> g_entries;
        std::atomic<uint64_t> g_submitEntries;

        bool Enabled()
        {
            static const bool enabled = [] {
                const char* v = std::getenv("ENTJOY_DIAG_NATIVE_SCHED");
                return v != nullptr && v[0] == '1';
            }();
            return enabled;
        }

        void Dump()
        {
            if (!Enabled()) return;
            static const char* kNames[Count] = {
                "resolve(jcc+chunksize)",
                "ctx(acquire+fields)",
                "storage(Acquire)",
                "state(CreateState)",
                "state(field stores)",
                "tile fill+batch tail",
                "submit.ds+branch",
                "submit.acct(inside)",
                "submit.tokens(inside)",
                "submit.notify(inside)",
                "submit.other(outer)",
                "calib(1x Now())" };
            const uint64_t entries = g_entries.load(std::memory_order_relaxed);
            const uint64_t submitEntries = g_submitEntries.load(std::memory_order_relaxed);
            const uint64_t calibCalls = g_calls[Calib].load(std::memory_order_relaxed);
            const uint64_t calibNs = calibCalls ? g_sumNs[Calib].load(std::memory_order_relaxed) / calibCalls : 0;
            uint64_t sumAll = 0;
            for (int i = 0; i < SubmitOther; ++i) sumAll += g_sumNs[i].load(std::memory_order_relaxed);
            const double sumUs = sumAll / 1000.0;
            const double perUs = entries ? sumUs / (double)entries : 0.0;
            const double taxUs = (16.0 * static_cast<double>(calibNs)) / 1000.0;
            std::printf("[NPSCHED] ScheduleParallelForBatch: entries=%llu submit=%llu net=%.3f us/entry | 1xNow()=%.1f ns -> tax(16 calls)=%.3f us/entry\n",
                (unsigned long long)entries, (unsigned long long)submitEntries,
                perUs - taxUs, static_cast<double>(calibNs), taxUs);
            for (int i = 0; i < Count; ++i)
            {
                const uint64_t ns = g_sumNs[i].load(std::memory_order_relaxed);
                const uint64_t n = g_calls[i].load(std::memory_order_relaxed);
                std::printf("[NPSCHED]   %-24s calls=%llu total=%.1f us mean=%.3f us\n",
                    kNames[i], (unsigned long long)n, ns / 1000.0, n ? (ns / 1000.0) / (double)n : 0.0);
            }
            // SubmitOther 是包住 submit.acct/tokens/notify 的外层段（那三段在调用链内部自记），
            // 单独报余量；不计入上方 net 合计。
            const uint64_t otherNs = g_sumNs[SubmitOther].load(std::memory_order_relaxed)
                - g_sumNs[SubmitAcct].load(std::memory_order_relaxed)
                - g_sumNs[SubmitTokens].load(std::memory_order_relaxed)
                - g_sumNs[SubmitNotify].load(std::memory_order_relaxed);
            std::printf("[NPSCHED]   submit.other residual mean=%.3f us (negative=非 Schedule 调用者也进了 acct/tokens/notify)\n",
                entries ? (static_cast<double>(otherNs) / 1000.0) / (double)entries : 0.0);
            // State 池命中分布：判定是否常态走 new HandleState（若如此则修池是确定收益）。
            const uint64_t hit = g_statePoolHit.load(std::memory_order_relaxed);
            const uint64_t refill = g_statePoolRefill.load(std::memory_order_relaxed);
            const uint64_t brandNew = g_statePoolNew.load(std::memory_order_relaxed);
            const uint64_t total = hit + refill + brandNew;
            std::printf("[NPSCHED] CreateState: total=%llu | tls-hit=%llu(%.1f%%) pool-refill=%llu(%.1f%%) new=%llu(%.1f%%)\n",
                (unsigned long long)total,
                (unsigned long long)hit, total ? 100.0 * (double)hit / (double)total : 0.0,
                (unsigned long long)refill, total ? 100.0 * (double)refill / (double)total : 0.0,
                (unsigned long long)brandNew, total ? 100.0 * (double)brandNew / (double)total : 0.0);
            const uint64_t recycled = g_stateRecycled.load(std::memory_order_relaxed);
            const uint64_t recycledOnWorker = g_stateRecycledOnWorker.load(std::memory_order_relaxed);
            std::printf("[NPSCHED] RecycleState: total=%llu (create=%llu, gap=%lld) | on-worker=%llu(%.1f%%) | sizeof(HandleState)=%llu B\n",
                (unsigned long long)recycled, (unsigned long long)total,
                (long long)total - (long long)recycled,
                (unsigned long long)recycledOnWorker,
                recycled ? 100.0 * (double)recycledOnWorker / (double)recycled : 0.0,
                (unsigned long long)sizeof(HandleState));
            std::printf("[NPSCHED] State thread slots (create | recycle):");
            for (size_t i = 0; i < kStateThreadSlots; ++i)
                std::printf(" [%zu]%llu|%llu", i,
                    (unsigned long long)g_stateCreateByThread[i].load(std::memory_order_relaxed),
                    (unsigned long long)g_stateRecycleByThread[i].load(std::memory_order_relaxed));
            std::printf("\n");
            std::fflush(stdout);
        }
    }

    // Schedule 一律异步提交。
    JobHandle Scheduler::ScheduleParallelForBatch
    (void (*func)(void*, int, int), void* context, int length, int batchSize, void (*cleanup)(void*), const JobHandle& dependency,
     uint32_t claimGeomApi)
    {
        const bool spDiag = SchedPhase::Enabled();
        uint64_t spMarks[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };
        if (spDiag) spMarks[0] = MonotonicNowNs();
        if (g_shuttingDown.load(std::memory_order_acquire))
            return MakeCompletedAfterCleanup(cleanup, context);
        ConsumeLongBatchBarriers();
        if (!func || length <= 0)
            return MakeCompletedAfterCleanup(cleanup, context);
        // batchSize<0 = 强制异步；INT_MIN 没有可表示的 int 绝对值，直接拒绝该输入。
        if (batchSize == (std::numeric_limits<int>::min)())
            return MakeCompletedAfterCleanup(cleanup, context);
        int reqBatch = batchSize < 0 ? -batchSize : batchSize;
        // JobCostCache：hash 在 ResolveChunkSize 前算；显式 batchSize（reqBatch>0）时用户意图优先。
        // `ENTJOY_FORCE_INNER_BATCH`（默认关）：把 auto（reqBatch==0）强制成显式内批并跳过 JCC。
        // `ENTJOY_JOB_BATCH_TABLE`（默认关）：按 funcHash **逐 job** 给内批，命中优先于全局强制（见 JobSystemInternal.h）。
        const bool autoBatch = (reqBatch <= 0);
        const bool tableOn = autoBatch && (g_jobBatchTableCount > 0);
        // 表键 = 内核在**其所属模块**内的 RVA（跨进程稳定；指针值本身受 ASLR 影响，故不用 HashFuncPtr）。
        // dump 制表（表为空）时也要算 ⇒ 把 dump 旗标并进 needKey。默认档（表空 + dump 关）不算。
        const bool needKey = autoBatch && (tableOn || g_jobBatchTableDump);
        const uint32_t tableKey = needKey
            ? JobFuncKey(reinterpret_cast<void (*)() noexcept>(func)) : 0u;
        const uint32_t tableBatch = tableOn ? LookupJobBatch(tableKey) : 0u;
        // 表项第三字段 = 按 job 的认领上限覆盖（0 = 不覆盖）。
        const uint32_t tableClaim = tableOn ? LookupJobClaim(tableKey) : 0u;
        // 表项第三字段的 **`e<N>` 形态 = 该调用点声明的元素跨度**（0 = 未声明）。
        const uint32_t tableSpan = tableOn ? LookupJobSpan(tableKey) : 0u;
        // 表项**第四字段** = 该**调用点**声明的**认领几何**（0 = Auto ⇒ 全局 env / F6 学习）。
        const uint32_t tableGeom = tableOn ? LookupJobGeom(tableKey) : 0u;
        // **代码里的调用点声明**（C# `Schedule(..., ClaimPolicy)` → 新导出）。
        //   优先级：批表（诊断覆盖）> API 声明 > F6 学习 > 全局 env > Adjacent。
        const uint32_t declareGeom = (tableGeom != kClaimGeomAuto) ? tableGeom : claimGeomApi;
        const uint32_t forced = (tableBatch > 0)
            ? tableBatch : (autoBatch ? g_forceInnerBatch : 0u);
        // 学习键（`jccEnabled && reqBatch<=0 && forced==0`）。
        // F6：内批被**表/强制档钉住**时也保留学习键 —— 内批仍由表决定（`cs = forced`），
        //   学习键只喂"每元素成本"EWMA 与由它派生的**认领几何**，不参与 batch 选择。
        const bool jccEnabled = g_jobCostCacheEnabled.load(std::memory_order_relaxed);
        const uint32_t funcHash = (!jccEnabled || !autoBatch) ? 0u
            : ((forced == 0)
                ? HashFuncPtr(reinterpret_cast<void (*)() noexcept>(func))
                : (g_claimAdaptiveEnabled
                    ? (tableKey != 0 ? tableKey
                                     : JobFuncKey(reinterpret_cast<void (*)() noexcept>(func)))
                    : 0u));
        bool jccFine = false;
        int cs = std::max(1, reqBatch > 0 ? reqBatch
                          : (forced > 0 ? static_cast<int>(forced)
                                        : ResolveChunkSize(length, 0, funcHash, &jccFine)));
        int rc = CeilDiv(length, cs);
        if (g_jobBatchTableDump) NoteJobBatchTableHash(tableKey, length, rc, forced, tableGeom, tableSpan);
        if (spDiag) spMarks[1] = MonotonicNowNs();
        // 单批次任务：走按依赖排序的池任务（异步）。
        if (rc <= 1)
            return ScheduleFastPath([func, context, length]() { func(context, 0, length); }, context, cleanup, dependency);

        const uint32_t targetWorkers = static_cast<uint32_t>(
            ResolveWorkerTarget(0, rc));
        auto* bc = AcquireGeneralBatchContext();
        *bc = GeneralBatchContext{ nullptr, func, context, cleanup };
        bc->funcHash = funcHash;
        // General 路径：guided 按工作量切 tile（可变代价 job 负载均衡）。
        const bool guided = g_guidedEnabled.load(std::memory_order_relaxed) != 0;
        const int guidedK = g_guidedK.load(std::memory_order_relaxed);
        const int guidedFloor = g_guidedFloor.load(std::memory_order_relaxed);
        const int tileCount = guided
            ? GuidedTileCount(length, static_cast<int>(targetWorkers),
                guidedK, guidedFloor)
            : rc;
        if (spDiag) spMarks[2] = MonotonicNowNs();
        BatchStorage* storage = nullptr;
        HandleState* state = nullptr;
        try
        {
            // 只对**薄 tile**（每 tile 元素数 `cs ≤ kClaimSpanThinElems`）启用"不物化 tileBuffer + 每批快照
            // 快路径"；厚 tile 逐位走旧路径（`tiles[]` 带 `PrefetchNextTileData` ⇒ **物化是对的**）。
            // ⚠ 但这条判据**名不副实**：`kClaimSpanThinElems = 16` 恰好等于 `ResolveChunkSize` 各返回路径
            //   `std::max(16, …)` 的硬编码下限 ⇒ `cs <= 16` 实际是"**JCC 顶在下限上**"，与"tile 厚薄"无关，
            //   且"进不进该 regime"随进程翻。当前判定为**良性**（短调度 tileCount 本来就小，O(tileCount) 代价小）；
            //   若要真正的"薄 tile"判据，必须换成**相对**口径（如把 `cs` 与 `length/W` 比较，或用 `rc`）。
            const bool thinTiles = !guided && static_cast<uint32_t>(cs) <= kClaimSpanThinElems;
            const bool uniformTiles = g_uniformTilesEnabled && thinTiles;
            // 等宽时**不申请 tile 缓冲**（只要 batch 对象）⇒ 消掉 O(tileCount) 物化与 tile 数组流量。
            storage = AcquireBatchStorage(uniformTiles ? 0u : static_cast<uint32_t>(tileCount));
            if (spDiag) spMarks[3] = MonotonicNowNs();
            auto* batch = &storage->batch;
            state = CreateState(false); batch->handle = state;
            if (spDiag) spMarks[4] = MonotonicNowNs();
            batch->context = bc; batch->cleanup = [](void* ctx) { CleanupGeneralContext(ctx); };
            batch->executeTile = &GeneralExecuteTile;
            // 薄 tile 才生效：把每-tile 的 trace/timing/firstTileAt 固定开销提到每批/每令牌。
            // 厚 tile 下 `AcquireBatchStorage` 快照的 g_tileFastPath 结果被这里覆盖为 false ⇒ 逐位不变。
            batch->tileFast = g_tileFastPath && thinTiles;
            // 本批是否真的拿到"每批快照"快路径（`ENTJOY_TILE_FASTPATH` 的生效证据）。
            if (batch->tileFast) g_tileFastApplied.fetch_add(1, std::memory_order_relaxed);
            batch->claimCapOverride = tableClaim;   // 按 job 的认领上限（0 = 不覆盖）
            batch->claimSpanOverride = tableSpan;   // 按调用点声明的**元素跨度**（0 = 不声明）
            batch->claimGeomOverride = declareGeom;   // 调用点声明的认领几何（0 = Auto）
            // 认领几何生效证据：按**声明值**分桶 —— 传了 `ClaimPolicy.Spread` 就必须看到 spread>0。
            if (declareGeom == kClaimGeomSpread)
                g_claimGeomDeclSpread.fetch_add(1, std::memory_order_relaxed);
            else if (declareGeom == kClaimGeomAdjacent)
                g_claimGeomDeclAdjacent.fetch_add(1, std::memory_order_relaxed);
            else
                g_claimGeomDeclAuto.fetch_add(1, std::memory_order_relaxed);
            // 认领几何按 job 定（判据 = JCC 学到的每元素成本 + 迟滞，见 JobCostCache.h）。
            //   `funcHash == 0`（表/强制档，或 JCC 关）⇒ 无样本 ⇒ 回退全局 env。
            batch->funcHash = funcHash;
            // 每-job 分母：索引在**提交线程**解析一次写进批（worker 只按索引累加 ⇒ 无碰撞混行）。
            // 键必须用 `JobFuncKey`（批表/jobkeys.txt 同一键空间），**不是** `funcHash`
            // （后者在"表 + CLAIM_ADAPT=0"时有意为 0 ⇒ 用它会正好让本仪器在对齐档里失效）。
            int unkeyedReason = -1;
            batch->perKeyIndex = JobPerKeyResolve(reinterpret_cast<void (*)() noexcept>(func), unkeyedReason);
            bc->perKeyIndex = batch->perKeyIndex;   // worker 侧从 **context** 读（不做哈希查找）
            if (batch->perKeyIndex >= 0)
            {
                const uint32_t pi = static_cast<uint32_t>(batch->perKeyIndex);
                g_perKeyBatches[pi].fetch_add(1, std::memory_order_relaxed);
                g_perKeyElems[pi].fetch_add(static_cast<uint64_t>(length), std::memory_order_relaxed);
                g_perKeyTiles[pi].fetch_add(static_cast<uint64_t>(tileCount), std::memory_order_relaxed);
                // 逐键归因薄批来源：该门已证明等价于"length 够短"，故这一列读出的是**短调度分布**
                //（配 `tiles` 可反查每批平均 cs）。
                if (thinTiles) g_perKeyThin[pi].fetch_add(1, std::memory_order_relaxed);
            }
            else if (unkeyedReason >= 0)
            {
                // 未归因：按原因 + 长度量级记账，使"未归因的薄批"可被指名（而不是只看见一个总数）。
                g_unkeyedBatches[unkeyedReason].fetch_add(1, std::memory_order_relaxed);
                g_unkeyedLenBucket[unkeyedReason][Log2Bucket(static_cast<uint64_t>(length < 0 ? 0 : length))]
                    .fetch_add(1, std::memory_order_relaxed);
                if (thinTiles) g_unkeyedThin[unkeyedReason].fetch_add(1, std::memory_order_relaxed);
            }
            batch->jccFine = jccFine;
            batch->totalElements = static_cast<uint32_t>(length);
            batch->tileCount = static_cast<uint32_t>(tileCount);
            batch->nextTile.store(0, std::memory_order_relaxed);
            batch->tilesRemaining.store(batch->tileCount, std::memory_order_relaxed);
            if (spDiag) spMarks[5] = MonotonicNowNs();
            if (uniformTiles)
            {
                // 不物化 —— worker 侧按 tileIndex 算术推导（JobSystem_Tiles.cpp 的 TryExecuteOneTile）。
                batch->uniformTileSize = static_cast<uint32_t>(cs);
                // 本批走了"不物化 tileBuffer"（`ENTJOY_TILES_UNIFORM` 的生效证据，与 F4 同一条 thinTiles 判据）。
                g_uniformTilesApplied.fetch_add(1, std::memory_order_relaxed);
                batch->tiles = nullptr;
            }
            else if (guided)
            {
                BuildGuidedTiles(storage->tileBuffer, length,
                    static_cast<int>(targetWorkers),
                    guidedK, guidedFloor);
                batch->tiles = storage->tileBuffer;
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
                batch->tiles = storage->tileBuffer;
            }
            // 唤醒多少个工作者 = 由预估工作量决定，再按物理核封顶（小 job）；tile 布局保持不变。
            batch->workerCount = static_cast<uint32_t>(
                ApplyPhysCoreCapForSmallJob(
                    ResolveWorkerTarget(0, rc),
                    batch->tileCount, length));
            // 切片认领的段游标必须在 publish 之前初始化（发布后 worker 会立刻执行）。
            InitSliceCursors(batch, ResolveClaimSliced(batch, true));
            JccDiagNoteWorkers(batch->workerCount);
            batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

            PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);
            if (spDiag) spMarks[6] = MonotonicNowNs();

            auto* ds = dependency.State();
            if (spDiag) spMarks[7] = MonotonicNowNs();
            if (!ds || ds->completed.load(std::memory_order_acquire))
            {
                try { SubmitOrPending(batch); }
                catch (...) { AbortUnsubmittedBatch(batch, std::current_exception()); }
            }
            else
            {
                AcquireState(state);
                RetainDependency(state, ds);
                try
                {
                    AddContinuationOrRunNow(ds, [state, batch]() {
                        try { SubmitBatch(batch); }
                        catch (...) { AbortUnsubmittedBatch(batch, std::current_exception()); }
                        ReleaseState(state);
                    });
                }
                catch (...)
                {
                    AbortUnsubmittedBatch(batch, std::current_exception());
                    ReleaseState(state);
                }
            }
            if (spDiag)
            {
                const uint64_t tEnd = MonotonicNowNs();
                const uint64_t c0 = MonotonicNowNs();
                const uint64_t c1 = MonotonicNowNs();
                SchedPhase::Add(SchedPhase::Resolve, spMarks[1] - spMarks[0]);
                SchedPhase::Add(SchedPhase::Ctx, spMarks[2] - spMarks[1]);
                SchedPhase::Add(SchedPhase::Storage, spMarks[3] - spMarks[2]);
                SchedPhase::Add(SchedPhase::StateCreate, spMarks[4] - spMarks[3]);
                SchedPhase::Add(SchedPhase::StateFields, spMarks[5] - spMarks[4]);
                SchedPhase::Add(SchedPhase::Tile, spMarks[6] - spMarks[5]);
                SchedPhase::Add(SchedPhase::SubmitDs, spMarks[7] - spMarks[6]);
                // SubmitAcct / SubmitPush 由 SubmitBatch 内部自记（嵌套在本段内）；
                // 本段余量（依赖分支尾部、句柄构造、异常门控）记入 SubmitOther。
                SchedPhase::Add(SchedPhase::SubmitOther, tEnd - spMarks[7]);
                SchedPhase::Add(SchedPhase::Calib, c1 - c0);
                SchedPhase::g_entries.fetch_add(1, std::memory_order_relaxed);
            }
            return JobHandle(state);
        }
        catch (...)
        {
            // RAII 兜底：构造阶段异常只释放 native wrapper/storage/state；原始 context
            // 仍由调用方拥有，避免 C++ cleanup 后 C# 异常路径再次 cleanup。
            if (storage)
            {
                auto* batch = &storage->batch;
                if (batch->handle)
                {
                    batch->context = nullptr;
                    batch->cleanup = nullptr;
                    AbortUnsubmittedBatch(batch, std::current_exception());
                }
                else
                    ReleaseBatchStorage(storage);
            }
            if (state)
                ReleaseState(state);
            DestroyGeneralContextWithoutCleanup(bc);
            throw;
        }
    }

    // ---------- ScheduleChunkBatchCore ----------
    static JobHandle ScheduleChunkBatchCore(
        void (*func)(void*, const ChunkJobData*), void (*rangeFunc)(void*, const ChunkJobData*, int, int),
        void (*entityRangeFunc)(void*, const EntityBatchData*, int, int),
        void* context, void (*cleanup)(void*),
        const ChunkJobData* chunks, const EntityBatchData* batches,
        int itemCount, const JobHandle& dependency,
        ChunkScheduleMode mode, int workerCap, int rangeSize, EcsJobKind jobKind,
        uint32_t unitGeneration)
    {
        if (g_shuttingDown.load(std::memory_order_acquire))
            return MakeCompletedAfterCleanup(cleanup, context);
        ConsumeLongBatchBarriers();
        if ((!func && !rangeFunc && !entityRangeFunc) || itemCount <= 0)
            return MakeCompletedAfterCleanup(cleanup, context);
        // 依赖未完成时不得 inline —— 小任务也走异步提交（由依赖完成触发）。
        const bool depOk = !dependency.State() || dependency.IsCompleted();

        // 按工作量与 worker 数选执行范围；物理 16KiB chunk 仅是存储单位。
        const int provisionalWorkers = ResolveWorkerTarget(workerCap, itemCount);
        int rs = rangeSize > 0
            ? rangeSize
            : ResolveEcsBatchRangeSize(itemCount, provisionalWorkers);
        // IJobChunk/IJobEntity 共用 EntityBatchData，jobKind 显式保留以支持独立策略。
        int rc = CeilDiv(itemCount, rs);

        // ImmediateNative：Run 直执语义——主线程同步执行，零 worker 唤醒。
        if (depOk && mode == ChunkScheduleMode::ImmediateNative)
        {
            auto* st = CreateState(true);
            const uint64_t diagId = AssignStateDiagnosticId(st);
            if (StatsEnabled())
                g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            RecordPublishedJob(diagId, 1);
            if (func) RunSyncJob(st, [&]() { for (int i = 0; i < itemCount; i++) func(context, &chunks[i]); });
            else if (rangeFunc) RunSyncJob(st, [&]() { rangeFunc(context, chunks, 0, itemCount); });
            else if (entityRangeFunc) RunSyncJob(st, [&]() { entityRangeFunc(context, batches, 0, itemCount); });
            if (cleanup)
            {
                try
                {
                    cleanup(context);
                }
                catch (...)
                {
                    RecordStateException(st, std::current_exception());
                }
            }
            return JobHandle(st);
        }

        ChunkBatchContext* cc = AcquireChunkBatchContext();
        *cc = ChunkBatchContext{ func, rangeFunc, entityRangeFunc, context, cleanup, chunks, batches };
        BatchStorage* storage = nullptr;
        HandleState* state = nullptr;
        try
        {

        // ── 实体数衡 tile ──：按每 unit(chunk/batch) 存活实体数前向扫描切块（约 targetEnt 实体/块），
        // 消除满/半满/空 chunk 混排时的负载失衡。
        const TileKind tileKind = func
            ? TileKind::ChunkCallbacks
            : (rangeFunc ? TileKind::ChunkRange : TileKind::EntityBatchRange);
        const int targetWorkers = ResolveWorkerTarget(workerCap, rc);
        // ── tile 布局缓存：同 key 下划分确定不变 → 跨 job 共享（同 query 只扫一次）；
        //    未命中才扫描构建（含 totalEntities 供 JCC 判重）。
        const void* tileKeyPtr = chunks != nullptr ? static_cast<const void*>(chunks)
                                                   : static_cast<const void*>(batches);
        uint32_t tileCount = 0;
        int64_t totalEntities = 0;
        // tile 布局的 bounds 是**每次提交都会拷一份**的大数组（tileCount+1 ≈ 60+ 项）。
        // 用 thread_local 复用容量 ⇒ 缓存命中路径不再有 malloc/free（只剩锁内一次 memcpy）。
        // 语义不变：本函数是唯一使用者，且每次进入都 clear()（容量保留、size 归零）。
        static thread_local std::vector<uint32_t> tileBounds;
        tileBounds.clear();
        const bool tileHit = tileKeyPtr != nullptr &&
            TileLayoutTryGet(tileKeyPtr, itemCount, workerCap, rangeSize, unitGeneration,
                tileCount, totalEntities, tileBounds);
        if (!tileHit)
        {
            for (int i = 0; i < itemCount; ++i) totalEntities += UnitEntityCount(cc, tileKind, i);
            const int targetEnt = ResolveEcsEntityTileTarget(totalEntities, targetWorkers);
            tileCount = static_cast<uint32_t>(
                BuildEntityBalancedTiles(nullptr, cc, tileKind, itemCount, targetEnt));
            tileBounds.assign(tileCount + 1, static_cast<uint32_t>(itemCount));
            tileBounds[0] = 0;
            long acc2 = 0;
            int bi = 1;
            for (int u = 0; u < itemCount && bi <= (int)tileCount; ++u)
            {
                acc2 += UnitEntityCount(cc, tileKind, u);
                if (acc2 >= targetEnt || u + 1 == itemCount)
                {
                    tileBounds[bi++] = static_cast<uint32_t>(u + 1);
                    acc2 = 0;
                }
            }
            if (tileKeyPtr)
                TileLayoutStore(tileKeyPtr, itemCount, workerCap, rangeSize, unitGeneration,
                    totalEntities, tileCount, tileBounds);
        }

        storage = AcquireBatchStorage(tileCount);
        auto* batch = &storage->batch;
        state = CreateState(false); batch->handle = state;
        batch->context = cc; batch->cleanup = &CleanupChunkContext;
        batch->diagnosticId = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;

        {
            auto* tiles = storage->tileBuffer;
            for (uint32_t i = 0; i < tileCount; ++i)
            {
                tiles[i].kind = tileKind;
                tiles[i].firstItem = tileBounds[i];
                tiles[i].itemCount = tileBounds[i + 1] - tileBounds[i];
            }
            batch->executeTile = &ChunkExecuteTile;
            batch->tiles = tiles;
            batch->tileCount = tileCount;
            batch->nextTile.store(0, std::memory_order_relaxed);
            batch->tilesRemaining.store(tileCount, std::memory_order_relaxed);
            batch->workerCount = static_cast<uint32_t>(targetWorkers);
            // 切片认领的段游标必须在 publish 之前初始化。
            // ⚠ chunk/entity 路的 tile 是**实体均衡**的（非等宽）⇒ 不参与 F6 的按-job 几何学习，
            //   仍沿用全局 env 的回退值。
            InitSliceCursors(batch, ResolveClaimSliced(batch, false));
            JccDiagNoteWorkers(batch->workerCount);
        }

        PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

        auto* ds = dependency.State();
        if (!ds || ds->completed.load(std::memory_order_acquire))
        {
            try { SubmitOrPending(batch); }
            catch (...) { AbortUnsubmittedBatch(batch, std::current_exception()); }
        }
        else
        {
            AcquireState(state);
            RetainDependency(state, ds);
            try
            {
                AddContinuationOrRunNow(ds, [state, batch, workerCap]() {
                    try { SubmitBatch(batch, workerCap); }
                    catch (...) { AbortUnsubmittedBatch(batch, std::current_exception()); }
                    ReleaseState(state);
                });
            }
            catch (...)
            {
                AbortUnsubmittedBatch(batch, std::current_exception());
                ReleaseState(state);
            }
        }
        return JobHandle(state);
        }
        catch (...)
        {
            // RAII 兜底：构造阶段异常只释放 native wrapper/storage/state；原始 context
            // 仍由调用方拥有，避免 C++ cleanup 后 C# 异常路径再次 cleanup。
            if (storage)
            {
                auto* batch = &storage->batch;
                if (batch->handle)
                {
                    batch->context = nullptr;
                    batch->cleanup = nullptr;
                    AbortUnsubmittedBatch(batch, std::current_exception());
                }
                else
                    ReleaseBatchStorage(storage);
            }
            if (state)
                ReleaseState(state);
            DestroyChunkContextWithoutCleanup(cc);
            throw;
        }
    }

    JobHandle Scheduler::ScheduleChunks(void (*f)(void*, const ChunkJobData*), void* ctx, void (*cl)(void*),
        const ChunkJobData* chunks, int cc, const JobHandle& dep, ChunkScheduleMode mode, int wc, int rs, uint32_t unitGeneration)
    { return ScheduleChunkBatchCore(f, nullptr, nullptr, ctx, cl, chunks, nullptr, cc, dep, mode, wc, rs, EcsJobKind::Chunk, unitGeneration); }

    JobHandle Scheduler::ScheduleChunkRanges(void (*f)(void*, const ChunkJobData*, int, int), void* ctx, void (*cl)(void*),
        const ChunkJobData* chunks, int cc, const JobHandle& dep, ChunkScheduleMode mode, int wc, int rs, uint32_t unitGeneration)
    { return ScheduleChunkBatchCore(nullptr, f, nullptr, ctx, cl, chunks, nullptr, cc, dep, mode, wc, rs, EcsJobKind::Chunk, unitGeneration); }

    JobHandle Scheduler::ScheduleEntityBatches(void (*f)(void*, const EntityBatchData*, int, int), void* ctx, void (*cl)(void*),
        const EntityBatchData* batches, int bc, const JobHandle& dep, ChunkScheduleMode mode, int wc, int rs, EcsJobKind jobKind, uint32_t unitGeneration)
    { return ScheduleChunkBatchCore(nullptr, nullptr, f, ctx, cl, nullptr, batches, bc, dep, mode, wc, rs, jobKind, unitGeneration); }


} // namespace JobSystem
