#pragma once

// ChaseLevScheduler — Chase-Lev 工作窃取调度器（crossbeam-deque 模型）。
//
// 模型（标准 Chase-Lev：共享入口 + 本地 Deque + 窃取）：
//   - 每个 worker 持有一个 SparseTileDeque（LIFO pop，FIFO steal）——执行队列
//   - 【共享活动 batch 注册表 + per-batch 原子游标】——任务入口。
//     SubmitBatch 构建任务的 tile 范围表并注册 batch；worker 用
//     batch->nextTile.fetch_add(kClaim) 动态认领一片，推入自己 deque 执行。
//     这是标准 Chase-Lev 的 Injector 角色，但用原子游标替代 MPMC 环形队列：
//     无全局队列 dequeue 争用（每次认领是分散到各 batch 的 relaxed fetch_add）。
//   - 动态认领保证负载均衡（快 worker 多拿），避免"每 worker 私有注入队列"
//     造成的 ① 任务锁死在私有队列不可被窃取（尾延迟爆炸）② 固定任务切分丢失
//     动态认领（p50 回归）。
//
// 生命周期（防 use-after-free）：
//   Batch 退役需满足双条件 tilesRemaining==0（所有 tile 执行完）&& pendingTasks==0
//   （所有任务执行完，无任务再引用本 storage）。认领临界安全：
//   只从"已注册 batch"认领，注册到退役期间 pendingTasks>0（任务计数）守护 batch
//   不回收，注册槽由退役方（TryFinalizeChaseLevBatch）清空——与旧路径一致。

#include "SparseTileDeque.h"
#include "JobSystemInternal.h"

#include <array>
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

        // 初始化：创建 workerCount 个持久 worker 线程 + deque。
        bool Start(uint32_t workerCount, TileExecutor executor,
            TaskDoneFn taskDone, bool bindThreads = false);
        void Stop() noexcept;

        // 提交一个 batch 的所有 tiles：注册该 batch 到共享认领表并唤醒所有 worker。
        // 可被任意线程调用（主线程或依赖 continuation 的 worker 线程）。
        // batch 的完成由 batch->tilesRemaining 归零驱动。
        void SubmitBatch(BatchState* batch) noexcept;

        // 退役方（TryFinalizeChaseLevBatch）在 ReleaseBatch 之前调用——
        // 从共享认领表注销 batch，并等正认领的 worker 完成（防 use-after-free）。
        void UnregisterBatch(BatchState* batch) noexcept;

        bool IsRunning() const noexcept;
        uint32_t WorkerCount() const noexcept;

        // 获取指定 worker 的持久 deque（供调试/诊断）。
        SparseTileDeque* GetWorkerDeque(uint32_t workerIndex) noexcept;

        // 诊断：dump 各 worker deque 状态（top/bottom/是否空）到 stderr。
        void DumpState(const char* tag) const noexcept;

        // 诊断：每个 worker 当前正在执行的 batch（0=空闲）。worker 线程写入，dump 读取。
        std::atomic<uint64_t> workerCurrentBatch[kMaxTrackedWorkers];

        // ---- 诊断计数（死锁/丢任务排查，relaxed 足够）----
        // 任务流计数：PushBottom / PopBottom / StealTop 成功 / 实际执行任务数。
        // dequePushed > dequePopped+dequeStolen 说明 deque 丢任务。
        std::atomic<uint64_t> dequePushed[kMaxTrackedWorkers];
        std::atomic<uint64_t> dequePopped[kMaxTrackedWorkers];
        std::atomic<uint64_t> dequeStolen[kMaxTrackedWorkers];
        std::atomic<uint64_t> tasksExecuted[kMaxTrackedWorkers];
        // 全局：认领任务总数（SubmitBatch 任务数） vs taskDone 调用总数。
        std::atomic<uint64_t> totalTasksPushed{ 0 };
        std::atomic<uint64_t> totalTasksDone{ 0 };

    private:
        static constexpr uint32_t kDequeCapacity = 4096;

        // ---- 共享活动 batch 注册表（标准 Chase-Lev 的 Injector 角色）----
        // SubmitBatch 注册 batch；worker 从注册表中的 batch 用 nextTile.fetch_add
        // 动态认领 tile 范围。容量须覆盖并发在飞的 batch 数（依赖链 2-3 个；256 足够）。
        // 认领临界防 UAF：batch 在 pendingTasks>0（任务计数）期间不回收；
        // 退役方（TryFinalizeChaseLevBatch）先清注册槽再 ReleaseBatch，且用
        // 槽上的 claimers 计数让"正在认领"的 worker 先完成（handshake）。
        static constexpr uint32_t kMaxClaimableBatches = 256;
        // 单次认领的 tile 数（对齐旧路径 kClaimBatch=4 的动态认领粒度）。
        static constexpr uint32_t kClaimBatchSize = 4;

        // 注册槽。batch 原子指针 + claimers 计数（清槽前等 claimers==0）。
        struct ClaimSlot
        {
            std::atomic<BatchState*> batch{ nullptr };
            std::atomic<uint32_t> claimers{ 0 };   // 认领中的 worker 数（退役 handshake）
        };
        struct alignas(64) ClaimSlotPadded : ClaimSlot {};
        std::array<ClaimSlotPadded, kMaxClaimableBatches> claimSlots_{};

        // 下次注册尝试的起始槽位（round-robin 起始，避免每次都从 0 扫描）
        std::atomic<uint32_t> claimSlotCursor_{ 0 };

        // 注册 / 注销 batch。注销由退役方调用（须在 ReleaseBatch 之前）。
        void RegisterBatch(BatchState* batch) noexcept;
        // worker 从注册表认领一片 tile 范围到任务（返回 false = 无可用任务）。
        bool ClaimRange(BatchState* batch, uint32_t& first, uint32_t& count) noexcept;

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
