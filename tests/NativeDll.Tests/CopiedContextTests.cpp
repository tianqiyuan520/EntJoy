#include "Exports.h"
#include "JobSystemInternal.h"
#include "JobContextPool.h"

#include <array>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <thread>

namespace
{
    void Require(bool condition, const char* message)
    {
        if (!condition) { std::fprintf(stderr, "FAIL: %s\n", message); std::exit(1); }
    }

    struct Gate { std::atomic<bool> open{ false }; };
    void WaitForGate(void* raw)
    {
        auto& gate = *static_cast<Gate*>(raw);
        while (!gate.open.load(std::memory_order_acquire)) std::this_thread::yield();
    }
    struct RangeContext
    {
        std::atomic<int>* visits;
        int* values;
        std::atomic<int>* calls;
        std::atomic<int>* bad;
        int offset;
        bool explicitBatch;
    };
    void ExecuteRange(void* raw, int first, int count)
    {
        auto& context = *static_cast<RangeContext*>(raw);
        if (context.explicitBatch && (first % 64 != 0 || count != std::min(64, 1031 - first)))
            context.bad->fetch_add(1);
        context.calls->fetch_add(1);
        for (int i = first; i < first + count; ++i)
        {
            context.visits[i].fetch_add(1);
            context.values[i] = i * 2 + context.offset;
        }
    }

    void TestCopiedRanges()
    {
        for (int indexed : { 0, 1 })
        {
            std::array<std::atomic<int>, 1031> visits{};
            std::array<int, 1031> values{};
            std::atomic<int> calls{ 0 }, bad{ 0 };
            Gate gate;
            void* dependency = JobSystem_Schedule(&WaitForGate, &gate, nullptr, nullptr);
            RangeContext source{ visits.data(), values.data(), &calls, &bad, 7, indexed == 0 };
            void* handle = nullptr;
            Require(JobSystem_ScheduleParallelForCopiedContext(&ExecuteRange, &source, sizeof(source),
                1031, 64, 0, indexed, dependency, &handle) == 0, "copied range submission");
            std::memset(&source, 0, sizeof(source));
            gate.open.store(true, std::memory_order_release);
            JobSystem_Complete(handle);
            for (int i = 0; i < 1031; ++i)
                Require(visits[i].load() == 1 && values[i] == i * 2 + 7, "copy survives source mutation and visits once");
            Require(bad.load() == 0, "explicit batch boundaries");
            if (!indexed) Require(calls.load() == 17, "explicit batch callback count");
            JobSystem_ReleaseHandle(handle);
            JobSystem_ReleaseHandle(dependency);
            JobSystem_DrainAll();
        }
    }

    std::atomic<int> emptyCalls{ 0 };
    void ExecuteEmpty(void* context) { Require(context == nullptr, "empty context pointer"); ++emptyCalls; }
    struct LargeContext { int* output; unsigned char payload[8193]; };
    void ExecuteLarge(void* raw)
    {
        auto& context = *static_cast<LargeContext*>(raw);
        *context.output = context.payload[0] + context.payload[8192];
    }
    void TestEmptyAndLarge()
    {
        void* handle = nullptr;
        Require(JobSystem_ScheduleCopiedContext(&ExecuteEmpty, nullptr, 0, nullptr, &handle) == 0, "empty submission");
        JobSystem_Complete(handle); JobSystem_ReleaseHandle(handle);
        Require(emptyCalls.load() == 1, "empty execution");
        Gate gate;
        void* dependency = JobSystem_Schedule(&WaitForGate, &gate, nullptr, nullptr);
        int output = 0;
        LargeContext source{}; source.output = &output; source.payload[0] = 19; source.payload[8192] = 37;
        Require(JobSystem_ScheduleCopiedContext(&ExecuteLarge, &source, sizeof(source), dependency, &handle) == 0,
            "large copied source");
        std::memset(&source, 0, sizeof(source)); gate.open.store(true, std::memory_order_release);
        JobSystem_Complete(handle); Require(output == 56, "large payload and dependency");
        JobSystem_ReleaseHandle(handle); JobSystem_ReleaseHandle(dependency); JobSystem_DrainAll();
    }

    void TestFailureOwnership()
    {
        void* handle = reinterpret_cast<void*>(1);
        int source = 0;
        Require(JobSystem_ScheduleCopiedContext(&ExecuteEmpty, nullptr, 1, nullptr, &handle) == 1 && !handle,
            "invalid source clears output");
        Require(JobSystem_ScheduleCopiedContext(&ExecuteEmpty, &source, -1, nullptr, &handle) == 1, "negative size");
        Require(JobSystem_ScheduleCopiedContext(&ExecuteEmpty, &source, (std::numeric_limits<int>::max)(), nullptr,
            &handle) == 1, "size overflow");
        Require(JobSystem_ScheduleCopiedContext(&ExecuteEmpty, nullptr, 0, nullptr, nullptr) == 1, "null output");
        JobSystem::ContextMemory::FailNextRentForTests(1);
        Require(JobSystem_ScheduleCopiedContext(&ExecuteEmpty, &source, sizeof(source), nullptr, &handle) == 2 && !handle,
            "allocation failure clears output");
        RangeContext context{};
        JobSystem::FailNextBatchStorageAcquireForTests(1);
        Require(JobSystem_ScheduleParallelForCopiedContext(&ExecuteRange, &context, sizeof(context), 1031, 64, 0, 1,
            nullptr, &handle) == 3 && !handle, "submission failure clears output");
        constexpr int bytes = sizeof(context) + 2 * sizeof(void*);
        void* first = JobSystem_RentJobContext(bytes);
        void* second = JobSystem_RentJobContext(bytes);
        Require(first && second && first != second, "failed context returned once");
        JobSystem_ReturnJobContext(first, bytes); JobSystem_ReturnJobContext(second, bytes);
        JobSystem_DrainAll();
        Require(JobSystem_GetLiveHandleCount() == 0, "failure paths reclaim all states");
    }

    struct PendingContext { Gate* gate; std::atomic<int>* completed; };
    void ExecutePending(void* raw)
    {
        auto& context = *static_cast<PendingContext*>(raw);
        WaitForGate(context.gate);
        context.completed->fetch_add(1);
    }
    void TestShutdownDrainsCopies()
    {
        Gate gate;
        std::atomic<int> completed{ 0 };
        std::array<void*, 128> handles{};
        for (auto& handle : handles)
        {
            PendingContext source{ &gate, &completed };
            Require(JobSystem_ScheduleCopiedContext(&ExecutePending, &source, sizeof(source), nullptr, &handle) == 0,
                "queued copied source");
        }
        gate.open.store(true, std::memory_order_release);
        JobSystem_Shutdown();
        Require(completed.load() == 128, "shutdown executes pending copies");
        for (void* handle : handles) { JobSystem_Complete(handle); JobSystem_ReleaseHandle(handle); }
        Require(JobSystem_GetLiveHandleCount() == 0, "shutdown returns copied states");
    }
}

int main()
{
    std::atomic<bool> done{ false };
    std::jthread watchdog([&] {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(20);
        while (!done.load())
        {
            if (std::chrono::steady_clock::now() > deadline) { std::fputs("FAIL: copied-context timeout\n", stderr); std::_Exit(2); }
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
    });
    Require(JobSystem_Initialize(2) == 0, "initialize");
    TestCopiedRanges(); TestEmptyAndLarge(); TestFailureOwnership(); TestShutdownDrainsCopies();
    JobSystem_Shutdown(); done.store(true);
    std::puts("CopiedContextTests: ALL PASS");
}
