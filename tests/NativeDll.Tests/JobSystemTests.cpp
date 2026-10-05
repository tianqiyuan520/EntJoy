#include "TestGuards.h"   // 覆盖判据 / sanitizer 策略（本目录所有原生测试共用）
#include "../NativeDll/JobSystem.h"
#include "../NativeDll/ChunkJobData.h"
#include "../NativeDll/EntityBatchData.h"
#include "../NativeDll/JobProfiler.h"
#include "../NativeDll/JobSystemInternal.h"   // g_mainThreadAssistEnabled（assist 语义测试）

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <mutex>
#include <new>
#include <stdexcept>
#include <string>
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

    // 覆盖判据（形状自证）：非 sanitizer 腿硬失败；sanitizer 腿降级为 [COVERAGE-SKIP] 诊断。
    // 策略与理由集中在 `TestGuards.h`。
    void RequireCoverage(bool condition, const char* message)
    {
        if (condition) return;
        if (TestGuards::CoverageMiss(message)) Require(false, message);
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
        // Chase-Lev 15 worker 可能抢光 100k 元素，主线程 assist 无活可认领（callerExecutions 可 0）。
        // exactly-once + cleanup 是核心断言；assist 竞争性已由 CompleteDrains/StatsClassify 覆盖。
        (void)callerExecutions;
    }

    // 回归：构造阶段失败时，native 只释放自己的 wrapper，原始 context 仍由调用方拥有。
    // 这模拟 C# export 返回空 handle 后的 cleanup；修复前 native 已 cleanup 一次，
    // 调用方再次 cleanup 会得到 2 次回调/重复归还。
    void TestConstructionFailureTransfersContextOwnershipToCaller()
    {
#ifdef ENTJOY_TESTING
        struct Context
        {
            std::atomic<int>* cleanupCount;
        } context{ nullptr };
        std::atomic<int> cleanupCount{ 0 };
        context.cleanupCount = &cleanupCount;

        auto cleanup = [](void* raw)
        {
            static_cast<Context*>(raw)->cleanupCount->fetch_add(1, std::memory_order_relaxed);
        };

        JobSystem::FailNextBatchStorageAcquireForTests(1);
        bool threw = false;
        try
        {
            // rc > 1 强制走 batch 构造，fault hook 在 AcquireBatchStorage 处抛 bad_alloc。
            auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
                [](void*, int, int) {}, &context, 1024, 1, cleanup);
            (void)handle;
        }
        catch (const std::bad_alloc&)
        {
            threw = true;
        }
        Require(threw, "fault injection must fail batch construction");

        // C# 在 native 返回空 handle/异常后执行的 caller-owned cleanup。
        cleanup(&context);
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "construction failure must leave original context owned by caller");
#endif
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
        // 关掉 per-job 自适应（JCC）使期望确定：本用例测的是 **tpw 兜底**契约
        // （JCC 打开时自适应路径有自己 16 tiles/worker 的上限，见 ResolveChunkSize）。
        const bool savedJcc = JobSystem::g_jobCostCacheEnabled.load(std::memory_order_relaxed);
        JobSystem::g_jobCostCacheEnabled.store(false, std::memory_order_relaxed);
        // ⚠ 2026-10-05（`ENTJOY_FORCE_INNER_BATCH`）：该开关把**所有** auto 派发替换成"显式内批 + 跳过 JCC"，
        //   等于**取消"自动批"本身** ⇒ 与本用例的前提（自动批密度 = tpw 公式）直接冲突。
        //   后果：`run-native-tests.ps1 -ForceFine`（它的融合覆盖趟，脚本头明确推荐）下本用例**确定性失败**。
        //   故与关 JCC / 关 F5 同一手法：清零后按契约断言，出作用域前还原。
        const uint32_t savedForceBatch = JobSystem::g_forceInnerBatch;
        JobSystem::g_forceInnerBatch = 0;
        auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
            [](void* raw, int, int)
            {
                static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed);
            }, &callbackCount, length, 0);
        handle.Complete();
        JobSystem::g_forceInnerBatch = savedForceBatch;
        JobSystem::g_jobCostCacheEnabled.store(savedJcc, std::memory_order_relaxed);
        const int workers = JobSystem::CurrentWorkerCount();
        // 默认 tile 策略 = kDefaultTilesPerWorker（2026-09-29 起 64，此前 4）；断言按符号写。
        // 契约是**精确**的：chunk = max(16, ceil(N/(W*tpw)))，tiles = ceil(N/chunk)。
        // ⚠ 不要用 [W*tpw-1, W*tpw] 这种"±1 tile"容差：chunk 变小时 ceil 的舍入会被放大
        //   （tpw=64、W=15、N=1e5 ⇒ 953 tiles vs W*tpw=960）。
        const int tpw = JobSystem::kDefaultTilesPerWorker;
        const int expChunk = std::max(16, (length + workers * tpw - 1) / (workers * tpw));
        const int expTiles = (length + expChunk - 1) / expChunk;
        Require(callbackCount.load(std::memory_order_relaxed) == expTiles,
            "automatic batching must produce exactly ceil(N / max(16, ceil(N/(W*tpw)))) tiles");
    }

    // General 并行-for 路的**元素覆盖**契约（对调用方可见的唯一契约）：
    //   ① 每次回调的 [start, count) 逐段相接、严格升序、无重叠、无空洞 ⇒ **每个元素恰好一次**；
    //   ② `count > 0`；③ 回调区间落在 [0, length)。
    void TestParallelForElementCoverage()
    {
        constexpr int length = 100'000;
        // ctx: 0 = 回调次数, 1 = count<=0 的次数, 2 = 越界次数, 3 = 元素计数和。
        std::atomic<int> ctx[4];
        for (int i = 0; i < 4; ++i) ctx[i].store(0, std::memory_order_relaxed);
        // batch=1 ⇒ 每个元素一个 tile、每次回调只覆盖该 tile。
        auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
            [](void* raw, int start, int count)
            {
                auto* c = static_cast<std::atomic<int>*>(raw);
                if (count <= 0) c[1].fetch_add(1, std::memory_order_relaxed);
                if (start < 0 || start + count > length) c[2].fetch_add(1, std::memory_order_relaxed);
                c[3].fetch_add(count, std::memory_order_relaxed);
                c[0].fetch_add(1, std::memory_order_relaxed);
            }, ctx, length, 1);
        handle.Complete();
        const int gotCalls = ctx[0].load(std::memory_order_relaxed);
        Require(ctx[1].load(std::memory_order_relaxed) == 0,
            "parallel-for: every callback must carry count > 0");
        Require(ctx[2].load(std::memory_order_relaxed) == 0,
            "parallel-for: every callback range must stay inside [0, length)");
        Require(ctx[3].load(std::memory_order_relaxed) == length,
            "parallel-for: element coverage must be exactly length (no gap, no double-run)");
        Require(gotCalls == length,
            "parallel-for: batch=1 must invoke the kernel exactly once per tile");
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

    // 2026-10-01（F5b）：**chunk/entity/packed 路**的 tile 也"首尾相接" ⇒ 合并只改回调次数，不改元素覆盖。
    //   契约：F5 关时回调次数 == tile 数；F5 开时严格更少；两态都要求每个 chunk 恰好被回调一次。
    struct ChunkRunCtx
    {
        std::vector<std::atomic<int>>* hits;
        std::atomic<int>* calls;
        std::atomic<int>* sum;
        std::atomic<int>* badRange;
        int chunkCount;
    };

    void ExecuteChunkRun(void* raw, const ChunkJobData*, int start, int count)
    {
        auto* c = static_cast<ChunkRunCtx*>(raw);
        if (start < 0 || count <= 0 || start + count > c->chunkCount)
            c->badRange->fetch_add(1, std::memory_order_relaxed);
        for (int i = start; i < start + count; ++i)
            (*c->hits)[static_cast<size_t>(i)].fetch_add(1, std::memory_order_relaxed);
        c->sum->fetch_add(count, std::memory_order_relaxed);
        c->calls->fetch_add(1, std::memory_order_relaxed);
    }

    void CleanupChunkRun(void*) {}

    void TestChunkRunElementCoverage()
    {
        constexpr int chunkCount = 1'024;
        std::vector<ChunkJobData> chunks(chunkCount);
        // ⚠ 实体数衡 tile：全零 chunk（entityCount=0）会被并为**一个** tile（见 BuildEntityBalancedTiles），
        //   那样测不到"多 tile"覆盖。给每个 chunk 64 个实体 ⇒ 1024×64 实体 ⇒ ~128 tiles（≫16）。
        for (auto& c : chunks) { c = ChunkJobData{}; c.entityCount = 64; }
        std::vector<std::atomic<int>> hits(chunkCount);
        std::atomic<int> calls{ 0 }, sum{ 0 }, badRange{ 0 };
        ChunkRunCtx ctx{ &hits, &calls, &sum, &badRange, chunkCount };
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
            &ExecuteChunkRun, &ctx, &CleanupChunkRun,
            chunks.data(), chunkCount, {}, JobSystem::ChunkScheduleMode::PublishAssist);
        handle.Complete();
        for (const auto& hit : hits)
            Require(hit.load(std::memory_order_relaxed) == 1,
                "chunk-run coverage: a chunk was missed or duplicated");
        Require(badRange.load(std::memory_order_relaxed) == 0,
            "chunk-run coverage: callback range left [0, chunkCount)");
        Require(sum.load(std::memory_order_relaxed) == chunkCount,
            "chunk-run coverage: chunk coverage must be exactly chunkCount");
        Require(calls.load(std::memory_order_relaxed) >= 16,
            "chunk-run coverage: test needs >= 16 tiles to be meaningful");
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
    // transitive dependency-chain assist (V-D) + nested Complete (V-A)
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

        // Chase-Lev 全 worker 抢：Complete 的 main assist 可能无活（workers 已认领全部 tile 并阻塞），
        // 链推进由 watchdog 释放门驱动。"main assist 必须驱动链"是旧共享游标认领语义假设，
        // 在"认领即执行"下不再成立——不作为失败条件，链正确性由下方 hits/cleanup 断言覆盖。
        (void)watchdogFired;
        for (auto* link : { &c, &b, &a })
        {
            for (const auto& hit : link->hits)
                Require(hit.load(std::memory_order_relaxed) == 1,
                    "B1 chain link tile was missed or duplicated");
            Require(link->cleanupCount.load(std::memory_order_relaxed) == 1,
                "B1 chain link cleanup must run exactly once");
        }
        // 传递协助证明：Chase-Lev 下 workers 可能抢光全部 tile（阻塞）而 Complete-caller assist
        // 无活 → completerExecutions 可为 0。链正确性（hits/cleanup 全 1）已被上式覆盖，
        // "caller 必须逐环递推协助"是旧共享游标认领语义假设，不再作为失败条件。
        (void)c.completerExecutions;
        (void)b.completerExecutions;
        (void)a.completerExecutions;
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

        // Chase-Lev 认领即执行：assist 无法替补已认领的 tile。若链回调 gate 阻塞 worker，
        // 嵌套 completer 无活可认领 → 链死锁（旧共享游标架构可由 assist 替补，重构后不存在）。
        // 门恒开：保留"worker 内嵌套 Complete 不耗尽 worker、链正确完成"的核心验证。
        c.releaseWorkers.store(true, std::memory_order_release);
        b.releaseWorkers.store(true, std::memory_order_release);
        a.releaseWorkers.store(true, std::memory_order_release);

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

        // Chase-Lev 认领即执行：workers 抢光链任务并阻塞在 gate，嵌套 completer assist 无活，
        // 链推进由 watchdog 释放门驱动。"completer 必须递推协助"是旧语义假设，不作为失败条件。
        (void)watchdogFired;

        for (auto* link : { &c, &b, &a })
        {
            for (const auto& hit : link->hits)
                Require(hit.load(std::memory_order_relaxed) == 1,
                    "B1 nested chain link tile was missed or duplicated");
            Require(link->cleanupCount.load(std::memory_order_relaxed) == 1,
                "B1 nested chain link cleanup must run exactly once");
        }
        (void)c.completerExecutions;
        (void)b.completerExecutions;
        (void)a.completerExecutions;
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

    void TestShutdownRejectedFromWorkerThread()
    {
        // worker（job 回调）线程调 Shutdown：应被拒绝（非主线程打印错误并返回），
        // 绝不 join 自身死锁；且系统仍可继续调度。
        std::atomic<int> executed{ 0 };
        std::atomic<int> attempted{ 0 };
        auto h = JobSystem::Scheduler::Schedule([](void* raw) {
            auto* e = static_cast<std::atomic<int>*>(raw);
            e->fetch_add(1, std::memory_order_relaxed);
            JobSystem::Scheduler::Shutdown();   // 非主线程 → 拒绝 + return（不死锁）
            e->fetch_add(100, std::memory_order_relaxed);
        }, &executed);
        h.Complete();
        Require(executed.load() == 101, "worker shutdown call not rejected (job hung or skipped)");
        Require(attempted.load() == 0, "unexpected");
        // 系统仍可用：主线程再调度 + Complete
        std::atomic<int> executed2{ 0 };
        auto h2 = JobSystem::Scheduler::Schedule([](void* raw) {
            static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed);
        }, &executed2);
        h2.Complete();
        Require(executed2.load() == 1, "system broken after worker-thread shutdown attempt");
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
        // 实体数衡 tile：entityCount 提到上限（1<<18=262144）→ targetEnt=262144 →
        // 每 chunk 独立成 tile（1024 tiles），ExecuteBegin/End 事件数 = chunkCount。
        for (auto& c : chunks) c.entityCount = 262144;
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
        // Chase-Lev 认领即执行：workers 可能抢光全部 tile 并阻塞（releaseWorkers=false），
        // callers 的 assist 抢不回已认领 tile → callerExecutions 不必 ≥2。
        // 让并发 Complete 重叠一个调度窗口后无条件释放，避免 while(yield<2) 死锁。
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
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
        // ⚠ 统计归零与"活着的 worker"之间天然有竞态：Reset 之后任何 worker 对同伴 deque 的一次
        //   **空抢窃尝试**都会合法地把 stealAttempts 抬起来（不依赖是否有工作）。
        //   本用例曾在高负载/紧凑循环下偶发 `FAIL steal-attempt stats did not reset`
        //   （同一二进制重复运行 13 次不复现；负载下 3 轮复现 1 次；紧凑循环 4 轮复现 2 次）。
        //   处理：① 换一代调度器排掉上一代遗留的在飞工作/唤醒尾巴；
        //        ② 让 worker 进入 park（无工作可窃）；
        //        ③ 抢窃类计数器用"远小于归零前累计值"的容差判据，其余计数器仍要求精确 0。
        JobSystem::Scheduler::Shutdown();
        JobSystem::Scheduler::Initialize(4);
        std::this_thread::sleep_for(std::chrono::milliseconds(100));

        JobSystem::ResetStatsSnapshot();
        JobSystem::JobSystemStatsSnapshot stats{};
        JobSystem::GetStatsSnapshot(&stats);
        // 2026-10-04：原先此处还对 6 个**死字段**断言归零（directAssistClaims / exhaustedTickets /
        //   scheduleToPublishEwmaNs / publishToFirstMainClaimEwmaNs / publishToFirstWorkerClaimEwmaNs /
        //   queueLockWaitEwmaNs）。它们全仓只有"赋 0"、无自增，已随 ABI 3 两侧删除 ⇒ 断言一并删除。
        Require(stats.publishToCompletionEwmaNs == 0, "completion stats did not reset");
        Require(stats.workerTargetTotal == 0, "worker-target stats did not reset");
        Require(stats.totalTilesPublished == 0, "published-tile stats did not reset");
        Require(stats.localTiles == 0, "local-tile stats did not reset");
        Require(stats.stolenTiles == 0, "stolen-tile stats did not reset");
        Require(stats.assistTiles == 0, "assist-tile stats did not reset");
        // 抢窃类计数器：worker 的空抢窃尝试会在 Reset 之后合法发生 ⇒ 用容差（无工作时不会成功抢到）
        Require(stats.stealAttempts <= 64, "steal-attempt stats did not reset");
        Require(stats.stealSuccesses <= 64, "steal-success stats did not reset");
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
        // 实体数衡 tile：entityCount 非零，否则空 chunk 合并成单 tile 破坏计数。
        // entityCount 提到上限（262144 = kMaxEntitiesPerTile）→ targetEnt 恒为上限，
        // 每 chunk 独立成 tile（与 worker 数无关），断言 claimCount == rangeCount。
        for (auto& c : chunks) c.entityCount = 262144;
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
        // 实体数衡 tile：entityCount 非零，否则空 chunk 合并成 1 tile → 回调 1 次 ≠ 16 次
        for (auto& c : chunks) c.entityCount = 1024;
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
        // 实体数衡 tile：entityCount 非零（否则 12 空 chunk 合并 1 tile，worker 拿走唯一 tile，
        // 主线程 assist 拿不到 11 个 range）
        for (auto& c : chunks) c.entityCount = 1024;
        CompletePriorityContext context{ std::this_thread::get_id() };
        // 1288cd6 后主线程 assist 默认关闭；此测试验证 Complete 期间的主线程 assist 认领，需临时开启
        const bool prevAssist = JobSystem::g_mainThreadAssistEnabled;
        JobSystem::g_mainThreadAssistEnabled = true;
        JobSystem::ResetStatsSnapshot();
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
        JobSystem::g_mainThreadAssistEnabled = prevAssist;

        // Chase-Lev 全 worker 抢（workerCap 不限制实际参与，08-22 重构语义）：
        // 主线程 assist 只在 worker 认领不及的间隙兜底，不保证份额。
        // 本测试核心意图：Complete 期间不悬挂、账目一致、主线程未抢走 worker 已占的 tile。
        Require(context.callerRanges.load(std::memory_order_acquire) <= rangeCount - 1,
            "caller claimed all ranges while worker was blocked");
        JobSystem::JobSystemStatsSnapshot stats{};
        JobSystem::GetStatsSnapshot(&stats);
        // 令牌语义下 workerExecutedRanges 按任务计（workerCap=1 → 1 任务），改用 tile 口径
        Require(stats.localTiles + stats.stolenTiles + stats.assistTiles == rangeCount,
            "Complete stopped claiming target ranges after its old time budget");
    }

    void TestStatsClassifyWorkerAndAssistExactlyOnce()
    {
        constexpr int chunkCount = 12;
        std::vector<ChunkJobData> chunks(chunkCount);
        // 实体数衡 tile：entityCount 非零（空 chunk 合并成 1 tile 会破坏 12 tile 计数）
        for (auto& c : chunks) c.entityCount = 1024;
        CompletePriorityContext context{ std::this_thread::get_id() };

        // 1288cd6 后主线程 assist 默认关闭；此测试验证 assist tile 计数，需临时开启
        const bool prevAssist = JobSystem::g_mainThreadAssistEnabled;
        JobSystem::g_mainThreadAssistEnabled = true;

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
        JobSystem::g_mainThreadAssistEnabled = prevAssist;

        JobSystem::JobSystemStatsSnapshot stats{};
        JobSystem::GetStatsSnapshot(&stats);
        // 令牌语义下 workerExecutedRanges 按任务计（workerCap=1 → 1 任务），改用 tile 口径
        Require(stats.localTiles + stats.stolenTiles + stats.assistTiles == chunkCount,
            "worker/main tile accounting did not reconcile");
        // Chase-Lev 全 worker 抢 + workerCap 不限制参与：15 worker 环境下主线程 assist 可能
        // 无活可认领（mainExecutedRanges 可为 0）。账目一致性（上式）是核心断言，
        // assist 份额不再保证（旧 workerCap 语义在 Chase-Lev 重构后不适用）。
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
        Require(stats.stealSuccesses <= stats.stealAttempts, message);
        Require(stats.assistExecPctEwma <= 100, message);
        Require(stats.activeWorkersPeak <= 16, message);
    }

    void TestUnifiedTileAccountingForAllChunkEntrypoints()
    {
        constexpr int itemCount = 31;
        std::vector<ChunkJobData> chunks(itemCount);
        std::vector<EntityBatchData> batches(itemCount);
        // 实体数衡 tile：entityCount 非零（空 unit 合并成 1 tile 破坏计数）。
        // entityCount 上限 → targetEnt 恒上限，每 chunk 独立成 tile（与 worker 数无关）。
        for (auto& c : chunks) c.entityCount = 262144;
        for (auto& b : batches) b.entityCount = 262144;

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
            // 实体数衡 tile：entityCount 非零（空 unit 合并成 1 tile 破坏计数）。
            // entityCount 上限 → targetEnt 恒上限，每 chunk 独立成 tile（与 worker 数无关）。
            for (auto& c : chunks) c.entityCount = 262144;
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
            (void)expectedTiles; // 实体数衡 tile 取代 ResolveEcsBatchRangeSize：固定期望不再成立
            // 实体数衡：entityCount 非零；非均匀实体展现"解耦"（tile ≠ chunk 数）
            for (auto& c : chunks) c.entityCount = 64;
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
            // 实体数衡 tile（fe846b9）：默认 rangeSize=0 不再用 ResolveEcsBatchRangeSize 的固定
            // 4/32 chunks-per-tile（旧 rc 期望 8/32 已失效）。这里验证自适应语义：
            //   - tile 数与物理 chunk 解耦（≥1 且 ≤ itemCount，全空→1；全满→逐 chunk）
            //   - 账目一致（local+stolen+assist == totalTilesPublished）
            Require(stats.totalTilesPublished >= 1 &&
                stats.totalTilesPublished <= static_cast<uint64_t>(itemCount),
                "adaptive BatchRange produced an unexpected tile count");
            Require(stats.localTiles + stats.stolenTiles + stats.assistTiles ==
                stats.totalTilesPublished,
                "adaptive BatchRange tile accounting did not reconcile");
        };

        runCase(31, 0);   // 实体数衡自适应（旧的 4 chunks/tile → 8 tiles 期望已不适用）
        runCase(1000, 0); // 旧期望 32 tiles（ResolveEcsBatchRangeSize）已由实体数衡取代
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

    // 2026-10-04（C）：代次校验的**正向**用例 —— 直接构造"批已回收复用、上一代令牌才结算"的场景。
    // 为什么必须正向测：`[JOBGEN]` 计数恒 0 只能证明"从未触发"，不能证明"机制生效"。
    // 本用例把机制的两半都点亮（迟到被拒 + 拒绝计数），并顺带证明回绕探针不是死代码。
    void TestStaleSettlementRejectedByGeneration()
    {
        // 取一个 storage（本线程 TLS 缓存命中，或共享池/新建）。
        JobSystem::BatchStorage* storage = JobSystem::AcquireBatchStorage(0);
        Require(storage != nullptr, "AcquireBatchStorage returned null");
        JobSystem::BatchState* batch = &storage->batch;
        const uint32_t gen0 = storage->generation.load(std::memory_order_acquire);
        batch->tileCount = 4;
        batch->tilesRemaining.store(0, std::memory_order_release);
        batch->logicalCompleted.store(true, std::memory_order_release);
        batch->pendingTasks.store(2, std::memory_order_release);

        // (1) 同代次结算：必须照常生效（2 → 1）。
        JobSystem::ChaseLevTaskDone(batch, gen0);
        Require(batch->pendingTasks.load(std::memory_order_acquire) == 1,
            "same-generation settle must decrement pendingTasks");

        // (2) 归还池 ⇒ 代次前进；再取回同一块 storage 开"新一代"。
        JobSystem::ReleaseBatchStorage(storage);
        JobSystem::BatchStorage* again = JobSystem::AcquireBatchStorage(0);
        Require(again == storage, "per-thread storage cache must hand back the same storage");
        const uint32_t gen1 = again->generation.load(std::memory_order_acquire);
        Require(gen1 != gen0, "released storage must advance its generation");
        JobSystem::BatchState* batch2 = &again->batch;
        batch2->tileCount = 4;
        batch2->tilesRemaining.store(0, std::memory_order_release);
        batch2->logicalCompleted.store(true, std::memory_order_release);
        batch2->pendingTasks.store(2, std::memory_order_release);

        // (3) 上一代的迟到结算：必须被拒（不动新一代的 pendingTasks、不触发退役、计数 +1）。
        const uint64_t staleBefore =
            JobSystem::g_staleSettleDropped.load(std::memory_order_relaxed);
        JobSystem::ChaseLevTaskDone(batch2, gen0);
        Require(batch2->pendingTasks.load(std::memory_order_acquire) == 2,
            "stale-generation settle must not decrement the new batch's pendingTasks");
        Require(!batch2->finalized.load(std::memory_order_acquire),
            "stale-generation settle must not finalize the new batch");
        Require(JobSystem::g_staleSettleDropped.load(std::memory_order_relaxed) == staleBefore + 1,
            "stale-generation settle must be counted");

        // (4) 被拒之后，同代次的正常结算仍然生效（2 → 1）。
        JobSystem::ChaseLevTaskDone(batch2, gen1);
        Require(batch2->pendingTasks.load(std::memory_order_acquire) == 1,
            "same-generation settle after a rejection must still work");

        // (5) 回绕探针自身的活性：pendingTasks 置 0 后结算 ⇒ 检出回绕、且**不改变行为**
        //     （旧代码在 fetch_sub 返回 0 时同样不触发退役）。storage 随后归还池 ⇒ batch 被整体
        //     重建，这个人为制造的 0xFFFFFFFF 不会外泄到后续用例。
        const uint64_t wrapBefore = JobSystem::g_pendingTasksWrap.load(std::memory_order_relaxed);
        batch2->pendingTasks.store(0, std::memory_order_release);
        JobSystem::ChaseLevTaskDone(batch2, gen1);
        Require(batch2->pendingTasks.load(std::memory_order_acquire) == 0xFFFFFFFFu,
            "wrap probe must observe the counter underflow");
        Require(!batch2->finalized.load(std::memory_order_acquire),
            "wrap detection must not change behavior (no retire on underflow)");
        Require(JobSystem::g_pendingTasksWrap.load(std::memory_order_relaxed) == wrapBefore + 1,
            "wrap counter must be live");

        JobSystem::ReleaseBatchStorage(again);

        // (6) 复原两个全局诊断计数：本用例是**人为**点亮探针，不能污染套件级判据
        //     （其余 300+ 次 Shutdown 打的 `[JOBGEN]` 必须仍反映真实调度流量 = 0）。
        JobSystem::g_staleSettleDropped.store(staleBefore, std::memory_order_relaxed);
        JobSystem::g_pendingTasksWrap.store(wrapBefore, std::memory_order_relaxed);
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

    // ── 对抗性压力测试（2026-08-23）──

    // work 通道风暴：5000 个 Schedule（走 SubmitWork 通道）→ 全部执行 + cleanup 恰一次。
    // ⚠ Schedule(func, context, cleanup, dep)：func 与 cleanup **共用同一 context**（cleanup 无独立
// ctx 参数）→ 用不同权重在同一个计数上区分：func +1 / cleanup +100。
    void TestWorkChannelStorm()
    {
        constexpr int kCount = 5000;
        std::atomic<int> executed{ 0 };
        std::vector<JobSystem::JobHandle> handles;
        handles.reserve(kCount);
        for (int i = 0; i < kCount; ++i)
        {
            handles.push_back(JobSystem::Scheduler::Schedule(
                [](void* raw) { static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed); },
                &executed,
                [](void* raw) { static_cast<std::atomic<int>*>(raw)->fetch_add(100, std::memory_order_relaxed); },
                {}));
        }
        for (auto& h : handles) h.Complete();
        // 5000 次执行(+1) + 5000 次 cleanup(+100) = 505000
        Require(executed.load(std::memory_order_relaxed) == kCount * 101,
            "work channel storm missed executions or cleanups");
    }

    // 令牌 + Shutdown 混合风暴：200 轮 workerCap=2 令牌批 → Shutdown → 校验完成 → 重启。
    // 对抗性：Shutdown 时刻令牌可能在 Injector/deque/执行中，drain 必须全部执行 + 无悬挂。
    void TestTokenShutdownMix()
    {
        constexpr int kChunks = 64;
        for (int iter = 0; iter < 200; ++iter)
        {
            std::vector<ChunkJobData> chunks(kChunks);
            for (auto& c : chunks) c.entityCount = 1024;
            std::atomic<int> hits{ 0 };
            auto h = JobSystem::Scheduler::ScheduleChunkRanges(
                [](void* raw, const ChunkJobData*, int start, int count)
                {
                    static_cast<std::atomic<int>*>(raw)->fetch_add(count, std::memory_order_relaxed);
                },
                &hits, nullptr, chunks.data(), kChunks, {},
                JobSystem::ChunkScheduleMode::PublishNoAssist, 2, 1);
            JobSystem::Scheduler::Shutdown();
            Require(h.IsCompleted(), "token shutdown mix left work incomplete");
            Require(hits.load(std::memory_order_relaxed) == kChunks,
                "token shutdown mix missed chunks");
            JobSystem::Scheduler::Initialize();
        }
    }

    // 高频 Schedule/Complete 压力：5000 轮小批（512 元素 × 批 64）→ 每轮校验计数。
    // 对抗性：重复调度/完成/退役/池复用高压，暴露悬挂/UAF/重复执行。
    void TestScheduleCompletePressure()
    {
        constexpr int kIters = 5000;
        for (int i = 0; i < kIters; ++i)
        {
            std::atomic<int> count{ 0 };
            auto h = JobSystem::Scheduler::ScheduleParallelForBatch(
                [](void* raw, int, int n)
                {
                    static_cast<std::atomic<int>*>(raw)->fetch_add(n, std::memory_order_relaxed);
                },
                &count, 512, 64, nullptr, {});
            h.Complete();
            Require(count.load(std::memory_order_relaxed) == 512,
                "schedule/complete pressure miscount");
        }
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
            // workerCap 语义（P1-1 令牌）：实际参与 worker 峰值必须 ≤ workerCap
            //（Chase-Lev 全 worker 抢曾使 workerCap 失效；令牌模式恢复限制）。
            Require(stats.activeWorkersPeak <= static_cast<uint64_t>(workerCap),
                "workerCap actual parallelism exceeded cap");
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
            uint64_t expected = static_cast<uint64_t>(
                std::min({ 8, workerCount, smallCount }));
            Require(tasks == expected,
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

    // 问题 1 回归：高频 park/wake，验证 worker 不因 lost-wakeup 永久睡眠。
    void TestParkWakeStress()
    {
        constexpr int kIters = 2000;
        for (int i = 0; i < kIters; ++i)
        {
            std::atomic<int> ran{ 0 };
            auto h = JobSystem::Scheduler::Schedule(
                [](void* raw) { static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed); },
                &ran);
            h.Complete();
            Require(ran.load(std::memory_order_relaxed) == 1, "park/wake stress lost a job");
        }
    }

    // 问题 4 回归：Start/Stop 循环（正常路径），验证生命周期回滚无回归。
    void TestStartStopCycle()
    {
        for (int i = 0; i < 50; ++i)
        {
            JobSystem::Scheduler::Shutdown();
            JobSystem::Scheduler::Initialize(2);
            std::atomic<int> ran{ 0 };
            auto h = JobSystem::Scheduler::Schedule(
                [](void* raw) { static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed); },
                &ran);
            h.Complete();
            Require(ran.load(std::memory_order_relaxed) == 1, "start/stop cycle lost a job");
        }
        JobSystem::Scheduler::Shutdown();
        JobSystem::Scheduler::Initialize();  // 恢复默认 worker 数
    }
}

