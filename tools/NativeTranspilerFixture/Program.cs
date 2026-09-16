using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspilerFixture;

// ─────────────────────────────────────────────────────────────────────────────
// NativeTranspiler 生成器回归夹具（F-4 / F-5 / F-10）
//
// 断言表（全部为"生成代码的运行时语义"断言，不是"能不能编译"）：
//   [1] BatchFillJob 单批（batchSize = N）：Visits[i] 恰好 == 1，Out[i] == N
//   [2] BatchFillJob 多批（batchSize = 64）：Visits[i] 恰好 == 1，Out[i] == 该批实际长度
//   [3] EarlyReturnJob 单批：Mark[0] == 0，且 Mark[i] == i+1 (i>=1)
//       ← 旧实现：批内 `return;` 退出整个批 ⇒ [1..N-1] 全为 0
//   [4] EarlyReturnDeepJob：Before[i] == 1 全部；After[i] == 2 仅当 i%3 != 0
//
// 退出码：0 = 全部通过；1 = 有断言失败。
// ─────────────────────────────────────────────────────────────────────────────

const int N = 1024;
int failed = 0;

NativeJobScheduler.Initialize();
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== NativeTranspiler fixture: generated-code semantics (F-4 / F-5) ===");

// ── [1] BatchFillJob 单批：batchSize == N ──
{
    var outN = new NativeArray<int>(N, Allocator.Persistent);
    var vis = new NativeArray<int>(N, Allocator.Persistent);
    var job = new BatchFillJob { Out = outN, Visits = vis };
    job.Schedule(N, N).Complete();

    int badVisits = 0, badOut = 0;
    for (int i = 0; i < N; i++)
    {
        if (vis[i] != 1) badVisits++;
        if (outN[i] != N) badOut++;
    }
    Report("[1] BatchFillJob single batch", badVisits == 0 && badOut == 0,
        $"visits!=1: {badVisits}, out!=N: {badOut}");

    outN.Dispose(); vis.Dispose();
}

// ── [2] BatchFillJob 多批：batchSize == 64（边界批长度不同）──
{
    const int batch = 64;
    var outN = new NativeArray<int>(N, Allocator.Persistent);
    var vis = new NativeArray<int>(N, Allocator.Persistent);
    var job = new BatchFillJob { Out = outN, Visits = vis };
    job.Schedule(N, batch).Complete();

    int badVisits = 0, badOut = 0;
    for (int i = 0; i < N; i++)
    {
        int expect = Math.Min(batch, N - (i / batch) * batch);
        if (vis[i] != 1) badVisits++;
        if (outN[i] != expect) badOut++;
    }
    Report("[2] BatchFillJob multi batch (64)", badVisits == 0 && badOut == 0,
        $"visits!=1: {badVisits}, out!=batchLen: {badOut}");

    outN.Dispose(); vis.Dispose();
}

// ── [3] EarlyReturnJob：批内 return 只结束本次 index ──
{
    var mark = new NativeArray<int>(N, Allocator.Persistent);
    var job = new EarlyReturnJob { Mark = mark };
    job.Schedule(N, N).Complete();

    int bad = 0, firstBad = -1;
    for (int i = 0; i < N; i++)
    {
        int expect = i == 0 ? 0 : i + 1;
        if (mark[i] != expect) { bad++; if (firstBad < 0) firstBad = i; }
    }
    Report("[3] IJobParallelFor return; skips only its own index", bad == 0,
        bad == 0 ? "" : $"mismatch {bad}/{N}, first@{firstBad}: got={mark[firstBad]} want={(firstBad == 0 ? 0 : firstBad + 1)}");

    mark.Dispose();
}

