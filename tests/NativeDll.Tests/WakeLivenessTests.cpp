// WakeLivenessTests.cpp -- N12 `ENTJOY_WAKE_POLL`（提交侧"醒着的人不够才写"）的**存活/饥饿**回归测试。
//
// 为什么单独要这一套：本协议的失效模式**不是崩溃，而是丢唤醒** —— 令牌已经进了注入器，但
// 提交侧判定"有人会自己领到"而**一个字节都没写**，若那一刻其实没人处于搜索区，这个令牌就要
// 等到下一次派发才被领走；如果调用方正阻塞在 `Complete()`，表现就是**死锁**。
// 现有 9 套件覆盖了功能语义，但它们基本都跑在"worker 已经醒着"的连续形态里；真正危险的是
// **全体 worker 都已在 futex 上停靠**时的那一次派发 —— 那是唯一必须走慢路径（bump+notify_all）
// 的时刻，也是本测试专门制造的形态。
//
// 五个形态（每个都断言"每个 job 恰好执行一次"，并把超时变成失败而不是挂住）：
//   A 全体停靠 + 单个小 job（`SubmitWork`，need=1）
//   B 全体停靠 + 真并行批（`SubmitBatch`，need=tokenCount）
//   C 池子叫热后连发突发（worker 都在搜索区 ⇒ 应当走快路径、一个字节都不写）
//   D "登记中的人都在执行"（长 job 占满 worker）+ 新小 job ⇒ 不得搁浅
//   E 随机形状 + 随机空隙（连发 / round-trip / 批 混合）
// 另外断言**两条路径都被走到**（`skips>0` 且 `wakes>0`）：否则"新代码没生效"会让测试假通过。
//
// 与死锁判定配套：`RunWithTimeout` 复用 ChaseLevIntegrationTests.cpp 的做法（超时 →
// 打印 outstanding/nativeBatches → DumpState → abort），所以丢唤醒会以 rc!=0 结束而不是卡住。
#include "TestGuards.h"   // 覆盖判据 / sanitizer 策略（本目录所有原生测试共用）
#include "../NativeDll/JobSystem.h"
#include "../NativeDll/JobSystemInternal.h"
#include "../NativeDll/ChaseLevScheduler.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <future>
#include <iostream>
#include <random>
#include <stdexcept>
#include <thread>
#include <vector>

namespace
{
    using Clock = std::chrono::steady_clock;
    constexpr int kWorkers = 8;
    constexpr int kTimeoutSec = 60;

    void Require(bool condition, const char* message)
    {
        if (!condition)
        {
            std::cerr << "[FAIL] " << message << std::endl;
            throw std::runtime_error(message);
        }
    }

    // 本文件里有两类判据，必须区分对待：
    //   1) 正确性判据（任何构建下都是硬判据）：形态 A~E 的"每个 job / 每个 index 恰好执行一次"；
    //      以及"不丢唤醒"——后者表现为**挂住**，由 RunWithTimeout 的 kTimeoutSec 兜住
    //      （打 [DEADLOCK] + DumpState + abort）。
    //   2) 覆盖判据（`skips>0` / `wakes>0` 这类"新代码确实被走到"的自证）：它们依赖
    //      "全体停靠且无人登记" / "有人登记在搜索区" 这些**形状**在采样瞬间成立。
    // `-fsanitize=thread` / `-fsanitize=address` 会显著改变调度（worker 更容易一直停在停靠态，
    // 或恰好在提交侧读计数的那个窗口里完成状态迁移），形状不一定复现。sanitizer 构建下把覆盖判据
    // 降级为诊断输出（计数照打，并打 [COVERAGE-SKIP] 便于人工核对），避免用"形状没出现"红掉 CI；
    // 同一套判据在 windows-test（无 sanitizer 的 Release 腿）上仍是硬判据。
    // 覆盖判据与 sanitizer 策略**集中**在 `TestGuards.h`（本项目所有原生测试共用同一套），
    // 本文件只保留「把硬失败交给自己的 Require」这一行适配 —— 不要再在此写 `#if __SANITIZE_*`。
    constexpr int kStateWaitMs  = TestGuards::WaitMs(3000, 30000);
    constexpr int kPollerWaitMs = TestGuards::WaitMs(1000, 2000);

    void RequireCoverage(bool condition, const char* message)
    {
        if (condition) return;
        if (TestGuards::CoverageMiss(message)) Require(false, message);
    }

