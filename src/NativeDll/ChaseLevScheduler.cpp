#include "ChaseLevScheduler.h"
#include "CpuPause.h"
#include "JobProfiler.h"
#include "ThreadAffinity.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <new>
#include <thread>

#if defined(_WIN32)
#include <intrin.h>   // __rdtsc（认领点探针 ENTJOY_CLAIM_STAT，见 ClaimProbeNow）
#include <windows.h>
#if !defined(_MSC_VER)
#include <pthread.h>  // MinGW: pthread_gethandle 把 pthread_t 转成 Win32 HANDLE
#endif
#endif

namespace JobSystem
{
    // ============================================================
    // 全局 RangeTask 池定义
    // ============================================================
    RangeTaskPool ChaseLevScheduler::s_taskPool_;

    // ────────────────────────────────────────────────────────────
    // 认领点探针（观测开关 `ENTJOY_CLAIM_STAT=1`，默认关 ⇒ 逐位不变、零时间戳开销）
    //
    // 为什么这么量：我们的 tile 认领是 `fetch_add`（**没有 CAS 失败可数**），"争用"唯一的表现形式
    // 就是**共享游标所在 cacheline 的跨核弹跳** ⇒ 只能直接测 fetch_add 的往返周期。
    // 三个认领点（General 共享游标 1 个 + 切片路自有/窃取游标 2 个）各自计时，累加到 **per-thread**
    // 本地量，每个令牌结束时 flush 一次（不把探针自身变成新的原子热路径）。
    // ────────────────────────────────────────────────────────────
    static thread_local uint64_t tl_claimProbeN = 0;
    static thread_local uint64_t tl_claimProbeCycles = 0;
    static thread_local uint64_t tl_claimProbeMax = 0;

    static inline uint64_t ClaimProbeNow() noexcept
    {
#if defined(_WIN32)
        return static_cast<uint64_t>(__rdtsc());
#else
        return 0;
#endif
    }

    static inline void ClaimProbeFlush() noexcept
    {
        if (tl_claimProbeN == 0) return;
        g_claimProbeN.fetch_add(tl_claimProbeN, std::memory_order_relaxed);
        g_claimProbeCycles.fetch_add(tl_claimProbeCycles, std::memory_order_relaxed);
        uint64_t cur = g_claimProbeMax.load(std::memory_order_relaxed);
        while (tl_claimProbeMax > cur &&
               !g_claimProbeMax.compare_exchange_weak(cur, tl_claimProbeMax, std::memory_order_relaxed))
        { /* retry with refreshed cur */ }
        tl_claimProbeN = 0; tl_claimProbeCycles = 0; tl_claimProbeMax = 0;
    }

    // 计时块：`const bool on = g_claimStatEnabled.load(relaxed);` 在令牌起点取一次。
    static inline uint64_t ClaimProbeBegin(bool on) noexcept { return on ? ClaimProbeNow() : 0; }
    static inline void ClaimProbeEnd(bool on, uint64_t t0) noexcept
    {
        if (!on) return;
        const uint64_t d = ClaimProbeNow() - t0;
        tl_claimProbeCycles += d; ++tl_claimProbeN;
        if (d > tl_claimProbeMax) tl_claimProbeMax = d;
    }

    // `ENTJOY_SPIN_NEEDS_WORK`（默认**开**；`=0` 关闭）：大自旋窗只在"注入器里有可认领的活"时给。
    static bool SpinNeedsWorkEnabled() noexcept
    {
        static const bool enabled = [] {
            const char* v = std::getenv("ENTJOY_SPIN_NEEDS_WORK");
            return !(v != nullptr && v[0] == '0');
        }();
        return enabled;
    }

    // ── `ENTJOY_WAKE_POLL`：提交侧唤醒决策（token **必须已经**入注入器）────────────────────
    // 全协议见 JobSystemInternal.h 的 `WakePollEnabled()` 注释块；此处只放实现与不变量。
    //
    // ⚠ 两个入口**必须分开判**（need 的口径不同）：
    //   · `SubmitWork`（小 job）：一次派发只需 **1** 个 worker 就能推进（该路径的 RangeTask
    //     `batch==nullptr` ⇒ 由**一个** worker 跑 `RunWorkTask`，语义上不可能并行）。
    //   · `SubmitBatch`（真并行趟）：一次派发要 `need` 个 worker 才跑得动 —— `need` 取
    //     **`tokenCount`（= min(workerCap, workerCount_, tileCount)，本函数的局部量）**，
    //     而不是 `batch->workerCount`（那只是**上限**）。理由：一趟只有 k 个 tile 时，唤醒超过 k 个
    //     worker 是纯浪费；`tokenCount` 就是这一趟真正会发布的令牌数，也是它真正需要的并行度。
    //
    // 不变量（三条，缺一不可）：
    //   I1 提交侧的顺序是 push → fence → 读 idle → 读 sleepers（读序不可交换，见下）。
    //   I2 停靠侧的顺序是 登记 sleepers → fence → 最后一次读注入器/deque → futex wait。
    //   I3 搜索区登记进出必须配平（粘性登记：只在停靠协议入口与退出主循环两处减）。
    //
    // 诊断计数：thread_local 累加 + 每 1024 次合并，读数前 flush。**不得**改成每条派发一次全局
    // 原子 RMW —— 那等于把唤醒决策刚消掉的共享行流量换个名字加回来（做法与认领探针一致）。
    static thread_local uint64_t tl_wakePollSkips = 0, tl_wakePollWakes = 0;
    static thread_local uint64_t tl_wakePollSkipsWork = 0, tl_wakePollWakesWork = 0;
    static thread_local uint64_t tl_wakePollSkipsBatch = 0, tl_wakePollWakesBatch = 0;
    static thread_local uint32_t tl_wakePollPending = 0;

    // 把**当前线程**的累加值合并进全局计数。调用点：本线程每 1024 次派发、worker 退出主循环、
    // 以及读取侧（`JobSystem_GetWakePollCounters` / `[JOBWAKEPOLL]` 打印）之前各一次。
    void WakePollFlushCurrentThread() noexcept
    {
        if (tl_wakePollPending == 0) return;
        tl_wakePollPending = 0;
        if (tl_wakePollSkips)
        {
            g_wakePollSkips.fetch_add(tl_wakePollSkips, std::memory_order_relaxed);
            tl_wakePollSkips = 0;
        }
        if (tl_wakePollWakes)
        {
            g_wakePollWakes.fetch_add(tl_wakePollWakes, std::memory_order_relaxed);
            tl_wakePollWakes = 0;
        }
        if (tl_wakePollSkipsWork)
        {
            g_wakePollSkipsWork.fetch_add(tl_wakePollSkipsWork, std::memory_order_relaxed);
            tl_wakePollSkipsWork = 0;
        }
        if (tl_wakePollWakesWork)
        {
            g_wakePollWakesWork.fetch_add(tl_wakePollWakesWork, std::memory_order_relaxed);
            tl_wakePollWakesWork = 0;
        }
        if (tl_wakePollSkipsBatch)
        {
            g_wakePollSkipsBatch.fetch_add(tl_wakePollSkipsBatch, std::memory_order_relaxed);
            tl_wakePollSkipsBatch = 0;
        }
        if (tl_wakePollWakesBatch)
        {
            g_wakePollWakesBatch.fetch_add(tl_wakePollWakesBatch, std::memory_order_relaxed);
            tl_wakePollWakesBatch = 0;
        }
    }

    static inline void WakePollAccount(bool woke, bool isBatch) noexcept
    {
        if (woke) { ++tl_wakePollWakes; if (isBatch) ++tl_wakePollWakesBatch; else ++tl_wakePollWakesWork; }
        else { ++tl_wakePollSkips; if (isBatch) ++tl_wakePollSkipsBatch; else ++tl_wakePollSkipsWork; }
        if (++tl_wakePollPending >= 1024) WakePollFlushCurrentThread();
    }

    static bool WakePollDecideAfterPush(ChaseLevScheduler& sched, int needWorkers, bool isBatch) noexcept
    {
        std::atomic_thread_fence(std::memory_order_seq_cst);   // PushFence
        const uint32_t idle = static_cast<uint32_t>(
            sched.wakeIdlePollers.load(std::memory_order_seq_cst));
        if (idle >= static_cast<uint32_t>(needWorkers > 0 ? needWorkers : 1))
        {
            WakePollAccount(/*woke=*/false, isBatch);
            return false;
        }
        const int sleepers = sched.parkedWorkers.load(std::memory_order_seq_cst);
        if (sleepers <= 0)
        {
            // 无人停靠 ⇒ 其余 worker 正在"主循环/执行体"里，它们回到搜索区时必然读注入器。
            WakePollAccount(/*woke=*/false, isBatch);
            return false;
        }
        WakePollAccount(/*woke=*/true, isBatch);
        sched.wakeEpoch.fetch_add(1, std::memory_order_seq_cst);
        sched.wakeEpoch.notify_all();
        return true;
    }

