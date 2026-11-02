#pragma once
#include <array>
#include <cstdlib>
#include <mutex>
#ifdef ENTJOY_TESTING
#include <atomic>
#endif

namespace JobSystem::ContextMemory
{
    constexpr int BucketCount = 64;
    constexpr int LocalLimit = 32;
    constexpr int SharedLimit = 512;
    struct Bucket { void* head = nullptr; int count = 0; };
    struct SharedBucket : Bucket { std::mutex gate; };
#ifdef ENTJOY_TESTING
    inline std::atomic<int> failRentCount{ 0 };
    inline void FailNextRentForTests(int count) noexcept
    {
        failRentCount.store(count > 0 ? count : 0, std::memory_order_relaxed);
    }
#endif

    // 进程级缓存先于任意 TLS 析构保持有效，避免 DLL/CLR 线程退出顺序依赖。
    inline std::array<SharedBucket, BucketCount>& Shared()
    {
        static auto* buckets = new std::array<SharedBucket, BucketCount>();
        return *buckets;
    }
    inline void Push(Bucket& bucket, void* ptr) noexcept
    {
        *static_cast<void**>(ptr) = bucket.head;
        bucket.head = ptr;
        ++bucket.count;
    }
    inline void* Pop(Bucket& bucket) noexcept
    {
        void* ptr = bucket.head;
        bucket.head = *static_cast<void**>(ptr);
        --bucket.count;
        return ptr;
    }
    struct LocalCache
    {
        std::array<Bucket, BucketCount> buckets;
        void Spill(int index)
        {
            auto& local = buckets[index];
            auto& shared = Shared()[index];
            std::lock_guard<std::mutex> lock(shared.gate);
            while (local.head)
            {
                void* ptr = Pop(local);
                if (shared.count < SharedLimit) Push(shared, ptr);
                else std::free(ptr);
            }
        }
        ~LocalCache()
        {
            for (int i = 0; i < BucketCount; ++i)
                if (buckets[i].head)
                {
                    try { Spill(i); }
                    catch (...) { while (buckets[i].head) std::free(Pop(buckets[i])); }
                }
        }
    };
    inline LocalCache& Local() { thread_local LocalCache cache; return cache; }
    inline int Index(int bytes) noexcept
    {
        // 桶从 0 编号，每桶对应最小的 64 字节整数倍容量。
        return bytes > 0 && bytes <= BucketCount * 64 ? (bytes - 1) >> 6 : -1;
    }

    inline void* Rent(int bytes)
    {
#ifdef ENTJOY_TESTING
        int remaining = failRentCount.load(std::memory_order_relaxed);
        while (remaining > 0)
            if (failRentCount.compare_exchange_weak(remaining, remaining - 1, std::memory_order_relaxed))
                return nullptr;
#endif
        int index = Index(bytes);
        if (index < 0) return bytes > 0 ? std::malloc(bytes) : nullptr;
        auto& local = Local().buckets[index];
        if (local.head) return Pop(local);
        auto& shared = Shared()[index];
        {
            std::lock_guard<std::mutex> lock(shared.gate);
            while (shared.head && local.count < LocalLimit) Push(local, Pop(shared));
        }
        return local.head ? Pop(local) : std::malloc((index + 1) << 6);
    }
    inline void Return(void* ptr, int bytes)
    {
        if (!ptr) return;
        int index = Index(bytes);
        if (index < 0) { std::free(ptr); return; }
        auto& cache = Local();
        auto& local = cache.buckets[index];
        if (local.count == LocalLimit) cache.Spill(index);
        Push(local, ptr);
    }
}