    void RunWithTimeout(const char* name, void (*fn)())
    {
        std::cout << "[START] " << name << std::endl << std::flush;
        auto future = std::async(std::launch::async, fn);
        if (future.wait_for(std::chrono::seconds(kTimeoutSec)) == std::future_status::timeout)
        {
            std::cerr << "[DEADLOCK] " << name << " timed out -- 丢唤醒（令牌无人领）" << std::endl;
            std::cerr << "  outstanding="
                      << JobSystem::g_backendBatchesOutstanding.load(std::memory_order_relaxed)
                      << " nativeBatches=" << JobSystem::g_nativeBatches.load(std::memory_order_relaxed)
                      << " wakePollSkips=" << JobSystem::g_wakePollSkips.load(std::memory_order_relaxed)
                      << " wakePollWakes=" << JobSystem::g_wakePollWakes.load(std::memory_order_relaxed)
                      << std::endl;
            if (auto* s = JobSystem::LoadChaseLevScheduler()) s->DumpState(name);
            std::abort();
        }
        future.get();
        std::cout << "[DONE]  " << name << std::endl << std::flush;
    }

    // 把提交线程（= 本线程）的 thread_local 计数合并进全局后再读 —— 计数走 TLS 累加，
    // 不 flush 会读到 <1024 的旧值（见 ChaseLevScheduler.cpp 的说明）。
    uint64_t Skips()
    {
        JobSystem::WakePollFlushCurrentThread();
        return JobSystem::g_wakePollSkips.load(std::memory_order_relaxed);
    }
    uint64_t Wakes()
    {
        JobSystem::WakePollFlushCurrentThread();
        return JobSystem::g_wakePollWakes.load(std::memory_order_relaxed);
    }
    int Parked()
    {
        auto* s = JobSystem::LoadChaseLevScheduler();
        return s ? s->parkedWorkers.load(std::memory_order_acquire) : -1;
    }
    uint64_t Idle()
    {
        auto* s = JobSystem::LoadChaseLevScheduler();
        return s ? s->wakeIdlePollers.load(std::memory_order_acquire) : 0;
    }

    // 等到"全体停靠"这个**前置状态真的成立**再派发 —— 否则测的就不是要测的那个形态。
    bool WaitAllParked(int expect, int timeoutMs)
    {
        const auto deadline = Clock::now() + std::chrono::milliseconds(timeoutMs);
        while (Clock::now() < deadline)
        {
            if (Parked() >= expect) return true;
            std::this_thread::sleep_for(std::chrono::microseconds(100));
        }
        return Parked() >= expect;
    }

    // 形态 A 要的前置状态是**两个量同时**成立："全体停靠" + "无人登记在搜索区"。
    // 两者都会被 worker 的合法状态迁移改动（停靠 → 被唤醒 → 重新登记进搜索区），所以必须在
    // 同一个循环里一起采样：先等停靠、再单点断言 idle==0，会在慢速调度下因这个窗口而假红。
    bool WaitAllParkedUnregistered(int expect, int timeoutMs)
    {
        const auto deadline = Clock::now() + std::chrono::milliseconds(timeoutMs);
        for (;;)
        {
            if (Parked() >= expect && Idle() == 0) return true;
            if (Clock::now() >= deadline) return false;
            std::this_thread::sleep_for(std::chrono::microseconds(100));
        }
    }

    // 形态 C 要的前置状态是"有人登记在搜索区"（快路径的前提）。尽力等到；等不到只说明形状没出现，
    // 由调用点的覆盖判据处理。
    bool WaitRegisteredPoller(int timeoutMs)
    {
        const auto deadline = Clock::now() + std::chrono::milliseconds(timeoutMs);
        for (;;)
        {
            if (Idle() > 0) return true;
            if (Clock::now() >= deadline) return false;
            std::this_thread::sleep_for(std::chrono::microseconds(100));
        }
    }

    // ---------- 工作负载 ----------
    std::atomic<uint64_t> g_executed{ 0 };

    void WorkFn(void* raw)
    {
        g_executed.fetch_add(1, std::memory_order_relaxed);
        if (raw) std::this_thread::sleep_for(std::chrono::microseconds(*static_cast<int*>(raw)));
    }

    // `ScheduleParallelForBatch` 的回调是 (ctx, start, count) —— 逐 index 恰好一次是本测试的判据。
    void PfBatchFn(void* raw, int start, int count)
    {
        auto* perIndex = static_cast<std::atomic<int>*>(raw);
        if (!perIndex) return;
        for (int i = start; i < start + count; ++i)
            perIndex[i].fetch_add(1, std::memory_order_relaxed);
    }

    void WorkCleanup(void*) {}

