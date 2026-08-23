using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EntJoy.Collections;

namespace EntJoy.JobSystem
{

/// <summary>
/// HandleState 的 C# 侧视图（与 C++ HandleState 内存布局一一对应）
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeJobSystemStats
{
    public ulong CompleteWaitLoops;
    public ulong AssistAttempts;
    public ulong AssistExecuted;
    public ulong FrameTasksSubmitted;
    public ulong FrameTasksCompleted;
    public ulong WorkerExecutedRanges;
    public ulong MainExecutedRanges;
    public ulong StealCount;
    public ulong ParkWakeCount;
    public ulong DeferredRuns;
    public ulong PublishedJobs;
    public ulong PrewakeCount;
    public ulong HotSpinHits;
    public ulong WaitFallbacks;
    public ulong NotifiedWorkers;
    public ulong WorkerClaimedTokens;
    public ulong MainClaimedTokens;
    public ulong ColdBatches;
    public ulong ActiveWorkersPeak;
    public ulong WakeLatencyEwmaNs;
    public ulong ScheduleModePublishNoAssist;
    public ulong ScheduleModePublishAssist;
    public ulong ScheduleModeDeferTinyOnly;
    public ulong ScheduleModeImmediateNative;
    public ulong ScheduleModeDeferredPublish;
    public ulong ScheduleModeDeferredPublishNoAssist;
    public int FrameQueueDepthPeak;
    public ulong DirectAssistClaims;
    public ulong ExhaustedTickets;
    public ulong ScheduleToPublishEwmaNs;
    public ulong PublishToFirstMainClaimEwmaNs;
    public ulong PublishToFirstWorkerClaimEwmaNs;
    public ulong PublishToCompletionEwmaNs;
    public ulong QueueLockWaitEwmaNs;
    public ulong PerRangeExecEwmaNs;       // 每个 range 平均执行时间 (ns, EWMA)
    public ulong AssistExecPctEwma;        // assist 有效率 (0~100)
    public ulong CompletionOverheadUs;     // 调度/等待开销 = completionUs - perRangeExecUs
    // Appended Tile/partition fields; keep order in sync with Exports.h.
    public ulong WorkerTargetTotal;
    public ulong TotalTilesPublished;
    public ulong LocalTiles;
    public ulong StolenTiles;
    public ulong AssistTiles;
    public ulong StealAttempts;
    public ulong StealSuccesses;
    public ulong PermitsReleased;
    public ulong VictimScans;
    public ulong StealEmptyExits;
    public ulong BatchStorageCreated;
    public ulong BatchStorageReused;
    public ulong BatchStorageReturned;
    public ulong BatchStorageDropped;
    public ulong SubmitToFirstWorkerEwmaNs;
    public ulong WorkerStartSpreadEwmaNs;
    public ulong LastTileToTopologyDoneEwmaNs;
    public ulong CompleteWakeToReturnEwmaNs;
    public ulong NativeBatches;
    public ulong InvalidBackendSelections;
    // Exact per-batch timing distribution appended for ABI compatibility.
    public ulong TimingSampleCount;
    public ulong TimingSamplesDropped;
    public ulong BatchTotalP50Ns;
    public ulong BatchTotalP95Ns;
    public ulong BatchTotalP99Ns;
    public ulong BatchTotalMaxNs;
    public ulong SubmitToFirstWorkerP50Ns;
    public ulong SubmitToFirstWorkerP95Ns;
    public ulong SubmitToFirstWorkerP99Ns;
    public ulong SubmitToFirstWorkerMaxNs;
    public ulong WorkerStartSpreadP50Ns;
    public ulong WorkerStartSpreadP95Ns;
    public ulong WorkerStartSpreadP99Ns;
    public ulong WorkerStartSpreadMaxNs;
    public ulong ExecutionSpanP50Ns;
    public ulong ExecutionSpanP95Ns;
    public ulong ExecutionSpanP99Ns;
    public ulong ExecutionSpanMaxNs;
    public ulong MaxRangeP50Ns;
    public ulong MaxRangeP95Ns;
    public ulong MaxRangeP99Ns;
    public ulong MaxRangeMaxNs;
    public ulong SlowBatchId;
    public ulong SlowBatchTotalNs;
    public ulong SlowSubmitToFirstWorkerNs;
    public ulong SlowWorkerStartSpreadNs;
    public ulong SlowExecutionSpanNs;
    public ulong SlowMaxRangeNs;
    public ulong SlowCoreMigrations;
    public ulong SlowAssistTiles;
    public ulong SlowRangeThreadCpuNs;
    public ulong SlowRangeThreadCycles;
    public ulong SlowBatchMinRangeThreadCycles;
    public ulong SlowBatchAverageRangeThreadCycles;
    public int SlowRangeIndex;
    public int SlowRangeWorker;
    public int SlowRangeStartLogicalCore;
    public int SlowRangeEndLogicalCore;
    public int SlowRangeStartPhysicalCore;
    public int SlowRangeEndPhysicalCore;
}

public enum NativeTraceEventType : ushort
{
    Publish,
    CompleteEnter,
    Claim,
    ExecuteBegin,
    ExecuteEnd,
    FinalizeBegin,
    HandleComplete,
    Park,
    Wake
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeTraceEvent
{
    public ulong TimestampNs;
    public ulong Sequence;
    public ulong BatchId;
    public int TileIndex;
    public int EntityStart;
    public int EntityCount;
    public int ThreadId;
    public int ProcessorIndex;
    public short WorkerIndex;
    public NativeTraceEventType EventType;
}

/// <summary>
/// 原生调度器门面（Jobs 程序集内的薄门面）。零 ECS 依赖，不持有任何被共享的可变状态——
/// 所有共享状态（委托缓存、上下文池、异常、ThreadStatic、纯 P/Invoke 指针）由
/// <see cref="NativeJobCore"/> 独占持有。ECS 的 chunk 调度（NativeEcsScheduler）
/// 与本文共用同一个引擎。
/// </summary>
public static unsafe partial class NativeJobScheduler
{
    // ======================== 配置 ========================
    /// <summary>
    /// 并行 for 默认 tiles/worker：batchSize=0 时原生 ResolveChunkSize 按此值个 tile/worker 切分。
    /// tpw=4 平衡 light/heavy 场景性能，与 ECS kTargetTilesPerWorker=4 一致。
    /// </summary>
    public static int TilesPerWorker = 4;

