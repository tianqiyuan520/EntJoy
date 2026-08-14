#include "JobSystemInternal.h"

#include <algorithm>
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
    std::atomic<bool> g_useFineRangesForNextEcsBatch{ false };

    void RegisterLongBatchBarrier(HandleState* state) noexcept
    {
        if (!state || state->backendRetired.load(std::memory_order_acquire))
            return;
        AcquireState(state);
        std::lock_guard<std::mutex> lock(g_longBatchBarrierMutex);
        g_longBatchBarriers.push_back(state);
    }

    void ConsumeLongBatchBarriers() noexcept
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

    void SubmitBackendAsync(std::function<void()> work)
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

} // namespace JobSystem
