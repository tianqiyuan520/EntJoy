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

NativeJobScheduler.Shutdown();

Console.WriteLine(failed == 0 ? "\nPASS: all fixture assertions hold." : $"\nFAIL: {failed} assertion(s) failed.");
return failed == 0 ? 0 : 1;

void Report(string name, bool ok, string detail)
{
    if (!ok) failed++;
    Console.WriteLine($"  {name,-52}: {(ok ? "✅ PASS" : "❌ FAIL")}{(ok || detail.Length == 0 ? "" : "  " + detail)}");
}
