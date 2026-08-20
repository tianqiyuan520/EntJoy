using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace EntJoy.JobSystem.Managed
{
    /// <summary>
    /// 托管 Job 句柄。用原子计数（Remaining）跟踪一个 job 的完成状态。
    /// 完全独立于原生 NativeJobScheduler，可脱离 NativeDll 使用。
    /// 非线程安全的字段仅为主线程初始化用；完成标志用原子操作。
    /// </summary>
    public struct ManagedJobHandle
    {
        internal ManagedCompletion Completion;
        internal int Gen; // 分配时的槽位代际快照：槽位被复用（归还后重租）时代际变化，旧 handle 据此判定过期，杜绝 ABA 误操作

        internal ManagedJobHandle(ManagedCompletion completion) { Completion = completion; Gen = completion.Generation; }

        /// <summary>是否已完成（Remaining == 0；若槽位已被复用，本 handle 视为已完成/过期）。</summary>
        public bool IsCompleted => IsExpired || Volatile.Read(ref Completion.Remaining) == 0;

        /// <summary>阻塞等待完成。等待后尝试回池复用（若由托管调度器分配）。</summary>
        public void Complete()
        {
            var c = Completion;
            if (c == null || IsExpired) return; // 槽位已复用 → 本 handle 过期，避免误操作新 job（防 ABA）
            ManagedJobScheduler.CompleteSchedule(c);
            // 等待期间本 completion 可能已被“自动归还”并立即被另一 job 重租（代际已 +1）。
            // 二次代际校验：若已过期则绝不再归还，杜绝双重释放/误操作新 job（防 ABA 双重归还）。
            if (IsExpired) return;
            var ex = c.ReadException();   // 先在归还前取出异常（ReturnCompletion 的 Reset 会清空）
            ManagedJobScheduler.ReturnCompletion(c);
            if (ex != null) ExceptionDispatchInfo.Capture(ex).Throw();
        }

        /// <summary>槽位是否已被复用（代际不匹配）→ 本 handle 过期，不再有效引用原 job。</summary>
        internal bool IsExpired => Completion == null || Volatile.Read(ref Completion.Generation) != Gen;

        /// <summary>合并多个依赖：返回 vs. 所有输入都完成才算完成。</summary>
        public static ManagedJobHandle CombineDependencies(ManagedJobHandle[] handles)
        {
            if (handles == null || handles.Length == 0)
                return default;
            if (handles.Length == 1)
                return handles[0];

            var combined = ManagedJobScheduler.RentCompletion();
            combined.Remaining = handles.Length;
            foreach (var h in handles)
            {
                // 依赖槽位若已被复用（过期）视为已完成，直接计入；否则挂回调等其完成
                if (!h.IsExpired && h.Completion != null)
                    h.Completion.OnCompleted(combined.Signal);
                else
                    combined.Signal();
            }
            return new ManagedJobHandle(combined);
        }
    }

    /// <summary>
    /// 完成计数体（class 引用）。原子递减 Remaining，归零时触发完成回调。
    /// </summary>
    public sealed class ManagedCompletion
    {
        // 原子完成计数。正值 = 未完成任务数；归零表示完成。
        internal int Remaining = 1;
        private Action _onComplete;   // 原子累积（OnCompleted 用 CAS）；完成时快照执行
        private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);

        // 预分配池管理字段（由 ManagedJobScheduler 管理）
        internal int SlotIndex = -1;   // 在预分配槽位池中的索引（-1 = 池外兜底分配）
        internal int NextFree = -1;    // 自由栈链表的下一个索引（-1 = 栈尾）
        internal int Generation;       // 槽位代际：每次被 Rent 分配新 job 时递增；旧 handle 存快照以判过期（防 ABA）
        internal int _returned;        // 1=已归还/自动归还（幂等防 double-return）。归还后保持 1，Rent 新 job 时清 0。
        internal int _autoReturn;      // 1=此 job 完成后由调度器自动归还（依赖链中间 handle，防泄漏）；一律经 Volatile.Read/Write 访问
        internal Exception _exception; // 首个 job 异常（first-wins），供异常传播；Reset 时清空

        internal ManagedCompletion()
        {
        }

        /// <summary>记录首个 job 异常（并发安全：多个分片/线程竞争时只保留第一个）。</summary>
        internal void RecordException(Exception ex)
        {
            if (ex != null) Interlocked.CompareExchange(ref _exception, ex, null);
        }

        /// <summary>读取已记录的异常（无则 null）。</summary>
        internal Exception ReadException() => Volatile.Read(ref _exception);

        /// <summary>重置为可复用状态。不清 _returned/Geration（归还后保持，直到被新 job Rent 时更新）。</summary>
        internal void Reset()
        {
            Remaining = 1;
            _onComplete = null;
            _exception = null;
            _done.Reset();
            _autoReturn = 0;
        }

        /// <summary>注册完成回调（原子累积，防菱形依赖并发注册丢回调）。若已完成后注册则立即调用。</summary>
        internal void OnCompleted(Action callback)
        {
            while (true)
            {
                var cur = Volatile.Read(ref _onComplete);
                var next = cur == null ? callback : (Action)Delegate.Combine(cur, callback);
                if (Interlocked.CompareExchange(ref _onComplete, next, cur) == cur)
                    break;
            }
            if (IsCompleted) callback();
        }

        /// <summary>任务片段完成；Remaining 减到 0 时触发完成信号，并（若标记自动归还）回池。</summary>
        internal void Signal()
        {
            if (Interlocked.Decrement(ref Remaining) == 0)
            {
                _done.Set();
                var c = Volatile.Read(ref _onComplete);
                if (c != null)
                {
                    Volatile.Write(ref _onComplete, null); // 立即清，防回调引用驻留
                    c();
                }
                // 依赖链中间 handle：完成后由完成线程自动归还，避免只等末端 handle 导致的连中部 completion 泄漏。
                if (Volatile.Read(ref _autoReturn) == 1 && Volatile.Read(ref _returned) == 0)
                    ManagedJobScheduler.AutoReturnCompletion(this);
            }
        }

        /// <summary>当前是否已完成</summary>
        internal bool IsCompleted => Volatile.Read(ref Remaining) == 0;

        /// <summary>阻塞等待完成。使用 ManualResetEventSlim，零 CPU 自旋。</summary>
        internal void Wait()
        {
            if (IsCompleted) return;
            _done.Wait();
        }
    }
}