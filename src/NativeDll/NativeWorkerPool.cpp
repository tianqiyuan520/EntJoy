#include "NativeWorkerPool.h"
#include "ThreadAffinity.h"

#include <atomic>
#include <climits>
#include <condition_variable>
#include <deque>
#include <memory>
#include <mutex>
#include <semaphore>
#include <thread>
#include <utility>
#include <vector>

namespace JobSystem
{
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
            std::counting_semaphore<INT_MAX> wake{ 0 };
            std::thread thread;
        };

        mutable std::mutex lifecycleMutex;
        std::condition_variable idle;
        std::vector<std::unique_ptr<BatchDescriptor>> descriptorStorage;
        std::vector<BatchDescriptor*> freeDescriptors;
        std::vector<std::unique_ptr<WorkerState>> workers;
        size_t outstandingBatches{ 0 };
        uint32_t nextSubmissionWorker{ 0 };
        bool accepting{ false };
        bool bindWorkers{ false };
        std::atomic<bool> stopRequested{ false };

        BatchDescriptor* AcquireDescriptorLocked()
        {
            if (!freeDescriptors.empty())
            {
                auto* descriptor = freeDescriptors.back();
                freeDescriptors.pop_back();
                return descriptor;
            }
            descriptorStorage.push_back(std::make_unique<BatchDescriptor>());
            return descriptorStorage.back().get();
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
            // lifecycle lock.
            batch->completion(batch->context);
            {
                std::lock_guard<std::mutex> lock(lifecycleMutex);
                freeDescriptors.push_back(batch);
                --outstandingBatches;
                if (outstandingBatches == 0) idle.notify_all();
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
            if (bindWorkers) BindCurrentThreadToLogicalProcessor(workerIndex);
#if defined(_WIN32)
            ::SetThreadPriority(::GetCurrentThread(), THREAD_PRIORITY_NORMAL);
#endif
            while (true)
            {
                worker->wake.acquire();
                DrainAvailableWork(workerIndex);
                if (stopRequested.load(std::memory_order_acquire))
                {
                    DrainAvailableWork(workerIndex);
                    return;
                }
            }
        }
    };

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
        for (auto& worker : _impl->workers) worker->wake.release();
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
        {
            std::lock_guard<std::mutex> lock(_impl->lifecycleMutex);
            if (!_impl->accepting || _impl->workers.empty()) return false;
            auto* descriptor = _impl->AcquireDescriptorLocked();
            descriptor->Reset(context, runSlot, completion, slotCount);
            ++_impl->outstandingBatches;

            const uint32_t workerCount = static_cast<uint32_t>(_impl->workers.size());
            wakeCounts.assign(workerCount, 0);
            const uint32_t first = _impl->nextSubmissionWorker++ % workerCount;
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
        }
        for (uint32_t i = 0; i < wakeCounts.size(); ++i)
            if (wakeCounts[i] != 0) _impl->workers[i]->wake.release();
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
}
