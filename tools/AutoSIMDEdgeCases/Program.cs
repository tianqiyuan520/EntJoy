// AutoSIMDEdgeCases — special floats + adversarial boundary verification.
// C# managed baseline as oracle, element-wise (bit-exact) comparison with AutoSIMD native output.
// Coverage: ±Inf / NaN / ±0 / subnormal / FLT_MAX / INT_MIN-MAX overflow /
//           multi-level && || mixed continue / nested return / while loop / non-constant loop bound,
//           plus large-scale random fuzz (with special value injection).
// 用法：dotnet run --project tools/AutoSIMDEdgeCases -c Release [--fast]
//       需先构建 EntJoySample 并把 NativeTranspiled.dll / NativeDll.dll 放到 bin/。
using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler.Bindings;
using EntJoySample.AutoSIMDTest;

namespace AutoSIMDEdgeCases
{
    public static class Program
    {
        private static bool _fastMode;

        public static void Main(string[] args)
        {
            _fastMode = args.Any(a => a == "--fast");
            NativeJobScheduler.Initialize();
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== AutoSIMD EdgeCase Verification (bit-exact, special floats + adversarial) ===\n");

            int passed = 0, failed = 0;
            RunEdgeCases(ref passed, ref failed);
            RunFuzz(ref passed, ref failed);

            // ★ Diagnostic: direct extension method call for EC5 (same as normal path)
            Console.WriteLine("  --- Direct batch call diagnostic ---");
            try
            {
                Console.Error.WriteLine("  [DIAG] Starting EC5 diagnostic...");
                int n = 512;
                var src = SeedFatalFloats(n, 1);
                Console.Error.WriteLine($"  [DIAG] src created: length={src.Length}, IsCreated={src.IsCreated}");
                var rc = new NativeArray<float>(n, Allocator.Persistent);
                var rs = new NativeArray<float>(n, Allocator.Persistent);
                Console.Error.WriteLine($"  [DIAG] rc/rs created: {rc.Length}/{rs.Length}");
                Console.Error.WriteLine($"  [DIAG] Creating C# job...");
                var j = new EdgeCase5_CSharp_For { A = src, R = rc };
                Console.Error.WriteLine($"  [DIAG] C# job created, running loop...");
                for (int i = 0; i < 3; i++) { j.Execute(i); Console.Error.WriteLine($"  [DIAG] i={i} done"); }
                var simdJob = new EdgeCase5_SIMD_For { A = src, R = rs };
                Console.Error.WriteLine($"  [DIAG] About to Schedule...");
                simdJob.Schedule(n, 64).Complete(); // try with explicit batch size
                Console.Error.WriteLine($"  [DIAG] Schedule complete, rs[0]={rs[0]:G6}");
                // Dump first 16 elements to see pattern
                for (int k = 0; k < 16; k++)
                    Console.Error.WriteLine($"  [DIAG] rs[{k}]={rs[k]:G6} (cs={rc[k]:G6})");
                int bad = 0;
                for (int i = 0; i < n; i++)
                {
                    if (rc[i] != rs[i])
                    {
                        bad++;
                        if (bad <= 3) Console.WriteLine($"    @{i}: A={src[i]:G6} cs={rc[i]:G6} simd={rs[i]:G6}");
                    }
                }
                Console.WriteLine($"  EC5-diag: {n - bad}/{n} correct, {bad} wrong");
                src.Dispose(); rc.Dispose(); rs.Dispose();
            }
            catch (Exception ex) { Console.Error.WriteLine($"  [DIAG] EXCEPTION: {ex.Message}\n{ex.StackTrace}"); }

            Console.WriteLine($"\n=  PASS {passed} / {passed + failed}");
            if (failed == 0) Console.WriteLine("✅ ALL EdgeCase + Fuzz tests correct.");
            else Console.WriteLine($"❌ {failed} FAILURES — see above.");
            NativeJobScheduler.Shutdown();
            if (failed > 0) Environment.Exit(1);
        }

        // ════════════════════════════════════════════════════════════════
        // Special value injection input sets
        // ════════════════════════════════════════════════════════════════
        private static readonly float[] FatalFloats =
        {
            float.PositiveInfinity, float.NegativeInfinity, float.NaN,
            0f, -0f,
            1e-45f, -1e-45f,                    // 最小次正规
            2.8e-44f, 1.1754944e-38f,           // 次正规 / 最小正规
            3.4028235e+38f, -3.4028235e+38f,    // ±FLT_MAX
            1.17549435e-38f,                    // FLT_MIN
            10f, 100f, 1000f, -1f, -0.5f, -100f,
        };

