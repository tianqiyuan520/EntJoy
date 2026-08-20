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
    }
}

