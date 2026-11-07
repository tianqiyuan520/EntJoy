using System.Diagnostics;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;

namespace RelationshipValidation;

public static class MultiSteady
{
    public static void Run(int size, int degree, int steps, string layout, Action<object> emit)
    {
        if (layout != "fixed")
        {
            Run<MultiLinks>(size, degree, steps, "many-to-many", emit);
            Run<MultiLinks>(1, 64, steps, "high-outgoing", emit);
        }
        if (layout != "list") Run<FixedLinks>(size, degree, steps, "many-to-many", emit);
    }

    private static void Run<T>(int size, int degree, int steps, string shape, Action<object> emit) where T : unmanaged, IRelationComponent
    {
        ChunkMemoryPool.Trim();
        long initialNative = SteadyScenario.NativeBytes("ReservedBytes");
        long firstHeap = 0, lastHeap = 0;
        using (var graph = new MultiGraph<T>(size, degree))
        using (var commands = new DeferredCommandBuffer())
        {
            graph.Verify();
            var em = graph.Manager;
            long added = 0, removed = 0, destroyed = 0, expectedAdded = 0, expectedRemoved = 0, expectedDestroyed = 0;
            em.AddObserver<T>(ObserverEvents.Added, (entities, _) => added += entities.Length);
            em.AddObserver<T>(ObserverEvents.Removed, (entities, _) => removed += entities.Length);
            em.AddObserver<T>(ObserverEvents.Destroyed, (entities, _) => destroyed += entities.Length);
            commands.EnsureParallelWriters(1);
            var writer = commands.CreateParallelWriter(0);
            int copySources = Math.Min(16, size);
            var copyInputs = new Entity[copySources + 1];
            var copyTargets = new Entity[degree];
            long nativeBudget = SteadyScenario.NativeBytes("ReservedBytes") * 4 + 16 * 1024 * 1024;
            int sampleEvery = Math.Max(1000, steps / 100), warm = Math.Min(steps - 1, Math.Max(1000, steps / 10));
            var timer = Stopwatch.StartNew();
            for (int step = 0; step < steps; step++)
            {
                int source = step % size;
                int slot = degree == 1 ? 0 : 1 + step / size % (degree - 1);
                int target = 1 + (step / size + step) % (graph.Targets.Length - 1);
                while (graph.HasTarget(source, target)) target = target % (graph.Targets.Length - 1) + 1;
                writer.RemoveRelationship<T>(0, graph.Sources[source], graph.Targets[graph.Edges[source, slot]]);
                writer.AddRelationship<T>(1, graph.Sources[source], graph.Targets[target]);
                if (step % 64 == 63) writer.AddRelationship<T>(2, graph.Sources[source], graph.Targets[target]);
                commands.PlaybackParallel(em);
                graph.Edges[source, slot] = target;
                expectedAdded++; expectedRemoved++;

                if (step % 32 == 31)
                {
                    new MultiReadJob().Schedule(new QueryBuilder().WithAll<State>().WithRelationship<T>(graph.Targets[0]), graph.World).Complete();
                    for (int i = 0; i < size; i++) if (graph.HasTarget(i, 0)) graph.Seen[i]++;
                }
                if (step % 128 == 127)
                {
                    copyInputs[0] = graph.Targets[0];
                    Array.Copy(graph.Sources, 0, copyInputs, 1, copySources);
                    var copies = em.CloneEntities(copyInputs);
                    expectedAdded += (long)copySources * degree;
                    for (int i = 0; i < copySources; i++)
                    {
                        MultiGraph<T>.Require(em.GetRelationships<T>(copies[i + 1], copyTargets) == degree, "steady clone degree");
                        for (int j = 0; j < degree; j++)
                        {
                            int index = graph.Edges[i, j];
                            MultiGraph<T>.Require(copyTargets.AsSpan().Contains(index == 0 ? copies[0] : graph.Targets[index]), "steady clone remapping");
                        }
                    }
                    MultiPressure.Destroy(em, copies);
                    expectedDestroyed += (long)copySources * degree;
                }
                if (step % 2048 == 2047)
                {
                    int index = 1 + step / 2048 % (graph.Targets.Length - 1);
                    var old = graph.Targets[index];
                    int affected = 0;
                    for (int i = 0; i < size; i++) if (graph.HasTarget(i, index)) affected++;
                    em.DestroyEntity(old);
                    expectedRemoved += affected;
                    graph.Targets[index] = em.NewEntity(typeof(State));
                    MultiGraph<T>.Require(!graph.Targets[index].Equals(old), "target identity generation");
                    for (int i = 0; i < size; i++)
                        if (graph.HasTarget(i, index)) em.AddRelationship<T>(graph.Sources[i], graph.Targets[index]);
                    expectedAdded += affected;
                    MultiGraph<T>.Require(em.GetRelationsOf<T>(old).Length == 0, "stale target reverse lookup");
                }
                if (step % 4096 == 4095) em.Restore(em.TakeSnapshot());
                if (step % sampleEvery == 0 || step == steps - 1)
                {
                    graph.Verify();
                    MultiGraph<T>.Require(added == expectedAdded && removed == expectedRemoved && destroyed == expectedDestroyed, "steady event counts");
                    MultiGraph<T>.Require(em.EntityCount <= size + graph.Targets.Length + copyInputs.Length, "identity high-water");
                    MultiGraph<T>.Require(SteadyScenario.NativeBytes("ReservedBytes") <= nativeBudget, "native reservation bound");
                    lastHeap = GC.GetTotalMemory(forceFullCollection: true);
                    if (step >= warm && firstHeap == 0) firstHeap = lastHeap;
                    var report = em.GetMemoryReport();
                    emit(new { kind = "multi-steady", layout = typeof(T).Name, shape, step, size, degree, edges = (long)size * degree,
                        entities = report.TotalEntityCount, chunks = report.TotalChunkCount, identity_high_water = em.EntityCount,
                        added, removed, destroyed, managed_live_bytes = lastHeap,
                        chunk_reserved_bytes = SteadyScenario.NativeBytes("ReservedBytes"), chunk_pooled_bytes = SteadyScenario.NativeBytes("PooledBytes"),
                        active_slab_bytes = report.TotalSlabBytes, private_bytes = Process.GetCurrentProcess().PrivateMemorySize64,
                        elapsed_ms = timer.Elapsed.TotalMilliseconds });
                }
            }
            if (lastHeap - firstHeap > 8 * 1024 * 1024) throw new Exception("M:N postwarm retained heap exceeded the 8 MiB growth guard.");
        }
        ChunkMemoryPool.Trim();
        if (SteadyScenario.NativeBytes("ReservedBytes") != initialNative) throw new Exception("M:N chunk reservations survived disposal and Trim.");
        emit(new { kind = "multi-steady-complete", layout = typeof(T).Name, shape, size, degree, cycles = steps,
            postwarm_managed_growth_bytes = lastHeap - firstHeap, chunk_reserved_bytes = SteadyScenario.NativeBytes("ReservedBytes"), success = true });
    }
}