    // ============================================================
    // 构造 / 析构
    // ============================================================

    ChaseLevScheduler::ChaseLevScheduler() = default;

    ChaseLevScheduler::~ChaseLevScheduler() { Stop(); }

    // ============================================================
    // 自旋 pause
    // ============================================================
    // CpuPause() defined in CpuPause.h (unity build safe)

    // ============================================================
    // ExecuteAndRelease — 执行一个 RangeTask 并释放回池
    // ============================================================

    // 通用 work 任务（batch==nullptr）：workFn → workCleanup → Release；异常就地吞掉（调用方 work 内已自处置）。
    static void RunWorkTask(RangeTask* task) noexcept
    {
        if (!task) return;
        try
        {
            if (task->workFn) task->workFn(task->workCtx);
        }
        catch (...)
        {
        }
        if (task->workCleanup)
        {
            try
            {
                task->workCleanup(task->workCtx);
            }
            catch (...)
            {
            }
        }
        ChaseLevScheduler::s_taskPool_.Release(task);
    }

    void ChaseLevScheduler::ExecuteAndRelease(RangeTask* task, uint32_t workerIndex, TileAccount account) noexcept
    {
        if (!task) return;
        if (!task->batch)
        {
            RunWorkTask(task);   // 无 batch 的通用 work（完成链由调用方负责）
            return;
        }
        if (task->firstTile == kClaimTokenMarker)
        {
            ExecuteClaimToken(task->batch, workerIndex, task->batchGen, account);   // workerCap 令牌：原子认领，内部已 taskDone
            s_taskPool_.Release(task);
            return;
        }
        if (task->tileCount == 0)
        {
            s_taskPool_.Release(task);   // 空区间任务：释放回池
            return;
        }

        BatchState* batch = task->batch;
        const uint32_t end = std::min(task->firstTile + task->tileCount, batch->tileCount);

        // 调试面板
        DebugBeginExec(batch->diagnosticId, batch->tileCount, batch->workerCount, false);
        SetCurrentBatchId(batch->diagnosticId);
        E1::Begin();
        if (workerIndex < kMaxTrackedWorkers)
            workerCurrentBatch[workerIndex].store(batch->diagnosticId, std::memory_order_relaxed);

        // 执行 tile 范围
        for (uint32_t t = task->firstTile; t < end; ++t)
            executor_(batch, t);

        if (workerIndex < kMaxTrackedWorkers)
            workerCurrentBatch[workerIndex].store(0, std::memory_order_relaxed);
        E1::End(workerIndex);
        SetCurrentBatchId(0);
        DebugEndExec();

        // 诊断计数
        if (workerIndex < kMaxTrackedWorkers)
            tasksExecuted[workerIndex].fetch_add(1, std::memory_order_relaxed);

        // tile 计数按执行者口径（local/stolen/assist）
        switch (account)
        {
        case TileAccount::Local:  g_localTiles.fetch_add(task->tileCount, std::memory_order_relaxed); break;
        case TileAccount::Stolen: g_stolenTiles.fetch_add(task->tileCount, std::memory_order_relaxed); break;
        case TileAccount::Assist: g_assistTiles.fetch_add(task->tileCount, std::memory_order_relaxed); break;
        }

        // 任务完成：pendingTasks--（可能触发退役）；携带本令牌的代次做迟到校验
        if (taskDone_)
        {
            activeTasks.fetch_sub(1, std::memory_order_acq_rel);
            taskDone_(batch, task->batchGen);
            totalTasksDone.fetch_add(1, std::memory_order_relaxed);
        }

        // 释放 RangeTask 回池
        s_taskPool_.Release(task);
    }

    // ============================================================
    // StealAndExecute — 从 Injector 或其他 worker 窃取一个任务并执行
    // ============================================================

    bool ChaseLevScheduler::StealAndExecute(uint32_t workerIndex) noexcept
    {
        // 1. 从 Injector 窃取（FIFO，1 CAS）
        RangeTask* task = nullptr;
        if (injector_.Pop(task))
        {
            if (task->batch == nullptr)
            {
                RunWorkTask(task);   // 通用 work：直接执行（内部 Release）
                return true;
            }
            ExecuteAndRelease(task, workerIndex, TileAccount::Assist);   // 内部按 Assist 口径记 tile
            // 主线程 assist 计数
            g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
            g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
            return true;
        }

        // 2. 从其他 worker deque 窃取（FIFO，1 CAS per victim）
        g_stealAttempts.fetch_add(1, std::memory_order_relaxed);
        for (uint32_t offset = 1; offset < workerCount_; ++offset)
        {
            const uint32_t victimIdx = (workerIndex + offset) % workerCount_;
            SparseTileDeque* victimDeque = workers_[victimIdx]->deque.get();
            // 空 deque 提前跳过：StealTop 必然失败，仍会白做 4 次 CAS 尝试。
            // 空判读的是本就被 StealTop 读取的同一对 atomic，语义不变；
            // 被跳过的 CAS 只可能命中此刻并发出现的元素，那种情况 owner 会自行取走。
            if (victimDeque->IsEmpty())
                continue;
            // 计数放在空判之后：扫描数只统计"真正发起窃取"的受害者，不再被空转刷高。
            g_victimScans.fetch_add(1, std::memory_order_relaxed);
            TileTask tileTask;
            if (victimDeque->StealTop(tileTask))
            {
                g_stealSuccesses.fetch_add(1, std::memory_order_relaxed);
                g_stealCount.fetch_add(1, std::memory_order_relaxed);
                if (workerIndex < kMaxTrackedWorkers)
                    dequeStolen[workerIndex].fetch_add(1, std::memory_order_relaxed);

                // 从 deque 窃取的是 TileTask，需要转换为 RangeTask 处理
                if (tileTask.batch && tileTask.tileCount > 0)
                {
                    // workerCap 令牌：主线程 assist 认领循环执行，内部已 taskDone
                    if (tileTask.firstTile == kClaimTokenMarker)
                    {
                        ExecuteClaimToken(tileTask.batch, workerIndex, tileTask.batchGen, TileAccount::Assist);
                        return true;
                    }
                    // 记录 worker 进入批次时间（供 timing 诊断）
                    ChaseLevRecordWorkerEntry(tileTask.batch);

                    // 创建临时 RangeTask 执行（不走池，因为是从 deque 窃取的）；
                    // 结算用的是 tileTask.batchGen（本地 tempTask 不参与结算）。
                    RangeTask tempTask;
                    tempTask.batch = tileTask.batch;
                    tempTask.firstTile = tileTask.firstTile;
                    tempTask.tileCount = tileTask.tileCount;

                    DebugBeginExec(tileTask.batch->diagnosticId, tileTask.batch->tileCount,
                                   tileTask.batch->workerCount, false);
                    SetCurrentBatchId(tileTask.batch->diagnosticId);
                    E1::Begin();
                    if (workerIndex < kMaxTrackedWorkers)
                        workerCurrentBatch[workerIndex].store(
                            tileTask.batch->diagnosticId, std::memory_order_relaxed);

                    const uint32_t end = std::min(tempTask.firstTile + tempTask.tileCount,
                                                  tempTask.batch->tileCount);
                    for (uint32_t t = tempTask.firstTile; t < end; ++t)
                        executor_(tempTask.batch, t);

                    if (workerIndex < kMaxTrackedWorkers)
                        workerCurrentBatch[workerIndex].store(0, std::memory_order_relaxed);
                    E1::End(workerIndex);
                    SetCurrentBatchId(0);
                    DebugEndExec();

                    if (workerIndex < kMaxTrackedWorkers)
                        tasksExecuted[workerIndex].fetch_add(1, std::memory_order_relaxed);

                    // 主线程 assist 计数
                    g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
                    g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
                    g_assistTiles.fetch_add(tileTask.tileCount, std::memory_order_relaxed);
                    tileTask.batch->batchAssistTiles.fetch_add(
                        tileTask.tileCount, std::memory_order_relaxed);

                    // 从 deque 窃取的任务也需要 taskDone（pendingTasks--）
                    if (taskDone_)
                    {
                        activeTasks.fetch_sub(1, std::memory_order_acq_rel);
                        taskDone_(tileTask.batch, tileTask.batchGen);
                        totalTasksDone.fetch_add(1, std::memory_order_relaxed);
                    }
                }
                return true;
            }
        }

        return false;
    }

    // ============================================================
    // Start — 创建持久 worker 线程 + deque
    // ============================================================

