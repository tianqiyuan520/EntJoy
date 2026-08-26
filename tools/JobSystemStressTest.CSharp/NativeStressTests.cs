// NativeStressTests — C# → C++ NativeJobScheduler 压力测试
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;

namespace JobSystemStressTest;

[StructLayout(LayoutKind.Sequential)]
struct NativeIncJob : IJob
{
    public long[] Counter;
    public void Execute() => Interlocked.Increment(ref Counter[0]);
}

[StructLayout(LayoutKind.Sequential)]
struct NativeParallelForJob : IJobParallelFor
{
    public int[] Hits;
    public void Execute(int index) => Interlocked.Increment(ref Hits[index]);
}

[StructLayout(LayoutKind.Sequential)]
struct NativeBatchJob : IJobParallelForBatch
{
    public int[] Hits;
    public void Execute(int startIndex, int count)
    {
        for (int i = startIndex; i < startIndex + count; i++)
            Interlocked.Increment(ref Hits[i]);
    }
}

[StructLayout(LayoutKind.Sequential)]
struct ThrowingNativeJob : IJob
{
    public void Execute() => throw new InvalidOperationException("native exception");
}

static class NativeStressTests
{
    static int _timeoutSec;
    static void RunWithTimeout(string name, Action fn)
    {
        Console.Write($"[RUN ] {name}..."); Console.Out.Flush();
        var sw = Stopwatch.StartNew();
        var task = Task.Run(fn);
        if (task.Wait(TimeSpan.FromSeconds(_timeoutSec)))
            Console.WriteLine($" PASS ({sw.ElapsedMilliseconds}ms)");
        else { Console.WriteLine($" FAIL (timeout)"); Environment.FailFast($"TIMEOUT: {name}"); }
    }
    static void Require(bool c, string m) { if (!c) throw new Exception($"[FAIL] {m}"); }

    public static void RunAll(int timeoutSec, bool longMode)
    {
        _timeoutSec = timeoutSec;
        JobScheduler.Initialize(0);
        RunWithTimeout("N1. MassiveScheduleComplete (50K)", Test_MassiveScheduleComplete);
        RunWithTimeout("N2. DeepDependencyChain (500)", Test_DependencyChain);
        RunWithTimeout("N3. ConcurrentSchedule (8 threads)", Test_ConcurrentSchedule);
        RunWithTimeout("N4. ParallelFor (50K)", Test_ParallelFor);
        RunWithTimeout("N5. ParallelForBatch (50K)", Test_ParallelForBatch);
        RunWithTimeout("N6. ExceptionPropagation", Test_ExceptionPropagation);
        RunWithTimeout("N7. DiamondDependency (100)", Test_DiamondDependency);
        RunWithTimeout("N8. FanOutFanIn (8x8)", Test_FanOutFanIn);
        RunWithTimeout("N9. ZeroLength (3K)", Test_ZeroLength);
        RunWithTimeout("N10. TinyJobStorm (200K)", Test_TinyJobStorm);
        RunWithTimeout("N11. MixedJobTypes (30K)", Test_MixedJobTypes);
        RunWithTimeout("N12. CombineDependencies (100)", Test_CombineDependencies);
        RunWithTimeout("N13. VariousBatchSizes", Test_VariousBatchSizes);
        if (longMode) RunWithTimeout("N14. LongRunning (30s)", Test_LongRunning);
    }

    static void Test_MassiveScheduleComplete()
    {
        long[] counter = [0];
        for (int i = 0; i < 50_000; i++)
        {
            var j = new NativeIncJob { Counter = counter };
            var h = NativeJobScheduler.Schedule(ref j);
            NativeJobScheduler.Complete(ref h);
        }
        Require(counter[0] == 50_000, $"N1: {counter[0]}");
    }

    static void Test_DependencyChain()
    {
        long[] counter = [0];
        var j0 = new NativeIncJob { Counter = counter };
        var prev = NativeJobScheduler.Schedule(ref j0);
        for (int i = 1; i < 500; i++)
        {
            var j = new NativeIncJob { Counter = counter };
            var h = NativeJobScheduler.Schedule(ref j, prev);
            NativeJobScheduler.Release(prev);
            prev = h;
        }
        NativeJobScheduler.Complete(ref prev);
        Require(counter[0] == 500, $"N2: {counter[0]}");
    }

