using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using NativeTranspiler;

namespace EntJoySample.Relationship;

// 每条“角色学会技能”的记录是一个实体；这条关系从记录指向角色。
// 删除角色时一起删除记录。
[RelationshipComponent(DeleteWithTarget = true)]
public struct SkillUser : IRelationComponent { public RelationSlot Target; }

// 另一条关系从记录指向技能；删除技能时一起删除记录，角色继续存在。
[RelationshipComponent(DeleteWithTarget = true)]
public struct SkillDefinition : IRelationComponent { public RelationSlot Target; }

// 等级和冷却属于这条学习记录，同一个技能在不同角色身上可以有不同进度。
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
    public static void Run(bool advanced)
    {
        using var world = new World("SkillEdgeData");
        var em = world.EntityManager;
        var mage = em.NewEntity(typeof(RelationshipItem));
        var knight = em.NewEntity(typeof(RelationshipItem));
        var fire = em.NewEntity(typeof(RelationshipItem));
        // 两条记录共享同一个火球技能，分别保存法师和骑士的进度。
        var mageFire = CreateEdge(mage, fire, new SkillProgress { Level = 3, Cooldown = 5 });
        var knightFire = CreateEdge(knight, fire, new SkillProgress { Level = 1, Cooldown = 9 });
        Require(em.GetRelationsOf<SkillDefinition>(fire).Length == 2, "shared skill has two independent edges");
        // 从记录指向的角色筛选：只更新法师的冷却，骑士的记录保持不变。
        var query = new QueryBuilder().WithAll<SkillProgress>().WithRelationship<SkillUser>(mage);
        if (advanced)
        {
            // 两个后端各减一次冷却，法师从 5 变为 3；默认模式保持为 5。
            new TickSkillCooldownCpp().Schedule(query, world).Complete();
            new TickSkillCooldownIspc().Schedule(query, world).Complete();
        }
        Require(em.GetComponentReadOnly<SkillProgress>(mageFire).Cooldown == (advanced ? 3 : 5)
            && em.GetComponentReadOnly<SkillProgress>(knightFire).Cooldown == 9, "native jobs change only the selected user's edge");

        Console.WriteLine("5. 技能进度：法师的火球是 3 级，骑士的火球是 1 级。用两个独立记录实体保存各自等级和冷却。\n");
        if (!advanced)
        {
            // 火球删除后，两条记录都随之删除，两个角色继续存在。
            em.DestroyEntity(fire);
            Require(em.GetRelationsOf<SkillUser>(mage).Length == 0
                && em.GetRelationsOf<SkillUser>(knight).Length == 0, "删除技能时清理记录");
            Require(em.GetEntityInfoRef(mage.Id).Archetype != null
                && em.GetEntityInfoRef(knight.Id).Archetype != null, "角色继续存在");
            return;
        }

        // 显式复制角色、技能和关联边，两个端点由已有关系复制核心统一重映射。
        var copies = em.CloneEntities(new[] { mage, fire, mageFire });
        Require(em.GetRelationship<SkillUser>(copies[2]).Equals(copies[0])
            && em.GetRelationship<SkillDefinition>(copies[2]).Equals(copies[1]), "edge endpoints remap together");
        Require(em.GetComponentReadOnly<SkillProgress>(copies[2]).Level == 3, "edge payload is copied");
        // 快照同时保存记录上的普通数据和两个端点关系。
        var snapshot = world.TakeSnapshot();
        em.SetComponent(mageFire, new SkillProgress { Level = 8, Cooldown = 99 });
        world.Restore(snapshot);
        Require(em.GetComponentReadOnly<SkillProgress>(mageFire).Level == 3, "edge payload is restored");

        // 删除原技能只清理原记录；复制出来的技能及记录独立存在。
        em.DestroyEntity(fire);
        Require(em.GetRelationsOf<SkillUser>(mage).Length == 0 && em.GetRelationsOf<SkillUser>(knight).Length == 0,
            "skill deletion destroys its edge entities");
        Require(world.CreateEntityQuery(new QueryBuilder().WithAll<SkillProgress>()).CalculateEntityCount() == 1,
            "the cloned skill's independent edge survives");
        Require(em.GetEntityInfoRef(mage.Id).Archetype != null && em.GetEntityInfoRef(knight.Id).Archetype != null,
            "endpoint deletion keeps the other endpoints");
        // 删除角色副本也会清理其记录：任意一端删除都能触发清理。
        em.DestroyEntity(copies[0]);
        Require(world.CreateEntityQuery(new QueryBuilder().WithAll<SkillProgress>()).CalculateEntityCount() == 0,
            "user deletion also destroys its edges");
        Console.WriteLine("进阶：技能记录的原生更新、端点复制、快照与删除清理全部通过。\n");

        Entity CreateEdge(Entity user, Entity skill, SkillProgress progress)
        {
            // 创建记录，连接两个端点，再保存进度；任一步失败都回收本次记录。
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
