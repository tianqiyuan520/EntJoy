#pragma once

// RangeTaskPool — 固定容量的 RangeTask 对象池。
//
// RangeTask 是标准 Chase-Lev 调度器的任务粒度：每个 task 携带一个 tile 范围
// [firstTile, firstTile+tileCount)，由 Injector 分发，worker 从 Injector 拉取
// 后推入自己 deque（owner-only PushBottom），标准 Chase-Lev 循环执行。
//
// 池化设计：
//   - 固定容量 kPoolSize（16384），覆盖 GridSearch 最大 ~6250 tasks/batch
//   - 无锁：原子 fetch_add 获取槽位，原子 store 归还
//   - 满时 Acquire 返回 nullptr（调用方自旋重试）
//   - 注意：wrap-around 时可能复用未释放的 task（仅在 pool 真的满时发生）

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
            for (uint32_t i = 0; i < kPoolSize; ++i)
                storage_[i].poolIndex = i;
        }

        RangeTaskPool(const RangeTaskPool&) = delete;
        RangeTaskPool& operator=(const RangeTaskPool&) = delete;

        // 获取一个 RangeTask 对象。满时返回 nullptr。
        RangeTask* Acquire() noexcept
        {
            const uint32_t idx = nextFree_.fetch_add(1, std::memory_order_relaxed);
            if (idx >= kPoolSize)
            {
                nextFree_.store(0, std::memory_order_relaxed);
                return nullptr;
            }
            return &storage_[idx].task;
        }

        // 归还一个 RangeTask 对象。
        void Release(RangeTask* task) noexcept
        {
            if (!task) return;
            task->batch = nullptr;
            task->firstTile = 0;
            task->tileCount = 0;
        }

    private:
        struct Slot
        {
            RangeTask task;
            uint32_t poolIndex{ 0 };
        };

        Slot storage_[kPoolSize];
        std::atomic<uint32_t> nextFree_{ 0 };
    };

} // namespace JobSystem
