#pragma once

#include <cstdint>
#include <memory>

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
