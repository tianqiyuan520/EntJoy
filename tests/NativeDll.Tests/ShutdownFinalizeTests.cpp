// Shutdown 竞态与残留 batch 终态测试（2026-09-26 审计 B3）。
//
// 缺陷（代码级确证）：`ChaseLevScheduler::SubmitBatch` 的批量入队快路径（injector_.PushMany）**没有**
// quit_/running_ 守卫，而同一文件里的 `PushTaskBackoff`（溢出回退）与 `SubmitWork` 一直都有。
// 外层 `JobSystem_Tiles.cpp::SubmitBatch` 只在入口做一次 `scheduler->IsRunning()` 检查（check-then-act），
// 一旦在「检查通过」与「内层 push」之间发生 Stop()（quit_=true + worker join + DrainRemaining 已完成），
// 这些 token 就落进一个无消费者的注入器：
//   - batch->pendingTasks 永不归零 → TryFinalizeChaseLevBatch 不执行
//   - cleanup / ReleaseBatch / HandleState / g_backendBatchesOutstanding 全部残留（泄漏）
//   - 调用方 JobHandle::Complete() 永久阻塞，JobSystem_GetStats 也会卡在 WaitForBackendBatches
// 该窗口是 TOCTOU 竞态（需要精确抢占点），因此本测试覆盖的是**可观测后置条件**：
//   ① 未 Complete 的 job 在 Shutdown 后不得留下 outstanding，且 DrainRemaining 必须把已发布工作跑完；
//   ② 并发「提交线程 + Shutdown」跑多轮，Shutdown 必须有界返回，结束后 outstanding 必须归零。
// 内层守卫本身由与 `SubmitWork` 对称的代码保证（`SubmitBatch` 顶部与每轮 bulk 前的 running_/quit_ 检查）。
#include "ChaseLevScheduler.h"
#include "JobSystem.h"
#include "JobSystemInternal.h"

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <thread>
#include <vector>

