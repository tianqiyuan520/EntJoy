using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using NativeTranspiler;

namespace EntJoySample.Relationship;

// 一条边是一个普通实体。两个端点各自通过关系指向，边数据由普通组件保存。
[OnTargetDeleted(Cascade = true)]
public struct SkillUser : IRelationComponent { public RelationSlot Target; }

[OnTargetDeleted(Cascade = true)]
public struct SkillDefinition : IRelationComponent { public RelationSlot Target; }

public struct SkillProgress : IComponentData
{
    public int Level;
    public int Cooldown;
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct TickSkillCooldownCpp : IJobEntity
{
    public void Execute(ref SkillProgress progress, in SkillDefinition skill)
    {
        if (progress.Cooldown > 0) progress.Cooldown--;
    }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct TickSkillCooldownIspc : IJobEntity
{
    public void Execute(ref SkillProgress progress, in SkillDefinition skill)
    {
        if (progress.Cooldown > 0) progress.Cooldown--;
    }
}

public static class EdgeDataScenario
{
    public static void Run()
    {
        using var world = new World("SkillEdgeData");
        var em = world.EntityManager;
        var mage = em.NewEntity(typeof(RelationshipItem));
        var knight = em.NewEntity(typeof(RelationshipItem));
        var fire = em.NewEntity(typeof(RelationshipItem));
        var mageFire = CreateEdge(mage, fire, new SkillProgress { Level = 3, Cooldown = 5 });
        var knightFire = CreateEdge(knight, fire, new SkillProgress { Level = 1, Cooldown = 9 });
        Require(em.GetRelationsOf<SkillDefinition>(fire).Length == 2, "shared skill has two independent edges");
        var query = new QueryBuilder().WithAll<SkillProgress>().WithRelationship<SkillUser>(mage);
        new TickSkillCooldownCpp().Schedule(query, world).Complete();
        new TickSkillCooldownIspc().Schedule(query, world).Complete();
        Require(em.GetComponentReadOnly<SkillProgress>(mageFire).Cooldown == 3
            && em.GetComponentReadOnly<SkillProgress>(knightFire).Cooldown == 9, "native jobs change only the selected user's edge");

        // 显式复制角色、技能和关联边，两个端点由已有关系复制核心统一重映射。
        var copies = em.CloneEntities(new[] { mage, fire, mageFire });
        Require(em.GetRelationship<SkillUser>(copies[2]).Equals(copies[0])
            && em.GetRelationship<SkillDefinition>(copies[2]).Equals(copies[1]), "edge endpoints remap together");
        Require(em.GetComponentReadOnly<SkillProgress>(copies[2]).Level == 3, "edge payload is copied");
        var snapshot = world.TakeSnapshot();
        em.SetComponent(mageFire, new SkillProgress { Level = 8, Cooldown = 99 });
        world.Restore(snapshot);
        Require(em.GetComponentReadOnly<SkillProgress>(mageFire).Level == 3, "edge payload is restored");

        em.DestroyEntity(fire);
        Require(em.GetRelationsOf<SkillUser>(mage).Length == 0 && em.GetRelationsOf<SkillUser>(knight).Length == 0,
            "skill deletion destroys its edge entities");
        Require(world.CreateEntityQuery(new QueryBuilder().WithAll<SkillProgress>()).CalculateEntityCount() == 1,
            "the cloned skill's independent edge survives");
        Require(em.GetEntityInfoRef(mage.Id).Archetype != null && em.GetEntityInfoRef(knight.Id).Archetype != null,
            "endpoint deletion keeps the other endpoints");
        em.DestroyEntity(copies[0]);
        Require(world.CreateEntityQuery(new QueryBuilder().WithAll<SkillProgress>()).CalculateEntityCount() == 0,
            "user deletion also destroys its edges");
        Console.WriteLine("PASS: edge entities: independent level/cooldown, C++/ISPC jobs, endpoint remapping, snapshot and cascade cleanup.\n");

        Entity CreateEdge(Entity user, Entity skill, SkillProgress progress)
        {
            var edge = em.NewEntity(typeof(SkillProgress));
            try
            {
                em.AddRelationship<SkillUser>(edge, user);
                em.AddRelationship<SkillDefinition>(edge, skill);
                em.SetComponent(edge, progress);
                return edge;
            }
            catch { em.DestroyEntity(edge); throw; }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Edge data scenario failed: {message}");
    }
}