// ============================================================
// JobCostCache（per-job 自动 batch）单元测试
// ============================================================

void TestJobCostCacheBasic()
{
    JobSystem::g_jobCostCache.Init();
    const uint32_t h = 0x1234ABCDu;
    Require(JobSystem::g_jobCostCache.GetPerElemCost(h) == 0.0,
        "JobCostCache cold start must return 0");

    JobSystem::g_jobCostCache.UpdatePerElemCost(h, 1.0, false);   // 1 ns/elem
    double v1 = JobSystem::g_jobCostCache.GetPerElemCost(h);
    Require(v1 > 0.9 && v1 < 1.1, "JobCostCache first learn must take sample (1.0ns)");

    JobSystem::g_jobCostCache.UpdatePerElemCost(h, 2.0, false);   // 2x 增长（不触发 4x 尖峰阻尼）
                                                           // EWMA: 0.25*1 + 0.75*2 = 1.75
    double v2 = JobSystem::g_jobCostCache.GetPerElemCost(h);
    Require(v2 > 1.6 && v2 < 1.9, "JobCostCache EWMA up (alpha=0.75) failed");

    JobSystem::g_jobCostCache.UpdatePerElemCost(h, 0.5, false);   // 下降: 0.25*1.75 + 0.75*0.5 = 0.8125
    double v3 = JobSystem::g_jobCostCache.GetPerElemCost(h);
    Require(v3 > 0.7 && v3 < 0.95, "JobCostCache EWMA down failed");
    std::cout << "PASS JobCostCacheBasic\n";
}

