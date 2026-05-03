using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
    // 紧接着是 job 的原始数据（变长）
}

/// <summary>
/// 原生作业调度器，所有作业通过 P/Invoke 调度到 C++ JobSystem 执行。
/// 支持 IJob、IJobFor、IJobParallelFor、IJobParallelForBatch、IJobChunk。
/// 此类型在全局命名空间中，便于源代码生成器引用。
/// </summary>
public static unsafe partial class NativeJobScheduler
{
    // ======================== DLL 函数指针 ========================
    // 不使用 DllImport（NativeAOT 下可能路径解析失败），
    // 改为手动 LoadLibrary + GetProcAddress 获取函数指针。
    // 这样可以指定绝对路径加载 DLL，绕过所有搜索路径问题。

    private static IntPtr _nativeDll = IntPtr.Zero;

    // 函数指针（通过 GetProcAddress 获取）
    private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_Initialize;
    private static delegate* unmanaged[Cdecl]<void> _jobSystem_Shutdown;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> _jobSystem_Schedule;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, IntPtr, IntPtr> _jobSystem_ScheduleParallelForBatch;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr> _jobSystem_ScheduleFor;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_CompleteAndRelease;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _jobSystem_IsCompleted;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_ReleaseHandle;
    private static delegate* unmanaged[Cdecl]<IntPtr*, int, IntPtr> _jobSystem_CombineDependencies;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, IntPtr> _jobSystem_ScheduleChunkJob;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static unsafe void LoadNativeDll()
    {
        const string dllName = "NativeDll.dll";
        string cwd = Environment.CurrentDirectory;
        string assemblyDir = Path.GetDirectoryName(typeof(NativeJobScheduler).Assembly.Location);
        string entryDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);

        var paths = new List<string>();

        // 1. 从入口程序集（exe）所在目录查找（EntJoySample.exe 输出到 ../../bin）
        if (!string.IsNullOrEmpty(entryDir))
        {
            paths.Add(Path.Combine(entryDir, dllName));
            // 上一级 bin（当 exe 在子目录时）
            var parentOfEntry = Path.GetDirectoryName(entryDir);
            if (!string.IsNullOrEmpty(parentOfEntry))
                paths.Add(Path.Combine(parentOfEntry, "bin", dllName));
        }

