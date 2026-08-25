#include "../NativeDll/SparseTileDeque.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <future>
#include <iostream>
#include <numeric>
#include <stdexcept>
#include <thread>
#include <vector>

namespace
{
    constexpr int kTimeoutSec = 30;

    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    template <typename Fn>
    void RunWithTimeout(const char* name, Fn&& fn)
    {
        std::cout << "[START] " << name << std::endl << std::flush;
        auto future = std::async(std::launch::async, std::forward<Fn>(fn));
        auto status = future.wait_for(std::chrono::seconds(kTimeoutSec));
        if (status == std::future_status::timeout)
        {
            std::cerr << "[DEADLOCK] " << name
                      << " timed out after " << kTimeoutSec << "s — aborting" << std::endl;
            std::abort();
        }
        future.get();
        std::cout << "[DONE]  " << name << std::endl << std::flush;
    }

    // ================================================================
    // Test 1: 单线程 PushBottom / PopBottom LIFO
    // ================================================================
    void TestSingleThreadPushPop()
    {
        JobSystem::SparseTileDeque dq(8);

        for (uint32_t i = 0; i < 8; ++i)
            dq.PushBottom(JobSystem::TileTask{ nullptr, i, 1 });

        Require(!dq.IsEmpty(), "deque should not be empty after 8 pushes");

        for (uint32_t expected = 7; ; --expected)
        {
            JobSystem::TileTask t;
            Require(dq.PopBottom(t), "PopBottom should succeed");
            Require(t.firstTile == expected, "LIFO order");
            if (expected == 0) break;
        }

        Require(dq.IsEmpty(), "deque should be empty");
        JobSystem::TileTask dummy;
        Require(!dq.PopBottom(dummy), "PopBottom on empty returns false");
    }

    // ================================================================
    // Test 2: 单线程交替 push/pop
    // ================================================================
    void TestSingleThreadPushPopAlternate()
    {
        JobSystem::SparseTileDeque dq(4);

        dq.PushBottom(JobSystem::TileTask{ nullptr, 0, 1 });
        dq.PushBottom(JobSystem::TileTask{ nullptr, 1, 1 });
        dq.PushBottom(JobSystem::TileTask{ nullptr, 2, 1 });

        JobSystem::TileTask t;
        Require(dq.PopBottom(t) && t.firstTile == 2, "LIFO: pop 2");
        dq.PushBottom(JobSystem::TileTask{ nullptr, 3, 1 });
        Require(dq.PopBottom(t) && t.firstTile == 3, "LIFO: pop 3");
        Require(dq.PopBottom(t) && t.firstTile == 1, "LIFO: pop 1");
        Require(dq.PopBottom(t) && t.firstTile == 0, "LIFO: pop 0");
        Require(dq.IsEmpty(), "empty after all pops");
    }

    // ================================================================
    // Test 3: 多线程 owner pop + thief steal
    // ================================================================
    void TestMultiThreadSteal()
    {
        constexpr uint32_t N = 1024;
        constexpr int kThiefCount = 4;

        JobSystem::SparseTileDeque dq(N);
        std::atomic<uint32_t> stolenCount{ 0 };
        std::atomic<uint32_t> ownerCount{ 0 };

        for (uint32_t i = 0; i < N; ++i)
            dq.PushBottom(JobSystem::TileTask{ nullptr, i, 1 });

        std::array<std::thread, kThiefCount> thieves;
        for (int t = 0; t < kThiefCount; ++t)
        {
            thieves[t] = std::thread([&]()
            {
                JobSystem::TileTask task;
                while (dq.StealTop(task))
                    stolenCount.fetch_add(1, std::memory_order_relaxed);
            });
        }

        JobSystem::TileTask task;
        while (dq.PopBottom(task))
            ownerCount.fetch_add(1, std::memory_order_relaxed);

        for (auto& t : thieves) t.join();

        Require(ownerCount.load() + stolenCount.load() == N, "all tiles consumed");
        Require(dq.IsEmpty(), "deque should be empty");
    }

    // ================================================================
    // Test 4: 多线程竞争最后一个元素
    // ================================================================
    void TestLastElementRace()
    {
        constexpr uint32_t N = 4096;

        JobSystem::SparseTileDeque dq(N);
        for (uint32_t i = 0; i < N; ++i)
            dq.PushBottom(JobSystem::TileTask{ nullptr, i, 1 });

        constexpr int kWorkerCount = 4;
        std::atomic<uint32_t> totalExecuted{ 0 };

        std::array<std::thread, kWorkerCount> workers;

        workers[0] = std::thread([&]()
        {
            JobSystem::TileTask t;
            while (dq.PopBottom(t))
                totalExecuted.fetch_add(1, std::memory_order_relaxed);
        });

        for (int i = 1; i < kWorkerCount; ++i)
        {
            workers[i] = std::thread([&]()
            {
                JobSystem::TileTask t;
                while (dq.StealTop(t))
                    totalExecuted.fetch_add(1, std::memory_order_relaxed);
            });
        }

        for (auto& w : workers) w.join();

        Require(totalExecuted.load() == N, "all tiles consumed (last-element CAS)");
    }

