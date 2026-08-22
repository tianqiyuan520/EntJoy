using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EntJoy.JobSystem.Managed
{
    /// <summary>
    /// 固定容量的 ManagedTileTask 对象池（无锁空闲栈，对齐 C++ RangeTaskPool）。
    ///
    /// ManagedTileTask 是 Chase-Lev 调度器的任务粒度：每个 task 携带一个 tile 范围
    /// [Start, Start+Count)，由 Injector 分发，worker 从 Injector 拉取
    /// 后推入自己 deque（owner-only PushBottom），标准 Chase-Lev 循环执行。
    ///
    /// 池化设计（Treiber 无锁空闲栈）：
    ///   - 固定容量 PoolSize（16384）
    ///   - Acquire：从空闲栈弹出（CAS 弹栈），空时返回 null
    ///   - Release：压回空闲栈（CAS 压栈），真正回收
    ///   - ABA 防护：64 位 tag（高 32 位 = push/pop 计数，低 32 位 = 栈顶索引）
    ///
    /// 调用方契约：池耗尽时 Acquire 返回 null，必须兜底（池外分配，
    /// PoolIndex=-1，Release 跳过归还）；不可跳过任务。
    /// </summary>
    internal sealed class ManagedTileTaskPool
    {
        public const int PoolSize = 16384;

        private readonly ManagedTileTask[] _storage;
        private readonly int[] _next;         // 空闲栈链表（_next[i] = 栈中 i 的下一个索引，-1 = 栈尾）
        private long _freeHead;               // 低 32 位 = 栈顶索引（-1 = 空栈），高 32 位 = push/pop 计数（ABA 防护）

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ManagedTileTaskPool()
        {
            _storage = new ManagedTileTask[PoolSize];
            _next = new int[PoolSize];
            // 初始空闲栈：所有槽位入栈（倒序，使 Acquire 升序返回）
            int head = -1; // 空栈
            for (int i = 0; i < PoolSize; i++)
            {
                int idx = PoolSize - 1 - i;
                _next[idx] = head;
                head = idx;
            }
            _freeHead = ((long)1 << 32) | (uint)head; // ABA 计数从 1 开始
        }

        /// <summary>
        /// 获取一个 ManagedTileTask 对象。池空时返回 null（调用方必须阻塞重试）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ManagedTileTask? Acquire()
        {
            long head = Volatile.Read(ref _freeHead);
            while (true)
            {
                int idx = (int)head;
                if (idx < 0) return null; // 空栈

                int nextIdx = Volatile.Read(ref _next[idx]);
                long newHead = ((long)(unchecked((int)(head >> 32)) + 1) << 32) | (uint)nextIdx;
                if (Interlocked.CompareExchange(ref _freeHead, newHead, head) == head)
                {
                    var task = _storage[idx];
                    _storage[idx] = default;
                    task.PoolIndex = idx; // 【关键】记录池内索引，Release 才能正确定位槽位
                    return task;
                }
                head = Volatile.Read(ref _freeHead);
            }
        }

        /// <summary>
        /// 归还一个 ManagedTileTask 对象（压回空闲栈，真正回收）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release(ManagedTileTask task)
        {
            int idx = task.PoolIndex;
            if (idx < 0 || idx >= PoolSize) return; // 非池内索引防御

            long head = Volatile.Read(ref _freeHead);
            while (true)
            {
                Volatile.Write(ref _next[idx], (int)head);
                long newHead = ((long)(unchecked((int)(head >> 32)) + 1) << 32) | (uint)idx;
                if (Interlocked.CompareExchange(ref _freeHead, newHead, head) == head)
                    return;
                head = Volatile.Read(ref _freeHead);
            }
        }

        /// <summary>诊断：近似可用数量（尽力而为）。</summary>
        public int ApproxFreeCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int count = 0;
                long head = Volatile.Read(ref _freeHead);
                int idx = (int)head;
                int guard = 0;
                while (idx >= 0 && guard++ < 64)
                {
                    count++;
                    idx = Volatile.Read(ref _next[idx]);
                }
                return count;
            }
        }
    }
}