    // ---------- 形态 A：全体停靠 + 单个小 job ----------
    void PhaseA_AllParkedSingleWork()
    {
        constexpr int rounds = 12;
        for (int r = 0; r < rounds; ++r)
        {
            // 先确认前置状态真的成立（全体停靠 + 无人登记），否则测的不是要测的那个形态。
            Require(WaitAllParkedUnregistered(kWorkers, kStateWaitMs),
                "phase A: workers never all parked with no registered poller");
            const uint64_t wakesBefore = Wakes();
            int spinUs = 0;
            auto h = JobSystem::Scheduler::Schedule(&WorkFn, &spinUs, &WorkCleanup);
            h.Complete();   // 丢唤醒会挂在这里 ⇒ RunWithTimeout 报 [DEADLOCK]，那才是正确性判据
            // 覆盖判据：本趟是否走慢路径，取决于**提交侧读 wakeIdlePollers 的那一刻**是否无人登记；
            // 采样与那次读之间还有窗口（提交侧要 flush 计数 + 从池里取任务），窗口里刚执行完上一趟的
            // worker 可以合法地重新登记进搜索区 ⇒ 那时走快路径是设计允许的，不代表丢唤醒。
            // （只在 N12 打开时才有这个计数；关掉开关时走的是基线路径，两个计数都不动。）
            if (JobSystem::WakePollEnabled())
                RequireCoverage(Wakes() > wakesBefore,
                    "phase A: no slow-path wake while every worker was parked (fast path taken?)");
        }
        std::cout << "  phase A ok (" << rounds << " rounds, all-parked dispatch took the slow path)" << std::endl;
    }

    // ---------- 形态 B：全体停靠 + 真并行批 ----------
    void PhaseB_AllParkedBatch()
    {
        constexpr int length = 4096;
        constexpr int rounds = 6;
        std::vector<std::atomic<int>> per(length);
        for (auto& v : per) v.store(0, std::memory_order_relaxed);
        for (int r = 0; r < rounds; ++r)
        {
            for (auto& v : per) v.store(0, std::memory_order_relaxed);
            Require(WaitAllParked(kWorkers, kStateWaitMs), "phase B: workers never all parked");
            auto h = JobSystem::Scheduler::ScheduleParallelForBatch(&PfBatchFn, per.data(), length, 64);
            h.Complete();
            for (int i = 0; i < length; ++i)
                Require(per[i].load(std::memory_order_relaxed) == 1, "phase B: element executed != once");
        }
        std::cout << "  phase B ok (" << rounds << " all-parked batch rounds, " << length << " elems each)" << std::endl;
    }

    // ---------- 形态 C：池子叫热后连发（应当走快路径）----------
    void PhaseC_BurstTakesFastPath()
    {
        // 注意：**不能**直接连发就断言 skips>0 —— 第一步派发时 worker 可能还全在停靠，前若干条会走慢路径；
        // 快路径的前提是"有人登记在搜索区"。所以每一轮都先把池子叫热（一次派发+等待会让 8 个 worker 醒来
        // 并进入搜索区），再连发，把 3 轮的 skip 累加后再断言。
        constexpr int rounds = 3;
        constexpr int jobs = 300;
        uint64_t totalSkips = 0;
        int spinUs = 0;
        for (int r = 0; r < rounds; ++r)
        {
            {
                auto warm = JobSystem::Scheduler::Schedule(&WorkFn, &spinUs, &WorkCleanup);
                warm.Complete();
            }
            // 快路径的前提是"有人登记在搜索区"：显式等到这个前提（等不到 = 形状没出现，
            // sanitizer 构建下由下面的覆盖判据只打诊断）。
            (void)WaitRegisteredPoller(kPollerWaitMs);
            const uint64_t skipsBefore = Skips();
            std::vector<JobSystem::JobHandle> handles;
            handles.reserve(jobs);
            for (int i = 0; i < jobs; ++i)
                handles.push_back(JobSystem::Scheduler::Schedule(&WorkFn, &spinUs, &WorkCleanup));
            for (auto& h : handles) h.Complete();
            totalSkips += Skips() - skipsBefore;
        }
        // 快路径若根本没被走到，"新代码没生效"会让本测试假通过 —— 覆盖判据（仅开关打开时）。
        if (JobSystem::WakePollEnabled())
            RequireCoverage(totalSkips > 0, "phase C: no packed skip at all -- fast path never taken?");
        std::cout << "  phase C ok (" << rounds << " x " << jobs << " burst jobs, packed skips=" << totalSkips << ")" << std::endl;
    }

