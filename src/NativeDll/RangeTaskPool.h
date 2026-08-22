#pragma once

// RangeTaskPool — 固定容量的 RangeTask 对象池（无锁空闲栈）。
//
// RangeTask 是标准 Chase-Lev 调度器的任务粒度：每个 task 携带一个 tile 范围
// [firstTile, firstTile+tileCount)，由 Injector 分发，worker 从 Injector 拉取
// 后推入自己 deque（owner-only PushBottom），标准 Chase-Lev 循环执行。
//
// 池化设计（Treiber 无锁空闲栈）：
//   - 固定容量 kPoolSize（16384）
//   - Acquire：从空闲栈弹出（CAS 弹栈），空时返回 nullptr
//   - Release：压回空闲栈（CAS 压栈），真正回收
//   - ABA 防护：64 位 tag（高 32 位 = push/pop 计数，低 32 位 = 栈顶索引）
//
// 调用方契约：池耗尽时 Acquire 返回 nullptr，必须兜底（堆分配，
// poolIndex=UINT32_MAX，Release 时 delete）；不可跳过任务。

#include <atomic>
#include <cstdint>

namespace JobSystem
{
    struct BatchState; // forward

    // 标准 Chase-Lev 任务对象：一个 tile 范围。
    struct RangeTask
    {
        BatchState* batch{ nullptr };
        uint32_t firstTile{ 0 };
        uint32_t tileCount{ 0 };
        uint32_t poolIndex{ 0 };  // 在 storage_ 中的索引（用于 Release）
    };

    class RangeTaskPool
    {
    public:
        static constexpr uint32_t kPoolSize = 16384;

        RangeTaskPool() noexcept
        {
            // 初始空闲栈：所有槽位入栈（倒序，使 Acquire 升序返回）
            uint32_t head = kPoolSize; // 哨兵：空栈
            for (uint32_t i = 0; i < kPoolSize; ++i)
            {
                const uint32_t idx = kPoolSize - 1 - i;
                storage_[idx].next = head;
                head = idx;
            }
            freeHead_.store(static_cast<uint64_t>(head), std::memory_order_relaxed);
        }

        RangeTaskPool(const RangeTaskPool&) = delete;
        RangeTaskPool& operator=(const RangeTaskPool&) = delete;

        // 获取一个 RangeTask 对象。池空时返回 nullptr（调用方必须重试）。
        RangeTask* Acquire() noexcept
        {
            uint64_t head = freeHead_.load(std::memory_order_relaxed);
            while (true)
            {
                const uint32_t idx = static_cast<uint32_t>(head);
                if (idx == kPoolSize) return nullptr; // 空栈

                const uint32_t nextIdx = storage_[idx].next.load(std::memory_order_relaxed);
                const uint64_t newHead =
                    ((static_cast<uint64_t>(static_cast<uint32_t>(head >> 32)) + 1) << 32)
                    | static_cast<uint64_t>(nextIdx);
                if (freeHead_.compare_exchange_weak(
                        head, newHead, std::memory_order_acq_rel, std::memory_order_relaxed))
                {
                    RangeTask* task = &storage_[idx].task;
                    task->poolIndex = idx; // 记录槽位索引，Release 据此回收
                    return task;
                }
                // CAS 失败：重读（ABA 计数推进保证安全）
            }
        }

        // 归还一个 RangeTask 对象（压回空闲栈，真正回收；堆分配任务 delete）。
        void Release(RangeTask* task) noexcept
        {
            if (!task) return;
            const uint32_t idx = task->poolIndex;
            if (idx >= kPoolSize) { delete task; return; } // 堆分配兜底任务

            // 清理残留数据
            task->batch = nullptr;
            task->firstTile = 0;
            task->tileCount = 0;

            uint64_t head = freeHead_.load(std::memory_order_relaxed);
            while (true)
            {
                storage_[idx].next.store(static_cast<uint32_t>(head), std::memory_order_relaxed);
                const uint64_t newHead =
                    ((static_cast<uint64_t>(static_cast<uint32_t>(head >> 32)) + 1) << 32)
                    | static_cast<uint64_t>(idx);
                if (freeHead_.compare_exchange_weak(
                        head, newHead, std::memory_order_acq_rel, std::memory_order_relaxed))
                    return;
            }
        }

        // 诊断：空闲槽位近似数
        uint32_t ApproxFree() const noexcept
        {
            uint32_t count = 0;
            uint64_t head = freeHead_.load(std::memory_order_relaxed);
            uint32_t idx = static_cast<uint32_t>(head);
            uint32_t guard = 0;
            while (idx != kPoolSize && guard++ < 64)
            {
                ++count;
                idx = storage_[idx].next.load(std::memory_order_relaxed);
            }
            return count;
        }

    private:
        struct Slot
        {
            RangeTask task;
            std::atomic<uint32_t> next{ kPoolSize }; // 空闲栈下一个索引（kPoolSize=栈尾）
        };

        Slot storage_[kPoolSize];
        // 低 32 位 = 栈顶索引（kPoolSize=空），高 32 位 = push/pop 计数（ABA 防护）
        std::atomic<uint64_t> freeHead_{ 0 };
    };

} // namespace JobSystem