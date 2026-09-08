using System;
using EntJoy.Collections;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 完整双向并行读写拦截：job 引用的原生容器（每容器独立句柄）在其活跃期间，
    /// 主线程对它的任何访问（读/写）都被拦截。覆盖 job 写期间主线程读、job 读期间主线程写/读。
    /// </summary>
    public class ParallelReadWriteContainmentTests
    {
        // job 忙等写：放大执行窗，使主线程在 job 活跃期间有机会插队访问
        private struct WriteJob : IJobParallelFor
        {
            public NativeArray<long> Data;
            public void Execute(int index)
            {
                for (int j = 0; j < 4; j++) System.Threading.Thread.SpinWait(8);
                long v = Data[index];
                Data[index] = v + 1;
            }
        }

        // job 忙等读：放大执行窗
        private struct ReadJob : IJobParallelFor
        {
            public NativeArray<long> Data;
            public void Execute(int index)
            {
                for (int j = 0; j < 4; j++) System.Threading.Thread.SpinWait(8);
                _ = Data[index];
            }
        }

        private const int N = 2048;
        private const int InnerBatch = 128;

        static ParallelReadWriteContainmentTests()
        {
            JobScheduler.Initialize();
        }

        private static bool ContainsMessage(Exception? ex, string fragment)
        {
            while (ex != null)
            {
                if (ex.Message.Contains(fragment, StringComparison.Ordinal))
                    return true;
                ex = ex.InnerException;
            }
            return false;
        }

        /// <summary>job 写容器期间，主线程读 → 拦（主线程读 vs 写者）。</summary>
        [Fact]
        public void MainThreadRead_WhileJobWrites_Throws()
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                using var data = new NativeArray<long>(N, Allocator.Persistent);
                var h = new WriteJob { Data = data }.Schedule(N, InnerBatch);   // 不 Complete，让 job 保持活跃
                var ex = Record.Exception(() =>
                {
                    for (int k = 0; k < N; k++) _ = data[k];   // 主线程读
                });
                h.Complete();
                if (ex != null && ContainsMessage(ex, "being written by an active job"))
                    return;
            }
            Assert.Fail("Main-thread read during job write was not caught in any of 40 attempts.");
        }

        /// <summary>job 读容器期间，主线程写 → 拦（主线程写 vs 读者）。</summary>
        [Fact]
        public void MainThreadWrite_WhileJobReads_Throws()
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                var data = new NativeArray<long>(N, Allocator.Persistent);
                try
                {
                    var h = new ReadJob { Data = data }.Schedule(N, InnerBatch);   // 不 Complete
                    var ex = Record.Exception(() =>
                    {
                        for (int k = 0; k < N; k++) data[k] = k;   // 主线程写
                    });
                    h.Complete();
                    if (ex != null && ContainsMessage(ex, "being read by an active job"))
                        return;
                }
                finally { data.Dispose(); }
            }
            Assert.Fail("Main-thread write during job read was not caught in any of 40 attempts.");
        }

        /// <summary>job 读容器期间，主线程读 → 拦（主线程读 vs 读者）。</summary>
        [Fact]
        public void MainThreadRead_WhileJobReads_Throws()
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                using var data = new NativeArray<long>(N, Allocator.Persistent);
                var h = new ReadJob { Data = data }.Schedule(N, InnerBatch);   // 不 Complete
                var ex = Record.Exception(() =>
                {
                    for (int k = 0; k < N; k++) _ = data[k];   // 主线程读
                });
                h.Complete();
                if (ex != null && ContainsMessage(ex, "being read by an active job"))
                    return;
            }
            Assert.Fail("Main-thread read during job read was not caught in any of 40 attempts.");
        }

        /// <summary>依赖串行化：前一 job Complete 后再访问，不应被拦。</summary>
        [Fact]
        public void DependencyComplete_NoFalsePositive()
        {
            var data = new NativeArray<long>(N, Allocator.Persistent);
            try
            {
                var h = new WriteJob { Data = data }.Schedule(N, InnerBatch);
                h.Complete();
                // Complete 后主线程访问不受限
                for (int k = 0; k < N; k++) data[k] = k;
                long s = 0;
                for (int k = 0; k < N; k++) s += data[k];
                Assert.Equal((long)N * (N - 1) / 2, s);
            }
            finally { data.Dispose(); }
        }

        /// <summary>
        /// 读者计数不应泄漏的回归护栏：同一容器反复被「并行读 job」使用（多 tile 并发登记/释放读）。
        /// 若 RegisterRead 与 ReleaseReadsForContext 并发时 +1 落进已移除的 ctx 集合，
        /// _readerCount 会永久泄漏 → job 完整 Complete 后主线程读仍被拦。
        /// 泄漏是持续态（一旦发生恒失败），故一旦触发本测试必然失败。
        /// </summary>
        [Fact]
        public void RepeatedParallelReadJobs_NoReaderCountLeak()
        {
            const int iters = 20000;
            var data = new NativeArray<long>(128, Allocator.Persistent);
            try
            {
                for (int t = 0; t < iters; t++)
                {
                    var h = new ReadJob { Data = data }.Schedule(128, 4);
                    h.Complete();
                    // Complete 后主线程读必须放行；若仍被拦 → 读者计数泄漏
                    _ = data[0];
                }
            }
            finally { data.Dispose(); }
        }
    }
}