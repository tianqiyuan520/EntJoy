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
//   [5-6] HelperInliningJob：内联提示在 Schedule / Run 下保持相同结果
//   [7] ZeroMaskJob：零值与非零掩码保持各自的计算结果
//   [8] ArrayAliasJob：同址、分离、部分重叠、不同长度与空视图保持数据访问语义
//   [9] SmallFixedLoopJob：最内层固定循环保留 continue / break 与三数组读写语义
//   [10-16] 整数与枚举掩码：零值、符号位、高位与字段对齐保持 Schedule / Run 结果
//   [18] Chunk 掩码：零值与非零值保持 Run / Schedule 的计算结果
//   [19] 只读 Chunk 上下文：同址列数据与多块遍历保持 Run / Schedule 的计算结果
//
// 退出码：0 = 全部通过；1 = 有断言失败。
// ─────────────────────────────────────────────────────────────────────────────

const int N = 1024;
int failed = 0;

NativeJobScheduler.Initialize();
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length > 0 && args[0] == "columns")
{
    try { UserColumnFixture.Run(); }
    finally { NativeJobScheduler.Shutdown(); }
    return 0;
}
if (args.Length > 0 && args[0] == "guards")
{
    try { GuardedElementFixture.Run(); }
    finally { NativeJobScheduler.Shutdown(); }
    return 0;
}
if (args.Length > 0 && args[0] == "alloc")
{
    try { NativeHandleContractProbe.Run(); }
    finally { NativeJobScheduler.Shutdown(); }
    return 0;
}
if (args.Length > 0 && args[0] == "copy")
{
    try { CopiedContextFixture.Run(); }
    finally { NativeJobScheduler.Shutdown(); }
    return 0;
}
if (args.Length > 0 && args[0] == "jobs")
{
    try { SchedulerDispatchProbe.Run(args.Length > 1 ? args[1] : "jobsystem.csv", args.Length > 2 ? int.Parse(args[2]) : 20); }
    finally { NativeJobScheduler.Shutdown(); }
    return 0;
}
Console.WriteLine("=== NativeTranspiler fixture: generated-code semantics (F-4 / F-5) ===");
GuardedElementFixture.Run();
UserColumnFixture.Run();
TranslationSemanticsFixture.Run();
DifferentialTranslationFixture.Run();
ChunkLoopBindingsFixture.Run();
RelationshipReadFixture.Run();
RelationshipMaskFixture.Run();
RelationshipDependencyFixture.Run();
RelationshipCommandFixture.Run();
#if ENTJOY_FIXTURE_ISPC
IspcBackendFixture.Run();
#endif

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

// Helpers with ordinary, forced and forbidden inlining must work through both native entries.
{
    var values = new NativeArray<int>(N, Allocator.Persistent);
    var job = new HelperInliningJob { Values = values };
    job.Schedule(N, 64).Complete();
    int bad = 0;
    for (int i = 0; i < N; i++)
        if (values[i] != (i * 2 + 7) * 3 + 1) bad++;
    Report("[5] helper inlining: Schedule", bad == 0, $"mismatch: {bad}/{N}");

    for (int i = 0; i < N; i++) values[i] = -1;
    job.Run(N);
    bad = 0;
    for (int i = 0; i < N; i++)
        if (values[i] != (i * 2 + 7) * 3 + 1) bad++;
    Report("[6] helper inlining: Run", bad == 0, $"mismatch: {bad}/{N}");
    values.Dispose();
}

// Zero specialization and the dynamic fallback must agree for every tested bit pattern.
{
    var values = new NativeArray<int>(N, Allocator.Persistent);
    int bad = 0;
    foreach (int flags in new[] { 0, 1, 2, 4, 7, 128, -1, int.MinValue })
    {
        var job = new ZeroMaskJob { Values = values, Flags = flags,
            B0 = 1, B1 = 2, B2 = 3, B3 = 4, B4 = 5, B5 = 6, B6 = 7, B7 = 8 };
        for (int path = 0; path < 2; path++)
        {
            for (int i = 0; i < N; i++) values[i] = int.MinValue;
            if (path == 0) job.Schedule(N, 63).Complete();
            else job.Run(N);
            for (int i = 0; i < N; i++)
            {
                int expected = 36;
                for (int k = 0; k < 8; k++)
                {
                    if ((flags & 1) == 0) expected += k;
                    if ((flags & 2) == 0) expected += 2;
                    if ((flags & 4) == 0) expected += 3;
                }
                if (values[i] != ((i & 15) == 0 ? -i : expected)) bad++;
            }
        }
    }
    Report("[7] zero mask and fallback: Schedule / Run", bad == 0, $"mismatch: {bad}");
    values.Dispose();
}