namespace {

std::atomic<int> g_executed{ 0 };
std::atomic<int> g_plainExecuted{ 0 };
std::atomic<int> g_serialExecuted{ 0 };

void BatchFunc(void*, int start, int count)
{
    (void)start;
    g_executed.fetch_add(count, std::memory_order_relaxed);
}

// plain IJob（ChaseLevScheduler::SubmitWork，性能项 1 的主战场）
void PlainJobFunc(void*)
{
    g_plainExecuted.fetch_add(1, std::memory_order_relaxed);
}

// IJobFor length>64（另一处 SubmitWork 调用点：Scheduler::ScheduleFor）
void SerialFunc(void*, int)
{
    g_serialExecuted.fetch_add(1, std::memory_order_relaxed);
}

int Failures = 0;

#define CHECK(cond, name)                                                    \
    do {                                                                     \
        if (!(cond)) {                                                       \
            printf("FAIL %s (line %d)\n", name, __LINE__);                   \
            ++Failures;                                                      \
        }                                                                    \
        else {                                                               \
            printf("PASS %s\n", name);                                       \
        }                                                                    \
    } while (0)

void WaitOutstandingZero(int timeoutMs)
{
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
    while (JobSystem::g_backendBatchesOutstanding.load(std::memory_order_acquire) != 0 &&
           std::chrono::steady_clock::now() < deadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
}

} // namespace

int main()
{
    setvbuf(stdout, nullptr, _IONBF, 0);
    // ── 性能项 1（2026-09-26）：`ENTJOY_DEFER_WAKE` 必须在**任何** Scheduler::Initialize 之前置位：
    //    DeferWakeEnabled() 用函数内 static 锁存 env，首次调用后不再重读。本文件因此把它放在
    //    main 顶部（Test3 专门验证 SubmitWork 与该 defer 模式的组合）。
#ifdef _WIN32
    _putenv_s("ENTJOY_DEFER_WAKE", "1");
#else
    setenv("ENTJOY_DEFER_WAKE", "1", 1);
#endif
    constexpr int kLength = 4096;
    constexpr int kBatch = 128;

    // ---- Test 1: 未 Complete 的 job 在 Shutdown 后必须收敛（无残留、工作已执行）----
    {
        JobSystem::Scheduler::Initialize(4);
        g_executed.store(0, std::memory_order_relaxed);

        // 故意不 Complete：Shutdown 必须自行排空并收敛账本，而不是留下悬挂 batch
        (void)JobSystem::Scheduler::ScheduleParallelForBatch(
            BatchFunc, nullptr, kLength, kBatch, nullptr, {});
        JobSystem::Scheduler::Shutdown();
        WaitOutstandingZero(2000);

        CHECK(JobSystem::g_backendBatchesOutstanding.load(std::memory_order_acquire) == 0,
            "Test1 no outstanding batch after Shutdown with unfinished job");
        CHECK(g_executed.load(std::memory_order_relaxed) == kLength,
            "Test1 drained job fully executed");
    }

    // ---- Test 2: 并发 提交 + Shutdown 压测（多轮）----
    {
        constexpr int kRounds = 20;
        int notBounded = 0;
        int notDrained = 0;
        int notZero = 0;

        for (int round = 0; round < kRounds; ++round)
        {
            JobSystem::Scheduler::Initialize(2);
            std::atomic<bool> stopSubmitter{ false };

            std::thread submitter([&] {
                while (!stopSubmitter.load(std::memory_order_acquire))
                {
                    // Shutdown 之后 LoadChaseLevScheduler() 会返回空 → 走 AbortUnsubmittedBatch（安全）
                    (void)JobSystem::Scheduler::ScheduleParallelForBatch(
                        BatchFunc, nullptr, 1024, 64, nullptr, {});
                    std::this_thread::sleep_for(std::chrono::microseconds(200));
                }
            });

            std::this_thread::sleep_for(std::chrono::milliseconds(2));
            const auto t0 = std::chrono::steady_clock::now();
            JobSystem::Scheduler::Shutdown();
            const auto elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - t0).count();

            stopSubmitter.store(true, std::memory_order_release);
            submitter.join();

            if (elapsedMs >= 5000) ++notBounded;

            WaitOutstandingZero(2000);
            if (JobSystem::g_backendBatchesOutstanding.load(std::memory_order_acquire) != 0)
            {
                ++notZero;
                // 下一轮 Initialize 前强制归零，避免把上轮残留算到本轮
                JobSystem::g_backendBatchesOutstanding.store(0, std::memory_order_release);
            }
        }

        CHECK(notBounded == 0, "Test2 Shutdown bounded in every round");
        CHECK(notZero == 0, "Test2 no outstanding batch after concurrent Shutdown");
        (void)notDrained;
    }

