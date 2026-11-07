using System.Diagnostics;
using EntJoy.ECS;

namespace RelationshipValidation;

public static class RelationCosts
{
    private static long checksum;
    private static void Measure(string name, int size, int hubs, long operations, Action action, Action verify, Action<object> emit)
    {
        action(); verify();
        for (int sample = 0; sample < 3; sample++)
        {
            long allocation = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            action();
            long ticks = Stopwatch.GetTimestamp() - start;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
            verify();
            emit(new { kind = "cost", name, size, hubs, sample, operations, elapsed_ticks = ticks,
                milliseconds = ticks * 1000.0 / Stopwatch.Frequency,
                ns_per_operation = ticks * 1e9 / Stopwatch.Frequency / Math.Max(1, operations), allocated_bytes = bytes,
                private_bytes = Process.GetCurrentProcess().PrivateMemorySize64,
                peak_working_set = Process.GetCurrentProcess().PeakWorkingSet64,
                chunk_reserved_bytes = SteadyScenario.NativeBytes("ReservedBytes"), checksum });
        }
    }
    public static unsafe void Run(int size, Action<object> emit)
    {
        foreach (int hubs in new[] { 1, 1000 })
        {
            using (var world = new World("RelationCosts"))
            {
                var em = world.EntityManager;
                var targets = em.CreateEntities(hubs + 1, typeof(State));
                var sources = em.CreateEntities(size, typeof(State), typeof(Link));
                var expected = new int[size];
                for (int i = 0; i < size; i++) { expected[i] = i % hubs; em.AddComponent(sources[i], new Link { Target = RelationSlot.From(targets[expected[i]]), Data = i }); }
                var scratch = new Entity[size + 1];
                void Read()
                {
                    long value = 0;
                    foreach (var source in sources) value += em.GetComponentReadOnly<Link>(source).Data;
                    checksum = value;
                }
                void VerifyAll()
                {
                    for (int i = 0; i < size; i++)
                    {
                        var value = em.GetComponentReadOnly<Link>(sources[i]);
                        if (!value.Target.Matches(targets[expected[i]]) || value.Data != i) throw new Exception("Cost graph mismatch.");
                    }
                    if (em.VerifyLocateTable(out _) != 0) throw new Exception("Cost locate mismatch.");
                }
                Read();
                Measure("direct-read", size, hubs, size, Read, () =>
                { if (checksum != (long)size * (size - 1) / 2) throw new Exception("Read checksum mismatch."); }, emit);
                Measure("get-target", size, hubs, size, () =>
                {
                    long value = 0; foreach (var source in sources) value += em.GetRelationship<Link>(source).Id; checksum = value;
                }, VerifyAll, emit);
                Measure("reverse-span", size, hubs, size, () =>
                {
                    int count = 0; for (int i = 0; i < hubs; i++) count += em.GetRelationsOf<Link>(targets[i], scratch); checksum = count;
                }, () => { if (checksum != size) throw new Exception("Reverse count mismatch."); }, emit);
                var query = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<State>().WithRelationship<Link>(targets[0]));
                query.CalculateEntityCount();
                Measure("precise-count", size, hubs, size, () => checksum = query.CalculateEntityCount(), () =>
                { if (checksum != (size + hubs - 1) / hubs) throw new Exception("Query count mismatch."); }, emit);
                foreach (int percent in new[] { 0, 1, 10 })
                foreach (bool observer in new[] { false, true })
                {
                    int events = 0, actualChanges = 0;
                    ObserverHandle subscription = default;
                    if (observer) subscription = em.AddObserver<Link>(ObserverEvents.Set, (entities, _) => events += entities.Length);
                    try
                    {
                        Measure($"rebind-{percent}pct-observer-{observer}", size, hubs, size, () =>
                        {
                            events = actualChanges = 0;
                            int changed = size * percent / 100;
                            for (int i = 0; i < size; i++)
                            {
                                if (i < changed) { expected[i] = (expected[i] + 1) % targets.Length; actualChanges++; }
                                em.AddRelationship<Link>(sources[i], targets[expected[i]]);
                            }
                        }, () => { VerifyAll(); if (observer && events != actualChanges) throw new Exception("Event cost mismatch."); }, emit);
                    }
                    finally { if (observer) em.RemoveObserver<Link>(subscription); }
                }
                Entity[] clones = null;
                Measure("clone-group", size, hubs, size, () => clones = em.CloneEntities(sources), () =>
                {
                    for (int i = 0; i < clones.Length; i++)
                        if (em.GetComponentReadOnly<Link>(clones[i]).Data != i || !em.GetRelationship<Link>(clones[i]).Equals(targets[expected[i]])) throw new Exception("Clone cost mismatch.");
                    fixed (Entity* ptr = clones) em.DestroyEntities(ptr, clones.Length);
                    VerifyAll();
                }, emit);
                var snapshot = em.TakeSnapshot();
                Measure("snapshot", size, hubs, size, () => snapshot = em.TakeSnapshot(), VerifyAll, emit);
                Measure("restore", size, hubs, size, () => em.Restore(snapshot), VerifyAll, emit);
                for (int sample = 0; sample < 3; sample++)
                {
                    em.Restore(snapshot);
                    int incoming = em.GetRelationsOf<Link>(targets[0], scratch);
                    long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                    em.DestroyEntity(targets[0]);
                    long ticks = Stopwatch.GetTimestamp() - start, allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (em.GetRelationsOf<Link>(targets[0], scratch) != 0 || em.VerifyLocateTable(out _) != 0) throw new Exception("Delete cost mismatch.");
                    for (int i = 0; i < size; i++)
                        if (em.HasRelationship<Link>(sources[i]) != (expected[i] != 0)) throw new Exception("Delete survivor mismatch.");
                    emit(new { kind = "cost", name = "delete-target", size, hubs, sample, operations = incoming,
                        elapsed_ticks = ticks, milliseconds = ticks * 1000.0 / Stopwatch.Frequency, allocated_bytes = allocated,
                        private_bytes = Process.GetCurrentProcess().PrivateMemorySize64, peak_working_set = Process.GetCurrentProcess().PeakWorkingSet64 });
                }
            }
            ChunkMemoryPool.Trim();
            if (SteadyScenario.NativeBytes("ReservedBytes") != 0) throw new Exception("Native chunk memory remained after disposal.");
        }
        using (var world = new World("RelationChainCost"))
        {
            var em = world.EntityManager;
            var chain = em.CreateEntities(size, typeof(State), typeof(Parent));
            for (int i = 0; i < size - 1; i++) em.AddRelationship<Parent>(chain[i], chain[i + 1]);
            var snapshot = em.TakeSnapshot();
            for (int sample = 0; sample < 3; sample++)
            {
                em.Restore(snapshot);
                long start = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
                em.DestroyEntityCascade(chain[^1]);
                long ticks = Stopwatch.GetTimestamp() - start; allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                if (em.GetAllArchetypes().Sum(arch => arch.EntityCount) != 0) throw new Exception("Chain cascade did not destroy the graph.");
                emit(new { kind = "cost", name = "cascade-chain", size, hubs = 0, sample, operations = size, elapsed_ticks = ticks,
                    milliseconds = ticks * 1000.0 / Stopwatch.Frequency, allocated_bytes = allocated,
                    private_bytes = Process.GetCurrentProcess().PrivateMemorySize64, peak_working_set = Process.GetCurrentProcess().PeakWorkingSet64 });
            }
        }
        ChunkMemoryPool.Trim();
    }
}
