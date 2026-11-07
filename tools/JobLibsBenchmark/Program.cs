using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;
using MisakiJobs = Misaki.HighPerformance.Jobs;
using PowerThreadPool.Options;

namespace JobLibsBenchmark;

internal interface IFramework : IDisposable
{
    string Name { get; }
    void Dispatch(Action<int> body, int tasks);
}

internal sealed class Framework(string name, Action<Action<int>, int> dispatch, Action? shutdown = null) : IFramework
{
    public string Name => name;
    public void Dispatch(Action<int> body, int tasks) => dispatch(body, tasks);
    public void Dispose() => shutdown?.Invoke();
}

internal struct RangeJob : IJobParallelFor
{
    public Action<int> Body;
    public void Execute(int index) => Body(index);
}

internal sealed class ZeroAllocRangeJob : Schedulers.IJobParallelFor
{
    public Action<int> Body = null!;
    public int BatchSize => 1;
    public int ThreadCount => 0;
    public void Execute(int index) => Body(index);
    public void Finish() { }
}

internal struct MisakiRangeJob : MisakiJobs.IJobParallelFor
{
    public Action<int> Body;
    public void Execute(int index, ref readonly MisakiJobs.JobExecutionContext context) => Body(index);
}

internal readonly record struct Scenario(string Name, int Count, int Grain, int Stage, int Stages = 1)
{
    public int Tasks => (Count + Grain - 1) / Grain;
}

internal sealed class Workload(uint[] data, Scenario scenario)
{
    public int Stage;

    // This exact method is called by every framework, including the serial reference.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Execute(int task)
    {
        int begin = task * scenario.Grain;
        int end = Math.Min(begin + scenario.Grain, scenario.Count);
        unchecked
        {
            switch (Stage)
            {
                case 0: for (int i = begin; i < end; i++) data[i] += 1u; break;
                case 1: for (int i = begin; i < end; i++) data[i] *= 2u; break;
                case 2: for (int i = begin; i < end; i++) data[i] -= 3u; break;
                case 3:
                    for (int i = begin; i < end; i++)
                    {
                        uint x = data[i];
                        for (int j = 0; j < 1000; j++)
                        {
                            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                        }
                        data[i] = x;
                    }
                    break;
                case 4:
                    for (int i = begin; i < end; i++)
                    {
                        uint x = data[i], sum = 0;
                        for (int j = 0; j < 1000; j++)
                        {
                            x = x * 1664525u + 1013904223u;
                            uint r = x % 13u;
                            if (r < 4u) sum += x;
                            else if (r < 8u) sum ^= x;
                            else sum -= x >> 3;
                            if ((x & 7u) == 0u) sum += (uint)j;
                        }
                        data[i] = sum;
                    }
                    break;
            }
        }
    }
}

internal static class Program
{
    internal static uint[] Initial(int count) => Enumerable.Range(0, count)
        .Select(i => unchecked((uint)i * 2654435761u + 1u)).ToArray();

    internal static uint[] Expected(Scenario scenario)
    {
        uint[] expected = Initial(scenario.Count);
        var work = new Workload(expected, scenario);
        for (int repeat = 0; repeat < (scenario.Count == 1 ? 64 : 1); repeat++)
        for (int stage = 0; stage < scenario.Stages; stage++)
        {
            work.Stage = scenario.Stage + stage;
            for (int task = 0; task < scenario.Tasks; task++) work.Execute(task);
        }
        return expected;
    }

    internal static ulong Checksum(uint[] values)
    {
        ulong hash = 14695981039346656037UL;
        foreach (uint value in values) hash = unchecked((hash ^ value) * 1099511628211UL);
        return hash;
    }