    /// <summary>Guided（chunk ∝ 剩余工作量）tile 调度。默认关闭（uniform 更通用）。</summary>
    public static bool GuidedEnabled = false;
    public static int GuidedK = 4;
    public static int GuidedFloor = 16;

    /// <summary>
    /// 启用 per-job 自动 batch（JobCostCache）。默认开启：
    /// worker 按每 job 的每元素成本 EWMA 自动求解最优 tile 数——
    /// 轻任务自动减 tiles（→ 减参与 worker → 减唤醒成本），重任务维持并行度。
    /// 关闭后走纯 tpw=4（冷启动/保守场景）。压测已验证：并发/成本波动/
    /// 泄漏/ASAN 全绿；冷启动 tpw=4 兜底 + kMaxAutoChunk 护栏无退化。
    /// </summary>
    public static bool JobCostCacheEnabled
    {
        get => _jobCostCacheEnabled;
        set
        {
            if (_jobCostCacheEnabled == value) return;
            _jobCostCacheEnabled = value;
            NativeJobCore.JobSystem_SetJobCostCacheEnabled(value ? 1 : 0);
        }
    }
    private static bool _jobCostCacheEnabled = true;

    private static void ConfigureGuidedFromEnv()
    {
        string on = System.Environment.GetEnvironmentVariable("ENTJOY_GUIDED_TILES");
        if (!string.IsNullOrEmpty(on) && int.TryParse(on, out int onVal))
            GuidedEnabled = onVal > 0;
        string k = System.Environment.GetEnvironmentVariable("ENTJOY_GUIDED_K");
        if (!string.IsNullOrEmpty(k) && int.TryParse(k, out int kVal) && kVal > 0)
            GuidedK = kVal;
        string floor = System.Environment.GetEnvironmentVariable("ENTJOY_GUIDED_FLOOR");
        if (!string.IsNullOrEmpty(floor) && int.TryParse(floor, out int floorVal) && floorVal > 0)
            GuidedFloor = floorVal;

        if (GuidedEnabled)
            NativeJobCore.JobSystem_ConfigureGuided(1, GuidedK, GuidedFloor);
        else
            NativeJobCore.JobSystem_ConfigureGuided(0, GuidedK, GuidedFloor);
        System.Console.WriteLine($"JobSystem|guided={GuidedEnabled}|k={GuidedK}|floor={GuidedFloor}");
    }