    static void Test_ConcurrentSchedule()
    {
        long[] counter = [0];
        var barrier = new Barrier(8);
        Parallel.For(0, 8, _ =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < 5000; i++)
            {
                var j = new NativeIncJob { Counter = counter };
                var h = NativeJobScheduler.Schedule(ref j);
                NativeJobScheduler.Complete(ref h);
            }
        });
    }

    static void Test_ParallelFor()
    {
        const int N = 50_000;
        int[] hits = new int[N];
        var j = new NativeParallelForJob { Hits = hits };
        var h = NativeJobScheduler.ScheduleParallelFor(ref j, N, 0);
        NativeJobScheduler.Complete(ref h);
        int miss = 0;
        for (int i = 0; i < N; i++) if (Volatile.Read(ref hits[i]) != 1) miss++;
        Require(miss == 0, $"N4: {miss} missed");
    }

    static void Test_ParallelForBatch()
    {
        const int N = 50_000;
        int[] hits = new int[N];
        var j = new NativeBatchJob { Hits = hits };
        var h = NativeJobScheduler.ScheduleParallelForBatch(ref j, N, 256);
        NativeJobScheduler.Complete(ref h);
        int miss = 0;
        for (int i = 0; i < N; i++) if (Volatile.Read(ref hits[i]) != 1) miss++;
        Require(miss == 0, $"N5: {miss} missed");
    }

    static void Test_ExceptionPropagation()
    {
        int caught = 0;
        var handles = new NativeJobHandle[500];
        for (int i = 0; i < 500; i++)
        {
            if (i % 2 == 0) { var j = new ThrowingNativeJob(); handles[i] = NativeJobScheduler.Schedule(ref j); }
            else { var j = new NativeIncJob { Counter = [0] }; handles[i] = NativeJobScheduler.Schedule(ref j); }
        }
        for (int i = 0; i < handles.Length; i++)
        {
            try { NativeJobScheduler.Complete(ref handles[i]); }
            catch { caught++; }
        }
        Require(caught >= 125, $"N6: {caught}/500");
    }

    static void Test_DiamondDependency()
    {
        for (int r = 0; r < 100; r++)
        {
            long[] c = [0];
            var ja = new NativeIncJob { Counter = c }; var a = NativeJobScheduler.Schedule(ref ja);
            var jb = new NativeIncJob { Counter = c }; var b = NativeJobScheduler.Schedule(ref jb, a);
            var jc = new NativeIncJob { Counter = c }; var cc = NativeJobScheduler.Schedule(ref jc, a);
            var jd = new NativeIncJob { Counter = c };
            var d = NativeJobScheduler.Schedule(ref jd, NativeJobScheduler.CombineDependencies(new[] { b, cc }));
            NativeJobScheduler.Complete(ref d);
            NativeJobScheduler.Release(d); NativeJobScheduler.Release(b);
            NativeJobScheduler.Release(cc); NativeJobScheduler.Release(a);
            Require(c[0] == 4, $"N7: {c[0]}");
        }
    }

    static void Test_FanOutFanIn()
    {
        for (int r = 0; r < 50; r++)
        {
            long[] c = [0];
            var jr = new NativeIncJob { Counter = c }; var root = NativeJobScheduler.Schedule(ref jr);
            var prev = new[] { root };
            for (int d = 0; d < 8; d++)
            {
                var curr = new NativeJobHandle[8];
                for (int w = 0; w < 8; w++)
                {
                    var dep = NativeJobScheduler.CombineDependencies(prev);
                    var j = new NativeIncJob { Counter = c };
                    curr[w] = NativeJobScheduler.Schedule(ref j, dep);
                    NativeJobScheduler.Release(dep);
                }
                foreach (var h in prev) NativeJobScheduler.Release(h);
                prev = curr;
            }
            var sd = NativeJobScheduler.CombineDependencies(prev);
            var js = new NativeIncJob { Counter = c };
            var sink = NativeJobScheduler.Schedule(ref js, sd);
            NativeJobScheduler.Complete(ref sink);
            NativeJobScheduler.Release(sink); NativeJobScheduler.Release(sd);
            foreach (var h in prev) NativeJobScheduler.Release(h);
            Require(c[0] == 1 + 8 * 8 + 1, $"N8: {c[0]}");
        }
    }

    static void Test_ZeroLength()
    {
        for (int i = 0; i < 3000; i++)
        {
            var j = new NativeParallelForJob { Hits = [0] };
            var h = NativeJobScheduler.ScheduleParallelFor(ref j, 0, 0);
            NativeJobScheduler.Complete(ref h);
        }
    }

    static void Test_TinyJobStorm()
    {
        long[] counter = [0];
        for (int i = 0; i < 200_000; i++)
        {
            var j = new NativeIncJob { Counter = counter };
            var h = NativeJobScheduler.Schedule(ref j);
            NativeJobScheduler.Complete(ref h);
        }
        Require(counter[0] == 200_000, $"N10: {counter[0]}");
    }

    static void Test_MixedJobTypes()
    {
        for (int i = 0; i < 30_000; i++)
        {
            switch (i % 3)
            {
                case 0: var j0 = new NativeIncJob { Counter = [0] }; var h0 = NativeJobScheduler.Schedule(ref j0); NativeJobScheduler.Complete(ref h0); break;
                case 1: var j1 = new NativeParallelForJob { Hits = new int[100] }; var h1 = NativeJobScheduler.ScheduleParallelFor(ref j1, 100, 0); NativeJobScheduler.Complete(ref h1); break;
                case 2: var j2 = new NativeBatchJob { Hits = new int[500] }; var h2 = NativeJobScheduler.ScheduleParallelForBatch(ref j2, 500, 50); NativeJobScheduler.Complete(ref h2); break;
            }
        }
    }

    static void Test_CombineDependencies()
    {
        for (int r = 0; r < 100; r++)
        {
            long[] c = [0];
            var ja = new NativeIncJob { Counter = c }; var a = NativeJobScheduler.Schedule(ref ja);
            var jb = new NativeIncJob { Counter = c }; var b = NativeJobScheduler.Schedule(ref jb);
            var jc = new NativeIncJob { Counter = c }; var cc = NativeJobScheduler.Schedule(ref jc);
            var jd = new NativeIncJob { Counter = c }; var d = NativeJobScheduler.Schedule(ref jd);
            var combined = NativeJobScheduler.CombineDependencies(new[] { a, b, cc, d });
            var js = new NativeIncJob { Counter = c };
            var sink = NativeJobScheduler.Schedule(ref js, combined);
            NativeJobScheduler.Complete(ref sink);
            NativeJobScheduler.Release(sink); NativeJobScheduler.Release(combined);
            NativeJobScheduler.Release(a); NativeJobScheduler.Release(b);
            NativeJobScheduler.Release(cc); NativeJobScheduler.Release(d);
            Require(c[0] == 5, $"N12: {c[0]}");
        }
    }

    static void Test_VariousBatchSizes()
    {
        foreach (int len in new[] { 1, 10, 64, 1024, 4096, 65536 })
        foreach (int batch in new[] { 0, 1, 7, 16, 128 })
        {
            int[] hits = new int[len];
            var j = new NativeBatchJob { Hits = hits };
            var h = NativeJobScheduler.ScheduleParallelForBatch(ref j, len, batch);
            NativeJobScheduler.Complete(ref h);
            int miss = 0;
            for (int i = 0; i < len; i++) if (Volatile.Read(ref hits[i]) != 1) miss++;
            Require(miss == 0, $"N13: len={len} batch={batch}: {miss} missed");
        }
    }

    static void Test_LongRunning()
    {
        var stop = new CancellationTokenSource(); long total = 0;
        var tasks = new Task[8];
        for (int t = 0; t < 8; t++)
            tasks[t] = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    int len = 1 + (Environment.CurrentManagedThreadId * 137 % 5000);
                    int[] h = new int[len]; var j = new NativeParallelForJob { Hits = h };
                    var handle = NativeJobScheduler.ScheduleParallelFor(ref j, len, 0);
                    NativeJobScheduler.Complete(ref handle);
                    Interlocked.Increment(ref total);
                }
            });
        Thread.Sleep(30_000); stop.Cancel(); Task.WaitAll(tasks);
        Console.WriteLine($"  LongRunning: {total} rounds in 30s");
    }
}
