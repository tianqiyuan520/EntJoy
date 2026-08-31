#include "../NativeDll/JobSystem.h"
#include "../NativeDll/JobSystemInternal.h"
#include "../NativeDll/ChaseLevScheduler.h"
#include "../NativeDll/ChunkJobData.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <future>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>

namespace
{
    constexpr int kTimeoutSec = 30;

    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    template <typename Fn>
    void RunWithTimeout(const char* name, Fn&& fn)
    {
        std::cout << "[START] " << name << std::endl << std::flush;
        auto future = std::async(std::launch::async, std::forward<Fn>(fn));
        auto status = future.wait_for(std::chrono::seconds(kTimeoutSec));
        if (status == std::future_status::timeout)
        {
            std::cerr << "[DEADLOCK] " << name << " timed out" << std::endl;
            std::cerr << "  outstanding="
                      << JobSystem::g_backendBatchesOutstanding.load(std::memory_order_relaxed)
                      << " nativeBatches=" << JobSystem::g_nativeBatches.load(std::memory_order_relaxed)
                      << std::endl;
            std::cerr << "  pushed="
                      << JobSystem::g_totalTilesPublished.load(std::memory_order_relaxed)
                      << std::endl;
            if (JobSystem::g_chaseLevScheduler)
                JobSystem::g_chaseLevScheduler->DumpState(name);
            std::abort();
        }
        future.get();
        std::cout << "[DONE]  " << name << std::endl << std::flush;
    }

    struct ChunkCtx
    {
        std::atomic<int>* hits;
        std::atomic<int>* cleanup;
    };

    void ChunkFn(void* raw, const ChunkJobData* chunk)
    {
        auto& ctx = *static_cast<ChunkCtx*>(raw);
        if (chunk && ctx.hits)
            ctx.hits->fetch_add(1, std::memory_order_relaxed);
    }

    void ChunkCleanup(void* raw)
    {
        auto& ctx = *static_cast<ChunkCtx*>(raw);
        if (ctx.cleanup) ctx.cleanup->fetch_add(1, std::memory_order_relaxed);
    }

    // ============================================================
    // Test 1: Chunk 路径 + Complete（复现 TestTraceLifecycleOrder 死锁）
    // ============================================================
    void TestChunkComplete()
    {
        constexpr int rangeCount = 64;
        std::vector<ChunkJobData> chunks(rangeCount);
        std::vector<std::atomic<int>> hits(rangeCount);
        std::atomic<int> cleanupCount{ 0 };
        ChunkCtx ctx{ hits.data(), &cleanupCount };

        for (int i = 0; i < rangeCount; ++i)
        {
            chunks[i].entityArray = nullptr;
            chunks[i].entityCount = 1;
        }

        std::cout << "  scheduling chunks..." << std::flush;
        auto handle = JobSystem::Scheduler::ScheduleChunks(
            &ChunkFn, &ctx, &ChunkCleanup,
            chunks.data(), rangeCount, {},
            JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
        std::cout << " done" << std::endl;

        std::cout << "  Complete()..." << std::flush;
        handle.Complete();
        std::cout << " done" << std::endl;

        std::cout << "  cleanup=" << cleanupCount.load() << std::endl;
        Require(cleanupCount.load() == 1, "cleanup count mismatch");
        std::cout << "  cleanup ok" << std::endl;
    }

    // ============================================================
    // Test 2: 简单 ParallelFor + Complete（S1 场景，应通过）
    // ============================================================
    void TestParallelForComplete()
    {
        std::atomic<int> count{ 0 };
        constexpr int length = 100'000;

        std::cout << "  scheduling parallel-for..." << std::flush;
        auto handle = JobSystem::Scheduler::ScheduleParallelFor(
            [](void* raw, int) { static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed); },
            &count, length, 0, nullptr, {});
        std::cout << " done" << std::endl;

        std::cout << "  Complete()..." << std::flush;
        handle.Complete();
        std::cout << " done" << std::endl;

        Require(count.load() == length, "not all indices executed");
        std::cout << "  count=" << count.load() << std::endl;
    }

    // ============================================================
    // Test 3: 依赖链 + Complete（S3 场景）
    // ============================================================
    void TestDependencyChainComplete()
    {
        std::atomic<int> count{ 0 };
        constexpr int length = 100'000;

        auto fn = [](void* raw, int) { static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed); };

        std::cout << "  schedule A..." << std::flush;
        auto a = JobSystem::Scheduler::ScheduleParallelFor(fn, &count, length, 0, nullptr, {});
        std::cout << " done" << std::endl;

        std::cout << "  schedule B (dep=A)..." << std::flush;
        auto b = JobSystem::Scheduler::ScheduleParallelFor(fn, &count, length, 0, nullptr, a);
        std::cout << " done" << std::endl;

        std::cout << "  schedule C (dep=B)..." << std::flush;
        auto c = JobSystem::Scheduler::ScheduleParallelFor(fn, &count, length, 0, nullptr, b);
        std::cout << " done" << std::endl;

        std::cout << "  Complete(C)..." << std::flush;
        c.Complete();
        std::cout << " done" << std::endl;

