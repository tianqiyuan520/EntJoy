#pragma once

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <vector>

// Forward declarations for taskflow
namespace tf {
class Executor;
}

// Forward declaration for ChunkJobData
struct ChunkJobData;
struct EntityBatchData;

#ifdef __cpp_lib_hardware_interference_size
using std::hardware_destructive_interference_size;
#else
constexpr size_t hardware_destructive_interference_size = 64;
#endif

namespace JobSystem {

    using AssistStepCallback = bool (*)(void*) noexcept;
    using AssistReleaseCallback = void (*)(void*) noexcept;

    struct AssistState {
        std::atomic<AssistStepCallback> callback{ nullptr };
        std::atomic<void*> context{ nullptr };
        std::atomic<AssistReleaseCallback> readersDrained{ nullptr };
        std::atomic<int> readers{ 0 };
    };

    enum class ChunkScheduleMode : int {
        PublishNoAssist = 0,
        PublishAssist = 1,
        DeferTinyOnly = 2,
        ImmediateNative = 3,
        DeferredPublish = 4,
        DeferredPublishNoAssist = 5,
    };

    // 对齐到缓存行，避免伪共享
    struct alignas(hardware_destructive_interference_size) HandleState {
        std::atomic<uint32_t> refCount{ 1 };
        std::atomic<bool> completed{ false };
        std::atomic<int> waiterCount{ 0 };

        // 延续任务相关（保留但极少使用）
        std::function<void()> inlineContinuation;
        std::vector<std::function<void()>> continuations;
        // Complete() and workers share one raw, allocation-free work claimant.
        AssistState assist;
        // Unity-style deferred schedule: Complete() 可先尝试 claim pending work 并同步执行。
        std::atomic<AssistStepCallback> pendingCallback{ nullptr };
        std::atomic<void*> pendingContext{ nullptr };
        std::mutex mtx;  // 保护 continuations

#ifdef _DEBUG
        std::atomic<uint32_t> generation{ 0 };
        std::atomic<bool> inPool{ false };
#endif

        explicit HandleState(bool initialCompleted = false) noexcept
            : completed(initialCompleted) {
        }
    };

    class JobHandle {
    public:
        JobHandle() noexcept = default;
        explicit JobHandle(HandleState* state, bool addRef = false) noexcept;
        JobHandle(const JobHandle& other) noexcept;
        JobHandle(JobHandle&& other) noexcept;
        JobHandle& operator=(const JobHandle& other) noexcept;
        JobHandle& operator=(JobHandle&& other) noexcept;
        ~JobHandle();

        void Complete() const;
        bool IsCompleted() const noexcept;
        HandleState* State() const noexcept;

        static void Acquire(HandleState* state) noexcept;
        static void Release(HandleState* state) noexcept;

    static JobHandle CombineDependencies(const std::vector<JobHandle>& handles);

private:
    HandleState* _state{ nullptr };
};

// ---------- Internal helpers (用于 Exports.cpp 等) ----------
void RecycleState(HandleState* state) noexcept;
HandleState* CreateState(bool completed = false);
void AcquireState(HandleState* state) noexcept;
void ReleaseState(HandleState* state) noexcept;
void CompleteState(HandleState* state);
void AddContinuationOrRunNow(HandleState* state, std::function<void()> continuation);
int ResolveWorkerCount(int numThreads);
std::shared_ptr<tf::Executor> EnsureExecutor();

struct JobSystemStatsSnapshot {
    uint64_t completeWaitLoops;
    uint64_t assistAttempts;
    uint64_t assistExecuted;
    uint64_t frameTasksSubmitted;
    uint64_t frameTasksCompleted;
    uint64_t workerExecutedRanges;
    uint64_t mainExecutedRanges;
    uint64_t stealCount;
    uint64_t parkWakeCount;
    uint64_t deferredRuns;
    uint64_t publishedJobs;
    uint64_t prewakeCount;
    uint64_t hotSpinHits;
    uint64_t waitFallbacks;
    uint64_t notifiedWorkers;
    uint64_t workerClaimedTokens;
    uint64_t mainClaimedTokens;
    uint64_t coldBatches;
    uint64_t activeWorkersPeak;
    uint64_t wakeLatencyEwmaNs;
    uint64_t scheduleModePublishNoAssist;
    uint64_t scheduleModePublishAssist;
    uint64_t scheduleModeDeferTinyOnly;
    uint64_t scheduleModeImmediateNative;
    uint64_t scheduleModeDeferredPublish;
    uint64_t scheduleModeDeferredPublishNoAssist;
    int frameQueueDepthPeak;
};

void GetStatsSnapshot(JobSystemStatsSnapshot* stats) noexcept;
void ResetStatsSnapshot() noexcept;

    class Scheduler {
    public:
        static void Initialize(int numThreads = 0);
        static void Shutdown();
        static void PrewakeWorkers();
        static void KeepWorkersWarm(int microseconds);
        static void SetFrameLowLatencyMode(bool enabled);
        static void FlushScheduledJobs();

        static JobHandle Schedule(
            void (*func)(void*), void* context,
            void (*cleanup)(void*) = nullptr,
            const JobHandle& dependency = {});

        static JobHandle ScheduleParallelForBatch(
            void (*func)(void*, int, int), void* context,
            int length, int batchSize,
            void (*cleanup)(void*) = nullptr,
            const JobHandle& dependency = {});

        static JobHandle ScheduleParallelFor(
            void (*func)(void*, int), void* context,
            int length, int batchSize = 0,
            void (*cleanup)(void*) = nullptr,
            const JobHandle& dependency = {});

        static JobHandle ScheduleFor(
            void (*func)(void*, int), void* context,
            int length,
            void (*cleanup)(void*) = nullptr,
            const JobHandle& dependency = {});

        // 调度多个 Chunk 任务（由 Exports.cpp 委托调用）
        // func 签名为: void callback(void* context, const ChunkJobData* chunkData)
        static JobHandle ScheduleChunks(
            void (*func)(void*, const struct ChunkJobData*), void* context,
            void (*cleanup)(void*) = nullptr,
            const struct ChunkJobData* chunks = nullptr,
            int chunkCount = 0,
            const JobHandle& dependency = {},
            ChunkScheduleMode mode = ChunkScheduleMode::PublishAssist,
            int workerCap = 0,
            int rangeSize = 0);

        // 调度多个 Chunk range 任务；func 每次处理 [start, start + count) 的 chunk range。
        static JobHandle ScheduleChunkRanges(
            void (*func)(void*, const struct ChunkJobData*, int, int), void* context,
            void (*cleanup)(void*) = nullptr,
            const struct ChunkJobData* chunks = nullptr,
            int chunkCount = 0,
            const JobHandle& dependency = {},
            ChunkScheduleMode mode = ChunkScheduleMode::PublishAssist,
            int workerCap = 0,
            int rangeSize = 0);

        static JobHandle ScheduleEntityBatches(
            void (*func)(void*, const struct EntityBatchData*, int, int), void* context,
            void (*cleanup)(void*) = nullptr,
            const struct EntityBatchData* batches = nullptr,
            int batchCount = 0,
            const JobHandle& dependency = {},
            ChunkScheduleMode mode = ChunkScheduleMode::PublishAssist,
            int workerCap = 0,
            int rangeSize = 0);
    };

} // namespace JobSystem
