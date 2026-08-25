using EntJoy.ECS.JobSystem;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using EntJoy.ECS;
using EntJoy.Collections;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;

namespace EntJoySample.SchedulerCompareTest
{
    // ────────────────────────────── Job 定义 ──────────────────────────────

    public struct AddOneParallelForJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) => Values[index] = Values[index] + 1;
    }

    // 空任务：体内用 Interlocked.MemoryBarrier() 作为"不可被 JIT 消除"的最小操作。
    // 若体为空，编译器会把整个循环折叠掉，导致空任务测出 0.005ms 这种不可能的数字。
    public struct EmptyParallelForJob : IJobParallelFor
    {
        public void Execute(int index) => Interlocked.MemoryBarrier();
    }

    // 依赖链 Job（均为 IJobParallelFor，内部并行遍历；依赖只保证顺序，
    // 反映调度器的依赖图流水线能力，而不是串行单线程任务）
    public struct ChainAddJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) => Values[index] = Values[index] + 1;
    }

    public struct ChainMultiplyJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) => Values[index] = Values[index] * 2;
    }

    public struct ChainSubtractJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) => Values[index] = Values[index] - 3;
    }

    public struct HeavyComputeJob : IJobParallelFor
    {
        public NativeArray<long> Results;
        public void Execute(int index)
        {
            long sum = 0;
            for (int i = 0; i < 1000; i++)
                sum += (long)index * i;
            Results[index] = sum;
        }
    }

    // ───────────── 正确性自检用的 Job（写 int[]，便于与串行参考结果对拍） ─────────────

    public struct CheckFillJob : IJobParallelFor
    {
        public int[] Data;
        public int Offset;
        public void Execute(int index) => Data[index] = index + Offset;
    }

    public struct CheckNegateJob : IJobParallelFor
    {
        public int[] Data;
        public void Execute(int index) => Data[index] = -Data[index];
    }

    public struct CheckSingleJob : IJob
    {
        public int[] Data;
        public void Execute()
        {
            for (int i = 0; i < Data.Length; i++) Data[i] += 1;
        }
    }

    public struct CheckThrowsJob : IJobParallelFor
    {
        public void Execute(int index)
        {
            if (index == 12345) throw new InvalidOperationException("boom");
        }
    }

    // ────────────────────────────── 基准主体 ──────────────────────────────

    public sealed unsafe class SchedulerCompareSample : IDisposable
    {
        // ─── 常量 ───
        private const int ArrayLength = 1_000_000;
        private const int WarmupFrames = 20;
        private const int MeasureFrames = 100;
        private const int HighContentionCount = 100_000;
        private const int ScheduleLatencyIterations = 20;   // 外层 MeasureManaged 已 ×120 帧，这里避免 12万次重分配
        private const int LatencyArrayLength = 1024;

        // ThreadPool 分片数 = worker 核心数（每个 worker 一片，公平对比 Native 的自动分片）
        private static int WorkerSlices = Environment.ProcessorCount;

        // ─── 数据（各调度器独立，避免缓存干扰） ───
        private NativeArray<int> _nativeValues;
        private NativeArray<int> _managedValues;
        private NativeArray<int> _parallelForValues;
        private NativeArray<int> _threadPoolValues;
        private NativeArray<long> _heavyResults;
        private NativeArray<int> _latencyValues;

        // Managed 调度器是否可用（已由 A.7 托管调度器填充）
        private const bool ManagedEnabled = true;

        public SchedulerCompareSample()
        {
            // 初始化托管调度器（如需固定 worker 数，改这里；0 = 自动 = ProcessorCount）
            ManagedJobScheduler.Initialize();

            _nativeValues = new NativeArray<int>(ArrayLength, Allocator.Persistent);
            _managedValues = new NativeArray<int>(ArrayLength, Allocator.Persistent);
            _parallelForValues = new NativeArray<int>(ArrayLength, Allocator.Persistent);
            _threadPoolValues = new NativeArray<int>(ArrayLength, Allocator.Persistent);
            _heavyResults = new NativeArray<long>(HighContentionCount, Allocator.Persistent);
            _latencyValues = new NativeArray<int>(LatencyArrayLength, Allocator.Persistent);
        }

        /// <summary>
        /// Managed JobSystem 正确性自检（对拍串行参考结果）。覆盖：
        /// 静态分片 / 共享游标（曾因双重 Signal 提前完成） / 依赖链 / 单 IJob / 零长度 / 异常传播（不挂死）/ 并发压测。
        /// 任一项不通过即抛异常，阻止把带 bug 的调度器带入基准/生产。
        /// </summary>
        private static void SelfCheckManaged()
        {
            Console.WriteLine("=== Managed JobSystem 正确性自检 ===");
            const int n = 1 << 20; // 1Mi，> 同步内联阈值 1024 → 走 worker 路径
            bool ok = true;
            void Assert(bool cond, string msg)
            {
                Console.WriteLine((cond ? "[PASS] " : "[FAIL] ") + msg);
                if (!cond) ok = false;
            }

            // 1) 静态分片 (innerBatchCount == 0)
            {
                var data = new int[n];
                for (int i = 0; i < n; i++) data[i] = i;
                var j = new CheckNegateJob { Data = data };
                ManagedJobScheduler.Schedule(ref j, n, 0).Complete();
                bool pass = true;
                for (int i = 0; i < n; i++) if (data[i] != -i) { pass = false; break; }
                Assert(pass, "静态分片 innerBatch=0 结果正确");
            }

            // 2) 共享游标 (innerBatchCount > 0) —— 曾因 ExecuteTask 与 Runner 双重 Signal 提前完成，
            //    基准只计时不校验结果而漏网；此处必须逐元素对拍。
            {
                var data = new int[n];
                var j = new CheckFillJob { Data = data, Offset = 7 };
                ManagedJobScheduler.Schedule(ref j, n, 8192).Complete();
                bool pass = true;
                for (int i = 0; i < n; i++) if (data[i] != i + 7) { pass = false; break; }
                Assert(pass, "共享游标 innerBatch=8192 结果正确");
            }

            // 3) 依赖链（+0 → 取负）
            {
                var data = new int[n];
                for (int i = 0; i < n; i++) data[i] = i;
                var j1 = new CheckFillJob { Data = data, Offset = 0 };
                var j2 = new CheckNegateJob { Data = data };
                var h1 = ManagedJobScheduler.Schedule(ref j1, n, 0);
                var h2 = ManagedJobScheduler.Schedule(ref j2, n, 0, h1);
                h2.Complete();
                bool pass = true;
                for (int i = 0; i < n; i++) if (data[i] != -i) { pass = false; break; }
                Assert(pass, "依赖链（静态分片）结果正确");
            }

            // 4) 单 IJob
            {
                var data = new int[16];
                var j = new CheckSingleJob { Data = data };
                ManagedJobScheduler.Schedule(ref j).Complete();
                bool pass = true;
                for (int i = 0; i < 16; i++) if (data[i] != 1) { pass = false; break; }
                Assert(pass, "单 IJob 执行正确");
            }

            // 5) 零长度并行 for（过去泄漏 completion 槽位；现应可正常 Complete 且不报错）
            {
                var j = new CheckFillJob { Data = Array.Empty<int>(), Offset = 0 };
                ManagedJobScheduler.Schedule(ref j, 0, 0).Complete();
                Assert(true, "零长度并行 for 可正常 Complete");
            }

            // 6) 异常处理：job 抛异常 → Complete() 必须把异常抛回（而非依赖永远不完成 → 永久死锁）。
            //    用 10s 超时 Join 检测挂死。
            {
                bool threw = false, timedOut = false;
                var t = new Thread(() =>
                {
                    var j = new CheckThrowsJob();
                    try { ManagedJobScheduler.Schedule(ref j, n, 0).Complete(); }
                    catch (InvalidOperationException) { threw = true; }
                })
                { IsBackground = true };
                t.Start();
                if (!t.Join(TimeSpan.FromSeconds(10))) timedOut = true;
                Assert(threw, "job 抛异常 → Complete() 抛回异常（未死锁）");
                Assert(!timedOut, "异常未导致调度器挂死");
            }

            // 7) 共享游标并发压测：连续多轮调度同一 buffer，验证每轮结果都正确
            {
                var data = new int[n];
                var j = new CheckFillJob { Data = data, Offset = 3 };
                for (int r = 0; r < 20; r++) ManagedJobScheduler.Schedule(ref j, n, 4096).Complete();
                bool pass = true;
                for (int i = 0; i < n; i++) if (data[i] != i + 3) { pass = false; break; }
                Assert(pass, "共享游标并发压测 20 轮结果正确");
            }

            Console.WriteLine();
            if (!ok) throw new Exception("Managed JobSystem 正确性自检失败，拒绝进入基准/生产。");
        }

        public void Run()
        {
            SelfCheckManaged();   // 先做正确性自检（尤其覆盖曾出 bug 的共享游标路径与异常路径），失败即抛
            Console.WriteLine("=== Scheduler Performance Comparison ===");
            Console.WriteLine($"Array size: {ArrayLength:N0} | Warmup: {WarmupFrames} | Measure: {MeasureFrames} | Native batch: 0(自动) | TPool slices: {WorkerSlices}");
            Console.WriteLine("  Native    = C++ 无锁 MPMC (NativeJobScheduler, P/Invoke)");
            Console.WriteLine("  Managed   = C# 无锁 MPMC (ManagedJobScheduler, 纯托管)");
            Console.WriteLine("  ParFor    = System.Threading.Tasks.Parallel.For");
            Console.WriteLine("  ThreadPool= System.Threading.ThreadPool + CountdownEvent");
            Console.WriteLine();

            Console.WriteLine($"{"Scenario",-32}  {"Native(ms)",-12}  {"Managed(ms)",-12}  {"ParFor(ms)",-12}  {"TPool(ms)",-12}");
            Console.WriteLine(new string('-', 84));

            // 场景 1：分片加法
            RunScenario("1. 分片加法 (100万+1)", MeasureAddOne_Native, MeasureAddOne_Managed,
                MeasureAddOne_ParallelFor, MeasureAddOne_ThreadPool);

            // 场景 2：空任务固定开销
            RunScenario("2. 空任务开销 (100万)", MeasureEmpty_Native, MeasureEmpty_Managed,
                MeasureEmpty_ParallelFor, MeasureEmpty_ThreadPool);

            // 场景 3：依赖链（三个 IJobParallelFor 并行任务串链）
            RunScenario("3. 依赖链 (+1→×2→-3)", MeasureChain_Native, MeasureChain_Managed,
                MeasureChain_ParallelFor, MeasureChain_ThreadPool);

            // 场景 4：调度延迟
            RunScenario("4. 调度延迟 (1000次)", MeasureLatency_Native, MeasureLatency_Managed,
                MeasureLatency_ParallelFor, MeasureLatency_ThreadPool);

            // 场景 5：高竞争吞吐
            RunScenario("5. 高竞争吞吐 (10万)", MeasureHeavy_Native, MeasureHeavy_Managed,
                MeasureHeavy_ParallelFor, MeasureHeavy_ThreadPool);

            Console.WriteLine();
            Console.WriteLine("* Managed 列 = C# 无锁 MPMC 调度器（ManagedJobScheduler，A.7 托管版）。");
            Console.WriteLine("* Parallel.For 无原生依赖链语义，场景3用连续顺序执行模拟，流水线差异在报告中标注。");
            Console.WriteLine("* 所有数值为 MeasureFrames 次测量均值，单位 ms。");
        }

        // ──────────────────── 通用测量框架 ────────────────────

        /// <summary>测量 NativeJobScheduler 路径：WarmupFrames 预热 → MeasureFrames 计时</summary>
        private static double MeasureNative(Action action)
        {
            for (int f = 0; f < WarmupFrames; f++) action();
            long totalTicks = 0;
            for (int f = 0; f < MeasureFrames; f++)
            {
                long start = Stopwatch.GetTimestamp();
                action();
                totalTicks += Stopwatch.GetTimestamp() - start;
            }
            return (double)totalTicks * 1000.0 / (Stopwatch.Frequency * MeasureFrames);
        }

        /// <summary>
        /// 测量 Parallel.For 路径。
        /// 注意：ParFor 的 Schedule/Complete 隐含在 Parallel.For 调用中，无需额外包装。
        /// </summary>
        private static double MeasureParFor(Action action)
        {
            for (int f = 0; f < WarmupFrames; f++) action();
            long totalTicks = 0;
            for (int f = 0; f < MeasureFrames; f++)
            {
                long start = Stopwatch.GetTimestamp();
                action();
                totalTicks += Stopwatch.GetTimestamp() - start;
            }
            return (double)totalTicks * 1000.0 / (Stopwatch.Frequency * MeasureFrames);
        }

        /// <summary>测量 ThreadPool 路径</summary>
        private static double MeasureTPool(Action action)
        {
            for (int f = 0; f < WarmupFrames; f++) action();
            long totalTicks = 0;
            for (int f = 0; f < MeasureFrames; f++)
            {
                long start = Stopwatch.GetTimestamp();
                action();
                totalTicks += Stopwatch.GetTimestamp() - start;
            }
            return (double)totalTicks * 1000.0 / (Stopwatch.Frequency * MeasureFrames);
        }

        /// <summary>测量 ManagedJobScheduler 路径</summary>
        private static double MeasureManaged(Action action)
        {
            for (int f = 0; f < WarmupFrames; f++) action();
            long totalTicks = 0;
            for (int f = 0; f < MeasureFrames; f++)
            {
                long start = Stopwatch.GetTimestamp();
                action();
                totalTicks += Stopwatch.GetTimestamp() - start;
            }
            return (double)totalTicks * 1000.0 / (Stopwatch.Frequency * MeasureFrames);
        }

        // ──────────────────── 场景 1：分片加法 ────────────────────

        private double MeasureAddOne_Native() =>
            MeasureNative(() => new AddOneParallelForJob { Values = _nativeValues }
                .Schedule(ArrayLength, 0).Complete());   // batch=0 → 调度器按 worker 数自动分片

        private double MeasureAddOne_Managed() =>
            MeasureManaged(() =>
            {
                var job = new AddOneParallelForJob { Values = _managedValues };
                ManagedJobScheduler.Schedule(ref job, ArrayLength, 0).Complete();
            });

        private double MeasureAddOne_ParallelFor() =>
            MeasureParFor(() =>
            {
                var v = _parallelForValues;
                Parallel.For(0, ArrayLength, i => { v[i] = v[i] + 1; });
            });

        private double MeasureAddOne_ThreadPool() =>
            MeasureTPool(() => ThreadPoolForEach(_threadPoolValues, ArrayLength, WorkerSlices,
                (values, start, end) => { for (int i = start; i < end; i++) values[i] = values[i] + 1; }));

        // ──────────────────── 场景 2：空任务 ────────────────────

        private double MeasureEmpty_Native() =>
            MeasureNative(() => new EmptyParallelForJob()
                .Schedule(ArrayLength, 0).Complete());   // batch=0 → 自动分片

        private double MeasureEmpty_Managed() =>
            MeasureManaged(() =>
            {
                var job = new EmptyParallelForJob();
                ManagedJobScheduler.Schedule(ref job, ArrayLength, 0).Complete();
            });

        private double MeasureEmpty_ParallelFor() =>
            MeasureParFor(() => Parallel.For(0, ArrayLength, i => Interlocked.MemoryBarrier()));

        private double MeasureEmpty_ThreadPool() =>
            MeasureTPool(() => ThreadPoolForEach(_threadPoolValues, ArrayLength, WorkerSlices,
                (values, start, end) => { for (int i = start; i < end; i++) Interlocked.MemoryBarrier(); }));

        // ──────────────────── 场景 3：依赖链 ────────────────────

        private double MeasureChain_Native() =>
            MeasureNative(() =>
            {
                // 三个 IJobParallelFor 串成依赖链，每个内部并行遍历；依赖只确保顺序
                // batch=0 → 调度器按 worker 数自动分片
                var h1 = new ChainAddJob { Values = _nativeValues }
                    .Schedule(ArrayLength, 0);
                var h2 = new ChainMultiplyJob { Values = _nativeValues }
                    .Schedule(ArrayLength, 0, h1);
                var h3 = new ChainSubtractJob { Values = _nativeValues }
                    .Schedule(ArrayLength, 0, h2);
                h3.Complete();
            });

        private double MeasureChain_Managed() =>
            MeasureManaged(() =>
            {
                var j1 = new ChainAddJob { Values = _managedValues };
                var h1 = ManagedJobScheduler.Schedule(ref j1, ArrayLength, 0);
                var j2 = new ChainMultiplyJob { Values = _managedValues };
                var h2 = ManagedJobScheduler.Schedule(ref j2, ArrayLength, 0, h1);
                var j3 = new ChainSubtractJob { Values = _managedValues };
                var h3 = ManagedJobScheduler.Schedule(ref j3, ArrayLength, 0, h2);
                h3.Complete();
            });

        private double MeasureChain_ParallelFor() =>
            MeasureParFor(() =>
            {
                // 与 Native 保持一致的操作序列（+1 → *2 → -3），连续 3 次并行遍历
                // （Parallel.For 无原生依赖语义，只能顺序提交，流水线差异在报告中标注）
                var v = _parallelForValues;
                Parallel.For(0, ArrayLength, i => { v[i] = v[i] + 1; });
                Parallel.For(0, ArrayLength, i => { v[i] = v[i] * 2; });
                Parallel.For(0, ArrayLength, i => { v[i] = v[i] - 3; });
            });

        private double MeasureChain_ThreadPool() =>
            MeasureTPool(() =>
            {
                ThreadPoolForEach(_threadPoolValues, ArrayLength, WorkerSlices,
                    (values, start, end) => { for (int i = start; i < end; i++) values[i] = values[i] + 1; });
                ThreadPoolForEach(_threadPoolValues, ArrayLength, WorkerSlices,
                    (values, start, end) => { for (int i = start; i < end; i++) values[i] = values[i] * 2; });
                ThreadPoolForEach(_threadPoolValues, ArrayLength, WorkerSlices,
                    (values, start, end) => { for (int i = start; i < end; i++) values[i] = values[i] - 3; });
            });

        // ──────────────────── 场景 4：调度延迟 ────────────────────

        private double MeasureLatency_Native() =>
            MeasureNative(() =>
            {
                for (int i = 0; i < ScheduleLatencyIterations; i++)
                    new AddOneParallelForJob { Values = _latencyValues }
                        .Schedule(LatencyArrayLength, 0).Complete();   // batch=0 → 自动
            });

        private double MeasureLatency_Managed() =>
            MeasureManaged(() =>
            {
                for (int i = 0; i < ScheduleLatencyIterations; i++)
                {
                    var job = new AddOneParallelForJob { Values = _latencyValues };
                    ManagedJobScheduler.Schedule(ref job, LatencyArrayLength, 0).Complete();
                }
            });

        private double MeasureLatency_ParallelFor() =>
            MeasureParFor(() =>
            {
                var v = _latencyValues;
                for (int i = 0; i < ScheduleLatencyIterations; i++)
                    Parallel.For(0, LatencyArrayLength, j => { v[j] = v[j] + 1; });
            });

        private double MeasureLatency_ThreadPool() =>
            MeasureTPool(() =>
            {
                for (int i = 0; i < ScheduleLatencyIterations; i++)
                    ThreadPoolForEach(_latencyValues, LatencyArrayLength, WorkerSlices,
                        (values, start, end) => { for (int j = start; j < end; j++) values[j] = values[j] + 1; });
            });

        // ──────────────────── 场景 5：高竞争 ────────────────────

        private double MeasureHeavy_Native() =>
            MeasureNative(() => new HeavyComputeJob { Results = _heavyResults }
                .Schedule(HighContentionCount, 0).Complete());   // batch=0 → 自动

        private double MeasureHeavy_Managed() =>
            MeasureManaged(() =>
            {
                var job = new HeavyComputeJob { Results = _heavyResults };
                ManagedJobScheduler.Schedule(ref job, HighContentionCount, 0).Complete();
            });

        private double MeasureHeavy_ParallelFor() =>
            MeasureParFor(() =>
            {
                Parallel.For(0, HighContentionCount, i =>
                {
                    long sum = 0;
                    for (int j = 0; j < 1000; j++)
                        sum += (long)i * j;
                    _heavyResults[i] = sum;   // 必须写回：否则 JIT 死代码消除整个循环
                });
            });

        private double MeasureHeavy_ThreadPool() =>
            MeasureTPool(() => ThreadPoolHeavyWork(HighContentionCount, WorkerSlices));

        // ──────────────────── ThreadPool 辅助 ────────────────────

        /// <summary>
        /// 用 ThreadPool 分片执行操作。slices = 期望的并发分片数（= worker 核心数），
        /// 每片大小 = ceil(length / slices)，公平对比 Native 调度器的自动分片。
        /// </summary>
        private static void ThreadPoolForEach(NativeArray<int> values, int length, int slices,
            Action<NativeArray<int>, int, int> work)
        {
            int numBatches = Math.Max(1, slices);
            int batchSize = (length + numBatches - 1) / numBatches;
            using var countdown = new CountdownEvent(numBatches);

            for (int b = 0; b < numBatches; b++)
            {
                int start = b * batchSize;
                int end = Math.Min(start + batchSize, length);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    work(values, start, end);
                    countdown.Signal();
                });
            }

            countdown.Wait();
        }

        /// <summary>ThreadPool 高竞争重计算，按 slices 分片</summary>
        private void ThreadPoolHeavyWork(int length, int slices)
        {
            int numBatches = Math.Max(1, slices);
            int batchSize = (length + numBatches - 1) / numBatches;
            using var countdown = new CountdownEvent(numBatches);

            for (int b = 0; b < numBatches; b++)
            {
                int start = b * batchSize;
                int end = Math.Min(start + batchSize, length);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    for (int i = start; i < end; i++)
                    {
                        long sum = 0;
                        for (int j = 0; j < 1000; j++)
                            sum += (long)i * j;
                        // 必须写回，否则 JIT 死代码消除整个循环（`sum` 未使用）
                        _heavyResults[i] = sum;
                    }
                    countdown.Signal();
                });
            }

            countdown.Wait();
        }

        // ──────────────────── 场景协调（输出表格行） ────────────────────

        private void RunScenario(string label,
            Func<double> measureNative, Func<double> measureManaged,
            Func<double> measureParFor, Func<double> measureTPool)
        {
            double nativeMs = measureNative();

            string managedStr;
            if (ManagedEnabled)
            {
                double managedMs = measureManaged();
                managedStr = $"{managedMs,8:F3}  ";
            }
            else
            {
                managedStr = " [待实现]  ";
            }

            double parForMs = measureParFor();
            double tpMs = measureTPool();

            Console.WriteLine($"{label,-32}  {nativeMs,8:F3}     {managedStr}  {parForMs,8:F3}     {tpMs,8:F3}");
            Console.Out.Flush();
        }

        // ──────────────────── 清理 ────────────────────

        public void Dispose()
        {
            ManagedJobScheduler.Shutdown();
            _nativeValues.Dispose();
            _managedValues.Dispose();
            _parallelForValues.Dispose();
            _threadPoolValues.Dispose();
            _heavyResults.Dispose();
            _latencyValues.Dispose();
        }
    }
}