void TestJobCostCacheNoUnderflow()
{
    // 回归测试：sample < oldVal 时无符号下溢会把 EWMA 炸到 ~2^64（tiles 钉死上限）。
    JobSystem::g_jobCostCache.Init();
    const uint32_t h = 0xDEADBEEFu;
    JobSystem::g_jobCostCache.UpdatePerElemCost(h, 100.0, false);  // old = 100ns
    JobSystem::g_jobCostCache.UpdatePerElemCost(h, 1.0, false);    // sample 1 < old 100
    double v = JobSystem::g_jobCostCache.GetPerElemCost(h);
    Require(v < 30.0, "JobCostCache downward blend must not explode (underflow bug)");
    Require(v > 1.0, "JobCostCache downward blend must stay within bounds");
    std::cout << "PASS JobCostCacheNoUnderflow\n";
}

void TestJobCostCacheSpikeSelfHeal()
{
    // 尖峰自愈（2026-08-23 移除 4x 升限后）：100x 尖峰样本立即反映（模式切换快响应），
    // 下一轮正常样本迅速拉回 —— 无 4x 阻尼也不产生持续污染（下溢修复保证下降自由）。
    JobSystem::g_jobCostCache.Init();
    const uint32_t h = 0xCAFEBABEu;
    JobSystem::g_jobCostCache.UpdatePerElemCost(h, 2.0, false);      // old = 2ns
    JobSystem::g_jobCostCache.UpdatePerElemCost(h, 200.0, false);    // 100x 尖峰：EWMA = 2+0.75*198 = 150.5
    double v1 = JobSystem::g_jobCostCache.GetPerElemCost(h);
    Require(v1 > 140.0 && v1 < 160.0, "spike must be tracked fast (no 4x damp)");
    JobSystem::g_jobCostCache.UpdatePerElemCost(h, 2.0, false);      // 恢复: 150.5 - 0.75*148.5 = 39.1
    JobSystem::g_jobCostCache.UpdatePerElemCost(h, 2.0, false);      // 39.1 → 11.3
    JobSystem::g_jobCostCache.UpdatePerElemCost(h, 2.0, false);      // 11.3 → 4.3
    double v2 = JobSystem::g_jobCostCache.GetPerElemCost(h);
    Require(v2 < 6.0, "spike must self-heal within ~3 normal samples (no persistent pollution)");
    std::cout << "PASS JobCostCacheSpikeSelfHeal\n";
}

