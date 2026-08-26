#pragma once

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <exception>
#include <functional>
#include <memory>
#include <mutex>
#include <vector>

// Forward declarations for chunk/entity batch data
struct ChunkJobData;
struct EntityBatchData;

#ifdef __cpp_lib_hardware_interference_size
using std::hardware_destructive_interference_size;
#else
constexpr size_t hardware_destructive_interference_size = 64;
#endif

namespace JobSystem {

    enum class ChunkScheduleMode : int {
        PublishNoAssist = 0,
        PublishAssist = 1,
        DeferTinyOnly = 2,
        ImmediateNative = 3,
        DeferredPublish = 4,
        DeferredPublishNoAssist = 5,
    };

    // EntityBatchData is the shared execution ABI for both native IJobChunk
    // and IJobEntity. Keep the semantic job kind explicit so scheduling
    // policy does not have to guess it from the callback representation.
    enum class EcsJobKind : int {
        Chunk = 0,
        Entity = 1,
    };

    // 无锁 continuation 链节点（前向声明；完整定义在 JobSystem.cpp）。
    struct ContinuationNode;

    // 对齐到缓存行，避免伪共享
    struct alignas(hardware_destructive_interference_size) HandleState {
        std::atomic<uint32_t> refCount{ 1 };
        std::atomic<bool> completed{ false };
        std::atomic<bool> backendRetired{ true };
        std::atomic<uint64_t> diagnosticBatchId{ 0 };

        // 延续任务相关。无锁快路径：单个 continuation 经 CAS 存进原子槽（堆节点）、
        // CompleteState 原子摘取执行；多 continuation（同 handle 扇出，罕见）才退化到
        // mtx + vector。C# HandleStateView 仅读前 8 字节（refCount/completed），
        // 此布局变化不破坏托管侧 ABI。
        std::atomic<ContinuationNode*> continuationSlot{ nullptr };
        std::atomic<bool> hasExtraContinuations{ false };
        std::vector<std::function<void()>> continuations;
        std::mutex mtx;  // 仅保护多 continuation 溢出 + retire 协调
        // 条件变量：Complete() wait_for 超时实现"周期回访"（避免病理场景死锁）。
        // wait_for 期间释放 mtx；notify_all 在 CompleteState 中与 completed 一起发。
        std::condition_variable completedCv;

        // 依赖（经 CombineDependencies 或 Schedule 依赖参数建立）。
        // 单依赖走 `dependency`（热路径）；合并走 `dependencies` 向量。
        // 均持有引用（AcquireState），RecycleState 释放，保证 handle 被丢弃后不悬垂。
        //
        // 【约束】依赖图必须是无环 DAG：若用户构造循环依赖（A→B→A），
        // Complete() 永不返回。运行时不做环检测（开销大），调用方必须保证无环。
        HandleState* dependency{ nullptr };
        std::vector<HandleState*> dependencies;

        // C++ 异常协议：退役时从 BatchState 转移的第一个回调异常；
        // Complete() 在退役完成后 std::rethrow_exception 抛给调用方（TBB 语义）。
        // 仅在 C++ 回调路径使用（C# 侧在 NativeJobCore.cs 记录，不走此字段）。
        std::exception_ptr batchExceptionPtr;

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

    // ---------- Internal helpers ----------
    void RecycleState(HandleState* state) noexcept;
    HandleState* CreateState(bool completed = false);
    void AcquireState(HandleState* state) noexcept;
    void ReleaseState(HandleState* state) noexcept;
    void CompleteState(HandleState* state);
    void AddContinuationOrRunNow(HandleState* state, std::function<void()> continuation);
    int CurrentWorkerCount();

    // C# 注册"当前 batch"回调。每次 job 执行窗口入口调 cb(batchId)、
    // 出口 cb(0)，C# 异常按此绑定到具体 batch。
    void RegisterCurrentBatchIdCallback(void (*cb)(uint64_t)) noexcept;

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
        uint64_t directAssistClaims;
        uint64_t exhaustedTickets;
        uint64_t scheduleToPublishEwmaNs;
        uint64_t publishToFirstMainClaimEwmaNs;
        uint64_t publishToFirstWorkerClaimEwmaNs;
        uint64_t publishToCompletionEwmaNs;
        uint64_t queueLockWaitEwmaNs;
        uint64_t perRangeExecEwmaNs;
        uint64_t assistExecPctEwma;
        uint64_t completionOverheadUs;

