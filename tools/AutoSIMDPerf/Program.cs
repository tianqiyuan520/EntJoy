// 端到端性能对照：同一 job 的托管实现与 AutoSIMD 原生实现，按相同调度参数交替运行取中位。
// 同一轮内先托管后原生，配对比较；跨轮不相减。
using System.Diagnostics;
using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler.Bindings;
using EntJoySample.AutoSIMDTest;

namespace AutoSIMDPerf
{
    public static class Program
    {
        private const int N = 1 << 20;
        private const int Rounds = 9;

        public static unsafe void Main()
        {
            NativeJobScheduler.Initialize();
            // 托管基线也走同一调度器，避免「单线程 vs 多线程」混进对比
            EntJoy.JobSystem.Managed.ManagedJobScheduler.Initialize();
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine($"=== AutoSIMD 端到端性能对照（每轮 {N} 元素，{Rounds} 轮取中位） ===");
            Console.WriteLine();

            using var input = new NativeArray<float>(N, Allocator.Persistent);
            using var outCs = new NativeArray<float>(N, Allocator.Persistent);
            using var outSimd = new NativeArray<float>(N, Allocator.Persistent);
            var rnd = new Random(20261117);
            for (int i = 0; i < N; i++) ((float*)input.GetUnsafePtr())[i] = (float)(rnd.NextDouble() * 2.0 - 1.0);

            Run("ExpLog", input, outCs, outSimd,
                () => new ExpLog_CSharp_PF { Input = input, Result = outCs }.Schedule(N, 0).Complete(),
                () => new ExpLog_SIMD_PF { Input = input, Result = outSimd }.Schedule(N, 0).Complete());

            Run("Transcend", input, outCs, outSimd,
                () => new Transcend_CSharp_PF { Input = input, Result = outCs }.Schedule(N, 0).Complete(),
                () => new Transcend_SIMD_PF { Input = input, Result = outSimd }.Schedule(N, 0).Complete());

            Run("HighPrecision", input, outCs, outSimd,
                () => new HighPrecision_CSharp_PF { Input = input, Result = outCs }.Schedule(N, 0).Complete(),
                () => new HighPrecision_SIMD_PF { Input = input, Result = outSimd }.Schedule(N, 0).Complete());

            RunSplit(input, outCs, outSimd);
            RunBodies(input);
        }

        private static void Run(string name, NativeArray<float> input,
            NativeArray<float> outCs, NativeArray<float> outSimd, Action scheduleCs, Action scheduleSimd)
        {
            // 预热：把首次 JIT、页错误与频率爬升排除在计时之外
            scheduleCs();
            scheduleSimd();

            var csNs = new List<double>(Rounds);
            var simdNs = new List<double>(Rounds);
            for (int round = 0; round < Rounds; round++)
            {
                var sw = Stopwatch.StartNew();
                scheduleCs();
                sw.Stop();
                csNs.Add(sw.Elapsed.TotalNanoseconds / N);

                sw.Restart();
                scheduleSimd();
                sw.Stop();
                simdNs.Add(sw.Elapsed.TotalNanoseconds / N);
            }

            Report(name, csNs, simdNs, outCs, outSimd);
        }

        /// <summary>按 body 拆解：只改计算量，看 SIMD 收益随数学函数密度的变化。</summary>
        private static void RunBodies(NativeArray<float> input)
        {
            using var o1 = new NativeArray<float>(N, Allocator.Persistent);
            using var o2 = new NativeArray<float>(N, Allocator.Persistent);

            Console.WriteLine();
            Console.WriteLine("  body 拆解（原生标量 vs 原生 SIMD，同一输入）");

            Body("arith (x*2.5+3)", input, o1, o2,
                () => new BodyArith_Scalar_PF { Input = input, Result = o1 }.Schedule(N, 0).Complete(),
                () => new BodyArith_SIMD_PF { Input = input, Result = o2 }.Schedule(N, 0).Complete());

            Body("sin", input, o1, o2,
                () => new BodySin_Scalar_PF { Input = input, Result = o1 }.Schedule(N, 0).Complete(),
                () => new BodySin_SIMD_PF { Input = input, Result = o2 }.Schedule(N, 0).Complete());

            Body("sin+cos", input, o1, o2,
                () => new BodySinCos_Scalar_PF { Input = input, Result = o1 }.Schedule(N, 0).Complete(),
                () => new BodySinCos_SIMD_PF { Input = input, Result = o2 }.Schedule(N, 0).Complete());

            Body("sin+cos+log", input, o1, o2,
                () => new BodySinCosLog_Scalar_PF { Input = input, Result = o1 }.Schedule(N, 0).Complete(),
                () => new BodySinCosLog_SIMD_PF { Input = input, Result = o2 }.Schedule(N, 0).Complete());
        }

