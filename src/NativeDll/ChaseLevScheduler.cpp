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
    // 每 worker MPSC 注入队列（多生产者 → 单消费者）
    // ============================================================

    bool ChaseLevScheduler::InjectPush(uint32_t workerIndex, const TileTask& task) noexcept
    {
        if (workerIndex >= injects_.size()) return false;
        auto& q = *injects_[workerIndex];
        uint64_t pos = q.enqueuePos.load(std::memory_order_relaxed);
        for (;;)
        {
            InjectSlot& cell = q.cells[pos & (kInjectCapacity - 1)];
            const uint64_t seq = cell.seq.load(std::memory_order_acquire);
            const int64_t diff = static_cast<int64_t>(seq) - static_cast<int64_t>(pos);
            if (diff == 0)
            {
                if (q.enqueuePos.compare_exchange_weak(pos, pos + 1,
                        std::memory_order_relaxed))
                {
                    cell.task = task;
                    cell.seq.store(pos + 1, std::memory_order_release);
                    return true;
                }
            }
            else if (diff < 0)
            {
                return false; // full
            }
            else
            {
                pos = q.enqueuePos.load(std::memory_order_relaxed);
            }
        }
    }

    bool ChaseLevScheduler::InjectPop(uint32_t workerIndex, TileTask& task) noexcept
    {
        if (workerIndex >= injects_.size()) return false;
        auto& q = *injects_[workerIndex];
        // 单消费者（owner worker）：dequeuePos 非原子，无 CAS 竞争。
        // 生产者（其他线程）并发 CAS enqueuePos —— 用 acquire 读 seq 确认可见。
        const uint64_t pos = q.dequeuePos;
        InjectSlot& cell = q.cells[pos & (kInjectCapacity - 1)];
        const uint64_t seq = cell.seq.load(std::memory_order_acquire);
        const int64_t diff = static_cast<int64_t>(seq) - static_cast<int64_t>(pos + 1);
        if (diff != 0) return false; // empty（seq 未到 pos+1）
        task = cell.task;
        cell.seq.store(pos + kInjectCapacity, std::memory_order_release);
        q.dequeuePos = pos + 1;
        return true;
    }

    // ============================================================
    // Start — 创建持久 worker 线程 + deque + 注入队列
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
            injectPushed[i].store(0, std::memory_order_relaxed);
            injectPopped[i].store(0, std::memory_order_relaxed);
            dequePushed[i].store(0, std::memory_order_relaxed);
            dequePopped[i].store(0, std::memory_order_relaxed);
            dequeStolen[i].store(0, std::memory_order_relaxed);
            tasksExecuted[i].store(0, std::memory_order_relaxed);
        }

        try
        {
            workers_.reserve(workerCount);
            injects_.reserve(workerCount);
            for (uint32_t i = 0; i < workerCount; ++i)
            {
                auto ctx = std::make_unique<WorkerContext>();
                ctx->deque = std::make_unique<SparseTileDeque>(kDequeCapacity);
#if defined(_WIN32)
                // manual-reset event：初始无信号，SetEvent 唤醒，ResetEvent 重置。
                ctx->wakeEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
#endif
                workers_.push_back(std::move(ctx));
                // 每 worker 一个 MPSC 注入队列：槽 seq 初始 = i（Vyukov）
                auto q = std::make_unique<InjectQueue>();
                for (uint32_t s = 0; s < kInjectCapacity; ++s)
                    q->cells[s].seq.store(s, std::memory_order_relaxed);
                injects_.push_back(std::move(q));
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
        injects_.clear();
    }

    // ============================================================
    // SubmitBatch — 任务 round-robin 进各 worker 的 MPSC 注入队列，唤醒 worker
    // 可被任意线程调用（主线程 / 依赖 continuation 的 worker 线程）。
    // 无线程亲和直入：全部经注入队列（单消费者），避免跨线程 PushBottom
    // 与 PopBottom 并发导致的孤儿元素覆盖（已验证稳定性）。
    // ============================================================

    void ChaseLevScheduler::SubmitBatch(BatchState* batch) noexcept
    {
        if (!batch || batch->tileCount == 0) return;
        const uint32_t wc = workerCount_;
        if (wc == 0) return;

        // 任务粒度：每 worker 约 kTasksPerWorker 个范围任务，再均匀切分。
        constexpr uint32_t kTasksPerWorker = 4;
        const uint32_t tileCount = batch->tileCount;
        uint32_t taskCount = wc * kTasksPerWorker;
        if (taskCount > tileCount) taskCount = tileCount;
        const uint32_t chunk = (tileCount + taskCount - 1) / taskCount;
        uint32_t actualTasks = (tileCount + chunk - 1) / chunk;

        // 记录在飞任务数（防 use-after-free：退役需等所有任务完成）
        batch->pendingTasks.store(actualTasks, std::memory_order_release);

        // 任务 round-robin 分发到各 worker 的 MPSC 注入队列
        uint32_t wi = 0;
        for (uint32_t start = 0; start < tileCount; start += chunk)
        {
            const uint32_t cnt = std::min(chunk, tileCount - start);
            wi = (wi + 1) % wc;
            TileTask t{ batch, start, cnt };
            while (!InjectPush(wi, t))
                std::this_thread::yield(); // 队列满（病理过载）：自旋等消费
            if (wi < kMaxTrackedWorkers)
                injectPushed[wi].fetch_add(1, std::memory_order_relaxed);
            totalTasksPushed.fetch_add(1, std::memory_order_relaxed);
        }

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
    //   2. 从自己的 MPSC 注入队列批量拉取到 deque（单消费者）
    //   3. 从其他 worker deque StealTop（FIFO，CAS）
    //   4. 空 → park（event wait）→ 被唤醒后重试
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
                // ---- 1.5 从【自己的】MPSC 注入队列批量拉取到 deque ----
                // 单消费者零 CAS 竞争；生产者（SubmitBatch 可来自任意线程）
                // 并发 CAS enqueuePos，注入队列内部保证可见性。
                uint32_t pulled = 0;
                TileTask t;
                while (pulled < 16 && InjectPop(workerIndex, t))
                {
                    myDeque->PushBottom(t);
                    ++pulled;
                    if (workerIndex < kMaxTrackedWorkers)
                    {
                        injectPopped[workerIndex].fetch_add(1, std::memory_order_relaxed);
                        dequePushed[workerIndex].fetch_add(1, std::memory_order_relaxed);
                    }
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
            const uint64_t eq = injects_[i]->enqueuePos.load(std::memory_order_relaxed);
            const uint64_t dq2 = injects_[i]->dequeuePos;
            std::fprintf(stderr, "  worker[%u] empty=%d approx=%u curBatch=%llu injectQueued=%llu"
                " injP=%llu injC=%llu dqP=%llu dqC=%llu dqS=%llu exec=%llu\n",
                i, (int)dq.IsEmpty(), dq.ApproxSize(),
                (unsigned long long)workerCurrentBatch[i].load(std::memory_order_relaxed),
                (unsigned long long)(eq - dq2),
                (unsigned long long)injectPushed[i].load(std::memory_order_relaxed),
                (unsigned long long)injectPopped[i].load(std::memory_order_relaxed),
                (unsigned long long)dequePushed[i].load(std::memory_order_relaxed),
                (unsigned long long)dequePopped[i].load(std::memory_order_relaxed),
                (unsigned long long)dequeStolen[i].load(std::memory_order_relaxed),
                (unsigned long long)tasksExecuted[i].load(std::memory_order_relaxed));
        }
        std::fflush(stderr);
    }
} // namespace JobSystem
