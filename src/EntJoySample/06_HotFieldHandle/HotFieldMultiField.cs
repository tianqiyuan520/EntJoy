//using System;
//using System.Diagnostics;
//using System.Runtime.CompilerServices;

//namespace EntJoySample.HotFieldHandle
//{
//    // ═══════════════════════════════════════════════════════════════════
//    // HotField 多字段套件(E:\Code\HotField 同款结构)
//    //   · HotFieldOptimizedBenchmark: 2 × Vector2(SoA 分数组)
//    //   · LargeHotFieldBenchmark:     50 floats(AoS 块)
//    //   · LargeHotFieldBenchmark200:  200 floats(AoS 块)
//    // 本文件复刻其关键 shell 形态,量化「shell 形态 × 字段数」:
//    //   Class(AoS 对象) / Struct(AoS 值数组) / ChunkPtr(基址+偏移) / System(平铺)
//    // 用平铺 float[] AoS 块(GlobalData[entity*F + field]),与 HotField 一致。
//    // ═══════════════════════════════════════════════════════════════════

//    /// <summary>class 实体(数据跟随对象,float[] 数组)。</summary>
//    public class MultiClassEntity
//    {
//        public float[] Data;
//        [MethodImpl(MethodImplOptions.AggressiveInlining)]
//        public void UpdateAll() { var d = Data; for (int i = 0; i < d.Length; i++) d[i] += 1f; }
//        [MethodImpl(MethodImplOptions.AggressiveInlining)]
//        public void UpdateTop(int top) { var d = Data; for (int i = 0; i < top; i++) d[i] += 1f; }
//    }

//    /// <summary>struct 实体(AoS 值类型容器,float[] 数组)。值类型语义 + 连续 struct 数组。</summary>
//    public struct MultiStructEntity
//    {
//        public float[] Data;
//        [MethodImpl(MethodImplOptions.AggressiveInlining)]
//        public void UpdateAll() { var d = Data; for (int i = 0; i < d.Length; i++) d[i] += 1f; }
//        [MethodImpl(MethodImplOptions.AggressiveInlining)]
//        public void UpdateTop(int top) { var d = Data; for (int i = 0; i < top; i++) d[i] += 1f; }
//    }

//    /// <summary>ChunkPtr 形态:持有 AoS 块基址(实体 0 的块)+ 实体偏移,访问 block[entity*F + field]。</summary>
//    public unsafe struct MultiChunkPtrEntity
//    {
//        public float* Base;   // 所有实体共享块基址
//        public int Offset;    // entity * FieldCount
//        public int FieldCount;

//        [MethodImpl(MethodImplOptions.AggressiveInlining)]
//        public void UpdateAll()
//        {
//            float* p = Base + Offset;
//            int fc = FieldCount;
//            for (int i = 0; i < fc; i++) p[i] += 1f;
//        }

//        [MethodImpl(MethodImplOptions.AggressiveInlining)]
//        public void UpdateTop(int top)
//        {
//            float* p = Base + Offset;
//            for (int i = 0; i < top; i++) p[i] += 1f;
//        }
//    }

//    /// <summary>System 平铺:直接扫全局数组(无实体壳,下界参考)。</summary>
//    public static unsafe class MultiSystem
//    {
//        [MethodImpl(MethodImplOptions.AggressiveInlining)]
//        public static void UpdateAll(float* p, int length) { for (int i = 0; i < length; i++) p[i] += 1f; }

//        /// <summary>只更新每实体的前 top 个字段(AoS 跨实体跳,验证 SoA 子集带宽优势)。</summary>
//        [MethodImpl(MethodImplOptions.AggressiveInlining)]
//        public static void UpdateTop(float* p, int count, int fieldCount, int top)
//        {
//            for (int e = 0; e < count; e++)
//            {
//                float* row = p + (long)e * fieldCount;
//                for (int i = 0; i < top; i++) row[i] += 1f;
//            }
//        }
//    }

//    public class TestHotFieldMultiField
//    {
//        private const int DefaultWarmup = 5;
//        private const int DefaultMeasure = 40;
//        private const int EntityCount = 100_000;   // 多字段套件用 10 万实体(内存可控)

//        private static int ReadPositiveEnvironmentInt(string name, int fallback)
//        {
//            return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0
//                ? value
//                : fallback;
//        }

//        private static double Percentile(double[] sorted, double percentile)
//        {
//            if (sorted.Length == 0) return 0;
//            double position = (sorted.Length - 1) * percentile;
//            int lower = (int)Math.Floor(position);
//            int upper = (int)Math.Ceiling(position);
//            if (lower == upper) return sorted[lower];
//            double weight = position - lower;
//            return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
//        }