// Compare native access with a sequential reference for equal, distinct and overlapping views.
{
    int bad = 0;
    for (int scenario = 0; scenario < 6; scenario++)
    {
        bool partial = scenario == 2 || scenario == 4;
        int count = partial ? 17 : N;
        int inputStart = scenario == 4 ? 1 : 0;
        int outputStart = scenario == 0 ? N + 1 : scenario == 2 ? 1 : 0;
        int inputLength = scenario == 5 ? 0 : count;
        int outputLength = scenario == 3 ? count - 3 : inputLength;
        var storage = new NativeArray<int>(2 * N + 2, Allocator.Persistent);
        var job = new ArrayAliasJob {
            Input = storage.GetSubArray(inputStart, inputLength),
            Output = storage.GetSubArray(outputStart, outputLength)
        };
        // Partial overlap is exercised serially; its cross-index dependencies require Run.
        for (int path = partial ? 1 : 0; path < 2; path++)
        {
            var expected = new int[storage.Length];
            for (int i = 0; i < storage.Length; i++) storage[i] = expected[i] = i % 17 + 1;
            for (int i = 0; i < count; i++)
            {
                if (i >= inputLength || i >= outputLength) continue;
                if ((i & 3) == 0) expected[outputStart + i] = expected[inputStart + i];
                else
                {
                    expected[outputStart + i] = expected[inputStart + i] + 1;
                    expected[outputStart + i] += expected[inputStart + i];
                }
            }
            if (path == 0) job.Schedule(count, 63).Complete();
            else job.Run(count);
            job.Run(0);
            for (int i = 0; i < storage.Length; i++)
                if (storage[i] != expected[i]) bad++;
        }
        storage.Dispose();
    }
    Report("[8] array alias: equal / distinct / overlap / lengths", bad == 0, $"mismatch: {bad}");
}

{
    var input = new NativeArray<int>(N, Allocator.Persistent);
    var output = new NativeArray<int>(N, Allocator.Persistent);
    var visits = new NativeArray<int>(N, Allocator.Persistent);
    var job = new SmallFixedLoopJob { Input = input, Output = output, Visits = visits };
    for (int i = 0; i < N; i++) input[i] = i % 5 - 2;
    int bad = 0;
    for (int path = 0; path < 2; path++)
    {
        if (path == 0) job.Schedule(N, 63).Complete();
        else job.Run(N);
        for (int i = 0; i < N; i++)
        {
            int value = input[i];
            int expectedSum = value == -1 ? 0 : value == 1 ? 0 : 2 * value;
            int expectedVisits = value == -1 ? 0 : value == 1 ? 1 : 2;
            if (output[i] != expectedSum || visits[i] != expectedVisits) bad++;
        }
    }
    Report("[9] fixed loop: continue / break / three arrays", bad == 0, $"mismatch: {bad}");
    input.Dispose(); output.Dispose(); visits.Dispose();
}

