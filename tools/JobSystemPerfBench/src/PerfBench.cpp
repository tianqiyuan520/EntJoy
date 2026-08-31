// ============================================================================
// JobSystemPerfBench — EntJoy JobSystem 性能微基准
//
// 测量三项关键指标（供阶段 0 基线对比，门槛见 docs/优化Jobsystem.md §9）：
//   1. Schedule+Complete 吞吐（空 IJob，ops/s）
//   2. Schedule 延迟（P50/P95/P99，ns）
//   3. ParallelFor 吞吐（elem/s）
//
// 构建：
//   cmake -S tools/JobSystemPerfBench -B tools/JobSystemPerfBench/build
//   cmake --build tools/JobSystemPerfBench/build --config Release
//   tools/JobSystemPerfBench/build/Release/JobSystemPerfBench.exe
// ============================================================================

#include "JobSystem.h"
#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <vector>

namespace {
using Clock = std::chrono::steady_clock;

double NowNs()
{
    using namespace std::chrono;
    return duration_cast<duration<double, std::nano>>(
        Clock::now().time_since_epoch()).count();
}

void Noop(void*) {}

void ParallelForInc(void* raw, int)
{
    static_cast<std::atomic<uint64_t>*>(raw)->fetch_add(1, std::memory_order_relaxed);
}

void BenchmarkScheduleThroughput()
{
    constexpr int N = 1'000'000;
    auto t0 = Clock::now();
    for (int i = 0; i < N; ++i)
    {
        auto h = JobSystem::Scheduler::Schedule(&Noop, nullptr);
        h.Complete();
    }
    double sec = std::chrono::duration<double>(Clock::now() - t0).count();
    std::printf("Schedule+Complete throughput : %.3f M ops/s (N=%d, %.3fs)\n",
        N / 1e6 / sec, N, sec);
}

void BenchmarkScheduleLatency()
{
    constexpr int N = 200'000;
    std::vector<double> lat(N);
    for (int i = 0; i < N; ++i)
    {
        double t0 = NowNs();
        auto h = JobSystem::Scheduler::Schedule(&Noop, nullptr);
        h.Complete();
        lat[i] = NowNs() - t0;
    }
    std::sort(lat.begin(), lat.end());
    std::printf("Schedule latency              : P50=%.0fns P95=%.0fns P99=%.0fns max=%.0fns\n",
        lat[N / 2], lat[static_cast<size_t>(N) * 95 / 100],
        lat[static_cast<size_t>(N) * 99 / 100], lat[N - 1]);
}

void BenchmarkParallelForThroughput()
{
    constexpr int N = 4'000'000;
    std::atomic<uint64_t> count{ 0 };
    auto t0 = Clock::now();
    auto h = JobSystem::Scheduler::ScheduleParallelFor(&ParallelForInc, &count, N, 0);
    h.Complete();
    double sec = std::chrono::duration<double>(Clock::now() - t0).count();
    std::printf("ParallelFor throughput        : %.1f M elem/s (N=%d, %.3fs)\n",
        N / 1e6 / sec, N, sec);
}
} // namespace

int main()
{
    JobSystem::Scheduler::Initialize(0);
    std::printf("Workers: %d\n", JobSystem::CurrentWorkerCount());
    BenchmarkScheduleThroughput();
    BenchmarkScheduleLatency();
    BenchmarkParallelForThroughput();
    JobSystem::Scheduler::Shutdown();
    return 0;
}
