using System.Diagnostics;
using System.Reflection;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;

namespace RelationshipValidation;

public static class SteadyScenario
{
    private static readonly MethodInfo VerifySlab = typeof(Archetype).GetMethod("VerifySlabInvariants", BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static bool InvalidSlabs(EntityManager manager) => manager.GetAllArchetypes().Any(arch => (int)VerifySlab.Invoke(arch, null)! != 0);
    internal static long NativeBytes(string name) => (long)typeof(ChunkMemoryPool).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    public static void Run(int steps, Action<object> emit)
    {
        ChunkMemoryPool.Trim();
        long initialNative = NativeBytes("ReservedBytes");
        var heapSamples = new List<long>();
        using (var world = new World("RelationshipSteady"))
        using (var buffer = new DeferredCommandBuffer())
        {
            var em = world.EntityManager;
            var targets = em.CreateEntities(8, typeof(State));
            var sources = em.CreateEntities(256, typeof(State), typeof(Link));
            var expectedTargets = new int[sources.Length];
            var seen = new int[sources.Length];
            for (int i = 0; i < sources.Length; i++) { expectedTargets[i] = i % 8; em.AddRelationship<Link>(sources[i], targets[i % 8]); }
            var prefab = em.NewEntity(typeof(State), typeof(Prefab), typeof(Link));
            em.AddRelationship<Link>(prefab, targets[0]);
            buffer.EnsureParallelWriters(1);
            var writer = buffer.CreateParallelWriter(0);
            var timer = Stopwatch.StartNew();
            for (int step = 0; step < steps; step++)
            {
                int source = step % sources.Length, target = (step / sources.Length + 1 + step % targets.Length) % targets.Length;
                expectedTargets[source] = target;
                writer.SetRelationshipTarget<Link>(step, sources[source], targets[target]);
                buffer.PlaybackParallel(em);
                var copies = em.SpawnFrom(prefab, 2);
                foreach (var copy in copies) em.DestroyEntity(copy);
                new ReadLinkJob().Schedule(new QueryBuilder().WithAll<State>().WithRelationship<Link>(targets[target]), world).Complete();
                for (int i = 0; i < sources.Length; i++) if (expectedTargets[i] == target) seen[i]++;
                if (step % 250 == 0) em.Restore(em.TakeSnapshot());
                if (step % 1000 == 0 || step == steps - 1)
                {
                    for (int i = 0; i < sources.Length; i++)
                    {
                        if (!em.GetRelationship<Link>(sources[i]).Equals(targets[expectedTargets[i]])
                            || em.GetComponentReadOnly<State>(sources[i]).Seen != seen[i]) throw new Exception("Steady state mismatch.");
                    }
                    if (em.EntityCount > 267 || em.VerifyLocateTable(out _) != 0) throw new Exception("Identity or locate storage grew unexpectedly.");
                    if (InvalidSlabs(em)) throw new Exception("Slab accounting mismatch.");
                    long heap = GC.GetTotalMemory(forceFullCollection: true);
                    if (step >= 1000) heapSamples.Add(heap);
                    var report = em.GetMemoryReport();
                    emit(new { kind = "steady", step, entities = report.TotalEntityCount, chunks = report.TotalChunkCount,
                        active_slab_bytes = report.TotalSlabBytes, chunk_reserved_bytes = NativeBytes("ReservedBytes"),
                        chunk_pooled_bytes = NativeBytes("PooledBytes"), managed_live_bytes = heap,
                        private_bytes = Process.GetCurrentProcess().PrivateMemorySize64,
                        elapsed_ms = timer.Elapsed.TotalMilliseconds });
                }
            }
        }
        ChunkMemoryPool.Trim();
        if (NativeBytes("ReservedBytes") != initialNative) throw new Exception("Chunk native reservations survived world disposal and Trim.");
        if (heapSamples.Count > 1 && heapSamples[^1] - heapSamples[0] > 8 * 1024 * 1024) throw new Exception("Managed retained memory did not stabilize within the 8 MiB guard.");
        emit(new { kind = "steady-final", chunk_reserved_bytes = NativeBytes("ReservedBytes"),
            managed_live_bytes = GC.GetTotalMemory(true), private_bytes = Process.GetCurrentProcess().PrivateMemorySize64 });
    }
}
