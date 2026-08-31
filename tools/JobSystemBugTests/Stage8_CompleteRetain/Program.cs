using System;
using System.Threading;
using System.Threading.Tasks;
using EntJoy.JobSystem;

static class Program
{
    private static int _passed;

    static void Main()
    {
        Run("concurrent Complete/Release aliases do not crash", TestConcurrentCompleteRelease);
        Run("Complete keeps waiting while an alias is released", TestCompleteWaitsDespiteConcurrentRelease);
        Console.WriteLine($"PASS Stage8: {_passed}/2");
        Environment.Exit(_passed == 2 ? 0 : 1);
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    // Complete 与 Release 共享同一个 Box（struct 副本别名）。修复前 Complete
    // 只读 h.Handle 不 retain，另一线程 Release 可在读 handle 与 P/Invoke 之间
    // 把 native state 回收到 0，导致悬垂。修复后 Complete 在等待窗口持有 retain。
    private static void TestConcurrentCompleteRelease()
    {
        NativeJobScheduler.Initialize(4);
        try
        {
            for (int iter = 0; iter < 4000; iter++)
            {
                var job = new NopJob();
                var handle = NativeJobScheduler.Schedule(ref job);
                var copy = handle;
                var completer = Task.Run(() => NativeJobScheduler.Complete(ref copy));
                var releaser = Task.Run(() => NativeJobScheduler.Release(handle));
                if (!Task.WaitAll(new[] { completer, releaser }, 2000))
                    throw new InvalidOperationException("Complete/Release timed out");
            }
        }
        finally
        {
            NativeJobScheduler.Shutdown();
        }
    }

    // 阻塞 job：waiter 在 Complete 等待期间，主线程释放原 handle 别名。
    // 修复后 waiter 持有的 retain 保证 state 存活，job 放行后 Complete 正常返回。
    private static void TestCompleteWaitsDespiteConcurrentRelease()
    {
        NativeJobScheduler.Initialize(2);
        ManualResetEventSlim? release = null;
        try
        {
            using var entered = new ManualResetEventSlim(false);
            release = new ManualResetEventSlim(false);
            var job = new BlockingJob { Entered = entered, Release = release };
            var handle = NativeJobScheduler.Schedule(ref job);
            var copy = handle;

            var waiter = Task.Run(() => NativeJobScheduler.Complete(ref copy));
            Require(entered.Wait(2000), "job did not start");
            NativeJobScheduler.Release(handle);
            release.Set();
            Require(waiter.Wait(2000), "Complete timed out after alias release");
            Require(NativeJobScheduler.IsCompleted(copy), "copy not completed");
        }
        finally
        {
            release?.Set();
            NativeJobScheduler.Shutdown();
            release?.Dispose();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private struct NopJob : IJob
    {
        public void Execute() { }
    }

    private struct BlockingJob : IJob
    {
        public ManualResetEventSlim Entered;
        public ManualResetEventSlim Release;

        public void Execute()
        {
            Entered.Set();
            Release.Wait();
        }
    }
}
