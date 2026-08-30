#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"
#include "ThreadAffinity.h"
#include "JobDebuggerGUI.h"

#include <algorithm>
#include <cctype>
#include <cstdlib>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <windows.h>
#include <timeapi.h>
#pragma comment(lib, "winmm.lib")
#endif

namespace JobSystem
{
    // ============================================================
    // Schedule helpers
    // ============================================================
    // ── tile 布局缓存：同 (unitsPtr, itemCount, workerCap, rangeSize, unitGeneration) 下
    //    实体数衡 tile 划分完全确定 → 跨 job 共享（同 query 多 job 只扫一次）。
    //    只存值（tile 边界拷贝），不持有指针 → 无悬垂。
    namespace {
        struct TileLayoutEntry
        {
            const void* unitsPtr = nullptr;
            int itemCount = 0;
            int workerCap = 0;
            int rangeSize = 0;
            uint32_t unitGeneration = 0; // C# cache StructuralVersion：重建必变 → 防指针地址复用误命中
            long totalEntities = 0;      // 实体总量（JCC 前置判重成本估算用）
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
            uint32_t unitGeneration, uint32_t& outTileCount, long& outTotalEntities,
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
            uint32_t unitGeneration, long totalEntities, uint32_t tileCount,
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
            // 非 batch 快速路径异步窗口——work() 即 C# func 执行点，
            // 执行期间 set/clear 当前-batch 使异常按本 job 归属。
            const uint64_t id = state->diagnosticBatchId.load(std::memory_order_acquire);
            // 调试面板：pool 执行窗口上报到本 worker 泳道（WorkerLoop 已预分配索引）
            DebugBeginExec(id, 1, 1, false); // 快速路径 Job：单线程执行
            if (id != 0) SetCurrentBatchId(id);
            try { work(); }
            catch (...)
            {
                // C++ 异常协议：快速路径（pool 窗口）异常记录到 handle state，
                // Complete() 统一重抛——不再静默吞掉。
                if (state->batchExceptionPtr == nullptr)
                    state->batchExceptionPtr = std::current_exception();
            }
            if (id != 0) SetCurrentBatchId(0);
            DebugEndExec();
            if (cleanup) cleanup(ctx);
            CompleteState(state);
            ReleaseState(state);
        });
    }

    template <typename Work>
    JobHandle ScheduleFastPath(Work&& work, void* ctx, void (*cleanup)(void*), const JobHandle& dep)
    {
        auto* state = CreateState(false);
        const uint64_t id = AssignStateDiagnosticId(state);
        // 与 SubmitBatch 同语义：调度即"发布"（pool 执行窗口另由 FastPath 上报泳道）
        g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
        RecordPublishedJob(id, 1);
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
        // 默认关闭 CPU 亲和性：worker 由 OS 自由调度（SMT 机器上避免
        // 每物理核心 2 线程死绑共享执行单元；实测 AMD 8845H 8物理16逻辑
        // 关闭绑定 p99 0.83ms vs 绑定 1.0-1.2ms）。ENTJOY_WORKER_AFFINITY=1
        // 可显式开启（无 SMT / 独占机器场景）。
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
            int resolved;
            int envWorkers = 0;
            // 默认 worker 数 = 逻辑核心-1（Unity 式，GridSearch 100K 大任务吞吐最优）。
            // SMT 竞争由"自适应亲和（SMT→关闭绑定）"消化，无需限制 worker ≤ 物理核心
            // （实测 7 物理-1 worker p50 0.68 vs 15 worker 0.60，吞吐损失更明显）。
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
            if (g_chaseLevScheduler && g_chaseLevScheduler->IsRunning())
                return;
            g_numThreads = resolved;
            g_workerAffinityEnabled.store(
                ResolveWorkerAffinityEnabled(), std::memory_order_relaxed);

            // 主线程 assist 默认关闭（g_mainThreadAssistEnabled = false，纯 worker 模式）。
            // 运行时可通过 JobSystem_SetMainThreadAssist(int) 开启（兜底慢 worker 尾延迟）。
            // 不再读取 ENTJOY_ASSIST 环境变量（避免空字符串误启用）。

            // Pin the calling thread (main thread) to logical core 0 so it
            // is never preempted by a worker that shares its L1/L2 cache.
            if (g_workerAffinityEnabled.load(std::memory_order_relaxed))
                BindCurrentThreadToLogicalProcessor(0);

            // Chase-Lev 调度器（唯一路径）：持久 worker 线程 + per-worker deque + MPMC Injector
            g_chaseLevScheduler = std::make_unique<ChaseLevScheduler>();
            g_chaseLevScheduler->Start(
                static_cast<uint32_t>(resolved),
                &ChaseLevExecuteTile,
                &ChaseLevTaskDone,
                g_workerAffinityEnabled.load(std::memory_order_relaxed));

            // 若设置了 ENTJOY_DEBUG=1，启动 Dear ImGui 调试窗口
            JobDebuggerGUI::TryLaunch();
        }
    }

    void Scheduler::Shutdown()
    {
        g_shuttingDown.store(true, std::memory_order_release);
        {
            std::lock_guard<std::mutex> lock(g_schedulerMutex);
            g_numThreads = 0;
        }
        // 隐式批排空：提交 pending 中尚未发布的 job（worker 尚在运行，可正常执行/退役；
        // 未及执行者按现有 in-flight 泄漏兜底，不产生 UAF）。
        FlushPendingSubmits();
        if (g_chaseLevScheduler) { g_chaseLevScheduler->Stop(); g_chaseLevScheduler.reset(); }
        ConsumeLongBatchBarriers();
        // 近无锁：先把 main 线程缓存中的 batch storage 交还共享池，再统一清空。
        // worker 已由 chaseLevScheduler->Stop() join，其 thread_local 缓存已在退出时交还。
        FlushBatchStorageCacheToSharedPool();
        ClearBatchStoragePool();
        // 先把当前线程（main）缓存中的 state 交还共享池，再统一清空。
        // worker 线程已由 Stop() join，其 thread_local 缓存已在退出时交还，
        // 故此处清空覆盖全部 state。
        FlushStateCacheToSharedPool();
        { std::lock_guard<std::mutex> lock(g_statePoolMutex); for (auto* s : g_statePool) delete s; g_statePool.clear(); }
    }

    void Scheduler::PrewakeWorkers()
    {
        // C# 初始化经 GetProcAddress 解析此导出，保留为 no-op。
        // Chase-Lev worker 常驻 spin/futex，无需显式唤醒。
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

    // ---------- IJob ----------
    JobHandle Scheduler::Schedule(void (*func)(void*), void* context, void (*cleanup)(void*), const JobHandle& dependency)
    {
        if (g_shuttingDown.load(std::memory_order_acquire)) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if (!func) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        if (!dependency.State() || dependency.IsCompleted())
        {
            auto* st = CreateState(true);
            const uint64_t diag = AssignStateDiagnosticId(st);
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            RecordPublishedJob(diag, 1);
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
        // 依赖未完成时绝不 inline —— 必须先等依赖。阈值仅在 depOk（无依赖或依赖已完成）下生效。
        // 注意：大长度不得 inline 主线程——主线程逐元素 C# 回调 ~20ns/次 vs worker ~1ns/次，
        // 长 for 主线程执行慢 ~20 倍（实测 IJobFor·100K：98.7µs → 705µs）。单线程语义 =
        // 单 worker 执行，不是主线程 inline。
        if (depOk && (length <= kSyncWithCompletedDepThreshold))
        {
            auto* st = CreateState(true);
            const uint64_t diag = AssignStateDiagnosticId(st);
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            RecordPublishedJob(diag, 1);
            RunSyncJob(st, [func, context, length]() { for (int i = 0; i < length; i++) func(context, i); });
            if (cleanup) cleanup(context);
            return JobHandle(st);
        }
        if (length <= 64) return ScheduleFastPath([func, context, length]() { for (int i = 0; i < length; i++) func(context, i); }, context, cleanup, dependency);
        return ScheduleWithDependency(dependency, [func, context, length, cleanup](HandleState* state) {
            const uint64_t id = state->diagnosticBatchId.load(std::memory_order_acquire);
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            RecordPublishedJob(id, 1);
            AcquireState(state);
            SubmitBackendAsync([func, context, length, cleanup, state]() {
                // state 由 ScheduleWithDependency 分配诊断 id，异步窗口同样需要归属。
                const uint64_t id = state->diagnosticBatchId.load(std::memory_order_acquire);
                DebugBeginExec(id, 1, 1, false); // ScheduleFor（异步单任务）Job：单线程执行
                if (id != 0) SetCurrentBatchId(id);
                for (int i = 0; i < length; i++) func(context, i);
                if (id != 0) SetCurrentBatchId(0);
                DebugEndExec();
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
        if (depOk && (length <= kSyncWithCompletedDepThreshold))
        {
            auto* st = CreateState(true);
            const uint64_t diag = AssignStateDiagnosticId(st);
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            RecordPublishedJob(diag, 1);
            RunSyncJob(st, [func, context, length]() { for (int i = 0; i < length; i++) func(context, i); });
            if (cleanup) cleanup(context);
            return JobHandle(st);
        }
        // JobCostCache：funcPtr hash 在 ResolveChunkSize 之前计算（自适应分支需要）。
        // FastPath（rc<=1）不学成本（已收敛为 1 tile）；batch 路径退役时学到。
        const uint32_t funcHash = g_jobCostCacheEnabled.load(std::memory_order_relaxed)
            ? HashFuncPtr(reinterpret_cast<void (*)() noexcept>(func)) : 0;
        bool jccFine = false;
        int cs = ResolveChunkSize(length, batchSize, funcHash, &jccFine);
        int rc = (length + cs - 1) / cs;
        if (rc <= 1) return ScheduleFastPath([func, context, length]() { for (int i = 0; i < length; i++) func(context, i); }, context, cleanup, dependency);

        const uint32_t targetWorkers = static_cast<uint32_t>(
            ResolveWorkerTarget(0, rc));
        auto* bc = new GeneralBatchContext{ func, nullptr, context, cleanup };
        bc->funcHash = funcHash;
        // General 路径默认走"等量 tile"（而非 guided 大前小后）：配合批量认领既均衡又低争用。
        // （requires: ENTJOY_GUIDED=1/ConfigureGuided(1) 仍可显式启用 guided，供可变代价 job 使用；
        //  这里保持 g_guidedEnabled 读取，默认环境若开启则在其 5. 场景下按需 —— 见下方注释）
        const bool guided = g_guidedEnabled != 0;   // 开启 guided：按工作量（chunk∝剩余）切 tile，可变代价 job 负载均衡
        const int tileCount = guided
            ? GuidedTileCount(length, static_cast<int>(targetWorkers), g_guidedK, g_guidedFloor)
            : rc;
        auto* storage = AcquireBatchStorage(
            static_cast<uint32_t>(tileCount));
        auto* batch = &storage->batch;
        auto* state = CreateState(false); batch->handle = state;
        batch->context = bc; batch->cleanup = [](void* ctx) { CleanupGeneralContext(ctx); };
        batch->executeTile = &GeneralExecuteTile;
        batch->funcHash = funcHash;
        batch->jccFine = jccFine;
        batch->totalElements = static_cast<uint32_t>(length);
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
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitOrPending(batch); }
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
        if (!forceAsync && depOk && (length <= kSyncWithCompletedDepThreshold))
        {
            auto* st = CreateState(true);
            const uint64_t diag = AssignStateDiagnosticId(st);
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            RecordPublishedJob(diag, 1);
            RunSyncJob(st, [func, context, length]() { func(context, 0, length); });
            if (cleanup) cleanup(context);
            return JobHandle(st);
        }
        // JobCostCache：funcPtr hash 在 ResolveChunkSize 之前计算（自适应分支需要）。
        // 显式 batchSize（reqBatch>0）时用户意图优先，不参与自动 batch。
        const uint32_t funcHash = (g_jobCostCacheEnabled.load(std::memory_order_relaxed) && reqBatch <= 0)
            ? HashFuncPtr(reinterpret_cast<void (*)() noexcept>(func)) : 0;
        bool jccFine = false;
        int cs = std::max(1, reqBatch > 0 ? reqBatch : ResolveChunkSize(length, 0, funcHash, &jccFine));
        int rc = (length + cs - 1) / cs;
        if (!forceAsync && depOk && rc <= 1)
        {
            auto* st = CreateState(true);
            const uint64_t diag = AssignStateDiagnosticId(st);
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            RecordPublishedJob(diag, 1);
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
        bc->funcHash = funcHash;
        // General 路径：guided 按工作量切 tile（可变代价 job 负载均衡）。
        const bool guided = g_guidedEnabled != 0;
        const int tileCount = guided
            ? GuidedTileCount(length, static_cast<int>(targetWorkers), g_guidedK, g_guidedFloor)
            : rc;
        auto* storage = AcquireBatchStorage(
            static_cast<uint32_t>(tileCount));
        auto* batch = &storage->batch; auto* state = CreateState(false); batch->handle = state;
        batch->context = bc; batch->cleanup = [](void* ctx) { CleanupGeneralContext(ctx); };
        batch->executeTile = &GeneralExecuteTile;
        batch->funcHash = funcHash;
        batch->jccFine = jccFine;
        batch->totalElements = static_cast<uint32_t>(length);
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
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitOrPending(batch); }
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
        ChunkScheduleMode mode, int workerCap, int rangeSize, EcsJobKind jobKind,
        uint32_t unitGeneration)
    {
        if (g_shuttingDown.load(std::memory_order_acquire)) { if (cleanup) cleanup(context); return JobHandle(CreateState(true)); }
        ConsumeLongBatchBarriers();
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

        // Inline for sync / trivial work（依赖已完成/无依赖时；依赖未完成走异步提交）：
        // — ImmediateNative：主线程同步执行，零 worker 唤醒（Run 场景）
        // — rc<=1 && workerCap<=1：单批次小任务顺带同步执行
        if (depOk && (mode == ChunkScheduleMode::ImmediateNative || (rc <= 1 && workerCap <= 1)))
        {
            auto* st = CreateState(true);
            const uint64_t diagId = AssignStateDiagnosticId(st);
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            RecordPublishedJob(diagId, 1);
            if (func) RunSyncJob(st, [&]() { for (int i = 0; i < itemCount; i++) func(context, &chunks[i]); });
            else if (rangeFunc) RunSyncJob(st, [&]() { rangeFunc(context, chunks, 0, itemCount); });
            else if (entityRangeFunc) RunSyncJob(st, [&]() { entityRangeFunc(context, batches, 0, itemCount); });
            if (cleanup) cleanup(context);
            return JobHandle(st);
        }

        auto* cc = new ChunkBatchContext{ func, rangeFunc, entityRangeFunc, context, cleanup,
            chunks, batches };

        // ---- 实体数衡 tile ----：按每个 unit(chunk/batch) 的存活实体数前向扫描切块，让每块
        // 约含 targetEnt 个实体（而非固定 chunk 数），消除满/半满/空 chunk 混排时的负载失衡。
        // 先规划 worker 数（用 chunk 数的粗略 rc 即可，只作参与 worker 上限），再实体扫描定 tile。
        const TileKind tileKind = func
            ? TileKind::ChunkCallbacks
            : (rangeFunc ? TileKind::ChunkRange : TileKind::EntityBatchRange);
        const int targetWorkers = ResolveWorkerTarget(workerCap, rc);
        // ── tile 布局缓存：同 (unitsPtr, itemCount, workerCap, rangeSize, unitGeneration) 下
        //    实体数衡 tile 划分确定不变 → 跨 job 共享（同 query 多 job 只扫一次）。
        //    未命中才扫描构建并入缓存（含 totalEntities 供 JCC 判重）。
        const void* tileKeyPtr = chunks != nullptr ? static_cast<const void*>(chunks)
                                                   : static_cast<const void*>(batches);
        uint32_t tileCount = 0;
        long totalEntities = 0;
        std::vector<uint32_t> tileBounds;
        const bool tileHit = tileKeyPtr != nullptr &&
            TileLayoutTryGet(tileKeyPtr, itemCount, workerCap, rangeSize, unitGeneration,
                tileCount, totalEntities, tileBounds);
        if (!tileHit)
        {
            for (int i = 0; i < itemCount; ++i) totalEntities += UnitEntityCount(cc, tileKind, i);
            const int targetEnt = ResolveEcsEntityTileTarget(static_cast<int>(totalEntities), targetWorkers);
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

        auto* storage = AcquireBatchStorage(tileCount);
        auto* batch = &storage->batch;
        auto* state = CreateState(false); batch->handle = state;
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
        }

        PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

        auto* ds = dependency.State();
        if (!ds || ds->completed.load(std::memory_order_acquire)) { SubmitOrPending(batch); }
        else { AcquireState(state); RetainDependency(state, ds); AddContinuationOrRunNow(ds, [state, batch, workerCap]() { SubmitBatch(batch, workerCap); ReleaseState(state); }); }
        return JobHandle(state);
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
