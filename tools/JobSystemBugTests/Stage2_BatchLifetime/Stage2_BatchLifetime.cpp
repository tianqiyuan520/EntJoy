#include "JobSystem.h"
#include "JobSystemInternal.h"
#include <atomic>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>

namespace {
void Require(bool ok, const char* msg) { if (!ok) throw std::runtime_error(msg); }
struct Ctx { std::atomic<int>* ran; std::atomic<int>* cleaned; };
void Work(void* p, int, int) { static_cast<Ctx*>(p)->ran->fetch_add(1, std::memory_order_relaxed); }
void Cleanup(void* p) { static_cast<Ctx*>(p)->cleaned->fetch_add(1, std::memory_order_relaxed); delete static_cast<Ctx*>(p); }

void PendingFlushRetainsState() {
    JobSystem::Scheduler::Initialize(2);
    JobSystem::g_implicitBatchEnabled.store(true, std::memory_order_release);
    std::atomic<int> ran{0}, cleaned{0};
    std::vector<JobSystem::JobHandle> handles;
    handles.reserve(128);
    for (int i = 0; i < 128; ++i) {
        auto* ctx = new Ctx{&ran, &cleaned};
        handles.push_back(JobSystem::Scheduler::ScheduleParallelForBatch(&Work, ctx, 32, 4, &Cleanup));
    }
    JobSystem::FlushPendingSubmits();
    for (auto& h : handles) h.Complete();
    Require(ran.load() == 128 * 8, "pending batches lost work");
    Require(cleaned.load() == 128, "pending batch cleanup count mismatch");
    JobSystem::g_implicitBatchEnabled.store(false, std::memory_order_release);
    JobSystem::Scheduler::Shutdown();
}

void ShutdownFinalizesResiduals() {
    JobSystem::Scheduler::Initialize(1);
    std::atomic<int> ran{0}, cleaned{0};
    std::vector<JobSystem::JobHandle> handles;
    for (int i = 0; i < 64; ++i) {
        auto* ctx = new Ctx{&ran, &cleaned};
        handles.push_back(JobSystem::Scheduler::ScheduleParallelForBatch(&Work, ctx, 64, 1, &Cleanup));
    }
    JobSystem::Scheduler::Shutdown();
    for (auto& h : handles) {
        h.Complete();
        Require(h.IsCompleted(), "shutdown left handle non-terminal");
    }
    Require(cleaned.load() == 64, "shutdown cleanup was not exactly once");
}

void ConcurrentFlushAndCompletion() {
    JobSystem::Scheduler::Initialize(4);
    JobSystem::g_implicitBatchEnabled.store(true, std::memory_order_release);
    std::atomic<int> ran{0}, cleaned{0};
    std::vector<JobSystem::JobHandle> handles;
    handles.reserve(256);
    for (int i = 0; i < 256; ++i) {
        auto* ctx = new Ctx{&ran, &cleaned};
        handles.push_back(JobSystem::Scheduler::ScheduleParallelForBatch(&Work, ctx, 16, 1, &Cleanup));
    }
    std::atomic<bool> stop{false};
    std::thread flusher([&] {
        while (!stop.load(std::memory_order_relaxed)) JobSystem::FlushPendingSubmits();
    });
    for (auto& h : handles) h.Complete();
    stop.store(true, std::memory_order_release);
    flusher.join();
    JobSystem::g_implicitBatchEnabled.store(false, std::memory_order_release);
    Require(cleaned.load() == 256, "concurrent flush cleanup mismatch");
    JobSystem::Scheduler::Shutdown();
}
}

int main() {
    try {
        PendingFlushRetainsState();
        std::cout << "PASS PendingFlushRetainsState\n";
        ShutdownFinalizesResiduals();
        std::cout << "PASS ShutdownFinalizesResiduals\nPASS Stage2: 2/2\n";
        ConcurrentFlushAndCompletion();
        std::cout << "PASS ConcurrentFlushAndCompletion\nPASS Stage2: 3/3\n";
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "FAIL Stage2: " << ex.what() << "\n";
        if (JobSystem::g_chaseLevScheduler) JobSystem::Scheduler::Shutdown();
        return 1;
    }
}
