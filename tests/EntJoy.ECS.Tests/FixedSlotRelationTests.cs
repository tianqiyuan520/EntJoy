using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>定长多槽列多值关系（[MultiRelation(MaxSlots=4)]，热数据，进 Job）。
    /// 源生成器注入 Slot1..Slot3 字段（列宽 32B）。</summary>
    [MultiRelation(MaxSlots = 4)]
    public partial struct HotSkill : IRelationComponent { public RelationSlot Target; }

    /// <summary>定长多槽列 + 入边唯一（背包热数据版）。</summary>
    [MultiRelation(MaxSlots = 4)]
    [ExclusiveTarget]
    public partial struct HotCarry : IRelationComponent { public RelationSlot Target; }

    public class FixedSlotRelationTests
    {
        private static World NewWorld() => new World("RelFixed" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void SourceGenerator_InjectedSlots_ColumnWidth()
        {
            var ct = ComponentTypeManager.GetComponentType(typeof(HotSkill));
            // 4 槽 × 8B = 32B 列宽（生成器注入 Slot1..3 + 首字段 Target）
            Assert.Equal(4, ct.MultiRelationMaxSlots);
            Assert.Equal(32, ct.Size);
            Assert.True(ct.IsMultiRelation);
        }

        [Fact]
        public void Add_AppendsSlots_GetAll()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));
            var heal = em.NewEntity(typeof(Position));
            var blink = em.NewEntity(typeof(Position));

            em.AddRelationship<HotSkill>(player, fireball);
            em.AddRelationship<HotSkill>(player, shield);
            em.AddRelationship<HotSkill>(player, heal);
            em.AddRelationship<HotSkill>(player, blink);

            Assert.Equal(4, em.GetRelationshipCount<HotSkill>(player));
            var skills = em.GetRelationships<HotSkill>(player);
            Assert.Equal(4, skills.Length);
            Assert.Contains(skills, e => e.Id == fireball.Id);
            Assert.Contains(skills, e => e.Id == shield.Id);
            Assert.Contains(skills, e => e.Id == heal.Id);
            Assert.Contains(skills, e => e.Id == blink.Id);
        }

        [Fact]
        public void Add_Idempotent_DuplicateTarget()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));

            em.AddRelationship<HotSkill>(player, fireball);
            em.AddRelationship<HotSkill>(player, fireball);   // 幂等

            Assert.Equal(1, em.GetRelationshipCount<HotSkill>(player));
        }

        [Fact]
        public void Add_ColumnFull_Throws()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var t1 = em.NewEntity(typeof(Position));
            var t2 = em.NewEntity(typeof(Position));
            var t3 = em.NewEntity(typeof(Position));
            var t4 = em.NewEntity(typeof(Position));
            var t5 = em.NewEntity(typeof(Position));

            em.AddRelationship<HotSkill>(player, t1);
            em.AddRelationship<HotSkill>(player, t2);
            em.AddRelationship<HotSkill>(player, t3);
            em.AddRelationship<HotSkill>(player, t4);
            Assert.Throws<InvalidOperationException>(() => em.AddRelationship<HotSkill>(player, t5));   // 槽满
        }

        [Fact]
        public void Remove_SpecificTarget_FreesSlot()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));

            em.AddRelationship<HotSkill>(player, fireball);
            em.AddRelationship<HotSkill>(player, shield);

            em.RemoveRelationship<HotSkill>(player, fireball);

            Assert.False(em.HasRelationship<HotSkill>(player, fireball));
            Assert.True(em.HasRelationship<HotSkill>(player, shield));
            Assert.Single(em.GetRelationships<HotSkill>(player));

            // 移除后槽位释放：可再次 Add 新 target
            var blink = em.NewEntity(typeof(Position));
            em.AddRelationship<HotSkill>(player, blink);
            Assert.True(em.HasRelationship<HotSkill>(player, blink));
        }

        [Fact]
        public void ExclusiveTarget_SecondOwner_Unbinds()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var a = em.NewEntity(typeof(Position));
            var b = em.NewEntity(typeof(Position));
            var sword = em.NewEntity(typeof(Position));

            em.AddRelationship<HotCarry>(a, sword);
            em.AddRelationship<HotCarry>(b, sword);   // 解绑 a

            Assert.False(em.HasRelationship<HotCarry>(a, sword));
            Assert.True(em.HasRelationship<HotCarry>(b, sword));
            Assert.Empty(em.GetRelationships<HotCarry>(a));
            Assert.Single(em.GetRelationsOf<HotCarry>(sword));
        }

        [Fact]
        public void WithRelationship_Filter_MultiSlot()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var p1 = em.NewEntity(typeof(Position));
            var p2 = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));

            // 两个角色都学火球（不同槽位），验证逐槽过滤
            em.AddRelationship<HotSkill>(p1, fireball);
            em.AddRelationship<HotSkill>(p2, fireball);

            int matched = 0;
            foreach (var _ in world.Query<Position>().WithRelationship<HotSkill>(fireball))
                matched++;
            Assert.Equal(2, matched);

            // 反向索引一致
            Assert.Equal(2, em.GetRelationsOf<HotSkill>(fireball).Length);
        }

        [Fact]
        public void WithRelationship_Filter_SecondarySlotOnly()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var p = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));

            // 槽 0 = fireball，槽 1 = shield；按 shield 过滤应命中 p（槽 1 匹配）
            em.AddRelationship<HotSkill>(p, fireball);
            em.AddRelationship<HotSkill>(p, shield);

            int matched = 0;
            foreach (var _ in world.Query<Position>().WithRelationship<HotSkill>(shield))
                matched++;
            Assert.Equal(1, matched);
        }

        [Fact]
        public void DestroyEntity_CleansIndex_AllSlots()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));

            em.AddRelationship<HotSkill>(player, fireball);
            em.AddRelationship<HotSkill>(player, shield);
            em.DestroyEntity(player);

            // 非槽 0 的反向索引条目也必须清理（CleanupSourceRelations 逐槽）
            Assert.Empty(em.GetRelationsOf<HotSkill>(fireball));
            Assert.Empty(em.GetRelationsOf<HotSkill>(shield));
        }

        [Fact]
        public void TargetDestroyed_BecomesInvalid()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));

            em.AddRelationship<HotSkill>(player, fireball);
            em.AddRelationship<HotSkill>(player, shield);
            em.DestroyEntity(fireball);   // 目标销毁（未级联）

            // 槽位 version 失效 → GetRelationships 过滤掉
            var skills = em.GetRelationships<HotSkill>(player);
            Assert.Single(skills);
            Assert.Equal(shield.Id, skills[0].Id);
        }

        [Fact]
        public void DeclaredCascade_MultiSlot_Works()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            // [OnTargetDeleted] 定长多槽列类型
            var a = em.NewEntity(typeof(Position));
            var hub = em.NewEntity(typeof(Position));
            em.AddRelationship<FixedSlotCascadeSkill>(a, hub);
            em.DestroyEntity(hub);

            Assert.Null(em.GetEntityInfoRef(a.Id).Archetype);
        }

        [Fact]
        public void ClearRelationships_AllSlots()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));

            em.AddRelationship<HotSkill>(player, fireball);
            em.AddRelationship<HotSkill>(player, shield);
            em.ClearRelationships<HotSkill>(player);

            Assert.Equal(0, em.GetRelationshipCount<HotSkill>(player));
            Assert.Empty(em.GetRelationships<HotSkill>(player));
            Assert.Empty(em.GetRelationsOf<HotSkill>(fireball));
        }

        [Fact]
        public void PrebuiltColumn_AddToPrecreatedEntity_Works()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            // 预建列：NewEntity 时直接带 HotSkill 列（槽位由 Chunk.AddEntity 初始化为 Default）
            var player = em.NewEntity(typeof(Position), typeof(HotSkill));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));

            em.AddRelationship<HotSkill>(player, fireball);
            em.AddRelationship<HotSkill>(player, shield);

            Assert.Equal(2, em.GetRelationshipCount<HotSkill>(player));
            Assert.True(em.HasRelationship<HotSkill>(player, fireball));
            Assert.True(em.HasRelationship<HotSkill>(player, shield));

            // 预建列未 Add 前：空槽不误判为占用（槽满仅在真正写满时）
            var fresh = em.NewEntity(typeof(Position), typeof(HotSkill));
            Assert.Equal(0, em.GetRelationshipCount<HotSkill>(fresh));
            Assert.Empty(em.GetRelationships<HotSkill>(fresh));
        }
    }

    /// <summary>声明级联的定长多槽列（测试用）。</summary>
    [MultiRelation(MaxSlots = 4)]
    [OnTargetDeleted(Cascade = true)]
    public partial struct FixedSlotCascadeSkill : IRelationComponent { public RelationSlot Target; }
}
