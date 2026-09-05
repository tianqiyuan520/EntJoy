using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>多值关系（出边 1:N / M:N）：技能列表。</summary>
    [MultiRelation]
    public struct Skill : IRelationComponent { public RelationSlot Target; }

    /// <summary>多值关系 + 入边唯一（背包：角色多物品，物品唯一持有者）。</summary>
    [MultiRelation]
    [ExclusiveTarget]
    public struct OwnsItem : IRelationComponent { public RelationSlot Target; }

    public class MultiRelationTests
    {
        private static World NewWorld() => new World("RelMulti" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Multi_Add_AppendsMultipleTargets()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));
            var heal = em.NewEntity(typeof(Position));

            em.AddRelationship<Skill>(player, fireball);
            em.AddRelationship<Skill>(player, shield);
            em.AddRelationship<Skill>(player, heal);

            var skills = em.GetRelationships<Skill>(player);
            Assert.Equal(3, skills.Length);
            Assert.Equal(3, em.GetRelationshipCount<Skill>(player));
            Assert.Contains(skills, e => e.Id == fireball.Id);
            Assert.Contains(skills, e => e.Id == shield.Id);
            Assert.Contains(skills, e => e.Id == heal.Id);
        }

        [Fact]
        public void Multi_Add_Idempotent_DuplicateTarget()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));

            em.AddRelationship<Skill>(player, fireball);
            em.AddRelationship<Skill>(player, fireball);   // 重复 Add → 幂等

            Assert.Equal(1, em.GetRelationshipCount<Skill>(player));
        }

        [Fact]
        public void Multi_RemoveSpecificTarget()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));

            em.AddRelationship<Skill>(player, fireball);
            em.AddRelationship<Skill>(player, shield);

            em.RemoveRelationship<Skill>(player, fireball);

            Assert.False(em.HasRelationship<Skill>(player, fireball));
            Assert.True(em.HasRelationship<Skill>(player, shield));
            Assert.Single(em.GetRelationships<Skill>(player));
        }

        [Fact]
        public void Multi_Has_And_ReverseQuery()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var playerA = em.NewEntity(typeof(Position));
            var playerB = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));

            em.AddRelationship<Skill>(playerA, fireball);
            em.AddRelationship<Skill>(playerB, fireball);   // M:N：两个角色都会火球

            Assert.True(em.HasRelationship<Skill>(playerA, fireball));
            Assert.True(em.HasRelationship<Skill>(playerB, fireball));

            // 反向：谁持有 fireball（反向索引，O(1)）
            var owners = em.GetRelationsOf<Skill>(fireball);
            Assert.Equal(2, owners.Length);
        }

        [Fact]
        public void ExclusiveTarget_SecondOwner_UnbindsOldOwner()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var playerA = em.NewEntity(typeof(Position));
            var playerB = em.NewEntity(typeof(Position));
            var sword = em.NewEntity(typeof(Position));

            em.AddRelationship<OwnsItem>(playerA, sword);
            Assert.True(em.HasRelationship<OwnsItem>(playerA, sword));

            // B 也持有 sword → A 被解绑（背包语义：物品唯一持有者）
            em.AddRelationship<OwnsItem>(playerB, sword);

            Assert.False(em.HasRelationship<OwnsItem>(playerA, sword), "A should lose the item");
            Assert.True(em.HasRelationship<OwnsItem>(playerB, sword));
            Assert.Empty(em.GetRelationships<OwnsItem>(playerA));
            Assert.Single(em.GetRelationsOf<OwnsItem>(sword));
            Assert.Equal(playerB.Id, em.GetRelationsOf<OwnsItem>(sword)[0].Id);
        }

        [Fact]
        public void Multi_ClearRelationships_RemovesAll()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));

            em.AddRelationship<Skill>(player, fireball);
            em.AddRelationship<Skill>(player, shield);

            em.ClearRelationships<Skill>(player);

            Assert.Equal(0, em.GetRelationshipCount<Skill>(player));
            Assert.Empty(em.GetRelationships<Skill>(player));
            Assert.Empty(em.GetRelationsOf<Skill>(fireball));
        }

        [Fact]
        public void Multi_DestroyEntity_CleansForwardAndReverse()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));

            em.AddRelationship<Skill>(player, fireball);
            em.AddRelationship<Skill>(player, shield);

            em.DestroyEntity(player);

            Assert.Empty(em.GetRelationships<Skill>(player));   // 已销毁 → 空
            Assert.Empty(em.GetRelationsOf<Skill>(fireball));   // 反向索引已清理
            Assert.Empty(em.GetRelationsOf<Skill>(shield));
        }

        [Fact]
        public void Multi_TargetDestroyed_BecomesInvalid()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));

            em.AddRelationship<Skill>(player, fireball);
            em.DestroyEntity(fireball);   // 目标销毁（未级联）

            // 反向索引被清理；正向列表槽位 version 失效 → GetRelationships 过滤掉
            Assert.Empty(em.GetRelationships<Skill>(player));
        }

        [Fact]
        public void Multi_ComponentType_Flagged()
        {
            var skill = ComponentTypeManager.GetComponentType(typeof(Skill));
            Assert.True(skill.IsMultiRelation);
            Assert.False(skill.IsExclusiveTarget);

            var owns = ComponentTypeManager.GetComponentType(typeof(OwnsItem));
            Assert.True(owns.IsMultiRelation);
            Assert.True(owns.IsExclusiveTarget);

            Assert.False(ComponentTypeManager.GetComponentType(typeof(ChildOf)).IsMultiRelation);
        }

        [Fact]
        public void SingleValueApi_OnMultiRelation_NoHarm()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));

            em.AddRelationship<Skill>(player, fireball);

            // 单参 API（列语义）对多值类型静默返回默认值（多值关系不占列），不误伤双参 API
            Assert.Equal(default, em.GetRelationship<Skill>(player));
            Assert.False(em.HasRelationship<Skill>(player));
            em.RemoveRelationship<Skill>(player);   // no-op，不抛异常
            Assert.True(em.HasRelationship<Skill>(player, fireball));   // 双参 API 不受影响
        }
    }
}
