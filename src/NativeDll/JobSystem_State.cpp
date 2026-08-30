#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"
#include "CpuPause.h"

#include <algorithm>
#include <chrono>
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
        state->diagnosticBatchId.store(0, std::memory_order_relaxed);
        state->completed.store(false, std::memory_order_relaxed);
        state->backendRetired.store(true, std::memory_order_relaxed);
        state->refCount.store(1, std::memory_order_relaxed);
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
            WaitBackendRetired(state);   // 复用兜底唤醒看门狗（条件触发 + 超时兜底）
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
    };

    static void RunBackendAsync(void* raw) noexcept
    {
        auto* context = static_cast<BackendAsyncContext*>(raw);
        try { context->work(); } catch (...) {}
    }

    static void CompleteBackendAsync(void* raw) noexcept
    {
        delete static_cast<BackendAsyncContext*>(raw);
    }

    void SubmitBackendAsync(std::function<void()> work)
    {
        auto* context = new BackendAsyncContext{ std::move(work) };
        // 统一走 Chase-Lev SubmitWork：worker 异步执行，不阻塞调用线程。
        // SubmitWork 内部 PushTaskBackoff 有限退避，injector 满时短暂自旋。
        g_chaseLevScheduler->SubmitWork(&RunBackendAsync, context, &CompleteBackendAsync);
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
        int wc = std::max(1, g_numThreads);

        // 自动 batch：仅在 g_jobCostCacheEnabled 且有该 job 的成本数据时。
        // 热路径开销 <3ns（GetPerElemCost 一次 AND + 一次数组读）。
        if (funcHash != 0 && g_jobCostCacheEnabled.load(std::memory_order_relaxed))
        {
            // ---- 带宽/延迟绑定自适应 ----
            // memory-bound job（GridSearch Query 空间哈希 gather）总耗时由共享
            // DRAM 带宽主导，加 tile 不线性提速，按每元素成本推 tile 数会错标。
            // 粗/细粒度对比学习后判为 mem-bound → 直接固定 tpw 分块（并发够用、
            // 调度开销最小）；compute-bound 走下方原公式。
            // 学习期两阶段：
            //   1) 先采 kCoarseProbeSamples 个粗粒度（tpw）样本作参考（此时细 EWMA=0，
            //      perElemNs==0 自然走 tpw 兜底，粗样本在退役侧自动积累）；
            //   2) 粗样本齐后，用粗成本作代理跑公式 → 产出细粒度分块 → 采集细样本，
            //      细样本满足阈值后 TryClassify 定 mode（mem-bound / parallel）。
            const auto mode = g_jobCostCache.GetMode(funcHash);
            if (mode == JobSystem::kModeMemBound)
            {
                if (g_jobCostCacheVerbose)
                    std::printf("[JCC] R length=%d MEM-BOUND → tpw chunk\n", length);
                return std::max(16, (length + wc * g_configuredTilesPerWorker - 1) / (wc * g_configuredTilesPerWorker));
            }
            const double perElemNs = g_jobCostCache.GetPerElemCost(funcHash);
            if (mode == JobSystem::kModeUnknown && !g_jobCostCache.HasLearnedCoarse(funcHash))
            {
                // 阶段 1：粗样本未齐 → tpw（perElemNs 通常为 0，本分支与兜底一致）
                return std::max(16, (length + wc * g_configuredTilesPerWorker - 1) / (wc * g_configuredTilesPerWorker));
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
                const int chunkTpw4 = std::max(16, (length + wc * g_configuredTilesPerWorker - 1) / (wc * g_configuredTilesPerWorker));

                // ── 两因子（C_fixed 每 tile 固定 + C_elem 每元素）优先 ──
                const double cfixed = g_jobCostCache.GetPerTileCost(funcHash);
                const double celem = perElemNs > 0.0 ? perElemNs : costNs;
                if (cfixed > 0.0 && celem > 0.0)
                {
                    // 空体/超轻：tpw 粒度单 tile 执行 << 调度开销 → 执行≈0，总成本被
                    // 调度/唤醒/worker 抖动主导，任何执行成本模型都无解 → tpw 兜底
                    //（64K/1M 达 docs；8K 空体 ~6µs 为已知局限——C_fixed×tiles 主导，
                    //  旧口径靠唤醒虚高（∝1/N）碰巧缓解，见 docs 20260830）。
                    const double tileTimeTpw =
                        cfixed + (static_cast<double>(length) / (wc * g_configuredTilesPerWorker)) * celem;
                    if (tileTimeTpw < kSchedulingOverheadNs)
                    {
                        // 空体/超轻 → tpw 兜底。仍按"公式产出"登记细样本（perElem 值有效），
                        // 使细/粗比值≈1 → mem-bound 分类 → 后续稳态固定 tpw（性能同兜底），
                        // 且细 EWMA 有值（JccConcurrentHeterogeneous 断言 perElem>0）。
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
                    return std::max(1, (length + targetTiles - 1) / targetTiles);
                }

                // ── 单因子回退（冷启动，C_fixed 未学）：既有公式 ──
                if (outJccFine) *outJccFine = true;   // JCC 公式产出（细粒度学习样本）
                const double totalUs = length * costNs / 1000.0;
                // perElem 是「并行 wall 稀释」成本（退役时 wall = 整批墙钟，÷N）。
                // 直接用它算 tiles 会把中间量级 job（wall ~0.1-5ms）塌成 4-15 个
                // 巨型 tile → 并行度损失 wc/tiles 倍（GridSearch 实测 2-3x 退化）。
                // 还原为「串行总量」：totalUs × wc ≈ 单 worker 串行所需时间。
                const double serialUs = totalUs * wc;
                double targetTilesD = std::clamp(serialUs / kTargetTileUs, 1.0,
                    static_cast<double>(wc) * kMaxAdaptiveTpw);
                int targetTiles = static_cast<int>(targetTilesD);
                if (targetTiles < 1) targetTiles = 1;
                // 安全护栏：单 tile 元素数上限（kMaxAutoChunk）。
                int floorTiles = (length + kMaxAutoChunk - 1) / kMaxAutoChunk;
                if (floorTiles > wc) floorTiles = wc;
                if (targetTiles < floorTiles) targetTiles = floorTiles;
                int chunk = std::max(1, (length + targetTiles - 1) / targetTiles);
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
                        floorTiles, chunk, (length + chunk - 1) / chunk);
                return chunk;
            }
        }
        // 冷启动 / flag 关闭 / 无数据 → tpw=4 兜底（现状）
        // 默认 g_configuredTilesPerWorker 个 tile/worker（可调，默认 4），
        // 比 Unity 默认 4/worker 更细：可变代价 job 的负载均衡收益 > claim 开销。
        // batch = N/(W*k) 随 N 自动缩放，无需每 job 标代价。
        return std::max(16, (length + wc * g_configuredTilesPerWorker - 1) / (wc * g_configuredTilesPerWorker));
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
    // 兜底唤醒看门狗：把"notify 错位型死锁"降级为可观测毛刺——
    //   ① 条件档：存在未关闭的 deferNotify 窗口（g_submitDeferDepth>0，最近的提交可能被吞唤醒）
    //      → 立即补一次广播再正常等待；正常路径 depth==0，零触发零开销；
    //   ② 超时档：等待 >5s 无进展 → 补广播 + 警告（兜住未知错位/遗漏路径，防永久挂）。
    static void WaitBackendRetired(HandleState* state) noexcept
    {
        if (!state) return;
        if (state->backendRetired.load(std::memory_order_acquire))
            return;
        if (g_submitDeferDepth.load(std::memory_order_relaxed) > 0 &&
            g_chaseLevScheduler)
        {
            g_chaseLevScheduler->WakePending();
        }
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
        while (!state->backendRetired.load(std::memory_order_acquire))
        {
            if (std::chrono::steady_clock::now() >= deadline)
            {
                std::fprintf(stderr,
                    "[JobSystem] WARN: backendRetired wait >5s (batch=%llu), forcing wake\n",
                    (unsigned long long)state->diagnosticBatchId.load(std::memory_order_relaxed));
                if (g_chaseLevScheduler)
                    g_chaseLevScheduler->WakePending();
                deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
            }
            state->backendRetired.wait(false, std::memory_order_relaxed);
        }
    }

    // C++ 异常协议：Complete 的每个退出点在等退役后调用——batch 上的异常已
    // 转移到 state->batchExceptionPtr（TryFinalizeChaseLevBatch 退役时复制），
    // 这里 rethrow 给调用方（TBB/Taskflow 语义）。so 只能抛一次：rethrow 后
    // 置 null，防止同一 state 多次 Complete 重复抛。
    static void RethrowBatchException(HandleState* state)
    {
        if (state && state->batchExceptionPtr)
        {
            auto ex = state->batchExceptionPtr;
            state->batchExceptionPtr = nullptr;
            std::rethrow_exception(ex);
        }
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

        // Phase 2: dense spin first (never yield before we've given the job a
        // chance to complete — yield triggers a full OS context switch).
        // Chase-Lev 模式：主线程在 spin 期间即参与认领执行（第 16 个执行者），
        // 不等 1ms 超时——消除"慢 worker 被 OS 抢占 + 主线程干等 1ms"的尾延迟。
        for (int i = 0; i < 2048; i++)
        {
            if (_state->completed.load(std::memory_order_acquire))
            {
                WaitBackendRetired(_state);
            RethrowBatchException(_state);
                return;
            }
            // Chase-Lev：spin 期间协助认领（每 16 次，更积极兜底慢 worker）
            if (g_mainThreadAssistEnabled && g_chaseLevScheduler && (i & 15) == 0)
            {
                if (!g_chaseLevScheduler->TryAssistOne()) { /* 无可认领，继续 spin */ }
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
            if (g_mainThreadAssistEnabled && g_chaseLevScheduler && (i & 15) == 0)
            {
                if (!g_chaseLevScheduler->TryAssistOne()) { /* 无可认领 */ }
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
        // 正常路径：worker 完成 → notify_all → condvar 谓词满足立即唤醒（无额外延迟）。
        // Chase-Lev 模式：主线程也参与共享认领执行（对齐旧 MPMC 的 assist——
        // 第 16 个执行者兜底"最后一片被 OS 抢占 worker 握着"的尾延迟）。
        g_waitFallbacks.fetch_add(1, std::memory_order_relaxed);
        g_completeWaitLoops.fetch_add(1, std::memory_order_relaxed);
        constexpr auto kCompleteRevisit = std::chrono::microseconds(256); // 256µs 回访间隔（更快兜底）
        while (!_state->completed.load(std::memory_order_acquire))
        {
            // 先 assist 再 wait：主线程持续认领执行（第 16 个执行者），
            // 避免"干等 256µs 才再干活的停顿窗口"。循环内每轮最多 16 片，
            // 防止链条级联时主线程无限 assist 不回查 completed。
            if (g_mainThreadAssistEnabled && g_chaseLevScheduler)
            {
                for (int assistN = 0; assistN < 16; ++assistN)
                {
                    if (_state->completed.load(std::memory_order_acquire)) break;
                    if (!g_chaseLevScheduler->TryAssistOne()) break;
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
