#include "../NativeDll/JobSystem.h"
#include "../NativeDll/ChunkJobData.h"

#include <atomic>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>

namespace
{
    struct TestFailure : std::runtime_error
    {
        using std::runtime_error::runtime_error;
    };

    void Require(bool value, const char* message)
    {
        if (!value) throw TestFailure(message);
    }

    struct ParallelContext
    {
        std::vector<std::atomic<int>>* hits;
        std::atomic<int>* cleanupCount;
        std::atomic<int>* callerExecutions;
        std::atomic<bool>* releaseWorkers;
        std::thread::id caller;
    };

    void ExecuteRange(void* raw, int start, int count)
    {
        auto& context = *static_cast<ParallelContext*>(raw);
        if (std::this_thread::get_id() == context.caller)
        {
            context.callerExecutions->fetch_add(1, std::memory_order_relaxed);
            context.releaseWorkers->store(true, std::memory_order_release);
            context.releaseWorkers->notify_all();
        }
        else
        {
            context.releaseWorkers->wait(false, std::memory_order_acquire);
        }

        for (int index = start; index < start + count; ++index)
        {
            (*context.hits)[static_cast<size_t>(index)].fetch_add(1, std::memory_order_relaxed);
        }
    }

    void Cleanup(void* raw)
    {
        static_cast<ParallelContext*>(raw)->cleanupCount->fetch_add(1, std::memory_order_relaxed);
    }

    void TestParallelForExactOnceAndCallerAssist()
    {
        constexpr int length = 100'000;
        std::vector<std::atomic<int>> hits(length);
        std::atomic<int> cleanupCount{ 0 };
        std::atomic<int> callerExecutions{ 0 };
        std::atomic<bool> releaseWorkers{ false };
        ParallelContext context{
            &hits,
            &cleanupCount,
            &callerExecutions,
            &releaseWorkers,
            std::this_thread::get_id()
        };

        std::jthread watchdog([&releaseWorkers]
        {
            for (int elapsed = 0; elapsed < 100 && !releaseWorkers.load(std::memory_order_acquire); ++elapsed)
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            releaseWorkers.store(true, std::memory_order_release);
            releaseWorkers.notify_all();
        });

        auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
            &ExecuteRange, &context, length, 0, &Cleanup);
        handle.Complete();