        private static readonly int[] FatalInts =
        {
            int.MinValue, int.MaxValue, int.MinValue + 1, int.MaxValue - 1,
            -1, 0, 1, 2, 3, -2, -3,
            0x7fffffff, unchecked((int)0x80000000), -1 /*0xffffffff*/, 0x55555555, int.MinValue,
        };

        private static void RunEdgeCases(ref int passed, ref int failed)
        {
            Console.WriteLine("  --- EdgeCase: special floats + adversarial semantics ---");
            int NS = 512;   // 8 的倍数（主 SIMD 路径）
            int NSOdd = 500; // 非 8 倍数 remainder 路径

            // EC1: 特殊浮点值算术 + MathF（math 允许 ULP 容差：SLEEF vs libm）
            RunPair("EC1 special-float math (Inf/NaN/±0/subnorm/FLT_MAX)",
                NS, (seed) => SeedFatalFloats(NS, seed), (a, r, n) => pc1(a, r, n), (a, r, n) => sc1(a, r, n),
                ref passed, ref failed, maxUlps: 8);
            RunPair("EC1-odd remainder path",
                NSOdd, (seed) => SeedFatalFloats(NSOdd, seed), (a, r, n) => pc1(a, r, n), (a, r, n) => sc1(a, r, n),
                ref passed, ref failed, maxUlps: 8);

            // EC2: Arbitrary bit patterns (NaN/Inf/-0/sign-zero semantics) — bit-pattern input constructed externally via BitConverter
            RunPair("EC2 bit-pattern NaN/±0 compare semantics",
                NS, (seed) => SeedFatalBitsAsFloats(NS, seed), (a, r, n) => pc2(a, r, n), (a, r, n) => sc2(a, r, n),
                ref passed, ref failed);

            // EC3: int 溢出边界
            RunPairIntInt("EC3 int overflow INT_MIN/MAX",
                NS, (seed) => SeedFatalInts(NS, seed), (a, b, r, n) => pc3(a, b, r, n), (a, b, r, n) => sc3(a, b, r, n),
                ref passed, ref failed);

            // EC4: 多层 && || 混合 continue
            RunPair("EC4 mixed &&|| De Morgan continue",
                NS, (seed) => SeedFatalFloats(NS * 400, seed), (a, r, n) => pc4(a, r, n), (a, r, n) => sc4(a, r, n),
                ref passed, ref failed);

            // EC5: 嵌套循环 return
            RunPair("EC5 nested loop return",
                NS, (seed) => SeedFatalFloats(NS * 64, seed), (a, r, n) => pc5(a, r, n), (a, r, n) => sc5(a, r, n),
                ref passed, ref failed);

            // EC6: 非常量循环边界 + 分支
            RunPairCount("EC6 non-const loop bound + branch",
                NS, (seed) => SeedFatalFloats(NS * 32, seed), (seed) => SeedCounts(NS, seed),
                (a, c, r, n) => pc6(a, c, r, n), (a, c, r, n) => sc6(a, c, r, n),
                ref passed, ref failed);

            // EC7: while 循环
            RunPair("EC7 while loop",
                NS, (seed) => SeedFatalFloats(NS * 10, seed), (a, r, n) => pc7(a, r, n), (a, r, n) => sc7(a, r, n),
                ref passed, ref failed);

            // EC8: 次正规 + 符号零累积（位模式特判）
            RunPair("EC8 subnormal + -0 bit-trick accumulation",
                NS, (seed) => SeedFatalFloats(NS, seed), (a, r, n) => pc8(a, r, n), (a, r, n) => sc8(a, r, n),
                ref passed, ref failed);

            // EC9: 多层嵌套 if-else + 多分支累积器
            RunPair("EC9 nested if/else multi-branch acc",
                NS, (seed) => SeedFatalFloats(NS, seed), (a, r, n) => pc9(a, r, n), (a, r, n) => sc9(a, r, n),
                ref passed, ref failed);

            // EC10: Focused regression — all-INT_MIN input for unchecked(-x) branch
            //   N is multiple of 8 + non-8 remainder, covering all lane positions + scalar fallback.
            foreach (var ec10n in new[] { 8, 9, 16, 17, 63, 64, 65, 1000, 4096, 4097 })
            {
                RunPairIntInt($"EC10 INT_MIN -x (n={ec10n})",
                    ec10n, (seed) => SeedAllMinInt(ec10n), (a, b, r, n) => pc10(a, r, n), (a, b, r, n) => sc10(a, r, n),
                    ref passed, ref failed);
            }

            // ★ EC8 diagnostic: simple NaN test
            Console.WriteLine("  --- EC8 NaN test ---");
            {
                int n = 8;
                float[] hostA = new float[n];
                for (int i = 0; i < n; i++) hostA[i] = float.NaN;
                var src = new NativeArray<float>(hostA, Allocator.Persistent);
                var rs = new NativeArray<float>(n, Allocator.Persistent);
                new EdgeCase8_SIMD_For { A = src, R = rs }.Schedule(n, 0).Complete();
                Console.WriteLine($"    All NaN → rs[0]={rs[0]:G6} (expect 0.5)");

                float[] hostB = new float[8] {0.5f, -0.5f, 0.0f, -0.0f, 1e-45f, 0.3f, float.NaN, 0.0f};
                var src2 = new NativeArray<float>(hostB, Allocator.Persistent);
                var rs2 = new NativeArray<float>(8, Allocator.Persistent);
                new EdgeCase8_SIMD_For { A = src2, R = rs2 }.Schedule(8, 0).Complete();
                Console.WriteLine($"    Mixed: rs0={rs2[0]:G6} rs1={rs2[1]:G6} rs4={rs2[4]:G6} rs6={rs2[6]:G6}");

                // Direct batch call via adapter using the generated Schedule internals
                // Use NativeJobScheduler directly to call the batch fn
                Console.WriteLine($"    [probe] src2[6]={src2[6]:G6} src[0]={src[0]:G6}");
                src.Dispose(); rs.Dispose(); src2.Dispose(); rs2.Dispose();
            }

        }