        private static unsafe void Body(string name, NativeArray<float> input,
            NativeArray<float> outScalar, NativeArray<float> outSimd, Action scalar, Action simd)
        {
            scalar(); simd();

            var scalarNs = new List<double>(Rounds);
            var simdNs = new List<double>(Rounds);
            for (int round = 0; round < Rounds; round++)
            {
                var sw = Stopwatch.StartNew();
                scalar();
                sw.Stop();
                scalarNs.Add(sw.Elapsed.TotalNanoseconds / N);

                sw.Restart();
                simd();
                sw.Stop();
                simdNs.Add(sw.Elapsed.TotalNanoseconds / N);
            }

            scalarNs.Sort(); simdNs.Sort();
            double s = scalarNs[Rounds / 2], v = simdNs[Rounds / 2];

            float worstRel = 0;
            for (int i = 0; i < outScalar.Length; i++)
            {
                float e = ((float*)outScalar.GetUnsafePtr())[i];
                float a = ((float*)outSimd.GetUnsafePtr())[i];
                float rel = MathF.Abs(a - e) / MathF.Max(1.0f, MathF.Abs(e));
                if (rel > worstRel) worstRel = rel;
            }

            Console.WriteLine($"    {name,-18} 标量 {s,6:F2} ns   SIMD {v,6:F2} ns   加速 {s / v,5:F2}x   worstRel={worstRel:E1}");
        }
        /// <summary>三元分解：托管 / 原生标量 / 原生 SIMD，同一算式、同一调度参数。
        /// 「原生标量」把托管开销剥离，剩下的比值才是纯 SIMD 收益。</summary>
        private static unsafe void RunSplit(NativeArray<float> input, NativeArray<float> outCs, NativeArray<float> outSimd)
        {
            using var outScalar = new NativeArray<float>(N, Allocator.Persistent);
            Action managed = () => new PerfSplit_CSharp_PF { Input = input, Result = outCs }.Schedule(N, 0).Complete();
            Action scalar = () => new PerfSplit_Scalar_PF { Input = input, Result = outScalar }.Schedule(N, 0).Complete();
            Action simd = () => new PerfSplit_SIMD_PF { Input = input, Result = outSimd }.Schedule(N, 0).Complete();

            managed(); scalar(); simd();

            var managedNs = new List<double>(Rounds);
            var scalarNs = new List<double>(Rounds);
            var simdNs = new List<double>(Rounds);
            for (int round = 0; round < Rounds; round++)
            {
                var sw = Stopwatch.StartNew();
                managed();
                sw.Stop();
                managedNs.Add(sw.Elapsed.TotalNanoseconds / N);

                sw.Restart();
                scalar();
                sw.Stop();
                scalarNs.Add(sw.Elapsed.TotalNanoseconds / N);

                sw.Restart();
                simd();
                sw.Stop();
                simdNs.Add(sw.Elapsed.TotalNanoseconds / N);
            }

            managedNs.Sort(); scalarNs.Sort(); simdNs.Sort();
            double m = managedNs[Rounds / 2], s = scalarNs[Rounds / 2], v = simdNs[Rounds / 2];

            float worstRel = 0;
            for (int i = 0; i < outCs.Length; i++)
            {
                float e = ((float*)outCs.GetUnsafePtr())[i];
                float a = ((float*)outSimd.GetUnsafePtr())[i];
                float rel = MathF.Abs(a - e) / MathF.Max(1.0f, MathF.Abs(e));
                if (rel > worstRel) worstRel = rel;
            }

            Console.WriteLine();
            Console.WriteLine("  三元分解（同一算式、同一线程配置）");
            Console.WriteLine($"    托管        {m,7:F2} ns/元素");
            Console.WriteLine($"    原生标量    {s,7:F2} ns/元素   托管 -> 原生 {m / s,5:F2}x");
            Console.WriteLine($"    原生 SIMD   {v,7:F2} ns/元素   原生标量 -> SIMD {s / v,5:F2}x   总加速 {m / v,5:F2}x   worstRel={worstRel:E1}");
        }
        private static unsafe void Report(string name, List<double> csNs, List<double> simdNs,
            NativeArray<float> outCs, NativeArray<float> outSimd)
        {
            csNs.Sort();
            simdNs.Sort();
            double cs = csNs[csNs.Count / 2];
            double simd = simdNs[simdNs.Count / 2];

            float worstRel = 0;
            for (int i = 0; i < outCs.Length; i++)
            {
                float e = ((float*)outCs.GetUnsafePtr())[i];
                float a = ((float*)outSimd.GetUnsafePtr())[i];
                if (float.IsNaN(a)) { worstRel = float.PositiveInfinity; break; }
                float rel = MathF.Abs(a - e) / MathF.Max(1.0f, MathF.Abs(e));
                if (rel > worstRel) worstRel = rel;
            }

            Console.WriteLine($"  {name,-14} 托管 {cs,7:F2} ns/元素   AutoSIMD {simd,7:F2} ns/元素   加速 {cs / simd,5:F2}x   worstRel={worstRel:E1}");
        }
    }
}
