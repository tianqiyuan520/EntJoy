using EntJoy.ECS;
using EntJoy.ECS.JobSystem;

namespace EntJoySample.Relationship;

public struct RelationshipItem : IComponentData
{
    public int Count;
    public int Visits;
}

// 每件物品只属于一个背包；同一背包可以被多件物品指向。
public struct StoredIn : IRelationComponent
{
    public RelationSlot Target;
    public int Slot;
}

[ExclusiveRelation]
public struct EquippedIn : IRelationComponent { public RelationSlot Target; }

[AcyclicRelation]
[OnTargetDeleted(Cascade = true)]
public struct OwnedChildOf : IRelationComponent { public RelationSlot Target; }

public struct VisitBackpackItemsJob : IJobEntity
{
    public void Execute(ref RelationshipItem item, in StoredIn owner) { item.Visits++; }
}

public static class SingleTargetScenarios
{
    public static void Run()
    {
        ShowBackpack();
        ShowEquipment();
        ShowHierarchy();
        ShowJobsAndCopies();
    }

    private static Entity New(EntityManager manager, int count = 0)
    {
        var entity = manager.NewEntity(typeof(RelationshipItem));
        manager.SetComponent(entity, new RelationshipItem { Count = count });
        return entity;
    }

    private static void ShowBackpack()
    {
        using var world = new World("SingleTargetBackpack");
        var em = world.EntityManager;
        var bag = New(em);
        var otherBag = New(em);
        var apple = New(em, 5);
        var coin = New(em, 10);
        var events = new List<string>();
        em.AddObserver<StoredIn>(ObserverEvents.Added, (_, _) => events.Add("Added"));
        em.AddObserver<StoredIn>(ObserverEvents.Set, (_, _) => events.Add("Set"));
        em.AddObserver<StoredIn>(ObserverEvents.Removed, (_, _) => events.Add("Removed"));

        em.AddComponent(apple, new StoredIn { Target = RelationSlot.From(bag), Slot = 1 });
        em.AddComponent(coin, new StoredIn { Target = RelationSlot.From(bag), Slot = 2 });
        CheckMembers(em.GetRelationsOf<StoredIn>(bag), apple, coin);
        Check(em.TryGetRelationship<StoredIn>(apple, out var owner) && owner.Equals(bag), "item -> bag");
        long version = em.RelationshipVersion;
        em.AddRelationship<StoredIn>(apple, bag);
        Check(em.RelationshipVersion == version && events.Count == 2, "same target is a no-op");

        em.AddRelationship<StoredIn>(apple, otherBag);
        CheckMembers(em.GetRelationsOf<StoredIn>(bag), coin);
        CheckMembers(em.GetRelationsOf<StoredIn>(otherBag), apple);
        Check(em.GetComponentReadOnly<StoredIn>(apple).Slot == 1
            && em.GetComponentReadOnly<RelationshipItem>(apple).Count == 5, "transfer preserves data");
        em.RemoveRelationship<StoredIn>(apple);
        Check(!em.GetEntityInfoRef(apple.Id).Archetype.Has(typeof(StoredIn))
            && em.GetComponentReadOnly<RelationshipItem>(apple).Count == 5, "unlink keeps the item");
        Check(events.SequenceEqual(new[] { "Added", "Added", "Set", "Removed" }), "committed edge events");

        em.DestroyEntity(bag);
        Check(!em.HasRelationship<StoredIn>(coin) && !em.GetEntityInfoRef(coin.Id).Archetype.Has(typeof(StoredIn))
            && em.GetComponentReadOnly<RelationshipItem>(coin).Count == 10, "default target deletion keeps items");
        CheckMembers(em.GetRelationsOf<StoredIn>(bag));
        Check(events.Last() == "Removed" && events.Count == 5, "target deletion publishes unlink");
        Console.WriteLine("PASS: backpack 1:N via item -> bag (N:1), reverse lookup, transfer, unlink and target deletion.");
        Console.WriteLine("  bag <- [apple, coin]; move apple -> other bag; delete bag -> coin survives.\n");
    }

    private static void ShowEquipment()
    {
        using var world = new World("ExclusiveEquipmentSlot");
        var em = world.EntityManager;
        var hand = New(em);
        var sword = New(em);
        var axe = New(em);
        em.AddRelationship<EquippedIn>(sword, hand);
        em.AddRelationship<EquippedIn>(axe, hand);
        Check(!em.HasRelationship<EquippedIn>(sword), "the previous item releases the equipment slot");
        Check(em.TryGetRelationship<EquippedIn>(axe, out var slot) && slot.Equals(hand), "the new item owns the slot");
        CheckMembers(em.GetRelationsOf<EquippedIn>(hand), axe);
        Console.WriteLine("PASS: 1:1 equipment slot; equipping axe releases sword.\n");
    }

