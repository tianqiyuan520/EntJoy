#include "NativeWorkerPool.h"
#include "ThreadAffinity.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#endif

namespace JobSystem
{
    static inline void CpuPause() noexcept
    {
#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
        _mm_pause();
#endif
    }

    // 混合等待的有界自旋窗：约 8192 × pause ≈ 360µs。
    // 覆盖背靠背 job 的尾宽（µs~几百µs），避免连续模式 park/wake；
    // 16ms 帧间隔远超此窗 → 仍 park，不空烧 CPU。
    static constexpr uint32_t kMaxSpinCount = 8192;

    // 每 worker 本地队列容量 + 全局溢出队列容量（须为 2 的幂）。
    // 本地环容纳 round-robin 分发的在飞任务；满则溢出到全局环
    //（worker 空闲时优先排空），全局也满 = 病理过载，wake-all 后自旋排空。
    static constexpr uint32_t kLocalQueueCapacity = 2048;
    static constexpr uint32_t kGlobalQueueCapacity = 32768;

    struct NativeWorkerPool::Impl
    {
        // 无锁有界 MPMC 环形队列（Dmitry Vyukov 经典算法）。
        // 序列号免 ABA：每槽位带单调 seq，producer/consumer 各持 CAS 头；
        // 无竞争时 Push/Pop 各只一次 CAS。满/空返回 false 由调用方处理。
        template <typename T, uint32_t Capacity>
        struct MpmcRing
        {
            static_assert((Capacity & (Capacity - 1)) == 0, "capacity must be a power of two");

            struct Cell
            {
                std::atomic<uint64_t> seq{ 0 };
                T data;
            };

            std::atomic<uint64_t> enqueuePos{ 0 };
            std::atomic<uint64_t> dequeuePos{ 0 };
            Cell cells[Capacity];

            MpmcRing() noexcept
            {
                for (uint32_t i = 0; i < Capacity; ++i)
                    cells[i].seq.store(i, std::memory_order_relaxed);
            }

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
        };
        struct BatchDescriptor
        {
            void* context{ nullptr };
            RunSlotFn runSlot{ nullptr };
            CompletionFn completion{ nullptr };
            std::atomic<uint32_t> remaining{ 0 };

            void Reset(void* value, RunSlotFn run, CompletionFn done,
                uint32_t count) noexcept
            {
                context = value;
                runSlot = run;
                completion = done;
                remaining.store(count, std::memory_order_relaxed);
            }
        };

        struct WorkItem
        {
            BatchDescriptor* batch;
            uint32_t slot;
        };

        struct WorkerState
        {
            // 无锁 MPMC 环：Submit round-robin push，owner pop，thief steal（victim 环 pop）。
            MpmcRing<WorkItem, kLocalQueueCapacity> queue;
            // 双 bit 职责分离：
            //  - wakeFlag：唤醒标志。Submit 置 1（push 前后各一次），owner 在 drain 开始消费。
            //    永不事后 clear——否则会抹掉 Submit 刚置的 flag，使落环 job 失去保护。
            //  - draining：owner-priority。owner drain 全程置 1，thief 跳过，防「偷走 owner
            //    即将认领的阻塞 job → 自身被阻塞 → 自身队列搁浅」。
            std::atomic<uint32_t> wakeFlag{ 0 };
            std::atomic<uint32_t> draining{ 0 };
            std::thread thread;
        };

        mutable std::mutex lifecycleMutex;
        std::condition_variable idle;
        std::vector<std::unique_ptr<BatchDescriptor>> descriptorStorage;
        std::vector<BatchDescriptor*> freeDescriptors;
        std::vector<std::unique_ptr<WorkerState>> workers;
        // 本地环满时的全局溢出环（worker 空闲时优先排空）。
        MpmcRing<WorkItem, kGlobalQueueCapacity> overflow;
        std::atomic<size_t> outstandingBatches{ 0 };
        uint32_t nextSubmissionWorker{ 0 };
        bool accepting{ false };
        bool bindWorkers{ false };
        std::atomic<bool> stopRequested{ false };

