#pragma once

#include <cctype>
#include <cstddef>
#include <cstdint>
#include "WorkerSnapshot.h"

#ifdef _WIN32
#ifdef JOB_SYSTEM_EXPORT
#define JOB_API __declspec(dllexport)
#else
#define JOB_API __declspec(dllimport)
#endif
#else
#define JOB_API __attribute__((visibility("default")))
#endif

// Forward declarations
struct ChunkJobData;
struct EntityBatchData;
struct ProfilerEntry;
struct JobSystemTuningNative;
struct JobSystemStatsNative;
namespace JobSystem { struct TraceEvent; }

extern "C" {

// Stable C ABI revision. Optional exports may be added without a bump.
JOB_API uint32_t JobSystem_GetAbiVersion();

    typedef void (*JobFunc)(void* context);
    typedef void (*IndexJobFunc)(void* context, int index);
    typedef void (*BatchJobFunc)(void* context, int startIndex, int count);
    typedef void (*ContextCleanupFunc)(void* context);
    // Chunk 任务回调：context 为 C# 传入的自定义数据，chunkData 为当前 Chunk 的描述块
    typedef void (*ChunkJobFunc)(void* context, const struct ChunkJobData* chunkData);
    typedef void (*ChunkRangeJobFunc)(void* context, const struct ChunkJobData* chunks, int startIndex, int count);
    typedef void (*EntityBatchRangeJobFunc)(void* context, const struct EntityBatchData* batches, int startIndex, int count);

    JOB_API int JobSystem_Initialize(int numThreads);
    // 强制启动 Dear ImGui 调试面板并开始监听（不依赖 ENTJOY_DEBUG 环境变量，幂等）。
    // 由 C# NativeJobScheduler.LaunchDebuggerGUI() 调用。
    JOB_API void JobDebuggerGUI_Launch();
    // Unity JobsUtility.JobWorkerCount equivalent: the number of persistent
    // job workers selected when the scheduler was initialized.
    JOB_API int JobSystem_GetWorkerCount();
    JOB_API void JobSystem_Shutdown();
    JOB_API void JobSystem_PrewakeWorkers();
    // 等所有在飞批退役，但不关 worker。老 DLL 无此导出 ⇒ 托管侧按可选导出处理（TryGetExport）。
    JOB_API void JobSystem_DrainAll();
    JOB_API void JobSystem_ConfigureTilesPerWorker(int tilesPerWorker);
    JOB_API void JobSystem_ConfigureGuided(int enabled, int k, int floor);
    // 启用/关闭 per-job 自动 batch（JobCostCache）。0=关闭（默认，纯 tpw=4），
    // 1=启用（worker 按 per-job 每元素成本 EWMA 自动求解最优 tile 数）。
    JOB_API void JobSystem_SetJobCostCacheEnabled(int enabled);
    // 把 `ENTJOY_JOB_BATCH_BY_NAME` 里按 job 名登记的槽位绑定到该 job 实际派发用的函数指针。
    // 由转译器生成的绑定在 `NativeExports` 静态构造里逐个 job 调用（名字 = `Type.Name`）。
    // 返回值：1 = 该名字在表里（已绑定）；0 = 表里没有这个名字。未设置该 env 时恒为 0 且无副作用。
    JOB_API int JobSystem_BindBatchName(const char* name, void* func);

    // 注册托管 Persistent 分配器回调（NativeContainers.h 的 UnsafeList 扩容/释放走托管侧，
    // 杜绝原生 free 内部指针导致的堆损坏）。alloc/free 参数为 C# 侧函数指针（cdecl）。
    typedef void* (*PersistentAllocCallback)(int32_t size);
    typedef void  (*PersistentFreeCallback)(void* ptr);
    JOB_API void JobSystem_RegisterPersistentAllocator(PersistentAllocCallback alloc, PersistentFreeCallback free);
    JOB_API void JobSystem_RegisterAllocator(int32_t label, PersistentAllocCallback alloc, PersistentFreeCallback free);

    // 注册"当前 batch"回调。每次 job 执行窗口入口调 cb(batchId)、出口 cb(0)，
    // C# 异常按 batch 归属。
    typedef void (*CurrentBatchIdCallback)(uint64_t batchId);
    JOB_API void JobSystem_RegisterCurrentBatchId(CurrentBatchIdCallback cb);

    // 注册"batch→Job 名"解析回调，供 Dear ImGui Timeline 显示 Job 名。
    // C# 侧维护 batchId→名字映射；GUI 线程按需查询。cb 返回写入 buf 的名长（0=无映射）。
    // 第二个参数为"清空"回调：GUI 线程退出时由 native 调用，让 C# 清空累积的字典并关闭捕获。
    typedef int (*BatchJobNameResolver)(uint64_t batchId, char* buf, int bufLen);
    typedef void (*BatchJobNameClear)();
    JOB_API void JobSystem_RegisterNameResolver(BatchJobNameResolver cb, BatchJobNameClear clearCb);
    // 供 ImGui GUI 读取已注册的解析器（无则返回 nullptr）
    JOB_API const BatchJobNameResolver& JobSystem_GetNameResolver();
    // 供 ImGui GUI 在退出时触发 C# 清理
    JOB_API void JobSystem_ClearNameResolver();
    // C# 直接调用（ISPC-MT 等方法直跑）时报告一次"发布"，计入 published + Activity
    JOB_API void JobSystem_RecordDirectCall(const char* jobName, unsigned int tiles);
    // 直调执行窗口（transpiler 包装器在 native 调用前后成对调用，事件驱动开/关泳道窗口）
    JOB_API uint64_t JobSystem_BeginDirectCall(const char* jobName, unsigned int tiles);
    JOB_API void JobSystem_EndDirectCall(uint64_t id);

    JOB_API void* JobSystem_Schedule(JobFunc func, void* context, ContextCleanupFunc cleanup, void* dependency);
    JOB_API void* JobSystem_ScheduleFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup, int length, void* dependency);
    JOB_API void* JobSystem_ScheduleParallelForBatch(BatchJobFunc func, void* context, ContextCleanupFunc cleanup, int length, int batchSize, void* dependency);
    // 同 `JobSystem_ScheduleParallelForBatch`，但带调用点声明的认领几何
    //   （claimGeom：0=Auto 1=Spread 2=Adjacent，与 C# `ClaimPolicy` 同值）。旧导出保留 ⇒ 老绑定不受影响；
    //   C# 侧用 `TryGetExport` 探测：缺这个导出时退回旧导出（claimGeom 被忽略）。
    JOB_API void* JobSystem_ScheduleParallelForBatchEx(BatchJobFunc func, void* context, ContextCleanupFunc cleanup, int length, int batchSize, int claimGeom, void* dependency);
    // The range adapter loops over Execute(index); adjacent ranges may be coalesced.
    JOB_API void* JobSystem_ScheduleParallelForRange(BatchJobFunc func, void* context, ContextCleanupFunc cleanup, int length, int batchSize, int claimGeom, void* dependency);

    // Borrow the source for this call; the scheduled job owns a pooled copy.
    // Status: 0 success, 1 invalid source/size/output, 2 allocation failure, 3 submission failure.
    JOB_API int JobSystem_ScheduleCopiedContext(JobFunc func, const void* source, int bytes, void* dependency, void** outHandle);
    JOB_API int JobSystem_ScheduleParallelForCopiedContext(BatchJobFunc func, const void* source, int bytes,
        int length, int batchSize, int claimGeom, int indexedRanges, void* dependency, void** outHandle);

    // ── 显式批提交（BatchScope.Submit 用）：一次 P/Invoke 提交一组 job，句柄回写 outHandles ──
    // kind: 0=IJob(JobFunc) 1=IJobFor(IndexJobFunc, length) 2=IJobParallelFor(BatchJobFunc, length+batchSize)
    // 内部 deferNotify 窗口：全部 submit 后统一唤醒一次（依赖未完成路径由 continuation 照常执行）。
    // 失败槽位 outHandles[i]=nullptr；返回成功提交数。
    typedef struct JobBatchDesc
    {
        unsigned char kind;
        unsigned char reserved[3];
        void* func;
        void* context;
        ContextCleanupFunc cleanup;
        void* dependency;
        int length;
        int batchSize;
    } JobBatchDesc;
    JOB_API int JobSystem_ScheduleBatch(const JobBatchDesc* descs, int count, void** outHandles);

    // ── 隐式批（native 收集）开关：1=启用（主线程直接提交的 tile 路径 job 挂 pending，
    //    EndFrame/Complete 统一提交 + 单次唤醒）；0=关闭并先排空积压。 ──
    JOB_API void JobSystem_SetImplicitBatchEnabled(int enabled);
    // 显式 force point（帧末 EndFrame）：提交全部 pending + 单次唤醒。
    JOB_API void JobSystem_FlushPendingSubmits();

    JOB_API void JobSystem_Complete(void* handle);
    JOB_API uint64_t JobSystem_CompleteAndRelease(void* handle);

    // ── 提交窗口延迟唤醒（deferNotify）：批提交期 Bump 打开 defer（SubmitBatch 跳过逐批
    //    notify），Flush 关闭并统一唤醒一次。支持嵌套（深度计数）；Flush 在归零时广播。 ──
    JOB_API void JobSystem_SubmitDeferBump(void);
    JOB_API void JobSystem_SubmitDeferFlush(void);
    JOB_API void JobSystem_RetainHandle(void* handle);
    JOB_API int JobSystem_IsCompleted(void* handle);
    JOB_API void JobSystem_ReleaseHandle(void* handle);
    JOB_API void JobSystem_ReleaseHandles(void** handles, int count);
    JOB_API void* JobSystem_RentJobContext(int bytes);
    JOB_API void JobSystem_ReturnJobContext(void* context, int bytes);
    JOB_API void JobSystem_ReleaseJobContext(void* context);
    JOB_API uint64_t JobSystem_GetCurrentBatchId();
    JOB_API void* JobSystem_CombineDependencies(void** handles, int count);
    // 存活句柄 state 数（CreateState 分配 - RecycleState 回收）。诊断/测试用：托管侧据此
    // 断言"组合依赖句柄被确定性回收"（不受 ENTJOY_STATS 门控）。
    JOB_API int64_t JobSystem_GetLiveHandleCount();
    // 读 handle 的 diagnosticBatchId（Complete 后 batch 必已 submit，id 已设置）。
    JOB_API uint64_t JobSystem_GetDiagnosticBatchId(void* handle);

    // ---- 调试面板：实时 Worker 状态快照 ----
    // 读取所有 worker 的实时状态快照，写入 buffer（最多 maxCount 条）。
    // 返回实际写入的条目数。WorkerSnapshot 定义在 WorkerSnapshot.h。
    JOB_API int JobSystem_GetWorkerSnapshots(struct WorkerSnapshot* buffer, int maxCount);

    // Combined Schedule+Complete: 调度后立即 inline assist，返回已完成的 handle
    JOB_API void* JobSystem_ScheduleAndCompleteEntityBatchJobEx(
        EntityBatchRangeJobFunc func, void* context, ContextCleanupFunc cleanup,
        const struct EntityBatchData* batches, int batchCount, void* dependency,
        int scheduleMode, int workerCap, int rangeSize, int jobKind, uint32_t unitGeneration);
    
    typedef struct JobSystemTuningNative {
        int spinBeforeWait;
        int assistAfterWaitLoops;
        int assistBurstMax;
        int assistCooldownWaitLoops;
        int minChunkSize;
        int workerPriorityMode; // 0=normal, 1=above_normal
    } JobSystemTuningNative;

    JOB_API void JobSystem_SetTuning(const JobSystemTuningNative* tuning);
    JOB_API void JobSystem_GetTuning(JobSystemTuningNative* tuning);

    // 字段表即结构体定义：改字段只改这一处，offsetof 与布局指纹都从它派生。
