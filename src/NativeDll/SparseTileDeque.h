#pragma once

// Chase-Lev 无锁双端队列 — 持久 per-worker 使用。
//
// 经典 Chase-Lev 协议（owner-only PushBottom/PopBottom）：
//   - Owner 从 bottom 端 PushBottom / PopBottom（无竞争，bottom_ 非原子）
//   - Thief 从 top 端 StealTop（CAS 竞争，低频）
//   - 容量固定（2 的幂），top/bottom 用 uint64_t 版本号，物理不可能回绕，免 ABA。
//
// 原子序（对齐 crossbeam-deque 的 stamp 校验）：
//   - top_    : atomic（thief CAS）
//   - bottom_ : 非 atomic（仅 owner 写）
//   - buffer_ : seq release/acquire 配对保证数据可见。
//     StealTop 在 CAS 前 acquire 校验 s.seq == t+1（数据已发布）——
//     若 CAS 成功后才发现未发布，top_ 已推进 → 任务永久丢失。
//
// 使用方式（crossbeam 标准模型）：
//   每个 Worker 持有一个 SparseTileDeque（owner-only 操作）。
//   跨线程提交经 Injector（MPSC 注入队列）→ worker 拉取到自己的 deque
//   （owner-only PushBottom）→ 标准 Chase-Lev 循环。deque 本身无跨线程 push。

#include <atomic>
#include <cstdint>
#include <cstddef>
#include <new>

namespace JobSystem
{
    struct BatchState; // forward

    // 每个 deque 元素：一个 tile 范围任务（Chase-Lev 任务粒度）。
    // 携带 batch 指针，跨 batch 混合存储时 thief 偷到的任务能正确执行。
    struct TileTask
    {
        BatchState* batch{ nullptr };
        uint32_t firstTile{ 0 };
        uint32_t tileCount{ 0 };
    };

    class SparseTileDeque
    {
    public:
        explicit SparseTileDeque(uint32_t capacity) noexcept
            : capacity_(RoundUpPow2(capacity < 8 ? 8 : capacity))
            , mask_(capacity_ - 1)
            , buffer_(new Slot[capacity_]())
            , top_{ 0 }
            , bottom_{ 0 }
        {
        }

        ~SparseTileDeque() noexcept { delete[] buffer_; }

        SparseTileDeque(const SparseTileDeque&) = delete;
        SparseTileDeque& operator=(const SparseTileDeque&) = delete;

        SparseTileDeque(SparseTileDeque&& o) noexcept
            : capacity_(o.capacity_), mask_(o.mask_), buffer_(o.buffer_)
            , top_{ o.top_.load(std::memory_order_relaxed) }, bottom_(o.bottom_.load(std::memory_order_relaxed))
        {
            o.buffer_ = nullptr; o.capacity_ = 0; o.mask_ = 0;
        }

        // ---- Owner（worker 线程，唯一调用者）----

        void PushBottom(TileTask task) noexcept
        {
            const uint64_t b = bottom_.load(std::memory_order_relaxed);
            Slot& s = Get(b);
            s.data = task;
            s.seq.store(b + 1, std::memory_order_release);
            bottom_.store(b + 1, std::memory_order_release);
        }

        bool PopBottom(TileTask& out) noexcept
        {
            uint64_t b = bottom_.load(std::memory_order_relaxed) - 1;
            bottom_.store(b, std::memory_order_release);

            // SeqCst fence：阻断 x86 store→load 重排 —— `bottom_.store(b)` 是 store，
            // 紧随的 `top_.load` 可能先执行读到陈旧 top_，使 owner 与 thief 双认领
            // 同一槽位（双执行）。对齐 crossbeam 的 back.store; fence(SeqCst); front.load。
            std::atomic_thread_fence(std::memory_order_seq_cst);

            uint64_t t = top_.load(std::memory_order_acquire);

            if (static_cast<int64_t>(b) >= static_cast<int64_t>(t))
            {
                Slot& s = Get(b);
                // 数据发布校验（owner 自身写入恒通过；防御竞争窗口）
                if (s.seq.load(std::memory_order_acquire) != b + 1)
                {
                    bottom_.store(b + 1, std::memory_order_release);
                    return false;
                }
                out = s.data;
                if (b == t)
                {
                    // 最后一个元素——与 StealTop 竞争（Chase-Lev 孤儿元素协议）
                    if (!top_.compare_exchange_strong(t, t + 1,
                            std::memory_order_acq_rel, std::memory_order_acquire))
                    {
                        bottom_.store(b + 1, std::memory_order_release);
                        return false;
                    }
                    bottom_.store(t + 1, std::memory_order_release); // Chase-Lev 关键：bottom 同步到 t+1
                }
                return true;
            }
            bottom_.store(b + 1, std::memory_order_release);
            return false;
        }

        // ---- Thief（其他 worker）----

        bool StealTop(TileTask& out) noexcept
        {
            // CAS 失败（其他 thief 抢先）重试有限次数，提高高并发窃取成功率。
            for (uint32_t attempt = 0; attempt < 4; ++attempt)
            {
                uint64_t t = top_.load(std::memory_order_acquire);
                uint64_t b = bottom_.load(std::memory_order_acquire);
                if (static_cast<int64_t>(t) >= static_cast<int64_t>(b))
                    return false;

                Slot& s = Get(t);

                // CAS 前 acquire 校验数据已发布：PushBottom 顺序是 data → seq(release)，
                // CAS 成功后才发现未发布则任务已丢失，故必须先验 seq==t+1。
                if (s.seq.load(std::memory_order_acquire) != t + 1)
                    return false; // 数据未发布（push 进行中）：放弃本次 steal

                if (top_.compare_exchange_strong(t, t + 1,
                        std::memory_order_acq_rel, std::memory_order_acquire))
                {
                    out = s.data;
                    return true;
                }
            }
            return false;
        }

        bool IsEmpty() const noexcept
        {
            return top_.load(std::memory_order_acquire) >= bottom_.load(std::memory_order_acquire);
        }

        uint32_t Capacity() const noexcept { return capacity_; }

        // 元素数量近似值（诊断用，thief 并发时可能读到过期值）。
        uint32_t ApproxSize() const noexcept
        {
            const int64_t t = static_cast<int64_t>(top_.load(std::memory_order_relaxed));
            const int64_t b = static_cast<int64_t>(bottom_.load(std::memory_order_relaxed));
            const int64_t sz = b - t;
            return static_cast<uint32_t>(sz > 0 ? sz : 0);
        }

    private:
        struct Slot
        {
            TileTask data{};
            std::atomic<uint64_t> seq{ 0 };
        };

        uint32_t capacity_{ 0 };
        uint32_t mask_{ 0 };
        Slot* buffer_{ nullptr };
        std::atomic<uint64_t> top_{ 0 };
        std::atomic<uint64_t> bottom_{ 0 };

        Slot& Get(uint64_t i) noexcept { return buffer_[i & mask_]; }

        static uint32_t RoundUpPow2(uint32_t v) noexcept
        {
            if (v == 0) return 1;
            v--; v |= v >> 1; v |= v >> 2; v |= v >> 4; v |= v >> 8; v |= v >> 16;
            return v + 1;
        }
    };
} // namespace JobSystem
