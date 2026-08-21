#include "../NativeDll/SparseTileDeque.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <future>
#include <iostream>
#include <stdexcept>
#include <thread>

namespace
{
    // 30s 超时：每个测试用 std::async 跑，超时直接 abort
    constexpr int kTimeoutSec = 30;

    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    // 运行 fn，超时 kTimeoutSec 秒则 abort
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
        future.get(); // propagate exception
        std::cout << "[DONE]  " << name << std::endl << std::flush;
    }

    void TestSingleThread()
    {
        std::cout << "  push 0..7..." << std::flush;
        JobSystem::SparseTileDeque dq(8);
        for (uint32_t i = 0; i < 8; ++i)
            dq.PushBottom(JobSystem::TileTask{ nullptr, i, 1 });
        std::cout << " done" << std::endl;

        Require(!dq.IsEmpty(), "not empty after push");

        std::cout << "  pop LIFO..." << std::flush;
        for (uint32_t expected = 7; ; --expected)
        {
            JobSystem::TileTask t;
            bool ok = dq.PopBottom(t);
            if (!ok)
            {
                std::cerr << "\n  PopBottom FAILED at expected=" << expected << std::endl;
                throw std::runtime_error("PopBottom returned false prematurely");
            }
            if (t.firstTile != expected)
            {
                std::cerr << "\n  tile=" << t.firstTile << " expected=" << expected << std::endl;
                throw std::runtime_error("wrong tile");
            }
            if (expected == 0) break;
        }
        std::cout << " done" << std::endl;

        Require(dq.IsEmpty(), "empty after all pops");

        JobSystem::TileTask dummy;
        Require(!dq.PopBottom(dummy), "PopBottom on empty returns false");
    }

    void TestTwoThread()
    {
        std::cout << "  push 0..4095..." << std::flush;
        constexpr uint32_t N = 4096;
        JobSystem::SparseTileDeque dq(N);
        for (uint32_t i = 0; i < N; ++i)
            dq.PushBottom(JobSystem::TileTask{ nullptr, i, 1 });
        std::cout << " done" << std::endl;

        std::atomic<uint32_t> count{ 0 };

        // owner pop
        std::thread owner([&]()
        {
            JobSystem::TileTask t;
            while (dq.PopBottom(t))
                count.fetch_add(1, std::memory_order_relaxed);
            std::cout << "  owner done" << std::endl << std::flush;
        });

        // thief steal
        std::thread thief([&]()
        {
            JobSystem::TileTask t;
            while (dq.StealTop(t))
                count.fetch_add(1, std::memory_order_relaxed);
            std::cout << "  thief done" << std::endl << std::flush;
        });

        owner.join();
        thief.join();

        std::cout << "  count=" << count.load() << " N=" << N << std::endl;
        Require(count.load() == N, "all tiles consumed");
    }
}

int main()
{
    try
    {
        RunWithTimeout("TestSingleThread", TestSingleThread);
        RunWithTimeout("TestTwoThread", TestTwoThread);

        std::cout << "PASS SparseTileDeque" << std::endl;
        return 0;
    }
    catch (const std::exception& e)
    {
        std::cerr << "FAIL " << e.what() << std::endl;
        return 1;
    }
}
