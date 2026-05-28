//using System;
//using System.Collections.Generic;
//using System.Diagnostics;
//using System.Linq;
//using System.Threading;

//// ===================== 测试用 Job 定义 =====================

//public struct HeavyCalcJob : IJob
//{
//    public int Iterations;
//    public void Execute()
//    {
//        double sum = 0;
//        for (int i = 0; i < Iterations; i++)
//            sum += Math.Sin(i * 0.1) * Math.Cos(i * 0.2) + Math.Sqrt(i + 1);
//        if (sum < 0) Console.WriteLine(sum);
//    }
//}

//public struct ArrayFillJob : IJobFor
//{
//    public int[] Data;
//    public int Value;
//    public void Execute(int i)
//    {
//        Data[i] = Value + (i % 100);
//    }
//}

//public struct VectorAddJob : IJobParallelFor
//{
//    public int[] A;
//    public int[] B;
//    public int[] Result;
//    public void Execute(int i)
//    {
//        Result[i] = A[i] + B[i];
//    }
//}

//public struct HeavyParallelJob : IJobParallelFor
//{
//    public float[] Input;
//    public float[] Output;
//    public void Execute(int i)
//    {
//        float x = Input[i];
//        float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1) + MathF.Exp(x * 0.001f);
//        Output[i] = res;
//    }
//}

//public struct BatchProcessJob : IJobParallelForBatch
//{
//    public float[] Data;
//    public float[] Output;
//    public void Execute(int startIndex, int count)
//    {
//        int end = startIndex + count;
//        for (int i = startIndex; i < end; i++)
//        {
//            float x = Data[i];
//            float res = MathF.Sqrt(x) + MathF.Sin(x * 0.5f) * MathF.Cos(x * 0.5f) * MathF.Log(x + 2);
//            Output[i] = res;
//        }
//    }
//}

//public struct MixedLightJob : IJobParallelFor
//{
//    public int[] Data;
//    public void Execute(int i)
//    {
//        Data[i] = (Data[i] * 7 + 13) % 10007;
//    }
//}

//// ===================== 测试入口 =====================

//public class JobProfilerTest
//{
//    private const int LARGE_SIZE = 5_000_000;
//    private const int MEDIUM_SIZE = 1_000_000;
//    private const int SMALL_SIZE = 100_000;

//    // OS 线程 ID → 连续 Worker 索引的映射
//    private static readonly Dictionary<int, int> _osThreadToWorkerIdx = new();
//    private static int _nextWorkerIdx = 0;
//    private static readonly object _workerIdxLock = new();

//    /// <summary>将 OS 线程 ID 映射为 0 开始的连续 Worker 索引</summary>
//    private static int MapToWorkerIdx(int osThreadId)
//    {
//        lock (_workerIdxLock)
//        {
//            if (!_osThreadToWorkerIdx.TryGetValue(osThreadId, out var idx))
//            {
//                idx = _nextWorkerIdx++;
//                _osThreadToWorkerIdx[osThreadId] = idx;
//            }
//            return idx;
//        }
//    }

//    /// <summary>格式化线程范围显示 (使用映射后的 Worker 索引)</summary>
//    private static string WorkerRange(int minOs, int maxOs)
//    {
//        int minW = MapToWorkerIdx(minOs);
//        int maxW = MapToWorkerIdx(maxOs);
//        return minW == maxW ? $"W{minW}" : $"W{minW}-W{maxW}";
//    }

//    /// <summary>按 Worker 索引排序的线程聚合</summary>
//    private static JobProfiler.ThreadSummaryInfo[] GetSortedThreadSummary()
//    {
//        var raw = JobProfiler.AggregateByThread();
//        var sorted = raw.OrderBy(t => MapToWorkerIdx(t.ThreadIndex)).ToArray();
//        return sorted;
//    }

