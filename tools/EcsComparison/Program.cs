using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Arch.Core;
using Arch.Core.Utils;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using NativeTranspiler;
using ArchWorld = Arch.Core.World;
using EntWorld = EntJoy.ECS.World;

if (args.Length > 0 && args[0] == "--matched-unity")
{
    foreach (int components in new[] { 2, 3 })
    {
        var probe = new SystemComparison { Components = components };
        try
        {
            probe.Setup();
            double[] times = ComparisonTiming.Measure(probe.EntJoyJobCpp);
            probe.VerifyEnt(4 + ComparisonTiming.Warmup + ComparisonTiming.Samples * ComparisonTiming.Repetitions);
            Console.WriteLine($"ENTJOY_MATCHED components={components} chunks={probe.ChunkCount()} {ComparisonTiming.Describe(times)}");
        }
        finally { probe.Cleanup(); }
    }
}
else if (args.Length > 0 && args[0] == "--matched-filtered")
    FilteredComparison.Run();
else if (args.Length > 0 && args[0] == "--matched-dependency")
    DependencyComparison.Run();
else if (args.Length > 0 && args[0] == "--matched-query")
    QueryMatchedComparison.Run();
else if (args.Length > 0 && args[0] == "--traversal-matrix")
    TraversalMatrix.Run(false);
else if (args.Length > 0 && args[0] == "--query-matrix")
    TraversalMatrix.Run(false, queryOnly: true);
else if (args.Length > 0 && args[0] == "--chunk-job-matrix")
    TraversalMatrix.Run(false, chunkJobOnly: true);
else if (args.Length > 0 && args[0] == "--raw-chunk-matrix")
    TraversalMatrix.Run(false, rawChunkOnly: true);
else if (args.Length > 0 && args[0] == "--verify-traversal-matrix")
    TraversalMatrix.Run(true);
else if (args.Length > 0 && args[0] == "--verify-native-filters")
    NativeFilterVerification.Run();
