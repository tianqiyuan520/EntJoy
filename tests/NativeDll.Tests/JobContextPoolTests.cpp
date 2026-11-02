#include "JobContextPool.h"
#include "MPMCInjector.h"

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <thread>

namespace
{
    void Require(bool condition, const char* message)
    {
        if (!condition)
        {
            std::fprintf(stderr, "FAIL: %s\n", message);
            std::exit(1);
        }
    }

    void TestCapacityBoundaries()
    {
        using namespace JobSystem::ContextMemory;
        for (int bytes = 1; bytes <= 4096; ++bytes)
        {
            const int index = Index(bytes);
            Require(index >= 0 && index < BucketCount, "pooled size has a valid bucket");
            const int capacity = (index + 1) * 64;
            Require(capacity >= bytes && capacity - bytes < 64, "smallest rounded capacity");
            void* memory = Rent(bytes);
            Require(memory != nullptr, "pooled allocation");
            std::memset(memory, 0xA5, bytes);
            Return(memory, bytes);
        }
        for (int bytes : { 0, -1, 4097, (std::numeric_limits<int>::max)() })
            Require(Index(bytes) == -1, "unpooled boundary");
        for (int bytes : { 4097, 8192 })
        {
            void* memory = Rent(bytes);
            Require(memory != nullptr, "large allocation");
            std::memset(memory, 0x5A, bytes);
            Return(memory, bytes);
        }
    }

    void TestCrossThreadReturns()
    {
        struct Item { void* memory; int bytes; unsigned char value; };
        JobSystem::MPMCInjector<Item, 128> queue;
        constexpr int count = 20000;
        std::atomic<int> received{ 0 };
        std::thread consumer([&]
        {
            for (int n = 0; n < count;)
            {
                Item item{};
                if (!queue.Pop(item))
                {
                    std::this_thread::yield();
                    continue;
                }
                auto* bytes = static_cast<unsigned char*>(item.memory);
                for (int i = 0; i < item.bytes; ++i)
                    Require(bytes[i] == item.value, "payload preserved across thread handoff");
                JobSystem::ContextMemory::Return(item.memory, item.bytes);
                ++n;
            }
            received.store(count);
        });
        for (int i = 0; i < count; ++i)
        {
            const int bytes = 1 + (i * 37) % 8192;
            const auto value = static_cast<unsigned char>(i);
            void* memory = JobSystem::ContextMemory::Rent(bytes);
            Require(memory != nullptr, "handoff allocation");
            std::memset(memory, value, bytes);
            const Item item{ memory, bytes, value };
            while (!queue.Push(item)) std::this_thread::yield();
        }
        consumer.join();
        Require(received.load() == count, "all handoffs completed");
    }
}

int main()
{
    TestCapacityBoundaries();
    TestCrossThreadReturns();
    std::puts("JobContextPoolTests: ALL PASS");
}
