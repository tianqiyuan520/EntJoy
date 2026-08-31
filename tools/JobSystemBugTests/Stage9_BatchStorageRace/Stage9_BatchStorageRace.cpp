#include "JobSystem.h"
#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"

#include <atomic>
#include <chrono>
#include <iostream>
#include <thread>
#include <vector>

namespace {
using namespace std::chrono_literals;

void Count(void* raw, int) {
    static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed);
}

// 每个 batch 的 storage 只被回收一次：无 double-free（计数偏多）也无泄漏（偏少）。
// length 足够大确保走 tile 批路径（每个 job 一个 BatchStorage）。
int TestStorageReturnedExactlyOnce() {
    JobSystem::Scheduler::Initialize(2);
    const uint64_t returned0 =
        JobSystem::g_batchStorageReturned.load(std::memory_order_relaxed);
    constexpr int N = 500;
    for (int i = 0; i < N; ++i) {
        std::atomic<int> ran{0};
        auto h = JobSystem::Scheduler::ScheduleParallelFor(&Count, &ran, 1024, 1, nullptr);
        h.Complete();
        if (ran.load(std::memory_order_acquire) != 1024) {
            std::cerr << "FAIL: iter " << i << " ran " << ran.load() << " != 1024\n";
            JobSystem::Scheduler::Shutdown();
            return 1;
        }
    }
    const uint64_t returned =
        JobSystem::g_batchStorageReturned.load(std::memory_order_relaxed);
    JobSystem::Scheduler::Shutdown();

    if (returned - returned0 != N) {
        std::cerr << "FAIL: storage returned " << (returned - returned0)
                  << " times, expected " << N << "\n";
        return 1;
    }
    return 0;
}

// 主线程 assist + 多 worker：让「最后 tile 完成者」与「最后 token 完成者」经常
// 落在不同线程。修复前两者并发进入 TryFinalizeChaseLevBatch 访问同一 batch 指针
// 存在 data race；修复后退役只由 pendingTasks 归零线程触发，稳定完成。
int TestAssistConcurrentCompletion() {
    JobSystem::Scheduler::Initialize(4);
    const bool prev = JobSystem::g_mainThreadAssistEnabled;
    JobSystem::g_mainThreadAssistEnabled = true;

    std::vector<std::thread> completers;
    std::atomic<bool> stop{false};
    std::atomic<int> jobsDone{0};
    for (int t = 0; t < 4; ++t) {
        completers.emplace_back([&] {
            while (!stop.load(std::memory_order_acquire)) {
                std::atomic<int> ran{0};
                auto h = JobSystem::Scheduler::ScheduleParallelFor(
                    &Count, &ran, 512, 1, nullptr);
                h.Complete();
                if (ran.load(std::memory_order_acquire) != 512) {
                    std::cerr << "FAIL: assist concurrent job incomplete\n";
                    std::_Exit(3);
                }
                jobsDone.fetch_add(1, std::memory_order_relaxed);
            }
        });
    }
    std::this_thread::sleep_for(1500ms);
    stop.store(true, std::memory_order_release);
    for (auto& t : completers) t.join();

    JobSystem::g_mainThreadAssistEnabled = prev;
    JobSystem::Scheduler::Shutdown();
    std::cout << "  (completed " << jobsDone.load() << " assisted batches)\n";
    return 0;
}
} // namespace

int main() {
    try {
        if (TestStorageReturnedExactlyOnce() != 0) return 1;
        std::cout << "PASS Stage9 StorageReturnedExactlyOnce\n";
        if (TestAssistConcurrentCompletion() != 0) return 1;
        std::cout << "PASS Stage9 AssistConcurrentCompletion\n";
        std::cout << "PASS Stage9: 2/2\n";
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "FAIL Stage9: " << ex.what() << "\n";
        return 1;
    }
}
