using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.ECS
{
    /// <summary>
    /// Native Job 可写的事件缓冲区（非托管内存，跨 C#/C++ 共享）。
    /// C++ 侧通过 EventBufferHeader POD struct 访问。
    /// </summary>
    public sealed class EventBuffer<T> : IDisposable where T : unmanaged
    {
        private IntPtr _dataPtr;    // T[] 数据区
        private IntPtr _countPtr;   // int 原子计数
        private int _capacity;
        private int _elementSize;
        private bool _disposed;

        public int Capacity => _capacity;
        public int ElementSize => _elementSize;
        public IntPtr DataPtr => _dataPtr;
        public IntPtr CountPtr => _countPtr;

        public EventBuffer(int capacity = 1024)
        {
            _capacity = capacity;
            _elementSize = Unsafe.SizeOf<T>();
            _dataPtr = Marshal.AllocHGlobal(capacity * _elementSize);
            _countPtr = Marshal.AllocHGlobal(sizeof(int));
            unsafe { *(int*)_countPtr = 0; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Reset()
        {
            *(int*)_countPtr = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe int GetCount()
        {
            return Volatile.Read(ref *(int*)_countPtr);
        }

        public unsafe ReadOnlySpan<T> Read()
        {
            int count = Math.Min(GetCount(), _capacity);
            return new ReadOnlySpan<T>((void*)_dataPtr, count);
        }

        /// <summary>构建 EventBufferHeader（供写入 context block）。</summary>
        internal EventBufferHeader ToHeader()
        {
            return new EventBufferHeader
            {
                dataPtr = _dataPtr,
                countPtr = _countPtr,
                capacity = _capacity,
                elementSize = _elementSize
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_dataPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_dataPtr); _dataPtr = IntPtr.Zero; }
            if (_countPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_countPtr); _countPtr = IntPtr.Zero; }
            _disposed = true;
        }
    }

    /// <summary>
    /// C++ 侧对应的 POD struct（与 __EntJoyEventBuffer 一一对应）。
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
