using EntJoy.JobSystem;
using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// 作业句柄，统一封装 C# Task 句柄和 C++ 原生句柄。
/// 当 NativeHandle 有效时优先使用 C++ 作业调度，否则回退到 Task 调度。
/// </summary>
public struct JobHandle
{
    private Task _task;
    private Task[] _tasks;
    public NativeJobHandle _nativeHandle;

    internal JobHandle(Task task)
    {
        _task = task;
        _tasks = null;
        _nativeHandle = default;
    }

    public JobHandle(NativeJobHandle nativeHandle)
    {
        _task = null;
        _tasks = null;
        _nativeHandle = nativeHandle;
    }

    /// <summary>是否已完成</summary>
    public bool IsCompleted
    {
        get
        {
            // 原生句柄优先
            if (_nativeHandle.IsValid)
                return NativeJobScheduler.IsCompleted(_nativeHandle);

            if (_task != null)
                return _task.IsCompleted;
            if (_tasks != null)
            {
                foreach (var t in _tasks)
                    if (!t.IsCompleted) return false;
                return true;
            }
            return true;
        }
    }

    /// <summary>强制等待所有关联 Job 完成（阻塞当前线程）</summary>
    public void Complete()
    {
        if (_nativeHandle.IsValid)
        {
            NativeJobScheduler.Complete(ref _nativeHandle);
            return;
        }

        if (_task != null)
            _task.Wait();
        else if (_tasks != null)
            Task.WaitAll(_tasks);
    }

    internal Task GetTask()
    {
        // 对于原生句柄包装返回已完成任务（用户应使用 Complete）
        if (_nativeHandle.IsValid)
            return Task.CompletedTask;
        if (_task != null) return _task;
        if (_tasks != null) return Task.WhenAll(_tasks);
        return Task.CompletedTask;
    }

    /// <summary>合并多个依赖句柄</summary>
    public static JobHandle CombineDependencies(params JobHandle[] handles)
    {
        if (handles == null || handles.Length == 0)
            return new JobHandle(Task.CompletedTask);

        // 检查是否有原生句柄
        bool hasNative = false;
        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i]._nativeHandle.IsValid)
            {
                hasNative = true;
                break;
            }
        }

        if (hasNative)
        {
            // 提取所有原生句柄
            var nativeHandles = new NativeJobHandle[handles.Length];
            for (int i = 0; i < handles.Length; i++)
                nativeHandles[i] = handles[i]._nativeHandle;
            var combined = NativeJobScheduler.CombineDependencies(nativeHandles);
            return new JobHandle(combined);
        }

        // 回退到 Task 合并
        Task singleTask = null;
        int validCount = 0;
        for (int i = 0; i < handles.Length; i++)
        {
            var t = handles[i].GetTask();
            if (t != null && t != Task.CompletedTask)
            {
                validCount++;
                singleTask = t;
            }
        }

        if (validCount == 0)
            return new JobHandle(Task.CompletedTask);
        if (validCount == 1)
            return new JobHandle(singleTask);

        // 多个依赖
        var combineNode = new CombineNode(validCount);
        for (int i = 0; i < handles.Length; i++)
        {
            var t = handles[i].GetTask();
            if (t != null && t != Task.CompletedTask)
                t.ContinueWith(_ => combineNode.Decrement(), TaskContinuationOptions.ExecuteSynchronously);
            else
                combineNode.Decrement();
        }
        return new JobHandle(combineNode.Task);
    }

    private class CombineNode
    {
        private int _remaining;
        private TaskCompletionSource<bool> _tcs;

        public CombineNode(int initialCount)
        {
            _remaining = initialCount;
            _tcs = new TaskCompletionSource<bool>();
        }

        public Task Task => _tcs.Task;

        public void Decrement()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
                _tcs.TrySetResult(true);
        }
    }
}
