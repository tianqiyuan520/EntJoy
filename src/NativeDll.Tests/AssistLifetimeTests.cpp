#include "../NativeDll/JobSystem.h"
#include "../NativeDll/ChunkJobData.h"

#include <atomic>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <thread>

namespace
{
    struct AssistLifetimeContext
    {
        std::atomic<int> started{ 0 };
        std::atomic<int> finished{ 0 };
        std::atomic<int> cleanupCount{ 0 };
        std::atomic<bool> releaseWorkers{ false };
    };

    void ExecuteBlockedChunk(void* raw, const ChunkJobData*)
    {
        auto& context = *static_cast<AssistLifetimeContext*>(raw);
        context.started.fetch_add(1, std::memory_order_release);
        context.started.notify_all();
        while (!context.releaseWorkers.load(std::memory_order_acquire))
            context.releaseWorkers.wait(false, std::memory_order_acquire);
        context.finished.fetch_add(1, std::memory_order_release);
    }

    void CleanupBlockedChunks(void* raw)
    {
        static_cast<AssistLifetimeContext*>(raw)->cleanupCount.fetch_add(
            1, std::memory_order_relaxed);
    }
}

int main()
{
    JobSystem::Scheduler::Initialize(2);

    AssistLifetimeContext context;
    ChunkJobData chunks[2]{};
    auto handle = JobSystem::Scheduler::ScheduleChunks(
        &ExecuteBlockedChunk,
        &context,
        &CleanupBlockedChunks,
        chunks,
        2,
        {},
        JobSystem::ChunkScheduleMode::PublishAssist,
        2,
        1);

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    while (context.started.load(std::memory_order_acquire) != 2 &&
           std::chrono::steady_clock::now() < deadline)
    {
        std::this_thread::yield();
    }

    if (context.started.load(std::memory_order_acquire) != 2)
        throw std::runtime_error("workers did not claim both tiles");

    std::jthread releaseThread([&context]
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        context.releaseWorkers.store(true, std::memory_order_release);
        context.releaseWorkers.notify_all();
    });

    handle.Complete();
    releaseThread.join();

    if (context.finished.load(std::memory_order_acquire) != 2)
        throw std::runtime_error("not all worker callbacks finished");
    if (context.cleanupCount.load(std::memory_order_acquire) != 1)
        throw std::runtime_error("cleanup did not run exactly once");

    JobSystem::Scheduler::Shutdown();
    std::cout << "PASS AssistLifetime\n";
    return 0;
}