// ── [4] EarlyReturnDeepJob：return 前后语句的可见性 ──
{
    var before = new NativeArray<int>(N, Allocator.Persistent);
    var after = new NativeArray<int>(N, Allocator.Persistent);
    var job = new EarlyReturnDeepJob { Before = before, After = after };
    job.Schedule(N, N).Complete();

    int badBefore = 0, badAfter = 0, firstBad = -1;
    for (int i = 0; i < N; i++)
    {
        if (before[i] != 1) badBefore++;
        int expect = i % 3 == 0 ? 0 : 2;
        if (after[i] != expect) { badAfter++; if (firstBad < 0) firstBad = i; }
    }
    Report("[4] statements before/after return;", badBefore == 0 && badAfter == 0,
        $"before!=1: {badBefore}, after!=expect: {badAfter}" + (firstBad >= 0 ? $" first@{firstBad}" : ""));

    before.Dispose(); after.Dispose();
}

if (args.Length > 0 && args[0] == "bench")
{
    Bench.Run();          // 必须早于 Shutdown：基准要用真实调度器
}

NativeJobScheduler.Shutdown();

Console.WriteLine(failed == 0 ? "\nPASS: all fixture assertions hold." : $"\nFAIL: {failed} assertion(s) failed.");
return failed == 0 ? 0 : 1;

void Report(string name, bool ok, string detail)
{
    if (!ok) failed++;
    Console.WriteLine($"  {name,-52}: {(ok ? "✅ PASS" : "❌ FAIL")}{(ok || detail.Length == 0 ? "" : "  " + detail)}");
}

// ═══════════════════════════════════════════════════════════════════════════
// 代码生成质量闸门（`dotnet run -c Release -- bench`）
//
// 目的：把"生成的内核"从框架与仿真里剥出来，按**每元素**计量，使"改发射规则 ⇒ 每元素
// 成本下降"可判定。判据是 ns/元素（同机、同编译标志、同输入），不是端到端墙钟。
// 输入为确定性伪随机（LCG），三臂共享同一份输入与同一套输出数组 ⇒ 可直接比校验和。
// ═══════════════════════════════════════════════════════════════════════════
static class Bench
{
    const int N = 1_000_000;
    const int CellsW = 784;
    const int CellsH = 448;
    const int Cells = CellsW * CellsH;

    static uint _seed = 12345;
    static uint NextU32() { _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5; return _seed; }
    static float Next01() => (NextU32() >> 8) * (1.0f / 16777216.0f);

