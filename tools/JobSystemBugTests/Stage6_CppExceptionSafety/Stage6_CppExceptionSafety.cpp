#include "JobSystem.h"
#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <future>
#include <iostream>
#include <process.h>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {
using namespace std::chrono_literals;

void Require(bool ok, const char* message) {
    if (!ok) throw std::runtime_error(message);
}

struct ThrowCtx {
    std::atomic<int>* executed;
    std::atomic<int>* cleaned;
};

std::atomic<int> g_batchCallbackDepth{0};
void TrackBatchId(uint64_t id) {
    if (id != 0)
        g_batchCallbackDepth.fetch_add(1, std::memory_order_relaxed);
    else
        g_batchCallbackDepth.fetch_sub(1, std::memory_order_relaxed);
}

void NoopJob(void*) {}

void ThrowFor(void* raw, int index) {
    auto& ctx = *static_cast<ThrowCtx*>(raw);
    ctx.executed->fetch_add(1, std::memory_order_relaxed);
    if (index == 7) throw std::runtime_error("schedule-for callback failure");
}

void ThrowBatch(void*, int, int) {
    throw std::runtime_error("batch callback failure");
}

void NoopBatch(void*, int, int) {}

void CountFor(void* raw, int) {
    static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed);
}

void ThrowAfterTwo(void* raw, int) {
    auto& ctx = *static_cast<ThrowCtx*>(raw);
    const int n = ctx.executed->fetch_add(1, std::memory_order_relaxed) + 1;
    if (n >= 2) throw std::runtime_error("many tile failures");
}

void ThrowCleanup(void* raw) {
    auto& ctx = *static_cast<ThrowCtx*>(raw);
    ctx.cleaned->fetch_add(1, std::memory_order_relaxed);
    throw std::runtime_error("cleanup failure");
}

void NoopCleanup(void* raw) {
    auto& ctx = *static_cast<ThrowCtx*>(raw);
    ctx.cleaned->fetch_add(1, std::memory_order_relaxed);
}

void ChildCleanupOnly(void* raw) {
    auto& ctx = *static_cast<ThrowCtx*>(raw);
    ctx.cleaned->fetch_add(1, std::memory_order_relaxed);
}

void DependencyChild(void* raw) {
    auto* cleaned = static_cast<std::atomic<int>*>(raw);
    if (cleaned->load(std::memory_order_acquire) != 1)
        throw std::runtime_error("dependency started before cleanup");
}

int ChildScheduleFor() {
    JobSystem::Scheduler::Initialize(2);
    std::atomic<int> executed{0}, cleaned{0};
    ThrowCtx ctx{&executed, &cleaned};
    auto handle = JobSystem::Scheduler::ScheduleFor(&ThrowFor, &ctx, 128, &NoopCleanup);
    auto waiter = std::async(std::launch::async, [&] {
        try { handle.Complete(); return false; }
        catch (const std::exception& ex) {
            std::cout << "caught ScheduleFor: " << ex.what() << "\n";
            return true;
        }
    });
    if (waiter.wait_for(2s) != std::future_status::ready) {
        std::cerr << "BASELINE_FAILURE ScheduleFor Complete timed out\n";
        std::_Exit(2);
    }
    bool caught = waiter.get();
    JobSystem::Scheduler::Shutdown();
    Require(caught, "ScheduleFor exception was not propagated");
    Require(cleaned.load() == 1, "ScheduleFor cleanup count mismatch");
    return 0;
}

int ChildFastPathCleanupException() {
    JobSystem::Scheduler::Initialize(2);
    std::atomic<int> executed{0}, cleaned{0};
    ThrowCtx ctx{&executed, &cleaned};
    auto handle = JobSystem::Scheduler::Schedule(&NoopJob, &ctx, &ThrowCleanup);
    auto waiter = std::async(std::launch::async, [&] {
        try { handle.Complete(); return false; }
        catch (const std::exception& ex) {
            std::cout << "caught fast cleanup: " << ex.what() << "\n";
            return true;
        }
    });
    if (waiter.wait_for(2s) != std::future_status::ready) {
        std::cerr << "BASELINE_FAILURE fast-path cleanup Complete timed out\n";
        std::_Exit(3);
    }
    bool caught = waiter.get();
    JobSystem::Scheduler::Shutdown();
    Require(caught, "fast-path cleanup exception was not propagated");
    Require(cleaned.load() == 1, "fast-path cleanup count mismatch");
    return 0;
}

int ChildCleanupException() {
    JobSystem::Scheduler::Initialize(2);
    std::atomic<int> executed{0}, cleaned{0};
    ThrowCtx ctx{&executed, &cleaned};
    auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
        &NoopBatch, &ctx, 2, 1, &ThrowCleanup);
    bool caught = false;
    try {
        handle.Complete();
    } catch (const std::exception& ex) {
        caught = true;
        std::cout << "caught cleanup/batch: " << ex.what() << "\n";
    }
    JobSystem::Scheduler::Shutdown();
    Require(caught, "cleanup exception was not propagated");
    Require(cleaned.load() == 1, "cleanup was not called exactly once");
    return 0;
}

int TestConcurrentCompleteException() {
    JobSystem::Scheduler::Initialize(4);
    std::atomic<int> executed{0}, cleaned{0};
    ThrowCtx ctx{&executed, &cleaned};
    auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
        &ThrowBatch, &ctx, 64, 1, &NoopCleanup);
    std::vector<std::thread> waiters;
    std::atomic<int> caught{0};
    for (int i = 0; i < 8; ++i) {
        waiters.emplace_back([&, handle]() mutable {
            try { handle.Complete(); }
            catch (...) { caught.fetch_add(1, std::memory_order_relaxed); }
        });
    }
    for (auto& t : waiters) t.join();
    Require(caught.load() >= 1, "concurrent Complete lost callback exception");
    JobSystem::Scheduler::Shutdown();
    return 0;
}