        for (const auto& hit : hits)
        {
            Require(hit.load(std::memory_order_relaxed) == 1,
                "index was missed or duplicated");
        }
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "cleanup must run exactly once");
        Require(callerExecutions.load(std::memory_order_relaxed) > 0,
            "Complete caller did not execute any parallel batch");
    }

    struct ExactOnceContext
    {
        std::vector<std::atomic<int>>* hits;
        std::atomic<int>* cleanupCount;
    };

    void ExecuteExactRange(void* raw, int start, int count)
    {
        auto& context = *static_cast<ExactOnceContext*>(raw);
        for (int index = start; index < start + count; ++index)
            (*context.hits)[static_cast<size_t>(index)].fetch_add(1, std::memory_order_relaxed);
    }

    void CleanupExactRange(void* raw)
    {
        static_cast<ExactOnceContext*>(raw)->cleanupCount->fetch_add(1, std::memory_order_relaxed);
    }

    void TestExplicitBatchSize(int batchSize)
    {
        constexpr int length = 100'000;
        std::vector<std::atomic<int>> hits(length);
        std::atomic<int> cleanupCount{ 0 };
        ExactOnceContext context{ &hits, &cleanupCount };
        auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
            &ExecuteExactRange, &context, length, batchSize, &CleanupExactRange);
        handle.Complete();
        for (const auto& hit : hits)
            Require(hit.load(std::memory_order_relaxed) == 1,
                "explicit batch size missed or duplicated an index");
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "explicit batch cleanup must run exactly once");
    }

    void TestDependencyOrdering()
    {
        std::atomic<bool> dependencyFinished{ false };
        std::atomic<bool> childRanEarly{ false };
        auto dependency = JobSystem::Scheduler::Schedule(
            [](void* raw)
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
                static_cast<std::atomic<bool>*>(raw)->store(true, std::memory_order_release);
            }, &dependencyFinished);

        struct DependentContext
        {
            std::atomic<bool>* dependencyFinished;
            std::atomic<bool>* childRanEarly;
        } context{ &dependencyFinished, &childRanEarly };

        auto child = JobSystem::Scheduler::ScheduleParallelForBatch(
            [](void* raw, int, int)
            {
                auto& dependent = *static_cast<DependentContext*>(raw);
                if (!dependent.dependencyFinished->load(std::memory_order_acquire))
                    dependent.childRanEarly->store(true, std::memory_order_release);
            }, &context, 100'000, 257, nullptr, dependency);
        child.Complete();
        Require(!childRanEarly.load(std::memory_order_acquire),
            "dependent parallel job ran before dependency");
    }

    void TestAutomaticBatchDensity()
    {
        constexpr int length = 100'000;
        std::atomic<int> callbackCount{ 0 };
        auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
            [](void* raw, int, int)
            {
                static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed);
            }, &callbackCount, length, 0);
        handle.Complete();
        const int workers = JobSystem::ResolveWorkerCount(0);
        Require(callbackCount.load(std::memory_order_relaxed) >= workers * 3,
            "automatic batching created too few work units for tail balancing");
        Require(callbackCount.load(std::memory_order_relaxed) <= workers * 4 + 1,
            "automatic batching exceeded the four-per-worker target");
    }

    struct ChunkRangeContext
    {
        std::vector<std::atomic<int>>* hits;
        std::atomic<int>* cleanupCount;
    };

    void ExecuteChunkRange(void* raw, const ChunkJobData*, int start, int count)
    {
        auto& context = *static_cast<ChunkRangeContext*>(raw);
        for (int index = start; index < start + count; ++index)
            (*context.hits)[static_cast<size_t>(index)].fetch_add(1, std::memory_order_relaxed);
    }

    void CleanupChunkRange(void* raw)
    {
        static_cast<ChunkRangeContext*>(raw)->cleanupCount->fetch_add(1, std::memory_order_relaxed);
    }

    struct CooperativeChunkContext
    {
        std::vector<std::atomic<int>>* hits;
        std::atomic<int>* cleanupCount;
    };

    void ExecuteCooperativeChunkRange(void* raw, const ChunkJobData*, int start, int count)
    {
        auto& context = *static_cast<CooperativeChunkContext*>(raw);
        for (int index = start; index < start + count; ++index)
        {
            (*context.hits)[static_cast<size_t>(index)].fetch_add(1, std::memory_order_relaxed);
            if ((index & 31) == 0) std::this_thread::yield();
        }
    }

    void CleanupCooperativeChunkRange(void* raw)
    {
        static_cast<CooperativeChunkContext*>(raw)->cleanupCount->fetch_add(1, std::memory_order_relaxed);
    }

    void TestChunkRangeExactOnce()
    {
        constexpr int chunkCount = 1'024;
        std::vector<ChunkJobData> chunks(chunkCount);
        std::vector<std::atomic<int>> hits(chunkCount);
        std::atomic<int> cleanupCount{ 0 };
        ChunkRangeContext context{ &hits, &cleanupCount };
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
            &ExecuteChunkRange, &context, &CleanupChunkRange,
            chunks.data(), chunkCount, {}, JobSystem::ChunkScheduleMode::PublishAssist);
        handle.Complete();
        for (const auto& hit : hits)
            Require(hit.load(std::memory_order_relaxed) == 1,
                "chunk range was missed or duplicated");
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "chunk cleanup must run exactly once");
    }

    void TestCopiedHandleCleansUpOnce()
    {
        constexpr int length = 20'000;
        std::vector<std::atomic<int>> hits(length);
        std::atomic<int> cleanupCount{ 0 };
        ExactOnceContext context{ &hits, &cleanupCount };
        auto original = JobSystem::Scheduler::ScheduleParallelForBatch(
            &ExecuteExactRange, &context, length, 257, &CleanupExactRange);
        auto copied = original;
        copied.Complete();
        original.Complete();
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "copied handle caused duplicate cleanup");
    }

    void TestCombinedDependencies()
    {
        std::atomic<int> completed{ 0 };
        auto callback = [](void* raw)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
            static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_release);
        };
        auto first = JobSystem::Scheduler::Schedule(callback, &completed);
        auto second = JobSystem::Scheduler::Schedule(callback, &completed);
        std::vector<JobSystem::JobHandle> dependencies{ first, second };
        auto combined = JobSystem::JobHandle::CombineDependencies(dependencies);
        combined.Complete();
        Require(completed.load(std::memory_order_acquire) == 2,
            "combined dependency completed before its inputs");
    }

    void TestShutdownWithOutstandingWork()
    {
        std::atomic<int> completedBatches{ 0 };
        auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
            [](void* raw, int, int)
            {
                std::this_thread::sleep_for(std::chrono::microseconds(100));
                static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed);
            }, &completedBatches, 100'000, 257);
        JobSystem::Scheduler::Shutdown();
        Require(handle.IsCompleted(), "shutdown left parallel work incomplete");
        JobSystem::Scheduler::Initialize();
    }

    void TestConcurrentChunkComplete()
    {
        constexpr int chunkCount = 4'096;
        std::vector<ChunkJobData> chunks(chunkCount);
        std::vector<std::atomic<int>> hits(chunkCount);
        std::atomic<int> cleanupCount{ 0 };
        CooperativeChunkContext context{ &hits, &cleanupCount };

        JobSystem::ResetStatsSnapshot();
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
            &ExecuteCooperativeChunkRange, &context, &CleanupCooperativeChunkRange,
            chunks.data(), chunkCount, {},
            JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
        auto first = handle;
        auto second = handle;
        auto third = handle;
        auto fourth = handle;
        std::jthread a([first]() mutable { first.Complete(); });
        std::jthread b([second]() mutable { second.Complete(); });
        std::jthread c([third]() mutable { third.Complete(); });
        std::jthread d([fourth]() mutable { fourth.Complete(); });
        a.join();
        b.join();
        c.join();
        d.join();
        handle.Complete();

        for (const auto& hit : hits)
            Require(hit.load(std::memory_order_relaxed) == 1,
                "concurrent Complete missed or duplicated a Chunk range");
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "concurrent Complete duplicated Chunk cleanup");
    }

    void TestExhaustedChunkTicketsDrain()
    {
        constexpr int chunkCount = 2;
        JobSystem::ResetStatsSnapshot();
        for (int iteration = 0; iteration < 256; ++iteration)
        {
            std::vector<ChunkJobData> chunks(chunkCount);
            std::vector<std::atomic<int>> hits(chunkCount);
            std::atomic<int> cleanupCount{ 0 };
            CooperativeChunkContext context{ &hits, &cleanupCount };
            auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
                &ExecuteCooperativeChunkRange, &context, &CleanupCooperativeChunkRange,
                chunks.data(), chunkCount, {},
                JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
            handle.Complete();
            for (const auto& hit : hits)
                Require(hit.load(std::memory_order_relaxed) == 1,
                    "exhausted ticket test missed or duplicated a range");
            Require(cleanupCount.load(std::memory_order_relaxed) == 1,
                "exhausted ticket test duplicated cleanup");
        }

        JobSystem::JobSystemStatsSnapshot stats{};
        for (int retry = 0; retry < 100; ++retry)
        {
            JobSystem::GetStatsSnapshot(&stats);
            if (stats.exhaustedTickets > 0) break;
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        Require(stats.exhaustedTickets > 0,
            "stale cooperative tickets did not drain through the exhausted path");
    }

    void TestCooperativeStatsReset()
    {
        JobSystem::ResetStatsSnapshot();
        JobSystem::JobSystemStatsSnapshot stats{};
        JobSystem::GetStatsSnapshot(&stats);
        Require(stats.directAssistClaims == 0, "direct assist stats did not reset");
        Require(stats.exhaustedTickets == 0, "exhausted ticket stats did not reset");
        Require(stats.scheduleToPublishEwmaNs == 0, "schedule-to-publish stats did not reset");
        Require(stats.publishToFirstMainClaimEwmaNs == 0, "main-claim stats did not reset");
        Require(stats.publishToFirstWorkerClaimEwmaNs == 0, "worker-claim stats did not reset");
        Require(stats.publishToCompletionEwmaNs == 0, "completion stats did not reset");
        Require(stats.queueLockWaitEwmaNs == 0, "queue-lock stats did not reset");
    }
}

