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
    // 自旋 pause（混合等待：自旋窗内 CpuPause）
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

        // 清空认领注册表（Start 前可能残留 Stop 未清的状态）
        for (auto& slot : claimSlots_)
        {
            slot.batch.store(nullptr, std::memory_order_relaxed);
            slot.claimers.store(0, std::memory_order_relaxed);
        }
        claimSlotHighWater_.store(0, std::memory_order_relaxed);
        wakeRoundRobin.store(0, std::memory_order_relaxed);
        for (uint32_t i = 0; i < kMaxTrackedWorkers; ++i)
            wakeStamp[i].store(0, std::memory_order_relaxed);

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

        // 唤醒所有 worker 退出（各自 stamp bump + notify）
        for (uint32_t i = 0; i < kMaxTrackedWorkers; ++i)
        {
            wakeStamp[i].fetch_add(1, std::memory_order_release);
            wakeStamp[i].notify_all();
        }

        for (auto& ctx : workers_)
        {
            if (ctx->thread.joinable())
                ctx->thread.join();
        }
        workers_.clear();
    }

    // ============================================================
    // 共享认领注册表（标准 Chase-Lev Injector 角色）
    // ============================================================

    void ChaseLevScheduler::RegisterBatch(BatchState* batch) noexcept
    {
        // 从 0 起扫描空槽（高水位内优先，避免把槽散布到高位拖慢 worker 空转扫描）。
        // 释放语义时序在 UnregisterBatch 的 claimers handshake 上（先清槽再等认领者
        // 归零→ReleaseBatch 在调用方）。
        for (uint32_t idx = 0; idx < kMaxClaimableBatches; ++idx)
        {
            auto& slot = claimSlots_[idx];
            BatchState* expected = nullptr;
            // 空 slot 且 claimers==0（无人认领）才可注册
            if (slot.claimers.load(std::memory_order_acquire) == 0 &&
                slot.batch.compare_exchange_strong(expected, batch,
                    std::memory_order_acq_rel, std::memory_order_acquire))
            {
                // 更新高水位（只增）
                uint32_t hw = claimSlotHighWater_.load(std::memory_order_relaxed);
                while (idx >= hw &&
                       !claimSlotHighWater_.compare_exchange_weak(hw, idx + 1,
                           std::memory_order_release, std::memory_order_relaxed)) {}
                return;
            }
        }
        // 注册表满（病理：并发在飞 batch > 容量）→ 自旋等空槽（不该发生）
        std::fprintf(stderr, "[ChaseLev] WARNING: claimable-batch table full, spinning\n");
        std::fflush(stderr);
        while (true)
        {
            for (uint32_t idx = 0; idx < kMaxClaimableBatches; ++idx)
            {
                auto& slot = claimSlots_[idx];
                BatchState* expected = nullptr;
                if (slot.claimers.load(std::memory_order_acquire) == 0 &&
                    slot.batch.compare_exchange_strong(expected, batch,
                        std::memory_order_acq_rel, std::memory_order_acquire))
                {
                    uint32_t hw = claimSlotHighWater_.load(std::memory_order_relaxed);
                    while (idx >= hw &&
                           !claimSlotHighWater_.compare_exchange_weak(hw, idx + 1,
                               std::memory_order_release, std::memory_order_relaxed)) {}
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
    // 主线程协助执行（assist）—— 对齐旧 MPMC 的协助模型
    // ============================================================

    bool ChaseLevScheduler::TryAssistOne() noexcept
    {
        if (!running_.load(std::memory_order_acquire)) return false;
        const uint32_t hw = claimSlotHighWater_.load(std::memory_order_acquire);
        for (uint32_t s = 0; s < hw; ++s)
        {
            auto& slot = claimSlots_[s];
            BatchState* b = slot.batch.load(std::memory_order_acquire);
            if (!b) continue;
            slot.claimers.fetch_add(1, std::memory_order_acq_rel);
            if (slot.batch.load(std::memory_order_acquire) != b)
            {
                slot.claimers.fetch_sub(1, std::memory_order_acq_rel);
                continue;
            }
            uint32_t first = 0, cnt = 0;
            if (ClaimRange(b, first, cnt))
            {
                slot.claimers.fetch_sub(1, std::memory_order_acq_rel);
                // 主线程无 deque：整片 [first, first+cnt) 直接内联执行
                DebugBeginExec(b->diagnosticId, b->tileCount, b->workerCount, false);
                SetCurrentBatchId(b->diagnosticId);
                const uint32_t end = first + cnt;
                for (uint32_t t = first; t < end; ++t)
                    executor_(b, t);
                SetCurrentBatchId(0);
                DebugEndExec();
                // 记账：与 worker 相同，认领一片 = 一个任务 → taskDone 一次
                if (taskDone_)
                {
                    activeTasks.fetch_sub(1, std::memory_order_acq_rel);
                    taskDone_(b);
                }
                return true;
            }
            slot.claimers.fetch_sub(1, std::memory_order_acq_rel);
        }
        return false;
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
        activeTasks.fetch_add(static_cast<int64_t>(taskCount), std::memory_order_acq_rel);

        // 注册该 batch 到共享认领表（worker 可开始认领）
        RegisterBatch(batch);
        totalTasksPushed.fetch_add(taskCount, std::memory_order_relaxed);

        // ---- 自适应唤醒（worker 数按任务量）----
        // 只唤醒能消化本 batch 任务的 worker 数（对齐 Misaki Release(N) 精确唤醒、
        // tejchid notify_one + runnable_ 谓词自愈）。每个被唤醒的 worker 会持续
        // scan 注册表循环认领，直到某轮认领不到；activeTasks>0 时 worker 不会 park
        // （WorkerLoop 4.5），因此剩余任务由活跃 worker 自愈消化，无需唤醒全部。
        // 每 worker 可连续认领多片（一个 worker 能消化到注册表空），故
        // needWake = ceil(taskCount / kClaimBatchSize) 是充足上界（每 worker 一轮
        // 至少消化 kClaimBatchSize 片）；小 batch 只唤醒少量 worker，大 batch 全醒。
        const uint32_t needWake = std::min(wc,
            std::max<uint32_t>(1, (taskCount + kClaimBatchSize - 1) / kClaimBatchSize));
        const uint32_t start = wakeRoundRobin.fetch_add(needWake, std::memory_order_relaxed) % wc;
        for (uint32_t i = 0; i < needWake && i < wc; ++i)
        {
            const uint32_t idx = (start + i) % wc;
            wakeStamp[idx].fetch_add(1, std::memory_order_release);
            wakeStamp[idx].notify_all();
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
        uint32_t parkSpin = 0; // park 前自旋计数（混合等待：自旋 → park）
        uint64_t seenStamp = 0; // 已消费的唤醒 stamp
        (void)seenStamp;

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
                const uint32_t hw = claimSlotHighWater_.load(std::memory_order_acquire);
                for (uint32_t s = 0; s < hw; ++s)
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
                        // ---- 认领成功：首 tile 内联执行（省 1 次 Push+Pop），剩余入队 ----
                        // 对齐旧 MPMC 的"动态认领内联执行"（每次认领 1 原子），同时保留
                        // Chase-Lev 的可窃取性：把 [s+1, s+cnt) 作为一个任务推进 deque
                        //（可被 steal），首 tile s 由本 worker 直接执行。
                        // 记账不变：一次认领 = 一个范围任务；剩余任务执行时 taskDone 一次
                        //（覆盖整次认领）。cnt==1 时无剩余，纯内联。
                        if (cnt > 1)
                        {
                            myDeque->PushBottom(TileTask{ b, first + 1, cnt - 1 });
                            if (workerIndex < kMaxTrackedWorkers)
                                dequePushed[workerIndex].fetch_add(1, std::memory_order_relaxed);
                        }
                        slot.claimers.fetch_sub(1, std::memory_order_acq_rel);
                        // 内联执行首 tile（与主执行路径相同语义）。taskDone 归属：
                        // cnt>1 → 剩余任务执行时 taskDone（覆盖整次认领）；
                        // cnt==1 → 无剩余任务，此处必须 taskDone 一次，否则 pendingTasks
                        //         永不归零（batch 永不退役）。
                        {
                            DebugBeginExec(b->diagnosticId, b->tileCount,
                                           b->workerCount, false);
                            SetCurrentBatchId(b->diagnosticId);
                            if (workerIndex < kMaxTrackedWorkers)
                                workerCurrentBatch[workerIndex].store(
                                    b->diagnosticId, std::memory_order_relaxed);
                            executor_(b, first);
                            if (workerIndex < kMaxTrackedWorkers)
                                workerCurrentBatch[workerIndex].store(
                                    0, std::memory_order_relaxed);
                            SetCurrentBatchId(0);
                            DebugEndExec();
                            if (cnt == 1 && taskDone_)
                            {
                                activeTasks.fetch_sub(1, std::memory_order_acq_rel);
                                taskDone_(b);
                            }
                        }
                        if (workerIndex < kMaxTrackedWorkers)
                            tasksExecuted[workerIndex].fetch_add(1, std::memory_order_relaxed);
                        break; // 拿到一片；剩余在 deque 里
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
                    activeTasks.fetch_sub(1, std::memory_order_acq_rel);
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

            // ---- 4.5 条件短自旋（仅当全局仍有未认领任务）----
            // 若 activeTasks>0 说明有任务已被认领/执行中但本 worker 抢不到（例如
            // 主流 commit 了 batch 而 worker 快于唤醒）。短自旋（~5µs）弥补
            // 唤醒竞态窗口；activeTasks==0 时直接 park，零自旋开销。
            // 对齐 tejchid/job-system 的 runnable_ 谓词（其 CV 谓词等效）。
            if (activeTasks.load(std::memory_order_acquire) > 0 &&
                parkSpin < kParkSpinMax)
            {
                ++parkSpin;
                CpuPause();
                continue; // 重查 PopBottom / 认领 / steal
            }
            parkSpin = 0;

            // ---- 5. Park — 等待唤醒 ----
            // per-worker stamp wait：无信号消费/Reset 竞态。被 SubmitBatch 选中的
            // worker 被唤醒；未选中的继续睡。自愈：醒来/完成任务的 worker 只要
            // activeTasks>0 就持续循环（4.5），保证任何"有活"时刻都有人消费。
#if defined(_WIN32)
            if (workerIndex < kMaxTrackedWorkers)
            {
                seenStamp = wakeStamp[workerIndex].load(std::memory_order_acquire);
                wakeStamp[workerIndex].wait(seenStamp, std::memory_order_relaxed);
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
