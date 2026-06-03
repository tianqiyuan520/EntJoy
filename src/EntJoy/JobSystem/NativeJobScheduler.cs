using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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

/// <summary>
/// 原生作业调度器，所有作业通过 P/Invoke 调度到 C++ JobSystem 执行。
/// 支持 IJob、IJobFor、IJobParallelFor、IJobParallelForBatch、IJobChunk。
/// 此类型在全局命名空间中，便于源代码生成器引用。
/// </summary>
public static unsafe partial class NativeJobScheduler
{
    // ======================== DLL 函数指针 ========================
    private static IntPtr _nativeDll = IntPtr.Zero;

    // 函数指针（通过 GetProcAddress 获取）
    private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_Initialize;
    private static delegate* unmanaged[Cdecl]<void> _jobSystem_Shutdown;
    private static delegate* unmanaged[Cdecl]<void> _jobSystem_PrewakeWorkers;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> _jobSystem_Schedule;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, IntPtr, IntPtr> _jobSystem_ScheduleParallelForBatch;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr> _jobSystem_ScheduleFor;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_CompleteAndRelease;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _jobSystem_IsCompleted;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_ReleaseHandle;
    private static delegate* unmanaged[Cdecl]<IntPtr*, int, IntPtr> _jobSystem_CombineDependencies;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, IntPtr> _jobSystem_ScheduleChunkJob;
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

