
/// <summary>
/// 作业句柄，统一封装 C++ 原生句柄（NativeJobScheduler）。
/// </summary>
public struct JobHandle
{
    public NativeJobHandle _nativeHandle;

    public JobHandle(NativeJobHandle nativeHandle)
    {
        _nativeHandle = nativeHandle;
    }

    /// <summary>是否已完成</summary>
    public bool IsCompleted
    {
        get
        {
            if (!_nativeHandle.IsValid)
                return true;
            return NativeJobScheduler.IsCompleted(_nativeHandle);
        }
    }

    /// <summary>强制等待所有关联 Job 完成（阻塞当前线程）</summary>
    public void Complete()
    {
        if (!_nativeHandle.IsValid)
            return;
        NativeJobScheduler.Complete(ref _nativeHandle);
    }

    /// <summary>原生依赖句柄（本路径句柄恒为原生，直接返回）</summary>
    internal NativeJobHandle GetNativeDependency() => _nativeHandle;

    /// <summary>合并多个依赖句柄</summary>
    public static JobHandle CombineDependencies(params JobHandle[] handles)
    {
        if (handles == null || handles.Length == 0)
            return default;

        var nativeHandles = new NativeJobHandle[handles.Length];
        for (int i = 0; i < handles.Length; i++)
            nativeHandles[i] = handles[i]._nativeHandle;

        var combined = NativeJobScheduler.CombineDependencies(nativeHandles);
        return new JobHandle(combined);
    }
}
