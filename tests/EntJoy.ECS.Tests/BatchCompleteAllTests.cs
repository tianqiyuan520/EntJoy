using System;
using System.Threading;
using EntJoy.Collections;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 契约：`BatchScope.CompleteAll()` / `ImplicitBatch.CompleteAll()` 必须**等待整批完成**
    /// （docs/public/Runtime-Contracts-and-Known-Limitations.md §Job 依赖和 Complete：
    /// 「Job 抛出的异常只在对应句柄 Complete 时传播；调用方必须完成需要观察异常的句柄」）。
    ///
    /// 缺陷形状：两个 CompleteAll 的实现都用 `if (h._nativeHandle.IsValid) h.Complete();` 守卫，
    /// 而 JobHandle.Complete() 本身是后端无关的 ⇒ 在 **Managed 回退后端**（NativeDll 缺失/ABI 不匹配时
    /// 官方支持的自动回退路径）句柄只带 `_managedHandle`，整个循环空转：既不等、也不抛。
    /// 后果是调用方以为已同步，随后 Dispose/读取 NativeArray → 与仍在运行的 worker 竞争（UAF/错值）。
    /// </summary>
    public class BatchCompleteAllTests
    {
        private struct ThrowingJob : IJob
        {
            public void Execute() => throw new InvalidOperationException("batch-job-boom");
        }

        private struct SlowWriteJob : IJob
        {
            public NativeArray<int> Target;
            public void Execute()
            {
                Thread.Sleep(120);
                Target[0] = 1; // 走索引器 → 登记写声明（这样未等待时主线程访问会被拦截，暴露问题）
            }
        }

        // ───────────────────────── BatchScope ─────────────────────────

        [Fact]
        public void BatchScope_CompleteAll_SurfacesJobExceptions()
        {
            TestBackend.EnsureInitialized();

            using var batch = new BatchScope();
            var job = new ThrowingJob();
            batch.Add(ref job);

            var ex = Record.Exception(() => batch.CompleteAll());

            Assert.NotNull(ex);
            Assert.Contains("batch-job-boom", ex!.ToString());
        }

        [Fact]
        public void BatchScope_CompleteAll_WaitsForJobs()
        {
            TestBackend.EnsureInitialized();

            var data = new NativeArray<int>(1, Allocator.Persistent);
            try
            {
                using var batch = new BatchScope();
                var job = new SlowWriteJob { Target = data };
                batch.Add(ref job);

                batch.CompleteAll();

                // 未等待时：要么这里抛「being written by an active job」，要么读到 0
                Assert.Equal(1, data[0]);
            }
            finally
            {
                data.Dispose();
            }
        }

        // ───────────────────────── ImplicitBatch ─────────────────────────

        [Fact]
        public void ImplicitBatch_CompleteAll_SurfacesJobExceptions()
        {
            TestBackend.EnsureInitialized();

            ImplicitBatch.SetEnabled(true);
            try
            {
                var job = new ThrowingJob();
                ImplicitBatch.Add(ref job);
                ImplicitBatch.EndFrame();

                var ex = Record.Exception(ImplicitBatch.CompleteAll);

                Assert.NotNull(ex);
                Assert.Contains("batch-job-boom", ex!.ToString());
            }
            finally
            {
                ImplicitBatch.SetEnabled(false);
            }
        }

        [Fact]
        public void ImplicitBatch_CompleteAll_WaitsForJobs()
        {
            TestBackend.EnsureInitialized();

            var data = new NativeArray<int>(1, Allocator.Persistent);
            ImplicitBatch.SetEnabled(true);
            try
            {
                var job = new SlowWriteJob { Target = data };
                ImplicitBatch.Add(ref job);
                ImplicitBatch.EndFrame();

                ImplicitBatch.CompleteAll();

                Assert.Equal(1, data[0]);
            }
            finally
            {
                ImplicitBatch.SetEnabled(false);
                data.Dispose();
            }
        }
    }
}
