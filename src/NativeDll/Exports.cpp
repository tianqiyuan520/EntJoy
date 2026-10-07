#include "Exports.h"
#include "JobSystem.h"
#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"
#include "ThreadAffinity.h"
#include "ChunkJobData.h"
#include "EntityBatchData.h"
#include "JobProfiler.h"
#include "JobDebuggerGUI.h"
#include "NativeContainers.h"
#include <cstdio>

// 加载期输出当前 SIMD 配置（DLL 加载时执行）
//
// 【约束】**加载期（`_CRT_INIT` = DllMain 期，持有 loader lock）一律不做 I/O**：
//   stderr 可能是父进程（Godot `_console.exe` / Start-Process）的**管道**，在 loader lock
//   里写管道会阻塞；加载期任一环节失败的表现就是 `ERROR_DLL_INIT_FAILED (0x8007045A)`
//   （整个 DLL 载不进来）。
//   ⇒ 所有加载期 banner 先追加进内存缓冲（实现与说明在 JobSystemInternal.h + JobSystem.cpp），
//     由 `JobSystem_Initialize()` 一次性 flush 到 stderr；输出文本（含 `[SIMD] …` 与
//     `[JOBBATCHTABLE] …`）与原先逐字节一致，只是**推迟到 Initialize**。
struct SimdInfo {
    SimdInfo() {
#if defined(__AVX2__)
        JobSystem::LoadBannerAppend("[SIMD] AVX2 8-wide\n");
#elif defined(__AVX__)
        JobSystem::LoadBannerAppend("[SIMD] AVX 8-wide\n");
#elif defined(__SSE4_2__) || defined(__SSE4_1__) || defined(__SSE__) || defined(_M_X64)
        JobSystem::LoadBannerAppend("[SIMD] SSE4 4-wide\n");
#elif defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
        JobSystem::LoadBannerAppend("[SIMD] NEON 4-wide\n");
#else
        JobSystem::LoadBannerAppend("[SIMD] SCALAR 1-wide\n");
#endif
    }
} g_simdInfo;

static JobSystem::HandleState* fromHandle(void* ptr)
{
    return static_cast<JobSystem::HandleState*>(ptr);
}

static void* toHandle(const JobSystem::JobHandle& handle)
{
    if (auto* state = handle.State())
    {
        JobSystem::JobHandle::Acquire(state);
        return static_cast<void*>(state);
    }
    return nullptr;
}

namespace {
    // 提交期 defer 窗口的异常安全守卫：异常路径析构也保证 fetch_sub 与 fetch_add 配对，
    // 防 depth 泄漏导致后续 SubmitBatch 永久跳过唤醒。
    struct DeferWindow {
        DeferWindow() noexcept { JobSystem::g_submitDeferDepth.fetch_add(1, std::memory_order_relaxed); }
        ~DeferWindow() { JobSystem::g_submitDeferDepth.fetch_sub(1, std::memory_order_relaxed); }
    };
}

