using System;
using System.Globalization;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

public struct TraversalSettings : IComponentData { public int Components; }

[DisableAutoCreation]
public partial struct ManagedQueryTraversal : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.GetSingleton<TraversalSettings>().Components == 2)
        {
            foreach (var (a, b) in SystemAPI.Query<RefRW<BenchA>, RefRO<BenchB>>())
                a.ValueRW.Value += b.ValueRO.Value;
        }
        else
        {
            foreach (var (a, b, c) in SystemAPI.Query<RefRW<BenchA>, RefRO<BenchB>, RefRO<BenchC>>())
                a.ValueRW.Value += b.ValueRO.Value + c.ValueRO.Value;
        }
    }
}

[DisableAutoCreation]
[BurstCompile(CompileSynchronously = true)]
public partial struct BurstQueryTraversal : ISystem
{
    [BurstCompile(CompileSynchronously = true)]
    public void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.GetSingleton<TraversalSettings>().Components == 2)
        {
            foreach (var (a, b) in SystemAPI.Query<RefRW<BenchA>, RefRO<BenchB>>())
                a.ValueRW.Value += b.ValueRO.Value;
        }
        else
        {
            foreach (var (a, b, c) in SystemAPI.Query<RefRW<BenchA>, RefRO<BenchB>, RefRO<BenchC>>())
                a.ValueRW.Value += b.ValueRO.Value + c.ValueRO.Value;
        }
    }
}

public struct ManagedChunkTwo : IJobChunk
{
    public ComponentTypeHandle<BenchA> A;
    [ReadOnly] public ComponentTypeHandle<BenchB> B;

    public void Execute(in ArchetypeChunk chunk, int index, bool useEnabledMask, in v128 mask)
    {
        var a = chunk.GetNativeArray(ref A);
        var b = chunk.GetNativeArray(ref B);
        int count = chunk.Count;
        for (int i = 0; i < count; i++)
        {
            var value = a[i];
            value.Value += b[i].Value;
            a[i] = value;
        }
    }
}

public struct ManagedChunkThree : IJobChunk
{
    public ComponentTypeHandle<BenchA> A;
    [ReadOnly] public ComponentTypeHandle<BenchB> B;
    [ReadOnly] public ComponentTypeHandle<BenchC> C;

    public void Execute(in ArchetypeChunk chunk, int index, bool useEnabledMask, in v128 mask)
    {
        var a = chunk.GetNativeArray(ref A);
        var b = chunk.GetNativeArray(ref B);
        var c = chunk.GetNativeArray(ref C);
        int count = chunk.Count;
        for (int i = 0; i < count; i++)
        {
            var value = a[i];
            value.Value += b[i].Value + c[i].Value;
            a[i] = value;
        }
    }
}

[BurstCompile(CompileSynchronously = true)]
public struct BurstChunkTwo : IJobChunk
{
    public ManagedChunkTwo Body;
    public void Execute(in ArchetypeChunk chunk, int index, bool useEnabledMask, in v128 mask)
        => Body.Execute(in chunk, index, useEnabledMask, in mask);
}

[BurstCompile(CompileSynchronously = true)]
public struct BurstChunkThree : IJobChunk
{
    public ManagedChunkThree Body;
    public void Execute(in ArchetypeChunk chunk, int index, bool useEnabledMask, in v128 mask)
        => Body.Execute(in chunk, index, useEnabledMask, in mask);
}

[DisableAutoCreation]
public partial class TraversalJobSystem : SystemBase
{
    private EntityQuery query;
    private int components, method;

    public void Initialize(int componentCount, int entry)
    {
        components = componentCount;
        method = entry;
        query = components == 2
            ? GetEntityQuery(ComponentType.ReadWrite<BenchA>(), ComponentType.ReadOnly<BenchB>())
            : GetEntityQuery(ComponentType.ReadWrite<BenchA>(), ComponentType.ReadOnly<BenchB>(), ComponentType.ReadOnly<BenchC>());
    }

    protected override void OnUpdate()
    {
        if (method == 2)
        {
            if (components == 2) new ManagedTwo().Run(query);
            else new ManagedThree().Run(query);
        }
        else if (method == 3)
        {
            if (components == 2) new BurstTwo().Run(query);
            else new BurstThree().Run(query);
        }
        else if (components == 2)
        {
            var job = new ManagedChunkTwo
            {
                A = GetComponentTypeHandle<BenchA>(false),
                B = GetComponentTypeHandle<BenchB>(true)
            };
            if (method == 4) job.Run(query);
            else new BurstChunkTwo { Body = job }.Run(query);
        }
        else
        {
            var job = new ManagedChunkThree
            {
                A = GetComponentTypeHandle<BenchA>(false),
                B = GetComponentTypeHandle<BenchB>(true),
                C = GetComponentTypeHandle<BenchC>(true)
            };
            if (method == 4) job.Run(query);
            else new BurstChunkThree { Body = job }.Run(query);
        }
    }
}

public static class TraversalMatrix
{
    public static void Run() => Execute(false);
    public static void Verify() => Execute(true);
    public static void RunDirectChunks() => Execute(false, 6);
    public static void RunBurstQueries() => Execute(false, 1, 2);