    bool ChaseLevScheduler::Start(uint32_t workerCount, TileExecutor executor,
        TaskDoneFn taskDone, bool bindThreads)
    {
        if (workerCount == 0 || !executor) return false;
        std::lock_guard<std::mutex> lock(lifecycleMutex_);
        if (running_) return workers_.size() == workerCount;
        if (!workers_.empty()) return false;

        quit_.store(false, std::memory_order_relaxed);
        bindThreads_ = bindThreads;
        workerCount_ = workerCount;
        executor_ = executor;
        taskDone_ = taskDone;
        for (auto& b : workerCurrentBatch)
            b.store(0, std::memory_order_relaxed);
        totalTasksPushed.store(0, std::memory_order_relaxed);
        totalTasksDone.store(0, std::memory_order_relaxed);
        activeTasks.store(0, std::memory_order_relaxed);
        for (int i = 0; i < kMaxTrackedWorkers; ++i)
        {
            dequePushed[i].store(0, std::memory_order_relaxed);
            dequePopped[i].store(0, std::memory_order_relaxed);
            dequeStolen[i].store(0, std::memory_order_relaxed);
            tasksExecuted[i].store(0, std::memory_order_relaxed);
        }

        wakeEpoch.store(0, std::memory_order_relaxed);
        // 搜索区登记计数归零。正常退出时由 WorkerLoop 的两个出口配平（停靠协议入口 / quit 分支），
        // 这里是防御性的 —— 一个残留的 >0 会让提交侧永久跳过广播。
        wakeIdlePollers.store(0, std::memory_order_relaxed);
        parkedWorkers.store(0, std::memory_order_relaxed);

        try
        {
            workers_.reserve(workerCount);
            for (uint32_t i = 0; i < workerCount; ++i)
            {
                auto ctx = std::make_unique<WorkerContext>();
                ctx->deque = std::make_unique<SparseTileDeque>(kDequeCapacity);
                workers_.push_back(std::move(ctx));
            }

            for (uint32_t i = 0; i < workerCount; ++i)
            {
                auto* raw = workers_[i].get();
                raw->thread = std::thread([this, i, raw]() { WorkerLoop(i, *raw); });
            }
        }
        catch (...)
        {
            // 异常回滚：此时已持有 lifecycleMutex_，不能调 Stop()（会二次加锁死锁）。
            // 直接置 quit + 唤醒 + join 已创建的线程 + clear。
            quit_.store(true, std::memory_order_release);
            wakeEpoch.fetch_add(1, std::memory_order_release);
            wakeEpoch.notify_all();
            for (auto& ctx : workers_)
            {
                if (ctx->thread.joinable())
                    ctx->thread.join();
            }
            workers_.clear();
            throw;
        }

        running_.store(true, std::memory_order_release);
        return true;
    }

    // ============================================================
    // Stop — 通知 quit，唤醒所有 worker，join
    // ============================================================

    void ChaseLevScheduler::Stop() noexcept
    {
        {
            std::lock_guard<std::mutex> lock(lifecycleMutex_);
            if (workers_.empty()) { running_.store(false); return; }
            running_.store(false);
        }

        quit_.store(true, std::memory_order_release);

        // 唤醒所有 worker 退出（单 epoch 广播：1 次 syscall 唤醒全部 waiter）
        wakeEpoch.fetch_add(1, std::memory_order_release);
        wakeEpoch.notify_all();

        for (auto& ctx : workers_)
        {
            if (ctx->thread.joinable())
                ctx->thread.join();
        }

        // 排空残留 task 并收集未退役 batch 到 drainedBatches，消除 shutdown 泄漏（worker 已 join，单线程安全）。
        DrainRemaining();

        workers_.clear();
    }

    // 排空 Injector 与各 worker deque 的残留 task（仅 Stop 内 join 后调用）。
    void ChaseLevScheduler::DrainRemaining() noexcept
    {
        drainedBatches.clear();

        // 1. Injector 残留（RangeTask：tile 令牌或通用 work）
        RangeTask* task = nullptr;
        while (injector_.Pop(task))
        {
            if (!task) continue;
            if (task->batch == nullptr)
            {
                // Shutdown 默认 Drain：通用异步任务必须执行 work（不只 cleanup），
                // 否则 state 永不 Complete，调用方在 shutdown 后永久等待。
                RunWorkTask(task);
                continue; // RunWorkTask 已将 task 归还池
            }
            // Drain 阶段直接执行残余 token/range，让 pendingTasks/tilesRemaining 正常收敛；
            // 只有无法执行的异常任务才交 Scheduler::ForceFinalizeBatch。
            ExecuteAndRelease(task, kMaxTrackedWorkers);
        }

        // 2. worker deque 残留（TileTask）
        for (auto& ctx : workers_)
        {
            TileTask t;
            while (ctx->deque->StealTop(t))
            {
                if (!t.batch || t.tileCount == 0) continue;
                if (t.firstTile == kClaimTokenMarker)
                {
                    ExecuteClaimToken(t.batch, kMaxTrackedWorkers, t.batchGen);
                    continue;
                }
                // deque 中是轻量 TileTask（无 work 回调）；逐 tile 执行，range 完成后平衡 taskDone。
                const uint32_t end = std::min(t.firstTile + t.tileCount, t.batch->tileCount);
                for (uint32_t i = t.firstTile; i < end; ++i)
                    executor_(t.batch, i);
                if (taskDone_)
                {
                    activeTasks.fetch_sub(1, std::memory_order_acq_rel);
                    taskDone_(t.batch, t.batchGen);
                    totalTasksDone.fetch_add(1, std::memory_order_relaxed);
                }
            }
        }
    }

    // ============================================================
    // SubmitBatch — 预切分为 RangeTask 推入 Injector，唤醒 worker
    // 标准 Chase-Lev：任务经 Injector 分发，worker 从 Injector 拉取推入 deque。
    // ============================================================


    // Injector 满：有限退避（yield + pause），避免提交线程 busy-loop
    bool ChaseLevScheduler::PushTaskBackoff(RangeTask* task) noexcept
    {
        if (!task) return false;
        uint32_t backoff = 0;
        while (!injector_.Push(task))
        {
            // Stop 开始后可能已无消费者：绝不为入队无限自旋（调度器正在排空）。
            if (quit_.load(std::memory_order_acquire) ||
                !running_.load(std::memory_order_acquire))
                return false;
            ++backoff;
            if ((backoff & 15) == 0)
                std::this_thread::yield();
            else
                CpuPause();
            if (backoff > 4096) { std::this_thread::yield(); backoff = 0; }
        }
        return true;
    }


