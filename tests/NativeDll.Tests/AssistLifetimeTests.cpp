#include "../NativeDll/JobSystem.h"
#include "../NativeDll/ChunkJobData.h"
#include "../NativeDll/ChaseLevScheduler.h"

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

    void TestStopWaitsForExternalAssist()
    {
        JobSystem::ChaseLevScheduler scheduler;
        auto unused = [](JobSystem::BatchState*, uint32_t) noexcept {};
        if (!scheduler.Start(1, unused, nullptr, false)) throw std::runtime_error("local scheduler start failed");
        struct Gate
        {
            std::atomic<bool> entered{ false };
            std::atomic<bool> release{ false };
        } worker, assist;
        auto block = [](void* raw)
        {
            auto& gate = *static_cast<Gate*>(raw);
            gate.entered.store(true, std::memory_order_release);
            gate.entered.notify_all();
            while (!gate.release.load(std::memory_order_acquire)) gate.release.wait(false);
        };
        scheduler.SubmitWork(block, &worker, nullptr);
        worker.entered.wait(false);
        scheduler.SubmitWork(block, &assist, nullptr);
        std::jthread caller([&] { scheduler.TryAssistOne(false); });
        assist.entered.wait(false);
        std::atomic<bool> stopped{ false };
        std::jthread stopper([&] { scheduler.Stop(); stopped.store(true, std::memory_order_release); });
        while (scheduler.IsRunning()) std::this_thread::yield();
        worker.release.store(true, std::memory_order_release);
        worker.release.notify_all();
        std::this_thread::sleep_for(std::chrono::milliseconds(30));
        const bool premature = stopped.load(std::memory_order_acquire);
        assist.release.store(true, std::memory_order_release);
        assist.release.notify_all();
        caller.join(); stopper.join();
        if (premature) throw std::runtime_error("Stop returned while an external assist still owned scheduler storage");
        if (scheduler.TryAssistOne()) throw std::runtime_error("stopped scheduler accepted an assist");
    }
}

int main()
{
    JobSystem::Scheduler::Initialize(2);

    AssistLifetimeContext context;
    ChunkJobData chunks[2]{};
    // 实体数衡 tile：entityCount 必须非零（空 chunk 会按实体数合并成 1 个 tile，
    // 导致回调次数≠chunk 数，测试断言 started==2 永不满足）。每 chunk 1024 实体 →
    // 2 个 tile、2 次回调（对齐实体数衡 tile 语义）。
    chunks[0].entityCount = 1024;
    chunks[1].entityCount = 1024;
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
    TestStopWaitsForExternalAssist();
    std::cout << "PASS AssistLifetime\n";
    return 0;
}
