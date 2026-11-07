using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.JobSystem.Managed
{
    /// <summary>
    /// 少量令牌先处理各自切片，再窃取剩余逻辑分片。
    /// 任务数据和游标工作区在最后一个令牌退出后归还。
    /// </summary>
    internal sealed class ManagedParallelBatch
    {
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct Slice
        {
            [FieldOffset(0)] internal int Next;
            [FieldOffset(4)] internal int End;
        }

        private object _job;
        private readonly ManagedJobScheduler.JobRunner _runner;
        private readonly Action<object> _releaseJob;
        private readonly ManagedCompletion _completion;
        private readonly int _arrayLength;
        private readonly int _batchSize;
        private readonly int _sliceCount;
        private Slice[] _slices;
        private int _remainingTokens;

        internal ManagedParallelBatch(
            object job,
            ManagedJobScheduler.JobRunner runner,
            Action<object> releaseJob,
            ManagedCompletion completion,
            int arrayLength,
            int batchSize,
            int tileCount,
            int executorCount)
        {
            _job = job;
            _runner = runner;
            _releaseJob = releaseJob;
            _completion = completion;
            _arrayLength = arrayLength;
            _batchSize = batchSize;
            _sliceCount = Math.Min(tileCount, executorCount);
            _remainingTokens = _sliceCount;

            if (tileCount > _sliceCount)
            {
                _slices = ArrayPool<Slice>.Shared.Rent(_sliceCount);
                for (int i = 0; i < _sliceCount; i++)
                {
                    _slices[i].Next = (int)((long)i * tileCount / _sliceCount);
                    _slices[i].End = (int)((long)(i + 1) * tileCount / _sliceCount);
                }
            }
        }

        private void Execute(object job, int tile)
        {
            int start = tile * _batchSize;
            // 每个逻辑分片独立调用 runner，保留 struct 副本和异常边界。
            try
            {
                _runner(job, start, Math.Min(_batchSize, _arrayLength - start));
            }
            catch (Exception ex)
            {
                _completion.RecordException(ex);
            }
        }

        internal void Run(int preferred)
        {
            object job = _job;
            var slices = _slices;
            try
            {
                if (slices == null)
                {
                    Execute(job, preferred);
                    return;
                }
                for (int offset = 0; offset < _sliceCount; offset++)
                {
                    int index = (preferred + offset) % _sliceCount;
                    ref var slice = ref slices[index];
                    while (true)
                    {
                        int next = Volatile.Read(ref slice.Next);
                        if (next >= slice.End) break;
                        if (Interlocked.CompareExchange(ref slice.Next, next + 1, next) != next)
                            continue;
                        Execute(job, next);
                    }
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _remainingTokens) == 0)
                {
                    _job = null;
                    _slices = null;
                    try
                    {
                        if (slices != null) ArrayPool<Slice>.Shared.Return(slices);
                    }
                    finally
                    {
                        _releaseJob(job);
                    }
                }
            }
        }
    }
}
