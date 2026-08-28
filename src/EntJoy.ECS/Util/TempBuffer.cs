using System;
using EntJoy.JobSystem;
using System.Runtime.InteropServices;

namespace EntJoy.ECS.JobSystem
{

/// <summary>
/// 线程静态临时缓冲（ulong 位图等）。生成器生成的 N 元组枚举器（用户程序集）也使用，
/// 故为 public。
/// </summary>
public static class TempBuffer
{
    [ThreadStatic]
    private static ulong[] _threadBuffer;
    [ThreadStatic]
    private static GCHandle _threadBufferHandle;
    [ThreadStatic]
    private static unsafe ulong* _threadBufferPtr;

    public static unsafe ulong* GetBuffer(int requiredLength)
    {
        if (_threadBuffer == null || _threadBuffer.Length < requiredLength)
        {
            if (_threadBufferHandle.IsAllocated)
                _threadBufferHandle.Free();

            _threadBuffer = new ulong[requiredLength];
            _threadBufferHandle = GCHandle.Alloc(_threadBuffer, GCHandleType.Pinned);
            _threadBufferPtr = (ulong*)_threadBufferHandle.AddrOfPinnedObject();
        }
        return _threadBufferPtr;
    }
}
}