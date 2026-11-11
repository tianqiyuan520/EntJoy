using System;
using System.Threading;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using NativeTranspiler;

public struct FilterRelation : IRelationComponent
{
    public RelationSlot Target;
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct EntUpdateChunkCpp : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var a = chunk.GetComponentDataSpan<A>();
        var b = chunk.GetComponentDataSpan<B>();
        for (int i = 0; i < chunk.Count; i++)
        {
            if (mask.Length != 0 && !mask.IsEnabled(i)) continue;
            a[i].Value += b[i].Value;
        }
    }
}

public static class NativeFilterVerification
{
    public static void Run()
    {
        JobScheduler.Initialize(2);
        int previousCapacity = Archetype.ChunkCapacityOverride;
        try
        {
            if (!JobScheduler.IsNative) throw new InvalidOperationException("Native scheduler required");
            foreach (string job in new[] { nameof(EntUpdateTwoCpp), nameof(EntUpdateChunkCpp) })
            {
                string entry = $"Get_SharpNative_Job__global_namespace__{job}_Execute_AdapterPtr";
                if (NativeJobScheduler.GetNativeExportPtr(entry) == IntPtr.Zero)
                    throw new InvalidOperationException("Native job export missing: " + entry);
            }

            Archetype.ChunkCapacityOverride = 300;
            foreach (bool chunkJob in new[] { false, true })
            {
                foreach (bool scheduled in new[] { false, true })
                    foreach (int count in new[] { 1, 63, 64, 65, 255, 256, 257, 299, 601 })
                        VerifyIntersection(count, chunkJob, scheduled);
                VerifyDependency(chunkJob);
            }
            VerifyChunkCacheLifecycle();
            Console.WriteLine("NATIVE_FILTERS passed: entity/chunk, Run/Schedule, relationship + two Enabled filters, tails, rebinding, target recycling, dependency");
        }
        finally
        {
            Archetype.ChunkCapacityOverride = previousCapacity;
            JobScheduler.Shutdown();
        }
    }

    private static void VerifyChunkCacheLifecycle()
    {
        using var world = new World("native-chunk-cache-lifecycle");
        var manager = world.EntityManager;
        var query = new QueryBuilder().WithAll<A, B>();
        new EntUpdateChunkCpp().Run(query, world);
        var first = manager.NewEntity(typeof(A), typeof(B));
        manager.Set(first, new B { Value = 1 });
        new EntUpdateChunkCpp().Run(query, world);
        var second = manager.NewEntity(typeof(A), typeof(B));
        manager.Set(second, new B { Value = 2 });
        new EntUpdateChunkCpp().Run(query, world);
        if (manager.GetComponentReadOnly<A>(first).Value != 2 || manager.GetComponentReadOnly<A>(second).Value != 2)
            throw new InvalidOperationException("Native chunk cache did not include new entities");
        manager.DestroyEntity(first);
        new EntUpdateChunkCpp().Run(query, world);
        ChunkJobScheduler.ClearRawChunkScheduleCaches(manager);
        new EntUpdateChunkCpp().Run(query, world);
        if (manager.GetComponentReadOnly<A>(second).Value != 6)
            throw new InvalidOperationException("Native chunk cache failed after removal or explicit retirement");
        manager.DestroyEntity(second);
        new EntUpdateChunkCpp().Run(query, world);
        Console.WriteLine("NATIVE_CHUNK_CACHE passed: empty, creation, removal, retirement, empty again");
    }

    private static void Execute(QueryBuilder query, World world, bool chunkJob, bool scheduled)
    {
        if (chunkJob)
        {
            if (scheduled) new EntUpdateChunkCpp().Schedule(query, world).Complete();
            else new EntUpdateChunkCpp().Run(query, world);
        }
        else
        {
            if (scheduled) new EntUpdateTwoCpp().Schedule(query, world).Complete();
            else new EntUpdateTwoCpp().Run(query, world);
        }
    }