    void ChaseLevScheduler::SubmitBatch(BatchState* batch) noexcept
    {
        if (!batch || batch->tileCount == 0) return;
        const uint32_t wc = workerCount_;
        if (wc == 0)
        {
            try
            {
                AbortUnsubmittedBatch(
                    batch,
                    std::make_exception_ptr(std::runtime_error(
                        "JobSystem has no workers")));
            }
            catch (...)
            {
                // If constructing the diagnostic exception itself fails,
                // still publish a terminal state through the no-throw path.
                AbortUnsubmittedBatch(batch, {});
            }
            return;
        }

        const uint32_t tileCount = batch->tileCount;
        const bool spDiag = SchedPhase::Enabled();
        uint64_t spT0 = 0;
        if (spDiag) spT0 = MonotonicNowNs();

        // ── token（令牌）提交（唯一路径）──
        // 只投 O(workers) 个令牌，令牌内 nextTile.fetch_add 细粒度认领（流量 O(tiles)→O(workers)，消除洪泛背压）。
        // workerCap 限制并行时实际参与 ≤ 令牌数。
        const bool capMode = batch->workerCount > 0 &&
            static_cast<uint32_t>(batch->workerCount) < wc;
        const uint32_t tokenTarget = capMode
            ? static_cast<uint32_t>(batch->workerCount) : wc;
        const uint32_t tokenCount = std::min(tokenTarget, tileCount);
        if (tokenCount == 0) return;
        batch->pendingTasks.store(tokenCount, std::memory_order_release);
        activeTasks.fetch_add(static_cast<int64_t>(tokenCount), std::memory_order_acq_rel);
        // 本批**这一代**的代次快照。所有令牌（入队的、以及兜底直执的）都携带它；
        // 结算侧 ChaseLevTaskDone 用它拒绝"批已被回收复用后才到达"的迟到结算。
        const uint32_t batchGen = batch->storage
            ? batch->storage->generation.load(std::memory_order_relaxed)
            : 0u;

        // 兜底（罕见：Stop 竞态 / 堆耗尽）：本线程同步执行一个认领令牌（原语义）。
        auto runFallbackToken = [&](uint32_t) noexcept { ExecuteClaimToken(batch, kMaxTrackedWorkers, batchGen); };

        // ── Stop 竞态守卫 ──
        // `quit_`/`running_` 一旦置位，worker 会退出且 DrainRemaining 已排空注入器：此后推入的 token
        // **再无人消费** ⇒ batch->pendingTasks 永不归零 ⇒ TryFinalizeChaseLevBatch 不执行
        // ⇒ cleanup/ReleaseBatch/HandleState/g_backendBatchesOutstanding 全部残留（泄漏），
        // 且调用方 JobHandle::Complete() 永久阻塞、JobSystem_GetStats 卡在 WaitForBackendBatches。
        // 拒绝入队时按既有 directTokens 语义在提交线程同步执行，保证每个发布 tile 都达终态。
        if (quit_.load(std::memory_order_acquire) || !running_.load(std::memory_order_acquire))
        {
            for (uint32_t i = 0; i < tokenCount; ++i)
                runFallbackToken(i);
            return;
        }

        uint64_t spT1 = 0;
        if (spDiag) spT1 = MonotonicNowNs();

        // PushMany：批量创建 token 任务 + 一次 CAS 批量入队注入器
        constexpr uint32_t kMaxBulkTokens = 64;   // 栈数组上限（worker 数实际 ≤ 64）
        RangeTask* bulk[kMaxBulkTokens];
        uint32_t directTokens = 0;
        uint32_t pushedTokens = 0;
        for (uint32_t base = 0; base < tokenCount; base += kMaxBulkTokens)
        {
            const uint32_t n = std::min(kMaxBulkTokens, tokenCount - base);

            // 循环期间 Stop 可能已经开始：不再入队，本组逻辑 token 转由提交线程同步执行兜底
            // （与分配的 OOM 兜底同语义：不分配 task，仅计入 directTokens，末尾统一 ExecuteClaimToken）。
            if (quit_.load(std::memory_order_acquire) || !running_.load(std::memory_order_acquire))
            {
                directTokens += n;
                continue;
            }

            uint32_t allocated = 0;
            for (uint32_t i = 0; i < n; ++i)
            {
                RangeTask* task = s_taskPool_.Acquire();
                if (!task)
                {
                    task = new (std::nothrow) RangeTask();
                    if (!task)
                    {
                        // Keep the task accounting exact even when the heap is exhausted:
                        // the token is executed by the submitting thread below (directTokens).
                        ++directTokens;
                        continue;
                    }
                    // 池耗尽兜底（poolIndex=UINT32_MAX → Release 时 delete）
                    task->poolIndex = UINT32_MAX;
                }
                task->batch = batch;
                task->batchGen = batchGen;
                task->firstTile = kClaimTokenMarker;
                task->tileCount = 1;
                bulk[allocated++] = task;
            }
            uint32_t pushed = injector_.PushMany(bulk, allocated);
            pushedTokens += pushed;
            while (pushed < allocated)
            {
                // 注入器容量不足：剩余项逐个退避入队（不丢任务）
                if (!PushTaskBackoff(bulk[pushed]))
                {
                    // Stop 竞态：入队循环后在本线程执行等效 token；pendingTasks 已含它，退役不会提前。
                    s_taskPool_.Release(bulk[pushed]);
                    ++directTokens;
                }
                else
                {
                    ++pushedTokens;
                }
                ++pushed;
            }
            // 分配失败时这些逻辑 token 由 directTokens 表示而非队列项。
        }
        totalTasksPushed.fetch_add(pushedTokens, std::memory_order_relaxed);

        // 未入队 token 由本线程同步执行兜底（罕见：OOM/Stop 竞态），保证每个发布 tile 都被执行
        // 或达终态，pendingTasks 不会永久为正。
        for (uint32_t i = 0; i < directTokens; ++i)
            ExecuteClaimToken(batch, kMaxTrackedWorkers, batchGen);

        uint64_t spT2 = 0;
        if (spDiag) spT2 = MonotonicNowNs();
        // deferNotify：窗口内跳过逐批唤醒，由 Flush 统一广播。depth<=0 才唤醒：
        // 负值（Flush 下溢）也广播，防止批量任务被永久搁置（下溢使 ==0 永不成立 → 永不唤醒）。
        if (g_submitDeferDepth.load(std::memory_order_relaxed) <= 0)
        {
            if (WakePollEnabled())
            {
                // 真并行趟按**真实需求**判 —— `tokenCount`（= min(workerCap, workerCount_, tileCount)）
                // 才是这一趟会发布的令牌数 / 真正需要的并行度；`batch->workerCount` 只是上限
                // （一趟只有 k 个 tile 时，唤醒超过 k 个 worker 是纯浪费，而且那正是小 pass 的形状）。
                // 该口径保守：唤醒只会更多、不会更少。
                WakePollDecideAfterPush(*this, static_cast<int>(tokenCount), /*isBatch=*/true);
            }
            else
            {
                wakeEpoch.fetch_add(1, std::memory_order_release);
                // 只有"确有等待者、且已醒人数不够本批名额"时才付 futex 广播（+ IPI）。
                //   已醒的 worker 在自旋区会看到注入器里的 token；睡着的 worker 不必为用不上的名额被叫醒。
                const int parked = parkedWorkers.load(std::memory_order_acquire);
                const int awake = static_cast<int>(workerCount_) - parked;
                const int need = batch->workerCount > 0
                    ? static_cast<int>(batch->workerCount) : static_cast<int>(workerCount_);
                if (parked > 0 && awake < need)
                    wakeEpoch.notify_all();
                else
                    g_notifySkipped.fetch_add(1, std::memory_order_relaxed);
            }
        }
        if (spDiag)
        {
            const uint64_t t3 = MonotonicNowNs();
            SchedPhase::Add(SchedPhase::SubmitTokens, spT2 - spT1);
            SchedPhase::Add(SchedPhase::SubmitNotify, t3 - spT2);
        }
    }

    // ============================================================
    // ExecuteClaimToken — workerCap 令牌：原子认领 nextTile 直到空
    // ============================================================