    // ================================================================
    // Test 5: 两线程并发 push/pop/steal（ABA 压力）
    // ================================================================
    void TestHighFrequencyAbaStress()
    {
        constexpr uint32_t kTotalTiles = 16384;

        JobSystem::SparseTileDeque dq(2048);
        std::atomic<uint32_t> totalExecuted{ 0 };

        std::thread owner([&]()
        {
            constexpr uint32_t kPushPerRound = 64;
            uint32_t pushed = 0;
            while (pushed < kTotalTiles)
            {
                uint32_t batch = std::min(kPushPerRound, kTotalTiles - pushed);
                for (uint32_t i = 0; i < batch; ++i)
                    dq.PushBottom(JobSystem::TileTask{ nullptr, pushed + i, 1 });
                pushed += batch;

                JobSystem::TileTask t;
                while (dq.PopBottom(t))
                    totalExecuted.fetch_add(1, std::memory_order_relaxed);
            }
        });

        std::thread thief([&]()
        {
            JobSystem::TileTask t;
            while (totalExecuted.load(std::memory_order_acquire) < kTotalTiles)
            {
                if (dq.StealTop(t))
                    totalExecuted.fetch_add(1, std::memory_order_relaxed);
                else
                    std::this_thread::yield();
            }
        });

        owner.join();
        thief.join();

        Require(totalExecuted.load() == kTotalTiles, "all tiles consumed");
    }

    // ================================================================
    // Test 6: Capacity 对齐验证
    // ================================================================
    void TestCapacityAlignment()
    {
        JobSystem::SparseTileDeque dq5(5);
        Require(dq5.Capacity() >= 8, "capacity rounded up");

        JobSystem::SparseTileDeque dq1(1);
        Require(dq1.Capacity() >= 8, "minimal capacity is 8");
    }

    // ================================================================
    // Test 7: 多轮 steal 覆盖
    // ================================================================
    void TestConcurrentStealRounds()
    {
        constexpr uint32_t N = 4096;
        constexpr int kThiefCount = 7;
        constexpr uint32_t kPushRoundSize = 64;

        JobSystem::SparseTileDeque dq(N);
        std::atomic<uint32_t> totalExecuted{ 0 };
        std::atomic<bool> pushDone{ false };

        std::thread owner([&]()
        {
            uint32_t pushed = 0;
            while (pushed < N)
            {
                uint32_t batch = std::min(kPushRoundSize, N - pushed);
                for (uint32_t i = 0; i < batch; ++i)
                    dq.PushBottom(JobSystem::TileTask{ nullptr, pushed + i, 1 });
                pushed += batch;

                JobSystem::TileTask t;
                while (dq.PopBottom(t))
                    totalExecuted.fetch_add(1, std::memory_order_relaxed);
            }
            pushDone.store(true, std::memory_order_release);
        });

        std::vector<std::thread> thieves;
        for (int t = 0; t < kThiefCount; ++t)
        {
            thieves.emplace_back([&]()
            {
                JobSystem::TileTask task;
                while (true)
                {
                    if (dq.StealTop(task))
                    {
                        totalExecuted.fetch_add(1, std::memory_order_relaxed);
                    }
                    else if (pushDone.load(std::memory_order_acquire) && dq.IsEmpty())
                    {
                        break;
                    }
                    else
                    {
                        std::this_thread::yield();
                    }
                }
            });
        }

        owner.join();
        for (auto& t : thieves) t.join();

        Require(totalExecuted.load() == N, "all tiles consumed");
    }
}

int main()
{
    try
    {
        RunWithTimeout("TestSingleThreadPushPop", TestSingleThreadPushPop);
        RunWithTimeout("TestSingleThreadPushPopAlternate", TestSingleThreadPushPopAlternate);
        RunWithTimeout("TestMultiThreadSteal", TestMultiThreadSteal);
        RunWithTimeout("TestLastElementRace", TestLastElementRace);
        RunWithTimeout("TestHighFrequencyAbaStress", TestHighFrequencyAbaStress);
        RunWithTimeout("TestCapacityAlignment", TestCapacityAlignment);
        RunWithTimeout("TestConcurrentStealRounds", TestConcurrentStealRounds);

        std::cout << "PASS SparseTileDeque\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL " << error.what() << '\n';
        return 1;
    }
}