int TestManyTileExceptions() {
    JobSystem::Scheduler::Initialize(8);
    std::atomic<int> executed{0}, cleaned{0};
    ThrowCtx ctx{&executed, &cleaned};
    auto handle = JobSystem::Scheduler::ScheduleParallelFor(
        &ThrowAfterTwo, &ctx, 1000, 1, &NoopCleanup);
    bool caught = false;
    try { handle.Complete(); }
    catch (...) { caught = true; }
    Require(caught, "multi-tile exception was not propagated");
    Require(executed.load() == 1000, "multi-tile job did not drain all tiles");
    JobSystem::Scheduler::Shutdown();
    return 0;
}

int TestShutdownWithException() {
    JobSystem::Scheduler::Initialize(2);
    std::atomic<int> executed{0}, cleaned{0};
    ThrowCtx ctx{&executed, &cleaned};
    auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
        &ThrowBatch, &ctx, 4096, 1, &NoopCleanup);
    JobSystem::Scheduler::Shutdown();
    bool caught = false;
    try { handle.Complete(); }
    catch (...) { caught = true; }
    Require(caught, "shutdown discarded scheduled exception");
    Require(handle.IsCompleted(), "shutdown exception handle is not terminal");
    return 0;
}

int TestBackendRejectRunsCleanup() {
    JobSystem::Scheduler::Initialize(1);
    // Stop the backend while retaining the global object.  This models a
    // submission racing the lifecycle gate and exercises the rollback path.
    Require(JobSystem::g_chaseLevScheduler != nullptr, "scheduler was not created");
    JobSystem::g_chaseLevScheduler->Stop();
    std::atomic<int> executed{0}, cleaned{0};
    ThrowCtx ctx{&executed, &cleaned};
    auto handle = JobSystem::Scheduler::Schedule(&NoopJob, &ctx, &ChildCleanupOnly);
    bool rejected = false;
    try { handle.Complete(); }
    catch (const std::exception& ex) {
        rejected = std::string(ex.what()).find("backend") != std::string::npos;
    }
    Require(rejected, "backend rejection was not observable through Complete");
    Require(cleaned.load() == 1, "backend rejection leaked user context");
    JobSystem::Scheduler::Shutdown();
    return 0;
}

int TestCleanupPrecedesDependency() {
    JobSystem::Scheduler::Initialize(2);
    std::atomic<int> parentRan{0}, parentCleaned{0};
    ThrowCtx parent{&parentRan, &parentCleaned};
    auto parentHandle = JobSystem::Scheduler::ScheduleParallelForBatch(
        &NoopBatch, &parent, 64, 1, &ChildCleanupOnly);
    auto childHandle = JobSystem::Scheduler::Schedule(
        [](void* raw) { DependencyChild(raw); }, &parentCleaned, nullptr, parentHandle);
    parentHandle.Complete();
    childHandle.Complete();
    Require(parentCleaned.load() == 1, "parent cleanup count mismatch");
    JobSystem::Scheduler::Shutdown();
    return 0;
}

int TestBatchIdScopeCleared() {
    JobSystem::RegisterCurrentBatchIdCallback(&TrackBatchId);
    g_batchCallbackDepth.store(0, std::memory_order_relaxed);
    JobSystem::Scheduler::Initialize(2);
    std::atomic<int> ran{0};
    auto handle = JobSystem::Scheduler::ScheduleParallelFor(
        &CountFor, &ran, 256, 1, nullptr);
    handle.Complete();
    JobSystem::Scheduler::Shutdown();
    Require(g_batchCallbackDepth.load(std::memory_order_acquire) == 0,
        "current batch id leaked across worker job boundaries");
    return 0;
}

int RunChild(const char* self, const char* mode) {
    int rc = _spawnl(_P_WAIT, self, self, mode, nullptr);
    if (rc != 0) {
        std::cerr << "child " << mode << " exited " << rc << "\n";
        return 1;
    }
    return 0;
}
}

int main(int argc, char** argv) {
    try {
        if (argc > 1 && std::string(argv[1]) == "--schedulefor")
            return ChildScheduleFor();
        if (argc > 1 && std::string(argv[1]) == "--cleanup")
            return ChildCleanupException();
        if (argc > 1 && std::string(argv[1]) == "--fast-cleanup")
            return ChildFastPathCleanupException();

        Require(RunChild(argv[0], "--schedulefor") == 0,
            "ScheduleFor exception child failed");
        std::cout << "PASS ScheduleForException\n";
        Require(RunChild(argv[0], "--cleanup") == 0,
            "cleanup exception child failed");
        std::cout << "PASS CleanupException\n";
        Require(RunChild(argv[0], "--fast-cleanup") == 0,
            "fast-path cleanup exception child failed");
        std::cout << "PASS FastPathCleanupException\n";
        TestConcurrentCompleteException();
        std::cout << "PASS ConcurrentCompleteException\n";
        TestManyTileExceptions();
        std::cout << "PASS ManyTileExceptions\n";
        TestShutdownWithException();
        std::cout << "PASS ShutdownWithException\n";
        TestBackendRejectRunsCleanup();
        std::cout << "PASS BackendRejectRunsCleanup\n";
        TestCleanupPrecedesDependency();
        std::cout << "PASS CleanupPrecedesDependency\n";
        TestBatchIdScopeCleared();
        std::cout << "PASS BatchIdScopeCleared\n";
        std::cout << "PASS Stage6: 9/9\n";
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "FAIL Stage6: " << ex.what() << "\n";
        return 1;
    }
}
