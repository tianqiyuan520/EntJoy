using System;
using System.Threading;
using EntJoy.Collections;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 契约：**Job 尚未完成时，主线程对 Job 正在读的容器访问必须被拦截**
    /// （docs/public/Runtime-Contracts-and-Known-Limitations.md §并行读写冲突检测：「拦截窗口从未完成 Job 持有容器开始」）。
    ///
    /// 本用例专门盯住「读者声明的释放时点」：一个 Job 被切成多个 tile，它们共享同一个执行上下文（ctx）。
    /// 若某个 tile 结束时就把整个 ctx 的读声明释放掉，而兄弟 tile 还在执行，主线程就会在 Job 未完成时
    /// 拿到「无读者」的假象 → 框架最核心的安全网静默失效（配合 GetUnsafePtr/AsSpan 即真实 data race / UAF）。
    ///
    /// 用例形状（与后端无关，Managed 应在 job 完成点释放 → 本用例在两条后端都必须通过）：
    ///   tile(index=1)：读 Arr[1]（登记读者）→ 置 flag → 自旋（之后不再读 Arr，避免重新登记）
    ///   tile(index=0)：读 Arr[0]（同 ctx，幂等不再登记）→ 等 tile1 已进入 → 返回（其 finally 正是嫌疑点）
    ///   主线程：确认两个 tile 同时在飞、Job 未完成 → 写 Arr[0] → 必须抛「being read by an active job」
    /// </summary>
    public class ReadClaimReleaseTests
    {
        private const int SpinBudget = 400_000_000;

        private unsafe struct ReadThenSpinJob : IJobParallelFor
        {
            public NativeArray<float> Arr;
            public int* Flags; // [0]=tile0 已进入, [1]=tile1 已进入(且已读), [2]=放行

            public void Execute(int index)
            {
                float v = Arr[index]; // 索引器 → 登记读者
                Flags[index] = 1;

                int spin = 0;
                if (index == 0)
                {
                    // 等兄弟 tile 先登记，保证「tile1 在飞」发生在「tile0 结束」之前
                    while (Flags[1] == 0 && ++spin < SpinBudget) Thread.SpinWait(50);
                }
                else
                {
                    // 停在 Job 内部，但不再触碰 Arr：避免把读声明重新登记回去
                    while (Flags[2] == 0 && ++spin < SpinBudget) Thread.SpinWait(50);
                }

                if (v == float.NaN) Console.WriteLine("keep-read-alive");
            }
        }

        [Fact]
        public unsafe void SiblingTileStillRunning_MainThreadAccess_MustBeIntercepted()
        {
            TestBackend.EnsureInitialized();
            Assert.True(JobScheduler.WorkerCount >= 2,
                $"前置条件不满足：构造『兄弟 tile 在飞』需要 >= 2 个执行槽，当前 WorkerCount={JobScheduler.WorkerCount}。");

            var arr = new NativeArray<float>(2, Allocator.Persistent);
            var flags = new NativeArray<int>(3, Allocator.Persistent);
            try
            {
                arr[0] = 1f;
                arr[1] = 2f;
                int* fp = (int*)flags.GetUnsafePtr();

                var job = new ReadThenSpinJob { Arr = arr, Flags = fp };
                var handle = JobScheduler.ScheduleParallelFor(ref job, 2, 1);

                int guard = 0;
                while ((fp[0] == 0 || fp[1] == 0) && ++guard < SpinBudget) Thread.SpinWait(50);
                Assert.True(fp[0] == 1 && fp[1] == 1,
                    "两个 tile 未能在预算内同时进入：本机/后端无法构造该并发场景（用例不作为通过处理）。");

                // 给先完成的 tile 时间跑完它的 finally —— 这正是读者声明可能被提前释放的位置
                Thread.Sleep(300);
                Assert.False(handle.IsCompleted, "tile1 仍在自旋时 Job 不应已完成");

                var ex = Record.Exception(() => arr[0] = 42f);

                fp[2] = 1; // 先放行，避免用例自身挂住
                handle.Complete();

                Assert.NotNull(ex);
                Assert.Contains("being read by an active job", ex!.Message);
            }
            finally
            {
                arr.Dispose();
                flags.Dispose();
            }
        }
    }
}