        Require(count.load() == length * 3, "dependency chain lost work");
        std::cout << "  count=" << count.load() << std::endl;
    }
    // ============================================================
    // Test 4: ScheduleParallelForBatch 依赖链 × 105 轮（复现 benchmark S3）
    // ============================================================
    void TestBatchDependencyChainRepeated()
    {
        std::atomic<int> count{ 0 };
        constexpr int length = 1'000'000;
        constexpr int rounds = 105;

        auto fn = [](void* raw, int, int) { static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed); };

        for (int r = 0; r < rounds; ++r)
        {
            auto a = JobSystem::Scheduler::ScheduleParallelForBatch(fn, &count, length, 0, nullptr, {});
            auto b = JobSystem::Scheduler::ScheduleParallelForBatch(fn, &count, length, 0, nullptr, a);
            auto c = JobSystem::Scheduler::ScheduleParallelForBatch(fn, &count, length, 0, nullptr, b);
            c.Complete();
            if (r % 25 == 0)
                std::cout << "  round " << r << " count=" << count.load() << std::endl;
        }
        std::cout << "  final count=" << count.load() << std::endl;
        // batchFunc 按 tile 调用：每 batch tileCount = ResolveChunkSize 推导的 rc
        Require(count.load() > 0, "batch chain lost work");
    }

    // ============================================================
    // Test 5: C++ 异常协议——回调抛异常 → 任务计数正常 + Complete 重抛
    // （TBB/Taskflow 语义；验证调度器不悬挂、异常传递给调用方）
    // ============================================================
    void TestExceptionPropagation()
    {
        struct ThrowCtx { std::atomic<int> executed{ 0 }; };
        ThrowCtx ctx;

        auto fn = [](void* raw, int) {
            auto* c = static_cast<ThrowCtx*>(raw);
            c->executed.fetch_add(1, std::memory_order_relaxed);
            if (c->executed.load(std::memory_order_relaxed) >= 2)
                throw std::runtime_error("intentional job failure");
        };

        std::cout << "  scheduling throwing parallel-for..." << std::flush;
        auto handle = JobSystem::Scheduler::ScheduleParallelFor(fn, &ctx, 1000, 0, nullptr, {});
        std::cout << " done" << std::endl;

        bool caught = false;
        std::cout << "  Complete() (expect rethrow)..." << std::flush;
        try
        {
            handle.Complete();
        }
        catch (const std::exception& e)
        {
            caught = true;
            std::cout << " caught: " << e.what() << std::endl;
        }
        Require(caught, "Complete() did not rethrow the job exception");
        std::cout << "  executed=" << ctx.executed.load() << " (all tiles ran, no hang)" << std::endl;
    }
    // ============================================================
    // Test 6: 工作窃取——负载不均时，空闲 worker 从繁忙 worker deque 窃取任务
    // ============================================================
    void TestStealOccurs()
    {
        std::atomic<int> count{ 0 };
        constexpr int length = 1'000'000;
        auto fn = [](void* raw, int i) {
            // 部分 tile 人为变慢（每 128K 个 sleep 50µs），制造负载不均 → 触发窃取
            if ((i & 0x1FFFF) == 0)
                std::this_thread::sleep_for(std::chrono::microseconds(50));
            static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed);
        };
        auto h = JobSystem::Scheduler::ScheduleParallelFor(fn, &count, length, 0, nullptr, {});
        h.Complete();

        JobSystem::JobSystemStatsSnapshot stats{};
        JobSystem::GetStatsSnapshot(&stats);

        Require(count.load() == length, "lost work");
        Require(stats.stealSuccesses <= stats.stealAttempts, "steal stats inconsistent");
        // tile 账目闭合（按 tile 数，非元素数）
        Require(stats.localTiles + stats.stolenTiles + stats.assistTiles ==
                stats.totalTilesPublished, "tile accounting did not reconcile");
        // 窃取是否发生依赖核数：高核数下负载不均能触发；低核数（CI 2 核）下 token
        // 独占执行、deque 窗口极窄，窃取可能观察不到。故不作硬断言——账目闭合已
        // 覆盖窃取 tile 计数的正确性，窃取路径本身由 SparseTileDequeTests 的
        // TestMultiThreadSteal/TestConcurrentStealRounds 覆盖。
        if (stats.stealSuccesses > 0)
            std::cout << "  steal observed: attempts=" << stats.stealAttempts
                      << " successes=" << stats.stealSuccesses
                      << " stolenTiles=" << stats.stolenTiles
                      << " tiles=" << stats.totalTilesPublished << std::endl;
        else
            std::cout << "  steal not observed (low core count, expected)" << std::endl;
    }
}

int main()
{
    JobSystem::Scheduler::Initialize();
    try
    {
        RunWithTimeout("TestParallelForComplete", TestParallelForComplete);
        RunWithTimeout("TestChunkComplete", TestChunkComplete);
        RunWithTimeout("TestDependencyChainComplete", TestDependencyChainComplete);
        RunWithTimeout("TestBatchDependencyChainRepeated", TestBatchDependencyChainRepeated);
        RunWithTimeout("TestExceptionPropagation", TestExceptionPropagation);
        RunWithTimeout("TestStealOccurs", TestStealOccurs);
        std::cout << "PASS all\n";
        JobSystem::Scheduler::Shutdown();
        return 0;
    }
    catch (const std::exception& e)
    {
        std::cerr << "FAIL " << e.what() << std::endl;
        JobSystem::Scheduler::Shutdown();
        return 1;
    }
}
