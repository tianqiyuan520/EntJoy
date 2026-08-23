using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Collections.Concurrent;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;
using EntJoy.Collections;
using Schedulers;
using JobsMP = Misaki.HighPerformance.Jobs;
using PowerThreadPool;
using PowerThreadPool.Options;

namespace JobLibsBenchmark
{
    // =====================================================================
    // 自研 Managed Job 定义（复用 ManagedJobScheduler，底层 .NET Parallel）
    // =====================================================================

    public struct ManagedAddJob : EntJoy.JobSystem.IJobParallelFor, EntJoy.JobSystem.IJobParallelForBatch
    {
        public int[] Values;
        public void Execute(int index) => Values[index] = Values[index] + 1;
        public void Execute(int startIndex, int count) { int e = startIndex + count; for (int i = startIndex; i < e; i++) Values[i] = Values[i] + 1; }
    }

    public struct ManagedEmptyJob : EntJoy.JobSystem.IJobParallelFor, EntJoy.JobSystem.IJobParallelForBatch
    {
        public void Execute(int index) => Thread.MemoryBarrier();
        public void Execute(int startIndex, int count) { int e = startIndex + count; for (int i = startIndex; i < e; i++) Thread.MemoryBarrier(); }
    }

    public struct ManagedChainJob1 : EntJoy.JobSystem.IJobParallelFor, EntJoy.JobSystem.IJobParallelForBatch
    {
        public int[] Values;
        public void Execute(int index) => Values[index] = Values[index] + 1;
        public void Execute(int startIndex, int count) { int e = startIndex + count; for (int i = startIndex; i < e; i++) Values[i] = Values[i] + 1; }
    }

    public struct ManagedChainJob2 : EntJoy.JobSystem.IJobParallelFor, EntJoy.JobSystem.IJobParallelForBatch
    {
        public int[] Values;
        public void Execute(int index) => Values[index] = Values[index] * 2;
        public void Execute(int startIndex, int count) { int e = startIndex + count; for (int i = startIndex; i < e; i++) Values[i] = Values[i] * 2; }
    }

    public struct ManagedChainJob3 : EntJoy.JobSystem.IJobParallelFor, EntJoy.JobSystem.IJobParallelForBatch
    {
        public int[] Values;
        public void Execute(int index) => Values[index] = Values[index] - 3;
        public void Execute(int startIndex, int count) { int e = startIndex + count; for (int i = startIndex; i < e; i++) Values[i] = Values[i] - 3; }
    }

    public struct ManagedHeavyJob : EntJoy.JobSystem.IJobParallelFor, EntJoy.JobSystem.IJobParallelForBatch
    {
        public long[] Results;
        public void Execute(int index) { long sum = 0; for (int j = 0; j < 1000; j++) sum += (long)index * j; Results[index] = sum; }
        public void Execute(int startIndex, int count) { int e = startIndex + count; for (int index = startIndex; index < e; index++) { long sum = 0; for (int j = 0; j < 1000; j++) sum += (long)index * j; Results[index] = sum; } }
    }

    public struct ManagedTinyJob : EntJoy.JobSystem.IJobParallelFor, EntJoy.JobSystem.IJobParallelForBatch
    {
        public void Execute(int index) { }
        public void Execute(int startIndex, int count) { }
    }

    // =====================================================================
    // ZeroAllocJobScheduler 1.1.2 Job 定义（class 型，需池化实例）
    // =====================================================================

    public class ZeroAllocAddJob : Schedulers.IJobParallelFor
    {
        public int[] Values;
        public void Execute(int i) => Values[i] = Values[i] + 1;
        public void Finish() { }
        public int BatchSize => 32;
        public int ThreadCount => 0;
    }

    public class ZeroAllocEmptyJob : Schedulers.IJobParallelFor
    {
        public void Execute(int i) => Thread.MemoryBarrier();
        public void Finish() { }
        public int BatchSize => 32;
        public int ThreadCount => 0;
    }

    public class ZeroAllocChainJob1 : Schedulers.IJobParallelFor
    {
        public int[] Values;
        public void Execute(int i) => Values[i] = Values[i] + 1;
        public void Finish() { }
        public int BatchSize => 32;
        public int ThreadCount => 0;
    }

    public class ZeroAllocChainJob2 : Schedulers.IJobParallelFor
    {
        public int[] Values;
        public void Execute(int i) => Values[i] = Values[i] * 2;
        public void Finish() { }
        public int BatchSize => 32;
        public int ThreadCount => 0;
    }

    public class ZeroAllocChainJob3 : Schedulers.IJobParallelFor
    {
        public int[] Values;
        public void Execute(int i) => Values[i] = Values[i] - 3;
        public void Finish() { }
        public int BatchSize => 32;
        public int ThreadCount => 0;
    }

    public class ZeroAllocHeavyJob : Schedulers.IJobParallelFor
    {
        public long[] Results;
        public void Execute(int i) { long sum = 0; for (int j = 0; j < 1000; j++) sum += (long)i * j; Results[i] = sum; }
        public void Finish() { }
        public int BatchSize => 32;
        public int ThreadCount => 0;
    }

    public class ZeroAllocTinyJob : Schedulers.IJobParallelFor
    {
        public void Execute(int i) { }
        public void Finish() { }
        public int BatchSize => 32;
        public int ThreadCount => 0;
    }

    // =====================================================================
    // Misaki.HighPerformance.Jobs 3.2.1 Job 定义（struct 型）
    // =====================================================================

    public struct MisakiAddJob : JobsMP.IJobParallelFor
    {
        public int[] Values;
        public void Execute(int loopIndex, ref readonly JobsMP.JobExecutionContext ctx) => Values[loopIndex] = Values[loopIndex] + 1;
    }

    public struct MisakiEmptyJob : JobsMP.IJobParallelFor
    {
        public void Execute(int loopIndex, ref readonly JobsMP.JobExecutionContext ctx) => Thread.MemoryBarrier();
    }

    public struct MisakiChainJob1 : JobsMP.IJobParallelFor
    {
        public int[] Values;
        public void Execute(int loopIndex, ref readonly JobsMP.JobExecutionContext ctx) => Values[loopIndex] = Values[loopIndex] + 1;
    }

    public struct MisakiChainJob2 : JobsMP.IJobParallelFor
    {
        public int[] Values;
        public void Execute(int loopIndex, ref readonly JobsMP.JobExecutionContext ctx) => Values[loopIndex] = Values[loopIndex] * 2;
    }

    public struct MisakiChainJob3 : JobsMP.IJobParallelFor
    {
        public int[] Values;
        public void Execute(int loopIndex, ref readonly JobsMP.JobExecutionContext ctx) => Values[loopIndex] = Values[loopIndex] - 3;
    }

    public struct MisakiHeavyJob : JobsMP.IJobParallelFor
    {
        public long[] Results;
        public void Execute(int loopIndex, ref readonly JobsMP.JobExecutionContext ctx) { long sum = 0; for (int j = 0; j < 1000; j++) sum += (long)loopIndex * j; Results[loopIndex] = sum; }
    }

    public struct MisakiTinyJob : JobsMP.IJobParallelFor
    {
        public void Execute(int loopIndex, ref readonly JobsMP.JobExecutionContext ctx) { }
    }

    // =====================================================================
    // S6: 控制流 + 重运算
    // 数据依赖分支（LCG 伪随机 → 分支），编译器无法代数简化/闭式替换。
    // 所有实现共用同一条计算语义（S6Compute.Run 内联），保证公平。
    // =====================================================================

    public static class S6Compute
    {
        /// <summary>S6 每元素计算：1000 次 LCG + 数据依赖分支。</summary>
        public static long Run(long seed)
        {
            long sum = 0;
            uint x = (uint)(seed * 2654435761u) + 1u;
            for (int j = 0; j < 1000; j++)
            {
                x = x * 1664525u + 1013904223u;
                uint r = x % 13u;
                if (r < 4u) sum += x;
                else if (r < 8u) sum ^= x;
                else sum -= (long)(x >> 3);
                if ((x & 7u) == 0u) sum += j;
            }
            return sum;
        }
    }

    public struct ManagedCtrlJob : EntJoy.JobSystem.IJobParallelFor, EntJoy.JobSystem.IJobParallelForBatch
    {
        public long[] Results;
        public void Execute(int index) => Results[index] = S6Compute.Run(index);
        public void Execute(int startIndex, int count) { int e = startIndex + count; for (int i = startIndex; i < e; i++) Results[i] = S6Compute.Run(i); }
    }

    public class ZeroAllocCtrlJob : Schedulers.IJobParallelFor
    {
        public long[] Results;
        public void Execute(int i) => Results[i] = S6Compute.Run(i);
        public void Finish() { }
        public int BatchSize => 32;
        public int ThreadCount => 0;
    }

