using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using EntJoy;
namespace EntJoy.JobSystem
{

/// <summary>
/// 跨语言共享的 Chunk 任务数据结构（与 C++ ChunkJobData 一一对应）
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChunkJobData
{
    public void* entityArray;           // Entity 数组首地址
    public int entityCount;             // 实体数量
    public int componentCount;          // 组件种类数
    public void** componentArrays;      // 每个组件数组首地址（长度为 componentCount）
    public int* componentSizes;         // 每个组件大小（字节，长度为 componentCount）
    public void** enableBitMaps;        // 每个 enableable 组件位图指针（可为 null，长度为 componentCount）
    public int* componentTypeIndices;   // 组件类型索引数组
    public IntPtr chunkHandle;          // GCHandle IntPtr，用于在回调中恢复 Chunk 对象
    public void** requiredComponentArrays; // NativeTranspile IJobChunk 所需组件数组指针
    public int requiredComponentCount;     // requiredComponentArrays 数量
}

/// <summary>
/// NativeTranspile 轻量 Chunk 数据结构（与 C++ ChunkData 一一对应）。
/// 只包含 NativeTranspile 作业实际需要的字段，跳过 ChunkJobData 的冗余信息。
/// enableBitMaps 预留为将来支持 IEnableComponent 做准备。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChunkData
{
    public void** componentArrays;      // 组件数组指针 [requiredCount]，编译时已知索引
    public int entityCount;             // 实体数量
    public int requiredComponentCount;  // 组件数组数量
    public void** enableBitMaps;        // enable 位图 [enableCount]，无过滤时为 null（预留）
    public int enableBitmapCount;       // enable 位图数量，0 表示无过滤（预留）
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct EntityBatchData
{
    public void** componentArrays;
    public void** enableBitMaps;
    public int entityCount;
    public int enableBitmapCount;
}

/// <summary>
/// Chunk 上下文包的内存布局（非托管）
/// 必须标记 Sequential 以确保内存布局与指针访问一致
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ChunkContextHeader
{
    public int chunkCount;               // Chunk 数量
    public int hasEnabledFilter;         // 是否有 enable 过滤
    public IntPtr queryAllEnabledTypes;  // int[]（类型哈希数组）指针
    public int allEnabledCount;          // AllEnabled 数组长度
    public int gcHandleStartIndex;       // GCHandle 列表起始索引（-1 = 无 GCHandle）
    public IntPtr chunksPtr;             // ChunkJobData 数组指针（用于 cleanup 回收）
    public int cleanupInProgress;        // 防止重复清理的标志
    public int ownsChunkData;            // 该 context 是否负责释放 chunksPtr + 每 chunk 缓冲区（与 GCHandle 解耦）
    public IntPtr requiredComponentTypeIds; // NativeTranspiler IJobChunk 所需组件类型 ID 数组
    public int requiredComponentTypeIdCount; // 所需组件类型 ID 数量
    // 紧接着是 job 的原始数据（变长）
}


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

internal enum ChunkScheduleMode
{
    PublishNoAssist = 0,
    PublishAssist = 1,
    DeferTinyOnly = 2,
    ImmediateNative = 3,
    DeferredPublish = 4,
    DeferredPublishNoAssist = 5
}

internal enum NativeEcsJobKind
{
    Chunk = 0,
    Entity = 1
}

/// <summary>
/// 原生作业调度器，所有作业通过 P/Invoke 调度到 C++ JobSystem 执行。
/// 支持 IJob、IJobFor、IJobParallelFor、IJobParallelForBatch、IJobChunk。
/// 此类型在全局命名空间中，便于源代码生成器引用。
/// </summary>
public static unsafe partial class NativeJobScheduler
{
    [ThreadStatic] private static int _jobExecutionDepth;
    // native 每 job 执行窗口 set/clear 的当前 batch id。C# 异常按此归属，
    // Complete(h) 只抛本 handle 的异常。
    [ThreadStatic] private static ulong _currentBatchId;

    // batchId → Job 名，供 native Dear ImGui Timeline 显示 Job 名。
    // GUI 线程只读并发字典，无锁安全。
    private static readonly ConcurrentDictionary<ulong, string> _batchIdToJobName = new();

    // 仅当调试面板（LaunchDebuggerGUI）开启后才记录 batchId→名字，避免影响正常调度热路径
    private static volatile bool _debugNameCaptureEnabled;

    internal static bool IsExecutingJob => _jobExecutionDepth > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnterJobExecution() => _jobExecutionDepth++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExitJobExecution() => _jobExecutionDepth--;

    // 记录当前 batch 对应的 Job 名（托管回调路径：执行线程上 native 已 set batch id）。
    private static void RegisterCurrentBatchJobName(string name)
    {
        if (!_debugNameCaptureEnabled) return;
        ulong batchId = _currentBatchId;
        if (batchId == 0) return;
        _batchIdToJobName[batchId] = name;
    }

    // 记录某个已调度 handle 的 Job 名（原生直跑路径：调度返回后立读 diagnosticId）。
    // 仅对立即发布的调度模式有效（ImmediateNative/PublishAssist 等）；延迟发布取不到则跳过。
    internal static void RegisterScheduledJobName(IntPtr handle, string name)
    {
        if (!_debugNameCaptureEnabled || handle == IntPtr.Zero || _jobSystem_GetDiagnosticBatchId == null)
            return;
        ulong id = _jobSystem_GetDiagnosticBatchId(handle);
        if (id != 0)
        {
            _batchIdToJobName.TryAdd(id, name);
        }
    }

    // ======================== DLL 函数指针 ========================
    private static IntPtr _nativeDll = IntPtr.Zero;
    private static int _shutdownRequested;

    // 函数指针（通过 GetProcAddress 获取）
    private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_Initialize;
    private static delegate* unmanaged[Cdecl]<int> _jobSystem_GetWorkerCount;
    private static delegate* unmanaged[Cdecl]<void> _jobSystem_Shutdown;
    private static delegate* unmanaged[Cdecl]<void> _jobSystem_PrewakeWorkers;
    private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_ConfigureTilesPerWorker;
    private static delegate* unmanaged[Cdecl]<int, int, int, void> _jobSystem_ConfigureGuided;
    private static delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, void*>, delegate* unmanaged[Cdecl]<void*, void>, void> _jobSystem_RegisterPersistentAllocator;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> _jobSystem_Schedule;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, IntPtr, IntPtr> _jobSystem_ScheduleParallelForBatch;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr> _jobSystem_ScheduleFor;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_Complete;
    private static delegate* unmanaged[Cdecl]<IntPtr, ulong> _jobSystem_GetDiagnosticBatchId;
    private static delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<ulong, void>, void> _jobSystem_RegisterCurrentBatchId;
    private static delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<ulong, byte*, int, int>, delegate* unmanaged[Cdecl]<void>, void> _jobSystem_RegisterNameResolver;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_RetainHandle;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _jobSystem_IsCompleted;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_ReleaseHandle;
    private static delegate* unmanaged[Cdecl]<IntPtr*, int, IntPtr> _jobSystem_CombineDependencies;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, IntPtr> _jobSystem_ScheduleChunkJob;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, IntPtr> _jobSystem_ScheduleChunkJobEx;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, IntPtr> _jobSystem_ScheduleChunkRangeJobEx;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, IntPtr> _jobSystem_ScheduleEntityBatchJobEx;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, IntPtr> _jobSystem_ScheduleAndCompleteEntityBatchJobEx;
    private static delegate* unmanaged[Cdecl]<NativeJobSystemStats*, void> _jobSystem_GetStats;
    private static delegate* unmanaged[Cdecl]<void> _jobSystem_ResetStats;
    private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_SetTimingDiagnostics;
    private static delegate* unmanaged[Cdecl]<void> _jobSystem_LaunchGUI;
    private static delegate* unmanaged[Cdecl]<byte*, uint, void> _jobSystem_RecordDirectCall;
    private static delegate* unmanaged[Cdecl]<byte*, uint, ulong> _jobSystem_BeginDirectCall;
    private static delegate* unmanaged[Cdecl]<ulong, void> _jobSystem_EndDirectCall;
    // Profiler 函数指针
    private static delegate* unmanaged[Cdecl]<int, void> _profiler_SetEnabled;
    private static delegate* unmanaged[Cdecl]<int> _profiler_IsEnabled;
    private static delegate* unmanaged[Cdecl]<ProfilerEntry*, int, int> _profiler_ReadAll;
    private static delegate* unmanaged[Cdecl]<void> _profiler_Clear;
    private static delegate* unmanaged[Cdecl]<int, void> _trace_SetEnabled;
    private static delegate* unmanaged[Cdecl]<int> _trace_IsEnabled;
    private static delegate* unmanaged[Cdecl]<NativeTraceEvent*, int, int> _trace_ReadAll;
    private static delegate* unmanaged[Cdecl]<ulong> _trace_DroppedEvents;
    private static delegate* unmanaged[Cdecl]<void> _trace_Clear;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static unsafe void LoadNativeDll()
    {
        const string dllName = "NativeDll.dll";
        string cwd = Environment.CurrentDirectory;
        string baseDir = AppContext.BaseDirectory;
        string assemblyDir = Path.GetDirectoryName(typeof(NativeJobScheduler).Assembly.Location);
        string entryDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);

        var paths = new List<string>();

        // 1. 首先从运行基目录查找（最接近当前进程实际加载目录）
        if (!string.IsNullOrEmpty(baseDir))
        {
            paths.Add(Path.Combine(baseDir, dllName));
            paths.Add(Path.Combine(baseDir, "Debug", dllName));
            paths.Add(Path.Combine(baseDir, "Release", dllName));
        }

        // 2. 从入口程序集（exe）所在目录查找
        if (!string.IsNullOrEmpty(entryDir))
        {
            paths.Add(Path.Combine(entryDir, dllName));
            var parentOfEntry = Path.GetDirectoryName(entryDir);
            if (!string.IsNullOrEmpty(parentOfEntry))
                paths.Add(Path.Combine(parentOfEntry, "bin", dllName));
        }

        // 3. 从程序集所在目录查找
        if (!string.IsNullOrEmpty(assemblyDir))
        {
            paths.Add(Path.Combine(assemblyDir, dllName));
            paths.Add(Path.Combine(assemblyDir, "Debug", dllName));
            paths.Add(Path.Combine(assemblyDir, "Release", dllName));
            var up2Bin = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "bin"));
            paths.Add(Path.Combine(up2Bin, dllName));
        }

        // 4. 从项目源路径推导
        {
            string probe = string.IsNullOrEmpty(assemblyDir) ? cwd : assemblyDir;
            while (probe != null && probe.Length >= 3)
            {
                var vcxproj = Path.Combine(probe, "src", "NativeDll", "NativeDll.vcxproj");
                if (File.Exists(vcxproj))
                {
                    var vcxprojDir = Path.GetDirectoryName(vcxproj);
                    if (!string.IsNullOrEmpty(vcxprojDir))
                    {
                        var nativeDllDir = Path.GetFullPath(Path.Combine(vcxprojDir, "..", "..", "bin"));
                        paths.Add(Path.Combine(nativeDllDir, dllName));
                    }
                    break;
                }
                var parent = Path.GetDirectoryName(probe);
                if (parent == probe) break;
                probe = parent;
            }
        }

        // 5. 从 CWD 查找
        {
            paths.Add(Path.Combine(cwd, ".godot", "mono", "temp", "bin", "Debug", dllName));
            paths.Add(Path.Combine(cwd, ".godot", "mono", "temp", "bin", "Release", dllName));
            paths.Add(Path.Combine(cwd, ".godot", "mono", "temp", "bin", "ExportDebug", "win-x64", dllName));
            paths.Add(Path.Combine(cwd, ".godot", "mono", "temp", "bin", "ExportRelease", "win-x64", dllName));
            paths.Add(Path.Combine(cwd, dllName));
            paths.Add(Path.Combine(cwd, "..", "bin", dllName));
            paths.Add(Path.Combine(cwd, "..", "..", "bin", dllName));
        }

        // 先按运行目录优先级尝试，确保 Godot/EntJoySample 各自加载自己的 NativeDll。
        // 只有运行目录没有 DLL 时，才按“最后写入时间”降序 fallback，避免串到旧 DLL。
        var primaryCandidates = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToArray();

        var existingCandidates = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Select(p => new { Path = p, LastWriteUtc = File.GetLastWriteTimeUtc(p) })
            .OrderByDescending(x => x.LastWriteUtc)
            .ToArray();

        var fullPaths = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IntPtr dllHandle = IntPtr.Zero;
        string loadedPath = string.Empty;
        foreach (var candidate in primaryCandidates)
        {
            try
            {
                dllHandle = NativeLibrary.Load(candidate);
                if (dllHandle != IntPtr.Zero)
                {
                    loadedPath = candidate;
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NativeJobScheduler] Failed to load {candidate}: {ex.Message}");
            }
        }

        foreach (var candidate in existingCandidates)
        {
            if (dllHandle != IntPtr.Zero)
                break;

            try
            {
                dllHandle = NativeLibrary.Load(candidate.Path);
                if (dllHandle != IntPtr.Zero)
                {
                    loadedPath = candidate.Path;
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NativeJobScheduler] Failed to load {candidate.Path}: {ex.Message}");
            }
        }

        if (dllHandle == IntPtr.Zero)
        {
            try { dllHandle = NativeLibrary.Load(dllName); } catch { }
        }

        if (dllHandle == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[NativeJobScheduler] ERROR: Cannot find {dllName}. Searched:");
            foreach (string path in fullPaths)
            {
                string fullPath = Path.GetFullPath(path);
                Console.Error.WriteLine($"  - {fullPath}: {(File.Exists(fullPath) ? "EXISTS" : "NOT FOUND")}");
            }
            Console.Error.WriteLine($"  - CWD: {cwd}");
            return;
        }

        _nativeDll = dllHandle;
        if (!string.IsNullOrEmpty(loadedPath))
        {
            Console.Error.WriteLine($"[NativeJobScheduler] Loaded NativeDll: {loadedPath} (UTC: {File.GetLastWriteTimeUtc(loadedPath):O})");
        }

        // DLL 分离：生成代码（wrapper/adapter）编译进 NativeTranspiled.dll，通过
        // [DllImport("NativeTranspiled", ...)] 由 CLR 延迟加载。此处主动从 NativeDll
        // 所在目录显式加载它，确保两块 DLL 都在运行时被找到、且 NativeDll 先于
        // NativeTranspiled 加载满足其 DLL 依赖。NativeTranspiled.dll 缺失不影响核心
        // 调度（仅生成代码路径不可用），故仅记录、不抛错。
        TryLoadNativeTranspiled(loadedPath);

