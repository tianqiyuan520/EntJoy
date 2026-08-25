using EntJoy.ECS.JobSystem;
//using EntJoy.JobSystem;

//using System.Diagnostics;

//namespace EntJoySample.PreciseWakeBench
//{
//    // 高竞争 Job（对齐 JobLibsBenchmark S5：10万 × 1000 迭代 FMA）
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

//    public static class Program
//    {
//        private const int HighContentionCount = 100_000;   // S5
//        private const int ChainLength = 1_000_000;          // S3
//        private const int WarmupFrames = 5;
//        private const int MeasureFrames = 9;

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

//        private static void Print(string label, double[] raw)
//        {
//            var (avg, p50, p95, p99, max) = Stats(raw);
//            Console.WriteLine($"{label,-28}: avg={avg:F3} p50={p50:F3} p95={p95:F3} p99={p99:F3} max={max:F3} ms");
//        }

//        public static void Main()
//        {
//            NativeJobScheduler.Initialize();
//            NativeJobScheduler.PrewakeWorkersOnce();

//            // 读取环境变量设置 assist / guided（可选，也可直接用 API）
//            string? assistEnv = Environment.GetEnvironmentVariable("ENTJOY_ASSIST");
//            if (int.TryParse(assistEnv, out int aOn))
//                NativeJobScheduler.SetMainThreadAssistEnabled(aOn > 0);
//            string? guidedEnv = Environment.GetEnvironmentVariable("ENTJOY_GUIDED_TILES");
//            if (int.TryParse(guidedEnv, out int gOn))
//                NativeJobScheduler.SetGuidedEnabled(gOn > 0);

//            Console.WriteLine($"WorkerCount={NativeJobScheduler.JobWorkerCount} assist={assistEnv ?? "?"} guided={guidedEnv ?? "?"}");
//            Console.WriteLine("=== 变化的 JobSystem wake 方式 ===");

//            var contention = new ContentionJob { Results = new long[HighContentionCount] };
//            var c1 = new ChainJob1 { Values = new long[ChainLength] };
//            var c2 = new ChainJob2 { Values = c1.Values };
//            var c3 = new ChainJob3 { Values = c1.Values };

//            // ---- 高竞争（S5）----
//            var contentionRaw = Measure(() =>
//            {
//                contention.Schedule(HighContentionCount, 0).Complete();
//            });
//            Print("高竞争(10万x1000)", contentionRaw);

//            // ---- 依赖链（S3）----
//            var chainRaw = Measure(() =>
//            {
//                var h1 = c1.Schedule(ChainLength, 0);
//                var h2 = c2.Schedule(ChainLength, 0, h1);
//                var h3 = c3.Schedule(ChainLength, 0, h2);
//                h3.Complete();
//            });
//            Print("依赖链(3级x100万)", chainRaw);

//            // ---- 空任务（S2）：唤醒开销敏感 ----
//            var emptyRaw = Measure(() =>
//            {
//                var j = default(EmptyJob);
//                j.Schedule(1_000_000, 0).Complete();
//            });
//            Print("空任务(100万)", emptyRaw);

//            // ---- 小批量（每帧多个 1K 任务）：精确唤醒主战场 ----
//            var smallRaw = Measure(() =>
//            {
//                for (int k = 0; k < 100; k++)
//                {
//                    var j = default(SmallJob);
//                    j.Schedule(1024, 0).Complete();
//                }
//            });
//            Print("小批量(100x1024)", smallRaw);

//            Console.WriteLine("\n完成。");
//        }

//        public struct EmptyJob : IJobParallelFor
//        {
//            public void Execute(int i) { }
//        }

//        public struct SmallJob : IJobParallelFor
//        {
//            public void Execute(int i) { }
//        }
//    }
//}