    public static void Run()
    {
        Console.WriteLine($"\n=== codegen bench: N={N} cells={Cells} (ns/element; deterministic input) ===");

        var pos = new NativeArray<EntJoy.Mathematics.float2>(N, Allocator.Persistent);
        var alive = new NativeArray<byte>(N, Allocator.Persistent);
        var state = new NativeArray<int>(N, Allocator.Persistent);
        var cfg = new NativeArray<int>(N, Allocator.Persistent);
        var team = new NativeArray<byte>(N, Allocator.Persistent);
        var counts = new NativeArray<int>(Cells + 1, Allocator.Persistent);
        var cellStart = new NativeArray<int>(Cells + 1, Allocator.Persistent);
        var sorted = new NativeArray<int>(N, Allocator.Persistent);
        var scanOrder = new NativeArray<int>(81, Allocator.Persistent);
        var kd2 = new NativeArray<float>(N * 8, Allocator.Persistent);
        var kpeer = new NativeArray<int>(N * 8, Allocator.Persistent);

        // ── 确定性输入：位置散布在战场 AABB 内（近似实测 3.5 单位/格）──
        const float halfW = 700f, halfH = 400f;
        for (int i = 0; i < N; i++)
        {
            pos[i] = new EntJoy.Mathematics.float2((Next01() * 2f - 1f) * halfW, (Next01() * 2f - 1f) * halfH);
            bool dead = NextU32() % 33 == 0;
            alive[i] = dead ? (byte)0 : (byte)1;
            state[i] = dead ? 3 : 0;
            cfg[i] = (int)(NextU32() % 4);
            team[i] = (byte)(i & 1);
        }

        // ── 构建哈希（与真实 Count/Place 同形），供 Melee 扫描用 ──
        // 关键：cellStart/sorted 必须由**同一份 Counts** 构建（否则扫描读到的是垃圾）。
        var cnt = new NativeArray<int>(Cells, Allocator.Persistent);
        var cursor = new NativeArray<int>(Cells, Allocator.Persistent);
        var seedJob = new BenchCountCellsJob
        {
            Positions = pos, Alive = alive, State = state, Counts = cnt, Length = N,
            InvCellSize = 1f / 5f, CellsW = CellsW, CellsH = CellsH,
            OriginX = CellsW * 0.5f, OriginY = CellsH * 0.5f, StateDeath = 3,
        };
        for (int i = 0; i < Cells; i++) { cnt[i] = 0; cursor[i] = 0; }
        seedJob.Schedule(N, 0).Complete();
        int total = 0;
        for (int i = 0; i < Cells; i++) { cellStart[i] = total; cursor[i] = total; total += cnt[i]; }
        cellStart[Cells] = total;
        // 放置（宿主侧，只为构造扫描输入；不计时）：槽号走同一套 clamp
        for (int i = 0; i < N; i++)
        {
            if (alive[i] == 0 || state[i] == 3) continue;
            int cx = (int)(pos[i].x * 0.2f + CellsW * 0.5f); cx = Math.Max(0, Math.Min(cx, CellsW - 1));
            int cy = (int)(pos[i].y * 0.2f + CellsH * 0.5f); cy = Math.Max(0, Math.Min(cy, CellsH - 1));
            sorted[cursor[cy * CellsW + cx]++] = i;
        }
        BuildScanOrder(scanOrder);
        Console.WriteLine($"    input: alive={CountAlive(alive)} hashed={total} ({total / (double)Cells:F2} units/cell)");
        for (int i = 0; i < N * 8; i++) { kd2[i] = 0f; kpeer[i] = -1; }

        // ── 臂 1：Count/Place 形状（生成内核）──
        var countJob = new BenchCountCellsJob
        {
            Positions = pos, Alive = alive, State = state, Counts = counts, Length = N,
            InvCellSize = 1f / 5f, CellsW = CellsW, CellsH = CellsH,
            OriginX = CellsW * 0.5f, OriginY = CellsH * 0.5f, StateDeath = 3,
        };
        TimeArm("count/place shape (generated)", 12, () =>
        {
            for (int i = 0; i < Cells + 1; i++) counts[i] = 0;
            countJob.Schedule(N, 0).Complete();
        }, () => SumCounts(counts));

        // ── 臂 2：Melee 扫描形状（生成内核）──
        var meleeJob = new BenchMeleeScanJob
        {
            Positions = pos, ConfigId = cfg, Team = team, CellStart = cellStart, SortedIndex = sorted,
            ScanOrder = scanOrder, KD2 = kd2, KPeer = kpeer, Length = N,
            CellsW = CellsW, CellsH = CellsH, InvCellSize = 1f / 5f,
            OriginX = CellsW * 0.5f, OriginY = CellsH * 0.5f,
            MyOrcaRadiusSq = 144f, MySeekR2 = 400f, MyMass = 1f, OuterCap = 64,
        };
        TimeArm("melee scan shape (generated)", 6, () => { meleeJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));

        // ── 臂 3：托管基线（同一算法、**同一并行度**：15 线程分块，单线程会不公平）──
        // 判据读法：生成内核 vs 并行托管基线的 ns/元素 之比 ≈ 1 表示"代码生成质量与手写相当"；
        // 显著 >1 表示转译器还有发射规则可挖。
        TimeArm("melee scan (managed C#, 15 threads)", 3,
            () => { ParallelManagedMelee(pos, cfg, team, cellStart, sorted, scanOrder, kd2, kpeer); },
            () => SumKd2(kd2));

        // ── 噪声底：同一臂连跑 5 次，暴露"同机同日"的漂移幅度 ──
        // 实测（本机）：generated 144–162 ns、managed 167–286 ns ⇒ **比值在 1.09–1.84 间摆动**。
        // 结论：**"生成内核 vs 并行托管"的比值不可判定**，任何基于它的"转译器快/慢 N 倍"都不成立。
        // 反之，分块粒度这类**同臂内单调**的效应可以判定（见下）。
        Console.WriteLine("  -- repeatability (same arm, 5 consecutive measurements; ratio spread = noise floor) --");
        for (int i = 0; i < 5; i++)
        {
            double gen = TimeArm2($"melee generated #{i}", 6, () => { meleeJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
            double man = TimeArm2($"melee managed   #{i}", 6,
                () => { ParallelManagedMelee(pos, cfg, team, cellStart, sorted, scanOrder, kd2, kpeer); }, () => SumKd2(kd2));
            Console.WriteLine($"      gen={gen,7:F1}  managed={man,7:F1}  ratio={man / gen:F3}");
        }

        // ── 分块粒度扫描（纯调度层，语义不变）：自适应（0）对显式每块元素数 ──
        // 读法：若某显式粒度明显优于自适应，说明 JCC 自适应分块对内存受限内核不是最优。
        foreach (int tile in new[] { 1000, 4000, 16000, 64000 })
        {
            int t = tile;
            TimeArm($"melee scan @ tile={t}", 4,
                () => { meleeJob.Schedule(N, t).Complete(); }, () => SumKd2(kd2));
        }
        foreach (int tile in new[] { 1000, 4000, 16000, 64000 })
        {
            int t = tile;
            TimeArm($"count/place @ tile={t}", 8, () =>
            {
                for (int i = 0; i < Cells + 1; i++) counts[i] = 0;
                countJob.Schedule(N, t).Complete();
            }, () => SumCounts(counts));
        }

        // ── 臂 4：邻域格预编译变体（隔离"逐格 hash->CellStart 查表 + 越界判断"的访问顺序差异）──
        var compStart = new NativeArray<int>(Cells + 1, Allocator.Persistent);
        var compCell = new NativeArray<int>(Cells * 81, Allocator.Persistent);
        const int kCompStride = 81;
        for (int i = 0; i <= Cells; i++) compStart[i] = i * kCompStride;   // 定长 81，空位留 0
        var compileJob = new BenchCompileCellsJob
        {
            ScanOrder = scanOrder, CompStart = compStart, CompCell = compCell,
            CellsW = CellsW, CellsH = CellsH,
        };
        compileJob.Schedule(Cells, 0).Complete();

        var compJob = new BenchMeleeScanCompJob
        {
            Positions = pos, ConfigId = cfg, Team = team, CellStart = cellStart, SortedIndex = sorted,
            CompStart = compStart, CompCell = compCell, KD2 = kd2, KPeer = kpeer, Length = N,
            CellsW = CellsW, CellsH = CellsH, InvCellSize = 1f / 5f,
            OriginX = CellsW * 0.5f, OriginY = CellsH * 0.5f,
            MyOrcaRadiusSq = 144f, MySeekR2 = 400f, MyMass = 1f, OuterCap = 64,
            RadarCells = 9,
        };
        // ── 臂 5：参数打包变体（隔离"形参过多 ⇒ 热循环从栈重载参数"）──
        var packJob = new BenchMeleeScanPackJob
        {
            Positions = pos, ConfigId = cfg, Team = team, CellStart = cellStart, SortedIndex = sorted,
            ScanOrder = scanOrder, KD2 = kd2, KPeer = kpeer,
            Props = new BenchScanProps
            {
                Length = N, CellsW = CellsW, CellsH = CellsH, InvCellSize = 1f / 5f,
                OriginX = CellsW * 0.5f, OriginY = CellsH * 0.5f,
                MyOrcaRadiusSq = 144f, MySeekR2 = 400f, MyMass = 1f, OuterCap = 64,
            },
        };
        Console.WriteLine("  -- scalar-params vs packed-params (5 interleaved pairs) --");
        for (int i = 0; i < 5; i++)
        {
            double s1 = TimeArm2("scalar-params", 6, () => { meleeJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
            double s2 = TimeArm2("packed-params", 6, () => { packJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
            Console.WriteLine($"      scalar={s1,7:F1}  packed={s2,7:F1}  packed/scalar={s2 / s1:F3}");
        }
        Console.WriteLine("  -- gather vs compiled-neighbour-cells (5 interleaved pairs) --");
        for (int i = 0; i < 5; i++)
        {
            double g = TimeArm2("gather", 6, () => { meleeJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
            double c = TimeArm2("compiled", 6, () => { compJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
            Console.WriteLine($"      gather={g,7:F1}  compiled={c,7:F1}  compiled/gather={c / g:F3}");
        }
        // ── 臂 6：参数数量三臂对照（125 vs 打包标量 vs 输入全打包）──
        // 目的：判定"形参数是否真是瓶颈"——用同一个内核体、同一份输入，只改参数形状。
        var padArr = new NativeArray<int>[32];
        for (int i = 0; i < 32; i++) padArr[i] = new NativeArray<int>(16, Allocator.Persistent);
        var counters = new NativeArray<int>(4, Allocator.Persistent);

        var job96 = new BenchParams96Job
        {
            Positions = pos, ConfigId = cfg, Team = team, CellStart = cellStart, SortedIndex = sorted,
            ScanOrder = scanOrder, KD2 = kd2, KPeer = kpeer, Counters = counters,
            CellsW = CellsW, CellsH = CellsH, InvCellSize = 1f / 5f,
            OriginX = CellsW * 0.5f, OriginY = CellsH * 0.5f,
            MyOrcaRadiusSq = 144f, MySeekR2 = 400f, MyMass = 1f, OuterCap = 64,
        };
        for (int i = 0; i < 32; i++)
        {
            var fld = typeof(BenchParams96Job).GetField("PadArr" + i);
            fld?.SetValueDirect(__makeref(job96), padArr[i]);
        }
        var job96In = new BenchParams96AllJob
        {
            In = new BenchParams96In
            {
                Positions = pos, ConfigId = cfg, Team = team, CellStart = cellStart,
                SortedIndex = sorted, ScanOrder = scanOrder,
                CellsW = CellsW, CellsH = CellsH, InvCellSize = 1f / 5f,
                OriginX = CellsW * 0.5f, OriginY = CellsH * 0.5f,
                MyOrcaRadiusSq = 144f, MySeekR2 = 400f, MyMass = 1f, OuterCap = 64,
            },
            KD2 = kd2, KPeer = kpeer, Counters = counters,
        };

        Console.WriteLine("  -- param-count sweep: 125 params vs packed-scalars vs packed-inputs(9) --");
        for (int i = 0; i < 5; i++)
        {
            for (int k = 0; k < 4; k++) counters[k] = 0;
            double a = TimeArm2("p125", 4, () => { job96.Schedule(N, 0).Complete(); }, () => counters[0]);
            int aCand = counters[0];
            for (int k = 0; k < 4; k++) counters[k] = 0;
            double b = TimeArm2("pScalarPacked", 4, () => { packJob.Schedule(N, 0).Complete(); },
                () => SumKd2(kd2));
            for (int k = 0; k < 4; k++) counters[k] = 0;
            double c2 = TimeArm2("p9", 4, () => { job96In.Schedule(N, 0).Complete(); }, () => counters[0]);
            int cCand = counters[0];
            Console.WriteLine($"      p125={a,7:F1}  p9={c2,7:F1}  p9/p125={c2 / a:F3}   cand(125)={aCand} cand(9)={cCand}");
        }
        for (int i = 0; i < 32; i++) padArr[i].Dispose();
        counters.Dispose();
        pos.Dispose(); alive.Dispose(); state.Dispose(); cfg.Dispose(); team.Dispose();
        counts.Dispose(); cellStart.Dispose(); sorted.Dispose(); scanOrder.Dispose();
        kd2.Dispose(); kpeer.Dispose(); cnt.Dispose(); cursor.Dispose();
    }

    static void TimeArm(string name, int reps, Action body, Func<double> checksum)
    {
        double ns = TimeArm2(name, reps, body, checksum);
        Console.WriteLine($"  {name,-44} {ns,10:F3} ns/elem");
    }

    /// <summary>计时并返回 ns/元素（不打印），供重复性与比值统计使用。</summary>
    static double TimeArm2(string name, int reps, Action body, Func<double> checksum)
    {
        body();                                   // 预热
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int r = 0; r < reps; r++) body();
        sw.Stop();
        _ = checksum();
        return sw.Elapsed.TotalMilliseconds * 1e6 / ((double)reps * N);
    }

    static int CountAlive(NativeArray<byte> a)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != 0) n++;
        return n;
    }

    static double SumCounts(NativeArray<int> c)
    {
        double s = 0;
        for (int i = 0; i < c.Length; i++) s += c[i];
        return s;
    }

    static double SumKd2(NativeArray<float> k)
    {
        double s = 0;
        for (int i = 7; i < k.Length; i += 8) s += k[i];
        return s;
    }

    static void BuildScanOrder(NativeArray<int> order)
    {
        int k = 0;
        order[k++] = 4 * 9 + 4;
        for (int ring = 1; ring <= 4; ring++)
            for (int dy = -ring; dy <= ring; dy++)
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring) continue;
                    if (k < 81) order[k++] = (4 + dy) * 9 + (4 + dx);
                }
    }

    /// <summary>裸指针打包（struct 而非 tuple：void* 不能作元组类型参数）。</summary>
    unsafe struct Ptrs
    {
        public EntJoy.Mathematics.float2* Pos;
        public int* Cfg;
        public byte* Team;
        public int* CellStart;
        public int* Sorted;
        public int* Order;
        public float* Kd2;
        public int* Kpeer;
    }

    static unsafe Ptrs PackPtrs(NativeArray<EntJoy.Mathematics.float2> pos, NativeArray<int> cfg, NativeArray<byte> team,
        NativeArray<int> cellStart, NativeArray<int> sorted, NativeArray<int> order,
        NativeArray<float> kd2, NativeArray<int> kpeer)
        => new Ptrs
        {
            Pos = (EntJoy.Mathematics.float2*)pos.GetUnsafePtr(),
            Cfg = (int*)cfg.GetUnsafePtr(),
            Team = (byte*)team.GetUnsafePtr(),
            CellStart = (int*)cellStart.GetUnsafePtr(),
            Sorted = (int*)sorted.GetUnsafePtr(),
            Order = (int*)order.GetUnsafePtr(),
            Kd2 = (float*)kd2.GetUnsafePtr(),
            Kpeer = (int*)kpeer.GetUnsafePtr(),
        };

    /// <summary>与生成内核**同并行度**的托管基线：15 个线程各跑一段连续 index 区间。</summary>
    static unsafe void ParallelManagedMelee(NativeArray<EntJoy.Mathematics.float2> pos, NativeArray<int> cfg,
        NativeArray<byte> team, NativeArray<int> cellStart, NativeArray<int> sorted, NativeArray<int> order,
        NativeArray<float> kd2, NativeArray<int> kpeer)
    {
        const int kThreads = 15;
        var packed = PackPtrs(pos, cfg, team, cellStart, sorted, order, kd2, kpeer);
        int chunk = (N + kThreads - 1) / kThreads;
        var threads = new System.Threading.Thread[kThreads];
        for (int t = 0; t < kThreads; t++)
        {
            int lo = t * chunk, hi = Math.Min(N, lo + chunk);
            if (lo >= hi) { threads[t] = new System.Threading.Thread(() => { }); continue; }
            threads[t] = new System.Threading.Thread(() =>
                ManagedMeleeScanRange(packed.Pos, packed.Cfg, packed.Team, packed.CellStart,
                    packed.Sorted, packed.Order, packed.Kd2, packed.Kpeer, lo, hi));
        }
        for (int t = 0; t < kThreads; t++) threads[t].Start();
        for (int t = 0; t < kThreads; t++) threads[t].Join();
    }

    /// <summary>手写基线：与 job 内层同一算法，单线程、不经过转译器。</summary>
    static unsafe void ManagedMeleeScan(NativeArray<EntJoy.Mathematics.float2> pos, NativeArray<int> cfg,
        NativeArray<byte> team, NativeArray<int> cellStart, NativeArray<int> sorted, NativeArray<int> order,
        NativeArray<float> kd2, NativeArray<int> kpeer)
        => ManagedMeleeScanRange(pos.GetUnsafePtr(), cfg.GetUnsafePtr(), team.GetUnsafePtr(),
            cellStart.GetUnsafePtr(), sorted.GetUnsafePtr(), order.GetUnsafePtr(),
            kd2.GetUnsafePtr(), kpeer.GetUnsafePtr(), 0, N);

    /// <summary>线性扫描整数哈希（整数运算无浮点差异风险）——用来校验两臂确实处理了同一批槽位。</summary>
    static unsafe int SumSorted(NativeArray<int> sorted)
    {
        long s = 0;
        var p = (int*)sorted.GetUnsafePtr();
        for (int i = 0; i < sorted.Length; i++) s += p[i];
        return (int)(s % 1000000007);
    }

    static unsafe void ManagedMeleeScanRange(void* posV, void* cfgV, void* teamV, void* csV, void* sV, void* oV,
        void* d2V, void* peerV, int lo, int hi)
    {
        const int CellsW = 784, CellsH = 448;
        var posP = (EntJoy.Mathematics.float2*)posV;
        var cfgP = (int*)cfgV;
        var teamP = (byte*)teamV;
        var csP = (int*)csV;
        var sP = (int*)sV;
        var oP = (int*)oV;
        var d2P = (float*)d2V;
        var peerP = (int*)peerV;
        int gridCells = CellsW * CellsH;
        for (int index = lo; index < hi; index++)
        {
            var p = posP[index];
            int cx = Math.Max(0, Math.Min((int)(p.x * 0.2f + CellsW * 0.5f), CellsW - 1));
            int cy = Math.Max(0, Math.Min((int)(p.y * 0.2f + CellsH * 0.5f), CellsH - 1));
            int hashId = cy * CellsW + cx;
            int outerProcessed = 0, orcaCount = 0, kBase = index * 8;
            float worstK2 = 0f, closestEnemyD2 = 1000f * 1000f;
            byte myTeam = teamP[index];
            for (int jj = 0; jj < 81; jj++)
            {
                int j = oP[jj];
                bool isCenter = jj == 0;
                if (!isCenter && outerProcessed >= 64) break;
                int newHash = hashId + (j % 9 - 4) - (4 - j / 9) * CellsW;
                if (newHash < 0 || newHash >= gridCells) continue;
                int start = csP[newHash], end = csP[newHash + 1];
                for (int s = start; s < end; s++)
                {
                    int i = sP[s];
                    if (i == index) continue;
                    if (!isCenter) { if (outerProcessed >= 64) break; outerProcessed++; }
                    var q = posP[i];
                    float dx = p.x - q.x, dy = p.y - q.y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < 144f && (orcaCount < 8 || d2 < worstK2))
                    {
                        float peerMass = cfgP[i] == 2 ? 20f : 1f;
                        if (peerMass / (1f + peerMass + 1e-6f) >= 0.1f)
                        {
                            int slot = orcaCount < 8 ? orcaCount : 8;
                            while (slot > 0 && d2P[kBase + slot - 1] > d2)
                            {
                                if (slot < 8) { d2P[kBase + slot] = d2P[kBase + slot - 1]; peerP[kBase + slot] = peerP[kBase + slot - 1]; }
                                slot--;
                            }
                            d2P[kBase + slot] = d2; peerP[kBase + slot] = i;
                            if (orcaCount < 8) orcaCount++;
                            worstK2 = d2P[kBase + 7];
                        }
                    }
                    if (jj < 9 && teamP[i] != myTeam && d2 < closestEnemyD2 && d2 < 400f)
                        closestEnemyD2 = d2;
                }
            }
            d2P[kBase + 7] = worstK2 + closestEnemyD2;
        }
    }
}
