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

    public void Complete()
    {
        if (_managedHandle.Completion != null) { ManagedJobScheduler.CompleteSchedule(_managedHandle.Completion); return; }
        if (!_nativeHandle.IsValid) return;
        NativeJobScheduler.Complete(ref _nativeHandle);
    }

    internal NativeJobHandle GetNativeDependency() => _nativeHandle;

    public static JobHandle CombineDependencies(params JobHandle[] handles)
    {
        if (handles == null || handles.Length == 0) return default;
        // 纯 C++ 后端合并
        var nativeHandles = new NativeJobHandle[handles.Length];
        bool allNative = true;
        for (int i = 0; i < handles.Length; i++)
        {
            nativeHandles[i] = handles[i]._nativeHandle;
            if (handles[i]._managedHandle.Completion != null) allNative = false;
        }
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