    // ======================== 生命周期 ========================
    public static void Initialize(int numThreads = 0)
    {
        // ENTJOY_JOB_WORKERS 环境变量可覆盖 worker 数（0=自动 PC-1）
        if (numThreads == 0)
        {
            string? env = Environment.GetEnvironmentVariable("ENTJOY_JOB_WORKERS");
            if (int.TryParse(env, out int w) && w >= 0)
                numThreads = w;
        }
        NativeJobCore.JobSystem_Initialize(numThreads);
        RegisterPersistentAllocator();
        NativeJobCore.ValidateStatsLayout(); // 布局防御：C#/C++ 统计结构字节数一致
        NativeJobCore.RegisterCurrentBatchIdCallback();
        if (TilesPerWorker > 0)
            NativeJobCore.JobSystem_ConfigureTilesPerWorker(TilesPerWorker);
        // 强制同步 JobCostCache 默认（默认开启；防 DLL 重载后 native 与托管不一致）
        NativeJobCore.JobSystem_SetJobCostCacheEnabled(JobCostCacheEnabled ? 1 : 0);
        ConfigureGuidedFromEnv();
    }

    public static int JobWorkerCount
    {
        get => NativeJobCore.JobSystem_GetWorkerCount();
    }

    public static void Shutdown() => NativeJobCore.SafeShutdown();
    public static void PrewakeWorkersOnce() => NativeJobCore.JobSystem_PrewakeWorkers();

    public static void LaunchDebuggerGUI()
    {
        NativeJobCore.SetDebugNameCapture(true);
        NativeJobCore.JobSystem_LaunchGUI();
    }

    // ======================== 持久分配器（托管回调注册到 native） ========================
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void* PersistentAllocUnmanaged(int size) => PersistentAllocator.Alloc(size);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void PersistentFreeUnmanaged(void* ptr) => PersistentAllocator.Free(ptr);

    private static void RegisterPersistentAllocator()
    {
        NativeJobCore.JobSystem_RegisterPersistentAllocator(&PersistentAllocUnmanaged, &PersistentFreeUnmanaged);
    }

    // ======================== 直调面板 ========================
    public static unsafe void RecordDirectCall(string jobName, uint tiles)
    {
        if (NativeJobCore.NativeDllHandle == IntPtr.Zero) return;
        if (jobName.Length > 127) jobName = jobName.Substring(0, 127);
        Span<byte> nameBuf = stackalloc byte[128];
        int n = jobName.Length;
        for (int i = 0; i < n; i++) nameBuf[i] = (byte)jobName[i];
        nameBuf[n] = 0;
        fixed (byte* p = nameBuf) NativeJobCore.JobSystem_RecordDirectCall(p, tiles);
    }

    public static unsafe ulong BeginDirectCall(string jobName, uint tiles)
    {
        if (NativeJobCore.NativeDllHandle == IntPtr.Zero) return 0;
        if (jobName.Length > 127) jobName = jobName.Substring(0, 127);
        Span<byte> nameBuf = stackalloc byte[128];
        int n = jobName.Length;
        for (int i = 0; i < n; i++) nameBuf[i] = (byte)jobName[i];
        nameBuf[n] = 0;
        fixed (byte* p = nameBuf) return NativeJobCore.JobSystem_BeginDirectCall(p, tiles);
    }

