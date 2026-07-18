#include "../NativeDll/JobSystem.h"
#include "../NativeDll/ChunkJobData.h"
#include "../NativeDll/JobProfiler.h"

#include <atomic>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#endif

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
        std::atomic<bool>* releaseWorkers;
        std::atomic<int>* callerExecutions;
    };

    thread_local bool g_isCooperativeCompleteCaller = false;

    void ExecuteCooperativeChunkRange(void* raw, const ChunkJobData*, int start, int count)
    {
        auto& context = *static_cast<CooperativeChunkContext*>(raw);
        if (g_isCooperativeCompleteCaller)
        {
            if (context.callerExecutions)
                context.callerExecutions->fetch_add(1, std::memory_order_relaxed);
        }
        else if (context.releaseWorkers)
        {
            context.releaseWorkers->wait(false, std::memory_order_acquire);
        }
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
        std::atomic<bool> releaseWorkers{ false };
        std::atomic<int> callerExecutions{ 0 };
        CooperativeChunkContext context{
            &hits, &cleanupCount, &releaseWorkers, &callerExecutions
        };

        JobSystem::ResetStatsSnapshot();
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
            &ExecuteCooperativeChunkRange, &context, &CleanupCooperativeChunkRange,
            chunks.data(), chunkCount, {},
            JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
        auto first = handle;
        auto second = handle;
        auto third = handle;
        auto fourth = handle;
        auto completeAsCaller = [](JobSystem::JobHandle copied) mutable {
            g_isCooperativeCompleteCaller = true;
            copied.Complete();
        };
        std::jthread a(completeAsCaller, first);
        std::jthread b(completeAsCaller, second);
        std::jthread c(completeAsCaller, third);
        std::jthread d(completeAsCaller, fourth);
        while (callerExecutions.load(std::memory_order_acquire) < 2)
            std::this_thread::yield();
        releaseWorkers.store(true, std::memory_order_release);
        releaseWorkers.notify_all();
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

        JobSystem::JobSystemStatsSnapshot stats{};
        JobSystem::GetStatsSnapshot(&stats);
        Require(stats.directAssistClaims > 1,
            "Complete callers did not claim target ranges directly");
        Require(stats.mainClaimedTokens == stats.directAssistClaims,
            "main claims used a path other than direct batch assist");
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
            CooperativeChunkContext context{ &hits, &cleanupCount, nullptr, nullptr };
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

    void TestDependentChunkRangeCooperation()
    {
        constexpr int chunkCount = 1'024;
        std::vector<ChunkJobData> chunks(chunkCount);
        std::vector<std::atomic<int>> hits(chunkCount);
        std::atomic<int> cleanupCount{ 0 };
        std::atomic<bool> releaseDependency{ false };

        auto dependency = JobSystem::Scheduler::ScheduleParallelForBatch(
            [](void* raw, int, int)
            {
                auto* release = static_cast<std::atomic<bool>*>(raw);
                release->wait(false, std::memory_order_acquire);
            }, &releaseDependency, 100'000, 100'000);

        CooperativeChunkContext context{
            &hits, &cleanupCount, nullptr, nullptr
        };
        auto original = JobSystem::Scheduler::ScheduleChunkRanges(
            &ExecuteCooperativeChunkRange, &context, &CleanupCooperativeChunkRange,
            chunks.data(), chunkCount, dependency,
            JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
        auto first = original;
        auto second = original;
        std::jthread firstCaller([first]() mutable { first.Complete(); });
        std::jthread secondCaller([second]() mutable { second.Complete(); });

        std::this_thread::sleep_for(std::chrono::milliseconds(2));
        for (const auto& hit : hits)
            Require(hit.load(std::memory_order_relaxed) == 0,
                "dependent Chunk range ran before its prerequisite");

        releaseDependency.store(true, std::memory_order_release);
        releaseDependency.notify_all();
        firstCaller.join();
        secondCaller.join();
        original.Complete();

        for (const auto& hit : hits)
            Require(hit.load(std::memory_order_relaxed) == 1,
                "dependent Chunk range was missed or duplicated");
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "dependent Chunk cleanup did not run exactly once");
    }

    void TestChunkShutdownRace()
    {
        for (int iteration = 0; iteration < 50; ++iteration)
        {
            constexpr int chunkCount = 1'024;
            std::vector<ChunkJobData> chunks(chunkCount);
            std::vector<std::atomic<int>> hits(chunkCount);
            std::atomic<int> cleanupCount{ 0 };
            CooperativeChunkContext context{
                &hits, &cleanupCount, nullptr, nullptr
            };

            auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
                &ExecuteCooperativeChunkRange, &context, &CleanupCooperativeChunkRange,
                chunks.data(), chunkCount, {},
                JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
            auto copied = handle;
            std::jthread caller([copied]() mutable { copied.Complete(); });
            JobSystem::Scheduler::Shutdown();
            caller.join();

            Require(handle.IsCompleted(), "shutdown left cooperative Chunk work incomplete");
            Require(cleanupCount.load(std::memory_order_relaxed) == 1,
                "shutdown raced cooperative Chunk cleanup");
            JobSystem::Scheduler::Initialize();
        }
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

#ifdef _WIN32
    struct WorkerPriorityContext
    {
        std::atomic<int> observedPriority{ INT_MIN };
    };

    void RecordChunkWorkerPriority(void* raw, const ChunkJobData*, int, int)
    {
        auto& context = *static_cast<WorkerPriorityContext*>(raw);
        context.observedPriority.store(
            GetThreadPriority(GetCurrentThread()), std::memory_order_release);
    }

    void TestChunkWorkersDoNotPreemptCompletingThread()
    {
        ChunkJobData chunk{};
        WorkerPriorityContext context;
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
            &RecordChunkWorkerPriority,
            &context,
            nullptr,
            &chunk,
            1,
            {},
            JobSystem::ChunkScheduleMode::PublishNoAssist,
            1,
            1);
        handle.Complete();

        Require(context.observedPriority.load(std::memory_order_acquire) ==
                THREAD_PRIORITY_NORMAL,
            "Chunk worker priority can preempt the completing thread");
    }

    struct TaskflowWorkerPriorityContext
    {
        std::atomic<int> observedPriority{ INT_MIN };
        std::thread::id caller;
    };

    void RecordTaskflowWorkerPriority(void* raw, int, int)
    {
        auto& context = *static_cast<TaskflowWorkerPriorityContext*>(raw);
        if (std::this_thread::get_id() != context.caller)
        {
            int expected = INT_MIN;
            context.observedPriority.compare_exchange_strong(
                expected,
                GetThreadPriority(GetCurrentThread()),
                std::memory_order_release,
                std::memory_order_relaxed);
        }
    }

    void TestTaskflowWorkersDoNotPreemptCompletingThread()
    {
        TaskflowWorkerPriorityContext context;
        context.caller = std::this_thread::get_id();
        auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
            &RecordTaskflowWorkerPriority, &context, 100'000, 1);

        for (int retry = 0;
            retry < 1'000 && context.observedPriority.load(std::memory_order_acquire) == INT_MIN;
            ++retry)
        {
            std::this_thread::yield();
        }
        handle.Complete();

        Require(context.observedPriority.load(std::memory_order_acquire) ==
                THREAD_PRIORITY_NORMAL,
            "Taskflow worker priority can preempt the completing thread");
    }
#endif

    void TestTraceOverflow()
    {
        JobSystem::TraceSetEnabled(false);
        JobSystem::TraceClear();
        JobSystem::TraceSetEnabled(true);

        constexpr int overflow = 32;
        for (int i = 0; i < JobSystem::kMaxTraceEventsPerThread + overflow; ++i)
        {
            JobSystem::PushTraceEvent(
                JobSystem::TraceEventType::Claim,
                7,
                i,
                i * 4,
                4);
        }

        std::vector<JobSystem::TraceEvent> events(JobSystem::kMaxTraceEventsPerThread + overflow);
        const int readCount = JobSystem::TraceReadAll(
            events.data(), static_cast<int>(events.size()));
        Require(readCount == JobSystem::kMaxTraceEventsPerThread,
            "trace buffer did not remain bounded");
        Require(JobSystem::TraceDroppedEvents() == overflow,
            "trace overflow count mismatch");
        for (int i = 1; i < readCount; ++i)
        {
            Require(events[i - 1].timestampNs <= events[i].timestampNs,
                "trace timestamps are not monotonic");
        }

        JobSystem::TraceSetEnabled(false);
        JobSystem::TraceClear();
    }

    void TestTraceLifecycleOrder()
    {
        constexpr int rangeCount = 64;
        std::vector<ChunkJobData> chunks(rangeCount);
        std::vector<std::atomic<int>> hits(rangeCount);
        std::atomic<int> cleanupCount{ 0 };
        CooperativeChunkContext context{ &hits, &cleanupCount, nullptr, nullptr };

        JobSystem::TraceSetEnabled(false);
        JobSystem::TraceClear();
        JobSystem::TraceSetEnabled(true);
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
            &ExecuteCooperativeChunkRange,
            &context,
            &CleanupCooperativeChunkRange,
            chunks.data(),
            rangeCount,
            {},
            JobSystem::ChunkScheduleMode::PublishAssist,
            8,
            1);
        handle.Complete();
        JobSystem::TraceSetEnabled(false);

        std::vector<JobSystem::TraceEvent> events(8192);
        const int readCount = JobSystem::TraceReadAll(events.data(), static_cast<int>(events.size()));
        Require(JobSystem::TraceDroppedEvents() == 0, "lifecycle trace dropped events");

        uint64_t batchId = 0;
        uint64_t publishNs = 0;
        uint64_t firstBeginNs = 0;
        uint64_t lastEndNs = 0;
        uint64_t finalizeNs = 0;
        uint64_t completeNs = 0;
        int claimCount = 0;
        int beginCount = 0;
        int endCount = 0;
        for (int i = 0; i < readCount; ++i)
        {
            const auto& event = events[i];
            if (static_cast<JobSystem::TraceEventType>(event.eventType) ==
                    JobSystem::TraceEventType::Publish && event.batchId != 0)
            {
                batchId = event.batchId;
                publishNs = event.timestampNs;
                break;
            }
        }
        for (int i = 0; i < readCount && batchId != 0; ++i)
        {
            const auto& event = events[i];
            if (event.batchId != batchId) continue;
            const auto type = static_cast<JobSystem::TraceEventType>(event.eventType);
            if (type == JobSystem::TraceEventType::Claim) ++claimCount;
            else if (type == JobSystem::TraceEventType::ExecuteBegin)
            {
                ++beginCount;
                if (firstBeginNs == 0) firstBeginNs = event.timestampNs;
            }
            else if (type == JobSystem::TraceEventType::ExecuteEnd)
            {
                ++endCount;
                lastEndNs = std::max(lastEndNs, event.timestampNs);
            }
            else if (type == JobSystem::TraceEventType::FinalizeBegin) finalizeNs = event.timestampNs;
            else if (type == JobSystem::TraceEventType::HandleComplete) completeNs = event.timestampNs;
        }

        Require(publishNs > 0, "missing publish event");
        Require(firstBeginNs >= publishNs, "execution began before publication");
        Require(lastEndNs >= firstBeginNs, "execution end preceded begin");
        Require(finalizeNs >= lastEndNs, "finalization preceded last range");
        Require(completeNs >= finalizeNs, "handle completed before finalization");
        Require(claimCount == rangeCount, "trace claim count mismatch");
        Require(beginCount == rangeCount, "trace execute-begin count mismatch");
        Require(endCount == rangeCount, "trace execute-end count mismatch");
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "traced batch cleanup did not run exactly once");
        JobSystem::TraceClear();
    }

    void TestChunkPublishWakesOnlyTargetWorkers()
    {
        constexpr int rangeCount = 16;
        std::vector<ChunkJobData> chunks(rangeCount);
        std::atomic<int> executions{ 0 };

        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        JobSystem::TraceSetEnabled(false);
        JobSystem::TraceClear();
        JobSystem::TraceSetEnabled(true);
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
            [](void* raw, const ChunkJobData*, int, int)
            {
                static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed);
                std::this_thread::sleep_for(std::chrono::microseconds(100));
            },
            &executions, nullptr, chunks.data(), rangeCount, {},
            JobSystem::ChunkScheduleMode::PublishNoAssist, 2, 1);
        handle.Complete();
        JobSystem::TraceSetEnabled(false);

        std::vector<JobSystem::TraceEvent> events(8192);
        const int count = JobSystem::TraceReadAll(events.data(), static_cast<int>(events.size()));
        int wakes = 0;
        for (int i = 0; i < count; ++i)
        {
            if (static_cast<JobSystem::TraceEventType>(events[i].eventType) ==
                JobSystem::TraceEventType::Wake)
                ++wakes;
        }
        Require(executions.load(std::memory_order_relaxed) == rangeCount,
            "targeted wake test missed ranges");
        Require(wakes == 2, "Chunk publication did not wake exactly workerTarget workers");
        JobSystem::TraceClear();
    }

    void TestTraceRecordsProcessorForRangeEvents()
    {
        ChunkJobData chunk{};
        std::atomic<int> executions{ 0 };
        JobSystem::TraceSetEnabled(false);
        JobSystem::TraceClear();
        JobSystem::TraceSetEnabled(true);
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
            [](void* raw, const ChunkJobData*, int, int)
            {
                static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed);
            },
            &executions, nullptr, &chunk, 1, {},
            JobSystem::ChunkScheduleMode::PublishAssist, 1, 1);
        handle.Complete();
        JobSystem::TraceSetEnabled(false);

        std::vector<JobSystem::TraceEvent> events(256);
        const int count = JobSystem::TraceReadAll(events.data(), static_cast<int>(events.size()));
        int processorEvents = 0;
        for (int i = 0; i < count; ++i)
        {
            const auto type = static_cast<JobSystem::TraceEventType>(events[i].eventType);
            if (type != JobSystem::TraceEventType::ExecuteBegin &&
                type != JobSystem::TraceEventType::ExecuteEnd)
            {
                continue;
            }

            Require(events[i].processorIndex >= 0 && events[i].processorIndex < 32'768,
                "range trace did not record a valid processor index");
            ++processorEvents;
        }
        Require(executions.load(std::memory_order_relaxed) == 1,
            "processor trace test did not execute its range");
        Require(processorEvents == 2,
            "processor trace test did not observe begin and end events");
        JobSystem::TraceClear();
    }

    struct CompletePriorityContext
    {
        std::thread::id caller;
        std::atomic<int> callerRanges{ 0 };
        std::atomic<bool> workerEntered{ false };
        std::atomic<bool> releaseWorker{ false };
    };

    void TestCompleteDrainsTargetBeyondOldBudget()
    {
        constexpr int rangeCount = 12;
        std::vector<ChunkJobData> chunks(rangeCount);
        CompletePriorityContext context{ std::this_thread::get_id() };
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
            [](void* raw, const ChunkJobData*, int, int)
            {
                auto& state = *static_cast<CompletePriorityContext*>(raw);
                if (std::this_thread::get_id() == state.caller)
                {
                    state.callerRanges.fetch_add(1, std::memory_order_release);
                    std::this_thread::sleep_for(std::chrono::microseconds(300));
                }
                else
                {
                    state.workerEntered.store(true, std::memory_order_release);
                    state.releaseWorker.wait(false, std::memory_order_acquire);
                }
            },
            &context, nullptr, chunks.data(), rangeCount, {},
            JobSystem::ChunkScheduleMode::PublishAssist, 1, 1);

        for (int retry = 0;
            retry < 10'000 && !context.workerEntered.load(std::memory_order_acquire);
            ++retry)
        {
            std::this_thread::yield();
        }
        Require(context.workerEntered.load(std::memory_order_acquire),
            "worker did not claim the range reserved by the test");

        std::jthread watchdog([&context]
        {
            for (int retry = 0; retry < 20; ++retry)
            {
                if (context.callerRanges.load(std::memory_order_acquire) == rangeCount - 1)
                    break;
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }
            context.releaseWorker.store(true, std::memory_order_release);
            context.releaseWorker.notify_all();
        });
        handle.Complete();
        watchdog.join();

        Require(context.callerRanges.load(std::memory_order_acquire) == rangeCount - 1,
            "Complete stopped claiming target ranges after its old time budget");
    }
}

