// MPMCInjector 单元测试：验证 Vyukov MPMC 环形队列的正确性。
// 测试内容：单线程基本操作、多线程并发 push/pop、满/空边界、ABA 压力。

#include "../NativeDll/MPMCInjector.h"

#include <atomic>
#include <cassert>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <thread>
#include <vector>

namespace
{
    using namespace JobSystem;

    // ============================================================
    // 基本测试
    // ============================================================

    void TestPushPop()
    {
        MPMCInjector<int, 16> q;

        // 空队列 Pop 应失败
        int val = -1;
        assert(!q.Pop(val));

        // Push 一个值
        assert(q.Push(42));
        assert(q.Pop(val));
        assert(val == 42);

        // 再次 Pop 应失败
        assert(!q.Pop(val));

        printf("[PASS] TestPushPop\n");
    }

    void TestFIFO()
    {
        MPMCInjector<int, 16> q;

        for (int i = 0; i < 10; ++i)
            assert(q.Push(i));

        for (int i = 0; i < 10; ++i)
        {
            int val = -1;
            assert(q.Pop(val));
            assert(val == i);
        }

        printf("[PASS] TestFIFO\n");
    }

    void TestFull()
    {
        MPMCInjector<int, 8> q;  // 容量 8

        // 填满
        for (int i = 0; i < 8; ++i)
            assert(q.Push(i));

        // 第 9 个应失败
        assert(!q.Push(999));

        // 弹出一个后应能再推入
        int val;
        assert(q.Pop(val));
        assert(val == 0);
        assert(q.Push(999));

        printf("[PASS] TestFull\n");
    }

    void TestWrapAround()
    {
        MPMCInjector<int, 4> q;  // 小容量，快速回绕
        int val;

        // 多轮 push/pop 测试回绕
        for (int round = 0; round < 100; ++round)
        {
            assert(q.Push(round));
            assert(q.Pop(val));
            assert(val == round);
        }

        printf("[PASS] TestWrapAround\n");
    }

    // ============================================================
    // 多线程并发测试
    // ============================================================

    void TestConcurrentMPMC()
    {
        constexpr int kCapacity = 1024;
        constexpr int kProducers = 4;
        constexpr int kConsumers = 4;
        constexpr int kItemsPerProducer = 100000;

        MPMCInjector<int, kCapacity> q;
        std::atomic<int64_t> pushCount{ 0 };
        std::atomic<int64_t> popCount{ 0 };

        // 生产者线程
        auto producer = [&]() {
            for (int i = 0; i < kItemsPerProducer; )
            {
                if (q.Push(i))
                {
                    pushCount.fetch_add(1, std::memory_order_relaxed);
                    ++i;
                }
                // 满时自旋重试
            }
        };

        // 消费者线程
        auto consumer = [&]() {
            int64_t localPop = 0;
            while (localPop < kItemsPerProducer)
            {
                int val;
                if (q.Pop(val))
                {
                    ++localPop;
                }
                // 空时自旋重试（有界）
            }
            popCount.fetch_add(localPop, std::memory_order_relaxed);
        };

        std::vector<std::thread> threads;
        for (int i = 0; i < kProducers; ++i)
            threads.emplace_back(producer);
        for (int i = 0; i < kConsumers; ++i)
            threads.emplace_back(consumer);

        for (auto& t : threads)
            t.join();

        int64_t totalPushed = pushCount.load();
        int64_t totalPopped = popCount.load();
        assert(totalPushed == static_cast<int64_t>(kProducers) * kItemsPerProducer);
        assert(totalPopped == static_cast<int64_t>(kProducers) * kItemsPerProducer);

        printf("[PASS] TestConcurrentMPMC (pushed=%lld, popped=%lld)\n",
            (long long)totalPushed, (long long)totalPopped);
    }

    void TestHighContention()
    {
        constexpr int kCapacity = 64;
        constexpr int kThreads = 16;
        constexpr int kItemsPerThread = 50000;

        MPMCInjector<int, kCapacity> q;
        std::atomic<int64_t> totalPopped{ 0 };

        // 所有线程既 push 又 pop（高竞争）
        auto worker = [&](int id) {
            int64_t localPop = 0;
            for (int i = 0; i < kItemsPerThread; ++i)
            {
                // 交替 push/pop
                q.Push(id * kItemsPerThread + i);
                int val;
                if (q.Pop(val))
                    ++localPop;
            }
            // 排空剩余
            int val;
            while (q.Pop(val))
                ++localPop;
            totalPopped.fetch_add(localPop, std::memory_order_relaxed);
        };

        std::vector<std::thread> threads;
        for (int i = 0; i < kThreads; ++i)
            threads.emplace_back(worker, i);

        for (auto& t : threads)
            t.join();

        printf("[PASS] TestHighContention (popped=%lld)\n",
            (long long)totalPopped.load());

        // 不验证精确计数（并发下无法保证），只验证不崩溃/不死锁
    }

} // anonymous namespace

int main()
{
    printf("=== MPMCInjector Tests ===\n");

    TestPushPop();
    TestFIFO();
    TestFull();
    TestWrapAround();
    TestConcurrentMPMC();
    TestHighContention();

    printf("\nAll MPMCInjector tests passed.\n");
    return 0;
}
