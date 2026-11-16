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
    public RelationSlot Target; // 指向背包：物品 → 背包。关系组件的首字段必须是 Target。
    public int Slot; // 背包格子号，由业务代码维护；更换目标时保留。
}

// 一个装备槽只放一件装备；新装备接管时，旧装备解除关系。
[RelationshipComponent(ExclusiveTarget = true)]
public struct EquippedIn : IRelationComponent { public RelationSlot Target; }

// 子节点只有一个父节点，不能成环；删除父节点时一起删除子节点。
[RelationshipComponent(Hierarchy = true, DeleteWithTarget = true)]
public struct OwnedChildOf : IRelationComponent { public RelationSlot Target; }

public struct VisitBackpackItemsJob : IJobEntity
{
    // 普通数据可以修改；关系参数使用 in，只读访问，关系变更通过 EntityManager 或 ECB 提交。
    public void Execute(ref RelationshipItem item, in StoredIn owner) { item.Visits++; }
}

public static class SingleTargetScenarios
{
    public static void Run(bool advanced)
    {
        ShowBackpack();
        ShowEquipment();
        ShowHierarchy();
        if (advanced) ShowJobsAndCopies();
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
        // 观察已提交的关系变化：放入、换包、取出。
        var events = new List<string>();
        em.AddObserver<StoredIn>(ObserverEvents.Added, (_, _) => events.Add("Added"));
        em.AddObserver<StoredIn>(ObserverEvents.Set, (_, _) => events.Add("Set"));
        em.AddObserver<StoredIn>(ObserverEvents.Removed, (_, _) => events.Add("Removed"));

        // 同时设置目标和格子号；两个物品都指向同一个背包。
        em.AddComponent(apple, new StoredIn { Target = RelationSlot.From(bag), Slot = 1 });
        em.AddComponent(coin, new StoredIn { Target = RelationSlot.From(bag), Slot = 2 });
        // GetRelationsOf 反向找物品；TryGetRelationship 从物品正向找背包。
        CheckMembers(em.GetRelationsOf<StoredIn>(bag), apple, coin);
        Check(em.TryGetRelationship<StoredIn>(apple, out var owner) && owner.Equals(bag), "item -> bag");
        // 重复设置同一个目标，不更新关系版本，也不重复通知观察者。
        long version = em.RelationshipVersion;
        em.AddRelationship<StoredIn>(apple, bag);
        Check(em.RelationshipVersion == version && events.Count == 2, "same target is a no-op");

        // 换包只更换目标，苹果数量和关系上的格子号都保留。
        em.AddRelationship<StoredIn>(apple, otherBag);
        CheckMembers(em.GetRelationsOf<StoredIn>(bag), coin);
        CheckMembers(em.GetRelationsOf<StoredIn>(otherBag), apple);
        Check(em.GetComponentReadOnly<StoredIn>(apple).Slot == 1
            && em.GetComponentReadOnly<RelationshipItem>(apple).Count == 5, "transfer preserves data");
        // 取出苹果会移除 StoredIn 组件，苹果实体及数量继续保留。
        em.RemoveRelationship<StoredIn>(apple);
        Check(!em.GetEntityInfoRef(apple.Id).Archetype.Has(typeof(StoredIn))
            && em.GetComponentReadOnly<RelationshipItem>(apple).Count == 5, "unlink keeps the item");
        Check(events.SequenceEqual(new[] { "Added", "Added", "Set", "Removed" }), "committed edge events");

        // 默认删除目标只解除关系：背包删除后，金币仍然存在。
        em.DestroyEntity(bag);
        Check(!em.HasRelationship<StoredIn>(coin) && !em.GetEntityInfoRef(coin.Id).Archetype.Has(typeof(StoredIn))
            && em.GetComponentReadOnly<RelationshipItem>(coin).Count == 10, "default target deletion keeps items");
        CheckMembers(em.GetRelationsOf<StoredIn>(bag));
        Check(events.Last() == "Removed" && events.Count == 5, "target deletion publishes unlink");
        Console.WriteLine("1. 背包：苹果和金币放入背包，可以从物品找背包，也可以从背包找物品。");
        Console.WriteLine("   苹果移到另一个背包后取出，数量保持为 5；删除原背包，10 枚金币仍然存在。\n");
    }

    private static void ShowEquipment()
    {
        using var world = new World("ExclusiveEquipmentSlot");
        var em = world.EntityManager;
        var hand = New(em);
        var sword = New(em);
        var axe = New(em);
        // 装备 → 手部槽位。目标独占，所以斧头接管时剑自动卸下，剑实体保留。
        em.AddRelationship<EquippedIn>(sword, hand);
        em.AddRelationship<EquippedIn>(axe, hand);
        Check(!em.HasRelationship<EquippedIn>(sword), "the previous item releases the equipment slot");
        Check(em.TryGetRelationship<EquippedIn>(axe, out var slot) && slot.Equals(hand), "the new item owns the slot");
        CheckMembers(em.GetRelationsOf<EquippedIn>(hand), axe);
        Console.WriteLine("2. 装备：手上先拿剑，再换斧头。剑自动卸下，斧头占用手部槽位。\n");
    }

