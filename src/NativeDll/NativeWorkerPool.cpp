#include "NativeWorkerPool.h"

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
            uint32_t slotCount{ 0 };
            uint32_t targetWorkerCount{ 0 };
            std::atomic<uint32_t> remaining{ 0 };
            std::atomic<uint32_t> nextSlot{ 0 };

            // Workers increment readers before touching a published descriptor.
            // This lets the completion thread safely return it to the pool without
            // shared_ptr or per-slot queue nodes.
            std::atomic<uint32_t> readers{ 0 };

            void Reset(
                void* value,
                RunSlotFn run,
                CompletionFn done,
                uint32_t count) noexcept
            {
                context = value;
                runSlot = run;
                completion = done;
                slotCount = count;
                targetWorkerCount = 0;
                remaining.store(count, std::memory_order_relaxed);
                nextSlot.store(0, std::memory_order_relaxed);
            }
        };

        struct WorkerState
        {
            // A fast worker may finish a batch before every released worker has
            // consumed its wake. Counting preserves those releases safely while
            // later batches are published.
            std::counting_semaphore<INT_MAX> wake{ 0 };
            std::thread thread;
        };

        mutable std::mutex mutex;
        std::condition_variable idle;

        // Only Submit and batch completion touch these queues. Worker hot paths
        // load activeBatch and claim slots entirely through atomics.
        std::deque<BatchDescriptor*> pendingBatches;
        std::vector<std::unique_ptr<BatchDescriptor>> descriptorStorage;
        std::vector<BatchDescriptor*> freeDescriptors;
        std::atomic<BatchDescriptor*> activeBatch{ nullptr };
        std::vector<std::unique_ptr<WorkerState>> workers;
        size_t outstandingBatches{ 0 };
        bool accepting{ false };
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

        BatchDescriptor* PublishNextLocked() noexcept
        {
            if (activeBatch.load(std::memory_order_relaxed) || pendingBatches.empty())
                return nullptr;

            auto* next = pendingBatches.front();
            pendingBatches.pop_front();
            next->targetWorkerCount = static_cast<uint32_t>((std::min)(
                static_cast<size_t>(next->slotCount), workers.size()));
            activeBatch.store(next, std::memory_order_release);
            return next;
        }

        void WakeTargetWorkers(const BatchDescriptor* descriptor) noexcept
        {
            if (!descriptor) return;
            for (uint32_t worker = 0; worker < descriptor->targetWorkerCount; ++worker)
                workers[worker]->wake.release();
        }

        BatchDescriptor* AcquireActiveBatch() noexcept
        {
            while (true)
            {
                auto* descriptor = activeBatch.load(std::memory_order_acquire);
                if (!descriptor) return nullptr;

                descriptor->readers.fetch_add(1, std::memory_order_acq_rel);
                if (activeBatch.load(std::memory_order_acquire) == descriptor)
                    return descriptor;

                descriptor->readers.fetch_sub(1, std::memory_order_release);
            }
        }

        void ReleaseActiveBatch(BatchDescriptor* descriptor) noexcept
        {
            descriptor->readers.fetch_sub(1, std::memory_order_release);
        }

        void FinishBatch(BatchDescriptor* descriptor) noexcept
        {
            BatchDescriptor* nextBatch = nullptr;
            {
                std::lock_guard<std::mutex> lock(mutex);
                if (activeBatch.load(std::memory_order_relaxed) == descriptor)
                    activeBatch.store(nullptr, std::memory_order_release);
                nextBatch = PublishNextLocked();
            }

            WakeTargetWorkers(nextBatch);

            // All valid slots have returned before remaining reaches zero, so the
            // user context can be completed even if a late worker is still dropping
            // a reader acquired for an already exhausted descriptor.
            descriptor->completion(descriptor->context);

            while (descriptor->readers.load(std::memory_order_acquire) != 0)
                std::this_thread::yield();

            {
                std::lock_guard<std::mutex> lock(mutex);
                freeDescriptors.push_back(descriptor);
                --outstandingBatches;
                if (outstandingBatches == 0)
                    idle.notify_all();
            }
        }

        void ExecutePublishedBatch(uint32_t workerIndex) noexcept
        {
            auto* descriptor = AcquireActiveBatch();
            if (!descriptor) return;

            const uint32_t targetWorkerCount = descriptor->targetWorkerCount;
            if (workerIndex >= targetWorkerCount || targetWorkerCount == 0)
            {
                ReleaseActiveBatch(descriptor);
                return;
            }

            bool completedBatch = false;
            // Completion depends on all partitions being executed, not on every
            // notified OS thread waking up. Any ready worker can drain additional
            // slots, matching a work-stealing executor and removing the slowest-
            // worker acknowledgement barrier from the batch tail.
            while (true)
            {
                const uint32_t slot = descriptor->nextSlot.fetch_add(
                    1, std::memory_order_relaxed);
                if (slot >= descriptor->slotCount) break;
                descriptor->runSlot(descriptor->context, slot);
                if (descriptor->remaining.fetch_sub(
                    1, std::memory_order_acq_rel) == 1)
                {
                    completedBatch = true;
                    break;
                }
            }

            ReleaseActiveBatch(descriptor);
            if (completedBatch)
                FinishBatch(descriptor);
        }

        void WorkerLoop(uint32_t workerIndex, WorkerState* worker) noexcept
        {
            while (true)
            {
                worker->wake.acquire();
                if (stopRequested.load(std::memory_order_acquire)) return;
                ExecutePublishedBatch(workerIndex);
            }
        }
    };

    NativeWorkerPool::NativeWorkerPool()
        : _impl(std::make_unique<Impl>())
    {
    }

    NativeWorkerPool::~NativeWorkerPool()
    {
        Stop();
    }

    bool NativeWorkerPool::Start(uint32_t workerCount)
    {
        if (workerCount == 0) return false;
        std::unique_lock<std::mutex> lock(_impl->mutex);
        if (_impl->accepting) return _impl->workers.size() == workerCount;
        if (!_impl->workers.empty()) return false;
        _impl->stopRequested.store(false, std::memory_order_relaxed);

        try
        {
            // Keep the lifecycle mutex until the complete stable worker set is
            // visible. Submit() must not publish against a partially built set.
            _impl->workers.reserve(workerCount);
            for (uint32_t i = 0; i < workerCount; ++i)
            {
                auto worker = std::make_unique<Impl::WorkerState>();
                auto* raw = worker.get();
                worker->thread = std::thread([this, i, raw]
                {
                    _impl->WorkerLoop(i, raw);
                });
                _impl->workers.push_back(std::move(worker));
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
            std::unique_lock<std::mutex> lock(_impl->mutex);
            if (_impl->workers.empty())
            {
                _impl->accepting = false;
                return;
            }
            _impl->accepting = false;
            _impl->idle.wait(lock, [this]
            {
                return _impl->outstandingBatches == 0;
            });
            _impl->stopRequested.store(true, std::memory_order_release);
        }
        for (auto& worker : _impl->workers)
            worker->wake.release();
        for (auto& worker : _impl->workers)
            if (worker->thread.joinable()) worker->thread.join();
        _impl->workers.clear();

        std::lock_guard<std::mutex> lock(_impl->mutex);
        _impl->stopRequested.store(false, std::memory_order_relaxed);
    }

    bool NativeWorkerPool::Submit(
        void* context,
        uint32_t slotCount,
        RunSlotFn runSlot,
        CompletionFn completion)
    {
        if (slotCount == 0 || !runSlot || !completion) return false;

        Impl::BatchDescriptor* publishedBatch = nullptr;
        {
            std::lock_guard<std::mutex> lock(_impl->mutex);
            if (!_impl->accepting) return false;

            auto* descriptor = _impl->AcquireDescriptorLocked();
            descriptor->Reset(context, runSlot, completion, slotCount);
            _impl->pendingBatches.push_back(descriptor);
            ++_impl->outstandingBatches;
            publishedBatch = _impl->PublishNextLocked();
        }

        _impl->WakeTargetWorkers(publishedBatch);
        return true;
    }

    bool NativeWorkerPool::IsRunning() const noexcept
    {
        std::lock_guard<std::mutex> lock(_impl->mutex);
        return _impl->accepting;
    }

    uint32_t NativeWorkerPool::WorkerCount() const noexcept
    {
        std::lock_guard<std::mutex> lock(_impl->mutex);
        return static_cast<uint32_t>(_impl->workers.size());
    }
}
