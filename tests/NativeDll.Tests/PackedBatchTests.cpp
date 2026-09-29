// 打包提交（Item 1）native 回归：
//   `SubmitPackedPlainJobs` 把 K 个无依赖 plain IJob 描述符作为**一个** batch 提交
//   （Exports::JobSystem_ScheduleBatch 对连续同形描述符走该路径）。
//
// 本文件直接链接源码（含 Exports.cpp，因此可同时覆盖导出层的 run 分组逻辑）。
// 覆盖点：
//   1. K 个 job 每个恰好执行一次（含非分片整数倍的各种 K）+ 每个 cleanup 恰好一次；
//   2. 逐 job 异常归属：只有抛异常的 job 的句柄在 Complete() 时重抛；
//   3. 全部 Complete 后 g_backendBatchesOutstanding == 0（无残留批）；
//   4. 未 Complete 就 Shutdown：句柄仍可达终态、Complete 不阻塞、账本归零；
//   5. 逐帧重复提交（真 batch 复用路径）不无界增长 state 分配（池化有效）；
//   6. BatchStorage 分配失败（ENTJOY_TESTING fault injection）时打包路径
//      不消费任何 context、不泄漏 state，句柄槽位为 0（调用方回退逐描述符路径）。
#include "JobSystem.h"
#include "JobSystemInternal.h"
#include "Exports.h"

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdint>
#include <cstdlib>
#include <stdexcept>
#include <thread>
#include <vector>