    public static void EndDirectCall(ulong id)
    {
        if (NativeJobCore.NativeDllHandle == IntPtr.Zero) return;
        NativeJobCore.JobSystem_EndDirectCall(id);
    }

    // ======================== 类型化调度 API ========================
    public static NativeJobHandle Schedule<T>(ref T job, NativeJobHandle? dependsOn = null)
        where T : struct, IJob
    {
        bool managedContext = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        var ctx = managedContext ? NativeJobCore.AllocManagedContext(ref job) : NativeJobCore.AllocContext(ref job);
        bool cleanupByCpp = false;
        try
        {
            var cache = NativeJobCore.GetOrCreateDelegateCache<T, NativeJobCore.JobFunc>(() => NativeJobCore.CreateJobCallback<T>());
            NativeJobHandle handle = NativeJobCore.ScheduleRaw(cache.FuncPtr, ctx, managedContext ? NativeJobCore.ManagedCleanupPtr : NativeJobCore.CleanupPtr, dependsOn);
            cleanupByCpp = true;
            NativeJobCore.RegisterScheduledJobName(handle.Handle, typeof(T).Name);
            return handle;
        }
        catch
        {
            if (!cleanupByCpp)
            {
                if (managedContext) NativeJobCore.ManagedCleanup(ctx);
                else NativeJobCore.Cleanup(ctx);
            }
            throw;
        }
    }

    public static NativeJobHandle ScheduleFor<T>(ref T job, int length, NativeJobHandle? dependsOn = null)
        where T : struct, IJobFor
    {
        if (length <= 0) return default;
        bool managedContext = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        var ctx = managedContext ? NativeJobCore.AllocManagedContext(ref job) : NativeJobCore.AllocContext(ref job);
        bool cleanupByCpp = false;
        try
        {
            var cache = NativeJobCore.GetOrCreateDelegateCache<T, NativeJobCore.IndexJobFunc>(() => NativeJobCore.CreateForCallback<T>());
            NativeJobHandle handle = NativeJobCore.ScheduleForRaw(cache.FuncPtr, ctx, managedContext ? NativeJobCore.ManagedCleanupPtr : NativeJobCore.CleanupPtr, length, dependsOn);
            cleanupByCpp = true;
            NativeJobCore.RegisterScheduledJobName(handle.Handle, typeof(T).Name);
            return handle;
        }
        catch
        {
            if (!cleanupByCpp)
            {
                if (managedContext) NativeJobCore.ManagedCleanup(ctx);
                else NativeJobCore.Cleanup(ctx);
            }
            throw;
        }
    }

    public static NativeJobHandle ScheduleParallelFor<T>(ref T job, int length, int batchSize, NativeJobHandle? dependsOn = null)
        where T : struct, IJobParallelFor
    {
        if (length <= 0) return default;
        bool managedContext = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        var ctx = managedContext ? NativeJobCore.AllocManagedContext(ref job) : NativeJobCore.AllocContext(ref job);
        bool cleanupByCpp = false;
        try
        {
            var cache = NativeJobCore.GetAutoParallelForCache<T>();
            NativeJobHandle handle = NativeJobCore.ScheduleParallelForBatchRaw(cache.FuncPtr, ctx, managedContext ? NativeJobCore.ManagedCleanupPtr : NativeJobCore.CleanupPtr, length, batchSize, dependsOn);
            cleanupByCpp = true;
            NativeJobCore.RegisterScheduledJobName(handle.Handle, typeof(T).Name);
            return handle;
        }
        catch
        {
            if (!cleanupByCpp)
            {
                if (managedContext) NativeJobCore.ManagedCleanup(ctx);
                else NativeJobCore.Cleanup(ctx);
            }
            throw;
        }
    }