        // 去重并按“最后写入时间”降序，优先尝试最新构建产物，避免串到旧 DLL
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
        foreach (var candidate in existingCandidates)
        {
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
        _jobSystem_Schedule = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_Schedule");
        _jobSystem_ScheduleParallelForBatch = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleParallelForBatch");
        _jobSystem_ScheduleFor = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleFor");
        _jobSystem_CompleteAndRelease = (delegate* unmanaged[Cdecl]<IntPtr, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_CompleteAndRelease");
        _jobSystem_IsCompleted = (delegate* unmanaged[Cdecl]<IntPtr, int>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_IsCompleted");
        _jobSystem_ReleaseHandle = (delegate* unmanaged[Cdecl]<IntPtr, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ReleaseHandle");
        _jobSystem_CombineDependencies = (delegate* unmanaged[Cdecl]<IntPtr*, int, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_CombineDependencies");
        _jobSystem_ScheduleChunkJob = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, IntPtr>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleChunkJob");

        _profiler_SetEnabled = (delegate* unmanaged[Cdecl]<int, void>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_SetEnabled");
        _profiler_IsEnabled = (delegate* unmanaged[Cdecl]<int>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_IsEnabled");
        _profiler_ReadAll = (delegate* unmanaged[Cdecl]<ProfilerEntry*, int, int>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_ReadAll");
        _profiler_Clear = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobProfiler_Clear");
    }

    // ======================== 包装函数 ========================
    private static void JobSystem_Initialize(int numThreads) => _jobSystem_Initialize(numThreads);
    private static void JobSystem_Shutdown() => _jobSystem_Shutdown();
    private static void JobSystem_PrewakeWorkers() => _jobSystem_PrewakeWorkers();
    private static IntPtr JobSystem_Schedule(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, IntPtr dependency)
        => _jobSystem_Schedule(funcPtr, context, cleanupPtr, dependency);
    private static IntPtr JobSystem_ScheduleParallelForBatch(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, int length, int batchSize, IntPtr dependency)
        => _jobSystem_ScheduleParallelForBatch(funcPtr, context, cleanupPtr, length, batchSize, dependency);
    private static IntPtr JobSystem_ScheduleFor(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, int length, IntPtr dependency)
        => _jobSystem_ScheduleFor(funcPtr, context, cleanupPtr, length, dependency);
    private static void JobSystem_CompleteAndRelease(IntPtr handle) => _jobSystem_CompleteAndRelease(handle);
    private static int JobSystem_IsCompleted(IntPtr handle) => _jobSystem_IsCompleted(handle);
    private static void JobSystem_ReleaseHandle(IntPtr handle) => _jobSystem_ReleaseHandle(handle);
    private static IntPtr JobSystem_CombineDependencies(IntPtr[] handles, int count)
    {
        fixed (IntPtr* ptr = handles) return _jobSystem_CombineDependencies(ptr, count);
    }
    private static IntPtr JobSystem_ScheduleChunkJob(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, ChunkJobData* chunks, int chunkCount, IntPtr dependency)
        => _jobSystem_ScheduleChunkJob(funcPtr, context, cleanupPtr, chunks, chunkCount, dependency);

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
    private delegate void CleanupFunc(IntPtr context);

    // ======================== 委托缓存 ========================
    private static readonly ConcurrentDictionary<Type, DelegateCache> _delegateCache = new();
    private sealed class DelegateCache { public readonly Delegate Delegate; public readonly IntPtr FuncPtr; public DelegateCache(Delegate del) { Delegate = del; FuncPtr = Marshal.GetFunctionPointerForDelegate(del); } }

    private static readonly CleanupFunc _cleanup = Cleanup;
    private static readonly IntPtr _cleanupPtr = Marshal.GetFunctionPointerForDelegate(_cleanup);
    private static readonly object _chunkGCHandlesLock = new();
    private static readonly List<GCHandle> _chunkGCHandles = new();

    // ======================== 公共接口 ========================
    public static void Initialize(int numThreads = 0) => JobSystem_Initialize(numThreads);
    public static void Shutdown() => JobSystem_Shutdown();
    public static void PrewakeWorkersOnce() => JobSystem_PrewakeWorkers();
    private static long s_lastParallelScheduleTicks;
    private static readonly long s_prewakeGapTicks = Stopwatch.Frequency / 1000; // 1ms

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AutoPrewakeIfNeeded(int length)
    {
        if (length < 1024) return;
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref s_lastParallelScheduleTicks);
        if (now - last >= s_prewakeGapTicks)
        {
            JobSystem_PrewakeWorkers();
        }
        Interlocked.Exchange(ref s_lastParallelScheduleTicks, now);
    }

    public static NativeJobHandle Schedule<T>(ref T job, NativeJobHandle? dependsOn = null)
        where T : struct, IJob
    {
        var ctx = AllocContext(ref job);
        try
        {
            var cache = GetOrCreateDelegateCache<T, JobFunc>(() => CreateJobCallback<T>());
            return new NativeJobHandle(
                JobSystem_Schedule(cache.FuncPtr, ctx, _cleanupPtr, dependsOn?.Handle ?? IntPtr.Zero));
        }
        catch { Cleanup(ctx); throw; }
    }

    public static NativeJobHandle ScheduleFor<T>(ref T job, int length, NativeJobHandle? dependsOn = null)
        where T : struct, IJobFor
    {
        if (length <= 0) return default;
        var ctx = AllocContext(ref job);
        try
        {
            var cache = GetOrCreateDelegateCache<T, IndexJobFunc>(() => CreateForCallback<T>());
            return new NativeJobHandle(
                JobSystem_ScheduleFor(cache.FuncPtr, ctx, _cleanupPtr, length, dependsOn?.Handle ?? IntPtr.Zero));
        }
        catch { Cleanup(ctx); throw; }
    }

    public static NativeJobHandle ScheduleParallelFor<T>(ref T job, int length, int batchSize, NativeJobHandle? dependsOn = null)
        where T : struct, IJobParallelFor
    {
        if (length <= 0) return default;
        AutoPrewakeIfNeeded(length);
        var ctx = AllocContext(ref job);
        try
        {
            var cache = GetOrCreateDelegateCache<T, BatchJobFunc>(() => CreateParallelForIndexCallback<T>());
            return new NativeJobHandle(
                JobSystem_ScheduleParallelForBatch(cache.FuncPtr, ctx, _cleanupPtr, length, batchSize, dependsOn?.Handle ?? IntPtr.Zero));
        }
        catch { Cleanup(ctx); throw; }
    }

    public static NativeJobHandle ScheduleParallelForBatch<T>(ref T job, int length, int batchSize, NativeJobHandle? dependsOn = null)
        where T : struct, IJobParallelForBatch
    {
        if (length <= 0) return default;
        AutoPrewakeIfNeeded(length);
        var ctx = AllocContext(ref job);
        try
        {
            var cache = GetOrCreateDelegateCache<T, BatchJobFunc>(() => CreateParallelForBatchCallback<T>());
            return new NativeJobHandle(
                JobSystem_ScheduleParallelForBatch(cache.FuncPtr, ctx, _cleanupPtr, length, batchSize, dependsOn?.Handle ?? IntPtr.Zero));
        }
        catch { Cleanup(ctx); throw; }
    }

    /// <summary>
    /// 等待作业完成并释放句柄。
    /// 使用 P/Invoke 确保可靠同步（C++ std::atomic::wait + notify_all）。
    /// 任务完成时 C++ 自动回收状态，无需 C# 调用 ReleaseHandle。
    /// </summary>
    public static void Complete(ref NativeJobHandle h)
    {
        if (h.Handle == IntPtr.Zero) return;

        // 始终使用 P/Invoke 完成等待，确保同步正确性
        // C++ std::atomic::wait 在内部自旋后执行等待原语，效率接近忙等但更可靠
        JobSystem_CompleteAndRelease(h.Handle);
        h.Handle = IntPtr.Zero;
    }

    public static bool IsCompleted(NativeJobHandle h)
    {
        if (h.Handle == IntPtr.Zero) return true;
        var view = (HandleStateView*)(byte*)h.Handle;
        return view->Completed;
    }

    public static void Release(NativeJobHandle h)
    {
        // C++ 自动管理 HandleState 生命周期；C# 仅释放引用
        // 无需 P/Invoke
    }

    public static NativeJobHandle ScheduleRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, NativeJobHandle? dependsOn = null)
        => new NativeJobHandle(JobSystem_Schedule(funcPtr, contextPtr, cleanupPtr, dependsOn?.Handle ?? IntPtr.Zero));

    public static NativeJobHandle ScheduleForRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, NativeJobHandle? dependsOn = null)
        => new NativeJobHandle(JobSystem_ScheduleFor(funcPtr, contextPtr, cleanupPtr, length, dependsOn?.Handle ?? IntPtr.Zero));

    public static NativeJobHandle ScheduleParallelForBatchRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, int batchSize, NativeJobHandle? dependsOn = null)
    {
        if (length > 0) AutoPrewakeIfNeeded(length);
        return new NativeJobHandle(JobSystem_ScheduleParallelForBatch(funcPtr, contextPtr, cleanupPtr, length, batchSize, dependsOn?.Handle ?? IntPtr.Zero));
    }


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
        for (int i = 0; i < handles.Length; i++) ptrs[i] = handles[i].Handle;
        return new NativeJobHandle(JobSystem_CombineDependencies(ptrs, handles.Length));
    }

    // ======================== IJobChunk 调度 ========================
    private static readonly object _rawChunkScheduleCacheLock = new();
    private static readonly Dictionary<RawChunkScheduleCacheKey, RawChunkScheduleCache> _rawChunkScheduleCaches = new();

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
        }
    }

