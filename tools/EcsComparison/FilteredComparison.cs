using System;
using EntJoy.ECS;
using EntJoy.JobSystem;
using EntWorld = EntJoy.ECS.World;

public static class FilteredComparison
{
    private const int EntityCount = 100_000;

    public static void Run()
    {
        foreach (int components in new[] { 2, 3 })
        foreach (int blockSize in new[] { 1, 64 })
            Measure(components, blockSize);
    }

    private static void Measure(int components, int blockSize)
    {
        string entry = components == 2
            ? "Get_SharpNative_Job__global_namespace__EntUpdateTwoCpp_Execute_AdapterPtr"
            : "Get_SharpNative_Job__global_namespace__EntUpdateThreeCpp_Execute_AdapterPtr";
        if (NativeJobScheduler.GetNativeExportPtr(entry) == IntPtr.Zero)
            throw new InvalidOperationException("Native job export missing: " + entry);

        using var world = new EntWorld("filtered-comparison");
        var entities = new Entity[EntityCount];
        Type[] types = components == 2
            ? new[] { typeof(A), typeof(B), typeof(Enabled) }
            : new[] { typeof(A), typeof(B), typeof(C), typeof(Enabled) };
        int enabled = 0;
        for (int i = 0; i < entities.Length; i++)
        {
            var entity = world.EntityManager.NewEntity(types);
            entities[i] = entity;
            world.EntityManager.Set(entity, new B { Value = 1 });
            if (components == 3) world.EntityManager.Set(entity, new C { Value = 2 });
            bool active = (i / blockSize) % 2 == 0;
            world.EntityManager.SetComponentEnabled<Enabled>(entity, active);
            if (active) enabled++;
        }

        var query = components == 2
            ? new QueryBuilder().WithAll<A, B>().WithEnabled<Enabled>()
            : new QueryBuilder().WithAll<A, B, C>().WithEnabled<Enabled>();
        void RunJob()
        {
            if (components == 2) new EntUpdateTwoCpp().Run(query, world);
            else new EntUpdateThreeCpp().Run(query, world);
        }
        void RunManagedJob()
        {
            if (components == 2) new EntUpdateTwo().Run(query, world);
            else new EntUpdateThree().Run(query, world);
        }
        double[] managedTimes = ComparisonTiming.Measure(RunManagedJob);
        int expected = (ComparisonTiming.Warmup + ComparisonTiming.Samples * ComparisonTiming.Repetitions) * (components == 2 ? 1 : 3);
        for (int i = 0; i < entities.Length; i++)
        {
            int value = world.EntityManager.GetComponentReadOnly<A>(entities[i]).Value;
            if (value != ((i / blockSize) % 2 == 0 ? expected : 0))
                throw new InvalidOperationException($"Filtered result mismatch at entity {i}: {value}");
        }
        double[] times = ComparisonTiming.Measure(RunJob);
        int chunks = 0;
        if (components == 2) { foreach (var chunk in world.QueryChunks<A, B>()) chunks++; }
        else { foreach (var chunk in world.QueryChunks<A, B, C>()) chunks++; }
        Console.WriteLine($"ENTJOY_FILTERED components={components} block={blockSize} entities={EntityCount} enabled={enabled} chunks={chunks} {ComparisonTiming.Describe(times)}");
        Console.WriteLine($"ENTJOY_FILTERED_MANAGED components={components} block={blockSize} entities={EntityCount} enabled={enabled} chunks={chunks} {ComparisonTiming.Describe(managedTimes)}");
    }
}
