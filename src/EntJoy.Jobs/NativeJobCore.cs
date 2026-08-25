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

namespace EntJoy.JobSystem
{
    /// <summary>
    /// 零 ECS 依赖的原生调度执行引擎。Jobs 的 <see cref="NativeJobScheduler"/>（门面）
    /// 与 ECS 的 <c>NativeEcsScheduler</c>（chunk 调度）共用。所有被两者共享的可变状态
    /// （委托缓存、上下文池、异常、ThreadStatic、纯 P/Invoke 函数指针）必须独占于此。
    /// </summary>
    internal static unsafe class NativeJobCore
    {
        // ======================== 委托类型 ========================
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void JobFunc(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void IndexJobFunc(IntPtr context, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void BatchJobFunc(IntPtr context, int startIndex, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void CleanupFunc(IntPtr context);

        // ======================== 委托缓存 ========================
        internal static readonly ConcurrentDictionary<Type, DelegateCache> _delegateCache = new();
        internal sealed class DelegateCache { public readonly Delegate Delegate; public readonly IntPtr FuncPtr; public DelegateCache(Delegate del) { Delegate = del; FuncPtr = Marshal.GetFunctionPointerForDelegate(del); } }

        private static readonly CleanupFunc _cleanup = Cleanup;
        private static readonly IntPtr _cleanupPtr = Marshal.GetFunctionPointerForDelegate(_cleanup);
        internal static readonly CleanupFunc _managedCleanup = ManagedCleanup;
        internal static readonly IntPtr _managedCleanupPtr = Marshal.GetFunctionPointerForDelegate(_managedCleanup);

        internal static IntPtr CleanupPtr => _cleanupPtr;
        internal static IntPtr ManagedCleanupPtr => _managedCleanupPtr;

        // ======================== 执行深度 / 当前 batch ========================
        [ThreadStatic] private static int _jobExecutionDepth;
        [ThreadStatic] private static ulong _currentBatchId;

        internal static bool IsExecutingJob => _jobExecutionDepth > 0;
        internal static ulong CurrentBatchId => _currentBatchId;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void EnterJobExecution() => _jobExecutionDepth++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ExitJobExecution() => _jobExecutionDepth--;

        // batchId → Job 名，供 native Dear ImGui Timeline 显示 Job 名。GUI 线程只读并发字典，无锁安全。
        private static readonly ConcurrentDictionary<ulong, string> _batchIdToJobName = new();
        // 仅当调试面板（LaunchDebuggerGUI）开启后才记录 batchId→名字，避免影响正常调度热路径
        private static volatile bool _debugNameCaptureEnabled;

        // 记录当前 batch 对应的 Job 名（托管回调路径：执行线程上 native 已 set batch id）。
        internal static void RegisterCurrentBatchJobName(string name)
        {
            if (!_debugNameCaptureEnabled) return;
            ulong batchId = _currentBatchId;
            if (batchId == 0) return;
            _batchIdToJobName[batchId] = name;
        }

        // 记录某个已调度 handle 的 Job 名（原生直跑路径：调度返回后立读 diagnosticId）。
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

        // 仅调试面板开启后才记录 batchId→Job名，避免影响正常调度热路径。
        internal static void SetDebugNameCapture(bool enabled) => _debugNameCaptureEnabled = enabled;

        // ======================== DLL 函数指针（纯 P/Invoke） ========================
        private static IntPtr _nativeDll = IntPtr.Zero;
        private static int _shutdownRequested;

        internal static IntPtr NativeDllHandle => _nativeDll;

        private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_Initialize;
        private static delegate* unmanaged[Cdecl]<int> _jobSystem_GetWorkerCount;
        private static delegate* unmanaged[Cdecl]<void> _jobSystem_Shutdown;
        private static delegate* unmanaged[Cdecl]<void> _jobSystem_PrewakeWorkers;
        private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_ConfigureTilesPerWorker;
        private static delegate* unmanaged[Cdecl]<int, int, int, void> _jobSystem_ConfigureGuided;
        private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_SetJobCostCacheEnabled;
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
        private static delegate* unmanaged[Cdecl]<NativeJobSystemStats*, void> _jobSystem_GetStats;
        private static delegate* unmanaged[Cdecl]<uint> _jobSystem_GetStatsSize;
        private static delegate* unmanaged[Cdecl]<void> _jobSystem_ResetStats;
        private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_SetTimingDiagnostics;
        private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_SetMainThreadAssist;
        private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_SetWorkerAffinity;
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
            string assemblyDir = Path.GetDirectoryName(typeof(NativeJobCore).Assembly.Location);
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
            _jobSystem_SetJobCostCacheEnabled = (delegate* unmanaged[Cdecl]<int, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_SetJobCostCacheEnabled");
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
            _jobSystem_GetStats = (delegate* unmanaged[Cdecl]<NativeJobSystemStats*, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_GetStats");
            _jobSystem_GetStatsSize = (delegate* unmanaged[Cdecl]<uint>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_GetStatsSize");
            _jobSystem_ResetStats = (delegate* unmanaged[Cdecl]<void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_ResetStats");
            _jobSystem_SetTimingDiagnostics = (delegate* unmanaged[Cdecl]<int, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_SetTimingDiagnostics");
            _jobSystem_SetMainThreadAssist = (delegate* unmanaged[Cdecl]<int, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_SetMainThreadAssist");
            _jobSystem_SetWorkerAffinity = (delegate* unmanaged[Cdecl]<int, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_SetWorkerAffinity");
            _jobSystem_LaunchGUI = (delegate* unmanaged[Cdecl]<void>)
                NativeLibrary.GetExport(dllHandle, "JobDebuggerGUI_Launch");
            _jobSystem_RecordDirectCall = (delegate* unmanaged[Cdecl]<byte*, uint, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_RecordDirectCall");
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
        private static void TryLoadNativeTranspiled(string nativeDllPath)
        {
            const string generatedDllName = "NativeTranspiled.dll";
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeJobCore).Assembly, (libName, assembly, searchPath) =>
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
                    return IntPtr.Zero;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void EnsureNativeLoaded()
        {
            if (!IsNativeLoaded)
                ThrowNativeNotLoaded();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNativeNotLoaded()
        {
            throw new InvalidOperationException("NativeDll.dll is not loaded. Ensure NativeDll.dll is copied next to the executable or Godot output directory.");
        }

        internal static void JobSystem_Initialize(int numThreads)
        {
            EnsureNativeLoaded();
            _jobSystem_Initialize(numThreads);
        }

        internal static void JobSystem_Shutdown()
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_Shutdown == null) return;
            _jobSystem_Shutdown();
        }

        internal static void JobSystem_PrewakeWorkers()
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_PrewakeWorkers == null) return;
            _jobSystem_PrewakeWorkers();
        }

        internal static void JobSystem_ConfigureTilesPerWorker(int tilesPerWorker)
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_ConfigureTilesPerWorker == null) return;
            _jobSystem_ConfigureTilesPerWorker(tilesPerWorker);
        }

        internal static void JobSystem_ConfigureGuided(int enabled, int k, int floor)
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_ConfigureGuided == null) return;
            _jobSystem_ConfigureGuided(enabled, k, floor);
        }

        internal static void JobSystem_SetJobCostCacheEnabled(int enabled)
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_SetJobCostCacheEnabled == null) return;
            _jobSystem_SetJobCostCacheEnabled(enabled);
        }

        internal static int JobSystem_GetWorkerCount()
        {
            EnsureNativeLoaded();
            return _jobSystem_GetWorkerCount();
        }

        internal static void JobSystem_RegisterPersistentAllocator(delegate* unmanaged[Cdecl]<int, void*> alloc, delegate* unmanaged[Cdecl]<void*, void> free)
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_RegisterPersistentAllocator == null) return;
            _jobSystem_RegisterPersistentAllocator(alloc, free);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IntPtr JobSystem_Schedule(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, IntPtr dependency)
        {
            EnsureNativeLoaded();
            return _jobSystem_Schedule(funcPtr, context, cleanupPtr, dependency);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IntPtr JobSystem_ScheduleParallelForBatch(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, int length, int batchSize, IntPtr dependency)
        {
            EnsureNativeLoaded();
            return _jobSystem_ScheduleParallelForBatch(funcPtr, context, cleanupPtr, length, batchSize, dependency);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IntPtr JobSystem_ScheduleFor(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, int length, IntPtr dependency)
        {
            EnsureNativeLoaded();
            return _jobSystem_ScheduleFor(funcPtr, context, cleanupPtr, length, dependency);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void JobSystem_Complete(IntPtr handle)
        {
            EnsureNativeLoaded();
            _jobSystem_Complete(handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong JobSystem_GetDiagnosticBatchId(IntPtr handle)
        {
            EnsureNativeLoaded();
            return _jobSystem_GetDiagnosticBatchId(handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void JobSystem_RetainHandle(IntPtr handle)
        {
            EnsureNativeLoaded();
            _jobSystem_RetainHandle(handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int JobSystem_IsCompleted(IntPtr handle)
        {
            EnsureNativeLoaded();
            return _jobSystem_IsCompleted(handle);
        }

        internal static void JobSystem_ReleaseHandle(IntPtr handle)
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_ReleaseHandle == null) return;
            _jobSystem_ReleaseHandle(handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IntPtr JobSystem_CombineDependencies(IntPtr[] handles, int count)
        {
            EnsureNativeLoaded();
            fixed (IntPtr* ptr = handles) return _jobSystem_CombineDependencies(ptr, count);
        }

        internal static NativeJobSystemStats JobSystem_GetStats()
        {
            EnsureNativeLoaded();
            NativeJobSystemStats stats = default;
            _jobSystem_GetStats(&stats);
            return stats;
        }

        /// <summary>布局防御：校验 C#/C++ 统计结构体字节数一致（防 GetStats 越界写）。
        /// 新增统计字段时必须两处同步。</summary>
        internal static void ValidateStatsLayout()
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_GetStatsSize == null) return;
            uint nativeSize = _jobSystem_GetStatsSize();
            int managedSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeJobSystemStats>();
            if (nativeSize != managedSize)
            {
                throw new InvalidOperationException(
                    $"NativeJobSystemStats 布局不匹配：C++={nativeSize}B C#={managedSize}B。请同步 Exports.h 与 NativeJobScheduler.cs 字段。");
            }
        }
        internal static void JobSystem_ResetStats()
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_ResetStats == null) return;
            _jobSystem_ResetStats();
        }
        internal static void JobSystem_SetTimingDiagnostics(bool enabled)
        {
            EnsureNativeLoaded();
            _jobSystem_SetTimingDiagnostics(enabled ? 1 : 0);
        }
        internal static void JobSystem_SetMainThreadAssist(bool enabled)
        {
            EnsureNativeLoaded();
            _jobSystem_SetMainThreadAssist(enabled ? 1 : 0);
        }
        internal static void JobSystem_SetWorkerAffinity(bool enabled)
        {
            EnsureNativeLoaded();
            _jobSystem_SetWorkerAffinity(enabled ? 1 : 0);
        }

        internal static void JobSystem_LaunchGUI()
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_LaunchGUI == null) return;
            _jobSystem_LaunchGUI();
        }

        internal static void JobSystem_RecordDirectCall(byte* name, uint tiles)
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_RecordDirectCall == null) return;
            _jobSystem_RecordDirectCall(name, tiles);
        }

        internal static ulong JobSystem_BeginDirectCall(byte* name, uint tiles)
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_BeginDirectCall == null) return 0;
            return _jobSystem_BeginDirectCall(name, tiles);
        }

