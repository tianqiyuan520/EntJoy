#include "ChaseLevScheduler.h"
#include "JobProfiler.h"
#include "ThreadAffinity.h"

#include <algorithm>
#include <cstdio>
#include <thread>

#if defined(_WIN32)
#include <windows.h>
#endif

namespace JobSystem
{
    // ============================================================
    // 全局 RangeTask 池定义
    // ============================================================
    RangeTaskPool ChaseLevScheduler::s_taskPool_;

    // ============================================================
    // 构造 / 析构
    // ============================================================

    ChaseLevScheduler::ChaseLevScheduler() = default;

    ChaseLevScheduler::~ChaseLevScheduler() { Stop(); }

    // ============================================================
    // 自旋 pause
    // ============================================================

    static inline void CpuPause() noexcept
    {
#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
        _mm_pause();
#else
        std::atomic_signal_fence(std::memory_order_seq_cst);
#endif
    }

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

    void ChaseLevScheduler::ExecuteAndRelease(RangeTask* task, uint32_t workerIndex) noexcept
    {
        if (!task) return;
        if (!task->batch)
        {
            RunWorkTask(task);   // 无 batch 的通用 work（完成链由调用方负责）
            return;
        }
        if (task->firstTile == kClaimTokenMarker)
        {
            ExecuteClaimToken(task->batch, workerIndex);   // workerCap 令牌：原子认领，内部已 taskDone
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
        if (workerIndex < kMaxTrackedWorkers)
            workerCurrentBatch[workerIndex].store(batch->diagnosticId, std::memory_order_relaxed);

        // 执行 tile 范围
        for (uint32_t t = task->firstTile; t < end; ++t)
            executor_(batch, t);

        if (workerIndex < kMaxTrackedWorkers)
            workerCurrentBatch[workerIndex].store(0, std::memory_order_relaxed);
        DebugEndExec();

        // 诊断计数
        if (workerIndex < kMaxTrackedWorkers)
            tasksExecuted[workerIndex].fetch_add(1, std::memory_order_relaxed);

        // 任务完成：pendingTasks--（可能触发退役）
        if (taskDone_)
        {
            activeTasks.fetch_sub(1, std::memory_order_acq_rel);
            taskDone_(batch);
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
            ExecuteAndRelease(task, workerIndex);
            // 主线程 assist 计数
            g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
            g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
            g_assistTiles.fetch_add(task->tileCount, std::memory_order_relaxed);
            return true;
        }

        // 2. 从其他 worker deque 窃取（FIFO，1 CAS per victim）
        for (uint32_t offset = 1; offset < workerCount_; ++offset)
        {
            const uint32_t victimIdx = (workerIndex + offset) % workerCount_;
            TileTask tileTask;
            if (workers_[victimIdx]->deque->StealTop(tileTask))
            {
                if (workerIndex < kMaxTrackedWorkers)
                    dequeStolen[workerIndex].fetch_add(1, std::memory_order_relaxed);

                // 从 deque 窃取的是 TileTask，需要转换为 RangeTask 处理
                if (tileTask.batch && tileTask.tileCount > 0)
                {
                    // workerCap 令牌：主线程 assist 认领循环执行，内部已 taskDone
                    if (tileTask.firstTile == kClaimTokenMarker)
                    {
                        ExecuteClaimToken(tileTask.batch, workerIndex);
                        return true;
                    }
                    // 记录 worker 进入批次时间（供 timing 诊断）
                    ChaseLevRecordWorkerEntry(tileTask.batch);

                    // 创建临时 RangeTask 执行（不走池，因为是从 deque 窃取的）
                    RangeTask tempTask;
                    tempTask.batch = tileTask.batch;
                    tempTask.firstTile = tileTask.firstTile;
                    tempTask.tileCount = tileTask.tileCount;

                    DebugBeginExec(tileTask.batch->diagnosticId, tileTask.batch->tileCount,
                                   tileTask.batch->workerCount, false);
                    SetCurrentBatchId(tileTask.batch->diagnosticId);
                    if (workerIndex < kMaxTrackedWorkers)
                        workerCurrentBatch[workerIndex].store(
                            tileTask.batch->diagnosticId, std::memory_order_relaxed);

                    const uint32_t end = std::min(tempTask.firstTile + tempTask.tileCount,
                                                  tempTask.batch->tileCount);
                    for (uint32_t t = tempTask.firstTile; t < end; ++t)
                        executor_(tempTask.batch, t);

                    if (workerIndex < kMaxTrackedWorkers)
                        workerCurrentBatch[workerIndex].store(0, std::memory_order_relaxed);
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
                        taskDone_(tileTask.batch);
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
            Stop();
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
        workers_.clear();
    }

    // ============================================================
    // SubmitBatch — 预切分为 RangeTask 推入 Injector，唤醒 worker
    // 标准 Chase-Lev：任务经 Injector 分发，worker 从 Injector 拉取推入 deque。
    // ============================================================

    // Injector 满：有限退避（yield + pause），避免提交线程 busy-loop
    void ChaseLevScheduler::PushTaskBackoff(RangeTask* task) noexcept
    {
        uint32_t backoff = 0;
        while (!injector_.Push(task))
        {
            ++backoff;
            if ((backoff & 15) == 0)
                std::this_thread::yield();
            else
                CpuPause();
            if (backoff > 4096) { std::this_thread::yield(); backoff = 0; }
        }
    }

    void ChaseLevScheduler::SubmitBatch(BatchState* batch) noexcept
    {
        if (!batch || batch->tileCount == 0) return;
        const uint32_t wc = workerCount_;
        if (wc == 0) return;

        const uint32_t tileCount = batch->tileCount;

        // ── workerCap 令牌模式 ──
        // workerCount（=ResolveWorkerTarget(workerCap,..)）< 全局线程数 → 限制实际并行：
        // 投 min(workerCount, tileCount) 个令牌，令牌内原子认领 nextTile（实际参与 ≤ 令牌数）。
        if (batch->workerCount > 0 &&
            static_cast<uint32_t>(batch->workerCount) < wc)
        {
            const uint32_t tokenCount = std::min(
                static_cast<uint32_t>(batch->workerCount), tileCount);
            batch->pendingTasks.store(tokenCount, std::memory_order_release);
            activeTasks.fetch_add(static_cast<int64_t>(tokenCount), std::memory_order_acq_rel);
            for (uint32_t i = 0; i < tokenCount; ++i)
            {
                RangeTask* task = s_taskPool_.Acquire();
                if (!task)
                {
                    task = new RangeTask();   // 池耗尽兜底（poolIndex=UINT32_MAX → Release 时 delete）
                    task->poolIndex = UINT32_MAX;
                }
                task->batch = batch;
                task->firstTile = kClaimTokenMarker;
                task->tileCount = 1;
                PushTaskBackoff(task);
            }
            totalTasksPushed.fetch_add(tokenCount, std::memory_order_relaxed);
            wakeEpoch.fetch_add(1, std::memory_order_release);
            wakeEpoch.notify_all();
            return;
        }

        // ── 默认：全 worker 并行（预切分 ≈wc×16 任务，平衡预切分开销与负载均衡）──
        uint32_t claimBatch = std::max(1u, (tileCount + wc * 16 - 1) / (wc * 16));

        // 池容量保险：taskCount ≤ 池容量/2，防止游标回绕复用未释放任务
        if (tileCount / claimBatch >= RangeTaskPool::kPoolSize / 2)
        {
            while (claimBatch < tileCount &&
                   (tileCount + claimBatch - 1) / claimBatch >=
                       RangeTaskPool::kPoolSize / 2)
                claimBatch *= 2;
        }
        const uint32_t taskCount = (tileCount + claimBatch - 1) / claimBatch;

        batch->pendingTasks.store(taskCount, std::memory_order_release);
        activeTasks.fetch_add(static_cast<int64_t>(taskCount), std::memory_order_acq_rel);

        for (uint32_t i = 0; i < taskCount; ++i)
        {
            // 池耗尽兜底：堆分配（poolIndex=UINT32_MAX，Release 时 delete）。不可跳过任务——
            // pendingTasks 预设 taskCount，跳过会导致永不到 0。
            RangeTask* task = s_taskPool_.Acquire();
            if (!task)
            {
                task = new RangeTask();
                task->poolIndex = UINT32_MAX;
            }

            task->batch = batch;
            task->firstTile = i * claimBatch;
            task->tileCount = std::min(claimBatch, tileCount - task->firstTile);

            PushTaskBackoff(task);
        }

        totalTasksPushed.fetch_add(taskCount, std::memory_order_relaxed);

        // 单 epoch 广播唤醒全部 worker（1 次 futex）。保持 wake-all——选择性唤醒有 35ms 尖峰教训。
        wakeEpoch.fetch_add(1, std::memory_order_release);
        wakeEpoch.notify_all();
    }

    // ============================================================
    // ExecuteClaimToken — workerCap 令牌：原子认领 nextTile 直到空
    // ============================================================

    void ChaseLevScheduler::ExecuteClaimToken(BatchState* batch, uint32_t workerIndex) noexcept
    {
        if (!batch) return;
        // timing 诊断：记录 worker 进入批次（首/末 worker 时间）
        ChaseLevRecordWorkerEntry(batch);
        DebugBeginExec(batch->diagnosticId, batch->tileCount, batch->workerCount, false);
        SetCurrentBatchId(batch->diagnosticId);
        if (workerIndex < kMaxTrackedWorkers)
            workerCurrentBatch[workerIndex].store(batch->diagnosticId, std::memory_order_relaxed);

        const uint32_t end = batch->tileCount;
        uint32_t executed = 0;
        while (true)
        {
            const uint32_t start = batch->nextTile.fetch_add(
                kClaimBatchSize, std::memory_order_relaxed);
            if (start >= end) break;
            const uint32_t last = std::min(end, start + kClaimBatchSize);
            for (uint32_t t = start; t < last; ++t)
            {
                executor_(batch, t);
                ++executed;
            }
        }
        // 令牌认领的 tile 计入本地 tile 统计（tile 口径，与 RangeTask 执行一致）
        g_localTiles.fetch_add(executed, std::memory_order_relaxed);
        g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);

        if (workerIndex < kMaxTrackedWorkers)
            workerCurrentBatch[workerIndex].store(0, std::memory_order_relaxed);
        DebugEndExec();
        if (workerIndex < kMaxTrackedWorkers)
            tasksExecuted[workerIndex].fetch_add(1, std::memory_order_relaxed);

        // 令牌完成：pendingTasks--（双条件退役由 ChaseLevTaskDone 检查）
        if (taskDone_)
        {
            activeTasks.fetch_sub(1, std::memory_order_acq_rel);
            taskDone_(batch);
            totalTasksDone.fetch_add(1, std::memory_order_relaxed);
        }
    }

    // ============================================================
    // SubmitWork — 通用 work 任务（无 batch）
    // ============================================================

    void ChaseLevScheduler::SubmitWork(void (*fn)(void*), void* ctx, void (*cleanup)(void*)) noexcept
    {
        if (!fn) return;
        RangeTask* task = s_taskPool_.Acquire();
        if (!task)
        {
            task = new RangeTask();   // 池耗尽兜底（poolIndex=UINT32_MAX → Release 时 delete）
            task->poolIndex = UINT32_MAX;
        }
        task->batch = nullptr;
        task->firstTile = 0;
        task->tileCount = 0;
        task->workFn = fn;
        task->workCtx = ctx;
        task->workCleanup = cleanup;

        PushTaskBackoff(task);

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
            HANDLE handle = ctx->thread.native_handle();
            if (enabled)
            {
                // 绑定逻辑核心 1+i（与 WorkerLoop 启动时一致）
                GROUP_AFFINITY affinity{};
                affinity.Group = 0;
                affinity.Mask = static_cast<KAFFINITY>(1) << (1 + i);
                ::SetThreadGroupAffinity(handle, &affinity, nullptr);
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
        WorkerIndexManager::SetCurrentIndex(static_cast<int>(workerIndex));

#if defined(_WIN32)
        if (bindThreads_)
            BindCurrentThreadToLogicalProcessor(1 + workerIndex);
        ::SetThreadPriority(::GetCurrentThread(), THREAD_PRIORITY_NORMAL);
#endif

        SparseTileDeque* myDeque = ctx.deque.get();
        TileTask task;
        uint64_t seenStamp;  // park epoch（第 5 步赋值后 wait 使用）

        while (true)
        {
            bool got = false;

            // ---- 1. 本地 PopBottom（LIFO，owner-only，零竞争）----
            got = myDeque->PopBottom(task);
            if (got && workerIndex < kMaxTrackedWorkers)
                dequePopped[workerIndex].fetch_add(1, std::memory_order_relaxed);

            if (got && task.batch && task.tileCount > 0)
            {
                // workerCap 令牌（firstTile==kClaimTokenMarker）：认领循环执行，内部已 taskDone
                if (task.firstTile == kClaimTokenMarker)
                {
                    ExecuteClaimToken(task.batch, workerIndex);
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
                if (workerIndex < kMaxTrackedWorkers)
                    workerCurrentBatch[workerIndex].store(
                        task.batch->diagnosticId, std::memory_order_relaxed);

                for (uint32_t t = task.firstTile; t < end; ++t)
                    executor_(task.batch, t);

                if (workerIndex < kMaxTrackedWorkers)
                    workerCurrentBatch[workerIndex].store(0, std::memory_order_relaxed);
                DebugEndExec();

                // 所有执行的任务都需要 taskDone（pendingTasks--）
                if (taskDone_)
                {
                    activeTasks.fetch_sub(1, std::memory_order_acq_rel);
                    taskDone_(task.batch);
                    totalTasksDone.fetch_add(1, std::memory_order_relaxed);
                }
                continue;
            }

            // ---- 2. 从 Injector 拉取（FIFO，1 CAS）----
            RangeTask* rangeTask = nullptr;
            if (injector_.Pop(rangeTask))
            {
                // 通用 work 任务（batch==nullptr）：直执行（不能 PushBottom 到 deque，
                // TileTask 无 work 回调，入 deque 会丢失 work）。
                if (rangeTask->batch == nullptr)
                {
                    RunWorkTask(rangeTask);
                    continue;
                }
                // 推入自己 deque（保持可窃取性 + LIFO 本地执行语义）
                // 标准 Chase-Lev：任务经 Injector → PushBottom → PopBottom 执行
                myDeque->PushBottom(TileTask{
                    rangeTask->batch,
                    rangeTask->firstTile,
                    rangeTask->tileCount
                });
                if (workerIndex < kMaxTrackedWorkers)
                    dequePushed[workerIndex].fetch_add(1, std::memory_order_relaxed);

                // 释放 RangeTask 对象（已推入 deque，不再需要）
                s_taskPool_.Release(rangeTask);

                // 继续循环，下一轮 PopBottom 会取出执行
                continue;
            }

            // ---- 3. 从其他 worker deque 窃取（FIFO，1 CAS per victim）----
            for (uint32_t offset = 1; offset < workerCount_; ++offset)
            {
                const uint32_t victimIdx = (workerIndex + offset) % workerCount_;
                if (workers_[victimIdx]->deque->StealTop(task))
                {
                    got = true;
                    if (workerIndex < kMaxTrackedWorkers)
                        dequeStolen[workerIndex].fetch_add(1, std::memory_order_relaxed);
                    break;
                }
            }

            if (got && task.batch && task.tileCount > 0)
            {
                // workerCap 令牌：认领循环执行，内部已 taskDone
                if (task.firstTile == kClaimTokenMarker)
                {
                    ExecuteClaimToken(task.batch, workerIndex);
                    continue;
                }
                // 记录 worker 进入批次时间（供 timing 诊断）
                ChaseLevRecordWorkerEntry(task.batch);

                if (workerIndex < kMaxTrackedWorkers)
                    tasksExecuted[workerIndex].fetch_add(1, std::memory_order_relaxed);
                g_localTiles.fetch_add(task.tileCount, std::memory_order_relaxed);
                g_workerExecutedRanges.fetch_add(1, std::memory_order_relaxed);

                uint32_t end = task.firstTile + task.tileCount;
                if (end > task.batch->tileCount) end = task.batch->tileCount;

                DebugBeginExec(task.batch->diagnosticId, task.batch->tileCount,
                               task.batch->workerCount, false);
                SetCurrentBatchId(task.batch->diagnosticId);
                if (workerIndex < kMaxTrackedWorkers)
                    workerCurrentBatch[workerIndex].store(
                        task.batch->diagnosticId, std::memory_order_relaxed);

                for (uint32_t t = task.firstTile; t < end; ++t)
                    executor_(task.batch, t);

                if (workerIndex < kMaxTrackedWorkers)
                    workerCurrentBatch[workerIndex].store(0, std::memory_order_relaxed);
                DebugEndExec();

                // 所有执行的任务都需要 taskDone（pendingTasks--）
                if (taskDone_)
                {
                    activeTasks.fetch_sub(1, std::memory_order_acq_rel);
                    taskDone_(task.batch);
                    totalTasksDone.fetch_add(1, std::memory_order_relaxed);
                }
                continue;
            }

            // ---- 4. 无工作 ----
        drain_quit:   // Stop 竞态入口：spin/park 期检测到 quit 直接进入排空退出
            if (quit_.load(std::memory_order_acquire))
            {
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
                                ExecuteClaimToken(task.batch, workerIndex);
                                continue;
                            }
                            uint32_t end2 = task.firstTile + task.tileCount;
                            if (end2 > task.batch->tileCount) end2 = task.batch->tileCount;
                            SetCurrentBatchId(task.batch->diagnosticId);
                            for (uint32_t t = task.firstTile; t < end2; ++t)
                                executor_(task.batch, t);
                            SetCurrentBatchId(0);
                            // 从 deque 执行的任务也需要 taskDone
                            if (taskDone_)
                            {
                                activeTasks.fetch_sub(1, std::memory_order_acq_rel);
                                taskDone_(task.batch);
                                totalTasksDone.fetch_add(1, std::memory_order_relaxed);
                            }
                        }
                    }
                }
                break;
            }

            // ---- 5. Park — 有界自旋（覆盖新批到达窗口）+ atomic::wait（跨平台 futex）----
            {
                uint64_t spinStamp = wakeEpoch.load(std::memory_order_acquire);
                uint32_t s = 0;
                while (s < 256)
                {
                    if (quit_.load(std::memory_order_acquire))
                        goto drain_quit;
                    // 新批/新任务 → 回主循环认领
                    if (wakeEpoch.load(std::memory_order_acquire) != spinStamp)
                        goto main_loop;
                    if (!injector_.IsEmpty() || !myDeque->IsEmpty())
                        goto main_loop;
                    CpuPause();
                    ++s;
                }
                if (!injector_.IsEmpty())
                    goto main_loop;
            }

// ---- 5b. Park — wait(共享 epoch) ----
            // 所有 worker wait 同一 epoch：SubmitBatch/Stop 一次 notify_all 唤醒全部（1 次 futex）。
            // park 前必须复查 quit：Stop 的 epoch bump 可能已发生在 seenStamp 捕获之后，
            // 无复查则 wait 永久挂起（Stop join 死锁）。
            if (quit_.load(std::memory_order_acquire))
                goto drain_quit;
            seenStamp = wakeEpoch.load(std::memory_order_acquire);
            wakeEpoch.wait(seenStamp, std::memory_order_relaxed);
            continue; // 唤醒后回到主循环

        main_loop:
            ; // 回到 while(true) 顶部
        }
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
        std::fprintf(stderr, "[ChaseLev:%s] workers=%zu quit=%d running=%d pushed=%llu done=%llu injector=%u\n",
            tag, workers_.size(),
            (int)quit_.load(std::memory_order_acquire),
            (int)running_.load(std::memory_order_acquire),
            (unsigned long long)totalTasksPushed.load(std::memory_order_relaxed),
            (unsigned long long)totalTasksDone.load(std::memory_order_relaxed),
            injector_.ApproxSize());
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