    // ---------- 形态 D：登记中的人都在执行 + 新小 job ----------
    void PhaseD_BusyRegistrantsThenSmallJob()
    {
        // 8 个长 job 占满 8 个 worker（每个 ~15 ms）⇒ 派发新小 job 时 idlePollers>0（登记还在）但那些人
        // 都在执行体里。快路径会跳过写；正确性要求小 job 在某个 worker 空出来后立刻被领走（不得搁浅）。
        for (int round = 0; round < 3; ++round)
        {
            std::vector<JobSystem::JobHandle> longJobs;
            int longUs = 15000;
            for (int i = 0; i < kWorkers; ++i)
                longJobs.push_back(JobSystem::Scheduler::Schedule(&WorkFn, &longUs, &WorkCleanup));
            std::this_thread::sleep_for(std::chrono::milliseconds(2));   // 让 8 个都进执行体
            int shortUs = 0;
            auto small = JobSystem::Scheduler::Schedule(&WorkFn, &shortUs, &WorkCleanup);
            small.Complete();                 // 必须在超时前完成（丢唤醒会挂住 → RunWithTimeout 抓）
            for (auto& h : longJobs) h.Complete();
        }
        std::cout << "  phase D ok (3 rounds: 8 busy workers + a late small job)" << std::endl;
    }

    // ---------- 形态 E：随机形状 + 随机空隙 ----------
    void PhaseE_Randomized()
    {
        std::mt19937 rng(12345);
        constexpr int iterations = 150;
        constexpr int length = 512;
        std::vector<std::atomic<int>> per(length);
        for (auto& v : per) v.store(0, std::memory_order_relaxed);
        for (int it = 0; it < iterations; ++it)
        {
            const int shape = static_cast<int>(rng() % 3u);
            const int gapUs = static_cast<int>(rng() % 3000u);
            if (gapUs > 0) std::this_thread::sleep_for(std::chrono::microseconds(gapUs));
            if (shape == 0)
            {
                int spinUs = 0;
                auto h = JobSystem::Scheduler::Schedule(&WorkFn, &spinUs, &WorkCleanup);
                h.Complete();
            }
            else if (shape == 1)
            {
                for (auto& v : per) v.store(0, std::memory_order_relaxed);
                auto h = JobSystem::Scheduler::ScheduleParallelForBatch(&PfBatchFn, per.data(), length, 64);
                h.Complete();
                for (int i = 0; i < length; ++i)
                    Require(per[i].load(std::memory_order_relaxed) == 1, "phase E: element executed != once");
            }
            else
            {
                // 连发一小串再一起等（连发 / round-trip 的混合形态）
                std::vector<JobSystem::JobHandle> hs;
                const int n = 1 + static_cast<int>(rng() % 32u);
                int spinUs = 0;
                for (int i = 0; i < n; ++i)
                    hs.push_back(JobSystem::Scheduler::Schedule(&WorkFn, &spinUs, &WorkCleanup));
                for (auto& h : hs) h.Complete();
            }
        }
        std::cout << "  phase E ok (" << iterations << " randomized shapes with random gaps)" << std::endl;
    }

    void Body()
    {
        g_executed.store(0, std::memory_order_relaxed);
        // 顺带覆盖 DumpState：丢唤醒的诊断**全靠它**（它现在会打 idlePollers/parked/wakePoll），
        // 若这段 printf 本身有问题，将来查死锁时会变成"dump 时崩掉"而不是"打出状态"。
        if (auto* s = JobSystem::LoadChaseLevScheduler()) s->DumpState("liveness-start");
        PhaseA_AllParkedSingleWork();
        PhaseB_AllParkedBatch();
        PhaseC_BurstTakesFastPath();
        PhaseD_BusyRegistrantsThenSmallJob();
        PhaseE_Randomized();

        // 生效证据：两条路径都被走到（否则"新代码没生效"会让上面的形态测试假通过）。
        if (JobSystem::WakePollEnabled())
        {
            const uint64_t skips = Skips(), wakes = Wakes();
            std::cout << "  evidence: wakePoll=ON skips=" << skips << " wakes=" << wakes
                      << " (executed=" << g_executed.load(std::memory_order_relaxed) << ")" << std::endl;
            RequireCoverage(skips > 0, "N12 enabled but the packed fast path was never taken (skips==0)");
            RequireCoverage(wakes > 0, "N12 enabled but the slow path was never taken (wakes==0)");
        }
        else
        {
            std::cout << "  evidence: wakePoll=OFF (baseline path; phases still must pass)" << std::endl;
        }
    }
}

int main()
{
    std::cout << "=== WakeLivenessTests (N12 ENTJOY_WAKE_POLL liveness/starvation) ===" << std::endl;
    if (!JobSystem::Scheduler::Initialize(kWorkers))
    {
        std::cerr << "[FAIL] Scheduler::Initialize failed" << std::endl;
        return 2;
    }
    int rc = 0;
    try
    {
        RunWithTimeout("WakePollLiveness", &Body);
    }
    catch (const std::exception& ex)
    {
        std::cerr << "[EXCEPTION] " << ex.what() << std::endl;
        rc = 3;
    }
    JobSystem::Scheduler::Shutdown();
    if (rc == 0) std::cout << "RESULT: PASS" << std::endl;
    else std::cout << "RESULT: FAIL" << std::endl;
    return rc;
}
