using System.Runtime.CompilerServices;
using System.Threading;

namespace EntJoy.JobSystem.Managed
{
    /// <summary>
    /// 无锁有界 MPMC 队列（Jeff Preshing 版，per-slot sequence 仲裁）。
    /// capacity 向上取整为 2 的幂；每个槽位用独立的 sequence 数组做 CAS 仲裁，
    /// 生产者/消费者分别从环形两侧推进 head/tail。无锁、无持有锁。
    ///
    /// 正确性要点：
    ///   - 初始化 buffer[i].Seq = i（槽位 i 就绪等待排在第 i 位的生产）
    ///   - 入队：CAS 槽位 seq 由 pos → pos+1 抢占写权，写数据后 release 发布
    ///   - 出队：CAS 槽位 seq 由 pos+1 → pos+1+capacity 抢占读权，读数据后推进
    /// </summary>
    internal sealed class ManagedMPMCQueue<T> where T : struct
    {
        private const int CacheLineSize = 128;

        // 用独立 long 数组存 sequence，避免 struct 字段不可原地写的限制
        private readonly long[] _seq;
        private readonly T[] _data;
        private readonly long _mask;
        private readonly int _capacity;

        // head/tail 用 padding 隔离缓存行，降低伪共享
        private long _enqueuePos;
        private readonly byte[] _pad1 = new byte[CacheLineSize];
        private long _dequeuePos;
        private readonly byte[] _pad2 = new byte[CacheLineSize];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ManagedMPMCQueue(int capacity)
        {
            int c = 2;
            while (c < capacity) c <<= 1;
            _capacity = c;
            _mask = c - 1;
            _seq = new long[c];
            _data = new T[c];
            for (int i = 0; i < c; i++)
                _seq[i] = i;            // 槽位 i 就绪
        }

        /// <summary>尝试入队。满返回 false。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryEnqueue(in T item)
        {
            while (true)
            {
                long pos = Volatile.Read(ref _enqueuePos);
                long idx = pos & _mask;
                long seq = Volatile.Read(ref _seq[idx]);

                if (seq == pos)
                {
                    // 槽位空、可写：CAS 抢占 pos → pos+1
                    if (Interlocked.CompareExchange(ref _enqueuePos, pos + 1, pos) == pos)
                    {
                        _data[idx] = item;
                        Volatile.Write(ref _seq[idx], pos + 1);  // 发布：标记有数据
                        return true;
                    }
                    // 他人抢先推进了 enqueuePos，重读
                }
                else if (seq < pos)
                {
                    // seq < pos：该槽位尚未被消费回到本轮起点 → 队列满
                    return false;
                }
                // seq > pos：槽位数据已被本次环的写者占用 / 尚未消费 → 重读 pos
            }
        }

        /// <summary>尝试出队。空返回 false。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(out T item)
        {
            while (true)
            {
                long pos = Volatile.Read(ref _dequeuePos);
                long idx = pos & _mask;
                long seq = Volatile.Read(ref _seq[idx]);

                if (seq == pos + 1)
                {
                    // 有数据可取：CAS 抢占 dequeuePos → pos+1，槽位 seq 推进一圈
                    if (Interlocked.CompareExchange(ref _dequeuePos, pos + 1, pos) == pos)
                    {
                        item = _data[idx];
                        // 释放槽位供下一圈（pos + capacity）的生产者写。
                        // 必须写 pos + capacity（而非 pos + 1 + capacity）：生产者在第 pos+capacity 轮
                        // 重访同一物理槽时期望看到 _seq[idx] == pos + capacity（即 seq == pos），
                        // 写 pos+1+capacity 会让生产者落入 seq > pos 分支无限自旋。
                        Volatile.Write(ref _seq[idx], pos + _capacity);
                        return true;
                    }
                    // 他人抢先消费，重读
                }
                else if (seq <= pos)
                {
                    // seq == pos：槽位仍空、未被生产 → 队列空
                    item = default;
                    return false;
                }
                // seq > pos+1：本轮槽位已被消费（seq 被推进了一圈）→ 重读
            }
        }

        /// <summary>尽力而为的空判断，用于优雅退出。</summary>
        public bool IsEmpty => Volatile.Read(ref _dequeuePos) >= Volatile.Read(ref _enqueuePos);

        /// <summary>尽力而为的队内元素数估算（诊断用，非精确）。环形落后/并发下可能瞬时失真。</summary>
        internal long DiagnosticCount
        {
            get
            {
                long d = Volatile.Read(ref _dequeuePos);
                long e = Volatile.Read(ref _enqueuePos);
                long n = e - d;
                return n < 0 ? 0 : n;
            }
        }
    }
}