    public struct MisakiCtrlJob : JobsMP.IJobParallelFor
    {
        public long[] Results;
        public void Execute(int loopIndex, ref readonly JobsMP.JobExecutionContext ctx) => Results[loopIndex] = S6Compute.Run(loopIndex);
    }

    // =====================================================================
    // 基准主体
    // =====================================================================

    public static class Program
    {
        private const int ArrayLength = 1_000_000;
        private const int HighContentionCount = 100_000;
        private const int LatencyIterations = 5; // S4 调度延迟：5 次（用户要求，避免逐库 1000 次调度过慢/卡）
        private const int LatencyLength = 1024;
        private const int LatencyWorks = 16; // PTP S4：每批次排少量空 work 模拟“调度小批量任务”
        private const int WarmupFrames = 5;
        private const int MeasureFrames = 5;
        private const int MinRounds = 9; // 方差控制：9 轮取中位数，可暴露 ±30% 波动的真实分布

        // 统一对比标准：所有库共用同一条 worker 数（默认 PC-1，尊重 EntJoy 主线程留核设计），
        // 消除 "EntJoy PC-1 vs 对手满核 PC" 的基准不对称。ENTJOY_BENCH_WORKERS 可覆盖做诊断。
        private static int _workerCount;

        private static int SliceCount => Math.Max(1, _workerCount);
        private static int SliceSize => (ArrayLength + SliceCount - 1) / SliceCount;
        private static int HeavySliceSize => (HighContentionCount + SliceCount - 1) / SliceCount;

        private static int[] _values = new int[ArrayLength];
        private static long[] _heavyResults = new long[HighContentionCount];
        private static long[] _ctrlResults = new long[HighContentionCount]; // S6 控制流重算（托管侧）

        // NativeTranspiler（C++/ISPC）基准用的原生容器缓冲：
        // 原生 job 的字段必须是非托管类型（NativeArray<int>），不能用 int[]
        private static EntJoy.Collections.NativeArray<int> _nativeValues;
        private static EntJoy.Collections.NativeArray<int> _nativeHeavyResults;
        private static EntJoy.Collections.NativeArray<int> _nativeCtrlResults; // S6（原生侧）
        private static EntJoy.Collections.NativeArray<int> _nativeAutoSIMDResults; // S5/S6 AutoSIMD

        // ZeroAlloc scheduler 实例
        private static JobScheduler? _zeroAllocScheduler;

        // ZeroAlloc：池化 class job 实例（README 要求尽量复用，避免每次 new 触发 GC）
        private static ZeroAllocAddJob? _zaAdd;
        private static ZeroAllocEmptyJob? _zaEmpty;
        private static ZeroAllocChainJob1? _zaChain1;
        private static ZeroAllocChainJob2? _zaChain2;
        private static ZeroAllocChainJob3? _zaChain3;
        private static ZeroAllocHeavyJob? _zaHeavy;
        private static ZeroAllocTinyJob? _zaTiny;
        private static ZeroAllocCtrlJob? _zaCtrl; // S6

        // Misaki scheduler 实例
        private static JobsMP.JobScheduler? _misakiScheduler;

        // PowerThreadPool 实例
        private static PowerPool? _ptp;

        public static void Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);

            // ── 统一对比标准 ──
            // 所有库用同一条 worker 数。默认 = PC-1（尊重主线程留核设计）；
            // 诊断"满核能否吃回空核吞吐"时用 ENTJOY_BENCH_WORKERS=16 覆盖。
            _workerCount = Math.Max(1, Environment.ProcessorCount - 1);
            var wEnv = Environment.GetEnvironmentVariable("ENTJOY_BENCH_WORKERS");
            if (int.TryParse(wEnv, out var wv) && wv >= 1 && wv <= Environment.ProcessorCount) _workerCount = wv;
            int wc = _workerCount;

            ManagedJobScheduler.Initialize(wc);
            // 原生 NativeDll JobSystem：加载已构建的 NativeDll.dll，开启原生 C++ 线程池
            NativeJobScheduler.Initialize(wc);
            // Part B：关闭每 tile 的 timing 诊断（跳过 MonotonicNowNs/CPU/cycle/core 探测 + slowRangeLock 争用）
            NativeJobScheduler.SetTimingDiagnosticsEnabled(false);

            // JobCostCache（per-job 自动 batch）：ENTJOY_JOB_COST_CACHE=1 开启。
            // 开启后 worker 按 per-job 每元素成本 EWMA 自动求解最优 tile 数。
            var costCacheEnv = Environment.GetEnvironmentVariable("ENTJOY_JOB_COST_CACHE");
            if (int.TryParse(costCacheEnv, out var cce) && cce > 0)
            {
                NativeJobScheduler.JobCostCacheEnabled = true;
                Console.WriteLine($"JobCostCache=ON (per-job auto batch)");
            }

            // TPL(TPool/ParFor) 走全局 ThreadPool，压到同一上限保证同为 wc 线程
            //（ManagedJobScheduler 用自有 Thread[]，不受此影响）
            ThreadPool.GetMinThreads(out int tpMinW, out int tpMinIo);
            ThreadPool.SetMinThreads(wc, tpMinIo);
            if (!ThreadPool.SetMaxThreads(wc, Environment.ProcessorCount))
                Console.WriteLine("Warning: 无法将 ThreadPool 上限设为统一 worker 数");

            _zeroAllocScheduler = new JobScheduler(new JobScheduler.Config
            {
                ThreadPrefixName = "ZeroAlloc",
                ThreadCount = wc,
                MaxExpectedConcurrentJobs = 4096,
                StrictAllocationMode = false,
            });

            // 池化 ZeroAlloc 的 class job 实例（复用，减少 GC）
            _zaAdd = new ZeroAllocAddJob();
            _zaEmpty = new ZeroAllocEmptyJob();
            _zaChain1 = new ZeroAllocChainJob1();
            _zaChain2 = new ZeroAllocChainJob2();
            _zaChain3 = new ZeroAllocChainJob3();
            _zaHeavy = new ZeroAllocHeavyJob();
            _zaTiny = new ZeroAllocTinyJob();
            _zaCtrl = new ZeroAllocCtrlJob();

            var misakiDesc = new JobsMP.JobSchedulerDesc
            {
                ThreadCount = wc,
                DependencyChainCapacity = 4096,
                ThreadPriority = ThreadPriority.Normal,
            };
            _misakiScheduler = new JobsMP.JobScheduler(in misakiDesc);

            _ptp = new PowerPool(new PowerPoolOption
            {
                MaxThreads = wc,
            });

            // NativeTranspiler（C++/ISPC）job 用原生缓冲：从 _values 复制一份 NativeArray<int>
            _nativeValues = new EntJoy.Collections.NativeArray<int>(_values, EntJoy.Collections.Allocator.Persistent);
            _nativeHeavyResults = new EntJoy.Collections.NativeArray<int>(HighContentionCount, EntJoy.Collections.Allocator.Persistent);
            _nativeCtrlResults = new EntJoy.Collections.NativeArray<int>(HighContentionCount, EntJoy.Collections.Allocator.Persistent);
            _nativeAutoSIMDResults = new EntJoy.Collections.NativeArray<int>(HighContentionCount, EntJoy.Collections.Allocator.Persistent);

            Console.WriteLine($"UnifiedWorkerCount={wc} (PC-1 默认，ENTJOY_BENCH_WORKERS 可覆盖) ProcessorCount={Environment.ProcessorCount}");
            Console.WriteLine();

