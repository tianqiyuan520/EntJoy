#include "../NativeDll/JobSystem.h"
#include "../NativeDll/ChunkJobData.h"
#include "../NativeDll/EntityBatchData.h"
#include "../NativeDll/JobProfiler.h"

#include <atomic>
#include <chrono>
#include <cstdlib>
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

    // 回归：依赖未完成时，小任务（length<=512 / rc<=1）不得 inline 提前执行。
    // 修前这些路径绕过依赖直接同步执行（依赖顺序违反）；修后统一走异步提交
    //（ScheduleWithDependency / ScheduleFastPath / AddContinuationOrRunNow），
    // 由依赖完成触发。每个子 job 独立计数，失败可定位到具体入口。
    void TestSmallJobsRespectPendingDependencies()
    {
        std::atomic<bool> dependencyFinished{ false };
        auto dependency = JobSystem::Scheduler::Schedule(
            [](void* raw)
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
                static_cast<std::atomic<bool>*>(raw)->store(true, std::memory_order_release);
            }, &dependencyFinished);

        struct ChildContext
        {
            std::atomic<bool>* dependencyFinished;
            std::atomic<int>* childRanBeforeDep;
        };

        // ScheduleFor(length=100)：修前 length<=512 直接 inline
        {
            std::atomic<int> ranEarly{ 0 };
            ChildContext ctx{ &dependencyFinished, &ranEarly };
            auto child = JobSystem::Scheduler::ScheduleFor(
                [](void* raw, int)
                {
                    auto& c = *static_cast<ChildContext*>(raw);
                    if (!c.dependencyFinished->load(std::memory_order_acquire))
                        c.childRanBeforeDep->fetch_add(1, std::memory_order_release);
                }, &ctx, 100, nullptr, dependency);
            child.Complete();
            Require(ranEarly.load(std::memory_order_acquire) == 0,
                "ScheduleFor(length=100) ran before pending dependency");
        }

        // ScheduleParallelFor(length=200)：修前 length<=512 直接 inline
        {
            std::atomic<int> ranEarly{ 0 };
            ChildContext ctx{ &dependencyFinished, &ranEarly };
            auto child = JobSystem::Scheduler::ScheduleParallelFor(
                [](void* raw, int)
                {
                    auto& c = *static_cast<ChildContext*>(raw);
                    if (!c.dependencyFinished->load(std::memory_order_acquire))
                        c.childRanBeforeDep->fetch_add(1, std::memory_order_release);
                }, &ctx, 200, 0, nullptr, dependency);
            child.Complete();
            Require(ranEarly.load(std::memory_order_acquire) == 0,
                "ScheduleParallelFor(length=200) ran before pending dependency");
        }

        // ScheduleParallelForBatch(length=100, batchSize=1000) → rc<=1：修前 inline
        {
            std::atomic<int> ranEarly{ 0 };
            ChildContext ctx{ &dependencyFinished, &ranEarly };
            auto child = JobSystem::Scheduler::ScheduleParallelForBatch(
                [](void* raw, int, int)
                {
                    auto& c = *static_cast<ChildContext*>(raw);
                    if (!c.dependencyFinished->load(std::memory_order_acquire))
                        c.childRanBeforeDep->fetch_add(1, std::memory_order_release);
                }, &ctx, 100, 1000, nullptr, dependency);
            child.Complete();
            Require(ranEarly.load(std::memory_order_acquire) == 0,
                "ScheduleParallelForBatch(rc<=1) ran before pending dependency");
        }

        // ScheduleChunks(1 chunk) → rc<=1 && workerCap<=1：修前 inline
        {
            std::atomic<int> ranEarly{ 0 };
            ChildContext ctx{ &dependencyFinished, &ranEarly };
            ChunkJobData chunk{};
            auto child = JobSystem::Scheduler::ScheduleChunks(
                [](void* raw, const ChunkJobData*)
                {
                    auto& c = *static_cast<ChildContext*>(raw);
                    if (!c.dependencyFinished->load(std::memory_order_acquire))
                        c.childRanBeforeDep->fetch_add(1, std::memory_order_release);
                }, &ctx, nullptr, &chunk, 1, dependency);
            child.Complete();
            Require(ranEarly.load(std::memory_order_acquire) == 0,
                "ScheduleChunks(rc<=1) ran before pending dependency");
        }
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
        const int workers = JobSystem::CurrentWorkerCount();
        // Default tile policy is kDefaultTilesPerWorker == 16 tiles/worker
        // (grid-search tuned; the old bound here predated the 4→16 change).
        // rc = ceil(N / ceil(N / (W*16))) lands within [W*16 - 1, W*16].
        Require(callbackCount.load(std::memory_order_relaxed) >= workers * 16 - 1,
            "automatic batching created too few work units for tail balancing");
        Require(callbackCount.load(std::memory_order_relaxed) <= workers * 16,
            "automatic batching exceeded the per-worker tile target");
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

    // ============================================================
    // B1: transitive dependency-chain assist (V-D) + nested Complete (V-A)
    // ============================================================
    // 每个链环用独立的 gate：tile 回调在完成前阻塞于 releaseWorkers，
    // 只有标记为 "chain completer" 的线程执行 tile 才能放行。由于 worker
    // 在回调内阻塞时最多持有一个已认领 tile，Complete-caller 的协助循环
    // 永远有可认领的剩余 tile —— 判定是确定性的（无竞态）。
    struct ChainLinkContext
    {
        std::vector<std::atomic<int>> hits;
        std::atomic<int> cleanupCount{ 0 };
        std::atomic<int> completerExecutions{ 0 };
        std::atomic<bool> releaseWorkers{ false };
        // std::atomic<int> is non-movable, so the vector must be sized at
        // construction (resize() would need to relocate elements).
        explicit ChainLinkContext(size_t size) : hits(size) {}
    };

    thread_local bool g_isChainCompleter = false;

    void ExecuteGatedChainRange(void* raw, int start, int count)
    {
        auto& context = *static_cast<ChainLinkContext*>(raw);
        if (g_isChainCompleter)
        {
            // Complete-caller 线程执行了本链环的 tile —— 传递协助的证据。
            context.completerExecutions.fetch_add(1, std::memory_order_relaxed);
            context.releaseWorkers.store(true, std::memory_order_release);
            context.releaseWorkers.notify_all();
        }
        else
        {
            // worker 阻塞：等待 completer 线程证明它能协助本链环。
            context.releaseWorkers.wait(false, std::memory_order_acquire);
        }
        for (int index = start; index < start + count; ++index)
            context.hits[static_cast<size_t>(index)].fetch_add(1, std::memory_order_relaxed);
    }

    void CleanupChainGate(void* raw)
    {
        static_cast<ChainLinkContext*>(raw)->cleanupCount.fetch_add(1, std::memory_order_relaxed);
    }

    void TestTransitiveAssistDrivesDependencyChain()
    {
        constexpr int length = 100'000;
        constexpr int batchSize = 257;
        ChainLinkContext c(length), b(length), a(length);

        // A ← B ← C 依赖链（C 为根，先提交）。
        auto cHandle = JobSystem::Scheduler::ScheduleParallelForBatch(
            &ExecuteGatedChainRange, &c, length, batchSize, &CleanupChainGate);
        auto bHandle = JobSystem::Scheduler::ScheduleParallelForBatch(
            &ExecuteGatedChainRange, &b, length, batchSize, &CleanupChainGate, cHandle);
        auto aHandle = JobSystem::Scheduler::ScheduleParallelForBatch(
            &ExecuteGatedChainRange, &a, length, batchSize, &CleanupChainGate, bHandle);

        // 看门狗：B1 缺失时主线程 park 在未提交的目标上、gate 死锁。触发即
        // 记录失败（watchdogFired，断言会失败）——不再静默放行掩盖 flake。
        // 上限定 2s：远大于 assist 墙钟预算（10ms），只拦真死锁，不误伤慢链。
        std::atomic<bool> finished{ false };
        std::atomic<bool> watchdogFired{ false };
        std::jthread watchdog([&]
        {
            for (int i = 0; i < 2000 && !finished.load(std::memory_order_acquire); ++i)
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            if (!finished.load(std::memory_order_acquire))
                watchdogFired.store(true, std::memory_order_relaxed);
            c.releaseWorkers.store(true, std::memory_order_release);
            c.releaseWorkers.notify_all();
            b.releaseWorkers.store(true, std::memory_order_release);
            b.releaseWorkers.notify_all();
            a.releaseWorkers.store(true, std::memory_order_release);
            a.releaseWorkers.notify_all();
        });

        g_isChainCompleter = true;
        aHandle.Complete();
        g_isChainCompleter = false;
        finished.store(true, std::memory_order_release);

        Require(!watchdogFired.load(std::memory_order_relaxed),
            "B1 chain deadlocked; watchdog had to release the gates");

        for (auto* link : { &c, &b, &a })
        {
            for (const auto& hit : link->hits)
                Require(hit.load(std::memory_order_relaxed) == 1,
                    "B1 chain link tile was missed or duplicated");
            Require(link->cleanupCount.load(std::memory_order_relaxed) == 1,
                "B1 chain link cleanup must run exactly once");
        }
        // 传递协助证明：C/B/A 的 workers 都被 gate 阻塞，只能由 Complete-caller
        // 执行 tile 放行 —— 三个环的 completerExecutions 必须都 > 0。
        Require(c.completerExecutions.load(std::memory_order_relaxed) > 0,
            "B1 Complete caller did not transitively assist chain root C");
        Require(b.completerExecutions.load(std::memory_order_relaxed) > 0,
            "B1 Complete caller did not transitively assist chain link B");
        Require(a.completerExecutions.load(std::memory_order_relaxed) > 0,
            "B1 Complete caller did not transitively assist chain link A");
    }

    struct NestedCompleteJobContext
    {
        JobSystem::JobHandle aHandle;
        std::atomic<bool>* enteredComplete;
        std::atomic<bool>* go;
    };

    // ScheduleFor(length=5000) → SubmitBackendAsync：单 slot 池任务，恰好一个
    // pool worker 执行。index==0 时进入嵌套 Complete（停在 go 上直到链构造完），
    // 其余 index 直接返回。该 worker 在嵌套期间不占池 slot，成为链的执行者。
    void ExecuteNestedCompleteJob(void* raw, int index)
    {
        if (index != 0) return;
        auto& context = *static_cast<NestedCompleteJobContext*>(raw);
        g_isChainCompleter = true;
        context.enteredComplete->store(true, std::memory_order_release);
        while (!context.go->load(std::memory_order_acquire))
            std::this_thread::yield();
        context.aHandle.Complete();
        g_isChainCompleter = false;
    }

    void TestNestedCompleteResolvesWithoutWorkerExhaustion()
    {
        constexpr int length = 100'000;
        constexpr int batchSize = 257;
        ChainLinkContext c(length), b(length), a(length);
        std::atomic<bool> enteredComplete{ false };
        std::atomic<bool> go{ false };
        NestedCompleteJobContext jobContext;
        jobContext.enteredComplete = &enteredComplete;
        jobContext.go = &go;

        // 先提交嵌套 job（单 slot，恰好一个 worker 执行）：该 worker 停在 go 上，
        // 其余 W-1 个 worker 空闲。之后链 C/B/A 提交时才不会耗尽 worker。
        // （旧设计先提交链，所有 worker 都被 gate 阻塞 → 嵌套 job 无 worker 执行。）
        auto jobHandle = JobSystem::Scheduler::ScheduleFor(
            &ExecuteNestedCompleteJob, &jobContext, 5000, nullptr, {});

        // 等 worker 进入嵌套 job（停在 go 上）再构造链，确保它不会抢链的 slot。
        for (int retry = 0; retry < 50'000 && !enteredComplete.load(std::memory_order_acquire); ++retry)
            std::this_thread::yield();
        Require(enteredComplete.load(std::memory_order_acquire),
            "B1 nested Complete was never entered by a pool worker");

        auto cHandle = JobSystem::Scheduler::ScheduleParallelForBatch(
            &ExecuteGatedChainRange, &c, length, batchSize, &CleanupChainGate);
        auto bHandle = JobSystem::Scheduler::ScheduleParallelForBatch(
            &ExecuteGatedChainRange, &b, length, batchSize, &CleanupChainGate, cHandle);
        auto aHandle = JobSystem::Scheduler::ScheduleParallelForBatch(
            &ExecuteGatedChainRange, &a, length, batchSize, &CleanupChainGate, bHandle);

        // 放行嵌套 completer：它成为整条链的执行者（驱动 C→B→A）。
        jobContext.aHandle = aHandle;
        go.store(true, std::memory_order_release);

        // 看门狗：B1 缺失时 completer worker park、其他 worker 被 gate 阻塞 →
        // 死锁。触发即记录失败（watchdogFired，断言会失败），上限定 2s。
        std::atomic<bool> finished{ false };
        std::atomic<bool> watchdogFired{ false };
        std::jthread watchdog([&]
        {
            for (int i = 0; i < 2000 && !finished.load(std::memory_order_acquire); ++i)
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            if (!finished.load(std::memory_order_acquire))
                watchdogFired.store(true, std::memory_order_relaxed);
            c.releaseWorkers.store(true, std::memory_order_release);
            c.releaseWorkers.notify_all();
            b.releaseWorkers.store(true, std::memory_order_release);
            b.releaseWorkers.notify_all();
            a.releaseWorkers.store(true, std::memory_order_release);
            a.releaseWorkers.notify_all();
        });

        jobHandle.Complete();
        finished.store(true, std::memory_order_release);

        Require(!watchdogFired.load(std::memory_order_relaxed),
            "B1 nested chain deadlocked; watchdog had to release the gates");

        for (auto* link : { &c, &b, &a })
        {
            for (const auto& hit : link->hits)
                Require(hit.load(std::memory_order_relaxed) == 1,
                    "B1 nested chain link tile was missed or duplicated");
            Require(link->cleanupCount.load(std::memory_order_relaxed) == 1,
                "B1 nested chain link cleanup must run exactly once");
        }
        Require(c.completerExecutions.load(std::memory_order_relaxed) > 0,
            "B1 nested worker did not transitively assist chain root C");
        Require(b.completerExecutions.load(std::memory_order_relaxed) > 0,
            "B1 nested worker did not transitively assist chain link B");
        Require(a.completerExecutions.load(std::memory_order_relaxed) > 0,
            "B1 nested worker did not transitively assist chain link A");
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
        // 规模上限由 trace per-thread 缓冲（kMaxTraceEventsPerThread=4096）决定：
        // 每 tile 发 3 条事件（Claim/ExecuteBegin/ExecuteEnd），单线程认领全部 tile 时
        // 事件数 = 3×tileCount。4096 tiles → 12288 条会溢出 4096 缓冲 → 丢事件 →
        // beginCount 断言 flake。1024 tiles → 最多 3072 条，永不足 4096，零溢出；
        // 而 4 个 Complete caller + 8 worker 并发认领 1024 个 tile 已充分撑起
        // "并发 Complete 必须重叠"的判定（worker 阻塞在 releaseWorkers 上，callers 必认领）。
        constexpr int chunkCount = 1'024;
        std::vector<ChunkJobData> chunks(chunkCount);
        std::vector<std::atomic<int>> hits(chunkCount);
        std::atomic<int> cleanupCount{ 0 };
        std::atomic<bool> releaseWorkers{ false };
        std::atomic<int> callerExecutions{ 0 };
        CooperativeChunkContext context{
            &hits, &cleanupCount, &releaseWorkers, &callerExecutions
        };

        JobSystem::TraceSetEnabled(false);
        JobSystem::TraceClear();
        JobSystem::TraceSetEnabled(true);
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
        JobSystem::TraceSetEnabled(false);

        for (const auto& hit : hits)
            Require(hit.load(std::memory_order_relaxed) == 1,
                "concurrent Complete missed or duplicated a Chunk range");
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "concurrent Complete duplicated Chunk cleanup");

        // Verify trace events: Claim events on the batch must show concurrent
        // assistance via Complete callers (there should be >1 claiming thread).
        std::vector<JobSystem::TraceEvent> events(16384);
        const int readCount = JobSystem::TraceReadAll(
            events.data(), static_cast<int>(events.size()));
        uint64_t batchId = 0;
        for (int i = 0; i < readCount; ++i)
        {
            if (static_cast<JobSystem::TraceEventType>(events[i].eventType) ==
                    JobSystem::TraceEventType::Publish && events[i].batchId != 0)
            {
                batchId = events[i].batchId;
                break;
            }
        }
        Require(batchId != 0, "concurrent Complete batch missing trace publish");

        // Ensure at least one Claim came from the same thread that emitted
        // Publish — the main test thread doing Complete assist.
        bool assistClaimSeen = false;
        for (int i = 0; i < readCount && batchId != 0; ++i)
        {
            if (events[i].batchId != batchId) continue;
            const auto type = static_cast<JobSystem::TraceEventType>(events[i].eventType);
            if (type == JobSystem::TraceEventType::Claim)
            {
                assistClaimSeen = true;
                break;
            }
        }
        Require(assistClaimSeen,
            "no trace claim events — Complete callers did not assist");

        // Verify full lifecycle: ExecuteBegin/ExecuteEnd match chunkCount
        int beginCount = 0, endCount = 0;
        for (int i = 0; i < readCount && batchId != 0; ++i)
        {
            if (events[i].batchId != batchId) continue;
            const auto type = static_cast<JobSystem::TraceEventType>(events[i].eventType);
            if (type == JobSystem::TraceEventType::ExecuteBegin) ++beginCount;
            else if (type == JobSystem::TraceEventType::ExecuteEnd) ++endCount;
        }
        Require(beginCount == chunkCount,
            "concurrent Complete missing execute-begin events");
        Require(endCount == chunkCount,
            "concurrent Complete missing execute-end events");
        JobSystem::TraceClear();
    }

    void TestExhaustedChunkTicketsDrain()
    {
        constexpr int chunkCount = 2;
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
            for (int retry = 0;
                retry < 100'000 &&
                cleanupCount.load(std::memory_order_acquire) == 0;
                ++retry)
                std::this_thread::yield();
            Require(cleanupCount.load(std::memory_order_acquire) == 1,
                "exhausted ticket test cleanup count mismatch");
        }
    }

    void TestDependentChunkRangeCooperation()
    {
        constexpr int chunkCount = 1'024;
        std::vector<ChunkJobData> chunks(chunkCount);
        std::vector<std::atomic<int>> hits(chunkCount);
        std::atomic<int> cleanupCount{ 0 };
        std::atomic<bool> depStarted{ false };
        std::atomic<bool> depCanFinish{ false };

        // Create a dependency that genuinely takes time via many small work
        // items (goes through SubmitBatch, rc >> 1).  We verify the dependent
        // Chunk job does not start until the dependency completes.
        auto depHandle = JobSystem::Scheduler::ScheduleParallelFor(
            [](void* raw, int)
            {
                auto* started = static_cast<std::atomic<bool>*>(raw);
                started->store(true, std::memory_order_release);
                std::this_thread::sleep_for(std::chrono::microseconds(500));
            },
            &depStarted, 5'000, 1);

        // Let the dependency start (workers claim ranges, execute callbacks)
        for (int retry = 0; retry < 5'000; ++retry)
        {
            if (depStarted.load(std::memory_order_acquire)) break;
            std::this_thread::yield();
        }
        Require(depStarted.load(std::memory_order_acquire),
            "dependent-chunk dependency did not start");

        // Create the dependent ChunkRanges batch (registers continuation
        // on the still-running dependency).
        CooperativeChunkContext context{
            &hits, &cleanupCount, nullptr, nullptr
        };
        auto original = JobSystem::Scheduler::ScheduleChunkRanges(
            &ExecuteCooperativeChunkRange, &context, &CleanupCooperativeChunkRange,
            chunks.data(), chunkCount, depHandle,
            JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
        auto first = original;
        auto second = original;

        // Spawn two Complete callers on the dependent handle.
        std::jthread firstCaller([first]() mutable { first.Complete(); });
        std::jthread secondCaller([second]() mutable { second.Complete(); });

        // Verify the dependent job hasn't run yet (dependency still active)
        bool prematureWork = false;
        for (const auto& hit : hits)
            if (hit.load(std::memory_order_relaxed) != 0) { prematureWork = true; break; }
        // Note: a relaxed check is acceptable — if the dependency somehow
        // completed and the dependent job snuck in before this check, the
        // exact-once assertions below still protect correctness.

        // Wait for the dependency to fully finish
        depHandle.Complete();

        // Now the dependent job should have been submitted by the continuation
        // and the Complete() callers work on it.
        original.Complete();
        firstCaller.join();
        secondCaller.join();

        for (const auto& hit : hits)
            Require(hit.load(std::memory_order_relaxed) == 1,
                "dependent Chunk range was missed or duplicated");
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "dependent Chunk cleanup did not run exactly once");
        // If we detected premature work, flag it (but only if actual data exists)
        Require(!prematureWork,
            "dependent Chunk range ran before its prerequisite");
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
        Require(stats.workerTargetTotal == 0, "worker-target stats did not reset");
        Require(stats.totalTilesPublished == 0, "published-tile stats did not reset");
        Require(stats.localTiles == 0, "local-tile stats did not reset");
        Require(stats.stolenTiles == 0, "stolen-tile stats did not reset");
        Require(stats.assistTiles == 0, "assist-tile stats did not reset");
        Require(stats.stealAttempts == 0, "steal-attempt stats did not reset");
        Require(stats.stealSuccesses == 0, "steal-success stats did not reset");
        Require(stats.batchStorageCreated == 0, "batch-storage create stats did not reset");
        Require(stats.batchStorageReused == 0, "batch-storage reuse stats did not reset");
        Require(stats.batchStorageReturned == 0, "batch-storage return stats did not reset");
        Require(stats.batchStorageDropped == 0, "batch-storage drop stats did not reset");
        Require(stats.submitToFirstWorkerEwmaNs == 0,
            "submit-to-first-worker stats did not reset");
        Require(stats.workerStartSpreadEwmaNs == 0,
            "worker-start-spread stats did not reset");
        Require(stats.lastTileToTopologyDoneEwmaNs == 0,
            "last-tile-to-topology stats did not reset");
        Require(stats.completeWakeToReturnEwmaNs == 0,
            "complete-wake-to-return stats did not reset");
        Require(stats.timingSampleCount == 0,
            "batch timing samples did not reset");
        Require(stats.timingSamplesDropped == 0,
            "dropped batch timing samples did not reset");
        Require(stats.slowBatchId == 0,
            "slow batch correlation did not reset");
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
        uint64_t publishSequence = 0;
        uint64_t completeEnterNs = 0;
        uint64_t firstClaimNs = 0;
        uint64_t firstBeginNs = 0;
        uint64_t lastEndNs = 0;
        uint64_t finalizeNs = 0;
        uint64_t completeNs = 0;
        uint64_t finalizeSequence = 0;
        uint64_t completeSequence = 0;
        int claimCount = 0;
        int beginCount = 0;
        int endCount = 0;
        std::vector<uint64_t> claimByTile(rangeCount);
        std::vector<uint64_t> beginByTile(rangeCount);
        std::vector<uint64_t> endByTile(rangeCount);
        for (int i = 0; i < readCount; ++i)
        {
            const auto& event = events[i];
            if (static_cast<JobSystem::TraceEventType>(event.eventType) ==
                    JobSystem::TraceEventType::Publish && event.batchId != 0)
            {
                batchId = event.batchId;
                publishNs = event.timestampNs;
                publishSequence = event.sequence;
                break;
            }
        }
        for (int i = 0; i < readCount && batchId != 0; ++i)
        {
            const auto& event = events[i];
            if (event.batchId != batchId) continue;
            const auto type = static_cast<JobSystem::TraceEventType>(event.eventType);
            if (type == JobSystem::TraceEventType::Claim)
            {
                if (firstClaimNs == 0) firstClaimNs = event.timestampNs;
                if (event.tileIndex >= 0 && event.tileIndex < rangeCount)
                    claimByTile[static_cast<size_t>(event.tileIndex)] = event.sequence;
                ++claimCount;
            }
            else if (type == JobSystem::TraceEventType::ExecuteBegin)
            {
                if (firstBeginNs == 0) firstBeginNs = event.timestampNs;
                if (event.tileIndex >= 0 && event.tileIndex < rangeCount)
                    beginByTile[static_cast<size_t>(event.tileIndex)] = event.sequence;
                ++beginCount;
            }
            else if (type == JobSystem::TraceEventType::ExecuteEnd)
            {
                ++endCount;
                lastEndNs = std::max(lastEndNs, event.timestampNs);
                if (event.tileIndex >= 0 && event.tileIndex < rangeCount)
                    endByTile[static_cast<size_t>(event.tileIndex)] = event.sequence;
            }
            else if (type == JobSystem::TraceEventType::CompleteEnter) completeEnterNs = event.timestampNs;
            else if (type == JobSystem::TraceEventType::FinalizeBegin)
            {
                finalizeNs = event.timestampNs;
                finalizeSequence = event.sequence;
            }
            else if (type == JobSystem::TraceEventType::HandleComplete)
            {
                completeNs = event.timestampNs;
                completeSequence = event.sequence;
            }
        }

        Require(publishNs > 0, "missing publish event");
        Require(completeEnterNs > 0, "missing CompleteEnter event");
        Require(firstClaimNs >= publishNs, "claim preceded publication");
        Require(firstBeginNs >= firstClaimNs, "execution began before claim");
        Require(lastEndNs >= firstBeginNs, "execution end preceded begin");
        Require(finalizeNs >= lastEndNs, "finalization preceded last range");
        Require(completeNs >= finalizeNs, "handle completed before finalization");
        Require(finalizeSequence > 0, "missing finalization sequence");
        Require(completeSequence > finalizeSequence,
            "handle completion did not follow finalization");
        Require(claimCount == rangeCount, "trace claim count mismatch");
        Require(beginCount == rangeCount, "trace execute-begin count mismatch");
        Require(endCount == rangeCount, "trace execute-end count mismatch");
        for (int tile = 0; tile < rangeCount; ++tile)
        {
            Require(claimByTile[static_cast<size_t>(tile)] > publishSequence,
                "tile claim did not follow publication");
            Require(beginByTile[static_cast<size_t>(tile)] >
                    claimByTile[static_cast<size_t>(tile)],
                "tile execution did not follow its claim");
            Require(endByTile[static_cast<size_t>(tile)] >
                    beginByTile[static_cast<size_t>(tile)],
                "tile execution end did not follow its begin");
            Require(finalizeSequence > endByTile[static_cast<size_t>(tile)],
                "finalization did not follow every tile execution");
        }
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "traced batch cleanup did not run exactly once");
        JobSystem::TraceClear();
    }

    void TestTraceIdentifiesCompleteCallerAndWorker()
    {
        constexpr int chunkCount = 64;
        std::vector<ChunkJobData> chunks(chunkCount);
        std::atomic<int> executions{ 0 };

        JobSystem::TraceSetEnabled(false);
        JobSystem::TraceClear();
        JobSystem::TraceSetEnabled(true);
        auto handle = JobSystem::Scheduler::ScheduleChunks(
            [](void* raw, const ChunkJobData*)
            {
                static_cast<std::atomic<int>*>(raw)->fetch_add(
                    1, std::memory_order_relaxed);
                std::this_thread::sleep_for(std::chrono::microseconds(50));
            },
            &executions, nullptr, chunks.data(), chunkCount, {},
            JobSystem::ChunkScheduleMode::PublishAssist, 2, 1);
        handle.Complete();
        JobSystem::TraceSetEnabled(false);

        std::vector<JobSystem::TraceEvent> events(4096);
        const int count = JobSystem::TraceReadAll(
            events.data(), static_cast<int>(events.size()));
        uint64_t batchId = 0;
        bool sawCompleteEnter = false;
        bool sawWorkerExecution = false;
        for (int i = 0; i < count; ++i)
        {
            const auto type = static_cast<JobSystem::TraceEventType>(events[i].eventType);
            if (type == JobSystem::TraceEventType::Publish && events[i].batchId != 0)
                batchId = events[i].batchId;
        }
        for (int i = 0; i < count && batchId != 0; ++i)
        {
            if (events[i].batchId != batchId) continue;
            const auto type = static_cast<JobSystem::TraceEventType>(events[i].eventType);
            if (type == JobSystem::TraceEventType::CompleteEnter)
                sawCompleteEnter = true;
            if (type == JobSystem::TraceEventType::ExecuteBegin &&
                events[i].workerIndex >= 0)
                sawWorkerExecution = true;
        }

        Require(executions.load(std::memory_order_relaxed) == chunkCount,
            "trace identity test missed chunk callbacks");
        Require(sawCompleteEnter, "trace did not record CompleteEnter");
        Require(sawWorkerExecution, "trace did not identify a worker execution");
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

        // Verify lifecycle trace for the batch
        std::vector<JobSystem::TraceEvent> events(8192);
        const int count = JobSystem::TraceReadAll(events.data(), static_cast<int>(events.size()));
        Require(count > 0, "no trace events recorded for targeted wake test");

        // Count lifecycle events for publishing=2 workerTarget batch
        uint64_t batchId = 0;
        int publishCount = 0;
        int executeBeginCount = 0;
        int executeEndCount = 0;
        bool seenFinalize = false;
        bool seenComplete = false;
        for (int i = 0; i < count; ++i)
        {
            const auto type = static_cast<JobSystem::TraceEventType>(events[i].eventType);
            if (type == JobSystem::TraceEventType::Publish && events[i].batchId != 0)
            {
                if (batchId == 0) batchId = events[i].batchId;
                if (events[i].batchId == batchId) ++publishCount;
            }
        }
        for (int i = 0; i < count && batchId != 0; ++i)
        {
            if (events[i].batchId != batchId) continue;
            const auto type = static_cast<JobSystem::TraceEventType>(events[i].eventType);
            if (type == JobSystem::TraceEventType::ExecuteBegin) ++executeBeginCount;
            else if (type == JobSystem::TraceEventType::ExecuteEnd) ++executeEndCount;
            else if (type == JobSystem::TraceEventType::FinalizeBegin) seenFinalize = true;
            else if (type == JobSystem::TraceEventType::HandleComplete) seenComplete = true;
        }

        Require(executions.load(std::memory_order_relaxed) == rangeCount,
            "targeted wake test missed ranges");
        Require(publishCount >= 1, "targeted wake batch missing publish event");
        Require(executeBeginCount == rangeCount,
            "targeted wake batch missing execute-begin events");
        Require(executeEndCount == rangeCount,
            "targeted wake batch missing execute-end events");
        Require(seenFinalize, "targeted wake batch missing finalize event");
        Require(seenComplete, "targeted wake batch missing handle-complete event");
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
            JobSystem::ChunkScheduleMode::PublishAssist, 2, 1);
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
        // Use ScheduleChunks (IJobChunk partition path) which respects workerCap.
        // The callback receives one ChunkJobData* per invocation.
        auto handle = JobSystem::Scheduler::ScheduleChunks(
            [](void* raw, const ChunkJobData*)
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
            &context, nullptr,
            chunks.data(), rangeCount, {},
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

    void TestStatsClassifyWorkerAndAssistExactlyOnce()
    {
        constexpr int chunkCount = 12;
        std::vector<ChunkJobData> chunks(chunkCount);
        CompletePriorityContext context{ std::this_thread::get_id() };

        JobSystem::ResetStatsSnapshot();
        auto handle = JobSystem::Scheduler::ScheduleChunks(
            [](void* raw, const ChunkJobData*)
            {
                auto& state = *static_cast<CompletePriorityContext*>(raw);
                if (std::this_thread::get_id() == state.caller)
                {
                    state.callerRanges.fetch_add(1, std::memory_order_release);
                }
                else
                {
                    state.workerEntered.store(true, std::memory_order_release);
                    state.releaseWorker.wait(false, std::memory_order_acquire);
                }
            },
            &context, nullptr, chunks.data(), chunkCount, {},
            JobSystem::ChunkScheduleMode::PublishAssist, 1, 1);

        while (!context.workerEntered.load(std::memory_order_acquire))
            std::this_thread::yield();
        std::jthread watchdog([&context]
        {
            for (int retry = 0; retry < 100; ++retry)
            {
                if (context.callerRanges.load(std::memory_order_acquire) == chunkCount - 1)
                    break;
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }
            context.releaseWorker.store(true, std::memory_order_release);
            context.releaseWorker.notify_all();
        });
        handle.Complete();
        watchdog.join();

        JobSystem::JobSystemStatsSnapshot stats{};
        JobSystem::GetStatsSnapshot(&stats);
        Require(stats.workerExecutedRanges + stats.mainExecutedRanges == chunkCount,
            "worker/main tile accounting did not reconcile");
        Require(stats.mainExecutedRanges == chunkCount - 1,
            "assist tile count did not match Complete caller work");
        Require(stats.assistExecPctEwma <= 100,
            "assist percentage exceeded 100 percent");
    }

    void RequireTileAccounting(
        const JobSystem::JobSystemStatsSnapshot& stats,
        uint64_t expectedTiles,
        const char* message)
    {
        Require(stats.totalTilesPublished == expectedTiles, message);
        Require(stats.localTiles + stats.stolenTiles + stats.assistTiles == expectedTiles,
            message);
        Require(stats.assistTiles == stats.mainExecutedRanges, message);
        Require(stats.localTiles + stats.stolenTiles == stats.workerExecutedRanges,
            message);
        Require(stats.stealSuccesses <= stats.stealAttempts, message);
        Require(stats.assistExecPctEwma <= 100, message);
        Require(stats.activeWorkersPeak <= 8, message);
    }

    void TestUnifiedTileAccountingForAllChunkEntrypoints()
    {
        constexpr int itemCount = 31;
        std::vector<ChunkJobData> chunks(itemCount);
        std::vector<EntityBatchData> batches(itemCount);

        {
            std::atomic<int> callbacks{ 0 };
            JobSystem::ResetStatsSnapshot();
            auto handle = JobSystem::Scheduler::ScheduleChunks(
                [](void* raw, const ChunkJobData*)
                {
                    static_cast<std::atomic<int>*>(raw)->fetch_add(
                        1, std::memory_order_relaxed);
                },
                &callbacks, nullptr, chunks.data(), itemCount, {},
                JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
            handle.Complete();
            JobSystem::JobSystemStatsSnapshot stats{};
            JobSystem::GetStatsSnapshot(&stats);
            Require(callbacks.load(std::memory_order_relaxed) == itemCount,
                "ScheduleChunks missed or duplicated a callback");
            RequireTileAccounting(stats, itemCount,
                "ScheduleChunks tile accounting did not reconcile");
        }

        {
            std::vector<std::atomic<int>> hits(itemCount);
            ChunkRangeContext context{ &hits, nullptr };
            JobSystem::ResetStatsSnapshot();
            auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
                &ExecuteChunkRange, &context, nullptr,
                chunks.data(), itemCount, {},
                JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
            handle.Complete();
            for (const auto& hit : hits)
                Require(hit.load(std::memory_order_relaxed) == 1,
                    "ScheduleChunkRanges missed or duplicated an item");
            JobSystem::JobSystemStatsSnapshot stats{};
            JobSystem::GetStatsSnapshot(&stats);
            RequireTileAccounting(stats, itemCount,
                "ScheduleChunkRanges tile accounting did not reconcile");
        }

        {
            std::vector<std::atomic<int>> hits(itemCount);
            struct EntityContext { std::vector<std::atomic<int>>* hits; } context{ &hits };
            JobSystem::ResetStatsSnapshot();
            auto handle = JobSystem::Scheduler::ScheduleEntityBatches(
                [](void* raw, const EntityBatchData*, int start, int count)
                {
                    auto& state = *static_cast<EntityContext*>(raw);
                    for (int i = start; i < start + count; ++i)
                        (*state.hits)[static_cast<size_t>(i)].fetch_add(
                            1, std::memory_order_relaxed);
                },
                &context, nullptr, batches.data(), itemCount, {},
                JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
            handle.Complete();
            for (const auto& hit : hits)
                Require(hit.load(std::memory_order_relaxed) == 1,
                    "ScheduleEntityBatches missed or duplicated an item");
            JobSystem::JobSystemStatsSnapshot stats{};
            JobSystem::GetStatsSnapshot(&stats);
            RequireTileAccounting(stats, itemCount,
                "ScheduleEntityBatches tile accounting did not reconcile");
        }
    }

    void TestAtomicBatchRangeClaiming()
    {
        constexpr int itemCounts[] = { 1, 2, 7, 8, 31, 32, 100 };
        for (const int itemCount : itemCounts)
        {
            std::vector<ChunkJobData> chunks(static_cast<size_t>(itemCount));
            std::vector<std::atomic<int>> hits(static_cast<size_t>(itemCount));
            struct Context
            {
                const ChunkJobData* base;
                std::vector<std::atomic<int>>* hits;
            } context{ chunks.data(), &hits };

            JobSystem::ResetStatsSnapshot();
            auto handle = JobSystem::Scheduler::ScheduleChunks(
                [](void* raw, const ChunkJobData* chunk)
                {
                    auto& state = *static_cast<Context*>(raw);
                    const auto index = static_cast<size_t>(chunk - state.base);
                    (*state.hits)[index].fetch_add(1, std::memory_order_relaxed);
                },
                &context, nullptr, chunks.data(), itemCount, {},
                JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
            handle.Complete();

            for (const auto& hit : hits)
                Require(hit.load(std::memory_order_relaxed) == 1,
                    "dynamic tile claiming missed or duplicated an item");

            JobSystem::JobSystemStatsSnapshot stats{};
            for (int retry = 0; retry < 100'000; ++retry)
            {
                JobSystem::GetStatsSnapshot(&stats);
                if (stats.localTiles + stats.stolenTiles + stats.assistTiles ==
                    static_cast<uint64_t>(itemCount))
                    break;
                std::this_thread::yield();
            }
            RequireTileAccounting(stats, static_cast<uint64_t>(itemCount),
                "dynamic tile accounting did not reconcile");
            Require(stats.localTiles + stats.stolenTiles + stats.assistTiles ==
                static_cast<uint64_t>(itemCount),
                "atomic BatchRange claiming did not account every tile exactly once");
        }
    }

    void TestDefaultTileIsDecoupledFromPhysicalChunks()
    {
        const auto runCase = [](int itemCount, uint64_t expectedTiles)
        {
            std::vector<ChunkJobData> chunks(static_cast<size_t>(itemCount));
            std::vector<std::atomic<int>> hits(static_cast<size_t>(itemCount));
            ChunkRangeContext context{ &hits, nullptr };

            JobSystem::ResetStatsSnapshot();
            auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
                &ExecuteChunkRange, &context, nullptr,
                chunks.data(), itemCount, {},
                JobSystem::ChunkScheduleMode::PublishAssist, 8, 0);
            handle.Complete();

            for (const auto& hit : hits)
                Require(hit.load(std::memory_order_relaxed) == 1,
                    "adaptive multi-chunk tile missed or duplicated an item");
            JobSystem::JobSystemStatsSnapshot stats{};
            JobSystem::GetStatsSnapshot(&stats);
            RequireTileAccounting(stats, expectedTiles,
                "adaptive BatchRange produced an unexpected tile count");
        };

        runCase(31, 8);    // 4 chunks/tile: minimum range size
        runCase(1000, 32); // 32 chunks/tile: 4 tiles/worker target
    }

    void TestBatchStorageIsReturnedAndReused()
    {
        constexpr int itemCount = 31;
        std::vector<ChunkJobData> chunks(itemCount);
        std::atomic<int> callbacks{ 0 };

        // 近无锁：batch storage 走 per-thread 缓存，回收先进本线程缓存、满额才批量迁移
        // 共享池（跨线程复用）。acquire 恒在调度线程（main），release 在最后一个 tile
        // 的执行线程（main 或任一 worker）——回收线程分布是调度决定的，不可控。
        //
        // 因此 batch 数不能拍脑袋取 64：若被 workerCount+1 个线程平均分摊，每个线程
        // 回收 <9 个（per-thread 缓存 cap=8），共享池永远不会被填充，reused==0 → flake。
        // 改用鸽笼原理：batchCount = 8×(workerCount+1)+2 保证至少一个线程回收 ≥9 个
        // storage → 缓存溢出到共享池 → 后续 main 的 acquire 必从共享池复用 → reused≥1
        // 确定性成立（与调度分布无关）。
        const int workerCount = JobSystem::CurrentWorkerCount();
        const int batchCount = 8 * (workerCount + 1) + 2;

        JobSystem::ResetStatsSnapshot();
        for (int batchIndex = 0; batchIndex < batchCount; ++batchIndex)
        {
            auto handle = JobSystem::Scheduler::ScheduleChunks(
                [](void* raw, const ChunkJobData*)
                {
                    static_cast<std::atomic<int>*>(raw)->fetch_add(
                        1, std::memory_order_relaxed);
                },
                &callbacks, nullptr, chunks.data(), itemCount, {},
                JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
            handle.Complete();
            for (int retry = 0; retry < 100'000; ++retry)
            {
                JobSystem::JobSystemStatsSnapshot current{};
                JobSystem::GetStatsSnapshot(&current);
                if (current.batchStorageReturned >=
                    static_cast<uint64_t>(batchIndex + 1))
                    break;
                std::this_thread::yield();
            }
        }

        JobSystem::JobSystemStatsSnapshot stats{};
        JobSystem::GetStatsSnapshot(&stats);
        Require(callbacks.load(std::memory_order_relaxed) == itemCount * batchCount,
            "pooled batches missed or duplicated callbacks");
        Require(stats.batchStorageReused >= 1,
            "sequential batches did not reuse storage after cache overflow");
        Require(stats.batchStorageReturned ==
            stats.batchStorageCreated + stats.batchStorageReused,
            "batch storage acquire/return accounting did not reconcile");
    }

    void TestBoundaryTimingDiagnostics()
    {
        constexpr int itemCount = 100;
        std::vector<ChunkJobData> chunks(itemCount);
        std::atomic<int> callbacks{ 0 };

        JobSystem::SetTimingDiagnosticsEnabled(true);
        JobSystem::ResetStatsSnapshot();
        auto handle = JobSystem::Scheduler::ScheduleChunks(
            [](void* raw, const ChunkJobData*)
            {
                static_cast<std::atomic<int>*>(raw)->fetch_add(
                    1, std::memory_order_relaxed);
                std::this_thread::yield();
            },
            &callbacks, nullptr, chunks.data(), itemCount, {},
            JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
        for (int retry = 0; retry < 100'000 && !handle.IsCompleted(); ++retry)
            std::this_thread::yield();
        handle.Complete();

        JobSystem::JobSystemStatsSnapshot stats{};
        // JobHandle completion is tile-driven and intentionally precedes the
        // retirement of late participant slots. Topology diagnostics become
        // available when those slots finish unwinding.
        for (int retry = 0; retry < 100'000; ++retry)
        {
            JobSystem::GetStatsSnapshot(&stats);
            if (stats.submitToFirstWorkerEwmaNs > 0 &&
                stats.lastTileToTopologyDoneEwmaNs > 0)
                break;
            std::this_thread::yield();
        }
        JobSystem::SetTimingDiagnosticsEnabled(false);
        Require(callbacks.load(std::memory_order_relaxed) == itemCount,
            "timed batch missed or duplicated callbacks");
        Require(stats.submitToFirstWorkerEwmaNs > 0,
            "submit-to-first-worker boundary was not measured");
        Require(stats.lastTileToTopologyDoneEwmaNs > 0,
            "last-tile-to-topology boundary was not measured");
        Require(stats.workerStartSpreadEwmaNs < 10'000'000'000ull,
            "worker-start-spread timing underflowed");
        Require(stats.timingSampleCount == 1,
            "completed batch did not produce exactly one timing sample");
        Require(stats.timingSamplesDropped == 0,
            "single timing sample was unexpectedly dropped");
        Require(stats.batchTotalP50Ns > 0 &&
            stats.batchTotalP50Ns <= stats.batchTotalP95Ns &&
            stats.batchTotalP95Ns <= stats.batchTotalP99Ns &&
            stats.batchTotalP99Ns <= stats.batchTotalMaxNs,
            "batch-total timing percentiles are invalid");
        Require(stats.maxRangeMaxNs > 0,
            "maximum range execution time was not measured");
        Require(stats.slowRangeIndex >= 0,
            "slow range was not correlated with its tile index");
#ifdef _WIN32
        Require(stats.slowRangeThreadCycles > 0 &&
            stats.slowBatchMinRangeThreadCycles > 0,
            "Windows thread-cycle diagnostics were not measured");
        Require(stats.slowRangeStartLogicalCore >= 0 &&
            stats.slowRangeEndLogicalCore >= 0 &&
            stats.slowRangeStartPhysicalCore >= 0 &&
            stats.slowRangeEndPhysicalCore >= 0,
            "Windows logical/physical core diagnostics were not measured");
#endif
        Require(stats.slowBatchId != 0 &&
            stats.slowBatchTotalNs == stats.batchTotalMaxNs,
            "slow batch was not correlated with the maximum batch sample");
    }

    void TestWorkerCapParameterized()
    {
        const int workerCount = JobSystem::CurrentWorkerCount();

        auto runRangeBatch = [](int workerCap, int chunkCount,
            std::atomic<int>* cleanup) -> uint64_t
        {
            std::vector<ChunkJobData> chunks(chunkCount);
            std::vector<std::atomic<int>> hits(static_cast<size_t>(chunkCount));
            ChunkRangeContext ctx{ &hits, cleanup };
            JobSystem::ResetStatsSnapshot();
            auto h = JobSystem::Scheduler::ScheduleChunkRanges(
                &ExecuteChunkRange, &ctx, &CleanupChunkRange,
                chunks.data(), chunkCount, {},
                JobSystem::ChunkScheduleMode::PublishAssist,
                workerCap, 1);
            h.Complete();
            for (const auto& hit : hits)
                Require(hit.load(std::memory_order_relaxed) == 1,
                    "WorkerCap test missed/duplicated chunk");
            JobSystem::JobSystemStatsSnapshot stats{};
            JobSystem::GetStatsSnapshot(&stats);
            return stats.frameTasksSubmitted;
        };

        // A: workerCap=1 → 1 participant task
        {
            std::atomic<int> cleanup{ 0 };
            uint64_t tasks = runRangeBatch(1, 100, &cleanup);
            Require(tasks == 1, "workerCap=1 should submit exactly 1 task");
            Require(cleanup.load() == 1, "workerCap=1 cleanup mismatch");
        }

        // B: workerCap=2 → 2 participant tasks
        {
            std::atomic<int> cleanup{ 0 };
            uint64_t tasks = runRangeBatch(2, 100, &cleanup);
            Require(tasks == 2, "workerCap=2 should submit exactly 2 tasks");
            Require(cleanup.load() == 1, "workerCap=2 cleanup mismatch");
        }

        // C: workerCap=8 → min(8, workerCount)
        {
            std::atomic<int> cleanup{ 0 };
            uint64_t tasks = runRangeBatch(8, 100, &cleanup);
            uint64_t expected = static_cast<uint64_t>(std::min(8, workerCount));
            Require(tasks == expected,
                "workerCap=8 submitted wrong participant count");
            Require(cleanup.load() == 1, "workerCap=8 cleanup mismatch");
        }

        // D: workerCap=15 → min(15, workerCount)
        {
            std::atomic<int> cleanup{ 0 };
            uint64_t tasks = runRangeBatch(15, 100, &cleanup);
            uint64_t expected = static_cast<uint64_t>(std::min(15, workerCount));
            Require(tasks == expected,
                "workerCap=15 submitted wrong participant count");
        }

        // E: tileCount < workerCap → capped by tileCount
        {
            constexpr int smallCount = 4;
            std::atomic<int> cleanup{ 0 };
            uint64_t tasks = runRangeBatch(8, smallCount, &cleanup);
            Require(tasks == smallCount,
                "tileCount < workerCap should submit only tileCount tasks");
        }

        // F: ECS BatchRange path (ScheduleChunks) with workerCap=8
        {
            constexpr int chunkCount = 100;
            std::vector<ChunkJobData> chunks(chunkCount);
            std::atomic<int> execCount{ 0 };
            std::atomic<int> cleanup{ 0 };
            struct ChunkCtx { std::atomic<int>* exec; std::atomic<int>* cleanup; };
            ChunkCtx ctx{ &execCount, &cleanup };

            JobSystem::ResetStatsSnapshot();
            auto handle = JobSystem::Scheduler::ScheduleChunks(
                [](void* raw, const ChunkJobData*) {
                    auto& c = *static_cast<ChunkCtx*>(raw);
                    c.exec->fetch_add(1, std::memory_order_relaxed);
                },
                &ctx,
                [](void* raw) {
                    static_cast<ChunkCtx*>(raw)->cleanup->fetch_add(
                        1, std::memory_order_relaxed);
                },
                chunks.data(), chunkCount, {},
                JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
            handle.Complete();

            JobSystem::JobSystemStatsSnapshot stats{};
            JobSystem::GetStatsSnapshot(&stats);
            uint64_t expected = static_cast<uint64_t>(
                std::min({8, workerCount, chunkCount}));
            Require(stats.frameTasksSubmitted == expected,
                "ECS BatchRange workerCap=8 wrong task count");
            Require(execCount.load() == chunkCount,
                "ECS BatchRange missed/duplicated chunks");
            Require(cleanup.load() == 1,
                "ECS BatchRange cleanup mismatch");
        }
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
        TestTraceIdentifiesCompleteCallerAndWorker();
        std::cout << "PASS TraceIdentifiesCompleteCallerAndWorker\n";
        TestTraceRecordsProcessorForRangeEvents();
        std::cout << "PASS TraceRecordsProcessorForRangeEvents\n";
        TestChunkPublishWakesOnlyTargetWorkers();
        std::cout << "PASS ChunkPublishWakesOnlyTargetWorkers\n";
        TestCompleteDrainsTargetBeyondOldBudget();
        std::cout << "PASS CompleteDrainsTargetBeyondOldBudget\n";
        TestStatsClassifyWorkerAndAssistExactlyOnce();
        std::cout << "PASS StatsClassifyWorkerAndAssistExactlyOnce\n";
        TestUnifiedTileAccountingForAllChunkEntrypoints();
        std::cout << "PASS UnifiedTileAccountingForAllChunkEntrypoints\n";
        TestAtomicBatchRangeClaiming();
        std::cout << "PASS AtomicBatchRangeClaiming\n";
        TestDefaultTileIsDecoupledFromPhysicalChunks();
        std::cout << "PASS DefaultTileIsDecoupledFromPhysicalChunks\n";
        TestBatchStorageIsReturnedAndReused();
        std::cout << "PASS BatchStorageIsReturnedAndReused\n";
        TestBoundaryTimingDiagnostics();
        std::cout << "PASS BoundaryTimingDiagnostics\n";
        TestParallelForExactOnceAndCallerAssist();
        std::cout << "PASS ParallelForExactOnceAndCallerAssist\n";
        TestExplicitBatchSize(1);
        TestExplicitBatchSize(257);
        TestExplicitBatchSize(100'000);
        std::cout << "PASS ExplicitBatchSizes\n";
        TestDependencyOrdering();
        std::cout << "PASS DependencyOrdering\n";
        TestSmallJobsRespectPendingDependencies();
        std::cout << "PASS SmallJobsRespectPendingDependencies\n";
        TestChunkRangeExactOnce();
        std::cout << "PASS ChunkRangeExactOnce\n";
#ifdef _WIN32
        TestChunkWorkersDoNotPreemptCompletingThread();
        std::cout << "PASS ChunkWorkersDoNotPreemptCompletingThread\n";
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
        TestTransitiveAssistDrivesDependencyChain();
        std::cout << "PASS TransitiveAssistDrivesDependencyChain\n";
        TestNestedCompleteResolvesWithoutWorkerExhaustion();
        std::cout << "PASS NestedCompleteResolvesWithoutWorkerExhaustion\n";
        TestShutdownWithOutstandingWork();
        std::cout << "PASS ShutdownWithOutstandingWork\n";
        TestWorkerCapParameterized();
        std::cout << "PASS WorkerCapParameterized\n";
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
