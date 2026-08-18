#pragma once

#include <cstdint>
#include <memory>

// 实时 Worker 状态快照（供调试面板读取）
// 放在全局命名空间，与 Exports.h 的 extern "C" 声明匹配
struct WorkerSnapshot {
    int32_t  workerIndex;      // worker 编号
    uint64_t currentBatchId;   // 当前执行的 batchId（0=空闲）
    uint32_t currentTile;      // 当前 tile 索引
    uint32_t tileCount;        // 总 tile 数
    bool     isActive;         // 是否正在执行 batch
};

namespace JobSystem
{
    class NativeWorkerPool
    {
    public:
        using RunSlotFn = void (*)(void*, uint32_t) noexcept;
        using CompletionFn = void (*)(void*) noexcept;

        NativeWorkerPool();
        ~NativeWorkerPool();

        NativeWorkerPool(const NativeWorkerPool&) = delete;
        NativeWorkerPool& operator=(const NativeWorkerPool&) = delete;

        bool Start(uint32_t workerCount, bool bindWorkers = false);
        void Stop() noexcept;
        bool Submit(
            void* context,
            uint32_t slotCount,
            RunSlotFn runSlot,
            CompletionFn completion);

        bool IsRunning() const noexcept;
        uint32_t WorkerCount() const noexcept;

        // 诊断计数器（有界 futex 混合等待；parkWakeCount = 实际内核态 park 次数，
        // hotSpinHits = 混合等待命中数（自旋/初始即有活，未 park））
        void GetCounters(uint64_t* parkWakeCount, uint64_t* hotSpinHits) const noexcept;
        void ResetCounters() noexcept;

    private:
        struct Impl;
        std::unique_ptr<Impl> _impl;
    };
}