        // 诊断计数器（有界 futex 混合等待）
        std::atomic<uint64_t> parkWakeCount{ 0 };  // worker 实际 park（内核态等待）次数
        std::atomic<uint64_t> hotSpinHits{ 0 };    // 混合等待命中（自旋/初始即有活，未 park）

        // 近无锁：descriptor per-thread 缓存。命中零锁；共享池（freeDescriptors）仅在
        // 缓存空/满时批量迁移（一次锁 / 缓存容量次）。poolSerial 防跨池代次
        //（Start/Stop/Start）复用陈旧缓存指针：换代后直接丢弃缓存。
        // 注意：身份不能使用 this 指针——池销毁后再建，Impl 可能落在同一堆地址，
        // this 相等会误判「同池」而复用已释放的陈旧 descriptor（UAF）。用单调
        // 递增的 poolSerial 唯一标识一代池，代次相等才允许复用缓存。
        static constexpr size_t kDescriptorCacheCap = 32;
        struct ThreadDescriptorCache
        {
            uint64_t poolSerial{ 0 };
            std::vector<BatchDescriptor*> entries;
        };
        static thread_local ThreadDescriptorCache t_descriptorCache;
        const uint64_t poolSerial;
        Impl() : poolSerial(NextPoolSerial()) {}
        static uint64_t NextPoolSerial()
        {
            static std::atomic<uint64_t> serial{ 0 };
            return serial.fetch_add(1, std::memory_order_relaxed);
        }

        BatchDescriptor* AcquireDescriptor()
        {
            auto& cache = t_descriptorCache;
            if (cache.poolSerial != poolSerial)
            {
                cache.poolSerial = poolSerial;
                cache.entries.clear();
            }
            if (!cache.entries.empty())
            {
                auto* d = cache.entries.back();
                cache.entries.pop_back();
                return d;
            }
            // 缓存空：锁内从共享池批量补满，池空则创建。
            std::lock_guard<std::mutex> lock(lifecycleMutex);
            const size_t available = std::min(freeDescriptors.size(), kDescriptorCacheCap);
            if (available > 0)
            {
                auto* d = freeDescriptors.back();
                freeDescriptors.pop_back();
                for (size_t i = 1; i < available; ++i)
                {
                    cache.entries.push_back(freeDescriptors.back());
                    freeDescriptors.pop_back();
                }
                return d;
            }
            descriptorStorage.push_back(std::make_unique<BatchDescriptor>());
            return descriptorStorage.back().get();
        }

        void ReleaseDescriptor(BatchDescriptor* descriptor) noexcept
        {
            auto& cache = t_descriptorCache;
            if (cache.poolSerial != poolSerial)
            {
                cache.poolSerial = poolSerial;
                cache.entries.clear();
            }
            if (cache.entries.size() < kDescriptorCacheCap)
            {
                cache.entries.push_back(descriptor);
                return;
            }
            // 缓存满：整体迁移共享池（一次锁）。
            std::lock_guard<std::mutex> lock(lifecycleMutex);
            for (auto* d : cache.entries) freeDescriptors.push_back(d);
            cache.entries.clear();
            cache.entries.push_back(descriptor);
        }

        bool TryPopLocal(uint32_t workerIndex, WorkItem& item) noexcept
        {
            return workers[workerIndex]->queue.Pop(item);
        }