        _jobSystem_Initialize = (delegate* unmanaged[Cdecl]<int, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_Initialize");
        _jobSystem_GetWorkerCount = (delegate* unmanaged[Cdecl]<int>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_GetWorkerCount");
        _jobSystem_Shutdown = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_Shutdown");
        _jobSystem_PrewakeWorkers = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_PrewakeWorkers");
        _jobSystem_ConfigureTilesPerWorker = (delegate* unmanaged[Cdecl]<int, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ConfigureTilesPerWorker");
        _jobSystem_ConfigureGuided = (delegate* unmanaged[Cdecl]<int, int, int, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ConfigureGuided");
        _jobSystem_RegisterPersistentAllocator = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, void*>, delegate* unmanaged[Cdecl]<void*, void>, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_RegisterPersistentAllocator");
        _jobSystem_Schedule = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_Schedule");
        _jobSystem_ScheduleParallelForBatch = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleParallelForBatch");
        _jobSystem_ScheduleFor = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleFor");
        _jobSystem_Complete = (delegate* unmanaged[Cdecl]<IntPtr, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_Complete");
        _jobSystem_GetDiagnosticBatchId = (delegate* unmanaged[Cdecl]<IntPtr, ulong>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_GetDiagnosticBatchId");
        _jobSystem_RegisterCurrentBatchId = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<ulong, void>, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_RegisterCurrentBatchId");
        _jobSystem_RegisterNameResolver = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<ulong, byte*, int, int>, delegate* unmanaged[Cdecl]<void>, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_RegisterNameResolver");
        _jobSystem_RetainHandle = (delegate* unmanaged[Cdecl]<IntPtr, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_RetainHandle");
        _jobSystem_IsCompleted = (delegate* unmanaged[Cdecl]<IntPtr, int>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_IsCompleted");
        _jobSystem_ReleaseHandle = (delegate* unmanaged[Cdecl]<IntPtr, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ReleaseHandle");
        _jobSystem_CombineDependencies = (delegate* unmanaged[Cdecl]<IntPtr*, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_CombineDependencies");
        _jobSystem_ScheduleChunkJob = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleChunkJob");
        _jobSystem_ScheduleChunkJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleChunkJobEx");
        _jobSystem_ScheduleChunkRangeJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleChunkRangeJobEx");
        _jobSystem_ScheduleEntityBatchJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleEntityBatchJobEx");
        _jobSystem_ScheduleAndCompleteEntityBatchJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleAndCompleteEntityBatchJobEx");
        _jobSystem_GetStats = (delegate* unmanaged[Cdecl]<NativeJobSystemStats*, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_GetStats");
        _jobSystem_ResetStats = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ResetStats");
        _jobSystem_SetTimingDiagnostics = (delegate* unmanaged[Cdecl]<int, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_SetTimingDiagnostics");
        _jobSystem_LaunchGUI = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobDebuggerGUI_Launch");
        _jobSystem_RecordDirectCall = (delegate* unmanaged[Cdecl]<byte*, uint, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_RecordDirectCall");
        // 新导出用 TryGetExport：旧 DLL 缺失时降级（Begin 返回 0 = 无窗口，不影响运行）
        if (NativeLibrary.TryGetExport(dllHandle, "JobSystem_BeginDirectCall", out IntPtr fnBeginDirectCall))
            _jobSystem_BeginDirectCall = (delegate* unmanaged[Cdecl]<byte*, uint, ulong>)fnBeginDirectCall;
        if (NativeLibrary.TryGetExport(dllHandle, "JobSystem_EndDirectCall", out IntPtr fnEndDirectCall))
            _jobSystem_EndDirectCall = (delegate* unmanaged[Cdecl]<ulong, void>)fnEndDirectCall;

        _profiler_SetEnabled = (delegate* unmanaged[Cdecl]<int, void>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_SetEnabled");
        _profiler_IsEnabled = (delegate* unmanaged[Cdecl]<int>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_IsEnabled");
        _profiler_ReadAll = (delegate* unmanaged[Cdecl]<ProfilerEntry*, int, int>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_ReadAll");
        _profiler_Clear = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_Clear");
        _trace_SetEnabled = (delegate* unmanaged[Cdecl]<int, void>)
            NativeLibrary.GetExport(dllHandle, "Trace_SetEnabled");
        _trace_IsEnabled = (delegate* unmanaged[Cdecl]<int>)
            NativeLibrary.GetExport(dllHandle, "Trace_IsEnabled");
        _trace_ReadAll = (delegate* unmanaged[Cdecl]<NativeTraceEvent*, int, int>)
            NativeLibrary.GetExport(dllHandle, "Trace_ReadAll");
        _trace_DroppedEvents = (delegate* unmanaged[Cdecl]<ulong>)
            NativeLibrary.GetExport(dllHandle, "Trace_DroppedEvents");
        _trace_Clear = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "Trace_Clear");

        AppDomain.CurrentDomain.ProcessExit += static (_, _) => SafeShutdown();
        AppDomain.CurrentDomain.DomainUnload += static (_, _) => SafeShutdown();
    }

    // DLL 分离：从 NativeDll 所在目录显式加载 NativeTranspiled.dll（生成代码 wrapper/adapter）。
    // 非致命——缺失仅使生成代码路径不可用，核心调度照常。加载成功后由 CLR 的
    // [DllImport("NativeTranspiled", ...)] 复用同一已加载模块，无需再次解析导出。
    // #21：同时注册 DllImportResolver，使 [DllImport("NativeTranspiled")] 的 P/Invoke
    // 解析走同一路径逻辑（NativeDll 目录 → 基目录 → CLR 默认），不再依赖脆弱搜索。
    private static void TryLoadNativeTranspiled(string nativeDllPath)
    {
        const string generatedDllName = "NativeTranspiled.dll";
        try
        {
            // 解析器：按 NativeDll 目录 / AppContext.BaseDirectory / 基目录子目录 / CLR 默认
            NativeLibrary.SetDllImportResolver(typeof(NativeJobScheduler).Assembly, (libName, assembly, searchPath) =>
            {
                if (!string.Equals(libName, "NativeTranspiled", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;
                string[] searchDirs =
                {
                    !string.IsNullOrEmpty(nativeDllPath) ? Path.GetDirectoryName(nativeDllPath) : null,
                    AppContext.BaseDirectory,
                };
                foreach (var dir in searchDirs)
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string candidate = Path.Combine(dir, generatedDllName);
                    if (File.Exists(candidate))
                        return NativeLibrary.Load(candidate);
                }
                return IntPtr.Zero;   // 让 CLR 走默认搜索
            });

            if (!string.IsNullOrEmpty(nativeDllPath))
            {
                string dir = Path.GetDirectoryName(nativeDllPath);
                string candidate = Path.Combine(dir ?? string.Empty, generatedDllName);
                if (File.Exists(candidate))
                {
                    NativeLibrary.Load(candidate);
                    Console.Error.WriteLine($"[NativeJobScheduler] Loaded {generatedDllName} from {candidate}");
                    return;
                }
            }
            // 回退：交给 CLR 默认搜索（基目录/PATH）
            try { NativeLibrary.Load(generatedDllName); }
            catch { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NativeJobScheduler] Warning: could not load {generatedDllName}: {ex.Message}");
        }
    }

    // ======================== 包装函数 ========================
    private static bool IsNativeLoaded => _nativeDll != IntPtr.Zero && _jobSystem_Initialize != null;

    // 热路径守卫：主体只剩一个分支 + 冷调用，保证可被 JIT 内联进包装函数。
    // throw 抽到 NoInlining 冷路径，否则异常路径会让方法无法内联。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureNativeLoaded()
    {
        if (!IsNativeLoaded)
            ThrowNativeNotLoaded();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNativeNotLoaded()
    {
        throw new InvalidOperationException("NativeDll.dll is not loaded. Ensure NativeDll.dll is copied next to the executable or Godot output directory.");
    }

    private static void JobSystem_Initialize(int numThreads)
    {
        EnsureNativeLoaded();
        _jobSystem_Initialize(numThreads);
    }

    private static void JobSystem_Shutdown()
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_Shutdown == null) return;
        _jobSystem_Shutdown();
    }

    private static void JobSystem_PrewakeWorkers()
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_PrewakeWorkers == null) return;
        _jobSystem_PrewakeWorkers();
    }

    private static void JobSystem_ConfigureTilesPerWorker(int tilesPerWorker)
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_ConfigureTilesPerWorker == null) return;
        _jobSystem_ConfigureTilesPerWorker(tilesPerWorker);
    }

    private static void JobSystem_ConfigureGuided(int enabled, int k, int floor)
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_ConfigureGuided == null) return;
        _jobSystem_ConfigureGuided(enabled, k, floor);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IntPtr JobSystem_Schedule(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, IntPtr dependency)
    {
        EnsureNativeLoaded();
        return _jobSystem_Schedule(funcPtr, context, cleanupPtr, dependency);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IntPtr JobSystem_ScheduleParallelForBatch(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, int length, int batchSize, IntPtr dependency)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleParallelForBatch(funcPtr, context, cleanupPtr, length, batchSize, dependency);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IntPtr JobSystem_ScheduleFor(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, int length, IntPtr dependency)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleFor(funcPtr, context, cleanupPtr, length, dependency);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void JobSystem_Complete(IntPtr handle)
    {
        EnsureNativeLoaded();
        _jobSystem_Complete(handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong JobSystem_GetDiagnosticBatchId(IntPtr handle)
    {
        EnsureNativeLoaded();
        return _jobSystem_GetDiagnosticBatchId(handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void JobSystem_RetainHandle(IntPtr handle)
    {
        EnsureNativeLoaded();
        _jobSystem_RetainHandle(handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int JobSystem_IsCompleted(IntPtr handle)
    {
        EnsureNativeLoaded();
        return _jobSystem_IsCompleted(handle);
    }

    private static void JobSystem_ReleaseHandle(IntPtr handle)
    {
        // 注意：与其它包装函数不同，此处不调用 EnsureNativeLoaded()
        // 因为此路径在 finalizer 线程、DomainUnload 或 ProcessExit 期间
        // 也可能被调用，此时 native DLL 可能已卸载。
        // 非 finalizer 路径调用前应通过 RetainedNativeDependency 确保有效性。
        if (_nativeDll == IntPtr.Zero || _jobSystem_ReleaseHandle == null) return;
        _jobSystem_ReleaseHandle(handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IntPtr JobSystem_CombineDependencies(IntPtr[] handles, int count)
    {
        EnsureNativeLoaded();
        fixed (IntPtr* ptr = handles) return _jobSystem_CombineDependencies(ptr, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IntPtr JobSystem_ScheduleChunkJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, ChunkJobData* chunks, int chunkCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleChunkJobEx(funcPtr, context, cleanupPtr, chunks, chunkCount, dependency, (int)mode, workerCap, rangeSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IntPtr JobSystem_ScheduleChunkRangeJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, ChunkJobData* chunks, int chunkCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleChunkRangeJobEx(funcPtr, context, cleanupPtr, chunks, chunkCount, dependency, (int)mode, workerCap, rangeSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IntPtr JobSystem_ScheduleEntityBatchJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, EntityBatchData* batches, int batchCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0, NativeEcsJobKind jobKind = NativeEcsJobKind.Entity)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleEntityBatchJobEx(funcPtr, context, cleanupPtr, batches, batchCount, dependency, (int)mode, workerCap, rangeSize, (int)jobKind);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IntPtr JobSystem_ScheduleAndCompleteEntityBatchJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, EntityBatchData* batches, int batchCount, IntPtr dependency, ChunkScheduleMode mode = ChunkScheduleMode.PublishAssist, int workerCap = 0, int rangeSize = 0, NativeEcsJobKind jobKind = NativeEcsJobKind.Entity)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleAndCompleteEntityBatchJobEx(funcPtr, context, cleanupPtr, batches, batchCount, dependency, (int)mode, workerCap, rangeSize, (int)jobKind);
    }

    private static NativeJobSystemStats JobSystem_GetStats()
    {
        EnsureNativeLoaded();
        NativeJobSystemStats stats = default;
        _jobSystem_GetStats(&stats);
        return stats;
    }
    private static void JobSystem_ResetStats()
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_ResetStats == null) return;
        _jobSystem_ResetStats();
    }
    private static void JobSystem_SetTimingDiagnostics(bool enabled)
    {
        EnsureNativeLoaded();
        _jobSystem_SetTimingDiagnostics(enabled ? 1 : 0);
    }

    // ======================== 委托类型 ========================
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void JobFunc(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void IndexJobFunc(IntPtr context, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BatchJobFunc(IntPtr context, int startIndex, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ChunkJobFuncDelegate(IntPtr context, ChunkJobData* chunkData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ChunkRangeJobFuncDelegate(IntPtr context, ChunkJobData* chunks, int startIndex, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CleanupFunc(IntPtr context);

    // ======================== 委托缓存 ========================
    internal static readonly ConcurrentDictionary<Type, DelegateCache> _delegateCache = new();
    internal sealed class DelegateCache { public readonly Delegate Delegate; public readonly IntPtr FuncPtr; public DelegateCache(Delegate del) { Delegate = del; FuncPtr = Marshal.GetFunctionPointerForDelegate(del); } }

    private static readonly CleanupFunc _cleanup = Cleanup;
    private static readonly IntPtr _cleanupPtr = Marshal.GetFunctionPointerForDelegate(_cleanup);
    internal static readonly CleanupFunc _managedCleanup = ManagedCleanup;
    internal static readonly IntPtr _managedCleanupPtr = Marshal.GetFunctionPointerForDelegate(_managedCleanup);
    internal static readonly CleanupFunc _rawChunkBatchCleanup = RawChunkBatchCleanup;
    internal static readonly IntPtr _rawChunkBatchCleanupPtr = Marshal.GetFunctionPointerForDelegate(_rawChunkBatchCleanup);
    internal static readonly object _chunkGCHandlesLock = new();
    internal static readonly List<GCHandle> _chunkGCHandles = new();

    // ======================== 公共接口 ========================
    public static void Initialize(int numThreads = 0)
    {
        Interlocked.Exchange(ref _shutdownRequested, 0);
        JobSystem_Initialize(numThreads);
        RegisterPersistentAllocator();
        RegisterCurrentBatchIdCallback();
        if (TilesPerWorker > 0)
            JobSystem_ConfigureTilesPerWorker(TilesPerWorker);
        ConfigureGuidedFromEnv();
    }

    /// <summary>
    /// 强制启动 Dear ImGui 调试面板并开始监听 JobSystem 实时状态（不依赖 ENTJOY_DEBUG 环境变量）。
    /// 幂等：重复调用只启动一次。需在 Initialize() 之后调用。
    /// </summary>
    public static void LaunchDebuggerGUI()
    {
        _debugNameCaptureEnabled = true; // 仅调试面板开启后记录 batchId→Job名
        if (_nativeDll == IntPtr.Zero || _jobSystem_LaunchGUI == null) return;
        _jobSystem_LaunchGUI();
    }

    // transpiler 直调方法（C#/C++/ISPC/ISPC-MT，不经调度器）也上报一次"发布"，计入面板统计。
    public static unsafe void RecordDirectCall(string jobName, uint tiles)
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_RecordDirectCall == null) return;
        Span<byte> nameBuf = stackalloc byte[128];
        int n = Math.Min(jobName.Length, 127);
        for (int i = 0; i < n; i++) nameBuf[i] = (byte)jobName[i];
        nameBuf[n] = 0;
        fixed (byte* p = nameBuf) _jobSystem_RecordDirectCall(p, tiles);
    }

    /// <summary>
    /// 直调执行窗口开始（transpiler 包装器在 native 调用前调用）：分配 id、记发布、
    /// 并在当前线程泳道开启执行窗口（事件驱动）。返回 0 表示面板未开启（无窗口）。
    /// 必须与 <see cref="EndDirectCall"/> 成对调用（包装器用 try/finally 保证）。
    /// </summary>
    public static unsafe ulong BeginDirectCall(string jobName, uint tiles)
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_BeginDirectCall == null) return 0;
        Span<byte> nameBuf = stackalloc byte[128];
        int n = Math.Min(jobName.Length, 127);
        for (int i = 0; i < n; i++) nameBuf[i] = (byte)jobName[i];
        nameBuf[n] = 0;
        fixed (byte* p = nameBuf) return _jobSystem_BeginDirectCall(p, tiles);
    }

    /// <summary>直调执行窗口结束：关闭当前线程泳道窗口，追加共享时间线段。</summary>
    public static void EndDirectCall(ulong id)
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_EndDirectCall == null) return;
        _jobSystem_EndDirectCall(id);
    }

    /// <summary>
    /// 并行 for 默认 tiles/worker：batchSize=0 时原生 ResolveChunkSize 按此值个 tile/worker 切分。
    /// 26 = N=100k/15 worker → batch 257 / 390 tiles，等价于 batch=256 定标（p50 0.577 / p99 0.714）。
    /// 0 = 用原生默认；&gt;0 时在 Initialize 期覆盖。
    /// 测试要扫描粒度直接改此字段重编译即可。
    /// </summary>
    public static int TilesPerWorker = 26; // k=26, rc=390：GridSearch 可变代价最优（A/B 与 adapter 双确认）

    /// <summary>
    /// Guided（chunk ∝ 剩余工作量）tile 调度（OpenMP schedule(guided) 同族）。
    /// 头部大块（Poisson 平滑、非 straggler）+ 尾部小块（钳 straggler 上界），
    /// 总认领数 ~ W*k*ln(N/floor)。默认开启（A/B：QueryCore p50 -7~14%、uniform 不回归）。
    /// env 可覆盖：ENTJOY_GUIDED_TILES=0 关闭 / ENTJOY_GUIDED_K / ENTJOY_GUIDED_FLOOR 调参。
    /// </summary>
    public static bool GuidedEnabled = true;
    public static int GuidedK = 4;        // A/B 实测甜点（p50 0.596 / p95 0.778 @ floor=16；k=2 略逊、k=8 回归）
    public static int GuidedFloor = 16;

    private static void ConfigureGuidedFromEnv()
    {
        // env 覆盖（A/B 用，无需重编译）：ENTJOY_GUIDED_TILES=1 开，K/FLOOR 调参。
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
            JobSystem_ConfigureGuided(1, GuidedK, GuidedFloor);
        else
            JobSystem_ConfigureGuided(0, GuidedK, GuidedFloor);
        System.Console.WriteLine($"JobSystem|guided={GuidedEnabled}|k={GuidedK}|floor={GuidedFloor}");
    }

    // 托管 Persistent 分配器回调：原生 UnsafeList 扩容/释放走托管侧（C# 池化块 payload=base+16，
    // 原生 free(Ptr) 是内部指针释放 → 堆损坏 0xc0000374）。用 [UnmanagedCallersOnly] 免 GC 根、直通。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void* PersistentAllocUnmanaged(int size) => EntJoy.Collections.PersistentAllocator.Alloc(size);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void PersistentFreeUnmanaged(void* ptr) => EntJoy.Collections.PersistentAllocator.Free(ptr);

    private static void RegisterPersistentAllocator()
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_RegisterPersistentAllocator == null) return;
        _jobSystem_RegisterPersistentAllocator(&PersistentAllocUnmanaged, &PersistentFreeUnmanaged);
    }

    // native 每 job 执行窗口调 SetCurrentBatchId 写线程局部当前 batch。
    // 回调在 job 执行线程（worker/main/assist）上执行，_currentBatchId 为
    // [ThreadStatic]，各线程各持一份，无跨线程污染。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void SetCurrentBatchId(ulong batchId) => _currentBatchId = batchId;

    private static void RegisterCurrentBatchIdCallback()
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_RegisterCurrentBatchId == null) return;
        _jobSystem_RegisterCurrentBatchId(&SetCurrentBatchId);
        // 同时把 batchId→Job名 解析回调注册给 native，供 Dear ImGui Timeline 显示名字
        if (_jobSystem_RegisterNameResolver != null)
            _jobSystem_RegisterNameResolver(&ResolveBatchJobName, &ClearBatchJobNames);
    }

    // native 调试面板经此查询某 batch 对应的 Job 名。
    // 返回名长；无映射返回 0。在 GUI 线程调用，仅读并发字典，安全。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ResolveBatchJobName(ulong batchId, byte* buf, int bufLen)
    {
        if (buf == null || bufLen <= 0) return 0;
        if (_batchIdToJobName.TryGetValue(batchId, out var name) && !string.IsNullOrEmpty(name))
        {
            int n = Math.Min(name.Length, bufLen - 1);
            for (int i = 0; i < n; i++) buf[i] = (byte)name[i];
            buf[n] = 0;
            return n;
        }
        return 0;
    }

    // 调试面板关闭（GUI 线程退出）时由 native 调用，清空名字字典避免长期运行累积。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ClearBatchJobNames()
    {
        _batchIdToJobName.Clear();
        _debugNameCaptureEnabled = false;
    }
    /// <summary>
    /// 当前持久 Job Worker 数。与 Unity JobsUtility.JobWorkerCount 的用途一致；
    /// 默认值是逻辑处理器数减一，也可通过 Initialize(numThreads) 显式指定。
    /// </summary>
    public static int JobWorkerCount
    {
        get
        {
            EnsureNativeLoaded();
            return _jobSystem_GetWorkerCount();
        }
    }
    public static void Shutdown() => SafeShutdown();
    public static void PrewakeWorkersOnce() => JobSystem_PrewakeWorkers();

    private static void SafeShutdown()
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_Shutdown == null)
            return;
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
            return;
        DumpTimingDiagnosticsIfRequested();
        JobSystem_Shutdown();
    }

    /// <summary>
    /// ENTJOY_DIAG_TIMING=1：进程退出前 dump 原生侧 batch 时序劈分（框架诊断，零侵入——batchTotal /
    /// submitToFirstWorker / executionSpan / maxRange 由 RecordFinalizedBatchTiming 无条件采集，
    /// 无需开 g_timingDiagnosticsEnabled）。
    /// 用途：把 C# 侧 QueryCore（Stopwatch 墙钟）劈成 C# 包装 / C++ 调度 / C++ 执行 三段，
    /// 验证瓶颈是否真在 C# 开销。最慢批次（slowBatch，reservoir 内 batchTotal 最大）必是 query 批次。
    /// </summary>
    private static void DumpTimingDiagnosticsIfRequested()
    {
        if (Environment.GetEnvironmentVariable("ENTJOY_DIAG_TIMING") != "1") return;
        NativeJobSystemStats s = JobSystem_GetStats();
        static double us(ulong ns) => ns / 1000.0;
        Console.WriteLine("[TIMING] 注意: reservoir 混合 build+query 批次; P50 偏 build(批数多), P99/max + slowBatch 由 query 主导");
        Console.WriteLine($"[TIMING] samples={s.TimingSampleCount} dropped={s.TimingSamplesDropped}");
        Console.WriteLine($"[TIMING] batchTotal    p50={us(s.BatchTotalP50Ns):F1} p95={us(s.BatchTotalP95Ns):F1} p99={us(s.BatchTotalP99Ns):F1} max={us(s.BatchTotalMaxNs):F1} us  (原生侧单batch总耗时分布)");
        Console.WriteLine($"[TIMING] submit2First  p50={us(s.SubmitToFirstWorkerP50Ns):F1} max={us(s.SubmitToFirstWorkerMaxNs):F1} us  (调度→首个worker认领 = wake)");
        Console.WriteLine($"[TIMING] workerSpread  p50={us(s.WorkerStartSpreadP50Ns):F1} max={us(s.WorkerStartSpreadMaxNs):F1} us  (首worker→末worker开始)");
        Console.WriteLine($"[TIMING] executionSpan p50={us(s.ExecutionSpanP50Ns):F1} max={us(s.ExecutionSpanMaxNs):F1} us  (首tile开始→末tile结束 = 纯C++执行段)");
        Console.WriteLine($"[TIMING] maxRange      p50={us(s.MaxRangeP50Ns):F1} p95={us(s.MaxRangeP95Ns):F1} max={us(s.MaxRangeMaxNs):F1} us  (单tile执行耗时分布 = 执行地板)");
        Console.WriteLine($"[TIMING] slowBatch     id={s.SlowBatchId} total={us(s.SlowBatchTotalNs):F1} submit2First={us(s.SlowSubmitToFirstWorkerNs):F1} spread={us(s.SlowWorkerStartSpreadNs):F1} execSpan={us(s.SlowExecutionSpanNs):F1} maxRange={us(s.SlowMaxRangeNs):F1} assistTiles={s.SlowAssistTiles} coreMigrations={s.SlowCoreMigrations} (最慢批次=query 分解)");
        Console.WriteLine($"[TIMING] ewma          wakeLatency={us(s.WakeLatencyEwmaNs):F1} submit2First={us(s.SubmitToFirstWorkerEwmaNs):F1} workerSpread={us(s.WorkerStartSpreadEwmaNs):F1} lastTileToDone={us(s.LastTileToTopologyDoneEwmaNs):F1} us | assistExecPct={s.AssistExecPctEwma}% | prewake={s.PrewakeCount} parkWake={s.ParkWakeCount}");
    }
    public static NativeJobHandle Schedule<T>(ref T job, NativeJobHandle? dependsOn = null)
        where T : struct, IJob
    {
        bool managedContext = JobHasManagedReferences<T>();
        var ctx = managedContext ? AllocManagedContext(ref job) : AllocContext(ref job);
        bool cleanupByCpp = false;
        try
        {
            var cache = GetOrCreateDelegateCache<T, JobFunc>(() => CreateJobCallback<T>());
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            IntPtr handle = JobSystem_Schedule(cache.FuncPtr, ctx, managedContext ? _managedCleanupPtr : _cleanupPtr, dependencyLease.Handle);
            cleanupByCpp = true; // C++ now owns ctx via cleanup callback
            RegisterScheduledJobName(handle, typeof(T).Name);
            return new NativeJobHandle(handle);
        }
        catch
        {
            if (!cleanupByCpp)
            {
                if (managedContext) ManagedCleanup(ctx);
                else Cleanup(ctx);
            } // else: C++ will call cleanup when job completes
            throw;
        }
    }

    public static NativeJobHandle ScheduleFor<T>(ref T job, int length, NativeJobHandle? dependsOn = null)
        where T : struct, IJobFor
    {
        if (length <= 0) return default;
        bool managedContext = JobHasManagedReferences<T>();
        var ctx = managedContext ? AllocManagedContext(ref job) : AllocContext(ref job);
        bool cleanupByCpp = false;
        try
        {
            var cache = GetOrCreateDelegateCache<T, IndexJobFunc>(() => CreateForCallback<T>());
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            IntPtr handle = JobSystem_ScheduleFor(cache.FuncPtr, ctx, managedContext ? _managedCleanupPtr : _cleanupPtr, length, dependencyLease.Handle);
            cleanupByCpp = true;
            RegisterScheduledJobName(handle, typeof(T).Name);
            return new NativeJobHandle(handle);
        }
        catch
        {
            if (!cleanupByCpp)
            {
                if (managedContext) ManagedCleanup(ctx);
                else Cleanup(ctx);
            }
            throw;
        }
    }

    public static NativeJobHandle ScheduleParallelFor<T>(ref T job, int length, int batchSize, NativeJobHandle? dependsOn = null)
        where T : struct, IJobParallelFor
    {
        if (length <= 0) return default;
        bool managedContext = JobHasManagedReferences<T>();
        var ctx = managedContext ? AllocManagedContext(ref job) : AllocContext(ref job);
        bool cleanupByCpp = false;
        try
        {
            // 自动批处理回调：若 T 同时实现 IJobParallelForBatch，则回调内一次 Execute(start,count)；
            // 否则退回逐元素 Execute(i)。减少轻任务(Native S1/S2)上逐元素接口调度的开销，调用方无需改代码。
            var cache = AutoParallelForCallback<T>.GetCache();
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            IntPtr handle = JobSystem_ScheduleParallelForBatch(cache.FuncPtr, ctx, managedContext ? _managedCleanupPtr : _cleanupPtr, length, batchSize, dependencyLease.Handle);
            cleanupByCpp = true;
            RegisterScheduledJobName(handle, typeof(T).Name);
            return new NativeJobHandle(handle);
        }
        catch
        {
            if (!cleanupByCpp)
            {
                if (managedContext) ManagedCleanup(ctx);
                else Cleanup(ctx);
            }
            throw;
        }
    }

    public static NativeJobHandle ScheduleParallelForBatch<T>(ref T job, int length, int batchSize, NativeJobHandle? dependsOn = null)
        where T : struct, IJobParallelForBatch
    {
        if (length <= 0) return default;
        bool managedContext = JobHasManagedReferences<T>();
        var ctx = managedContext ? AllocManagedContext(ref job) : AllocContext(ref job);
        bool cleanupByCpp = false;
        try
        {
            var cache = GetOrCreateDelegateCache<T, BatchJobFunc>(() => CreateParallelForBatchCallback<T>());
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            IntPtr handle = JobSystem_ScheduleParallelForBatch(cache.FuncPtr, ctx, managedContext ? _managedCleanupPtr : _cleanupPtr, length, batchSize, dependencyLease.Handle);
            cleanupByCpp = true;
            RegisterScheduledJobName(handle, typeof(T).Name);
            return new NativeJobHandle(handle);
        }
        catch
        {
            if (!cleanupByCpp)
            {
                if (managedContext) ManagedCleanup(ctx);
                else Cleanup(ctx);
            }
            throw;
        }
    }

    /// <summary>
    /// 等待作业完成并释放句柄。
    /// 使用 P/Invoke 确保可靠同步（C++ std::atomic::wait + notify_all）。
    /// 任务完成时 C++ 自动回收状态，无需 C# 调用 ReleaseHandle。
    /// </summary>
    public static void Complete(ref NativeJobHandle h)
    {
        IntPtr handle = h.Detach();
        if (handle == IntPtr.Zero) return;

        // 先等待（Complete 保留调用方引用），再读 batchId，最后释放——
        // batch 可能在依赖链完成后才提交，diagnosticBatchId 提交后才有效，
        // 因此必须在等待之后、释放之前读。然后只抛本 batch 的异常。
        // finally 保证：即使 JobSystem_Complete / GetDiagnosticBatchId 抛异常
        //（如 DLL 卸载、进程退出竞态），ReleaseHandle 也必然执行，避免 HandleState 泄漏。
        ulong batchId = 0;
        try
        {
            JobSystem_Complete(handle);
            batchId = JobSystem_GetDiagnosticBatchId(handle);
        }
        finally
        {
            JobSystem_ReleaseHandle(handle);
        }
        ThrowRecordedJobExceptions(batchId);
    }

    public static bool IsCompleted(NativeJobHandle h)
    {
        using var handleLease = new RetainedNativeDependency(h);
        return handleLease.Handle == IntPtr.Zero || JobSystem_IsCompleted(handleLease.Handle) != 0;
    }

    public static void Release(NativeJobHandle h)
    {
        IntPtr handle = h.Detach();
        if (handle != IntPtr.Zero)
        {
            JobSystem_ReleaseHandle(handle);
        }
    }

    internal static void ReleaseRawHandleForFinalizer(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        JobSystem_ReleaseHandle(handle);
    }

    internal static void RetainRawHandleForUse(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        JobSystem_RetainHandle(handle);
    }

    internal readonly struct RetainedNativeDependency : IDisposable
    {
        public readonly IntPtr Handle;

        public RetainedNativeDependency(NativeJobHandle? dependency)
        {
            Handle = dependency.HasValue ? dependency.Value.RetainForUse() : IntPtr.Zero;
        }

        public RetainedNativeDependency(NativeJobHandle dependency)
        {
            Handle = dependency.RetainForUse();
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                JobSystem_ReleaseHandle(Handle);
        }
    }

    private static NativeJobHandle TrackEntityJob(EntityManager entityManager, NativeJobHandle handle)
    {
        entityManager?.RegisterActiveJob(handle);
        return handle;
    }

    public static NativeJobHandle ScheduleRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, NativeJobHandle? dependsOn = null)
    {
        using var dependencyLease = new RetainedNativeDependency(dependsOn);
        return new NativeJobHandle(JobSystem_Schedule(funcPtr, contextPtr, cleanupPtr, dependencyLease.Handle));
    }

    public static NativeJobHandle ScheduleForRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, NativeJobHandle? dependsOn = null)
    {
        using var dependencyLease = new RetainedNativeDependency(dependsOn);
        return new NativeJobHandle(JobSystem_ScheduleFor(funcPtr, contextPtr, cleanupPtr, length, dependencyLease.Handle));
    }

    public static NativeJobHandle ScheduleParallelForBatchRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, int batchSize, NativeJobHandle? dependsOn = null)
    {
        using var dependencyLease = new RetainedNativeDependency(dependsOn);
        return new NativeJobHandle(JobSystem_ScheduleParallelForBatch(funcPtr, contextPtr, cleanupPtr, length, batchSize, dependencyLease.Handle));
    }

    // transpiler 生成的 Schedule_{Job} 在调度后调用，把 batchId → Job 名注册进调试器字典。
    public static void RegisterScheduledJob(IntPtr handle, string jobName)
    {
        RegisterScheduledJobName(handle, jobName);
    }

    public static NativeJobSystemStats GetStats() => JobSystem_GetStats();
    public static void ResetStats() => JobSystem_ResetStats();
    public static void SetTimingDiagnosticsEnabled(bool enabled) =>
        JobSystem_SetTimingDiagnostics(enabled);

    // ======================== Profiler 公共接口 ========================
    internal static void Profiler_SetEnabled(int enabled) => _profiler_SetEnabled(enabled);
    internal static int Profiler_IsEnabled() => _profiler_IsEnabled();
    internal static unsafe int Profiler_ReadAll(ProfilerEntry[] buffer, int maxCount)
    {
        if (buffer == null || buffer.Length == 0) return 0;
        int count = Math.Min(maxCount, buffer.Length);
        fixed (ProfilerEntry* ptr = buffer) return _profiler_ReadAll(ptr, count);
    }
    internal static void Profiler_Clear() => _profiler_Clear();

    public static void TraceSetEnabled(bool enabled) => _trace_SetEnabled(enabled ? 1 : 0);
    public static bool TraceIsEnabled() => _trace_IsEnabled() != 0;
    public static ulong TraceDroppedEvents() => _trace_DroppedEvents();
    public static void TraceClear() => _trace_Clear();
    public static int TraceReadAll(NativeTraceEvent[] buffer, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length == 0 || maxCount <= 0) return 0;
        int count = Math.Min(maxCount, buffer.Length);
        fixed (NativeTraceEvent* ptr = buffer) return _trace_ReadAll(ptr, count);
    }

    public static NativeJobHandle CombineDependencies(params NativeJobHandle[] handles)
    {
        if (handles == null || handles.Length == 0) return default;
        var ptrs = new IntPtr[handles.Length];
        var leases = new RetainedNativeDependency[handles.Length];
        try
        {
            for (int i = 0; i < handles.Length; i++)
            {
                leases[i] = new RetainedNativeDependency(handles[i]);
                ptrs[i] = leases[i].Handle;
            }
            return new NativeJobHandle(JobSystem_CombineDependencies(ptrs, handles.Length));
        }
        finally
        {
            for (int i = 0; i < leases.Length; i++)
                leases[i].Dispose();
        }
    }

    // ======================== IJobChunk 调度 ========================
    internal static readonly object _rawChunkScheduleCacheLock = new();
    internal static readonly Dictionary<RawChunkScheduleCacheKey, RawChunkScheduleCache> _rawChunkScheduleCaches = new();
    internal static readonly Dictionary<RawChunkScheduleCacheKey, ManagedChunkScheduleCache> _managedChunkScheduleCaches = new();
    internal static readonly Dictionary<RawChunkScheduleCacheKey, EntityBatchScheduleCache> _entityBatchScheduleCaches = new();
    internal static readonly ConcurrentDictionary<IntPtr, GCHandle> _chunkContextLeases = new();

    public static void ClearRawChunkScheduleCaches(EntityManager entityManager)
    {
        if (entityManager == null) return;

        lock (_rawChunkScheduleCacheLock)
        {
            var keysToRemove = new List<RawChunkScheduleCacheKey>();
            foreach (var pair in _rawChunkScheduleCaches)
            {
                if (pair.Key.Matches(entityManager))
                {
                    pair.Value.Dispose();
                    keysToRemove.Add(pair.Key);
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
            {
                _rawChunkScheduleCaches.Remove(keysToRemove[i]);
            }

            keysToRemove.Clear();
            foreach (var pair in _managedChunkScheduleCaches)
            {
                if (pair.Key.Matches(entityManager))
                {
                    keysToRemove.Add(pair.Key);
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
            {
                _managedChunkScheduleCaches.Remove(keysToRemove[i]);
            }

            keysToRemove.Clear();
            foreach (var pair in _entityBatchScheduleCaches)
            {
                if (pair.Key.Matches(entityManager))
                {
                    pair.Value.Dispose();
                    keysToRemove.Add(pair.Key);
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
            {
                _entityBatchScheduleCaches.Remove(keysToRemove[i]);
            }
        }
    }

    public static NativeJobHandle ScheduleChunk<T>(ref T job, EntityManager entityManager, QueryBuilder query, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleChunkCore(ref job, entityManager, query, IntPtr.Zero, null, dependsOn);

    public static NativeJobHandle ScheduleChunkWithWorkerCap<T>(ref T job, EntityManager entityManager, QueryBuilder query, int workerCap, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleChunkCore(ref job, entityManager, query, IntPtr.Zero, null, dependsOn, workerCap: workerCap);

    public static NativeJobHandle ScheduleChunkRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleChunkCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn);

    public static NativeJobHandle ScheduleChunkRangeRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr rangeFuncPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn = null)
        where T : struct
        => ScheduleNativeChunkRangeRawCore(
            ref job, entityManager, query, rangeFuncPtr,
            requiredComponentTypeIds, dependsOn, workerCap: 0, rangeSize: 0);

    public static NativeJobHandle ScheduleChunkRawWithWorkerCap<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleChunkCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap: workerCap);

    public static NativeJobHandle ScheduleChunkRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleChunkCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap: workerCap, rangeSize: rangeSize);

    public static NativeJobHandle ScheduleEntityRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
        where T : struct
        => ScheduleNativeChunkRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize);

    public static NativeJobHandle ScheduleEntityRangeRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
        where T : struct
        => ScheduleNativeChunkRangeRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize);

    /// <summary>
    /// IJobEntity ISPC 轻量调度：跳过 entity tracking + query cache，
    /// 直接迭代 archetype chunk 构建 ChunkJobData 并调度。
    /// </summary>
    public static NativeJobHandle ScheduleEntityBatchRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
        where T : struct
        => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize);

    public static NativeJobHandle ScheduleChunkEntityBatchRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize, jobKind: NativeEcsJobKind.Chunk);

    /// <summary>
    /// Schedule + Complete 一步完成，消除一次 P/Invoke 往返和 handle boxing 开销。
    /// 适用于基准测试和一次性同步 job。
    /// </summary>
    public static NativeJobHandle ScheduleAndCompleteEntityBatchRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap = 0, int rangeSize = 0)
        where T : struct
        => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, null, workerCap, rangeSize, useScheduleAndComplete: true);

    public static void RunChunkRawImmediate<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds)
        where T : struct, IJobChunk
    {
        var handle = ScheduleChunkCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, null, ChunkScheduleMode.ImmediateNative);
        Complete(ref handle);
    }

    public static void RunEntityRawImmediate<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds)
        where T : struct
    {
        var handle = ScheduleNativeChunkRawImmediateCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds);
        Complete(ref handle);
    }