    private static IFramework Create(string name, int workers)
    {
        switch (name)
        {
            case "Serial": return new Framework(name, (body, count) => { for (int i = 0; i < count; i++) body(i); });
            case "EntJoy Managed":
                ManagedJobScheduler.Initialize(workers - 1);
                return new Framework(name, (body, count) => {
                    var job = new RangeJob { Body = body };
                    ManagedJobScheduler.Schedule(ref job, count, 1).Complete();
                }, ManagedJobScheduler.Shutdown);
            case "EntJoy NativeCallback":
                NativeJobScheduler.Initialize(workers - 1);
                NativeJobScheduler.SetMainThreadAssistEnabled(true);
                NativeJobScheduler.SetTimingDiagnosticsEnabled(false);
                return new Framework(name, (body, count) => {
                    var job = new RangeJob { Body = body };
                    var handle = NativeJobScheduler.ScheduleParallelFor(ref job, count, 1);
                    NativeJobScheduler.Complete(ref handle);
                }, NativeJobScheduler.Shutdown);
            case "ZeroAlloc":
                var zero = new Schedulers.JobScheduler(new Schedulers.JobScheduler.Config {
                    ThreadPrefixName = "Benchmark", ThreadCount = workers,
                    MaxExpectedConcurrentJobs = 4096, StrictAllocationMode = false
                });
                var zeroJob = new ZeroAllocRangeJob();
                return new Framework(name, (body, count) => {
                    zeroJob.Body = body;
                    var handle = zero.Schedule(zeroJob, count); zero.Flush(); handle.Complete();
                }, zero.Dispose);
            case "Misaki":
                var description = new MisakiJobs.JobSchedulerDesc {
                    ThreadCount = workers - 1, DependencyChainCapacity = 4096, ThreadPriority = ThreadPriority.Normal
                };
                var misaki = new MisakiJobs.JobScheduler(in description);
                return new Framework(name, (body, count) => {
                    var job = new MisakiRangeJob { Body = body };
                    var handle = misaki.ScheduleParallelFor(in job, count, 1);
                    misaki.Wait(handle, inlineExecution: true);
                }, misaki.Dispose);
            case "PowerThreadPool":
                var power = new PowerThreadPool.PowerPool(new PowerPoolOption { MaxThreads = workers - 1 });
                return new Framework(name, (body, count) => {
                    var handles = new PowerThreadPool.Works.WorkID[count];
                    for (int i = 0; i < count; i++)
                    {
                        int task = i;
                        handles[i] = power.QueueWorkItem(() => body(task));
                    }
                    foreach (var handle in handles) power.Wait(handle, helpWhileWaiting: true);
                }, power.Dispose);
            case "ThreadPool":
                return new Framework(name, (body, count) => {
                    using var done = new CountdownEvent(count);
                    for (int i = 0; i < count; i++) {
                        int task = i;
                        ThreadPool.QueueUserWorkItem(_ => { try { body(task); } finally { done.Signal(); } });
                    }
                    done.Wait();
                });
            case "Parallel.For":
                var options = new ParallelOptions { MaxDegreeOfParallelism = workers };
                return new Framework(name, (body, count) => Parallel.For(0, count, options, body));
            case "Task.Run":
                return new Framework(name, (body, count) => {
                    var tasks = new Task[count];
                    for (int i = 0; i < count; i++) { int task = i; tasks[i] = Task.Run(() => body(task)); }
                    Task.WhenAll(tasks).GetAwaiter().GetResult();
                });
            case "Bag+threads":
                return new Framework(name, (body, count) => {
                    var queue = new ConcurrentBag<int>(Enumerable.Range(0, count));
                    RunThreads(Math.Min(workers, count), () => { while (queue.TryTake(out int task)) body(task); });
                });
            case "Channel+threads":
                return new Framework(name, (body, count) => {
                    var channel = Channel.CreateBounded<int>(count);
                    for (int i = 0; i < count; i++)
                        if (!channel.Writer.TryWrite(i)) throw new InvalidOperationException("Channel enqueue failed");
                    channel.Writer.Complete();
                    RunThreads(Math.Min(workers, count), () => { while (channel.Reader.TryRead(out int task)) body(task); });
                });
            default: throw new ArgumentException(name);
        }
    }

    private static void RunThreads(int count, ThreadStart body)
    {
        var threads = new Thread[count];
        for (int i = 0; i < count; i++) { threads[i] = new Thread(body); threads[i].Start(); }
        foreach (var thread in threads) thread.Join();
    }

