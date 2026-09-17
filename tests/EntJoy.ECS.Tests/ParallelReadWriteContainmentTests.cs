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
        // ────────────────────────────────────────────────────────────────────────────
        // 确定性握手：并行读写声明是「job 内首次访问时」惰性登记的
        //   写：索引器写 → SafetyHandleManager.TryAcquireWriteContext（_writerCtx[index] = ctx）
        //   读：CheckReadAndThrow（job 内 ctx != 0）→ RegisterRead（_readerCount[index]++）
        // 因此「Schedule 后主线程立刻访问」本身存在竞态：主线程可能在任何 worker 完成首次访问之前
        // 就把整个访问循环跑完，此时没有任何声明可拦（CI 上实测 40 次尝试全部落空 ⇒ 假失败）。
        // 这里改为显式握手：job 完成首次访问（声明已登记）后置 s_claimReady 并驻留，
        // 主线程等到该信号后再访问 ⇒ 拦截必然发生，无需重试。
        // s_holdJob 的驻留有界（<= 3s），即使将来 job 被内联到调用线程执行也不会死锁。
        // ────────────────────────────────────────────────────────────────────────────
        private static int s_claimReady;
        private static int s_holdJob;

        private static bool WaitForClaimReady(int timeoutMs = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (System.Threading.Volatile.Read(ref s_claimReady) == 0 && sw.ElapsedMilliseconds < timeoutMs)
                System.Threading.Thread.SpinWait(64);
            return System.Threading.Volatile.Read(ref s_claimReady) != 0;
        }

        /// <summary>job 侧：首次访问（即声明登记）之后调用；通知主线程并驻留到主线程放行。</summary>
        private static void HoldAfterFirstAccess()
        {
            if (System.Threading.Volatile.Read(ref s_holdJob) == 0) return;
            System.Threading.Volatile.Write(ref s_claimReady, 1);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (System.Threading.Volatile.Read(ref s_holdJob) != 0 && sw.ElapsedMilliseconds < 3000)
                System.Threading.Thread.SpinWait(64);
        }

        private static void ReleaseHeldJob() => System.Threading.Volatile.Write(ref s_holdJob, 0);

        /// <summary>
        /// 主线程访问期间的确定性断言：等到声明登记 → 访问必须被拦 → 放行 job 并 Complete。
        /// </summary>
        private static void AssertMainThreadAccessThrows(NativeArray<long> data, JobHandle handle, string fragment)
        {
            bool ready = WaitForClaimReady();
            Exception? ex = null;
            try
            {
                ex = Record.Exception(() =>
                {
                    for (int k = 0; k < data.Length; k++) _ = data[k];
                });
            }
            finally
            {
                ReleaseHeldJob();
                handle.Complete();
            }
            Assert.True(ready, "job 未在超时内完成首次访问（声明未登记）——job 是否根本没被并发执行？");
            Assert.NotNull(ex);
            Assert.True(ContainsMessage(ex, fragment), $"拦截信息不含期望片段：{ex!.Message}");
        }

        // job 忙等写：首次访问即登记写声明，之后驻留（由测试放行）
        private struct WriteJob : IJobParallelFor
        {
            public NativeArray<long> Data;
            public void Execute(int index)
            {
                long v = Data[index];
                Data[index] = v + 1;
                HoldAfterFirstAccess();
            }
        }

        // job 忙等读：首次访问即登记读者，之后驻留
        private struct ReadJob : IJobParallelFor
        {
            public NativeArray<long> Data;
            public void Execute(int index)
            {
                _ = Data[index];
                HoldAfterFirstAccess();
            }
        }

        private const int N = 2048;
        private const int InnerBatch = 128;

        // Span 路径：AsSpan() 只在取 Span 时做一次检查并登记读者（循环内零检查）
        private struct SpanReadJob : IJobParallelFor
        {
            public NativeArray<long> Data;
            public void Execute(int index)
            {
                var span = Data.AsSpan();
                _ = span[index];
                HoldAfterFirstAccess();
            }
        }

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
            using var data = new NativeArray<long>(N, Allocator.Persistent);
            System.Threading.Volatile.Write(ref s_claimReady, 0);
            System.Threading.Volatile.Write(ref s_holdJob, 1);
            try
            {
                var h = new WriteJob { Data = data }.Schedule(N, InnerBatch);   // 不 Complete，让 job 保持活跃
                AssertMainThreadAccessThrows(data, h, "being written by an active job");
            }
            finally { ReleaseHeldJob(); }
        }

        /// <summary>job 读容器期间，主线程写 → 拦（主线程写 vs 读者）。</summary>
        [Fact]
        public void MainThreadWrite_WhileJobReads_Throws()
        {
            var data = new NativeArray<long>(N, Allocator.Persistent);
            System.Threading.Volatile.Write(ref s_claimReady, 0);
            System.Threading.Volatile.Write(ref s_holdJob, 1);
            try
            {
                var h = new ReadJob { Data = data }.Schedule(N, InnerBatch);   // 不 Complete
                bool ready = WaitForClaimReady();
                Exception? ex = null;
                try
                {
                    ex = Record.Exception(() =>
                    {
                        for (int k = 0; k < N; k++) data[k] = k;   // 主线程写
                    });
                }
                finally
                {
                    ReleaseHeldJob();
                    h.Complete();
                }
                Assert.True(ready, "job 未在超时内登记读者（声明未登记）。");
                Assert.NotNull(ex);
                Assert.True(ContainsMessage(ex, "being read by an active job"), ex!.Message);
            }
            finally
            {
                ReleaseHeldJob();
                data.Dispose();
            }
        }

        /// <summary>job 读容器期间，主线程读 → 拦（主线程读 vs 读者）。</summary>
        [Fact]
        public void MainThreadRead_WhileJobReads_Throws()
        {
            using var data = new NativeArray<long>(N, Allocator.Persistent);
            System.Threading.Volatile.Write(ref s_claimReady, 0);
            System.Threading.Volatile.Write(ref s_holdJob, 1);
            try
            {
                var h = new ReadJob { Data = data }.Schedule(N, InnerBatch);   // 不 Complete
                AssertMainThreadAccessThrows(data, h, "being read by an active job");
            }
            finally { ReleaseHeldJob(); }
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

        /// <summary>
        /// Span 路径不得绕过并行读登记：job 通过 AsSpan() 访问容器时，
        /// 主线程在 job 活跃期间读仍须被拦（否则 use-after-free 检测被静默削弱）。
        /// </summary>
        [Fact]
        public void MainThreadAccess_WhileSpanJobReads_Throws()
        {
            var data = new NativeArray<long>(N, Allocator.Persistent);
            System.Threading.Volatile.Write(ref s_claimReady, 0);
            System.Threading.Volatile.Write(ref s_holdJob, 1);
            try
            {
                var h = new SpanReadJob { Data = data }.Schedule(N, InnerBatch);   // 不 Complete，保持活跃
                AssertMainThreadAccessThrows(data, h, "being read by an active job");
            }
            finally
            {
                ReleaseHeldJob();
                data.Dispose();
            }
        }

        /// <summary>
        /// 写者声明不得残留：多 tile 并发的写 job，Complete() 后主线程访问必须全部放行。
        /// 回归护栏（曾复现）：写声明若在 tile 级释放，先结束的 tile 会把同 ctx 仍在运行 tile 的登记
        /// 写进一份随后被丢弃的 list，释放时扫不到该 index，_writerCtx 永久残留该 ctx，主线程访问被永久误拦。
        /// 泄漏一旦发生是持续态（后续尝试恒失败），故本测试必然失败。
        /// </summary>
        [Fact]
        public void MultiTileWriteJob_AfterComplete_MainThreadNotBlocked()
        {
            const int len = 32768;
            const int batch = 2048;   // 16 tile：与复现配置同量级

            for (int attempt = 0; attempt < 400; attempt++)
            {
                var data = new NativeArray<long>(len, Allocator.Persistent);
                try
                {
                    for (int j = 0; j < 5; j++)
                    {
                        var h = new WriteJob { Data = data }.Schedule(len, batch);   // 不 Complete，保持活跃
                        // 主线程与 job 竞争：期间必须被拦（有 job 持写）
                        Record.Exception(() =>
                        {
                            for (int k = 0; k < len; k++) _ = data[k];
                        });
                        h.Complete();

                        // Complete 后必须放行 —— 若写者声明残留，这里会抛
                        try
                        {
                            for (int k = 0; k < len; k++) _ = data[k];
                        }
                        catch (Exception ex)
                        {
                            Assert.Fail($"attempt {attempt}/job {j}: Complete() 后主线程访问仍被拦（写者声明残留）: {ex.Message}");
                        }
                    }
                }
                finally { data.Dispose(); }
            }
        }
    }
}