        bool TrySteal(uint32_t thiefIndex, WorkItem& item) noexcept
        {
            // 全局溢出环优先排空（FIFO），再偷各 worker 本地环。
            if (overflow.Pop(item))
                return true;
            const uint32_t count = static_cast<uint32_t>(workers.size());
            for (uint32_t offset = 1; offset < count; ++offset)
            {
                const uint32_t victimIndex = (thiefIndex + offset) % count;
                auto& victim = *workers[victimIndex];
                // owner-priority：owner 正在 drain（draining）或待醒（wakeFlag）→
                // 即将/正在认领自己的环，跳过。防「偷走阻塞 job → 自身被阻塞 →
                // 自身队列搁浅」。由「Submit 置 flag 后才 push + owner 只在 drain 内消费 +
                // 永不事后 clear」保证：环非空 ⟹ flag==1 或 draining==1，跳过者必是
                // owner 即将认领的环，不误跳被遗弃的环。
                const uint32_t protecting = victim.draining.load(std::memory_order_acquire) |
                    victim.wakeFlag.load(std::memory_order_acquire);
                if (protecting != 0)
                    continue;
                WorkItem stolen{};
                if (victim.queue.Pop(stolen))
                {
                    // 双检：预检读与 pop 之间 Submit 可能已完成「置 flag → push」
                    //（check-then-pop 竞态，读到陈旧 0 却 pop 到刚提交的 job）。
                    // pop 后重读：若 victim 已被保护 → 归还其环由 owner 认领。
                    if ((victim.draining.load(std::memory_order_acquire) |
                        victim.wakeFlag.load(std::memory_order_acquire)) != 0)
                    {
                        // 归还给 victim；环满（空位在头部，enqueuePos 处仍占用）则直接执行。
                        if (!victim.queue.Push(stolen))
                            FinishWork(stolen);
                        continue;
                    }
                    item = stolen;
                    return true;
                }
            }
            return false;
        }

        void FinishWork(const WorkItem& item) noexcept
        {
            auto* batch = item.batch;
            batch->runSlot(batch->context, item.slot);
            if (batch->remaining.fetch_sub(1, std::memory_order_acq_rel) != 1)
                return;

            // All work items have returned before the counter reaches zero.
            // Completion may publish dependent jobs, so it must run outside the
            // lifecycle lock. 末位递减须持锁 notify：否则 Stop 的
            // idle.wait(lock, outstanding==0) 在「谓词检查(false) 与 阻塞」之间会
            // 丢失末位 notify 而永久阻塞。末位递减先于取锁：Stop 持锁检查要么见 0
            // 直接放行，要么阻塞（worker 取锁 notify 必然唤醒）。锁仅在末位 batch 取一次。
            batch->completion(batch->context);
            ReleaseDescriptor(batch);
            if (outstandingBatches.fetch_sub(1, std::memory_order_acq_rel) == 1)
            {
                std::lock_guard<std::mutex> lock(lifecycleMutex);
                idle.notify_all();
            }
        }

        void DrainAvailableWork(uint32_t workerIndex) noexcept
        {
            WorkItem item{};
            while (TryPopLocal(workerIndex, item) || TrySteal(workerIndex, item))
                FinishWork(item);
        }

