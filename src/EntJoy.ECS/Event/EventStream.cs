using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EntJoy.ECS
{
    /// <summary>非泛型事件流接口：用于 World 统一管理，避免反射调用。</summary>
    internal interface IEventStream
    {
        void NextFrame();
        unsafe int DrainFromBuffer(void* dataPtr, int count, int expectedElementSize);
    }

    /// <summary>
    /// 双缓冲事件流：零结构变更的系统间消息传递。
    ///
    /// 生命周期：
    ///   帧 N：SendEvent 写 buffer[0]（writeBuffer），writeCount++
    ///   帧末：NextFrame swap，writeCount → readCount，writeCount = 0
    ///   帧 N+1：ReadBuffer 读 buffer[1]（readBuffer），取 readCount 条
    /// </summary>
    public sealed class EventStream<T> : IEventStream, IDisposable where T : unmanaged
    {
        private readonly T[][] _buffers = new T[2][];
        private int _writeCount;   // 本帧已写入数
        private int _readCount;    // 上一帧写入数（swap 后可读）
        private uint _generation;
        private readonly int _capacity;
        private readonly object _sync = new object();

        public uint Generation => Volatile.Read(ref _generation);
        public int Capacity => _capacity;

        public EventStream(int capacity = 1024)
        {
            _capacity = capacity;
            _buffers[0] = new T[capacity];
            _buffers[1] = new T[capacity];
        }

        /// <summary>
        /// 生产者：写入当前帧事件（线程安全，Interlocked 原子计数）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SendEvent(in T evt)
        {
            lock (_sync)
            {
                if (_writeCount >= _capacity) return false;
                _buffers[0][_writeCount++] = evt;
                return true;
            }
        }

        /// <summary>
        /// 消费者：读取上一帧的事件缓冲区（只读 Span）。
        /// 必须在 NextFrame 之后调用才有数据。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<T> ReadBuffer()
        {
            lock (_sync)
            {
                int count = _readCount;
                if (count > _capacity) count = _capacity;
                return new ReadOnlySpan<T>(_buffers[1], 0, count);
            }
        }

        /// <summary>
        /// 帧末：交换缓冲区，将 writeCount 转为 readCount，清零 writeCount。
        /// </summary>
        public void NextFrame()
        {
            lock (_sync)
            {
                var tmp = _buffers[0];
                _buffers[0] = _buffers[1];
                _buffers[1] = tmp;
                _readCount = _writeCount;
                _writeCount = 0;
                _generation++;
            }
        }

        /// <summary>
        /// 从 Native EventBuffer drain：将 count 个 T 从 dataPtr 复制到写入缓冲区。
        /// expectedElementSize：C# 分配 buffer 时记录的 Marshal.SizeOf&lt;T&gt;()。
        /// 若与 T 的真实步长（Unsafe.SizeOf&lt;T&gt;()）不一致，说明 native 侧写的 stride 与 C# 布局
        /// 错位（ISPC uniform struct 对齐 vs C# Sequential），立即失败而不是静默读到错位数据。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe int DrainFromBuffer(void* dataPtr, int count, int expectedElementSize)
        {
            if (count <= 0) return 0;
            int realElementSize = Unsafe.SizeOf<T>();
            if (expectedElementSize != realElementSize)
                throw new InvalidOperationException(
                    $"EventStream<{typeof(T).Name}> element size mismatch: buffer allocated with " +
                    $"{expectedElementSize} bytes, T requires {realElementSize} bytes. " +
                    $"Native event writer stride disagrees with C# layout (ISPC vs Sequential ABI).");
            lock (_sync)
            {
                int toWrite = Math.Min(count, _capacity - _writeCount);
                if (toWrite <= 0) return 0;
                var src = new ReadOnlySpan<T>(dataPtr, toWrite);
                src.CopyTo(new Span<T>(_buffers[0], _writeCount, toWrite));
                _writeCount += toWrite;
                return toWrite;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _buffers[0] = Array.Empty<T>();
                _buffers[1] = Array.Empty<T>();
                _writeCount = 0;
                _readCount = 0;
            }
        }
    }
}
