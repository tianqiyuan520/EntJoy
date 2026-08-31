#include "JobSystem.h"
#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"

#include <atomic>
#include <chrono>
#include <iostream>
#include <mutex>
#include <thread>

namespace {
using namespace std::chrono_literals;

void Count(void* raw, int) {
    static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed);
}

// 隐式批开关切换与 pending 入队的竞态：scheduler 线程持续 Schedule，
// 主线程反复「开启 → 锁内置 false → flush」。修复前 SubmitOrPending 在锁外读
// 开关，可能在读 true 后、入队前开关被关闭且 flush 空，把 batch 留在 pending；
// 修复后锁内复核开关，flush 后 pending 必为空。
int TestToggleLeavesNoStrandedBatch() {
    JobSystem::Scheduler::Initialize(2);
    std::atomic<bool> stop{false};
    std::atomic<int> ran{0};
    std::atomic<int> scheduled{0};
    std::atomic<int> stranded{0};

    std::thread scheduler([&] {
        while (!stop.load(std::memory_order_acquire)) {
            JobSystem::Scheduler::ScheduleParallelFor(&Count, &ran, 256, 1, nullptr);
            scheduled.fetch_add(1, std::memory_order_relaxed);
        }
    });

    for (int round = 0; round < 100; ++round) {
        JobSystem::g_implicitBatchEnabled.store(true, std::memory_order_relaxed);
        std::this_thread::sleep_for(1ms);
        // 与 JobSystem_SetImplicitBatchEnabled(0) 相同的关闭顺序：锁内置 false 再 flush。
        {
            std::lock_guard<std::mutex> lock(JobSystem::g_pendingBatchesMutex);
            JobSystem::g_implicitBatchEnabled.store(false, std::memory_order_relaxed);
        }
        JobSystem::FlushPendingSubmits();
        {
            std::lock_guard<std::mutex> lock(JobSystem::g_pendingBatchesMutex);
            if (!JobSystem::g_pendingBatches.empty())
                stranded.fetch_add(1, std::memory_order_relaxed);
        }
    }

    stop.store(true, std::memory_order_release);
    scheduler.join();

    JobSystem::FlushPendingSubmits();
    JobSystem::Scheduler::Shutdown();

    std::cout << "  (scheduled " << scheduled.load() << ", executed " << ran.load()
              << ", stranded-rounds " << stranded.load() << ")\n";
    if (stranded.load() != 0) {
        std::cerr << "FAIL: pending queue not empty after flush in "
                  << stranded.load() << " rounds\n";
        return 1;
    }
    if (ran.load() == 0) {
        std::cerr << "FAIL: no job executed\n";
        return 1;
    }
    return 0;
}
} // namespace

int main() {
    try {
        if (TestToggleLeavesNoStrandedBatch() != 0) return 1;
        std::cout << "PASS Stage10 ToggleLeavesNoStrandedBatch\n";
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "FAIL Stage10: " << ex.what() << "\n";
        return 1;
    }
}
