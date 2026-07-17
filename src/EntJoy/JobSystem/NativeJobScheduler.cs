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
    public int gcHandleStartIndex;       // GCHandle 列表起始索引
    public IntPtr chunksPtr;             // ChunkJobData 数组指针（用于 cleanup 回收）
    public int cleanupInProgress;        // 防止重复清理的标志
    public IntPtr requiredComponentTypeIds; // NativeTranspiler IJobChunk 所需组件类型 ID 数组
    public int requiredComponentTypeIdCount; // 所需组件类型 ID 数量
    // 紧接着是 job 的原始数据（变长）
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ManagedChunkContextHeader
{
    public IntPtr jobHandle;             // GCHandle -> ManagedJobBox<T>
    public int chunkCount;               // Chunk 数量
    public int hasEnabledFilter;         // 是否有 enable 过滤
    public IntPtr queryAllEnabledTypes;  // int[]（类型哈希数组）指针
    public int allEnabledCount;          // AllEnabled 数组长度
    public IntPtr chunksPtr;             // ChunkJobData 数组指针（用于 cleanup 回收）
    public int ownsChunkData;            // 是否由该 context 释放 chunksPtr
}

/// <summary>
/// HandleState 的 C# 侧视图（与 C++ HandleState 内存布局一一对应）
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HandleStateView
{
    // C++ HandleState:
    // std::atomic<uint32_t> refCount;   // offset 0
    // std::atomic<bool> completed;       // offset 4
    private uint _refCount;
    private byte _completed;

    public bool Completed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Thread.VolatileRead(ref _completed) != 0;
    }
}

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

/// <summary>
/// 原生作业调度器，所有作业通过 P/Invoke 调度到 C++ JobSystem 执行。
/// 支持 IJob、IJobFor、IJobParallelFor、IJobParallelForBatch、IJobChunk。
/// 此类型在全局命名空间中，便于源代码生成器引用。
/// </summary>
public static unsafe partial class NativeJobScheduler
{
    private const int MaxRecordedJobExceptions = 16;
    private static readonly ConcurrentQueue<ExceptionDispatchInfo> _jobExceptions = new();
    private static int _recordedJobExceptionCount;
    [ThreadStatic] private static int _jobExecutionDepth;

    internal static bool IsExecutingJob => _jobExecutionDepth > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnterJobExecution() => _jobExecutionDepth++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExitJobExecution() => _jobExecutionDepth--;

    // ======================== DLL 函数指针 ========================
    private static IntPtr _nativeDll = IntPtr.Zero;
    private static int _shutdownRequested;

    // 函数指针（通过 GetProcAddress 获取）
    private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_Initialize;
    private static delegate* unmanaged[Cdecl]<void> _jobSystem_Shutdown;
    private static delegate* unmanaged[Cdecl]<void> _jobSystem_PrewakeWorkers;
    private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_KeepWorkersWarm;
    private static delegate* unmanaged[Cdecl]<void> _jobSystem_FlushScheduledJobs;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> _jobSystem_Schedule;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, IntPtr, IntPtr> _jobSystem_ScheduleParallelForBatch;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr> _jobSystem_ScheduleFor;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_CompleteAndRelease;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_RetainHandle;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _jobSystem_IsCompleted;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_ReleaseHandle;
    private static delegate* unmanaged[Cdecl]<IntPtr*, int, IntPtr> _jobSystem_CombineDependencies;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, IntPtr> _jobSystem_ScheduleChunkJob;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, IntPtr> _jobSystem_ScheduleChunkJobEx;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, IntPtr> _jobSystem_ScheduleChunkRangeJobEx;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, IntPtr> _jobSystem_ScheduleEntityBatchJobEx;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, IntPtr> _jobSystem_ScheduleAndCompleteEntityBatchJobEx;
    private static delegate* unmanaged[Cdecl]<NativeJobSystemStats*, void> _jobSystem_GetStats;
    private static delegate* unmanaged[Cdecl]<void> _jobSystem_ResetStats;
    // Profiler 函数指针
    private static delegate* unmanaged[Cdecl]<int, void> _profiler_SetEnabled;
    private static delegate* unmanaged[Cdecl]<int> _profiler_IsEnabled;
    private static delegate* unmanaged[Cdecl]<ProfilerEntry*, int, int> _profiler_ReadAll;
    private static delegate* unmanaged[Cdecl]<void> _profiler_Clear;

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