#define JOB_STATS_FIELDS(X) \
        X(unsigned long long, completeWaitLoops) \
        X(unsigned long long, assistAttempts) \
        X(unsigned long long, assistExecuted) \
        X(unsigned long long, frameTasksSubmitted) \
        X(unsigned long long, frameTasksCompleted) \
        X(unsigned long long, workerExecutedRanges) \
        X(unsigned long long, mainExecutedRanges) \
        X(unsigned long long, stealCount) \
        X(unsigned long long, parkWakeCount) \
        X(unsigned long long, deferredRuns) \
        X(unsigned long long, publishedJobs) \
        X(unsigned long long, prewakeCount) \
        X(unsigned long long, hotSpinHits) \
        X(unsigned long long, waitFallbacks) \
        X(unsigned long long, notifiedWorkers) \
        X(unsigned long long, workerClaimedTokens) \
        X(unsigned long long, mainClaimedTokens) \
        X(unsigned long long, coldBatches) \
        X(unsigned long long, activeWorkersPeak) \
        X(unsigned long long, wakeLatencyEwmaNs) \
        X(unsigned long long, scheduleModePublishNoAssist) \
        X(unsigned long long, scheduleModePublishAssist) \
        X(unsigned long long, scheduleModeDeferTinyOnly) \
        X(unsigned long long, scheduleModeImmediateNative) \
        X(unsigned long long, scheduleModeDeferredPublish) \
        X(unsigned long long, scheduleModeDeferredPublishNoAssist) \
        X(int, frameQueueDepthPeak) \
        X(unsigned long long, publishToCompletionEwmaNs) /* ABI = 3。C# 与 Unity 侧 port 必须与本结构体同序同步。 */ \
        X(unsigned long long, perRangeExecEwmaNs) \
        X(unsigned long long, assistExecPctEwma) \
        X(unsigned long long, completionOverheadUs) \
        X(unsigned long long, workerTargetTotal) /* Tile/partition fields; keep order in sync with C#. */ \
        X(unsigned long long, totalTilesPublished) \
        X(unsigned long long, localTiles) \
        X(unsigned long long, stolenTiles) \
        X(unsigned long long, assistTiles) \
        X(unsigned long long, stealAttempts) \
        X(unsigned long long, stealSuccesses) \
        X(unsigned long long, permitsReleased) \
        X(unsigned long long, victimScans) \
        X(unsigned long long, stealEmptyExits) \
        X(unsigned long long, batchStorageCreated) \
        X(unsigned long long, batchStorageReused) \
        X(unsigned long long, batchStorageReturned) \
        X(unsigned long long, batchStorageDropped) \
        X(unsigned long long, submitToFirstWorkerEwmaNs) \
        X(unsigned long long, workerStartSpreadEwmaNs) \
        X(unsigned long long, lastTileToTopologyDoneEwmaNs) \
        X(unsigned long long, completeWakeToReturnEwmaNs) \
        X(unsigned long long, nativeBatches) \
        X(unsigned long long, invalidBackendSelections) \
        X(unsigned long long, timingSampleCount) /* Exact per-batch timing distribution; keep order in sync with C#. */ \
        X(unsigned long long, timingSamplesDropped) \
        X(unsigned long long, batchTotalP50Ns) \
        X(unsigned long long, batchTotalP95Ns) \
        X(unsigned long long, batchTotalP99Ns) \
        X(unsigned long long, batchTotalMaxNs) \
        X(unsigned long long, submitToFirstWorkerP50Ns) \
        X(unsigned long long, submitToFirstWorkerP95Ns) \
        X(unsigned long long, submitToFirstWorkerP99Ns) \
        X(unsigned long long, submitToFirstWorkerMaxNs) \
        X(unsigned long long, workerStartSpreadP50Ns) \
        X(unsigned long long, workerStartSpreadP95Ns) \
        X(unsigned long long, workerStartSpreadP99Ns) \
        X(unsigned long long, workerStartSpreadMaxNs) \
        X(unsigned long long, executionSpanP50Ns) \
        X(unsigned long long, executionSpanP95Ns) \
        X(unsigned long long, executionSpanP99Ns) \
        X(unsigned long long, executionSpanMaxNs) \
        X(unsigned long long, maxRangeP50Ns) \
        X(unsigned long long, maxRangeP95Ns) \
        X(unsigned long long, maxRangeP99Ns) \
        X(unsigned long long, maxRangeMaxNs) \
        X(unsigned long long, slowBatchId) \
        X(unsigned long long, slowBatchTotalNs) \
        X(unsigned long long, slowSubmitToFirstWorkerNs) \
        X(unsigned long long, slowWorkerStartSpreadNs) \
        X(unsigned long long, slowExecutionSpanNs) \
        X(unsigned long long, slowMaxRangeNs) \
        X(unsigned long long, slowCoreMigrations) \
        X(unsigned long long, slowAssistTiles) \
        X(unsigned long long, slowRangeThreadCpuNs) \
        X(unsigned long long, slowRangeThreadCycles) \
        X(unsigned long long, slowBatchMinRangeThreadCycles) \
        X(unsigned long long, slowBatchAverageRangeThreadCycles) \
        X(int, slowRangeIndex) \
        X(int, slowRangeWorker) \
        X(int, slowRangeStartLogicalCore) \
        X(int, slowRangeEndLogicalCore) \
        X(int, slowRangeStartPhysicalCore) \
        X(int, slowRangeEndPhysicalCore)

    typedef struct JobSystemStatsNative {
#define JOB_STATS_LOCAL_DECL(type, name) type name;
        JOB_STATS_FIELDS(JOB_STATS_LOCAL_DECL)
#undef JOB_STATS_LOCAL_DECL
    } JobSystemStatsNative;

    // 布局指纹用的 FNV-1a 常量：托管侧 NativeJobCore.cs 必须使用同值。
    inline constexpr uint64_t kLayoutFnvBasis = 1469598103934665603ull;
    inline constexpr uint64_t kLayoutFnvPrime = 1099511628211ull;
    inline constexpr uint64_t kLayoutSalt = 0x9e3779b97f4a7c15ull;

    // 单个字段的指纹：名字（忽略大小写）+ 偏移 + 大小。
    // 忽略大小写是因为 C# 侧字段是 PascalCase、C++ 侧是 camelCase。
    inline constexpr uint64_t JobStatsFieldHash(const char* name, uint64_t offset, uint64_t size)
    {
        uint64_t hash = kLayoutFnvBasis;
        for (const char* p = name; *p != '\0'; ++p)
        {
            const auto lower = static_cast<unsigned char>(std::tolower(static_cast<unsigned char>(*p)));
            hash ^= lower;
            hash *= kLayoutFnvPrime;
        }
        hash = (hash ^ offset) * kLayoutFnvPrime;
        hash = (hash ^ size) * kLayoutFnvPrime;
        return hash;
    }

    // 整个结构体的指纹：字段指纹按加法合成，所以与字段遍历顺序无关，两端可按各自方便的顺序枚举。
    inline constexpr uint64_t JobStatsLayoutFingerprint()
    {
        uint64_t sum = 0;
#define JOB_STATS_LOCAL_HASH(type, name) \
        sum += JobStatsFieldHash(#name, offsetof(JobSystemStatsNative, name), sizeof(JobSystemStatsNative{}.name));
        JOB_STATS_FIELDS(JOB_STATS_LOCAL_HASH)
#undef JOB_STATS_LOCAL_HASH
        return sum + (kLayoutSalt ^ static_cast<uint64_t>(sizeof(JobSystemStatsNative)));
    }

#undef JOB_STATS_FIELDS

    /** 布局指纹：托管侧按自己的结构体反射算出同值，Initialize 时比对（同宽换序也能抓到）。*/
    JOB_API uint64_t JobSystem_GetStatsLayoutFingerprint();

    JOB_API void JobSystem_GetStats(JobSystemStatsNative* stats);
    JOB_API void JobSystem_ResetStats();

    /** `ENTJOY_WAKE_POLL` 的**生效证据**通道（诊断，不是功能）：
     *  提交侧 [跳过写唤醒字, 真的 bump+notify_all] 两个计数；只看耗时曲线无法区分"无效"与"没生效"。
     *  读法：skips 必须 >> wakes（连发小 job 的形状下应差 2~3 个数量级）。
     *  老 DLL 无此导出 ⇒ 调用方必须 TryGetExport（见 NativeJobCore.cs）。*/
    JOB_API void JobSystem_GetWakePollCounters(unsigned long long* skips, unsigned long long* wakes);

    /** 认领几何（`ClaimPolicy`）的**生效证据**：按**声明值**分桶的批数 [spread, adjacent, auto]。
     *  传了 Spread 就必须看到 spread>0（否则说明几何静默降级）。
     *  老 DLL 无此导出 ⇒ 调用方必须 TryGetExport。*/
    JOB_API void JobSystem_GetClaimGeomCounters(unsigned long long* spread, unsigned long long* adjacent,
                                                unsigned long long* autoDecl);
    JOB_API void JobSystem_SetTimingDiagnostics(int enabled);
    JOB_API void JobSystem_SetMainThreadAssist(int enabled);
    JOB_API void JobSystem_SetWorkerAffinity(int enabled);

    /** 布局防御：返回 C++ JobSystemStatsNative 字节数，须与 C# 侧 Marshal.SizeOf 相等，
     *  否则 GetStats 会越界写（新增字段未同步 → 堆损坏）。*/
    JOB_API uint32_t JobSystem_GetStatsSize();

    /** 
     * 调度多个 Chunk 任务，每个 Chunk 并行执行一次 func 回调。
     * @param func        C# 回调函数指针
     * @param context     C# 上下文数据指针（包含 job 拷贝和辅助数据）
     * @param cleanup     所有 Chunk 任务完成后的清理回调
     * @param chunks      ChunkJobData 数组（非托管内存）
     * @param chunkCount  数组长度
     * @param dependency  依赖的 JobHandle 指针（可为 nullptr）
     * @return 新的 JobHandle 指针，表示所有 Chunk 任务完成
     */
    JOB_API void* JobSystem_ScheduleChunkJob(
        ChunkJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const struct ChunkJobData* chunks,
        int chunkCount,
        void* dependency);

    JOB_API void* JobSystem_ScheduleChunkJobEx(
        ChunkJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const struct ChunkJobData* chunks,
        int chunkCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        uint32_t unitGeneration);

    JOB_API void* JobSystem_ScheduleChunkRangeJobEx(
        ChunkRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const struct ChunkJobData* chunks,
        int chunkCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        uint32_t unitGeneration);

    JOB_API void* JobSystem_ScheduleEntityBatchJobEx(
        EntityBatchRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const struct EntityBatchData* batches,
        int batchCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        int jobKind,
        uint32_t unitGeneration);

    // ======================== Profiler API ========================
    // 启用/禁用 Profiler
    JOB_API void JobProfiler_SetEnabled(int enabled);
    JOB_API int  JobProfiler_IsEnabled();

    // 读取并清空所有 Profiler 记录
    // returns: 实际读取的条目数
    JOB_API int  JobProfiler_ReadAll(struct ProfilerEntry* buffer, int maxCount);

    // 清空 Profiler 缓冲
    JOB_API void JobProfiler_Clear();

    JOB_API void Trace_SetEnabled(int enabled);
    JOB_API int Trace_IsEnabled();
    JOB_API int Trace_ReadAll(JobSystem::TraceEvent* buffer, int maxCount);
    JOB_API uint64_t Trace_DroppedEvents();
    JOB_API void Trace_Clear();

} // extern "C"
