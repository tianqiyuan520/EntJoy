//using System.Diagnostics;
//using EntJoy.JobSystem;
//using EntJoy.ECS.JobSystem;

//namespace EntJoySample.PreciseWakeBench
//{
//    // 高竞争 Job（10 万 × 1000 迭代 FMA）
//    public struct ContentionJob : IJobParallelFor
//    {
//        public long[] Results;
//        public void Execute(int i)
//        {
//            long sum = 0;
//            for (int j = 0; j < 1000; j++) sum += (long)i * j;
//            Results[i] = sum;
//        }
//    }

//    // 依赖链 Job 1（+1）
//    public struct ChainJob1 : IJobParallelFor
//    {
//        public long[] Values;
//        public void Execute(int i) => Values[i] = Values[i] + 1;
//    }

//    // 依赖链 Job 2（×2）
//    public struct ChainJob2 : IJobParallelFor
//    {
//        public long[] Values;
//        public void Execute(int i) => Values[i] = Values[i] * 2;
//    }

//    // 依赖链 Job 3（-3）
//    public struct ChainJob3 : IJobParallelFor
//    {
//        public long[] Values;
//        public void Execute(int i) => Values[i] = Values[i] - 3;
//    }

//    // 调度侧恒等实现：只测调度与唤醒，不含元素级工作。
//    public struct EmptyJob : IJobParallelFor
//    {
//        public void Execute(int i) { }
//    }

//    public static class Program
//    {
//        private const int HighContentionCount = 100_000;
//        private const int ChainLength = 1_000_000;
//        // 帧数由环境变量给出：p99 要落在同一进程内，跨进程比较受 tier-up 影响。
//        private static readonly int WarmupFrames =
//            int.TryParse(Environment.GetEnvironmentVariable("ENTJOY_BENCH_WARMUP"), out int wf) ? wf : 20;
//        private static readonly int MeasureFrames =
//            int.TryParse(Environment.GetEnvironmentVariable("ENTJOY_BENCH_FRAMES"), out int mf) ? mf : 40;

//        private static double Percentile(double[] sorted, double p)
//        {
//            if (sorted.Length == 0) return 0;
//            double pos = (sorted.Length - 1) * p;
//            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
//            if (lo == hi) return sorted[lo];
//            double w = pos - lo;
//            return sorted[lo] * (1 - w) + sorted[hi] * w;
//        }

//        private static (double avg, double p50, double p95, double p99, double max) Stats(double[] raw)
//        {
//            var s = (double[])raw.Clone();
//            Array.Sort(s);
//            var sum = 0.0;
//            foreach (var v in s) sum += v;
//            return (sum / s.Length, Percentile(s, 0.50), Percentile(s, 0.95), Percentile(s, 0.99), s[^1]);
//        }

//        private static double[] Measure(Action frame)
//        {
//            for (int f = 0; f < WarmupFrames; f++) frame();
//            var r = new double[MeasureFrames];
//            for (int f = 0; f < MeasureFrames; f++)
//            {
//                long t0 = Stopwatch.GetTimestamp();
//                frame();
//                long t1 = Stopwatch.GetTimestamp();
//                r[f] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
//            }
//            return r;
//        }

//        // 每帧固定工作量 = 一次完整调度 + 等待，用于对齐 JobLibs 的口径。
//        private static void Print(string label, double[] raw, ulong assistBefore, ulong tilesBefore)
//        {
//            var (avg, p50, p95, p99, max) = Stats(raw);
//            var st = NativeJobScheduler.GetStats();
//            Console.WriteLine($"{label,-24}: avg={avg:F3} p50={p50:F3} p95={p95:F3} p99={p99:F3} max={max:F3} ms" +
//                $" | assistExec+={st.AssistExecuted - assistBefore} assistTiles+={st.AssistTiles - tilesBefore}");
//        }

//        public static void Main()
//        {
//            JobScheduler.Initialize();
//            NativeJobScheduler.PrewakeWorkersOnce();

//            int assist = int.TryParse(Environment.GetEnvironmentVariable("ENTJOY_ASSIST"), out int a) ? a : 0;
//            NativeJobScheduler.SetMainThreadAssistEnabled(assist > 0);
//            if (int.TryParse(Environment.GetEnvironmentVariable("ENTJOY_GUIDED_TILES"), out int g))
//                NativeJobScheduler.SetGuidedEnabled(g > 0);

//            Console.WriteLine($"workers={NativeJobScheduler.JobWorkerCount} assist={assist} " +
//                $"guided={Environment.GetEnvironmentVariable("ENTJOY_GUIDED_TILES") ?? "0"}");

//            var contention = new ContentionJob { Results = new long[HighContentionCount] };
//            var c1 = new ChainJob1 { Values = new long[ChainLength] };
//            var c2 = new ChainJob2 { Values = c1.Values };
//            var c3 = new ChainJob3 { Values = c1.Values };

//            var raw = Measure(() => contention.Schedule(HighContentionCount, 0).Complete());
//            var st0 = NativeJobScheduler.GetStats();
//            Print("高竞争(10万x1000)", raw, st0.AssistExecuted, st0.AssistTiles);

//            ulong ae = NativeJobScheduler.GetStats().AssistExecuted, at = NativeJobScheduler.GetStats().AssistTiles;
//            raw = Measure(() =>
//            {
//                var h1 = c1.Schedule(ChainLength, 0);
//                var h2 = c2.Schedule(ChainLength, 0, h1);
//                var h3 = c3.Schedule(ChainLength, 0, h2);
//                h3.Complete();
//            });
//            Print("依赖链(3级x100万)", raw, ae, at);

//            ae = NativeJobScheduler.GetStats().AssistExecuted; at = NativeJobScheduler.GetStats().AssistTiles;
//            raw = Measure(() =>
//            {
//                var j = default(EmptyJob);
//                j.Schedule(1_000_000, 0).Complete();
//            });
//            Print("空任务(100万)", raw, ae, at);

//            ae = NativeJobScheduler.GetStats().AssistExecuted; at = NativeJobScheduler.GetStats().AssistTiles;
//            raw = Measure(() =>
//            {
//                for (int k = 0; k < 100; k++)
//                {
//                    var j = default(EmptyJob);
//                    j.Schedule(1024, 0).Complete();
//                }
//            });
//            Print("小批量(100x1024)", raw, ae, at);

//            var fin = NativeJobScheduler.GetStats();
//            Console.WriteLine($"total assistExecuted={fin.AssistExecuted} assistTiles={fin.AssistTiles} " +
//                $"slowAssistTiles={fin.SlowAssistTiles} assistPct={fin.AssistExecPctEwma:F4}");
//            NativeJobScheduler.Shutdown();
//        }
//    }
//}
