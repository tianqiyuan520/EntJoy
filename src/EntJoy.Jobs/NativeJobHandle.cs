using System;
using System.Runtime.CompilerServices;

namespace EntJoy.JobSystem
{
    /// <summary>
    /// 原生任务的共享所有权标识。完成或释放使副本失效，代次校验隔离槽位复用。
    /// 句柄须显式完成或释放，调度器关闭时回收剩余所有权。
    /// </summary>
    public readonly unsafe struct NativeJobHandle : IEquatable<NativeJobHandle>
    {
        internal readonly IntPtr Slot;
        internal readonly long Generation;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeJobHandle(IntPtr handle) => this = handle == IntPtr.Zero ? default : NativeHandleStorage.Create(handle);
        internal NativeJobHandle(IntPtr slot, long generation) { Slot = slot; Generation = generation; }
        public IntPtr Handle => Slot == IntPtr.Zero ? IntPtr.Zero : NativeHandleStorage.GetHandle(this);
        public bool IsValid => Slot != IntPtr.Zero && Handle != IntPtr.Zero;
        internal IntPtr RetainForUse() => NativeHandleStorage.Retain(this);
        internal bool IsCompletedFast => NativeHandleStorage.IsCompleted(this);
        public bool Equals(NativeJobHandle other) => Slot == other.Slot && Generation == other.Generation;
        public override bool Equals(object obj) => obj is NativeJobHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Slot, Generation);
        public static bool operator ==(NativeJobHandle left, NativeJobHandle right) => left.Equals(right);
        public static bool operator !=(NativeJobHandle left, NativeJobHandle right) => !left.Equals(right);
    }
}