else if (args.Length > 0 && args[0] == "--job-overhead")
{
    foreach (int count in TraversalProtocol.EntityCounts)
    foreach (int components in new[] { 2, 3 })
    {
        var probe = new SystemComparison { Components = components, EntityCount = count };
        try
        {
            probe.Setup();
            var entries = new (string Name, Action Run)[]
            {
                ("job", probe.EntJoyJobCSharp),
                ("job-empty-body", probe.EntJoyEmptyJobCSharp),
                ("chunk-job", probe.EntJoyChunkJobCSharp),
                ("chunk", probe.EntJoyChunk),
            };
            foreach (var entry in entries)
            {
                double[] times = ComparisonTiming.Measure(entry.Run, TraversalProtocol.Warmup, TraversalProtocol.Repetitions);
                Array.Sort(times);
                Console.WriteLine($"JOBOVERHEAD entities={count} components={components} entry={entry.Name} median_us={(times[5] + times[6]) / 2:F1}");
            }
        }
        finally { probe.Cleanup(); }
    }
}
else if (args.Length > 0 && args[0] == "--body-bisect")
{
// 2 组件 100k：定位 IJobEntity 生成体相对手写体的开销来源，全部走同一 Job 遍历。
    var probe = new SystemComparison { Components = 2, EntityCount = 100_000 };
    try
    {
        probe.Setup();
        var entries = new (string Name, Action Run)[]
        {
            ("job", probe.EntJoyJobCSharp),
            ("job-empty-body", probe.EntJoyEmptyJobCSharp),
            ("chunk-job", probe.EntJoyChunkJobCSharp),
            ("chunk-job-noop", probe.EntJoyChunkJobNoop),
            ("chunk", probe.EntJoyChunk),
        };
        for (int round = 0; round < 3; round++)
        {
            int first = 0, last = entries.Length - 1;
            for (int k = 0; k < entries.Length; k++)
            {
                var entry = entries[round % 2 == 0 ? first + k : last - k];
                double[] times = ComparisonTiming.Measure(entry.Run, TraversalProtocol.Warmup, TraversalProtocol.Repetitions);
                Array.Sort(times);
                Console.WriteLine($"BODY_ROW round={round + 1} entry={entry.Name} median_us={(times[5] + times[6]) / 2:F1}");
            }
        }
    }
    finally { probe.Cleanup(); }
}
else if (args.Length > 0 && args[0] == "--struct")
{
    // 结构变更基准：创建/销毁实体、添加/移除组件。单次计时，按轮取最小。
    int n = args.Length > 1 ? int.Parse(args[1]) : 100_000;
    int rounds = args.Length > 2 ? int.Parse(args[2]) : 5;
    StructuralBench.Run(n, rounds);
}
else if (args.Length > 0 && args[0] == "--query-snapshot")
{
    // 结构变更后的首次查询会重建 Chunk 快照：成对测量重建与复用两段的耗时和托管分配。
    int qsEntities = args.Length > 1 ? int.Parse(args[1]) : 100_000;
    int qsRounds = args.Length > 2 ? int.Parse(args[2]) : 300;
    QuerySnapshotBench.Run(qsEntities, qsRounds);
}
else if (args.Length > 0 && args[0] == "--chunk-layout")
{
    // 布局诊断：打印两侧每档的块数、块内实体数与块地址分布，用于确认对照臂处理的是同一份工作。
    foreach (int components in new[] { 2, 3 })
    {
        var probe = new SystemComparison { Components = components, EntityCount = 100_000 };
        try
        {
            probe.Setup();
            probe.DumpChunkLayout();
        }
        finally { probe.Cleanup(); }
    }
}
else if (args.Length > 0 && args[0] == "--ab-pair")
{
// 成对交错：循环里只放被测两臂，轮内 A/B 顺序反转，剔除其它臂的邻居效应对比值的干扰。
    string pairA = args.Length > 1 ? args[1] : "query";
    string pairB = args.Length > 2 ? args[2] : "arch-query";
    int pairRounds = args.Length > 3 ? int.Parse(args[3]) : 15;
    // 可选容量覆盖：把 EntJoy 的块容量强行对齐到对照实现，用于分离「块数/容量」与「实现」两个因素。
    int pairCapacity = args.Length > 4 ? int.Parse(args[4]) : 0;
    EntJoy.ECS.Archetype.ChunkCapacityOverride = pairCapacity;
    foreach (int components in new[] { 2, 3 })
    {
        var probe = new SystemComparison { Components = components, EntityCount = 100_000 };
        try
        {
            probe.Setup();
            var arms = new (string Name, Action Run)[]
            {
                ("query", probe.EntJoy),
                ("chunk", probe.EntJoyChunk),
                ("job", probe.EntJoyJobCSharp),
                ("chunk-job", probe.EntJoyChunkJobCSharp),
                ("query-movenext", probe.EntJoyMoveNextOnly),
                ("query-read1", probe.EntJoyRead1Only),
                ("arch-query", probe.Arch),
                ("arch-query-movenext", probe.ArchMoveNextOnly),
                ("arch-chunk", probe.ArchChunk),
                ("chunk-movenext", probe.EntJoyChunkMoveNext),
                ("chunk-length", probe.EntJoyChunkLength),
                ("writable-guard", probe.WritableGuardOnly),
                ("absent-remove", probe.RemoveAbsentOnly),
            ("absent-destroy", probe.DestroyAbsentOnly),
                ("arch-chunk-length", probe.ArchChunkLength),
                ("arch-chunk-movenext", probe.ArchChunkMoveNext),
            };
            Action runA = null, runB = null;
            foreach (var arm in arms)
            {
                if (arm.Name == pairA) runA = arm.Run;
                if (arm.Name == pairB) runB = arm.Run;
            }
            if (runA == null || runB == null) throw new ArgumentException($"Unknown arm in pair: {pairA} / {pairB}");
            var ratios = new List<double>();
            // 最小值是可复现的统计量：同一构建内偶发慢轮会把中位抬到 20% 以外，最小值两次重复相差不到 1%。
            double minA = double.MaxValue, minB = double.MaxValue;
            for (int round = 0; round < pairRounds; round++)
            {
                double a, b;
                if (round % 2 == 0) { a = MeasureArm(runA); b = MeasureArm(runB); }
                else { b = MeasureArm(runB); a = MeasureArm(runA); }
                if (a < minA) minA = a;
                if (b < minB) minB = b;
                ratios.Add(a / b);
                Console.WriteLine($"ABPAIR_ROW round={round + 1} components={components} a={pairA} b={pairB} a_us={a:F1} b_us={b:F1} ratio={a / b:F4}");
            }
            ratios.Sort();
            int faster = 0;
            foreach (double r in ratios) if (r < 1) faster++;
            Console.WriteLine($"ABPAIR_SUM components={components} a={pairA} b={pairB} n={ratios.Count} median_ratio={ratios[ratios.Count / 2]:F4} faster={faster} a_min_us={minA:F1} b_min_us={minB:F1} min_ratio={minA / minB:F4}");

            static double MeasureArm(Action run)
            {
                double[] times = ComparisonTiming.Measure(run, TraversalProtocol.Warmup, TraversalProtocol.Repetitions);
                Array.Sort(times);
                return (times[5] + times[6]) / 2;
            }
        }
        finally { probe.Cleanup(); }
    }
}
else if (args.Length > 0 && args[0] == "--ab")
{
// 交错重复：同一进程内多轮测同一组入口，轮内顺序反转，用于消除热漂移后的配对比较。
    int abRounds = args.Length > 1 ? int.Parse(args[1]) : 6;
    int abCapacity = args.Length > 2 ? int.Parse(args[2]) : 0;
    EntJoy.ECS.Archetype.ChunkCapacityOverride = abCapacity;
    try
    {
    foreach (int components in new[] { 2, 3 })
    {
        var probe = new SystemComparison { Components = components, EntityCount = 100_000 };
        try
        {
            probe.Setup();
            for (int round = 0; round < abRounds; round++)
            {
                var entries = new (string Name, Action Run)[]
                {
                    ("query", probe.EntJoy),
                    ("chunk", probe.EntJoyChunk),
                    ("job", probe.EntJoyJobCSharp),
                    ("chunk-job", probe.EntJoyChunkJobCSharp),
                    ("query-movenext", probe.EntJoyMoveNextOnly),
                    ("query-read1", probe.EntJoyRead1Only),
                    ("arch-query", probe.Arch),
                    ("arch-query-movenext", probe.ArchMoveNextOnly),
                    ("arch-chunk", probe.ArchChunk),
                };
                int first = 0, last = entries.Length - 1;
                for (int k = 0; k < entries.Length; k++)
                {
                    var entry = entries[round % 2 == 0 ? first + k : last - k];
                    double[] times = ComparisonTiming.Measure(entry.Run, TraversalProtocol.Warmup, TraversalProtocol.Repetitions);
                    Array.Sort(times);
                    Console.WriteLine($"AB_ROW round={round + 1} components={components} entry={entry.Name} median_us={(times[5] + times[6]) / 2:F1}");
                }
            }
        }
        finally { probe.Cleanup(); }
    }
    }
    finally { EntJoy.ECS.Archetype.ChunkCapacityOverride = 0; }
}
else if (args.Length > 0 && args[0] == "--allocation-probe")
{
    JobScheduler.Initialize(2);
    if (!JobScheduler.IsNative) throw new InvalidOperationException("Native scheduler required for allocation probe");
    try
    {
        foreach (int components in new[] { 2, 3 })
        {
            var probe = new SystemComparison { Components = components };
            probe.Setup();
            for (int i = 0; i < 100; i++) probe.EntJoyJobCpp();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100_000; i++) probe.EntJoyJobCpp();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"C++ Job ({components} components) bytes/call: {allocated / 100_000.0}");
            probe.VerifyEnt(100_104);
            for (int i = 0; i < 100; i++) probe.EntJoy();
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100_000; i++) probe.EntJoy();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"Query ({components} components) bytes/call: {allocated / 100_000.0}");
            probe.VerifyEnt(200_204);
            probe.VerifyCacheRebuild();
            probe.VerifyEnabledFilter();
            probe.VerifyLargeEnabledFilter();
            probe.VerifyEnabledDependency();
            probe.Cleanup();
        }
    }
    finally { JobScheduler.Shutdown(); }
}
else
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

