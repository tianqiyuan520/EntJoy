using System;
using System.Runtime.CompilerServices;

namespace EntJoy.JobSystem.Managed
{
    /// <summary>
    /// Chase-Lev 调度器的范围任务结构体（对齐 C++ TileTask + RangeTask）。
    ///
    /// 存储在 ManagedWorkStealingDeque 的结构体数组中：
    ///   - Owner（worker）通过 PushBottom/PopBottom 操作
    ///   - Thief（其他 worker）通过 StealTop 操作
    ///
    /// 含引用字段（Job/Runner/Completion），GC 会扫描。
    /// 由 ManagedTileTaskPool 池化管理，热路径零分配。
    /// </summary>
    internal struct ManagedTileTask
    {
        /// <summary>盒化的 job 对象（泛型装箱后持有）。</summary>
        public object Job;

        /// <summary>执行委托：Runner(job, start, count)。</summary>
        public ManagedJobScheduler.JobRunner Runner;

        /// <summary>释放委托：执行完成后回收 job 盒。null 表示由 completion.OnCompleted 释放。</summary>
        public Action<object>? Release;

        /// <summary>完成计数体。每个 task 执行完后 Signal() 一次，Remaining 归零时触发完成。</summary>
        public ManagedCompletion? Completion;

        /// <summary>范围起始索引（inclusive）。</summary>
        public int Start;

        /// <summary>范围长度（元素个数）。</summary>
        public int Count;

        /// <summary>在 ManagedTileTaskPool 中的槽位索引（-1 = 非池内）。Acquire 时写入，Release 时用于定位。</summary>
        public int PoolIndex;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ManagedTileTask(
            object job,
            ManagedJobScheduler.JobRunner runner,
            Action<object>? release,
            ManagedCompletion? completion,
            int start,
            int count,
            int poolIndex = -1)
        {
            Job = job;
            Runner = runner;
            Release = release;
            Completion = completion;
            Start = start;
            Count = count;
            PoolIndex = poolIndex;
        }
    }
}
