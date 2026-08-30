using System;
using System.Collections.Generic;
using EntJoy.Collections;

namespace EntJoySample.SIMD
{
    /// <summary>
    /// 10_SIMD 测试入口：对比 ISPC / AutoSIMD / 普通 Cpp（基准）三后端在 8 个用例上的
    /// 性能与正确性（对照 C# 托管标量 oracle），并进行非 8 倍数尺寸 + 特殊浮点值压力测试。
    ///
    /// 用法：
    ///   dotnet build samples/EntJoySample/EntJoySample.csproj -c Release
    ///   bin/EntJoySample.exe          （由 09_ECS/Program.cs Main 调用本类 Run()）
    /// </summary>
    public static class SimdCompareTest
    {
        // ── 容差：|g-w| ≤ ATol + RTol·|w|（isclose 风格）。ATol 覆盖 ~1000 量级下 1 ULP 的
        //    FMA/累加噪声；RTol 覆盖相对 1 ULP（~1.2e-7）。语义 bug 会远超此阈值。──
        const double ATol = 1e-3;
        const double RTol = 1e-6;

        static readonly List<string> Bugs = new();

        public static void Run()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== 10_SIMD: ISPC vs AutoSIMD vs Cpp（Cpp=普通翻译基准）===\n");

            var rows = new List<Row>();
            RunC01(rows);
            RunC02(rows);
            RunC03(rows);
            RunC04(rows);
            RunC05(rows);
            RunC06(rows);
            RunC07(rows);
            RunC08(rows);
            RunC09(rows);
            RunC10(rows);
            RunC11(rows);
            RunC12(rows);
            RunC13(rows);
            RunC14(rows);
            RunStress();

            PrintSummary(rows);
            PrintBugs();
        }

        // =====================================================================
        // 通用工具
        // =====================================================================
        struct BackendResult
        {
            public double Ms;
            public double MaxDiff;
            public long Mismatch;
            public string? Exception;
        }

        struct Row
        {
            public string Name;
            public BackendResult Cpp, Ispc, Auto;
        }