int main()
{
    JobSystem::Scheduler::Initialize();
    try
    {
        TestCooperativeStatsReset();
        std::cout << "PASS CooperativeStatsReset\n";
        TestParallelForExactOnceAndCallerAssist();
        std::cout << "PASS ParallelForExactOnceAndCallerAssist\n";
        TestExplicitBatchSize(1);
        TestExplicitBatchSize(257);
        TestExplicitBatchSize(100'000);
        std::cout << "PASS ExplicitBatchSizes\n";
        TestDependencyOrdering();
        std::cout << "PASS DependencyOrdering\n";
        TestChunkRangeExactOnce();
        std::cout << "PASS ChunkRangeExactOnce\n";
        TestConcurrentChunkComplete();
        std::cout << "PASS ConcurrentChunkComplete\n";
        TestExhaustedChunkTicketsDrain();
        std::cout << "PASS ExhaustedChunkTicketsDrain\n";
        TestAutomaticBatchDensity();
        std::cout << "PASS AutomaticBatchDensity\n";
        TestCopiedHandleCleansUpOnce();
        std::cout << "PASS CopiedHandleCleansUpOnce\n";
        TestCombinedDependencies();
        std::cout << "PASS CombinedDependencies\n";
        TestShutdownWithOutstandingWork();
        std::cout << "PASS ShutdownWithOutstandingWork\n";
        JobSystem::Scheduler::Shutdown();
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL " << error.what() << '\n';
        JobSystem::Scheduler::Shutdown();
        return 1;
    }
}
