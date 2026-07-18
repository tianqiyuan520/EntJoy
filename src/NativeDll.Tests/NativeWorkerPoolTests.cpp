#include "../NativeDll/NativeWorkerPool.h"

#include <atomic>
#include <cstdint>
#include <iostream>
#include <stdexcept>

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

        pool.Stop();
        Require(!pool.IsRunning(), "pool remained running after Stop");
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
