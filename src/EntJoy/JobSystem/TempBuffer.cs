using System;
using System.Runtime.InteropServices;

internal static class TempBuffer
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

    public unsafe static void ReleaseCurrentThreadBuffer()
    {
        if (_threadBufferHandle.IsAllocated)
        {
            _threadBufferHandle.Free();
            _threadBuffer = null;
            _threadBufferPtr = null;
        }
    }
}