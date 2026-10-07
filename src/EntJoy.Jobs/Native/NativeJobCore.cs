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
using EntJoy.Collections;

namespace EntJoy.JobSystem
{
    /// <summary>
    /// 零依赖的原生调度执行引擎。NativeJobScheduler 和 ChunkJobScheduler 共用。
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

        // 显式批描述符（Exports.h JobBatchDesc 一一对应，Sequential 布局）
        // kind: 0=IJob 1=IJobFor 2=IJobParallelFor
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct NativeJobBatchDesc
        {
            public byte Kind;
            public byte R0, R1, R2;
            public IntPtr Func;
            public IntPtr Context;
            public IntPtr Cleanup;
            public IntPtr Dependency;
            public int Length;
            public int BatchSize;
        }

        // ======================== 委托缓存 ========================
        internal static readonly ConcurrentDictionary<Type, DelegateCache> _delegateCache = new();
        internal sealed class DelegateCache { public readonly Delegate Delegate; public readonly IntPtr FuncPtr; public DelegateCache(Delegate del) { Delegate = del; FuncPtr = Marshal.GetFunctionPointerForDelegate(del); } }

        private static readonly CleanupFunc _cleanup = Cleanup;
        private static readonly IntPtr _cleanupPtr = Marshal.GetFunctionPointerForDelegate(_cleanup);
        internal static readonly CleanupFunc _managedCleanup = ManagedCleanup;
        internal static readonly IntPtr _managedCleanupPtr = Marshal.GetFunctionPointerForDelegate(_managedCleanup);

        internal static IntPtr CleanupPtr => _cleanupPtr;
        internal static IntPtr ManagedCleanupPtr => _managedCleanupPtr;

        // ── 共享的 FreeHGlobal cleanup thunk ──
        // 生成代码统一引用它（不是每个 job 各造一个 lambda + thunk）。
        // ⚠ 刻意不复用 `CleanupPtr`：后者会释放安全句柄账本并读 ctx 前的尺寸前缀，语义不同。
        private static readonly CleanupFunc _freeHGlobalCleanup = FreeHGlobalCleanup;
        private static readonly IntPtr _sharedFreeHGlobalCleanupPtr =
            Marshal.GetFunctionPointerForDelegate(_freeHGlobalCleanup);

        private static void FreeHGlobalCleanup(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }

        /// <summary>共享的 `Marshal.FreeHGlobal` cleanup 指针（生成代码统一用它）。</summary>
        internal static IntPtr SharedFreeHGlobalCleanupPtr => _sharedFreeHGlobalCleanupPtr;

        // ── 重载回调注册表 ──
        // 注册表（字段写入器 / adapter 指针 / 批表绑名）是按 `Type` 键存的指针快照，换句柄后必须重跑
        // 才会指向新模块；生成代码的 `EnsureNativeJobRegistrations()` 把自己登记在这里。
        private static readonly List<Action> _reloadCallbacks = new();

        /// <summary>登记一个"换过 NativeTranspiled 句柄后要重跑"的回调（幂等：同一方法只登记一次）。</summary>
        internal static void RegisterReloadCallback(Action callback)
        {
            if (callback == null) return;
            lock (_reloadCallbacks)
            {
                if (!_reloadCallbacks.Contains(callback)) _reloadCallbacks.Add(callback);
            }
        }

        /// <summary>当前登记的重载回调数（诊断/验收用）。</summary>
        internal static int ReloadCallbackCount
        {
            get { lock (_reloadCallbacks) return _reloadCallbacks.Count; }
        }

        /// <summary>
        /// 重跑全部重载回调（在**新句柄已绑定之后**调用）。
        /// 先快照再回调：回调里可能再次 `RegisterReloadCallback`（生成代码每次都会调）⇒ 不能在锁内调用。
        /// </summary>
        internal static void RunReloadCallbacks()
        {
            Action[] snapshot;
            lock (_reloadCallbacks) snapshot = _reloadCallbacks.ToArray();
            foreach (var callback in snapshot) callback();
        }

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
        // 生成内核（NativeTranspiled）的当前句柄；取 adapter 指针必须基于它（`[DllImport]` 的解析结果
        // 被运行时按 (程序集, 库名) 缓存，换不掉）。
        private static IntPtr _nativeTranspiledDll = IntPtr.Zero;
        private static int _shutdownRequested;
        // ABI 2: JobSystem_Initialize now returns an int status code.
        // ABI 3: 删除 6 个死 stats 字段（stats 结构体布局已变）⇒ 旧的 NativeDll.dll 必须被拒绝，
        //        否则会按错位偏移静默读错诊断值。与 Exports.cpp 的 JobSystem_GetAbiVersion 同步。
        private const uint ExpectedAbiVersion = 3;

        internal static IntPtr NativeDllHandle => _nativeDll;

        /// <summary>等所有已提交 job 跑完并物理退役（不关 worker）。false = 当前 NativeDll 没有该导出。</summary>
        internal static bool DrainAllCore()
        {
            if (_jobSystem_DrainAll == null) return false;
            _jobSystem_DrainAll();
            return true;
        }

        /// <summary>该 NativeDll 是否提供排空导出。</summary>
        internal static bool HasDrainAll => _jobSystem_DrainAll != null;

        // ── 组件布局指纹（守卫）──
        // 只换 native 时 C# 组件布局不变 ⇒ "改组件定义后只重载 native" 会让两侧对同一块内存做不同解释。
        // 故本次构建把布局假设写成 DLL 旁的 `<dll>.layout.json`，重载前逐类型比对，不一致即拒绝。
        // 键 = `程序集名|类型全名`（同名类型可来自不同程序集，裸名会互相覆盖）。
        private static readonly Dictionary<string, ulong> _componentLayoutHashes = new(StringComparer.Ordinal);
        private static readonly object _componentLayoutLock = new();

        /// <summary>登记一个组件的布局哈希（由 `ComponentMetaRegistry.Register` 在模块初始化期逐组件调用）。</summary>
        internal static void RecordComponentLayout(Assembly owner, string typeName, ulong layoutHash)
        {
            if (string.IsNullOrEmpty(typeName)) return;
            string asm = owner?.GetName().Name ?? "";
            string key = asm + "|" + typeName;
            lock (_componentLayoutLock)
            {
                if (_componentLayoutHashes.Count == 0)
                    Console.Error.WriteLine("[LAYOUT] component layout fingerprint: recording (guard armed)");
                _componentLayoutHashes[key] = layoutHash;
            }
        }

        /// <summary>当前已加载程序集的组件布局指纹：逐类型哈希 **异或**（与顺序无关）。0 = 没有组件。</summary>
        internal static ulong ComponentLayoutFingerprint
        {
            get
            {
                ulong acc = 0;
                lock (_componentLayoutLock)
                    foreach (var kv in _componentLayoutHashes) acc ^= kv.Value;
                return acc;
            }
        }