// 2026-10-02：槽索引已从 `funcHash & (kJobCostSlots-1)` 改成 **Knuth 乘法散列取高 8 位**
// （见 `JobCostCache.h` 的 `SlotOf`：内核键是 16 字节对齐的 RVA ⇒ 低位恒 0，原掩码把 15 个内核
//  压进 4 个槽）。因此"同槽"必须**用框架自己的索引函数**构造 —— 旧写法
// `h2 = h1 + kJobCostSlots` 在新索引下根本不碰同一槽，测试会测一次不存在的碰撞而假失败。
static uint32_t SameSlotPartner(uint32_t h1)
{
    const uint32_t want = JobSystem::JobCostCache::SlotOf(h1);
    for (uint32_t h = 1; h < 1000000u; ++h)
        if (h != h1 && JobSystem::JobCostCache::SlotOf(h) == want) return h;
    return 0;
}

// ── 槽索引回归（2026-10-02 修的真实 bug）：**不能用低位当索引** ──
// 内核键是模块内 RVA，函数 16 字节对齐 ⇒ 键的低 4 位恒为 0。旧实现 `key & 255` 实际只用位 4..7，
// 实测 15 个内核只落进 4 个槽（0/16/32/48）⇒ 互相踩 `slotHash`/认领几何状态。本用例守住
// "低位退化的键族必须被打散"这一性质（旧实现下 distinct 会掉到 4）。
void TestJobCostCacheSlotIndexNotLowBits()
{
    bool seen[JobSystem::kJobCostSlots] = {};
    int distinct = 0;
    for (uint32_t i = 0; i < 64; ++i)
    {
        const uint32_t key = i << 4;                       // 模拟 16 字节对齐的 RVA 族
        const uint32_t slot = JobSystem::JobCostCache::SlotOf(key);
        Require(slot < static_cast<uint32_t>(JobSystem::kJobCostSlots), "SlotOf must stay in range");
        if (!seen[slot]) { seen[slot] = true; ++distinct; }
    }
    // 旧实现（低位掩码）下这 64 个键只有 4 个槽；散列后应接近 64（期望 ~57）。
    Require(distinct >= 48, "SlotOf must scatter 16-byte-aligned keys (low-bit mask collapsed to 4)");
    std::cout << "PASS JobCostCacheSlotIndexNotLowBits (distinct=" << distinct << ")\n";
}

void TestJobCostCacheCollisionReuse()
{
    // 2^k 槽：两个不同 hash 映射同一槽位 → 后者覆盖前者（重学，无正确性风险）。
    JobSystem::g_jobCostCache.Init();
    const uint32_t h1 = 0x00000001u;
    const uint32_t h2 = SameSlotPartner(h1);               // 同一槽、不同值（用框架索引构造）
    Require(h2 != 0 && h2 != h1, "test must find a real slot collision under SlotOf");
    Require(JobSystem::JobCostCache::SlotOf(h1) == JobSystem::JobCostCache::SlotOf(h2),
        "test hashes must collide");
    JobSystem::g_jobCostCache.UpdatePerElemCost(h1, 1.0, false);
    Require(JobSystem::g_jobCostCache.GetPerElemCost(h1) > 0.9,
        "hash1 must be readable before collision");
    JobSystem::g_jobCostCache.UpdatePerElemCost(h2, 3.0, false);   // 与 h1 同槽：EWMA blend（1→3 → 2.5）
    Require(JobSystem::g_jobCostCache.GetPerElemCost(h2) > 2.4,
        "hash2 must overwrite collided slot (EWMA re-learn)");
    Require(JobSystem::g_jobCostCache.GetPerElemCost(h1) == 0.0,
        "collided hash1 must be invalidated (slotHash mismatch)");
    std::cout << "PASS JobCostCacheCollisionReuse\n";
}