        void WorkerLoop(uint32_t workerIndex, WorkerState* worker) noexcept
        {
            if (bindWorkers)
            {
                // Workers use logical cores 1..N so they avoid competing with
                // the main thread (pinned to core 0).
                BindCurrentThreadToLogicalProcessor(1 + workerIndex);
            }
#if defined(_WIN32)
            ::SetThreadPriority(::GetCurrentThread(), THREAD_PRIORITY_NORMAL);
#endif
            while (true)
            {
                // 混合等待：有界自旋 → std::atomic::wait（C++20，MSVC 下即 futex）。
                // 自旋只覆盖背靠背 job（µs 级），不烧 16ms 间隔。
                uint32_t spins = 0;
                while (worker->wakeFlag.load(std::memory_order_acquire) == 0 && spins < kMaxSpinCount)
                {
                    CpuPause();
                    ++spins;
                }
                const bool hasPendingFlag =
                    worker->wakeFlag.load(std::memory_order_acquire) != 0;
                if (!hasPendingFlag)
                {
                    ++parkWakeCount;
                    // 睡前自查（draining 保护内）：Submit 先置 flag 后 push，本 worker
                    // 可能已消费 flag 而 job 此刻才落环——直接 park 会错过。命中即排空，
                    // 仍空才睡（std::atomic::wait 原子比较+注册，无丢失唤醒）。
                    worker->draining.store(1, std::memory_order_release);
                    WorkItem preItem{};
                    if (TryPopLocal(workerIndex, preItem))
                    {
                        FinishWork(preItem);
                        worker->wakeFlag.exchange(0, std::memory_order_acq_rel);
                        DrainAvailableWork(workerIndex);
                        worker->draining.store(0, std::memory_order_release);
                        continue;
                    }
                    worker->draining.store(0, std::memory_order_release);
                    while (worker->wakeFlag.load(std::memory_order_acquire) == 0)
                        worker->wakeFlag.wait(0, std::memory_order_acquire);
                }
                else
                {
                    ++hotSpinHits; // 自旋/初始即有活 → 未 park
                }

                // 进入 drain：先置 draining 再消费 flag——防「消费 flag 与真正认领」
                // 之间的空窗被 thief 抢走未认领 job。wakeFlag 只负责唤醒（drain 后
                // 永不 clear），draining 只负责 owner-priority。
                worker->draining.store(1, std::memory_order_release);
                worker->wakeFlag.exchange(0, std::memory_order_acq_rel);
                DrainAvailableWork(workerIndex);
                worker->draining.store(0, std::memory_order_release);
                if (stopRequested.load(std::memory_order_acquire))
                {
                    DrainAvailableWork(workerIndex);
                    return;
                }
            }
        }
    };

    // 定义静态 thread_local 成员。线程退出时缓存中的 descriptor 不归还共享池——
    // 它们仍归 descriptorStorage（unique_ptr）所有，随池析构释放，无泄漏。
    thread_local NativeWorkerPool::Impl::ThreadDescriptorCache
        NativeWorkerPool::Impl::t_descriptorCache;

    NativeWorkerPool::NativeWorkerPool() : _impl(std::make_unique<Impl>()) {}
    NativeWorkerPool::~NativeWorkerPool() { Stop(); }

    bool NativeWorkerPool::Start(uint32_t workerCount, bool bindWorkers)
    {
        if (workerCount == 0) return false;
        std::unique_lock<std::mutex> lock(_impl->lifecycleMutex);
        if (_impl->accepting) return _impl->workers.size() == workerCount;
        if (!_impl->workers.empty()) return false;
        _impl->stopRequested.store(false, std::memory_order_relaxed);
        _impl->bindWorkers = bindWorkers;
        try
        {
            _impl->workers.reserve(workerCount);
            for (uint32_t i = 0; i < workerCount; ++i)
                _impl->workers.push_back(std::make_unique<Impl::WorkerState>());
            for (uint32_t i = 0; i < workerCount; ++i)
            {
                auto* raw = _impl->workers[i].get();
                raw->thread = std::thread([this, i, raw]
                {
                    _impl->WorkerLoop(i, raw);
                });
            }
        }
        catch (...)
        {
            lock.unlock();
            Stop();
            throw;
        }
        _impl->accepting = true;
        return true;
    }

    void NativeWorkerPool::Stop() noexcept
    {
        {
            std::unique_lock<std::mutex> lock(_impl->lifecycleMutex);
            if (_impl->workers.empty())
            {
                _impl->accepting = false;
                return;
            }
            _impl->accepting = false;
            _impl->idle.wait(lock, [this] { return _impl->outstandingBatches == 0; });
            _impl->stopRequested.store(true, std::memory_order_release);
        }
        for (auto& worker : _impl->workers)
        {
            worker->wakeFlag.store(1, std::memory_order_relaxed);
            worker->wakeFlag.notify_one();
        }
        for (auto& worker : _impl->workers)
            if (worker->thread.joinable()) worker->thread.join();
        _impl->workers.clear();
        std::lock_guard<std::mutex> lock(_impl->lifecycleMutex);
        _impl->stopRequested.store(false, std::memory_order_relaxed);
        _impl->nextSubmissionWorker = 0;
    }

