using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using EntJoySample.ECS;
using NativeTranspiler;

namespace EntJoySample.Relationship;

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct VisitSkillUsersCpp : IJobEntity
{
    public void Execute(ref RelationshipItem item) { item.Visits++; }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct VisitSkillUsersIspc : IJobEntity
{
    public void Execute(ref RelationshipItem item) { item.Visits++; }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct ReadTargetsCpp : IJobParallelFor
{
    public NativeArray<Entity>.ReadOnly Targets;
    public NativeArray<int> Ids;
    public void Execute(int index) { Ids[index] = Targets[index].Id; }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct ReadTargetsIspc : IJobParallelFor
{
    public NativeArray<Entity>.ReadOnly Targets;
    public NativeArray<int> Ids;
    public void Execute(int index) { Ids[index] = Targets[index].Id; }
}

public static class MultiTargetScenarios
{
    public static void Run()
    {
        ShowLifecycle<KnowsSkill>("managed list");
        ShowLifecycle<HotSkillSlot>("four-slot column");
    }

    private static void ShowLifecycle<T>(string storage) where T : unmanaged, IRelationComponent
    {
        using var world = new World($"MultiTarget-{storage}");
        var em = world.EntityManager;
        Entity New() => em.NewEntity(typeof(RelationshipItem));
        var mage = New(); var knight = New(); var fire = New(); var ice = New();
        em.AddRelationship<T>(mage, fire); em.AddRelationship<T>(mage, ice);
        em.AddRelationship<T>(knight, fire);
        CheckMembers(em.GetRelationships<T>(mage), fire, ice);
        CheckMembers(em.GetRelationsOf<T>(fire), mage, knight);
        Console.WriteLine($"--- M:N ({storage}): mage -> [fire, ice]; knight -> [fire] ---");

        var query = new QueryBuilder().WithAll<RelationshipItem>().WithRelationship<T>(fire);
        new VisitSkillUsersCpp().Schedule(query, world).Complete();
        new VisitSkillUsersIspc().Schedule(query, world).Complete();
        Require(em.GetComponentReadOnly<RelationshipItem>(mage).Visits == 2
            && em.GetComponentReadOnly<RelationshipItem>(knight).Visits == 2
            && em.GetComponentReadOnly<RelationshipItem>(fire).Visits == 0, "native exact target filter");

        // 托管列表不直接借给 worker。复制到自己拥有的原生列表，保留到 Job 完成。
        using var targets = new NativeList<Entity>(2);
        em.CopyRelationships<T>(mage, targets);
        using var ids = new NativeArray<int>(targets.Length);
        new ReadTargetsCpp { Targets = targets.AsArray().AsReadOnly(), Ids = ids }.Schedule(ids.Length, 1).Complete();
        Require(ids[0] == fire.Id && ids[1] == ice.Id, "C++ forward snapshot");
        em.RemoveRelationship<T>(mage, ice);
        new ReadTargetsIspc { Targets = targets.AsArray().AsReadOnly(), Ids = ids }.Schedule(ids.Length, 1).Complete();
        Require(ids[1] == ice.Id && !em.HasRelationship<T>(mage, ice), "ISPC uses the owned snapshot");

        using var commands = new DeferredCommandBuffer();
        commands.EnsureParallelWriters(2);
        commands.CreateParallelWriter(0).AddRelationship<T>(30, mage, fire);
        commands.CreateParallelWriter(1).ClearRelationships<T>(10, mage);
        commands.CreateParallelWriter(1).AddRelationship<T>(20, mage, ice);
        commands.PlaybackParallel(em);
        CheckMembers(em.GetRelationships<T>(mage), ice, fire);
        commands.RemoveRelationship<T>(mage, ice);
        commands.Playback(em);
        CheckMembers(em.GetRelationships<T>(mage), fire);
        commands.AddRelationship<T>(mage, ice); commands.Playback(em);

        var copies = em.CloneEntities(new[] { mage, fire });
        CheckMembers(em.GetRelationships<T>(copies[0]), copies[1], ice);
        CheckMembers(em.GetRelationsOf<T>(copies[1]), copies[0]);
        var prefab = em.NewEntity(typeof(RelationshipItem), typeof(Prefab));
        em.AddRelationship<T>(prefab, fire); em.AddRelationship<T>(prefab, ice);
        var spawned = em.SpawnFrom(prefab, 2);
        foreach (var entity in spawned) CheckMembers(em.GetRelationships<T>(entity), fire, ice);

        int events = 0;
        em.AddObserver<T>(ObserverEvents.Added | ObserverEvents.Removed | ObserverEvents.Destroyed, (_, _) => events++);
        var snapshot = world.TakeSnapshot();
        em.ClearRelationships<T>(mage); em.DestroyEntity(ice);
        int afterMutation = events;
        world.Restore(snapshot);
        Require(events == afterMutation, "restore does not replay relationship events");
        CheckMembers(em.GetRelationships<T>(mage), fire, ice);
        CheckMembers(em.GetRelationships<T>(copies[0]), copies[1], ice);

        em.DestroyEntity(fire);
        CheckMembers(em.GetRelationships<T>(mage), ice);
        CheckMembers(em.GetRelationships<T>(knight));
        em.DestroyEntity(mage);
        Require(!em.GetRelationsOf<T>(ice).Contains(mage), "source destruction clears reverse entries");
        Require(em.VerifyLocateTable(out _) == 0, "multi-target entity locations");
        Console.WriteLine($"PASS: M:N {storage}: C++/ISPC filter and owned target snapshots, sorted ECB, clone, Prefab, snapshot and deletion.\n");
    }

    private static void CheckMembers(Entity[] actual, params Entity[] expected)
        => Require(actual.Length == expected.Length && actual.ToHashSet().SetEquals(expected), "exact target/source set");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Multi-target scenario failed: {message}");
    }
}
