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
    public static void Run(bool advanced)
    {
        ShowSkills();
        if (advanced)
        {
            // 复用 09_ECS 中的关系声明，用同一组操作验证两种存储方式。
            // KnowsSkill 使用主线程列表；HotSkillSlot 使用四个 Job 可读的固定槽位。
            ShowLifecycle<KnowsSkill>("主线程目标列表");
            ShowLifecycle<HotSkillSlot>("四个固定槽位");
        }
    }

    private static void ShowSkills()
    {
        using var world = new World("Skills");
        var em = world.EntityManager;
        var mage = em.NewEntity(typeof(RelationshipItem));
        var knight = em.NewEntity(typeof(RelationshipItem));
        var fire = em.NewEntity(typeof(RelationshipItem));
        var ice = em.NewEntity(typeof(RelationshipItem));

        // 一个角色会多种技能；同一种技能也可以被多个角色学会。
        em.AddRelationship<KnowsSkill>(mage, fire);
        em.AddRelationship<KnowsSkill>(mage, ice);
        em.AddRelationship<KnowsSkill>(knight, fire);
        // 正向问“法师会什么”，反向问“谁会火球”。
        CheckMembers(em.GetRelationships<KnowsSkill>(mage), fire, ice);
        CheckMembers(em.GetRelationsOf<KnowsSkill>(fire), mage, knight);

        // 只解除法师与冰冻的连接，角色、技能及其他连接都保留。
        em.RemoveRelationship<KnowsSkill>(mage, ice);
        CheckMembers(em.GetRelationships<KnowsSkill>(mage), fire);
        Console.WriteLine("4. 技能：法师会火球和冰冻，骑士会火球。法师忘掉冰冻后，仍然会火球。\n");
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
        Console.WriteLine($"进阶多目标（{storage}）：法师 → 火球、冰冻；骑士 → 火球");

        // 多目标关系也能按某个目标筛选；两种原生 Job 都只访问会火球的角色。
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
        // 副本保存的是复制时的目标；原关系移除冰冻后，Job 仍能读取副本里的冰冻。
        em.RemoveRelationship<T>(mage, ice);
        new ReadTargetsIspc { Targets = targets.AsArray().AsReadOnly(), Ids = ids }.Schedule(ids.Length, 1).Complete();
        Require(ids[1] == ice.Id && !em.HasRelationship<T>(mage, ice), "ISPC uses the owned snapshot");

        // 并行记录按 sortKey 回放：10 清空、20 加冰冻、30 加火球，与记录先后无关。
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

        // 复制集合内的火球改指副本；集合外的冰冻仍指向原实体。
        var copies = em.CloneEntities(new[] { mage, fire });
        CheckMembers(em.GetRelationships<T>(copies[0]), copies[1], ice);
        CheckMembers(em.GetRelationsOf<T>(copies[1]), copies[0]);
        // 只实例化角色模板，技能没有一起复制，所以实例共享原技能目标。
        var prefab = em.NewEntity(typeof(RelationshipItem), typeof(Prefab));
        em.AddRelationship<T>(prefab, fire); em.AddRelationship<T>(prefab, ice);
        var spawned = em.SpawnFrom(prefab, 2);
        foreach (var entity in spawned) CheckMembers(em.GetRelationships<T>(entity), fire, ice);

        int events = 0;
        em.AddObserver<T>(ObserverEvents.Added | ObserverEvents.Removed | ObserverEvents.Destroyed, (_, _) => events++);
        // 恢复整份快照会还原实体和关系，但不会逐条重发关系通知。
        var snapshot = world.TakeSnapshot();
        em.ClearRelationships<T>(mage); em.DestroyEntity(ice);
        int afterMutation = events;
        world.Restore(snapshot);
        Require(events == afterMutation, "restore does not replay relationship events");
        CheckMembers(em.GetRelationships<T>(mage), fire, ice);
        CheckMembers(em.GetRelationships<T>(copies[0]), copies[1], ice);

        // 默认删除技能只移除对应连接；删除角色则同时清理其反向索引。
        em.DestroyEntity(fire);
        CheckMembers(em.GetRelationships<T>(mage), ice);
        CheckMembers(em.GetRelationships<T>(knight));
        em.DestroyEntity(mage);
        Require(!em.GetRelationsOf<T>(ice).Contains(mage), "source destruction clears reverse entries");
        Require(em.VerifyLocateTable(out _) == 0, "multi-target entity locations");
        Console.WriteLine($"通过：{storage} 的原生筛选、目标副本、ECB、复制、快照及删除验证。\n");
    }

    private static void CheckMembers(Entity[] actual, params Entity[] expected)
        => Require(actual.Length == expected.Length && actual.ToHashSet().SetEquals(expected), "exact target/source set");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Multi-target scenario failed: {message}");
    }
}