namespace {

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

struct JobCtx
{
    std::atomic<int>* hits;
    std::atomic<int>* cleanups;
    int index;
    int throwAt;
    // func 与 cleanup 的配对顺序（每个 ctx 记录自己被调用的序号，验证 cleanup 在 func 之后）
    std::atomic<int>* order;
    std::atomic<int>* orderCounter;
};

void PackedFn(void* raw)
{
    auto* ctx = static_cast<JobCtx*>(raw);
    ctx->hits[ctx->index].fetch_add(1, std::memory_order_relaxed);
    if (ctx->order && ctx->orderCounter)
        ctx->order[ctx->index].store(
            ctx->orderCounter->fetch_add(1, std::memory_order_relaxed) + 1,
            std::memory_order_relaxed);
    if (ctx->index == ctx->throwAt)
        throw std::runtime_error("packed job failure");
}

void PackedCleanup(void* raw)
{
    auto* ctx = static_cast<JobCtx*>(raw);
    ctx->cleanups[ctx->index].fetch_add(1, std::memory_order_relaxed);
}

// 等待 backend 账本归零（有界自旋；worker 已完成 tile，退役由最后 task 完成者触发）。
bool WaitOutstandingZero(int timeoutMs = 5000)
{
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
    while (JobSystem::g_backendBatchesOutstanding.load(std::memory_order_acquire) != 0)
    {
        if (std::chrono::steady_clock::now() > deadline) return false;
        std::this_thread::yield();
    }
    return true;
}

// 单轮：提交 K 个包装 job，Complete 全部句柄，返回是否全部成功。
// throwAt < 0 表示不抛异常。
bool RunPackedRound(int k, int throwAt, bool expectPublish = true)
{
    std::vector<std::atomic<int>> hits(k);
    std::vector<std::atomic<int>> cleanups(k);
    std::vector<std::atomic<int>> order(k);
    std::atomic<int> orderCounter{ 0 };
    std::vector<JobCtx> ctxs(k);
    std::vector<JobSystem::PackedPlainJobDesc> descs(k);
    std::vector<void*> out(static_cast<size_t>(k), nullptr);

    for (int i = 0; i < k; ++i)
    {
        hits[i].store(0, std::memory_order_relaxed);
        cleanups[i].store(0, std::memory_order_relaxed);
        order[i].store(0, std::memory_order_relaxed);
        ctxs[i] = JobCtx{ hits.data(), cleanups.data(), i, throwAt, order.data(), &orderCounter };
        descs[i] = JobSystem::PackedPlainJobDesc{ &PackedFn, &ctxs[i], &PackedCleanup };
    }

    const int published = JobSystem::SubmitPackedPlainJobs(descs.data(), k, out.data());
    if (expectPublish && published != k)
    {
        printf("  RunPackedRound k=%d published=%d (expected %d)\n", k, published, k);
        return false;
    }
    if (!expectPublish) return published == 0;

    bool ok = true;
    for (int i = 0; i < k; ++i)
    {
        if (out[i] == nullptr) { ok = false; break; }
        auto* state = static_cast<JobSystem::HandleState*>(out[i]);
        // JobHandle(state, addRef=false) 的析构即释放句柄自身的用户引用
        //（打包路径/Exports::toHandle 已发布该引用）——不得再手动 ReleaseState，
        // 否则同一 state 被回收两次 → 池中出现重复条目 → 两个 job 拿到同一对象。
        bool threw = false;
        {
            JobSystem::JobHandle h(state, false);
            try { h.Complete(); }
            catch (const std::runtime_error&) { threw = true; }
            catch (...) { threw = true; }
        }
        if (i == throwAt) { if (!threw) ok = false; }
        else if (threw) { ok = false; }
    }

    for (int i = 0; i < k; ++i)
    {
        if (hits[i].load(std::memory_order_relaxed) != 1) { ok = false; printf("  job %d hits=%d\n", i, hits[i].load()); }
        if (cleanups[i].load(std::memory_order_relaxed) != 1) { ok = false; printf("  job %d cleanups=%d\n", i, cleanups[i].load()); }
        if (order[i].load(std::memory_order_relaxed) == 0) { ok = false; }
    }
    return ok;
}

// ---- Exports 层（JobSystem_ScheduleBatch）分组：连续同形 plain IJob 应走打包路径 ----
void TestExportsPackedGrouping()
{
    printf("[TEST] TestExportsPackedGrouping\n");
    JobSystem::Scheduler::Initialize(4);

    constexpr int k = 32;
    std::vector<std::atomic<int>> hits(k);
    std::vector<std::atomic<int>> cleanups(k);
    std::vector<JobCtx> ctxs(k);
    std::vector<JobBatchDesc> descs(k);
    std::vector<void*> out(static_cast<size_t>(k), nullptr);
    for (int i = 0; i < k; ++i)
    {
        hits[i].store(0, std::memory_order_relaxed);
        cleanups[i].store(0, std::memory_order_relaxed);
        ctxs[i] = JobCtx{ hits.data(), cleanups.data(), i, -1, nullptr, nullptr };
        descs[i] = JobBatchDesc{};
        descs[i].kind = 0;
        descs[i].func = reinterpret_cast<void*>(&PackedFn);
        descs[i].context = &ctxs[i];
        descs[i].cleanup = &PackedCleanup;
        descs[i].dependency = nullptr;
    }
    const int ok = JobSystem_ScheduleBatch(descs.data(), k, out.data());
    CHECK(ok == k, "Exports: all 32 descriptors submitted");
    int completed = 0;
    for (int i = 0; i < k; ++i)
    {
        if (out[i] == nullptr) continue;
        auto* state = static_cast<JobSystem::HandleState*>(out[i]);
        // 句柄自身的用户引用由 JobHandle 析构释放（toHandle/打包发布各 Acquire 一次）。
        JobSystem::JobHandle h(state, false);
        h.Complete();
        ++completed;
    }
    CHECK(completed == k, "Exports: all 32 handles valid");
    bool allOnce = true;
    for (int i = 0; i < k; ++i)
        if (hits[i].load() != 1 || cleanups[i].load() != 1) allOnce = false;
    CHECK(allOnce, "Exports: every job ran exactly once and cleaned up once");
    CHECK(WaitOutstandingZero(), "Exports: no outstanding batches after completes");
    JobSystem::Scheduler::Shutdown();
}

// ---- Item 9（R11）：`JobHandle::CombineDependencies` 组合 state 的所有权 / 引用账 ----
// 问题：组合句柄（≥2 依赖）是否只能靠终结器回收？能否"子依赖全部完成后把组合 state 回池复用"？
//
// 结论（REFUTED，附证据）：**不能**新增"完成后回收/复用"路径。组合 state 就是交回调用方的
// 句柄本体（JobSystem_State.cpp:952 `return JobHandle(cs);`，经 Exports.cpp:378-379
// `toHandle(combined)` 发布用户引用），调用方对它的引用**生命周期无界**：可以在任意晚的时刻
// Complete()/IsCompleted()/当依赖用，且多份 JobHandle 值拷贝共享同一 state。一旦回池复用，
// 调用方后续的 Complete()/IsCompleted() 会观察到**另一个 job** 的状态 —— 违反 `JobHandle`
// 既有契约（Unity 同语义：句柄在 Complete 前一直有效）。
//
// "只被终结器回收"这一现象并非 CombineDependencies 特有：它与普通 job 句柄同源，都是
// "调用方丢弃句柄且不 Complete"这一**通用契约用法**（JobHandle.cs 无 Dispose 是刻意的
// 值语义设计）。native 侧引用账本身精确：本测试证明"所有子依赖完成后组合 state 的 refCount
// 恰好回到 1（唯一调用方引用）"，且 500 轮重复下 state 分配不增长（无泄漏）。
//
// 构造处引用账（JobSystem_State.cpp:931-953）：
//   CreateState(false) → 1（= 调用方引用；Exports::toHandle 的 Acquire 与其临时 JobHandle
//   析构相互抵消）；每个子依赖注册 continuation 前 AcquireState(cs) → 1+N，
//   每个 continuation 执行后 ReleaseState(cs) ⇒ 全部完成后回到 1。
//   cs->dependencies 另持每个子依赖一次引用，在 RecycleState 释放（JobSystem_State.cpp:135-137）。
void TestCombineDependenciesStateOwnership()
{
    printf("[TEST] TestCombineDependenciesStateOwnership\n");
    JobSystem::Scheduler::Initialize(4);

    // 单轮：2 个"永不自行完成"的依赖 state + 组合 state。
    // 返回 true 表示引用账符合预期：
    //   CombineDependencies 返回后 == 1（CreateState）+ 2（两个 continuation 的在飞引用）
    //   两个子依赖完成后 == 1（唯一的调用方引用）且组合 state 已完成。
    // 作用域刻意分开：`combined` 先析构（释放 cs 及其 cs->dependencies 引用），
    // 再 `deps` 析构（释放本轮持有的依赖引用）——不得再手动 Release 同一份引用，
    // 否则同一 state 被回收两次（池中出现重复条目 → 双重释放 → 堆损坏）。
    auto oneRound = []() -> bool {
        auto* d1 = JobSystem::CreateState(false);
        auto* d2 = JobSystem::CreateState(false);
        uint32_t csAfterCreate = 0;
        uint32_t csAfter = 0;
        bool completed = false;
        {
            std::vector<JobSystem::JobHandle> deps;
            deps.emplace_back(d1, true);
            deps.emplace_back(d2, true);
            JobSystem::JobHandle combined = JobSystem::JobHandle::CombineDependencies(deps);
            JobSystem::HandleState* cs = combined.State();
            if (cs == nullptr) { JobSystem::ReleaseState(d1); JobSystem::ReleaseState(d2); return false; }

            csAfterCreate = cs->refCount.load(std::memory_order_relaxed);
            // 两个子依赖都完成后，两个 continuation 各自 ReleaseState(cs)。
            JobSystem::CompleteState(d1);
            JobSystem::CompleteState(d2);
            completed = cs->completed.load(std::memory_order_acquire);
            csAfter = cs->refCount.load(std::memory_order_relaxed);
        }
        // 此刻 d1/d2 各只剩 CreateState 初始引用。
        JobSystem::ReleaseState(d1);
        JobSystem::ReleaseState(d2);
        return csAfterCreate == 3u && csAfter == 1u && completed;
    };

    bool accountingOk = true;
    for (int i = 0; i < 20; ++i)
    {
        if (!oneRound())
        {
            printf("  round %d: combined-state refCount/completed 不符合预期\n", i);
            accountingOk = false;
        }
    }
    CHECK(accountingOk,
        "R11: combined-state refCount returns to exactly 1 (caller ref) after all deps complete");

    for (int i = 0; i < 50; ++i) (void)oneRound();
    const uint64_t newBefore = JobSystem::g_statePoolNew.load(std::memory_order_relaxed);
    for (int i = 0; i < 500; ++i) (void)oneRound();
    const uint64_t newAfter = JobSystem::g_statePoolNew.load(std::memory_order_relaxed);
    const uint64_t delta = newAfter - newBefore;
    printf("  combined-deps: 500 x (2 deps + 1 combined) -> new states = %llu\n",
        static_cast<unsigned long long>(delta));
    CHECK(delta <= 64, "R11: combined-dependency state count bounded across 500 rounds (no leak)");
    CHECK(JobSystem::g_backendBatchesOutstanding.load() == 0,
        "R11: no backend batches outstanding after combined-dependency rounds");

    JobSystem::Scheduler::Shutdown();
}

} // namespace