    private static void ShowHierarchy()
    {
        using var world = new World("OwnedHierarchy");
        var em = world.EntityManager;
        var root = New(em);
        var child = New(em);
        var leaf = New(em);
        var outside = New(em);
        em.AddRelationship<OwnedChildOf>(child, root);
        em.AddRelationship<OwnedChildOf>(leaf, child);
        Check(em.GetAncestors<OwnedChildOf>(leaf).SequenceEqual(new[] { child, root }), "ancestor order");
        CheckMembers(em.GetDescendants<OwnedChildOf>(root), child, leaf);
        bool rejected = false;
        try { em.AddRelationship<OwnedChildOf>(root, leaf); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && !em.HasRelationship<OwnedChildOf>(root), "cycle is rejected before mutation");
        CheckMembers(em.GetRelationsOf<OwnedChildOf>(root), child);

        em.DestroyEntity(root);
        Check(world.CreateEntityQuery(new QueryBuilder().WithAll<RelationshipItem>()).CalculateEntityCount() == 1
            && em.GetEntityInfoRef(outside.Id).Archetype != null, "cascade preserves only the outside entity");
        Console.WriteLine("PASS: acyclic hierarchy and declared cascade; root, child and leaf deleted, outside entity survives.\n");
    }

    private static void ShowJobsAndCopies()
    {
        using var world = new World("RelationshipJobsAndCopies");
        var em = world.EntityManager;
        var bag = New(em);
        var otherBag = New(em);
        var apple = New(em, 5);
        var coin = New(em, 10);
        em.AddComponent(apple, new StoredIn { Target = RelationSlot.From(bag), Slot = 1 });
        em.AddComponent(coin, new StoredIn { Target = RelationSlot.From(bag), Slot = 2 });
        var query = new QueryBuilder().WithAll<RelationshipItem>().WithRelationship<StoredIn>(bag);
        new VisitBackpackItemsJob().Schedule(query, world).Complete();
        Check(em.GetComponentReadOnly<RelationshipItem>(apple).Visits == 1
            && em.GetComponentReadOnly<RelationshipItem>(coin).Visits == 1
            && em.GetComponentReadOnly<RelationshipItem>(bag).Visits == 0, "Job visits only matching sources");

        using var commands = new DeferredCommandBuffer();
        commands.SetRelationshipTarget<StoredIn>(apple, otherBag);
        Check(em.GetRelationship<StoredIn>(apple).Equals(bag), "recording does not change the graph");
        commands.Playback(em);
        Check(em.GetRelationship<StoredIn>(apple).Equals(otherBag)
            && em.GetComponentReadOnly<StoredIn>(apple).Slot == 1, "ECB target change preserves payload");
        new VisitBackpackItemsJob().Schedule(query, world).Complete();
        Check(em.GetComponentReadOnly<RelationshipItem>(apple).Visits == 1
            && em.GetComponentReadOnly<RelationshipItem>(coin).Visits == 2, "Job selection follows rebinding");

        var clones = em.CloneEntities(new[] { bag, coin });
        Check(em.GetRelationship<StoredIn>(clones[1]).Equals(clones[0]), "clone remaps its internal target");
        CheckMembers(em.GetRelationsOf<StoredIn>(clones[0]), clones[1]);
        CheckMembers(em.GetRelationsOf<StoredIn>(bag), coin);
        Check(em.GetComponentReadOnly<RelationshipItem>(clones[1]).Count == 10, "clone keeps item data");

        var prefab = em.NewEntity(typeof(RelationshipItem), typeof(Prefab));
        em.AddComponent(prefab, new StoredIn { Target = RelationSlot.From(bag), Slot = 3 });
        var spawned = em.SpawnFrom(prefab, 2);
        Check(spawned.All(entity => !em.GetEntityInfoRef(entity.Id).Archetype.Has(typeof(Prefab))
            && em.GetRelationship<StoredIn>(entity).Equals(bag)
            && em.GetComponentReadOnly<StoredIn>(entity).Slot == 3), "Prefab keeps its external target");

        var snapshot = world.TakeSnapshot();
        em.AddRelationship<StoredIn>(coin, otherBag);
        world.Restore(snapshot);
        Check(em.GetRelationship<StoredIn>(coin).Equals(bag), "restore recovers the forward edge");
        CheckMembers(em.GetRelationsOf<StoredIn>(bag), coin, prefab, spawned[0], spawned[1]);
        commands.RemoveRelationship<StoredIn>(apple);
        commands.Playback(em);
        Check(!em.HasRelationship<StoredIn>(apple), "ECB unlink after restore");
        Check(em.VerifyLocateTable(out _) == 0, "entity locations remain consistent");
        Console.WriteLine("PASS: readonly Job filter, deferred ECB writes, group clone, Prefab and snapshot restore.\n");
    }

    private static void CheckMembers(Entity[] actual, params Entity[] expected)
        => Check(actual.Length == expected.Length && actual.ToHashSet().SetEquals(expected), "exact entity set");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Relationship scenario failed: {message}");
    }
}