        // ════════════════════════════════════════════════════════════════
        // Random fuzz: multiple seeds, random data + random special value injection
        // ════════════════════════════════════════════════════════════════
        private static void RunFuzz(ref int passed, ref int failed)
        {
            int seeds = _fastMode ? 6 : 24;
            Console.WriteLine($"  --- Fuzz: {seeds} seeds (random + special-value injection, sizes 1001/2047/4093/8192) ---");
            for (int seed = 1; seed <= seeds; seed++)
            {
                int n = new int[] { 1001, 2047, 4093, 8192 }[seed % 4];
                RunFuzzOne(seed, n, ref passed, ref failed);
            }
        }

        private static void RunFuzzOne(int seed, int n, ref int passed, ref int failed)
        {
            var rnd = new Random(seed * 7919);
            // Construct random + special value mixed input
            var fa = new NativeArray<float>(n, Allocator.Persistent);
            var fb = new NativeArray<float>(n, Allocator.Persistent);
            var ia = new NativeArray<int>(n, Allocator.Persistent);
            var ib = new NativeArray<int>(n, Allocator.Persistent);
            var big5 = new NativeArray<float>(n * 64, Allocator.Persistent);  // EC5 需要 i*64 访问
            for (int i = 0; i < n; i++)
            {
                fa[i] = MixFloat(rnd);
                fb[i] = MixFloat(rnd);
                ia[i] = MixInt(rnd);
                ib[i] = MixInt(rnd);
            }
            for (int i = 0; i < n * 64; i++) big5[i] = MixFloat(rnd);
            try
            {
                RunPair("FZ1 fuzz arithmetic+math", n,
                    (s) => fa, (a, r, cnt) => pc1(a, r, cnt), (a, r, cnt) => sc1(a, r, cnt),
                    ref passed, ref failed, dispose: false, maxUlps: 8);
                RunPair("FZ2 fuzz bit-pattern compare", n,
                    (s) => fa, (a, r, cnt) => pc2(a, r, cnt), (a, r, cnt) => sc2(a, r, cnt),
                    ref passed, ref failed, dispose: false);
                RunPairIntInt("FZ3 fuzz int overflow", n,
                    (s) => ia, (a, b, r, cnt) => pc3(a, b, r, cnt), (a, b, r, cnt) => sc3(a, b, r, cnt),
                    ref passed, ref failed, dispose: false);
                RunPair("FZ4 fuzz nested-loop return", n,
                    (s) => big5, (a, r, cnt) => pc5(a, r, cnt), (a, r, cnt) => sc5(a, r, cnt),
                    ref passed, ref failed, dispose: false);
                RunPair("FZ5 fuzz nested if/else acc", n,
                    (s) => fa, (a, r, cnt) => pc9(a, r, cnt), (a, r, cnt) => sc9(a, r, cnt),
                    ref passed, ref failed, dispose: false);
            }
            finally
            {
                fa.Dispose(); fb.Dispose(); ia.Dispose(); ib.Dispose(); big5.Dispose();
            }
        }

