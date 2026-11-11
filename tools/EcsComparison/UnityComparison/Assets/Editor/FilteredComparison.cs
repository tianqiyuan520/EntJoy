using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

public struct BenchEnabled : IComponentData, IEnableableComponent { }

[DisableAutoCreation]
public partial class FilteredComparisonSystem : SystemBase
{
    private EntityQuery query;
    private int components;

    public void Initialize(int componentCount)
    {
        components = componentCount;
        query = components == 2
            ? GetEntityQuery(ComponentType.ReadWrite<BenchA>(), ComponentType.ReadOnly<BenchB>(), ComponentType.ReadOnly<BenchEnabled>())
            : GetEntityQuery(ComponentType.ReadWrite<BenchA>(), ComponentType.ReadOnly<BenchB>(), ComponentType.ReadOnly<BenchC>(), ComponentType.ReadOnly<BenchEnabled>());
    }

    protected override void OnUpdate()
    {
        if (components == 2) new BurstTwo().Run(query);
        else new BurstThree().Run(query);
    }
}

public static class FilteredComparison
{
    private const int EntityCount = 100_000;

    public static void RunNoBurstSafety()
    {
        bool safetyChecks = BurstCompiler.Options.EnableBurstSafetyChecks;
        bool forceSafetyChecks = BurstCompiler.Options.ForceEnableBurstSafetyChecks;
        try
        {
            BurstCompiler.Options.EnableBurstSafetyChecks = false;
            BurstCompiler.Options.ForceEnableBurstSafetyChecks = false;
            Run();
        }
        finally
        {
            BurstCompiler.Options.EnableBurstSafetyChecks = safetyChecks;
            BurstCompiler.Options.ForceEnableBurstSafetyChecks = forceSafetyChecks;
        }
    }

    public static void Run()
    {
        try
        {
            if (!BurstCompiler.IsEnabled) throw new InvalidOperationException("Burst is disabled");
            UnityEngine.Debug.Log($"UNITY_FILTERED_OPTIONS safety_checks={BurstCompiler.Options.EnableBurstSafetyChecks} force_safety_checks={BurstCompiler.Options.ForceEnableBurstSafetyChecks}");
            foreach (int components in new[] { 2, 3 })
            foreach (int blockSize in new[] { 1, 64 })
                Measure(components, blockSize);
            UnityEditor.EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogException(e);
            UnityEditor.EditorApplication.Exit(1);
        }
    }

    private static void Measure(int components, int blockSize)
    {
        using (var world = new World("filtered-comparison"))
        {
            var em = world.EntityManager;
            var archetype = components == 2
                ? em.CreateArchetype(typeof(BenchA), typeof(BenchB), typeof(BenchEnabled))
                : em.CreateArchetype(typeof(BenchA), typeof(BenchB), typeof(BenchC), typeof(BenchEnabled));
            using (var entities = new NativeArray<Entity>(EntityCount, Allocator.Temp))
            {
                em.CreateEntity(archetype, entities);
                int enabled = 0;
                for (int i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    em.SetComponentData(entity, new BenchB { Value = 1 });
                    if (components == 3) em.SetComponentData(entity, new BenchC { Value = 2 });
                    bool active = (i / blockSize) % 2 == 0;
                    em.SetComponentEnabled<BenchEnabled>(entity, active);
                    if (active) enabled++;
                }
                var system = world.CreateSystemManaged<FilteredComparisonSystem>();
                system.Initialize(components);
                double[] times = ComparisonTiming.Measure(system.Update);
                int expected = (ComparisonTiming.Warmup + ComparisonTiming.Samples * ComparisonTiming.Repetitions) * (components == 2 ? 1 : 3);
                for (int i = 0; i < entities.Length; i++)
                {
                    int value = em.GetComponentData<BenchA>(entities[i]).Value;
                    if (value != ((i / blockSize) % 2 == 0 ? expected : 0))
                        throw new InvalidOperationException($"Filtered result mismatch at entity {i}: {value}");
                }
                using (var query = em.CreateEntityQuery(typeof(BenchA)))
                    UnityEngine.Debug.Log($"UNITY_FILTERED components={components} block={blockSize} entities={EntityCount} enabled={enabled} chunks={query.CalculateChunkCount()} burst_enabled={BurstCompiler.IsEnabled} {ComparisonTiming.Describe(times)}");
            }
        }
    }
}
