#include "../NativeDll/NativeWorkerPool.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <thread>

namespace
{
    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    struct SubmissionContext
    {
        std::atomic<uint64_t> slots{ 0 };
        std::atomic<uint32_t> completions{ 0 };
    };

    void RunSlot(void* raw, uint32_t slot) noexcept
    {
        auto& context = *static_cast<SubmissionContext*>(raw);
        context.slots.fetch_or(uint64_t{ 1 } << slot, std::memory_order_relaxed);
    }

    void CompleteSubmission(void* raw) noexcept
    {
        auto& context = *static_cast<SubmissionContext*>(raw);
        context.completions.fetch_add(1, std::memory_order_release);
        context.completions.notify_all();
    }

    void WaitForCompletion(SubmissionContext& context)
    {
        while (context.completions.load(std::memory_order_acquire) == 0)
            context.completions.wait(0, std::memory_order_acquire);
    }

    constexpr uint32_t StressBatchCount = 128;
    constexpr uint32_t StressSlotCapacity = 32;

    struct StressContext
    {
        std::array<std::atomic<uint32_t>, StressSlotCapacity> slots{};
        std::atomic<uint32_t> completions{ 0 };
        uint32_t slotCount{ 0 };
    };

    void RunStressSlot(void* raw, uint32_t slot) noexcept
    {
        auto& context = *static_cast<StressContext*>(raw);
        context.slots[slot].fetch_add(1, std::memory_order_relaxed);
        if ((slot & 3u) == 0)
            std::this_thread::yield();
    }

    void CompleteStressSubmission(void* raw) noexcept
    {
        auto& context = *static_cast<StressContext*>(raw);
        context.completions.fetch_add(1, std::memory_order_release);
        context.completions.notify_all();
    }

    void WaitForCompletion(StressContext& context)
    {
        while (context.completions.load(std::memory_order_acquire) == 0)
            context.completions.wait(0, std::memory_order_acquire);
    }

    void VerifyStressContext(const StressContext& context)
    {
        Require(context.completions.load(std::memory_order_acquire) == 1,
            "stress submission completed more than once");
        for (uint32_t slot = 0; slot < context.slotCount; ++slot)
        {
            Require(context.slots[slot].load(std::memory_order_relaxed) == 1,
                "stress submission missed or duplicated a slot");
        }
    }
}

int main()
{
    try
    {
        JobSystem::NativeWorkerPool pool;
        Require(!pool.IsRunning(), "pool started threads in its constructor");
        Require(pool.Start(4), "pool failed to start");
        Require(pool.IsRunning(), "pool did not report running");
        Require(pool.WorkerCount() == 4, "pool created the wrong worker count");

        SubmissionContext first;
        Require(pool.Submit(&first, 8, &RunSlot, &CompleteSubmission),
            "pool rejected an active submission");
        WaitForCompletion(first);
        Require(first.slots.load(std::memory_order_relaxed) == 0xff,
            "pool missed a submitted slot");
        Require(first.completions.load(std::memory_order_relaxed) == 1,
            "pool completed a submission more than once");

        SubmissionContext second;
        Require(pool.Submit(&second, 3, &RunSlot, &CompleteSubmission),
            "pool rejected a sequential submission");
        WaitForCompletion(second);
        Require(second.slots.load(std::memory_order_relaxed) == 0x7,
            "pool failed on a sequential submission");

        // Queue many batches before waiting. This verifies that a descriptor is
        // published once per batch, every slot is claimed exactly once, and batch
        // completion safely promotes the next descriptor.
        std::array<StressContext, StressBatchCount> stress{};
        for (uint32_t batch = 0; batch < StressBatchCount; ++batch)
        {
            stress[batch].slotCount = 1 + (batch % StressSlotCapacity);
            Require(pool.Submit(
                &stress[batch], stress[batch].slotCount,
                &RunStressSlot, &CompleteStressSubmission),
                "pool rejected a queued stress submission");
        }
        for (auto& context : stress)
        {
            WaitForCompletion(context);
            VerifyStressContext(context);
        }

        // Continuations can publish from worker threads while the main thread also
        // submits work. Exercise multiple producers against the one-descriptor-per-
        // batch pending queue.
        std::array<StressContext, StressBatchCount> concurrent{};
        std::array<std::thread, 4> producers;
        std::atomic<bool> submitFailed{ false };
        for (uint32_t producer = 0; producer < producers.size(); ++producer)
        {
            producers[producer] = std::thread([&, producer]
            {
                for (uint32_t batch = producer; batch < StressBatchCount;
                    batch += static_cast<uint32_t>(producers.size()))
                {
                    concurrent[batch].slotCount = 1 + (batch % StressSlotCapacity);
                    if (!pool.Submit(
                        &concurrent[batch], concurrent[batch].slotCount,
                        &RunStressSlot, &CompleteStressSubmission))
                    {
                        submitFailed.store(true, std::memory_order_relaxed);
                    }
                }
            });
        }
        for (auto& producer : producers)
            producer.join();
        Require(!submitFailed.load(std::memory_order_relaxed),
            "pool rejected a concurrent producer submission");
        for (auto& context : concurrent)
        {
            WaitForCompletion(context);
            VerifyStressContext(context);
        }

        // Stop must drain active and pending descriptors before joining workers.
        std::array<StressContext, 32> drainOnStop{};
        for (uint32_t batch = 0; batch < drainOnStop.size(); ++batch)
        {
            drainOnStop[batch].slotCount = StressSlotCapacity;
            Require(pool.Submit(
                &drainOnStop[batch], drainOnStop[batch].slotCount,
                &RunStressSlot, &CompleteStressSubmission),
                "pool rejected a drain-on-stop submission");
        }

        pool.Stop();
        Require(!pool.IsRunning(), "pool remained running after Stop");
        for (const auto& context : drainOnStop)
            VerifyStressContext(context);
        SubmissionContext rejected;
        Require(!pool.Submit(&rejected, 1, &RunSlot, &CompleteSubmission),
            "pool accepted work after Stop");
        Require(rejected.slots.load(std::memory_order_relaxed) == 0,
            "rejected submission executed work");

        std::cout << "PASS NativeWorkerPool\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL " << error.what() << '\n';
        return 1;
    }
}