CheckTypedMask("[10] short mask", 0x8000, (flags, values, run) => {
    var job = new SmallZeroMaskJob { Values = values, Padding = 0xCC, Flags = unchecked((short)flags), Sentinel = 0xFFFFFFFF };
    if (run) job.Run(N); else job.Schedule(N, 63).Complete();
});
CheckTypedMask("[11] uint mask", 0x80000000, (flags, values, run) => {
    var job = new UnsignedZeroMaskJob { Values = values, Padding = 0xCC, Flags = (uint)flags, Sentinel = 0xFFFFFFFF };
    if (run) job.Run(N); else job.Schedule(N, 63).Complete();
});
CheckTypedMask("[12] long mask", 1UL << 63, (flags, values, run) => {
    var job = new SignedWideZeroMaskJob { Values = values, Padding = 0xCC, Flags = unchecked((long)flags), Sentinel = 0xFFFFFFFF };
    if (run) job.Run(N); else job.Schedule(N, 63).Complete();
});
CheckTypedMask("[13] ulong mask", 1UL << 63, (flags, values, run) => {
    var job = new WideZeroMaskJob { Values = values, Padding = 0xCC, Flags = flags, Sentinel = 0xFFFFFFFF };
    if (run) job.Run(N); else job.Schedule(N, 63).Complete();
});
CheckTypedMask("[14] byte enum mask", 128, (flags, values, run) => {
    var job = new SmallEnumZeroMaskJob { Values = values, Padding = 0xCC, Flags = (SmallMask)flags, Sentinel = 0xFFFFFFFF };
    if (run) job.Run(N); else job.Schedule(N, 63).Complete();
});
CheckTypedMask("[15] ulong enum mask", 1UL << 63, (flags, values, run) => {
    var job = new WideEnumZeroMaskJob { Values = values, Padding = 0xCC, Flags = (WideMask)flags, Sentinel = 0xFFFFFFFF };
    if (run) job.Run(N); else job.Schedule(N, 63).Complete();
});
CheckTypedMask("[16] native unsigned mask", 4, (flags, values, run) => {
    var job = new NativeZeroMaskJob { Values = values, Padding = 0xCC, Flags = (nuint)flags, Sentinel = 0xFFFFFFFF };
    if (run) job.Run(N); else job.Schedule(N, 63).Complete();
});

if (args.Length > 0 && args[0] == "bench")
{
    Bench.Run();          // 必须早于 Shutdown：基准要用真实调度器
}

// Chunk masks exercise the zero specialization and nonzero fallback through both native APIs.
unsafe
{
    using var world = new EntJoy.ECS.World("ChunkMaskFixture");
    world.CreateEntities(4097, typeof(MaskValue));
    var query = new EntJoy.ECS.QueryBuilder().WithAll<MaskValue>();
    int bad = 0;
    foreach (uint flags in new uint[] { 0, 1, 2, 4, 7, 0x80000000u })
    {
        var job = new ChunkMaskJob { Flags = flags };
        for (int mode = 0; mode < 2; mode++)
        {
            if (mode == 0) NativeTranspiler.Bindings.NativeExports.RunImmediate_ChunkMaskJob(ref job, query, world);
            else NativeTranspiler.Bindings.NativeExports.Schedule_ChunkMaskJob(ref job, query, default, world).Complete();
            int visited = 0;
            foreach (var archetype in world.EntityManager.GetAllArchetypes())
            foreach (var chunk in archetype.GetChunks())
            {
                var values = new Span<MaskValue>((void*)chunk.GetComponentArrayPointer(archetype.GetComponentTypeIndex<MaskValue>()), chunk.EntityCount);
                for (int i = 0; i < values.Length; i++)
                {
                    int expected = ((flags & 1u) == 0 ? i : 0) + ((flags & 2u) == 0 ? 10 : 0) + ((flags & 4u) == 0 ? 100 : 0);
                    if (values[i].Number != expected) bad++;
                }
                visited += values.Length;
            }
            if (visited != 4097) bad++;
        }
    }
    Report("[18] chunk mask Run / Schedule zero and nonzero semantics", bad == 0, $"mismatch: {bad}");
}