//    public static void Main(string[] args)
//    {
//        Console.WriteLine("==========================================");
//        Console.WriteLine("   EntJoy JobSystem - Profiler 性能测试");
//        Console.WriteLine("==========================================");
//        Console.WriteLine();

//        Test1_IJob();
//        Test2_IJobFor();
//        Test3_VectorAdd();
//        Test4_HeavyCalc();
//        Test5_Batch();
//        Test6_Mixed();
//        Test7_Overhead();
//        Test8_Aggregation();

//        Console.WriteLine();
//        Console.WriteLine("==========================================");
//        Console.WriteLine("   Profiler 测试完成");
//        Console.WriteLine("==========================================");

//        if (args.Length > 0 && args[0] == "--pause")
//        {
//            Console.Write("按任意键退出...");
//            Console.ReadKey();
//        }
//    }

//    // ======================= 测试 1: IJob =======================
//    private static void Test1_IJob()
//    {
//        Console.WriteLine("--- [测试 1] IJob（单次执行，500万次迭代）---");

//        JobProfiler.Enabled = false;
//        var warmup = new HeavyCalcJob { Iterations = 5000000 };
//        warmup.Execute();

//        JobProfiler.Enabled = true;
//        JobProfiler.Clear();

//        var job = new HeavyCalcJob { Iterations = 5000000 };
//        var sw = Stopwatch.StartNew();
//        var handle = NativeJobScheduler.Schedule(ref job);
//        NativeJobScheduler.Complete(ref handle);
//        sw.Stop();

//        JobProfiler.PullFrameData();
//        Console.WriteLine($"  耗时: {sw.Elapsed.TotalMilliseconds,8:F3} ms  |  条目: {JobProfiler.CurrentFrameEntryCount}");
//        DumpFrame();
//        Console.WriteLine();
//    }

//    // ======================= 测试 2: IJobFor =======================
//    private static void Test2_IJobFor()
//    {
//        Console.WriteLine("--- [测试 2] IJobFor（串行循环，10万次）---");

//        int[] data = new int[SMALL_SIZE];
//        var job = new ArrayFillJob { Data = data, Value = 42 };
//        JobProfiler.Enabled = true;
//        JobProfiler.Clear();

//        var sw = Stopwatch.StartNew();
//        var handle = NativeJobScheduler.ScheduleFor(ref job, data.Length);
//        NativeJobScheduler.Complete(ref handle);
//        sw.Stop();

//        JobProfiler.PullFrameData();
//        Console.WriteLine($"  数据量: {SMALL_SIZE,8:N0}  |  耗时: {sw.Elapsed.TotalMilliseconds,8:F3} ms  |  条目: {JobProfiler.CurrentFrameEntryCount}");
//        DumpFrame();
//        Console.WriteLine();
//    }

//    // ======================= 测试 3: 向量加法 =======================
//    private static void Test3_VectorAdd()
//    {
//        Console.WriteLine("--- [测试 3] VectorAdd 轻量并行（500万元素）---");

//        int[] a = new int[LARGE_SIZE];
//        int[] b = new int[LARGE_SIZE];
//        int[] result = new int[LARGE_SIZE];
//        for (int i = 0; i < LARGE_SIZE; i++) { a[i] = i; b[i] = i * 2; }

//        var job = new VectorAddJob { A = a, B = b, Result = result };
//        JobProfiler.Enabled = true;
//        JobProfiler.Clear();

//        var sw = Stopwatch.StartNew();
//        var handle = NativeJobScheduler.ScheduleParallelFor(ref job, LARGE_SIZE, 65536);
//        NativeJobScheduler.Complete(ref handle);
//        sw.Stop();

//        JobProfiler.PullFrameData();
//        Console.WriteLine($"  数据量: {LARGE_SIZE,8:N0}  |  耗时: {sw.Elapsed.TotalMilliseconds,8:F3} ms  |  条数: {JobProfiler.CurrentFrameEntryCount}");
//        DumpFrame();
//        Console.WriteLine();
//    }

