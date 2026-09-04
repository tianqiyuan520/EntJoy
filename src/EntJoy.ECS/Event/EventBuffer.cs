using System;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// C++ 侧 EventBuffer 的 POD 描述（与 __EntJoyEventBuffer 一一对应）。
    /// 实际非托管内存由 <see cref="ChunkJobScheduler.AllocateEventBuffers"/> 分配，
    /// drain 时经 <see cref="EventStream{T}.DrainFromBuffer"/> 拷贝进双缓冲事件流。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct EventBufferHeader
    {
        public IntPtr dataPtr;     // T* 数据数组
        public IntPtr countPtr;    // int* 原子计数
        public int capacity;       // 最大容量
        public int elementSize;    // sizeof(T)
    }
}