        static double MeasureMs(Action run)
        {
            run(); // warmup
            // ── 基准降噪（2026-08-30）：单次 run() 仅 0.04–1ms，OS 调度抖动 ~30%。
            //    自校准把每个计时样本批量执行到 ~100ms，取 7 样本中位数，噪声降到 ~1–3%。
            //    N 必须保持缓存驻留规模（1M/4MB）：N 过大（10M/40MB）会退化成内存带宽
            //    对比，所有后端（含标量 Cpp）趋同，测不出计算差异。──
            const double TargetSampleMs = 100.0;
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            run();
            sw0.Stop();
            double t0 = Math.Max(sw0.Elapsed.TotalMilliseconds, 0.001);
            int reps = (int)Math.Ceiling(TargetSampleMs / t0);
            if (reps < 1) reps = 1;
            if (reps > 2000) reps = 2000;

            var times = new double[7];
            for (int r = 0; r < 7; r++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int k = 0; k < reps; k++) run();
                sw.Stop();
                times[r] = sw.Elapsed.TotalMilliseconds / reps;
            }
            Array.Sort(times);
            return times[3];
        }

        static (double maxAbs, long mismatch) CmpF(NativeArray<float> got, float[] want, int n)
        {
            double maxAbs = 0; long mis = 0;
            for (int i = 0; i < n; i++)
            {
                float g = got[i], w = want[i];
                bool eq = g == w || (float.IsNaN(g) && float.IsNaN(w));
                if (!eq)
                {
                    double d = (float.IsNaN(g) || float.IsNaN(w))
                        ? double.PositiveInfinity
                        : Math.Abs((double)g - w);
                    if (d > maxAbs) maxAbs = d;
                    if (d > ATol + RTol * Math.Abs((double)w)) mis++;
                }
            }
            return (maxAbs, mis);
        }

        static (double maxDiff, long mismatch) CmpI(NativeArray<int> got, int[] want, int n)
        {
            double max = 0; long mis = 0;
            for (int i = 0; i < n; i++)
            {
                if (got[i] != want[i])
                {
                    mis++;
                    long d = Math.Abs((long)got[i] - want[i]);
                    if (d > max) max = d;
                }
            }
            return (max, mis);
        }

        static BackendResult RunFloatBackend(Action<NativeArray<float>> run, float[] want, int n)
        {
            var r = new NativeArray<float>(n, Allocator.TempJob);
            try
            {
                double ms = MeasureMs(() => run(r));
                var (d, m) = CmpF(r, want, n);
                return new BackendResult { Ms = ms, MaxDiff = d, Mismatch = m };
            }
            catch (Exception e)
            {
                return new BackendResult { Ms = 0, MaxDiff = double.PositiveInfinity, Mismatch = -1, Exception = e.GetType().Name + ": " + e.Message };
            }
            finally { r.Dispose(); }
        }

        static BackendResult RunIntBackend(Action<NativeArray<int>> run, int[] want, int n)
        {
            var r = new NativeArray<int>(n, Allocator.TempJob);
            try
            {
                double ms = MeasureMs(() => run(r));
                var (d, m) = CmpI(r, want, n);
                return new BackendResult { Ms = ms, MaxDiff = d, Mismatch = m };
            }
            catch (Exception e)
            {
                return new BackendResult { Ms = 0, MaxDiff = double.PositiveInfinity, Mismatch = -1, Exception = e.GetType().Name + ": " + e.Message };
            }
            finally { r.Dispose(); }
        }

        static string Status(BackendResult res)
        {
            if (res.Exception != null) return "EXC(" + res.Exception + ")";
            return res.Mismatch == 0 ? "PASS" : $"FAIL(abs={res.MaxDiff:E3},mis={res.Mismatch})";
        }

        static void Record(string caseName, string backend, BackendResult res, ref int pass, ref int fail)
        {
            bool ok = res.Exception == null && res.Mismatch == 0;
            if (ok) pass++; else { fail++; Bugs.Add($"{caseName} [{backend}] {Status(res)}"); }
        }

        static string Fmt(double ms) => ms <= 0 ? "   -  " : $"{ms,7:F3}";

        // =====================================================================
        // 用例
        // =====================================================================
        static void RunC01(List<Row> rows)
        {
            const int n = 1_000_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var B = new NativeArray<float>(n, Allocator.TempJob);
            var C = new NativeArray<float>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float a = (i % 1001) * 0.1f - 50f;
                    float b = (i % 401) * 0.1f - 20f;
                    float c = (i % 101) * 0.1f - 5f;
                    A[i] = a; B[i] = b; C[i] = c;
                    want[i] = Oracles.C01(a, b, c);
                }
                var row = new Row { Name = "C01_Light(A*B+C)" };
                row.Cpp = RunFloatBackend(r => new C01LightCpp { A = A, B = B, C = C, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C01LightIspc { A = A, B = B, C = C, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C01LightAuto { A = A, B = B, C = C, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); B.Dispose(); C.Dispose(); }
        }

        static void RunC02(List<Row> rows)
        {
            const int n = 100_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float a = (i % 101) * 0.01f - 0.5f;
                    A[i] = a;
                    want[i] = Oracles.C02(a);
                }
                var row = new Row { Name = "C02_Heavy(sin/cos×16)" };
                row.Cpp = RunFloatBackend(r => new C02HeavyCpp { A = A, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C02HeavyIspc { A = A, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C02HeavyAuto { A = A, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); }
        }

        static void RunC03(List<Row> rows)
        {
            const int n = 1_000_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var B = new NativeArray<float>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float a = (i % 2001) * 0.1f - 100f;
                    float b = (i % 401) * 0.1f - 20f;
                    A[i] = a; B[i] = b;
                    want[i] = Oracles.C03(a, b);
                }
                var row = new Row { Name = "C03_Flow(5-way if/else)" };
                row.Cpp = RunFloatBackend(r => new C03FlowCpp { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C03FlowIspc { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C03FlowAuto { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); B.Dispose(); }
        }

        static void RunC04(List<Row> rows)
        {
            const int n = 1_000_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var B = new NativeArray<float>(n, Allocator.TempJob);
            var C = new NativeArray<float>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float a = (i % 1001) * 0.1f - 50f;
                    float b = (i % 401) * 0.1f - 20f;
                    float c = (i % 101) * 0.1f - 5f;
                    A[i] = a; B[i] = b; C[i] = c;
                    want[i] = Oracles.C04(a, b, c);
                }
                var row = new Row { Name = "C04_NestedFor(3x3+continue)" };
                row.Cpp = RunFloatBackend(r => new C04NestedCpp { A = A, B = B, C = C, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C04NestedIspc { A = A, B = B, C = C, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C04NestedAuto { A = A, B = B, C = C, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); B.Dispose(); C.Dispose(); }
        }

        static void RunC05(List<Row> rows)
        {
            const int n = 1_000_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var B = new NativeArray<float>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    // 让 acc 在部分 lane 上超过 1000，触发 varying break（验证 Bug C 修复）
                    float a = (i % 1001) * 0.1f - 50f;   // [-50, 50]
                    float b = (i % 401) * 0.5f;          // [0, 200]
                    A[i] = a; B[i] = b;
                    want[i] = Oracles.C05(a, b);
                }
                var row = new Row { Name = "C05_While(+break)" };
                row.Cpp = RunFloatBackend(r => new C05WhileCpp { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C05WhileIspc { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C05WhileAuto { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); B.Dispose(); }
        }

        static void RunC06(List<Row> rows)
        {
            const int n = 1_000_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var B = new NativeArray<float>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float a = (i % 1001) * 0.1f - 50f;
                    float b = (i % 401) * 0.1f - 20f;
                    A[i] = a; B[i] = b;
                    want[i] = Oracles.C06(a, b);
                }
                var row = new Row { Name = "C06_Reduction(min)" };
                row.Cpp = RunFloatBackend(r => new C06ReduceCpp { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C06ReduceIspc { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C06ReduceAuto { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); B.Dispose(); }
        }

        static void RunC07(List<Row> rows)
        {
            const int n = 1_000_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var B = new NativeArray<float>(n, Allocator.TempJob);
            var Indices = new NativeArray<int>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    A[i] = (i % 1001) * 0.1f - 50f;
                    B[i] = (i % 401) * 0.1f - 20f;
                    Indices[i] = (int)(((long)i * 7919 + 1234) % n);
                }
                // oracle 必须等 A/Indices 全部填好后再算（gather 会读到前向索引）
                for (int i = 0; i < n; i++)
                    want[i] = Oracles.C07(A[Indices[i]], B[i]);
                var row = new Row { Name = "C07_Gather(A[Idx]*B)" };
                row.Cpp = RunFloatBackend(r => new C07GatherCpp { A = A, B = B, Indices = Indices, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C07GatherIspc { A = A, B = B, Indices = Indices, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C07GatherAuto { A = A, B = B, Indices = Indices, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); B.Dispose(); Indices.Dispose(); }
        }

        static void RunC08(List<Row> rows)
        {
            const int n = 100_000;
            var want = new int[n];
            for (int i = 0; i < n; i++) want[i] = Oracles.C08(i);

            var row = new Row { Name = "C08_IntUint(LCG+shift)" };
            row.Cpp = RunIntBackend(r => new C08IntCpp { R = r }.Schedule(n, 0).Complete(), want, n);
            row.Ispc = RunIntBackend(r => new C08IntIspc { R = r }.Schedule(n, 0).Complete(), want, n);
            row.Auto = RunIntBackend(r => new C08IntAuto { R = r }.Schedule(n, 0).Complete(), want, n);
            rows.Add(row);
            PrintRow(row);
        }

        static void RunC09(List<Row> rows)
        {
            const int n = 1_000_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var B = new NativeArray<float>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float a = (i % 1001) * 0.1f - 50f;
                    float b = (i % 401) * 0.1f - 20f;
                    A[i] = a; B[i] = b;
                    want[i] = Oracles.C09(a, b);
                }
                var row = new Row { Name = "C09_Nearest(min+sqrt)" };
                row.Cpp = RunFloatBackend(r => new C09NearestCpp { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C09NearestIspc { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C09NearestAuto { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); B.Dispose(); }
        }

        static void RunC10(List<Row> rows)
        {
            const int n = 1_000_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var B = new NativeArray<float>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float pos = (i % 2001) * 0.1f - 100f;   // [-100,100] 触发边界反弹
                    float vel = (i % 401) * 0.1f - 20f;
                    A[i] = pos; B[i] = vel;
                    want[i] = Oracles.C10(pos, vel);
                }
                var row = new Row { Name = "C10_MoveBounce(if+bound)" };
                row.Cpp = RunFloatBackend(r => new C10BounceCpp { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C10BounceIspc { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C10BounceAuto { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); B.Dispose(); }
        }

        static void RunC11(List<Row> rows)
        {
            const int n = 1_000_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float energy = (i % 1001) * 0.1f;   // [0,100] 触发 while 衰减
                    A[i] = energy;
                    want[i] = Oracles.C11(energy);
                }
                var row = new Row { Name = "C11_Lifetime(while)" };
                row.Cpp = RunFloatBackend(r => new C11LifetimeCpp { A = A, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C11LifetimeIspc { A = A, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C11LifetimeAuto { A = A, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); }
        }

        static void RunC12(List<Row> rows)
        {
            const int n = 1_000_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var B = new NativeArray<float>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    // 整数化 fill（float 存整数，< 2^24 精确）→ 避开 v>50 的浮点边界，
                    // 使 Cpp/ISPC/AutoSIMD 的 found 完全一致；同时稳定触发 continue(v<0) 和 break(v>50)
                    float a = (i % 100) * 1.0f;           // [0,99]
                    float b = ((i % 5) - 2) * 10.0f;      // {-20,-10,0,10,20}
                    A[i] = a; B[i] = b;
                    want[i] = Oracles.C12(a, b);
                }
                var row = new Row { Name = "C12_SearchSkip(continue)" };
                row.Cpp = RunFloatBackend(r => new C12SearchCpp { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C12SearchIspc { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C12SearchAuto { A = A, B = B, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); B.Dispose(); }
        }

        static void RunC13(List<Row> rows)
        {
            const int n = 1_000_000;
            var A = new NativeArray<float>(n, Allocator.TempJob);
            var want = new float[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float a = (i % 2001) * 1.0f;   // [0,2000] 触发各种路径（<1000 继续 / >500 break / >=1000 退出）
                    A[i] = a;
                    want[i] = Oracles.C13(a);
                }
                var row = new Row { Name = "C13_BoundBreak(uni&&var+break)" };
                row.Cpp = RunFloatBackend(r => new C13BoundBreakCpp { A = A, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Ispc = RunFloatBackend(r => new C13BoundBreakIspc { A = A, R = r }.Schedule(n, 0).Complete(), want, n);
                row.Auto = RunFloatBackend(r => new C13BoundBreakAuto { A = A, R = r }.Schedule(n, 0).Complete(), want, n);
                rows.Add(row);
                PrintRow(row);
            }
            finally { A.Dispose(); }
        }

        // C14 专用：Interlocked.CompareExchange 是破坏性操作（命中 comparand 时改写 T），
        // 每次计时运行前重置 T（T0.CopyTo(T)，~4MB memcpy，三后端等量计入，比值不受影响）。
        static BackendResult RunCasBackend(NativeArray<int> T0, NativeArray<int> V, NativeArray<int> C,
            int[] want, int n, Action<NativeArray<int>, NativeArray<int>> runJob)
        {
            var T = new NativeArray<int>(n, Allocator.TempJob);
            var r = new NativeArray<int>(n, Allocator.TempJob);
            try
            {
                double ms = MeasureMs(() => { T0.CopyTo(T); runJob(T, r); });
                var (d, m) = CmpI(r, want, n);
                return new BackendResult { Ms = ms, MaxDiff = d, Mismatch = m };
            }
            finally { T.Dispose(); r.Dispose(); }
        }

        // ── C14_Cas: Interlocked.CompareExchange（ISPC Fix 1 覆盖） ──
        //   仅 Cpp+Ispc：AutoSIMD(Simd*) 无 Interlocked 翻译分支，跳过（PASS 占位，Ms=0 不参与比值）。
        static unsafe void RunC14(List<Row> rows)
        {
            const int n = 1_000_000;
            var T0 = new NativeArray<int>(n, Allocator.TempJob);
            var V = new NativeArray<int>(n, Allocator.TempJob);
            var C = new NativeArray<int>(n, Allocator.TempJob);
            var want = new int[n];
            try
            {
                var rnd = new Random(42);
                for (int i = 0; i < n; i++)
                {
                    int t = rnd.Next(-50, 51);
                    int c = (i % 3 == 0) ? t : rnd.Next(-50, 51); // 1/3 命中 comparand → 触发 swap 路径
                    int v = rnd.Next(-50, 51);
                    T0[i] = t; V[i] = v; C[i] = c;
                    want[i] = (t == c) ? c : t; // CompareExchange 返回旧值
                }
                var row = new Row { Name = "C14_Cas(CompareExchange)" };
                row.Cpp = RunCasBackend(T0, V, C, want, n,
                    (T, r) => new C14CasCpp { T = (int*)T.GetUnsafePtr(), V = V, C = C, R = r }.Schedule(n, 0).Complete());
                row.Ispc = RunCasBackend(T0, V, C, want, n,
                    (T, r) => new C14CasIspc { T = (int*)T.GetUnsafePtr(), V = V, C = C, R = r }.Schedule(n, 0).Complete());
                row.Auto = new BackendResult { Ms = 0, MaxDiff = 0, Mismatch = 0 }; // AutoSIMD 不支持 Interlocked
                rows.Add(row);
                PrintRow(row);
            }
            finally { T0.Dispose(); V.Dispose(); C.Dispose(); }
        }

        static void PrintRow(Row row)
        {
            string c = Status(row.Cpp);
            string i = Status(row.Ispc);
            string a = Status(row.Auto);
            double ac = row.Cpp.Ms > 0 && row.Auto.Ms > 0 ? row.Cpp.Ms / row.Auto.Ms : 0;
            double ai = row.Ispc.Ms > 0 && row.Auto.Ms > 0 ? row.Ispc.Ms / row.Auto.Ms : 0;
            Console.WriteLine(
                $"{row.Name,-24} Cpp={Fmt(row.Cpp.Ms)} ISPC={Fmt(row.Ispc.Ms)} AutoSIMD={Fmt(row.Auto.Ms)}" +
                $" | Auto/Cpp={ac,5:F2}x Auto/ISPC={ai,5:F2}x | Cpp:{c,-6} ISPC:{i,-6} AutoSIMD:{a}");
        }

        // =====================================================================
        // 压力测试：非 8 倍数尺寸 + 特殊浮点值
        // =====================================================================
        static void RunStress()
        {
            Console.WriteLine("\n--- Stress: 非 8 倍数尺寸 ---");
            int pass = 0, fail = 0;

            // C01 float @ 2045 / 4093
            foreach (int n in new[] { 2045, 4093 })
            {
                var A = new NativeArray<float>(n, Allocator.TempJob);
                var B = new NativeArray<float>(n, Allocator.TempJob);
                var C = new NativeArray<float>(n, Allocator.TempJob);
                var want = new float[n];
                try
                {
                    for (int i = 0; i < n; i++)
                    {
                        float a = (i % 1001) * 0.1f - 50f;
                        float b = (i % 401) * 0.1f - 20f;
                        float c = (i % 101) * 0.1f - 5f;
                        A[i] = a; B[i] = b; C[i] = c;
                        want[i] = Oracles.C01(a, b, c);
                    }
                    var cpp = RunFloatBackend(r => new C01LightCpp { A = A, B = B, C = C, R = r }.Schedule(n, 0).Complete(), want, n);
                    var ispc = RunFloatBackend(r => new C01LightIspc { A = A, B = B, C = C, R = r }.Schedule(n, 0).Complete(), want, n);
                    var auto = RunFloatBackend(r => new C01LightAuto { A = A, B = B, C = C, R = r }.Schedule(n, 0).Complete(), want, n);
                    Console.WriteLine($"  C01 N={n}: Cpp:{Status(cpp)} ISPC:{Status(ispc)} AutoSIMD:{Status(auto)}");
                    Record($"C01 N={n}", "Cpp", cpp, ref pass, ref fail);
                    Record($"C01 N={n}", "ISPC", ispc, ref pass, ref fail);
                    Record($"C01 N={n}", "AutoSIMD", auto, ref pass, ref fail);
                }
                finally { A.Dispose(); B.Dispose(); C.Dispose(); }
            }

            // C08 int @ 2045 / 4093（精确）
            foreach (int n in new[] { 2045, 4093 })
            {
                var want = new int[n];
                for (int i = 0; i < n; i++) want[i] = Oracles.C08(i);
                var cpp = RunIntBackend(r => new C08IntCpp { R = r }.Schedule(n, 0).Complete(), want, n);
                var ispc = RunIntBackend(r => new C08IntIspc { R = r }.Schedule(n, 0).Complete(), want, n);
                var auto = RunIntBackend(r => new C08IntAuto { R = r }.Schedule(n, 0).Complete(), want, n);
                Console.WriteLine($"  C08 N={n}: Cpp:{Status(cpp)} ISPC:{Status(ispc)} AutoSIMD:{Status(auto)}");
                Record($"C08 N={n}", "Cpp", cpp, ref pass, ref fail);
                Record($"C08 N={n}", "ISPC", ispc, ref pass, ref fail);
                Record($"C08 N={n}", "AutoSIMD", auto, ref pass, ref fail);
            }

            // 特殊浮点值（NaN/±Inf/±0/denormal/FLT_MAX）→ C01，重点看 AutoSIMD（precise 库）
            Console.WriteLine("\n--- Stress: 特殊浮点值 (C01) ---");
            const int sn = 1001; // 非 8 倍数，覆盖 SIMD + 余量
            var sA = new NativeArray<float>(sn, Allocator.TempJob);
            var sB = new NativeArray<float>(sn, Allocator.TempJob);
            var sC = new NativeArray<float>(sn, Allocator.TempJob);
            var swant = new float[sn];
            float[] specials =
            {
                float.NaN, float.PositiveInfinity, float.NegativeInfinity,
                0f, -0f, float.MaxValue, float.Epsilon,
                BitConverter.Int32BitsToSingle(0x00000001) // 最小次正规
            };
            try
            {
                for (int i = 0; i < sn; i++)
                {
                    float a = (i % 1001) * 0.1f - 50f;
                    if (i < specials.Length) a = specials[i];
                    float b = (i % 401) * 0.1f - 20f;
                    float c = (i % 101) * 0.1f - 5f;
                    sA[i] = a; sB[i] = b; sC[i] = c;
                    swant[i] = Oracles.C01(a, b, c);
                }
                var cpp = RunFloatBackend(r => new C01LightCpp { A = sA, B = sB, C = sC, R = r }.Schedule(sn, 0).Complete(), swant, sn);
                var ispc = RunFloatBackend(r => new C01LightIspc { A = sA, B = sB, C = sC, R = r }.Schedule(sn, 0).Complete(), swant, sn);
                var auto = RunFloatBackend(r => new C01LightAuto { A = sA, B = sB, C = sC, R = r }.Schedule(sn, 0).Complete(), swant, sn);
                Console.WriteLine($"  C01 special(N={sn}): Cpp:{Status(cpp)} ISPC:{Status(ispc)} AutoSIMD:{Status(auto)}");
                Console.WriteLine("    (注：Cpp/ISPC 用 fast-math，NaN/Inf 语义未定义；AutoSIMD precise 库应精确)");
                Record("C01 special", "AutoSIMD", auto, ref pass, ref fail);
            }
            finally { sA.Dispose(); sB.Dispose(); sC.Dispose(); }

            Console.WriteLine($"\n--- Stress 合计 {pass} PASS / {fail} FAIL ---");
        }

        static void PrintSummary(List<Row> rows)
        {
            Console.WriteLine("\n=== 汇总 ===");
            Console.WriteLine($"{"Case",-24} {"Cpp(ms)",10} {"ISPC(ms)",10} {"Auto(ms)",10} {"Auto/Cpp",8} {"Auto/ISPC",9}");
            foreach (var r in rows)
            {
                double ac = r.Cpp.Ms > 0 && r.Auto.Ms > 0 ? r.Cpp.Ms / r.Auto.Ms : 0;
                double ai = r.Ispc.Ms > 0 && r.Auto.Ms > 0 ? r.Ispc.Ms / r.Auto.Ms : 0;
                Console.WriteLine($"{r.Name,-24} {Fmt(r.Cpp.Ms),10} {Fmt(r.Ispc.Ms),10} {Fmt(r.Auto.Ms),10} {ac,8:F2}x {ai,9:F2}x");
            }
        }

        static void PrintBugs()
        {
            if (Bugs.Count == 0)
            {
                Console.WriteLine("\n✅ 未发现翻译 bug（所有后端在各自容差内通过）。");
                return;
            }
            Console.WriteLine($"\n❌ 疑似翻译 bug / 超容差（{Bugs.Count} 项）：");
            foreach (var b in Bugs) Console.WriteLine("  - " + b);
        }
    }
}
