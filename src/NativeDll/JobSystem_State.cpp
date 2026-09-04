#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"
#include "CpuPause.h"

#include <algorithm>
#include <chrono>
#include <stdexcept>
#include <thread>
#include <utility>

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#endif

namespace JobSystem
{
    // ---------- State lifecycle ----------
    // 无锁 continuation 节点：fn 完整构造后才 CAS 入原子槽（无发布竞态）。
    // CompleteState 摘取后执行并 delete。槽位 ≤1 节点，CAS 只对 nullptr 比较，
    // 无 Treiber 栈的 ABA 问题（不会拿陈旧节点指针做比较）。
    struct ContinuationNode {
        std::function<void()> fn;
        ContinuationNode* next{ nullptr };
    };

    // 执行并释放一条 continuation 链（含单个节点）；异常吞掉。
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
        // 释放依赖链持有引用（依赖 state 可能仍被自身 batch 持有，不会悬垂）。
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
        {
            std::lock_guard<std::mutex> lock(state->exceptionMutex);
            state->batchExceptionPtr = nullptr;
        }
        state->diagnosticBatchId.store(0, std::memory_order_relaxed);
        state->completed.store(false, std::memory_order_relaxed);
        state->backendRetired.store(true, std::memory_order_relaxed);
        state->refCount.store(1, std::memory_order_relaxed);
        // Handles released after Shutdown cannot be reused by a later scheduler
        // generation. Destroy them directly instead of repopulating the pool
        // after Shutdown has already drained it.
        if (g_shuttingDown.load(std::memory_order_acquire))
        {
            delete state;
            return;
        }
        // 先入 per-thread 缓存；满额时一次性迁移共享池（一次锁 / 64 次回收）。
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
            // 从共享池批量补满线程缓存（一次锁 / 64 次创建），池空则 new。
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
        state->diagnosticBatchId.store(0, std::memory_order_relaxed);
        state->continuationSlot.store(nullptr, std::memory_order_relaxed);
        state->hasExtraContinuations.store(false, std::memory_order_relaxed);
        state->continuations.clear();
        state->dependency = nullptr;
        state->dependencies.clear();
        return state;
    }

    // 把依赖 state 挂到被依赖 state 上并持引用，保证传递协助链不会悬垂。
    // 释放点在 RecycleState（refcount 归零时）。仅在依赖未完成（需要等）时调用。
    void RetainDependency(HandleState* state, HandleState* dep) noexcept
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

    void RecordStateException(HandleState* state, std::exception_ptr exception) noexcept
    {
        if (!state || !exception) return;
        try
        {
            std::lock_guard<std::mutex> lock(state->exceptionMutex);
            if (!state->batchExceptionPtr)
                state->batchExceptionPtr = std::move(exception);
        }
        catch (...)
        {
            // Exception recording is best effort if allocation/locking itself
            // fails.  The worker must still reach its completion protocol.
        }
    }

    std::exception_ptr TakeStateException(HandleState* state) noexcept
    {
        if (!state) return {};
        try
        {
            std::lock_guard<std::mutex> lock(state->exceptionMutex);
            auto exception = state->batchExceptionPtr;
            state->batchExceptionPtr = nullptr;
            return exception;
        }
        catch (...)
        {
            return {};
        }
    }

    std::mutex g_longBatchBarrierMutex;
    std::vector<HandleState*> g_longBatchBarriers;
    thread_local HandleState* g_completingBatchState = nullptr;

    void RegisterLongBatchBarrier(HandleState* state) noexcept
    {
        if (!state || state->backendRetired.load(std::memory_order_acquire))
            return;
        AcquireState(state);
        std::lock_guard<std::mutex> lock(g_longBatchBarrierMutex);
        g_longBatchBarriers.push_back(state);
    }

    static void WaitBackendRetired(HandleState* state) noexcept;   // 定义见 Complete 段（含兜底唤醒看门狗）

    void ConsumeLongBatchBarriers() noexcept
    {
        std::vector<HandleState*> barriers;
        std::vector<HandleState*> deferred;
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
            WaitBackendRetired(state);   // defer 窗口补广播 + 等待退役
            ReleaseState(state);
        }
        if (!deferred.empty())
        {
            std::lock_guard<std::mutex> lock(g_longBatchBarrierMutex);
            g_longBatchBarriers.insert(
                g_longBatchBarriers.end(), deferred.begin(), deferred.end());
        }
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
        state->completedCv.notify_all();
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
        HandleState* state{ nullptr };
    };

    static void RunBackendAsync(void* raw) noexcept
    {
        auto* context = static_cast<BackendAsyncContext*>(raw);
        try
        {
            context->work();
        }
        catch (...)
        {
            // 正常路径由操作自身收尾完成；此为意外异常的兜底边界：保持句柄终结状态
            // 并释放在飞引用，避免 Complete() 永久阻塞。
            if (context->state)
            {
                RecordStateException(context->state, std::current_exception());
                try { CompleteState(context->state); } catch (...) {}
            }
        }
        if (context->state)
            ReleaseState(context->state);
    }

    static void CompleteBackendAsync(void* raw) noexcept
    {
        delete static_cast<BackendAsyncContext*>(raw);
    }

    bool SubmitBackendAsync(
        std::function<void()> work,
        HandleState* state,
        void (*failureCleanup)(void*),
        void* failureContext) noexcept
    {
        BackendAsyncContext* context = nullptr;
        try
        {
            auto scheduler = LoadChaseLevScheduler();
            if (!scheduler || !scheduler->IsRunning())
                throw std::runtime_error("JobSystem backend is not running");

            context = new BackendAsyncContext{ std::move(work), state };
            // 统一走 Chase-Lev SubmitWork：worker 异步执行，不阻塞调用线程。
            // SubmitWork 内部 PushTaskBackoff 有限退避，injector 满时短暂自旋。
            if (!scheduler->SubmitWork(
                    &RunBackendAsync, context, &CompleteBackendAsync))
            {
                CompleteBackendAsync(context);
                context = nullptr;
                throw std::runtime_error("JobSystem backend rejected asynchronous work");
            }
            // Ownership of context and the acquired state reference now belongs
            // to the queued RangeTask/RunBackendAsync wrapper.
            return true;
        }
        catch (...)
        {
            if (context)
                CompleteBackendAsync(context);
            if (state)
            {
                // 调用方在进入前已取得在飞引用，所有失败路径必须消费；清理先于发布
                // 完成执行，调用方不会观察到句柄已终结而 context 仍存活。
                RecordStateException(state, std::current_exception());
                if (failureCleanup)
                {
                    try
                    {
                        failureCleanup(failureContext);
                    }
                    catch (...)
                    {
                        RecordStateException(state, std::current_exception());
                    }
                }
                try { CompleteState(state); }
                catch (...) { RecordStateException(state, std::current_exception()); }
                ReleaseState(state);
            }
            else if (failureCleanup)
            {
                try { failureCleanup(failureContext); }
                catch (...) {}
            }
            return false;
        }
    }

    int ResolveChunkSize(int length, int requestedChunk)
    {
        return ResolveChunkSize(length, requestedChunk, 0);
    }

    int ResolveChunkSize(int length, int requestedChunk, uint32_t funcHash,
        bool* outJccFine)
    {
        if (outJccFine) *outJccFine = false;
        if (length <= 0) return 1;
        if (requestedChunk > 0) return requestedChunk;
        int wc = std::max(1, g_numThreads.load(std::memory_order_relaxed));

        // 自动 batch：仅当成本缓存开启且有该 job 成本数据时（热路径开销极小）。
        if (funcHash != 0 && g_jobCostCacheEnabled.load(std::memory_order_relaxed))
        {
            // ---- 带宽/延迟绑定自适应 ----
            // memory-bound job 总耗时由共享 DRAM 带宽主导，按元素成本推 tile 数会错标 →
            // 固定 tpw；compute-bound 走下方公式。两阶段学习：先采粗样本，再以粗成本
            // 为代理产细样本，TryClassify 定 mode（mem-bound / parallel）。
            const auto mode = g_jobCostCache.GetMode(funcHash);
            if (mode == JobSystem::kModeMemBound)
            {
                if (g_jobCostCacheVerbose)
                    std::printf("[JCC] R length=%d MEM-BOUND → tpw chunk\n", length);
                return std::max(16, CeilDiv(length, wc * g_configuredTilesPerWorker.load(std::memory_order_relaxed)));
            }
            const double perElemNs = g_jobCostCache.GetPerElemCost(funcHash);
            if (mode == JobSystem::kModeUnknown && !g_jobCostCache.HasLearnedCoarse(funcHash))
            {
                // 阶段 1：粗样本未齐 → tpw（perElemNs 通常为 0，本分支与兜底一致）
                return std::max(16, CeilDiv(length, wc * g_configuredTilesPerWorker.load(std::memory_order_relaxed)));
            }
            // 阶段 2（或 parallel 稳态）：细成本优先，缺省用粗成本代理（学习中/冷启动）
            double costNs = perElemNs;
            if (costNs <= 0.0) costNs = g_jobCostCache.GetCoarseCost(funcHash);
            if (costNs > 0.0)
            {
                constexpr double kTargetTileUs = 150.0;     // 目标每 tile 串行量
                constexpr int kMaxAdaptiveTpw = 16;         // tiles 上限 = workers×16
                constexpr int kMaxAutoChunk = 32768;        // 单 tile 最多 32k 元素
                constexpr double kSchedulingOverheadNs = 16000.0;  // ~16μs per tile
                const int chunkTpw4 = std::max(16, CeilDiv(length, wc * g_configuredTilesPerWorker.load(std::memory_order_relaxed)));

                // ── 两因子（C_fixed 每 tile 固定 + C_elem 每元素）优先 ──
                const double cfixed = g_jobCostCache.GetPerTileCost(funcHash);
                const double celem = perElemNs > 0.0 ? perElemNs : costNs;
                if (cfixed > 0.0 && celem > 0.0)
                {
                    // 空体/超轻：执行≈0，总成本由调度/唤醒/worker 抖动主导，
                    // 任何执行成本模型都无解 → tpw 兜底。
                    const double tileTimeTpw =
                        cfixed + (static_cast<double>(length) / (wc * g_configuredTilesPerWorker.load(std::memory_order_relaxed))) * celem;
                    if (tileTimeTpw < kSchedulingOverheadNs)
                    {
                        // 仍按"公式产出"登记细样本：使细/粗比值≈1 → mem-bound 分类 →
                        // 稳态固定 tpw，且细 EWMA 有值（JccConcurrentHeterogeneous 断言 perElem>0）。
                        if (outJccFine) *outJccFine = true;
                        return chunkTpw4;
                    }
                    // 执行主导：目标每 tile ≈150µs，tileSize = (target − C_fixed)/C_elem，
                    // 下限 256 元素/tile 防 C_fixed 占比过高。
                    if (outJccFine) *outJccFine = true;
                    double tileSize = (kTargetTileUs * 1000.0 - cfixed) / celem;
                    if (tileSize < 256.0) tileSize = 256.0;
                    int targetTiles = static_cast<int>(length / tileSize + 0.9999);
                    if (targetTiles < wc) targetTiles = wc;
                    if (targetTiles > wc * kMaxAdaptiveTpw) targetTiles = wc * kMaxAdaptiveTpw;
                    return std::max(1, CeilDiv(length, targetTiles));
                }

                // ── 单因子回退（冷启动，C_fixed 未学）：既有公式 ──
                if (outJccFine) *outJccFine = true;   // JCC 公式产出（细粒度学习样本）
                const double totalUs = length * costNs / 1000.0;
                // perElem 是「并行 wall 稀释」成本，直接用它算 tiles 会产出巨型 tile
                // 损失并行度；还原为「串行总量」：totalUs × wc ≈ 单 worker 串行所需时间。
                const double serialUs = totalUs * wc;
                double targetTilesD = std::clamp(serialUs / kTargetTileUs, 1.0,
                    static_cast<double>(wc) * kMaxAdaptiveTpw);
                int targetTiles = static_cast<int>(targetTilesD);
                if (targetTiles < 1) targetTiles = 1;
                // 安全护栏：单 tile 元素数上限（kMaxAutoChunk）。
                int floorTiles = CeilDiv(length, kMaxAutoChunk);
                if (floorTiles > wc) floorTiles = wc;
                if (targetTiles < floorTiles) targetTiles = floorTiles;
                int chunk = std::max(1, CeilDiv(length, targetTiles));
                // Floor：chunk 不比 tpw 兜底更粗，防止快 job 退化（tpw 冗余吸收 worker 抖动）。
                double tileTimeNs = costNs * chunkTpw4;
                bool schedulingDominated = (tileTimeNs < kSchedulingOverheadNs);
                double jccTiles = length * costNs * wc / (kTargetTileUs * 1000.0);
                bool loadBalancingOK = (jccTiles >= wc);
                if (!schedulingDominated || !loadBalancingOK) {
                    chunk = std::min(chunk, chunkTpw4);
                }
                if (g_jobCostCacheVerbose)
                    std::printf("[JCC] R length=%d perElem=%.2fns totalUs=%.1f serialUs=%.1f formula=%d floor=%d chunk=%d rc=%d\n",
                        length, costNs, totalUs, serialUs, (int)(serialUs / kTargetTileUs),
                        floorTiles, chunk, CeilDiv(length, chunk));
                return chunk;
            }
        }
        // 冷启动 / flag 关闭 / 无数据 → tpw 兜底：batch = N/(W×k) 随 N 自动缩放，
        // 无需每 job 标代价。
        return std::max(16, CeilDiv(length, wc * g_configuredTilesPerWorker.load(std::memory_order_relaxed)));
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

    // CpuPause() defined in CpuPause.h (unity build safe)

    // Chase-Lev 退役是异步的（completed 由最后 tile 设置，退役由最后 taskDone 触发）。
    // Complete() 返回前等 backendRetired，保证"Complete 后 batch 已完全退役"
    // （cleanup/存储回收已完成）——测试与用户代码依赖这一契约。
    // 存在未关闭 deferNotify 窗口时补广播（C++ Complete 不自动 Flush，
    // defer 窗口内提交的 job 可能无人唤醒，这里补一次）。
    static void WaitBackendRetired(HandleState* state) noexcept
    {
        if (!state) return;
        if (state->backendRetired.load(std::memory_order_acquire))
            return;
        if (g_submitDeferDepth.load(std::memory_order_relaxed) > 0)
        {
            if (auto scheduler = LoadChaseLevScheduler())
                scheduler->WakePending();
        }
        while (!state->backendRetired.load(std::memory_order_acquire))
            state->backendRetired.wait(false, std::memory_order_relaxed);
    }

    // C++ 异常协议：Complete 的每个退出点在等退役后调用；异常通过冷路径
    // mutex 一次性摘取，多个 Complete 调用不会并发读写 exception_ptr。
    static void RethrowBatchException(HandleState* state)
    {
        auto ex = TakeStateException(state);
        if (ex) std::rethrow_exception(ex);
    }

    void JobHandle::Complete() const
    {
        if (!_state) return;

        const uint64_t diagnosticId =
            _state->diagnosticBatchId.load(std::memory_order_acquire);
        if (diagnosticId != 0)
            PushTraceEvent(TraceEventType::CompleteEnter, diagnosticId, -1, 0, 0);

        if (_state->completed.load(std::memory_order_acquire))
        {
            WaitBackendRetired(_state);
            RethrowBatchException(_state);
            return;
        }

        // Chase-Lev 唯一路径：主线程不参与 tile 级协助计数（tiles 在持久
        // deque），直接进入 spin/wait，由 worker 完成退役并置 completed。

        // Phase 2: 先密集 spin（过早 yield 触发完整 OS 上下文切换）。
        // Chase-Lev：主线程 spin 期间即协助认领执行，消除"慢 worker 被抢占
        // + 主线程干等"的尾延迟。
        for (int i = 0; i < 2048; i++)
        {
            if (_state->completed.load(std::memory_order_acquire))
            {
                WaitBackendRetired(_state);
            RethrowBatchException(_state);
                return;
            }
            // Chase-Lev：spin 期间协助认领（每 16 次，更积极兜底慢 worker）
            if (g_mainThreadAssistEnabled.load(std::memory_order_relaxed) && (i & 15) == 0)
            {
                if (auto scheduler = LoadChaseLevScheduler(); scheduler && !scheduler->TryAssistOne()) { /* 无可认领，继续 spin */ }
            }
            CpuPause();
        }
        if (_state->completed.load(std::memory_order_acquire))
        {
            WaitBackendRetired(_state);
            RethrowBatchException(_state);
            return;
        }

        // Brief yield — let other threads run if the job is truly not done.
        std::this_thread::yield();

        // One more short spin after yielding.
        for (int i = 0; i < 256; i++)
        {
            if (_state->completed.load(std::memory_order_acquire))
            {
                WaitBackendRetired(_state);
            RethrowBatchException(_state);
                return;
            }
            if (g_mainThreadAssistEnabled.load(std::memory_order_relaxed) && (i & 15) == 0)
            {
                if (auto scheduler = LoadChaseLevScheduler(); scheduler && !scheduler->TryAssistOne()) { /* 无可认领 */ }
            }
            CpuPause();
        }
        if (_state->completed.load(std::memory_order_acquire))
        {
            WaitBackendRetired(_state);
            RethrowBatchException(_state);
            return;
        }

        // Phase 3: blocking wait with periodic 主线程协助。
        // 正常路径：worker 完成 → notify_all → 谓词满足立即唤醒；
        // Chase-Lev：主线程也参与认领执行，兜底"最后一片被 OS 抢占"的尾延迟。
        g_waitFallbacks.fetch_add(1, std::memory_order_relaxed);
        g_completeWaitLoops.fetch_add(1, std::memory_order_relaxed);
        constexpr auto kCompleteRevisit = std::chrono::microseconds(256); // 256µs 回访间隔（更快兜底）
        while (!_state->completed.load(std::memory_order_acquire))
        {
            // 先 assist 再 wait：避免干等 256µs 的停顿窗口；每轮最多 assist 16 次，
            // 防止链条级联时主线程无限 assist 不回查 completed。
            if (g_mainThreadAssistEnabled.load(std::memory_order_relaxed))
            {
                auto scheduler = LoadChaseLevScheduler();
                if (!scheduler) continue;
                for (int assistN = 0; assistN < 16; ++assistN)
                {
                    if (_state->completed.load(std::memory_order_acquire)) break;
                    if (!scheduler->TryAssistOne()) break;
                }
            }

            if (_state->completed.load(std::memory_order_acquire)) break;

            // 无事可做才 wait（短超时兜底）
            {
                std::unique_lock<std::mutex> lock(_state->mtx);
                if (!_state->completedCv.wait_for(lock, kCompleteRevisit,
                        [state = _state] { return state->completed.load(std::memory_order_acquire); }))
                {
                    // 超时：继续 assist 循环
                }
            }
        }
        WaitBackendRetired(_state);
            RethrowBatchException(_state);
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
        // 合成 state 持有每个父依赖的引用，保证传递协助链不悬垂；
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

} // namespace JobSystem