// 结构变更基准：两侧用相同的逐实体调用语义，单次计时、按轮取最小；复位一律在计时之外。
internal static class StructuralBench
{
    private static readonly string[] Ops = { "create", "add", "remove", "destroy" };

    // 逐轮交替两侧、逐操作成对：同一轮里 EntJoy 与 Arch 只隔一次复位，比值可跨构建比较。
    public static void Run(int n, int rounds)
    {
        Console.WriteLine($"STRUCT_HEAD entities={n} rounds={rounds}");
        // 与 Arch 侧对称：类型在这里解析一次，循环里传预解析好的 ComponentType。
        var entComps2 = new[] { EntJoy.ECS.ComponentTypeManager.GetComponentType(typeof(A)), EntJoy.ECS.ComponentTypeManager.GetComponentType(typeof(B)) };
        var entComps3 = new[] { EntJoy.ECS.ComponentTypeManager.GetComponentType(typeof(A)), EntJoy.ECS.ComponentTypeManager.GetComponentType(typeof(B)), EntJoy.ECS.ComponentTypeManager.GetComponentType(typeof(C)) };
        var archTypes2 = new Arch.Core.Utils.ComponentType[] { typeof(A), typeof(B) };
        var archTypes3 = new Arch.Core.Utils.ComponentType[] { typeof(A), typeof(B), typeof(C) };
        var sw = new System.Diagnostics.Stopwatch();
        var entMin = new double[Ops.Length];
        var archMin = new double[Ops.Length];
        for (int i = 0; i < Ops.Length; i++) { entMin[i] = double.MaxValue; archMin[i] = double.MaxValue; }

        using var world = new EntWorld("structural");
        var m = world.EntityManager;
        var arch = ArchWorld.Create();
        var entBuffer = new EntJoy.ECS.Entity[n];
        var archBuffer = new Arch.Core.Entity[n];

        for (int r = 1; r <= rounds; r++)
        {
            bool entFirst = r % 2 == 1;
            for (int o = 0; o < Ops.Length; o++)
            {
                if (entFirst)
                {
                    EntOp(o, m, entBuffer, n, entComps2, entComps3, sw, ref entMin[o], r);
                    ArchOp(o, arch, archBuffer, n, archTypes2, archTypes3, sw, ref archMin[o], r);
                }
                else
                {
                    ArchOp(o, arch, archBuffer, n, archTypes2, archTypes3, sw, ref archMin[o], r);
                    EntOp(o, m, entBuffer, n, entComps2, entComps3, sw, ref entMin[o], r);
                }
            }
        }

        for (int i = 0; i < Ops.Length; i++)
            Console.WriteLine(
                $"STRUCT_RATIO op={Ops[i]} entjoy_us={entMin[i]:F1} arch_us={archMin[i]:F1} ratio={entMin[i] / archMin[i]:F4} " +
                $"ns_entjoy={entMin[i] * 1000.0 / n:F2} ns_arch={archMin[i] * 1000.0 / n:F2}");
        ArchWorld.Destroy(arch);
    }

    private static void EntOp(int op, EntityManager m, EntJoy.ECS.Entity[] buf, int n,
        EntJoy.ECS.ComponentType[] c2, EntJoy.ECS.ComponentType[] c3, System.Diagnostics.Stopwatch sw, ref double min, int round)
    {
        switch (op)
        {
            case 0:
                sw.Restart();
                for (int i = 0; i < n; i++) buf[i] = m.NewEntity(c2);
                sw.Stop();
                Report("create", "EntJoy", round, sw.Elapsed.TotalMicroseconds, ref min);
                for (int i = 0; i < n; i++) m.DestroyEntity(buf[i]);
                break;
            case 1:
                for (int i = 0; i < n; i++) buf[i] = m.NewEntity(c2);
                sw.Restart();
                for (int i = 0; i < n; i++) m.AddComponent<C>(buf[i], default);
                sw.Stop();
                Report("add", "EntJoy", round, sw.Elapsed.TotalMicroseconds, ref min);
                for (int i = 0; i < n; i++) m.DestroyEntity(buf[i]);
                break;
            case 2:
                for (int i = 0; i < n; i++) buf[i] = m.NewEntity(c3);
                sw.Restart();
                for (int i = 0; i < n; i++) m.RemoveComponent<C>(buf[i]);
                sw.Stop();
                Report("remove", "EntJoy", round, sw.Elapsed.TotalMicroseconds, ref min);
                for (int i = 0; i < n; i++) m.DestroyEntity(buf[i]);
                break;
            default:
                for (int i = 0; i < n; i++) buf[i] = m.NewEntity(c2);
                sw.Restart();
                for (int i = 0; i < n; i++) m.DestroyEntity(buf[i]);
                sw.Stop();
                Report("destroy", "EntJoy", round, sw.Elapsed.TotalMicroseconds, ref min);
                break;
        }
    }

