using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

public struct BenchA : IComponentData { public int Value; }
public struct BenchB : IComponentData { public int Value; }
public struct BenchC : IComponentData { public int Value; }

public partial struct ManagedTwo : IJobEntity
{
    public void Execute(ref BenchA a, in BenchB b) => a.Value += b.Value;
}
public partial struct ManagedThree : IJobEntity
{
    public void Execute(ref BenchA a, in BenchB b, in BenchC c) => a.Value += b.Value + c.Value;
}
[BurstCompile(CompileSynchronously = true)]
public partial struct BurstTwo : IJobEntity
{
    public void Execute(ref BenchA a, in BenchB b) => a.Value += b.Value;
}
[BurstCompile(CompileSynchronously = true)]
public partial struct BurstThree : IJobEntity
{
    public void Execute(ref BenchA a, in BenchB b, in BenchC c) => a.Value += b.Value + c.Value;
}

[DisableAutoCreation]
public partial class ComparisonSystem : SystemBase
{
    private EntityQuery query;
    public int Components;
    public bool Burst;

    public void Initialize(int components, bool burst)
    {
        Components = components;
        Burst = burst;
        query = components == 2
            ? GetEntityQuery(ComponentType.ReadWrite<BenchA>(), ComponentType.ReadOnly<BenchB>())
            : GetEntityQuery(ComponentType.ReadWrite<BenchA>(), ComponentType.ReadOnly<BenchB>(), ComponentType.ReadOnly<BenchC>());
    }
    protected override void OnUpdate()
    {
        if (Components == 2)
        {
            if (Burst) new BurstTwo().Run(query);
            else new ManagedTwo().Run(query);
        }
        else
        {
            if (Burst) new BurstThree().Run(query);
            else new ManagedThree().Run(query);
        }
    }
}

public static class Comparison
{
    public static void RunMatchedNoBurstSafety()
    {
        bool safetyChecks = BurstCompiler.Options.EnableBurstSafetyChecks;
        bool forceSafetyChecks = BurstCompiler.Options.ForceEnableBurstSafetyChecks;
        try
        {
            BurstCompiler.Options.EnableBurstSafetyChecks = false;
            BurstCompiler.Options.ForceEnableBurstSafetyChecks = false;
            RunMatched();
        }
        finally
        {
            BurstCompiler.Options.EnableBurstSafetyChecks = safetyChecks;
            BurstCompiler.Options.ForceEnableBurstSafetyChecks = forceSafetyChecks;
        }
    }

    public static void RunMatched()
    {
        try
        {
            if (!BurstCompiler.IsEnabled) throw new InvalidOperationException("Burst is disabled");
            foreach (int components in new[] { 2, 3 }) Measure(components, true, true);
            UnityEngine.Debug.Log($"UNITY_BURST_OPTIONS safety_checks={BurstCompiler.Options.EnableBurstSafetyChecks} force_safety_checks={BurstCompiler.Options.ForceEnableBurstSafetyChecks}");
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                if (module.FileName.IndexOf("burst", StringComparison.OrdinalIgnoreCase) >= 0)
                    UnityEngine.Debug.Log($"UNITY_BURST_MODULE {module.FileName}");
            UnityEditor.EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogException(e);
            UnityEditor.EditorApplication.Exit(1);
        }
    }

    public static void Run()
    {
        try
        {
            foreach (int components in new[] { 2, 3 })
                foreach (bool burst in new[] { false, true }) Measure(components, burst);
            UnityEditor.EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogException(e);
            UnityEditor.EditorApplication.Exit(1);
        }
    }

    private static void Measure(int components, bool burst, bool matched = false)
    {
        const int count = 100000, warmup = 50, samples = 12;
        int repetitions = burst ? 8192 : 200;
        using (var world = new World("comparison"))
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
            var system = world.CreateSystemManaged<ComparisonSystem>();
            system.Initialize(components, burst);
            double[] times;
            if (matched)
                times = ComparisonTiming.Measure(system.Update);
            else
            {
                for (int i = 0; i < warmup; i++) system.Update();
                times = new double[samples];
                for (int sample = 0; sample < samples; sample++)
                {
                    var timer = Stopwatch.StartNew();
                    for (int i = 0; i < repetitions; i++) system.Update();
                    timer.Stop();
                    times[sample] = timer.Elapsed.TotalMilliseconds * 1000 / repetitions;
                }
            }
            using (var query = em.CreateEntityQuery(typeof(BenchA)))
            using (var values = query.ToComponentDataArray<BenchA>(Allocator.Temp))
            {
                if (values.Length != count) throw new InvalidOperationException("Entity count mismatch");
                int expected = (warmup + samples * repetitions) * (components == 2 ? 1 : 3);
                foreach (var value in values)
                    if (value.Value != expected) throw new InvalidOperationException("Result mismatch");
            }
            if (matched)
            {
                using (var query = components == 2
                    ? em.CreateEntityQuery(typeof(BenchA), typeof(BenchB))
                    : em.CreateEntityQuery(typeof(BenchA), typeof(BenchB), typeof(BenchC)))
                    UnityEngine.Debug.Log($"UNITY_MATCHED components={components} chunks={query.CalculateChunkCount()} burst_enabled={BurstCompiler.IsEnabled} {ComparisonTiming.Describe(times)}");
                return;
            }
            Array.Sort(times);
            UnityEngine.Debug.Log($"UNITY_COMPARISON components={components} burst={burst} median_us={(times[5] + times[6]) / 2:F3} min_us={times[0]:F3} max_us={times[11]:F3} samples={samples} repetitions={repetitions}");
        }
    }
}
