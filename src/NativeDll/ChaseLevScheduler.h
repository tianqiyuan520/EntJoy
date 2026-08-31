#pragma once

// ChaseLevScheduler — 标准 Chase-Lev 工作窃取调度器（crossbeam-deque 模型）。
//
// 模型（标准 Chase-Lev：Injector + 本地 Deque + 窃取）：
//   - 每个 worker 持有一个 SparseTileDeque（LIFO pop，FIFO steal）——执行队列
//   - MPMCInjector（Vyukov 无锁 MPMC 环形队列）——跨线程提交入口
//   - SubmitBatch 预切分为 RangeTask 并推入 Injector；worker 从 Injector 拉取
//     推入自己 deque（owner-only PushBottom），标准 Chase-Lev 循环执行。
//   - 无共享注册表、无 claimers handshake、无扫描开销。
//
// 标准 Chase-Lev 协议：
//   - PopBottom: owner-only，SeqCst fence 阻断 x86 store→load 重排
//   - PushBottom: owner-only，release store
//   - StealTop: thief，CAS + seq 校验（防数据未发布）
//   - 本地操作零竞争（PopBottom 仅 owner 调用）
//   - 窃取是低频事件（StealTop 仅在本地 deque 空时触发）

#include "SparseTileDeque.h"
#include "MPMCInjector.h"
#include "RangeTaskPool.h"
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

        // tile 计数口径：Local=worker 本地，Stolen=worker 窃取，Assist=主线程 assist。
        enum class TileAccount : uint8_t { Local = 0, Stolen = 1, Assist = 2 };

        ChaseLevScheduler();
        ~ChaseLevScheduler();

        ChaseLevScheduler(const ChaseLevScheduler&) = delete;
        ChaseLevScheduler& operator=(const ChaseLevScheduler&) = delete;

        // 初始化：创建 workerCount 个持久 worker 线程 + deque + Injector。
        bool Start(uint32_t workerCount, TileExecutor executor,
            TaskDoneFn taskDone, bool bindThreads = false);
        void Stop() noexcept;

        // 提交 batch 全部 tiles：预切分推入 Injector 并唤醒 worker；任意线程可调用。
        // batch 完成由 tilesRemaining 归零驱动。
        void SubmitBatch(BatchState* batch) noexcept;

        // 提交通用 work 任务（无 batch，完成链由调用方负责）；执行序：
        // workFn(ctx) → workCleanup(ctx) → Release。
        bool SubmitWork(void (*fn)(void*), void* ctx, void (*cleanup)(void*)) noexcept;

        // 提交窗口统一唤醒（deferNotify 的 Flush）：bump epoch + notify_all 一次。
        // 供 JobSystem_SubmitDeferFlush 调用；defer 期 SubmitBatch 跳过 per-batch 唤醒。
        void WakePending() noexcept;

        // 主线程协助执行：从 Injector 或其他 worker deque 窃取一个任务并执行。
        // 返回是否执行了任务。
        bool TryAssistOne() noexcept;

        // 运行时切换 worker CPU 亲和性（enabled=true 绑定核心 1+i；false 清除）。
        // 应用到所有已启动的 worker 线程。主线程绑定核心 0 由调用方处理。
        void ApplyAffinity(bool enabled) noexcept;

        bool IsRunning() const noexcept;
        uint32_t WorkerCount() const noexcept;

        // 获取指定 worker 的持久 deque（供调试/诊断）。
        SparseTileDeque* GetWorkerDeque(uint32_t workerIndex) noexcept;

        // 诊断：dump 各 worker deque 状态到 stderr。
        void DumpState(const char* tag) const noexcept;

        // 诊断：每个 worker 当前正在执行的 batch（0=空闲）。worker 线程写入，dump 读取。
        std::atomic<uint64_t> workerCurrentBatch[kMaxTrackedWorkers];

        // ---- 诊断计数（relaxed 足够）----
        std::atomic<uint64_t> dequePushed[kMaxTrackedWorkers];
        std::atomic<uint64_t> dequePopped[kMaxTrackedWorkers];
        std::atomic<uint64_t> dequeStolen[kMaxTrackedWorkers];
        std::atomic<uint64_t> tasksExecuted[kMaxTrackedWorkers];
        std::atomic<uint64_t> totalTasksPushed{ 0 };
        std::atomic<uint64_t> totalTasksDone{ 0 };

        // 全局在飞任务计数（park 谓词）：SubmitBatch +=，taskDone -=；
        // worker park 前读它：>0 说明仍有未认领任务，短自旋后再 park。
        std::atomic<int64_t> activeTasks{ 0 };

        // 唤醒纪元（C++20 atomic::wait）：单一共享 epoch，一次 fetch_add + notify_all 唤醒全部 waiter。
        // 保持 wake-all 语义：绝不做选择性唤醒。
        std::atomic<uint64_t> wakeEpoch{ 0 };

        // 全局 Injector（标准 Chase-Lev 的任务入口）
        static constexpr uint32_t kInjectorCapacity = 32768;
        MPMCInjector<RangeTask*, kInjectorCapacity> injector_;

        // 全局 RangeTask 池
        static RangeTaskPool s_taskPool_;

        // Stop() 排空残留 task 后收集的未退役 batch（供 Shutdown 调 ForceFinalizeBatch
        // 释放 context，消除 shutdown 未完成 job 的泄漏）。Stop 内部填充，Stop 后读取。
        std::vector<BatchState*> drainedBatches;

    private:
        static constexpr uint32_t kDequeCapacity = 4096;
        // 每次认领的 tile 数（预切分粒度；放大窗口无端到端收益——fetch_add 被 tile 执行吸收）
        static constexpr uint32_t kClaimBatchSize = 4;

        // ── 自适应自旋参数（WorkerLoop park 段）──
        // 执行后拉满 → 连续调度零唤醒；空转退火 → 快速让出 CPU；activeTasks>0 用更大窗口。
        static constexpr uint32_t kSpinBase = 256;
        static constexpr uint32_t kSpinMax = 4096;
        static constexpr uint32_t kSpinBusy = 8192;
        static constexpr uint32_t kSpinMin = 64;
        // workerCap 令牌标记：firstTile==UINT32_MAX 为参与令牌，执行体原子认领 nextTile（并行度 ≤ 令牌数）。
        static constexpr uint32_t kClaimTokenMarker = UINT32_MAX;

        // workerCap 令牌执行：原子认领 nextTile 直到空（实际并行受令牌数限制）。
        // 内部处理 taskDone（pendingTasks--）；不 Release（调用方负责）。
        void ExecuteClaimToken(BatchState* batch, uint32_t workerIndex,
            TileAccount account = TileAccount::Local) noexcept;

        // Injector 满时有限退避入队（yield + pause），供所有提交路径共用。
        bool PushTaskBackoff(RangeTask* task) noexcept;

        struct WorkerContext
        {
            std::unique_ptr<SparseTileDeque> deque;
            std::thread thread;
        };

        // 执行一个 RangeTask 并释放回池
        void ExecuteAndRelease(RangeTask* task, uint32_t workerIndex,
            TileAccount account = TileAccount::Local) noexcept;

        // 从 Injector 或其他 worker 窃取一个任务并执行（TryAssistOne 内部）
        bool StealAndExecute(uint32_t workerIndex) noexcept;

        // 排空 Injector + 各 worker deque 残留 task：通用 work 调 workCleanup，tile 收集 batch 到
        // drainedBatches，RangeTask 回池。仅 Stop() join 后调用（单线程、worker 已退出）。
        void DrainRemaining() noexcept;

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