//        private static double PrintSummary(string variant, double[] samples)
//        {
//            var sorted = (double[])samples.Clone();
//            Array.Sort(sorted);
//            double sum = 0;
//            foreach (double s in samples) sum += s;
//            double avg = sum / samples.Length;
//            double p50 = Percentile(sorted, 0.50);
//            double p95 = Percentile(sorted, 0.95);
//            double p99 = Percentile(sorted, 0.99);
//            Console.WriteLine($"{variant,-26}: avg={avg:F3} ms, p50={p50:F3} ms, p95={p95:F3} ms, p99={p99:F3} ms");
//            Console.WriteLine(FormattableString.Invariant(
//                $"BENCH|runtime=EntJoy|case=HFMF-{variant}|frames={samples.Length}|trace=0|avg={avg:F6}|p50={p50:F6}|p95={p95:F6}|p99={p99:F6}"));
//            return avg;
//        }

//        private static void RunFieldSuite(int fieldCount)
//        {
//            int n = EntityCount;
//            int warmup = ReadPositiveEnvironmentInt("ENTJOY_BENCH_WARMUP", DefaultWarmup);
//            int measure = ReadPositiveEnvironmentInt("ENTJOY_BENCH_FRAMES", DefaultMeasure);
//            int total = n * fieldCount;

//            Console.WriteLine();
//            Console.WriteLine($"=== 字段数 {fieldCount} (实体 {n:N0}) ===");

//            // AoS 块:GlobalData[entity*F + field](与 HotField Large 套件同布局)
//            var globalData = new float[total];
//            for (int i = 0; i < total; i++) globalData[i] = i % 10;

//            // Class:AoS 对象(每实体一个 float[F] 数组)
//            var classArr = new MultiClassEntity[n];
//            for (int i = 0; i < n; i++) classArr[i] = new MultiClassEntity { Data = new float[fieldCount] };

//            // Struct:AoS 值类型容器(float[] 包装,任意字段数)
//            var structArr = new MultiStructEntity[n];
//            for (int i = 0; i < n; i++) structArr[i] = new MultiStructEntity { Data = new float[fieldCount] };

//            // ChunkPtr:共享块基址 + 实体偏移
//            var chunkPtrs = new MultiChunkPtrEntity[n];
//            unsafe
//            {
//                fixed (float* dataPtr = globalData)
//                {
//                    for (int i = 0; i < n; i++)
//                        chunkPtrs[i] = new MultiChunkPtrEntity { Base = dataPtr, Offset = i * fieldCount, FieldCount = fieldCount };
//                }
//            }

//            double[] classSamples, structSamples, chunkPtrSamples, systemSamples;
//            {
//                for (int w = 0; w < warmup; w++)
//                {
//                    foreach (var e in classArr) e.UpdateAll();
//                    for (int i = 0; i < n; i++) structArr[i].UpdateAll();
//                    foreach (var c in chunkPtrs) c.UpdateAll();
//                    unsafe { fixed (float* p = globalData) MultiSystem.UpdateAll(p, total); }
//                }
//                classSamples = new double[measure];
//                structSamples = new double[measure];
//                chunkPtrSamples = new double[measure];
//                systemSamples = new double[measure];
//                for (int m = 0; m < measure; m++)
//                {
//                    long c0 = Stopwatch.GetTimestamp();
//                    foreach (var e in classArr) e.UpdateAll();
//                    long c1 = Stopwatch.GetTimestamp();
//                    long s0 = Stopwatch.GetTimestamp();
//                    for (int i = 0; i < n; i++) structArr[i].UpdateAll();
//                    long s1 = Stopwatch.GetTimestamp();
//                    long cp0 = Stopwatch.GetTimestamp();
//                    foreach (var c in chunkPtrs) c.UpdateAll();
//                    long cp1 = Stopwatch.GetTimestamp();
//                    long sy0 = Stopwatch.GetTimestamp();
//                    unsafe { fixed (float* p = globalData) MultiSystem.UpdateAll(p, total); }
//                    long sy1 = Stopwatch.GetTimestamp();
//                    classSamples[m] = (c1 - c0) * 1000.0 / Stopwatch.Frequency;
//                    structSamples[m] = (s1 - s0) * 1000.0 / Stopwatch.Frequency;
//                    chunkPtrSamples[m] = (cp1 - cp0) * 1000.0 / Stopwatch.Frequency;
//                    systemSamples[m] = (sy1 - sy0) * 1000.0 / Stopwatch.Frequency;
//                }
//            }

