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
    public IntPtr Handle;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeJobHandle(IntPtr handle) => Handle = handle;

    public readonly bool IsValid => Handle != IntPtr.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(NativeJobHandle other) => Handle == other.Handle;

    public override readonly bool Equals(object obj) => obj is NativeJobHandle other && Equals(other);

    public override readonly int GetHashCode() => Handle.GetHashCode();

    public static bool operator ==(NativeJobHandle left, NativeJobHandle right) => left.Handle == right.Handle;

    public static bool operator !=(NativeJobHandle left, NativeJobHandle right) => left.Handle != right.Handle;
}
