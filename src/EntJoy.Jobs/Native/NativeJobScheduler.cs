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
    // 2026-10-04：原先此处的 6 个**死字段**（DirectAssistClaims / ExhaustedTickets /
    //   ScheduleToPublishEwmaNs / PublishToFirstMainClaimEwmaNs / PublishToFirstWorkerClaimEwmaNs /
    //   QueueLockWaitEwmaNs）已两侧同步删除，ABI 升到 3。修改此结构体必须同时改
    //   `src/NativeDll/JobSystem.h` + `Exports.h` 与 Unity 侧 port（三处同序），否则错位读错。
    public ulong PublishToCompletionEwmaNs;
    public ulong PerRangeExecEwmaNs;       // 每个 range 平均执行时间 (ns, EWMA)
    public ulong AssistExecPctEwma;        // assist 有效率 (0~100)
    public ulong CompletionOverheadUs;     // 调度/等待开销 = completionUs - perRangeExecUs
    // Tile/partition fields; keep order in sync with Exports.h.
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
    // Exact per-batch timing distribution; keep order in sync with Exports.h.
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
/// 原生调度器门面（P/Invoke → C++ Chase-Lev）。零跨层依赖。
/// 所有共享状态（委托缓存、上下文池、异常、ThreadStatic、纯 P/Invoke 指针）由
/// <see cref="NativeJobCore"/> 独占持有；上层 chunk 调度层与本文共用。
/// </summary>
public static unsafe partial class NativeJobScheduler
{
    // ======================== 配置 ========================
        /// <summary>当 NativeDll 不可用时自动回退到 ManagedJobScheduler。</summary>
        internal static bool UseFallback { get; set; }
    /// <summary>
    /// 并行 for 默认 tiles/worker：batchSize=0 时按此值个 tile/worker 切分。
    /// 2026-09-29 由 4 改为 64：flat parallel-for 路径在 8/15 worker 两档实测最优
    /// （8w 整步 −4~−5.6 ms 6/6 同号；15w −1~−2.2 ms；tpw=16 与 ≈2000 都更差）。
    /// 只影响 flat 路径；ECS chunk 路径用自己的常数。可用 `ENTJOY_TILES_PER_WORKER` 覆盖。
    /// </summary>
    public static int TilesPerWorker = 64;

    /// <summary>Guided（chunk ∝ 剩余工作量）tile 调度。默认关闭（uniform 更通用）。</summary>
    public static bool GuidedEnabled = false;
    public static int GuidedK = 4;
    public static int GuidedFloor = 16;

    /// <summary>
    /// 启用 per-job 自动 batch（JobCostCache）。默认开启：按每 job 每元素成本 EWMA
    /// 自动求解最优 tile 数，轻任务减 tiles（减参与 worker → 减唤醒成本），重任务维持并行度。
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
    private static bool _jobCostCacheEnabled = ReadJccEnabledFromEnv();