    private static void ArchOp(int op, Arch.Core.World arch, Arch.Core.Entity[] buf, int n,
        Arch.Core.Utils.ComponentType[] t2, Arch.Core.Utils.ComponentType[] t3,
        System.Diagnostics.Stopwatch sw, ref double min, int round)
    {
        switch (op)
        {
            case 0:
                sw.Restart();
                for (int i = 0; i < n; i++) buf[i] = arch.Create(t2);
                sw.Stop();
                Report("create", "Arch", round, sw.Elapsed.TotalMicroseconds, ref min);
                for (int i = 0; i < n; i++) arch.Destroy(buf[i]);
                break;
            case 1:
                for (int i = 0; i < n; i++) buf[i] = arch.Create(t2);
                sw.Restart();
                for (int i = 0; i < n; i++) arch.Add<C>(buf[i]);
                sw.Stop();
                Report("add", "Arch", round, sw.Elapsed.TotalMicroseconds, ref min);
                for (int i = 0; i < n; i++) arch.Destroy(buf[i]);
                break;
            case 2:
                for (int i = 0; i < n; i++) buf[i] = arch.Create(t3);
                sw.Restart();
                for (int i = 0; i < n; i++) arch.Remove<C>(buf[i]);
                sw.Stop();
                Report("remove", "Arch", round, sw.Elapsed.TotalMicroseconds, ref min);
                for (int i = 0; i < n; i++) arch.Destroy(buf[i]);
                break;
            default:
                for (int i = 0; i < n; i++) buf[i] = arch.Create(t2);
                sw.Restart();
                for (int i = 0; i < n; i++) arch.Destroy(buf[i]);
                sw.Stop();
                Report("destroy", "Arch", round, sw.Elapsed.TotalMicroseconds, ref min);
                break;
        }
    }

    private static void Report(string op, string framework, int round, double us, ref double min)
    {
        if (us < min) min = us;
        Console.WriteLine($"STRUCT_ROW op={op} framework={framework} round={round} us={us:F1}");
    }
}

public struct A : IComponentData { public int Value; }
public struct B : IComponentData { public int Value; }
public struct C : IComponentData { public int Value; }
public struct Enabled : IComponentData, IEnableableComponent { }
public struct Enabled2 : IComponentData, IEnableableComponent { }

public struct EnableAfterGate : IJobChunk
{
    public System.Threading.ManualResetEventSlim Release;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Enabled writer timed out");
        for (int i = 0; i < chunk.Count; i++) chunk.SetComponentEnabled<Enabled>(i, i % 2 == 0);
    }
}

public struct EntUpdateTwo : IJobEntity
{
    public void Execute(ref A a, in B b) { a.Value += b.Value; }
}