    private static void ShowHierarchy()
    {
        using var world = new World("OwnedHierarchy");
        var em = world.EntityManager;
        var root = New(em);
        var child = New(em);
        var leaf = New(em);
        var outside = New(em);
        // 关系方向为子 → 父：leaf → child → root。
        em.AddRelationship<OwnedChildOf>(child, root);
        em.AddRelationship<OwnedChildOf>(leaf, child);
        Check(em.GetAncestors<OwnedChildOf>(leaf).SequenceEqual(new[] { child, root }), "ancestor order");
        CheckMembers(em.GetDescendants<OwnedChildOf>(root), child, leaf);
        // root 再指向 leaf 就会成环；失败后原层级保持不变。
        bool rejected = false;
        try { em.AddRelationship<OwnedChildOf>(root, leaf); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && !em.HasRelationship<OwnedChildOf>(root), "cycle is rejected before mutation");
        CheckMembers(em.GetRelationsOf<OwnedChildOf>(root), child);

        // DeleteWithTarget 让删除沿父 → 子传播；无关的 outside 实体保留。
        em.DestroyEntity(root);
        Check(world.CreateEntityQuery(new QueryBuilder().WithAll<RelationshipItem>()).CalculateEntityCount() == 1
            && em.GetEntityInfoRef(outside.Id).Archetype != null, "cascade preserves only the outside entity");
        Console.WriteLine("3. 父子：根节点 → 子节点 → 孙节点。拒绝把根挂到孙节点下；删除根时一起删除子孙。\n");
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
        // 按目标筛选：Job 只访问放在 bag 中的物品，不访问背包本身。
        var query = new QueryBuilder().WithAll<RelationshipItem>().WithRelationship<StoredIn>(bag);
        new VisitBackpackItemsJob().Schedule(query, world).Complete();
        Check(em.GetComponentReadOnly<RelationshipItem>(apple).Visits == 1
            && em.GetComponentReadOnly<RelationshipItem>(coin).Visits == 1
            && em.GetComponentReadOnly<RelationshipItem>(bag).Visits == 0, "Job visits only matching sources");

        // ECB 先记录换包命令，到 Playback 才修改关系；之后查询会匹配新的关系状态。
        using var commands = new DeferredCommandBuffer();
        commands.SetRelationshipTarget<StoredIn>(apple, otherBag);
        Check(em.GetRelationship<StoredIn>(apple).Equals(bag), "recording does not change the graph");
        commands.Playback(em);
        Check(em.GetRelationship<StoredIn>(apple).Equals(otherBag)
            && em.GetComponentReadOnly<StoredIn>(apple).Slot == 1, "ECB target change preserves payload");
        new VisitBackpackItemsJob().Schedule(query, world).Complete();
        Check(em.GetComponentReadOnly<RelationshipItem>(apple).Visits == 1
            && em.GetComponentReadOnly<RelationshipItem>(coin).Visits == 2, "Job selection follows rebinding");

        // 背包和金币一起复制：金币副本指向背包副本，原来的关系保持不变。
        var clones = em.CloneEntities(new[] { bag, coin });
        Check(em.GetRelationship<StoredIn>(clones[1]).Equals(clones[0]), "clone remaps its internal target");
        CheckMembers(em.GetRelationsOf<StoredIn>(clones[0]), clones[1]);
        CheckMembers(em.GetRelationsOf<StoredIn>(bag), coin);
        Check(em.GetComponentReadOnly<RelationshipItem>(clones[1]).Count == 10, "clone keeps item data");

        // Prefab 的目标 bag 在实例化集合之外，因此实例继续指向原背包。
        var prefab = em.NewEntity(typeof(RelationshipItem), typeof(Prefab));
        em.AddComponent(prefab, new StoredIn { Target = RelationSlot.From(bag), Slot = 3 });
        var spawned = em.SpawnFrom(prefab, 2);
        Check(spawned.All(entity => !em.GetEntityInfoRef(entity.Id).Archetype.Has(typeof(Prefab))
            && em.GetRelationship<StoredIn>(entity).Equals(bag)
            && em.GetComponentReadOnly<StoredIn>(entity).Slot == 3), "Prefab keeps its external target");

        // 保存后换包，再恢复：正向关系和反向查询一起回到保存时的状态。
        var snapshot = world.TakeSnapshot();
        em.AddRelationship<StoredIn>(coin, otherBag);
        world.Restore(snapshot);
        Check(em.GetRelationship<StoredIn>(coin).Equals(bag), "restore recovers the forward edge");
        CheckMembers(em.GetRelationsOf<StoredIn>(bag), coin, prefab, spawned[0], spawned[1]);
        commands.RemoveRelationship<StoredIn>(apple);
        commands.Playback(em);
        Check(!em.HasRelationship<StoredIn>(apple), "ECB unlink after restore");
        Check(em.VerifyLocateTable(out _) == 0, "entity locations remain consistent");
        Console.WriteLine("进阶：按背包筛选 Job、延迟换包、成组复制、Prefab 和快照恢复全部通过。\n");
    }

    private static void CheckMembers(Entity[] actual, params Entity[] expected)
        => Check(actual.Length == expected.Length && actual.ToHashSet().SetEquals(expected), "exact entity set");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Relationship scenario failed: {message}");
    }
}