        private static float MixFloat(Random rnd)
        {
            int kind = rnd.Next(8);
            if (kind == 0) return FatalFloats[rnd.Next(FatalFloats.Length)];
            return (float)((rnd.NextDouble() * 400) - 200);
        }
        private static int MixInt(Random rnd)
        {
            int kind = rnd.Next(4);
            if (kind == 0) return FatalInts[rnd.Next(FatalInts.Length)];
            return rnd.Next(-200000, 200000);
        }

        // ════════════════════════════════════════════════════════════════
        // Comparison infrastructure (float bit-exact / int exact / float ULP tolerance)
        // ════════════════════════════════════════════════════════════════
        // ULP 容差：SLEEF 多项式 vs C# libm 允许一定 ULP 差（报告 5.2：~3.5 ULP）。
        // 通过把两个 float 转 int 位模式后比较整数差（忽略符号位误区：需按带符号 ULP 差处理）。
        private static bool CompareUlps(string label, NativeArray<float> c, NativeArray<float> s, int n, int maxUlps, out string detail)
        {
            int firstBad = -1; int badCount = 0; float worstRel = 0;
            for (int i = 0; i < n; i++)
            {
                float a = c[i], b = s[i];
                // NaN: 任意 NaN 位模式视为一致（载荷可能不同）
                if (float.IsNaN(a) && float.IsNaN(b)) continue;
                // ±Inf 必须精确相等
                if (float.IsInfinity(a) || float.IsInfinity(b))
                {
                    if (a == b) continue;
                    if (firstBad < 0) firstBad = i; badCount++; continue;
                }
                if (a == b) continue;
                // ULP 差：同符号时比较相邻整数表示
                int ia = BitConverter.SingleToInt32Bits(a);
                int ib = BitConverter.SingleToInt32Bits(b);
                if (ia == ib) continue;
                if ((ia < 0) != (ib < 0)) { if (firstBad < 0) firstBad = i; badCount++; continue; }
                long diff = Math.Abs((long)ia - (long)ib);
                if (diff > maxUlps) { if (firstBad < 0) firstBad = i; badCount++; continue; }
                // 相对误差监控（仅报告用）
                float rel = Math.Abs(a - b) / Math.Max(Math.Abs(a), 1e-30f);
                if (rel > worstRel) worstRel = rel;
            }
            detail = badCount == 0 ? (worstRel > 0 ? $" (maxRelErr={worstRel:G3})" : "")
                : $"first@{firstBad} cs={BitConverter.SingleToInt32Bits(c[firstBad]):X8} simd={BitConverter.SingleToInt32Bits(s[firstBad]):X8} nbad={badCount}";
            return badCount == 0;
        }

        private static bool CompareBits(string label, NativeArray<float> c, NativeArray<float> s, int n, out string detail)
        {
            int firstBad = -1; int badCount = 0;
            for (int i = 0; i < n; i++)
            {
                int bc = BitConverter.SingleToInt32Bits(c[i]);
                int bs = BitConverter.SingleToInt32Bits(s[i]);
                // NaN: 任意 NaN 位模式视为一致（C# 与 SIMD 的 NaN 载荷可能不同）
                if (float.IsNaN(c[i]) && float.IsNaN(s[i])) continue;
                if (bc != bs) { if (firstBad < 0) firstBad = i; badCount++; }
            }
            detail = badCount == 0 ? "" : $"first@{firstBad} src={BitConverter.SingleToInt32Bits(c[firstBad]):X8} simd={BitConverter.SingleToInt32Bits(s[firstBad]):X8} nbad={badCount}";
            return badCount == 0;
        }

