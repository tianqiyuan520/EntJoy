using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;

static class Program
{
    private static int _passed;

    static void Main()
    {
        Run("Managed ScheduleFor propagates dependency", TestManagedScheduleForDependency);
        Run("Managed ScheduleBatch propagates dependency", TestManagedScheduleBatchDependency);
        Run("Native handle copies all observe completion", TestNativeHandleCopyCompletion);

        Console.WriteLine($"PASS Stage1: {_passed}/3");
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

    private static void TestManagedScheduleForDependency()
    {
        SetJobSchedulerUseNative(false);
        ManagedJobScheduler.Initialize(2);
        ManualResetEventSlim? release = null;

        try
        {
            using var entered = new ManualResetEventSlim(false);
            release = new ManualResetEventSlim(false);
            var blocker = new BlockingJob { Entered = entered, Release = release };
            var dependency = JobScheduler.Schedule(ref blocker);

            int ran = 0;
            var childJob = new ForProbeJob { Ran = new[] { 0 } };
            var child = JobScheduler.ScheduleFor(ref childJob, 1, dependency);

            Require(entered.Wait(2000), "dependency did not start");
            Thread.Sleep(100);
            Require(Volatile.Read(ref childJob.Ran[0]) == 0,
                "ScheduleFor child ran before dependency completed");

            release.Set();
            CompleteWithTimeout(child, 2000);
            ran = Volatile.Read(ref childJob.Ran[0]);
            Require(ran == 1, $"ScheduleFor ran {ran} times");
        }
        finally
        {
            release?.Set();
            ManagedJobScheduler.Shutdown();
            release?.Dispose();
        }
    }

    private static void TestManagedScheduleBatchDependency()
    {
        SetJobSchedulerUseNative(false);
        ManagedJobScheduler.Initialize(2);
        ManualResetEventSlim? release = null;

        try
        {
            using var entered = new ManualResetEventSlim(false);
            release = new ManualResetEventSlim(false);
            var blocker = new BlockingJob { Entered = entered, Release = release };
            var dependency = JobScheduler.Schedule(ref blocker);

            var childJob = new BatchProbeJob { Ran = new[] { 0 } };
            var child = JobScheduler.ScheduleBatch(ref childJob, 4, 2, dependency);

            Require(entered.Wait(2000), "dependency did not start");
            Thread.Sleep(100);
            Require(Volatile.Read(ref childJob.Ran[0]) == 0,
                "ScheduleBatch child ran before dependency completed");

            release.Set();
            CompleteWithTimeout(child, 2000);
            Require(Volatile.Read(ref childJob.Ran[0]) == 2,
                "ScheduleBatch did not execute expected ranges");
        }
        finally
        {
            release?.Set();
            ManagedJobScheduler.Shutdown();
            release?.Dispose();
        }
    }

    private static void TestNativeHandleCopyCompletion()
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
            Require(entered.Wait(2000), "native job did not start");
            Thread.Sleep(100);
            Require(!NativeJobScheduler.IsCompleted(handle),
                "original handle became invalid/complete after copy Complete");

            release.Set();
            Require(waiter.Wait(2000), "copy Complete timed out");
            Require(NativeJobScheduler.IsCompleted(handle),
                "native job did not complete after release");
            NativeJobScheduler.Release(handle);
        }
        finally
        {
            release?.Set();
            NativeJobScheduler.Shutdown();
            release?.Dispose();
        }
    }

    private static void SetJobSchedulerUseNative(bool value)
    {
        var property = typeof(JobScheduler).GetProperty(
            "UseNative", BindingFlags.Static | BindingFlags.NonPublic);
        property?.SetValue(null, value);
    }

    private static void CompleteWithTimeout(JobHandle handle, int timeoutMs)
    {
        var task = Task.Run(handle.Complete);
        Require(task.Wait(timeoutMs), "Complete timed out");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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

    private struct ForProbeJob : IJobFor
    {
        public int[] Ran;
        public void Execute(int index) => Interlocked.Increment(ref Ran[index]);
    }

    private struct BatchProbeJob : IJobParallelForBatch
    {
        public int[] Ran;
        public void Execute(int startIndex, int count) => Interlocked.Add(ref Ran[0], count / 2);
    }
}