    void ChaseLevScheduler::ExecuteClaimToken(BatchState* batch, uint32_t workerIndex, uint32_t batchGen, TileAccount account) noexcept
    {
        if (!batch) return;
        // 令牌认领计数（`[M-16] 令牌 worker/main`）。
        if (workerIndex >= kMaxTrackedWorkers)
            g_mainClaimedTokens.fetch_add(1, std::memory_order_relaxed);
        else
            g_workerClaimedTokens.fetch_add(1, std::memory_order_relaxed);
        // timing 诊断：记录 worker 进入批次（首/末 worker 时间）
        ChaseLevRecordWorkerEntry(batch);
        DebugBeginExec(batch->diagnosticId, batch->tileCount, batch->workerCount, false);
        SetCurrentBatchId(batch->diagnosticId);
        E1::Begin();
        if (workerIndex < kMaxTrackedWorkers)
            workerCurrentBatch[workerIndex].store(batch->diagnosticId, std::memory_order_relaxed);

        const uint32_t end = batch->tileCount;
        // 认领粒度随批次规模收缩（较小时降到 1），保证小批次每个 tile 可被独立认领——
        // 阻塞型回调（worker 等待外部事件）时与 slice 语义等价；大批次维持 kClaimBatchSize=4。
        // 注意 `tileCount / workers` 这一项**保留** ⇒ 小批次自动退回细粒度，只有大批次被摊薄。
        // 上限可覆盖（优先级从高到低）：job 的 claimCapOverride（0 = 不覆盖）、
        // `ENTJOY_CLAIM_BATCH`（0 = 用内置默认）、内建 kClaimBatchSize。
        const uint32_t claimCap =
            (batch->claimCapOverride != 0) ? batch->claimCapOverride
            : ((g_claimBatchSize != 0) ? g_claimBatchSize : kClaimBatchSize);
        // `ENTJOY_CLAIM_SPAN`：把"每次认领的**元素跨度**"钉住，而不是钉 tile 数 —— 只对**薄 tile**
        // （itemsPerTile 小）抬高 cap；厚 tile 保持 cap（=4），以维持 8 个 worker 在 index 空间上的邻近
        // （Melee 依赖它的空间复用）。
        // 另有调用点声明腿（`claimSpanOverride`：单位=元素、与 tile 厚薄无关），优先级高于该全局规则。
        uint32_t capEff = claimCap;
        // ⚠ 两条规则都**只作用于"等宽 GeneralRange"**，且不能只看 totalElements 的数值：chunk/entity 路
        //   根本不设它（会继承被复用 BatchStorage 的陈旧值），packed 路显式置 0
        //   ⇒ 只看数值会让门在这两条路径上误开。故要求"kind == GeneralRange"这一硬条件，
        //   并用 `totalElements >= tileCount` 兜住语义（GeneralRange 下 = length ≥ rc，恒成立）。
        // 声明腿共用同一几何前置条件 ⇒ 这里把"几何合格"与"是否走全局规则"拆成两件事。
        const bool geomEligible =
            batch->tileCount > 0 &&
            batch->totalElements >= batch->tileCount &&
            // 等宽不物化路本身就是"等宽 GeneralRange"（此时 tiles == nullptr）⇒ 直接算合格；
            // 否则要求 tiles[0].kind == GeneralRange（chunk/entity/packed 一律不合格）。
            (batch->uniformTileSize != 0 ||
             (batch->tiles != nullptr && batch->tiles[0].kind == TileKind::GeneralRange));
        if (geomEligible)
        {
            const uint32_t itemsPerTile = batch->totalElements / batch->tileCount;
            // 调用点声明腿：声明值优先，且**与 tile 厚薄无关**。
            //   `capEff = clamp(声明元素数 / itemsPerTile, 1, tileCount)` —— 单位是**元素**，
            //   所以内批（tile 大小）怎么变，"每次内核调用的元素数"都不变。
            //   ⚠ 声明值可以**缩小** cap（厚 tile 声明小跨度时 c 会 < claimCap）—— 这是刻意的：
            //     声明的就是"我要的元素跨度"，没有隐藏下限（`step` 另有 [1, tileCount] 钳位）。
            if (batch->claimSpanOverride != 0 && itemsPerTile > 0)
            {
                uint32_t cd = batch->claimSpanOverride / itemsPerTile;
                if (cd < 1) cd = 1;
                if (cd > batch->tileCount) cd = batch->tileCount;
                capEff = cd;
            }
            // 未声明 ⇒ 全局规则（薄 tile 才抬高 cap，且不低于内建 claimCap）。
            else if (g_claimSpanElems > 0 && itemsPerTile <= kClaimSpanThinElems)
            {
                uint32_t c = g_claimSpanElems / itemsPerTile;
                if (c < claimCap) c = claimCap;
                if (c > g_claimSpanElems) c = g_claimSpanElems;
                capEff = c;
            }
            // 厚 tile 的**细档**：也按元素跨度给 cap（`kClaimSpanThickElems`），上限 = 每 worker 的
            // 公平份额（`tileCount/workers`）⇒ 不会退化成静态切分。
            // ⚠ 更粗的 tile（itemsPerTile > kClaimSpanMidElems）保持 `claimCap`（=4）以维持 worker 邻近
            //   （Melee 依赖"worker 邻近 ⇒ 空间哈希格复用"；实测把规则放到所有厚 tile 会让默认档
            //    Melee 84–93 ms → 100–102 ms，3/3）。
            // ⚠ 优先级：**批表的 per-job claim 声明（`claimCapOverride`）与全局 `ENTJOY_CLAIM_BATCH`
            //   都必须压过本规则** —— 否则 `key:64:4` 这种"该调用点只要 4 tile/认领"的声明会被**静默忽略**
            //   （与"批表 > API > F6 > env > 默认"的既有优先级相反）。
            else if (kClaimSpanThickElems > 0
                     && itemsPerTile > kClaimSpanThinElems
                     && itemsPerTile <= kClaimSpanMidElems
                     && batch->claimCapOverride == 0
                     && g_claimBatchSize == 0)
            {
                uint32_t c = kClaimSpanThickElems / itemsPerTile;
                if (c < 1) c = 1;
                const uint32_t fair = batch->tileCount / std::max(1u, workerCount_);
                if (c > fair) c = fair;
                if (c < 1) c = 1;
                capEff = c;
            }
        }
        uint32_t step = std::clamp(
            batch->tileCount / std::max(1u, workerCount_),
            1u, capEff);
        uint32_t executed = 0;
        // `ENTJOY_TILE_FASTPATH`：firstTileAt 每个**令牌**只判一次（逐 tile 判会多一次 load+compare；
        // 语义差 = "首个令牌开始" vs "首个 tile 开始"，只影响 JCC execSpan/诊断）。
        if (g_tileFastPath && end > 0 && batch->firstTileAt.load(std::memory_order_relaxed) == 0)
        {
            uint64_t empty = 0;
            batch->firstTileAt.compare_exchange_strong(empty, MonotonicNowNs(),
                std::memory_order_release, std::memory_order_relaxed);
        }
        // ── 切片认领 + 空手才窃取 ──
        // 由调用点声明 sliceCount（ResolveClaimSliced / InitSliceCursors）开启；语义见
        // JobSystemInternal.h 的 BatchState 注释块。
        //   ① 常态：worker 只从**自己那一段**的游标取 tile（独占 cacheline ⇒ 零跨核弹跳 + 顺序访问）；
        //   ② 空手才窃取：自己那段跑干后，才去别的段的游标上偷（每 tile 仍只被发放一次）。
        // "段"始终可被空手者窃取 ⇒ 均匀 job 拿零争用、异构 job 保均衡，无需按 job 分类。
        // 认领点探针开关（每令牌取一次；默认关 ⇒ on=false，ClaimProbe* 全是空操作）
        const bool claimProbe = g_claimStatEnabled.load(std::memory_order_relaxed);
        if (batch->sliceCount > 0 && end > 0)
        {
            // 切片路：段内游标每次 fetch_add 的是**连续** tile。
            const uint32_t mySlice = batch->sliceTaken.fetch_add(1, std::memory_order_relaxed);
            TileAcctGroupBegin();
            if (mySlice < batch->sliceCount)
            {
                const uint32_t sliceEnd = batch->sliceEnd[mySlice];
                while (true)
                {
                    const uint64_t cp0 = ClaimProbeBegin(claimProbe);
                    const uint32_t t = batch->sliceCursors[mySlice].next.fetch_add(
                        step, std::memory_order_relaxed);
                    ClaimProbeEnd(claimProbe, cp0);
                    if (t >= sliceEnd) break;
                    const uint32_t last = std::min(sliceEnd, t + step);
                    const uint32_t fast = TileExecuteUniformRun(batch, t, last - t);
                    if (fast > 0) { executed += fast; continue; }
                    for (uint32_t i = t; i < last; ++i) { executor_(batch, i); ++executed; }
                }
            }
            // 空手才窃取：从别的段偷。起点按 workerIndex 错开，避免所有空手者挤同一条游标行。
            const uint32_t slices = batch->sliceCount;
            for (uint32_t k = 1; k <= slices; ++k)
            {
                const uint32_t i = (mySlice + workerIndex + k) % slices;
                if (i == mySlice) continue;
                const uint32_t sliceEnd = batch->sliceEnd[i];
                while (true)
                {
                    const uint64_t cp0 = ClaimProbeBegin(claimProbe);
                    const uint32_t t = batch->sliceCursors[i].next.fetch_add(
                        step, std::memory_order_relaxed);
                    ClaimProbeEnd(claimProbe, cp0);
                    if (t >= sliceEnd) break;
                    const uint32_t last = std::min(sliceEnd, t + step);
                    const uint32_t fast = TileExecuteUniformRun(batch, t, last - t);
                    if (fast > 0) { executed += fast; continue; }
                    for (uint32_t u = t; u < last; ++u) { executor_(batch, u); ++executed; }
                }
            }
            TileAcctGroupFlush(batch);
        }
        else
        {
        // 认领组聚合记账（固定启用）：组内 tile 只本地计数，组末一次 fetch_sub。
        // 见 JobSystemInternal.h 的语义说明；executor_ 为 noexcept 路径，循环体不会抛出。
        TileAcctGroupBegin();
        while (true)
        {
            const uint64_t cp0 = ClaimProbeBegin(claimProbe);
            const uint32_t start = batch->nextTile.fetch_add(
                step, std::memory_order_relaxed);
            ClaimProbeEnd(claimProbe, cp0);
            if (start >= end) break;
            const uint32_t last = std::min(end, start + step);
            // 等宽路（内批 ≤ 16 的调用点，如对齐档 batch=1 的 MarkDead/Flow 各趟）：
            // 逐 tile 仍各一次内核调用（契约不变），但走直调把链路从 4 跳压到 1 跳（doc16 §14）。
            const uint32_t fast = TileExecuteUniformRun(batch, start, last - start);
            if (fast > 0) { executed += fast; continue; }
            for (uint32_t t = start; t < last; ++t)
            {
                executor_(batch, t);
                ++executed;
            }
        }
        TileAcctGroupFlush(batch);
        }
        // 令牌认领的 tile 按执行者口径计数（local/stolen/assist 三态）
        switch (account)
        {
        case TileAccount::Local:  g_localTiles.fetch_add(executed, std::memory_order_relaxed); break;
        case TileAccount::Stolen: g_stolenTiles.fetch_add(executed, std::memory_order_relaxed); break;
        case TileAccount::Assist: g_assistTiles.fetch_add(executed, std::memory_order_relaxed); break;
        }
        g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);

        ClaimProbeFlush();
        if (workerIndex < kMaxTrackedWorkers)
            workerCurrentBatch[workerIndex].store(0, std::memory_order_relaxed);
        E1::End(workerIndex);
        SetCurrentBatchId(0);
        DebugEndExec();
        if (workerIndex < kMaxTrackedWorkers)
            tasksExecuted[workerIndex].fetch_add(1, std::memory_order_relaxed);

