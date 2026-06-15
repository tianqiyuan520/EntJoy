using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// 表示一个由 C++ JobSystem 调度的原生作业句柄。
/// 该句柄持有对 C++ HandleState 的一次引用，释放时需调用 Release 或 Complete。
/// 此类型在全局命名空间中，便于源代码生成器引用。
/// </summary>
public struct NativeJobHandle : IEquatable<NativeJobHandle>
{
    private NativeJobHandleBox _box;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeJobHandle(IntPtr handle) => _box = handle == IntPtr.Zero ? null : new NativeJobHandleBox(handle);

    public readonly IntPtr Handle => _box?.Handle ?? IntPtr.Zero;

    public readonly bool IsValid => Handle != IntPtr.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly IntPtr Detach()
    {
        return _box?.Detach() ?? IntPtr.Zero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly void Clear()
    {
        _box?.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(NativeJobHandle other) => Handle == other.Handle;

    public override readonly bool Equals(object obj) => obj is NativeJobHandle other && Equals(other);

    public override readonly int GetHashCode() => Handle.GetHashCode();

    public static bool operator ==(NativeJobHandle left, NativeJobHandle right) => left.Handle == right.Handle;

    public static bool operator !=(NativeJobHandle left, NativeJobHandle right) => left.Handle != right.Handle;
}

internal sealed class NativeJobHandleBox
{
    private IntPtr _handle;

    public NativeJobHandleBox(IntPtr handle)
    {
        _handle = handle;
    }

    public IntPtr Handle => Volatile.Read(ref _handle);

    public IntPtr Detach()
    {
        return Interlocked.Exchange(ref _handle, IntPtr.Zero);
    }

    public void Clear()
    {
        Volatile.Write(ref _handle, IntPtr.Zero);
    }

    ~NativeJobHandleBox()
    {
        IntPtr handle = Detach();
        if (handle != IntPtr.Zero)
        {
            NativeJobScheduler.ReleaseRawHandleForFinalizer(handle);
        }
    }
}
