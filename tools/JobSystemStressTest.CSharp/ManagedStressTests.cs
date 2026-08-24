// ManagedStressTests — 纯 C# ManagedJobScheduler 压力测试
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;

namespace JobSystemStressTest;

struct IncJob : IJob
{
    public long[] Counter;
    public void Execute() => Interlocked.Increment(ref Counter[0]);
}

struct ParallelForIncJob : IJobParallelFor
{
    public int[] Hits;
    public void Execute(int index) => Interlocked.Increment(ref Hits[index]);
}

struct ThrowingJob : IJob
{
    public void Execute() => throw new InvalidOperationException("stress test exception");
}

struct HeavyJob : IJobParallelFor
{
    public double[] Data;
    public void Execute(int index)
    {
        double val = 0;
        for (int i = 0; i < 100; i++) val += Math.Sin(index + i);
        Volatile.Write(ref Data[index], val);
    }
}

static class ManagedStressTests
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
        ManagedJobScheduler.Initialize(0);
        RunWithTimeout("M1. MassiveScheduleComplete (100K)", Test_MassiveScheduleComplete);
        RunWithTimeout("M2. DeepDependencyChain (1000)", Test_DependencyChain);
        RunWithTimeout("M3. ConcurrentSchedule (8 threads)", Test_ConcurrentSchedule);
        RunWithTimeout("M4. ParallelFor (100K)", Test_ParallelFor);
        RunWithTimeout("M5. ParallelForBatch (100K)", Test_ParallelForBatch);
        RunWithTimeout("M6. ExceptionPropagation", Test_ExceptionPropagation);
        RunWithTimeout("M7. DiamondDependency (200)", Test_DiamondDependency);
        RunWithTimeout("M8. FanOutFanIn (8x10)", Test_FanOutFanIn);
        RunWithTimeout("M9. ZeroLength (5K)", Test_ZeroLength);
        RunWithTimeout("M10. TinyJobStorm (500K)", Test_TinyJobStorm);
        RunWithTimeout("M11. MixedJobTypes (50K)", Test_MixedJobTypes);
        RunWithTimeout("M12. HeavyParallelFor (50K)", Test_HeavyParallelFor);
        RunWithTimeout("M13. CombineDependencies (200)", Test_CombineDependencies);
        if (longMode) RunWithTimeout("M14. LongRunning (30s)", Test_LongRunning);
    }

    static void Test_MassiveScheduleComplete()
    {
        long[] counter = [0];
        for (int i = 0; i < 100_000; i++)
        {
            var j = new IncJob { Counter = counter };
            ManagedJobScheduler.Schedule(ref j).Complete();
        }
        Require(counter[0] == 100_000, $"M1: {counter[0]}");
    }

    static void Test_DependencyChain()
    {
        long[] counter = [0];
        var j0 = new IncJob { Counter = counter };
        var prev = ManagedJobScheduler.Schedule(ref j0);
        for (int i = 1; i < 1000; i++)
        {
            var j = new IncJob { Counter = counter };
            prev = ManagedJobScheduler.Schedule(ref j, prev);
        }
        prev.Complete();
        Require(counter[0] == 1000, $"M2: {counter[0]}");
    }

    static void Test_ConcurrentSchedule()
    {
        long[] counter = [0];
        var barrier = new Barrier(8);
        Parallel.For(0, 8, _ =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < 10_000; i++)
            {
                var j = new IncJob { Counter = counter };
                ManagedJobScheduler.Schedule(ref j).Complete();
            }
        });
    }

    static void Test_ParallelFor()
    {
        const int N = 100_000;
        int[] hits = new int[N];
        var j = new ParallelForIncJob { Hits = hits };
        ManagedJobScheduler.Schedule(ref j, N, 0).Complete();
        int miss = 0;
        for (int i = 0; i < N; i++) if (Volatile.Read(ref hits[i]) != 1) miss++;
        Require(miss == 0, $"M4: {miss} missed");
    }

    static void Test_ParallelForBatch()
    {
        const int N = 100_000;
        int[] hits = new int[N];
        var j = new ParallelForIncJob { Hits = hits };
        ManagedJobScheduler.Schedule(ref j, N, 256).Complete();
        int miss = 0;
        for (int i = 0; i < N; i++) if (Volatile.Read(ref hits[i]) != 1) miss++;
        Require(miss == 0, $"M5: {miss} missed");
    }

    static void Test_ExceptionPropagation()
    {
        int caught = 0;
        var handles = new ManagedJobHandle[1000];
        for (int i = 0; i < 1000; i++)
        {
            if (i % 2 == 0) { var j = new ThrowingJob(); handles[i] = ManagedJobScheduler.Schedule(ref j); }
            else { var j = new IncJob { Counter = [0] }; handles[i] = ManagedJobScheduler.Schedule(ref j); }
        }
        foreach (var h in handles) try { h.Complete(); } catch { caught++; }
        Require(caught >= 250, $"M6: {caught}/1000");
        long[] c = [0]; var j2 = new IncJob { Counter = c };
        ManagedJobScheduler.Schedule(ref j2).Complete();
        Require(c[0] == 1, "M6: broken");
    }

    static void Test_DiamondDependency()
    {
        for (int r = 0; r < 200; r++)
        {
            long[] c = [0];
            var ja = new IncJob { Counter = c }; var a = ManagedJobScheduler.Schedule(ref ja);
            var jb = new IncJob { Counter = c }; var b = ManagedJobScheduler.Schedule(ref jb, a);
            var jc = new IncJob { Counter = c }; var cc = ManagedJobScheduler.Schedule(ref jc, a);
            var jd = new IncJob { Counter = c };
            var d = ManagedJobScheduler.Schedule(ref jd, ManagedJobHandle.CombineDependencies([b, cc]));
            d.Complete();
            Require(c[0] == 4, $"M7: {c[0]}");
        }
    }

    static void Test_FanOutFanIn()
    {
        for (int r = 0; r < 100; r++)
        {
            long[] c = [0];
            var jr = new IncJob { Counter = c }; var root = ManagedJobScheduler.Schedule(ref jr);
            var prev = new[] { root };
            for (int d = 0; d < 10; d++)
            {
                var curr = new ManagedJobHandle[8];
                for (int w = 0; w < 8; w++)
                {
                    var dep = ManagedJobHandle.CombineDependencies(prev);
                    var j = new IncJob { Counter = c };
                    curr[w] = ManagedJobScheduler.Schedule(ref j, dep);
                }
                prev = curr;
            }
            var sd = ManagedJobHandle.CombineDependencies(prev);
            var js = new IncJob { Counter = c };
            ManagedJobScheduler.Schedule(ref js, sd).Complete();
            Require(c[0] == 1 + 10 * 8 + 1, $"M8: {c[0]}");
        }
    }

    static void Test_ZeroLength()
    {
        for (int i = 0; i < 5000; i++)
        {
            int[] c = [0]; var j = new ParallelForIncJob { Hits = c };
            ManagedJobScheduler.Schedule(ref j, 0, 0).Complete();
            Require(c[0] == 0, "M9: should not execute");
        }
    }

    static void Test_TinyJobStorm()
    {
        long[] counter = [0];
        for (int i = 0; i < 500_000; i++)
        {
            var j = new IncJob { Counter = counter };
            ManagedJobScheduler.Schedule(ref j).Complete();
        }
        Require(counter[0] == 500_000, $"M10: {counter[0]}");
    }

    static void Test_MixedJobTypes()
    {
        for (int i = 0; i < 50_000; i++)
        {
            switch (i % 4)
            {
                case 0: var j0 = new IncJob { Counter = [0] }; ManagedJobScheduler.Schedule(ref j0).Complete(); break;
                case 1: var j1 = new ParallelForIncJob { Hits = new int[100] }; ManagedJobScheduler.Schedule(ref j1, 100, 0).Complete(); break;
                case 2: var j2 = new ParallelForIncJob { Hits = new int[1000] }; ManagedJobScheduler.Schedule(ref j2, 1000, 50).Complete(); break;
                case 3: var j3 = new ThrowingJob(); try { ManagedJobScheduler.Schedule(ref j3).Complete(); } catch { } break;
            }
        }
    }

    static void Test_HeavyParallelFor()
    {
        double[] data = new double[50_000];
        var j = new HeavyJob { Data = data };
        ManagedJobScheduler.Schedule(ref j, 50_000, 0).Complete();
        int zeros = 0;
        for (int i = 0; i < 50_000; i++) if (Volatile.Read(ref data[i]) == 0) zeros++;
        Require(zeros == 0, $"M12: {zeros} zeros");
    }

    static void Test_CombineDependencies()
    {
        for (int r = 0; r < 200; r++)
        {
            long[] c = [0];
            var ja = new IncJob { Counter = c }; var a = ManagedJobScheduler.Schedule(ref ja);
            var jb = new IncJob { Counter = c }; var b = ManagedJobScheduler.Schedule(ref jb);
            var jc = new IncJob { Counter = c }; var cc = ManagedJobScheduler.Schedule(ref jc);
            var jd = new IncJob { Counter = c }; var d = ManagedJobScheduler.Schedule(ref jd);
            var combined = ManagedJobHandle.CombineDependencies([a, b, cc, d]);
            long[] sc = [0]; var js = new IncJob { Counter = sc };
            ManagedJobScheduler.Schedule(ref js, combined).Complete();
            Require(sc[0] == 1, $"M13: {sc[0]}");
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
                    int len = 1 + (Environment.CurrentManagedThreadId * 137 % 10000);
                    int[] h = new int[len]; var j = new ParallelForIncJob { Hits = h };
                    ManagedJobScheduler.Schedule(ref j, len, 0).Complete();
                    Interlocked.Increment(ref total);
                }
            });
        Thread.Sleep(30_000); stop.Cancel(); Task.WaitAll(tasks);
        Console.WriteLine($"  LongRunning: {total} rounds in 30s");
    }
}