        internal static void JobSystem_EndDirectCall(ulong id)
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_EndDirectCall == null) return;
            _jobSystem_EndDirectCall(id);
        }

        internal static void Profiler_SetEnabled(int enabled) => _profiler_SetEnabled(enabled);
        internal static int Profiler_IsEnabled() => _profiler_IsEnabled();
        internal static unsafe int Profiler_ReadAll(ProfilerEntry[] buffer, int maxCount)
        {
            if (buffer == null || buffer.Length == 0) return 0;
            int count = Math.Min(maxCount, buffer.Length);
            fixed (ProfilerEntry* ptr = buffer) return _profiler_ReadAll(ptr, count);
        }
        internal static void Profiler_Clear() => _profiler_Clear();

        internal static void Trace_SetEnabled(bool enabled) => _trace_SetEnabled(enabled ? 1 : 0);
        internal static bool Trace_IsEnabled() => _trace_IsEnabled() != 0;
        internal static ulong Trace_DroppedEvents() => _trace_DroppedEvents();
        internal static void Trace_Clear() => _trace_Clear();
        internal static unsafe int Trace_ReadAll(NativeTraceEvent[] buffer, int maxCount)
        {
            if (buffer == null || buffer.Length == 0 || maxCount <= 0) return 0;
            int count = Math.Min(maxCount, buffer.Length);
            fixed (NativeTraceEvent* ptr = buffer) return _trace_ReadAll(ptr, count);
        }

        // ======================== batch id / 名字解析回调 ========================
        // native 每 job 执行窗口调 SetCurrentBatchId 写线程局部当前 batch。
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static void SetCurrentBatchId(ulong batchId) => _currentBatchId = batchId;

        internal static void RegisterCurrentBatchIdCallback()
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_RegisterCurrentBatchId == null) return;
            _jobSystem_RegisterCurrentBatchId(&SetCurrentBatchId);
            if (_jobSystem_RegisterNameResolver != null)
                _jobSystem_RegisterNameResolver(&ResolveBatchJobName, &ClearBatchJobNames);
        }

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

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void ClearBatchJobNames()
        {
            _batchIdToJobName.Clear();
            _debugNameCaptureEnabled = false;
        }

        // ======================== 关闭 ========================
        internal static void SafeShutdown()
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_Shutdown == null)
                return;
            if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
                return;
            DumpTimingDiagnosticsIfRequested();
            JobSystem_Shutdown();
        }

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

        // ======================== 上下文内存池 ========================
        internal static class ContextPool
        {
            private const int BucketShift = 6;
            private const int MaxBucket = 64;
            private static readonly ConcurrentStack<IntPtr>[] _buckets = new ConcurrentStack<IntPtr>[MaxBucket];

            private static int GetBucketIndex(int size)
            {
                int idx = (size + (1 << BucketShift) - 1) >> BucketShift;
                return idx >= MaxBucket ? -1 : idx;
            }

            private static int GetBucketAllocSize(int idx)
            {
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
            return _delegateCache.GetOrAdd(typeof(T), _ => new DelegateCache(factory()));
        }

        /// <summary>
        /// 自动批处理回调（per 泛型 T 缓存一次）：若 T 同时实现 IJobParallelForBatch，则调度用批回调
        /// （回调内一次 Execute(start,count)），否则退回逐元素 Execute(i)。减少轻任务上逐元素接口调度开销。
        /// </summary>
        private static class AutoParallelForCallback<T>
            where T : struct, IJobParallelFor
        {
            public static readonly DelegateCache Cache =
                GetOrCreateDelegateCache<T, BatchJobFunc>(() => CreateParallelForIndexCallback<T>());

            public static DelegateCache GetCache() => Cache;
        }

        internal static DelegateCache GetAutoParallelForCache<T>() where T : struct, IJobParallelFor
            => AutoParallelForCallback<T>.GetCache();

        // 按 batchId 归集的 Job 异常。Complete(h) 只抛本 batch 的异常；batch 0 为未归属异常。
        private static readonly object _exceptionLock = new();
        private static Dictionary<ulong, List<ExceptionDispatchInfo>> _recordedJobExceptions = new();
        private const int MaxRecordedJobExceptionsPerBatch = 16;
        private static int _droppedJobExceptionCount;

        internal static void RecordJobException(ulong batchId, Exception exception)
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

        /// <summary>抛出所有已记录的 Job 异常（跨所有 batch，含未归属的 batch 0）。</summary>
        internal static void FlushRecordedExceptions()
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

        internal static void ThrowRecordedJobExceptions(ulong batchId)
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
        internal static bool JobHasManagedReferences<T>() where T : struct
            => RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        private sealed class ManagedJobBox<T> where T : struct
        {
            public T Job;

            public ManagedJobBox(T job)
            {
                Job = job;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe static ref T GetJob<T>(IntPtr ctx, bool managedContext) where T : struct
        {
            if (managedContext)
            {
                return ref GetManagedJob<T>(ctx);
            }

            return ref Unsafe.AsRef<T>((void*)ctx);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref T GetManagedJob<T>(IntPtr ctx) where T : struct
        {
            var handle = GCHandle.FromIntPtr(ctx);
            var box = (ManagedJobBox<T>)handle.Target;
            return ref box.Job;
        }

        internal static IntPtr AllocManagedContext<T>(ref T job) where T : struct
        {
            var handle = GCHandle.Alloc(new ManagedJobBox<T>(job), GCHandleType.Normal);
            return GCHandle.ToIntPtr(handle);
        }

        internal static void ManagedCleanup(IntPtr ctx)
        {
            if (ctx == IntPtr.Zero) return;
            var handle = GCHandle.FromIntPtr(ctx);
            if (handle.IsAllocated) handle.Free();
        }

        internal unsafe static IntPtr AllocContext<T>(ref T job) where T : struct
        {
            int size = Unsafe.SizeOf<T>();
            int totalSize = size + sizeof(int);
            IntPtr dataPtr = ContextPool.Rent(totalSize);
            *(int*)dataPtr = size;
            byte* jobPtr = (byte*)dataPtr + sizeof(int);
            Unsafe.CopyBlockUnaligned(jobPtr, Unsafe.AsPointer(ref job), (uint)size);
            return (IntPtr)jobPtr;
        }

        internal unsafe static void Cleanup(IntPtr dataPtr)
        {
            if (dataPtr == IntPtr.Zero) return;
            int size = *(int*)((byte*)dataPtr - sizeof(int));
            ContextPool.Return((IntPtr)((byte*)dataPtr - sizeof(int)), size + sizeof(int));
        }

        // ======================== 回调工厂 ========================
        internal unsafe static JobFunc CreateJobCallback<T>() where T : struct, IJob
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

        internal unsafe static IndexJobFunc CreateForCallback<T>() where T : struct, IJobFor
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

        internal unsafe static BatchJobFunc CreateParallelForIndexCallback<T>() where T : struct, IJobParallelFor
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

        internal unsafe static BatchJobFunc CreateParallelForBatchCallback<T>() where T : struct, IJobParallelForBatch
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

        // ======================== 低级原语 ========================
        internal static NativeJobHandle ScheduleRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, NativeJobHandle? dependsOn = null)
        {
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            return new NativeJobHandle(JobSystem_Schedule(funcPtr, contextPtr, cleanupPtr, dependencyLease.Handle));
        }

        internal static NativeJobHandle ScheduleForRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, NativeJobHandle? dependsOn = null)
        {
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            return new NativeJobHandle(JobSystem_ScheduleFor(funcPtr, contextPtr, cleanupPtr, length, dependencyLease.Handle));
        }

        internal static NativeJobHandle ScheduleParallelForBatchRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, int batchSize, NativeJobHandle? dependsOn = null)
        {
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            return new NativeJobHandle(JobSystem_ScheduleParallelForBatch(funcPtr, contextPtr, cleanupPtr, length, batchSize, dependencyLease.Handle));
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
    }
}
