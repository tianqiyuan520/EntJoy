using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

[BurstCompile(CompileSynchronously = true)]
public partial struct BurstDependency : IJobEntity
{
    public void Execute(ref BenchA a, in BenchB b)
    {
        int value = a.Value;
        value = ((value * 33) ^ b.Value) & 65535;
        value = ((value * 33) ^ b.Value) & 65535;
        value = ((value * 33) ^ b.Value) & 65535;
        value = ((value * 33) ^ b.Value) & 65535;
        a.Value = value;
    }
}

[DisableAutoCreation]
public partial class DependencySystem : SystemBase
{
    private EntityQuery query;
    protected override void OnCreate()
    {
        query = GetEntityQuery(ComponentType.ReadWrite<BenchA>(), ComponentType.ReadOnly<BenchB>());
    }
    protected override void OnUpdate() => new BurstDependency().Run(query);
}

public static class UnityDependencyComparison
{
    public static void Run()
    {
        bool safetyChecks = BurstCompiler.Options.EnableBurstSafetyChecks;
        bool forceSafetyChecks = BurstCompiler.Options.ForceEnableBurstSafetyChecks;
        int exitCode = 0;
        try
        {
            BurstCompiler.Options.EnableBurstSafetyChecks = false;
            BurstCompiler.Options.ForceEnableBurstSafetyChecks = false;
            if (!BurstCompiler.IsEnabled) throw new InvalidOperationException("Burst is disabled");
            Measure();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            exitCode = 1;
        }
        finally
        {
            BurstCompiler.Options.EnableBurstSafetyChecks = safetyChecks;
            BurstCompiler.Options.ForceEnableBurstSafetyChecks = forceSafetyChecks;
        }
        UnityEditor.EditorApplication.Exit(exitCode);
    }

    private static void Measure()
    {
        const int count = 100_000;
        using (var world = new World("dependency-comparison"))
        {
            var em = world.EntityManager;
            var archetype = em.CreateArchetype(typeof(BenchA), typeof(BenchB));
            using (var entities = new NativeArray<Entity>(count, Allocator.Temp))
            {
                em.CreateEntity(archetype, entities);
                foreach (var entity in entities) em.SetComponentData(entity, new BenchB { Value = 1 });
            }
            var system = world.CreateSystemManaged<DependencySystem>();
            var times = ComparisonTiming.Measure(system.Update);
            int expected = ExpectedValue();
            using (var query = em.CreateEntityQuery(typeof(BenchA), typeof(BenchB)))
            using (var values = query.ToComponentDataArray<BenchA>(Allocator.Temp))
            using (var inputs = query.ToComponentDataArray<BenchB>(Allocator.Temp))
            {
                if (values.Length != count || inputs.Length != count) throw new InvalidOperationException("Dependency entity count mismatch");
                for (int i = 0; i < values.Length; i++)
                    if (values[i].Value != expected || inputs[i].Value != 1)
                        throw new InvalidOperationException("Dependency result mismatch");
                Debug.Log($"UNITY_DEPENDENCY chunks={query.CalculateChunkCount()} expected={expected} burst_enabled={BurstCompiler.IsEnabled} safety_checks={BurstCompiler.Options.EnableBurstSafetyChecks} {ComparisonTiming.Describe(times)}");
            }
        }
    }

    private static int ExpectedValue()
    {
        int value = 0;
        int steps = 4 * (ComparisonTiming.Warmup + ComparisonTiming.Samples * ComparisonTiming.Repetitions);
        for (int i = 0; i < steps; i++) value = ((value * 33) ^ 1) & 65535;
        return value;
    }
}
