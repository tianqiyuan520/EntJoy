using System;
using EntJoy.JobSystem.Managed;

namespace EntJoy.JobSystem
{
/// <summary>
/// 作业句柄，支持 C++ 原生（NativeJobScheduler）和纯 C#（ManagedJobScheduler）双后端。
/// 当 NativeDll 不可用时自动回退 ManagedJobScheduler，句柄统一。
/// </summary>
public struct JobHandle
{
    public NativeJobHandle _nativeHandle;
    internal ManagedJobHandle _managedHandle;

    public JobHandle(NativeJobHandle nativeHandle) => _nativeHandle = nativeHandle;

    internal JobHandle(ManagedJobHandle managedHandle) => _managedHandle = managedHandle;

    public bool IsCompleted
    {
        get
        {
            if (_managedHandle.Completion != null) return _managedHandle.IsCompleted;
            if (!_nativeHandle.IsValid) return true;
            return NativeJobScheduler.IsCompleted(_nativeHandle);
        }
    }

    /// <summary>是否为空句柄（default(JobHandle)，两个后端均无有效依赖）。</summary>
    public bool IsNull => !_nativeHandle.IsValid && _managedHandle.Completion == null;

    public void Complete()
    {
        if (_managedHandle.Completion != null) { _managedHandle.Complete(); return; }
        if (!_nativeHandle.IsValid) return;
        NativeJobScheduler.Complete(ref _nativeHandle);
        // Complete **消费**句柄（Unity 同语义）：等待完成后立刻确定性释放 native HandleState，
        // 不再把释放推迟到 .NET 终结器。动机（实测，docs/gridsearch/07 §7l / §7m）：
        // 托管 NativeJobHandleBox 的终结器负责 ReleaseRawHandleForFinalizer ⇒ 回收发生在
        // **终结器线程**、且只在 GC 批量发生时成批出现，于是调度线程的 TLS 状态缓存恒空：
        // 每次 Schedule 都要 `new HandleState`（384 B），实测 `new` 占 CreateState 的 92～95%，
        // 且运行中最多有半数 state 悬着不回收（25 s 内 ~5.2 万个 = ~20 MB）。
        // 释放后 _nativeHandle 置空 ⇒ 已完成句柄继续当依赖/查询一律安全
        // （IsValid=false ⇒ 视为"已完成、无依赖"）；重复 Complete 变为空操作。
        NativeJobScheduler.Release(_nativeHandle);
        _nativeHandle = default;
    }

    internal NativeJobHandle GetNativeDependency() => _nativeHandle;

    public static JobHandle CombineDependencies(params JobHandle[] handles)
    {
        if (handles == null || handles.Length == 0) return default;
        // 纯 C++ 后端合并
        var nativeHandles = new NativeJobHandle[handles.Length];
        bool allNative = true;
        bool hasNative = false;
        bool hasManaged = false;
        for (int i = 0; i < handles.Length; i++)
        {
            nativeHandles[i] = handles[i]._nativeHandle;
            if (handles[i]._nativeHandle.IsValid) hasNative = true;
            if (handles[i]._managedHandle.Completion != null) { hasManaged = true; allNative = false; }
        }
        if (hasNative && hasManaged)
            throw new InvalidOperationException("Native and Managed job handles cannot be combined.");
        if (!hasNative && !hasManaged) return default;
        if (allNative) return new JobHandle(NativeJobScheduler.CombineDependencies(nativeHandles));
        // 混合/托管后端：合并所有 Completed 的 handle
        ManagedJobHandle? first = null;
        var managed = new ManagedJobHandle[handles.Length];
        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i]._managedHandle.Completion != null) { managed[i] = handles[i]._managedHandle; first ??= managed[i]; }
        }
        if (first.HasValue) return new JobHandle(ManagedJobHandle.CombineDependencies(managed));
        return default;
    }
}
}