    private static void VerifyCoverage(IFramework framework)
    {
        foreach (int count in new[] { 1, 7, 257, 4096 })
        {
            var visits = new int[count];
            int invalid = 0;
            framework.Dispatch(i => {
                if ((uint)i >= (uint)count) Interlocked.Exchange(ref invalid, 1);
                else Interlocked.Increment(ref visits[i]);
            }, count);
            if (invalid != 0 || visits.Any(n => n != 1))
                throw new InvalidOperationException($"{framework.Name}: callbacks must execute exactly once");
        }
    }

    internal static void VerifyWorkerLimit(IFramework framework, int limit)
    {
        int expected = framework.Name == "Serial" ? 1 : limit;
        int caller = Environment.CurrentManagedThreadId;
        var threads = new ConcurrentDictionary<int, byte>();
        using var release = new ManualResetEventSlim(false);
        int active = 0, peak = 0, timedOut = 0, entered = 0;
        framework.Dispatch(_ => {
            int sequence = Interlocked.Increment(ref entered);
            threads.TryAdd(Environment.CurrentManagedThreadId, 0);
            int count = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref peak); }
            while (count > previous && Interlocked.CompareExchange(ref peak, count, previous) != previous);
            if (count >= expected) release.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                Interlocked.Exchange(ref timedOut, 1);
                release.Set();
            }
            // Keep the first wave occupied so an extra caller/worker cannot hide behind turnover.
            if (sequence <= expected * 2) Thread.Sleep(20);
            Interlocked.Decrement(ref active);
        }, 4096);
        bool callerUsed = threads.ContainsKey(caller);
        Console.WriteLine($"WORKER_AUDIT {framework.Name}: limit={expected} peak={peak} threads={threads.Count} background={threads.Count - (callerUsed ? 1 : 0)} caller={(callerUsed ? 1 : 0)} timeout={timedOut}");
        if (timedOut != 0 || peak != expected)
            throw new InvalidOperationException($"{framework.Name}: compute thread limit verification failed");
    }

    internal static void Measure(string framework, Scenario scenario, int workers, int grain, int rounds,
        Action reset, Action run, Action verify, ulong checksum, StreamWriter csv, bool verifyOnly)
    {
        double Frame()
        {
            reset();
            long start = Stopwatch.GetTimestamp();
            run();
            long end = Stopwatch.GetTimestamp();
            verify();
            return (end - start) * 1000.0 / Stopwatch.Frequency / (scenario.Count == 1 ? 64 : 1);
        }
        for (int i = 0; i < 5; i++) Frame();
        if (verifyOnly) { Console.WriteLine($"VERIFY {framework} {scenario.Name} PASS checksum={checksum}"); return; }
        var means = new List<double>();
        var all = new List<double>();
        int samples = 5;
        for (int round = 0; round < rounds; round++)
        {
            double total = 0;
            for (int sample = 0; sample < samples; sample++)
            {
                double ms = Frame(); total += ms; all.Add(ms);
                csv.WriteLine(FormattableString.Invariant($"{framework},{workers},{grain},{scenario.Name},{scenario.Tasks},{round},{sample},{ms:R},{checksum}"));
            }
            means.Add(total / samples);
        }
        means.Sort(); all.Sort();
        double median = means[means.Count / 2];
        Console.WriteLine(FormattableString.Invariant($"{framework,-22} {scenario.Name,-16} tasks={scenario.Tasks,4} median={median,10:F6} ms p95={all[(all.Count * 95 + 99) / 100 - 1],10:F6} min={all[0],10:F6} max={all[^1],10:F6} spread={(means[^1] - means[0]) / median * 100,6:F1}% PASS checksum={checksum}"));
        csv.Flush();
    }

    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            int workers = 16, grain = 4096, rounds = 9;
            bool verifyOnly = false, codegen = false;
            string filter = "", frameworkFilter = "", csvPath = "managed-results.csv";
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--verify-only": verifyOnly = true; break;
                    case "--codegen": codegen = true; break;
                    case "--workers": workers = int.Parse(args[++i]); break;
                    case "--grain": grain = int.Parse(args[++i]); break;
                    case "--rounds": rounds = int.Parse(args[++i]); break;
                    case "--filter": filter = args[++i]; break;
                    case "--framework": frameworkFilter = args[++i]; break;
                    case "--csv": csvPath = args[++i]; break;
                    default: throw new ArgumentException($"Unknown argument: {args[i]}");
                }
            }
            if (workers < 2 || grain < 1 || rounds < 3) throw new ArgumentException("Invalid workers/grain/rounds");
            if (Environment.GetEnvironmentVariable("ENTJOY_PHYSCAP_SMALLJOB") != "0")
                throw new InvalidOperationException("Set ENTJOY_PHYSCAP_SMALLJOB=0 before launching the benchmark; use run_benchmarks.py.");
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.GetCurrentProcess();
                ulong allowed = (ulong)process.ProcessorAffinity.ToInt64(), selected = 0;
                int count = 0;
                for (int bit = 0; bit < 64 && count < workers; bit++)
                    if ((allowed & (1UL << bit)) != 0) { selected |= 1UL << bit; count++; }
                if (count != workers) throw new ArgumentException("Cannot enforce CPU budget");
                process.ProcessorAffinity = (nint)selected;
                Console.WriteLine($"CPU affinity=0x{selected:x}");
            }
            ThreadPool.GetMinThreads(out _, out int minIo);
            ThreadPool.GetMaxThreads(out _, out int maxIo);
            if (!ThreadPool.SetMinThreads(workers, minIo) || !ThreadPool.SetMaxThreads(workers, maxIo))
                throw new InvalidOperationException("Cannot set ThreadPool budget");
            using var csv = new StreamWriter(csvPath);
            csv.WriteLine("framework,workers,grain,scenario,tasks,round,sample,ms,checksum");
            Console.WriteLine($"Compute thread limit={workers} (including caller), small-job cap=OFF, grain={grain} rounds={rounds} warmup=5 samples=5 (S4: 64 round trips/sample)");
            if (codegen) return CodegenBenchmark.Run(workers, grain, rounds, filter, csv, verifyOnly);
            var scenarios = new[] {
                new Scenario("S1-Add", 1000003, grain, 0),
                new Scenario("S2-TinyBatch", 4096, 1, 0),
                new Scenario("S3-ThreeStages", 1000003, grain, 0, 3),
                new Scenario("S4-OneTask", 1, 1, 0),
                new Scenario("S5-Xorshift", 100003, grain, 3),
                new Scenario("S6-Branch", 100003, grain, 4)
            };
            foreach (string name in new[] { "Serial", "EntJoy Managed", "EntJoy NativeCallback", "ZeroAlloc", "Misaki", "PowerThreadPool", "ThreadPool", "Parallel.For", "Task.Run", "Bag+threads", "Channel+threads" })
            {
                if (frameworkFilter.Length != 0 && frameworkFilter != name) continue;
                using var framework = Create(name, workers);
                Console.WriteLine($"VERIFY coverage {name}...");
                VerifyCoverage(framework);
                VerifyWorkerLimit(framework, workers);
                foreach (var scenario in scenarios)
                {
                    if (!scenario.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    uint[] initial = Initial(scenario.Count), data = new uint[scenario.Count], expected = Expected(scenario);
                    var work = new Workload(data, scenario);
                    Action<int> body = work.Execute;
                    void Run()
                    {
                        for (int repeat = 0; repeat < (scenario.Count == 1 ? 64 : 1); repeat++)
                        for (int stage = 0; stage < scenario.Stages; stage++)
                        {
                            work.Stage = scenario.Stage + stage;
                            framework.Dispatch(body, scenario.Tasks);
                        }
                    }
                    Measure(name, scenario, workers, grain, rounds, () => initial.CopyTo(data, 0), Run,
                        () => { if (!data.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException($"{name} {scenario.Name}: output mismatch"); },
                        Checksum(expected), csv, verifyOnly);
                }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine($"FAIL: {error}"); return 1; }
    }
}