        // 令牌完成：pendingTasks--（双条件退役由 ChaseLevTaskDone 检查；带代次做迟到校验）
        if (taskDone_)
        {
            activeTasks.fetch_sub(1, std::memory_order_acq_rel);
            taskDone_(batch, batchGen);
            totalTasksDone.fetch_add(1, std::memory_order_relaxed);
        }
    }

    // ============================================================
    // SubmitWork — 通用 work 任务（无 batch）
    // ============================================================

    bool ChaseLevScheduler::SubmitWork(void (*fn)(void*), void* ctx, void (*cleanup)(void*)) noexcept
    {
        if (!fn || !running_.load(std::memory_order_acquire) ||
            quit_.load(std::memory_order_acquire)) return false;
        RangeTask* task = s_taskPool_.Acquire();
        if (!task)
        {
            try
            {
                task = new RangeTask();   // 池耗尽兜底（poolIndex=UINT32_MAX → Release 时 delete）
            }
            catch (...)
            {
                return false;
            }
            task->poolIndex = UINT32_MAX;
        }
        task->batch = nullptr;
        task->firstTile = 0;
        task->tileCount = 0;
        task->workFn = fn;
        task->workCtx = ctx;
        task->workCleanup = cleanup;

        if (!PushTaskBackoff(task))
        {
            s_taskPool_.Release(task);
            return false;
        }

        // 与 SubmitBatch 保持**同一**守卫：defer 窗口内（depth>0）跳过逐 job 广播。
        // 窗口关闭处会无条件 WakePending 一次 ⇒ 不会丢唤醒；任务已进入注入器，仍在自旋的 worker
        // 会自行领取（入 park 前有 IsEmpty 复查）⇒ 推迟广播不改变可观察语义，只把 N 次广播合并为 1 次。
        if (g_submitDeferDepth.load(std::memory_order_relaxed) <= 0)
        {
            if (WakePollEnabled())
            {
                // 本路径**只有这一条**是无条件唤醒，是 `ENTJOY_WAKE_POLL` 的主要对象。
                // 小 job 一次只需要 1 个 worker 推进 ⇒ need=1（"有一个登记中的人"就够）。
                WakePollDecideAfterPush(*this, /*needWorkers=*/1, /*isBatch=*/false);
            }
            else
            {
                wakeEpoch.fetch_add(1, std::memory_order_release);
                wakeEpoch.notify_all();
            }
        }
        return true;
    }

    // 提交窗口统一唤醒（deferNotify 的 Flush）：一次 bump + notify_all。
    // defer 期 SubmitBatch 跳过 per-batch 唤醒；本方法在窗口关闭时执行唯一一次广播。
    void ChaseLevScheduler::WakePending() noexcept
    {
        wakeEpoch.fetch_add(1, std::memory_order_release);
        wakeEpoch.notify_all();
    }

    // ============================================================
    // TryAssistOne — 主线程协助执行：从 Injector 或其他 worker 窃取
    // ============================================================

    bool ChaseLevScheduler::TryAssistOne() noexcept
    {
        if (!running_.load(std::memory_order_acquire)) return false;
        // 主线程没有 workerIndex，用 0 作为诊断索引（不影响正确性）
        return StealAndExecute(0);
    }

    // ============================================================
    // ApplyAffinity — 运行时切换 worker CPU 亲和性
    // ============================================================

    void ChaseLevScheduler::ApplyAffinity(bool enabled) noexcept
    {
        bindThreads_ = enabled;
#if defined(_WIN32)
        std::lock_guard<std::mutex> lock(lifecycleMutex_);
        for (uint32_t i = 0; i < workers_.size(); ++i)
        {
            auto* ctx = workers_[i].get();
            if (!ctx->thread.joinable()) continue;
#if defined(_MSC_VER)
            HANDLE handle = ctx->thread.native_handle();
#else
            // MinGW 的 std::thread::native_handle() 返回 pthread_t（uintptr_t），
            // 不是 Win32 HANDLE，需经 pthread_gethandle 转换。
            HANDLE handle = pthread_gethandle(ctx->thread.native_handle());
#endif
            if (enabled)
            {
                // 绑定逻辑核心 1+i（与 WorkerLoop 启动时一致）。
                // ⚠ `static_cast<KAFFINITY>(1) << (1 + i)` 在 `1+i >= 位宽(64)` 时是 **UB**，且结果
                //   掩码为 0 ⇒ SetThreadGroupAffinity 静默失败/不绑核。worker 数由用户请求（可达数百），
                //   必须显式跳过超范围的核心；跨 processor group（cpuIndex ≥ 64）需要
                //   GROUP_AFFINITY.Group，这里不做 —— 保持"系统自选核心"比写入非法掩码安全。
                // 掩码计算提取为纯函数 ComputeAffinityMask（ThreadAffinity.h），
                // 使 `>= 位宽 ⇒ nullopt` 这一分支可被 AffinityMaskTests 覆盖。
                const auto mask = ComputeAffinityMask(i);
                if (mask.has_value())
                {
                    GROUP_AFFINITY affinity{};
                    affinity.Group = 0;
                    affinity.Mask = *mask;
                    ::SetThreadGroupAffinity(handle, &affinity, nullptr);
                }
            }
            else
            {
                // 清除：允许当前 group 所有核心
                GROUP_AFFINITY affinity{};
                affinity.Group = 0;
                affinity.Mask = static_cast<KAFFINITY>(~static_cast<KAFFINITY>(0));
                ::SetThreadGroupAffinity(handle, &affinity, nullptr);
            }
        }
#endif
    }

    // 本线程是否为本调度器的 worker（仅 WorkerLoop 入口置位）。用途：`Scheduler::Shutdown()`
    // 只拒绝**自己人**（worker 调 Shutdown 会 join 自身死锁），不拒绝其它非主线程调用 ——
    // 后者会让 `AppDomain.ProcessExit` 兜底关停（跑在运行时线程上）被误拒 ⇒ 关停统计整段不打印。
    static thread_local bool tl_isSchedulerWorker = false;

    bool ChaseLevScheduler_IsWorkerThread() noexcept { return tl_isSchedulerWorker; }

    // ============================================================
    // WorkerLoop — 标准 Chase-Lev 工作循环
    //
    //   1. PopBottom(myDeque)           — LIFO，owner-only，零竞争
    //   2. injector_.Pop → PushBottom   — 从 Injector 拉取推入 deque
    //   3. StealTop(otherDeque)         — 从其他 worker 窃取
    //   4. Park                         — atomic::wait epoch
    //   5. quit_ → 排空 deque 后退出
    // ============================================================

    void ChaseLevScheduler::WorkerLoop(uint32_t workerIndex, WorkerContext& ctx) noexcept
    {
        // 标记"当前线程是本调度器的 worker"（见 ChaseLevScheduler_IsWorkerThread 的用途：
        // 只有 worker 调 Shutdown 才会 join 自身死锁；其它非主线程调用是安全的）。
        tl_isSchedulerWorker = true;
        WorkerIndexManager::SetCurrentIndex(static_cast<int>(workerIndex));

#if defined(_WIN32)
        if (bindThreads_)
            BindCurrentThreadToLogicalProcessor(1 + workerIndex);
        ::SetThreadPriority(::GetCurrentThread(), THREAD_PRIORITY_NORMAL);
#endif

        SparseTileDeque* myDeque = ctx.deque.get();
        TileTask task;
        uint64_t seenStamp;  // park epoch（第 5 步赋值后 wait 使用）

        // ── 自适应自旋预算 ──
        // 执行过任务的 worker 拉满 kSpinMax → 新批到达仍自旋，零唤醒；空转指数退火
        // （下限 kSpinMin）快速让出 CPU；activeTasks>0 用更大窗口（kSpinBusy，下一任务即将被认领）。
        // thread_local：每 worker 独立一份。
        thread_local uint32_t spinBudget = kSpinBase;
        // `ENTJOY_WAKE_POLL`：本 worker 是否已登记在"搜索区"里（粘性，见第 5 步的注释）。
        const bool wakePoll = WakePollEnabled();
        bool idleRegistered = false;

        while (true)
        {
            bool got = false;

            // ---- 1. 本地 PopBottom（LIFO，owner-only，零竞争）----
            got = myDeque->PopBottom(task);
            if (got && workerIndex < kMaxTrackedWorkers)
                dequePopped[workerIndex].fetch_add(1, std::memory_order_relaxed);

            if (got && task.batch && task.tileCount > 0)
            {
                spinBudget = kSpinMax;   // 有活：拉高自旋预算
                // workerCap 令牌（firstTile==kClaimTokenMarker）：认领循环执行，内部已 taskDone
                if (task.firstTile == kClaimTokenMarker)
                {
                    ExecuteClaimToken(task.batch, workerIndex, task.batchGen);
                    continue;
                }
                // 记录 worker 进入批次时间（供 timing 诊断）
                ChaseLevRecordWorkerEntry(task.batch);

                // 执行从 deque 取出的任务
                if (workerIndex < kMaxTrackedWorkers)
                    tasksExecuted[workerIndex].fetch_add(1, std::memory_order_relaxed);
                g_localTiles.fetch_add(task.tileCount, std::memory_order_relaxed);
                g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);

                uint32_t end = task.firstTile + task.tileCount;
                if (end > task.batch->tileCount) end = task.batch->tileCount;

                DebugBeginExec(task.batch->diagnosticId, task.batch->tileCount,
                               task.batch->workerCount, false);
                SetCurrentBatchId(task.batch->diagnosticId);
                E1::Begin();
                if (workerIndex < kMaxTrackedWorkers)
                    workerCurrentBatch[workerIndex].store(
                        task.batch->diagnosticId, std::memory_order_relaxed);

                TileAcctGroupBegin();
                for (uint32_t t = task.firstTile; t < end; ++t)
                    executor_(task.batch, t);
                TileAcctGroupFlush(task.batch);

                if (workerIndex < kMaxTrackedWorkers)
                    workerCurrentBatch[workerIndex].store(0, std::memory_order_relaxed);
                E1::End(workerIndex);
                SetCurrentBatchId(0);
                DebugEndExec();

                // 所有执行的任务都需要 taskDone（pendingTasks--）
                if (taskDone_)
                {
                    activeTasks.fetch_sub(1, std::memory_order_acq_rel);
                    taskDone_(task.batch, task.batchGen);
                    totalTasksDone.fetch_add(1, std::memory_order_relaxed);
                }
                continue;
            }

            // ---- 2. 从 Injector 拉取（FIFO，1 CAS）----
            RangeTask* rangeTask = nullptr;
            if (injector_.Pop(rangeTask))
            {
                spinBudget = kSpinMax;   // 有活：拉高自旋预算
                // 通用 work（batch==nullptr）直接执行：TileTask 无 work 回调，入 deque 会丢失 work。
                if (rangeTask->batch == nullptr)
                {
                    RunWorkTask(rangeTask);
                    continue;
                }
                // 推入自己 deque（保持可窃取性 + LIFO 本地执行）：Injector → PushBottom → PopBottom
                myDeque->PushBottom(TileTask{
                    rangeTask->batch,
                    rangeTask->firstTile,
                    rangeTask->tileCount,
                    rangeTask->batchGen
                });
                if (workerIndex < kMaxTrackedWorkers)
                    dequePushed[workerIndex].fetch_add(1, std::memory_order_relaxed);

                // 释放 RangeTask 对象（已推入 deque，不再需要）
                s_taskPool_.Release(rangeTask);

                // 继续循环，下一轮 PopBottom 会取出执行
                continue;
            }

            // ---- 3. 从其他 worker deque 窃取（FIFO，1 CAS per victim）----
            g_stealAttempts.fetch_add(1, std::memory_order_relaxed);
            for (uint32_t offset = 1; offset < workerCount_; ++offset)
            {
                const uint32_t victimIdx = (workerIndex + offset) % workerCount_;
                // 空 deque 提前跳过（同上：空判语义不变，空转不再进全局计数）
                if (workers_[victimIdx]->deque->IsEmpty())
                    continue;
                g_victimScans.fetch_add(1, std::memory_order_relaxed);
                if (workers_[victimIdx]->deque->StealTop(task))
                {
                    got = true;
                    g_stealSuccesses.fetch_add(1, std::memory_order_relaxed);
                    g_stealCount.fetch_add(1, std::memory_order_relaxed);
                    if (workerIndex < kMaxTrackedWorkers)
                        dequeStolen[workerIndex].fetch_add(1, std::memory_order_relaxed);
                    break;
                }
            }
            if (!got)
                g_stealEmptyExits.fetch_add(1, std::memory_order_relaxed);

            if (got && task.batch && task.tileCount > 0)
            {
                spinBudget = kSpinMax;   // 有活（窃取成功）：拉高自旋预算
                // workerCap 令牌：认领循环执行，内部已 taskDone
                if (task.firstTile == kClaimTokenMarker)
                {
                    ExecuteClaimToken(task.batch, workerIndex, task.batchGen, TileAccount::Stolen);
                    continue;
                }
                // 记录 worker 进入批次时间（供 timing 诊断）
                ChaseLevRecordWorkerEntry(task.batch);

                if (workerIndex < kMaxTrackedWorkers)
                    tasksExecuted[workerIndex].fetch_add(1, std::memory_order_relaxed);
                g_stolenTiles.fetch_add(task.tileCount, std::memory_order_relaxed);
                g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);

                uint32_t end = task.firstTile + task.tileCount;
                if (end > task.batch->tileCount) end = task.batch->tileCount;

                DebugBeginExec(task.batch->diagnosticId, task.batch->tileCount,
                               task.batch->workerCount, false);
                SetCurrentBatchId(task.batch->diagnosticId);
                E1::Begin();
                if (workerIndex < kMaxTrackedWorkers)
                    workerCurrentBatch[workerIndex].store(
                        task.batch->diagnosticId, std::memory_order_relaxed);

                TileAcctGroupBegin();
                for (uint32_t t = task.firstTile; t < end; ++t)
                    executor_(task.batch, t);
                TileAcctGroupFlush(task.batch);

                if (workerIndex < kMaxTrackedWorkers)
                    workerCurrentBatch[workerIndex].store(0, std::memory_order_relaxed);
                E1::End(workerIndex);
                SetCurrentBatchId(0);
                DebugEndExec();

                // 所有执行的任务都需要 taskDone（pendingTasks--）
                if (taskDone_)
                {
                    activeTasks.fetch_sub(1, std::memory_order_acq_rel);
                    taskDone_(task.batch, task.batchGen);
                    totalTasksDone.fetch_add(1, std::memory_order_relaxed);
                }
                continue;
            }

            // ---- 4. 无工作 ----
        drain_quit:   // Stop 竞态入口：spin/park 期检测到 quit 直接进入排空退出
            if (quit_.load(std::memory_order_acquire))
            {
                // 这是"离开搜索区登记"的第二个出口（第一个是停靠协议入口）。注意**必须写在
                // quit 分支里**：本标签每轮都会经过（正常"没找到活"也落到这里），写在标签下会每轮
                // 清一次登记，退化成"每 job 一次共享行 RMW"。
                // 两个出口合起来覆盖了 worker 退出 while 循环的全部路径（唯二的 break 都在本分支里），
                // 所以登记计数严格配平 —— 残留 >0 会让提交侧永久跳过广播。
                if (wakePoll && idleRegistered)
                {
                    wakeIdlePollers.fetch_sub(1, std::memory_order_seq_cst);
                    idleRegistered = false;
                }
                // 退出前协作排空 Injector + 自己 deque，防止遗留任务永久悬挂。
                bool anyWork = true;
                while (anyWork)
                {
                    anyWork = false;

                    // 从 Injector 拉取（协作排空）
                    RangeTask* rangeTask = nullptr;
                    if (injector_.Pop(rangeTask))
                    {
                        anyWork = true;
                        ExecuteAndRelease(rangeTask, workerIndex);
                        continue; // 继续排空
                    }

                    // 从 deque 弹出
                    if (myDeque->PopBottom(task))
                    {
                        anyWork = true;
                        if (task.batch && task.tileCount > 0)
                        {
                            // workerCap 令牌：认领循环执行，内部已 taskDone
                            if (task.firstTile == kClaimTokenMarker)
                            {
                                ExecuteClaimToken(task.batch, workerIndex, task.batchGen);
                                continue;
                            }
                            uint32_t end2 = task.firstTile + task.tileCount;
                            if (end2 > task.batch->tileCount) end2 = task.batch->tileCount;
                            SetCurrentBatchId(task.batch->diagnosticId);
                            E1::Begin();
                            TileAcctGroupBegin();
                            for (uint32_t t = task.firstTile; t < end2; ++t)
                                executor_(task.batch, t);
                            TileAcctGroupFlush(task.batch);
                            E1::End(workerIndex);
                            SetCurrentBatchId(0);
                            // 从 deque 执行的任务也需要 taskDone
                            if (taskDone_)
                            {
                                activeTasks.fetch_sub(1, std::memory_order_acq_rel);
                                taskDone_(task.batch, task.batchGen);
                                totalTasksDone.fetch_add(1, std::memory_order_relaxed);
                            }
                        }
                    }
                }
                break;
            }

            // ---- 5. Park — 自适应自旋（覆盖新批到达窗口）+ atomic::wait（跨平台 futex）----
            {
                uint64_t spinStamp = wakeEpoch.load(std::memory_order_acquire);
                // activeTasks>0 → 更大自旋窗：与 JobCostCache 协同，轻任务塌缩后未参与
                // worker 停在自旋区，避免每帧重复 park+唤醒。
                const bool globalBusy =
                    activeTasks.load(std::memory_order_acquire) > 0;
                // `ENTJOY_SPIN_NEEDS_WORK`（默认**开**，`=0` 关闭）：大自旋窗只在"注入器里还有可认领的活"时才给。
                // 动机：批的 workerCount 被物理核封顶后（见 ApplyPhysCoreCapForSmallJob），未被唤醒的 worker
                //   仍因 `activeTasks>0` 拿 kSpinBusy 硬自旋整整一波 ⇒ 与真正干活的 worker 抢 SMT 执行单元。
                //   本开关让它们在注入器为空时走普通退火预算（→ 逐步 park）。
                const bool spinNeedsWork = SpinNeedsWorkEnabled();
                const bool busy = globalBusy &&
                    (!spinNeedsWork || !injector_.IsEmpty());
                const uint32_t spinCap = busy ? SpinBusyCap() : spinBudget;
                // `ENTJOY_WAKE_POLL`：登记"我在搜索区"——搜索区每轮都读注入器，所以登记中的
                // worker 一定能自己领到新 token；提交侧据此决定"一个字节都不写"。
                //
                // **粘性登记**：只在"进入停靠协议"时登记一次，突发期内保持登记 ⇒ 那条共享行几乎不被写。
                // 安全性：登记中的 worker 要么在搜索区里读注入器、要么在执行任务，执行完必然回主循环
                // 读注入器，而**离开登记态的唯一出口**是停靠协议入口——那里紧接着就是 fence + 最后一次
                // 读注入器（不变量 I2）。所以"提交侧看到有人在登记"⇒ 那个人的下一次读必然在 push 之后。
                if (wakePoll && !idleRegistered)
                {
                    wakeIdlePollers.fetch_add(1, std::memory_order_seq_cst);
                    idleRegistered = true;
                }
                uint32_t s = 0;
                while (s < spinCap)
                {
                    if (quit_.load(std::memory_order_acquire))
                        goto drain_quit;
                    // 新批/新任务 → 回主循环认领
                    if (wakeEpoch.load(std::memory_order_acquire) != spinStamp)
                    {
                        g_hotSpinHits.fetch_add(1, std::memory_order_relaxed);
                        goto main_loop;
                    }
                    if (!injector_.IsEmpty() || !myDeque->IsEmpty())
                    {
                        g_hotSpinHits.fetch_add(1, std::memory_order_relaxed);
                        goto main_loop;
                    }
                    CpuPause();
                    ++s;
                }
                // 本轮无活：指数退火（快速让出 CPU 回 park），下限 kSpinMin 保底。
                if (spinBudget > kSpinMin)
                    spinBudget /= 2;
                if (!injector_.IsEmpty())
                {
                    g_hotSpinHits.fetch_add(1, std::memory_order_relaxed);
                    goto main_loop;
                }
            }