            // 可选参数过滤：如 "S2" 只跑该场景，便于定位/冒烟。
            var filter = args.Length > 0 ? args[0] : null;
            var scenarios = new (string name, string label, Func<double> managed, Func<double> native, Func<double> cpp, Func<double> ispc, Func<double> autoSIMD, Func<double> zeroAlloc, Func<double> misaki, Func<double> ptp, Func<double> tpool, Func<double> parFor, Func<double> cBag, Func<double> channel, Func<double> ntdls)[]
            {
                ("S1", "S1 分片加法(100万)", MeasureManagedAdd, MeasureNativeAdd, MeasureNativeAddCpp, MeasureNativeAddIspc, MeasureNativeAddAutoSIMD, MeasureZeroAllocAdd, MeasureMisakiAdd, MeasurePtForAdd, MeasureTpAdd, MeasureParForAdd, MeasureConcurrentBagAdd, MeasureChannelAdd, MeasureTaskAdd),
                ("S2", "S2 空任务(100万)", MeasureManagedEmpty, MeasureNativeEmpty, MeasureNativeEmptyCpp, MeasureNativeEmptyIspc, null, MeasureZeroAllocEmpty, MeasureMisakiEmpty, MeasurePtForEmpty, MeasureTpEmpty, MeasureParForEmpty, MeasureConcurrentBagEmpty, MeasureChannelEmpty, MeasureTaskEmpty),
                ("S3", "S3 依赖链(+1→x2→-3)", MeasureManagedChain, MeasureNativeChain, MeasureNativeChainCpp, MeasureNativeChainIspc, MeasureNativeChainAutoSIMD, MeasureZeroAllocChain, MeasureMisakiChain, MeasurePtChain, MeasureTpChain, MeasureParForChain, MeasureConcurrentBagChain, MeasureChannelChain, MeasureTaskChain),
                ("S4", "S4 调度延迟(1000x1024)", MeasureManagedLatency, MeasureNativeLatency, MeasureNativeEmptyLatencyCpp, MeasureNativeEmptyLatencyIspc, null, MeasureZeroAllocLatency, MeasureMisakiLatency, MeasurePtLatency, MeasureTpLatency, MeasureParForLatency, MeasureConcurrentBagLatency, MeasureChannelLatency, MeasureTaskLatency),
                ("S5", "S5 高竞争(10万x1000)", MeasureManagedHeavy, MeasureNativeHeavy, MeasureNativeHeavyCpp, MeasureNativeHeavyIspc, MeasureNativeHeavyAutoSIMD, MeasureZeroAllocHeavy, MeasureMisakiHeavy, MeasurePtForHeavy, MeasureTpHeavy, MeasureParForHeavy, MeasureConcurrentBagHeavy, MeasureChannelHeavy, MeasureTaskHeavy),
                ("S6", "S6 控制流重算(10万x1000)", MeasureManagedCtrl, MeasureNativeCtrl, MeasureNativeCtrlCpp, MeasureNativeCtrlIspc, MeasureNativeCtrlAutoSIMD, MeasureZeroAllocCtrl, MeasureMisakiCtrl, MeasurePtForCtrl, MeasureTpCtrl, MeasureParForCtrl, MeasureConcurrentBagCtrl, MeasureChannelCtrl, MeasureTaskCtrl),
            };

            var summary = new List<SummaryRow>();
            foreach (var sc in scenarios)
            {
                if (filter != null && !sc.name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) continue;
                summary.Add(RunScenario(sc.name, sc.label, sc.managed, sc.native, sc.cpp, sc.ispc, sc.autoSIMD, sc.zeroAlloc, sc.misaki, sc.ptp, sc.tpool, sc.parFor, sc.cBag, sc.channel, sc.ntdls));
            }
            PrintSummaryTable(summary);

            // AutoSIMD 结果验证（S1/S3/S5/S6）
            if (filter == null || filter.StartsWith("S", StringComparison.OrdinalIgnoreCase))
            {
                VerifyAutoSIMD();
            }

