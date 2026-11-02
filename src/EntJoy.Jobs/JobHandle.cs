using System;
using System.Runtime.CompilerServices;
using EntJoy.JobSystem.Managed;

namespace EntJoy.JobSystem
{
/// <summary>
/// 作业句柄，支持 C++ 原生（NativeJobScheduler）和纯 C#（ManagedJobScheduler）双后端。
/// 当 NativeDll 不可用时自动回退 ManagedJobScheduler，句柄统一。
/// </summary>
public struct JobHandle : IEquatable<JobHandle>
{
    public NativeJobHandle _nativeHandle;
    internal ManagedJobHandle _managedHandle;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public JobHandle(NativeJobHandle nativeHandle) => _nativeHandle = nativeHandle;

    internal JobHandle(ManagedJobHandle managedHandle) => _managedHandle = managedHandle;

    public readonly bool Equals(JobHandle other)
        => _nativeHandle.Equals(other._nativeHandle)
            && ReferenceEquals(_managedHandle.Completion, other._managedHandle.Completion)
            && _managedHandle.Gen == other._managedHandle.Gen
            && _managedHandle.Epoch == other._managedHandle.Epoch;

    public override readonly bool Equals(object obj) => obj is JobHandle other && Equals(other);

    public override readonly int GetHashCode()
        => HashCode.Combine(_nativeHandle,
            _managedHandle.Completion == null ? 0 : RuntimeHelpers.GetHashCode(_managedHandle.Completion),
            _managedHandle.Gen, _managedHandle.Epoch);

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
    }

    internal NativeJobHandle GetNativeDependency() => _nativeHandle;

    public static JobHandle CombineDependencies(params JobHandle[] handles)
        => CombineDependencies((ReadOnlySpan<JobHandle>)handles);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static JobHandle CombineDependencies(ReadOnlySpan<JobHandle> handles)
    {
        if (handles.IsEmpty) return default;
        int nativeCount = 0;
        bool hasManaged = false;
        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i]._nativeHandle.IsValid) nativeCount++;
            if (handles[i]._managedHandle.Completion != null) hasManaged = true;
        }
        if (nativeCount != 0 && hasManaged)
            throw new InvalidOperationException("Native and Managed job handles cannot be combined.");
        if (nativeCount == 0 && !hasManaged) return default;
        if (!hasManaged) return new JobHandle(NativeHandleStorage.Combine(handles, default, nativeCount));
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

    public static JobHandle CombineDependencies(JobHandle handle)
    {
        if (handle._managedHandle.Completion != null) return CombineDependencies(new[] { handle });
        return new JobHandle(NativeJobScheduler.CombineDependencies(handle._nativeHandle));
    }

    public static JobHandle CombineDependencies(JobHandle first, JobHandle second)
    {
        if (first._managedHandle.Completion != null || second._managedHandle.Completion != null)
            return CombineDependencies(new[] { first, second });
        Span<NativeJobHandle> handles = stackalloc NativeJobHandle[2] { first._nativeHandle, second._nativeHandle };
        return new JobHandle(NativeJobScheduler.CombineDependencies(handles));
    }

    public static JobHandle CombineDependencies(JobHandle first, JobHandle second, JobHandle third)
    {
        if (first._managedHandle.Completion != null || second._managedHandle.Completion != null || third._managedHandle.Completion != null)
            return CombineDependencies(new[] { first, second, third });
        Span<NativeJobHandle> handles = stackalloc NativeJobHandle[3] { first._nativeHandle, second._nativeHandle, third._nativeHandle };
        return new JobHandle(NativeJobScheduler.CombineDependencies(handles));
    }
}
}