    // ---- Test 3: SubmitWork 必须与 SubmitBatch 共用 defer-wake 守卫（性能项 1）----
    // 缺陷（代码级）：`ChaseLevScheduler::SubmitWork` 无条件 `wakeEpoch.fetch_add + notify_all`，
    // 完全忽略 `g_submitDeferDepth` 与 `ENTJOY_DEFER_WAKE`：N 个批量 plain IJob = N 次广播
    // （一次唤醒 parked worker 的广播实测 37～39 µs ⇒ 空闲后 100 job 多付 ~61 µs/frame）。
    // 修复后 SubmitWork 与 SubmitBatch(:557-570) 同守卫：窗口内或 defer 模式下不逐 job 广播，
    // 只置 `g_pendingDeferredWake`，由窗口关闭处 / `JobHandle::Complete()` 的
    // `FlushDeferredWake()` 补一次。
    // 本用例验证的是**可观测后置条件**（丢唤醒 ⇒ Complete 永久阻塞、job 不执行）：
    //   ① 大量 defer 模式下的 plain IJob / IJobFor / tile 批必须在有界时间内全部 Complete；
    //   ② 每个 plain IJob 恰好执行一次；
    //   ③ 推迟的广播确实被补齐（g_deferredWakeFlushes 递增）；
    //   ④ outstanding 批次回到 0（无泄漏、无半退役）。
    {
        constexpr int kJobs = 400;
        constexpr int kSerialJobs = 32;
        JobSystem::Scheduler::Initialize(8);
        g_plainExecuted.store(0, std::memory_order_relaxed);
        g_serialExecuted.store(0, std::memory_order_relaxed);
        g_executed.store(0, std::memory_order_relaxed);
        // 先让所有 worker 真正 park（自旋窗耗尽后进入 wakeEpoch.wait）。这样"提交期没有广播"
        // 就等价于"没有 worker 会来领活"：一旦补齐广播的路径失效，本用例必然在下面的有界等待里
        // 暴露为 Complete 永久阻塞（而不是被仍在自旋的 worker 掩盖）。
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        JobSystem::g_deferredWakeFlushes.store(0, std::memory_order_relaxed);
        JobSystem::ResetStatsSnapshot();   // 清 g_publishedJobs 等，避免把 Test1/2 的计数混进来

        std::vector<JobSystem::JobHandle> handles;
        handles.reserve(kJobs + kSerialJobs + 32);
        for (int i = 0; i < kJobs; ++i)
            handles.push_back(JobSystem::Scheduler::Schedule(PlainJobFunc, nullptr, nullptr, {}));
        for (int i = 0; i < kSerialJobs; ++i)
            handles.push_back(JobSystem::Scheduler::ScheduleFor(SerialFunc, nullptr, 257, nullptr, {}));
        for (int i = 0; i < 32; ++i)
            handles.push_back(JobSystem::Scheduler::ScheduleParallelForBatch(
                BatchFunc, nullptr, kLength, kBatch, nullptr, {}));

        // 有界等待：Complete 若丢唤醒会永久阻塞 ⇒ 用看门狗线程 + 1ms 轮询判定"有界"。
        std::atomic<bool> done{ false };
        std::thread waiter([&] {
            for (auto& h : handles) h.Complete();
            done.store(true, std::memory_order_release);
        });

        const auto deadline =
            std::chrono::steady_clock::now() + std::chrono::seconds(10);
        while (!done.load(std::memory_order_acquire) &&
               std::chrono::steady_clock::now() < deadline)
            std::this_thread::sleep_for(std::chrono::milliseconds(1));

        const bool bounded = done.load(std::memory_order_acquire);
        CHECK(bounded, "Test3 deferWake handles all completed within 10s bound");
        if (!bounded)
        {
            // 挂死的 waiter 无法 join：直接终止进程，避免测试进程被 CI 超时杀掉而无诊断输出。
            printf("RESULT: FAIL (deferred-wake lost wakeup: Complete blocked)\n");
            std::_Exit(1);
        }
        waiter.join();

        CHECK(g_plainExecuted.load(std::memory_order_relaxed) == kJobs,
            "Test3 every deferred plain IJob executed exactly once");
        CHECK(g_serialExecuted.load(std::memory_order_relaxed) == kSerialJobs * 257,
            "Test3 every deferred IJobFor iteration executed");
        CHECK(g_executed.load(std::memory_order_relaxed) == 32 * kLength,
            "Test3 every deferred batch tile executed");
        CHECK(JobSystem::g_deferredWakeFlushes.load(std::memory_order_relaxed) > 0,
            "Test3 deferred wake broadcasts were flushed (no silent drop of the wakeup)");
        WaitOutstandingZero(2000);
        CHECK(JobSystem::g_backendBatchesOutstanding.load(std::memory_order_acquire) == 0,
            "Test3 outstanding batches return to 0 after deferred-wake mode");
        JobSystem::Scheduler::Shutdown();
    }

    printf("RESULT: %s\n", Failures == 0 ? "PASS" : "FAIL");
    return Failures == 0 ? 0 : 1;
}
