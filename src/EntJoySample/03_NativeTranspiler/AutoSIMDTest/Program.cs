//// Auto-SIMD 基准测试 — 跨后端性能对比
//// 运行方式: 编译后自动由 NativeCompileTask 调用原生运行时

//using EntJoy;
//using EntJoy.Collections;
//using EntJoySample.AutoSIMDTest;
//using System.Diagnostics;

//namespace EntJoySample.AutoSIMDTest
//{
//    public static class Program
//    {
//        public static void Main()
//        {
//            NativeJobScheduler.Initialize();
//            Console.WriteLine("=".PadRight(68, '='));
//            Console.WriteLine("  Auto-SIMD Benchmark Suite");
//            Console.WriteLine("=".PadRight(68, '='));
//            Console.WriteLine();

//            int N = 100000;
//            int Warmup = 3;
//            int Iterations = 100;
//            var rnd = new Random(42);

//            // ── 公共数据生成 ──
//            Console.Write("Generating data... ");
//            var aData = GenFloats(N, rnd);
//            var bData = GenFloats(N, rnd);
//            var cData = GenFloats(N, rnd);
//            var qxData = GenFloats(N, rnd);
//            var qyData = GenFloats(N, rnd);
//            var dxData = GenFloats(N * 2, rnd);
//            var dyData = GenFloats(N * 2, rnd);
//            var idxData = new int[N * 50];
//            for (int i = 0; i < N * 50; i++)
//                idxData[i] = rnd.Next(N * 2);
//            Console.WriteLine("OK");

//            // ── NativeArray ──
//            using var nativeA = new NativeArray<float>(aData, Allocator.Persistent);
//            using var nativeB = new NativeArray<float>(bData, Allocator.Persistent);
//            using var nativeC = new NativeArray<float>(cData, Allocator.Persistent);
//            using var nativeQx = new NativeArray<float>(qxData, Allocator.Persistent);
//            using var nativeQy = new NativeArray<float>(qyData, Allocator.Persistent);
//            using var nativeDx = new NativeArray<float>(dxData, Allocator.Persistent);
//            using var nativeDy = new NativeArray<float>(dyData, Allocator.Persistent);
//            using var nativeIdx = new NativeArray<int>(idxData, Allocator.Persistent);

//            // ── Case 1: 纯算术 ──
//            RunCase("1_SimpleArith", () => {
//                using var r1 = NewResult(N);
//                var job = new SimpleArith_CSharp { A = nativeA, B = nativeB, C = nativeC, Result = r1 };
//                return MeasurePF(ref job, N, Warmup, Iterations);
//            }, () => {
//                using var r1 = NewResult(N);
//                var job = new SimpleArith_SIMD { A = nativeA, B = nativeB, C = nativeC, Result = r1 };
//                return MeasureFor(ref job, N, Warmup, Iterations);
//            }, () => {
//                using var r1 = NewResult(N);
//                var job = new SimpleArith_SIMD_IJob { A = nativeA, B = nativeB, C = nativeC, Result = r1, Count = N };
//                return MeasureIJob(ref job, Warmup, Iterations);
//            });

//            // ── Case 2: 数学函数 ──
//            RunCase("2_MathFunctions", () => {
//                using var r1 = NewResult(N);
//                var job = new MathFuncs_CSharp { A = nativeA, Result = r1 };
//                return MeasurePF(ref job, N, Warmup, Iterations);
//            }, () => {
//                using var r1 = NewResult(N);
//                var job = new MathFuncs_SIMD { A = nativeA, Result = r1 };
//                return MeasureFor(ref job, N, Warmup, Iterations);
//            }, () => {
//                using var r1 = NewResult(N);
//                var job = new MathFuncs_SIMD_IJob { A = nativeA, Result = r1, Count = N };
//                return MeasureIJob(ref job, Warmup, Iterations);
//            });

//            // ── Case 3: 简单归约 ──
//            RunCase("3_SimpleReduce", () => {
//                using var r1 = NewResult(N);
//                var job = new SimpleReduce_CSharp { A = nativeA, Result = r1 };
//                return MeasurePF(ref job, N, Warmup, 10);
//            }, () => {
//                using var r1 = NewResult(N);
//                var job = new SimpleReduce_SIMD { A = nativeA, Result = r1 };
//                return MeasureFor(ref job, N, Warmup, 10);
//            }, () => {
//                using var r1 = NewResult(N);
//                var job = new SimpleReduce_SIMD_IJob { A = nativeA, Result = r1, Count = N };
//                return MeasureIJob(ref job, Warmup, 10);
//            });