// Snapshot descriptors retain aliasing of their external data across multiple chunks.
unsafe
{
    const int count = 4097;
    using var world = new EntJoy.ECS.World("ChunkSnapshotFixture");
    world.CreateEntities(count, typeof(SnapshotValue));
    using var data = new NativeArray<int>(count, Allocator.Persistent);
    var query = new EntJoy.ECS.QueryBuilder().WithAll<SnapshotValue>();
    int bad = 0;
    for (int mode = 0; mode < 2; mode++)
    {
        int slot = 0;
        foreach (var archetype in world.EntityManager.GetAllArchetypes())
        foreach (var chunk in archetype.GetChunks())
        {
            var values = new Span<SnapshotValue>((void*)chunk.GetComponentArrayPointer(archetype.GetComponentTypeIndex<SnapshotValue>()), chunk.EntityCount);
            for (int i = 0; i < values.Length; i++, slot++)
            {
                values[i].Slot = slot;
                ((int*)data.GetUnsafePtr())[slot] = slot * 3;
            }
        }
        var job = new ChunkSnapshotJob {
            Source = new UserColumnView<int> { Flat = (int*)data.GetUnsafePtr(), Length = count },
            Destination = data, Bias = 17
        };
        if (mode == 0) NativeTranspiler.Bindings.NativeExports.RunImmediate_ChunkSnapshotJob(ref job, query, world);
        else NativeTranspiler.Bindings.NativeExports.Schedule_ChunkSnapshotJob(ref job, query, default, world).Complete();
        foreach (var archetype in world.EntityManager.GetAllArchetypes())
        foreach (var chunk in archetype.GetChunks())
        {
            var values = new Span<SnapshotValue>((void*)chunk.GetComponentArrayPointer(archetype.GetComponentTypeIndex<SnapshotValue>()), chunk.EntityCount);
            foreach (var value in values)
                if (value.Number != value.Slot * 3 + 17 || data[value.Slot] != value.Number) bad++;
        }
        if (slot != count) bad++;
    }
    Report("[19] read-only chunk context preserves aliased data Run / Schedule", bad == 0, $"mismatch: {bad}");
}

// Reduction loops retain their initial value, update order and loop condition.
{
    const int lanes = 19;
    using var input = new NativeArray<int>(65, Allocator.Persistent);
    using var output = new NativeArray<int>(lanes, Allocator.Persistent);
    using var maximum = new NativeArray<int>(lanes, Allocator.Persistent);
    unsafe
    {
        int* values = (int*)input.GetUnsafePtr();
        for (int i = 0; i < input.Length; i++) values[i] = (i % 2 == 0 ? 1 : -1) * (i + 1);
    }
    int badSum = 0, badSimd = 0, badOrder = 0, badMinMax = 0, badBound = 0;
    foreach (int count in new[] {0, 1, 3, 4, 5, 8, 9, 17, 65})
    foreach (int seed in new[] {-7, 0, 5})
    for (int path = 0; path < 2; path++)
    {
        var sum = new SeededReductionJob { Input = input, Output = output, Count = count, Seed = seed };
        if (path == 0) sum.Run(lanes); else sum.Schedule(lanes, 1).Complete();
        for (int i = 0; i < lanes; i++)
        {
            int expected = seed + i;
            for (int k = 0; k < count; k++) expected += input[k];
            if (output[i] != expected) badSum++;
        }
        var simd = new SimdSeededReductionJob { Input = input, Output = output, Count = count, Seed = seed };
        if (path == 0) simd.Run(lanes); else simd.Schedule(lanes, 64).Complete();
        for (int i = 0; i < lanes; i++)
        {
            int expected = seed + i;
            for (int k = 0; k < count; k++) expected += input[k];
            if (output[i] != expected) badSimd++;
        }
        var ordered = new OrderedReductionJob { Input = input, Output = output, Count = count, Seed = seed };
        if (path == 0) ordered.Run(lanes); else ordered.Schedule(lanes, 3).Complete();
        for (int i = 0; i < lanes; i++)
        {
            int expected = seed + i;
            for (int k = 0; k < count; k++) expected -= input[k];
            if (output[i] != expected) badOrder++;
        }
        var minMax = new MinMaxReductionJob { Input = input, Minimum = output, Maximum = maximum, Count = count, Seed = seed };
        if (path == 0) minMax.Run(lanes); else minMax.Schedule(lanes, 3).Complete();
        for (int i = 0; i < lanes; i++)
        {
            int low = seed + i, high = low;
            for (int k = 0; k < count; k++) { low = Math.Min(low, input[k]); high = Math.Max(high, input[k]); }
            if (output[i] != low || maximum[i] != high) badMinMax++;
        }
        var bound = new SimdMutableReductionBoundJob { Output = output, Count = count, Seed = seed };
        if (path == 0) bound.Run(lanes); else bound.Schedule(lanes, 64).Complete();
        for (int i = 0; i < lanes; i++)
        {
            int expected = seed + i, end = count;
            for (int k = 0; k < end; k++) { expected += k; end--; }
            if (output[i] != expected) badBound++;
        }
    }
    Report("[20] seeded integer reductions Run / Schedule", badSum == 0, $"mismatch: {badSum}");
    Report("[21] AutoSIMD reductions and remainder", badSimd == 0, $"mismatch: {badSimd}");
    Report("[22] ordered custom reduction", badOrder == 0, $"mismatch: {badOrder}");
    Report("[23] seeded min/max reductions", badMinMax == 0, $"mismatch: {badMinMax}");
    Report("[24] AutoSIMD mutable reduction bound", badBound == 0, $"mismatch: {badBound}");
}

