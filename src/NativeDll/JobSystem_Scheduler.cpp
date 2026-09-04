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
        // 与 SubmitBatch 同语义：调度即"发布"（pool 执行窗口另由 FastPath 上报泳道）
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
    static bool ResolveWorkerAffinityEnabled() noexcept
    {
        // 默认关闭 CPU 亲和性：worker 交 OS 自由调度（避免 SMT 双线程死绑共享执行单元）。
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
            if (g_chaseLevScheduler && g_chaseLevScheduler->IsRunning()) return true;
            g_numThreads.store(resolved, std::memory_order_relaxed);
            g_workerAffinityEnabled.store(
                ResolveWorkerAffinityEnabled(), std::memory_order_relaxed);

            // 主线程 assist 默认关闭（纯 worker 模式）；JobSystem_SetMainThreadAssist(int) 可运行时开启。

            // 主线程钉到逻辑核 0，避免被共享 L1/L2 的 worker 抢占。
            if (g_workerAffinityEnabled.load(std::memory_order_relaxed))
                BindCurrentThreadToLogicalProcessor(0);

            // Chase-Lev 调度器（唯一路径）：持久 worker 线程 + per-worker deque + MPMC Injector
            g_chaseLevScheduler = std::make_unique<ChaseLevScheduler>();
            if (!g_chaseLevScheduler->Start(
                static_cast<uint32_t>(resolved),
                &ChaseLevExecuteTile,
                &ChaseLevTaskDone,
                g_workerAffinityEnabled.load(std::memory_order_relaxed)))
            {
                // worker 创建失败：回滚，避免「无 worker 但仍认为 Native 可用」。
                g_chaseLevScheduler.reset();
                g_shuttingDown.store(true, std::memory_order_release);
                return false;
            }

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
        // 线程防护：worker 线程调用 shutdown 会走到 ChaseLevScheduler::Stop 的 join 自身
        // → 永不返回死锁。非主线程调用直接拒绝（打印并返回，不执行）。
        if (g_mainThreadId != std::thread::id{} &&
            std::this_thread::get_id() != g_mainThreadId)
        {
            std::fprintf(stderr,
                "[JobSystem] Shutdown() called from non-main thread — rejected (would self-join deadlock).\n");
            return;
        }

        std::lock_guard<std::mutex> lifecycleLock(g_schedulerMutex);
        // Shutdown 幂等；停止/重置期间保持 gate，防止 Initialize 与 teardown 并发发布新一代。
        g_shuttingDown.store(true, std::memory_order_release);
        g_numThreads.store(0, std::memory_order_relaxed);
        // 关键：在 pending 锁内关闭隐式批，再 flush——否则「Schedule 读到 enabled=true → 暂停 →
        // Shutdown flush 空队列 → 调度线程继续入队」会把 batch 留在无人 flush 的队列，永久悬挂/泄漏。
        {
            std::lock_guard<std::mutex> lock(g_pendingBatchesMutex);
            g_implicitBatchEnabled.store(false, std::memory_order_release);
        }
        // 隐式批排空：执行 pending 中未发布的 job（worker 尚在运行）；未及执行者由 in-flight 兜底，不产生 UAF。
        FlushPendingSubmits();
        if (g_chaseLevScheduler)
        {
            g_chaseLevScheduler->Stop();
            // 释放 Stop 排空出的未退役 batch（cleanup + ReleaseBatch + ReleaseState），
            // 消除 shutdown 未完成 job 的 context 泄漏。
            for (auto* batch : g_chaseLevScheduler->drainedBatches)
                ForceFinalizeBatch(batch);
            g_chaseLevScheduler.reset();
        }
        ConsumeLongBatchBarriers();
        // 先把 main 线程缓存的 batch storage 交还共享池再清空；worker 已 join，其 thread_local 缓存已交还。
        FlushBatchStorageCacheToSharedPool();
        ClearBatchStoragePool();
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
        g_configuredTilesPerWorker.store(std::max(1, tilesPerWorker), std::memory_order_relaxed);
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
        const uint32_t funcHash = g_jobCostCacheEnabled.load(std::memory_order_relaxed)
            ? HashFuncPtr(reinterpret_cast<void (*)() noexcept>(func)) : 0;
        bool jccFine = false;
        int cs = ResolveChunkSize(length, batchSize, funcHash, &jccFine);
        int rc = CeilDiv(length, cs);
        if (rc <= 1) return ScheduleFastPath([func, context, length]() { for (int i = 0; i < length; i++) func(context, i); }, context, cleanup, dependency);

        const uint32_t targetWorkers = static_cast<uint32_t>(
            ResolveWorkerTarget(0, rc));
        auto* bc = new GeneralBatchContext{ func, nullptr, context, cleanup };
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
            storage = AcquireBatchStorage(static_cast<uint32_t>(tileCount));
            auto* batch = &storage->batch;
            state = CreateState(false); batch->handle = state;
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
                    static_cast<int>(targetWorkers), guidedK, guidedFloor);
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
    // Schedule 一律异步提交。
    JobHandle Scheduler::ScheduleParallelForBatch
    (void (*func)(void*, int, int), void* context, int length, int batchSize, void (*cleanup)(void*), const JobHandle& dependency)
    {
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
        const uint32_t funcHash = (g_jobCostCacheEnabled.load(std::memory_order_relaxed) && reqBatch <= 0)
            ? HashFuncPtr(reinterpret_cast<void (*)() noexcept>(func)) : 0;
        bool jccFine = false;
        int cs = std::max(1, reqBatch > 0 ? reqBatch : ResolveChunkSize(length, 0, funcHash, &jccFine));
        int rc = CeilDiv(length, cs);
        // 单批次任务：走按依赖排序的池任务（异步）。
        if (rc <= 1)
            return ScheduleFastPath([func, context, length]() { func(context, 0, length); }, context, cleanup, dependency);

        const uint32_t targetWorkers = static_cast<uint32_t>(
            ResolveWorkerTarget(0, rc));
        auto* bc = new GeneralBatchContext{ nullptr, func, context, cleanup };
        bc->funcHash = funcHash;
        // General 路径：guided 按工作量切 tile（可变代价 job 负载均衡）。
        const bool guided = g_guidedEnabled.load(std::memory_order_relaxed) != 0;
        const int guidedK = g_guidedK.load(std::memory_order_relaxed);
        const int guidedFloor = g_guidedFloor.load(std::memory_order_relaxed);
        const int tileCount = guided
            ? GuidedTileCount(length, static_cast<int>(targetWorkers),
                guidedK, guidedFloor)
            : rc;
        BatchStorage* storage = nullptr;
        HandleState* state = nullptr;
        try
        {
            storage = AcquireBatchStorage(static_cast<uint32_t>(tileCount));
            auto* batch = &storage->batch;
            state = CreateState(false); batch->handle = state;
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
                    static_cast<int>(targetWorkers),
                    guidedK, guidedFloor);
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
        // IJobChunk/IJobEntity 共用 EntityBatchData，jobKind 显式保留以支持独立策略；
        // useFineRanges 刻意禁用。
        int rc = CeilDiv(itemCount, rs);

        // ImmediateNative：Run 直执语义——主线程同步执行，零 worker 唤醒。
        if (depOk && mode == ChunkScheduleMode::ImmediateNative)
        {
            auto* st = CreateState(true);
            const uint64_t diagId = AssignStateDiagnosticId(st);
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

        ChunkBatchContext* cc = new ChunkBatchContext{ func, rangeFunc, entityRangeFunc, context, cleanup,
            chunks, batches };
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
        std::vector<uint32_t> tileBounds;
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