// ---- 5b. Park — wait(共享 epoch) ----
            // 所有 worker wait 同一 epoch：一次 notify_all 唤醒全部（1 次 futex）。
            // 快照必须早于 quit/队列复查：否则 producer 在「检查空」与「读快照」之间
            // bump epoch + notify，worker 读到新 epoch 后 wait 会永久睡眠（lost-wakeup）。
            seenStamp = wakeEpoch.load(std::memory_order_acquire);
            if (WakePollEnabled())
            {
                // 停靠协议：**先退出"搜索区"登记 → 再登记 sleepers → 再 fence → 最后复查**（不变量 I2）。
                // 退出登记必须在这里（而不是搜索区出口）：粘性登记期间 worker 可能在执行任务，
                // 而"离开登记态"的这一刻起，它下面紧接的最后一次注入器复查就是提交侧依赖的那次读。
                if (idleRegistered)
                {
                    wakeIdlePollers.fetch_sub(1, std::memory_order_seq_cst);
                    idleRegistered = false;
                }
                parkedWorkers.fetch_add(1, std::memory_order_seq_cst);
                std::atomic_thread_fence(std::memory_order_seq_cst);   // SleepFence
                if (quit_.load(std::memory_order_acquire))
                {
                    parkedWorkers.fetch_sub(1, std::memory_order_seq_cst);
                    goto drain_quit;
                }
                // 最后一次复查：注入器 / 本线程 deque / epoch 三者任一有变化 ⇒ 不睡。
                if (!injector_.IsEmpty() || !myDeque->IsEmpty() ||
                    wakeEpoch.load(std::memory_order_acquire) != seenStamp)
                {
                    parkedWorkers.fetch_sub(1, std::memory_order_seq_cst);
                    continue;
                }
                wakeEpoch.wait(seenStamp, std::memory_order_relaxed);
                parkedWorkers.fetch_sub(1, std::memory_order_seq_cst);
                // 真的在 futex 上睡过并被唤醒（`[M-16] 唤醒=`）。
                g_parkWakeCount.fetch_add(1, std::memory_order_relaxed);
                continue; // 唤醒后回到主循环
            }
            if (quit_.load(std::memory_order_acquire))
                goto drain_quit;
            if (!injector_.IsEmpty() || !myDeque->IsEmpty())
                goto main_loop;
            // 登记"我在停靠"，随后**复查 epoch** 防丢失唤醒
            // （唤醒者先 bump epoch 再读本计数；若它读到 0 而跳过了广播，这里必能看到 epoch 已变 ⇒ 不睡）。
            parkedWorkers.fetch_add(1, std::memory_order_acq_rel);
            if (wakeEpoch.load(std::memory_order_acquire) != seenStamp)
            {
                parkedWorkers.fetch_sub(1, std::memory_order_acq_rel);
                continue;
            }
            wakeEpoch.wait(seenStamp, std::memory_order_relaxed);
            parkedWorkers.fetch_sub(1, std::memory_order_acq_rel);
            // 真的在 futex 上睡过并被唤醒（`[M-16] 唤醒=`）。
            g_parkWakeCount.fetch_add(1, std::memory_order_relaxed);
            continue; // 唤醒后回到主循环

        main_loop:
            ; // 回到 while(true) 顶部
        }
        // worker 线程退出主循环时，把本线程计数尾巴（<1024 次的部分）合并进全局，
        // 否则读取侧永远看不到它。
        WakePollFlushCurrentThread();
    }

    // ============================================================
    // 查询
    // ============================================================

    bool ChaseLevScheduler::IsRunning() const noexcept
    {
        return running_.load(std::memory_order_acquire);
    }

    uint32_t ChaseLevScheduler::WorkerCount() const noexcept
    {
        return workerCount_;
    }

    SparseTileDeque* ChaseLevScheduler::GetWorkerDeque(uint32_t workerIndex) noexcept
    {
        if (workerIndex >= workers_.size()) return nullptr;
        return workers_[workerIndex]->deque.get();
    }

    void ChaseLevScheduler::DumpState(const char* tag) const noexcept
    {
        // 把**唤醒决策所依赖的两个量**也打出来（idlePollers / parkedWorkers）。丢唤醒的诊断
        // 全在这两个数上：`idle>0` 说明提交侧认为"有人会自己领到"，`parked==W` 说明那一刻其实
        // 全员在 futex 上（此时不写唤醒字就是丢唤醒）。
        std::fprintf(stderr, "[ChaseLev:%s] workers=%zu quit=%d running=%d pushed=%llu done=%llu injector=%u"
            " idlePollers=%llu parked=%d wakePoll=%d\n",
            tag, workers_.size(),
            (int)quit_.load(std::memory_order_acquire),
            (int)running_.load(std::memory_order_acquire),
            (unsigned long long)totalTasksPushed.load(std::memory_order_relaxed),
            (unsigned long long)totalTasksDone.load(std::memory_order_relaxed),
            injector_.ApproxSize(),
            (unsigned long long)wakeIdlePollers.load(std::memory_order_acquire),
            parkedWorkers.load(std::memory_order_acquire),
            (int)WakePollEnabled());
        for (uint32_t i = 0; i < workers_.size(); ++i)
        {
            const auto& dq = *workers_[i]->deque;
            std::fprintf(stderr, "  worker[%u] empty=%d approx=%u curBatch=%llu"
                " dqP=%llu dqC=%llu dqS=%llu exec=%llu\n",
                i, (int)dq.IsEmpty(), dq.ApproxSize(),
                (unsigned long long)workerCurrentBatch[i].load(std::memory_order_relaxed),
                (unsigned long long)dequePushed[i].load(std::memory_order_relaxed),
                (unsigned long long)dequePopped[i].load(std::memory_order_relaxed),
                (unsigned long long)dequeStolen[i].load(std::memory_order_relaxed),
                (unsigned long long)tasksExecuted[i].load(std::memory_order_relaxed));
        }
        std::fflush(stderr);
    }
} // namespace JobSystem