{
    int badBreak = 0, badContinue = 0, badReturn = 0, badNested = 0;
    foreach (int length in new[] { 0, 1, 7, 8, 9, 19 })
    {
        using var output = new NativeArray<int>(Math.Max(1, length), Allocator.Persistent);
        for (int path = 0; path < 2; path++)
        {
            var breaking = new SimdFixedBreakJob { Output = output };
            if (path == 0) breaking.Run(length); else breaking.Schedule(length, 64).Complete();
            for (int i = 0; i < length; i++)
            {
                int expected = i;
                for (int k = 0; k < 4; k++) { if (k == i % 4) { expected += 100; break; } expected += k + 1; }
                if (output[i] != expected) badBreak++;
            }
            var continuing = new SimdFixedContinueJob { Output = output };
            if (path == 0) continuing.Run(length); else continuing.Schedule(length, 64).Complete();
            for (int i = 0; i < length; i++)
            {
                int expected = i;
                for (int k = 0; k < 4; k++) { if (k == i % 4) { expected += 100; continue; } expected += k + 1; }
                if (output[i] != expected) badContinue++;
            }
            var returning = new SimdFixedReturnJob { Output = output };
            if (path == 0) returning.Run(length); else returning.Schedule(length, 64).Complete();
            for (int i = 0; i < length; i++)
            {
                int expected = i;
                for (int k = 0; k < i % 4; k++) expected += k + 1;
                if (output[i] != expected) badReturn++;
            }
            var nested = new SimdNestedContinueJob { Output = output };
            if (path == 0) nested.Run(length); else nested.Schedule(length, 64).Complete();
            for (int i = 0; i < length; i++)
            {
                int expected = i;
                for (int k = -1; k < 2; k++)
                for (int j = -1; j < 2; j++) { if (j + 1 == i % 3) { expected += 100; continue; } expected += j + k; }
                if (output[i] != expected) badNested++;
            }
        }
    }
    Report("[25] SIMD fixed-loop per-lane break", badBreak == 0, $"mismatch: {badBreak}");
    Report("[26] SIMD fixed-loop per-lane continue", badContinue == 0, $"mismatch: {badContinue}");
    Report("[27] SIMD fixed-loop per-lane return", badReturn == 0, $"mismatch: {badReturn}");
    Report("[28] SIMD nested-loop per-lane continue", badNested == 0, $"mismatch: {badNested}");
}

{
    using var output = new NativeArray<int>(19, Allocator.Persistent);
    int bad = 0;
    foreach (bool enabled in new[] { false, true })
    for (int path = 0; path < 2; path++)
    {
        var job = new IndexedAdapterJob { Output = output, Enabled = enabled, Bias = 7 };
        if (path == 0) job.Run(output.Length); else job.Schedule(output.Length, 3).Complete();
        for (int i = 0; i < output.Length; i++)
            if (output[i] != (enabled ? i + 7 : 7 - i)) bad++;
    }
    Report("[29] IJobFor context ABI and bool variants", bad == 0, $"mismatch: {bad}");
}

