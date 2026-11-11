using System;
using System.Threading;
using EntJoy.JobSystem;

namespace EntJoySample.JobContractsSample
{
    // JobContractsSample —— 三条容易踩错的运行时契约，各给一个最小可跑例子：
    //   1) 异常归属：抛异常的 job 只在它自己的句柄上重抛，依赖它的 job 不继承。
    //   2) 隐式批：两后端都支持；native 攒到 EndFrame 一次提交，managed 是 Add 即调度。
    //   3) 依赖组合：先合并多个句柄，再把合并结果挂给下游。
    // 契约细节见 docs/public/Runtime-Contracts-and-Known-Limitations.md。

    public struct ThrowingJob : IJob
    {
        public void Execute() => throw new InvalidOperationException("boom-from-job");
    }

    public struct CounterJob : IJob
    {
        public int[] Slot;
        public void Execute() => Interlocked.Increment(ref Slot[0]);
    }

    // 隐式批要求 job 是 unmanaged（不含托管引用），所以这里用裸指针而不是 int[]。
    public unsafe struct FillJob : IJobParallelFor
    {
        public int* Values;
        public void Execute(int index) => Values[index] = index * 2;
    }

    public sealed class JobContractsSample
    {
        public void Run()
        {
            JobScheduler.Initialize(2);
            try
            {
                Console.WriteLine($"[后端] native={JobScheduler.IsNative} workers={JobScheduler.WorkerCount}");

                ShowExceptionOwnership();
                ShowImplicitBatch();
                ShowCombinedDependency();
            }
            finally
            {
                JobScheduler.Shutdown();
            }
        }

        // 上游 job 抛出的异常留在它自己的句柄上；下游照常执行，Complete 不抛。
        private static void ShowExceptionOwnership()
        {
            var failing = new ThrowingJob();
            var failed = JobScheduler.Schedule(ref failing);

            var counter = new int[1];
            var downstream = new CounterJob { Slot = counter };
            var dependent = JobScheduler.Schedule(ref downstream, failed);

            try
            {
                failed.Complete();
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"[1] 上游句柄重抛：{ex.Message}");
            }

            dependent.Complete();
            Console.WriteLine($"[1] 下游未被牵连，counter={counter[0]}");
        }

        // 隐式批把一帧内的多个调度合并成一次提交；native 走 defer 窗口，managed 是 Add 即调度。
        private static unsafe void ShowImplicitBatch()
        {
            var values = stackalloc int[16];
            var job = new FillJob { Values = values };

            ImplicitBatch.SetEnabled(true);
            try
            {
                ImplicitBatch.AddParallelFor(ref job, 16, 4);
                ImplicitBatch.EndFrame();
                ImplicitBatch.CompleteAll();   // 句柄在手；这行之前不能离开当前栈帧

                Console.WriteLine($"[2] 隐式批完成（native={JobScheduler.IsNative}）：values[15]={values[15]}");
            }
            finally
            {
                ImplicitBatch.SetEnabled(false);
            }
        }

        // 两个独立 job 合并成一个依赖，下游等它们都完成后再跑。
        private static void ShowCombinedDependency()
        {
            var first = new int[1];
            var second = new int[1];
            var jobA = new CounterJob { Slot = first };
            var jobB = new CounterJob { Slot = second };
            var handleA = JobScheduler.Schedule(ref jobA);
            var handleB = JobScheduler.Schedule(ref jobB);
            var combined = JobHandle.CombineDependencies(handleA, handleB);

            var third = new int[1];
            var jobC = new CounterJob { Slot = third };
            var handleC = JobScheduler.Schedule(ref jobC, combined);
            handleC.Complete();

            Console.WriteLine($"[3] 组合依赖后下游完成：a={first[0]} b={second[0]} c={third[0]}");
        }
    }
}
