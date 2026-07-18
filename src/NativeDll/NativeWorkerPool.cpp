#include "NativeWorkerPool.h"

#include <atomic>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>

namespace JobSystem
{
    struct NativeWorkerPool::Impl
    {
        struct Submission
        {
            void* context;
            RunSlotFn runSlot;
            CompletionFn completion;
            std::atomic<uint32_t> remaining;

            Submission(
                void* value,
                RunSlotFn run,
                CompletionFn done,
                uint32_t count) noexcept
                : context(value), runSlot(run), completion(done), remaining(count)
            {
            }
        };

        struct Token
        {
            std::shared_ptr<Submission> submission;
            uint32_t slot;
        };

        mutable std::mutex mutex;
        std::condition_variable workAvailable;
        std::condition_variable idle;
        std::deque<Token> queue;
        std::vector<std::thread> workers;
        size_t activeTokens{ 0 };
        bool accepting{ false };
        bool stopRequested{ false };

        void WorkerLoop() noexcept
        {
            while (true)
            {
                Token token;
                {
                    std::unique_lock<std::mutex> lock(mutex);
                    workAvailable.wait(lock, [this]
                    {
                        return stopRequested || !queue.empty();
                    });
                    if (stopRequested && queue.empty()) return;
                    token = std::move(queue.front());
                    queue.pop_front();
                    ++activeTokens;
                }

                token.submission->runSlot(
                    token.submission->context, token.slot);
                if (token.submission->remaining.fetch_sub(
                    1, std::memory_order_acq_rel) == 1)
                {
                    token.submission->completion(token.submission->context);
                }

                {
                    std::lock_guard<std::mutex> lock(mutex);
                    --activeTokens;
                    if (queue.empty() && activeTokens == 0) idle.notify_all();
                }
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
                return _impl->queue.empty() && _impl->activeTokens == 0;
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
        auto submission = std::make_shared<Impl::Submission>(
            context, runSlot, completion, slotCount);
        {
            std::lock_guard<std::mutex> lock(_impl->mutex);
            if (!_impl->accepting) return false;
            for (uint32_t slot = 0; slot < slotCount; ++slot)
                _impl->queue.push_back(Impl::Token{ submission, slot });
        }
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