int main()
{
    std::cout << std::unitbuf;
    JobSystem::Scheduler::Initialize();
    try
    {
        TestCooperativeStatsReset();
        std::cout << "PASS CooperativeStatsReset\n";
        TestTraceOverflow();
        std::cout << "PASS TraceOverflow\n";
        TestTraceLifecycleOrder();
        std::cout << "PASS TraceLifecycleOrder\n";
        TestTraceRecordsProcessorForRangeEvents();
        std::cout << "PASS TraceRecordsProcessorForRangeEvents\n";
        TestChunkPublishWakesOnlyTargetWorkers();
        std::cout << "PASS ChunkPublishWakesOnlyTargetWorkers\n";
        TestCompleteDrainsTargetBeyondOldBudget();
        std::cout << "PASS CompleteDrainsTargetBeyondOldBudget\n";
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
#ifdef _WIN32
        TestChunkWorkersDoNotPreemptCompletingThread();
        std::cout << "PASS ChunkWorkersDoNotPreemptCompletingThread\n";
        TestTaskflowWorkersDoNotPreemptCompletingThread();
        std::cout << "PASS TaskflowWorkersDoNotPreemptCompletingThread\n";
#endif
        TestConcurrentChunkComplete();
        std::cout << "PASS ConcurrentChunkComplete\n";
        TestExhaustedChunkTicketsDrain();
        std::cout << "PASS ExhaustedChunkTicketsDrain\n";
        TestDependentChunkRangeCooperation();
        std::cout << "PASS DependentChunkRangeCooperation\n";
        TestChunkShutdownRace();
        std::cout << "PASS ChunkShutdownRace\n";
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