        // 2. 从程序集所在目录查找（EntJoy.dll 所在位置）
        if (!string.IsNullOrEmpty(assemblyDir))
        {
            paths.Add(Path.Combine(assemblyDir, dllName));

            // 调试子目录（Godot 等宿主环境）
            paths.Add(Path.Combine(assemblyDir, "Debug", dllName));
            paths.Add(Path.Combine(assemblyDir, "Release", dllName));

            // 上两级 bin 目录（当 EntJoy.dll 在 bin\Debug\net8.0 时，../../bin = 输出目录）
            var up2Bin = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "bin"));
            paths.Add(Path.Combine(up2Bin, dllName));
        }

        // 3. 从 ../../bin 自动计算（相对于项目源路径）
        //    无论程序集在哪，从项目源代码所在结构推导输出目录
        {
            // 从 assemblyDir 逐层向上查找 src/NativeDll/
            string probe = string.IsNullOrEmpty(assemblyDir) ? cwd : assemblyDir;
            while (probe != null && probe.Length >= 3)
            {
                // 检查 src/NativeDll/NativeDll.vcxproj 是否存在
                var vcxproj = Path.Combine(probe, "src", "NativeDll", "NativeDll.vcxproj");
                if (File.Exists(vcxproj))
                {
                    // 找到了项目根目录，../../bin 就是输出目录
                    var outputBin = Path.GetFullPath(Path.Combine(probe, "..", "..", "bin"));
                    // 但注意这里的 probe 可能不是根目录，需要计算
                    // 而 vcxproj 中 <OutDir>..\..\bin</OutDir> 相对于 vcxproj 本身
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

        // 4. 从 CWD 查找
        {
            // Godot 编辑器运行游戏时，CWD = Godot 项目目录
            paths.Add(Path.Combine(cwd, ".godot", "mono", "temp", "bin", "Debug", dllName));
            paths.Add(Path.Combine(cwd, ".godot", "mono", "temp", "bin", "Release", dllName));
            paths.Add(Path.Combine(cwd, ".godot", "mono", "temp", "bin", "ExportDebug", "win-x64", dllName));
            paths.Add(Path.Combine(cwd, ".godot", "mono", "temp", "bin", "ExportRelease", "win-x64", dllName));
            paths.Add(Path.Combine(cwd, dllName));
            paths.Add(Path.Combine(cwd, "..", "bin", dllName));
            paths.Add(Path.Combine(cwd, "..", "..", "bin", dllName));
        }

        var fullPaths = paths.ToArray();

        IntPtr dllHandle = IntPtr.Zero;

        // 逐一尝试加载
        foreach (string path in fullPaths)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                try
                {
                    dllHandle = NativeLibrary.Load(fullPath);
                    if (dllHandle != IntPtr.Zero)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    // 记录加载失败信息，继续尝试下一个路径
                    Console.Error.WriteLine($"[NativeJobScheduler] Failed to load {fullPath}: {ex.Message}");
                }
            }
        }

        // 最后尝试默认搜索（如果 PATH 中有设置）
        if (dllHandle == IntPtr.Zero)
        {
            try
            {
                dllHandle = NativeLibrary.Load(dllName);
            }
            catch { }
        }

        if (dllHandle == IntPtr.Zero)
        {
            // 无法加载 DLL，打印详细信息帮助调试
            Console.Error.WriteLine($"[NativeJobScheduler] ERROR: Cannot find {dllName}. Searched:");
            foreach (string path in fullPaths)
            {
                string fullPath = Path.GetFullPath(path);
                Console.Error.WriteLine($"  - {fullPath}: {(File.Exists(fullPath) ? "EXISTS" : "NOT FOUND")}");
            }
            Console.Error.WriteLine($"  - CWD: {cwd}");
            Console.Error.WriteLine($"  - System PATH loading: attempted");
            return;
        }

        _nativeDll = dllHandle;

        // 获取所有函数指针
        _jobSystem_Initialize = (delegate* unmanaged[Cdecl]<int, void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_Initialize");
        _jobSystem_Shutdown = (delegate* unmanaged[Cdecl]<void>)
            NativeLibrary.GetExport(dllHandle, "JobSystem_Shutdown");
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
    }

    // ======================== 包装函数 ========================
    // 将函数指针调用包装成静态方法，保持原有 API 不变

    private static void JobSystem_Initialize(int numThreads)
        => _jobSystem_Initialize(numThreads);

    private static void JobSystem_Shutdown()
        => _jobSystem_Shutdown();

    private static IntPtr JobSystem_Schedule(
        IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, IntPtr dependency)
        => _jobSystem_Schedule(funcPtr, context, cleanupPtr, dependency);

    private static IntPtr JobSystem_ScheduleParallelForBatch(
        IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr,
        int length, int batchSize, IntPtr dependency)
        => _jobSystem_ScheduleParallelForBatch(funcPtr, context, cleanupPtr, length, batchSize, dependency);

    private static IntPtr JobSystem_ScheduleFor(
        IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr,
        int length, IntPtr dependency)
        => _jobSystem_ScheduleFor(funcPtr, context, cleanupPtr, length, dependency);

    private static void JobSystem_CompleteAndRelease(IntPtr handle)
        => _jobSystem_CompleteAndRelease(handle);

    private static int JobSystem_IsCompleted(IntPtr handle)
        => _jobSystem_IsCompleted(handle);

    private static void JobSystem_ReleaseHandle(IntPtr handle)
        => _jobSystem_ReleaseHandle(handle);

    private static IntPtr JobSystem_CombineDependencies(IntPtr[] handles, int count)
    {
        fixed (IntPtr* ptr = handles)
            return _jobSystem_CombineDependencies(ptr, count);
    }

    private static IntPtr JobSystem_ScheduleChunkJob(
        IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr,
        ChunkJobData* chunks, int chunkCount, IntPtr dependency)
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

    private sealed class DelegateCache
    {
        public readonly Delegate Delegate;
        public readonly IntPtr FuncPtr;
        public DelegateCache(Delegate del) { Delegate = del; FuncPtr = Marshal.GetFunctionPointerForDelegate(del); }
    }

    private static readonly CleanupFunc _cleanup = Cleanup;
    private static readonly IntPtr _cleanupPtr = Marshal.GetFunctionPointerForDelegate(_cleanup);

    // Chunk 作业的 GCHandle 管理（使用锁保护，因为可能被多个线程访问）
    private static readonly object _chunkGCHandlesLock = new();
    private static readonly List<GCHandle> _chunkGCHandles = new();

    // ======================== 公共接口 ========================
    public static void Initialize(int numThreads = 0) => JobSystem_Initialize(numThreads);
    public static void Shutdown() => JobSystem_Shutdown();

    /// <summary>调度 IJob（单次执行任务）</summary>
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
        catch
        {
            Marshal.FreeHGlobal(ctx);
            throw;
        }
    }

    /// <summary>调度 IJobFor（串行 for 循环）</summary>
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
        catch
        {
            Marshal.FreeHGlobal(ctx);
            throw;
        }
    }

    /// <summary>调度 IJobParallelFor（并行 for 循环，使用 batch 调度）</summary>
    public static NativeJobHandle ScheduleParallelFor<T>(ref T job, int length, int batchSize,
        NativeJobHandle? dependsOn = null)
        where T : struct, IJobParallelFor
    {
        if (length <= 0) return default;
        var ctx = AllocContext(ref job);
        try
        {
            var cache = GetOrCreateDelegateCache<T, BatchJobFunc>(() => CreateParallelForIndexCallback<T>());
            return new NativeJobHandle(
                JobSystem_ScheduleParallelForBatch(cache.FuncPtr, ctx, _cleanupPtr,
                    length, batchSize, dependsOn?.Handle ?? IntPtr.Zero));
        }
        catch
        {
            Marshal.FreeHGlobal(ctx);
            throw;
        }
    }

    /// <summary>调度 IJobParallelForBatch</summary>
    public static NativeJobHandle ScheduleParallelForBatch<T>(ref T job, int length, int batchSize,
        NativeJobHandle? dependsOn = null)
        where T : struct, IJobParallelForBatch
    {
        if (length <= 0) return default;
        var ctx = AllocContext(ref job);
        try
        {
            var cache = GetOrCreateDelegateCache<T, BatchJobFunc>(() => CreateParallelForBatchCallback<T>());
            return new NativeJobHandle(
                JobSystem_ScheduleParallelForBatch(cache.FuncPtr, ctx, _cleanupPtr,
                    length, batchSize, dependsOn?.Handle ?? IntPtr.Zero));
        }
        catch
        {
            Marshal.FreeHGlobal(ctx);
            throw;
        }
    }

    /// <summary>等待作业完成并释放句柄</summary>
    public static void Complete(ref NativeJobHandle h)
    {
        if (h.Handle != IntPtr.Zero)
        {
            JobSystem_CompleteAndRelease(h.Handle);
            h.Handle = IntPtr.Zero;
        }
    }

    /// <summary>检查作业是否已完成</summary>
    public static bool IsCompleted(NativeJobHandle h)
    {
        return h.Handle == IntPtr.Zero || JobSystem_IsCompleted(h.Handle) != 0;
    }

    /// <summary>释放句柄引用（不等待任务完成）</summary>
    public static void Release(NativeJobHandle h)
    {
        if (h.Handle != IntPtr.Zero)
            JobSystem_ReleaseHandle(h.Handle);
    }

    /// <summary>调度原始作业（已有函数指针和上下文）</summary>
    public static NativeJobHandle ScheduleRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, NativeJobHandle? dependsOn = null)
    {
        return new NativeJobHandle(
            JobSystem_Schedule(funcPtr, contextPtr, cleanupPtr, dependsOn?.Handle ?? IntPtr.Zero));
    }

    /// <summary>调度原始 For 作业</summary>
    public static NativeJobHandle ScheduleForRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, NativeJobHandle? dependsOn = null)
    {
        return new NativeJobHandle(
            JobSystem_ScheduleFor(funcPtr, contextPtr, cleanupPtr, length, dependsOn?.Handle ?? IntPtr.Zero));
    }

    /// <summary>调度原始并行 For 批处理作业</summary>
    public static NativeJobHandle ScheduleParallelForBatchRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, int batchSize, NativeJobHandle? dependsOn = null)
    {
        return new NativeJobHandle(
            JobSystem_ScheduleParallelForBatch(funcPtr, contextPtr, cleanupPtr, length, batchSize, dependsOn?.Handle ?? IntPtr.Zero));
    }

    /// <summary>合并多个依赖句柄</summary>
    public static NativeJobHandle CombineDependencies(params NativeJobHandle[] handles)
    {
        if (handles == null || handles.Length == 0) return default;
        var ptrs = new IntPtr[handles.Length];
        for (int i = 0; i < handles.Length; i++)
            ptrs[i] = handles[i].Handle;
        return new NativeJobHandle(JobSystem_CombineDependencies(ptrs, handles.Length));
    }

    // ======================== IJobChunk 调度 ========================

    /// <summary>
    /// 调度 IJobChunk，对每个匹配查询的 Chunk 并行执行 Execute。
    /// </summary>
    public static NativeJobHandle ScheduleChunk<T>(
        ref T job,
        EntityManager entityManager,
        QueryBuilder query,
        NativeJobHandle? dependsOn = null)
        where T : struct, IJobChunk
    {
        // 1. 收集匹配的 Chunk
        var chunkList = new List<Chunk>(128);
        for (int i = 0; i < entityManager.ArchetypeCount; i++)
        {
            var arch = entityManager.Archetypes[i];
            if (arch != null && arch.IsMatch(query))
            {
                foreach (var c in arch.GetChunks())
                    if (c.EntityCount > 0)
                        chunkList.Add(c);
            }
        }

        int chunkCount = chunkList.Count;
        if (chunkCount == 0) return default;

        // 2. 准备 AllEnabled 组件索引（用于 enable mask 组合）
        var allEnabledTypes = query.AllEnabled;
        bool hasEnabledFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;

        // 3. 分配所有非托管内存
        var chunksPtr = (ChunkJobData*)Marshal.AllocHGlobal(chunkCount * sizeof(ChunkJobData));

        int gcHandleStartIndex;
        lock (_chunkGCHandlesLock)
        {
            gcHandleStartIndex = _chunkGCHandles.Count;
        }

        for (int ci = 0; ci < chunkCount; ci++)
        {
            var chunk = chunkList[ci];
            var arch = chunk.Archetype;

            // 固定 Chunk 对象防止 GC
            var gch = GCHandle.Alloc(chunk, GCHandleType.WeakTrackResurrection);
            lock (_chunkGCHandlesLock)
            {
                _chunkGCHandles.Add(gch);
            }

            int compCount = chunk.ComponentCount;

            // 分配每个 Chunk 的辅助数组
            var compPtrs = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var compSizes = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));
            var bitmaps = (void**)Marshal.AllocHGlobal(compCount * sizeof(void*));
            var typeIndices = (int*)Marshal.AllocHGlobal(compCount * sizeof(int));

            // 填充组件信息
            for (int c = 0; c < compCount; c++)
            {
                compPtrs[c] = (void*)chunk.GetComponentArrayPointer(c);
                compSizes[c] = arch.Types[c].Size;
                bitmaps[c] = chunk.GetEnableBitMapPointer(c);
                typeIndices[c] = c;
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
                chunkHandle = (IntPtr)gch
            };
        }

        // 4. 创建上下文包
        var contextBlock = CreateChunkContextBlock(
            ref job, chunksPtr, chunkCount,
            hasEnabledFilter, allEnabledTypes,
            gcHandleStartIndex);

        try
        {
            var cache = GetOrCreateDelegateCache<T, ChunkJobFuncDelegate>(
                () => CreateChunkCallback<T>());
            var handle = JobSystem_ScheduleChunkJob(
                cache.FuncPtr,
                contextBlock,
                _chunkCleanupPtr,
                chunksPtr,
                chunkCount,
                dependsOn?.Handle ?? IntPtr.Zero);
            return new NativeJobHandle(handle);
        }
        catch
        {
            ChunkCleanup(contextBlock);
            throw;
        }
    }

    // ======================== 内部实现 ========================

    private static readonly CleanupFunc _chunkCleanup = ChunkCleanup;
    private static readonly IntPtr _chunkCleanupPtr = Marshal.GetFunctionPointerForDelegate(_chunkCleanup);

    private unsafe static IntPtr CreateChunkContextBlock<T>(
        ref T job,
        ChunkJobData* chunksPtr,
        int chunkCount,
        bool hasEnabledFilter,
        ComponentType[] allEnabledTypes,
        int gcHandleStartIndex) where T : struct
    {
        int jobSize = Unsafe.SizeOf<T>();
        int headerSize = Unsafe.SizeOf<ChunkContextHeader>();

        int typesDataSize = 0;
        int[] typeHashes = null;
        if (hasEnabledFilter && allEnabledTypes != null)
        {
            typeHashes = new int[allEnabledTypes.Length];
            for (int i = 0; i < allEnabledTypes.Length; i++)
                typeHashes[i] = allEnabledTypes[i].GetHashCode();
            typesDataSize = allEnabledTypes.Length * sizeof(int);
        }

        int totalSize = headerSize + jobSize + typesDataSize;
        var block = Marshal.AllocHGlobal(totalSize);
        Unsafe.InitBlockUnaligned((void*)block, 0, (uint)totalSize);

        // 写入 header
        var header = (ChunkContextHeader*)block;
        header->chunkCount = chunkCount;
        header->hasEnabledFilter = hasEnabledFilter ? 1 : 0;
        header->gcHandleStartIndex = gcHandleStartIndex;
        header->chunksPtr = (IntPtr)chunksPtr;
        header->cleanupInProgress = 0;

        if (hasEnabledFilter && typeHashes != null)
        {
            var typeHashPtr = (int*)((byte*)block + headerSize);
            for (int i = 0; i < typeHashes.Length; i++)
                typeHashPtr[i] = typeHashes[i];
            header->allEnabledCount = typeHashes.Length;
            header->queryAllEnabledTypes = (IntPtr)typeHashPtr;
        }
        else
        {
            header->allEnabledCount = 0;
            header->queryAllEnabledTypes = IntPtr.Zero;
        }

        // 写入 job 拷贝
        byte* jobPtr = (byte*)block + headerSize + typesDataSize;
        Unsafe.CopyBlockUnaligned(jobPtr, Unsafe.AsPointer(ref job), (uint)jobSize);

        return block;
    }

    private unsafe static void ChunkCleanup(IntPtr contextBlock)
    {
        if (contextBlock == IntPtr.Zero) return;

        var header = (ChunkContextHeader*)contextBlock;

        // 防止重复清理
        if (Interlocked.CompareExchange(ref header->cleanupInProgress, 1, 0) != 0)
            return;

        int chunkCount = header->chunkCount;
        int gcHandleStartIndex = header->gcHandleStartIndex;
        var chunksPtr = (ChunkJobData*)header->chunksPtr;

        // 释放 GCHandle
        if (chunksPtr != null)
        {
            lock (_chunkGCHandlesLock)
            {
                int count = Math.Min(chunkCount, _chunkGCHandles.Count - gcHandleStartIndex);
                for (int i = 0; i < chunkCount && (gcHandleStartIndex + i) < _chunkGCHandles.Count; i++)
                {
                    int index = gcHandleStartIndex + i;
                    if (_chunkGCHandles[index].IsAllocated)
                    {
                        _chunkGCHandles[index].Free();
                        _chunkGCHandles[index] = default;
                    }
                }
            }
        }

        // 释放每个 Chunk 内部的辅助数组
        for (int i = 0; i < chunkCount; i++)
        {
            if (chunksPtr != null)
            {
                var cd = chunksPtr[i];
                if (cd.componentArrays != null)
                    Marshal.FreeHGlobal((IntPtr)cd.componentArrays);
                if (cd.componentSizes != null)
                    Marshal.FreeHGlobal((IntPtr)cd.componentSizes);
                if (cd.enableBitMaps != null)
                    Marshal.FreeHGlobal((IntPtr)cd.enableBitMaps);
                if (cd.componentTypeIndices != null)
                    Marshal.FreeHGlobal((IntPtr)cd.componentTypeIndices);
            }
        }

        // 释放 chunks 数组
        if (chunksPtr != null)
            Marshal.FreeHGlobal((IntPtr)chunksPtr);

        // 释放 contextBlock 本身
        Marshal.FreeHGlobal(contextBlock);
    }

    // ======================== 回调工厂 ========================

    private unsafe static JobFunc CreateJobCallback<T>() where T : struct, IJob
    {
        return (IntPtr ctx) => Unsafe.AsRef<T>((void*)ctx).Execute();
    }

    private unsafe static IndexJobFunc CreateForCallback<T>() where T : struct, IJobFor
    {
        return (IntPtr ctx, int i) => Unsafe.AsRef<T>((void*)ctx).Execute(i);
    }

    private unsafe static BatchJobFunc CreateParallelForIndexCallback<T>() where T : struct, IJobParallelFor
    {
        return (IntPtr ctx, int start, int count) =>
        {
            ref var job = ref Unsafe.AsRef<T>((void*)ctx);
            int end = start + count;
            for (int i = start; i < end; i++)
                job.Execute(i);
        };
    }

    private unsafe static BatchJobFunc CreateParallelForBatchCallback<T>() where T : struct, IJobParallelForBatch
    {
        return (IntPtr ctx, int start, int count) =>
        {
            ref var job = ref Unsafe.AsRef<T>((void*)ctx);
            job.Execute(start, count);
        };
    }

    private unsafe static ChunkJobFuncDelegate CreateChunkCallback<T>() where T : struct, IJobChunk
    {
        return (IntPtr ctx, ChunkJobData* cd) =>
        {
            var header = (ChunkContextHeader*)ctx;
            int headerSize = Unsafe.SizeOf<ChunkContextHeader>();
            int typesDataSize = header->allEnabledCount * sizeof(int);

            // 恢复 job 引用
            byte* jobPtr = (byte*)ctx + headerSize + typesDataSize;
            ref var job = ref Unsafe.AsRef<T>(jobPtr);

            // 恢复 Chunk 对象
            var chunkHandle = cd->chunkHandle;
            Chunk chunk = null;
            if (chunkHandle != IntPtr.Zero)
            {
                try
                {
                    var gch = GCHandle.FromIntPtr(chunkHandle);
                    if (gch.IsAllocated && gch.Target is Chunk c)
                        chunk = c;
                }
                catch
                {
                    // GCHandle 已释放，chunk 不可用
                }
            }

            if (chunk == null) return;

            // 构建 enabled mask
            if (header->hasEnabledFilter != 0 && header->allEnabledCount > 0)
            {
                int* typeHashArray = (int*)header->queryAllEnabledTypes;
                int ulongCount = (cd->entityCount + 63) / 64;

                ulong* combinedMask;
                const int maxStackAlloc = 256;
                if (ulongCount <= maxStackAlloc)
                {
                    var u = stackalloc ulong[ulongCount];
                    combinedMask = u;
                }
                else
                {
                    combinedMask = TempBuffer.GetBuffer(ulongCount);
                }

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
                                if (!firstFound)
                                {
                                    Buffer.MemoryCopy(bitmap, combinedMask,
                                        ulongCount * 8, ulongCount * 8);
                                    firstFound = true;
                                }
                                else
                                {
                                    for (int b = 0; b < ulongCount; b++)
                                        combinedMask[b] &= bitmap[b];
                                }
                            }
                            break;
                        }
                    }
                }

                if (firstFound)
                {
                    var enabledMask = new ChunkEnabledMask(combinedMask, cd->entityCount);
                    job.Execute(new ArchetypeChunk(chunk), enabledMask);
                }
                else
                {
                    job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
                }
            }
            else
            {
                job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
            }
        };
    }

    // ======================== 辅助方法 ========================

    private static DelegateCache GetOrCreateDelegateCache<T, TDelegate>(Func<TDelegate> factory)
        where TDelegate : Delegate
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
        IntPtr dataPtr = Marshal.AllocHGlobal(size);
        Unsafe.CopyBlockUnaligned((void*)dataPtr, Unsafe.AsPointer(ref job), (uint)size);
        return dataPtr;
    }

    private unsafe static void Cleanup(IntPtr dataPtr)
    {
        if (dataPtr != IntPtr.Zero)
            Marshal.FreeHGlobal(dataPtr);
    }
}
