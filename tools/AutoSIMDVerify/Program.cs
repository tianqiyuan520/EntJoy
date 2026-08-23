// AutoSIMD 正确性验证：对比每个 Case 的 C# 基线 与 AutoSIMD native 输出。
// 依赖 EntJoySample 编译链（NativeExports 绑定已生成）。
using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler.Bindings;

namespace AutoSIMDVerify
{
    public static class Program
    {
        private const int N = 4093;          // 非 8 倍数 → 触发 remainder 标量回退路径（4096 会掩盖）
        private const int SUB = 100;         // 子索引规模（Reduce/Gather）

        public static void Main()
        {
            NativeJobScheduler.Initialize();
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== AutoSIMD Correctness Verification (C# baseline vs AutoSIMD native) ===\n");

            int passed = 0, failed = 0;

            try
            {
                // ── Case 1: SimpleArith A*B+C ──
                var a1 = RandF(N, 42); var b1 = RandF(N, 43); var c1 = RandF(N, 44);
                var r1c = new NativeArray<float>(N, Allocator.Persistent);
                var r1s = new NativeArray<float>(N, Allocator.Persistent);
                EntJoySample.AutoSIMDTest.SimpleArith_StaticFuncs.SimpleArith_Stc_CSharp(a1, b1, c1, r1c, N);
                NativeExports.SimpleArith_Stc_SIMD(a1, b1, c1, r1s, N);
                if (Compare("S1 SimpleArith A*B+C", r1c, r1s, N)) passed++; else failed++;
                // Case1 非 Static 变体（IJob / IJobFor / IJobParallelFor）—— 走 OuterSimdGenerator jobStruct 路径
                {
                    var jcF = new NativeArray<float>(N, Allocator.Persistent);
                    var jsF = new NativeArray<float>(N, Allocator.Persistent);
                    var p1 = new EntJoySample.AutoSIMDTest.SimpleArith_CSharp_For { A = a1, B = b1, C = c1, Result = jcF };
                    for (int i = 0; i < N; i++) p1.Execute(i);
                    var q1 = new EntJoySample.AutoSIMDTest.SimpleArith_SIMD_For { A = a1, B = b1, C = c1, Result = jsF };
                    q1.Schedule(N, 0).Complete();
                    if (VerifyPair("S1 For   A*B+C", jcF, jsF, N)) passed++; else failed++;

                    var jcP = new NativeArray<float>(N, Allocator.Persistent);
                    var jsP = new NativeArray<float>(N, Allocator.Persistent);
                    var p2 = new EntJoySample.AutoSIMDTest.SimpleArith_CSharp_PF { A = a1, B = b1, C = c1, Result = jcP };
                    for (int i = 0; i < N; i++) p2.Execute(i);
                    var q2 = new EntJoySample.AutoSIMDTest.SimpleArith_SIMD_PF { A = a1, B = b1, C = c1, Result = jsP };
                    q2.Schedule(N, 0).Complete();
                    if (VerifyPair("S1 PF    A*B+C", jcP, jsP, N)) passed++; else failed++;

                    var jcJ = new NativeArray<float>(N, Allocator.Persistent);
                    var jsJ = new NativeArray<float>(N, Allocator.Persistent);
                    var p3 = new EntJoySample.AutoSIMDTest.SimpleArith_CSharp_Job { A = a1, B = b1, C = c1, Result = jcJ, Count = N };
                    p3.Execute();
                    var q3 = new EntJoySample.AutoSIMDTest.SimpleArith_SIMD_Job { A = a1, B = b1, C = c1, Result = jsJ, Count = N };
                    q3.Execute();
                    if (VerifyPair("S1 Job   A*B+C", jcJ, jsJ, N)) passed++; else failed++;
                    jcF.Dispose(); jsF.Dispose(); jcP.Dispose(); jsP.Dispose(); jcJ.Dispose(); jsJ.Dispose();
                }
                a1.Dispose(); b1.Dispose(); c1.Dispose(); r1c.Dispose(); r1s.Dispose();

                // ── Case 2: MathFuncs sqrt+sin*cos+log ──
                var a2 = RandF(N, 52, positive: true);
                var r2c = new NativeArray<float>(N, Allocator.Persistent);
                var r2s = new NativeArray<float>(N, Allocator.Persistent);
                var r2cpp = new NativeArray<float>(N, Allocator.Persistent);
                EntJoySample.AutoSIMDTest.MathFuncs_StaticFuncs.MathFuncs_Stc_CSharp(a2, r2c, N);
                EntJoySample.AutoSIMDTest.MathFuncs_StaticFuncs.MathFuncs_Stc_Cpp(a2, r2cpp, N);
                NativeExports.MathFuncs_Stc_SIMD(a2, r2s, N);
                // 打印误差最大的样本，定位数学函数实现问题
                {
                    float maxD2 = 0; int maxIdx2 = -1;
                    for (int i = 0; i < N; i++)
                    {
                        float d = Math.Abs(r2c[i] - r2s[i]);
                        if (d > maxD2) { maxD2 = d; maxIdx2 = i; }
                    }
                    if (maxD2 > 0)
                        Console.WriteLine($"    [S2] x={a2[maxIdx2]:G6} cs={r2c[maxIdx2]:G9} simd={r2s[maxIdx2]:G9} | sqrt={MathF.Sqrt(a2[maxIdx2]):G6} sin*cos={MathF.Sin(a2[maxIdx2])*MathF.Cos(a2[maxIdx2]):G6} log={MathF.Log(a2[maxIdx2]+1):G6}");
                }
                // Cpp 标量对照（C++ libm）vs C# 基线：确认 baseline 一致
                Compare("S2 MathFuncs Cpp-vs-C#", r2cpp, r2c, N);
                if (Compare("S2 MathFuncs sqrt+sin*cos+log", r2c, r2s, N)) passed++; else failed++;
                // S2 For 变体
                {
                    var f2c = new NativeArray<float>(N, Allocator.Persistent);
                    var f2s = new NativeArray<float>(N, Allocator.Persistent);
                    var pf = new EntJoySample.AutoSIMDTest.MathFuncs_CSharp_For { A = a2, Result = f2c };
                    for (int i = 0; i < N; i++) pf.Execute(i);
                    var qf = new EntJoySample.AutoSIMDTest.MathFuncs_SIMD_For { A = a2, Result = f2s };
                    qf.Schedule(N, 0).Complete();
                    if (VerifyPair("S2 For   sqrt+sin*cos+log", f2c, f2s, N)) passed++; else failed++;
                    f2c.Dispose(); f2s.Dispose();
                }
                a2.Dispose(); r2c.Dispose(); r2s.Dispose(); r2cpp.Dispose();

                // ── Case 3: SimpleReduce min-over-100 ──
                var a3 = RandF(N * SUB, 62);
                var r3c = new NativeArray<float>(N, Allocator.Persistent);
                var r3s = new NativeArray<float>(N, Allocator.Persistent);
                EntJoySample.AutoSIMDTest.SimpleReduce_StaticFuncs.SimpleReduce_Stc_CSharp(a3, r3c, N);
                NativeExports.SimpleReduce_Stc_SIMD(a3, r3s, N);
                if (Compare("S3 SimpleReduce min/j100", r3c, r3s, N)) passed++; else failed++;
                // S3 For 变体（reduction → count-loop jobStruct 路径）
                {
                    var f3c = new NativeArray<float>(N, Allocator.Persistent);
                    var f3s = new NativeArray<float>(N, Allocator.Persistent);
                    var pf3 = new EntJoySample.AutoSIMDTest.SimpleReduce_CSharp_For { A = a3, Result = f3c };
                    for (int i = 0; i < N; i++) pf3.Execute(i);
                    var qf3 = new EntJoySample.AutoSIMDTest.SimpleReduce_SIMD_For { A = a3, Result = f3s };
                    qf3.Schedule(N, 0).Complete();
                    if (VerifyPair("S3 For   min/j100", f3c, f3s, N)) passed++; else failed++;
                    f3c.Dispose(); f3s.Dispose();
                }
                a3.Dispose(); r3c.Dispose(); r3s.Dispose();

                // ── Case 4: ComplexFlow if/else-if/else ──
                var a4 = RandF(N, 72); var b4 = RandF(N, 73);
                var r4c = new NativeArray<float>(N, Allocator.Persistent);
                var r4s = new NativeArray<float>(N, Allocator.Persistent);
                EntJoySample.AutoSIMDTest.ComplexFlow_StaticFuncs.ComplexFlow_Stc_CSharp(a4, b4, r4c, 0.5f, N);
                NativeExports.ComplexFlow_Stc_SIMD(a4, b4, r4s, 0.5f, N);
                if (Compare("S4 ComplexFlow if/elif/else", r4c, r4s, N)) passed++; else failed++;
                // S4 非 Static 变体（jobStruct 路径的分支+数组写）
                {
                    var j4c = new NativeArray<float>(N, Allocator.Persistent);
                    var j4s = new NativeArray<float>(N, Allocator.Persistent);
                    var p4 = new EntJoySample.AutoSIMDTest.ComplexFlow_CSharp_For { A = a4, B = b4, Result = j4c, Threshold = 0.5f };
                    for (int i = 0; i < N; i++) p4.Execute(i);
                    var q4 = new EntJoySample.AutoSIMDTest.ComplexFlow_SIMD_For { A = a4, B = b4, Result = j4s, Threshold = 0.5f };
                    q4.Schedule(N, 0).Complete();
                    if (VerifyPair("S4 For   if/elif/else", j4c, j4s, N)) passed++; else failed++;

                    var j4p = new NativeArray<float>(N, Allocator.Persistent);
                    var j4ps = new NativeArray<float>(N, Allocator.Persistent);
                    var p4b = new EntJoySample.AutoSIMDTest.ComplexFlow_CSharp_PF { A = a4, B = b4, Result = j4p, Threshold = 0.5f };
                    for (int i = 0; i < N; i++) p4b.Execute(i);
                    var q4b = new EntJoySample.AutoSIMDTest.ComplexFlow_SIMD_PF { A = a4, B = b4, Result = j4ps, Threshold = 0.5f };
                    q4b.Schedule(N, 0).Complete();
                    if (VerifyPair("S4 PF    if/elif/else", j4p, j4ps, N)) passed++; else failed++;
                    j4c.Dispose(); j4s.Dispose(); j4p.Dispose(); j4ps.Dispose();
                }
                a4.Dispose(); b4.Dispose(); r4c.Dispose(); r4s.Dispose();

                // ── Case 5: GatherReduce gather + min ──
                var qx = RandF(N, 82); var qy = RandF(N, 83);
                var dx = RandF(SUB, 84); var dy = RandF(SUB, 85);
                var idx = new NativeArray<int>(N * 50, Allocator.Persistent);
                var rnd = new Random(86);
                for (int i = 0; i < N * 50; i++) idx[i] = rnd.Next(SUB);
                var r5c = new NativeArray<float>(N, Allocator.Persistent);
                var r5s = new NativeArray<float>(N, Allocator.Persistent);
                EntJoySample.AutoSIMDTest.GatherReduce_StaticFuncs.GatherReduce_Stc_CSharp(qx, qy, dx, dy, idx, r5c, N);
                NativeExports.GatherReduce_Stc_SIMD(qx, qy, dx, dy, idx, r5s, N);
                if (Compare("S5 GatherReduce gather+min", r5c, r5s, N)) passed++; else failed++;
                // S5 For 变体（gather + reduction jobStruct 路径）
                {
                    var f5c = new NativeArray<float>(N, Allocator.Persistent);
                    var f5s = new NativeArray<float>(N, Allocator.Persistent);
                    var pf5 = new EntJoySample.AutoSIMDTest.GatherReduce_CSharp_For { QueryX = qx, QueryY = qy, DataX = dx, DataY = dy, Index = idx, Result = f5c };
                    for (int i = 0; i < N; i++) pf5.Execute(i);
                    var qf5 = new EntJoySample.AutoSIMDTest.GatherReduce_SIMD_For { QueryX = qx, QueryY = qy, DataX = dx, DataY = dy, Index = idx, Result = f5s };
                    qf5.Schedule(N, 0).Complete();
                    if (VerifyPair("S5 For   gather+min", f5c, f5s, N)) passed++; else failed++;
                    f5c.Dispose(); f5s.Dispose();
                }
                qx.Dispose(); qy.Dispose(); dx.Dispose(); dy.Dispose(); idx.Dispose(); r5c.Dispose(); r5s.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠ CRASH: {ex.Message}");
                var inner = ex.InnerException;
                while (inner != null)
                {
                    Console.WriteLine($"    └─ {inner.GetType().Name}: {inner.Message}");
                    inner = inner.InnerException;
                }
                Console.WriteLine(ex.StackTrace);
                failed++;
            }

            // ═══════════ 对抗性压力测试（嵌套×分支×循环×多变量）═══════════
            RunStress(passed: ref passed, failed: ref failed);

            Console.WriteLine($"\n{'=',-1}{"",-1} PASS {passed} / {passed + failed}");
            Console.WriteLine(passed > 0 && failed == 0 ? "✅ ALL AutoSIMD cases correct." : "❌ AutoSIMD has FAILING cases!");
            Console.WriteLine();

            NativeJobScheduler.Shutdown();
        }

        private static NativeArray<float> RandF(int n, int seed, bool positive = false)
        {
            var rnd = new Random(seed);
            var arr = new NativeArray<float>(n, Allocator.Persistent);
            for (int i = 0; i < n; i++)
            {
                float v = positive ? (float)(rnd.NextDouble() * 100.0 + 1.0)
                                   : (float)(rnd.NextDouble() * 200.0 - 100.0);
                arr[i] = v;
            }
            return arr;
        }

        private static bool Compare(string name, NativeArray<float> a, NativeArray<float> b, int n)
        {
            int errs = 0, first = -1;
            float maxDelta = 0f;
            for (int i = 0; i < n; i++)
            {
                float da = a[i], db = b[i];
                float delta = Math.Abs(da - db);
                if (delta > maxDelta) maxDelta = delta;
                // 容差：fast-math 下 sin/cos/log 精度差异，绝对 1e-3
                if (delta > 1e-3f)
                {
                    errs++;
                    if (first < 0) first = i;
                }
            }
            Console.WriteLine($"  {name,-28}: {(errs == 0 ? "✅ PASS" : $"❌ FAIL ({errs}/{n} errors, first@{first}: cs={a[first]:G9} simd={b[first]:G9} maxΔ={maxDelta:G4}")}");
            return errs == 0;
        }

        /// <summary>对比一个 job 变体的 C# 基线 与 AutoSIMD 变体</summary>
        private static bool VerifyPair(string name, NativeArray<float> cs, NativeArray<float> simd, int n)
        {
            int errs = 0, first = -1; float maxDelta = 0;
            for (int i = 0; i < n; i++)
            {
                float d = Math.Abs(cs[i] - simd[i]);
                if (d > maxDelta) maxDelta = d;
                if (d > 1e-3f) { errs++; if (first < 0) first = i; }
            }
            Console.WriteLine($"  {name,-28}: {(errs == 0 ? "✅ PASS" : $"❌ FAIL ({errs}/{n} errors, first@{first}: cs={cs[first]:G9} simd={simd[first]:G9} maxΔ={maxDelta:G4}")}");
            return errs == 0;
        }

        /// <summary>int 数组对比</summary>
        private static bool VerifyPairInt(string name, NativeArray<int> cs, NativeArray<int> simd, int n)
        {
            int errs = 0, first = -1; long maxDelta = 0;
            for (int i = 0; i < n; i++)
            {
                long d = Math.Abs((long)cs[i] - simd[i]);
                if (d > maxDelta) maxDelta = d;
                if (d != 0) { errs++; if (first < 0) first = i; }
            }
            Console.WriteLine($"  {name,-28}: {(errs == 0 ? "✅ PASS" : $"❌ FAIL ({errs}/{n} errors, first@{first}: cs={cs[first]} simd={simd[first]} maxΔ={maxDelta:###}")}");
            return errs == 0;
        }

        /// <summary>专用：float 目标数组 + int 基线数组对比（Stress8 返回 int 存入 float 槽）</summary>
        private static bool VerifyPair1F1I(string name, NativeArray<float> cs, NativeArray<float> simd, int n, NativeArray<int> refInt)
        {
            int errs = 0, first = -1;
            for (int i = 0; i < n; i++)
            {
                if ((int)cs[i] != (int)simd[i]) { errs++; if (first < 0) first = i; }
            }
            Console.WriteLine($"  {name,-28}: {(errs == 0 ? "✅ PASS" : $"❌ FAIL ({errs}/{n} errors, first@{first}: cs={(int)cs[first]} simd={(int)simd[first]}")}");
            return errs == 0;
        }

        /// <summary>对抗性压力测试：嵌套 × 分支 × 循环 × 多变量</summary>
        private static void RunStress(ref int passed, ref int failed)
        {
            Console.WriteLine("  --- Stress (nested × branch × loop × multi-var) ---");
            int NS = 2045; // 非 8 倍数

            // ST1 深层 if-else-if 链
            {
                var a = RandF(NS, 101); var rc = new NativeArray<float>(NS, Allocator.Persistent); var rs = new NativeArray<float>(NS, Allocator.Persistent);
                var pc = new EntJoySample.AutoSIMDTest.Stress1_CSharp_For { A = a, R = rc };
                for (int i = 0; i < NS; i++) pc.Execute(i);
                new EntJoySample.AutoSIMDTest.Stress1_SIMD_For { A = a, R = rs }.Schedule(NS, 0).Complete();
                if (VerifyPair("ST1 5-way if/elif chain", rc, rs, NS)) passed++; else failed++;
                a.Dispose(); rc.Dispose(); rs.Dispose();
            }
            // ST2 多变量分支写
            {
                var a = RandF(NS, 102); var b = RandF(NS, 103);
                var rc = new NativeArray<float>(NS, Allocator.Persistent); var rs = new NativeArray<float>(NS, Allocator.Persistent);
                var pc = new EntJoySample.AutoSIMDTest.Stress2_CSharp_For { A = a, B = b, R = rc };
                for (int i = 0; i < NS; i++) pc.Execute(i);
                new EntJoySample.AutoSIMDTest.Stress2_SIMD_For { A = a, B = b, R = rs }.Schedule(NS, 0).Complete();
                if (VerifyPair("ST2 3-var branch write", rc, rs, NS)) passed++; else failed++;
                a.Dispose(); b.Dispose(); rc.Dispose(); rs.Dispose();
            }
            // ST3 内层循环+分支
            {
                var a = RandF(NS * 100, 104);
                var rc = new NativeArray<float>(NS, Allocator.Persistent); var rs = new NativeArray<float>(NS, Allocator.Persistent);
                var pc = new EntJoySample.AutoSIMDTest.Stress3_CSharp_For { A = a, R = rc };
                for (int i = 0; i < NS; i++) pc.Execute(i);
                new EntJoySample.AutoSIMDTest.Stress3_SIMD_For { A = a, R = rs }.Schedule(NS, 0).Complete();
                if (VerifyPair("ST3 loop+parity branch", rc, rs, NS)) passed++; else failed++;
                a.Dispose(); rc.Dispose(); rs.Dispose();
            }
            // ST4 分支内 reduction + 外部分支
            {
                var a = RandF(NS * 50, 105);
                var rc = new NativeArray<float>(NS, Allocator.Persistent); var rs = new NativeArray<float>(NS, Allocator.Persistent);
                var pc = new EntJoySample.AutoSIMDTest.Stress4_CSharp_For { A = a, R = rc };
                for (int i = 0; i < NS; i++) pc.Execute(i);
                new EntJoySample.AutoSIMDTest.Stress4_SIMD_For { A = a, R = rs }.Schedule(NS, 0).Complete();
                if (VerifyPair("ST4 reduce+branch", rc, rs, NS)) passed++; else failed++;
                a.Dispose(); rc.Dispose(); rs.Dispose();
            }
            // ST5 嵌套循环+嵌套分支（3x3 邻域）
            {
                var a = RandF(64 * 64, 106);
                var rc = new NativeArray<float>(NS, Allocator.Persistent); var rs = new NativeArray<float>(NS, Allocator.Persistent);
                var pc = new EntJoySample.AutoSIMDTest.Stress5_CSharp_For { A = a, R = rc };
                for (int i = 0; i < NS; i++) pc.Execute(i);
                new EntJoySample.AutoSIMDTest.Stress5_SIMD_For { A = a, R = rs }.Schedule(NS, 0).Complete();
                if (VerifyPair("ST5 nested loop+branch", rc, rs, NS)) passed++; else failed++;
                a.Dispose(); rc.Dispose(); rs.Dispose();
            }
            // ST6 多 int 变量 + 位运算分支
            {
                var a = RandI(NS, 107); var b = RandI(NS, 108);
                var rc = new NativeArray<int>(NS, Allocator.Persistent); var rs = new NativeArray<int>(NS, Allocator.Persistent);
                var pc = new EntJoySample.AutoSIMDTest.Stress6_CSharp_For { A = a, B = b, R = rc };
                for (int i = 0; i < NS; i++) pc.Execute(i);
                new EntJoySample.AutoSIMDTest.Stress6_SIMD_For { A = a, B = b, R = rs }.Schedule(NS, 0).Complete();
                if (VerifyPairInt("ST6 int bitwise branch", rc, rs, NS)) passed++; else failed++;
                a.Dispose(); b.Dispose(); rc.Dispose(); rs.Dispose();
            }
            // ST7 min+max 双 reduction + 分支
            {
                var a = RandF(NS * 64, 109);
                var rc = new NativeArray<float>(NS, Allocator.Persistent); var rs = new NativeArray<float>(NS, Allocator.Persistent);
                var pc = new EntJoySample.AutoSIMDTest.Stress7_CSharp_For { A = a, R = rc };
                for (int i = 0; i < NS; i++) pc.Execute(i);
                new EntJoySample.AutoSIMDTest.Stress7_SIMD_For { A = a, R = rs }.Schedule(NS, 0).Complete();
                if (VerifyPair("ST7 min+max reduce", rc, rs, NS)) passed++; else failed++;
                a.Dispose(); rc.Dispose(); rs.Dispose();
            }
            // ST8 break 提前退出
            {
                var a = RandF(NS * 100, 110);
                var rc = new NativeArray<int>(NS, Allocator.Persistent); var rs = new NativeArray<int>(NS, Allocator.Persistent);
                var pc = new EntJoySample.AutoSIMDTest.Stress8_CSharp_For { A = a, R = rc };
                for (int i = 0; i < NS; i++) pc.Execute(i);
                new EntJoySample.AutoSIMDTest.Stress8_SIMD_For { A = a, R = rs }.Schedule(NS, 0).Complete();
                if (VerifyPairInt("ST8 break early-exit", rc, rs, NS)) passed++; else failed++;
                a.Dispose(); rc.Dispose(); rs.Dispose();
            }
            // ST9 uint 混合比较
            {
                var a = RandI(NS, 111);
                var rc = new NativeArray<int>(NS, Allocator.Persistent); var rs = new NativeArray<int>(NS, Allocator.Persistent);
                var pc = new EntJoySample.AutoSIMDTest.Stress9_CSharp_For { A = a, R = rc };
                for (int i = 0; i < NS; i++) pc.Execute(i);
                new EntJoySample.AutoSIMDTest.Stress9_SIMD_For { A = a, R = rs }.Schedule(NS, 0).Complete();
                if (VerifyPairInt("ST9 uint cmp+shift", rc, rs, NS)) passed++; else failed++;
                a.Dispose(); rc.Dispose(); rs.Dispose();
            }
            // ST10 gather + 分支累加
            {
                var q = RandF(NS, 112); var d = RandF(256, 113);
                var idx = new NativeArray<int>(NS * 32, Allocator.Persistent);
                var rnd = new Random(114);
                for (int i = 0; i < NS * 32; i++) idx[i] = rnd.Next(256);
                var rc = new NativeArray<float>(NS, Allocator.Persistent); var rs = new NativeArray<float>(NS, Allocator.Persistent);
                var pc = new EntJoySample.AutoSIMDTest.Stress10_CSharp_For { Q = q, D = d, Idx = idx, R = rc };
                for (int i = 0; i < NS; i++) pc.Execute(i);
                new EntJoySample.AutoSIMDTest.Stress10_SIMD_For { Q = q, D = d, Idx = idx, R = rs }.Schedule(NS, 0).Complete();
                if (VerifyPair("ST10 gather+branch", rc, rs, NS)) passed++; else failed++;
                q.Dispose(); d.Dispose(); idx.Dispose(); rc.Dispose(); rs.Dispose();
            }
        }

        private static NativeArray<int> RandI(int n, int seed)
        {
            var rnd = new Random(seed);
            var arr = new NativeArray<int>(n, Allocator.Persistent);
            for (int i = 0; i < n; i++) arr[i] = rnd.Next(-100000, 100000);
            return arr;
        }
    }
}