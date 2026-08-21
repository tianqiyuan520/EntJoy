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
    // 构造 / 析构
    // ============================================================

    ChaseLevScheduler::ChaseLevScheduler() = default;

    ChaseLevScheduler::~ChaseLevScheduler() { Stop(); }

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
        for (int i = 0; i < kMaxTrackedWorkers; ++i)
        {
            dequePushed[i].store(0, std::memory_order_relaxed);
            dequePopped[i].store(0, std::memory_order_relaxed);
            dequeStolen[i].store(0, std::memory_order_relaxed);
            tasksExecuted[i].store(0, std::memory_order_relaxed);
        }

        // 清空认领注册表（Start 前可能残留 Stop 未清的状态）
        for (auto& slot : claimSlots_)
        {
            slot.batch.store(nullptr, std::memory_order_relaxed);
            slot.claimers.store(0, std::memory_order_relaxed);
        }
        claimSlotCursor_.store(0, std::memory_order_relaxed);

        try
        {
            workers_.reserve(workerCount);
            for (uint32_t i = 0; i < workerCount; ++i)
            {
                auto ctx = std::make_unique<WorkerContext>();
                ctx->deque = std::make_unique<SparseTileDeque>(kDequeCapacity);
#if defined(_WIN32)
                // manual-reset event：初始无信号，SetEvent 唤醒，ResetEvent 重置。
                ctx->wakeEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
#endif
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

        // 唤醒所有 worker 退出
        for (auto& ctx : workers_)
        {
#if defined(_WIN32)
            if (ctx->wakeEvent)
                ::SetEvent(ctx->wakeEvent);
#endif
        }

        for (auto& ctx : workers_)
        {
            if (ctx->thread.joinable())
                ctx->thread.join();
#if defined(_WIN32)
            if (ctx->wakeEvent) { ::CloseHandle(ctx->wakeEvent); ctx->wakeEvent = nullptr; }
#endif
        }
        workers_.clear();
    }

    // ============================================================
    // 共享认领注册表（标准 Chase-Lev Injector 角色）
    // ============================================================

    void ChaseLevScheduler::RegisterBatch(BatchState* batch) noexcept
    {
        // round-robin 起始扫描空槽；释放语义时序在 UnregisterBatch 的 claimers
        // handshake 上（先清槽再等认领者归零→ReleaseBatch 在调用方）。
        for (uint32_t attempt = 0; attempt < kMaxClaimableBatches; ++attempt)
        {
            const uint32_t idx = (claimSlotCursor_.fetch_add(1, std::memory_order_relaxed)
                + attempt) % kMaxClaimableBatches;
            auto& slot = claimSlots_[idx];
            BatchState* expected = nullptr;
            // 空 slot 且 claimers==0（无人认领）才可注册
            if (slot.claimers.load(std::memory_order_acquire) == 0 &&
                slot.batch.compare_exchange_strong(expected, batch,
                    std::memory_order_acq_rel, std::memory_order_acquire))
            {
                return;
            }
        }
        // 注册表满（病理：并发在飞 batch > 容量）→ 自旋等空槽（不该发生）
        std::fprintf(stderr, "[ChaseLev] WARNING: claimable-batch table full, spinning\n");
        std::fflush(stderr);
        while (true)
        {
            for (uint32_t attempt = 0; attempt < kMaxClaimableBatches; ++attempt)
            {
                const uint32_t idx = (attempt) % kMaxClaimableBatches;
                auto& slot = claimSlots_[idx];
                BatchState* expected = nullptr;
                if (slot.claimers.load(std::memory_order_acquire) == 0 &&
                    slot.batch.compare_exchange_strong(expected, batch,
                        std::memory_order_acq_rel, std::memory_order_acquire))
                {
                    return;
                }
            }
            std::this_thread::yield();
        }
    }

    void ChaseLevScheduler::UnregisterBatch(BatchState* batch) noexcept
    {
        // 找到 batch 所在的槽，清空指针。
        for (auto& slot : claimSlots_)
        {
            BatchState* cur = slot.batch.load(std::memory_order_acquire);
            if (cur == batch)
            {
                // 先清指针（新认领者看到 null 跳过），再等正认领中的 worker 归零。
                // 清指针与等 claimers 之间的排序保证：正在 claimers++ 的 worker 要么
                // 在清指针前完成（其认领已在 lastTile 执行前完成），要么在清后看到
                // null 而放弃。claimers==0 后才允许调用方 ReleaseBatch。
                slot.batch.store(nullptr, std::memory_order_release);
                while (slot.claimers.load(std::memory_order_acquire) != 0)
                    std::this_thread::yield();
                return;
            }
        }
        // 未找到（可能注册表被 Stop 清空 / 从未来得及注册）——直接忽略。
    }

    bool ChaseLevScheduler::ClaimRange(
        BatchState* batch, uint32_t& first, uint32_t& count) noexcept
    {
        // 调用前提：调用方已确认 batch 在注册表中且已通过 claimers handshake。
        // 动态认领一片 tile：nextTile.fetch_add(kClaim)，返回值即本 worker 认领的
        // 起点；越界（>= tileCount）→ 无任务返回 false。
        const uint32_t start = batch->nextTile.fetch_add(
            kClaimBatchSize, std::memory_order_acq_rel);
        if (start >= batch->tileCount) return false;
        first = start;
        count = std::min(kClaimBatchSize, batch->tileCount - start);
        return true;
    }

    // ============================================================
    // SubmitBatch — 注册 batch 到共享认领表，唤醒所有 worker
    // 可被任意线程调用（主线程 / 依赖 continuation 的 worker 线程）。
    // 任务不再预切分进私有注入队列：worker 从 nextTile.fetch_add 动态认领。
    // ============================================================

    void ChaseLevScheduler::SubmitBatch(BatchState* batch) noexcept
    {
        if (!batch || batch->tileCount == 0) return;
        const uint32_t wc = workerCount_;
        if (wc == 0) return;

        // 任务总数按认领粒度预置 pendingTasks（防 use-after-free：退役需等所有任务完成）；
        // 实际认领数 = ceil(tileCount/kClaim)，与 taskDone 次数一致。
        const uint32_t taskCount = (batch->tileCount + kClaimBatchSize - 1) / kClaimBatchSize;
        batch->pendingTasks.store(taskCount, std::memory_order_release);

        // 注册该 batch 到共享认领表（worker 可开始认领）
        RegisterBatch(batch);
        totalTasksPushed.fetch_add(taskCount, std::memory_order_relaxed);

        // 唤醒所有 worker（他们可能正在 park）
        for (auto& ctx : workers_)
        {
#if defined(_WIN32)
            if (ctx->wakeEvent)
                ::SetEvent(ctx->wakeEvent);
#endif
        }
    }

    // ============================================================
    // WorkerLoop — Chase-Lev 工作循环
    //
    //   1. 从自己 deque PopBottom（LIFO，owner-only）
    //   2. 空 → 扫描共享认领注册表：claimers 上锁 → 确认 batch 仍注册 →
    //      nextTile.fetch_add(kClaim) 认领一片 → 推入自己 deque → 解锁
    //   3. 仍空 → 从其他 worker deque StealTop（FIFO，CAS）
    //   4. 全空 → park（event wait）→ 被唤醒后重试
    //   5. quit_ → 排空 deque 后退出
    // ============================================================

    void ChaseLevScheduler::WorkerLoop(uint32_t workerIndex, WorkerContext& ctx) noexcept
    {
        // 调试面板：预分配泳道索引
        WorkerIndexManager::SetCurrentIndex(static_cast<int>(workerIndex));

#if defined(_WIN32)
        if (bindThreads_)
            BindCurrentThreadToLogicalProcessor(1 + workerIndex);
        ::SetThreadPriority(::GetCurrentThread(), THREAD_PRIORITY_NORMAL);
#endif

        SparseTileDeque* myDeque = ctx.deque.get();
        TileTask task;

        while (true)
        {
            bool got = false;

            // ---- 1. 本地 PopBottom（LIFO，owner-only，无竞争）----
            got = myDeque->PopBottom(task);
            if (got && workerIndex < kMaxTrackedWorkers)
                dequePopped[workerIndex].fetch_add(1, std::memory_order_relaxed);

            if (!got)
            {
                // ---- 1.5 扫描共享认领注册表：动态认领 tile 范围到自己的 deque ----
                // 认领前必须确认 batch 仍注册（不被退役回收）。用槽上 claimers 计数
                // handshake：claimers++ 后重读 batch 指针，若已被 UnregisterBatch 清空
                // （batch 正在退役）则放弃本次认领。退役方等 claimers==0 才 ReleaseBatch，
                // 因此持有 claimers 的 worker 认领临界内 batch 保证存活。
                for (uint32_t s = 0; s < kMaxClaimableBatches; ++s)
                {
                    auto& slot = claimSlots_[s];
                    BatchState* b = slot.batch.load(std::memory_order_acquire);
                    if (!b) continue;
                    slot.claimers.fetch_add(1, std::memory_order_acq_rel);
                    // 重读：若 batch 已被清槽（退役中），放弃本次认领
                    if (slot.batch.load(std::memory_order_acquire) != b)
                    {
                        slot.claimers.fetch_sub(1, std::memory_order_acq_rel);
                        continue;
                    }
                    uint32_t first = 0, cnt = 0;
                    if (ClaimRange(b, first, cnt))
                    {
                        // 认领成功：推入自己的 deque（owner-only PushBottom）
                        myDeque->PushBottom(TileTask{ b, first, cnt });
                        if (workerIndex < kMaxTrackedWorkers)
                            dequePushed[workerIndex].fetch_add(1, std::memory_order_relaxed);
                        slot.claimers.fetch_sub(1, std::memory_order_acq_rel);
                        break; // 拿到一片即可；deque 里已有可执行的
                    }
                    slot.claimers.fetch_sub(1, std::memory_order_acq_rel);
                }
                got = myDeque->PopBottom(task);
                if (got && workerIndex < kMaxTrackedWorkers)
                    dequePopped[workerIndex].fetch_add(1, std::memory_order_relaxed);
            }

            // ---- 2. 跨 worker StealTop（FIFO，CAS）----
            if (!got)
            {
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
            }

            // ---- 3. 执行整个范围任务 ----
            if (got && task.batch && task.tileCount > 0)
            {
                if (workerIndex < kMaxTrackedWorkers)
                    tasksExecuted[workerIndex].fetch_add(1, std::memory_order_relaxed);
                uint32_t end = task.firstTile + task.tileCount;
                if (end > task.batch->tileCount) end = task.batch->tileCount; // 防越界

                // 调试面板（一次窗口覆盖整个范围任务）
                DebugBeginExec(task.batch->diagnosticId, task.batch->tileCount,
                               task.batch->workerCount, false);
                SetCurrentBatchId(task.batch->diagnosticId);
                if (workerIndex < kMaxTrackedWorkers)
                    workerCurrentBatch[workerIndex].store(
                        task.batch->diagnosticId, std::memory_order_relaxed);

                for (uint32_t t = task.firstTile; t < end; ++t)
                    executor_(task.batch, t);

                // 任务完成：pendingTasks--（可能触发双条件退役）
                if (taskDone_)
                {
                    taskDone_(task.batch);
                    totalTasksDone.fetch_add(1, std::memory_order_relaxed);
                }

                if (workerIndex < kMaxTrackedWorkers)
                    workerCurrentBatch[workerIndex].store(0, std::memory_order_relaxed);
                SetCurrentBatchId(0);
                DebugEndExec();
                continue;
            }

            // ---- 4. 无工作 ----
            if (quit_.load(std::memory_order_acquire))
            {
                // 退出前排空剩余 tiles（防止丢失）
                while (myDeque->PopBottom(task))
                {
                    if (task.batch && task.tileCount > 0)
                    {
                        const uint32_t end2 = task.firstTile + task.tileCount;
                        SetCurrentBatchId(task.batch->diagnosticId);
                        for (uint32_t t = task.firstTile; t < end2; ++t)
                            executor_(task.batch, t);
                        SetCurrentBatchId(0);
                    }
                }
                break;
            }

            // ---- 5. Park — 等待唤醒 ----
#if defined(_WIN32)
            if (ctx.wakeEvent)
            {
                ::WaitForSingleObject(ctx.wakeEvent, 1/*ms 超时，防假醒*/);
                ::ResetEvent(ctx.wakeEvent);
            }
#else
            // 非 Windows：短暂 yield 避免空转
            std::this_thread::yield();
#endif
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
        std::fprintf(stderr, "[ChaseLev:%s] workers=%zu quit=%d running=%d pushed=%llu done=%llu\n",
            tag, workers_.size(),
            (int)quit_.load(std::memory_order_acquire),
            (int)running_.load(std::memory_order_acquire),
            (unsigned long long)totalTasksPushed.load(std::memory_order_relaxed),
            (unsigned long long)totalTasksDone.load(std::memory_order_relaxed));
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
        // 活动 batch 注册表快照
        std::fprintf(stderr, "  claimSlots:");
        for (uint32_t s = 0; s < kMaxClaimableBatches; ++s)
        {
            auto* b = claimSlots_[s].batch.load(std::memory_order_relaxed);
            if (!b) continue;
            std::fprintf(stderr, " [%u]=id%llu(nt=%u/tc=%u) ",
                s, (unsigned long long)b->diagnosticId,
                b->nextTile.load(std::memory_order_relaxed), b->tileCount);
        }
        std::fprintf(stderr, "\n");
        std::fflush(stderr);
    }
} // namespace JobSystem