//            // ── Case 4: 复杂控制流 ──
//            RunCase("4_ComplexFlow", () => {
//                using var r1 = NewResult(N);
//                var job = new ComplexFlow_CSharp { A = nativeA, B = nativeB, Result = r1, Threshold = 50 };
//                return MeasurePF(ref job, N, Warmup, Iterations);
//            }, () => {
//                using var r1 = NewResult(N);
//                var job = new ComplexFlow_SIMD { A = nativeA, B = nativeB, Result = r1, Threshold = 50 };
//                return MeasureFor(ref job, N, Warmup, Iterations);
//            }, () => {
//                using var r1 = NewResult(N);
//                var job = new ComplexFlow_SIMD_IJob { A = nativeA, B = nativeB, Result = r1, Threshold = 50, Count = N };
//                return MeasureIJob(ref job, Warmup, Iterations);
//            });

//            // ── Case 5: Gather + Reduce ──
//            RunCase("5_GatherReduce", () => {
//                using var r1 = NewResult(N);
//                var job = new GatherReduce_CSharp { QueryX = nativeQx, QueryY = nativeQy, DataX = nativeDx, DataY = nativeDy, Index = nativeIdx, Result = r1 };
//                return MeasurePF(ref job, N, Warmup, 10);
//            }, () => {
//                using var r1 = NewResult(N);
//                var job = new GatherReduce_SIMD { QueryX = nativeQx, QueryY = nativeQy, DataX = nativeDx, DataY = nativeDy, Index = nativeIdx, Result = r1 };
//                return MeasureFor(ref job, N, Warmup, 10);
//            }, () => {
//                using var r1 = NewResult(N);
//                var job = new GatherReduce_SIMD_IJob { QueryX = nativeQx, QueryY = nativeQy, DataX = nativeDx, DataY = nativeDy, Index = nativeIdx, Result = r1, Count = N };
//                return MeasureIJob(ref job, Warmup, 10);
//            });

//            Console.WriteLine();
//            Console.WriteLine("基准测试完成。");
//            Console.WriteLine("注意: C++/ISPC 后端需要完整的 NativeCompileTask 构建链。");
//            Console.WriteLine("上表中 SIMD/IJob 列通过 IJob 运行时验证 Auto-SIMD。");
//            Console.ReadLine();
//        }

//        // ════════════════════ 工具函数 ════════════════════

//        static NativeArray<float> NewResult(int n) => new NativeArray<float>(n, Allocator.Persistent);

//        static float[] GenFloats(int n, Random rnd)
//        {
//            var d = new float[n];
//            for (int i = 0; i < n; i++) d[i] = (float)(rnd.NextDouble() * 200 - 100);
//            return d;
//        }

//        /// <summary>运行一个 Case，打印 C#/SIMD/IJob 三列结果</summary>
//        static void RunCase(string name,
//            Func<double> measureCs, Func<double> measureSimd, Func<double> measureSimdIJob)
//        {
//            Console.Write($"  {name,-18} ");
//            double cs = measureCs();
//            double simd = measureSimd();
//            double simdIJob = measureSimdIJob();
//            Console.WriteLine($"{cs,8:F3} ms  {simd,8:F3} ms  {simdIJob,8:F3} ms");
//        }

//        static double MeasurePF<T>(ref T job, int count, int warmup, int iter)
//            where T : struct, IJobParallelFor
//        {
//            for (int w = 0; w < warmup; w++)
//                for (int i = 0; i < count; i++) job.Execute(i);
//            var sw = Stopwatch.StartNew();
//            for (int t = 0; t < iter; t++)
//                for (int i = 0; i < count; i++) job.Execute(i);
//            sw.Stop();
//            return sw.Elapsed.TotalMilliseconds / iter;
//        }

//        static double MeasureFor<T>(ref T job, int count, int warmup, int iter)
//            where T : struct, IJobFor
//        {
//            for (int w = 0; w < warmup; w++)
//                for (int i = 0; i < count; i++) job.Execute(i);
//            var sw = Stopwatch.StartNew();
//            for (int t = 0; t < iter; t++)
//                for (int i = 0; i < count; i++) job.Execute(i);
//            sw.Stop();
//            return sw.Elapsed.TotalMilliseconds / iter;
//        }

//        static double MeasureIJob<T>(ref T job, int warmup, int iter)
//            where T : struct, IJob
//        {
//            for (int w = 0; w < warmup; w++) job.Execute();
//            var sw = Stopwatch.StartNew();
//            for (int t = 0; t < iter; t++) job.Execute();
//            sw.Stop();
//            return sw.Elapsed.TotalMilliseconds / iter;
//        }
//    }
//}
