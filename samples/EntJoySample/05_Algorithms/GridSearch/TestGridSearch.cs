using EntJoy.JobSystem;
using System.Diagnostics;
using Vector3 = System.Numerics.Vector3;
using EntJoy.Collections;
using EntJoy.Mathematics;


public class TestGridSearch
{
    private const int N = 100000;
    private const int K = 100000;
    private const int DefaultWarmup = 5;
    private const int DefaultMeasure = 100;
    private const int FrameSleepMilliseconds = 16;

    private static int ReadPositiveEnvironmentInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0
            ? value
            : fallback;
    }

    // 复用 IJobChunkMoveCompareSample.cs:1054 的插值分位数实现（保持两端一致）
    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        double weight = position - lower;
        return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
    }

    private static void PrintSummary(string label, double[] samples)
    {
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        double sum = 0;
        foreach (double s in samples) sum += s;
        double avg = sum / samples.Length;
        double p50 = Percentile(sorted, 0.50);
        double p95 = Percentile(sorted, 0.95);
        double p99 = Percentile(sorted, 0.99);
        double max = sorted[samples.Length - 1];
        Console.WriteLine($"{label,-28}: avg={avg:F3} ms, p50={p50:F3} ms, p95={p95:F3} ms, p99={p99:F3} ms, max={max:F3} ms");
        Console.WriteLine(FormattableString.Invariant(
            $"BENCH|runtime=EntJoy|case={label}|entities={N}|queries={K}|frames={samples.Length}|trace=0|avg={avg:F6}|p50={p50:F6}|p95={p95:F6}|p99={p99:F6}|max={max:F6}"));
    }

    // 稳态采样：Stopwatch.GetTimestamp 计时，与 Unity 端 RunBenchmark 一致；sleepMs>0 时每帧后插 Thread.Sleep
    private static double[] RunSteadyPhase(int warmup, int measure, int sleepMs, Action step, Action onSample)
    {
        for (int i = 0; i < warmup; i++)
        {
            step();
            if (sleepMs > 0) Thread.Sleep(sleepMs);
        }

        var samples = new double[measure];
        for (int i = 0; i < measure; i++)
        {
            long start = Stopwatch.GetTimestamp();
            step();
            long end = Stopwatch.GetTimestamp();
            samples[i] = (end - start) * 1000.0 / Stopwatch.Frequency;
            onSample();
            if (sleepMs > 0) Thread.Sleep(sleepMs);
        }
        return samples;
    }

    public static void Main()
    {
        NativeJobScheduler.Initialize();
        NativeJobScheduler.PrewakeWorkersOnce();
        NativeJobScheduler.JobCostCacheEnabled = true;

        int tilesPerWorker = NativeJobScheduler.TilesPerWorker > 0 ? NativeJobScheduler.TilesPerWorker : 16;

        int warmup = ReadPositiveEnvironmentInt("ENTJOY_BENCH_WARMUP", DefaultWarmup);
        int measure = ReadPositiveEnvironmentInt("ENTJOY_BENCH_FRAMES", DefaultMeasure);
        bool sleepMode = Environment.GetEnvironmentVariable("ENTJOY_BENCH_SLEEP") == "1";
        int sleepMs = sleepMode ? FrameSleepMilliseconds : 0;

        var pos = new Vector3[N];
        var queries = new Vector3[K];
        var rnd = new Random(1234);
        for (int i = 0; i < N; i++)
            pos[i] = new Vector3((float)(rnd.NextDouble() * 200 - 100), (float)(rnd.NextDouble() * 200 - 100), 0);
        for (int i = 0; i < K; i++)
            queries[i] = new Vector3((float)(rnd.NextDouble() * 200 - 100), (float)(rnd.NextDouble() * 200 - 100), 0);

        var nativePos = new NativeArray<float2>(N, Allocator.Persistent);
        var nativeQueries = new NativeArray<float2>(K, Allocator.Persistent);
        for (int i = 0; i < N; i++) nativePos[i] = new float2(pos[i].X, pos[i].Y);
        for (int i = 0; i < K; i++) nativeQueries[i] = new float2(queries[i].X, queries[i].Y);

        var gsb = new GridSearch2D(-1f, 200);

        Console.WriteLine("=== GridSearch2D SoA + ISPC 稳态测量 ===");
        Console.WriteLine($"Warmup: {warmup}, Measure: {measure}, Sleep: {(sleepMode ? sleepMs + "ms" : "off")}, QueryBatch: {GridSearch2D.QueryBatchSize}, WorkerCount: {NativeJobScheduler.JobWorkerCount}");

        // ---- COLD 阶段：每轮全量重建（对齐 Unity GridSearchBurst 真实路径） ----
        // 墙钟 = Dispose + 重新分配 + 复制 + 6 个 job；core = 纯 job 阶段
        // sumPhases/sumQueryCore：恢复原始分阶段计时（[核心]XXX耗时，对齐 b22a56c 的 平均详细计时 块）
        GridSearch2D.BuildTimings sumPhases = default;
        double sumQueryCore = 0;
        var coreBuildCold = new double[measure];
        int coldIdx = 0;
        double[] buildWallCold = RunSteadyPhase(warmup, measure, sleepMs,
            () => gsb.InitializeGrid(nativePos).Complete(),
            () =>
            {
                var t = gsb.LastBuildTimings;
                coreBuildCold[coldIdx++] = t.CoreBuildTotal;
                sumPhases.DisposeNative += t.DisposeNative;
                sumPhases.CreateAndCopy += t.CreateAndCopy;
                sumPhases.BoundingBox += t.BoundingBox;
                sumPhases.HashCounting += t.HashCounting;
                sumPhases.PrefixAndFill += t.PrefixAndFill;
                sumPhases.ElementPlacement += t.ElementPlacement;
                sumPhases.CoreBuildTotal += t.CoreBuildTotal;
            });

        var coldTimings = gsb.LastBuildTimings;
        Console.WriteLine($"COLD 分配诊断 (最后一次): dispose={coldTimings.DisposeNative:F3} ms, alloc+copy={coldTimings.CreateAndCopy:F3} ms — 不计入稳态指标");

        // ---- STEADY 阶段：暖路径重排（复用缓冲，无重分配），隔离分配器/冷内存方差 ----
        var coreBuildSteady = new double[measure];
        int steadyIdx = 0;
        double[] buildWallSteady = RunSteadyPhase(warmup, measure, sleepMs,
            () => gsb.UpdatePositions(nativePos).Complete(),
            () => coreBuildSteady[steadyIdx++] = gsb.LastBuildTimings.CoreBuildTotal);

        // ---- QUERY 阶段：对同一网格重复查询 ----
        var coreQuery = new double[measure];
        int queryIdx = 0;
        double[] queryWall = RunSteadyPhase(warmup, measure, sleepMs,
            () => gsb.SearchClosestPoint(nativeQueries).Dispose(),
            () =>
            {
                coreQuery[queryIdx++] = gsb.LastBuildTimings.QueryTotal;
                sumQueryCore += gsb.LastBuildTimings.QueryTotal;
            });

        Console.WriteLine();
        PrintSummary("GridSearch-BuildCore-Cold", coreBuildCold);   // 纯 job 阶段（冷分配），跨端主指标 vs Unity BuildCore
        PrintSummary("GridSearch-BuildWall-Cold", buildWallCold);   // 墙钟 = Dispose+alloc+copy+6 job，对齐 Unity BuildWall
        PrintSummary("GridSearch-BuildCore-Steady", coreBuildSteady); // 暖路径纯 job，隔离分配噪声
        PrintSummary("GridSearch-Query", queryWall);                // 墙钟，含 TempJob results 分配，与 Unity swQuery 对齐
        PrintSummary("GridSearch-QueryCore", coreQuery);            // 纯 job 查询

        // ---- 平均详细计时（恢复原始输出，对齐 b22a56c）：分阶段 [核心]XXX耗时 ----
        // Percentile 假定入参已排序（PrintSummary 内部先 Sort），此处必须先排再算 p50
        var buildSorted = (double[])coreBuildCold.Clone(); Array.Sort(buildSorted);
        var querySorted = (double[])coreQuery.Clone(); Array.Sort(querySorted);
        Console.WriteLine();
        Console.WriteLine("--- 平均详细计时 (COLD 冷分配路径) ---");
        Console.WriteLine($"[外围] 释放 NativeCollections: {sumPhases.DisposeNative / measure:F3} ms");
        Console.WriteLine($"[外围] 创建 NativeCollections + 复制数据: {sumPhases.CreateAndCopy / measure:F3} ms");
        Console.WriteLine($"[核心] 包围盒计算: {sumPhases.BoundingBox / measure:F3} ms");
        Console.WriteLine($"[核心] 哈希分配+计数: {sumPhases.HashCounting / measure:F3} ms");
        Console.WriteLine($"[核心] 前缀和+填充起止: {sumPhases.PrefixAndFill / measure:F3} ms");
        Console.WriteLine($"[核心] 元素放置: {sumPhases.ElementPlacement / measure:F3} ms");
        Console.WriteLine($"[核心] 核心构建总耗时: {sumPhases.CoreBuildTotal / measure:F3} ms (p50 {Percentile(buildSorted, 0.50):F3} ms)");
        Console.WriteLine($"[核心] 核心查询总耗时: {sumQueryCore / measure:F3} ms (p50 {Percentile(querySorted, 0.50):F3} ms)");

        // 结果抽查（沿用原逻辑）
        var results = gsb.SearchClosestPoint(nativeQueries);
        var resultsArray = new int[results.Length];
        results.CopyTo(resultsArray);
        Console.WriteLine("查询结果前10个: {0}", string.Join(" ", resultsArray[..10]));
        results.Dispose();

        // ---- DIAG 行 ----
        var js = NativeJobScheduler.GetStats();
        Console.WriteLine(FormattableString.Invariant(
            $"DIAG|runtime=EntJoy|case=GridSearch2D|entities={N}|queries={K}|workerCount={NativeJobScheduler.JobWorkerCount}|warmup={warmup}|frames={measure}|sleepMs={sleepMs}|queryBatch={GridSearch2D.QueryBatchSize}|tilesPerWorker={tilesPerWorker}|parkWake={js.ParkWakeCount}|hotSpin={js.HotSpinHits}"));
        Console.WriteLine(FormattableString.Invariant(
            $"TIMING|submitToFirstWorker={js.SubmitToFirstWorkerEwmaNs / 1000.0:F1}us|workerSpread={js.WorkerStartSpreadEwmaNs / 1000.0:F1}us|lastTileToTopology={js.LastTileToTopologyDoneEwmaNs / 1000.0:F1}us|completeWaitLoops={js.CompleteWaitLoops}|assistAttempts={js.AssistAttempts}|assistExecuted={js.AssistExecuted}|assistPct={js.AssistExecPctEwma}"));
        Console.WriteLine(FormattableString.Invariant(
            $"TIMING2|publishToFirstWorkerClaim={js.PublishToFirstWorkerClaimEwmaNs / 1000.0:F1}us|publishToCompletion={js.PublishToCompletionEwmaNs / 1000.0:F1}us|perRangeExec={js.PerRangeExecEwmaNs / 1000.0:F1}us|completionOverhead={js.CompletionOverheadUs}us|wakeLatency={js.WakeLatencyEwmaNs / 1000.0:F1}us"));

        gsb.Dispose();
        nativePos.Dispose();
        nativeQueries.Dispose();

        // Persistent free-list 统计（门控：ENTJOY_PERSISTENT_POOL_STATS=1）
        if (Environment.GetEnvironmentVariable("ENTJOY_PERSISTENT_POOL_STATS") == "1")
        {
            var ps = PersistentAllocator.GetStats();
            double hitRate = ps.Allocs > 0 ? (double)ps.Hits / ps.Allocs * 100.0 : 0.0;
            Console.WriteLine(FormattableString.Invariant(
                $"PERSISTENT_POOL|allocs={ps.Allocs}|frees={ps.Frees}|hits={ps.Hits}|misses={ps.Misses}|toOS={ps.ToOS}|foreign={ps.Foreign}|hitRate={hitRate:F1}%"));
        }

        Console.WriteLine("\n测试完成。");
        //Console.Read();
    }
}

