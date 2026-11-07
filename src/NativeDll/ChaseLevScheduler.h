#pragma once

// ChaseLevScheduler — 标准 Chase-Lev 工作窃取调度器（crossbeam-deque 模型）。
//
// 模型（标准 Chase-Lev：Injector + 本地 Deque + 窃取）：
//   - 每个 worker 持有一个 SparseTileDeque（LIFO pop，FIFO steal）——执行队列
//   - MPMCInjector（Vyukov 无锁 MPMC 环形队列）——跨线程提交入口
//   - SubmitBatch 发布初始 RangeTask，执行者按需补充参与令牌；worker 从 Injector 拉取
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
#include <condition_variable>
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
        // 第二参为令牌创建时的 BatchStorage 代次，结算侧据此拒绝迟到结算。
        using TaskDoneFn = void (*)(BatchState* batch, uint32_t batchGen) noexcept;

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

        // 提交 batch：发布初始令牌，执行者按需补充参与令牌；任意线程可调用。
        // batch 完成由 tilesRemaining 归零驱动。
        void SubmitBatch(BatchState* batch) noexcept;

        // 提交通用 work 任务（无 batch，完成链由调用方负责）；执行序：
        // workFn(ctx) → workCleanup(ctx) → Release。
        bool SubmitWork(void (*fn)(void*), void* ctx, void (*cleanup)(void*)) noexcept;
        bool SubmitDirectWork(DirectWorkKind kind, void (*fn)(), void* ctx, int length,
            void (*cleanup)(void*), HandleState* state) noexcept;
        bool PublishWorkTask(RangeTask* task) noexcept;

        // 提交窗口统一唤醒（deferNotify 的 Flush）：bump epoch + notify_all 一次。
        // 供 JobSystem_SubmitDeferFlush 调用；defer 期 SubmitBatch 跳过 per-batch 唤醒。
        void WakePending() noexcept;
        // 只补足尚未覆盖的通知；等待者接收或取消停靠时消费额度。
        bool WakeWorkers(uint32_t needed) noexcept;
        uint32_t SearchingWorkers(uint32_t limit) const noexcept;

        // 主线程协助执行：从 Injector 或其他 worker deque 窃取一个任务并执行。
        // scanDeques=false 时只消费 Injector，用于等待无未完成依赖的直通任务。
        // 返回是否执行了任务。
        bool TryAssistOne(bool scanDeques = true) noexcept;

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
        PaddedAtomic<uint64_t> workerCurrentBatch[kMaxTrackedWorkers];

        // ---- 诊断计数（relaxed 足够）----
        PaddedAtomic<uint64_t> dequePushed[kMaxTrackedWorkers];
        PaddedAtomic<uint64_t> dequePopped[kMaxTrackedWorkers];
        PaddedAtomic<uint64_t> dequeStolen[kMaxTrackedWorkers];
        PaddedAtomic<uint64_t> tasksExecuted[kMaxTrackedWorkers];
        std::atomic<uint64_t> totalTasksPushed{ 0 };
        std::atomic<uint64_t> totalTasksDone{ 0 };

        // 全局在飞任务计数（park 谓词）：SubmitBatch +=，taskDone -=；
        // worker park 前读它：>0 说明仍有未认领任务，短自旋后再 park。
        std::atomic<int64_t> activeTasks{ 0 };

        // 显式 Flush/退出的唤醒纪元，也供自旋和停靠复查观察。
        // 关闭需求感知通知时，worker 使用该原子的 wait/notify_all。
        std::atomic<uint64_t> wakeEpoch{ 0 };

        // 已进入停靠协议的执行者数量，包含已通知但尚未恢复的执行者。
        // 需求感知路径在 parkMutex_ 内登记和退出，并以 pendingWakes_ 合并通知。
        std::atomic<int> parkedWorkers{ 0 };

        // `ENTJOY_WAKE_POLL`：搜索区人数 —— "登记中"的 worker 数（在搜索区里读注入器，或刚领到
        // 任务正在执行、执行完必然回主循环读注入器）。有停靠者时核对真实搜索状态。
        // 登记是粘性的：只在"进入停靠协议"与"退出主循环"两处增减（见 .cpp），
        // 两处严格配平 —— 残留 >0 会让提交侧永久跳过广播。
        // 独占一条 cacheline：与 `wakeEpoch` 同线会让提交侧的 epoch 写入更拥挤（那正是要消除的流量）。
        PaddedAtomic<uint64_t> wakeIdlePollers;

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
        // 每次认领的 tile 数：认领上限的下限兜底。
        static constexpr uint32_t kClaimBatchSize = 4;
        // 认领跨度按元素给（`g_claimSpanElems`，`ENTJOY_CLAIM_SPAN`，默认 1024），只对
        // `itemsPerTile <= kClaimSpanMaxItemsPerTile` 生效 —— 更粗的 tile 已是长连续段，
        // 再加宽只增尾部量子风险 ⇒ 保持 `claimCap`。
        static constexpr uint32_t kClaimSpanMaxItemsPerTile = 256;

        // ── 自适应自旋参数（WorkerLoop park 段）──
        // 执行后拉满 → 连续调度零唤醒；空转退火 → 快速让出 CPU；activeTasks>0 用更大窗口。
        // kSpinBusy 可由 `ENTJOY_SPIN_BUSY`（pause 次数）覆盖；该旋钮只改自旋时长，不改变任何语义。
        static constexpr uint32_t kSpinBase = 256;
        static constexpr uint32_t kSpinMax = 4096;
        static constexpr uint32_t kSpinBusy = 8192;
        static constexpr uint32_t kSpinMin = 64;
        static uint32_t SpinBusyCap() noexcept
        {
            static const uint32_t cap = []() -> uint32_t {
                const char* v = std::getenv("ENTJOY_SPIN_BUSY");
                if (v == nullptr) return kSpinBusy;
                const long long n = std::atoll(v);
                return n > 0 ? static_cast<uint32_t>(n) : kSpinBusy;
            }();
            return cap;
        }
        // workerCap 令牌标记：firstTile==UINT32_MAX 为参与令牌，执行体原子认领 nextTile（并行度 ≤ 令牌数）。
        static constexpr uint32_t kClaimTokenMarker = UINT32_MAX;

        // workerCap 令牌执行：原子认领 nextTile 直到空（实际并行受令牌数限制）。
        // 内部处理 taskDone（pendingTasks--）；不 Release（调用方负责）。
        // batchGen = 令牌创建时的代次，透传给 taskDone_ 做迟到结算校验。
        void ExecuteClaimToken(BatchState* batch, uint32_t workerIndex, uint32_t batchGen,
            TileAccount account = TileAccount::Local) noexcept;

        void PublishDemandTokenIfNeeded(BatchState* batch, uint32_t batchGen) noexcept;

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

        // 从 Injector 或 worker deque 窃取一个任务并执行（TryAssistOne 内部）。
        // stealStart 是窃取扫描起点，accountIndex 是计账/数组下标，两者分开传：
        // 主线程没有 worker 槽位，用 kMaxTrackedWorkers 作 accountIndex 让所有数组写入跳过、
        // 认领计数落进 main 分支；同时 stealStart 仍从 0 起扫全部 deque。
        // scanAllWorkers 区分调用者：worker 跳过自己，主线程 assist 扫描全部 worker。
        bool StealAndExecute(uint32_t stealStart, uint32_t accountIndex,
            bool scanAllWorkers, bool scanDeques) noexcept;

        // 排空 Injector + 各 worker deque 残留 task：通用 work 调 workCleanup，tile 收集 batch 到
        // drainedBatches，RangeTask 回池。仅 Stop() join 后调用（单线程、worker 已退出）。
        void DrainRemaining() noexcept;

        void WorkerLoop(uint32_t workerIndex, WorkerContext& ctx) noexcept;

        void WakeForQueuedWork() noexcept;
        // 每个执行者独占缓存行；取得任务后清除、搜索时设置，避免全局逐任务登记写入。
        PaddedAtomic<bool> searchingWorkers_[kMaxTrackedWorkers];
        // 登记、复查、等待以及通知额度的发放/消费在同一把锁内完成。
        std::mutex parkMutex_;
        std::condition_variable parkCv_;
        // 0 <= pendingWakes_ <= parkedWorkers；原子读用于已有通知覆盖需求时的快速返回。
        PaddedAtomic<uint32_t> pendingWakes_;
        std::mutex lifecycleMutex_;
        std::vector<std::unique_ptr<WorkerContext>> workers_;
        std::atomic<bool> running_{ false };
        static constexpr uint32_t kAssistClosed = uint32_t{1} << 31;
        // 高位关闭新 assist，低位保留已进入调用；队列释放前等待归零。
        std::atomic<uint32_t> assistGate_{ kAssistClosed };
        std::atomic<bool> quit_{ false };
        uint32_t workerCount_{ 0 };
        bool bindThreads_{ false };
        TileExecutor executor_{ nullptr };

        TaskDoneFn taskDone_{ nullptr };
    };
} // namespace JobSystem