        /// <summary>布局守卫的判定结果：把"拒因"也带上，供上层映射成 <see cref="NativeReloadOutcome"/>。</summary>
        internal readonly struct LayoutVerdict
        {
            public bool Ok { get; }
            public NativeReloadOutcome Outcome { get; }
            public string Message { get; }

            private LayoutVerdict(bool ok, NativeReloadOutcome outcome, string message)
            {
                Ok = ok;
                Outcome = outcome;
                Message = message ?? "";
            }

            public static LayoutVerdict Pass() => new LayoutVerdict(true, NativeReloadOutcome.Swapped, "");
            public static LayoutVerdict Refuse(NativeReloadOutcome outcome, string message)
                => new LayoutVerdict(false, outcome, message);
        }

        /// <summary>
        /// 把 `dllPath` 旁的布局清单与当前已登记指纹比对（含差异类型名与两侧哈希）。
        /// 规则：无清单 + 无组件 ⇒ 放行；无清单 + 有组件 ⇒ 拒；空清单 ⇒ 放行；
        /// 清单声明的程序集在本进程无组件 ⇒ 拒；作用域内缺/多/哈希不同 ⇒ 拒。
        /// </summary>
        internal static LayoutVerdict CheckComponentLayoutManifest(string dllPath)
        {
            string manifestPath = Path.ChangeExtension(dllPath, ".layout.json");
            int types;
            lock (_componentLayoutLock) types = _componentLayoutHashes.Count;

            if (!File.Exists(manifestPath))
            {
                if (types == 0)
                {
                    Console.Error.WriteLine(
                        "[LAYOUT] no manifest and no registered components => nothing to verify (accepted)");
                    return LayoutVerdict.Pass();
                }
                return LayoutVerdict.Refuse(NativeReloadOutcome.LayoutManifestMissing,
                    $"layout manifest not found next to '{dllPath}' (expected '{manifestPath}'); "
                    + $"this process has {types} component(s) whose layout cannot be verified. "
                    + "Build the project so the manifest is copied next to the DLL.");
            }

            string manifestAssembly;
            Dictionary<string, ulong> manifest;
            try { (manifestAssembly, manifest) = ParseLayoutManifest(File.ReadAllText(manifestPath)); }
            catch (Exception ex)
            {
                return LayoutVerdict.Refuse(NativeReloadOutcome.LayoutManifestInvalid,
                    $"layout manifest '{manifestPath}' could not be parsed: {ex.GetType().Name}: {ex.Message}");
            }

            // 空清单 = 该 DLL 声明"对组件布局没有任何假设" ⇒ 放行（它不可能按偏移读组件）
            if (manifest.Count == 0)
            {
                Console.Error.WriteLine(
                    $"[LAYOUT] manifest '{Path.GetFileName(manifestPath)}' declares no components "
                    + "=> that DLL makes no component-layout assumptions (accepted)");
                return LayoutVerdict.Pass();
            }

            // 作用域：清单没写程序集名（老格式）时退回"全部已登记"
            string prefix = string.IsNullOrEmpty(manifestAssembly) ? null : manifestAssembly + "|";
            if (prefix == null)
                Console.Error.WriteLine(
                    $"[LAYOUT] manifest '{Path.GetFileName(manifestPath)}' has no \"assembly\" field; "
                    + "falling back to comparing ALL registered components");

            var scope = new Dictionary<string, ulong>(StringComparer.Ordinal);
            lock (_componentLayoutLock)
            {
                foreach (var kv in _componentLayoutHashes)
                {
                    if (prefix == null) { scope[StripScope(kv.Key)] = kv.Value; continue; }
                    if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) scope[StripScope(kv.Key)] = kv.Value;
                }
            }

            if (scope.Count == 0)
            {
                // 清单声明了组件，本进程该作用域里一个都没有 ⇒ 无法证明安全
                return LayoutVerdict.Refuse(NativeReloadOutcome.LayoutScopeMissing,
                    $"the new NativeTranspiled was built for assembly '{manifestAssembly}' with {manifest.Count} "
                    + "component(s), but this process has none of them registered (the managed assembly is not "
                    + "loaded yet, or it is a different build). Refusing: the layout cannot be verified.");
            }

            var problems = new List<string>();
            int matched = 0;
            foreach (var kv in scope)
            {
                if (!manifest.TryGetValue(kv.Key, out var expected))
                {
                    problems.Add($"{kv.Key}: missing from manifest");
                    continue;
                }
                if (expected != kv.Value)
                    problems.Add($"{kv.Key}: layout changed (0x{kv.Value:X16} -> 0x{expected:X16})");
                else
                    matched++;
            }
            foreach (var name in manifest.Keys)
            {
                if (!scope.ContainsKey(name)) problems.Add($"{name}: in manifest but not registered in this process");
            }

            if (problems.Count > 0)
            {
                int show = Math.Min(problems.Count, 8);
                string more = problems.Count > show ? $" …(+{problems.Count - show} more)" : "";
                return LayoutVerdict.Refuse(NativeReloadOutcome.LayoutMismatch,
                    $"component layout changed: {string.Join("; ", problems.GetRange(0, show))}{more} "
                    + "=> the new NativeTranspiled was generated for a DIFFERENT component layout than the "
                    + "assembly loaded in this process; rebuild the World (or reload the managed assembly) "
                    + "instead of hot-reloading the native DLL.");
            }