        private static bool CompareInts(string label, NativeArray<int> c, NativeArray<int> s, int n, out string detail)
        {
            int firstBad = -1; int badCount = 0;
            var details = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if (c[i] != s[i])
                {
                    if (firstBad < 0) firstBad = i;
                    badCount++;
                    if (details.Count < 4)
                        details.Add($"@{i}: cs={c[i]} simd={s[i]}");
                }
            }
            detail = badCount == 0 ? "" : $"first@{firstBad} cs={c[firstBad]} simd={s[firstBad]} nbad={badCount} | {string.Join(" ", details)}";
            return badCount == 0;
        }

        // ════════════════════════════════════════════════════════════════
        // XxxPair: 通用模板包装（float 输出）
        // ════════════════════════════════════════════════════════════════
        private static void RunPair(string label, int n,
            Func<int, NativeArray<float>> seedIn,
            Action<NativeArray<float>, NativeArray<float>, int> cSharp,
            Action<NativeArray<float>, NativeArray<float>, int> simd,
            ref int passed, ref int failed, bool dispose = true, int maxUlps = 0)
        {
            var src = seedIn(n);
            var rc = new NativeArray<float>(n, Allocator.Persistent);
            var rs = new NativeArray<float>(n, Allocator.Persistent);
            try
            {
                cSharp(src, rc, n);
                simd(src, rs, n);
                bool ok = maxUlps > 0
                    ? CompareUlps(label, rc, rs, n, maxUlps, out var detail)
                    : CompareBits(label, rc, rs, n, out detail);
                if (ok) { Pass(label); passed++; }
                else { Fail(label + " | " + detail); failed++; }
            }
            catch (Exception ex) { Fail(label + " | EXCEPTION: " + ex.Message); failed++; }
            finally
            {
                if (dispose) src.Dispose();
                rc.Dispose(); rs.Dispose();
            }
        }

        private static void RunPairInt(string label, int n,
            Func<int, NativeArray<int>> seedIn,
            Action<NativeArray<int>, NativeArray<float>, int> cSharp,
            Action<NativeArray<int>, NativeArray<float>, int> simd,
            ref int passed, ref int failed, bool dispose = true)
        {
            var src = seedIn(n);
            var rc = new NativeArray<float>(n, Allocator.Persistent);
            var rs = new NativeArray<float>(n, Allocator.Persistent);
            try
            {
                cSharp(src, rc, n);
                simd(src, rs, n);
                if (CompareBits(label, rc, rs, n, out var detail)) { Pass(label); passed++; }
                else { Fail(label + " | " + detail); failed++; }
            }
            catch (Exception ex) { Fail(label + " | EXCEPTION: " + ex.Message); failed++; }
            finally
            {
                if (dispose) src.Dispose();
                rc.Dispose(); rs.Dispose();
            }
        }

        private static void RunPairIntInt(string label, int n,
            Func<int, NativeArray<int>> seedIn,
            Action<NativeArray<int>, NativeArray<int>, NativeArray<int>, int> cSharp,
            Action<NativeArray<int>, NativeArray<int>, NativeArray<int>, int> simd,
            ref int passed, ref int failed, bool dispose = true)
        {
            var srcA = seedIn(n);
            var srcB = seedIn(n);
            var rc = new NativeArray<int>(n, Allocator.Persistent);
            var rs = new NativeArray<int>(n, Allocator.Persistent);
            // ★ Sentinel-fill SIMD output: distinguishes "lane never written" (retains sentinel)
            //   from "written but wrong value". Persistent allocator memory may be zeroed,
            //   so a skipped write looks identical to a real 0 without this.
            for (int i = 0; i < n; i++) rs[i] = unchecked((int)0x5A5A5A5A);
            try
            {
                cSharp(srcA, srcB, rc, n);
                simd(srcA, srcB, rs, n);
                if (CompareInts(label, rc, rs, n, out var detail)) { Pass(label); passed++; }
                else
                {
                    // ★ Debug: dump the first few mismatches with their INPUTS (A/B).
                    string dbg = "";
                    int shown = 0;
                    for (int k = 0; k < n && shown < 5; k++)
                    {
                        if (rc[k] != rs[k])
                        {
                            dbg += $" @{k}:A={srcA[k]},B={srcB[k]},cs={rc[k]},simd={rs[k]}";
                            shown++;
                        }
                    }
                    Fail(label + " | " + detail + dbg);
                    failed++;
                }
            }
            catch (Exception ex) { Fail(label + " | EXCEPTION: " + ex.Message); failed++; }
            finally
            {
                if (dispose) { srcA.Dispose(); srcB.Dispose(); }
                rc.Dispose(); rs.Dispose();
            }
        }