//    // ======================= 测试 4: 重计算 =======================
//    private static void Test4_HeavyCalc()
//    {
//        Console.WriteLine("--- [测试 4] 计算密集型并行（100万元素）---");

//        float[] input = new float[MEDIUM_SIZE];
//        float[] output = new float[MEDIUM_SIZE];
//        var rand = new Random(42);
//        for (int i = 0; i < MEDIUM_SIZE; i++) input[i] = (float)rand.NextDouble() * 100f;

//        var job = new HeavyParallelJob { Input = input, Output = output };
//        JobProfiler.Enabled = true;
//        JobProfiler.Clear();

//        var sw = Stopwatch.StartNew();
//        var handle = NativeJobScheduler.ScheduleParallelFor(ref job, MEDIUM_SIZE, 32768);
//        NativeJobScheduler.Complete(ref handle);
//        sw.Stop();

//        JobProfiler.PullFrameData();
//        Console.WriteLine($"  数据量: {MEDIUM_SIZE,8:N0}  |  耗时: {sw.Elapsed.TotalMilliseconds,8:F3} ms  |  条数: {JobProfiler.CurrentFrameEntryCount}");

//        // 结果校核
//        bool ok = true;
//        for (int i = 0; i < 10; i++)
//        {
//            float expected = MathF.Sqrt(input[i]) + MathF.Sin(input[i]) * MathF.Cos(input[i])
//                + MathF.Log(input[i] + 1) + MathF.Exp(input[i] * 0.001f);
//            if (Math.Abs(output[i] - expected) > 0.001f)
//            {
//                Console.WriteLine($"  【错误】索引 {i}: 期望 {expected:F6}, 实际 {output[i]:F6}");
//                ok = false;
//            }
//        }
//        Console.WriteLine($"  结果校验: {(ok ? "通过" : "失败")}");

//        DumpFrame();
//        Console.WriteLine();
//    }

//    // ======================= 测试 5: Batch =======================
//    private static void Test5_Batch()
//    {
//        Console.WriteLine("--- [测试 5] IJobParallelForBatch 批量处理（100万元素）---");

//        float[] data = new float[MEDIUM_SIZE];
//        float[] output = new float[MEDIUM_SIZE];
//        var rand = new Random(123);
//        for (int i = 0; i < MEDIUM_SIZE; i++) data[i] = (float)rand.NextDouble() * 100f;

//        var job = new BatchProcessJob { Data = data, Output = output };
//        JobProfiler.Enabled = true;
//        JobProfiler.Clear();

//        var sw = Stopwatch.StartNew();
//        var handle = NativeJobScheduler.ScheduleParallelForBatch(ref job, MEDIUM_SIZE, 32768);
//        NativeJobScheduler.Complete(ref handle);
//        sw.Stop();

//        JobProfiler.PullFrameData();
//        Console.WriteLine($"  数据量: {MEDIUM_SIZE,8:N0}  |  耗时: {sw.Elapsed.TotalMilliseconds,8:F3} ms  |  条数: {JobProfiler.CurrentFrameEntryCount}");
//        DumpFrame();
//        Console.WriteLine();
//    }

//    // ======================= 测试 6: 混合 =======================
//    private static void Test6_Mixed()
//    {
//        Console.WriteLine("--- [测试 6] 多 Job 混合调度（3个 MixedLightJob 同时执行）---");

//        JobProfiler.Enabled = true;
//        JobProfiler.Clear();

//        int[] data1 = new int[SMALL_SIZE];
//        var j1 = new MixedLightJob { Data = data1 };
//        var h1 = NativeJobScheduler.ScheduleParallelFor(ref j1, SMALL_SIZE, 16384);

//        int[] data2 = new int[SMALL_SIZE];
//        var j2 = new MixedLightJob { Data = data2 };
//        var h2 = NativeJobScheduler.ScheduleParallelFor(ref j2, SMALL_SIZE, 16384);