            Console.Error.WriteLine(
                $"[LAYOUT] verified: {matched} component(s) match '{Path.GetFileName(manifestPath)}' (fingerprint=0x{ComponentLayoutFingerprint:X16})");
            return LayoutVerdict.Pass();
        }

        /// <summary>把 `程序集名|类型全名` 的键剥成类型全名（对外消息里只报类型名，键是本框架内部约定）。</summary>
        private static string StripScope(string key)
        {
            int bar = key.IndexOf('|');
            return bar >= 0 ? key.Substring(bar + 1) : key;
        }

        /// <summary>解析布局清单：`{"version":1,"assembly":"…","entries":[{"type":"…","hash":"0x…"}]}`。</summary>
        private static (string AssemblyName, Dictionary<string, ulong> Entries) ParseLayoutManifest(string text)
        {
            var map = new Dictionary<string, ulong>(StringComparer.Ordinal);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new FormatException("manifest root is not an object");
            if (!doc.RootElement.TryGetProperty("entries", out var entries))
                throw new FormatException("manifest has no 'entries' array");
            string assembly = "";
            if (doc.RootElement.TryGetProperty("assembly", out var asmEl)
                && asmEl.ValueKind == System.Text.Json.JsonValueKind.String)
                assembly = asmEl.GetString() ?? "";
            foreach (var e in entries.EnumerateArray())
            {
                string name = e.GetProperty("type").GetString();
                string hex = e.GetProperty("hash").GetString() ?? "";
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
                if (!string.IsNullOrEmpty(name)) map[name] = Convert.ToUInt64(hex, 16);
            }
            return (assembly, map);
        }

        /// <summary>
        /// 热重载：排空 → 缓存换代 → 换到新的 NativeTranspiled（顺序即不变量，前两步失败不改变任何状态）。
        /// 调用方必须先停派发；`path` 必须是**新文件名**（同路径 `Load` 会返回旧模块）；
        /// 旧模块不 Free（可能仍被 DllImport 引用）；布局守卫比对在换句柄之前。
        /// </summary>
        /// <returns>成功 = `Swapped`；其余为带原因的拒绝，不抛异常。</returns>
        internal static NativeReloadResult ReloadNativeTranspiledCore(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path))
                return NativeReloadResult.Refuse(NativeReloadOutcome.FileNotFound,
                    $"NativeTranspiled not found for reload: {path}", false, DelegateCacheGeneration);

            // 先 Load 候选模块但不提交（拒绝时状态零改动）；被拒时同样不 Free（与"旧模块不 Free"同一取舍）
            IntPtr candidate;
            try
            {
                candidate = LoadNativeTranspiledModule(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HOTRELOAD] REFUSED: loading '{path}' failed: {ex.GetType().Name}: {ex.Message}");
                return NativeReloadResult.Refuse(NativeReloadOutcome.LoadFailed,
                    $"loading '{path}' failed: {ex.GetType().Name}: {ex.Message}", false, DelegateCacheGeneration);
            }

            // 布局守卫在任何副作用之前（不排空、不换代、不提交句柄）
            LayoutVerdict layout = CheckComponentLayoutManifest(path);
            if (!layout.Ok)
            {
                Console.Error.WriteLine($"[HOTRELOAD] REFUSED: {layout.Message}");
                return NativeReloadResult.Refuse(layout.Outcome, layout.Message, false, DelegateCacheGeneration);
            }

            bool drained = DrainAllCore();
            if (!drained)
            {
                // 排空失败必须阻断：DrainAllCore 返回 false 只能是当前 NativeDll 没有 JobSystem_DrainAll
                // 导出（老件），此时没有"等在飞 job 跑完"的手段。DrainAllCore 无副作用 ⇒ 拒绝是干净的。
                const string msg = "could not drain in-flight jobs (this NativeDll has no JobSystem_DrainAll export); "
                                 + "refusing to swap the module.";
                Console.Error.WriteLine($"[HOTRELOAD] REFUSED: {msg}");
                return NativeReloadResult.Refuse(NativeReloadOutcome.DrainNotAvailable, msg, false, DelegateCacheGeneration);
            }
            InvalidateDelegateCaches();

            IntPtr old = _nativeTranspiledDll;
            _nativeTranspiledDll = candidate;   // ← 提交（候选已 Load 好）
            // 句柄已换 ⇒ 让生成代码重跑注册（注册表里存的指针快照要跟着新句柄刷新）。
            RunReloadCallbacks();
            bool swapped = old != _nativeTranspiledDll;
            Console.Error.WriteLine(
                $"[HOTRELOAD] old=0x{old:X} new=0x{_nativeTranspiledDll:X} drained={drained} generation={DelegateCacheGeneration} callbacks={ReloadCallbackCount} layout={ComponentLayoutFingerprint:X16} path={path}");
            return new NativeReloadResult(
                swapped ? NativeReloadOutcome.Swapped : NativeReloadOutcome.NoChange,
                "", drained, DelegateCacheGeneration);
        }

        /// <summary>
        /// 在当前 NativeTranspiled 句柄上按名取 adapter 指针。
        /// ⚠ 导出是"取指针的**函数**"（`void* Get_…_AdapterPtr()`），必须**调用它**；直接把 `GetExport`
        /// 的返回值当 adapter 会让原生按 adapter ABI 调用 getter ⇒ 作业静默不执行。
        /// </summary>
        internal static IntPtr GetNativeExportPtr(string entryPointName)
        {
            if (string.IsNullOrEmpty(entryPointName))
                throw new ArgumentNullException(nameof(entryPointName));
            IntPtr h = EnsureNativeTranspiledHandle();
            delegate* unmanaged[Cdecl]<IntPtr> getter =
                (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(h, entryPointName);
            IntPtr adapter = getter();
            if (adapter == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"NativeTranspiled export '{entryPointName}' returned a NULL adapter pointer.");
            return adapter;
        }

        /// <summary>
        /// 取 NativeTranspiled 的句柄（优先显式路径）。
        /// Windows 加载器按**基名**匹配已加载模块，裸名 `Load` 可能命中别的目录里的同名件 ⇒ 取不到生成导出。
        /// </summary>
        private static IntPtr EnsureNativeTranspiledHandle()
        {
            if (_nativeTranspiledDll != IntPtr.Zero) return _nativeTranspiledDll;

            string beside = Path.Combine(AppContext.BaseDirectory, "NativeTranspiled.dll");
            if (File.Exists(beside))
            {
                _nativeTranspiledDll = NativeLibrary.Load(beside);
                return _nativeTranspiledDll;
            }
            _nativeTranspiledDll = NativeLibrary.Load("NativeTranspiled");
            return _nativeTranspiledDll;
        }

        private static delegate* unmanaged[Cdecl]<int, int> _jobSystem_Initialize;
        private static delegate* unmanaged[Cdecl]<uint> _jobSystem_GetAbiVersion;
        private static delegate* unmanaged[Cdecl]<int> _jobSystem_GetWorkerCount;
        private static delegate* unmanaged[Cdecl]<void> _jobSystem_Shutdown;
        private static delegate* unmanaged[Cdecl]<void> _jobSystem_PrewakeWorkers;
        // 可选导出（排空）：老 NativeDll 没有 ⇒ 保持 null（调用方按 false 处理）
        private static delegate* unmanaged[Cdecl]<void> _jobSystem_DrainAll;
        private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_ConfigureTilesPerWorker;
        private static delegate* unmanaged[Cdecl]<int, int, int, void> _jobSystem_ConfigureGuided;
        private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_SetJobCostCacheEnabled;
        // doc16 §46：可选导出（老 DLL 没有 ⇒ null ⇒ 按名批表绑不上，行为 = 该 env 无效）。
        private static delegate* unmanaged[Cdecl]<byte*, IntPtr, int> _jobSystem_BindBatchName;
        private static delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, void*>, delegate* unmanaged[Cdecl]<void*, void>, void> _jobSystem_RegisterPersistentAllocator;
        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> _jobSystem_Schedule;
        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, IntPtr, IntPtr> _jobSystem_ScheduleParallelForBatch;
        // 2026-10-02（ClaimPolicy）：可选导出（老 DLL 没有 ⇒ null ⇒ 退回上面那个，claim 被忽略）。
        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, int, IntPtr, IntPtr> _jobSystem_ScheduleParallelForBatchEx;
        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr> _jobSystem_ScheduleFor;
        private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_Complete;
        private static delegate* unmanaged[Cdecl]<IntPtr, ulong> _jobSystem_CompleteAndRelease;
        private static delegate* unmanaged[Cdecl]<NativeJobBatchDesc*, int, IntPtr*, int> _jobSystem_ScheduleBatch;
        private static delegate* unmanaged[Cdecl]<int, void> _jobSystem_SetImplicitBatchEnabled;
        private static delegate* unmanaged[Cdecl]<void> _jobSystem_FlushPendingSubmits;
        private static delegate* unmanaged[Cdecl]<void> _jobSystem_SubmitDeferBump;
        private static delegate* unmanaged[Cdecl]<void> _jobSystem_SubmitDeferFlush;
        private static delegate* unmanaged[Cdecl]<IntPtr, ulong> _jobSystem_GetDiagnosticBatchId;
        private static delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<ulong, void>, void> _jobSystem_RegisterCurrentBatchId;
        private static delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<ulong, byte*, int, int>, delegate* unmanaged[Cdecl]<void>, void> _jobSystem_RegisterNameResolver;
        private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_RetainHandle;
        private static delegate* unmanaged[Cdecl]<IntPtr, int> _jobSystem_IsCompleted;
        private static delegate* unmanaged[Cdecl]<IntPtr, void> _jobSystem_ReleaseHandle;
        private static delegate* unmanaged[Cdecl]<IntPtr*, int, IntPtr> _jobSystem_CombineDependencies;
        private static delegate* unmanaged[Cdecl]<NativeJobSystemStats*, void> _jobSystem_GetStats;
        private static delegate* unmanaged[Cdecl]<uint> _jobSystem_GetStatsSize;
        // 诊断（句柄是否被确定性回收）：老 NativeDll.dll 无此导出 ⇒ 用 TryGetExport，缺失时上层报告"不可用"。
        private static delegate* unmanaged[Cdecl]<long> _jobSystem_GetLiveHandleCount;
        // N12 `ENTJOY_WAKE_POLL` 生效证据（可选导出，老 DLL 为 null）。
        private static delegate* unmanaged[Cdecl]<ulong*, ulong*, void> _jobSystem_GetWakePollCounters;
        // 认领几何生效证据（可选导出，老 DLL 为 null）。
        private static delegate* unmanaged[Cdecl]<ulong*, ulong*, ulong*, void> _jobSystem_GetClaimGeomCounters;
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

        // 2026-10-02：`LoadLibraryExW` 的**瞬态** `ERROR_DLL_INIT_FAILED`(0x8007045A) 重试次数。
        //   实测：同一份字节、同一路径，第一次 `LoadLibraryExW` 报 0x8007045A，紧接着再载成功
        //   （失败尝试里 DLL 的加载期 static 初始化**已经跑过**，stderr 里有 `[SIMD]`/`[JOBBATCHTABLE]`；
        //   详见 docs/gridsearch/09 §14）。旧代码因此掉到"另一个目录的那份 DLL"上 ⇒ 静默换二进制。
        private const int kNativeLoadAttempts = 3;

        private static bool TryLoadNative(string path, out IntPtr handle, out string error)
        {
            handle = IntPtr.Zero;
            error = string.Empty;
            try
            {
                handle = NativeLibrary.Load(path);
                if (handle != IntPtr.Zero)
                    return true;
                error = $"LoadLibraryEx returned NULL (Win32 {Marshal.GetLastPInvokeError()})";
                return false;
            }
            catch (Exception ex)
            {
                handle = IntPtr.Zero;
                error = $"{ex.GetType().Name}: {ex.Message} (Win32 {Marshal.GetLastPInvokeError()})";
                return false;
            }
        }

        private static string FileSha256(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var sha = System.Security.Cryptography.SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(fs));
            }
            catch (Exception ex)
            {
                return "hash-error:" + ex.GetType().Name;
            }
        }

        private static string ShortHash(string hash) => string.IsNullOrEmpty(hash) || hash.Length <= 16
            ? hash
            : hash.Substring(0, 16);

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

            // 2026-10-02（BUG-1 收口）：加载语义改成"**首选路径 = 意图路径**"，
            //   ① 同一路径失败**重试同一路径**（实测 `LoadLibraryExW` 会以 `ERROR_DLL_INIT_FAILED`
            //      (0x8007045A) 瞬态失败一次，紧接着用**同一份字节**再载就成功 —— 见 09 §14），
            //   ② 只有在首选路径彻底失败后才考虑别的目录，且**必须字节等价**（长度+SHA256）；
            //   ③ 字节不等价时**默认拒绝**（老行为"静默用另一份 DLL"曾把一次测量变成两台不同机器：
            //      Debug 22:18 / Release 21:23 一对不匹配的二进制 ⇒ `applied=0` + 假的 ~10 ms 双峰）。
            //   想恢复老行为需显式 `ENTJOY_NATIVE_ALLOW_MISMATCHED_FALLBACK=1`。
            IntPtr dllHandle = IntPtr.Zero;
            string loadedPath = string.Empty;
            string intendedPath = primaryCandidates.Length > 0 ? primaryCandidates[0] : string.Empty;
            int attemptsUsed = 0;
            string fallbackNote = string.Empty;

            if (intendedPath.Length > 0)
            {
                for (int attempt = 1; attempt <= kNativeLoadAttempts; attempt++)
                {
                    attemptsUsed = attempt;
                    Console.Error.WriteLine($"[NativeJobScheduler] Trying NativeDll: {intendedPath} (attempt {attempt}/{kNativeLoadAttempts})");
                    if (TryLoadNative(intendedPath, out dllHandle, out string loadError))
                    {
                        loadedPath = intendedPath;
                        break;
                    }
                    Console.Error.WriteLine($"[NativeJobScheduler] load attempt {attempt}/{kNativeLoadAttempts} FAILED: {loadError}");
                    if (attempt < kNativeLoadAttempts)
                        Thread.Sleep(60 * attempt);
                }
            }

            if (dllHandle == IntPtr.Zero)
            {
                var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (intendedPath.Length > 0) tried.Add(intendedPath);
                var fallbacks = primaryCandidates.Skip(1)
                    .Concat(existingCandidates.Select(x => x.Path))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                string intendedLen = intendedPath.Length > 0 ? new FileInfo(intendedPath).Length.ToString() : "?";
                string intendedHash = string.Empty;
                bool allowMismatch = Environment.GetEnvironmentVariable("ENTJOY_NATIVE_ALLOW_MISMATCHED_FALLBACK") == "1";
                foreach (var candidate in fallbacks)
                {
                    if (dllHandle != IntPtr.Zero) break;
                    if (!tried.Add(candidate) || !File.Exists(candidate)) continue;
                    long len = new FileInfo(candidate).Length;
                    bool identical;
                    if (intendedPath.Length == 0)
                    {
                        identical = true;
                    }
                    else
                    {
                        if (intendedHash.Length == 0) intendedHash = FileSha256(intendedPath);
                        identical = len.ToString() == intendedLen
                            && string.Equals(FileSha256(candidate), intendedHash, StringComparison.Ordinal);
                    }
                    if (!identical && !allowMismatch)
                    {
                        Console.Error.WriteLine(
                            $"[NativeJobScheduler] REFUSED fallback (bytes differ from intended): {candidate} " +
                            $"(size {len} vs {intendedLen}, sha256 {ShortHash(FileSha256(candidate))} vs {ShortHash(intendedHash)}) " +
                            "-- set ENTJOY_NATIVE_ALLOW_MISMATCHED_FALLBACK=1 to restore the old silent behaviour");
                        continue;
                    }
                    Console.Error.WriteLine($"[NativeJobScheduler] Trying NativeDll fallback: {candidate} (byte-identical={identical})");
                    if (TryLoadNative(candidate, out dllHandle, out string fbError))
                    {
                        loadedPath = candidate;
                        fallbackNote = identical ? "byte-identical" : "MISMATCHED(override)";
                        break;
                    }
                    Console.Error.WriteLine($"[NativeJobScheduler] fallback FAILED: {candidate}: {fbError}");
                }
            }

            if (dllHandle == IntPtr.Zero)
            {
                try
                {
                    dllHandle = NativeLibrary.Load(dllName);
                    if (dllHandle != IntPtr.Zero)
                    {
                        loadedPath = dllName;
                        fallbackNote = "byname";
                    }
                }
                catch { }
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
                // 自证行：把"实际加载了哪一份 / 是不是回退 / 重试了几次"写进日志，任何回退都不再是静默的。
                string proofHash = File.Exists(loadedPath) ? ShortHash(FileSha256(loadedPath)) : "n/a";
                string proofSize = File.Exists(loadedPath) ? new FileInfo(loadedPath).Length.ToString() : "n/a";
                Console.Error.WriteLine(
                    $"[NativeJobScheduler] dll self-proof: intended={intendedPath} attempts={attemptsUsed} " +
                    $"fallback={(fallbackNote.Length == 0 ? "none" : fallbackNote)} size={proofSize} sha256={proofHash}");
                if (fallbackNote.Length != 0 && fallbackNote != "byte-identical")
                {
                    Console.Error.WriteLine(
                        "[NativeJobScheduler] **********************************************\n" +
                        "[NativeJobScheduler] WARNING: running a DIFFERENT native binary than intended.\n" +
                        "[NativeJobScheduler]          measurements from this process are NOT comparable.\n" +
                        "[NativeJobScheduler] **********************************************");
                }
                else if (fallbackNote == "byte-identical")
                {
                    Console.Error.WriteLine("[NativeJobScheduler] NOTE: fell back to a byte-identical copy in another directory (same content, different path).");
                }
            }

            TryLoadNativeTranspiled(loadedPath);

            // Validate the stable ABI before resolving the rest of the table:
            // incompatible DLLs must not fail the process; release and fall back to Managed.
            if (!NativeLibrary.TryGetExport(dllHandle, "JobSystem_GetAbiVersion", out IntPtr abiPtr))
            {
                NativeLibrary.Free(dllHandle);
                _nativeDll = IntPtr.Zero;
                Console.Error.WriteLine("[NativeJobScheduler] NativeDll ABI export missing; using Managed fallback.");
                return;
            }
            _jobSystem_GetAbiVersion = (delegate* unmanaged[Cdecl]<uint>)abiPtr;
            if (_jobSystem_GetAbiVersion() != ExpectedAbiVersion)
            {
                NativeLibrary.Free(dllHandle);
                _nativeDll = IntPtr.Zero;
                _jobSystem_GetAbiVersion = null;
                Console.Error.WriteLine("[NativeJobScheduler] NativeDll ABI mismatch; using Managed fallback.");
                return;
            }

            _jobSystem_Initialize = (delegate* unmanaged[Cdecl]<int, int>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_Initialize");
            _jobSystem_GetWorkerCount = (delegate* unmanaged[Cdecl]<int>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_GetWorkerCount");
            _jobSystem_Shutdown = (delegate* unmanaged[Cdecl]<void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_Shutdown");
            _jobSystem_PrewakeWorkers = (delegate* unmanaged[Cdecl]<void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_PrewakeWorkers");
            // 可选（排空）：老 NativeDll 没有该导出 ⇒ 保持 null
            if (NativeLibrary.TryGetExport(dllHandle, "JobSystem_DrainAll", out IntPtr drainAllPtr))
                _jobSystem_DrainAll = (delegate* unmanaged[Cdecl]<void>)drainAllPtr;
            _jobSystem_ConfigureTilesPerWorker = (delegate* unmanaged[Cdecl]<int, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_ConfigureTilesPerWorker");
            _jobSystem_ConfigureGuided = (delegate* unmanaged[Cdecl]<int, int, int, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_ConfigureGuided");
            _jobSystem_SetJobCostCacheEnabled = (delegate* unmanaged[Cdecl]<int, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_SetJobCostCacheEnabled");
            if (NativeLibrary.TryGetExport(dllHandle, "JobSystem_BindBatchName", out IntPtr bindBatchNamePtr))
                _jobSystem_BindBatchName = (delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)bindBatchNamePtr;
            _jobSystem_RegisterPersistentAllocator = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, void*>, delegate* unmanaged[Cdecl]<void*, void>, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_RegisterPersistentAllocator");
            _jobSystem_Schedule = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_Schedule");
            _jobSystem_ScheduleParallelForBatch = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, IntPtr, IntPtr>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleParallelForBatch");
            // 能力探测（非必需）：老 NativeDll 没有该导出 ⇒ 保持 null，claim 参数被忽略（行为与改动前一致）。
            if (NativeLibrary.TryGetExport(dllHandle, "JobSystem_ScheduleParallelForBatchEx", out IntPtr fnSchedBatchEx))
                _jobSystem_ScheduleParallelForBatchEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, int, IntPtr, IntPtr>)fnSchedBatchEx;
            if (_jobSystem_ScheduleParallelForBatchEx != null)
                Console.Error.WriteLine("[NativeJobScheduler] ClaimPolicy API: JobSystem_ScheduleParallelForBatchEx present (per-call-site claim geometry)");
            else
                Console.Error.WriteLine("[NativeJobScheduler] ClaimPolicy API: NOT present in this NativeDll (claim parameters ignored)");
            _jobSystem_ScheduleFor = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleFor");
            _jobSystem_Complete = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_Complete");
            _jobSystem_CompleteAndRelease = (delegate* unmanaged[Cdecl]<IntPtr, ulong>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_CompleteAndRelease");
            if (NativeLibrary.TryGetExport(dllHandle, "JobSystem_ScheduleBatch", out IntPtr scheduleBatchPtr))
                _jobSystem_ScheduleBatch = (delegate* unmanaged[Cdecl]<NativeJobBatchDesc*, int, IntPtr*, int>)scheduleBatchPtr;
            _jobSystem_SetImplicitBatchEnabled = (delegate* unmanaged[Cdecl]<int, void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_SetImplicitBatchEnabled");
            _jobSystem_FlushPendingSubmits = (delegate* unmanaged[Cdecl]<void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_FlushPendingSubmits");
            _jobSystem_SubmitDeferBump = (delegate* unmanaged[Cdecl]<void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_SubmitDeferBump");
            _jobSystem_SubmitDeferFlush = (delegate* unmanaged[Cdecl]<void>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_SubmitDeferFlush");
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
            if (NativeLibrary.TryGetExport(dllHandle, "JobSystem_GetLiveHandleCount", out IntPtr fnLiveHandleCount))
                _jobSystem_GetLiveHandleCount = (delegate* unmanaged[Cdecl]<long>)fnLiveHandleCount;
            if (NativeLibrary.TryGetExport(dllHandle, "JobSystem_GetWakePollCounters", out IntPtr fnWakePollCounters))
                _jobSystem_GetWakePollCounters = (delegate* unmanaged[Cdecl]<ulong*, ulong*, void>)fnWakePollCounters;
            if (NativeLibrary.TryGetExport(dllHandle, "JobSystem_GetClaimGeomCounters", out IntPtr fnClaimGeomCounters))
                _jobSystem_GetClaimGeomCounters = (delegate* unmanaged[Cdecl]<ulong*, ulong*, ulong*, void>)fnClaimGeomCounters;
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

        /// <summary>
        /// 只 Load、不提交（重载用：先在候选句柄上做布局判定）。
        /// 打印实际绑定的路径/大小/sha —— 加载器按基名匹配，且 NativeDll 与 NativeTranspiled 必须成对，
        /// 所以"绑到哪一份"要可观测。
        /// </summary>
        private static IntPtr LoadNativeTranspiledModule(string path)
        {
            IntPtr handle = NativeLibrary.Load(path);
            string size = "n/a", sha = "n/a";
            try { if (File.Exists(path)) { size = new FileInfo(path).Length.ToString(); sha = ShortHash(FileSha256(path)); } } catch { }
            Console.Error.WriteLine($"[NativeJobScheduler] NativeTranspiled self-proof: path={path} size={size} sha256={sha}");
            return handle;
        }

        private static void BindNativeTranspiled(string path)
            => _nativeTranspiledDll = LoadNativeTranspiledModule(path);

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
                        {
                            BindNativeTranspiled(candidate);
                            return _nativeTranspiledDll;
                        }
                    }
                    return IntPtr.Zero;
                });

                if (!string.IsNullOrEmpty(nativeDllPath))
                {
                    string dir = Path.GetDirectoryName(nativeDllPath);
                    string candidate = Path.Combine(dir ?? string.Empty, generatedDllName);
                    if (File.Exists(candidate))
                    {
                        BindNativeTranspiled(candidate);
                        return;
                    }
                }
                try { BindNativeTranspiled(generatedDllName); }
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

        internal static int JobSystem_Initialize(int numThreads)
        {
            EnsureNativeLoaded();
            return _jobSystem_Initialize(numThreads);
        }

        internal static bool SupportsScheduleBatch => _jobSystem_ScheduleBatch != null;

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

        /// <summary>
        /// doc16 §46：把 `ENTJOY_JOB_BATCH_BY_NAME` 里按 job 名登记的批表槽位绑定到**该 job 实际派发
        /// 用的函数指针**（名字 = 托管类型名 ⇒ 与 C++ 符号命名规则/命名空间无关）。
        /// </summary>
        /// <returns>1 = 名字在表里（已绑定）；0 = 表里没有该名字，或该导出不存在。</returns>
        internal static unsafe int JobSystem_BindBatchName(string jobName, IntPtr funcPtr)
        {
            if (string.IsNullOrEmpty(jobName) || funcPtr == IntPtr.Zero) return 0;
            EnsureNativeLoaded();
            if (_nativeDll == IntPtr.Zero || _jobSystem_BindBatchName == null) return 0;
            // UTF-8 编码到栈上（无堆分配）。C# 标识符可以是任意 Unicode ⇒ 必须按 UTF-8 编码成字节，
            // 与 env 里的字节做比较；只搬 ASCII 会在非 ASCII 名上悄悄搬错名字。
            // 上界：一个 UTF-16 码元最多编成 3 字节 ⇒ `Length <= 42` 保证放得下 127 字节 + NUL。
            const int kMaxName = 128;
            if (jobName.Length > (kMaxName - 1) / 3) return 0;
            byte* buf = stackalloc byte[kMaxName];
            int n;
            fixed (char* pName = jobName)
            {
                n = System.Text.Encoding.UTF8.GetBytes(pName, jobName.Length, buf, kMaxName - 1);
            }
            buf[n] = 0;
            return _jobSystem_BindBatchName(buf, funcPtr);
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

        // 2026-10-02（ClaimPolicy 通解）：带**调用点声明的认领几何**的调度。
        // 老 NativeDll 没有 `...BatchEx` 导出 ⇒ 退回旧导出（claim 被忽略 = 逐位不变），不打 WARN（能力探测）。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IntPtr JobSystem_ScheduleParallelForBatchEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr,
            int length, int batchSize, int claimGeom, IntPtr dependency)
        {
            EnsureNativeLoaded();
            if (_jobSystem_ScheduleParallelForBatchEx == null)
                return _jobSystem_ScheduleParallelForBatch(funcPtr, context, cleanupPtr, length, batchSize, dependency);
            return _jobSystem_ScheduleParallelForBatchEx(funcPtr, context, cleanupPtr, length, batchSize, claimGeom, dependency);
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

        /// <summary>Complete + 读 diagnosticBatchId + 释放句柄引用（三合一，native 侧一次完成）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong JobSystem_CompleteAndRelease(IntPtr handle)
        {
            EnsureNativeLoaded();
            return _jobSystem_CompleteAndRelease(handle);
        }

        /// <summary>显式批：一次 P/Invoke 提交 count 个 job 描述符，句柄写回 outHandles（内部 defer+统一唤醒）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe int JobSystem_ScheduleBatch(NativeJobBatchDesc* descs, int count, IntPtr* outHandles)
        {
            EnsureNativeLoaded();
            if (_jobSystem_ScheduleBatch == null)
                throw new NotSupportedException("NativeDll does not export JobSystem_ScheduleBatch.");
            return _jobSystem_ScheduleBatch(descs, count, outHandles);
        }

        /// <summary>隐式批（native 收集）开关：1=启用（Schedule* 透明挂 pending）；0=关闭并排空积压。</summary>
        internal static void JobSystem_SetImplicitBatchEnabled(int enabled)
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_SetImplicitBatchEnabled == null) return;
            _jobSystem_SetImplicitBatchEnabled(enabled);
        }

        /// <summary>隐式批 force point：提交全部 pending + 单次唤醒（帧末 EndFrame / Complete 自动触发）。</summary>
        internal static void JobSystem_FlushPendingSubmits()
        {
            if (_nativeDll == IntPtr.Zero || _jobSystem_FlushPendingSubmits == null) return;
            _jobSystem_FlushPendingSubmits();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void JobSystem_SubmitDeferBump()
        {
            EnsureNativeLoaded();
            _jobSystem_SubmitDeferBump();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void JobSystem_SubmitDeferFlush()
        {
            EnsureNativeLoaded();
            _jobSystem_SubmitDeferFlush();
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

        /// <summary>存活句柄 state 数（诊断/测试）。老 NativeDll.dll 缺该导出时返回 false。</summary>
        internal static bool TryGetLiveHandleStateCount(out long count)
        {
            count = 0;
            if (_nativeDll == IntPtr.Zero || _jobSystem_GetLiveHandleCount == null) return false;
            count = _jobSystem_GetLiveHandleCount();
            return true;
        }

        /// <summary>N12 `ENTJOY_WAKE_POLL` 生效证据：提交侧 [跳过写唤醒字, 真的广播] 次数。
        /// skips 必须远大于 wakes，否则"开关打开但无效"会被误读成"改动无效"。
        /// 老 NativeDll.dll 缺该导出时返回 false。</summary>
        internal static bool TryGetWakePollCounters(out ulong skips, out ulong wakes)
        {
            skips = 0; wakes = 0;
            if (_nativeDll == IntPtr.Zero || _jobSystem_GetWakePollCounters == null) return false;
            ulong s = 0, w = 0;
            _jobSystem_GetWakePollCounters(&s, &w);
            skips = s; wakes = w;
            return true;
        }

        /// <summary>认领几何（`ClaimPolicy`）的生效证据：按声明值分桶的批数 [spread, adjacent, auto]。
        /// 传了 <c>ClaimPolicy.Spread</c> 就必须看到 spread&gt;0 —— 否则说明调用点静默降级到了托管回调
        /// （09 §52.5）。老 NativeDll.dll 缺该导出时返回 false。</summary>
        internal static bool TryGetClaimGeomCounters(out ulong spread, out ulong adjacent, out ulong autoDecl)
        {
            spread = 0; adjacent = 0; autoDecl = 0;
            if (_nativeDll == IntPtr.Zero || _jobSystem_GetClaimGeomCounters == null) return false;
            ulong sp = 0, ad = 0, au = 0;
            _jobSystem_GetClaimGeomCounters(&sp, &ad, &au);
            spread = sp; adjacent = ad; autoDecl = au;
            return true;
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
            // 【上限探针】`ENTJOY_BATCHID_CALLBACK=0` 时不注册：native 侧每执行窗口 2 次的反向托管
            // 调用（`ChaseLevScheduler.cpp:620/660` 的 SetCurrentBatchId(id)/SetCurrentBatchId(0)）就退化成
            // 一次 null 检查。用途：量化"每 job 的托管反向回调"成本。
            // ⚠ 实测（2026-09-29，8 worker，[NP-4e] 7936×64 空体）：**开关两臂同为 4.46 µs/job**
            // ⇒ 该回调 **<0.1 µs/job**，**不是**每-job 残余差距的来源（此候选已否证，勿再试）。
            if (System.Environment.GetEnvironmentVariable("ENTJOY_BATCHID_CALLBACK") == "0") return;
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

        internal static void ResetShutdownGate() => Interlocked.Exchange(ref _shutdownRequested, 0);

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
        // 泛型委托缓存：供跨程序集路径（EntJoy.ECS ChunkJobScheduler 等）使用；
        // 本程序集热路径用静态泛型缓存（JobDelegateCacheFor 等），此处服务自定义委托类型参数。
        internal static DelegateCache GetOrCreateDelegateCache<T, TDelegate>(Func<TDelegate> factory) where TDelegate : Delegate
        {
            return _delegateCache.GetOrAdd(typeof(T), _ => new DelegateCache(factory()));
        }

        // ── 委托缓存换代 ──
        // 这些缓存持有指向某个具体模块导出的 thunk，换 DLL 后会命中旧指针 ⇒ 换代：清字典 + 自增代次，
        // 下游静态泛型缓存按代次惰性重建（闭泛型静态无法从外部枚举）。
        private static int _delegateCacheGeneration;
        internal static int DelegateCacheGeneration => Volatile.Read(ref _delegateCacheGeneration);

        internal static void InvalidateDelegateCaches()
        {
            _delegateCache.Clear();
            Interlocked.Increment(ref _delegateCacheGeneration);
        }

        // 静态泛型委托缓存：per (T) 静态字段 + 代次校验（换代后惰性重建）。
        // 并发换代时可能重复重建（最后写入者胜出，各实例都有效），代价只在换代瞬间。
        internal static class JobDelegateCacheFor<T> where T : struct, IJob
        {
            private static DelegateCache _cache;
            private static int _gen = -1;
            public static DelegateCache Cache
            {
                get
                {
                    int g = DelegateCacheGeneration;
                    if (_gen != g) { _cache = new(CreateJobCallback<T>()); _gen = g; }
                    return _cache;
                }
            }
        }

        internal static class ForDelegateCacheFor<T> where T : struct, IJobFor
        {
            private static DelegateCache _cache;
            private static int _gen = -1;
            public static DelegateCache Cache
            {
                get
                {
                    int g = DelegateCacheGeneration;
                    if (_gen != g) { _cache = new(CreateForCallback<T>()); _gen = g; }
                    return _cache;
                }
            }
        }

        internal static class ParallelForBatchDelegateCacheFor<T> where T : struct, IJobParallelForBatch
        {
            private static DelegateCache _cache;
            private static int _gen = -1;
            public static DelegateCache Cache
            {
                get
                {
                    int g = DelegateCacheGeneration;
                    if (_gen != g) { _cache = new(CreateParallelForBatchCallback<T>()); _gen = g; }
                    return _cache;
                }
            }
        }

        /// <summary>
        /// 自动批处理回调（per 泛型 T 缓存一次） + 代次校验：T 实现 IJobParallelForBatch 时用批回调
        /// （一次 Execute(start,count)），否则逐元素 Execute(i)，减轻轻任务调度开销。
        /// </summary>
        private static class AutoParallelForCallback<T>
            where T : struct, IJobParallelFor
        {
            private static DelegateCache _cache;
            private static int _gen = -1;

            public static DelegateCache GetCache()
            {
                int g = DelegateCacheGeneration;
                if (_gen != g) { _cache = new(CreateParallelForIndexCallback<T>()); _gen = g; }
                return _cache;
            }
        }

        internal static DelegateCache GetAutoParallelForCache<T>() where T : struct, IJobParallelFor
            => AutoParallelForCallback<T>.GetCache();

        // 按 batchId 归集的 Job 异常。Complete(h) 只抛本 batch 的异常；batch 0 为未归属异常。
        private static readonly object _exceptionLock = new();
        private static Dictionary<ulong, List<ExceptionDispatchInfo>> _recordedJobExceptions = new();
        private const int MaxRecordedJobExceptionsPerBatch = 16;
        private static int _droppedJobExceptionCount;
        // 快速门控：>0 表示有待取异常，避免每次 Complete 都 lock+查字典（异常是罕见路径）。
        private static int _pendingJobExceptionCount;

        /// <summary>
        /// 是否有待取的 Job 异常（罕见路径）。Complete 用它门控一次 `JobSystem_GetDiagnosticBatchId`
        /// 的 P/Invoke：计数器为 0 时该 batch 不可能有已记录异常（记录必先自增），
        /// 与 <see cref="ThrowRecordedJobExceptions"/> 的首行判断完全等价。
        /// </summary>
        internal static bool HasPendingJobExceptions => Volatile.Read(ref _pendingJobExceptionCount) != 0;

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
                Interlocked.Increment(ref _pendingJobExceptionCount);
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
            if (Volatile.Read(ref _pendingJobExceptionCount) == 0) return;
            List<ExceptionDispatchInfo> all = new();
            int dropped;
            lock (_exceptionLock)
            {
                foreach (var list in _recordedJobExceptions.Values)
                    all.AddRange(list);
                _recordedJobExceptions.Clear();
                Interlocked.Exchange(ref _pendingJobExceptionCount, 0);
                dropped = _droppedJobExceptionCount;
                _droppedJobExceptionCount = 0;
            }
            if (dropped > 0)
                Console.Error.WriteLine($"[JobSystem] {dropped} job exceptions dropped (per-batch cap {MaxRecordedJobExceptionsPerBatch}).");
            ThrowAll(all);
        }

        internal static void ThrowRecordedJobExceptions(ulong batchId)
        {
            if (Volatile.Read(ref _pendingJobExceptionCount) == 0) return;
            List<ExceptionDispatchInfo> captured;
            int dropped;
            lock (_exceptionLock)
            {
                if (!_recordedJobExceptions.TryGetValue(batchId, out captured))
                {
                    // 本 batch 无异常；仅当字典已清空时才关闭门控（其他 batch 的异常仍在等待被取走）
                    if (_recordedJobExceptions.Count == 0)
                        Interlocked.Exchange(ref _pendingJobExceptionCount, 0);
                    return;
                }
                _recordedJobExceptions.Remove(batchId);
                if (_recordedJobExceptions.Count == 0)
                    Interlocked.Exchange(ref _pendingJobExceptionCount, 0);
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
            // job 完整结束点（RunBatchCleanup 只认领一次，在所有 tile 之后）：
            // 释放本 ctx 的写声明与读声明。**两者都必须在 job 完成点释放**：
            // 按 tile 释放会和仍在运行的同 ctx 兄弟 tile 竞争 —— 第一个结束的 tile 会把整个 ctx 的
            // 读声明清空，兄弟 tile 此后不再重新登记（_readMark/TLS 快路径命中），于是 job 未完成时
            // 主线程访问不再被拦截（契约见 Runtime-Contracts §并行读写冲突检测）。
            SafetyHandleManager.ReleaseWritesForContext(ctx);
            SafetyHandleManager.ReleaseReadsForContext(ctx);
            var handle = GCHandle.FromIntPtr(ctx);
            if (handle.IsAllocated) handle.Free();
        }

        internal unsafe static IntPtr AllocContext<T>(ref T job) where T : struct
        {
            int size = Unsafe.SizeOf<T>();
            int totalSize = size + sizeof(int);
            long t0 = CSharpPhaseDiag.Sampling("ctx.alloc") ? CSharpPhaseDiag.Now() : 0;
            IntPtr dataPtr = ContextPool.Rent(totalSize);
            *(int*)dataPtr = size;
            byte* jobPtr = (byte*)dataPtr + sizeof(int);
            Unsafe.CopyBlockUnaligned(jobPtr, Unsafe.AsPointer(ref job), (uint)size);
            if (t0 != 0) CSharpPhaseDiag.Add("ctx.alloc", CSharpPhaseDiag.Us(t0, CSharpPhaseDiag.Now()));
            return (IntPtr)jobPtr;
        }

        /// <summary>
        /// 2026-10-02（09 §26）：**按生成代码的逐字段布局**租一块 ctx（供原生 adapter 直调）。
        /// 与 <see cref="AllocContext{T}"/> 共用同一个 <see cref="ContextPool"/> 与同一套 4 字节长度前缀，
        /// 因此**释放也复用同一个 <see cref="CleanupPtr"/>**（回池 + 释放读写声明），
        /// 不需要 `Marshal.AllocHGlobal` / `FreeHGlobal` 的逐派发 malloc/free。
        /// 调用方随后用生成代码的 `JobFieldWriter&lt;T&gt;` 把字段写进返回指针（布局与 C++ adapter 的偏移一致）。
        /// </summary>
        internal unsafe static IntPtr RentMarshalledContext(int contextSize)
        {
            if (contextSize <= 0) return IntPtr.Zero;
            int totalSize = contextSize + sizeof(int);
            IntPtr dataPtr = ContextPool.Rent(totalSize);
            *(int*)dataPtr = contextSize;
            return (IntPtr)((byte*)dataPtr + sizeof(int));
        }

        internal unsafe static void Cleanup(IntPtr dataPtr)
        {
            if (dataPtr == IntPtr.Zero) return;
            // job 完整结束点（RunBatchCleanup 只认领一次，在所有 tile 之后）：
            // 释放本 ctx 的写声明与读声明（理由同 ManagedCleanup：按 tile 释放会提前清空兄弟 tile 的读者计数）。
            SafetyHandleManager.ReleaseWritesForContext(dataPtr);
            SafetyHandleManager.ReleaseReadsForContext(dataPtr);
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
                nint prevCtx = JobIdentity.CurrentContext;
                JobIdentity.SetCurrentContext(ctx);
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
                    JobIdentity.SetCurrentContext(prevCtx);
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
                nint prevCtx = JobIdentity.CurrentContext;
                JobIdentity.SetCurrentContext(ctx);
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
                    JobIdentity.SetCurrentContext(prevCtx);
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
                nint prevCtx = JobIdentity.CurrentContext;
                JobIdentity.SetCurrentContext(ctx);
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
                    JobIdentity.SetCurrentContext(prevCtx);
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
                nint prevCtx = JobIdentity.CurrentContext;
                JobIdentity.SetCurrentContext(ctx);
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
                    JobIdentity.SetCurrentContext(prevCtx);
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

        internal static NativeJobHandle ScheduleParallelForBatchRaw(IntPtr funcPtr, IntPtr contextPtr, IntPtr cleanupPtr, int length, int batchSize, NativeJobHandle? dependsOn = null, ClaimPolicy claim = ClaimPolicy.Auto)
        {
            long t0 = CSharpPhaseDiag.Sampling("parfor.pinvoke") ? CSharpPhaseDiag.Now() : 0;
            using var dependencyLease = new RetainedNativeDependency(dependsOn);
            // Auto(0) 走原导出（零额外分支，与引入 ClaimPolicy 前逐位一致）；显式声明走 Ex 导出。
            var ret = (claim == ClaimPolicy.Auto)
                ? new NativeJobHandle(JobSystem_ScheduleParallelForBatch(funcPtr, contextPtr, cleanupPtr, length, batchSize, dependencyLease.Handle))
                : new NativeJobHandle(JobSystem_ScheduleParallelForBatchEx(funcPtr, contextPtr, cleanupPtr, length, batchSize, (int)claim, dependencyLease.Handle));
            if (t0 != 0) CSharpPhaseDiag.Add("parfor.pinvoke", CSharpPhaseDiag.Us(t0, CSharpPhaseDiag.Now()));
            return ret;
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
