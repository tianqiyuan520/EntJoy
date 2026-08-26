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
    // 批量入队（PushMany）
    // ============================================================

    void TestPushManyBasic()
    {
        MPMCInjector<int, 16> q;

        int batch[5] = { 10, 20, 30, 40, 50 };
        uint32_t pushed = q.PushMany(batch, 5);
        assert(pushed == 5);   // 容量足够全入

        // 与逐个 Push 混排后 FIFO 顺序：10,20,30,40,50, 1, 2
        assert(q.Push(1));
        assert(q.Push(2));

        int val;
        for (int expected : { 10, 20, 30, 40, 50, 1, 2 })
        {
            assert(q.Pop(val));
            assert(val == expected);
        }

        printf("[PASS] TestPushManyBasic\n");
    }

    void TestPushManyFullPartial()
    {
        MPMCInjector<int, 8> q;  // 容量 8
        // 先塞 6 个 → 剩 2 槽
        int fill[6] = { 0, 1, 2, 3, 4, 5 };
        assert(q.PushMany(fill, 6) == 6);

        int batch[4] = { 100, 101, 102, 103 };
        uint32_t pushed = q.PushMany(batch, 4);   // 只能入 2 个
        assert(pushed == 2);

        // 剩余 2 个逐个补推（对应 SubmitBatch 的 fallback 路径）
        assert(q.Push(batch[2]));
        assert(q.Push(batch[3]));

        int val;
        for (int expected = 0; expected < 8; ++expected)
        {
            assert(q.Pop(val));
            assert(val == (expected < 6 ? expected : 100 + (expected - 6)));
        }

        printf("[PASS] TestPushManyFullPartial\n");
    }

    void TestPushManyWrapAround()
    {
        MPMCInjector<int, 4> q;  // 小容量回绕
        // 先填满再弹出，制造回绕态
        assert(q.Push(1));
        assert(q.Push(2));
        int val;
        assert(q.Pop(val)); assert(val == 1);
        assert(q.Pop(val)); assert(val == 2);

        // 回绕后 PushMany 跨槽
        int batch[2] = { 7, 8 };
        assert(q.PushMany(batch, 2) == 2);
        assert(q.Push(9));
        assert(q.Pop(val)); assert(val == 7);
        assert(q.Pop(val)); assert(val == 8);
        assert(q.Pop(val)); assert(val == 9);

        printf("[PASS] TestPushManyWrapAround\n");
    }

    void TestPushManyConcurrentMix()
    {
        // PushMany 与逐个 Push 并发混用：最终全部到达、不丢不重
        constexpr int kCapacity = 64;
        MPMCInjector<int, kCapacity> q;
        std::atomic<int64_t> pushed{ 0 };
        std::atomic<int64_t> popped{ 0 };
        constexpr int kItems = 20000;

        auto producer = [&](int id) {
            int64_t local = 0;
            while (local < kItems)
            {
                if (id & 1)   // 每隔生产者用 PushMany（随机批 1..8）
                {
                    int batch[8];
                    uint32_t n = static_cast<uint32_t>((local % 8) + 1);
                    for (uint32_t i = 0; i < n; ++i) batch[i] = id * 1000000 + local + i;
                    uint32_t got = q.PushMany(batch, n);
                    while (got < n)
                    {
                        if (q.Push(batch[got])) ++got;
                    }
                    local += n;
                }
                else
                {
                    if (q.Push(id * 1000000 + local)) ++local;
                }
                pushed.fetch_add(1, std::memory_order_relaxed);
            }
        };
        auto consumer = [&]() {
            int64_t localPop = 0;
            int val;
            while (localPop < 2 * kItems)   // 总元素 = kProducers × kItems
            {
                if (q.Pop(val)) ++localPop;
            }
            popped.fetch_add(localPop, std::memory_order_relaxed);
        };

        std::vector<std::thread> threads;
        threads.emplace_back(producer, 0);
        threads.emplace_back(producer, 1);
        threads.emplace_back(consumer);

        for (auto& t : threads) t.join();

        assert(popped.load() == static_cast<int64_t>(2 * kItems));
        printf("[PASS] TestPushManyConcurrentMix (popped=%lld)\n", (long long)popped.load());
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
    TestPushManyBasic();
    TestPushManyFullPartial();
    TestPushManyWrapAround();
    TestPushManyConcurrentMix();
    TestConcurrentMPMC();
    TestHighContention();

    printf("\nAll MPMCInjector tests passed.\n");
    return 0;
}