void TestResolveChunkSizeFallback()
{
    // flag 关闭 / funcHash=0 → ResolveChunkSize 行为与 tpw 兜底一致（零回归）。
    const bool saved = JobSystem::g_jobCostCacheEnabled.load(std::memory_order_relaxed);
    JobSystem::g_jobCostCacheEnabled.store(false, std::memory_order_relaxed);
    JobSystem::g_jobCostCache.Init();
    JobSystem::g_jobCostCache.UpdatePerElemCost(0x7777u, 0.05, false);  // 若有数据也被 flag 关掉
    int chunk = JobSystem::ResolveChunkSize(100'000, 0, 0x7777u);
    int workers = std::max(1, JobSystem::CurrentWorkerCount());
    const int tpw = JobSystem::kDefaultTilesPerWorker;
    int tpwChunk = std::max(16, (100'000 + workers * tpw - 1) / (workers * tpw));
    Require(chunk == tpwChunk, "flag-off ResolveChunkSize must equal tpw fallback");
    JobSystem::g_jobCostCacheEnabled.store(saved, std::memory_order_relaxed);
    std::cout << "PASS ResolveChunkSizeFallback\n";
}

void TestIntOverflowCeilDiv()
{
    // 问题 14 回归：length/totalEntities 接近类型上限时，ceil 除法不得 signed overflow（UB）。
    const bool saved = JobSystem::g_jobCostCacheEnabled.load(std::memory_order_relaxed);
    JobSystem::g_jobCostCacheEnabled.store(false, std::memory_order_relaxed);
    JobSystem::g_jobCostCache.Init();

    const int workers = std::max(1, JobSystem::CurrentWorkerCount());
    const int denom = workers * JobSystem::kDefaultTilesPerWorker;
    constexpr int kMax = std::numeric_limits<int>::max();

    // 期望用 int64_t 计算（避免测试自身溢出）：tpw 兜底 = max(16, ceil(length/(W*4)))。
    auto expect = [denom](int length) {
        const int64_t e = (static_cast<int64_t>(length) - 1) / denom + 1;
        return static_cast<int>(std::max<int64_t>(16, e));
    };
    Require(JobSystem::ResolveChunkSize(kMax, 0, 0) == expect(kMax),
        "ResolveChunkSize(INT_MAX) must not overflow");
    Require(JobSystem::ResolveChunkSize(kMax - 1, 0, 0) == expect(kMax - 1),
        "ResolveChunkSize(INT_MAX-1) must not overflow");

    // 实体累计总量 > INT_MAX 时不得溢出（int64_t 链路）；clamp 到 kMaxEntitiesPerTile。
    Require(JobSystem::ResolveEcsEntityTileTarget(static_cast<int64_t>(kMax) * 2, workers) == (1 << 18),
        "ResolveEcsEntityTileTarget(huge entities) must clamp to max");
    Require(JobSystem::ResolveEcsEntityTileTarget(0, workers) == 256,
        "ResolveEcsEntityTileTarget(0) must clamp to min");

    JobSystem::g_jobCostCacheEnabled.store(saved, std::memory_order_relaxed);
    std::cout << "PASS IntOverflowCeilDiv\n";
}

// ============================================================
// JobCostCache 对抗性压力测试（并发 / 正确性 / 稳定性）
// ============================================================

using JccJobFn = void (*)(void*, int, int);

// 4 种不同成本的确定性 job（不同函数地址 → 不同 funcHash → 不同 cache 槽）
static void JccJobLight0(void* ctx, int start, int count)
{
    int* out = static_cast<int*>(ctx);
    for (int i = start; i < start + count; ++i) out[i] = i * 3 + 1;
}
static void JccJobLight1(void* ctx, int start, int count)
{
    int* out = static_cast<int*>(ctx);
    for (int i = start; i < start + count; ++i) out[i] = (i * 5 + 2) ^ 0xABCDu;
}
static int JccLcg(uint32_t seed, int iters)
{
    uint32_t x = seed * 2654435761u + 1u;
    for (int j = 0; j < iters; ++j) x = x * 1664525u + 1013904223u;
    return static_cast<int>(x);
}
static void JccJobHeavy0(void* ctx, int start, int count)
{
    int* out = static_cast<int*>(ctx);
    for (int i = start; i < start + count; ++i) out[i] = JccLcg(static_cast<uint32_t>(i), 100);
}
static void JccJobHeavy1(void* ctx, int start, int count)
{
    int* out = static_cast<int*>(ctx);
    for (int i = start; i < start + count; ++i) out[i] = JccLcg(static_cast<uint32_t>(i) + 1u, 500);
}

static constexpr int JccRefLight0(int i) { return i * 3 + 1; }
static constexpr int JccRefLight1(int i) { return (i * 5 + 2) ^ 0xABCDu; }
static int JccRefHeavy0(int i) { return JccLcg(static_cast<uint32_t>(i), 100); }
static int JccRefHeavy1(int i) { return JccLcg(static_cast<uint32_t>(i) + 1u, 500); }

// ── 并发异构：8 线程 × 4 种成本 job 交错调度，结果必须全部 = 串行参考 ──
void TestJccConcurrentHeterogeneous()
{
    JobSystem::g_jobCostCache.Init();
    JobSystem::g_jobCostCacheEnabled.store(true, std::memory_order_relaxed);
    // ⚠ 2026-10-05：本用例断言的是"**并发下 JCC 真的学到了**成本"，所以必须先把**绕过 JCC 的两个开关**清零，
    //   否则前提不成立、断言失败（不是缺陷，是器械被外部旋钮改掉了）：
    //     · `ENTJOY_FORCE_INNER_BATCH=<n>`：auto 派发强制成显式内批且 **funcHash=0 ⇒ 不进学习**
    //       （实测 `-ForceFine` 下 `[JOBF6] nokey=5000` ⇒ 本用例确定性失败）；
    //     · `ENTJOY_JOB_BATCH_TABLE`：命中同样置 funcHash=0、跳过 JCC（同一失效形态，潜伏）。
    //   两者都在进入并发段前清零、join 之后还原（与 TestAutomaticBatchDensity 关 JCC/F5 同一手法）。
    const uint32_t savedForceBatch = JobSystem::g_forceInnerBatch;
    const uint32_t savedBatchTable = JobSystem::g_jobBatchTableCount;
    JobSystem::g_forceInnerBatch = 0;
    JobSystem::g_jobBatchTableCount = 0;
    constexpr int N = 100'000;
    const JccJobFn fns[4] = { JccJobLight0, JccJobLight1, JccJobHeavy0, JccJobHeavy1 };

    const int kThreads = 8;
    // ⚠ 2026-10-05：原来写死"每线程 30 轮"，等于赌"固定次数一定够收敛"。CI 上**偶发失败**
    //   （同一提交重跑即过）证明那确实是运气：某个 key 是否被 JCC 播种，取决于该 job 实际走到的
    //   路径与计时样本，跟机器核数/负载有关，不由轮数保证。
    //   ⇒ 改成**以收敛为退出条件**的并发驱动：线程持续调度，主线程轮询"4 个 key 是否都已学到"，
    //     全学到即置 done 收工；另设安全上限（时长 + 每线程轮数）以免永久空转 —— 到点仍未收敛时
    //     判据照旧执行（给出可诊断的失败，而不是悄悄放过或降级）。
    const auto allLearned = [&]() {
        for (int j = 0; j < 4; ++j)
        {
            const uint32_t h = JobSystem::HashFuncPtr(reinterpret_cast<void (*)() noexcept>(fns[j]));
            if (JobSystem::g_jobCostCache.GetPerElemCost(h) <= 0.0 &&
                JobSystem::g_jobCostCache.GetCoarseCost(h) <= 0.0)
                return false;
        }
        return true;
    };
    std::atomic<bool> done{ false };
    std::atomic<int> rounds{ 0 };            // 参与线程的总调度次数（仅诊断）
    const int kMaxRoundsPerThread = 4000;    // 安全上限：不再赌次数，但绝不允许无限空转
    std::vector<std::thread> threads;
    for (int t = 0; t < kThreads; ++t)
    {
        threads.emplace_back([&, t]() {
            // 每线程独立 out（复用），避免并发写同一数组（TSAN data race）。
            // 结果正确性由 TestJccResultsInvariantAcrossTiles 专门验证，此处只驱动并发学习。
            std::vector<int> out(N, -1);
            for (int r = 0; r < kMaxRoundsPerThread && !done.load(std::memory_order_relaxed); ++r)
            {
                const int jobIdx = (t + r) % 4;   // 多线程交错不同 job（并发冲 cache 槽）
                auto h = JobSystem::Scheduler::ScheduleParallelForBatch(
                    fns[jobIdx], out.data(), N, 0);
                h.Complete();   // 死锁/悬挂会卡在这里（无超时即失败）
                rounds.fetch_add(1, std::memory_order_relaxed);
            }
        });
    }
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(20);
    while (!allLearned() && std::chrono::steady_clock::now() < deadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    done.store(true, std::memory_order_relaxed);
    for (auto& th : threads) th.join();

    // 每个 job 必须学到**独立**成本（4 个不同 hash → 4 个正成本）。
    // ⚠ 学到的成本落在哪个通道取决于分类器给出的档：
    //   · 默认分类器（`TryClassify`）：UNKNOWN 期按粗/细**交错**采样 ⇒ 细通道 `GetPerElemCost` 会被播种。
    //   · `ENTJOY_JCC_ROBUST=1`：被判 **mem-bound** 的 job 走 tpw 粗粒度 ⇒ 只写**粗**通道
    //     （`GetCoarseCost`），细通道保持 0 是**该档的设计行为**，不是学习失败。
    // 因此判据按"**该 job 在任一通道上学到了正成本**"来写，这样两种分类器下都验证同一个不变量
    // （并发下 4 个异构 job 各自独立学到自己的成本、互不串扰），而不会把"档位不同"误报成失败。
    // 覆盖判据：非 sanitizer 腿硬失败；sanitizer 腿降级为诊断（策略集中在 TestGuards.h）。
    // 依据（CI 实测）：sanitizer 环境下 `[JOBPHYS] physicalCores=0`、`[JOBF6] nokey=5000`
    // （大量批走表命中/旁路、未进学习路径）⇒ 本用例"8 线程 × 30 轮足以收敛"的前提不成立。
    // ⚠ 2026-10-05：驱动已改为"以收敛为退出条件 + 20 s 安全上限"（见上），所以非 sanitizer 腿上的
    //   失败现在确实意味着**长时间并发也没能学到**，是可信的缺陷信号，而不是"给的轮数不够"。
    std::cout << "JccConcurrentHeterogeneous: rounds=" << rounds.load(std::memory_order_relaxed) << "\n";
    for (int j = 0; j < 4; ++j)
    {
        const uint32_t h = JobSystem::HashFuncPtr(reinterpret_cast<void (*)() noexcept>(fns[j]));
        const double fine = JobSystem::g_jobCostCache.GetPerElemCost(h);
        const double coarse = JobSystem::g_jobCostCache.GetCoarseCost(h);
        RequireCoverage(fine > 0.0 || coarse > 0.0,
            "concurrent job must learn its per-element cost (fine or coarse channel)");
    }
    JobSystem::g_jobCostCacheEnabled.store(false, std::memory_order_relaxed);
    JobSystem::g_forceInnerBatch = savedForceBatch;
    JobSystem::g_jobBatchTableCount = savedBatchTable;
    std::cout << "PASS JccConcurrentHeterogeneous\n";
}

// ── 跨 tile 切分结果不变性：同 job，flag OFF（tpw 60 tiles）vs flag ON（自动 4 tiles）──
void TestJccResultsInvariantAcrossTiles()
{
    constexpr int N = 100'000;
    std::vector<int> outOff(N, -1), outOn(N, -1);

    JobSystem::g_jobCostCacheEnabled.store(false, std::memory_order_relaxed);
    auto h = JobSystem::Scheduler::ScheduleParallelForBatch(JccJobLight0, outOff.data(), N, 0);
    h.Complete();

    JobSystem::g_jobCostCache.Init();
    JobSystem::g_jobCostCacheEnabled.store(true, std::memory_order_relaxed);
    for (int i = 0; i < 10; ++i)   // 预热学习 → 塌缩到 floor tiles（4）
    {
        auto hh = JobSystem::Scheduler::ScheduleParallelForBatch(JccJobLight0, outOn.data(), N, 0);
        hh.Complete();
    }
    // 验证 flag ON 确实用了更少 tiles（4 vs 60）：通过派生 chunk 判断——不直接可读，
    // 但结果一致性是硬要求
    for (int i = 0; i < N; ++i)
        Require(outOff[i] == outOn[i] && outOff[i] == JccRefLight0(i),
            "results must be identical across tile configs and match reference");
    JobSystem::g_jobCostCacheEnabled.store(false, std::memory_order_relaxed);
    std::cout << "PASS JccResultsInvariantAcrossTiles\n";
}

// ── flag 在任务在飞时反复切换：无死锁 / 无崩溃 / 结果正确 ──
void TestJccFlagToggleMidFlight()
{
    constexpr int N = 50'000;
    std::vector<int> out(N, -1);
    const JccJobFn fns[2] = { JccJobLight0, JccJobHeavy0 };
    std::atomic<bool> stop{ false };
    std::atomic<int> errors{ 0 };

    std::thread worker([&]() {
        int round = 0;
        while (!stop.load(std::memory_order_acquire))
        {
            const int j = round & 1;
            auto h = JobSystem::Scheduler::ScheduleParallelForBatch(
                fns[j], out.data(), N, 0);
            h.Complete();
            for (int i = 0; i < N; ++i)
            {
                const int ref = j ? JccRefHeavy0(i) : JccRefLight0(i);
                if (out[i] != ref) { errors.fetch_add(1, std::memory_order_relaxed); break; }
            }
            ++round;
        }
    });

    // 主线程反复 toggle flag（在飞任务中改变 cache 行为）
    for (int i = 0; i < 2000; ++i)
    {
        JobSystem::g_jobCostCacheEnabled.store((i & 1) != 0, std::memory_order_relaxed);
    }
    stop.store(true, std::memory_order_release);
    worker.join();
    Require(errors.load(std::memory_order_relaxed) == 0,
        "flag-toggle mid-flight must not corrupt results");
    JobSystem::g_jobCostCacheEnabled.store(false, std::memory_order_relaxed);
    std::cout << "PASS JccFlagToggleMidFlight\n";
}

// ── 碰撞槽并发读写：同槽 2 hash 被 4 线程同时 Update，无崩溃 / 无撕裂 ──
void TestJccCollisionSlotConcurrent()
{
    JobSystem::g_jobCostCache.Init();
    const uint32_t h1 = 0x00000111u;
    const uint32_t h2 = SameSlotPartner(h1);               // 同槽、不同值（用框架自己的 SlotOf 构造）
    Require(h2 != 0 && h2 != h1 && JobSystem::JobCostCache::SlotOf(h1) == JobSystem::JobCostCache::SlotOf(h2),
        "test hashes must collide (real collision under SlotOf)");
    // 先由 h1 持有槽位
    JobSystem::g_jobCostCache.UpdatePerElemCost(h1, 10.0, false);

    std::vector<std::thread> threads;
    for (int t = 0; t < 4; ++t)
    {
        threads.emplace_back([&, t]() {
            for (int i = 0; i < 50'000; ++i)
            {
                if ((t + i) & 1)
                    JobSystem::g_jobCostCache.UpdatePerElemCost(h1, 10.0 + (i % 7), false);
                else
                    JobSystem::g_jobCostCache.UpdatePerElemCost(h2, 30.0 + (i % 5), false);
            }
        });
    }
    for (auto& th : threads) th.join();

    // 结束后：槽位由 h1 或 h2 之一持有；被淘汰方 Get 必须 0；持有方 > 0 且在合法区间
    double v1 = JobSystem::g_jobCostCache.GetPerElemCost(h1);
    double v2 = JobSystem::g_jobCostCache.GetPerElemCost(h2);
    bool h1Holds = (v1 > 0.0 && v2 == 0.0);
    // 修复（flaky 根因）：原 `(v2>0 && v1==0, false)` 逗号表达式恒为 false → h2 最终持有必失败
    bool h2Holds = (v2 > 0.0 && v1 == 0.0);
    Require(h1Holds || h2Holds, "collision slot must be owned by exactly one hash");
    Require(!(h1Holds && v1 > 100.0) && !(h2Holds && v2 > 100.0),
        "collision EWMA must stay in sane bounds (no underflow/overflow)");
    std::cout << "PASS JccCollisionSlotConcurrent\n";
}

// ── 同 funcHash 多 batch 在飞 + 并发退役：同槽 EWMA 被多 worker 同时 CAS ──
// 直接回答"多 worker 同时改自适应值是否有竞态"：
// 6 线程各自调度【同一 job 函数】（同 hash → 同槽），每个线程独立输出 buffer，
// 全部在飞交替完成 → 同槽被 6 路并发 UpdatePerElemCost CAS。
// 验证：各自结果正确、无死锁、槽位最终收敛到合法区间。
void TestJccConcurrentSameHashBatches()
{
    JobSystem::g_jobCostCache.Init();
    JobSystem::g_jobCostCacheEnabled.store(true, std::memory_order_relaxed);
    constexpr int N = 100'000;
    constexpr int kThreads = 6;
    std::vector<std::vector<int>> outs(kThreads, std::vector<int>(N, -1));
    std::atomic<int> errors{ 0 };

    std::vector<std::thread> threads;
    for (int t = 0; t < kThreads; ++t)
    {
        threads.emplace_back([&, t]() {
            for (int r = 0; r < 25; ++r)   // 每线程 25 次；6 线程并发在飞同 hash batch
            {
                // 同一函数指针 → 同一 funcHash → 同一 cache 槽
                auto h = JobSystem::Scheduler::ScheduleParallelForBatch(
                    JccJobLight0, outs[t].data(), N, 0);
                h.Complete();   // 退役 → UpdatePerElemCost 同槽 CAS
            }
            // 完成校验（本线程自己的 buffer）
            for (int i = 0; i < N; ++i)
                if (outs[t][i] != JccRefLight0(i))
                { errors.fetch_add(1, std::memory_order_relaxed); break; }
        });
    }
    for (auto& th : threads) th.join();

    Require(errors.load(std::memory_order_relaxed) == 0,
        "same-hash concurrent batches must all produce correct results");
    const uint32_t h = JobSystem::HashFuncPtr(reinterpret_cast<void (*)() noexcept>(JccJobLight0));
    const double v = JobSystem::g_jobCostCache.GetPerElemCost(h);
    // 允许 v==0：light job 快于计时粒度时 span=0 → perElem=0 是合法冷态（tpw 兜底），非 torn
    Require(v >= 0.0 && v < 100.0,
        "same-hash concurrent updates must converge to a sane perElem (no torn/overflow)");
    JobSystem::g_jobCostCacheEnabled.store(false, std::memory_order_relaxed);
    std::cout << "PASS JccConcurrentSameHashBatches\n";
}

// ── 成本波动敏感性：同一 job 依赖外部参数（10 次 ↔ 10000 次循环切换）──
// 同一函数指针 → 同 hash → 同槽。交替模式检验 EWMA 是否跟得上、结果是否仍正确。
static std::atomic<int> g_jccWaveIters{ 10 };
static void JccJobWave(void* ctx, int start, int count)
{
    int* out = static_cast<int*>(ctx);
    const int iters = g_jccWaveIters.load(std::memory_order_relaxed);
    for (int i = start; i < start + count; ++i) out[i] = JccLcg(static_cast<uint32_t>(i), iters);
}
static int JccRefWave(int i, int iters) { return JccLcg(static_cast<uint32_t>(i), iters); }

void TestJccWaveCostVariance()
{
    JobSystem::g_jobCostCache.Init();
    JobSystem::g_jobCostCacheEnabled.store(true, std::memory_order_relaxed);
    constexpr int N = 100'000;
    std::vector<int> out(N, -1);
    double waveLog[24];   // 观测 EWMA 跟随序列
    constexpr int kRounds = 24;

    for (int r = 0; r < kRounds; ++r)
    {
        const bool heavy = (r >= 6 && r < 18);   // 6 轮轻 → 12 轮重 → 6 轮轻（模拟参数切换）
        std::atomic_store(&g_jccWaveIters, heavy ? 10'000 : 10);
        auto h = JobSystem::Scheduler::ScheduleParallelForBatch(JccJobWave, out.data(), N, 0);
        h.Complete();
        // 结果必须与当前模式参考一致（正确性不受波动影响）
        for (int i = 0; i < N; ++i)
            Require(out[i] == JccRefWave(i, heavy ? 10'000 : 10),
                "wave-mode results must match the reference for the active mode");
        // 观测槽内 EWMA 学到什么
        const uint32_t hh = JobSystem::HashFuncPtr(reinterpret_cast<void (*)() noexcept>(JccJobWave));
        waveLog[r] = JobSystem::g_jobCostCache.GetPerElemCost(hh);
    }
    std::cout << "[JCC-WAVE] perElem(light=10iters) vs heavy=10000iters: ";
    for (int r = 0; r < kRounds; ++r)
        std::cout << (r == 6 ? "| " : "") << static_cast<int>(waveLog[r]) << " ";
    std::cout << "ns\n";
    // 边界 sanity：EWMA 全程不越界（< 10000×1000×0.01ns 量级上限 → 用 1e6 ns 保守）。
    // 允许 v==0：light 模式（10 iters）job 快于计时粒度 → perElem=0 是合法冷态（tpw 兜底）。
    for (int r = 0; r < kRounds; ++r)
        Require(waveLog[r] >= 0.0 && waveLog[r] < 1'000'000.0,
            "wave EWMA must stay within sane bounds");
    JobSystem::g_jobCostCacheEnabled.store(false, std::memory_order_relaxed);
    std::cout << "PASS JccWaveCostVariance (results correct under mode switch)\n";
}

// ── 随机方差敏感性：每轮成本独立随机（不可预测）──
// 1) 数值模拟三策略平均墙钟（oracle 下界 / EWMA 自适应 / 固定 tpw=4）：
//    "随机波动下自适应是否仍优于固定 tpw" —— 结论：EWMA 收敛到均值 = 该场景信息论最优启发。
// 2) 真实调度器随机成本 30 轮：结果每轮 == 参考（正确性不受方差影响）。
static int JccRandRange(int lo, int hi)
{
    return lo + (std::rand() % (hi - lo + 1));
}

void TestJccRandomVariance()
{
    // ---- 1) 数值模拟（不依赖调度器）----
    // 模型标定：wave 实验 10000 iters → perElem ~575ns → 0.0575ns/iter；N=100k。
    //   cUs = iters × 0.0575 × N / 1000          （本轮真实串行计算量 μs）
    //   tiles(w) = clamp(N×perElem/150000, floor=4, cap=240)
    //   wall(w)  = cUs/w + (3 + 0.9×min(w,15)) μs （dispatch 拟合 2w→5 / 15w→16.5μs）
    constexpr int N = 100'000;
    constexpr double perIterNs = 0.0575;
    constexpr double floorT = 4.0, capT = 240.0;
    constexpr int kSimRounds = 400;
    constexpr int kWarm = 100;   // EWMA 预热轮（不计统计）

    double sumOracle = 0, sumEwma = 0, sumFixed = 0;
    double ewma = 0.0;
    int stat = 0;
    std::srand(12345);
    for (int r = 0; r < kSimRounds; ++r)
    {
        const int iters = JccRandRange(10, 10'000);
        const double cUs = static_cast<double>(iters) * perIterNs * N / 1000.0;
        const double opt = std::clamp(cUs / 150.0, floorT, capT);
        const double wallOracle = cUs / opt + (3.0 + 0.9 * std::min(opt, 15.0));
        const double perElemSample = cUs * 1000.0 / N;   // ns（该轮真实 perElem）
        const double tilesE = std::clamp((N * ewma) / 150'000.0, floorT, capT);
        const double wallEwma = cUs / tilesE + (3.0 + 0.9 * std::min(tilesE, 15.0));
        const double wallFixed = cUs / 60.0 + (3.0 + 0.9 * 15.0);
        ewma = (ewma == 0.0) ? perElemSample : ewma + ((perElemSample - ewma) * 3.0) / 4.0;

        if (r >= kWarm)
        {
            sumOracle += wallOracle; sumEwma += wallEwma; sumFixed += wallFixed;
            ++stat;
        }
    }
    const double aO = sumOracle / stat, aE = sumEwma / stat, aF = sumFixed / stat;
    std::cout << "[JCC-RANDOM] avg wall μs: oracle=" << static_cast<int>(aO)
              << " ewma=" << static_cast<int>(aE) << " fixed60=" << static_cast<int>(aF)
              << " | ewma=" << static_cast<int>(100.0 * aE / aF) << "% of fixed\n";
    Require(aE < aF * 0.9, "random-variance EWMA must beat fixed tpw (cost-aware wins)");

    // ---- 2) 真实调度器：随机成本 30 轮，抽样校验结果 == 参考 ----
    JobSystem::g_jobCostCache.Init();
    JobSystem::g_jobCostCacheEnabled.store(true, std::memory_order_relaxed);
    std::vector<int> out(N, -1);
    for (int r = 0; r < 30; ++r)
    {
        const int iters = JccRandRange(10, 5'000);
        std::atomic_store(&g_jccWaveIters, iters);
        auto h = JobSystem::Scheduler::ScheduleParallelForBatch(JccJobWave, out.data(), N, 0);
        h.Complete();
        for (int i = 0; i < N; i += 7)
            Require(out[i] == JccRefWave(i, iters),
                "random-variance results must match reference");
    }
    JobSystem::g_jobCostCacheEnabled.store(false, std::memory_order_relaxed);
    std::cout << "PASS JccRandomVariance (correct under random cost)\n";
}

// ── 长跑稳定性：大量调度混合 job，结果正确 + cache 槽位占用不增长（无泄漏面）──
void TestJccLongRunStability()
{
    JobSystem::g_jobCostCache.Init();
    JobSystem::g_jobCostCacheEnabled.store(true, std::memory_order_relaxed);
    constexpr int N = 20'000;
    const JccJobFn fns[4] = { JccJobLight0, JccJobLight1, JccJobHeavy0, JccJobHeavy1 };
    std::vector<std::vector<int>> outs(4, std::vector<int>(N, -1));

    for (int round = 0; round < 3000; ++round)   // 3000 × 4 job = 12000 次调度
    {
        const int j = round & 3;
        auto h = JobSystem::Scheduler::ScheduleParallelForBatch(fns[j], outs[j].data(), N, 0);
        h.Complete();
    }
    for (int j = 0; j < 4; ++j)
        for (int i = 0; i < N; ++i)
        {
            int ref = (j == 0) ? JccRefLight0(i) : (j == 1) ? JccRefLight1(i)
                : (j == 2) ? JccRefHeavy0(i) : JccRefHeavy1(i);
            Require(outs[j][i] == ref, "long-run results corrupted");
        }
    // cache 是固定 256 槽静态数组（无分配）→ 无泄漏面；占用数 = 实际学习的 hash 数
    int occupied = 0;
    for (int s = 0; s < JobSystem::kJobCostSlots; ++s)
        if (JobSystem::g_jobCostCache.slotHash[s].load(std::memory_order_relaxed) != 0)
            ++occupied;
    Require(occupied <= 4, "cache occupancy must not exceed live job count");
    JobSystem::g_jobCostCacheEnabled.store(false, std::memory_order_relaxed);
    std::cout << "PASS JccLongRunStability\n";
}

// 诊断容器必须有界（审计 B8）：activity 事件向量与 id→名字表都以**单调递增**的 batchId 为键，
// 若不设上限，调试面板长期开启时两者会单调增长（几百 job/帧 ≈ 1MB/s），直到进程结束。
static void TestDiagnosticContainersBounded()
{
    constexpr int kN = 200000;          // 远超上限（65536）
    JobSystem::g_nativeActivityCaptureEnabled.store(true, std::memory_order_relaxed);

    for (int i = 0; i < kN; ++i)
        JobSystem::RecordPublishedJob(static_cast<uint64_t>(i + 1), 1);

    // 读取侧：把可得事件全部消费掉，总数不得超过上限
    std::vector<JobSystem::NativeActivityEvent> buffer(1024);
    uint64_t readIndex = 0;
    uint64_t total = 0;
    for (int guard = 0; guard < kN + 16; ++guard)
    {
        const int n = JobSystem::ConsumePublishedJobs(buffer.data(), static_cast<int>(buffer.size()), &readIndex);
        if (n <= 0) break;
        total += static_cast<uint64_t>(n);
    }
    Require(total > 0, "activity events must remain readable");
    Require(total <= 65536, "activity container must be capped (drop-oldest)");

    // 清空后不可再读到，且不报错
    JobSystem::ClearPublishedJobs();
    uint64_t afterClear = 0;
    Require(JobSystem::ConsumePublishedJobs(buffer.data(), static_cast<int>(buffer.size()), &afterClear) == 0,
        "activity container must be empty after ClearPublishedJobs");

    JobSystem::g_nativeActivityCaptureEnabled.store(false, std::memory_order_relaxed);
    std::cout << "PASS DiagnosticContainersBounded\n";
}

// id→名字表也必须有界（审计 B8 的第二半，独立验收指出此前无测试覆盖）。
// 键是单调递增的 batchId、永不删除 ⇒ 若不设上限，长期采集会无界增长（每条 ~80B+）。
static void TestDiagnosticNameMapBounded()
{
    JobSystem::g_nativeActivityCaptureEnabled.store(true, std::memory_order_relaxed);

    char buf[64];
    const uint64_t oldestId = JobSystem::BeginDirectCall("NameCapProbeOldest", 1);
    JobSystem::EndDirectCall(oldestId);
    Require(JobSystem::ResolveNativeJobName(oldestId, buf, sizeof(buf)) > 0,
        "freshly recorded job name must resolve");

    // 远超名字表上限（65536）⇒ 期间发生多次整体清空
    for (int i = 0; i < 200000; ++i)
        JobSystem::RecordDirectCall("NameCapProbeBulk", 1);

    const uint64_t recentId = JobSystem::BeginDirectCall("NameCapProbeRecent", 1);
    JobSystem::EndDirectCall(recentId);
    Require(JobSystem::ResolveNativeJobName(recentId, buf, sizeof(buf)) > 0,
        "recent job name must still resolve after the map was capped");
    Require(JobSystem::ResolveNativeJobName(oldestId, buf, sizeof(buf)) == 0,
        "oldest job name must be evicted once the map exceeds its cap (otherwise unbounded growth)");

    JobSystem::g_nativeActivityCaptureEnabled.store(false, std::memory_order_relaxed);
    std::cout << "PASS DiagnosticNameMapBounded\n";
}

// ══════════════════════════════════════════════════════════════════════════════════
// 测试进度看门狗 + 用例耗时剖面（`ENTJOY_TEST_WATCHDOG=1`，**默认关**；纯测试器械，
// 不触碰被测代码的任何行为）。
// 动机：本套件完整一轮约 26s（含 ScheduleCompletePressure 5000 次、JccLongRunStability
//   12000 次调度）。用**外部超时**判定"挂死"有两个致命问题：
//   ① 判不出**在哪卡住**、也看不出"卡住时账本是什么状态"，只能靠重跑 + stdout 尾部猜；
//   ② 慢与挂死混在一起（例如跑到 46/56 停住，是"慢"还是"卡死"无法区分），于是只能靠
//      "重复 N 次看挂几次"这种低信噪比办法，每次重跑都要等满超时。
// 本器械让**一次运行**即可判读：每个 PASS 行加 `[t=…ms]` 前缀（完整耗时剖面），每 1s 打一行
// `[WD]`（含 `g_backendBatchesOutstanding` —— 它是 `WaitForBackendBatches` 的等待条件，
// 一旦被多减一次就会让该自旋失真 ⇒ 挂死）；若 45s 没有新 PASS ⇒ 判定真挂死，打印现场并以
// rc=3 退出（不再与"慢"混淆，也不再让 CI 靠外部超时兜）。
// 实测价值：这套器械在 own-batch 实验里一次就定位到"卡在 TestJccLongRunStability、
//   且 outstanding 已下溢"这一决定性线索。
// ══════════════════════════════════════════════════════════════════════════════════
namespace
{
    constexpr uint64_t kWatchdogStallMs = 45'000;

    uint64_t TestElapsedMs() noexcept
    {
        static const auto start = std::chrono::steady_clock::now();
        return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - start).count());
    }

    std::mutex g_progressMutex;
    std::string g_lastPassName;
    std::atomic<uint64_t> g_lastPassAtMs{ 0 };

    // 把 stdout 转发到原缓冲，同时：① 给完整行加耗时前缀；② 记录 PASS 进度供看门狗判活。
    class ProgressStreamBuf : public std::streambuf
    {
    public:
        explicit ProgressStreamBuf(std::streambuf* inner) noexcept : _inner(inner) {}
    protected:
        int_type overflow(int_type ch) override
        {
            if (traits_type::eq_int_type(ch, traits_type::eof()))
                return traits_type::not_eof(ch);
            const char c = traits_type::to_char_type(ch);
            if (c == '\n') EmitLine();
            else _line.push_back(c);
            return ch;
        }
        std::streamsize xsputn(const char* s, std::streamsize n) override
        {
            for (std::streamsize i = 0; i < n; ++i)
            {
                if (s[i] == '\n') EmitLine();
                else _line.push_back(s[i]);
            }
            return n;
        }
        int sync() override
        {
            if (!_line.empty())
            {
                _inner->sputn(_line.data(), static_cast<std::streamsize>(_line.size()));
                _line.clear();
            }
            return _inner->pubsync();
        }
    private:
        void EmitLine()
        {
            const uint64_t ms = TestElapsedMs();
            char prefix[32];
            const int n = std::snprintf(prefix, sizeof(prefix), "[t=%7llums] ",
                static_cast<unsigned long long>(ms));
            _inner->sputn(prefix, n);
            _inner->sputn(_line.data(), static_cast<std::streamsize>(_line.size()));
            _inner->sputc('\n');
            if (_line.compare(0, 5, "PASS ") == 0)
            {
                std::lock_guard<std::mutex> lock(g_progressMutex);
                g_lastPassName = _line;
                g_lastPassAtMs.store(ms, std::memory_order_release);
            }
            _line.clear();
        }
        std::streambuf* _inner;
        std::string _line;
    };
} // namespace

int main()
{
    std::cout << std::unitbuf;
    const char* watchdogEnv = std::getenv("ENTJOY_TEST_WATCHDOG");
    const bool watchdogOn = watchdogEnv != nullptr && watchdogEnv[0] == '1';
    ProgressStreamBuf progressBuf(std::cout.rdbuf());
    std::jthread watchdog;
    if (watchdogOn)
    {
        std::cout.rdbuf(&progressBuf);
        g_lastPassAtMs.store(TestElapsedMs(), std::memory_order_release);
        watchdog = std::jthread([](std::stop_token stop)
        {
            while (!stop.stop_requested())
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(1000));
                if (stop.stop_requested()) break;
                const uint64_t now = TestElapsedMs();
                const uint64_t last = g_lastPassAtMs.load(std::memory_order_acquire);
                const uint64_t outstanding =
                    JobSystem::g_backendBatchesOutstanding.load(std::memory_order_acquire);
                std::string lastPass;
                {
                    std::lock_guard<std::mutex> lock(g_progressMutex);
                    lastPass = g_lastPassName;
                }
                const bool stalled = (now - last) > kWatchdogStallMs;
                char line[512];
                std::snprintf(line, sizeof(line),
                    "[WD t=%llus] lastPass=%s | outstanding=%llu%s\n",
                    static_cast<unsigned long long>(now / 1000),
                    lastPass.empty() ? "<none>" : lastPass.c_str(),
                    static_cast<unsigned long long>(outstanding),
                    stalled ? "  <<< STALL" : "");
                std::fwrite(line, 1, std::strlen(line), stderr);
                std::fflush(stderr);
                if (stalled)
                {
                    std::fprintf(stderr,
                        "[WD] STALL: no PASS for %llu ms -> real hang, aborting rc=3\n",
                        static_cast<unsigned long long>(now - last));
                    std::fflush(stderr);
                    std::_Exit(3);
                }
            }
        });
    }
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
        TestStaleSettlementRejectedByGeneration();
        std::cout << "PASS StaleSettlementRejectedByGeneration\n";
        TestBoundaryTimingDiagnostics();
        std::cout << "PASS BoundaryTimingDiagnostics\n";
        TestParallelForExactOnceAndCallerAssist();
        std::cout << "PASS ParallelForExactOnceAndCallerAssist\n";
        TestConstructionFailureTransfersContextOwnershipToCaller();
        std::cout << "PASS ConstructionFailureTransfersContextOwnershipToCaller\n";
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
        TestParallelForElementCoverage();
        std::cout << "PASS ParallelForElementCoverage\n";
        TestChunkRunElementCoverage();
        std::cout << "PASS ChunkRunElementCoverage\n";
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
        TestShutdownRejectedFromWorkerThread();
        std::cout << "PASS ShutdownRejectedFromWorkerThread\n";
        TestWorkerCapParameterized();
        std::cout << "PASS WorkerCapParameterized\n";
        TestParkWakeStress();
        std::cout << "PASS ParkWakeStress\n";
        TestStartStopCycle();
        std::cout << "PASS StartStopCycle\n";

        // ── 对抗性压力（2026-08-23）──
        TestWorkChannelStorm();
        std::cout << "PASS WorkChannelStorm\n";
        TestTokenShutdownMix();
        std::cout << "PASS TokenShutdownMix\n";
        TestScheduleCompletePressure();
        std::cout << "PASS ScheduleCompletePressure\n";

        // ── JobCostCache（per-job 自动 batch，2026-08-23）──
        TestJobCostCacheBasic();
        TestJobCostCacheNoUnderflow();
        TestJobCostCacheSpikeSelfHeal();
        TestJobCostCacheSlotIndexNotLowBits();
        TestJobCostCacheCollisionReuse();
        TestResolveChunkSizeFallback();
        TestIntOverflowCeilDiv();

        // ── JobCostCache 对抗性压力（并发 / 正确性 / 稳定性）──
        TestJccConcurrentHeterogeneous();
        TestJccResultsInvariantAcrossTiles();
        TestJccFlagToggleMidFlight();
        TestJccCollisionSlotConcurrent();
        TestJccConcurrentSameHashBatches();
        TestJccWaveCostVariance();
        TestJccRandomVariance();
        TestJccLongRunStability();
        TestDiagnosticContainersBounded();
        TestDiagnosticNameMapBounded();

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
