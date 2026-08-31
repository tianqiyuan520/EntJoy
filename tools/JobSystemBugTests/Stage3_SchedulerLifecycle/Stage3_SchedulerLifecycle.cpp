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
void Work(void* p, int) { static_cast<std::atomic<int>*>(p)->fetch_add(1, std::memory_order_relaxed); }

void ReinitializeManyTimes() {
    for (int i = 0; i < 1000; ++i) {
        JobSystem::Scheduler::Initialize(2);
        std::atomic<int> ran{0};
        auto h = JobSystem::Scheduler::ScheduleParallelFor(&Work, &ran, 32, 1);
        h.Complete();
        Require(ran.load() == 32, "job lost during reinitialization");
        JobSystem::Scheduler::Shutdown();
        JobSystem::Scheduler::Shutdown();
    }
}

void CompleteAndShutdownConcurrently() {
    JobSystem::Scheduler::Initialize(4);
    std::atomic<int> ran{0};
    std::vector<JobSystem::JobHandle> handles;
    for (int i = 0; i < 128; ++i)
        handles.push_back(JobSystem::Scheduler::ScheduleParallelFor(&Work, &ran, 64, 1));
    std::vector<std::thread> waiters;
    for (auto& h : handles)
        waiters.emplace_back([&h] { h.Complete(); });
    JobSystem::Scheduler::Shutdown();
    for (auto& t : waiters) t.join();
    Require(ran.load() == 128 * 64, "concurrent Complete/Shutdown lost work");
}

void ScheduleWhileShutdownGateCloses() {
    JobSystem::Scheduler::Initialize(2);
    std::atomic<int> ran{0};
    std::atomic<bool> stop{false};
    std::thread producer([&] {
        while (!stop.load(std::memory_order_relaxed)) {
            auto h = JobSystem::Scheduler::ScheduleParallelFor(&Work, &ran, 8, 1);
            h.Complete();
        }
    });
    std::this_thread::sleep_for(std::chrono::milliseconds(10));
    JobSystem::Scheduler::Shutdown();
    stop.store(true, std::memory_order_release);
    producer.join();
    JobSystem::Scheduler::Shutdown();
}
}

int main() {
    try {
        ReinitializeManyTimes();
        std::cout << "PASS ReinitializeManyTimes\n";
        CompleteAndShutdownConcurrently();
        std::cout << "PASS CompleteAndShutdownConcurrently\n";
        ScheduleWhileShutdownGateCloses();
        std::cout << "PASS ScheduleWhileShutdownGateCloses\nPASS Stage3: 3/3\n";
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "FAIL Stage3: " << ex.what() << "\n";
        return 1;
    }
}