    bool NativeWorkerPool::Submit(void* context, uint32_t slotCount,
        RunSlotFn runSlot, CompletionFn completion)
    {
        if (slotCount == 0 || !runSlot || !completion) return false;
        std::vector<uint32_t> wakeCounts;
        uint32_t workerCount = 0, first = 0;
        {
            // 临界区仅 guard：accepting 检查 + outstanding 递增 + 快照 worker。
            // 临界区外取 descriptor（per-thread 缓存）与入队（无锁环）。outstanding
            // 已递增，Stop 的 idle 等待阻塞于此，workers 不会被中途 clear。
            std::lock_guard<std::mutex> lock(_impl->lifecycleMutex);
            if (!_impl->accepting || _impl->workers.empty()) return false;
            _impl->outstandingBatches.fetch_add(1, std::memory_order_relaxed);
            workerCount = static_cast<uint32_t>(_impl->workers.size());
            first = _impl->nextSubmissionWorker++ % workerCount;
        }

        auto* descriptor = _impl->AcquireDescriptor();
        descriptor->Reset(context, runSlot, completion, slotCount);
        wakeCounts.assign(workerCount, 0);
        bool overflowUsed = false;
        for (uint32_t slot = 0; slot < slotCount; ++slot)
        {
            const uint32_t workerIndex = (first + slot) % workerCount;
            auto& worker = *_impl->workers[workerIndex];
            // flag 先置（release）再 push：落环 job 必带 pending wake。push 成功后
            // 再置一次 flag——闭环「owner 在 drain 内消费 pre-push flag → job 落环时
            // flag 已被抹」的窗口，保证「环非空 ⟹ flag==1 或 draining==1」。
            // push 失败走全局溢出环，由末尾 wake-all 兜底。
            worker.wakeFlag.store(1, std::memory_order_release);
            if (worker.queue.Push({ descriptor, slot }))
            {
                worker.wakeFlag.store(1, std::memory_order_release);
                ++wakeCounts[workerIndex];
            }
            else
            {
                overflowUsed = true;
                while (!_impl->overflow.Push({ descriptor, slot }))
                {
                    // 全局环也满 = 病理过载：唤醒全部 worker 排空后自旋。
                    // worker 永不阻塞，必收敛。
                    for (auto& w : _impl->workers)
                        w->wakeFlag.store(1, std::memory_order_release);
                    for (auto& w : _impl->workers)
                        w->wakeFlag.notify_one();
                    std::this_thread::yield();
                }
            }
        }
        for (uint32_t i = 0; i < wakeCounts.size(); ++i)
            if (wakeCounts[i] != 0)
            {
                _impl->workers[i]->wakeFlag.notify_one();
            }
        if (overflowUsed)
        {
            for (auto& w : _impl->workers)
            {
                w->wakeFlag.store(1, std::memory_order_release);
                w->wakeFlag.notify_one();
            }
        }
        return true;
    }

    bool NativeWorkerPool::IsRunning() const noexcept
    {
        std::lock_guard<std::mutex> lock(_impl->lifecycleMutex);
        return _impl->accepting;
    }

    uint32_t NativeWorkerPool::WorkerCount() const noexcept
    {
        std::lock_guard<std::mutex> lock(_impl->lifecycleMutex);
        return static_cast<uint32_t>(_impl->workers.size());
    }

    void NativeWorkerPool::GetCounters(uint64_t* parkWakeCount, uint64_t* hotSpinHits) const noexcept
    {
        if (parkWakeCount) *parkWakeCount = _impl->parkWakeCount.load(std::memory_order_relaxed);
        if (hotSpinHits) *hotSpinHits = _impl->hotSpinHits.load(std::memory_order_relaxed);
    }

    void NativeWorkerPool::ResetCounters() noexcept
    {
        _impl->parkWakeCount.store(0, std::memory_order_relaxed);
        _impl->hotSpinHits.store(0, std::memory_order_relaxed);
    }
}
