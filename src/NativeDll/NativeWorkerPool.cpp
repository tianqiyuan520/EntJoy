#include "NativeWorkerPool.h"

#include <atomic>
#include <condition_variable>
#include <deque>
#include <memory>
#include <mutex>
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
            std::atomic<uint32_t> nextSlot{ 0 };
            std::atomic<uint32_t> remaining{ 0 };

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
                nextSlot.store(0, std::memory_order_relaxed);
                remaining.store(count, std::memory_order_relaxed);
            }
        };

        mutable std::mutex mutex;
        std::condition_variable workAvailable;
        std::condition_variable idle;

        // Only Submit and batch completion touch these queues. Worker hot paths
        // load activeBatch and claim slots entirely through atomics.
        std::deque<BatchDescriptor*> pendingBatches;
        std::vector<std::unique_ptr<BatchDescriptor>> descriptorStorage;
        std::vector<BatchDescriptor*> freeDescriptors;
        std::atomic<BatchDescriptor*> activeBatch{ nullptr };
        std::atomic<uint64_t> generation{ 0 };

        std::vector<std::thread> workers;
        size_t outstandingBatches{ 0 };
        bool accepting{ false };
        bool stopRequested{ false };

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

        bool PublishNextLocked() noexcept
        {
            if (activeBatch.load(std::memory_order_relaxed) || pendingBatches.empty())
                return false;

            auto* next = pendingBatches.front();
            pendingBatches.pop_front();
            activeBatch.store(next, std::memory_order_release);
            generation.fetch_add(1, std::memory_order_release);
            return true;
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
            bool wakeWorkers = false;
            {
                std::lock_guard<std::mutex> lock(mutex);
                if (activeBatch.load(std::memory_order_relaxed) == descriptor)
                    activeBatch.store(nullptr, std::memory_order_release);
                wakeWorkers = PublishNextLocked();
            }

            if (wakeWorkers)
                workAvailable.notify_all();

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

        void ExecutePublishedBatch() noexcept
        {
            auto* descriptor = AcquireActiveBatch();
            if (!descriptor) return;

            bool completedBatch = false;
            while (true)
            {
                const uint32_t slot = descriptor->nextSlot.fetch_add(
                    1, std::memory_order_relaxed);
                if (slot >= descriptor->slotCount)
                    break;

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

        void WorkerLoop() noexcept
        {
            uint64_t observedGeneration;
            {
                // Start() may return and Submit() may publish before this thread
                // enters its loop. Treat an already-active generation as unseen so
                // a late-starting worker cannot miss the first batch.
                std::lock_guard<std::mutex> lock(mutex);
                observedGeneration = generation.load(std::memory_order_acquire);
                if (activeBatch.load(std::memory_order_acquire))
                    --observedGeneration;
            }
            while (true)
            {
                {
                    std::unique_lock<std::mutex> lock(mutex);
                    workAvailable.wait(lock, [this, observedGeneration]
                    {
                        return stopRequested ||
                            generation.load(std::memory_order_acquire) != observedGeneration;
                    });
                    if (stopRequested) return;
                    observedGeneration = generation.load(std::memory_order_acquire);
                }

                ExecutePublishedBatch();
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
        {
            std::lock_guard<std::mutex> lock(_impl->mutex);
            if (_impl->accepting) return _impl->workers.size() == workerCount;
            if (!_impl->workers.empty()) return false;
            _impl->stopRequested = false;
            _impl->accepting = true;
        }

        try
        {
            _impl->workers.reserve(workerCount);
            for (uint32_t i = 0; i < workerCount; ++i)
                _impl->workers.emplace_back([this] { _impl->WorkerLoop(); });
        }
        catch (...)
        {
            Stop();
            throw;
        }
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
            _impl->stopRequested = true;
        }
        _impl->workAvailable.notify_all();
        for (auto& worker : _impl->workers)
            if (worker.joinable()) worker.join();
        _impl->workers.clear();

        std::lock_guard<std::mutex> lock(_impl->mutex);
        _impl->stopRequested = false;
    }

    bool NativeWorkerPool::Submit(
        void* context,
        uint32_t slotCount,
        RunSlotFn runSlot,
        CompletionFn completion)
    {
        if (slotCount == 0 || !runSlot || !completion) return false;

        bool wakeWorkers = false;
        {
            std::lock_guard<std::mutex> lock(_impl->mutex);
            if (!_impl->accepting) return false;

            auto* descriptor = _impl->AcquireDescriptorLocked();
            descriptor->Reset(context, runSlot, completion, slotCount);
            _impl->pendingBatches.push_back(descriptor);
            ++_impl->outstandingBatches;
            wakeWorkers = _impl->PublishNextLocked();
        }

        if (wakeWorkers)
            _impl->workAvailable.notify_all();
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