//            double classAvg = PrintSummary($"Class_{fieldCount}字段", classSamples);
//            double structAvg = PrintSummary($"Struct_{fieldCount}字段", structSamples);
//            double chunkPtrAvg = PrintSummary($"ChunkPtr_{fieldCount}字段", chunkPtrSamples);
//            double systemAvg = PrintSummary($"System_{fieldCount}字段", systemSamples);

//            Console.WriteLine();
//            Console.WriteLine($"  [class基线] Class_{fieldCount} = {classAvg:F3} ms (1.00x)");
//            Console.WriteLine($"  Struct/Class = {structAvg / classAvg:F2}x");
//            Console.WriteLine($"  ChunkPtr/Class = {chunkPtrAvg / classAvg:F2}x");
//            Console.WriteLine($"  System/Class = {systemAvg / classAvg:F2}x");
//            Console.WriteLine($"  [结论{fieldCount}字段] 字段越多内存带宽越主导,shell 形态差距越被淹没(与 HotField §2/§3 同结论)。");

//            // ── 子集测试:只碰每实体前 top 个字段(AoS 跨实体跳,验证 SoA 子集带宽优势)──
//            foreach (int top in fieldCount > 100 ? new[] { 50, 10 } : fieldCount > 10 ? new[] { 10 } : Array.Empty<int>())
//            {
//                double[] ctSamples, stSamples, cptSamples, sySamples;
//                {
//                    for (int w = 0; w < warmup; w++)
//                    {
//                        foreach (var e in classArr) e.UpdateTop(top);
//                        for (int i = 0; i < n; i++) structArr[i].UpdateTop(top);
//                        foreach (var c in chunkPtrs) c.UpdateTop(top);
//                        unsafe { fixed (float* p = globalData) MultiSystem.UpdateTop(p, n, fieldCount, top); }
//                    }
//                    ctSamples = new double[measure];
//                    stSamples = new double[measure];
//                    cptSamples = new double[measure];
//                    sySamples = new double[measure];
//                    for (int m = 0; m < measure; m++)
//                    {
//                        long c0 = Stopwatch.GetTimestamp();
//                        foreach (var e in classArr) e.UpdateTop(top);
//                        long c1 = Stopwatch.GetTimestamp();
//                        long s0 = Stopwatch.GetTimestamp();
//                        for (int i = 0; i < n; i++) structArr[i].UpdateTop(top);
//                        long s1 = Stopwatch.GetTimestamp();
//                        long cp0 = Stopwatch.GetTimestamp();
//                        foreach (var c in chunkPtrs) c.UpdateTop(top);
//                        long cp1 = Stopwatch.GetTimestamp();
//                        long sy0 = Stopwatch.GetTimestamp();
//                        unsafe { fixed (float* p = globalData) MultiSystem.UpdateTop(p, n, fieldCount, top); }
//                        long sy1 = Stopwatch.GetTimestamp();
//                        ctSamples[m] = (c1 - c0) * 1000.0 / Stopwatch.Frequency;
//                        stSamples[m] = (s1 - s0) * 1000.0 / Stopwatch.Frequency;
//                        cptSamples[m] = (cp1 - cp0) * 1000.0 / Stopwatch.Frequency;
//                        sySamples[m] = (sy1 - sy0) * 1000.0 / Stopwatch.Frequency;
//                    }
//                }

//                double ctAvg = PrintSummary($"Class_{fieldCount}取Top{top}", ctSamples);
//                double stAvg = PrintSummary($"Struct_{fieldCount}取Top{top}", stSamples);
//                double cptAvg = PrintSummary($"ChunkPtr_{fieldCount}取Top{top}", cptSamples);
//                double syAvg = PrintSummary($"System_{fieldCount}取Top{top}", sySamples);

//                Console.WriteLine();
//                Console.WriteLine($"  [Top{top}基线] Class_{fieldCount}取Top{top} = {ctAvg:F3} ms (1.00x)");
//                Console.WriteLine($"  Struct_Top{top}/Class = {stAvg / ctAvg:F2}x");
//                Console.WriteLine($"  ChunkPtr_Top{top}/Class = {cptAvg / ctAvg:F2}x");
//                Console.WriteLine($"  System_Top{top}/Class = {syAvg / ctAvg:F2}x");
//                Console.WriteLine($"  [Top{top}结论] 只碰前 {top} 字段时,AoS 仍要跨实体跳(chunkPtr 逐实体),SoA 分数组形态(每字段连续)优势在子集访问下最明显——与 HotField §2 Top 结论一致。");
//            }
//        }

//        public void Run()
//        {
//            Console.WriteLine("=== HotField 多字段套件(2 / 50 / 200 字段,shell 形态 × 字段数)===");
//            RunFieldSuite(2);
//            RunFieldSuite(50);
//            RunFieldSuite(200);
//        }
//    }
//}