{
    using var output = new NativeArray<int>(19, Allocator.Persistent);
    int badMaximum = 0, badMutable = 0, badIncrement = 0;
    for (int path = 0; path < 2; path++)
    {
        var maximum = new SimdMaximumBoundJob { Output = output };
        if (path == 0) maximum.Run(output.Length); else maximum.Schedule(output.Length, 64).Complete();
        for (int i = 0; i < output.Length; i++)
            if (output[i] != i + 2 - i % 2) badMaximum++;
        var mutable = new SimdMutableMinBoundJob { Output = output };
        if (path == 0) mutable.Run(output.Length); else mutable.Schedule(output.Length, 64).Complete();
        for (int i = 0; i < output.Length; i++)
            if (output[i] != i + 1) badMutable++;
        var increment = new SimdMaskedIncrementJob { Output = output };
        if (path == 0) increment.Run(output.Length); else increment.Schedule(output.Length, 64).Complete();
        for (int i = 0; i < output.Length; i++)
            if (output[i] != (i % 2 == 0 ? 1 : -1)) badIncrement++;
    }
    Report("[30] SIMD inclusive maximum loop bound", badMaximum == 0, $"mismatch: {badMaximum}");
    Report("[31] SIMD mutable min/max reduction bound", badMutable == 0, $"mismatch: {badMutable}");
    Report("[32] SIMD conditional increment/decrement", badIncrement == 0, $"mismatch: {badIncrement}");
}

{
    using var world = new EntJoy.ECS.World("ClrNativeLayout");
    var em = world.EntityManager;
    var entities = em.CreateEntities(130, typeof(ClrFlagData));
    for (int i = 0; i < entities.Length; i++)
    {
        var value = new ClrFlagData { Enabled = i % 2 == 0, Tag = (byte)i, Code = '\u4e2d' };
        if (i % 2 == 0) em.SetRaw(entities[i], typeof(ClrFlagData), value);
        else em.SetComponent(entities[i], value);
    }
    using var output = new NativeArray<int>(entities.Length, Allocator.Persistent);
    var query = new EntJoy.ECS.QueryBuilder().WithAll<ClrFlagData>();
    int bad = 0;
    var job = new ClrLayoutJob { Output = output };
    NativeTranspiler.Bindings.NativeExports.RunImmediate_ClrLayoutJob(ref job, query, world);
    for (int i = 0; i < entities.Length; i++)
        if (output[i] != (i % 2 == 0 ? 0x4e2d + i : -0x4e2d)) bad++;
    NativeTranspiler.Bindings.NativeExports.Schedule_ClrLayoutJob(ref job, query, default, world).Complete();
    for (int i = 0; i < entities.Length; i++)
        if (output[i] != (i % 2 == 0 ? 0x4e2d + i : -0x4e2d)) bad++;
    Report("[33] CLR bool/byte/char native chunk layout", bad == 0, $"mismatch: {bad}");
}

NativeJobScheduler.Shutdown();

Console.WriteLine(failed + Bench.ExtraFailures == 0 ? "\nPASS: all fixture assertions hold." : $"\nFAIL: {failed + Bench.ExtraFailures} assertion(s) failed.");
return failed + Bench.ExtraFailures == 0 ? 0 : 1;

void Report(string name, bool ok, string detail)
{
    if (!ok) failed++;
    Console.WriteLine($"  {name,-52}: {(ok ? "✅ PASS" : "❌ FAIL")}{(ok || detail.Length == 0 ? "" : "  " + detail)}");
}