//        int[] data3 = new int[SMALL_SIZE];
//        var j3 = new MixedLightJob { Data = data3 };
//        var h3 = NativeJobScheduler.ScheduleParallelFor(ref j3, SMALL_SIZE, 16384);

//        NativeJobScheduler.Complete(ref h1);
//        NativeJobScheduler.Complete(ref h2);
//        NativeJobScheduler.Complete(ref h3);

//        var sw = Stopwatch.StartNew();
//        JobProfiler.PullFrameData();
//        sw.Stop();
//        Console.WriteLine($"  总条目: {JobProfiler.CurrentFrameEntryCount}  |  拉取耗时: {sw.Elapsed.TotalMilliseconds:F3} ms");
//        DumpFrame();
//        Console.WriteLine();
//    }

//    // ======================= 测试 7: 开销 =======================
//    private static void Test7_Overhead()
//    {
//        Console.WriteLine("--- [测试 7] Profiler 性能开销 ---");

//        float[] input = new float[LARGE_SIZE];
//        float[] output1 = new float[LARGE_SIZE];
//        float[] output2 = new float[LARGE_SIZE];
//        var rand = new Random(99);
//        for (int i = 0; i < LARGE_SIZE; i++) input[i] = (float)rand.NextDouble() * 100f;

//        // 关闭 Profiler 跑一次
//        JobProfiler.Enabled = false;
//        var job = new HeavyParallelJob { Input = input, Output = output1 };
//        var sw = Stopwatch.StartNew();
//        var h1 = NativeJobScheduler.ScheduleParallelFor(ref job, LARGE_SIZE, 65536);
//        NativeJobScheduler.Complete(ref h1);
//        sw.Stop();
//        double withoutMs = sw.Elapsed.TotalMilliseconds;

//        // 开启 Profiler 跑一次
//        job.Output = output2;
//        JobProfiler.Enabled = true;
//        JobProfiler.Clear();
//        sw.Restart();
//        var h2 = NativeJobScheduler.ScheduleParallelFor(ref job, LARGE_SIZE, 65536);
//        NativeJobScheduler.Complete(ref h2);
//        sw.Stop();
//        double withMs = sw.Elapsed.TotalMilliseconds;

//        double overhead = (withMs - withoutMs) / withoutMs * 100.0;
//        Console.WriteLine($"  关闭 Profiler: {withoutMs,8:F3} ms");
//        Console.WriteLine($"  开启 Profiler: {withMs,8:F3} ms");
//        Console.WriteLine($"  额外开销:      {withMs - withoutMs,8:F3} ms  ({overhead:F2}%)");
//        Console.WriteLine();
//    }

//    // ======================= 测试 8: 聚合 =======================
//    private static void Test8_Aggregation()
//    {
//        Console.WriteLine("--- [测试 8] 聚合统计演示 ---");

//        JobProfiler.Enabled = true;
//        JobProfiler.Clear();

//        // HeavyParallelJob × 2 (不同数据量)
//        float[] input1 = new float[MEDIUM_SIZE];
//        float[] output1 = new float[MEDIUM_SIZE];
//        var rand = new Random(1);
//        for (int i = 0; i < MEDIUM_SIZE; i++) input1[i] = (float)rand.NextDouble() * 100f;
//        var job1 = new HeavyParallelJob { Input = input1, Output = output1 };
//        var h1 = NativeJobScheduler.ScheduleParallelFor(ref job1, MEDIUM_SIZE, 32768);

//        float[] input2 = new float[SMALL_SIZE];
//        float[] output2 = new float[SMALL_SIZE];
//        var job2 = new HeavyParallelJob { Input = input2, Output = output2 };
//        var h2 = NativeJobScheduler.ScheduleParallelFor(ref job2, SMALL_SIZE, 16384);

