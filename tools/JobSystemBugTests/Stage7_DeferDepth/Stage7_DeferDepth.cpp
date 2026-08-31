#include "JobSystem.h"
#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <future>
#include <iostream>
#include <thread>

namespace {
using namespace std::chrono_literals;

void Count(void* raw, int) {
    static_cast<std::atomic<int>*>(raw)->fetch_add(1, std::memory_order_relaxed);
}

// defer depth 下溢：失配的 SubmitDeferFlush 会让 g_submitDeferDepth 变负。
// 修复前 ChaseLevScheduler::SubmitBatch 只在 depth==0 时唤醒，负 depth 会
// 跳过唤醒；此时 worker 已 park，批量任务无人执行，Complete 永久阻塞。
int TestDeferDepthUnderflowDoesNotStrandWork() {
    JobSystem::Scheduler::Initialize(1);
    // 等 worker 自旋耗尽、进入 park（wakeEpoch.wait），确保它只能靠 notify 唤醒。
    std::this_thread::sleep_for(400ms);

    // 模拟下溢后的深度状态。修复后 SubmitBatch 对 depth<=0 也会广播唤醒。
    JobSystem::g_submitDeferDepth.store(-1, std::memory_order_relaxed);

    std::atomic<int> ran{0};
    auto handle = JobSystem::Scheduler::ScheduleParallelFor(
        &Count, &ran, 1024, 1, nullptr);

    auto fut = std::async(std::launch::async, [&] { handle.Complete(); });
    if (fut.wait_for(3s) != std::future_status::ready) {
        std::cerr << "FAIL: defer-depth underflow stranded the batch (Complete hung)\n";
        JobSystem::Scheduler::Shutdown();
        std::_Exit(2);
    }
    fut.get();
    JobSystem::Scheduler::Shutdown();

    if (ran.load(std::memory_order_acquire) != 1024) {
        std::cerr << "FAIL: defer-depth underflow executed " << ran.load()
                  << "/1024 tiles\n";
        return 1;
    }
    return 0;
}
} // namespace

int main() {
    try {
        if (TestDeferDepthUnderflowDoesNotStrandWork() != 0)
            return 1;
        std::cout << "PASS Stage7a DeferDepthUnderflow\n";
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "FAIL Stage7a: " << ex.what() << "\n";
        return 1;
    }
}
