using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EntJoy.JobSystem.Managed
{
    /// <summary>
    /// Chase-Lev 无锁双端队列 — 持久 per-worker 使用。
    ///
    /// 经典 Chase-Lev 协议（owner-only PushBottom/PopBottom）：
    ///   - Owner 从 bottom 端 PushBottom / PopBottom（无竞争，bottom_ 非原子）
    ///   - Thief 从 top 端 StealTop（CAS 竞争，低频）
    ///   - 容量固定（2 的幂），top/bottom 用 long 版本号，物理不可能回绕，免 ABA。
    ///
    /// 原子序（对齐 crossbeam-deque 的 stamp 校验）：
    ///   - top_    : Interlocked（thief CAS）
    ///   - bottom_ : 普通字段（仅 owner 写）
    ///   - _seq[]  : Volatile.Write（release）/ Volatile.Read（acquire）
    ///   - PopBottom: bottom_ 写后 + Thread.MemoryBarrier() 阻断 store→load 重排
    ///                （对齐 C++ 的 atomic_thread_fence(seq_cst)，
    ///                 这是修复 Native 105 轮死锁的关键修复）
    ///
    /// 使用方式（crossbeam 标准模型）：
    ///   每个 Worker 持有一个 ManagedWorkStealingDeque（owner-only 操作）。
    ///   跨线程提交经 Injector（MPMC 队列）→ worker 拉取到自己的 deque
    ///   （owner-only PushBottom）→ 标准 Chase-Lev 循环。deque 本身无跨线程 push。
    /// </summary>
    internal sealed class ManagedWorkStealingDeque
    {
        // ───── 常量 ─────
        private const int MinCapacity = 8;

        // ───── 数据 ─────
        private readonly ManagedTileTask[] _buffer;  // 环形数组
        private readonly long[] _seq;                // 每 slot 的发布序号（对齐 crossbeam stamp）
        private readonly int _capacity;
        private readonly int _mask;

        /// <summary>
        /// 顶端索引（thief CAS 修改）。
        /// 对齐 C++ SparseTileDeque::top_ (std::atomic&lt;uint64_t&gt;)。
        /// </summary>
        private long _top;

        /// <summary>
        /// 底端索引（仅 owner 写，非原子）。
        /// 对齐 C++ SparseTileDeque::bottom_ (uint64_t，非 atomic)。
        /// </summary>
        private long _bottom;

        // ───── 构造 ─────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ManagedWorkStealingDeque(int capacity = 4096)
        {
            _capacity = RoundUpPow2(capacity < MinCapacity ? MinCapacity : capacity);
            _mask = _capacity - 1;
            _buffer = new ManagedTileTask[_capacity];
            _seq = new long[_capacity];
            // 初始化 seq[i] = i（槽位 i 就绪等待排在第 i 位的生产）
            for (int i = 0; i < _capacity; i++)
                _seq[i] = i;
            _top = 0;
            _bottom = 0;
        }

        // ───── Owner（worker 线程，唯一调用者）─────

        /// <summary>
        /// Owner 从底端推入一个任务（无竞争）。
        /// 对齐 C++ SparseTileDeque::PushBottom。
        ///
        /// 顺序：
        ///   1. 写 _buffer[b] = task
        ///   2. Volatile.Write(_seq[b], b+1) — release 发布数据
        ///   3. _bottom = b+1 — 推进底端
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void PushBottom(ManagedTileTask task)
        {
            long b = _bottom;
            int idx = (int)(b & _mask);
            _buffer[idx] = task;
            // release store：确保 task 数据在 seq 发布前对其他线程可见
            Volatile.Write(ref _seq[idx], b + 1);
            _bottom = b + 1;
        }

        /// <summary>
        /// Owner 从底端弹出一个任务（无竞争，但需处理与 Steal 的孤儿元素竞争）。
        /// 对齐 C++ SparseTileDeque::PopBottom。
        ///
        /// 【关键】x86 允许 store→load 重排：`_bottom = b` 是普通 store，
        /// 会滞留在 store buffer；紧随的 `_top` 读取可能提前执行、
        /// 读到陈旧的 _top —— owner 据此把 b 判为"非最后元素"直接取走，
        /// 而一个已读过旧 _bottom 的 thief 同时 CAS 认领同一槽位 → 双执行。
        /// 缺少此 fence 是 C++ 实现依赖链 105 轮偶发死锁（~11/30）的根因。
        ///
        /// crossbeam 在 back store 与 front load 之间插入 SeqCst fence 阻断该重排。
        /// C# 等价：Thread.MemoryBarrier()（full fence，对齐 atomic_thread_fence(seq_cst)）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool PopBottom(out ManagedTileTask task)
        {
            long b = _bottom - 1;
            _bottom = b;

            // 阻断 x86 store→load 重排（对齐 C++ atomic_thread_fence(seq_cst)）：
            // 防止 `_bottom = b` 后读到陈旧 _top 而双认领槽位。
            Thread.MemoryBarrier();

            long t = Volatile.Read(ref _top);

            if (b >= t)
            {
                int idx = (int)(b & _mask);

                // 数据发布校验（owner 自身写入恒通过；防御竞争窗口）
                if (Volatile.Read(ref _seq[idx]) != b + 1)
                {
                    _bottom = b + 1;
                    task = default;
                    return false;
                }

                task = _buffer[idx];

                if (b == t)
                {
                    // 最后一个元素——与 StealTop 竞争（Chase-Lev 孤儿元素协议）
                    if (Interlocked.CompareExchange(ref _top, t + 1, t) != t)
                    {
                        // thief 先抢走了 → 恢复 bottom
                        _bottom = t + 1;
                        task = default;
                        return false;
                    }
                    // Chase-Lev 关键：bottom 同步到 t+1
                    _bottom = t + 1;
                }
                return true;
            }

            // b < t：deque 空（bottom 被 thief 推进过）
            _bottom = b + 1;
            task = default;
            return false;
        }

        // ───── Thief（其他 worker / 主线程）─────

        /// <summary>
        /// Thief 从顶端窃取一个任务（CAS 竞争，低频）。
        /// 对齐 C++ SparseTileDeque::StealTop。
        ///
        /// CAS 前 acquire 校验数据已完整发布（PushBottom 顺序是 data → seq(release)，
        /// CAS 成功后才发现未发布则任务已丢失）；CAS 失败（其他 thief 抢先）
        /// 重试有限次数，提高高并发窃取成功率。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool StealTop(out ManagedTileTask task)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                long t = Volatile.Read(ref _top);
                long b = Volatile.Read(ref _bottom);
                if (t >= b)
                {
                    task = default;
                    return false;
                }

                int idx = (int)(t & _mask);

                // CAS 前 acquire 校验数据已完整发布（对齐 crossbeam stamp 校验）
                if (Volatile.Read(ref _seq[idx]) != t + 1)
                {
                    // 数据未发布（push 进行中）：放弃本次 steal
                    task = default;
                    return false;
                }

                // CAS 抢占 top → t+1
                if (Interlocked.CompareExchange(ref _top, t + 1, t) == t)
                {
                    task = _buffer[idx];
                    return true;
                }
                // CAS 失败：其他 thief 抢先，重试
            }
            task = default;
            return false;
        }

        // ───── 查询 ─────

        /// <summary>deque 是否为空（尽力而为，thief 并发时可能读到过期值）。</summary>
        public bool IsEmpty => Volatile.Read(ref _top) >= Volatile.Read(ref _bottom);

        /// <summary>元素数量近似值（诊断用）。</summary>
        public int ApproxSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                long t = Volatile.Read(ref _top);
                long b = Volatile.Read(ref _bottom);
                long sz = b - t;
                return sz > 0 ? (int)sz : 0;
            }
        }

        /// <summary>容量（诊断用）。</summary>
        public int Capacity => _capacity;

        // ───── 工具 ─────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RoundUpPow2(int v)
        {
            if (v == 0) return 1;
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            return v + 1;
        }
    }
}