    public static NativeJobHandle ScheduleChunk<T>(ref T job, EntityManager entityManager, QueryBuilder query, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleChunkCore(ref job, entityManager, query, IntPtr.Zero, null, dependsOn);

    public static NativeJobHandle ScheduleChunkRaw<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
        => ScheduleChunkCore(ref job, entityManager, query, funcPtr, requiredComponentTypeIds, dependsOn);

    private static NativeJobHandle ScheduleChunkCore<T>(ref T job, EntityManager entityManager, QueryBuilder query, IntPtr funcPtr, int[] requiredComponentTypeIds, NativeJobHandle? dependsOn)
        where T : struct, IJobChunk
    {
        var allEnabledTypes = query.AllEnabled;
        bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;
        bool canUseRawCache = funcPtr != IntPtr.Zero &&
                              !hasEnabledFilter;
        if (canUseRawCache &&
            TryGetRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds, out var rawCache) &&
            rawCache.ChunkCount > 0)
        {
            var rawContextBlock = CreateChunkContextBlock(ref job, rawCache.ChunksPtr, rawCache.ChunkCount, false, null, -1, requiredComponentTypeIds);
            try
            {
                return new NativeJobHandle(JobSystem_ScheduleChunkJob(funcPtr, rawContextBlock, _chunkCleanupPtr, rawCache.ChunksPtr, rawCache.ChunkCount, dependsOn?.Handle ?? IntPtr.Zero));
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

        int gcHandleStartIndex;
        lock (_chunkGCHandlesLock) { gcHandleStartIndex = _chunkGCHandles.Count; }

        for (int ci = 0; ci < chunkCount; ci++)
        {
            var chunk = chunkList[ci];
            var arch = chunk.Archetype;
            var gch = GCHandle.Alloc(chunk, GCHandleType.WeakTrackResurrection);
            lock (_chunkGCHandlesLock) { _chunkGCHandles.Add(gch); }

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
                chunkHandle = (IntPtr)gch,
                requiredComponentArrays = requiredArrays,
                requiredComponentCount = requiredCount
            };
        }

        var contextBlock = CreateChunkContextBlock(ref job, chunksPtr, chunkCount, hasEnabledFilter, allEnabledTypes, gcHandleStartIndex, requiredComponentTypeIds);

        try
        {
            IntPtr callbackPtr = funcPtr;
            if (callbackPtr == IntPtr.Zero)
            {
                var cache = GetOrCreateDelegateCache<T, ChunkJobFuncDelegate>(() => CreateChunkCallback<T>());
                callbackPtr = cache.FuncPtr;
            }
            return new NativeJobHandle(JobSystem_ScheduleChunkJob(callbackPtr, contextBlock, _chunkCleanupPtr, chunksPtr, chunkCount, dependsOn?.Handle ?? IntPtr.Zero));
        }
        catch { ChunkCleanup(contextBlock); throw; }
    }

    private static bool TryGetRawChunkScheduleCache(EntityManager entityManager, QueryBuilder query, int[] requiredComponentTypeIds, out RawChunkScheduleCache cache)
    {
        var key = new RawChunkScheduleCacheKey(entityManager, GetQueryHash(query), GetRequiredComponentHash(requiredComponentTypeIds));
        lock (_rawChunkScheduleCacheLock)
        {
            if (_rawChunkScheduleCaches.TryGetValue(key, out cache))
            {
                if (cache.StructuralVersion == entityManager.StructuralVersion)
                {
                    return true;
                }

                cache.Dispose();
                _rawChunkScheduleCaches.Remove(key);
            }

            cache = BuildRawChunkScheduleCache(entityManager, query, requiredComponentTypeIds);
            _rawChunkScheduleCaches[key] = cache;
            return true;
        }
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

        public RawChunkScheduleCacheKey(EntityManager entityManager, int queryHash, int requiredHash)
        {
            _entityManager = entityManager;
            _managerHash = RuntimeHelpers.GetHashCode(entityManager);
            _queryHash = queryHash;
            _requiredHash = requiredHash;
        }

        public bool Equals(RawChunkScheduleCacheKey other)
            => ReferenceEquals(_entityManager, other._entityManager) &&
               _queryHash == other._queryHash &&
               _requiredHash == other._requiredHash;

        public bool Matches(EntityManager entityManager)
            => ReferenceEquals(_entityManager, entityManager);

        public override bool Equals(object obj)
            => obj is RawChunkScheduleCacheKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(_managerHash, _queryHash, _requiredHash);
    }

    private sealed class RawChunkScheduleCache : IDisposable
    {
        public readonly int StructuralVersion;
        public readonly ChunkJobData* ChunksPtr;
        public readonly int ChunkCount;

        public RawChunkScheduleCache(int structuralVersion, ChunkJobData* chunksPtr, int chunkCount)
        {
            StructuralVersion = structuralVersion;
            ChunksPtr = chunksPtr;
            ChunkCount = chunkCount;
        }

        ~RawChunkScheduleCache()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (ChunksPtr == null)
            {
                GC.SuppressFinalize(this);
                return;
            }

            for (int i = 0; i < ChunkCount; i++)
            {
                var chunkData = ChunksPtr[i];
                if (chunkData.componentArrays != null) Marshal.FreeHGlobal((IntPtr)chunkData.componentArrays);
                if (chunkData.componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)chunkData.componentTypeIndices);
                if (chunkData.requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)chunkData.requiredComponentArrays);
            }