    /// <summary>2026-10-05（doc16 §40）：JCC 的**托管默认值也读 env**。
    /// 否则 <c>Initialize</c> 里的 <c>JobSystem_SetJobCostCacheEnabled(JobCostCacheEnabled)</c>
    /// 会把 native 侧读到的 env 值**盖回去**，`ENTJOY_JOB_COST_CACHE=0` 就形同虚设。
    /// 未设/非 0 ⇒ true（与历史行为一致）。</summary>
    private static bool ReadJccEnabledFromEnv()
    {
        string v = System.Environment.GetEnvironmentVariable("ENTJOY_JOB_COST_CACHE");
        return string.IsNullOrEmpty(v) || v != "0";
    }

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
        if (numThreads == 0)
        {
            string? env = Environment.GetEnvironmentVariable("ENTJOY_JOB_WORKERS");
            if (int.TryParse(env, out int w) && w >= 0)
                numThreads = w;
        }
        try
        {
            // Re-arm the idempotent shutdown gate after a previous ProcessExit/DomainUnload callback.
            NativeJobCore.ResetShutdownGate();
            if (NativeJobCore.JobSystem_Initialize(numThreads) != 0)
                throw new InvalidOperationException("Native JobSystem failed to initialize (worker creation/OOM).");
            UseFallback = false;
            RegisterPersistentAllocator();
            NativeJobCore.ValidateStatsLayout();
            NativeJobCore.RegisterCurrentBatchIdCallback();
            if (TilesPerWorker > 0)
                NativeJobCore.JobSystem_ConfigureTilesPerWorker(TilesPerWorker);
            NativeJobCore.JobSystem_SetJobCostCacheEnabled(JobCostCacheEnabled ? 1 : 0);
            ConfigureGuidedFromEnv();
        }
        catch (Exception ex)
        {
            // C++ 调度器不可用 → 自动回退到纯 C# ManagedJobScheduler
            NativeJobCore.SafeShutdown();
            UseFallback = true;
            System.Console.Error.WriteLine(
                $"[EntJoy][WARN] Native JobSystem (NativeDll.dll/C++ Chase-Lev) failed to initialize; " +
                $"falling back to pure C# ManagedJobScheduler. Native kernels will NOT run. Reason: {ex.GetType().Name}: {ex.Message}");
            global::EntJoy.JobSystem.Managed.ManagedJobScheduler.Initialize(
                numThreads <= 0 ? Math.Max(1, Environment.ProcessorCount - 1) : numThreads);
        }
    }

    public static int JobWorkerCount
    {
        get => NativeJobCore.JobSystem_GetWorkerCount();
    }

    public static void Shutdown() { if (UseFallback) { Managed.ManagedJobScheduler.Shutdown(); return; } NativeJobCore.SafeShutdown(); }
    public static void PrewakeWorkersOnce() => NativeJobCore.JobSystem_PrewakeWorkers();

    /// <summary>让全部委托缓存换代（清字典 + 自增代次，静态泛型缓存按代次惰性重建）。</summary>
    public static void InvalidateDelegateCaches() => NativeJobCore.InvalidateDelegateCaches();

    /// <summary>当前委托缓存代次（每次 <see cref="InvalidateDelegateCaches"/> +1）。</summary>
    public static int DelegateCacheGeneration => NativeJobCore.DelegateCacheGeneration;

    /// <summary>共享的 `Marshal.FreeHGlobal` cleanup 指针（生成代码统一用它）。</summary>
    public static IntPtr SharedFreeHGlobalCleanupPtr => NativeJobCore.SharedFreeHGlobalCleanupPtr;

    /// <summary>
    /// 热重载：布局校验 → 排空 → 缓存换代 → 换句柄 → 重跑注册（前两步失败不改变任何状态）。
    /// 调用方必须先停派发；`newPath` 必须是新文件名（同路径 `Load` 返回旧模块）。
    /// 拒绝不抛异常，看 <see cref="NativeReloadResult.Outcome"/> / <see cref="NativeReloadResult.Message"/>。
    /// </summary>
    public static NativeReloadResult ReloadNativeLibrary(string newPath)
    {
        if (string.IsNullOrEmpty(newPath)) throw new ArgumentNullException(nameof(newPath));
        if (UseFallback)
            return NativeReloadResult.Refuse(NativeReloadOutcome.NotApplicable,
                "managed fallback tier: no native NativeTranspiled module to reload.", false, DelegateCacheGeneration);
        return NativeJobCore.ReloadNativeTranspiledCore(newPath);
    }

    /// <summary>登记"换过 NativeTranspiled 句柄后要重跑"的回调（幂等；生成物在 `[ModuleInitializer]` 里登记自己）。</summary>
    public static void RegisterReloadCallback(Action callback)
        => NativeJobCore.RegisterReloadCallback(callback);

    /// <summary>当前登记的重载回调数（诊断用）。</summary>
    public static int ReloadCallbackCount => NativeJobCore.ReloadCallbackCount;

    /// <summary>登记一个组件的布局哈希（`ComponentMetaRegistry.Register` 在模块初始化期逐组件调用）。</summary>
    public static void RecordComponentLayout(System.Reflection.Assembly owner, string typeName, ulong layoutHash)
        => NativeJobCore.RecordComponentLayout(owner, typeName, layoutHash);

    /// <summary>当前加载程序集的组件布局指纹（逐类型哈希异或，与顺序无关）。</summary>
    public static ulong ComponentLayoutFingerprint => NativeJobCore.ComponentLayoutFingerprint;

    /// <summary>当前档位是否提供排空导出（托管回退档恒为 false）。</summary>
    public static bool SupportsDrainAll => !UseFallback && NativeJobCore.HasDrainAll;

    /// <summary>
    /// 在当前 NativeTranspiled 句柄上按名取导出函数指针（生成代码用它替代
    /// `[DllImport(EntryPoint="Get_…_AdapterPtr")]`：DllImport 的解析结果被运行时缓存、换不掉）。
    /// ⚠ 名字不存在 ⇒ 抛（显式失败，不返回 0）。
    /// </summary>
    public static IntPtr GetNativeExportPtr(string entryPointName)
        => NativeJobCore.GetNativeExportPtr(entryPointName);

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

    internal static void RegisterPersistentAllocator()
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
        // 2026-10-02（09 §22.6）：优先走**原生 adapter 直调**（去掉 native→managed→native thunk）。
        // 前提（缺一不可）：① 生成代码注册了 adapter 指针；② 该类型有**字段显式写入器**
        //   —— 原生 adapter 按 C++ 偏移读 ctx，Debug 下 NativeArray 带 DisposeSentinel ⇒ 裸拷贝布局不可靠，
        //   必须与生成代码同一套逐字段写入（`NativeExports` 静态构造里注册）。
        // 任一不满足 ⇒ 原样回退下述托管路径，未转译 job / 老行为完全不受影响。
        if (s_nativeSingleJobEnabled
            && !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            && TryGetNativeJobAdapter(typeof(T), out IntPtr nativeAdapter, out int nativeCtxSize)
            && TryGetJobFieldWriter(typeof(T), out Delegate fieldWriter))
        {
            if (TryScheduleRawWithNativeAdapter(nativeAdapter, nativeCtxSize, fieldWriter, ref job, dependsOn, typeof(T), out NativeJobHandle nativeHandle))
                return nativeHandle;
        }

        bool managedContext = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        var ctx = managedContext ? NativeJobCore.AllocManagedContext(ref job) : NativeJobCore.AllocContext(ref job);
        try
        {
            var cache = NativeJobCore.JobDelegateCacheFor<T>.Cache;
            NativeJobHandle handle = NativeJobCore.ScheduleRaw(cache.FuncPtr, ctx, managedContext ? NativeJobCore.ManagedCleanupPtr : NativeJobCore.CleanupPtr, dependsOn);
            if (!handle.IsValid)
            {
                if (managedContext) NativeJobCore.ManagedCleanup(ctx);
                else NativeJobCore.Cleanup(ctx);
                return default;
            }
            NativeJobCore.RegisterScheduledJobName(handle.Handle, typeof(T).Name);
            return handle;
        }
        catch
        {
            if (managedContext) NativeJobCore.ManagedCleanup(ctx);
            else NativeJobCore.Cleanup(ctx);
            throw;
        }
    }

    public static NativeJobHandle ScheduleFor<T>(ref T job, int length, NativeJobHandle? dependsOn = null)
        where T : struct, IJobFor
    {
        if (length <= 0) return default;
        // 2026-10-02（IJobFor 补齐，09 §26）：走 `IJobFor` **专用的 index 形原生 adapter**
        //（`X_Execute_IndexAdapter(void*, int index)`，转译器为 IJobFor 专门发射），
        // 由原生 `Scheduler::ScheduleFor` 直调 —— 与它的**单线程串行**语义
        //（`for (i<length) func(ctx,i)`）**同一形态**，不再借道批量入口。
        if (s_nativeSingleJobEnabled
            && !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            && TryGetNativeForAdapter(typeof(T), out IntPtr nativeForAdapter, out int nativeForCtx)
            && TryGetJobFieldWriter(typeof(T), out Delegate forWriter))
        {
            if (TryScheduleForWithNativeAdapter(nativeForAdapter, nativeForCtx, forWriter, ref job, dependsOn, typeof(T),
                    length, out NativeJobHandle forHandle))
                return forHandle;
        }
        bool managedContext = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        var ctx = managedContext ? NativeJobCore.AllocManagedContext(ref job) : NativeJobCore.AllocContext(ref job);
        try
        {
            var cache = NativeJobCore.ForDelegateCacheFor<T>.Cache;
            NativeJobHandle handle = NativeJobCore.ScheduleForRaw(cache.FuncPtr, ctx, managedContext ? NativeJobCore.ManagedCleanupPtr : NativeJobCore.CleanupPtr, length, dependsOn);
            if (!handle.IsValid)
            {
                if (managedContext) NativeJobCore.ManagedCleanup(ctx);
                else NativeJobCore.Cleanup(ctx);
                return default;
            }
            NativeJobCore.RegisterScheduledJobName(handle.Handle, typeof(T).Name);
            return handle;
        }
        catch
        {
            if (managedContext) NativeJobCore.ManagedCleanup(ctx);
            else NativeJobCore.Cleanup(ctx);
            throw;
        }
    }

    public static NativeJobHandle ScheduleParallelFor<T>(ref T job, int length, int batchSize, NativeJobHandle? dependsOn = null,
        ClaimPolicy claim = ClaimPolicy.Auto)
        where T : struct, IJobParallelFor
    {
        if (length <= 0) return default;
        // 2026-10-02（同类修复，09 §24）：静态运行期 API 也优先走原生 adapter
        //（生成扩展 `job.Schedule(len,batch)` 早已原生；这条静态路径此前一律托管）。
        if (s_nativeSingleJobEnabled
            && !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            && TryGetNativeJobAdapter(typeof(T), out IntPtr nativePfaAdapter, out int nativePfaCtx)
            && TryGetJobFieldWriter(typeof(T), out Delegate pfaWriter))
        {
            if (TryScheduleBatchWithNativeAdapter(nativePfaAdapter, nativePfaCtx, pfaWriter, ref job, dependsOn, typeof(T), length, batchSize, claim, out NativeJobHandle pfaHandle))
                return pfaHandle;
        }
        bool managedContext = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        var ctx = managedContext ? NativeJobCore.AllocManagedContext(ref job) : NativeJobCore.AllocContext(ref job);
        try
        {
            var cache = NativeJobCore.GetAutoParallelForCache<T>();
            NativeJobHandle handle = NativeJobCore.ScheduleParallelForBatchRaw(cache.FuncPtr, ctx, managedContext ? NativeJobCore.ManagedCleanupPtr : NativeJobCore.CleanupPtr, length, batchSize, dependsOn, claim);
            if (!handle.IsValid)
            {
                if (managedContext) NativeJobCore.ManagedCleanup(ctx);
                else NativeJobCore.Cleanup(ctx);
                return default;
            }
            NativeJobCore.RegisterScheduledJobName(handle.Handle, typeof(T).Name);
            return handle;
        }
        catch
        {
            if (managedContext) NativeJobCore.ManagedCleanup(ctx);
            else NativeJobCore.Cleanup(ctx);
            throw;
        }
    }

    public static NativeJobHandle ScheduleParallelForBatch<T>(ref T job, int length, int batchSize, NativeJobHandle? dependsOn = null,
        ClaimPolicy claim = ClaimPolicy.Auto)
        where T : struct, IJobParallelForBatch
    {
        if (length <= 0) return default;
        // 2026-10-02（同类修复，09 §24）：同上，`IJobParallelForBatch` 的原生 adapter 也是 BatchJobFunc 形。
        if (s_nativeSingleJobEnabled
            && !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            && TryGetNativeJobAdapter(typeof(T), out IntPtr nativePfbAdapter, out int nativePfbCtx)
            && TryGetJobFieldWriter(typeof(T), out Delegate pfbWriter))
        {
            if (TryScheduleBatchWithNativeAdapter(nativePfbAdapter, nativePfbCtx, pfbWriter, ref job, dependsOn, typeof(T), length, batchSize, claim, out NativeJobHandle pfbHandle))
                return pfbHandle;
        }
        bool managedContext = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        var ctx = managedContext ? NativeJobCore.AllocManagedContext(ref job) : NativeJobCore.AllocContext(ref job);
        try
        {
            var cache = NativeJobCore.ParallelForBatchDelegateCacheFor<T>.Cache;
            NativeJobHandle handle = NativeJobCore.ScheduleParallelForBatchRaw(cache.FuncPtr, ctx, managedContext ? NativeJobCore.ManagedCleanupPtr : NativeJobCore.CleanupPtr, length, batchSize, dependsOn, claim);
            if (!handle.IsValid)
            {
                if (managedContext) NativeJobCore.ManagedCleanup(ctx);
                else NativeJobCore.Cleanup(ctx);
                return default;
            }
            NativeJobCore.RegisterScheduledJobName(handle.Handle, typeof(T).Name);
            return handle;
        }
        catch
        {
            if (managedContext) NativeJobCore.ManagedCleanup(ctx);
            else NativeJobCore.Cleanup(ctx);
            throw;
        }
    }

    // ======================== Complete / IsCompleted / Release ========================
    public static void Complete(ref NativeJobHandle h)
    {
        if (UseFallback) return; // 托管路径通过 JobHandle._managedHandle 处理
        // 诊断（ENTJOY_DIAG_CSHARP_PHASE=1）：把每 job 的 Complete 拆成 5 段，
        // 用来回答"每 job 调度的开销里，哪些是 EntJoy 真的多付的"（对照 Unity 同形状 4.71 µs/job）。
        bool cDiag = CSharpPhaseDiag.Sampling("complete.t0");
        long t0 = cDiag ? CSharpPhaseDiag.Now() : 0;
        // 隐式批：Complete 前先 flush 当前批（Unity ScheduleBatchedJobs 同语义：Complete 隐式刷新）
        ImplicitBatch.FlushForComplete();
        long t1 = cDiag ? CSharpPhaseDiag.Now() : 0;
        // Complete 不消费句柄（Unity 值语义；拷贝共享 Box，不能在此 detach）。
        // 等待窗口持 retain，防并发 Release/finalizer 回收正在等待的 state（TOCTOU）。
        using var handleLease = new NativeJobCore.RetainedNativeDependency(h);
        IntPtr handle = handleLease.Handle;
        if (handle == IntPtr.Zero) return;
        long t2 = cDiag ? CSharpPhaseDiag.Now() : 0;

        NativeJobCore.JobSystem_Complete(handle);            // ← 原生：自旋/等待/退役握手全在这里
        long t3 = cDiag ? CSharpPhaseDiag.Now() : 0;
        // 性能项 2（收尾）：无待取异常（正常路径）时**不付** `JobSystem_GetDiagnosticBatchId`
        // 那次 P/Invoke —— 计数器为 0 ⇒ 本 batch 不可能有已记录异常（记录必先自增，
        // 且 Complete 返回时本批 job 已全部结束），与 ThrowRecordedJobExceptions 首行判断等价。
        // 每 job 省一次跨托管/原生调用（10k jobs/frame 量级下有意义）；异常路径语义不变。
        ulong batchId = NativeJobCore.HasPendingJobExceptions
            ? NativeJobCore.JobSystem_GetDiagnosticBatchId(handle)
            : 0UL;
        long t4 = cDiag ? CSharpPhaseDiag.Now() : 0;
        NativeJobCore.ThrowRecordedJobExceptions(batchId);
        long t5 = cDiag ? CSharpPhaseDiag.Now() : 0;

        if (cDiag)
        {
            CSharpPhaseDiag.Add("complete.flush", CSharpPhaseDiag.Us(t0, t1));
            CSharpPhaseDiag.Add("complete.leaseAcquire", CSharpPhaseDiag.Us(t1, t2));
            CSharpPhaseDiag.Add("complete.native_wait", CSharpPhaseDiag.Us(t2, t3));
            CSharpPhaseDiag.Add("complete.getBatchId", CSharpPhaseDiag.Us(t3, t4));
            CSharpPhaseDiag.Add("complete.excCheck", CSharpPhaseDiag.Us(t4, t5));
            CSharpPhaseDiag.Add("complete.t0", CSharpPhaseDiag.Us(t0, t5));
        }
    }

    public static bool IsCompleted(NativeJobHandle h)
    {
        if (UseFallback) return true; // 托管路径由 ManagedJobHandle 单独处理
        ImplicitBatch.FlushForComplete();   // 隐式批：查询前 flush，防误报未完成
        if (!h.IsValid) return true;
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

    public static NativeJobHandle ScheduleParallelForBatchRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, int batchSize, NativeJobHandle? dependsOn = null, ClaimPolicy claim = ClaimPolicy.Auto)
        => NativeJobCore.ScheduleParallelForBatchRaw(funcPtr, contextPtr, cleanupPtr, length, batchSize, dependsOn, claim);

    // transpiler 生成的 Schedule_{Job} 在调度后调用，把 batchId → Job 名注册进调试器字典。
    public static void RegisterScheduledJob(IntPtr handle, string jobName)
    {
        long t0 = CSharpPhaseDiag.Sampling("sched.register") ? CSharpPhaseDiag.Now() : 0;
        NativeJobCore.RegisterScheduledJobName(handle, jobName);
        if (t0 != 0) CSharpPhaseDiag.Add("sched.register", CSharpPhaseDiag.Us(t0, CSharpPhaseDiag.Now()));
    }

    // ======================== 面板 / 状态 ========================
    public static NativeJobSystemStats GetStats() => NativeJobCore.JobSystem_GetStats();

    /// <summary>N12 `ENTJOY_WAKE_POLL` 的生效证据：提交侧 [跳过写唤醒字, 真的广播] 次数。
    /// 只看耗时曲线无法区分"开关没生效"与"生效了但无用"，所以必须读这两个数：
    /// 连发小 job 的形状下 skips 应比 wakes 大 2~3 个数量级。老 DLL 无该导出时返回 false。</summary>
    public static bool TryGetWakePollCounters(out ulong skips, out ulong wakes) =>
        NativeJobCore.TryGetWakePollCounters(out skips, out wakes);

    /// <summary>认领几何（<see cref="ClaimPolicy"/>）的生效证据：按声明值分桶的批数 [spread, adjacent, auto]。
    /// 验收判据：调用点传了 <c>ClaimPolicy.Spread</c> 就必须看到 spread&gt;0；否则说明该调用点**静默**
    /// 绑到了托管的 <c>JobExtensions.Schedule&lt;T&gt;</c>（键会从模块内 RVA 变成堆地址，09 §52.5）。
    /// 老 DLL 无该导出时返回 false。</summary>
    public static bool TryGetClaimGeomCounters(out ulong spread, out ulong adjacent, out ulong autoDecl) =>
        NativeJobCore.TryGetClaimGeomCounters(out spread, out adjacent, out autoDecl);

    /// <summary>运行时开关主线程 assist（第 N+1 个执行者）。默认关闭。</summary>
    public static void SetMainThreadAssistEnabled(bool enabled) =>
        NativeJobCore.JobSystem_SetMainThreadAssist(enabled);

    /// <summary>
    /// 隐式批收集层互斥切换：
    /// - true  = Native 收集（透明拦截 Schedule* 的 tile 路径 job，EndFrame/Complete 统一提交 + 单次唤醒）；
    ///           切换前先关闭 C# 层（排空其积压）。NativeDll 不可用时自动回退 C# ImplicitBatch 收集。
    /// - false = 关闭 Native 收集（内部 flush 排空积压），转接启用 C# ImplicitBatch 收集（显式 Add 路径）。
    /// </summary>
    public static void SetImplicitBatchEnabled(bool enabled)
    {
        if (enabled)
        {
            if (NativeJobCore.NativeDllHandle != IntPtr.Zero)
            {
                ImplicitBatch.SetEnabled(false);   // 切走 C# 层（先排空积压）
                NativeJobCore.JobSystem_SetImplicitBatchEnabled(1);
            }
            else
            {
                // native 不可用：回退 C# 隐式收集层
                ImplicitBatch.SetEnabled(true);
            }
        }
        else
        {
            NativeJobCore.JobSystem_SetImplicitBatchEnabled(0);   // native 关闭（内部排空积压）
            ImplicitBatch.SetEnabled(true);                        // 转接 C# 收集层
        }
    }

    /// <summary>隐式批 force point：提交当前收集层（Native pending + C# 批）并统一唤醒（帧末调用）。</summary>
    public static void FlushPendingSubmits()
    {
        NativeJobCore.JobSystem_FlushPendingSubmits();
        ImplicitBatch.EndFrame();
    }

    /// <summary>帧末别名（无头框架推荐：每个 tick 末尾调用一次）。</summary>
    public static void EndFrame()
    {
        NativeJobCore.JobSystem_FlushPendingSubmits();
        ImplicitBatch.EndFrame();
    }

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

    // ======================== 句柄辅助（内部，供 JobHandle 使用） ========================
    internal static void RetainRawHandleForUse(IntPtr handle) => NativeJobCore.RetainRawHandleForUse(handle);
    internal static void ReleaseRawHandleForFinalizer(IntPtr handle) => NativeJobCore.ReleaseRawHandleForFinalizer(handle);

    // ======================== Job 字段写入器注册表（为非 blittable 结构） ========================
    public unsafe delegate void JobFieldWriter<T>(byte* dst, ref T job) where T : struct;
    internal static readonly Dictionary<Type, Delegate> s_jobFieldWriters = new();

    /// <summary>注册 Job 字段显式写入器（由 NativeTranspiler 生成代码在 NativeExports 静态构造时调用）</summary>
    public static void RegisterJobFieldWriter(Type type, Delegate writer) => s_jobFieldWriters[type] = writer;

    internal static bool TryGetJobFieldWriter(Type type, out Delegate writer) => s_jobFieldWriters.TryGetValue(type, out writer);

    // ==================== IJob 原生 adapter 注册表（2026-10-02，09 §22.6） ====================
    // 背景：转译器一直为 `IJob` 产出原生 adapter + `Get_X_Execute_AdapterPtr()`，但绑定层漏接线
    //（`BindingsGenerator` 的 IJob 分支用 `Marshal.GetFunctionPointerForDelegate` 造托管 thunk；
    //  运行时 `NativeJobScheduler.Schedule<T>` 也只有托管路由）⇒ 单任务 job 每次派发都多一次
    //  native→managed→native 转换。Unity 侧是 IL2CPP（原生 AOT），故只有走原生直调才是同性质对比。
    //
    // 设计：**注册表**（与上面的 JobFieldWriter 注册表同型）。
    //   · 生成代码在 `NativeExports` 静态构造里注册 `typeof(T) → Get_X_Execute_AdapterPtr()`；
    //   · 运行时泛型 `Schedule<T>` / `ScheduleFor<T>` 优先查表：命中且**该类型也有字段写入器**时走原生直调
    //     （原生 adapter 按 C++ 偏移读 ctx ⇒ 必须用生成代码的**显式逐字段写入**，不能用裸拷贝）；
    //   · 未命中（未转译 job、或 Debug 下非 blittable 且无写入器）⇒ **原样回退托管 delegate**，行为不变。
    internal static readonly Dictionary<Type, IntPtr> s_nativeJobAdapterPtrs = new();
    /// <summary>与 adapter 配套的 ctx 字节数（由生成代码给出，= 其逐字段写入器的总长）。</summary>
    internal static readonly Dictionary<Type, int> s_nativeJobCtxSizes = new();
    /// <summary>
    /// 2026-10-02（09 §26）：`IJobFor` 专用的 **index 形** adapter（`IndexJobFunc(void*, int)`）。
    /// 与批形态分开存：两者 ABI 不同，混用会签名错位。
    /// </summary>
    internal static readonly Dictionary<Type, IntPtr> s_nativeForAdapterPtrs = new();

    /// <summary>注册 `IJob`/批形态的原生 adapter 指针（由 NativeTranspiler 生成代码调用）。</summary>
    public static void RegisterNativeJobAdapter(Type type, IntPtr adapterPtr, int ctxSize)
    {
        s_nativeJobAdapterPtrs[type] = adapterPtr;
        s_nativeJobCtxSizes[type] = ctxSize;
        // 自证横幅（与框架其它自证同风格）：证明该类型的原生直调**已接线**（注册发生在
        // NativeExports 静态构造里 ⇒ 进程启动即可见）。避免"静默 no-op 当成已修复"。
        Console.Error.WriteLine($"[NATIVEJOB] native direct-dispatch wired: {type.Name} (ctx={ctxSize}B)");
    }

    /// <summary>注册 `IJobFor` 的 index 形原生 adapter（`ScheduleFor` 的直调路径）。</summary>
    public static void RegisterNativeForAdapter(Type type, IntPtr indexAdapterPtr, int ctxSize)
    {
        s_nativeForAdapterPtrs[type] = indexAdapterPtr;
        s_nativeJobCtxSizes[type] = ctxSize;
        Console.Error.WriteLine($"[NATIVEJOB] IJobFor index-shaped native adapter wired: {type.Name} (ctx={ctxSize}B)");
    }

    // ==================== 按 job 名的批表绑定（doc16 §46） ====================
    /// <summary>
    /// 把 `ENTJOY_JOB_BATCH_BY_NAME` 里按**job 名**登记的批表槽位绑定到该 job **实际派发用的函数指针**。
    /// 由转译器生成的绑定在 `NativeExports` 静态构造里逐个 job 调用（`funcPtr` = 该 job 交给
    /// `ScheduleRaw` / `ScheduleParallelForBatchRaw` 的那个指针，单任务形与批形各取本形）。
    /// <para>
    /// 通用性：名字取 <see cref="Type.Name"/>、指针取托管自己派发用的值 ⇒ **不依赖任何 C++ 符号命名
    /// 规则/命名空间/模块**（这正是不再拼导出名、不再扫 PE 导出表的原因）。
    /// </para>
    /// <para>
    /// 时序：在**静态构造期**完成 ⇒ 批表在任何派发之前就是终态，派发路径对表**只读**（无竞态）。
    /// </para>
    /// </summary>
    public static void BindNativeJobBatchName(Type type, IntPtr funcPtr)
    {
        if (type == null || funcPtr == IntPtr.Zero) return;
        int matched = NativeJobCore.JobSystem_BindBatchName(type.Name, funcPtr);
        if (!s_batchNameBannerDone)
        {
            s_batchNameBannerDone = true;
            Console.Error.WriteLine(
                "[NATIVEJOB] batch-by-name binding: wired (name = managed Type.Name -> the exact dispatch function pointer)");
        }
        // 只有该名字确实出现在 `ENTJOY_JOB_BATCH_BY_NAME` 里时才打（默认档零噪声）；同时给出所绑指针，
        // 自证"绑的就是派发用的那个指针"而不是一个靠命名规则猜出来的符号。
        if (matched > 0)
            Console.Error.WriteLine(
                $"[NATIVEJOB] batch-by-name bound: {type.Name} -> 0x{funcPtr.ToInt64():X} (ENTJOY_JOB_BATCH_BY_NAME entry)");
    }

    private static bool s_batchNameBannerDone;

    private static readonly HashSet<Type> s_nativeFirstUseLogged = new();

    /// <summary>
    /// 单任务 job 原生直调的开关（`ENTJOY_NATIVE_SINGLE_JOB=0` = 回退托管 delegate）。
    /// 默认开；存在两个用途：① A/B 对照臂（同一 DLL、只换 env）；② 出问题时的回退阀。
    /// </summary>
    private static readonly bool s_nativeSingleJobEnabled = ReadNativeSingleJobEnabled();

    private static bool ReadNativeSingleJobEnabled()
    {
        string? v = Environment.GetEnvironmentVariable("ENTJOY_NATIVE_SINGLE_JOB");
        bool on = v == null || v[0] != '0';
        Console.Error.WriteLine(on
            ? "[NATIVEJOB] single-job native direct-dispatch: on (default 2026-10-02; =0 falls back to managed thunk)"
            : "[NATIVEJOB] single-job native direct-dispatch: off (explicit ENTJOY_NATIVE_SINGLE_JOB=0)");
        return on;
    }

    /// <summary>首次真正走原生直调时打一行（运行时自证：注册 ≠ 实际被用）。</summary>
    private static void LogNativeFirstUse(Type type)
    {
        lock (s_nativeFirstUseLogged)
        {
            if (!s_nativeFirstUseLogged.Add(type)) return;
        }
        Console.Error.WriteLine($"[NATIVEJOB] first native direct-dispatch: {type.Name}");
    }

    internal static bool TryGetNativeJobAdapter(Type type, out IntPtr adapterPtr, out int ctxSize)
    {
        if (s_nativeJobAdapterPtrs.TryGetValue(type, out adapterPtr)
            && s_nativeJobCtxSizes.TryGetValue(type, out ctxSize))
            return ctxSize > 0;
        ctxSize = 0;
        return false;
    }

    /// <summary>`IJobFor` 的 index 形 adapter 查表（含配套 ctxSize）。</summary>
    internal static bool TryGetNativeForAdapter(Type type, out IntPtr indexAdapterPtr, out int ctxSize)
    {
        if (s_nativeForAdapterPtrs.TryGetValue(type, out indexAdapterPtr)
            && s_nativeJobCtxSizes.TryGetValue(type, out ctxSize))
            return ctxSize > 0;
        ctxSize = 0;
        return false;
    }

    /// <summary>
    /// 三种原生 adapter 形态共用的"租 ctx + 逐字段写入"一步。
    /// ctx 来自框架自带 `ContextPool`（与 `AllocContext` 同一套 4 字节前缀 ⇒ 释放复用同一个
    /// <see cref="NativeJobCore.CleanupPtr"/>，无逐派发 malloc/free）。
    /// `ctxSize &lt;= 0`（无字段的 job —— 生成器不会为它注册）是**唯一**的不可用判据。
    /// 字段写入器是生成代码的纯指针写、不会抛 ⇒ 不设 try/catch：真有异常就冒泡（fail-fast），不静默回退。
    /// </summary>
    private static unsafe bool TryRentMarshalledContext<T>(
        int ctxSize, Delegate fieldWriter, ref T job, out IntPtr ctx) where T : struct
    {
        ctx = IntPtr.Zero;
        if (ctxSize <= 0) return false;
        ctx = NativeJobCore.RentMarshalledContext(ctxSize);
        ((JobFieldWriter<T>)fieldWriter)((byte*)ctx, ref job);
        return true;
    }

    /// <summary>
    /// 单任务（`JobFunc(void*)`）原生直调。ctx 布局 = 生成代码的逐字段写入。
    /// </summary>
    private static unsafe bool TryScheduleRawWithNativeAdapter<T>(
        IntPtr nativeAdapter, int ctxSize, Delegate fieldWriter, ref T job, NativeJobHandle? dependsOn, Type type,
        out NativeJobHandle handle) where T : struct, IJob
    {
        handle = default;
        if (!TryRentMarshalledContext(ctxSize, fieldWriter, ref job, out IntPtr ctx))
            return false;
        handle = NativeJobCore.ScheduleRaw(nativeAdapter, ctx, NativeJobCore.CleanupPtr, dependsOn);
        if (!handle.IsValid)
        {
            NativeJobCore.Cleanup(ctx);
            return false;
        }
        LogNativeFirstUse(type);
        NativeJobCore.RegisterScheduledJobName(handle.Handle, type.Name);
        return true;
    }

    /// <summary>
    /// 2026-10-02（09 §26）：`IJobFor` 的**index 形**（`IndexJobFunc(void*, int)`）原生直调 ——
    /// 与原生 `Scheduler::ScheduleFor`（单线程串行 `for(i) func(ctx,i)`）**同一形态**，
    /// 不再借道批量入口。adapter 由转译器为 `IJobFor` 专门发射（`X_Execute_IndexAdapter`）。
    /// </summary>
    private static unsafe bool TryScheduleForWithNativeAdapter<T>(
        IntPtr nativeIndexAdapter, int ctxSize, Delegate fieldWriter, ref T job, NativeJobHandle? dependsOn, Type type,
        int length, out NativeJobHandle handle) where T : struct
    {
        handle = default;
        if (!TryRentMarshalledContext(ctxSize, fieldWriter, ref job, out IntPtr ctx))
            return false;
        handle = NativeJobCore.ScheduleForRaw(nativeIndexAdapter, ctx, NativeJobCore.CleanupPtr, length, dependsOn);
        if (!handle.IsValid)
        {
            NativeJobCore.Cleanup(ctx);
            return false;
        }
        LogNativeFirstUse(type);
        NativeJobCore.RegisterScheduledJobName(handle.Handle, type.Name);
        return true;
    }

    /// <summary>
    /// 批形态（`IJobParallelFor` / `IJobParallelForBatch`）的原生直调：
    /// 生成的原生 adapter 签名 `void(void* ctx, int startIndex, int count)` 与原生 typedef
    /// `BatchJobFunc` **逐字一致** ⇒ 可直接把指针交给 `ScheduleParallelForBatchRaw`。
    /// </summary>
    private static unsafe bool TryScheduleBatchWithNativeAdapter<T>(
        IntPtr nativeAdapter, int ctxSize, Delegate fieldWriter, ref T job, NativeJobHandle? dependsOn, Type type,
        int length, int batchSize, ClaimPolicy claim, out NativeJobHandle handle) where T : struct
    {
        handle = default;
        if (!TryRentMarshalledContext(ctxSize, fieldWriter, ref job, out IntPtr ctx))
            return false;
        handle = NativeJobCore.ScheduleParallelForBatchRaw(
            nativeAdapter, ctx, NativeJobCore.CleanupPtr, length, batchSize, dependsOn, claim);
        if (!handle.IsValid)
        {
            NativeJobCore.Cleanup(ctx);
            return false;
        }
        LogNativeFirstUse(type);
        NativeJobCore.RegisterScheduledJobName(handle.Handle, type.Name);
        return true;
    }
}
}
