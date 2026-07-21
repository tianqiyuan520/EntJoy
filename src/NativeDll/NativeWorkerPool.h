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

    private:
        struct Impl;
        std::unique_ptr<Impl> _impl;
    };
}