        private static void RunPairCount(string label, int n,
            Func<int, NativeArray<float>> seedF,
            Func<int, NativeArray<int>> seedC,
            Action<NativeArray<float>, NativeArray<int>, NativeArray<float>, int> cSharp,
            Action<NativeArray<float>, NativeArray<int>, NativeArray<float>, int> simd,
            ref int passed, ref int failed)
        {
            var src = seedF(n);
            var counts = seedC(n);
            var rc = new NativeArray<float>(n, Allocator.Persistent);
            var rs = new NativeArray<float>(n, Allocator.Persistent);
            try
            {
                cSharp(src, counts, rc, n);
                simd(src, counts, rs, n);
                if (CompareBits(label, rc, rs, n, out var detail)) { Pass(label); passed++; }
                else { Fail(label + " | " + detail); failed++; }
            }
            catch (Exception ex) { Fail(label + " | EXCEPTION: " + ex.Message); failed++; }
            finally
            {
                src.Dispose(); counts.Dispose(); rc.Dispose(); rs.Dispose();
            }
        }

        private static void Pass(string label) => Console.WriteLine($"  {label,-70}: ✅");
        private static void Fail(string label) => Console.WriteLine($"  {label,-70}: ❌");

        // ════════════════════════════════════════════════════════════════
        // Input generators
        // ════════════════════════════════════════════════════════════════
        private static NativeArray<float> SeedFatalFloats(int n, int seed)
        {
            var arr = new NativeArray<float>(n, Allocator.Persistent);
            var rnd = new Random(seed);
            for (int i = 0; i < n; i++)
            {
                if (i % 5 == 0) arr[i] = FatalFloats[rnd.Next(FatalFloats.Length)];
                else arr[i] = (float)((rnd.NextDouble() * 400) - 200);
            }
            return arr;
        }
        private static NativeArray<float> SeedFatalBitsAsFloats(int n, int seed)
        {
            var arr = new NativeArray<float>(n, Allocator.Persistent);
            var rnd = new Random(seed);
            // 特殊位模式注入（±Inf / NaN 载荷 / ±0 / 次正规 / FLT_MAX）
            int[] specialBits =
            {
                unchecked((int)0x7F800000), unchecked((int)0xFF800000),           // ±Inf
                unchecked((int)0x7FC00000), unchecked((int)0x7F800001), unchecked((int)0xFFC00000), unchecked((int)0x7FBFFFFF), // NaN 家族
                unchecked((int)0x80000000), 0x00000000,            // -0 / +0
                0x00000001, unchecked((int)0x80000001),            // ±次正规最小
                0x7F7FFFFF, unchecked((int)0xFF7FFFFF),            // ±FLT_MAX
                0x00800000, unchecked((int)(0x80000000 + 0x00800000)), // ±FLT_MIN
                0x3F800000, 0x40000000,            // 1.0 / 2.0
            };
            for (int i = 0; i < n; i++)
            {
                if (i % 5 == 0) arr[i] = BitConverter.Int32BitsToSingle(specialBits[rnd.Next(specialBits.Length)]);
                else arr[i] = (float)((rnd.NextDouble() * 400) - 200);
            }
            return arr;
        }
        private static NativeArray<int> SeedFatalInts(int n, int seed)
        {
            var arr = new NativeArray<int>(n, Allocator.Persistent);
            var rnd = new Random(seed);
            for (int i = 0; i < n; i++)
            {
                if (i % 4 == 0) arr[i] = FatalInts[rnd.Next(FatalInts.Length)];
                else arr[i] = rnd.Next(-200000, 200000);
            }
            return arr;
        }
        private static NativeArray<int> SeedCounts(int n, int seed)
        {
            var arr = new NativeArray<int>(n, Allocator.Persistent);
            var rnd = new Random(seed);
            for (int i = 0; i < n; i++) arr[i] = rnd.Next(0, 64);
            return arr;
        }
        private static NativeArray<int> SeedAllMinInt(int n)
        {
            var arr = new NativeArray<int>(n, Allocator.Persistent);
            for (int i = 0; i < n; i++) arr[i] = int.MinValue;
            return arr;
        }

