#include "../NativeDll/SparseTileDeque.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>

int main()
{
    using namespace JobSystem;
    constexpr uint32_t kTotalTiles = 4096;

    std::cerr << "[1] creating deque..." << std::flush;
    SparseTileDeque dq(2048);
    std::cerr << " ok" << std::endl;

    std::vector<uint8_t> executed(kTotalTiles, 0);
    std::atomic<uint32_t> totalExecuted{ 0 };
    std::atomic<uint32_t> ownerPops{ 0 };
    std::atomic<uint32_t> thiefSteals{ 0 };

    std::cerr << "[2] creating owner thread..." << std::flush;
    std::thread owner([&]()
    {
        std::cerr << "  owner: started" << std::endl;
        constexpr uint32_t kPushPerRound = 64;
        uint32_t pushed = 0;
        while (pushed < kTotalTiles)
        {
            uint32_t batch = std::min(kPushPerRound, kTotalTiles - pushed);
            for (uint32_t i = 0; i < batch; ++i)
                dq.PushBottom(TileTask{ nullptr, pushed + i, 1 });
            pushed += batch;

            TileTask t;
            while (dq.PopBottom(t))
            {
                const uint32_t tile = t.firstTile;
                if (tile < kTotalTiles && executed[tile] != 0)
                    std::cerr << "  DUP tile=" << tile << " by owner prev=" << (int)executed[tile] << std::endl;
                if (tile < kTotalTiles) executed[tile] = 1;
                ownerPops.fetch_add(1, std::memory_order_relaxed);
                totalExecuted.fetch_add(1, std::memory_order_relaxed);
            }
        }
        std::cerr << "  owner: done pops=" << ownerPops.load() << std::endl;
    });
    std::cerr << " ok" << std::endl;

    std::cerr << "[3] creating thief thread..." << std::flush;
    std::thread thief([&]()
    {
        std::cerr << "  thief: started" << std::endl;
        TileTask t;
        while (totalExecuted.load(std::memory_order_acquire) < kTotalTiles)
        {
            if (dq.StealTop(t))
            {
                const uint32_t tile = t.firstTile;
                if (tile < kTotalTiles && executed[tile] != 0)
                    std::cerr << "  DUP tile=" << tile << " by thief prev=" << (int)executed[tile] << std::endl;
                if (tile < kTotalTiles) executed[tile] = 2;
                thiefSteals.fetch_add(1, std::memory_order_relaxed);
                totalExecuted.fetch_add(1, std::memory_order_relaxed);
            }
            else
            {
                std::this_thread::yield();
            }
        }
        std::cerr << "  thief: done steals=" << thiefSteals.load() << std::endl;
    });
    std::cerr << " ok" << std::endl;

    std::cerr << "[4] joining..." << std::flush;
    owner.join();
    thief.join();
    std::cerr << " ok" << std::endl;

    std::vector<uint32_t> lost;
    for (uint32_t i = 0; i < kTotalTiles; ++i)
        if (executed[i] == 0) lost.push_back(i);

    std::cerr << "owner=" << ownerPops.load()
              << " thief=" << thiefSteals.load()
              << " total=" << totalExecuted.load()
              << " lost=" << lost.size() << std::endl;

    if (!lost.empty())
    {
        uint32_t show = std::min((uint32_t)lost.size(), 20u);
        std::cerr << "lost: ";
        for (uint32_t i = 0; i < show; ++i)
            std::cerr << lost[i] << " ";
        if (lost.size() > show) std::cerr << "...(" << lost.size() << ")";
        std::cerr << std::endl;
    }

    if (lost.empty())
        std::cout << "PASS" << std::endl;
    else
        std::cout << "FAIL" << std::endl;
    return lost.empty() ? 0 : 1;
}