    private static NativeJobHandle ScheduleChunkCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, ChunkScheduleMode? forcedMode = null, int workerCap = 0, int rangeSize = 0)
        where T : struct, IJobChunk
    {
        var allEnabledTypes = query.AllEnabled;
        bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
        bool canUseRawCache = funcPtr != IntPtr.Zero &&
                              !hasEnabledFilter;
        if (canUseRawCache &&
            TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) &&
            rawCache.ChunkCount > 0)
        {
            var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
            var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease);
            try
            {
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                IntPtr h1268 = JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, mode, workerCap, rangeSize);
                RegisterScheduledJobName(h1268, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(h1268));
            }
            catch { ChunkCleanup(rawContextBlock); throw; }
        }

        bool jobHasManagedReferences = JobHasManagedReferences<T>();

        if (funcPtr == IntPtr.Zero &&
            !jobHasManagedReferences &&
            TryGetManagedChunkScheduleCache(entityManager, query, out var csharpRawCache, out var csharpRawCacheLease) &&
            csharpRawCache.ChunkCount > 0)
        {
            var csharpRawContextBlock = CreateChunkContextBlock(ref job, csharpRawCache.ChunksPtr, csharpRawCache.ChunkCount, hasEnabledFilter, allEnabledTypes, -1, false, null, csharpRawCacheLease);
            try
            {
                var cache = GetOrCreateDelegateCache<T, ChunkRangeJobFuncDelegate>(() => CreateChunkRangeCallback<T>());
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                IntPtr h1285 = JobSystem_ScheduleChunkRangeJobEx(cache.FuncPtr, csharpRawContextBlock, _chunkCleanupPtr, csharpRawCache.ChunksPtr, csharpRawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
                RegisterScheduledJobName(h1285, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(h1285));
            }
            catch { ChunkCleanup(csharpRawContextBlock); throw; }
        }

        if (funcPtr == IntPtr.Zero &&
            TryGetManagedChunkArrayCache(entityManager, query, out var managedCache) &&
            managedCache.Chunks.Length > 0)
        {
            var managedContextBlock = jobHasManagedReferences
                ? AllocManagedChunkBatchContext(ref job, managedCache.Chunks, allEnabledTypes)
                : AllocRawChunkBatchContext(ref job, managedCache.Chunks, allEnabledTypes);
            try
            {
                var cache = GetOrCreateDelegateCache<T, BatchJobFunc>(() => CreateChunkArrayBatchCallback<T>());
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                IntPtr h1301 = JobSystem_ScheduleParallelForBatch(cache.FuncPtr, managedContextBlock, jobHasManagedReferences ? _managedCleanupPtr : _rawChunkBatchCleanupPtr, managedCache.Chunks.Length, -1, dependencyLease.Handle);
                RegisterScheduledJobName(h1301, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(h1301));
            }
            catch
            {
                if (jobHasManagedReferences) ManagedCleanup(managedContextBlock);
                else RawChunkBatchCleanup(managedContextBlock);
                throw;
            }
        }

        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var arch = entityManager.Archetypes[i];
            if (arch != null && arch.IsMatch(query))
            {
                foreach (var c in arch.GetChunks())
                    if (c.EntityCount > 0) chunkList.Add(c);
            }
        }

        int chunkCount = chunkList.Count;
        if (chunkCount == 0) return default;

        var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));

        // 原生 adapter 回调只读 ChunkJobData 原始指针，无需恢复托管 Chunk 对象 → 不装箱。
        // 仅托管回调（funcPtr == 0）需要 GCHandle 恢复 chunk 对象。
        bool nativeCallback = funcPtr != IntPtr.Zero;
        var gcHandles = nativeCallback ? null : new GCHandle[chunkCount];
        int gcHandleStartIndex = -1;
        if (!nativeCallback)
        {
            // 先预分配所有 GCHandle（无锁安全），再原子性加入列表
            for (int ci = 0; ci < chunkCount; ci++)
                gcHandles![ci] = GCHandle.Alloc(chunkList[ci], GCHandleType.WeakTrackResurrection);
            lock (_chunkGCHandlesLock)
            {
                gcHandleStartIndex = _chunkGCHandles.Count;
                for (int ci = 0; ci < chunkCount; ci++)
                    _chunkGCHandles.Add(gcHandles[ci]);
            }
        }

        var contextBlock = IntPtr.Zero;
        try
        {
            for (int ci = 0; ci < chunkCount; ci++)
            {
                var chunk = chunkList[ci];
                var arch = chunk.Archetype;

                int compCount = chunk.ComponentCount;
                var compPtrs = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
                var compSizes = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
                var bitmaps = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
                var typeIndices = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
                void** requiredArrays = null;
                int requiredCount = requiredComponentTypeIds?.Length ?? 0;
                if (requiredCount > 0)
                {
                    requiredArrays = (void**)Marshal.AllocHGlobal(requiredCount * sizeof(void*));
                    for (int r = 0; r < requiredCount; r++) requiredArrays[r] = null;
                }

                for (int c = 0; c < compCount; c++)
                {
                    compPtrs[c] = (void*)chunk.GetComponentArrayPointer(c);
                    compSizes[c] = arch.Types[c].Size;
                    bitmaps[c] = chunk.GetEnableBitMapPointer(c);
                    typeIndices[c] = arch.Types[c].Id;
                }

                if (requiredArrays != null)
                {
                    for (int r = 0; r < requiredCount; r++)
                    {
                        int requiredTypeId = requiredComponentTypeIds[r];
                        for (int c = 0; c < compCount; c++)
                        {
                            if (typeIndices[c] == requiredTypeId)
                            {
                                requiredArrays[r] = compPtrs[c];
                                break;
                            }
                        }
                    }
                }

                chunksPtr[ci] = new ChunkJobData
                {
                    entityArray = (void*)chunk.GetEntityPointer(),
                    entityCount = chunk.EntityCount,
                    componentCount = compCount,
                    componentArrays = compPtrs,
                    componentSizes = compSizes,
                    enableBitMaps = bitmaps,
                    componentTypeIndices = typeIndices,
                    chunkHandle = nativeCallback ? IntPtr.Zero : (IntPtr)gcHandles![ci],
                    requiredComponentArrays = requiredArrays,
                    requiredComponentCount = requiredCount
                };
            }

            contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, true, requiredComponentTypeIds);

            IntPtr callbackPtr = funcPtr;
            if (callbackPtr == IntPtr.Zero)
            {
                var cache = GetOrCreateDelegateCache<T, ChunkJobFuncDelegate>(() => CreateChunkCallback<T>());
                callbackPtr = cache.FuncPtr;
            }
            var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            IntPtr h1415 = JobSystem_ScheduleChunkJobEx(callbackPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, mode, workerCap, rangeSize);
            RegisterScheduledJobName(h1415, typeof(T).Name);
            return TrackEntityJob(entityManager, new NativeJobHandle(h1415));
        }
        catch
        {
            if (contextBlock != IntPtr.Zero)
            {
                ChunkCleanup(contextBlock);
            }
            else
            {
                // 分配循环未完成：部分清理 per-chunk 分配和 chunksPtr
                if (chunksPtr != null)
                {
                    for (int ci = 0; ci < chunkCount; ci++)
                    {
                        var cd = chunksPtr[ci];
                        if (cd.componentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.componentArrays);
                        if (cd.componentSizes != null) Marshal.FreeHGlobal((IntPtr)cd.componentSizes);
                        if (cd.enableBitMaps != null) Marshal.FreeHGlobal((IntPtr)cd.enableBitMaps);
                        if (cd.componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)cd.componentTypeIndices);
                        if (cd.requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.requiredComponentArrays);
                    }
                    Marshal.FreeHGlobal((IntPtr)chunksPtr);
                }
                if (gcHandles != null)
                    foreach (var gch in gcHandles)
                        if (gch.IsAllocated) gch.Free();
                // 注：GCHandle 已释放，但对应 slot 仍在 _chunkGCHandles 中。
                // 异常路径罕见，孤立条目可接受；正常路径的尾压实可回收尾部段落。
            }
            throw;
        }
    }

    private static NativeJobHandle ScheduleNativeChunkRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize)
        where T : struct
    {
        if (funcPtr == IntPtr.Zero)
            throw new ArgumentException("Native chunk raw scheduling requires a function pointer.", nameof(funcPtr));

        var allEnabledTypes = query.AllEnabled;
        bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
        if (!hasEnabledFilter &&
            TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) &&
            rawCache.ChunkCount > 0)
        {
            var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease);
            try
            {
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                IntPtr h1465 = JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
                RegisterScheduledJobName(h1465, typeof(T).Name);
                return TrackEntityJob(entityManager, new NativeJobHandle(h1465));
            }
            catch { ChunkCleanup(rawContextBlock); throw; }
        }

        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var arch = entityManager.Archetypes[i];
            if (arch != null && arch.IsMatch(query))
            {
                foreach (var c in arch.GetChunks())
                    if (c.EntityCount > 0) chunkList.Add(c);
            }
        }

        int chunkCount = chunkList.Count;
        if (chunkCount == 0) return default;

        var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
        // 原生 adapter 回调只读 ChunkJobData 原始指针，无需恢复托管 Chunk 对象 → 不装箱（无 GCHandle）。
        const int gcHandleStartIndex = -1;

        for (int ci = 0; ci < chunkCount; ci++)
        {
            var chunk = chunkList[ci];
            var arch = chunk.Archetype;

            int compCount = chunk.ComponentCount;
            var compPtrs = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var compSizes = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
            var bitmaps = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var typeIndices = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
            void** requiredArrays = null;
            int requiredCount = requiredComponentTypeIds?.Length ?? 0;
            if (requiredCount > 0)
            {
                requiredArrays = (void**)Marshal.AllocHGlobal(requiredCount * sizeof(void*));
                for (int r = 0; r < requiredCount; r++) requiredArrays[r] = null;
            }

            for (int c = 0; c < compCount; c++)
            {
                compPtrs[c] = (void*)chunk.GetComponentArrayPointer(c);
                compSizes[c] = arch.Types[c].Size;
                bitmaps[c] = chunk.GetEnableBitMapPointer(c);
                typeIndices[c] = arch.Types[c].Id;
            }

            if (requiredArrays != null)
            {
                for (int r = 0; r < requiredCount; r++)
                {
                    int requiredTypeId = requiredComponentTypeIds[r];
                    for (int c = 0; c < compCount; c++)
                    {
                        if (typeIndices[c] == requiredTypeId)
                        {
                            requiredArrays[r] = compPtrs[c];
                            break;
                        }
                    }
                }
            }

            chunksPtr[ci] = new ChunkJobData
            {
                entityArray = (void*)chunk.GetEntityPointer(),
                entityCount = chunk.EntityCount,
                componentCount = compCount,
                componentArrays = compPtrs,
                componentSizes = compSizes,
                enableBitMaps = bitmaps,
                componentTypeIndices = typeIndices,
                chunkHandle = IntPtr.Zero,
                requiredComponentArrays = requiredArrays,
                requiredComponentCount = requiredCount
            };
        }

        var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, true, requiredComponentTypeIds);
        try
        {
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            IntPtr h1549 = JobSystem_ScheduleChunkJobEx(funcPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
            RegisterScheduledJobName(h1549, typeof(T).Name);
            return TrackEntityJob(entityManager, new NativeJobHandle(h1549));
        }
        catch { ChunkCleanup(contextBlock); throw; }
    }

    private static NativeJobHandle ScheduleNativeChunkRangeRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize)
        where T : struct
    {
        if (funcPtr == IntPtr.Zero)
            throw new ArgumentException("Native chunk range raw scheduling requires a function pointer.", nameof(funcPtr));

        var allEnabledTypes = query.AllEnabled;
        bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
            if (!hasEnabledFilter &&
            TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) &&
            rawCache.ChunkCount > 0)
        {
            var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease);
            try
            {
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleChunkRangeJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize)));
            }
            catch { ChunkCleanup(rawContextBlock); throw; }
        }

        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var arch = entityManager.Archetypes[i];
            if (arch != null && arch.IsMatch(query))
            {
                foreach (var c in arch.GetChunks())
                    if (c.EntityCount > 0) chunkList.Add(c);
            }
        }

        int chunkCount = chunkList.Count;
        if (chunkCount == 0) return default;

        var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
        // 原生 adapter 回调只读 ChunkJobData 原始指针，无需恢复托管 Chunk 对象 → 不装箱（无 GCHandle）。
        const int gcHandleStartIndex = -1;

        for (int ci = 0; ci < chunkCount; ci++)
        {
            var chunk = chunkList[ci];
            var arch = chunk.Archetype;

            int compCount = chunk.ComponentCount;
            var compPtrs = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var compSizes = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
            var bitmaps = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var typeIndices = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
            void** requiredArrays = null;
            int requiredCount = requiredComponentTypeIds?.Length ?? 0;
            if (requiredCount > 0)
            {
                requiredArrays = (void**)Marshal.AllocHGlobal(requiredCount * sizeof(void*));
                for (int r = 0; r < requiredCount; r++) requiredArrays[r] = null;
            }

            for (int c = 0; c < compCount; c++)
            {
                compPtrs[c] = (void*)chunk.GetComponentArrayPointer(c);
                compSizes[c] = arch.Types[c].Size;
                bitmaps[c] = chunk.GetEnableBitMapPointer(c);
                typeIndices[c] = arch.Types[c].Id;
            }

            if (requiredArrays != null)
            {
                for (int r = 0; r < requiredCount; r++)
                {
                    int requiredTypeId = requiredComponentTypeIds[r];
                    for (int c = 0; c < compCount; c++)
                    {
                        if (typeIndices[c] == requiredTypeId)
                        {
                            requiredArrays[r] = compPtrs[c];
                            break;
                        }
                    }
                }
            }

            chunksPtr[ci] = new ChunkJobData
            {
                entityArray = (void*)chunk.GetEntityPointer(),
                entityCount = chunk.EntityCount,
                componentCount = compCount,
                componentArrays = compPtrs,
                componentSizes = compSizes,
                enableBitMaps = bitmaps,
                componentTypeIndices = typeIndices,
                chunkHandle = IntPtr.Zero,
                requiredComponentArrays = requiredArrays,
                requiredComponentCount = requiredCount
            };
        }

        var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, true, requiredComponentTypeIds);
        try
        {
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleChunkRangeJobEx(funcPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize)));
        }
        catch { ChunkCleanup(contextBlock); throw; }
    }

    private static NativeJobHandle ScheduleNativeEntityBatchRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize, bool useScheduleAndComplete = false, NativeEcsJobKind jobKind = NativeEcsJobKind.Entity)
        where T : struct
    {
        if (funcPtr == IntPtr.Zero)
            throw new ArgumentException("Native entity batch raw scheduling requires a function pointer.", nameof(funcPtr));

        var allEnabledTypes = query.AllEnabled;
        bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
        if (hasEnabledFilter)
            throw new NotSupportedException("Native IJobEntity DirectEntityBatch does not support AllEnabled filters yet.");

        if (!TryGetEntityBatchScheduleCache(entityManager, query, requiredComponentTypeIds, out var cache, out var cacheLease) ||
            cache.BatchCount == 0)
            return default;

        var contextBlock = CreateChunkContextBlock(ref job, null, cache.BatchCount, false, null, -1, false, requiredComponentTypeIds, cacheLease);
        try
        {
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            var handle = useScheduleAndComplete
                ? JobSystem_ScheduleAndCompleteEntityBatchJobEx(funcPtr, contextBlock, _chunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, jobKind)
                : JobSystem_ScheduleEntityBatchJobEx(funcPtr, contextBlock, _chunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize, jobKind);
            RegisterScheduledJobName(handle, typeof(T).Name);
            return TrackEntityJob(entityManager, new NativeJobHandle(handle));
        }
        catch { ChunkCleanup(contextBlock); throw; }
    }

    private static NativeJobHandle ScheduleNativeChunkRawImmediateCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds)
        where T : struct
    {
        if (funcPtr == IntPtr.Zero)
            throw new ArgumentException("Native chunk raw immediate requires a function pointer.", nameof(funcPtr));

        if (!TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache, out var rawCacheLease) ||
            rawCache.ChunkCount == 0)
            return default;

        var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, false, requiredComponentTypeIds, rawCacheLease);
        try
        {
            IntPtr h1699 = JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, IntPtr.Zero, ChunkScheduleMode.ImmediateNative);
            RegisterScheduledJobName(h1699, typeof(T).Name);
            return TrackEntityJob(entityManager, new NativeJobHandle(h1699));
        }
        catch { ChunkCleanup(rawContextBlock); throw; }
    }

    private static bool TryGetRawChunkScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds, out RawChunkScheduleCache cache, out IDisposable lease)
    {
        lease = null;
        var key = new RawChunkScheduleCacheKey(entityManager, GetQueryHash(query), GetRequiredComponentHash(requiredComponentTypeIds), 0);
        lock (_rawChunkScheduleCacheLock)
        {
            if (_rawChunkScheduleCaches.TryGetValue(key, out cache))
            {
                if (cache.StructuralVersion == entityManager.StructuralVersion)
                {
                    if (cache.ChunkCount > 0)
                    {
                        lease = cache.RetainLease();
                    }

                    return true;
                }

                cache.Dispose();
                _rawChunkScheduleCaches.Remove(key);
            }

            cache = BuildRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds);
            _rawChunkScheduleCaches[key] = cache;
            if (cache.ChunkCount > 0)
            {
                lease = cache.RetainLease();
            }

            return true;
        }
    }

    private static bool TryGetManagedChunkScheduleCache(EntityManager entityManager, QueryBuilder query, out RawChunkScheduleCache cache, out IDisposable lease)
    {
        lease = null;
        var key = new RawChunkScheduleCacheKey(entityManager, GetQueryHash(query), 0, 1);
        lock (_rawChunkScheduleCacheLock)
        {
            if (_rawChunkScheduleCaches.TryGetValue(key, out cache))
            {
                if (cache.StructuralVersion == entityManager.StructuralVersion)
                {
                    if (cache.ChunkCount > 0)
                    {
                        lease = cache.RetainLease();
                    }

                    return true;
                }

                cache.Dispose();
                _rawChunkScheduleCaches.Remove(key);
            }

            cache = BuildManagedChunkScheduleCache(entityManager, query);
            _rawChunkScheduleCaches[key] = cache;
            if (cache.ChunkCount > 0)
            {
                lease = cache.RetainLease();
            }

            return true;
        }
    }

    private static bool TryGetManagedChunkArrayCache(EntityManager entityManager, QueryBuilder query, out ManagedChunkScheduleCache cache)
    {
        var key = new RawChunkScheduleCacheKey(entityManager, GetQueryHash(query), 0, 2);
        lock (_rawChunkScheduleCacheLock)
        {
            if (_managedChunkScheduleCaches.TryGetValue(key, out cache))
            {
                if (cache.StructuralVersion == entityManager.StructuralVersion)
                {
                    return true;
                }

                _managedChunkScheduleCaches.Remove(key);
            }

            cache = BuildManagedChunkArrayCache(entityManager, query);
            _managedChunkScheduleCaches[key] = cache;
            return true;
        }
    }

    private static bool TryGetEntityBatchScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds, out EntityBatchScheduleCache cache, out IDisposable lease)
    {
        lease = null;
        var key = new RawChunkScheduleCacheKey(entityManager, GetQueryHash(query), GetRequiredComponentHash(requiredComponentTypeIds), 3);
        lock (_rawChunkScheduleCacheLock)
        {
            if (_entityBatchScheduleCaches.TryGetValue(key, out cache))
            {
                if (cache.StructuralVersion == entityManager.StructuralVersion)
                {
                    if (cache.BatchCount > 0)
                    {
                        lease = cache.RetainLease();
                    }

                    return true;
                }

                cache.Dispose();
                _entityBatchScheduleCaches.Remove(key);
            }

            cache = BuildEntityBatchScheduleCache(entityManager, query, requiredComponentTypeIds);
            _entityBatchScheduleCaches[key] = cache;
            if (cache.BatchCount > 0)
            {
                lease = cache.RetainLease();
            }

            return true;
        }
    }

    private static EntityBatchScheduleCache BuildEntityBatchScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds)
    {
        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var archetype = entityManager.Archetypes[i];
            if (archetype != null && archetype.IsMatch(query))
            {
                foreach (var chunk in archetype.GetChunks())
                {
                    if (chunk.EntityCount > 0)
                    {
                        chunkList.Add(chunk);
                    }
                }
            }
        }

        int batchCount = chunkList.Count;
        if (batchCount == 0)
        {
            return new EntityBatchScheduleCache(entityManager.StructuralVersion, null, 0, null, null);
        }

        int requiredCount = requiredComponentTypeIds?.Length ?? 0;
        bool hasEnableFilter = query.AllEnabled != null && query.AllEnabled.Length > 0;
        int enableBitmapCount = hasEnableFilter ? requiredCount : 0;

        // 三次分配替代 per-chunk × N 次分配：
        // 1) EntityBatchData 数组
        var batchesPtr = (EntityBatchData*)Marshal.AllocHGlobal(batchCount * sizeof(EntityBatchData));
        // 2) 所有 componentArrays 指针（连续存储）
        void* componentArraysBlock = null;
        if (requiredCount > 0)
            componentArraysBlock = (void*)Marshal.AllocHGlobal(batchCount * requiredCount * sizeof(void*));
        // 3) 所有 enableBitMaps 指针（连续存储，可选）
        void* enableBitMapsBlock = null;
        if (enableBitmapCount > 0)
            enableBitMapsBlock = (void*)Marshal.AllocHGlobal(batchCount * enableBitmapCount * sizeof(void*));

        for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var chunk = chunkList[batchIndex];
            var archetype = chunk.Archetype;

            // 用偏移量填充连续块，而非每次分配
            if (componentArraysBlock != null)
            {
                void** arraysBase = (void**)componentArraysBlock + batchIndex * requiredCount;
                for (int r = 0; r < requiredCount; r++)
                {
                    arraysBase[r] = null;
                    int requiredTypeId = requiredComponentTypeIds[r];
                    for (int componentIndex = 0; componentIndex < chunk.ComponentCount; componentIndex++)
                    {
                        if (archetype.Types[componentIndex].Id == requiredTypeId)
                        {
                            arraysBase[r] = (void*)chunk.GetComponentArrayPointer(componentIndex);
                            break;
                        }
                    }
                }

                batchesPtr[batchIndex].componentArrays = arraysBase;
            }
            else
            {
                batchesPtr[batchIndex].componentArrays = null;
            }

            if (enableBitMapsBlock != null)
            {
                void** bitmapsBase = (void**)enableBitMapsBlock + batchIndex * enableBitmapCount;
                batchesPtr[batchIndex].enableBitMaps = bitmapsBase;
                batchesPtr[batchIndex].enableBitmapCount = enableBitmapCount;
                for (int e = 0; e < enableBitmapCount; e++)
                {
                    bitmapsBase[e] = null;
                    int requiredTypeId = requiredComponentTypeIds[e];
                    for (int componentIndex = 0; componentIndex < chunk.ComponentCount; componentIndex++)
                    {
                        if (archetype.Types[componentIndex].Id == requiredTypeId)
                        {
                            bitmapsBase[e] = chunk.GetEnableBitMapPointer(componentIndex);
                            break;
                        }
                    }
                }
            }
            else
            {
                batchesPtr[batchIndex].enableBitMaps = null;
                batchesPtr[batchIndex].enableBitmapCount = 0;
            }

            batchesPtr[batchIndex].entityCount = chunk.EntityCount;
        }

        return new EntityBatchScheduleCache(entityManager.StructuralVersion, batchesPtr, batchCount, componentArraysBlock, enableBitMapsBlock);
    }

    private static ManagedChunkScheduleCache BuildManagedChunkArrayCache(EntityManager entityManager, QueryBuilder query)
    {
        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var archetype = entityManager.Archetypes[i];
            if (archetype != null && archetype.IsMatch(query))
            {
                foreach (var chunk in archetype.GetChunks())
                {
                    if (chunk.EntityCount > 0)
                    {
                        chunkList.Add(chunk);
                    }
                }
            }
        }

        return new ManagedChunkScheduleCache(entityManager.StructuralVersion, chunkList.ToArray());
    }

    private static RawChunkScheduleCache BuildRawChunkScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds)
    {
        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var archetype = entityManager.Archetypes[i];
            if (archetype != null && archetype.IsMatch(query))
            {
                foreach (var chunk in archetype.GetChunks())
                {
                    if (chunk.EntityCount > 0)
                    {
                        chunkList.Add(chunk);
                    }
                }
            }
        }

        int chunkCount = chunkList.Count;
        if (chunkCount == 0)
        {
            return new RawChunkScheduleCache(entityManager.StructuralVersion, null, 0);
        }

        var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
        int requiredCount = requiredComponentTypeIds?.Length ?? 0;

        for (int ci = 0; ci < chunkCount; ci++)
        {
            var chunk = chunkList[ci];
            var archetype = chunk.Archetype;
            int componentCount = chunk.ComponentCount;
            var componentArrays = (void**)Marshal.AllocHGlobal(componentCount * sizeof(void*));
            var componentTypeIndices = (int*)Marshal.AllocHGlobal(componentCount * sizeof(int));
            void** requiredArrays = null;

            if (requiredCount > 0)
            {
                requiredArrays = (void**)Marshal.AllocHGlobal(requiredCount * sizeof(void*));
                for (int r = 0; r < requiredCount; r++) requiredArrays[r] = null;
            }

            for (int componentIndex = 0; componentIndex < componentCount; componentIndex++)
            {
                componentArrays[componentIndex] = (void*)chunk.GetComponentArrayPointer(componentIndex);
                componentTypeIndices[componentIndex] = archetype.Types[componentIndex].Id;
            }

            if (requiredArrays != null)
            {
                for (int r = 0; r < requiredCount; r++)
                {
                    int requiredTypeId = requiredComponentTypeIds[r];
                    for (int componentIndex = 0; componentIndex < componentCount; componentIndex++)
                    {
                        if (componentTypeIndices[componentIndex] == requiredTypeId)
                        {
                            requiredArrays[r] = componentArrays[componentIndex];
                            break;
                        }
                    }
                }
            }

            chunksPtr[ci] = new ChunkJobData
            {
                entityArray = (void*)chunk.GetEntityPointer(),
                entityCount = chunk.EntityCount,
                componentCount = componentCount,
                componentArrays = componentArrays,
                componentSizes = null,
                enableBitMaps = null,
                componentTypeIndices = componentTypeIndices,
                chunkHandle = IntPtr.Zero,
                requiredComponentArrays = requiredArrays,
                requiredComponentCount = requiredCount
            };
        }

        return new RawChunkScheduleCache(entityManager.StructuralVersion, chunksPtr, chunkCount);
    }

    private static RawChunkScheduleCache BuildManagedChunkScheduleCache(EntityManager entityManager, QueryBuilder query)
    {
        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var archetype = entityManager.Archetypes[i];
            if (archetype != null && archetype.IsMatch(query))
            {
                foreach (var chunk in archetype.GetChunks())
                {
                    if (chunk.EntityCount > 0)
                    {
                        chunkList.Add(chunk);
                    }
                }
            }
        }

        int chunkCount = chunkList.Count;
        if (chunkCount == 0)
        {
            return new RawChunkScheduleCache(entityManager.StructuralVersion, null, 0, false);
        }

        var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));
        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var chunk = chunkList[chunkIndex];
            var archetype = chunk.Archetype;
            int componentCount = chunk.ComponentCount;
            var componentArrays = (void**)Marshal.AllocHGlobal(componentCount * sizeof(void*));
            var componentSizes = (int*)Marshal.AllocHGlobal(componentCount * sizeof(int));
            var enableBitMaps = (void**)Marshal.AllocHGlobal(componentCount * sizeof(void*));
            var componentTypeIndices = (int*)Marshal.AllocHGlobal(componentCount * sizeof(int));
            var chunkHandle = GCHandle.Alloc(chunk, GCHandleType.Normal);

            for (int componentIndex = 0; componentIndex < componentCount; componentIndex++)
            {
                componentArrays[componentIndex] = (void*)chunk.GetComponentArrayPointer(componentIndex);
                componentSizes[componentIndex] = archetype.Types[componentIndex].Size;
                enableBitMaps[componentIndex] = chunk.GetEnableBitMapPointer(componentIndex);
                componentTypeIndices[componentIndex] = archetype.Types[componentIndex].Id;
            }

            chunksPtr[chunkIndex] = new ChunkJobData
            {
                entityArray = (void*)chunk.GetEntityPointer(),
                entityCount = chunk.EntityCount,
                componentCount = componentCount,
                componentArrays = componentArrays,
                componentSizes = componentSizes,
                enableBitMaps = enableBitMaps,
                componentTypeIndices = componentTypeIndices,
                chunkHandle = GCHandle.ToIntPtr(chunkHandle),
                requiredComponentArrays = null,
                requiredComponentCount = 0
            };
        }

        return new RawChunkScheduleCache(entityManager.StructuralVersion, chunksPtr, chunkCount, true);
    }

    private static int GetQueryHash(QueryBuilder query)
    {
        var hash = new HashCode();
        AddComponentTypesHash(ref hash, query.All);
        AddComponentTypesHash(ref hash, query.Any);
        AddComponentTypesHash(ref hash, query.None);
        AddComponentTypesHash(ref hash, query.AllEnabled);
        hash.Add(query.LimitCount);
        return hash.ToHashCode();
    }

    private static void AddComponentTypesHash(ref HashCode hash, ComponentType[] types)
    {
        if (types == null)
        {
            hash.Add(0);
            return;
        }

        hash.Add(types.Length);
        for (int i = 0; i < types.Length; i++)
        {
            hash.Add(types[i].Id);
        }
    }

    private static int GetRequiredComponentHash(int[] requiredComponentTypeIds)
    {
        var hash = new HashCode();
        if (requiredComponentTypeIds == null)
        {
            hash.Add(0);
            return hash.ToHashCode();
        }

        hash.Add(requiredComponentTypeIds.Length);
        for (int i = 0; i < requiredComponentTypeIds.Length; i++)
        {
            hash.Add(requiredComponentTypeIds[i]);
        }

        return hash.ToHashCode();
    }

    internal readonly struct RawChunkScheduleCacheKey : IEquatable<RawChunkScheduleCacheKey>
    {
        private readonly EntityManager _entityManager;
        private readonly int _managerHash;
        private readonly int _queryHash;
        private readonly int _requiredHash;
        private readonly int _mode;

        public RawChunkScheduleCacheKey(EntityManager entityManager, int queryHash, int requiredHash, int mode)
        {
            _entityManager = entityManager;
            _managerHash = RuntimeHelpers.GetHashCode(entityManager);
            _queryHash = queryHash;
            _requiredHash = requiredHash;
            _mode = mode;
        }

        public bool Equals(RawChunkScheduleCacheKey other)
            => ReferenceEquals(_entityManager, other._entityManager) &&
               _queryHash == other._queryHash &&
               _requiredHash == other._requiredHash &&
               _mode == other._mode;

        public bool Matches(EntityManager entityManager)
            => ReferenceEquals(_entityManager, entityManager);

        public override bool Equals(object obj)
            => obj is RawChunkScheduleCacheKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(_managerHash, _queryHash, _requiredHash, _mode);
    }

    internal sealed class RawChunkScheduleCache : IDisposable
    {
        public readonly int StructuralVersion;
        public readonly ChunkJobData* ChunksPtr;
        public readonly int ChunkCount;
        public readonly bool OwnsChunkHandles;
        private int _leaseCount;
        private int _retired;
        private int _disposed;

        public RawChunkScheduleCache(int structuralVersion, ChunkJobData* chunksPtr, int chunkCount, bool ownsChunkHandles = false)
        {
            StructuralVersion = structuralVersion;
            ChunksPtr = chunksPtr;
            ChunkCount = chunkCount;
            OwnsChunkHandles = ownsChunkHandles;
        }

        ~RawChunkScheduleCache()
        {
            Dispose();
        }

        public IDisposable RetainLease()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(RawChunkScheduleCache));
            }

            Interlocked.Increment(ref _leaseCount);
            if (Volatile.Read(ref _disposed) != 0)
            {
                ReleaseLease();
                throw new ObjectDisposedException(nameof(RawChunkScheduleCache));
            }

            return new CacheLease(this);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _retired, 1);
            GC.SuppressFinalize(this);
            TryDisposeNow();
        }

        private void ReleaseLease()
        {
            if (Interlocked.Decrement(ref _leaseCount) == 0)
            {
                TryDisposeNow();
            }
        }

        private void TryDisposeNow()
        {
            if (ChunksPtr == null)
            {
                Interlocked.Exchange(ref _disposed, 1);
                return;
            }

            if (Volatile.Read(ref _retired) == 0 ||
                Volatile.Read(ref _leaseCount) != 0 ||
                Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            for (int i = 0; i < ChunkCount; i++)
            {
                var chunkData = ChunksPtr[i];
                if (chunkData.componentArrays != null) Marshal.FreeHGlobal((IntPtr)chunkData.componentArrays);
                if (chunkData.componentSizes != null) Marshal.FreeHGlobal((IntPtr)chunkData.componentSizes);
                if (chunkData.enableBitMaps != null) Marshal.FreeHGlobal((IntPtr)chunkData.enableBitMaps);
                if (chunkData.componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)chunkData.componentTypeIndices);
                if (chunkData.requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)chunkData.requiredComponentArrays);
                if (OwnsChunkHandles && chunkData.chunkHandle != IntPtr.Zero)
                {
                    var handle = GCHandle.FromIntPtr(chunkData.chunkHandle);
                    if (handle.IsAllocated) handle.Free();
                }
            }

            Marshal.FreeHGlobal((IntPtr)ChunksPtr);
        }

        internal sealed class CacheLease : IDisposable
        {
            private RawChunkScheduleCache _owner;

            public CacheLease(RawChunkScheduleCache owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.ReleaseLease();
            }
        }
    }

    internal sealed class ManagedChunkScheduleCache
    {
        public readonly int StructuralVersion;
        public readonly Chunk[] Chunks;

        public ManagedChunkScheduleCache(int structuralVersion, Chunk[] chunks)
        {
            StructuralVersion = structuralVersion;
            Chunks = chunks;
        }
    }

    internal sealed class EntityBatchScheduleCache : IDisposable
    {
        public readonly int StructuralVersion;
        public readonly EntityBatchData* BatchesPtr;
        public readonly int BatchCount;
        private void* _componentArraysBlock;  // 批量分配的 componentArrays（可为 null）
        private void* _enableBitMapsBlock;    // 批量分配的 enableBitMaps（可为 null）
        private int _leaseCount;
        private int _retired;
        private int _disposed;

        public EntityBatchScheduleCache(int structuralVersion, EntityBatchData* batchesPtr, int batchCount, void* componentArraysBlock, void* enableBitMapsBlock)
        {
            StructuralVersion = structuralVersion;
            BatchesPtr = batchesPtr;
            BatchCount = batchCount;
            _componentArraysBlock = componentArraysBlock;
            _enableBitMapsBlock = enableBitMapsBlock;
        }

        ~EntityBatchScheduleCache()
        {
            Dispose();
        }

        public IDisposable RetainLease()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(EntityBatchScheduleCache));
            }

            Interlocked.Increment(ref _leaseCount);
            if (Volatile.Read(ref _disposed) != 0)
            {
                ReleaseLease();
                throw new ObjectDisposedException(nameof(EntityBatchScheduleCache));
            }

            return new CacheLease(this);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _retired, 1);
            GC.SuppressFinalize(this);
            TryDisposeNow();
        }

        private void ReleaseLease()
        {
            if (Interlocked.Decrement(ref _leaseCount) == 0)
            {
                TryDisposeNow();
            }
        }

        private void TryDisposeNow()
        {
            if (BatchesPtr == null)
            {
                Interlocked.Exchange(ref _disposed, 1);
                return;
            }

            if (Volatile.Read(ref _retired) == 0 ||
                Volatile.Read(ref _leaseCount) != 0 ||
                Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // 释放批量分配的块（仅 2-3 次 Free，而非 per-chunk）
            if (_componentArraysBlock != null)
                Marshal.FreeHGlobal((IntPtr)_componentArraysBlock);
            if (_enableBitMapsBlock != null)
                Marshal.FreeHGlobal((IntPtr)_enableBitMapsBlock);
            Marshal.FreeHGlobal((IntPtr)BatchesPtr);
        }

        internal sealed class CacheLease : IDisposable
        {
            private EntityBatchScheduleCache _owner;

            public CacheLease(EntityBatchScheduleCache owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.ReleaseLease();
            }
        }
    }

    // ======================== 内部实现 ========================
    internal static readonly CleanupFunc _chunkCleanup = ChunkCleanup;
    internal static readonly IntPtr _chunkCleanupPtr = Marshal.GetFunctionPointerForDelegate(_chunkCleanup);

    // 显式逐字段写入器注册表：Debug 下 NativeArray 含 GC 引用（DisposeSentinel）→ Job struct 非 blittable，
    // 裸拷贝布局不可靠；NativeTranspiler 为每个 transpiled Job 生成 WriteJobFields_{Job}，静态构造时登记，
    // CreateChunkContextBlock 按类型分发，未登记（非 transpiled / 含不支持字段）回退裸拷贝。
    public unsafe delegate void JobFieldWriter<T>(byte* dst, ref T job) where T : struct;
    internal static readonly Dictionary<Type, Delegate> s_jobFieldWriters = new();

    /// <summary>注册 Job 字段显式写入器（由 NativeTranspiler 生成代码在 NativeExports 静态构造时调用）</summary>
    public static void RegisterJobFieldWriter(Type type, Delegate writer) => s_jobFieldWriters[type] = writer;

    internal static bool TryGetJobFieldWriter(Type type, out Delegate writer) => s_jobFieldWriters.TryGetValue(type, out writer);

    private unsafe static IntPtr CreateChunkContextBlock<T>(ref T job, ChunkJobData* chunksPtr, int chunkCount, bool hasEnabledFilter, ComponentType[] allEnabledTypes, int gcHandleStartIndex, bool ownsChunkData, int[] requiredComponentTypeIds = null, IDisposable cacheLease = null) where T : struct
    {
        int jobSize = Unsafe.SizeOf<T>();
        int headerSize = Unsafe.SizeOf<ChunkContextHeader>();
        int typesDataSize = 0;
        int[] typeHashes = null;
        if (hasEnabledFilter && allEnabledTypes != null)
        {
            typeHashes = new int[allEnabledTypes.Length];
            for (int i = 0; i < allEnabledTypes.Length; i++) typeHashes[i] = allEnabledTypes[i].GetHashCode();
            typesDataSize = allEnabledTypes.Length * sizeof(int);
        }
        int requiredTypesDataSize = requiredComponentTypeIds != null ? requiredComponentTypeIds.Length * sizeof(int) : 0;
        int totalSize = headerSize + typesDataSize + requiredTypesDataSize + jobSize;
        int pooledSize = IntPtr.Size + totalSize;
        var pooledBlock = ContextPool.Rent(pooledSize);
        var block = pooledBlock + IntPtr.Size;
        *(int*)pooledBlock = pooledSize;
        Unsafe.InitBlockUnaligned((void*)block, 0, (uint)totalSize);
        var header = (ChunkContextHeader*)block;
        header->chunkCount = chunkCount;
        header->hasEnabledFilter = hasEnabledFilter ? 1 : 0;
        header->gcHandleStartIndex = gcHandleStartIndex;
        header->ownsChunkData = ownsChunkData ? 1 : 0;
        header->chunksPtr = (IntPtr)chunksPtr;
        header->cleanupInProgress = 0;
        if (hasEnabledFilter && typeHashes != null)
        {
            var typeHashPtr = (int*)((byte*)block + headerSize);
            for (int i = 0; i < typeHashes.Length; i++) typeHashPtr[i] = typeHashes[i];
            header->allEnabledCount = typeHashes.Length;
            header->queryAllEnabledTypes = (IntPtr)typeHashPtr;
        }
        else { header->allEnabledCount = 0; header->queryAllEnabledTypes = IntPtr.Zero; }
        if (requiredComponentTypeIds != null && requiredComponentTypeIds.Length > 0)
        {
            var requiredTypePtr = (int*)((byte*)block + headerSize + typesDataSize);
            for (int i = 0; i < requiredComponentTypeIds.Length; i++) requiredTypePtr[i] = requiredComponentTypeIds[i];
            header->requiredComponentTypeIdCount = requiredComponentTypeIds.Length;
            header->requiredComponentTypeIds = (IntPtr)requiredTypePtr;
        }
        else { header->requiredComponentTypeIdCount = 0; header->requiredComponentTypeIds = IntPtr.Zero; }
        byte* jobPtr = (byte*)block + headerSize + typesDataSize + requiredTypesDataSize;
        // 优先走显式逐字段写入器（非 blittable Job struct 裸拷贝布局不可靠）；未登记回退裸拷贝。
        if (TryGetJobFieldWriter(typeof(T), out var __fieldWriter))
        {
            ((JobFieldWriter<T>)__fieldWriter)(jobPtr, ref job);
        }
        else
        {
            Unsafe.CopyBlockUnaligned(jobPtr, Unsafe.AsPointer(ref job), (uint)jobSize);
        }
        if (cacheLease != null)
        {
            _chunkContextLeases[block] = GCHandle.Alloc(cacheLease, GCHandleType.Normal);
        }

        return block;
    }

    private unsafe static void ChunkCleanup(IntPtr contextBlock)
    {
        if (contextBlock == IntPtr.Zero) return;
        var header = (ChunkContextHeader*)contextBlock;
        if (Interlocked.CompareExchange(ref header->cleanupInProgress, 1, 0) != 0) return;
        int chunkCount = header->chunkCount;
        int gcHandleStartIndex = header->gcHandleStartIndex;
        var chunksPtr = (ChunkJobData*)header->chunksPtr;
        bool ownsChunkData = header->ownsChunkData != 0;

        try
        {
            if (chunksPtr != null && gcHandleStartIndex >= 0)
            {
                lock (_chunkGCHandlesLock)
                {
                    for (int i = 0; i < chunkCount && (gcHandleStartIndex + i) < _chunkGCHandles.Count; i++)
                    {
                        int index = gcHandleStartIndex + i;
                        if (_chunkGCHandles[index].IsAllocated) { _chunkGCHandles[index].Free(); _chunkGCHandles[index] = default; }
                    }
                    // 清理尾部连续的 default 条目，防止 _chunkGCHandles 无界增长
                    while (_chunkGCHandles.Count > 0 && !_chunkGCHandles[_chunkGCHandles.Count - 1].IsAllocated)
                        _chunkGCHandles.RemoveAt(_chunkGCHandles.Count - 1);
                    // #24：空洞积累（中间 default 条目被活跃 job 的 gcHandleStartIndex 引用，
                    // 不能移动元素；但底层数组可能远大于 Count）。TrimExcess 收缩容量但不移动
                    // 元素，释放空洞占用的数组空间，不影响索引寻址。仅在大容量时触发避免频繁拷贝。
                    if (_chunkGCHandles.Capacity > 8192 && _chunkGCHandles.Capacity > _chunkGCHandles.Count * 4)
                        _chunkGCHandles.TrimExcess();
                }
            }

            if (ownsChunkData)
            {
                for (int i = 0; i < chunkCount; i++)
                {
                    if (chunksPtr != null)
                    {
                        var cd = chunksPtr[i];
                        if (cd.componentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.componentArrays);
                        if (cd.componentSizes != null) Marshal.FreeHGlobal((IntPtr)cd.componentSizes);
                        if (cd.enableBitMaps != null) Marshal.FreeHGlobal((IntPtr)cd.enableBitMaps);
                        if (cd.componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)cd.componentTypeIndices);
                        if (cd.requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.requiredComponentArrays);
                    }
                }
            }

            if (chunksPtr != null && ownsChunkData) Marshal.FreeHGlobal((IntPtr)chunksPtr);
        }
        finally
        {
            // 兜底（#7/#16）：即使上面释放过程中抛异常（AccessViolation/StackOverflow 等），
            // 也必须复位 cleanupInProgress、释放 _chunkContextLeases 的 IDisposable 并归还池块，
            // 否则 flag 永久置位 + lease 泄漏（chunk 数据 MB 级永不回收）。
            if (_chunkContextLeases.TryRemove(contextBlock, out var leaseHandle))
            {
                try
                {
                    if (leaseHandle.Target is IDisposable lease)
                        lease.Dispose();
                }
                catch { }
                try { leaseHandle.Free(); } catch { }
            }

            try
            {
                var pooledBlock = contextBlock - IntPtr.Size;
                int pooledSize = *(int*)pooledBlock;
                ContextPool.Return(pooledBlock, pooledSize);
            }
            catch { }

            Interlocked.Exchange(ref header->cleanupInProgress, 0);
        }
    }

    // ======================== 回调工厂 ========================
    private unsafe static JobFunc CreateJobCallback<T>() where T : struct, IJob
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        bool managedContext = JobHasManagedReferences<T>();
        return (IntPtr ctx) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(name);
            try
            {
                long start = 0;
                if (JobProfiler.Enabled) start = Stopwatch.GetTimestamp();
                ref var job = ref GetJob<T>(ctx, managedContext);
                job.Execute();
                if (JobProfiler.Enabled) { int threadId = Environment.CurrentManagedThreadId; long end = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, start, end, threadId, 0); }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    private unsafe static IndexJobFunc CreateForCallback<T>() where T : struct, IJobFor
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        bool managedContext = JobHasManagedReferences<T>();
        return (IntPtr ctx, int i) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(name);
            try
            {
                long start = 0;
                if (JobProfiler.Enabled) start = Stopwatch.GetTimestamp();
                ref var job = ref GetJob<T>(ctx, managedContext);
                job.Execute(i);
                if (JobProfiler.Enabled) { int threadId = Environment.CurrentManagedThreadId; long end = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, start, end, threadId, 1); }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    private unsafe static BatchJobFunc CreateParallelForIndexCallback<T>() where T : struct, IJobParallelFor
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        bool managedContext = JobHasManagedReferences<T>();
        return (IntPtr ctx, int start, int count) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(name);
            try
            {
                long startTicks = 0;
                if (JobProfiler.Enabled) startTicks = Stopwatch.GetTimestamp();
                ref var job = ref GetJob<T>(ctx, managedContext);
                int end = start + count;
                for (int i = start; i < end; i++) job.Execute(i);
                if (JobProfiler.Enabled) { int threadId = Environment.CurrentManagedThreadId; long endTicks = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, startTicks, endTicks, threadId, 2); }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    private unsafe static BatchJobFunc CreateParallelForBatchCallback<T>() where T : struct, IJobParallelForBatch
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        bool managedContext = JobHasManagedReferences<T>();
        return (IntPtr ctx, int start, int count) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(name);
            try
            {
                long startTicks = 0;
                if (JobProfiler.Enabled) startTicks = Stopwatch.GetTimestamp();
                ref var job = ref GetJob<T>(ctx, managedContext);
                job.Execute(start, count);
                if (JobProfiler.Enabled) { int threadId = Environment.CurrentManagedThreadId; long endTicks = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, startTicks, endTicks, threadId, 3); }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    private unsafe static ChunkJobFuncDelegate CreateChunkCallback<T>() where T : struct, IJobChunk
    {
        return (IntPtr ctx, ChunkJobData* cd) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(typeof(T).Name);
            try
            {
                var header = (ChunkContextHeader*)ctx;
                int headerSize = Unsafe.SizeOf<ChunkContextHeader>();
                int typesDataSize = header->allEnabledCount * sizeof(int);
                int requiredTypesDataSize = header->requiredComponentTypeIdCount * sizeof(int);
                byte* jobPtr = (byte*)ctx + headerSize + typesDataSize + requiredTypesDataSize;
                ref var job = ref Unsafe.AsRef<T>(jobPtr);

                var chunkHandle = cd->chunkHandle;
                Chunk chunk = null;
                if (chunkHandle != IntPtr.Zero)
                {
                    try
                    {
                        var gch = GCHandle.FromIntPtr(chunkHandle);
                        if (gch.IsAllocated && gch.Target is Chunk c) chunk = c;
                    }
                    catch { }
                }
                if (chunk == null) return;

                if (header->hasEnabledFilter != 0 && header->allEnabledCount > 0)
                {
                    int* typeHashArray = (int*)header->queryAllEnabledTypes;
                    int ulongCount = (cd->entityCount + 63) / 64;
                    ulong* combinedMask = TempBuffer.GetBuffer(ulongCount);

                    bool firstFound = false;
                    for (int j = 0; j < header->allEnabledCount; j++)
                    {
                        int typeHash = typeHashArray[j];
                        var arch = chunk.Archetype;
                        for (int k = 0; k < cd->componentCount; k++)
                        {
                            if (arch.Types[k].GetHashCode() == typeHash)
                            {
                                ulong* bitmap = (ulong*)cd->enableBitMaps[k];
                                if (bitmap != null)
                                {
                                    if (!firstFound) { Buffer.MemoryCopy(bitmap, combinedMask, ulongCount * 8, ulongCount * 8); firstFound = true; }
                                    else { for (int b = 0; b < ulongCount; b++) combinedMask[b] &= bitmap[b]; }
                                }
                                break;
                            }
                        }
                    }

                    if (firstFound) job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(combinedMask, cd->entityCount));
                    else job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
                }
                else job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    [SkipLocalsInit]
    private unsafe static ChunkRangeJobFuncDelegate CreateChunkRangeCallback<T>() where T : struct, IJobChunk
    {
        return (IntPtr ctx, ChunkJobData* chunks, int startIndex, int count) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(typeof(T).Name);
            try
            {
                var header = (ChunkContextHeader*)ctx;
                int headerSize = Unsafe.SizeOf<ChunkContextHeader>();
                int typesDataSize = header->allEnabledCount * sizeof(int);
                int requiredTypesDataSize = header->requiredComponentTypeIdCount * sizeof(int);
                byte* jobPtr = (byte*)ctx + headerSize + typesDataSize + requiredTypesDataSize;
                ref var job = ref Unsafe.AsRef<T>(jobPtr);

                int end = startIndex + count;
                // 快速路径：无 enabled filter，减少调用链
                if (header->hasEnabledFilter == 0 || header->allEnabledCount == 0)
                {
                    for (int index = startIndex; index < end; index++)
                    {
                        var cd = chunks + index;
                        var chunk = ResolveChunk(cd->chunkHandle);
                        if (chunk != null)
                            job.Execute(new ArchetypeChunk(chunk), default);
                    }
                }
                else
                {
                    for (int index = startIndex; index < end; index++)
                    {
                        ExecuteRawChunk(ref job, header, chunks + index);
                    }
                }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Chunk ResolveChunk(IntPtr chunkHandle)
    {
        if (chunkHandle == IntPtr.Zero) return null;
        try
        {
            var gch = GCHandle.FromIntPtr(chunkHandle);
            if (gch.IsAllocated && gch.Target is Chunk c) return c;
        }
        catch { }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static void ExecuteRawChunk<T>(ref T job, ChunkContextHeader* header, ChunkJobData* cd)
        where T : struct, IJobChunk
    {
        var chunkHandle = cd->chunkHandle;
        Chunk chunk = null;
        if (chunkHandle != IntPtr.Zero)
        {
            try
            {
                var gch = GCHandle.FromIntPtr(chunkHandle);
                if (gch.IsAllocated && gch.Target is Chunk c) chunk = c;
            }
            catch { }
        }
        if (chunk == null) return;

        if (header->hasEnabledFilter != 0 && header->allEnabledCount > 0)
        {
            int* typeHashArray = (int*)header->queryAllEnabledTypes;
            int ulongCount = (cd->entityCount + 63) / 64;
            ulong* combinedMask = TempBuffer.GetBuffer(ulongCount);

            bool firstFound = false;
            for (int j = 0; j < header->allEnabledCount; j++)
            {
                int typeHash = typeHashArray[j];
                var arch = chunk.Archetype;
                for (int k = 0; k < cd->componentCount; k++)
                {
                    if (arch.Types[k].GetHashCode() != typeHash) continue;
                    ulong* bitmap = (ulong*)cd->enableBitMaps[k];
                    if (bitmap != null)
                    {
                        if (!firstFound)
                        {
                            Buffer.MemoryCopy(bitmap, combinedMask, ulongCount * 8, ulongCount * 8);
                            firstFound = true;
                        }
                        else
                        {
                            for (int b = 0; b < ulongCount; b++) combinedMask[b] &= bitmap[b];
                        }
                    }
                    break;
                }
            }

            if (firstFound) job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(combinedMask, cd->entityCount));
            else job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
        }
        else
        {
            job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
        }
    }

    private unsafe static BatchJobFunc CreateChunkArrayBatchCallback<T>() where T : struct, IJobChunk
    {
        bool managedContext = JobHasManagedReferences<T>();
        return (IntPtr ctx, int start, int count) =>
        {
            EnterJobExecution();
            RegisterCurrentBatchJobName(typeof(T).Name);
            try
            {
                ref var job = ref GetChunkBatchJob<T>(ctx, managedContext, out var chunks, out var allEnabledTypes);
                int end = start + count;
                for (int index = start; index < end; index++)
                {
                    ExecuteManagedChunk(ref job, chunks[index], allEnabledTypes);
                }
            }
            catch (Exception exception)
            {
                RecordJobException(_currentBatchId, exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static void ExecuteManagedChunk<T>(ref T job, Chunk chunk, ComponentType[] allEnabledTypes) where T : struct, IJobChunk
    {
        if (chunk == null) return;
        if (allEnabledTypes != null && allEnabledTypes.Length > 0)
        {
            int ulongCount = (chunk.EntityCount + 63) / 64;
            ulong* combinedMask = TempBuffer.GetBuffer(ulongCount);

            bool firstFound = false;
            var archetype = chunk.Archetype;
            for (int i = 0; i < allEnabledTypes.Length; i++)
            {
                int componentIndex = archetype.GetComponentTypeIndex(allEnabledTypes[i]);
                if (componentIndex < 0) continue;
                ulong* bitmap = chunk.GetEnableBitMapPointer(componentIndex);
                if (bitmap == null) continue;
                if (!firstFound)
                {
                    Buffer.MemoryCopy(bitmap, combinedMask, ulongCount * 8, ulongCount * 8);
                    firstFound = true;
                }
                else
                {
                    for (int b = 0; b < ulongCount; b++) combinedMask[b] &= bitmap[b];
                }
            }

            if (firstFound) job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(combinedMask, chunk.EntityCount));
            else job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
        }
        else
        {
            job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
        }
    }

    // ======================== 上下文内存池 ========================
    private static class ContextPool
    {
        private const int BucketShift = 6;
        private const int MaxBucket = 64;
        private static readonly ConcurrentStack<IntPtr>[] _buckets = new ConcurrentStack<IntPtr>[MaxBucket];

        private static int GetBucketIndex(int size)
        {
            int idx = (size + (1 << BucketShift) - 1) >> BucketShift;
            return idx >= MaxBucket ? -1 : idx;
        }

        /// <summary>返回桶 idx 对应的分配大小。</summary>
        /// <remarks>
        /// 桶 idx 覆盖 size ∈ (64*idx, 64*(idx+1)]，分配桶上界 64*(idx+1) 即可。
        /// 修复两个历史缺陷：
        ///  1) 旧实现 1L&lt;&lt;(6+idx) 幂次分配：idx=63 时 1L&lt;&lt;69 在 C# long 移位只取低 6 位
        ///     回绕为 1L&lt;&lt;5=32 字节 → 请求 3969-4032 字节却分配 32 → 堆缓冲区溢出；
        ///  2) idx≥26 时 2^(6+idx) ≥ 2^32 → clamp 到 int.MaxValue(~2GB) → 必然 OOM。
        /// 线性上界同时消除溢出、爆炸、与线性分桶不匹配的浪费。
        /// </remarks>
        private static int GetBucketAllocSize(int idx)
        {
            // idx 最大 63 → (63+1)&lt;&lt;6 = 4096，int 无溢出
            return (idx + 1) << BucketShift;
        }

        public static IntPtr Rent(int size)
        {
            int idx = GetBucketIndex(size);
            if (idx < 0) return Marshal.AllocHGlobal(size);
            var bucket = _buckets[idx];
            if (bucket != null && bucket.TryPop(out var ptr)) return ptr;
            return Marshal.AllocHGlobal(GetBucketAllocSize(idx));
        }

        public static void Return(IntPtr ptr, int size)
        {
            if (ptr == IntPtr.Zero) return;
            int idx = GetBucketIndex(size);
            if (idx < 0) { Marshal.FreeHGlobal(ptr); return; }
            var bucket = Volatile.Read(ref _buckets[idx]);
            if (bucket == null)
            {
                bucket = new ConcurrentStack<IntPtr>();
                bucket = Interlocked.CompareExchange(ref _buckets[idx], bucket, null) ?? bucket;
            }
            const int MaxPerBucket = 256;
            if (bucket.Count < MaxPerBucket) bucket.Push(ptr);
            else Marshal.FreeHGlobal(ptr);
        }
    }

    // ======================== 辅助方法 ========================
    internal static DelegateCache GetOrCreateDelegateCache<T, TDelegate>(Func<TDelegate> factory) where TDelegate : Delegate
    {
        // 必须用 GetOrAdd：手写 TryGetValue-创建-赋值在并发首次调度同一 T 时，
        // loser 实例会被覆盖，其委托可能被 GC 回收但函数指针已交原生侧 → 悬空。
        return _delegateCache.GetOrAdd(typeof(T), _ => new DelegateCache(factory()));
    }

    /// <summary>
    /// 自动批处理回调（per 泛型 T 缓存一次）：若 T 同时实现 IJobParallelForBatch，则调度用批回调
    /// （回调内一次 Execute(start,count)），否则退回逐元素 Execute(i)。减少轻任务上逐元素接口调度开销。
    /// 用反射仅在首次调度该类型时做一次 IsAssignableFrom 判定 + 泛型构造，热路径零开销。
    /// </summary>
    private static class AutoParallelForCallback<T>
        where T : struct, IJobParallelFor
    {
        public static readonly DelegateCache Cache = Build();

        private static DelegateCache Build()
        {
            if (typeof(IJobParallelForBatch).IsAssignableFrom(typeof(T)))
            {
                // T 同时是 IJobParallelForBatch → 批回调
                var create = typeof(NativeJobScheduler)
                    .GetMethod(nameof(CreateParallelForBatchCallback), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    .MakeGenericMethod(typeof(T))
                    .Invoke(null, null);
                return new DelegateCache((BatchJobFunc)create!);
            }
            // 退回逐元素回调
            return GetOrCreateDelegateCache<T, BatchJobFunc>(() => CreateParallelForIndexCallback<T>());
        }

        public static DelegateCache GetCache() => Cache;
    }

    // 按 batchId 归集的 Job 异常。Complete(h) 只抛本 batch 的异常；
    // batch 0 为未归属异常（实际不发生，防御兜底），由 Flush 统一抛。
    private static readonly object _exceptionLock = new();
    private static Dictionary<ulong, List<ExceptionDispatchInfo>> _recordedJobExceptions = new();
    // 每 batch 上限而非全局：一个坏 batch 不应饿死其它 batch 的异常上报。
    private const int MaxRecordedJobExceptionsPerBatch = 16;
    // 因每 batch 容量被丢弃的异常数（上报而非静默丢失）。
    private static int _droppedJobExceptionCount;

    private static void RecordJobException(ulong batchId, Exception exception)
    {
        lock (_exceptionLock)
        {
            if (!_recordedJobExceptions.TryGetValue(batchId, out var list))
            {
                list = new List<ExceptionDispatchInfo>();
                _recordedJobExceptions[batchId] = list;
            }
            if (list.Count >= MaxRecordedJobExceptionsPerBatch)
            {
                _droppedJobExceptionCount++;
                return;
            }
            list.Add(ExceptionDispatchInfo.Capture(exception));
        }
    }

    private static void ThrowAll(List<ExceptionDispatchInfo> captured)
    {
        if (captured.Count == 0) return;
        if (captured.Count == 1)
        {
            ExceptionDispatchInfo.Capture(captured[0].SourceException).Throw();
        }

        var exceptions = new List<Exception>(captured.Count);
        foreach (var ei in captured)
            exceptions.Add(ei.SourceException);
        throw new AggregateException("One or more scheduled C# jobs failed.", exceptions);
    }

    /// <summary>
    /// 抛出所有已记录的 Job 异常（跨所有 batch，含未归属的 batch 0）。
    /// 公有接口，可在帧末通过 TempAllocator.Reset() 或自定义检查点调用。
    /// </summary>
    public static void FlushRecordedExceptions()
    {
        List<ExceptionDispatchInfo> all = new();
        int dropped;
        lock (_exceptionLock)
        {
            foreach (var list in _recordedJobExceptions.Values)
                all.AddRange(list);
            _recordedJobExceptions.Clear();
            dropped = _droppedJobExceptionCount;
            _droppedJobExceptionCount = 0;
        }
        if (dropped > 0)
            Console.Error.WriteLine($"[JobSystem] {dropped} job exceptions dropped (per-batch cap {MaxRecordedJobExceptionsPerBatch}).");
        ThrowAll(all);
    }

    private static void ThrowRecordedJobExceptions(ulong batchId)
    {
        List<ExceptionDispatchInfo> captured;
        int dropped;
        lock (_exceptionLock)
        {
            if (!_recordedJobExceptions.TryGetValue(batchId, out captured))
                return;
            _recordedJobExceptions.Remove(batchId);
            dropped = _droppedJobExceptionCount;
            _droppedJobExceptionCount = 0;
        }
        if (dropped > 0)
            Console.Error.WriteLine($"[JobSystem] {dropped} job exceptions dropped (per-batch cap {MaxRecordedJobExceptionsPerBatch}).");
        ThrowAll(captured);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool JobHasManagedReferences<T>() where T : struct
        => RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    private sealed class ManagedJobBox<T> where T : struct
    {
        public T Job;

        public ManagedJobBox(T job)
        {
            Job = job;
        }
    }

    private sealed class RawChunkBatchContext
    {
        public IntPtr JobPtr;
        public Chunk[] Chunks;
        public ComponentType[] AllEnabledTypes;

        public RawChunkBatchContext(IntPtr jobPtr, Chunk[] chunks, ComponentType[] allEnabledTypes)
        {
            JobPtr = jobPtr;
            Chunks = chunks;
            AllEnabledTypes = allEnabledTypes;
        }
    }

    private sealed class ManagedChunkBatchContext<T> where T : struct, IJobChunk
    {
        public T Job;
        public readonly Chunk[] Chunks;
        public readonly ComponentType[] AllEnabledTypes;

        public ManagedChunkBatchContext(T job, Chunk[] chunks, ComponentType[] allEnabledTypes)
        {
            Job = job;
            Chunks = chunks;
            AllEnabledTypes = allEnabledTypes;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static ref T GetJob<T>(IntPtr ctx, bool managedContext) where T : struct
    {
        if (managedContext)
        {
            return ref GetManagedJob<T>(ctx);
        }

        return ref Unsafe.AsRef<T>((void*)ctx);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static ref T GetChunkBatchJob<T>(IntPtr ctx, bool managedContext, out Chunk[] chunks, out ComponentType[] allEnabledTypes)
        where T : struct, IJobChunk
    {
        var handle = GCHandle.FromIntPtr(ctx);
        if (managedContext)
        {
            var context = (ManagedChunkBatchContext<T>)handle.Target;
            chunks = context.Chunks;
            allEnabledTypes = context.AllEnabledTypes;
            return ref context.Job;
        }
        else
        {
            var context = (RawChunkBatchContext)handle.Target;
            chunks = context.Chunks;
            allEnabledTypes = context.AllEnabledTypes;
            return ref Unsafe.AsRef<T>((void*)context.JobPtr);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref T GetManagedJob<T>(IntPtr ctx) where T : struct
    {
        var handle = GCHandle.FromIntPtr(ctx);
        var box = (ManagedJobBox<T>)handle.Target;
        return ref box.Job;
    }

    private static IntPtr AllocManagedContext<T>(ref T job) where T : struct
    {
        var handle = GCHandle.Alloc(new ManagedJobBox<T>(job), GCHandleType.Normal);
        return GCHandle.ToIntPtr(handle);
    }

    private static IntPtr AllocManagedChunkBatchContext<T>(ref T job, Chunk[] chunks, ComponentType[] allEnabledTypes) where T : struct, IJobChunk
    {
        var handle = GCHandle.Alloc(new ManagedChunkBatchContext<T>(job, chunks, allEnabledTypes), GCHandleType.Normal);
        return GCHandle.ToIntPtr(handle);
    }

    private static IntPtr AllocRawChunkBatchContext<T>(ref T job, Chunk[] chunks, ComponentType[] allEnabledTypes) where T : struct, IJobChunk
    {
        IntPtr jobPtr = AllocContext(ref job);
        try
        {
            var handle = GCHandle.Alloc(new RawChunkBatchContext(jobPtr, chunks, allEnabledTypes), GCHandleType.Normal);
            return GCHandle.ToIntPtr(handle);
        }
        catch
        {
            Cleanup(jobPtr);
            throw;
        }
    }

    internal static void ManagedCleanup(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(ctx);
        if (handle.IsAllocated) handle.Free();
    }

    private static void RawChunkBatchCleanup(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(ctx);
        if (handle.IsAllocated)
        {
            if (handle.Target is RawChunkBatchContext context)
            {
                Cleanup(context.JobPtr);
                context.JobPtr = IntPtr.Zero;
            }

            handle.Free();
        }
    }

    private unsafe static IntPtr AllocContext<T>(ref T job) where T : struct
    {
        int size = Unsafe.SizeOf<T>();
        int totalSize = size + sizeof(int);
        IntPtr dataPtr = ContextPool.Rent(totalSize);
        *(int*)dataPtr = size;
        byte* jobPtr = (byte*)dataPtr + sizeof(int);
        Unsafe.CopyBlockUnaligned(jobPtr, Unsafe.AsPointer(ref job), (uint)size);
        return (IntPtr)jobPtr;
    }

    private unsafe static void Cleanup(IntPtr dataPtr)
    {
        if (dataPtr == IntPtr.Zero) return;
        int size = *(int*)((byte*)dataPtr - sizeof(int));
        ContextPool.Return((IntPtr)((byte*)dataPtr - sizeof(int)), size + sizeof(int));
    }
}
}