    public static NativeJobHandle ScheduleParallelForBatch<T>(ref T job, int length, int batchSize, NativeJobHandle? dependsOn = null)
        where T : struct, IJobParallelForBatch
    {
        if (length <= 0) return default;
        bool managedContext = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        var ctx = managedContext ? NativeJobCore.AllocManagedContext(ref job) : NativeJobCore.AllocContext(ref job);
        bool cleanupByCpp = false;
        try
        {
            var cache = NativeJobCore.GetOrCreateDelegateCache<T, NativeJobCore.BatchJobFunc>(() => NativeJobCore.CreateParallelForBatchCallback<T>());
            NativeJobHandle handle = NativeJobCore.ScheduleParallelForBatchRaw(cache.FuncPtr, ctx, managedContext ? NativeJobCore.ManagedCleanupPtr : NativeJobCore.CleanupPtr, length, batchSize, dependsOn);
            cleanupByCpp = true;
            NativeJobCore.RegisterScheduledJobName(handle.Handle, typeof(T).Name);
            return handle;
        }
        catch
        {
            if (!cleanupByCpp)
            {
                if (managedContext) NativeJobCore.ManagedCleanup(ctx);
                else NativeJobCore.Cleanup(ctx);
            }
            throw;
        }
    }

    // ======================== Complete / IsCompleted / Release ========================
    public static void Complete(ref NativeJobHandle h)
    {
        IntPtr handle = h.Detach();
        if (handle == IntPtr.Zero) return;

        ulong batchId = 0;
        try
        {
            NativeJobCore.JobSystem_Complete(handle);
            batchId = NativeJobCore.JobSystem_GetDiagnosticBatchId(handle);
        }
        finally
        {
            NativeJobCore.JobSystem_ReleaseHandle(handle);
        }
        NativeJobCore.ThrowRecordedJobExceptions(batchId);
    }

    public static bool IsCompleted(NativeJobHandle h)
    {
        using var handleLease = new NativeJobCore.RetainedNativeDependency(h);
        return handleLease.Handle == IntPtr.Zero || NativeJobCore.JobSystem_IsCompleted(handleLease.Handle) != 0;
    }

    public static void Release(NativeJobHandle h)
    {
        IntPtr handle = h.Detach();
        if (handle != IntPtr.Zero)
        {
            NativeJobCore.JobSystem_ReleaseHandle(handle);
        }
    }

    public static NativeJobHandle CombineDependencies(params NativeJobHandle[] handles)
    {
        if (handles == null || handles.Length == 0) return default;
        var ptrs = new IntPtr[handles.Length];
        var leases = new NativeJobCore.RetainedNativeDependency[handles.Length];
        try
        {
            for (int i = 0; i < handles.Length; i++)
            {
                leases[i] = new NativeJobCore.RetainedNativeDependency(handles[i]);
                ptrs[i] = leases[i].Handle;
            }
            return new NativeJobHandle(NativeJobCore.JobSystem_CombineDependencies(ptrs, handles.Length));
        }
        finally
        {
            for (int i = 0; i < leases.Length; i++)
                leases[i].Dispose();
        }
    }

    // ======================== 低级原始接口（transpiler 直调） ========================
    public static NativeJobHandle ScheduleRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, NativeJobHandle? dependsOn = null)
        => NativeJobCore.ScheduleRaw(funcPtr, contextPtr, cleanupPtr, dependsOn);

    public static NativeJobHandle ScheduleForRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, NativeJobHandle? dependsOn = null)
        => NativeJobCore.ScheduleForRaw(funcPtr, contextPtr, cleanupPtr, length, dependsOn);

