#pragma once

// MPMCInjector — 无锁有界 MPMC 环形队列（Dmitry Vyukov 经典算法）。
//
// 标准 Chase-Lev 模型的 Injector 角色：跨线程提交经此队列，
// worker 从中拉取任务推入自己 deque（owner-only PushBottom）。
//
// 线程安全：
//   - 多线程并发 Push（生产者）
//   - 多线程并发 Pop（消费者/stealer）
//   - 无锁，无 ABA（序列号免 ABA）
//
// 性能特征：
//   - 无竞争时 Push/Pop 各只一次 CAS
//   - 有竞争时 CAS 重试，但仍远优于全局锁
//   - 容量固定为 2 的幂，满/空返回 false 由调用方处理

#include <atomic>
#include <cstdint>
#include <type_traits>

namespace JobSystem
{
    template <typename T, uint32_t Capacity>
    struct MPMCInjector
    {
        static_assert((Capacity & (Capacity - 1)) == 0, "capacity must be a power of two");
        static_assert(Capacity >= 2, "capacity must be at least 2");

        struct Cell
        {
            std::atomic<uint64_t> seq{ 0 };
            T data;
        };

        std::atomic<uint64_t> enqueuePos{ 0 };
        std::atomic<uint64_t> dequeuePos{ 0 };
        Cell cells[Capacity];

        MPMCInjector() noexcept
        {
            for (uint32_t i = 0; i < Capacity; ++i)
                cells[i].seq.store(i, std::memory_order_relaxed);
        }

        // 非拷贝、非移动（原子成员不可拷贝）
        MPMCInjector(const MPMCInjector&) = delete;
        MPMCInjector& operator=(const MPMCInjector&) = delete;
        MPMCInjector(MPMCInjector&&) = delete;
        MPMCInjector& operator=(MPMCInjector&&) = delete;

        // 生产者：推入一个值。满时返回 false。
        // 无竞争时仅一次 CAS（enqueuePos relaxed fetch_add）。
        bool Push(const T& value) noexcept
        {
            uint64_t pos = enqueuePos.load(std::memory_order_relaxed);
            for (;;)
            {
                Cell& cell = cells[pos & (Capacity - 1)];
                const uint64_t seq = cell.seq.load(std::memory_order_acquire);
                const int64_t diff = static_cast<int64_t>(seq) - static_cast<int64_t>(pos);
                if (diff == 0)
                {
                    if (enqueuePos.compare_exchange_weak(pos, pos + 1, std::memory_order_relaxed))
                        break;
                }
                else if (diff < 0)
                {
                    return false; // full
                }
                else
                {
                    pos = enqueuePos.load(std::memory_order_relaxed);
                }
            }
            cells[pos & (Capacity - 1)].data = value;
            cells[pos & (Capacity - 1)].seq.store(pos + 1, std::memory_order_release);
            return true;
        }

        // 消费者/stealer：弹出一个值。空时返回 false。
        // 无竞争时仅一次 CAS（dequeuePos relaxed fetch_add）。
        bool Pop(T& value) noexcept
        {
            uint64_t pos = dequeuePos.load(std::memory_order_relaxed);
            for (;;)
            {
                Cell& cell = cells[pos & (Capacity - 1)];
                const uint64_t seq = cell.seq.load(std::memory_order_acquire);
                const int64_t diff = static_cast<int64_t>(seq) - static_cast<int64_t>(pos + 1);
                if (diff == 0)
                {
                    if (dequeuePos.compare_exchange_weak(pos, pos + 1, std::memory_order_relaxed))
                        break;
                }
                else if (diff < 0)
                {
                    return false; // empty
                }
                else
                {
                    pos = dequeuePos.load(std::memory_order_relaxed);
                }
            }
            value = cells[pos & (Capacity - 1)].data;
            cells[pos & (Capacity - 1)].seq.store(pos + Capacity, std::memory_order_release);
            return true;
        }

        // 诊断：近似占用数（并发时可能不精确）
        uint32_t ApproxSize() const noexcept
        {
            uint64_t enq = enqueuePos.load(std::memory_order_relaxed);
            uint64_t deq = dequeuePos.load(std::memory_order_relaxed);
            return static_cast<uint32_t>(enq >= deq ? enq - deq : 0);
        }
    };

} // namespace JobSystem
