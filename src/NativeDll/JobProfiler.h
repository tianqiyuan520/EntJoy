#pragma once

#include <atomic>
#include <cstdint>
#include <chrono>
#include <cstring>
#include <algorithm>

// 单条 Profiler 记录 (轻量级，无锁)
struct alignas(16) ProfilerEntry {
    uint64_t jobNameHash;   // Job 名称哈希 (用于聚合)
    uint64_t startCycles;   // 开始时间戳 (std::chrono::steady_clock)
    uint64_t endCycles;     // 结束时间戳
    int32_t  threadIndex;   // Worker 线程索引 (-1 表示未知)
    int32_t  jobType;       // 作业类型: 0=IJob, 1=IJobFor, 2=ParallelFor, 3=ParallelForBatch, 4=Chunk
};

// 无锁环形缓冲区 (多生产者单消费者)
class ProfilerRingBuffer {
public:
    static constexpr size_t kMaxEntries = 1 << 18; // 256K 条目, ~16MB

    ProfilerRingBuffer() = default;

    // 写入一条记录 (多生产者安全)
    void Push(const ProfilerEntry& e) {
        size_t idx = head_.fetch_add(1, std::memory_order_relaxed) & (kMaxEntries - 1);
        entries_[idx] = e;
    }

    // 读取最近的 maxCount 条记录 (消费者)
    size_t Read(size_t maxCount, ProfilerEntry* dst) {
        size_t current = head_.load(std::memory_order_acquire);
        size_t avail = std::min(maxCount, current);
        if (avail == 0) return 0;

        size_t start = (current - avail) & (kMaxEntries - 1);
        for (size_t i = 0; i < avail; ++i) {
            dst[i] = entries_[(start + i) & (kMaxEntries - 1)];
        }
        return avail;
    }

    // 读取并清空 (消费者)
    size_t ReadAll(size_t maxCount, ProfilerEntry* dst) {
        size_t current = head_.exchange(0, std::memory_order_acq_rel);
        size_t avail = std::min(maxCount, current);
        if (avail == 0) return 0;

        size_t start = (current - avail) & (kMaxEntries - 1);
        for (size_t i = 0; i < avail; ++i) {
            dst[i] = entries_[(start + i) & (kMaxEntries - 1)];
        }
        return avail;
    }

    void Clear() {
        head_.store(0, std::memory_order_release);
    }

    size_t Count() const {
        return head_.load(std::memory_order_acquire);
    }

private:
    std::atomic<size_t> head_{ 0 };
    ProfilerEntry entries_[kMaxEntries];
};

// 全局 Profiler 实例 (先声明，供 ProfilerScope 使用)
extern ProfilerRingBuffer g_profilerBuffer;
extern std::atomic<bool> g_profilerEnabled;

// ProfilerScope RAII 计时器 - 仅在 profiler 启用时记录
class ProfilerScope {
    uint64_t nameHash_;
    int32_t  threadIndex_;
    int32_t  jobType_;
    std::chrono::steady_clock::time_point start_;
    ProfilerRingBuffer* buffer_;
    bool active_;
public:
    ProfilerScope(ProfilerRingBuffer* buffer, uint64_t nameHash, int32_t threadIndex, int32_t jobType)
        : buffer_(buffer)
        , nameHash_(nameHash)
        , threadIndex_(threadIndex)
        , jobType_(jobType)
        , active_(buffer_ && g_profilerEnabled.load(std::memory_order_relaxed))
    {
        if (active_) {
            start_ = std::chrono::steady_clock::now();
        }
    }

    ~ProfilerScope() {
        if (active_) {
            auto end = std::chrono::steady_clock::now();
            ProfilerEntry entry;
            entry.jobNameHash = nameHash_;
            entry.startCycles = static_cast<uint64_t>(start_.time_since_epoch().count());
            entry.endCycles   = static_cast<uint64_t>(end.time_since_epoch().count());
            entry.threadIndex = threadIndex_;
            entry.jobType     = jobType_;
            buffer_->Push(entry);
        }
    }

    // 手动记录完成 (用于提前结束计时)
    void Complete() {
        if (active_) {
            auto end = std::chrono::steady_clock::now();
            ProfilerEntry entry;
            entry.jobNameHash = nameHash_;
            entry.startCycles = static_cast<uint64_t>(start_.time_since_epoch().count());
            entry.endCycles   = static_cast<uint64_t>(end.time_since_epoch().count());
            entry.threadIndex = threadIndex_;
            entry.jobType     = jobType_;
            buffer_->Push(entry);
            active_ = false; // 防止析构时重复写入
        }
    }
};

// Worker 线程索引管理
class WorkerIndexManager {
public:
    // 分配一个新的 worker 索引
    static int AllocateIndex() {
        static std::atomic<int> nextIndex{ 0 };
        return nextIndex.fetch_add(1, std::memory_order_relaxed);
    }

    // 获取当前线程的索引（注意：Get/Set 共享同一个 thread_local）
    static int GetCurrentIndex() {
        return CurrentIndexRef();
    }

    // 设置当前线程的索引
    static void SetCurrentIndex(int index) {
        CurrentIndexRef() = index;
    }

private:
    // 所有访问统一经过此函数返回的引用，确保跨函数共享同一个 thread_local
    static int& CurrentIndexRef() {
        thread_local int tls_index = -1;
        return tls_index;
    }
public:
};