            _ptp.Dispose();
            _misakiScheduler.Dispose();
            _zeroAllocScheduler.Dispose();
            _nativeValues.Dispose();
            _nativeHeavyResults.Dispose();
            _nativeCtrlResults.Dispose();
            _nativeAutoSIMDResults.Dispose();
            NativeJobScheduler.Shutdown();
            ManagedJobScheduler.Shutdown();
        }

        private static SummaryRow RunScenario(
            string name, string label,
            Func<double> managed, Func<double> native, Func<double> cpp, Func<double> ispc,
            Func<double> autoSIMD, Func<double> zeroAlloc, Func<double> misaki, Func<double> ptp,
            Func<double> tpool, Func<double> parFor,
            Func<double> cBag, Func<double> channel, Func<double> ntdls)
        {
            GcClean();
            var ms = StatsOf(managed, MinRounds);
            var ns = StatsOf(native, MinRounds);
            var cs = StatsOf(cpp, MinRounds);
            var is_ = StatsOf(ispc, MinRounds);
            var as_ = autoSIMD != null ? StatsOf(autoSIMD, MinRounds) : default;
            var zs = StatsOf(zeroAlloc, MinRounds);
            var ks = StatsOf(misaki, MinRounds);   // k = misaki (is 是 C# 关键字)
            var ps = StatsOf(ptp, MinRounds);
            var ts = StatsOf(tpool, MinRounds);
            var fs = StatsOf(parFor, MinRounds);
            var bs = StatsOf(cBag, MinRounds);
            var chs = StatsOf(channel, MinRounds);
            var ns2 = StatsOf(ntdls, MinRounds);

            // 胜负按中位数判定（不取最小值，避免挑运气轮）
            double mm = ms.Median, nm = ns.Median, cm = cs.Median, im = is_.Median,
                   asm_ = as_.Median, zm = zs.Median, km = ks.Median,
                   pm = ps.Median, tm = ts.Median, fm = fs.Median,
                   bm = bs.Median, chm = chs.Median, nm2 = ns2.Median;
            // 自动 SIMD 胜负（仅 S5/S6 有效，null 则跳过）
            string winner;
            if (autoSIMD == null)
                winner = mm <= nm && mm <= cm && mm <= im && mm <= zm && mm <= km && mm <= pm && mm <= tm && mm <= fm && mm <= bm && mm <= chm && mm <= nm2 ? "Managed" :
                            nm <= cm && nm <= im && nm <= zm && nm <= km && nm <= pm && nm <= tm && nm <= fm && nm <= bm && nm <= chm && nm <= nm2 ? "NativeDll" :
                            cm <= im && cm <= zm && cm <= km && cm <= pm && cm <= tm && cm <= fm && cm <= bm && cm <= chm && cm <= nm2 ? "Cpp" :
                            im <= zm && im <= km && im <= pm && im <= tm && im <= fm && im <= bm && im <= chm && im <= nm2 ? "Ispc" :
                            zm <= km && zm <= pm && zm <= tm && zm <= fm && zm <= bm && zm <= chm && zm <= nm2 ? "ZeroAlloc" :
                            km <= pm && km <= tm && km <= fm && km <= bm && km <= chm && km <= nm2 ? "Misaki" :
                            pm <= tm && pm <= fm && pm <= bm && pm <= chm && pm <= nm2 ? "PTP" :
                            tm <= fm && tm <= bm && tm <= chm && tm <= nm2 ? "TPool" :
                            fm <= bm && fm <= chm && fm <= nm2 ? "ParFor" :
                            bm <= chm && bm <= nm2 ? "CBag" :
                            chm <= nm2 ? "Channel" : "NTDLS";
            else
                winner = mm <= nm && mm <= cm && mm <= im && mm <= asm_ && mm <= zm && mm <= km && mm <= pm && mm <= tm && mm <= fm && mm <= bm && mm <= chm && mm <= nm2 ? "Managed" :
                            nm <= cm && nm <= im && nm <= asm_ && nm <= zm && nm <= km && nm <= pm && nm <= tm && nm <= fm && nm <= bm && nm <= chm && nm <= nm2 ? "NativeDll" :
                            cm <= im && cm <= asm_ && cm <= zm && cm <= km && cm <= pm && cm <= tm && cm <= fm && cm <= bm && cm <= chm && cm <= nm2 ? "Cpp" :
                            im <= asm_ && im <= zm && im <= km && im <= pm && im <= tm && im <= fm && im <= bm && im <= chm && im <= nm2 ? "Ispc" :
                            asm_ <= zm && asm_ <= km && asm_ <= pm && asm_ <= tm && asm_ <= fm && asm_ <= bm && asm_ <= chm && asm_ <= nm2 ? "AutoSIMD" :
                            zm <= km && zm <= pm && zm <= tm && zm <= fm && zm <= bm && zm <= chm && zm <= nm2 ? "ZeroAlloc" :
                            km <= pm && km <= tm && km <= fm && km <= bm && km <= chm && km <= nm2 ? "Misaki" :
                            pm <= tm && pm <= fm && pm <= bm && pm <= chm && pm <= nm2 ? "PTP" :
                            tm <= fm && tm <= bm && tm <= chm && tm <= nm2 ? "TPool" :
                            fm <= bm && fm <= chm && fm <= nm2 ? "ParFor" :
                            bm <= chm && bm <= nm2 ? "CBag" :
                            chm <= nm2 ? "Channel" : "NTDLS";

            string Fmt(RunStats s) => $"{s.Median,7:F3} ±{s.SpreadPct,4:F0}%";
            string FmtOptional(RunStats s) => s.Count == 0 ? "      -" : $"{s.Median,7:F3} ±{s.SpreadPct,4:F0}%";

            Console.WriteLine($"{label,-22}  Managed={Fmt(ms)}  Native={Fmt(ns)}  Cpp={Fmt(cs)}  Ispc={Fmt(is_)}  AutoSIMD={FmtOptional(as_)}  ZeroAlloc={Fmt(zs)}  Misaki={Fmt(ks)}  PTP={Fmt(ps)}  TPool={Fmt(ts)}  ParFor={Fmt(fs)}  CBag={Fmt(bs)}  Chan={Fmt(chs)}  NTDLS={Fmt(ns2)}   [胜={winner}]");
            Console.Out.Flush();

            return new SummaryRow(name, mm, nm, cm, im, asm_, zm, km, pm, tm, fm, bm, chm, nm2, winner);
        }

        /// <summary>每个场景的汇总行（含全部实现的中位数，用于完整汇总对比表）</summary>
        private readonly struct SummaryRow
        {
            public readonly string Name;
            public readonly double Managed, Native, Cpp, Ispc, AutoSIMD, ZeroAlloc, Misaki, PTP, TPool, ParFor, CBag, Channel, NTDLS;
            public readonly string Winner;

            public SummaryRow(string name, double managed, double native, double cpp, double ispc,
                              double autoSIMD, double zeroAlloc, double misaki, double ptp,
                              double tpool, double parFor,
                              double cBag, double channel, double ntdls, string winner)
            {
                Name = name;
                Managed = managed; Native = native; Cpp = cpp; Ispc = ispc; AutoSIMD = autoSIMD;
                ZeroAlloc = zeroAlloc; Misaki = misaki; PTP = ptp; TPool = tpool; ParFor = parFor;
                CBag = cBag; Channel = channel; NTDLS = ntdls;
                Winner = winner;
            }
        }

        /// <summary>打印完整汇总对比表（全部 12 个实现 + 胜者）</summary>
        private static void PrintSummaryTable(List<SummaryRow> rows)
        {
            if (rows.Count == 0) return;
            Console.WriteLine();
            Console.WriteLine("=== Summary (median ms): all implementations ===");
            string hdr = $"| {"Scene",-5} | {"Manag",7} | {"Native",7} | {"Cpp",7} | {"Ispc",7} | {"AutoSIMD",7} | {"ZeroAlloc",9} | {"Misaki",7} | {"PTP",7} | {"TPool",7} | {"ParFor",7} | {"CBag",7} | {"Chan",7} | {"NTDLS",7} | {"Winner",-8} |";
            Console.WriteLine(hdr);
            Console.WriteLine(new string('-', hdr.Length));
            foreach (var r in rows)
            {
                string asStr = r.AutoSIMD > 0 ? $"{r.AutoSIMD,7:F3}" : "      -";
                Console.WriteLine($"| {r.Name,-5} | {r.Managed,7:F3} | {r.Native,7:F3} | {r.Cpp,7:F3} | {r.Ispc,7:F3} | {asStr} | {r.ZeroAlloc,9:F3} | {r.Misaki,7:F3} | {r.PTP,7:F3} | {r.TPool,7:F3} | {r.ParFor,7:F3} | {r.CBag,7:F3} | {r.Channel,7:F3} | {r.NTDLS,7:F3} | {r.Winner,-8} |");
            }
            Console.WriteLine(new string('-', hdr.Length));
            Console.WriteLine("注：数值为各实现中位数（ms）。AutoSIMD=C++ SIMD向量化（NativeTranspiler AutoSIMD），仅S5/S6有效");
            Console.Out.Flush();
        }

        private static void GcClean()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>9 轮跑完后收集排序，返回中位数/极差，暴露 S5 ±30% 级波动。</summary>
        private readonly struct RunStats
        {
            public readonly double Median, Min, Max;
            public readonly int Count;
            public readonly double[] All; // sorted

            public RunStats(double[] raw)
            {
                Array.Sort(raw);
                All = raw;
                Count = raw.Length;
                Median = raw[Count / 2];
                Min = raw[0];
                Max = raw[^1];
            }

            public double SpreadPct => Median > 1e-6 ? (Max - Min) / Median * 100.0 : 0;
        }

        private static RunStats StatsOf(Func<double> measure, int k)
        {
            GcClean();
            var r = new double[k];
            for (int i = 0; i < k; i++) r[i] = measure();
            return new RunStats(r);
        }

        private static double Measure(Action frame)
        {
            for (int f = 0; f < WarmupFrames; f++) frame();
            long total = 0;
            for (int f = 0; f < MeasureFrames; f++) { long s = Stopwatch.GetTimestamp(); frame(); total += Stopwatch.GetTimestamp() - s; }
            return (double)total * 1000.0 / (Stopwatch.Frequency * MeasureFrames);
        }

        // ──────────────────── S1: 分片加法 ────────────────────

        static double MeasureManagedAdd() => Measure(() =>
        {
            var j = new ManagedAddJob { Values = _values };
            ManagedJobScheduler.Schedule(ref j, ArrayLength, 0).Complete();
        });

        static double MeasureZeroAllocAdd() => Measure(() =>
        {
            _zaAdd!.Values = _values;
            var handle = _zeroAllocScheduler!.Schedule(_zaAdd, ArrayLength);
            _zeroAllocScheduler.Flush();
            handle.Complete();
        });

        static double MeasureMisakiAdd() => Measure(() =>
        {
            var job = new MisakiAddJob { Values = _values };
            var handle = _misakiScheduler!.ScheduleParallelFor(in job, ArrayLength, 64);
            _misakiScheduler.Wait(handle, inlineExecution: false);
        });

        // PTP 的 For 是「每迭代一个 work item」的逐元素 API，不适合 100 万元素直接跑。
        // 按 TPool 基线的做法粗分片后 QueueWorkItem（每片一个 work item）+ 池级 Wait——
        // 即“无依赖的批量 IJob 型”对照。
        static double MeasurePtForAdd() => Measure(() =>
        {
            var v = _values;
            for (int i = 0; i < SliceCount; i++)
            {
                int s = i * SliceSize, e = Math.Min(s + SliceSize, ArrayLength);
                _ptp!.QueueWorkItem(() => { for (int k = s; k < e; k++) v[k] = v[k] + 1; });
            }
            _ptp!.Wait(helpWhileWaiting: true);
        });

        static double MeasureTpAdd() => Measure(() =>
        {
            var v = _values;
            int slices = _workerCount;
            int bs = (ArrayLength + slices - 1) / slices;
            using var cd = new CountdownEvent(slices);
            for (int b = 0; b < slices; b++)
            {
                int s = b * bs, e = Math.Min(s + bs, ArrayLength);
                ThreadPool.QueueUserWorkItem(_ => { for (int i = s; i < e; i++) v[i] = v[i] + 1; cd.Signal(); });
            }
            cd.Wait();
        });

        static double MeasureParForAdd() => Measure(() =>
        {
            var v = _values;
            System.Threading.Tasks.Parallel.For(0, ArrayLength, i => v[i] = v[i] + 1);
        });

        // ──────────────────── S1 新框架: ConcurrentBag / Channels / Task ────────────────────

        static double MeasureConcurrentBagAdd() => Measure(() =>
        {
            var v = _values;
            int slices = SliceCount;
            int bs = SliceSize;
            var bag = new ConcurrentBag<Action>();
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, ArrayLength);
                bag.Add(() => { for (int k = s; k < e; k++) v[k] = v[k] + 1; });
            }
            using var cd = new CountdownEvent(bag.Count);
            var threads = new Thread[bag.Count];
            int idx = 0;
            foreach (var action in bag)
            {
                int i = idx++;
                threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                threads[i].Start();
            }
            cd.Wait();
        });

        static double MeasureChannelAdd() => Measure(() =>
        {
            var v = _values;
            int slices = SliceCount;
            int bs = SliceSize;
            var channel = Channel.CreateBounded<Action>(new BoundedChannelOptions(slices) { SingleReader = false, SingleWriter = true });
            var writer = channel.Writer;
            var reader = channel.Reader;
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, ArrayLength);
                writer.TryWrite(() => { for (int k = s; k < e; k++) v[k] = v[k] + 1; });
            }
            writer.Complete();
            using var cd = new CountdownEvent(slices);
            var threads = new Thread[slices];
            int idx = 0;
            while (reader.TryRead(out var action))
            {
                int i = idx++;
                threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                threads[i].Start();
            }
            cd.Wait();
        });

        static double MeasureTaskAdd() => Measure(() =>
        {
            var v = _values;
            int slices = SliceCount;
            int bs = SliceSize;
            var tasks = new Task[slices];
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, ArrayLength);
                tasks[i] = Task.Run(() => { for (int k = s; k < e; k++) v[k] = v[k] + 1; });
            }
            Task.WaitAll(tasks);
        });

        // ──────────────────── S2: 空任务 ────────────────────

        static double MeasureManagedEmpty() => Measure(() =>
        {
            var j = new ManagedEmptyJob();
            // S2 走静态大块（空任务认领竞争致命，共享游标 0.79 远差于静态 0.40）
            ManagedJobScheduler.Schedule(ref j, ArrayLength, 0).Complete();
        });

        static double MeasureZeroAllocEmpty() => Measure(() =>
        {
            var handle = _zeroAllocScheduler!.Schedule(_zaEmpty!, ArrayLength);
            _zeroAllocScheduler.Flush();
            handle.Complete();
        });

        static double MeasureMisakiEmpty() => Measure(() =>
        {
            var job = new MisakiEmptyJob();
            var handle = _misakiScheduler!.ScheduleParallelFor(in job, ArrayLength, 64);
            _misakiScheduler.Wait(handle, inlineExecution: false);
        });

        static double MeasurePtForEmpty() => Measure(() =>
        {
            for (int i = 0; i < SliceCount; i++)
            {
                int s = i * SliceSize, e = Math.Min(s + SliceSize, ArrayLength);
                _ptp!.QueueWorkItem(() => { for (int k = s; k < e; k++) Thread.MemoryBarrier(); });
            }
            _ptp!.Wait(helpWhileWaiting: true);
        });

        static double MeasureTpEmpty() => Measure(() =>
        {
            int slices = _workerCount;
            int bs = (ArrayLength + slices - 1) / slices;
            using var cd = new CountdownEvent(slices);
            for (int b = 0; b < slices; b++)
            {
                int s = b * bs, e = Math.Min(s + bs, ArrayLength);
                ThreadPool.QueueUserWorkItem(_ => { for (int i = s; i < e; i++) Thread.MemoryBarrier(); cd.Signal(); });
            }
            cd.Wait();
        });

        static double MeasureParForEmpty() => Measure(() =>
        {
            System.Threading.Tasks.Parallel.For(0, ArrayLength, i => Thread.MemoryBarrier());
        });

        // ──────────────────── S2 新框架 ────────────────────

        static double MeasureConcurrentBagEmpty() => Measure(() =>
        {
            int slices = SliceCount;
            int bs = SliceSize;
            var bag = new ConcurrentBag<Action>();
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, ArrayLength);
                bag.Add(() => { for (int k = s; k < e; k++) Thread.MemoryBarrier(); });
            }
            using var cd = new CountdownEvent(bag.Count);
            var threads = new Thread[bag.Count];
            int idx = 0;
            foreach (var action in bag)
            {
                int i = idx++;
                threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                threads[i].Start();
            }
            cd.Wait();
        });

        static double MeasureChannelEmpty() => Measure(() =>
        {
            int slices = SliceCount;
            int bs = SliceSize;
            var channel = Channel.CreateBounded<Action>(new BoundedChannelOptions(slices) { SingleReader = false, SingleWriter = true });
            var writer = channel.Writer;
            var reader = channel.Reader;
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, ArrayLength);
                writer.TryWrite(() => { for (int k = s; k < e; k++) Thread.MemoryBarrier(); });
            }
            writer.Complete();
            using var cd = new CountdownEvent(slices);
            var threads = new Thread[slices];
            int idx = 0;
            while (reader.TryRead(out var action))
            {
                int i = idx++;
                threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                threads[i].Start();
            }
            cd.Wait();
        });

        static double MeasureTaskEmpty() => Measure(() =>
        {
            int slices = SliceCount;
            int bs = SliceSize;
            var tasks = new Task[slices];
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, ArrayLength);
                tasks[i] = Task.Run(() => { for (int k = s; k < e; k++) Thread.MemoryBarrier(); });
            }
            Task.WaitAll(tasks);
        });

        // ──────────────────── S3: 依赖链 ────────────────────

        static double MeasureManagedChain() => Measure(() =>
        {
            var v = _values;
            var j1 = new ManagedChainJob1 { Values = v };
            var j2 = new ManagedChainJob2 { Values = v };
            var j3 = new ManagedChainJob3 { Values = v };
            var h1 = ManagedJobScheduler.Schedule(ref j1, ArrayLength, 0);
            var h2 = ManagedJobScheduler.Schedule(ref j2, ArrayLength, 0, h1);
            var h3 = ManagedJobScheduler.Schedule(ref j3, ArrayLength, 0, h2);
            h3.Complete();
        });

        static double MeasureZeroAllocChain() => Measure(() =>
        {
            _zaChain1!.Values = _values;
            _zaChain2!.Values = _values;
            _zaChain3!.Values = _values;
            var h1 = _zeroAllocScheduler!.Schedule(_zaChain1, ArrayLength);
            var h2 = _zeroAllocScheduler.Schedule(_zaChain2, ArrayLength, h1);
            var h3 = _zeroAllocScheduler.Schedule(_zaChain3, ArrayLength, h2);
            _zeroAllocScheduler.Flush();
            h3.Complete();
        });

        static double MeasureMisakiChain() => Measure(() =>
        {
            var v = _values;
            var j1 = new MisakiChainJob1 { Values = v };
            var j2 = new MisakiChainJob2 { Values = v };
            var j3 = new MisakiChainJob3 { Values = v };
            var h1 = _misakiScheduler!.ScheduleParallelFor(in j1, ArrayLength, 64);
            var h2 = _misakiScheduler.ScheduleParallelFor(in j2, ArrayLength, 64, h1);
            var h3 = _misakiScheduler.ScheduleParallelFor(in j3, ArrayLength, 64, h2);
            // inlineExecution:false —— Misaki 3.2.1 的 inline Wait 纯自旋，配合依赖链在此环境偶发死锁，
            // 改由 worker 推进，主线程只等待。
            _misakiScheduler.Wait(h3, inlineExecution: false);
        });

        // PTP：无原生 parallel-for 依赖链，用三阶段 QueueWorkItem(粗分片)+Wait 近似（如实记录）
        static double MeasurePtChain() => Measure(() =>
        {
            var v = _values;
            void Stage(Func<int, int> f)
            {
                for (int i = 0; i < SliceCount; i++)
                {
                    int s = i * SliceSize, e = Math.Min(s + SliceSize, ArrayLength);
                    _ptp!.QueueWorkItem(() => { for (int k = s; k < e; k++) v[k] = f(v[k]); });
                }
                _ptp!.Wait(helpWhileWaiting: true);
            }
            Stage(x => x + 1);
            Stage(x => x * 2);
            Stage(x => x - 3);
        });

        static double MeasureTpChain() => Measure(() =>
        {
            var v = _values;
            void Stage(Func<int, int> f)
            {
                int slices = _workerCount;
                int bs = (ArrayLength + slices - 1) / slices;
                using var cd = new CountdownEvent(slices);
                for (int b = 0; b < slices; b++)
                {
                    int s = b * bs, e = Math.Min(s + bs, ArrayLength);
                    ThreadPool.QueueUserWorkItem(_ => { for (int i = s; i < e; i++) v[i] = f(v[i]); cd.Signal(); });
                }
                cd.Wait();
            }
            Stage(x => x + 1);
            Stage(x => x * 2);
            Stage(x => x - 3);
        });

        static double MeasureParForChain() => Measure(() =>
        {
            var v = _values;
            System.Threading.Tasks.Parallel.For(0, ArrayLength, i => v[i] = v[i] + 1);
            System.Threading.Tasks.Parallel.For(0, ArrayLength, i => v[i] = v[i] * 2);
            System.Threading.Tasks.Parallel.For(0, ArrayLength, i => v[i] = v[i] - 3);
        });

        // ──────────────────── S3 新框架 ────────────────────

        static double MeasureConcurrentBagChain() => Measure(() =>
        {
            var v = _values;
            void Stage(Func<int, int> op)
            {
                int slices = SliceCount;
                int bs = SliceSize;
                var bag = new ConcurrentBag<Action>();
                for (int i = 0; i < slices; i++)
                {
                    int s = i * bs, e = Math.Min(s + bs, ArrayLength);
                    bag.Add(() => { for (int k = s; k < e; k++) v[k] = op(v[k]); });
                }
                using var cd = new CountdownEvent(bag.Count);
                var threads = new Thread[bag.Count];
                int idx = 0;
                foreach (var action in bag)
                {
                    int i = idx++;
                    threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                    threads[i].Start();
                }
                cd.Wait();
            }
            Stage(x => x + 1);
            Stage(x => x * 2);
            Stage(x => x - 3);
        });

        static double MeasureChannelChain() => Measure(() =>
        {
            var v = _values;
            void Stage(Func<int, int> op)
            {
                int slices = SliceCount;
                int bs = SliceSize;
                var channel = Channel.CreateBounded<Action>(new BoundedChannelOptions(slices) { SingleReader = false, SingleWriter = true });
                var writer = channel.Writer;
                var reader = channel.Reader;
                for (int i = 0; i < slices; i++)
                {
                    int s = i * bs, e = Math.Min(s + bs, ArrayLength);
                    writer.TryWrite(() => { for (int k = s; k < e; k++) v[k] = op(v[k]); });
                }
                writer.Complete();
                using var cd = new CountdownEvent(slices);
                var threads = new Thread[slices];
                int idx = 0;
                while (reader.TryRead(out var action))
                {
                    int i = idx++;
                    threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                    threads[i].Start();
                }
                cd.Wait();
            }
            Stage(x => x + 1);
            Stage(x => x * 2);
            Stage(x => x - 3);
        });

        static double MeasureTaskChain() => Measure(() =>
        {
            var v = _values;
            void Stage(Func<int, int> op)
            {
                int slices = SliceCount;
                int bs = SliceSize;
                var tasks = new Task[slices];
                for (int i = 0; i < slices; i++)
                {
                    int s = i * bs, e = Math.Min(s + bs, ArrayLength);
                    tasks[i] = Task.Run(() => { for (int k = s; k < e; k++) v[k] = op(v[k]); });
                }
                Task.WaitAll(tasks);
            }
            Stage(x => x + 1);
            Stage(x => x * 2);
            Stage(x => x - 3);
        });

        // ──────────────────── S4: 调度延迟 ────────────────────

        static double MeasureManagedLatency() => Measure(() =>
        {
            for (int k = 0; k < LatencyIterations; k++)
            {
                var j = new ManagedTinyJob();
                ManagedJobScheduler.Schedule(ref j, LatencyLength, 0).Complete();
            }
        });

        static double MeasureZeroAllocLatency() => Measure(() =>
        {
            // S4=调度延迟：单任务 schedule→flush→complete 的往返（池化实例避免每帧 new/GC）。
            // 不批量并发——多个并行 for 并发会线程过订用，反而不利于测“调度延迟”。
            for (int k = 0; k < LatencyIterations; k++)
            {
                var handle = _zeroAllocScheduler!.Schedule(_zaTiny!, LatencyLength);
                _zeroAllocScheduler.Flush();
                handle.Complete();
            }
        });

        static double MeasureMisakiLatency() => Measure(() =>
        {
            for (int k = 0; k < LatencyIterations; k++)
            {
                var job = new MisakiTinyJob();
                var handle = _misakiScheduler!.ScheduleParallelFor(in job, LatencyLength, 64);
                _misakiScheduler.Wait(handle, inlineExecution: false);
            }
        });

        static double MeasurePtLatency() => Measure(() =>
        {
            for (int k = 0; k < LatencyIterations; k++)
            {
                for (int i = 0; i < LatencyWorks; i++) _ptp!.QueueWorkItem(() => { });
                _ptp!.Wait(helpWhileWaiting: true);
            }
        });

        static double MeasureTpLatency() => Measure(() =>
        {
            for (int k = 0; k < LatencyIterations; k++)
            {
                using var cd = new CountdownEvent(1);
                ThreadPool.QueueUserWorkItem(_ => cd.Signal());
                cd.Wait();
            }
        });

        static double MeasureParForLatency() => Measure(() =>
        {
            for (int k = 0; k < LatencyIterations; k++)
            {
                System.Threading.Tasks.Parallel.For(0, LatencyLength, i => { });
            }
        });

        // ──────────────────── S4 新框架 ────────────────────

        static double MeasureConcurrentBagLatency() => Measure(() =>
        {
            for (int k = 0; k < LatencyIterations; k++)
            {
                int slices = SliceCount;
                int bs = (LatencyLength + slices - 1) / slices;
                var bag = new ConcurrentBag<Action>();
                for (int i = 0; i < slices; i++)
                {
                    int s = i * bs, e = Math.Min(s + bs, LatencyLength);
                    bag.Add(() => { for (int j = s; j < e; j++) { } });
                }
                using var cd = new CountdownEvent(bag.Count);
                var threads = new Thread[bag.Count];
                int idx = 0;
                foreach (var action in bag)
                {
                    int i = idx++;
                    threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                    threads[i].Start();
                }
                cd.Wait();
            }
        });

        static double MeasureChannelLatency() => Measure(() =>
        {
            for (int k = 0; k < LatencyIterations; k++)
            {
                int slices = SliceCount;
                int bs = (LatencyLength + slices - 1) / slices;
                var channel = Channel.CreateBounded<Action>(new BoundedChannelOptions(slices) { SingleReader = false, SingleWriter = true });
                var writer = channel.Writer;
                var reader = channel.Reader;
                for (int i = 0; i < slices; i++)
                {
                    int s = i * bs, e = Math.Min(s + bs, LatencyLength);
                    writer.TryWrite(() => { for (int j = s; j < e; j++) { } });
                }
                writer.Complete();
                using var cd = new CountdownEvent(slices);
                var threads = new Thread[slices];
                int idx = 0;
                while (reader.TryRead(out var action))
                {
                    int i = idx++;
                    threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                    threads[i].Start();
                }
                cd.Wait();
            }
        });

        static double MeasureTaskLatency() => Measure(() =>
        {
            for (int k = 0; k < LatencyIterations; k++)
            {
                int slices = SliceCount;
                int bs = (LatencyLength + slices - 1) / slices;
                var tasks = new Task[slices];
                for (int i = 0; i < slices; i++)
                {
                    int s = i * bs, e = Math.Min(s + bs, LatencyLength);
                    tasks[i] = Task.Run(() => { for (int j = s; j < e; j++) { } });
                }
                Task.WaitAll(tasks);
            }
        });

        // ──────────────────── S5: 高竞争重计算 ────────────────────

        static double MeasureManagedHeavy() => Measure(() =>
        {
            var j = new ManagedHeavyJob { Results = _heavyResults };
            // innerBatchCount=0 → 由调度器自动计算认领粒度（对齐全 benchmark 默认）。
            // 此前硬编码 8192（13 片）在 S5 高竞争下片数过少，尾部失衡；改为 0 由
            // 系统自动切片（guided/等量，默认对齐 Native 路径的 S5 配置）。
            ManagedJobScheduler.Schedule(ref j, HighContentionCount, 0).Complete();
        });

        static double MeasureZeroAllocHeavy() => Measure(() =>
        {
            _zaHeavy!.Results = _heavyResults;
            var handle = _zeroAllocScheduler!.Schedule(_zaHeavy, HighContentionCount);
            _zeroAllocScheduler.Flush();
            handle.Complete();
        });

        static double MeasureMisakiHeavy() => Measure(() =>
        {
            var job = new MisakiHeavyJob { Results = _heavyResults };
            var handle = _misakiScheduler!.ScheduleParallelFor(in job, HighContentionCount, 64);
            _misakiScheduler.Wait(handle, inlineExecution: false);
        });

        static double MeasurePtForHeavy() => Measure(() =>
        {
            var r = _heavyResults;
            for (int i = 0; i < SliceCount; i++)
            {
                int s = i * HeavySliceSize, e = Math.Min(s + HeavySliceSize, HighContentionCount);
                _ptp!.QueueWorkItem(() => { for (int k = s; k < e; k++) { long sum = 0; for (int j = 0; j < 1000; j++) sum += (long)k * j; r[k] = sum; } });
            }
            _ptp!.Wait(helpWhileWaiting: true);
        });

        static double MeasureTpHeavy() => Measure(() =>
        {
            var r = _heavyResults;
            int slices = _workerCount;
            int bs = (HighContentionCount + slices - 1) / slices;
            using var cd = new CountdownEvent(slices);
            for (int b = 0; b < slices; b++)
            {
                int s = b * bs, e = Math.Min(s + bs, HighContentionCount);
                ThreadPool.QueueUserWorkItem(_ => { for (int i = s; i < e; i++) { long sum = 0; for (int j = 0; j < 1000; j++) sum += (long)i * j; r[i] = sum; } cd.Signal(); });
            }
            cd.Wait();
        });

        static double MeasureParForHeavy() => Measure(() =>
        {
            var r = _heavyResults;
            System.Threading.Tasks.Parallel.For(0, HighContentionCount, i => { long sum = 0; for (int j = 0; j < 1000; j++) sum += (long)i * j; r[i] = sum; });
        });

        // ──────────────────── S5 新框架 ────────────────────

        static double MeasureConcurrentBagHeavy() => Measure(() =>
        {
            var r = _heavyResults;
            int slices = SliceCount;
            int bs = HeavySliceSize;
            var bag = new ConcurrentBag<Action>();
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, HighContentionCount);
                bag.Add(() => { for (int idx = s; idx < e; idx++) { long sum = 0; for (int j = 0; j < 1000; j++) sum += (long)idx * j; r[idx] = sum; } });
            }
            using var cd = new CountdownEvent(bag.Count);
            var threads = new Thread[bag.Count];
            int idx2 = 0;
            foreach (var action in bag)
            {
                int i = idx2++;
                threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                threads[i].Start();
            }
            cd.Wait();
        });

        static double MeasureChannelHeavy() => Measure(() =>
        {
            var r = _heavyResults;
            int slices = SliceCount;
            int bs = HeavySliceSize;
            var channel = Channel.CreateBounded<Action>(new BoundedChannelOptions(slices) { SingleReader = false, SingleWriter = true });
            var writer = channel.Writer;
            var reader = channel.Reader;
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, HighContentionCount);
                writer.TryWrite(() => { for (int idx = s; idx < e; idx++) { long sum = 0; for (int j = 0; j < 1000; j++) sum += (long)idx * j; r[idx] = sum; } });
            }
            writer.Complete();
            using var cd = new CountdownEvent(slices);
            var threads = new Thread[slices];
            int idx2 = 0;
            while (reader.TryRead(out var action))
            {
                int i = idx2++;
                threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                threads[i].Start();
            }
            cd.Wait();
        });

        static double MeasureTaskHeavy() => Measure(() =>
        {
            var r = _heavyResults;
            int slices = SliceCount;
            int bs = HeavySliceSize;
            var tasks = new Task[slices];
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, HighContentionCount);
                tasks[i] = Task.Run(() => { for (int idx = s; idx < e; idx++) { long sum = 0; for (int j = 0; j < 1000; j++) sum += (long)idx * j; r[idx] = sum; } });
            }
            Task.WaitAll(tasks);
        });

        // ──────────────────── S6: 控制流 + 重运算（编译器无法代数简化） ────────────────────

        static double MeasureManagedCtrl() => Measure(() =>
        {
            var j = new ManagedCtrlJob { Results = _ctrlResults };
            ManagedJobScheduler.Schedule(ref j, HighContentionCount, 0).Complete();
        });

        static double MeasureZeroAllocCtrl() => Measure(() =>
        {
            _zaCtrl!.Results = _ctrlResults;
            var handle = _zeroAllocScheduler!.Schedule(_zaCtrl, HighContentionCount);
            _zeroAllocScheduler.Flush();
            handle.Complete();
        });

        static double MeasureMisakiCtrl() => Measure(() =>
        {
            var job = new MisakiCtrlJob { Results = _ctrlResults };
            var handle = _misakiScheduler!.ScheduleParallelFor(in job, HighContentionCount, 64);
            _misakiScheduler.Wait(handle, inlineExecution: false);
        });

        static double MeasurePtForCtrl() => Measure(() =>
        {
            var r = _ctrlResults;
            for (int i = 0; i < SliceCount; i++)
            {
                int s = i * HeavySliceSize, e = Math.Min(s + HeavySliceSize, HighContentionCount);
                _ptp!.QueueWorkItem(() => { for (int k = s; k < e; k++) r[k] = S6Compute.Run(k); });
            }
            _ptp!.Wait(helpWhileWaiting: true);
        });

        static double MeasureTpCtrl() => Measure(() =>
        {
            var r = _ctrlResults;
            int slices = _workerCount;
            int bs = (HighContentionCount + slices - 1) / slices;
            using var cd = new CountdownEvent(slices);
            for (int b = 0; b < slices; b++)
            {
                int s = b * bs, e = Math.Min(s + bs, HighContentionCount);
                ThreadPool.QueueUserWorkItem(_ => { for (int i = s; i < e; i++) r[i] = S6Compute.Run(i); cd.Signal(); });
            }
            cd.Wait();
        });

        static double MeasureParForCtrl() => Measure(() =>
        {
            var r = _ctrlResults;
            System.Threading.Tasks.Parallel.For(0, HighContentionCount, i => r[i] = S6Compute.Run(i));
        });

        // ──────────────────── S6 新框架 ────────────────────

        static double MeasureConcurrentBagCtrl() => Measure(() =>
        {
            var r = _ctrlResults;
            int slices = SliceCount;
            int bs = HeavySliceSize;
            var bag = new ConcurrentBag<Action>();
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, HighContentionCount);
                bag.Add(() => { for (int idx = s; idx < e; idx++) r[idx] = S6Compute.Run(idx); });
            }
            using var cd = new CountdownEvent(bag.Count);
            var threads = new Thread[bag.Count];
            int idx2 = 0;
            foreach (var action in bag)
            {
                int i = idx2++;
                threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                threads[i].Start();
            }
            cd.Wait();
        });

        static double MeasureChannelCtrl() => Measure(() =>
        {
            var r = _ctrlResults;
            int slices = SliceCount;
            int bs = HeavySliceSize;
            var channel = Channel.CreateBounded<Action>(new BoundedChannelOptions(slices) { SingleReader = false, SingleWriter = true });
            var writer = channel.Writer;
            var reader = channel.Reader;
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, HighContentionCount);
                writer.TryWrite(() => { for (int idx = s; idx < e; idx++) r[idx] = S6Compute.Run(idx); });
            }
            writer.Complete();
            using var cd = new CountdownEvent(slices);
            var threads = new Thread[slices];
            int idx2 = 0;
            while (reader.TryRead(out var action))
            {
                int i = idx2++;
                threads[i] = new Thread(() => { action(); cd.Signal(); }) { IsBackground = true };
                threads[i].Start();
            }
            cd.Wait();
        });

        static double MeasureTaskCtrl() => Measure(() =>
        {
            var r = _ctrlResults;
            int slices = SliceCount;
            int bs = HeavySliceSize;
            var tasks = new Task[slices];
            for (int i = 0; i < slices; i++)
            {
                int s = i * bs, e = Math.Min(s + bs, HighContentionCount);
                tasks[i] = Task.Run(() => { for (int idx = s; idx < e; idx++) r[idx] = S6Compute.Run(idx); });
            }
            Task.WaitAll(tasks);
        });

        // ──────────────────── 原生 NativeDll JobSystem（NativeJobScheduler，托管回调路径） ────────────────────
        // 复用同一批 IJobParallelFor struct（实现 EntJoy.JobSystem.IJobParallelFor），
        // 通过 JobExtensions.Schedule 调度到原生 C++ 线程池执行（JobHasManagedReferences→托管上下文回调 C# Execute）。

        static double MeasureNativeAdd() => Measure(() =>
        {
            var j = new ManagedAddJob { Values = _values };
            j.Schedule(ArrayLength, 0).Complete();
        });

        static double MeasureNativeEmpty() => Measure(() =>
        {
            var j = new ManagedEmptyJob();
            j.Schedule(ArrayLength, 0).Complete();
        });

        static double MeasureNativeChain() => Measure(() =>
        {
            var v = _values;
            var j1 = new ManagedChainJob1 { Values = v };
            var j2 = new ManagedChainJob2 { Values = v };
            var j3 = new ManagedChainJob3 { Values = v };
            var h1 = j1.Schedule(ArrayLength, 0);
            var h2 = j2.Schedule(ArrayLength, 0, h1);
            var h3 = j3.Schedule(ArrayLength, 0, h2);
            h3.Complete();
        });

        static double MeasureNativeLatency() => Measure(() =>
        {
            for (int k = 0; k < LatencyIterations; k++)
            {
                var j = new ManagedTinyJob();
                j.Schedule(LatencyLength, 0).Complete();
            }
        });

        static double MeasureNativeHeavy() => Measure(() =>
        {
            var j = new ManagedHeavyJob { Results = _heavyResults };
            j.Schedule(HighContentionCount, 0).Complete();
        });

        static double MeasureNativeCtrl() => Measure(() =>
        {
            var j = new ManagedCtrlJob { Results = _ctrlResults };
            j.Schedule(HighContentionCount, 0).Complete();
        });

        // ──────────────────── NativeTranspiler（C++ / ISPC 直跑）────────────────────
        // 这些 job 由 NativeTranspiler 源生成器翻译成 C++/ISPC，经绑定 .g.cs 生成的
        // Schedule 扩展方法调度到原生 Adapter（原生→原生，无托管回调）。

        static double MeasureNativeAddCpp() => Measure(() =>
        {
            var j = new NativeAddCpp { Values = _nativeValues };
            j.Schedule(ArrayLength, 65536).Complete();
        });

        static double MeasureNativeAddIspc() => Measure(() =>
        {
            var j = new NativeAddIspc { Values = _nativeValues };
            j.Schedule(ArrayLength, 65536).Complete();
        });

        static double MeasureNativeAddAutoSIMD() => Measure(() =>
        {
            var j = new NativeAddAutoSIMD { Values = _nativeValues };
            j.Schedule(ArrayLength, 65536).Complete();
        });

        static double MeasureNativeEmptyCpp() => Measure(() =>
        {
            var j = new NativeEmptyCpp();
            j.Schedule(ArrayLength, 0).Complete();
        });

        static double MeasureNativeEmptyIspc() => Measure(() =>
        {
            var j = new NativeEmptyIspc();
            j.Schedule(ArrayLength, 0).Complete();
        });

        static double MeasureNativeChainCpp() => Measure(() =>
        {
            var v = _nativeValues;
            var j1 = new NativeChainCpp1 { Values = v };
            var j2 = new NativeChainCpp2 { Values = v };
            var j3 = new NativeChainCpp3 { Values = v };
            var h1 = j1.Schedule(ArrayLength, 0);
            var h2 = j2.Schedule(ArrayLength, 0, h1);
            var h3 = j3.Schedule(ArrayLength, 0, h2);
            h3.Complete();
        });

        static double MeasureNativeChainIspc() => Measure(() =>
        {
            var v = _nativeValues;
            var j1 = new NativeChainIspc1 { Values = v };
            var j2 = new NativeChainIspc2 { Values = v };
            var j3 = new NativeChainIspc3 { Values = v };
            var h1 = j1.Schedule(ArrayLength, 0);
            var h2 = j2.Schedule(ArrayLength, 0, h1);
            var h3 = j3.Schedule(ArrayLength, 0, h2);
            h3.Complete();
        });

        static double MeasureNativeChainAutoSIMD() => Measure(() =>
        {
            var v = _nativeValues;
            var j1 = new NativeChainAutoSIMD1 { Values = v };
            var j2 = new NativeChainAutoSIMD2 { Values = v };
            var j3 = new NativeChainAutoSIMD3 { Values = v };
            var h1 = j1.Schedule(ArrayLength, 0);
            var h2 = j2.Schedule(ArrayLength, 0, h1);
            var h3 = j3.Schedule(ArrayLength, 0, h2);
            h3.Complete();
        });

        static double MeasureNativeEmptyLatencyCpp() => Measure(() =>
        {
            for (int k = 0; k < LatencyIterations; k++)
            {
                var j = new NativeEmptyCpp();
                j.Schedule(LatencyLength, 0).Complete();
            }
        });

        static double MeasureNativeEmptyLatencyIspc() => Measure(() =>
        {
            for (int k = 0; k < LatencyIterations; k++)
            {
                var j = new NativeEmptyIspc();
                j.Schedule(LatencyLength, 0).Complete();
            }
        });

        static double MeasureNativeHeavyCpp() => Measure(() =>
        {
            var j = new NativeHeavyCpp { Results = _nativeHeavyResults };
            j.Schedule(HighContentionCount, 0).Complete();
        });

        static double MeasureNativeHeavyIspc() => Measure(() =>
        {
            var j = new NativeHeavyIspc { Results = _nativeHeavyResults };
            j.Schedule(HighContentionCount, 0).Complete();
        });

        static double MeasureNativeCtrlCpp() => Measure(() =>
        {
            var j = new NativeCtrlCpp { Results = _nativeCtrlResults };
            j.Schedule(HighContentionCount, 0).Complete();
        });

        static double MeasureNativeCtrlIspc() => Measure(() =>
        {
            var j = new NativeCtrlIspc { Results = _nativeCtrlResults };
            j.Schedule(HighContentionCount, 0).Complete();
        });

        // ──────────────────── AutoSIMD（C++ SIMD 向量化）────────────────────
        static double MeasureNativeHeavyAutoSIMD() => Measure(() =>
        {
            var j = new NativeHeavyAutoSIMD { Results = _nativeAutoSIMDResults };
            j.Schedule(HighContentionCount, 0).Complete();
        });

                static double MeasureNativeCtrlAutoSIMD() => Measure(() =>
        {
            var j = new NativeCtrlAutoSIMD { Results = _nativeAutoSIMDResults };
            j.Schedule(HighContentionCount, 0).Complete();
        });

        // AutoSIMD 结果验证：对比每个场景的 AutoSIMD 实现与对应的 Cpp 标量实现
        private static void VerifyAutoSIMD()
        {
            Console.WriteLine("\n=== AutoSIMD Results Verification ===");

            // 通用对比：给定输入，分别跑 Cpp 与 AutoSIMD，逐元素必须一致
            bool allPass = true;

            // ── S1: 分片加法 Values[i] = Values[i] + 1 ──
            {
                int n = Math.Min(10000, ArrayLength);
                var inputCpp = new NativeArray<int>(n, Allocator.TempJob);
                var inputAuto = new NativeArray<int>(n, Allocator.TempJob);
                for (int i = 0; i < n; i++) { int v = (i * 31) % 100000 - 50000; inputCpp[i] = v; inputAuto[i] = v; }

                new NativeAddCpp { Values = inputCpp }.Schedule(n, 0).Complete();
                new NativeAddAutoSIMD { Values = inputAuto }.Schedule(n, 0).Complete();
                int errs = 0, first = -1;
                for (int i = 0; i < n; i++) if (inputCpp[i] != inputAuto[i]) { errs++; if (first < 0) first = i; }
                Console.WriteLine($"S1 Add        : {(errs == 0 ? "✅ PASS" : $"❌ FAIL ({errs} errors, first@{first}: cpp={inputCpp[first]} auto={inputAuto[first]})")}");
                allPass &= errs == 0;
                // 校验 Cpp 本身正确（标量参考）
                for (int i = 0; i < n; i++)
                {
                    int v = (i * 31) % 100000 - 50000;
                    if (inputCpp[i] != v + 1) { Console.WriteLine($"  ⚠ S1 Cpp oracle broken @{i}"); allPass = false; break; }
                }
                inputCpp.Dispose(); inputAuto.Dispose();
            }

            // ── S3: 依赖链 +1 → ×2 → -3 ──
            {
                int n = Math.Min(10000, ArrayLength);
                var cv = new NativeArray<int>(n, Allocator.TempJob);
                var av = new NativeArray<int>(n, Allocator.TempJob);
                for (int i = 0; i < n; i++) { int v = (i * 17) % 200000 - 100000; cv[i] = v; av[i] = v; }

                var h1 = new NativeChainCpp1 { Values = cv }.Schedule(n, 0);
                var h2 = new NativeChainCpp2 { Values = cv }.Schedule(n, 0, h1);
                new NativeChainCpp3 { Values = cv }.Schedule(n, 0, h2).Complete();

                var a1 = new NativeChainAutoSIMD1 { Values = av }.Schedule(n, 0);
                var a2 = new NativeChainAutoSIMD2 { Values = av }.Schedule(n, 0, a1);
                new NativeChainAutoSIMD3 { Values = av }.Schedule(n, 0, a2).Complete();

                int errs = 0, first = -1;
                for (int i = 0; i < n; i++) if (cv[i] != av[i]) { errs++; if (first < 0) first = i; }
                Console.WriteLine($"S3 Chain      : {(errs == 0 ? "✅ PASS" : $"❌ FAIL ({errs} errors, first@{first}: cpp={cv[first]} auto={av[first]})")}");
                allPass &= errs == 0;
                cv.Dispose(); av.Dispose();
            }

            // ── S5: 高竞争 sum += index * j (1000 次) ──
            {
                int n = Math.Min(1000, HighContentionCount);
                var cr = new NativeArray<int>(n, Allocator.TempJob);
                var ar = new NativeArray<int>(n, Allocator.TempJob);
                new NativeHeavyCpp { Results = cr }.Schedule(n, 0).Complete();
                new NativeHeavyAutoSIMD { Results = ar }.Schedule(n, 0).Complete();
                int errs = 0, first = -1;
                for (int i = 0; i < n; i++) if (cr[i] != ar[i]) { errs++; if (first < 0) first = i; }
                Console.WriteLine($"S5 Heavy      : {(errs == 0 ? "✅ PASS" : $"❌ FAIL ({errs} errors, first@{first}: cpp={cr[first]} auto={ar[first]})")}");
                allPass &= errs == 0;
                cr.Dispose(); ar.Dispose();
            }

            // ── S6: 控制流 LCG + 分支 ──
            {
                int n = Math.Min(1000, HighContentionCount);
                var cr = new NativeArray<int>(n, Allocator.TempJob);
                var ar = new NativeArray<int>(n, Allocator.TempJob);
                var ir = new NativeArray<int>(n, Allocator.TempJob);
                new NativeCtrlCpp { Results = cr }.Schedule(n, 0).Complete();
                new NativeCtrlAutoSIMD { Results = ar }.Schedule(n, 0).Complete();
                new NativeCtrlIspc { Results = ir }.Schedule(n, 0).Complete();

                // 托管标量参考
                int errsAuto = 0, errsCpp = 0, errsIspc = 0, firstAuto = -1;
                for (int i = 0; i < n; i++)
                {
                    int sum = 0; uint x = (uint)(i * 2654435761u) + 1u;
                    for (int j = 0; j < 1000; j++)
                    {
                        x = x * 1664525u + 1013904223u; uint r = x % 13u;
                        if (r < 4u) sum += (int)x; else if (r < 8u) sum ^= (int)x; else sum -= (int)(x >> 3);
                        if ((x & 7u) == 0u) sum += j;
                    }
                    if (sum != ar[i]) { errsAuto++; if (firstAuto < 0) firstAuto = i; }
                    if (sum != cr[i]) errsCpp++;
                    if (sum != ir[i]) errsIspc++;
                }
                Console.WriteLine($"S6 Ctrl       : AutoSIMD {(errsAuto == 0 ? "✅ PASS" : $"❌ FAIL ({errsAuto} errors, first@{firstAuto})")} | Cpp {(errsCpp == 0 ? "✅" : "❌")} | ISPC {(errsIspc == 0 ? "✅" : "❌")}");
                allPass &= errsAuto == 0 && errsCpp == 0 && errsIspc == 0;
                cr.Dispose(); ar.Dispose(); ir.Dispose();
            }

            Console.WriteLine(allPass
                ? "\n✅ ALL AutoSIMD scenarios produce CORRECT results (match Cpp/scalar)."
                : "\n❌ AutoSIMD has FAILING scenarios — see above.");
        }
    }
}