        _jobSystem_Initialize = (delegate* unmanaged[Cdecl]<int, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_Initialize");
        _jobSystem_Shutdown = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_Shutdown");
        _jobSystem_PrewakeWorkers = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_PrewakeWorkers");
        _jobSystem_KeepWorkersWarm = (delegate* unmanaged[Cdecl]<int, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_KeepWorkersWarm");
        _jobSystem_FlushScheduledJobs = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_FlushScheduledJobs");
        _jobSystem_Schedule = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_Schedule");
        _jobSystem_ScheduleParallelForBatch = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleParallelForBatch");
        _jobSystem_ScheduleFor = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleFor");
        _jobSystem_CompleteAndRelease = (delegate* unmanaged[Cdecl]<IntPtr, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_CompleteAndRelease");
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
        _jobSystem_ScheduleEntityBatchJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleEntityBatchJobEx");
        _jobSystem_ScheduleAndCompleteEntityBatchJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleAndCompleteEntityBatchJobEx");
        _jobSystem_GetStats = (delegate* unmanaged[Cdecl]<NativeJobSystemStats*, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_GetStats");
        _jobSystem_ResetStats = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ResetStats");

        _profiler_SetEnabled = (delegate* unmanaged[Cdecl]<int, void>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_SetEnabled");
        _profiler_IsEnabled = (delegate* unmanaged[Cdecl]<int>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_IsEnabled");
        _profiler_ReadAll = (delegate* unmanaged[Cdecl]<ProfilerEntry*, int, int>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_ReadAll");
        _profiler_Clear = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_Clear");

        AppDomain.CurrentDomain.ProcessExit += static (_, _) => SafeShutdown();
        AppDomain.CurrentDomain.DomainUnload += static (_, _) => SafeShutdown();
    }

    // ======================== 包装函数 ========================
    private static bool IsNativeLoaded => _nativeDll != IntPtr.Zero && _jobSystem_Initialize != null;

    private static void EnsureNativeLoaded()
    {
        if (!IsNativeLoaded)
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

    private static void JobSystem_KeepWorkersWarm(int microseconds)
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_KeepWorkersWarm == null) return;
        _jobSystem_KeepWorkersWarm(microseconds);
    }

    private static void JobSystem_FlushScheduledJobs()
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_FlushScheduledJobs == null) return;
        _jobSystem_FlushScheduledJobs();
    }

    private static IntPtr JobSystem_Schedule(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, IntPtr dependency)
    {
        EnsureNativeLoaded();
        return _jobSystem_Schedule(funcPtr, context, cleanupPtr, dependency);
    }

    private static IntPtr JobSystem_ScheduleParallelForBatch(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, int length, int batchSize, IntPtr dependency)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleParallelForBatch(funcPtr, context, cleanupPtr, length, batchSize, dependency);
    }

    private static IntPtr JobSystem_ScheduleFor(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, int length, IntPtr dependency)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleFor(funcPtr, context, cleanupPtr, length, dependency);
    }

    private static void JobSystem_CompleteAndRelease(IntPtr handle)
    {
        EnsureNativeLoaded();
        _jobSystem_CompleteAndRelease(handle);
    }

    private static void JobSystem_RetainHandle(IntPtr handle)
    {
        EnsureNativeLoaded();
        _jobSystem_RetainHandle(handle);
    }

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

    private static IntPtr JobSystem_CombineDependencies(IntPtr[] handles, int count)
    {
        EnsureNativeLoaded();
        fixed (IntPtr* ptr = handles) return _jobSystem_CombineDependencies(ptr, count);
    }
    private static IntPtr JobSystem_ScheduleChunkJob(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, ChunkJobData* chunks, int chunkCount, IntPtr dependency)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleChunkJob(funcPtr, context, cleanupPtr, chunks, chunkCount, dependency);
    }