void CheckTypedMask(string name, ulong highMask, Action<ulong, NativeArray<int>, bool> execute)
{
    var values = new NativeArray<int>(N, Allocator.Persistent);
    int bad = 0;
    try
    {
        foreach (ulong flags in new[] { 0UL, 1UL, 2UL, highMask, highMask | 3, highMask - 1 })
        {
            int expected = ((flags & 1) == 0 ? 3 : 0) + ((flags & 2) == 0 ? 6 : 0)
                + ((flags & highMask) == 0 ? 12 : 0) + (flags != 0 ? 8 : 0);
            for (int path = 0; path < 2; path++)
            {
                for (int i = 0; i < N; i++) values[i] = int.MinValue;
                execute(flags, values, path == 1);
                for (int i = 0; i < N; i++) if (values[i] != i + expected) bad++;
            }
        }
    }
    finally { values.Dispose(); }
    Report(name + ": Schedule / Run", bad == 0, $"mismatch: {bad}");
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

        // ── 臂 4b（B2 内核结构）：逐格元数据预计算（%9 / /9 / *cellsW → 调用方偏移表）──
        // 判据：① 语义等价（同一输入 + 清零后的输出数组 ⇒ 校验和必须逐位相同，Report 进断言门）
        //       ② 同会话交错 A/B 的 ns/element（8 对，臂序交替），报中位与 p95
        var cellDelta = new NativeArray<int>(81, Allocator.Persistent);
        for (int jj = 0; jj < 81; jj++)
        {
            int j = scanOrder[jj];
            int ox = j % 9 - 4;
            int oy = 4 - j / 9;
            cellDelta[jj] = ox - oy * CellsW;
        }
        var offJob = new BenchMeleeScanOffsetsJob
        {
            Positions = pos, ConfigId = cfg, Team = team, CellStart = cellStart, SortedIndex = sorted,
            CellDelta = cellDelta, KD2 = kd2, KPeer = kpeer,
            Props = packJob.Props,
        };
        Console.WriteLine("  -- melee_pack (baseline) vs melee_offsets (kernel-structure variant): 8 interleaved pairs --");
        var packMed = new System.Collections.Generic.List<double>();
        var offMed = new System.Collections.Generic.List<double>();
        double packSum = 0, offSum = 0, packPeer = 0, offPeer = 0;
        for (int i = 0; i < 8; i++)
        {
            bool packFirst = (i % 2) == 0;
            double b, v;
            if (packFirst)
            {
                ZeroOut(kd2, kpeer);
                b = TimeArm2("melee_pack", 6, () => { packJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
                packSum = SumKd2(kd2); packPeer = SumKpeer(kpeer);
                ZeroOut(kd2, kpeer);
                v = TimeArm2("melee_offsets", 6, () => { offJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
                offSum = SumKd2(kd2); offPeer = SumKpeer(kpeer);
            }
            else
            {
                ZeroOut(kd2, kpeer);
                v = TimeArm2("melee_offsets", 6, () => { offJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
                offSum = SumKd2(kd2); offPeer = SumKpeer(kpeer);
                ZeroOut(kd2, kpeer);
                b = TimeArm2("melee_pack", 6, () => { packJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
                packSum = SumKd2(kd2); packPeer = SumKpeer(kpeer);
            }
            packMed.Add(b); offMed.Add(v);
            Console.WriteLine($"      pair {i + 1}: pack={b,7:F1}  offsets={v,7:F1}  off/pack={v / b:F3}");
        }
        ReportBench("B2 melee_offsets: same KD2 output as melee_pack", packSum == offSum,
            $"sum_pack={packSum:R} sum_offsets={offSum:R}");
        ReportBench("B2 melee_offsets: same KPeer output as melee_pack", packPeer == offPeer,
            $"sum_pack={packPeer:R} sum_offsets={offPeer:R}");
        PrintStat("melee_pack   ", packMed);
        PrintStat("melee_offsets", offMed);

        // ── 臂 4c（09 §54.5 预注册门槛）：两遍切分 = live-set 缩小 ──
        // 门槛口径：同会话交错 A/B 的 ns/element **比值 < 1** 才算"做出一个环比 <1 的臂"，
        // 才有资格谈"碰生成器"；否则这条轴按 §54.5 关闭。
        // 判据①仍是语义等价：同一输入 + 清零后的输出数组 ⇒ KD2/KPeer 校验和逐位相同（进断言门）。
        var twoJob = new BenchMeleeScanTwoPassJob
        {
            Positions = pos, ConfigId = cfg, Team = team, CellStart = cellStart, SortedIndex = sorted,
            ScanOrder = scanOrder, KD2 = kd2, KPeer = kpeer,
            Props = packJob.Props,
        };
        Console.WriteLine("  -- melee_pack (baseline) vs melee_twopass (live-set gate, 09 s54.5): 8 interleaved pairs --");
        var pack2Med = new System.Collections.Generic.List<double>();
        var twoMed = new System.Collections.Generic.List<double>();
        var twoRatios = new System.Collections.Generic.List<double>();
        double pack2Sum = 0, twoSum = 0, pack2Peer = 0, twoPeer = 0;
        for (int i = 0; i < 8; i++)
        {
            bool packFirst = (i % 2) == 0;
            double b, v;
            if (packFirst)
            {
                ZeroOut(kd2, kpeer);
                b = TimeArm2("melee_pack", 6, () => { packJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
                pack2Sum = SumKd2(kd2); pack2Peer = SumKpeer(kpeer);
                ZeroOut(kd2, kpeer);
                v = TimeArm2("melee_twopass", 6, () => { twoJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
                twoSum = SumKd2(kd2); twoPeer = SumKpeer(kpeer);
            }
            else
            {
                ZeroOut(kd2, kpeer);
                v = TimeArm2("melee_twopass", 6, () => { twoJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
                twoSum = SumKd2(kd2); twoPeer = SumKpeer(kpeer);
                ZeroOut(kd2, kpeer);
                b = TimeArm2("melee_pack", 6, () => { packJob.Schedule(N, 0).Complete(); }, () => SumKd2(kd2));
                pack2Sum = SumKd2(kd2); pack2Peer = SumKpeer(kpeer);
            }
            pack2Med.Add(b); twoMed.Add(v); twoRatios.Add(v / b);
            Console.WriteLine($"      pair {i + 1}: pack={b,7:F1}  twopass={v,7:F1}  two/pack={v / b:F3}");
        }
        ReportBench("B2 melee_twopass: same KD2 output as melee_pack", pack2Sum == twoSum,
            $"sum_pack={pack2Sum:R} sum_twopass={twoSum:R}");
        ReportBench("B2 melee_twopass: same KPeer output as melee_pack", pack2Peer == twoPeer,
            $"sum_pack={pack2Peer:R} sum_twopass={twoPeer:R}");
        PrintStat("melee_pack    ", pack2Med);
        PrintStat("melee_twopass ", twoMed);
        PrintStat("two/pack ratio", twoRatios);
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

    /// <summary>B2 实验：把输出数组清零，保证两条臂从**同一前置状态**开始（内核会读回自己写下的槽位）。</summary>
    static void ZeroOut(NativeArray<float> kd2, NativeArray<int> kpeer)
    {
        for (int i = 0; i < kd2.Length; i++) kd2[i] = 0f;
        for (int i = 0; i < kpeer.Length; i++) kpeer[i] = 0;
    }

    /// <summary>B2 实验：KPeer 输出校验和（等价性对拍的第二个口径）。</summary>
    static double SumKpeer(NativeArray<int> kpeer)
    {
        double s = 0;
        for (int i = 0; i < kpeer.Length; i++) s += kpeer[i];
        return s;
    }

    /// <summary>B2 实验：打印一组固定数量样本的中位与 p95（臂内 8 对，臂序已交替）。</summary>
    static void PrintStat(string name, System.Collections.Generic.List<double> values)
    {
        var s = new System.Collections.Generic.List<double>(values);
        s.Sort();
        double med = s.Count % 2 == 1 ? s[s.Count / 2] : 0.5 * (s[s.Count / 2 - 1] + s[s.Count / 2]);
        int idx = (int)System.Math.Ceiling(0.95 * s.Count) - 1;
        if (idx < 0) idx = 0;
        if (idx >= s.Count) idx = s.Count - 1;
        Console.WriteLine($"      {name}: median {med,7:F1} ns/elem  p95 {s[idx],7:F1}  (n={s.Count})");
    }

    /// <summary>bench 模式的断言失败计数（顶层的 <c>failed</c> 局部量在静态类里不可见）。</summary>
    public static int ExtraFailures;

    static void ReportBench(string name, bool ok, string detail)
    {
        if (!ok) ExtraFailures++;
        Console.WriteLine($"  {name,-52}: {(ok ? "✅ PASS" : "❌ FAIL")}{(ok || detail.Length == 0 ? "" : "  " + detail)}");
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