extern "C"
{
    uint32_t JobSystem_GetAbiVersion()
    {
        // ABI 2: JobSystem_Initialize returns an int status code.
        // ABI 3: 删除 6 个死 stats 字段（directAssistClaims / exhaustedTickets /
        //        scheduleToPublishEwmaNs / publishToFirstMainClaimEwmaNs /
        //        publishToFirstWorkerClaimEwmaNs / queueLockWaitEwmaNs）—— 布局已变，
        //        故必须升版本，使旧二进制在加载期被拒绝，而不是按错位偏移静默读错。
        return 3u;
    }

    int JobSystem_Initialize(int numThreads)
    {
        // 先把加载期 banner 一次性吐出（加载期不 I/O，见文件头约束）。
        JobSystem::LoadBannerFlush();
        // 返回 0=成功，非 0=失败；不再静默吞掉初始化失败，让 C# 据此回退 Managed backend。
        try
        {
            return JobSystem::Scheduler::Initialize(numThreads) ? 0 : 1;
        }
        catch (...) { return 1; }
    }

    void JobDebuggerGUI_Launch()
    {
        JobSystem::JobDebuggerGUI::Launch();
    }

    int JobSystem_GetWorkerCount()
    {
        return JobSystem::CurrentWorkerCount();
    }

    void JobSystem_Shutdown()
    {
        JobSystem::Scheduler::Shutdown();
    }

    void JobSystem_PrewakeWorkers()
    {
        JobSystem::Scheduler::PrewakeWorkers();
    }

    // 排空所有在飞批，但不关 worker；`JobSystem_Shutdown` 是终态，不能复用。
    void JobSystem_DrainAll()
    {
        JobSystem::DrainAll();
    }

    void JobSystem_ConfigureTilesPerWorker(int tilesPerWorker)
    {
        JobSystem::Scheduler::ConfigureTilesPerWorker(tilesPerWorker);
    }

    void JobSystem_ConfigureGuided(int enabled, int k, int floor)
    {
        JobSystem::Scheduler::ConfigureGuided(enabled, k, floor);
    }

    void JobSystem_SetJobCostCacheEnabled(int enabled)
    {
        JobSystem::g_jobCostCacheEnabled.store(enabled != 0, std::memory_order_release);
    }

    int JobSystem_BindBatchName(const char* name, void* func)
    {
        return JobSystem::BindJobBatchName(name, func);
    }

    void JobSystem_RegisterPersistentAllocator(PersistentAllocCallback alloc, PersistentFreeCallback free)
    {
        EntJoy::Collections::RegisterPersistentAllocator(alloc, free);
    }

    // 托管 Persistent 回调槽单一存储归属 NativeDll.dll：跨 DLL 经这两个导出访问器共享同一槽，
    // 避免 inline+static 各持副本（回退 malloc/free → 堆损坏）；显式 dllexport 确保进导出表。
    namespace {
        PersistentAllocCallback g_persistentAlloc = nullptr;
        PersistentFreeCallback  g_persistentFree  = nullptr;
    }
    ENTJOY_PERSISTENT_ALLOC_API PersistentAllocCallback* EntJoy_GetPersistentAllocRef() { return &g_persistentAlloc; }
    ENTJOY_PERSISTENT_ALLOC_API PersistentFreeCallback*  EntJoy_GetPersistentFreeRef()  { return &g_persistentFree; }

    void JobSystem_RegisterCurrentBatchId(CurrentBatchIdCallback cb)
    {
        JobSystem::RegisterCurrentBatchIdCallback(cb);
    }

    // batchId→Job名 解析器（C# 注册，供 ImGui Timeline 用）。GUI-only，存静态指针。
    namespace { BatchJobNameResolver g_batchNameResolver = nullptr; BatchJobNameClear g_batchNameClear = nullptr; }

    void JobSystem_RegisterNameResolver(BatchJobNameResolver cb, BatchJobNameClear clearCb)
    {
        g_batchNameResolver = cb;
        g_batchNameClear = clearCb;
    }

    const BatchJobNameResolver& JobSystem_GetNameResolver()
    {
        return g_batchNameResolver;
    }

    void JobSystem_ClearNameResolver()
    {
        if (g_batchNameClear) g_batchNameClear();
    }

    void JobSystem_RecordDirectCall(const char* jobName, unsigned int tiles)
    {
        JobSystem::RecordDirectCall(jobName, tiles);
    }

    uint64_t JobSystem_BeginDirectCall(const char* jobName, unsigned int tiles)
    {
        return JobSystem::BeginDirectCall(jobName, tiles);
    }

    void JobSystem_EndDirectCall(uint64_t id)
    {
        JobSystem::EndDirectCall(id);
    }

    void* JobSystem_Schedule(JobFunc func, void* context, ContextCleanupFunc cleanup, void* dependency)
    {
        try
        {
            JobSystem::JobHandle dep;
            if (dependency)
                dep = JobSystem::JobHandle(fromHandle(dependency), true);
            auto handle = JobSystem::Scheduler::Schedule(func, context, cleanup, dep);
            return toHandle(handle);
        }
        catch (...) { return nullptr; }
    }

    void* JobSystem_ScheduleFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup,
        int length, void* dependency)
    {
        try
        {
            JobSystem::JobHandle dep;
            if (dependency)
                dep = JobSystem::JobHandle(fromHandle(dependency), true);
            auto handle = JobSystem::Scheduler::ScheduleFor(func, context, length, cleanup, dep);
            return toHandle(handle);
        }
        catch (...) { return nullptr; }
    }

    void* JobSystem_ScheduleParallelForBatch(BatchJobFunc func, void* context, ContextCleanupFunc cleanup,
        int length, int batchSize, void* dependency)
    {
        // 旧导出：claimGeom 缺省 0（Auto）。
        return JobSystem_ScheduleParallelForBatchEx(func, context, cleanup, length, batchSize, 0, dependency);
    }

    // 调用点**在代码里**声明认领几何的入口。
    //   claimGeom: 0=Auto（默认）1=Spread（每 worker 独占连续段 + 空手尾部窃取）
    //              2=Adjacent（共享游标发相邻窗口；Melee 的空间复用靠它）
    //   只在 1/2 时生效；批表第四字段（显式声明）优先级更高。
    void* JobSystem_ScheduleParallelForBatchEx(BatchJobFunc func, void* context, ContextCleanupFunc cleanup,
        int length, int batchSize, int claimGeom, void* dependency)
    {
        try
        {
            JobSystem::JobHandle dep;
            if (dependency)
                dep = JobSystem::JobHandle(fromHandle(dependency), true);
            uint32_t geom = 0u;
            if (claimGeom == static_cast<int>(JobSystem::kClaimGeomSpread)) geom = JobSystem::kClaimGeomSpread;
            else if (claimGeom == static_cast<int>(JobSystem::kClaimGeomAdjacent)) geom = JobSystem::kClaimGeomAdjacent;
            auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(func, context, length, batchSize, cleanup, dep, geom);
            return toHandle(handle);
        }
        catch (...) { return nullptr; }
    }

    int JobSystem_ScheduleBatch(const JobBatchDesc* descs, int count, void** outHandles)
    {
        if (!descs || count <= 0 || !outHandles) return 0;
        // 打包 fast path 的最小 run 长度：短 run 走逐描述符路径，避免为 1-3 个 job
        // 付一个 batch（BatchStorage + token 提交 + 退役）的固定成本。
        constexpr int kPackMinRun = 4;
        int ok = 0;
        // 单描述符提交；返回是否成功提交。
        auto submitOne = [&](int idx) -> bool {
            outHandles[idx] = nullptr;
            const JobBatchDesc& d0 = descs[idx];
            if (!d0.func) return false;
            JobSystem::JobHandle dep;
            if (d0.dependency)
                dep = JobSystem::JobHandle(fromHandle(d0.dependency), true);
            JobSystem::JobHandle handle;
            switch (d0.kind)
            {
            case 0: // IJob
                handle = JobSystem::Scheduler::Schedule(
                    reinterpret_cast<JobFunc>(d0.func), d0.context, d0.cleanup, dep);
                break;
            case 1: // IJobFor
                handle = JobSystem::Scheduler::ScheduleFor(
                    reinterpret_cast<IndexJobFunc>(d0.func), d0.context, d0.length, d0.cleanup, dep);
                break;
            case 2: // IJobParallelFor（auto-batch 语义：batchFunc(ctx,start,count)）
                handle = JobSystem::Scheduler::ScheduleParallelForBatch(
                    reinterpret_cast<BatchJobFunc>(d0.func), d0.context, d0.length, d0.batchSize, d0.cleanup, dep);
                break;
            default:
                return false;
            }
            outHandles[idx] = toHandle(handle);
            return true;
        };
        try
        {
            {
                // deferNotify 窗口：本批 submit 结束后统一唤醒一次（异常路径由 DeferWindow 兜底）。
                DeferWindow deferWindow;
                // 打包描述符暂存：thread_local 复用容量，避免每帧一次大数组分配。
                static thread_local std::vector<JobSystem::PackedPlainJobDesc> packBuf;
                int i = 0;
                while (i < count)
                {
                    outHandles[i] = nullptr;
                    const JobBatchDesc& d = descs[i];
                    if (!d.func)
                    {
                        ++i;
                        continue;
                    }

                    // ---- 打包提交：连续、无依赖的 plain IJob（kind==0）合并为一个 batch ----
                    // 只合并"完全同形"的描述符（无依赖）。任一描述符带依赖即就地断开 run
                    // —— 依赖语义与 IJobFor/IJobParallelFor 的既有提交路径逐位不变。
                    if (d.kind == 0 && !d.dependency)
                    {
                        int j = i;
                        while (j < count && descs[j].func && descs[j].kind == 0 &&
                               !descs[j].dependency)
                        {
                            ++j;
                        }
                        const int runLen = j - i;
                        bool packed = false;
                        if (runLen >= kPackMinRun)
                        {
                            try
                            {
                                packBuf.clear();
                                packBuf.reserve(static_cast<size_t>(runLen));
                                for (int k = i; k < j; ++k)
                                {
                                    packBuf.push_back(JobSystem::PackedPlainJobDesc{
                                        reinterpret_cast<JobFunc>(descs[k].func),
                                        descs[k].context,
                                        descs[k].cleanup });
                                }
                                const int n = JobSystem::SubmitPackedPlainJobs(
                                    packBuf.data(), runLen, outHandles + i);
                                // 全有或全无：只有整段成功才接受打包结果；
                                // 否则整段回退逐描述符（打包未消费任何 context）。
                                if (n == runLen)
                                {
                                    ok += n;
                                    packed = true;
                                }
                                else if (n > 0)
                                {
                                    // 理论上不可达（打包是事务式的）。保守处理：该段视为
                                    // 未打包并整段回退，同时把已写出的句柄清空，避免
                                    // 同一 context 被提交两次（重复执行）。
                                    for (int k = i; k < j; ++k) outHandles[k] = nullptr;
                                }
                            }
                            catch (...)
                            {
                                // 降级：整段落入下方逐描述符路径。
                            }
                        }
                        if (packed)
                        {
                            i = j;
                            continue;
                        }
                        // 整段回退：逐描述符提交 run 内全部描述符（异常与既有路径一致地
                        // 穿透到外层 catch，返回已成功提交数）。
                        for (int k = i; k < j; ++k)
                        {
                            if (submitOne(k)) ++ok;
                        }
                        i = j;
                        continue;
                    }

                    // ---- 带依赖 / IJobFor / IJobParallelFor：逐描述符路径 ----
                    if (submitOne(i)) ++ok;
                    ++i;
                }
            }
            if (auto scheduler = JobSystem::LoadChaseLevScheduler())
                scheduler->WakePending();   // 统一唤醒一次
        }
        catch (...)
        {
            // OOM：返回已成功提交的数量，不穿透 C ABI。defer 窗口已 RAII 关闭但统一
            // 唤醒被跳过，此处补广播防止已提交批滞留无人唤醒。
            if (auto scheduler = JobSystem::LoadChaseLevScheduler())
                scheduler->WakePending();
        }
        return ok;
    }

    void JobSystem_SetImplicitBatchEnabled(int enabled)
    {
        if (enabled)
        {
            JobSystem::g_implicitBatchEnabled.store(true, std::memory_order_relaxed);
            return;
        }
        // 关闭：先锁内置 false（此后不再入队）再排空积压；颠倒顺序会让 flush 后新入队的 batch 滞留。
        {
            std::lock_guard<std::mutex> lock(JobSystem::g_pendingBatchesMutex);
            JobSystem::g_implicitBatchEnabled.store(false, std::memory_order_relaxed);
        }
        JobSystem::FlushPendingSubmits();
    }

    void JobSystem_FlushPendingSubmits()
    {
        JobSystem::FlushPendingSubmits();
    }

    void JobSystem_Complete(void* handle)
    {
        // 隐式批：Complete 前先提交 pending（Unity ScheduleBatchedJobs 同语义，防死等）
        JobSystem::FlushPendingSubmits();
        // 仅等待任务完成，不改变引用计数。C++ 异常不穿透 C ABI：C# job 的异常由
        // 托管侧 ThrowRecordedJobExceptions 抛出，此处仅兜底防 RethrowBatchException 越界。
        if (!handle) return;
        try
        {
            JobSystem::JobHandle(fromHandle(handle), true).Complete();
        }
        catch (...) {}
    }

    uint64_t JobSystem_GetDiagnosticBatchId(void* handle)
    {
        // 读 handle 的 diagnosticBatchId。调用方须在 Complete 之后、Release 之前
        // 调用（此时 batch 必已 submit、id 已设置，且调用方引用使 state 存活）。
        if (!handle) return 0;
        return fromHandle(handle)->diagnosticBatchId.load(std::memory_order_acquire);
    }

    int JobSystem_GetWorkerSnapshots(WorkerSnapshot* buffer, int maxCount)
    {
        if (!buffer || maxCount <= 0) return 0;
        const int workerCount = JobSystem::CurrentWorkerCount();
        const int count = (maxCount < workerCount) ? maxCount : workerCount;
        for (int i = 0; i < count; ++i)
        {
            auto& snap = buffer[i];
            snap.workerIndex = i;
            snap.currentBatchId = JobSystem::g_workerCurrentBatchId[i].load(std::memory_order_relaxed);
            snap.currentTile = JobSystem::g_workerCurrentTile[i].load(std::memory_order_relaxed);
            snap.tileCount = JobSystem::g_workerBatchTileCount[i].load(std::memory_order_relaxed);
            snap.isActive = JobSystem::g_workerIsActive[i].load(std::memory_order_relaxed);
        }
        return count;
    }

    uint64_t JobSystem_CompleteAndRelease(void* handle)
    {
        // 隐式批：Complete 前先提交 pending（Unity ScheduleBatchedJobs 同语义，防死等）
        JobSystem::FlushPendingSubmits();
        // 接管调用方引用：等待完成后读 diagnosticBatchId，引用析构自动释放；
        // 即 C# 的 Complete+GetDiagnosticBatchId+ReleaseHandle 三合一（省 2 次 P/Invoke）。
        if (!handle) return 0;
        JobSystem::HandleState* state = fromHandle(handle);
        JobSystem::JobHandle jobHandle(fromHandle(handle), false); // 不增加引用
        try
        {
            jobHandle.Complete();
        }
        catch (...) {}   // C++ 异常不穿透 C ABI
        uint64_t id = state ? state->diagnosticBatchId.load(std::memory_order_acquire) : 0;
        return id; // jobHandle 析构 → Release(state)
    }

    void JobSystem_SubmitDeferBump()
    {
        JobSystem::g_submitDeferDepth.fetch_add(1, std::memory_order_relaxed);
    }

    void JobSystem_SubmitDeferFlush()
    {
        const int d = JobSystem::g_submitDeferDepth.fetch_sub(1, std::memory_order_relaxed);
        if (d <= 1)   // 归零：统一唤醒一次（嵌套失衡时也兜底广播，不丢唤醒）
        {
            if (auto scheduler = JobSystem::LoadChaseLevScheduler())
                scheduler->WakePending();
        }
    }

    void JobSystem_RetainHandle(void* handle)
    {
        if (handle)
            JobSystem::JobHandle::Acquire(fromHandle(handle));
    }

    int JobSystem_IsCompleted(void* handle)
    {
        // 隐式批：查询前先提交 pending，防误报未完成
        JobSystem::FlushPendingSubmits();
        if (!handle) return 1;
        return fromHandle(handle)->completed.load(std::memory_order_acquire) ? 1 : 0;
    }

    void JobSystem_ReleaseHandle(void* handle)
    {
        if (!handle) return;
        JobSystem::JobHandle::Release(fromHandle(handle));
    }

    void* JobSystem_CombineDependencies(void** handles, int count)
    {
        if (count <= 0 || !handles)
            return nullptr;
        try
        {
            std::vector<JobSystem::JobHandle> vec;
            vec.reserve(count);
            for (int i = 0; i < count; ++i)
            {
                if (handles[i])
                    vec.emplace_back(fromHandle(handles[i]), true);
            }
            auto combined = JobSystem::JobHandle::CombineDependencies(vec);
            return toHandle(combined);
        }
        catch (...) { return nullptr; }
    }

    int64_t JobSystem_GetLiveHandleCount()
    {
        return JobSystem::g_liveHandleStates.load(std::memory_order_relaxed);
    }

    void* JobSystem_ScheduleChunkJob(
        ChunkJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const ChunkJobData* chunks,
        int chunkCount,
        void* dependency)
    {
        try
        {
            JobSystem::JobHandle dep;
            if (dependency)
                dep = JobSystem::JobHandle(fromHandle(dependency), true);
            auto handle = JobSystem::Scheduler::ScheduleChunks(func, context, cleanup, chunks, chunkCount, dep, JobSystem::ChunkScheduleMode::PublishAssist, 0, 0);
            return toHandle(handle);
        }
        catch (...) { return nullptr; }
    }

    void* JobSystem_ScheduleChunkJobEx(
        ChunkJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const ChunkJobData* chunks,
        int chunkCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        uint32_t unitGeneration)
    {
        try
        {
            JobSystem::JobHandle dep;
            if (dependency)
                dep = JobSystem::JobHandle(fromHandle(dependency), true);
            auto mode = JobSystem::ChunkScheduleMode::PublishAssist;
            if (scheduleMode == 0)
                mode = JobSystem::ChunkScheduleMode::PublishNoAssist;
            else if (scheduleMode == 2)
                mode = JobSystem::ChunkScheduleMode::DeferTinyOnly;
            else if (scheduleMode == 3)
                mode = JobSystem::ChunkScheduleMode::ImmediateNative;
            else if (scheduleMode == 4)
                mode = JobSystem::ChunkScheduleMode::DeferredPublish;
            else if (scheduleMode == 5)
                mode = JobSystem::ChunkScheduleMode::DeferredPublishNoAssist;
            auto handle = JobSystem::Scheduler::ScheduleChunks(func, context, cleanup, chunks, chunkCount, dep, mode, workerCap, rangeSize, unitGeneration);
            return toHandle(handle);
        }
        catch (...) { return nullptr; }
    }

    void* JobSystem_ScheduleChunkRangeJobEx(
        ChunkRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const ChunkJobData* chunks,
        int chunkCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        uint32_t unitGeneration)
    {
        try
        {
            JobSystem::JobHandle dep;
            if (dependency)
                dep = JobSystem::JobHandle(fromHandle(dependency), true);
            auto mode = JobSystem::ChunkScheduleMode::PublishAssist;
            if (scheduleMode == 0)
                mode = JobSystem::ChunkScheduleMode::PublishNoAssist;
            else if (scheduleMode == 2)
                mode = JobSystem::ChunkScheduleMode::DeferTinyOnly;
            else if (scheduleMode == 3)
                mode = JobSystem::ChunkScheduleMode::ImmediateNative;
            else if (scheduleMode == 4)
                mode = JobSystem::ChunkScheduleMode::DeferredPublish;
            else if (scheduleMode == 5)
                mode = JobSystem::ChunkScheduleMode::DeferredPublishNoAssist;
            auto handle = JobSystem::Scheduler::ScheduleChunkRanges(func, context, cleanup, chunks, chunkCount, dep, mode, workerCap, rangeSize, unitGeneration);
            return toHandle(handle);
        }
        catch (...) { return nullptr; }
    }

    void* JobSystem_ScheduleEntityBatchJobEx(
        EntityBatchRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const EntityBatchData* batches,
        int batchCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        int jobKind,
        uint32_t unitGeneration)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto mode = JobSystem::ChunkScheduleMode::PublishAssist;
        if (scheduleMode == 0)
            mode = JobSystem::ChunkScheduleMode::PublishNoAssist;
        else if (scheduleMode == 2)
            mode = JobSystem::ChunkScheduleMode::DeferTinyOnly;
        else if (scheduleMode == 3)
            mode = JobSystem::ChunkScheduleMode::ImmediateNative;
        else if (scheduleMode == 4)
            mode = JobSystem::ChunkScheduleMode::DeferredPublish;
        else if (scheduleMode == 5)
            mode = JobSystem::ChunkScheduleMode::DeferredPublishNoAssist;
        const auto kind = jobKind == 0
            ? JobSystem::EcsJobKind::Chunk
            : JobSystem::EcsJobKind::Entity;
        try
        {
            auto handle = JobSystem::Scheduler::ScheduleEntityBatches(func, context, cleanup, batches, batchCount, dep, mode, workerCap, rangeSize, kind, unitGeneration);
            return toHandle(handle);
        }
        catch (...) { return nullptr; }
    }

    void* JobSystem_ScheduleAndCompleteEntityBatchJobEx(
        EntityBatchRangeJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const EntityBatchData* batches,
        int batchCount,
        void* dependency,
        int scheduleMode,
        int workerCap,
        int rangeSize,
        int jobKind,
        uint32_t unitGeneration)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto mode = JobSystem::ChunkScheduleMode::PublishAssist;
        if (scheduleMode == 0)
            mode = JobSystem::ChunkScheduleMode::PublishNoAssist;
        else if (scheduleMode == 2)
            mode = JobSystem::ChunkScheduleMode::DeferTinyOnly;
        else if (scheduleMode == 3)
            mode = JobSystem::ChunkScheduleMode::ImmediateNative;
        else if (scheduleMode == 4)
            mode = JobSystem::ChunkScheduleMode::DeferredPublish;
        else if (scheduleMode == 5)
            mode = JobSystem::ChunkScheduleMode::DeferredPublishNoAssist;
        // 一步完成 Schedule+Complete，消除 P/Invoke 往返（主线程尽早进入 assist）
        const auto kind = jobKind == 0
            ? JobSystem::EcsJobKind::Chunk
            : JobSystem::EcsJobKind::Entity;
        try
        {
            auto handle = JobSystem::Scheduler::ScheduleEntityBatches(func, context, cleanup, batches, batchCount, dep, mode, workerCap, rangeSize, kind, unitGeneration);
            handle.Complete();
            return toHandle(handle);
        }
        catch (...) { return nullptr; }
    }

    uint32_t JobSystem_GetStatsSize()
    {
        // 布局防御：必须与 C# NativeJobSystemStats（Marshal.SizeOf）相等。
        // 新增统计字段时若不同步，C# GetStats 会越界写 → 堆损坏。
        return static_cast<uint32_t>(sizeof(JobSystemStatsNative));
    }

    void JobSystem_GetStats(JobSystemStatsNative* stats)
    {
        if (!stats) return;
        JobSystem::JobSystemStatsSnapshot snapshot{};
        JobSystem::GetStatsSnapshot(&snapshot);
        stats->completeWaitLoops = snapshot.completeWaitLoops;
        stats->assistAttempts = snapshot.assistAttempts;
        stats->assistExecuted = snapshot.assistExecuted;
        stats->frameTasksSubmitted = snapshot.frameTasksSubmitted;
        stats->frameTasksCompleted = snapshot.frameTasksCompleted;
        stats->workerExecutedRanges = snapshot.workerExecutedRanges;
        stats->mainExecutedRanges = snapshot.mainExecutedRanges;
        stats->stealCount = snapshot.stealCount;
        stats->parkWakeCount = snapshot.parkWakeCount;
        stats->deferredRuns = snapshot.deferredRuns;
        stats->publishedJobs = snapshot.publishedJobs;
        stats->prewakeCount = snapshot.prewakeCount;
        stats->hotSpinHits = snapshot.hotSpinHits;
        stats->waitFallbacks = snapshot.waitFallbacks;
        stats->notifiedWorkers = snapshot.notifiedWorkers;
        stats->workerClaimedTokens = snapshot.workerClaimedTokens;
        stats->mainClaimedTokens = snapshot.mainClaimedTokens;
        stats->coldBatches = snapshot.coldBatches;
        stats->activeWorkersPeak = snapshot.activeWorkersPeak;
        stats->wakeLatencyEwmaNs = snapshot.wakeLatencyEwmaNs;
        stats->scheduleModePublishNoAssist = snapshot.scheduleModePublishNoAssist;
        stats->scheduleModePublishAssist = snapshot.scheduleModePublishAssist;
        stats->scheduleModeDeferTinyOnly = snapshot.scheduleModeDeferTinyOnly;
        stats->scheduleModeImmediateNative = snapshot.scheduleModeImmediateNative;
        stats->scheduleModeDeferredPublish = snapshot.scheduleModeDeferredPublish;
        stats->scheduleModeDeferredPublishNoAssist = snapshot.scheduleModeDeferredPublishNoAssist;
        stats->frameQueueDepthPeak = snapshot.frameQueueDepthPeak;
        stats->publishToCompletionEwmaNs = snapshot.publishToCompletionEwmaNs;
        stats->perRangeExecEwmaNs = snapshot.perRangeExecEwmaNs;
        stats->assistExecPctEwma = snapshot.assistExecPctEwma;
        stats->completionOverheadUs = snapshot.completionOverheadUs;
        stats->workerTargetTotal = snapshot.workerTargetTotal;
        stats->totalTilesPublished = snapshot.totalTilesPublished;
        stats->localTiles = snapshot.localTiles;
        stats->stolenTiles = snapshot.stolenTiles;
        stats->assistTiles = snapshot.assistTiles;
        stats->stealAttempts = snapshot.stealAttempts;
        stats->stealSuccesses = snapshot.stealSuccesses;
        stats->permitsReleased = snapshot.permitsReleased;
        stats->victimScans = snapshot.victimScans;
        stats->stealEmptyExits = snapshot.stealEmptyExits;
        stats->batchStorageCreated = snapshot.batchStorageCreated;
        stats->batchStorageReused = snapshot.batchStorageReused;
        stats->batchStorageReturned = snapshot.batchStorageReturned;
        stats->batchStorageDropped = snapshot.batchStorageDropped;
        stats->submitToFirstWorkerEwmaNs = snapshot.submitToFirstWorkerEwmaNs;
        stats->workerStartSpreadEwmaNs = snapshot.workerStartSpreadEwmaNs;
        stats->lastTileToTopologyDoneEwmaNs = snapshot.lastTileToTopologyDoneEwmaNs;
        stats->completeWakeToReturnEwmaNs = snapshot.completeWakeToReturnEwmaNs;
        stats->nativeBatches = snapshot.nativeBatches;
        stats->invalidBackendSelections = snapshot.invalidBackendSelections;
        stats->timingSampleCount = snapshot.timingSampleCount;
        stats->timingSamplesDropped = snapshot.timingSamplesDropped;
        stats->batchTotalP50Ns = snapshot.batchTotalP50Ns;
        stats->batchTotalP95Ns = snapshot.batchTotalP95Ns;
        stats->batchTotalP99Ns = snapshot.batchTotalP99Ns;
        stats->batchTotalMaxNs = snapshot.batchTotalMaxNs;
        stats->submitToFirstWorkerP50Ns = snapshot.submitToFirstWorkerP50Ns;
        stats->submitToFirstWorkerP95Ns = snapshot.submitToFirstWorkerP95Ns;
        stats->submitToFirstWorkerP99Ns = snapshot.submitToFirstWorkerP99Ns;
        stats->submitToFirstWorkerMaxNs = snapshot.submitToFirstWorkerMaxNs;
        stats->workerStartSpreadP50Ns = snapshot.workerStartSpreadP50Ns;
        stats->workerStartSpreadP95Ns = snapshot.workerStartSpreadP95Ns;
        stats->workerStartSpreadP99Ns = snapshot.workerStartSpreadP99Ns;
        stats->workerStartSpreadMaxNs = snapshot.workerStartSpreadMaxNs;
        stats->executionSpanP50Ns = snapshot.executionSpanP50Ns;
        stats->executionSpanP95Ns = snapshot.executionSpanP95Ns;
        stats->executionSpanP99Ns = snapshot.executionSpanP99Ns;
        stats->executionSpanMaxNs = snapshot.executionSpanMaxNs;
        stats->maxRangeP50Ns = snapshot.maxRangeP50Ns;
        stats->maxRangeP95Ns = snapshot.maxRangeP95Ns;
        stats->maxRangeP99Ns = snapshot.maxRangeP99Ns;
        stats->maxRangeMaxNs = snapshot.maxRangeMaxNs;
        stats->slowBatchId = snapshot.slowBatchId;
        stats->slowBatchTotalNs = snapshot.slowBatchTotalNs;
        stats->slowSubmitToFirstWorkerNs = snapshot.slowSubmitToFirstWorkerNs;
        stats->slowWorkerStartSpreadNs = snapshot.slowWorkerStartSpreadNs;
        stats->slowExecutionSpanNs = snapshot.slowExecutionSpanNs;
        stats->slowMaxRangeNs = snapshot.slowMaxRangeNs;
        stats->slowCoreMigrations = snapshot.slowCoreMigrations;
        stats->slowAssistTiles = snapshot.slowAssistTiles;
        stats->slowRangeThreadCpuNs = snapshot.slowRangeThreadCpuNs;
        stats->slowRangeThreadCycles = snapshot.slowRangeThreadCycles;
        stats->slowBatchMinRangeThreadCycles = snapshot.slowBatchMinRangeThreadCycles;
        stats->slowBatchAverageRangeThreadCycles = snapshot.slowBatchAverageRangeThreadCycles;
        stats->slowRangeIndex = snapshot.slowRangeIndex;
        stats->slowRangeWorker = snapshot.slowRangeWorker;
        stats->slowRangeStartLogicalCore = snapshot.slowRangeStartLogicalCore;
        stats->slowRangeEndLogicalCore = snapshot.slowRangeEndLogicalCore;
        stats->slowRangeStartPhysicalCore = snapshot.slowRangeStartPhysicalCore;
        stats->slowRangeEndPhysicalCore = snapshot.slowRangeEndPhysicalCore;
    }

    void JobSystem_ResetStats()
    {
        JobSystem::ResetStatsSnapshot();
    }

    // `ENTJOY_WAKE_POLL` 生效证据（见 Exports.h 的说明）。
    void JobSystem_GetWakePollCounters(unsigned long long* skips, unsigned long long* wakes)
    {
        // 计数走 thread_local 累加（热路径不写全局原子）⇒ 读取前先把**本线程**的尾巴合并进来，
        // 否则最近 <1024 次派发读不到（调用方通常是提交线程，正是计数的主要来源）。
        JobSystem::WakePollFlushCurrentThread();
        if (skips) *skips = JobSystem::g_wakePollSkips.load(std::memory_order_relaxed);
        if (wakes) *wakes = JobSystem::g_wakePollWakes.load(std::memory_order_relaxed);
    }

    // 认领几何生效证据（见 Exports.h）。
    void JobSystem_GetClaimGeomCounters(unsigned long long* spread, unsigned long long* adjacent,
                                        unsigned long long* autoDecl)
    {
        if (spread) *spread = JobSystem::g_claimGeomDeclSpread.load(std::memory_order_relaxed);
        if (adjacent) *adjacent = JobSystem::g_claimGeomDeclAdjacent.load(std::memory_order_relaxed);
        if (autoDecl) *autoDecl = JobSystem::g_claimGeomDeclAuto.load(std::memory_order_relaxed);
    }

    void JobSystem_SetTimingDiagnostics(int enabled)
    {
        JobSystem::SetTimingDiagnosticsEnabled(enabled != 0);
    }

    // 主线程 assist 运行时开关。默认关闭，由 API 控制。
    // g_mainThreadAssistEnabled 声明于 JobSystemInternal.h（namespace JobSystem 内）
    void JobSystem_SetMainThreadAssist(int enabled)
    {
        JobSystem::g_mainThreadAssistEnabled.store(enabled != 0, std::memory_order_relaxed);
    }

    // CPU 亲和性运行时开关：立即应用到主线程 + 所有 worker。
    void JobSystem_SetWorkerAffinity(int enabled)
    {
        JobSystem::g_workerAffinityEnabled.store(
            enabled != 0, std::memory_order_relaxed);
        // worker：遍历已启动线程设置/清除亲和性
        if (auto scheduler = JobSystem::LoadChaseLevScheduler())
            scheduler->ApplyAffinity(enabled != 0);
        // 主线程：绑定核心 0 或清除
#if defined(_WIN32)
        if (enabled)
            JobSystem::BindCurrentThreadToLogicalProcessor(0);
        else
            JobSystem::ClearCurrentThreadAffinity();
#endif
    }

    // ======================== Profiler API ========================

    void JobProfiler_SetEnabled(int enabled)
    {
        g_profilerEnabled.store(enabled != 0, std::memory_order_release);
        if (!enabled) {
            g_profilerBuffer.Clear();
        }
    }

    int JobProfiler_IsEnabled()
    {
        return g_profilerEnabled.load(std::memory_order_acquire) ? 1 : 0;
    }

    int JobProfiler_ReadAll(struct ProfilerEntry* buffer, int maxCount)
    {
        if (!buffer || maxCount <= 0) return 0;
        return static_cast<int>(g_profilerBuffer.ReadAll(static_cast<size_t>(maxCount), buffer));
    }

    void JobProfiler_Clear()
    {
        g_profilerBuffer.Clear();
    }

    void Trace_SetEnabled(int enabled)
    {
        JobSystem::TraceSetEnabled(enabled != 0);
    }

    int Trace_IsEnabled()
    {
        return JobSystem::TraceIsEnabled() ? 1 : 0;
    }

    int Trace_ReadAll(JobSystem::TraceEvent* buffer, int maxCount)
    {
        return JobSystem::TraceReadAll(buffer, maxCount);
    }

    uint64_t Trace_DroppedEvents()
    {
        return JobSystem::TraceDroppedEvents();
    }

    void Trace_Clear()
    {
        JobSystem::TraceClear();
    }

} // extern "C"