    private static void VerifyIntersection(int count, bool chunkJob, bool scheduled)
    {
        using var world = new World("native-filter-intersection");
        var manager = world.EntityManager;
        var target = manager.NewEntity(typeof(C));
        var other = manager.NewEntity(typeof(C));
        var entities = new Entity[count];
        var expected = new int[count];
        for (int i = 0; i < count; i++)
        {
            entities[i] = manager.NewEntity(typeof(A), typeof(B), typeof(Enabled), typeof(Enabled2), typeof(FilterRelation));
            manager.Set(entities[i], new B { Value = 1 });
            manager.AddRelationship<FilterRelation>(entities[i], i % 3 == 0 ? target : other);
            manager.SetComponentEnabled<Enabled>(entities[i], i % 5 != 0);
            manager.SetComponentEnabled<Enabled2>(entities[i], i % 7 != 0);
        }
        var query = new QueryBuilder().WithAll<A, B>().WithRelationship<FilterRelation>(target)
            .WithEnabled<Enabled>().WithEnabled<Enabled2>();
        RunAndVerify(i => i % 3 == 0 && i % 5 != 0 && i % 7 != 0);

        foreach (var entity in entities) manager.SetComponentEnabled<Enabled2>(entity, false);
        RunAndVerify(_ => false);

        int structuralVersion = manager.StructuralVersion;
        foreach (var entity in entities)
        {
            manager.SetComponentEnabled<Enabled>(entity, true);
            manager.SetComponentEnabled<Enabled2>(entity, true);
            manager.AddRelationship<FilterRelation>(entity, target);
        }
        if (manager.StructuralVersion != structuralVersion)
            throw new InvalidOperationException("Rebinding verification requires unchanged structure");
        RunAndVerify(_ => true);

        manager.DestroyEntity(target);
        var recycled = manager.NewEntity(typeof(C));
        if (recycled.Id != target.Id) throw new InvalidOperationException("Target recycling verification requires reused id");
        foreach (var entity in entities) manager.AddRelationship<FilterRelation>(entity, recycled);
        RunAndVerify(_ => false);

        void RunAndVerify(Func<int, bool> matches)
        {
            Execute(query, world, chunkJob, scheduled);
            for (int i = 0; i < count; i++)
            {
                if (matches(i)) expected[i]++;
                int actual = manager.GetComponentReadOnly<A>(entities[i]).Value;
                if (actual != expected[i])
                    throw new InvalidOperationException($"Native filter mismatch: count={count}, chunk={chunkJob}, scheduled={scheduled}, index={i}, expected={expected[i]}, actual={actual}");
            }
        }
    }

    private static void VerifyDependency(bool chunkJob)
    {
        using var world = new World("native-filter-dependency");
        using var release = new ManualResetEventSlim(false);
        var manager = world.EntityManager;
        var target = manager.NewEntity(typeof(C));
        var other = manager.NewEntity(typeof(C));
        var entities = new Entity[299];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = manager.NewEntity(typeof(A), typeof(B), typeof(Enabled), typeof(FilterRelation));
            manager.Set(entities[i], new B { Value = 1 });
            manager.AddRelationship<FilterRelation>(entities[i], i % 3 == 0 ? target : other);
            manager.SetComponentEnabled<Enabled>(entities[i], false);
        }
        var all = new QueryBuilder().WithAll<A, B>().WithAll<Enabled>();
        var filtered = all;
        filtered = filtered.WithEnabled<Enabled>().WithRelationship<FilterRelation>(target);
        Execute(filtered, world, chunkJob, false);
        var parent = new EnableAfterGate { Release = release }.Schedule(all, world);
        try
        {
            var child = chunkJob
                ? new EntUpdateChunkCpp().Schedule(filtered, world, dependsOn: parent)
                : new EntUpdateTwoCpp().Schedule(filtered, world, dependsOn: parent);
            release.Set();
            child.Complete();
            for (int i = 0; i < entities.Length; i++)
                if (manager.GetComponentReadOnly<A>(entities[i]).Value != (i % 6 == 0 ? 1 : 0))
                    throw new InvalidOperationException($"Native filter dependency mismatch: chunk={chunkJob}, index={i}");
        }
        finally { release.Set(); parent.Complete(); }
    }
}
