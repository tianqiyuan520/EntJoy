using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace EntJoy.Collections
{
    /// <summary>
    /// 页锁定（pinned）内存注册表。
    /// 用途：CUDA cuMemAllocHost / D3D12 页锁定堆等"CPU 可直写、GPU 可直读"的宿主机内存，
    /// 经 <see cref="NativeArray{T}.FromExternalPtr"/> 包成 NativeArray 视图后，把指针登记在此，
    /// 供 GPU 调度（ScheduleCuda 等）识别：上传/回读可直接对该指针做单跳传输，免去 C# 侧拷贝。
    /// 注意：登记是进程期标记（GPU pinned buffer 本为缓存/常驻形态）；Unregister 由拥有方在释放时显式调用。
    /// </summary>
    public static unsafe class PinnedMemory
    {
        private static readonly ConcurrentDictionary<IntPtr, byte> _pinned = new();

        /// <summary>标记外部指针为页锁定内存（FromExternalPtr(pinned:true) 自动调用；重复登记幂等）</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Register(void* ptr)
        {
            if (ptr != null) _pinned[(IntPtr)ptr] = 0;
        }

        /// <summary>释放页锁定内存后取消登记（拥有方负责调用）</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Unregister(void* ptr)
        {
            if (ptr != null) _pinned.TryRemove((IntPtr)ptr, out _);
        }

        /// <summary>指针是否登记为页锁定内存</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPinned(void* ptr) => ptr != null && _pinned.ContainsKey((IntPtr)ptr);
    }
}