//        // VectorAddJob
//        int size = LARGE_SIZE / 2;
//        int[] a = new int[size];
//        int[] b = new int[size];
//        int[] r = new int[size];
//        for (int i = 0; i < size; i++) { a[i] = i; b[i] = i * 2; }
//        var addJob = new VectorAddJob { A = a, B = b, Result = r };
//        var h3 = NativeJobScheduler.ScheduleParallelFor(ref addJob, size, 65536);

//        NativeJobScheduler.Complete(ref h1);
//        NativeJobScheduler.Complete(ref h2);
//        NativeJobScheduler.Complete(ref h3);

//        JobProfiler.PullFrameData();
//        Console.WriteLine($"  原始条目数: {JobProfiler.CurrentFrameEntryCount}");
//        Console.WriteLine();

//        // 按 Job 聚合 （使用映射后的 Worker 索引）
//        var byJob = JobProfiler.AggregateByJob();
//        Console.WriteLine("  ┌──────────────────────────────────┬──────────┬──────────┬───────┬──────────┐");
//        Console.WriteLine("  │ Job 名称                         │ 总耗时   │ 平均耗时 │ 次数  │ 线程范围  │");
//        Console.WriteLine("  ├──────────────────────────────────┼──────────┼──────────┼───────┼──────────┤");
//        foreach (var j in byJob)
//        {
//            Console.WriteLine($"  │ {j.JobName,-32} │ {j.TotalMs,8:F3} │ {j.AvgMs,8:F3} │ {j.CallCount,5} │ {WorkerRange(j.MinThreadIndex, j.MaxThreadIndex),8} │");
//        }
//        Console.WriteLine("  └──────────────────────────────────┴──────────┴──────────┴───────┴──────────┘");
//        Console.WriteLine();

//        // 按线程聚合（按 Worker 索引排序）
//        var byThread = GetSortedThreadSummary();
//        Console.WriteLine("  ┌──────────────┬──────────┬──────────┐");
//        Console.WriteLine("  │ Worker 线程  │ 总耗时   │ Job 数量 │");
//        Console.WriteLine("  ├──────────────┼──────────┼──────────┤");
//        foreach (var t in byThread)
//        {
//            int wIdx = MapToWorkerIdx(t.ThreadIndex);
//            Console.WriteLine($"  │ Worker {wIdx,-2}         │ {t.TotalMs,8:F3} │ {t.JobCount,8} │");
//        }
//        Console.WriteLine("  └──────────────┴──────────┴──────────┘");
//        Console.WriteLine();

//        // 汇总
//        double totalMs = 0;
//        foreach (var j in byJob) totalMs += j.TotalMs;
//        Console.WriteLine($"  聚合总耗时: {totalMs:F3} ms");
//    }

//    // ======================= 辅助输出 =======================

//    private static void DumpFrame()
//    {
//        if (JobProfiler.CurrentFrameEntryCount == 0)
//        {
//            Console.WriteLine("  (无 Profiler 数据)");
//            return;
//        }

//        // 按 Job 聚合（使用映射后的 Worker 索引）
//        var byJob = JobProfiler.AggregateByJob();
//        foreach (var j in byJob)
//        {
//            Console.WriteLine($"  {j.JobName,-20}  总{j.TotalMs,8:F3}ms  均{j.AvgMs,8:F3}ms  x{j.CallCount}次  [{WorkerRange(j.MinThreadIndex, j.MaxThreadIndex)}]");
//        }

//        // 按 Worker 线程聚合（按 Worker 索引排序）
//        var sortedThread = GetSortedThreadSummary();
//        foreach (var t in sortedThread)
//        {
//            int wIdx = MapToWorkerIdx(t.ThreadIndex);
//            Console.WriteLine($"  Worker {wIdx,-2}            总{t.TotalMs,8:F3}ms  {t.JobCount}个Job");
//        }
//    }
//}