    private static void Execute(bool verifyOnly, int firstMethod = 0, int lastMethod = 8)
    {
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            if (!BurstCompiler.IsEnabled) throw new InvalidOperationException("Burst is disabled");
            Debug.Log($"MATRIX_ENV framework=Unity editor={Application.unityVersion} entities_package=1.4.6 burst_package=1.8.28 cpu={SystemInfo.processorType.Replace(' ', '_')} safety_checks={BurstCompiler.Options.EnableBurstSafetyChecks} force_safety_checks={BurstCompiler.Options.ForceEnableBurstSafetyChecks} gc_before_measure=full");
            int rounds = verifyOnly ? 1 : TraversalProtocol.Rounds;
            for (int round = 0; round < rounds; round++)
            foreach (int count in verifyOnly ? new[] { 129 } : TraversalProtocol.EntityCounts)
            foreach (int components in new[] { 2, 3 })
            for (int index = firstMethod; index < lastMethod; index++)
                Measure(round, count, components, round % 2 == 0 ? index : lastMethod - 1 - index + firstMethod, verifyOnly);
            UnityEditor.EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            UnityEditor.EditorApplication.Exit(1);
        }
    }

    private static void Measure(int round, int count, int components, int method, bool verifyOnly)
    {
        using (var world = new World("traversal-matrix"))
        {
            var em = world.EntityManager;
            var archetype = components == 2
                ? em.CreateArchetype(typeof(BenchA), typeof(BenchB))
                : em.CreateArchetype(typeof(BenchA), typeof(BenchB), typeof(BenchC));
            using (var entities = new NativeArray<Entity>(count, Allocator.Temp))
            {
                em.CreateEntity(archetype, entities);
                foreach (var entity in entities)
                {
                    em.SetComponentData(entity, new BenchB { Value = 1 });
                    if (components == 3) em.SetComponentData(entity, new BenchC { Value = 2 });
                }
            }
            var settings = em.CreateEntity(typeof(TraversalSettings));
            em.SetComponentData(settings, new TraversalSettings { Components = components });

            Action run;
            if (method < 2 || method >= 6)
            {
                SystemHandle handle;
                switch (method)
                {
                    case 0: handle = world.CreateSystem<ManagedQueryTraversal>(); break;
                    case 1: handle = world.CreateSystem<BurstQueryTraversal>(); break;
                    case 6: handle = world.CreateSystem<ManagedDirectChunkTraversal>(); break;
                    default: handle = world.CreateSystem<BurstDirectChunkTraversal>(); break;
                }
                run = () => handle.Update(world.Unmanaged);
            }
            else
            {
                var system = world.CreateSystemManaged<TraversalJobSystem>();
                system.Initialize(components, method);
                run = system.Update;
            }

            using (var query = components == 2
                ? em.CreateEntityQuery(typeof(BenchA), typeof(BenchB))
                : em.CreateEntityQuery(typeof(BenchA), typeof(BenchB), typeof(BenchC)))
            {
                void VerifyResult(int calls)
                {
                    using (var values = query.ToComponentDataArray<BenchA>(Allocator.Temp))
                    using (var inputs = query.ToComponentDataArray<BenchB>(Allocator.Temp))
                    {
                        if (values.Length != count || inputs.Length != count) throw new InvalidOperationException("Entity count mismatch");
                        int expected = calls * (components == 2 ? 1 : 3);
                        for (int i = 0; i < count; i++)
                            if (values[i].Value != expected || inputs[i].Value != 1)
                                throw new InvalidOperationException($"Result mismatch at entity {i}: {values[i].Value}, expected {expected}");
                    }
                    if (components == 3)
                        using (var inputs = query.ToComponentDataArray<BenchC>(Allocator.Temp))
                            foreach (var value in inputs)
                                if (value.Value != 2) throw new InvalidOperationException("C input changed");
                }

                string api = method < 2 ? "SystemAPI.Query" : method < 4 ? "IJobEntity.Run" : method < 6 ? "IJobChunk.Run" : "ToArchetypeChunkArray";
                string metadata = $"round={round + 1} framework=Unity layer={(method < 4 ? "Query" : "Chunk")} api={api} backend={(method % 2 == 0 ? "CSharp" : "Burst")} kernel={(method % 2 == 0 ? "Scalar" : "Compiler")} entities={count} components={components} chunks={query.CalculateChunkCount()}";
                run();
                VerifyResult(1);
                if (verifyOnly)
                {
                    Debug.Log($"MATRIX_VERIFIED {metadata}");
                    return;
                }
                double[] times = ComparisonTiming.Measure(run, TraversalProtocol.Warmup, TraversalProtocol.Repetitions);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < TraversalProtocol.AllocationCalls; i++) run();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                VerifyResult(1 + TraversalProtocol.Warmup + ComparisonTiming.Samples * TraversalProtocol.Repetitions + TraversalProtocol.AllocationCalls);
                Debug.Log($"MATRIX_RESULT {metadata} warmup={TraversalProtocol.Warmup} bytes_per_call={allocated / (double)TraversalProtocol.AllocationCalls:F3} verified=true {ComparisonTiming.Describe(times, TraversalProtocol.Repetitions)}");
            }
        }
    }
}
