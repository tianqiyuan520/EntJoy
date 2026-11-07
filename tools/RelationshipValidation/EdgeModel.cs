using System.Diagnostics;
using System.Runtime.CompilerServices;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;

namespace RelationshipValidation;

public sealed unsafe class EdgeModel
{
    private readonly record struct Value(Entity? Target, int Data);
    private readonly Dictionary<Entity, Value>[] columns = Enumerable.Range(0, 4).Select(_ => new Dictionary<Entity, Value>()).ToArray();
    private readonly Dictionary<Entity, int> alive = new();
    private readonly List<Entity> retired = new();
    private readonly List<string> wantedEvents = new(), actualEvents = new();
    private readonly Random random;
    private readonly int seed, population;
    private World world;
    private EntityManager em;
    private Exception observerFailure;
    private Entity[] reverseBuffer = new Entity[1024];

    public EdgeModel(int seed, int population) { this.seed = seed; this.population = population; random = new Random(seed); }
    private static string Event(int kind, string action, Entity source, Value value)
        => $"{kind}:{action}:{source.Id}/{source.Version}:{value.Target?.Id}/{value.Target?.Version}:{value.Data}";
    private void Observe<T>(int kind) where T : unmanaged, IRelationComponent
    {
        void Handler(string action, ReadOnlySpan<Entity> entities, ReadOnlySpan<T> values)
        {
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var value = values[i]; var slot = Unsafe.As<T, RelationSlot>(ref value);
                    int data = *(int*)((byte*)Unsafe.AsPointer(ref value) + 8);
                    actualEvents.Add(Event(kind, action, entities[i], new Value(slot.IsValid ? slot.ToEntity() : null, data)));
                    if (action == "Added" || action == "Set")
                    {
                        if (!em.TryGetRelationship<T>(entities[i], out var target) || !slot.Matches(target)
                            || !em.GetRelationsOf<T>(target).Contains(entities[i])) throw new Exception("Observer saw a partial graph.");
                    }
                }
            }
            catch (Exception failure) { observerFailure ??= failure; }
        }
        em.AddObserver<T>(ObserverEvents.Added, (entities, values) => Handler("Added", entities, values));
        em.AddObserver<T>(ObserverEvents.Set, (entities, values) => Handler("Set", entities, values));
        em.AddObserver<T>(ObserverEvents.Removed, (entities, values) => Handler("Removed", entities, values));
        em.AddObserver<T>(ObserverEvents.Destroyed, (entities, values) => Handler("Destroyed", entities, values));
    }
    private void Set(int kind, Entity source, Entity? target, int? data = null)
    {
        bool hadColumn = columns[kind].TryGetValue(source, out var old);
        if (data == null && hadColumn && old.Target.HasValue && old.Target.Equals(target)) return;
        var value = new Value(target, data ?? (hadColumn ? old.Data : 0));
        if (kind == 1 && target.HasValue)
        {
            var owners = columns[kind].Where(pair => !pair.Key.Equals(source) && pair.Value.Target.Equals(target)).ToArray();
            foreach (var owner in owners) { wantedEvents.Add(Event(kind, "Removed", owner.Key, owner.Value)); columns[kind].Remove(owner.Key); }
        }
        if (old.Target.HasValue || target.HasValue)
            wantedEvents.Add(Event(kind, old.Target.HasValue ? target.HasValue ? "Set" : "Removed" : "Added", source, target.HasValue ? value : old));
        columns[kind][source] = value;
    }
    private void Remove(int kind, Entity source)
    {
        if (columns[kind].Remove(source, out var old) && old.Target.HasValue) wantedEvents.Add(Event(kind, "Removed", source, old));
    }
    private void SetActual(int kind, Entity source, Entity target)
    {
        switch (kind)
        {
            case 0: em.AddRelationship<Link>(source, target); break;
            case 1: em.AddRelationship<Unique>(source, target); break;
            case 2: em.AddRelationship<Parent>(source, target); break;
            case 3: em.AddRelationship<Owns>(source, target); break;
        }
    }
    private void RemoveActual(int kind, Entity source)
    {
        switch (kind)
        {
            case 0: em.RemoveRelationship<Link>(source); break;
            case 1: em.RemoveRelationship<Unique>(source); break;
            case 2: em.RemoveRelationship<Parent>(source); break;
            case 3: em.RemoveRelationship<Owns>(source); break;
        }
    }
    private bool ParentCycle(Entity source, Entity target)
    {
        var visited = new HashSet<Entity>();
        for (var current = target; ;)
        {
            if (current.Equals(source) || !visited.Add(current)) return true;
            if (!columns[2].TryGetValue(current, out var value) || !value.Target.HasValue) return false;
            current = value.Target.Value;
        }
    }
    private void Delete(Entity root, bool force)
    {
        var deleted = new HashSet<Entity> { root };
        var queue = new Queue<Entity>(); queue.Enqueue(root);
        while (queue.TryDequeue(out var target))
            for (int kind = 0; kind < 4; kind++)
                if (force || kind == 3)
                    foreach (var pair in columns[kind])
                        if (pair.Value.Target.HasValue && pair.Value.Target.Value.Equals(target) && deleted.Add(pair.Key)) queue.Enqueue(pair.Key);
        for (int kind = 0; kind < 4; kind++)
            foreach (var pair in columns[kind].ToArray())
            {
                if (deleted.Contains(pair.Key))
                {
                    if (pair.Value.Target.HasValue) wantedEvents.Add(Event(kind, "Destroyed", pair.Key, pair.Value));
                    columns[kind].Remove(pair.Key);
                }
                else if (pair.Value.Target.HasValue && deleted.Contains(pair.Value.Target.Value))
                { wantedEvents.Add(Event(kind, "Removed", pair.Key, pair.Value)); columns[kind].Remove(pair.Key); }
            }
        foreach (var entity in deleted) { alive.Remove(entity); retired.Add(entity); }
        if (retired.Count > 2048) retired.RemoveRange(0, retired.Count - 2048);
        if (force) em.DestroyEntityCascade(root); else em.DestroyEntity(root);
    }
    private void Clone(Entity[] sources)
    {
        bool conflict = sources.Any(source => columns[1].TryGetValue(source, out var value)
            && value.Target.HasValue && !sources.Contains(value.Target.Value));
        if (conflict)
        {
            try { em.CloneEntities(sources); throw new Exception("Expected a clone conflict."); }
            catch (InvalidOperationException) { return; }
        }
        var copies = em.CloneEntities(sources);
        var map = sources.Select((entity, i) => (entity, copies[i])).ToDictionary(pair => pair.entity, pair => pair.Item2);
        foreach (var source in sources) alive[map[source]] = alive[source];
        for (int kind = 0; kind < 4; kind++)
            foreach (var source in sources)
                if (columns[kind].TryGetValue(source, out var value))
                {
                    if (value.Target.HasValue && map.TryGetValue(value.Target.Value, out var mapped)) value = value with { Target = mapped };
                    columns[kind][map[source]] = value;
                    if (value.Target.HasValue) wantedEvents.Add(Event(kind, "Added", map[source], value));
                }
    }
    private void Verify<T>(int kind) where T : unmanaged, IRelationComponent
    {
        var reverse = new Dictionary<Entity, HashSet<Entity>>();
        foreach (var pair in columns[kind])
            if (pair.Value.Target.HasValue)
            {
                if (!reverse.TryGetValue(pair.Value.Target.Value, out var sources)) reverse[pair.Value.Target.Value] = sources = new();
                sources.Add(pair.Key);
            }
        foreach (var source in alive.Keys)
        {
            var info = em.GetEntityInfoRef(source.Id);
            bool present = info.Archetype.Has(typeof(T)), expected = columns[kind].TryGetValue(source, out var wanted);
            if (present != expected) throw new Exception($"Column mismatch {kind}/{source.Id}");
            if (present)
            {
                var value = em.GetComponentReadOnly<T>(source);
                var target = Unsafe.As<T, RelationSlot>(ref value);
                int data = *(int*)((byte*)Unsafe.AsPointer(ref value) + 8);
                if (wanted.Target.HasValue ? !target.Matches(wanted.Target.Value) : !target.Matches(RelationSlot.Default)) throw new Exception("Forward mismatch.");
                if (data != wanted.Data) throw new Exception("Relationship payload mismatch.");
            }
            int count = em.GetRelationsOf<T>(source, reverseBuffer);
            if (reverse.TryGetValue(source, out var incoming))
            {
                if (count != incoming.Count || !incoming.SetEquals(reverseBuffer.Take(count))) throw new Exception("Reverse mismatch.");
            }
            else if (count != 0) throw new Exception("Unexpected reverse edge.");
        }
    }
    private void Verify()
    {
        if (observerFailure != null) throw new Exception("Observer graph verification failed.", observerFailure);
        if (em.GetAllArchetypes().Sum(arch => arch.EntityCount) != alive.Count) throw new Exception("Live population mismatch.");
        foreach (var pair in alive)
        {
            var info = em.GetEntityInfoRef(pair.Key.Id);
            if (info.Archetype == null || info.Version != pair.Key.Version || !info.Archetype.ChunkList[info.ChunkIndex].GetEntity(info.SlotInChunk).Equals(pair.Key)) throw new Exception("Identity mismatch.");
            if (em.GetComponentReadOnly<State>(pair.Key).Seen != pair.Value) throw new Exception("Job payload mismatch.");
        }
        Verify<Link>(0); Verify<Unique>(1); Verify<Parent>(2); Verify<Owns>(3);
        if (!wantedEvents.OrderBy(value => value).SequenceEqual(actualEvents.OrderBy(value => value)))
            throw new Exception($"Event mismatch: wanted {string.Join('|', wantedEvents)}, actual {string.Join('|', actualEvents)}");
        wantedEvents.Clear(); actualEvents.Clear();
    }
    public void Run(int steps, Action<object> emit)
    {
        using (world = new World("RelationModel"))
        {
            em = world.EntityManager;
            Observe<Link>(0); Observe<Unique>(1); Observe<Parent>(2); Observe<Owns>(3);
            for (int i = 0; i < population; i++) alive[em.NewEntity(typeof(State))] = 0;
            var timer = Stopwatch.StartNew();
            for (int step = 0; step < steps; step++)
            {
                int operation = random.Next(17), kind = random.Next(4);
                var entities = alive.Keys.ToArray();
                Entity source = entities[random.Next(entities.Length)], target = entities[random.Next(entities.Length)];
                try
                {
                    if (operation < 4 || operation == 7)
                    {
                        if (kind == 2 && ParentCycle(source, target))
                        { try { SetActual(kind, source, target); throw new Exception("Expected cycle rejection."); } catch (InvalidOperationException) { } }
                        else { Set(kind, source, target); SetActual(kind, source, target); }
                    }
                    else if (operation == 4) { Set(0, source, target, step); em.AddComponent(source, new Link { Target = RelationSlot.From(target), Data = step }); }
                    else if (operation == 5) { Remove(kind, source); RemoveActual(kind, source); }
                    else if (operation == 6) { Set(0, source, null, step); em.AddComponent(source, new Link { Target = RelationSlot.Default, Data = step }); }
                    else if (operation == 8 || operation == 9) Delete(source, operation == 9);
                    else if (operation == 10) Clone(entities.OrderBy(_ => random.Next()).Take(3).ToArray());
                    else if (operation == 11)
                    {
                        foreach (var pair in columns[0]) if (pair.Value.Target.HasValue && pair.Value.Target.Value.Equals(target)) alive[pair.Key]++;
                        new ReadLinkJob().Schedule(new QueryBuilder().WithAll<State>().WithRelationship<Link>(target), world).Complete();
                    }
                    else if (operation == 12) { var snapshot = em.TakeSnapshot(); em.Restore(snapshot); retired.Clear(); }
                    else if (operation == 13)
                    {
                        using var buffer = new DeferredCommandBuffer(); buffer.EnsureParallelWriters(2);
                        buffer.CreateParallelWriter(0).SetRelationshipTarget<Link>(20, source, target);
                        buffer.CreateParallelWriter(1).RemoveRelationship<Link>(10, source);
                        Remove(0, source); Set(0, source, target); buffer.PlaybackParallel(em);
                    }
                    else if (operation == 14 && retired.Count > 0)
                    { try { em.AddRelationship<Link>(source, retired[random.Next(retired.Count)]); throw new Exception("Expected stale target rejection."); } catch (InvalidOperationException) { } }
                    else if (operation == 15)
                    { try { em.AddRelationship<Parent>(source, source); throw new Exception("Expected self-cycle rejection."); } catch (InvalidOperationException) { } }
                    else if (columns[0].TryGetValue(source, out var old) && old.Target.HasValue)
                    { Set(0, source, old.Target); em.AddRelationship<Link>(source, old.Target.Value); }
                    while (alive.Count < population) alive[em.NewEntity(typeof(State))] = 0;
                    while (alive.Count > population + 16) Delete(alive.Keys.Last(), false);
                    Verify();
                    if (step % 1000 == 0 || step == steps - 1)
                    {
                        if (em.VerifyLocateTable(out _) != 0 || SteadyScenario.InvalidSlabs(em)) throw new Exception("Storage invariant failure.");
                        emit(new { kind = "model", seed, step, alive = alive.Count, elapsed_ms = timer.Elapsed.TotalMilliseconds });
                    }
                }
                catch (Exception failure) { throw new Exception($"seed={seed} step={step} operation={operation} relation={kind}", failure); }
            }
        }
    }
}