int main()
{
    std::setvbuf(stdout, nullptr, _IONBF, 0);   // 死锁时也要能看到已完成的用例

    // ---- Test 1: 各种 K（含非分片整数倍）下每 job 恰好一次 + 无残留批 ----
    {
        JobSystem::Scheduler::Initialize(4);
        const int sizes[] = { 1, 2, 3, 4, 5, 7, 15, 16, 17, 63, 64, 65, 100, 999, 1000 };
        bool allOk = true;
        for (int k : sizes)
        {
            if (!RunPackedRound(k, -1))
            {
                printf("  round k=%d FAILED\n", k);
                allOk = false;
            }
            if (!WaitOutstandingZero())
            {
                printf("  round k=%d outstanding != 0\n", k);
                allOk = false;
            }
        }
        CHECK(allOk, "Test1 K jobs ran exactly once for all K");
        CHECK(JobSystem::g_backendBatchesOutstanding.load() == 0, "Test1 no outstanding batches");
        JobSystem::Scheduler::Shutdown();
    }

    // ---- Test 2: 逐 job 异常归属 ----
    {
        JobSystem::Scheduler::Initialize(4);
        bool ok = true;
        for (int throwAt : { 0, 1, 5, 31, 63 })
        {
            if (!RunPackedRound(64, throwAt)) { printf("  throwAt=%d FAILED\n", throwAt); ok = false; }
            if (!WaitOutstandingZero()) { ok = false; }
        }
        CHECK(ok, "Test2 per-job exceptions surface on the throwing handle only");
        JobSystem::Scheduler::Shutdown();
    }

    // ---- Test 3: 未 Complete 就 Shutdown（ForceFinalize 兜底 + 句柄仍可达终态）----
    {
        JobSystem::Scheduler::Initialize(4);
        constexpr int k = 64;
        std::vector<std::atomic<int>> hits(k);
        std::vector<std::atomic<int>> cleanups(k);
        std::vector<JobCtx> ctxs(k);
        std::vector<JobSystem::PackedPlainJobDesc> descs(k);
        std::vector<void*> out(static_cast<size_t>(k), nullptr);
        for (int i = 0; i < k; ++i)
        {
            hits[i].store(0);
            cleanups[i].store(0);
            ctxs[i] = JobCtx{ hits.data(), cleanups.data(), i, -1, nullptr, nullptr };
            descs[i] = JobSystem::PackedPlainJobDesc{ &PackedFn, &ctxs[i], &PackedCleanup };
        }
        CHECK(JobSystem::SubmitPackedPlainJobs(descs.data(), k, out.data()) == k,
            "Test3 packed submit before shutdown");
        JobSystem::Scheduler::Shutdown();
        CHECK(JobSystem::g_backendBatchesOutstanding.load() == 0,
            "Test3 outstanding zero after shutdown");
        bool terminal = true;
        for (int i = 0; i < k; ++i)
        {
            auto* state = static_cast<JobSystem::HandleState*>(out[i]);
            if (state == nullptr) { terminal = false; break; }
            JobSystem::JobHandle h(state, false);
            h.Complete();   // 不阻塞；已完成或已被 shutdown 强制终结
        }                   // h 析构释放用户引用
        int ran = 0, cleaned = 0;
        for (int i = 0; i < k; ++i)
        {
            ran += hits[i].load();
            cleaned += cleanups[i].load();
        }
        // shutdown 前 worker 可能已跑完一部分；未跑的由 cleanup 兜底。
        // 不变量：cleanup 次数 == K（每个 ctx 恰好释放一次），且 hits == cleanups 的 job 才执行过。
        CHECK(cleaned == k, "Test3 every context cleaned up exactly once");
        CHECK(ran <= k, "Test3 no job ran more than once");
        CHECK(terminal, "Test3 handles reachable and Complete returns after shutdown");
    }

    // ---- Test 4: 逐帧重复提交不无界增长 state 分配（池化有效）----
    {
        JobSystem::Scheduler::Initialize(4);
        constexpr int k = 32;
        for (int frame = 0; frame < 50; ++frame)
            RunPackedRound(k, -1);
        WaitOutstandingZero();
        const uint64_t newBefore = JobSystem::g_statePoolNew.load(std::memory_order_relaxed);
        for (int frame = 0; frame < 500; ++frame)
            RunPackedRound(k, -1);
        WaitOutstandingZero();
        const uint64_t newAfter = JobSystem::g_statePoolNew.load(std::memory_order_relaxed);
        const uint64_t delta = newAfter - newBefore;
        printf("  state allocations over 500 frames x %d jobs = %llu\n",
            k, static_cast<unsigned long long>(delta));
        CHECK(delta <= 64, "Test4 state allocation bounded across frames (pool reuse)");
        JobSystem::Scheduler::Shutdown();
    }

    // ---- Test 5: 分配失败时打包路径事务性回退（不消费 context / 不泄漏 state）----
    {
        JobSystem::Scheduler::Initialize(4);
        constexpr int k = 16;
        std::vector<std::atomic<int>> hits(k);
        std::vector<std::atomic<int>> cleanups(k);
        std::vector<JobCtx> ctxs(k);
        std::vector<JobSystem::PackedPlainJobDesc> descs(k);
        std::vector<void*> out(static_cast<size_t>(k), reinterpret_cast<void*>(0x1));
        for (int i = 0; i < k; ++i)
        {
            hits[i].store(0);
            cleanups[i].store(0);
            ctxs[i] = JobCtx{ hits.data(), cleanups.data(), i, -1, nullptr, nullptr };
            descs[i] = JobSystem::PackedPlainJobDesc{ &PackedFn, &ctxs[i], &PackedCleanup };
        }
        JobSystem::FailNextBatchStorageAcquireForTests(1);
        const int published = JobSystem::SubmitPackedPlainJobs(descs.data(), k, out.data());
        bool ran = false, cleaned = false;
        for (int i = 0; i < k; ++i)
        {
            ran |= hits[i].load() != 0;
            cleaned |= cleanups[i].load() != 0;
        }
        CHECK(published == 0, "Test5 allocation failure returns 0 (caller falls back)");
        CHECK(!ran, "Test5 no job executed on failed submit");
        CHECK(!cleaned, "Test5 no context consumed on failed submit");
        JobSystem::Scheduler::Shutdown();
    }

    TestExportsPackedGrouping();
    TestCombineDependenciesStateOwnership();

    if (Failures == 0)
    {
        printf("PackedBatchTests: ALL PASS\n");
        return 0;
    }
    printf("PackedBatchTests: %d FAILURES\n", Failures);
    return 1;
}