            Marshal.FreeHGlobal((IntPtr)ChunksPtr);
            GC.SuppressFinalize(this);
        }
    }

    // ======================== 内部实现 ========================
    private static readonly CleanupFunc _chunkCleanup = ChunkCleanup;
    private static readonly IntPtr _chunkCleanupPtr = Marshal.GetFunctionPointerForDelegate(_chunkCleanup);

    private unsafe static IntPtr CreateChunkContextBlock<T>(ref T job, ChunkJobData* chunksPtr, int chunkCount, bool hasEnabledFilter, ComponentType[] allEnabledTypes, int gcHandleStartIndex, int[] requiredComponentTypeIds = null) where T : struct
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
        var block = Marshal.AllocHGlobal(totalSize);
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
        Marshal.FreeHGlobal(contextBlock);
    }

    // ======================== 回调工厂 ========================
    private unsafe static JobFunc CreateJobCallback<T>() where T : struct, IJob
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        return (IntPtr ctx) =>
        {
            int threadId = Environment.CurrentManagedThreadId;
            long start = 0;
            if (JobProfiler.Enabled) start = Stopwatch.GetTimestamp();
            Unsafe.AsRef<T>((void*)ctx).Execute();
            if (JobProfiler.Enabled) { long end = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, start, end, threadId, 0); }
        };
    }

    private unsafe static IndexJobFunc CreateForCallback<T>() where T : struct, IJobFor
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        return (IntPtr ctx, int i) =>
        {
            int threadId = Environment.CurrentManagedThreadId;
            long start = 0;
            if (JobProfiler.Enabled) start = Stopwatch.GetTimestamp();
            Unsafe.AsRef<T>((void*)ctx).Execute(i);
            if (JobProfiler.Enabled) { long end = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, start, end, threadId, 1); }
        };
    }

    private unsafe static BatchJobFunc CreateParallelForIndexCallback<T>() where T : struct, IJobParallelFor
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        return (IntPtr ctx, int start, int count) =>
        {
            int threadId = Environment.CurrentManagedThreadId;
            long startTicks = 0;
            if (JobProfiler.Enabled) startTicks = Stopwatch.GetTimestamp();
            ref var job = ref Unsafe.AsRef<T>((void*)ctx);
            int end = start + count;
            for (int i = start; i < end; i++) job.Execute(i);
            if (JobProfiler.Enabled) { long endTicks = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, startTicks, endTicks, threadId, 2); }
        };
    }

    private unsafe static BatchJobFunc CreateParallelForBatchCallback<T>() where T : struct, IJobParallelForBatch
    {
        string name = typeof(T).Name;
        ulong hash = StableHash.Compute(name);
        JobProfiler.RegisterJobName(hash, name);
        return (IntPtr ctx, int start, int count) =>
        {
            int threadId = Environment.CurrentManagedThreadId;
            long startTicks = 0;
            if (JobProfiler.Enabled) startTicks = Stopwatch.GetTimestamp();
            ref var job = ref Unsafe.AsRef<T>((void*)ctx);
            job.Execute(start, count);
            if (JobProfiler.Enabled) { long endTicks = Stopwatch.GetTimestamp(); ProfilerRecorder.Record(hash, startTicks, endTicks, threadId, 3); }
        };
    }

    private unsafe static ChunkJobFuncDelegate CreateChunkCallback<T>() where T : struct, IJobChunk
    {
        return (IntPtr ctx, ChunkJobData* cd) =>
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
                ulong* combinedMask;
                const int maxStackAlloc = 256;
                if (ulongCount <= maxStackAlloc) { var u = stackalloc ulong[ulongCount]; combinedMask = u; }
                else { combinedMask = TempBuffer.GetBuffer(ulongCount); }

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
        };
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

        public static IntPtr Rent(int size)
        {
            int idx = GetBucketIndex(size);
            if (idx < 0) return Marshal.AllocHGlobal(size);
            var bucket = _buckets[idx];
            if (bucket != null && bucket.TryPop(out var ptr)) return ptr;
            return Marshal.AllocHGlobal(1 << (BucketShift + idx));
        }

        public static void Return(IntPtr ptr, int size)
        {
            if (ptr == IntPtr.Zero) return;
            int idx = GetBucketIndex(size);
            if (idx < 0) { Marshal.FreeHGlobal(ptr); return; }
            var bucket = _buckets[idx];
            if (bucket == null) { bucket = new ConcurrentStack<IntPtr>(); _buckets[idx] = bucket; }
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
