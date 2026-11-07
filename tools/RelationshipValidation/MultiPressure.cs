using System.Diagnostics;
using System.Reflection;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;

namespace RelationshipValidation;

public static class MultiPressure
{
    public static void Run(int size, int degree, string layout, Action<object> emit)
    {
        if (layout != "fixed") RunGraph<MultiLinks>(size, degree, emit);
        if (layout != "list") RunGraph<FixedLinks>(size, degree, emit);
        if (layout != "fixed") RunEnabled<MultiLinks>(emit);
        if (layout != "list") RunEnabled<FixedLinks>(emit);
        if (layout != "fixed") RunHighOutgoing(size, emit);
        RunCascade(Math.Min(size, 10000), emit);
    }

    private static void RunEnabled<T>(Action<object> emit) where T : unmanaged, IRelationComponent
    {
        using var graph = new MultiGraph<T>(129, 8, enabled: true);
        var query = new QueryBuilder().WithAll<State>().WithRelationship<T>(graph.Targets[0]).WithEnabled<StressActive>();
        new MultiReadJob().Schedule(query, graph.World).Complete();
        for (int i = 0; i < graph.Sources.Length; i++) if (i % 5 != 0) graph.Seen[i]++;
        graph.Verify();
        emit(new { kind = "multi-enabled", layout = typeof(T).Name, sources = 129, selected = 103, success = true });
    }

    private static void RunGraph<T>(int size, int degree, Action<object> emit) where T : unmanaged, IRelationComponent
    {
        using (var graph = new MultiGraph<T>(size, degree))
        {
            graph.Verify();
            var em = graph.Manager;
            var inputs = graph.CopyInputs();
            Entity[] copies = null;
            Measure("multi-clone", typeof(T).Name, size, (long)size * degree,
                () => copies = em.CloneEntities(inputs), () =>
                {
                    graph.VerifyCopies(copies);
                    Destroy(em, copies);
                    graph.Verify();
                }, emit);
            var snapshot = em.TakeSnapshot();
            for (int sample = 0; sample < 3; sample++)
            {
                em.Restore(snapshot);
                long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                em.DestroyEntity(graph.Targets[0]);
                long ticks = Stopwatch.GetTimestamp() - start, bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                var scratch = new Entity[degree];
                foreach (var source in graph.Sources)
                {
                    int count = em.GetRelationships<T>(source, scratch);
                    MultiGraph<T>.Require(count == degree - 1 && !scratch.AsSpan(0, count).Contains(graph.Targets[0]), "wide target deletion");
                }
                MultiGraph<T>.Require(em.VerifyLocateTable(out _) == 0, "wide deletion locate");
                emit(new { kind = "multi-cost", name = "multi-delete-hub", layout = typeof(T).Name, size, edges = (long)size * degree,
                    sample, elapsed_ticks = ticks, milliseconds = ticks * 1000.0 / Stopwatch.Frequency, allocated_bytes = bytes });
            }
            em.Restore(snapshot);
            graph.Verify();
        }
        ChunkMemoryPool.Trim();
        if (SteadyScenario.NativeBytes("ReservedBytes") != 0) throw new Exception("M:N graph retained native chunk memory.");
    }

    private static void RunHighOutgoing(int size, Action<object> emit)
    {
        using (var world = new World("HighOutgoingMultiRelation"))
        {
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(State));
            var targets = em.CreateEntities(size, typeof(State));
            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            foreach (var target in targets) em.AddRelationship<MultiLinks>(source, target);
            long ticks = Stopwatch.GetTimestamp() - start;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var actual = em.GetRelationships<MultiLinks>(source);
            if (actual.Length != size || !actual.ToHashSet().SetEquals(targets)) throw new Exception("High-outgoing forward set mismatch.");
            var snapshot = em.TakeSnapshot();
            var clone = em.CloneEntities(new[] { source });
            if (!em.GetRelationships<MultiLinks>(clone[0]).ToHashSet().SetEquals(targets)) throw new Exception("High-outgoing clone mismatch.");
            em.DestroyEntity(clone[0]);
            emit(new { kind = "multi-cost", name = "multi-build-high-outgoing", layout = "MultiLinks", size, edges = size,
                sample = 0, elapsed_ticks = ticks, milliseconds = ticks * 1000.0 / Stopwatch.Frequency, allocated_bytes = allocated });
            for (int sample = 0; sample < 3; sample++)
            {
                em.Restore(snapshot);
                before = GC.GetAllocatedBytesForCurrentThread(); start = Stopwatch.GetTimestamp();
                Destroy(em, targets);
                ticks = Stopwatch.GetTimestamp() - start; allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (em.GetRelationshipCount<MultiLinks>(source) != 0 || !IndexEmpty(em)) throw new Exception("High-outgoing bulk deletion left edges.");
                if (em.GetAllArchetypes().Sum(arch => arch.EntityCount) != 1 || em.VerifyLocateTable(out _) != 0) throw new Exception("High-outgoing survivor mismatch.");
                emit(new { kind = "multi-cost", name = "multi-delete-many-targets", layout = "MultiLinks", size, edges = size,
                    sample, elapsed_ticks = ticks, milliseconds = ticks * 1000.0 / Stopwatch.Frequency, allocated_bytes = allocated });
            }
        }
        ChunkMemoryPool.Trim();
        if (SteadyScenario.NativeBytes("ReservedBytes") != 0) throw new Exception("High-outgoing graph retained native chunk memory.");
    }

    private static void RunCascade(int size, Action<object> emit)
    {
        using (var world = new World("MultiCascadeCycle"))
        {
            var em = world.EntityManager;
            var entities = em.CreateEntities(Math.Max(3, size), typeof(State));
            for (int i = 0; i < entities.Length; i++)
            {
                em.AddRelationship<CascadeLinks>(entities[i], entities[(i + 1) % entities.Length]);
                em.AddRelationship<CascadeLinks>(entities[i], entities[(i + 2) % entities.Length]);
            }
            em.DestroyEntity(entities[0]);
            if (em.GetAllArchetypes().Sum(arch => arch.EntityCount) != 0 || !IndexEmpty(em) || em.VerifyLocateTable(out _) != 0)
                throw new Exception("M:N cascade cycle did not destroy its closure exactly once.");
            emit(new { kind = "multi-cascade", entities = entities.Length, edges = entities.Length * 2, success = true });
        }
        ChunkMemoryPool.Trim();
    }

    private static void Measure(string name, string layout, int size, long edges, Action action, Action verify, Action<object> emit)
    {
        action(); verify();
        for (int sample = 0; sample < 3; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            action();
            long ticks = Stopwatch.GetTimestamp() - start, bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            verify();
            emit(new { kind = "multi-cost", name, layout, size, edges, sample, elapsed_ticks = ticks,
                milliseconds = ticks * 1000.0 / Stopwatch.Frequency, allocated_bytes = bytes });
        }
    }

    internal static unsafe void Destroy(EntityManager manager, Entity[] entities)
    {
        fixed (Entity* pointer = entities)
            if (manager.DestroyEntities(pointer, entities.Length) != entities.Length) throw new Exception("M:N batch destruction count mismatch.");
    }

    internal static bool IndexEmpty(EntityManager manager)
    {
        var index = typeof(EntityManager).GetField("_relationIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        return (bool)typeof(RelationIndex).GetProperty("IsEmpty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(index)!;
    }
}