        // ════════════════════════════════════════════════════════════════
        // Case execution wrapper (C# baseline / SIMD)
        // ════════════════════════════════════════════════════════════════
        static void pc1(NativeArray<float> a, NativeArray<float> r, int n)
        { var j = new EdgeCase1_CSharp_For { A = a, R = r }; for (int i = 0; i < n; i++) j.Execute(i); }
        static void sc1(NativeArray<float> a, NativeArray<float> r, int n)
        { new EdgeCase1_SIMD_For { A = a, R = r }.Schedule(n, 0).Complete(); }

        static void pc2(NativeArray<float> a, NativeArray<float> r, int n)
        { var j = new EdgeCase2_CSharp_For { A = a, R = r }; for (int i = 0; i < n; i++) j.Execute(i); }
        static void sc2(NativeArray<float> a, NativeArray<float> r, int n)
        { new EdgeCase2_SIMD_For { A = a, R = r }.Schedule(n, 0).Complete(); }

        static void pc3(NativeArray<int> a, NativeArray<int> b, NativeArray<int> r, int n)
        { var j = new EdgeCase3_CSharp_For { A = a, B = b, R = r }; for (int i = 0; i < n; i++) j.Execute(i); }
        static void sc3(NativeArray<int> a, NativeArray<int> b, NativeArray<int> r, int n)
        { new EdgeCase3_SIMD_For { A = a, B = b, R = r }.Schedule(n, 0).Complete(); }

        static void pc4(NativeArray<float> a, NativeArray<float> r, int n)
        { var j = new EdgeCase4_CSharp_For { A = a, R = r }; for (int i = 0; i < n; i++) j.Execute(i); }
        static void sc4(NativeArray<float> a, NativeArray<float> r, int n)
        { new EdgeCase4_SIMD_For { A = a, R = r }.Schedule(n, 0).Complete(); }

        static void pc5(NativeArray<float> a, NativeArray<float> r, int n)
        { var j = new EdgeCase5_CSharp_For { A = a, R = r }; for (int i = 0; i < n; i++) j.Execute(i); }
        static void sc5(NativeArray<float> a, NativeArray<float> r, int n)
        { new EdgeCase5_SIMD_For { A = a, R = r }.Schedule(n, 0).Complete(); }

        static void pc6(NativeArray<float> a, NativeArray<int> c, NativeArray<float> r, int n)
        { var j = new EdgeCase6_CSharp_For { A = a, Counts = c, R = r }; for (int i = 0; i < n; i++) j.Execute(i); }
        static void sc6(NativeArray<float> a, NativeArray<int> c, NativeArray<float> r, int n)
        { new EdgeCase6_SIMD_For { A = a, Counts = c, R = r }.Schedule(n, 0).Complete(); }

        static void pc7(NativeArray<float> a, NativeArray<float> r, int n)
        { var j = new EdgeCase7_CSharp_For { A = a, R = r }; for (int i = 0; i < n; i++) j.Execute(i); }
        static void sc7(NativeArray<float> a, NativeArray<float> r, int n)
        { new EdgeCase7_SIMD_For { A = a, R = r }.Schedule(n, 0).Complete(); }

        static void pc8(NativeArray<float> a, NativeArray<float> r, int n)
        { var j = new EdgeCase8_CSharp_For { A = a, R = r }; for (int i = 0; i < n; i++) j.Execute(i); }
        static void sc8(NativeArray<float> a, NativeArray<float> r, int n)
        { new EdgeCase8_SIMD_For { A = a, R = r }.Schedule(n, 0).Complete(); }

        static void pc9(NativeArray<float> a, NativeArray<float> r, int n)
        { var j = new EdgeCase9_CSharp_For { A = a, R = r }; for (int i = 0; i < n; i++) j.Execute(i); }
        static void sc9(NativeArray<float> a, NativeArray<float> r, int n)
        { new EdgeCase9_SIMD_For { A = a, R = r }.Schedule(n, 0).Complete(); }

        static void pc10(NativeArray<int> a, NativeArray<int> r, int n)
        { var j = new EdgeCase10_CSharp_For { A = a, R = r }; for (int i = 0; i < n; i++) j.Execute(i); }
        static void sc10(NativeArray<int> a, NativeArray<int> r, int n)
        { new EdgeCase10_SIMD_For { A = a, R = r }.Schedule(n, 0).Complete(); }
    }
}