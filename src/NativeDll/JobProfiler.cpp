#include "JobProfiler.h"

#include <array>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

#ifdef _WIN32
#include <Windows.h>
#elif defined(__linux__)
#include <sched.h>
#endif

// 全局 Profiler 实例定义
ProfilerRingBuffer g_profilerBuffer;
std::atomic<bool> g_profilerEnabled{ false };

namespace JobSystem
{
    namespace
    {
        struct TraceThreadBuffer
        {
            std::array<TraceEvent, kMaxTraceEventsPerThread> entries{};
            std::atomic<int> publishedCount{ 0 };
        };

        std::atomic<bool> g_traceEnabled{ false };
        std::atomic<uint64_t> g_traceDroppedEvents{ 0 };
        std::mutex g_traceRegistryMutex;
        std::vector<std::unique_ptr<TraceThreadBuffer>> g_traceBuffers;
        thread_local TraceThreadBuffer* g_threadTraceBuffer = nullptr;

        TraceThreadBuffer* RegisterCurrentThread()
        {
            auto buffer = std::make_unique<TraceThreadBuffer>();
            TraceThreadBuffer* raw = buffer.get();
            std::lock_guard lock(g_traceRegistryMutex);
            g_traceBuffers.push_back(std::move(buffer));
            return raw;
        }

        TraceThreadBuffer* EnsureCurrentThreadBuffer() noexcept
        {
            if (g_threadTraceBuffer != nullptr) return g_threadTraceBuffer;
            try
            {
                g_threadTraceBuffer = RegisterCurrentThread();
                return g_threadTraceBuffer;
            }
            catch (...)
            {
                g_traceDroppedEvents.fetch_add(1, std::memory_order_relaxed);
                return nullptr;
            }
        }

        uint64_t TimestampNs() noexcept
        {
            return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now().time_since_epoch()).count());
        }

        int32_t CurrentThreadId() noexcept
        {
#ifdef _WIN32
            return static_cast<int32_t>(::GetCurrentThreadId());
#else
            return static_cast<int32_t>(std::hash<std::thread::id>{}(std::this_thread::get_id()));
#endif
        }

        int32_t CurrentProcessorIndex() noexcept
        {
#ifdef _WIN32
            PROCESSOR_NUMBER processor{};
            ::GetCurrentProcessorNumberEx(&processor);
            return static_cast<int32_t>(processor.Group) * 64 +
                static_cast<int32_t>(processor.Number);
#elif defined(__linux__)
            return static_cast<int32_t>(::sched_getcpu());
#else
            return -1;
#endif
        }
    }

    void TraceSetEnabled(bool enabled) noexcept
    {
        if (enabled) TracePrepareCurrentThread();
        g_traceEnabled.store(enabled, std::memory_order_release);
    }

    void TracePrepareCurrentThread() noexcept
    {
        (void)EnsureCurrentThreadBuffer();
    }

    bool TraceIsEnabled() noexcept
    {
        return g_traceEnabled.load(std::memory_order_acquire);
    }

    int TraceReadAll(TraceEvent* buffer, int maxCount) noexcept
    {
        if (buffer == nullptr || maxCount <= 0) return 0;
        try
        {
            std::vector<TraceEvent> snapshot;
            std::lock_guard lock(g_traceRegistryMutex);
            for (const auto& threadBuffer : g_traceBuffers)
            {
                const int count = (std::min)(
                    threadBuffer->publishedCount.load(std::memory_order_acquire),
                    kMaxTraceEventsPerThread);
                snapshot.insert(
                    snapshot.end(), threadBuffer->entries.begin(),
                    threadBuffer->entries.begin() + count);
            }
            std::sort(snapshot.begin(), snapshot.end(), [](const TraceEvent& left, const TraceEvent& right) {
                return left.timestampNs < right.timestampNs;
            });
            const int readCount = (std::min)(maxCount, static_cast<int>(snapshot.size()));
            std::copy_n(snapshot.begin(), readCount, buffer);
            return readCount;
        }
        catch (...)
        {
            return 0;
        }
    }

    uint64_t TraceDroppedEvents() noexcept
    {
        return g_traceDroppedEvents.load(std::memory_order_acquire);
    }

    void TraceClear() noexcept
    {
        std::lock_guard lock(g_traceRegistryMutex);
        for (const auto& buffer : g_traceBuffers)
            buffer->publishedCount.store(0, std::memory_order_release);
        g_traceDroppedEvents.store(0, std::memory_order_release);
    }

    void PushTraceEvent(
        TraceEventType type,
        uint64_t batchId,
        int tileIndex,
        int entityStart,
        int entityCount) noexcept
    {
        if (!g_traceEnabled.load(std::memory_order_relaxed)) return;
        TraceThreadBuffer* buffer = EnsureCurrentThreadBuffer();
        if (buffer == nullptr) return;
        const int index = buffer->publishedCount.load(std::memory_order_relaxed);
        if (index >= kMaxTraceEventsPerThread)
        {
            g_traceDroppedEvents.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        buffer->entries[index] = TraceEvent{
            TimestampNs(),
            batchId,
            tileIndex,
            entityStart,
            entityCount,
            CurrentThreadId(),
            CurrentProcessorIndex(),
            static_cast<int16_t>(WorkerIndexManager::GetCurrentIndex()),
            static_cast<uint16_t>(type) };
        buffer->publishedCount.store(index + 1, std::memory_order_release);
    }
}