    private static IntPtr JobSystem_ScheduleChunkJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, ChunkJobData* chunks, int chunkCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleChunkJobEx(funcPtr, context, cleanupPtr, chunks, chunkCount, dependency, (int)mode, workerCap, rangeSize);
    }

    private static IntPtr JobSystem_ScheduleChunkRangeJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, ChunkJobData* chunks, int chunkCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleChunkRangeJobEx(funcPtr, context, cleanupPtr, chunks, chunkCount, dependency, (int)mode, workerCap, rangeSize);
    }

    private static IntPtr JobSystem_ScheduleEntityBatchJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, EntityBatchData* batches, int batchCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleEntityBatchJobEx(funcPtr, context, cleanupPtr, batches, batchCount, dependency, (int)mode, workerCap, rangeSize);
    }

    private static IntPtr JobSystem_ScheduleAndCompleteEntityBatchJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, EntityBatchData* batches, int batchCount, IntPtr dependency, ChunkScheduleMode mode = ChunkScheduleMode.PublishAssist, int workerCap = 0, int rangeSize = 0)
    {
        EnsureNativeLoaded();
        return _jobSystem_ScheduleAndCompleteEntityBatchJobEx(funcPtr, context, cleanupPtr, batches, batchCount, dependency, (int)mode, workerCap, rangeSize);
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
    private delegate void EntityBatchJobFuncDelegate(IntPtr context, EntityBatchData* batches, int startIndex, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CleanupFunc(IntPtr context);

    // ======================== 委托缓存 ========================
    private static readonly ConcurrentDictionary<Type, DelegateCache> _delegateCache = new();
    private sealed class DelegateCache { public readonly Delegate Delegate; public readonly IntPtr FuncPtr; public DelegateCache(Delegate del) { Delegate = del; FuncPtr = Marshal.GetFunctionPointerForDelegate(del); } }

    private static readonly CleanupFunc _cleanup = Cleanup;
    private static readonly IntPtr _cleanupPtr = Marshal.GetFunctionPointerForDelegate(_cleanup);
    private static readonly CleanupFunc _managedCleanup = ManagedCleanup;
    private static readonly IntPtr _managedCleanupPtr = Marshal.GetFunctionPointerForDelegate(_managedCleanup);
    private static readonly CleanupFunc _rawChunkBatchCleanup = RawChunkBatchCleanup;
    private static readonly IntPtr _rawChunkBatchCleanupPtr = Marshal.GetFunctionPointerForDelegate(_rawChunkBatchCleanup);
    private static readonly object _chunkGCHandlesLock = new();
    private static readonly List<GCHandle> _chunkGCHandles = new();

    // ======================== 公共接口 ========================
    public static void Initialize(int numThreads = 0)
    {
        Interlocked.Exchange(ref _shutdownRequested, 0);
        JobSystem_Initialize(numThreads);
    }
    public static void Shutdown() => SafeShutdown();
    public static void PrewakeWorkersOnce() => JobSystem_PrewakeWorkers();
    public static void KeepWorkersWarm(int microseconds) => JobSystem_KeepWorkersWarm(microseconds);
    public static void FlushScheduledJobs() => JobSystem_FlushScheduledJobs();

    private static void SafeShutdown()
    {
        if (_nativeDll == IntPtr.Zero || _jobSystem_Shutdown == null)
            return;
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
            return;
        JobSystem_Shutdown();
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
            var cache = GetOrCreateDelegateCache<T, BatchJobFunc>(() => CreateParallelForIndexCallback<T>());
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            IntPtr handle = JobSystem_ScheduleParallelForBatch(cache.FuncPtr, ctx, managedContext ? _managedCleanupPtr : _cleanupPtr, length, batchSize, dependencyLease.Handle);
            cleanupByCpp = true;
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

        // 始终使用 P/Invoke 完成等待，确保同步正确性
        // C++ std::atomic::wait 在内部自旋后执行等待原语，效率接近忙等但更可靠
        JobSystem_CompleteAndRelease(handle);

        ThrowRecordedJobExceptions();
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

    private readonly struct RetainedNativeDependency : IDisposable
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

    public static NativeJobSystemStats GetStats() => JobSystem_GetStats();
    public static void ResetStats() => JobSystem_ResetStats();

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
    private static readonly object _rawChunkScheduleCacheLock = new();
    private static readonly Dictionary<RawChunkScheduleCacheKey, RawChunkScheduleCache> _rawChunkScheduleCaches = new();
    private static readonly Dictionary<RawChunkScheduleCacheKey, ManagedChunkScheduleCache> _managedChunkScheduleCaches = new();
    private static readonly Dictionary<RawChunkScheduleCacheKey, EntityBatchScheduleCache> _entityBatchScheduleCaches = new();
    private static readonly ConcurrentDictionary<IntPtr, GCHandle> _chunkContextLeases = new();

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

    public static NativeJobHandle ScheduleChunkRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleChunkCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn);

    public static NativeJobHandle ScheduleChunkRangeRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr rangeFuncPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
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

    public static NativeJobHandle ScheduleEntityBatchRawWithWorkerCapAndRangeSize<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap, int rangeSize, NativeJobHandle? dependsOn = null)
        where T : struct
        => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn, workerCap, rangeSize);

    /// <summary>
    /// Schedule + Complete 一步完成，消除一次 P/Invoke 往返和 handle boxing 开销。
    /// 适用于基准测试和一次性同步 job。
    /// </summary>
    public static NativeJobHandle ScheduleAndCompleteEntityBatchRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, int workerCap = 0, int rangeSize = 0)
        where T : struct
        => ScheduleNativeEntityBatchRawCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, null, workerCap, rangeSize, useScheduleAndComplete: true);

    public static NativeJobHandle ScheduleManagedEntityBatch<TJob, TExecutor>(ref TJob job, EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn = null)
        where TJob : struct, IJobEntity
        where TExecutor : struct, IJobEntityBatchExecutor<TJob>
    {
        if (query.AllEnabled != null && query.AllEnabled.Length > 0)
            throw new NotSupportedException("Direct managed IJobEntity batches do not support AllEnabled filters.");

        if (!TryGetEntityBatchScheduleCache(entityManager, query, requiredComponentTypeIds, out var cache, out var cacheLease) ||
            cache.BatchCount == 0)
            return default;

        var contextBlock = CreateChunkContextBlock(ref job, null, cache.BatchCount, false, null, -1, requiredComponentTypeIds, cacheLease);
        try
        {
            var callback = GetOrCreateDelegateCache<TExecutor, EntityBatchJobFuncDelegate>(() => CreateManagedEntityBatchCallback<TJob, TExecutor>());
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleEntityBatchJobEx(
                callback.FuncPtr, contextBlock, _chunkCleanupPtr, cache.BatchesPtr, cache.BatchCount,
                dependencyLease.Handle, ChunkScheduleMode.PublishAssist)));
        }
        catch
        {
            ChunkCleanup(contextBlock);
            throw;
        }
    }

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
            var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, requiredComponentTypeIds, rawCacheLease);
            try
            {
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, mode, workerCap, rangeSize)));
            }
            catch { ChunkCleanup(rawContextBlock); throw; }
        }

        bool jobHasManagedReferences = JobHasManagedReferences<T>();

        if (funcPtr == IntPtr.Zero &&
            !jobHasManagedReferences &&
            TryGetManagedChunkScheduleCache(entityManager, query, out var csharpRawCache, out var csharpRawCacheLease) &&
            csharpRawCache.ChunkCount > 0)
        {
            var csharpRawContextBlock = CreateChunkContextBlock(ref job, csharpRawCache.ChunksPtr, csharpRawCache.ChunkCount, hasEnabledFilter, allEnabledTypes, -1, null, csharpRawCacheLease);
            try
            {
                var cache = GetOrCreateDelegateCache<T, ChunkRangeJobFuncDelegate>(() => CreateChunkRangeCallback<T>());
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleChunkRangeJobEx(cache.FuncPtr, csharpRawContextBlock, _chunkCleanupPtr, csharpRawCache.ChunksPtr, csharpRawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist)));
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
                return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleParallelForBatch(cache.FuncPtr, managedContextBlock, jobHasManagedReferences ? _managedCleanupPtr : _rawChunkBatchCleanupPtr, managedCache.Chunks.Length, -1, dependencyLease.Handle)));
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

        // 先预分配所有 GCHandle（无锁安全），再原子性加入列表
        var gcHandles = new GCHandle[chunkCount];
        for (int ci = 0; ci < chunkCount; ci++)
            gcHandles[ci] = GCHandle.Alloc(chunkList[ci], GCHandleType.WeakTrackResurrection);

        int gcHandleStartIndex;
        lock (_chunkGCHandlesLock)
        {
            gcHandleStartIndex = _chunkGCHandles.Count;
            for (int ci = 0; ci < chunkCount; ci++)
                _chunkGCHandles.Add(gcHandles[ci]);
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
                    chunkHandle = (IntPtr)gcHandles[ci],
                    requiredComponentArrays = requiredArrays,
                    requiredComponentCount = requiredCount
                };
            }

            contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, requiredComponentTypeIds);

            IntPtr callbackPtr = funcPtr;
            if (callbackPtr == IntPtr.Zero)
            {
                var cache = GetOrCreateDelegateCache<T, ChunkJobFuncDelegate>(() => CreateChunkCallback<T>());
                callbackPtr = cache.FuncPtr;
            }
            var mode = forcedMode ?? ChunkScheduleMode.PublishAssist;
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleChunkJobEx(callbackPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, mode, workerCap, rangeSize)));
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
            var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, requiredComponentTypeIds, rawCacheLease);
            try
            {
                using var dependencyLease = new RetainedNativeDependency(dependsOn);
                return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize)));
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
        // 先预分配所有 GCHandle（无锁安全），再原子性加入列表
        var gcHandles = new GCHandle[chunkCount];
        for (int ci = 0; ci < chunkCount; ci++)
            gcHandles[ci] = GCHandle.Alloc(chunkList[ci], GCHandleType.WeakTrackResurrection);

        int gcHandleStartIndex;
        lock (_chunkGCHandlesLock)
        {
            gcHandleStartIndex = _chunkGCHandles.Count;
            for (int ci = 0; ci < chunkCount; ci++)
                _chunkGCHandles.Add(gcHandles[ci]);
        }

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
                chunkHandle = (IntPtr)gcHandles[ci],
                requiredComponentArrays = requiredArrays,
                requiredComponentCount = requiredCount
            };
        }

        var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, requiredComponentTypeIds);
        try
        {
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleChunkJobEx(funcPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize)));
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
            var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, requiredComponentTypeIds, rawCacheLease);
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
        // 先预分配所有 GCHandle（无锁安全），再原子性加入列表
        var gcHandles = new GCHandle[chunkCount];
        for (int ci = 0; ci < chunkCount; ci++)
            gcHandles[ci] = GCHandle.Alloc(chunkList[ci], GCHandleType.WeakTrackResurrection);

        int gcHandleStartIndex;
        lock (_chunkGCHandlesLock)
        {
            gcHandleStartIndex = _chunkGCHandles.Count;
            for (int ci = 0; ci < chunkCount; ci++)
                _chunkGCHandles.Add(gcHandles[ci]);
        }

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
                chunkHandle = (IntPtr)gcHandles[ci],
                requiredComponentArrays = requiredArrays,
                requiredComponentCount = requiredCount
            };
        }

        var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, requiredComponentTypeIds);
        try
        {
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleChunkRangeJobEx(funcPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize)));
        }
        catch { ChunkCleanup(contextBlock); throw; }
    }

    private static NativeJobHandle ScheduleNativeEntityBatchRawCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn, int workerCap, int rangeSize, bool useScheduleAndComplete = false)
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

        var contextBlock = CreateChunkContextBlock(ref job, null, cache.BatchCount, false, null, -1, requiredComponentTypeIds, cacheLease);
        try
        {
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            var handle = useScheduleAndComplete
                ? JobSystem_ScheduleAndCompleteEntityBatchJobEx(funcPtr, contextBlock, _chunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize)
                : JobSystem_ScheduleEntityBatchJobEx(funcPtr, contextBlock, _chunkCleanupPtr, cache.BatchesPtr, cache.BatchCount, dependencyLease.Handle, ChunkScheduleMode.PublishAssist, workerCap, rangeSize);
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

        var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, requiredComponentTypeIds, rawCacheLease);
        try
        {
            return TrackEntityJob(entityManager, new NativeJobHandle(JobSystem_ScheduleChunkJobEx(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, IntPtr.Zero, ChunkScheduleMode.ImmediateNative)));
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

    private readonly struct RawChunkScheduleCacheKey : IEquatable<RawChunkScheduleCacheKey>
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

    private sealed class RawChunkScheduleCache : IDisposable
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

        private sealed class CacheLease : IDisposable
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

    private sealed class ManagedChunkScheduleCache
    {
        public readonly int StructuralVersion;
        public readonly Chunk[] Chunks;

        public ManagedChunkScheduleCache(int structuralVersion, Chunk[] chunks)
        {
            StructuralVersion = structuralVersion;
            Chunks = chunks;
        }
    }

    private sealed class EntityBatchScheduleCache : IDisposable
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

        private sealed class CacheLease : IDisposable
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
    private static readonly CleanupFunc _chunkCleanup = ChunkCleanup;
    private static readonly IntPtr _chunkCleanupPtr = Marshal.GetFunctionPointerForDelegate(_chunkCleanup);
    private static readonly CleanupFunc _managedChunkCleanup = ManagedChunkCleanup;
    private static readonly IntPtr _managedChunkCleanupPtr = Marshal.GetFunctionPointerForDelegate(_managedChunkCleanup);

    private unsafe static IntPtr CreateChunkContextBlock<T>(ref T job, ChunkJobData* chunksPtr, int chunkCount, bool hasEnabledFilter, ComponentType[] allEnabledTypes, int gcHandleStartIndex, int[] requiredComponentTypeIds = null, IDisposable cacheLease = null) where T : struct
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
        Unsafe.CopyBlockUnaligned(jobPtr, Unsafe.AsPointer(ref job), (uint)jobSize);
        if (cacheLease != null)
        {
            _chunkContextLeases[block] = GCHandle.Alloc(cacheLease, GCHandleType.Normal);
        }

        return block;
    }

    private unsafe static IntPtr CreateManagedChunkContextBlock<T>(ref T job, ChunkJobData* chunksPtr, int chunkCount, bool hasEnabledFilter, ComponentType[] allEnabledTypes, bool ownsChunkData) where T : struct
    {
        int headerSize = Unsafe.SizeOf<ManagedChunkContextHeader>();
        int typesDataSize = 0;
        int[] typeHashes = null;
        if (hasEnabledFilter && allEnabledTypes != null)
        {
            typeHashes = new int[allEnabledTypes.Length];
            for (int i = 0; i < allEnabledTypes.Length; i++) typeHashes[i] = allEnabledTypes[i].GetHashCode();
            typesDataSize = typeHashes.Length * sizeof(int);
        }

        var block = Marshal.AllocHGlobal(headerSize + typesDataSize);
        Unsafe.InitBlockUnaligned((void*)block, 0, (uint)(headerSize + typesDataSize));
        var header = (ManagedChunkContextHeader*)block;
        header->jobHandle = AllocManagedContext(ref job);
        header->chunkCount = chunkCount;
        header->hasEnabledFilter = hasEnabledFilter ? 1 : 0;
        header->chunksPtr = (IntPtr)chunksPtr;
        header->ownsChunkData = ownsChunkData ? 1 : 0;

        if (typeHashes != null && typeHashes.Length > 0)
        {
            var typeHashPtr = (int*)((byte*)block + headerSize);
            for (int i = 0; i < typeHashes.Length; i++) typeHashPtr[i] = typeHashes[i];
            header->allEnabledCount = typeHashes.Length;
            header->queryAllEnabledTypes = (IntPtr)typeHashPtr;
        }
        else
        {
            header->allEnabledCount = 0;
            header->queryAllEnabledTypes = IntPtr.Zero;
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
        bool ownsChunkData = gcHandleStartIndex >= 0;

        if (chunksPtr != null && ownsChunkData)
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
        if (_chunkContextLeases.TryRemove(contextBlock, out var leaseHandle))
        {
            if (leaseHandle.Target is IDisposable lease)
            {
                lease.Dispose();
            }

            leaseHandle.Free();
        }

        var pooledBlock = contextBlock - IntPtr.Size;
        int pooledSize = *(int*)pooledBlock;
        ContextPool.Return(pooledBlock, pooledSize);
    }

    private unsafe static void ManagedChunkCleanup(IntPtr contextBlock)
    {
        if (contextBlock == IntPtr.Zero) return;
        var header = (ManagedChunkContextHeader*)contextBlock;
        ManagedCleanup(header->jobHandle);

        var chunksPtr = (ChunkJobData*)header->chunksPtr;
        if (chunksPtr != null && header->ownsChunkData != 0)
        {
            for (int i = 0; i < header->chunkCount; i++)
            {
                var cd = chunksPtr[i];
                if (cd.componentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.componentArrays);
                if (cd.componentSizes != null) Marshal.FreeHGlobal((IntPtr)cd.componentSizes);
                if (cd.enableBitMaps != null) Marshal.FreeHGlobal((IntPtr)cd.enableBitMaps);
                if (cd.componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)cd.componentTypeIndices);
                if (cd.requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.requiredComponentArrays);
                if (cd.chunkHandle != IntPtr.Zero)
                {
                    var handle = GCHandle.FromIntPtr(cd.chunkHandle);
                    if (handle.IsAllocated) handle.Free();
                }
            }

            Marshal.FreeHGlobal((IntPtr)chunksPtr);
        }

        Marshal.FreeHGlobal(contextBlock);
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
                RecordJobException(exception);
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
                RecordJobException(exception);
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
                RecordJobException(exception);
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
                RecordJobException(exception);
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
                RecordJobException(exception);
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
                RecordJobException(exception);
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

    private unsafe static EntityBatchJobFuncDelegate CreateManagedEntityBatchCallback<TJob, TExecutor>()
        where TJob : struct, IJobEntity
        where TExecutor : struct, IJobEntityBatchExecutor<TJob>
    {
        return (IntPtr ctx, EntityBatchData* batches, int startIndex, int count) =>
        {
            EnterJobExecution();
            try
            {
                var header = (ChunkContextHeader*)ctx;
                int headerSize = Unsafe.SizeOf<ChunkContextHeader>();
                int typesDataSize = header->allEnabledCount * sizeof(int);
                int requiredTypesDataSize = header->requiredComponentTypeIdCount * sizeof(int);
                byte* jobPtr = (byte*)ctx + headerSize + typesDataSize + requiredTypesDataSize;
                ref var job = ref Unsafe.AsRef<TJob>(jobPtr);
                TExecutor.Execute(ref job, batches, startIndex, count);
            }
            catch (Exception exception)
            {
                RecordJobException(exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
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

    private unsafe static ChunkJobFuncDelegate CreateManagedChunkCallback<T>() where T : struct, IJobChunk
    {
        return (IntPtr ctx, ChunkJobData* cd) =>
        {
            EnterJobExecution();
            try
            {
                var header = (ManagedChunkContextHeader*)ctx;
                ref var job = ref GetManagedJob<T>(header->jobHandle);
                ExecuteManagedChunk(ref job, header, cd);
            }
            catch (Exception exception)
            {
                RecordJobException(exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    private unsafe static BatchJobFunc CreateManagedChunkBatchCallback<T>() where T : struct, IJobChunk
    {
        return (IntPtr ctx, int start, int count) =>
        {
            EnterJobExecution();
            try
            {
                var header = (ManagedChunkContextHeader*)ctx;
                ref var job = ref GetManagedJob<T>(header->jobHandle);
                var chunks = (ChunkJobData*)header->chunksPtr;
                int end = start + count;
                for (int index = start; index < end; index++)
                {
                    ExecuteManagedChunk(ref job, header, &chunks[index]);
                }
            }
            catch (Exception exception)
            {
                RecordJobException(exception);
            }
            finally
            {
                ExitJobExecution();
            }
        };
    }

    private unsafe static BatchJobFunc CreateChunkArrayBatchCallback<T>() where T : struct, IJobChunk
    {
        bool managedContext = JobHasManagedReferences<T>();
        return (IntPtr ctx, int start, int count) =>
        {
            EnterJobExecution();
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
                RecordJobException(exception);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static void ExecuteManagedChunk<T>(ref T job, ManagedChunkContextHeader* header, ChunkJobData* cd) where T : struct, IJobChunk
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

        /// <summary>安全计算桶大小，避免 1&lt;&lt;31 / 1&lt;&lt;32 溢出。</summary>
        private static int GetBucketAllocSize(int idx)
        {
            // idx 范围 0..63, BucketShift=6 → 移位 6..69
            // C# int 左移只用低 5 位，idx>=26 时会截断导致分配远小于预期。
            // 改用 long 计算再 clamp。
            long size = 1L << (BucketShift + idx);
            return (int)Math.Min(size, int.MaxValue);
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
    private static DelegateCache GetOrCreateDelegateCache<T, TDelegate>(Func<TDelegate> factory) where TDelegate : Delegate
    {
        var type = typeof(T);
        if (!_delegateCache.TryGetValue(type, out var cache))
        {
            cache = new DelegateCache(factory());
            _delegateCache[type] = cache;
        }
        return cache;
    }

    private static readonly object _exceptionLock = new();
    private static List<ExceptionDispatchInfo> _recordedJobExceptions = new();

    private static void RecordJobException(Exception exception)
    {
        lock (_exceptionLock)
        {
            if (_recordedJobExceptions.Count < MaxRecordedJobExceptions)
                _recordedJobExceptions.Add(ExceptionDispatchInfo.Capture(exception));
        }
    }

    /// <summary>
    /// 抛出所有已记录的 Job 异常。
    /// 公有接口，可在帧末通过 TempAllocator.Reset() 或自定义检查点调用。
    /// </summary>
    public static void FlushRecordedExceptions()
    {
        ThrowRecordedJobExceptions();
    }

    private static void ThrowRecordedJobExceptions()
    {
        List<ExceptionDispatchInfo> captured;
        lock (_exceptionLock)
        {
            captured = _recordedJobExceptions;
            _recordedJobExceptions = new List<ExceptionDispatchInfo>();
        }

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

    private static void ManagedCleanup(IntPtr ctx)
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
