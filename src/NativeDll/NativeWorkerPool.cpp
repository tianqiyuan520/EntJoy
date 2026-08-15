#include "NativeWorkerPool.h"
#include "ThreadAffinity.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
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
    // 连续模式背靠背 job 的 straggler 尾（快 worker 干完到 Submit 下一帧）在 µs~几百µs 量级，
    // 自旋须覆盖该尾宽才能避免 park；16ms 帧间隔远超此窗 → 仍 park，不烧 CPU
    //（keep-warm 无界自旋烧穿整个间隔的回归，有界即不碰）。
    static constexpr uint32_t kMaxSpinCount = 8192;

    struct NativeWorkerPool::Impl
    {
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
            // The owner pops from the front; thieves pop from the back. A mutex
            // keeps the first implementation portable while preserving the
            // Unity-style local-queue/work-stealing topology.
            std::mutex queueMutex;
            std::deque<WorkItem> queue;
            // futex 式唤醒标志（C++20 std::atomic::wait，MSVC 下即 futex），
            // 取代 counting_semaphore（MSVC 下为 win32 内核对象，每次唤醒走内核态）。
            std::atomic<uint32_t> wakeFlag{ 0 };
            std::thread thread;
        };

        mutable std::mutex lifecycleMutex;
        std::condition_variable idle;
        std::vector<std::unique_ptr<BatchDescriptor>> descriptorStorage;
        std::vector<BatchDescriptor*> freeDescriptors;
        std::vector<std::unique_ptr<WorkerState>> workers;
        std::atomic<size_t> outstandingBatches{ 0 };
        uint32_t nextSubmissionWorker{ 0 };
        bool accepting{ false };
        bool bindWorkers{ false };
        std::atomic<bool> stopRequested{ false };

        // 诊断计数器（keep-warm 实验后；有界 futex 混合等待下 hotSpinHits 重新启用）
        std::atomic<uint64_t> parkWakeCount{ 0 };  // worker 实际 park（内核态等待）次数
        std::atomic<uint64_t> hotSpinHits{ 0 };    // 混合等待命中（自旋/初始即有活，未 park）

        // 近无锁：descriptor per-thread 缓存。命中零锁；共享池（freeDescriptors）仅在
        // 缓存空/满时批量迁移（一次锁 / ~32 次）。poolId 防跨池代次（Start/Stop/Start）
        // 复用陈旧缓存指针：换代后直接丢弃缓存（旧 descriptorStorage 已释放，不可触碰）。
        static constexpr size_t kDescriptorCacheCap = 32;
        struct ThreadDescriptorCache
        {
            uintptr_t poolId{ 0 };
            std::vector<BatchDescriptor*> entries;
        };
        static thread_local ThreadDescriptorCache t_descriptorCache;

        BatchDescriptor* AcquireDescriptor()
        {
            auto& cache = t_descriptorCache;
            if (cache.poolId != reinterpret_cast<uintptr_t>(this))
            {
                cache.poolId = reinterpret_cast<uintptr_t>(this);
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
            if (cache.poolId != reinterpret_cast<uintptr_t>(this))
            {
                cache.poolId = reinterpret_cast<uintptr_t>(this);
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
            auto& worker = *workers[workerIndex];
            std::lock_guard<std::mutex> lock(worker.queueMutex);
            if (worker.queue.empty()) return false;
            item = worker.queue.front();
            worker.queue.pop_front();
            return true;
        }

        bool TrySteal(uint32_t thiefIndex, WorkItem& item) noexcept
        {
            const uint32_t count = static_cast<uint32_t>(workers.size());
            for (uint32_t offset = 1; offset < count; ++offset)
            {
                const uint32_t victimIndex = (thiefIndex + offset) % count;
                auto& victim = *workers[victimIndex];
                std::unique_lock<std::mutex> lock(victim.queueMutex, std::try_to_lock);
                if (!lock || victim.queue.empty()) continue;
                item = victim.queue.back();
                victim.queue.pop_back();
                return true;
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
            // lifecycle lock. 近无锁：descriptor 归还走 per-thread 缓存（零锁除非满），
            // outstanding 原子递减，末位递减才通知 idle（无锁 notify 对谓词等待安全）。
            batch->completion(batch->context);
            ReleaseDescriptor(batch);
            if (outstandingBatches.fetch_sub(1, std::memory_order_acq_rel) == 1)
            {
                // 持锁 notify：堵住 Stop 的 lost-wakeup 窗口。
                // Stop 在 lifecycleMutex 内检查谓词 outstanding==0；若末位 fetch_sub→0
                // 恰在 Stop 谓词检查(false) 与 wait 真正阻塞之间无锁 notify，唤醒即丢失 →
                // Stop 永久阻塞（实测 ~60% 概率挂死）。末位递减先于取锁，Stop 持锁检查时
                // 要么看到 0（直接放行），要么看到 >0（阻塞，worker 随后取锁 notify 必然唤醒）。
                // 锁仅在末位 batch 取一次，热路径零开销。
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
                // 自旋只覆盖背靠背 job（µs 级），不烧 16ms 间隔
                //（keep-warm 无界自旋回归的负结果 + futex 化唤醒 = 行业标准两件套）。
                uint32_t spins = 0;
                while (worker->wakeFlag.load(std::memory_order_acquire) == 0 && spins < kMaxSpinCount)
                {
                    CpuPause();
                    ++spins;
                }
                if (worker->wakeFlag.load(std::memory_order_acquire) == 0)
                {
                    ++parkWakeCount;
                    while (worker->wakeFlag.load(std::memory_order_acquire) == 0)
                        worker->wakeFlag.wait(0, std::memory_order_acquire); // 同 JobSystem.cpp completed.wait
                }
                else
                {
                    ++hotSpinHits; // 自旋/初始即有活 → 未 park
                }
                worker->wakeFlag.store(0, std::memory_order_relaxed);
                DrainAvailableWork(workerIndex);
                if (stopRequested.load(std::memory_order_acquire))
                {
                    DrainAvailableWork(workerIndex);
                    return;
                }
            }
        }
    };

    // 定义静态 thread_local 成员。线程退出时缓存中的 descriptor 不归还共享池——
    // 它们仍归 descriptorStorage（unique_ptr）所有，随池析构释放，无泄漏；仅
    // 失去复用（有界浪费，正常池生命周期内 worker 常驻无影响）。
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
            // 仅 guard 临界区：accepting 检查 + outstanding 递增 + 快照 worker。
            // 临界区外取 descriptor（per-thread 缓存，零锁除非空）与入队（各 worker 队列锁）。
            // outstanding 已递增，Stop 的 idle 等待在 <0 之外阻塞，workers 不会被 clear。
            std::lock_guard<std::mutex> lock(_impl->lifecycleMutex);
            if (!_impl->accepting || _impl->workers.empty()) return false;
            _impl->outstandingBatches.fetch_add(1, std::memory_order_relaxed);
            workerCount = static_cast<uint32_t>(_impl->workers.size());
            first = _impl->nextSubmissionWorker++ % workerCount;
        }

        auto* descriptor = _impl->AcquireDescriptor();
        descriptor->Reset(context, runSlot, completion, slotCount);
        wakeCounts.assign(workerCount, 0);
        for (uint32_t slot = 0; slot < slotCount; ++slot)
        {
            const uint32_t workerIndex = (first + slot) % workerCount;
            auto& worker = *_impl->workers[workerIndex];
            {
                std::lock_guard<std::mutex> queueLock(worker.queueMutex);
                worker.queue.push_front({ descriptor, slot });
            }
            ++wakeCounts[workerIndex];
        }
        for (uint32_t i = 0; i < wakeCounts.size(); ++i)
            if (wakeCounts[i] != 0)
            {
                _impl->workers[i]->wakeFlag.store(1, std::memory_order_release);
                _impl->workers[i]->wakeFlag.notify_one();
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
