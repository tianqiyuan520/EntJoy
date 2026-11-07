using System;
using System.Runtime.CompilerServices;

namespace EntJoy.JobSystem.Managed
{
    /// <summary>
    /// Chase-Lev 调度器的范围任务结构体（对齐 C++ TileTask + RangeTask）。
    ///
    /// 存储在 ManagedWorkStealingDeque 的结构体数组中：
    /// - Owner（worker）通过 PushBottom/PopBottom 操作
    /// - Thief（其他 worker）通过 StealTop 操作
    ///
    /// 含引用字段（Job/Runner/Completion），GC 会扫描。
    /// 按值存储于固定容量队列，描述符本身不产生堆分配。
    /// </summary>
    internal struct ManagedTileTask
    {
        /// <summary>盒化的直接任务，或持有 payload 的批次。</summary>
        public object Job;

        /// <summary>执行委托：Runner(job, start, count)。</summary>
        public ManagedJobScheduler.JobRunner Runner;

        /// <summary>直接任务的回收委托；空值由完成回调或批次退役处理。</summary>
        public Action<object>? Release;

        /// <summary>完成计数体。每个 task 执行完后 Signal() 一次，Remaining 归零时触发完成。</summary>
        public ManagedCompletion? Completion;

        /// <summary>直接任务的范围起点，或令牌优先处理的切片编号。</summary>
        public int Start;

        /// <summary>直接任务的范围长度；认领令牌不使用此值。</summary>
        public int Count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ManagedTileTask(
            object job,
            ManagedJobScheduler.JobRunner runner,
            Action<object>? release,
            ManagedCompletion? completion,
            int start,
            int count)
        {
            Job = job;
            Runner = runner;
            Release = release;
            Completion = completion;
            Start = start;
            Count = count;
        }
    }
}
