#pragma once

// ChaseLevScheduler — Chase-Lev 工作窃取调度器（crossbeam-deque 模型）。
//
// 模型（对齐 crossbeam-deque / tejchid/job-system）：
//   - 每个 worker 持有一个 SparseTileDeque（LIFO pop，FIFO steal）——执行队列
//   - 每个 worker 持有一个 MPSC 注入队列 ——任务入口（SubmitBatch 写入，
//     owner worker 单消费者拉取到自己的 Chase-Lev deque）
//   - 无全局 MPMC 环形队列：注入分散到每 worker 队列，无全局 dequeue 争用
//
// 生命周期（防 use-after-free）：
//   Batch 退役需满足双条件 tilesRemaining==0（所有 tile 执行完）&& pendingTasks==0
//   （所有任务执行完，无任务再引用本 storage）——由"最后完成者"执行 TryFinalizeChaseLevBatch。

#include "SparseTileDeque.h"
#include "JobSystemInternal.h"

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif

namespace JobSystem
{
    class ChaseLevScheduler
    {
    public:
        // Tile 执行回调：executor(batch, tileIndex) → 调用方实现 TryExecuteOneTile 逻辑。
        using TileExecutor = void (*)(BatchState* batch, uint32_t tileIndex) noexcept;
        // 任务完成回调：范围任务执行完后调用（batch 的 pendingTasks-- 由调用方处理）。
        using TaskDoneFn = void (*)(BatchState* batch) noexcept;

        ChaseLevScheduler();
        ~ChaseLevScheduler();

        ChaseLevScheduler(const ChaseLevScheduler&) = delete;
        ChaseLevScheduler& operator=(const ChaseLevScheduler&) = delete;

        // 初始化：创建 workerCount 个持久 worker 线程 + deque + 注入队列。
        bool Start(uint32_t workerCount, TileExecutor executor,
            TaskDoneFn taskDone, bool bindThreads = false);
        void Stop() noexcept;

        // 提交一个 batch 的所有 tiles：任务 round-robin 进各 worker 的 MPSC 注入队列。
        // 可被任意线程调用（主线程或依赖 continuation 的 worker 线程）。
        // batch 的完成由 batch->tilesRemaining 归零驱动。
        void SubmitBatch(BatchState* batch) noexcept;

        bool IsRunning() const noexcept;
        uint32_t WorkerCount() const noexcept;

        // 获取指定 worker 的持久 deque（供调试/诊断）。
        SparseTileDeque* GetWorkerDeque(uint32_t workerIndex) noexcept;

        // 诊断：dump 各 worker deque 状态（top/bottom/是否空）到 stderr。
        void DumpState(const char* tag) const noexcept;

        // 诊断：每个 worker 当前正在执行的 batch（0=空闲）。worker 线程写入，dump 读取。
        std::atomic<uint64_t> workerCurrentBatch[kMaxTrackedWorkers];

        // ---- 诊断计数（死锁/丢任务排查，relaxed 足够）----
        // 任务流计数：InjectPush 成功 / InjectPop 成功 / PushBottom / PopBottom /
        // StealTop 成功 / 实际执行任务数。若某 worker 的 pushed>popped 说明任务
        // 滞留在注入队列；dequePushed > dequePopped+dequeStolen 说明 deque 丢任务。
        std::atomic<uint64_t> injectPushed[kMaxTrackedWorkers];
        std::atomic<uint64_t> injectPopped[kMaxTrackedWorkers];
        std::atomic<uint64_t> dequePushed[kMaxTrackedWorkers];
        std::atomic<uint64_t> dequePopped[kMaxTrackedWorkers];
        std::atomic<uint64_t> dequeStolen[kMaxTrackedWorkers];
        std::atomic<uint64_t> tasksExecuted[kMaxTrackedWorkers];
        // 全局：任务 push 总数（SubmitBatch） vs taskDone 调用总数。done<pushed ⇒ 有任务
        // 被消费但未执行（或未回调 taskDone）。
        std::atomic<uint64_t> totalTasksPushed{ 0 };
        std::atomic<uint64_t> totalTasksDone{ 0 };

    private:
        static constexpr uint32_t kDequeCapacity = 4096;

        // ---- 每 worker 一个 MPSC 注入队列 ----
        // 多生产者（SubmitBatch 可来自任意线程）→ 单消费者（owner worker）。
        // Vyukov 序列号免 ABA；容量 2 的幂。
        static constexpr uint32_t kInjectCapacity = 8192;
        struct InjectSlot
        {
            std::atomic<uint64_t> seq{ 0 };
            TileTask task{};
        };
        struct InjectQueue
        {
            std::vector<InjectSlot> cells;      // 容量 kInjectCapacity
            std::atomic<uint64_t> enqueuePos{ 0 };  // 生产者 CAS（多生产者）
            uint64_t dequeuePos{ 0 };               // 消费者（owner worker）非原子
            InjectQueue() : cells(kInjectCapacity) {}
        };
        std::vector<std::unique_ptr<InjectQueue>> injects_; // 每 worker 一个

        bool InjectPush(uint32_t workerIndex, const TileTask& task) noexcept;
        bool InjectPop(uint32_t workerIndex, TileTask& task) noexcept;

        struct WorkerContext
        {
            std::unique_ptr<SparseTileDeque> deque;
            std::thread thread;
#if defined(_WIN32)
            HANDLE wakeEvent{ nullptr }; // manual-reset event for precise wake
#endif
        };

        void WorkerLoop(uint32_t workerIndex, WorkerContext& ctx) noexcept;

        std::mutex lifecycleMutex_;
        std::vector<std::unique_ptr<WorkerContext>> workers_;
        std::atomic<bool> running_{ false };
        std::atomic<bool> quit_{ false };
        uint32_t workerCount_{ 0 };
        bool bindThreads_{ false };
        TileExecutor executor_{ nullptr };
        TaskDoneFn taskDone_{ nullptr };
    };
} // namespace JobSystem