    public static NativeJobHandle ScheduleParallelForBatchRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, int batchSize, NativeJobHandle? dependsOn = null)
        => NativeJobCore.ScheduleParallelForBatchRaw(funcPtr, contextPtr, cleanupPtr, length, batchSize, dependsOn);

    // transpiler 生成的 Schedule_{Job} 在调度后调用，把 batchId → Job 名注册进调试器字典。
    public static void RegisterScheduledJob(IntPtr handle, string jobName)
    {
        NativeJobCore.RegisterScheduledJobName(handle, jobName);
    }

    // ======================== 面板 / 状态 ========================
    public static NativeJobSystemStats GetStats() => NativeJobCore.JobSystem_GetStats();

    /// <summary>运行时开关主线程 assist（第 N+1 个执行者）。默认关闭。</summary>
    public static void SetMainThreadAssistEnabled(bool enabled) =>
        NativeJobCore.JobSystem_SetMainThreadAssist(enabled);

    /// <summary>运行时开关 worker CPU 亲和性。默认关闭（OS 自由调度）。</summary>
    public static void SetWorkerAffinityEnabled(bool enabled) =>
        NativeJobCore.JobSystem_SetWorkerAffinity(enabled);

    /// <summary>运行时开关 guided tile 调度（chunk ∝ 剩余）。默认关闭（uniform 更通用）。</summary>
    public static void SetGuidedEnabled(bool enabled)
    {
        GuidedEnabled = enabled;
        NativeJobCore.JobSystem_ConfigureGuided(enabled ? 1 : 0, GuidedK, GuidedFloor);
    }
    public static void ResetStats() => NativeJobCore.JobSystem_ResetStats();
    public static void SetTimingDiagnosticsEnabled(bool enabled) =>
        NativeJobCore.JobSystem_SetTimingDiagnostics(enabled);

    // ======================== Profiler 透传（内部） ========================
    internal static void Profiler_SetEnabled(int enabled) => NativeJobCore.Profiler_SetEnabled(enabled);
    internal static int Profiler_IsEnabled() => NativeJobCore.Profiler_IsEnabled();
    internal static unsafe int Profiler_ReadAll(ProfilerEntry[] buffer, int maxCount) => NativeJobCore.Profiler_ReadAll(buffer, maxCount);
    internal static void Profiler_Clear() => NativeJobCore.Profiler_Clear();

    // ======================== Trace 透传 ========================
    public static void TraceSetEnabled(bool enabled) => NativeJobCore.Trace_SetEnabled(enabled);
    public static bool TraceIsEnabled() => NativeJobCore.Trace_IsEnabled();
    public static ulong TraceDroppedEvents() => NativeJobCore.Trace_DroppedEvents();
    public static void TraceClear() => NativeJobCore.Trace_Clear();
    public static int TraceReadAll(NativeTraceEvent[] buffer, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return NativeJobCore.Trace_ReadAll(buffer, maxCount);
    }

    // ======================== 执行中 / 异常冲排（透传） ========================
    /// <summary>当前线程是否正在执行某个 job。</summary>
    public static bool IsExecutingJob => NativeJobCore.IsExecutingJob;

    /// <summary>抛出所有已记录的 Job 异常（跨所有 batch）。</summary>
    public static void FlushRecordedExceptions() => NativeJobCore.FlushRecordedExceptions();

    // ======================== 句柄辅助（内部，供 ECS/句柄使用） ========================
    internal static void RetainRawHandleForUse(IntPtr handle) => NativeJobCore.RetainRawHandleForUse(handle);
    internal static void ReleaseRawHandleForFinalizer(IntPtr handle) => NativeJobCore.ReleaseRawHandleForFinalizer(handle);

    // ======================== Job 字段写入器注册表（为 IJobChunk 非 blittable 结构） ========================
    public unsafe delegate void JobFieldWriter<T>(byte* dst, ref T job) where T : struct;
    internal static readonly Dictionary<Type, Delegate> s_jobFieldWriters = new();

    /// <summary>注册 Job 字段显式写入器（由 NativeTranspiler 生成代码在 NativeExports 静态构造时调用）</summary>
    public static void RegisterJobFieldWriter(Type type, Delegate writer) => s_jobFieldWriters[type] = writer;

    internal static bool TryGetJobFieldWriter(Type type, out Delegate writer) => s_jobFieldWriters.TryGetValue(type, out writer);
}
}