        // Tile/partition statistics. These are appended for ABI compatibility.
        uint64_t workerTargetTotal;
        uint64_t totalTilesPublished;
        uint64_t localTiles;
        uint64_t stolenTiles;
        uint64_t assistTiles;
        uint64_t stealAttempts;
        uint64_t stealSuccesses;
        uint64_t permitsReleased;
        uint64_t victimScans;
        uint64_t stealEmptyExits;
        uint64_t batchStorageCreated;
        uint64_t batchStorageReused;
        uint64_t batchStorageReturned;
        uint64_t batchStorageDropped;
        uint64_t submitToFirstWorkerEwmaNs;
        uint64_t workerStartSpreadEwmaNs;
        uint64_t lastTileToTopologyDoneEwmaNs;
        uint64_t completeWakeToReturnEwmaNs;
        uint64_t nativeBatches;
        uint64_t invalidBackendSelections;

        // Exact per-batch timing distribution, reset by ResetStatsSnapshot().
        // Fields are appended to preserve the existing native/managed ABI prefix.
        uint64_t timingSampleCount;
        uint64_t timingSamplesDropped;
        uint64_t batchTotalP50Ns;
        uint64_t batchTotalP95Ns;
        uint64_t batchTotalP99Ns;
        uint64_t batchTotalMaxNs;
        uint64_t submitToFirstWorkerP50Ns;
        uint64_t submitToFirstWorkerP95Ns;
        uint64_t submitToFirstWorkerP99Ns;
        uint64_t submitToFirstWorkerMaxNs;
        uint64_t workerStartSpreadP50Ns;
        uint64_t workerStartSpreadP95Ns;
        uint64_t workerStartSpreadP99Ns;
        uint64_t workerStartSpreadMaxNs;
        uint64_t executionSpanP50Ns;
        uint64_t executionSpanP95Ns;
        uint64_t executionSpanP99Ns;
        uint64_t executionSpanMaxNs;
        uint64_t maxRangeP50Ns;
        uint64_t maxRangeP95Ns;
        uint64_t maxRangeP99Ns;
        uint64_t maxRangeMaxNs;
        uint64_t slowBatchId;
        uint64_t slowBatchTotalNs;
        uint64_t slowSubmitToFirstWorkerNs;
        uint64_t slowWorkerStartSpreadNs;
        uint64_t slowExecutionSpanNs;
        uint64_t slowMaxRangeNs;
        uint64_t slowCoreMigrations;
        uint64_t slowAssistTiles;
        uint64_t slowRangeThreadCpuNs;
        uint64_t slowRangeThreadCycles;
        uint64_t slowBatchMinRangeThreadCycles;
        uint64_t slowBatchAverageRangeThreadCycles;
        int32_t slowRangeIndex;
        int32_t slowRangeWorker;
        int32_t slowRangeStartLogicalCore;
        int32_t slowRangeEndLogicalCore;
        int32_t slowRangeStartPhysicalCore;
        int32_t slowRangeEndPhysicalCore;
    };

    void GetStatsSnapshot(JobSystemStatsSnapshot* stats) noexcept;
    void ResetStatsSnapshot() noexcept;
    void SetTimingDiagnosticsEnabled(bool enabled) noexcept;
    void UpdateUnsignedEwma(std::atomic<uint64_t>& target, uint64_t sample) noexcept;

    class Scheduler {
    public:
        static void Initialize(int numThreads = 0);
        static void Shutdown();
        static void PrewakeWorkers();
        static void ConfigureTilesPerWorker(int tilesPerWorker);
        static void ConfigureGuided(int enabled, int k, int floor);

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

        // func 签名为: void callback(void* context, const ChunkJobData* chunkData)
        static JobHandle ScheduleChunks(
            void (*func)(void*, const struct ChunkJobData*), void* context,
            void (*cleanup)(void*) = nullptr,
            const struct ChunkJobData* chunks = nullptr,
            int chunkCount = 0,
            const JobHandle& dependency = {},
            ChunkScheduleMode mode = ChunkScheduleMode::PublishAssist,
            int workerCap = 0,
            int rangeSize = 0,
            uint32_t unitGeneration = 0);

        static JobHandle ScheduleChunkRanges(
            void (*func)(void*, const struct ChunkJobData*, int, int), void* context,
            void (*cleanup)(void*) = nullptr,
            const struct ChunkJobData* chunks = nullptr,
            int chunkCount = 0,
            const JobHandle& dependency = {},
            ChunkScheduleMode mode = ChunkScheduleMode::PublishAssist,
            int workerCap = 0,
            int rangeSize = 0,
            uint32_t unitGeneration = 0);

        static JobHandle ScheduleEntityBatches(
            void (*func)(void*, const struct EntityBatchData*, int, int), void* context,
            void (*cleanup)(void*) = nullptr,
            const struct EntityBatchData* batches = nullptr,
            int batchCount = 0,
            const JobHandle& dependency = {},
            ChunkScheduleMode mode = ChunkScheduleMode::PublishAssist,
            int workerCap = 0,
            int rangeSize = 0,
            EcsJobKind jobKind = EcsJobKind::Entity,
            uint32_t unitGeneration = 0);
    };

} // namespace JobSystem
