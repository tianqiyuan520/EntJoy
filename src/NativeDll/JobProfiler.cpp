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
    // trace 开关（命名空间可见）：调用方编译单元用它做快速守卫，
    // trace 关闭时跳过 PushTraceEvent 的跨 TU 调用（内联 relaxed load）。
    std::atomic<bool> g_traceEnabled{ false };
    // 清空与推送的静默期握手：只统计已进入 PushTraceEvent 的线程（tracing 关闭时无流量）。
    std::atomic<int> g_traceInFlight{ 0 };

    namespace
    {
        struct TraceThreadBuffer
        {
            std::array<TraceEvent, kMaxTraceEventsPerThread> entries{};
            std::atomic<int> publishedCount{ 0 };
        };

        std::atomic<uint64_t> g_traceDroppedEvents{ 0 };
        std::atomic<uint64_t> g_traceSequence{ 0 };
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
                return left.sequence < right.sequence;
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
        // 先关门再等在飞推送退场：否则它们会把清空前的 publishedCount 写回（事件复活）。
        const bool wasEnabled = g_traceEnabled.exchange(false, std::memory_order_acq_rel);
        while (g_traceInFlight.load(std::memory_order_acquire) != 0)
            std::this_thread::yield();
        std::lock_guard lock(g_traceRegistryMutex);
        for (const auto& buffer : g_traceBuffers)
            buffer->publishedCount.store(0, std::memory_order_release);
        g_traceDroppedEvents.store(0, std::memory_order_release);
        g_traceSequence.store(0, std::memory_order_release);
        g_traceEnabled.store(wasEnabled, std::memory_order_release);
    }

    void PushTraceEvent(
        TraceEventType type,
        uint64_t batchId,
        int tileIndex,
        int entityStart,
        int entityCount) noexcept
    {
        // 先登记再读开关：与 TraceClear 的「关门 → 等在飞归零」配对，登记晚于关门的推送必然读到 false。
        g_traceInFlight.fetch_add(1, std::memory_order_acq_rel);
        struct InFlightGuard { std::atomic<int>& n; ~InFlightGuard() { n.fetch_sub(1, std::memory_order_acq_rel); } } inFlight{ g_traceInFlight };
        if (!g_traceEnabled.load(std::memory_order_acquire)) return;
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
            g_traceSequence.fetch_add(1, std::memory_order_relaxed) + 1,
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
