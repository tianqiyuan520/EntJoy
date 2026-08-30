// 隐式批（native 收集）测试：g_implicitBatchEnabled + SubmitOrPending + FlushPendingSubmits。
// 直接链接源码（不经 Exports.cpp），故开关用 g_implicitBatchEnabled 直写、
// force point 用 FlushPendingSubmits 直调；导出层（JobSystem_SetImplicitBatchEnabled /
// Complete 自动 flush）由 C# 冒烟另行验证。
#include "JobSystem.h"
#include "JobSystemInternal.h"

#include <atomic>
#include <cstdio>
#include <cstdlib>

namespace {

std::atomic<int> g_execCountA{ 0 };
std::atomic<int> g_execCountB{ 0 };
std::atomic<int> g_execStartA{ -1 };

void BatchFuncA(void*, int start, int count)
{
    g_execStartA.store(start, std::memory_order_relaxed);
    g_execCountA.fetch_add(count, std::memory_order_relaxed);
}

void BatchFuncB(void*, int start, int count)
{
    (void)start;
    g_execCountB.fetch_add(count, std::memory_order_relaxed);
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

} // namespace

int main()
{
    // ---- Test 1: 开关开 → 大批 job 挂 pending，未 flush 不执行；Flush 后执行 ----
    {
        JobSystem::Scheduler::Initialize(4);
        JobSystem::g_implicitBatchEnabled.store(true, std::memory_order_relaxed);
        g_execCountA.store(0, std::memory_order_relaxed);
        g_execStartA.store(-1, std::memory_order_relaxed);
        auto h = JobSystem::Scheduler::ScheduleParallelForBatch(
            BatchFuncA, nullptr, 8192, 128, nullptr, {});
        const bool pendingNotCompleted = !h.IsCompleted();
        JobSystem::FlushPendingSubmits();
        h.Complete();
        CHECK(pendingNotCompleted, "Test1 pending job not completed before flush");
        CHECK(g_execCountA.load(std::memory_order_relaxed) == 8192, "Test1 all elements executed after flush");
        JobSystem::g_implicitBatchEnabled.store(false, std::memory_order_relaxed);
        JobSystem::Scheduler::Shutdown();
    }

    // ---- Test 2: 关闭开关（先 flush 排空）→ pending 被提交执行 ----
    {
        JobSystem::Scheduler::Initialize(4);
        JobSystem::g_implicitBatchEnabled.store(true, std::memory_order_relaxed);
        g_execCountA.store(0, std::memory_order_relaxed);
        auto h = JobSystem::Scheduler::ScheduleParallelForBatch(
            BatchFuncA, nullptr, 8192, 128, nullptr, {});
        // 模拟 JobSystem_SetImplicitBatchEnabled(0) 内部：先 flush 排空，再置 false
        JobSystem::FlushPendingSubmits();
        JobSystem::g_implicitBatchEnabled.store(false, std::memory_order_relaxed);
        h.Complete();
        CHECK(g_execCountA.load(std::memory_order_relaxed) == 8192, "Test2 disable drains pending");
        JobSystem::Scheduler::Shutdown();
    }

    // ---- Test 3: 依赖未完成 job 走 continuation，不受 pending 影响 ----
    {
        JobSystem::Scheduler::Initialize(4);
        JobSystem::g_implicitBatchEnabled.store(true, std::memory_order_relaxed);
        g_execCountA.store(0, std::memory_order_relaxed);
        g_execCountB.store(0, std::memory_order_relaxed);
        auto hA = JobSystem::Scheduler::ScheduleParallelForBatch(
            BatchFuncA, nullptr, 8192, 128, nullptr, {});
        // B 依赖 A（A 未完成 → B 走 continuation，不挂 pending）
        auto hB = JobSystem::Scheduler::ScheduleParallelForBatch(
            BatchFuncB, nullptr, 8192, 128, nullptr, hA);
        JobSystem::FlushPendingSubmits();
        hA.Complete();
        hB.Complete();
        CHECK(g_execCountA.load(std::memory_order_relaxed) == 8192, "Test3 A executed");
        CHECK(g_execCountB.load(std::memory_order_relaxed) == 8192, "Test3 B executed via continuation");
        JobSystem::g_implicitBatchEnabled.store(false, std::memory_order_relaxed);
        JobSystem::Scheduler::Shutdown();
    }

    // ---- Test 4: 开关关闭（默认）行为与现状一致 ----
    {
        JobSystem::Scheduler::Initialize(4);
        g_execCountA.store(0, std::memory_order_relaxed);
        auto h = JobSystem::Scheduler::ScheduleParallelForBatch(
            BatchFuncA, nullptr, 8192, 128, nullptr, {});
        h.Complete();
        CHECK(g_execCountA.load(std::memory_order_relaxed) == 8192, "Test4 off path unchanged");
        JobSystem::Scheduler::Shutdown();
    }

    // ---- Test 5: 开关开时小 job（rc<=1，fast path）不挂 pending，仍立即执行 ----
    {
        JobSystem::Scheduler::Initialize(4);
        JobSystem::g_implicitBatchEnabled.store(true, std::memory_order_relaxed);
        g_execCountA.store(0, std::memory_order_relaxed);
        // length=32 → rc<=1 → ScheduleFastPath（SubmitWork 池任务），不经过 SubmitOrPending
        auto h = JobSystem::Scheduler::ScheduleParallelForBatch(
            BatchFuncA, nullptr, 32, 32, nullptr, {});
        h.Complete(); // fast path 已提交，无需 flush 即可完成
        CHECK(g_execCountA.load(std::memory_order_relaxed) == 32, "Test5 small job not collected");
        JobSystem::g_implicitBatchEnabled.store(false, std::memory_order_relaxed);
        JobSystem::Scheduler::Shutdown();
    }

    if (Failures == 0)
    {
        printf("ImplicitBatchTests: ALL PASS\n");
        return 0;
    }
    printf("ImplicitBatchTests: %d FAILURES\n", Failures);
    return 1;
}
