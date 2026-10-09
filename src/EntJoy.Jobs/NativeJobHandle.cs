using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.JobSystem
{
    /// <summary>
    /// 表示一个由 C++ JobSystem 调度的原生作业句柄。
    /// 该句柄持有对 C++ HandleState 的一次引用，释放时需调用 Release 或 Complete。
    /// </summary>
    public struct NativeJobHandle : IEquatable<NativeJobHandle>
    {
        private NativeJobHandleBox _box;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeJobHandle(IntPtr handle) => _box = handle == IntPtr.Zero ? null : new NativeJobHandleBox(handle);

        public readonly IntPtr Handle => _box?.Handle ?? IntPtr.Zero;

        public readonly bool IsValid => Handle != IntPtr.Zero;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly IntPtr Detach() => _box?.Detach() ?? IntPtr.Zero;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly IntPtr RetainForUse() => _box?.RetainForUse() ?? IntPtr.Zero;

        /// <summary>completed 标志在原生 HandleState 中的字节偏移（refCount 之后，见下方 ABI 说明）。</summary>
        private const int CompletedFlagOffset = 4;

        /// <summary>
        /// 直接读原生 HandleState 的 <c>completed</c> 标志：不做 P/Invoke、不 flush 隐式批。
        /// ABI 依据：HandleState 前 8 字节为 <c>atomic&lt;uint32_t&gt; refCount</c>（偏移 0）
        /// + <c>atomic&lt;bool&gt; completed</c>（偏移 4，1 字节），JobSystem.h 显式声明"C# 侧仅读前 8 字节"；
        /// 偏移与字节宽度由 JobSystem_State.cpp 的 static_assert 钉住。
        /// ⚠ 必须按 1 字节 读：偏移 5 是 <c>backendRetired</c>（恒为 1），读 4 字节整数会把它的 1
        /// 一并读进来 ⇒ 永远"已完成"。
        /// 安全性：box 自身持有一个引用（refCount ≥ 1），读取期间该 state 不可能被 RecycleState
        /// 回收；标志只单向 0→1，单字节读不会撕裂。
        /// 语义偏保守：隐式批尚未提交、或非 native 句柄时返回 false ⇒ 调用方退化为"不提前回收"。
        /// </summary>
        internal readonly bool IsCompletedFast
        {
            get
            {
                IntPtr state = Handle;
                if (state == IntPtr.Zero) return false;
                unsafe { return Volatile.Read(ref *(byte*)((byte*)state + CompletedFlagOffset)) != 0; }
            }
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
        // 性能项 2：不再额外 `new object()` 作 _gate，直接以 box 自身为锁。
        // 每 job 省一次 24 B 的小对象分配（10k jobs/frame ⇒ 240 KB/frame 的额外分配 +
        // 同一把锁而引入死锁的可能；锁的临界区内容与加锁语义与改动前逐位一致。
        private IntPtr _handle;

        public NativeJobHandleBox(IntPtr handle) => _handle = handle;

        // 单字原子读：等价于原 `lock (_gate) return _handle;`（8 字节对齐的 IntPtr 读不会撕裂），
        // 但省掉 Monitor.Enter/Exit —— 该 getter 在热路径上被 IsValid/Equals/Complete 反复调用。
        public IntPtr Handle => Volatile.Read(ref _handle);

        public IntPtr RetainForUse()
        {
            lock (this)
            {
                if (_handle != IntPtr.Zero)
                    NativeJobScheduler.RetainRawHandleForUse(_handle);
                return _handle;
            }
        }

        public IntPtr Detach()
        {
            IntPtr handle;
            lock (this)
            {
                handle = _handle;
                _handle = IntPtr.Zero;
            }
            // 性能项 2：句柄已被确定性消费（JobHandle.Complete → Release → Detach）后，
            // 使 10k jobs/frame 的 box 不再进入终结队列（避免其被提升 + 终结器线程批量处理）。
            // 语义不变：抑制后终结器路径本就不产生任何副作用。
            if (handle != IntPtr.Zero)
                GC.SuppressFinalize(this);
            return handle;
        }

        ~NativeJobHandleBox()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
                NativeJobScheduler.ReleaseRawHandleForFinalizer(handle);
        }
    }
}