public struct EntUpdateThree : IJobEntity
{
    public void Execute(ref A a, in B b, in C c) { a.Value += b.Value + c.Value; }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct EntUpdateTwoCpp : IJobEntity
{
    public void Execute(ref A a, in B b) { a.Value += b.Value; }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct EntUpdateThreeCpp : IJobEntity
{
    public void Execute(ref A a, in B b, in C c) { a.Value += b.Value + c.Value; }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct EntEmptyTwoCpp : IJobEntity
{
    public void Execute(ref A a, in B b) { }
}

public struct EntEmptyTwo : IJobEntity
{
    public void Execute(ref A a, in B b) { }
}

public struct EntEmptyThree : IJobEntity
{
    public void Execute(ref A a, in B b, in C c) { }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct EntEmptyThreeCpp : IJobEntity
{
    public void Execute(ref A a, in B b, in C c) { }
}

[MemoryDiagnoser]
public class SystemComparison
{
    [Params(2, 3)] public int Components { get; set; }
    public int EntityCount { get; set; } = 100_000;
    private EntWorld ent;
    private ArchWorld arch;
    private QueryDescription query;
    private QueryBuilder entQuery;
    private EntityQuery entCachedQuery;
    private Arch.Core.Query archCachedQuery;
    private UpdateTwo updateTwo;
    private UpdateThree updateThree;
    private UpdateTwoCount updateTwoCount;
    private UpdateThreeCount updateThreeCount;

    // 只累计实体数、不读写组件：与 EntJoy 的 query-movenext 同口径，用于拆分枚举开销与体开销。
    public struct UpdateTwoCount : IForEach<A, B>
    {
        public int Count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(ref A a, ref B b) => Count++;
    }

    public struct UpdateThreeCount : IForEach<A, B, C>
    {
        public int Count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(ref A a, ref B b, ref C c) => Count++;
    }

    public struct UpdateTwo : IForEach<A, B>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(ref A a, ref B b)
        {
            var value = a;
            value.Value += b.Value;
            a = value;
        }
    }

    public struct UpdateThree : IForEach<A, B, C>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(ref A a, ref B b, ref C c)
        {
            var value = a;
            value.Value += b.Value + c.Value;
            a = value;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void AddTwoSpans(Span<A> a, ReadOnlySpan<B> b)
    {
        if (a.Length != b.Length) throw new InvalidOperationException("Component span lengths differ");
        ref A firstA = ref MemoryMarshal.GetReference(a);
        ref B firstB = ref MemoryMarshal.GetReference(b);
        AddTwoReferences(ref firstA, ref firstB, a.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void AddThreeSpans(Span<A> a, ReadOnlySpan<B> b, ReadOnlySpan<C> c)
    {
        if (a.Length != b.Length || a.Length != c.Length) throw new InvalidOperationException("Component span lengths differ");
        ref A firstA = ref MemoryMarshal.GetReference(a);
        ref B firstB = ref MemoryMarshal.GetReference(b);
        ref C firstC = ref MemoryMarshal.GetReference(c);
        AddThreeReferences(ref firstA, ref firstB, ref firstC, a.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddTwoReferences(ref A a, ref B b, int count)
    {
        for (int i = count - 1; i >= 0; i--) Unsafe.Add(ref a, i).Value += Unsafe.Add(ref b, i).Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddThreeReferences(ref A a, ref B b, ref C c, int count)
    {
        for (int i = count - 1; i >= 0; i--)
            Unsafe.Add(ref a, i).Value += Unsafe.Add(ref b, i).Value + Unsafe.Add(ref c, i).Value;
    }

    [GlobalSetup]
    public void Setup()
    {
        ent = new EntWorld("comparison");
        arch = ArchWorld.Create();
        Type[] types = Components == 2
            ? new Type[] { typeof(A), typeof(B) }
            : new Type[] { typeof(A), typeof(B), typeof(C) };
        Arch.Core.Utils.ComponentType[] archTypes = Components == 2
            ? new Arch.Core.Utils.ComponentType[] { typeof(A), typeof(B) }
            : new Arch.Core.Utils.ComponentType[] { typeof(A), typeof(B), typeof(C) };
        query = new QueryDescription { All = archTypes };
        entQuery = Components == 2 ? new QueryBuilder().WithAll<A, B>() : new QueryBuilder().WithAll<A, B, C>();
        arch.Reserve(archTypes, EntityCount);
        for (int i = 0; i < EntityCount; i++)
        {
            var e = ent.EntityManager.NewEntity(types);
            ent.EntityManager.Set(e, new B { Value = 1 });
            if (Components == 3) ent.EntityManager.Set(e, new C { Value = 2 });
            var a = arch.Create(archTypes);
            arch.Set(a, new B { Value = 1 });
            if (Components == 3) arch.Set(a, new C { Value = 2 });
        }
        entCachedQuery = ent.GetOrCreateEntityQuery(entQuery);
        archCachedQuery = arch.Query(in query);
        EntJoy();
        VerifyEnt(1);
        Arch();
        VerifyArch(1);
        EntJoyChunk();
        VerifyEnt(2);
        ArchChunk();
        VerifyArch(2);
        EntJoyJobCSharp();
        VerifyEnt(3);
        var entry = Components == 2 ? "Get_SharpNative_Job__global_namespace__EntUpdateTwoCpp_Execute_AdapterPtr" : "Get_SharpNative_Job__global_namespace__EntUpdateThreeCpp_Execute_AdapterPtr";
        if (NativeJobScheduler.GetNativeExportPtr(entry) == IntPtr.Zero) throw new InvalidOperationException("Native job export missing: " + entry);
        EntJoyJobCpp();
        VerifyEnt(4);
        EntJoyEmptyJobCpp();
        VerifyEnt(4);
    }

    public void VerifyEnt(int iterations)
    {
        int count = 0;
        foreach (var item in ent.Query<A, B>())
        {
            if (item.Comp0.Value != iterations * (Components == 2 ? 1 : 3)) throw new InvalidOperationException("EntJoy result mismatch");
            count++;
        }
        if (count != EntityCount) throw new InvalidOperationException("EntJoy entity count mismatch");
    }

    public int ChunkCount()
    {
        int count = 0;
        if (Components == 2) { foreach (var chunk in ent.QueryChunks<A, B>()) count++; }
        else { foreach (var chunk in ent.QueryChunks<A, B, C>()) count++; }
        return count;
    }

    public int ArchChunkCount()
    {
        int count = 0;
        foreach (ref var chunk in arch.Query(in query)) count++;
        return count;
    }

    public void VerifyInputs(bool entJoy)
    {
        if (entJoy)
        {
            foreach (var item in ent.Query<A, B>())
                if (item.Comp1.Value != 1) throw new InvalidOperationException("EntJoy B input changed");
            if (Components == 3)
                foreach (var item in ent.Query<A, C>())
                    if (item.Comp1.Value != 2) throw new InvalidOperationException("EntJoy C input changed");
        }
        else
        {
            foreach (ref var chunk in arch.Query(in query))
            {
                foreach (var value in chunk.GetSpan<B>().Slice(0, chunk.Size))
                    if (value.Value != 1) throw new InvalidOperationException("Arch B input changed");
                if (Components == 3)
                    foreach (var value in chunk.GetSpan<C>().Slice(0, chunk.Size))
                        if (value.Value != 2) throw new InvalidOperationException("Arch C input changed");
            }
        }
    }

    public void EntJoyChunkJobCSharp()
    {
        if (Components == 2) new EntChunkTwo().Run(entQuery, ent);
        else new EntChunkThree().Run(entQuery, ent);
    }

    public void EntJoyChunkJobNoop()
    {
        new EntChunkNoop().Run(entQuery, ent);
    }

    public void EntJoyChunkJobCpp()
    {
        if (Components == 2) new EntChunkTwoCpp().Run(entQuery, ent);
        else new EntChunkThreeCpp().Run(entQuery, ent);
    }

    public void VerifyCacheRebuild()
    {
        var entity = Components == 2
            ? ent.EntityManager.NewEntity(typeof(A), typeof(B))
            : ent.EntityManager.NewEntity(typeof(A), typeof(B), typeof(C));
        ent.EntityManager.Set(entity, new B { Value = 1 });
        if (Components == 3) ent.EntityManager.Set(entity, new C { Value = 2 });
        EntJoyJobCpp();
        global::EntJoy.ECS.JobSystem.ChunkJobScheduler.ClearRawChunkScheduleCaches(ent.EntityManager);
        EntJoyJobCpp();
        if (ent.EntityManager.GetComponentReadOnly<A>(entity).Value != 2 * (Components == 2 ? 1 : 3))
            throw new InvalidOperationException("C++ job cache rebuild failed");
    }

    public void VerifyEnabledFilter()
    {
        using var world = new EntWorld("filter-verification");
        var entities = new EntJoy.ECS.Entity[260];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.EntityManager.NewEntity(typeof(A), typeof(B), typeof(Enabled), typeof(Enabled2));
            world.EntityManager.Set(entities[i], new B { Value = 1 });
        }
        var filtered = new QueryBuilder().WithAll<A, B>().WithEnabled<Enabled>();
        for (int phase = 0; phase < 3; phase++)
        {
            for (int i = 0; i < entities.Length; i++)
                world.EntityManager.SetComponentEnabled<Enabled>(entities[i], phase == 0 ? i % 3 == 0 : phase == 2);
            new EntUpdateTwoCpp().Run(filtered, world);
            for (int i = 0; i < entities.Length; i++)
            {
                int expected = (i % 3 == 0 ? 1 : 0) + (phase == 2 ? 1 : 0);
                if (world.EntityManager.GetComponentReadOnly<A>(entities[i]).Value != expected)
                    throw new InvalidOperationException($"C++ job enabled filter failed at phase {phase}, entity {i}");
            }
        }
        var both = filtered.WithEnabled<Enabled2>();
        for (int i = 0; i < entities.Length; i++)
            world.EntityManager.SetComponentEnabled<Enabled2>(entities[i], i % 2 == 0);
        new EntUpdateTwoCpp().Run(both, world);
        for (int i = 0; i < entities.Length; i++)
        {
            int expected = (i % 3 == 0 ? 1 : 0) + 1 + (i % 2 == 0 ? 1 : 0);
            if (world.EntityManager.GetComponentReadOnly<A>(entities[i]).Value != expected)
                throw new InvalidOperationException($"C++ job multiple enabled filters failed at entity {i}");
        }
        var added = world.EntityManager.NewEntity(typeof(A), typeof(B), typeof(Enabled), typeof(Enabled2));
        world.EntityManager.Set(added, new B { Value = 1 });
        new EntUpdateTwoCpp().Run(filtered, world);
        if (world.EntityManager.GetComponentReadOnly<A>(added).Value != 1)
            throw new InvalidOperationException("C++ job enabled filter cache rebuild failed");
    }

    public void VerifyLargeEnabledFilter()
    {
        int previous = global::EntJoy.ECS.Archetype.ChunkCapacityOverride;
        global::EntJoy.ECS.Archetype.ChunkCapacityOverride = 300;
        try
        {
            using var world = new EntWorld("large-filter-verification");
            var entities = new EntJoy.ECS.Entity[299];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = world.EntityManager.NewEntity(typeof(A), typeof(B), typeof(Enabled), typeof(Enabled2));
                world.EntityManager.Set(entities[i], new B { Value = 1 });
                world.EntityManager.SetComponentEnabled<Enabled>(entities[i], i % 2 == 0);
                world.EntityManager.SetComponentEnabled<Enabled2>(entities[i], i % 3 == 0);
            }
            var query = new QueryBuilder().WithAll<A, B>().WithEnabled<Enabled>().WithEnabled<Enabled2>();
            new EntUpdateTwoCpp().Run(query, world);
            for (int i = 0; i < entities.Length; i++)
                if (world.EntityManager.GetComponentReadOnly<A>(entities[i]).Value != (i % 6 == 0 ? 1 : 0))
                    throw new InvalidOperationException($"C++ job large enabled filter failed at entity {i}");
        }
        finally { global::EntJoy.ECS.Archetype.ChunkCapacityOverride = previous; }
    }

    public void VerifyEnabledDependency()
    {
        using var world = new EntWorld("filter-dependency-verification");
        using var release = new System.Threading.ManualResetEventSlim(false);
        var entities = new global::EntJoy.ECS.Entity[65];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.EntityManager.NewEntity(typeof(A), typeof(B), typeof(Enabled));
            world.EntityManager.Set(entities[i], new B { Value = 1 });
            world.EntityManager.SetComponentEnabled<Enabled>(entities[i], false);
        }
        var all = new QueryBuilder().WithAll<A, B>().WithAll<Enabled>();
        var filtered = all;
        filtered.WithEnabled<Enabled>();
        new EntUpdateTwoCpp().Run(filtered, world);
        var parent = new EnableAfterGate { Release = release }.Schedule(all, world);
        try
        {
            var child = new EntUpdateTwoCpp().Schedule(filtered, world, dependsOn: parent);
            release.Set();
            child.Complete();
            for (int i = 0; i < entities.Length; i++)
                if (world.EntityManager.GetComponentReadOnly<A>(entities[i]).Value != (i % 2 == 0 ? 1 : 0))
                    throw new InvalidOperationException($"C++ job enabled dependency failed at entity {i}");
        }
        finally { release.Set(); parent.Complete(); }
    }

    public void VerifyArch(int iterations)
    {
        var verify = new Verify { Expected = iterations * (Components == 2 ? 1 : 3) };
        arch.InlineQuery<Verify, A>(in query, ref verify);
        if (verify.Count != EntityCount) throw new InvalidOperationException("Arch entity count mismatch");
    }

    public struct Verify : IForEach<A>
    {
        public int Expected;
        public int Count;
        public void Update(ref A a)
        {
            if (a.Value != Expected) throw new InvalidOperationException("Arch result mismatch");
            Count++;
        }
    }

    [Benchmark]
    public void EntJoy()
    {
        if (Components == 2) EntJoyTwo();
        else EntJoyThree();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EntJoyTwo()
    {
        foreach (var item in ent.Query<A, B>())
        {
            ref A target = ref item.Comp0;
            var value = target;
            value.Value += item.ReadOnlyComp1.Value;
            target = value;
        }
    }

    private static int _abSink;

    public void EntJoyMoveNextOnly()
    {
        var enumerator = ent.Query<A, B>().GetEnumerator();
        int count = 0;
        while (enumerator.MoveNext()) count++;
        _abSink += count;
    }

    public void EntJoyRead1Only()
    {
        var enumerator = ent.Query<A, B>().GetEnumerator();
        int count = 0;
        while (enumerator.MoveNext()) count += enumerator.Current.ReadOnlyComp1.Value;
        _abSink += count;
    }

    public void ArchMoveNextOnly()
    {
        if (Components == 2)
        {
            updateTwoCount = default;
            arch.InlineQuery<UpdateTwoCount, A, B>(in query, ref updateTwoCount);
            _abSink += updateTwoCount.Count;
        }
        else
        {
            updateThreeCount = default;
            arch.InlineQuery<UpdateThreeCount, A, B, C>(in query, ref updateThreeCount);
            _abSink += updateThreeCount.Count;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EntJoyThree()
    {
        foreach (var item in ent.Query<A, B, C>())
        {
            ref A target = ref item.Comp0;
            var value = target;
            value.Value += item.ReadOnlyComp1.Value + item.ReadOnlyComp2.Value;
            target = value;
        }
    }

    [Benchmark]
    public void Arch()
    {
        if (Components == 2) ArchTwo();
        else ArchThree();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ArchTwo() => arch.InlineQuery<UpdateTwo, A, B>(in query, ref updateTwo);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ArchThree() => arch.InlineQuery<UpdateThree, A, B, C>(in query, ref updateThree);

    [Benchmark]
    public void EntJoyChunk()
    {
        if (Components == 2) EntJoyChunkTwo();
        else EntJoyChunkThree();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EntJoyChunkTwo()
    {
        foreach (var chunk in ent.QueryChunks<A, B>())
        {
            var a = chunk.GetSpan0();
            var b = chunk.GetReadOnlySpan1();
            AddTwoSpans(a, b);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EntJoyChunkThree()
    {
        foreach (var chunk in ent.QueryChunks<A, B, C>())
        {
            var a = chunk.GetSpan0();
            var b = chunk.GetReadOnlySpan1();
            var c = chunk.GetReadOnlySpan2();
            AddThreeSpans(a, b, c);
        }
    }

    [Benchmark]
    public void ArchChunk()
    {
        if (Components == 2) ArchChunkTwo();
        else ArchChunkThree();
    }

    // 只数 Chunk、不取任何列：把 chunk 档总时拆成「枚举块」与「取列+体」。
    public void EntJoyChunkMoveNext()
    {
        int count = 0;
        if (Components == 2) { foreach (var chunk in ent.QueryChunks<A, B>()) count++; }
        else { foreach (var chunk in ent.QueryChunks<A, B, C>()) count++; }
        _abSink += count;
    }

    // 只取每块的长度：把「枚举块」与「取一列」之间的差量单独量出来。
    public void EntJoyChunkLength()
    {
        int count = 0;
        if (Components == 2) { foreach (var chunk in ent.QueryChunks<A, B>()) count += chunk.Length; }
        else { foreach (var chunk in ent.QueryChunks<A, B, C>()) count += chunk.Length; }
        _abSink += count;
    }

    // 对不含 C 的实体反复调用 RemoveComponent<C>：走到「完成 job + 取结构锁 + 判定不在就返回」，
    // 用来量结构变更的固定开销，不含任何迁移搬运。循环取 1000 次，避免被成对基准的重复次数放大。
    public void RemoveAbsentOnly()
    {
        var target = ent.EntityManager.NewEntity(typeof(A), typeof(B));
        for (int i = 0; i < 1_000; i++) ent.EntityManager.RemoveComponent<C>(target);
        _abSink++;
    }

    // 只调写权限守卫：量它在每实体路径上的单位成本。
    // 对已销毁实体重复 DestroyEntity：走完整入口（守卫、锁、校验）后核心早退，用来量销毁的入口开销。
    public void DestroyAbsentOnly()
    {
        var target = ent.EntityManager.NewEntity(typeof(A), typeof(B));
        ent.EntityManager.DestroyEntity(target);
        for (int i = 0; i < 1_000; i++) ent.EntityManager.DestroyEntity(target);
        _abSink++;
    }

    public void WritableGuardOnly()
    {
        for (int i = 0; i < 100_000; i++) ComponentTypeManager.CheckWritableAccess<A>();
        _abSink++;
    }

    // 布局与工作量诊断：块数、块内实体数、块地址跨度与迭代间距。两侧必须在同一份工作量上比较。
    public unsafe void DumpChunkLayout()
    {
        int chunks = 0, min = int.MaxValue, max = 0, spanCap = 0;
        long total = 0;
        long base0 = 0, delta0 = 0, firstBase = 0, prevBase = 0, minGap = long.MaxValue, maxGap = 0;
        long addrMin = long.MaxValue, addrMax = 0;
        if (Components == 2)
        {
            foreach (var chunk in ent.QueryChunks<A, B>())
            {
                int n = chunk.Length;
                chunks++; total += n;
                if (n < min) min = n;
                if (n > max) max = n;
                var s0 = chunk.GetReadOnlySpan0();
                var s1 = chunk.GetReadOnlySpan1();
                spanCap = s0.Length;
                long cur = (long)Unsafe.AsPointer(ref MemoryMarshal.GetReference(s0));
                if (base0 == 0)
                {
                    base0 = cur;
                    firstBase = cur;
                    delta0 = (long)Unsafe.AsPointer(ref MemoryMarshal.GetReference(s1)) - base0;
                }
                if (addrMin == long.MaxValue || cur < addrMin) addrMin = cur;
                if (cur > addrMax) addrMax = cur;
                if (prevBase != 0)
                {
                    long g = cur - prevBase;
                    if (g < minGap) minGap = g;
                    if (g > maxGap) maxGap = g;
                }
                prevBase = cur;
            }
            Console.WriteLine($"LAYOUT_ROW stack=EntJoy components=2 chunks={chunks} min={min} max={max} total={total} spanLen={spanCap} sizeofA={Unsafe.SizeOf<A>()} sizeofB={Unsafe.SizeOf<B>()} base0_low12={base0 & 0xFFF} ab_delta={delta0} base_span={prevBase - firstBase} min_gap={minGap} max_gap={maxGap} addr_span={addrMax - addrMin}");
        }
        else
        {
            foreach (var chunk in ent.QueryChunks<A, B, C>())
            {
                int n = chunk.Length;
                chunks++; total += n;
                if (n < min) min = n;
                if (n > max) max = n;
                var s0 = chunk.GetReadOnlySpan0();
                var s1 = chunk.GetReadOnlySpan1();
                spanCap = s0.Length;
                if (base0 == 0)
                {
                    base0 = (long)Unsafe.AsPointer(ref MemoryMarshal.GetReference(s0));
                    delta0 = (long)Unsafe.AsPointer(ref MemoryMarshal.GetReference(s1)) - base0;
                }
            }
            Console.WriteLine($"LAYOUT_ROW stack=EntJoy components=3 chunks={chunks} min={min} max={max} total={total} spanLen={spanCap} sizeofA={Unsafe.SizeOf<A>()} sizeofB={Unsafe.SizeOf<B>()} sizeofC={Unsafe.SizeOf<C>()} base0_low12={base0 & 0xFFF} ab_delta={delta0}");
        }

        int archChunks = 0, archMin = int.MaxValue, archMax = 0, archSpanCap = 0;
        long archTotal = 0, archBase0 = 0, archDelta0 = 0, archFirst = 0, archPrev = 0, archMinGap = long.MaxValue, archMaxGap = 0;
        foreach (ref var chunk in arch.Query(in query))
        {
            int n = chunk.Size;
            archChunks++; archTotal += n;
            if (n < archMin) archMin = n;
            if (n > archMax) archMax = n;
            var s0 = chunk.GetSpan<A>();
            var s1 = chunk.GetSpan<B>();
            archSpanCap = s0.Length;
            long cur = (long)Unsafe.AsPointer(ref MemoryMarshal.GetReference(s0));
            if (archBase0 == 0)
            {
                archBase0 = cur;
                archFirst = cur;
                archDelta0 = (long)Unsafe.AsPointer(ref MemoryMarshal.GetReference(s1)) - archBase0;
            }
            if (archPrev != 0)
            {
                long g = cur - archPrev;
                if (g < archMinGap) archMinGap = g;
                if (g > archMaxGap) archMaxGap = g;
            }
            archPrev = cur;
        }
        Console.WriteLine($"LAYOUT_ROW stack=Arch components={Components} chunks={archChunks} min={archMin} max={archMax} total={archTotal} spanLen={archSpanCap} base0_low12={archBase0 & 0xFFF} ab_delta={archDelta0} base_span={archPrev - archFirst} min_gap={archMinGap} max_gap={archMaxGap}");
    }

    public void ArchChunkLength()
    {
        int count = 0;
        foreach (ref var chunk in arch.Query(in query)) count += chunk.Size;
        _abSink += count;
    }

    public void ArchChunkMoveNext()
    {
        int count = 0;
        foreach (ref var chunk in arch.Query(in query)) count++;
        _abSink += count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ArchChunkTwo()
    {
        foreach (ref var chunk in arch.Query(in query))
        {
            int count = chunk.Size;
            var a = chunk.GetSpan<A>().Slice(0, count);
            var b = chunk.GetSpan<B>().Slice(0, count);
            AddTwoSpans(a, b);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ArchChunkThree()
    {
        foreach (ref var chunk in arch.Query(in query))
        {
            int count = chunk.Size;
            var a = chunk.GetSpan<A>().Slice(0, count);
            var b = chunk.GetSpan<B>().Slice(0, count);
            var c = chunk.GetSpan<C>().Slice(0, count);
            AddThreeSpans(a, b, c);
        }
    }

    public void EntJoyRawChunks()
    {
        if (Components == 2) EntJoyRawTwo();
        else EntJoyRawThree();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private unsafe void EntJoyRawTwo()
    {
        var archetypes = entCachedQuery.MatchingArchetypes;
        for (int index = 0; index < archetypes.Count; index++)
        {
            var archetype = archetypes[index];
            int aIndex = archetype.GetComponentTypeIndex<A>();
            int bIndex = archetype.GetComponentTypeIndex<B>();
            foreach (ref readonly var chunk in archetype.ChunkSpan)
            {
                var a = (A*)chunk.GetComponentArrayPointer(aIndex);
                var b = (B*)chunk.GetComponentArrayPointer(bIndex);
                AddTwoSpans(new Span<A>(a, chunk.EntityCount), new ReadOnlySpan<B>(b, chunk.EntityCount));
            }
        }
    }

    public void ArchCachedChunks()
    {
        if (Components == 2) ArchRawTwo();
        else ArchRawThree();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private unsafe void EntJoyRawThree()
    {
        var archetypes = entCachedQuery.MatchingArchetypes;
        for (int index = 0; index < archetypes.Count; index++)
        {
            var archetype = archetypes[index];
            int aIndex = archetype.GetComponentTypeIndex<A>();
            int bIndex = archetype.GetComponentTypeIndex<B>();
            int cIndex = archetype.GetComponentTypeIndex<C>();
            foreach (ref readonly var chunk in archetype.ChunkSpan)
            {
                var a = (A*)chunk.GetComponentArrayPointer(aIndex);
                var b = (B*)chunk.GetComponentArrayPointer(bIndex);
                var c = (C*)chunk.GetComponentArrayPointer(cIndex);
                AddThreeSpans(new Span<A>(a, chunk.EntityCount), new ReadOnlySpan<B>(b, chunk.EntityCount), new ReadOnlySpan<C>(c, chunk.EntityCount));
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ArchRawTwo()
    {
        foreach (ref var chunk in archCachedQuery)
        {
            AddTwoSpans(chunk.GetSpan<A>().Slice(0, chunk.Size), chunk.GetSpan<B>().Slice(0, chunk.Size));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ArchRawThree()
    {
        foreach (ref var chunk in archCachedQuery)
        {
            AddThreeSpans(chunk.GetSpan<A>().Slice(0, chunk.Size), chunk.GetSpan<B>().Slice(0, chunk.Size), chunk.GetSpan<C>().Slice(0, chunk.Size));
        }
    }

    [Benchmark]
    public void EntJoyJobCSharp()
    {
        if (Components == 2) new EntUpdateTwo().Run(entQuery, ent);
        else new EntUpdateThree().Run(entQuery, ent);
    }

    public void EntJoyEmptyJobCSharp()
    {
        if (Components == 2) new EntEmptyTwo().Run(entQuery, ent);
        else new EntEmptyThree().Run(entQuery, ent);
    }

    [Benchmark]
    public void EntJoyJobCpp()
    {
        if (Components == 2) new EntUpdateTwoCpp().Run(entQuery, ent);
        else new EntUpdateThreeCpp().Run(entQuery, ent);
    }

    [Benchmark]
    public void EntJoyEmptyJobCpp()
    {
        if (Components == 2) new EntEmptyTwoCpp().Run(entQuery, ent);
        else new EntEmptyThreeCpp().Run(entQuery, ent);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        ent.Dispose();
        ArchWorld.Destroy(arch);
    }
}
