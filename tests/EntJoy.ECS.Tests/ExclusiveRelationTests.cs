using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>独占关系（真 1:1）：target 侧唯一约束，新 Add 自动解绑旧 source。</summary>
    [ExclusiveRelation]
    public struct OccupiedBy : IRelationComponent { public RelationSlot Target; }

    public class ExclusiveRelationTests
    {
        private static World NewWorld() => new World("RelEx" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Exclusive_SecondAdd_UnbindsOldSource()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var playerA = em.NewEntity(typeof(Position));
            var playerB = em.NewEntity(typeof(Position));
            var cell = em.NewEntity(typeof(Position));

            em.AddRelationship<OccupiedBy>(playerA, cell);
            Assert.True(em.HasRelationship<OccupiedBy>(playerA));
            Assert.Equal(cell.Id, em.GetRelationship<OccupiedBy>(playerA).Id);

            // B 也占用同一格 → A 被解绑
            em.AddRelationship<OccupiedBy>(playerB, cell);

            Assert.False(em.HasRelationship<OccupiedBy>(playerA), "A should be unbound");
            Assert.True(em.HasRelationship<OccupiedBy>(playerB));
            Assert.Equal(cell.Id, em.GetRelationship<OccupiedBy>(playerB).Id);

            // 反向索引一致：cell 只有 1 个 source
            var sources = em.GetRelationsOf<OccupiedBy>(cell);
            Assert.Single(sources);
            Assert.Equal(playerB.Id, sources[0].Id);
        }

        [Fact]
        public void Exclusive_SameSourceReAdd_NoUnbind()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var cell = em.NewEntity(typeof(Position));

            em.AddRelationship<OccupiedBy>(player, cell);
            em.AddRelationship<OccupiedBy>(player, cell);  // 自身重复 Add → 覆盖路径，不解绑

            Assert.True(em.HasRelationship<OccupiedBy>(player));
            Assert.Equal(cell.Id, em.GetRelationship<OccupiedBy>(player).Id);
            Assert.Single(em.GetRelationsOf<OccupiedBy>(cell));
        }

        [Fact]
        public void Exclusive_UnboundSource_ColumnRemoved()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var a = em.NewEntity(typeof(Position));
            var b = em.NewEntity(typeof(Position));
            var cell = em.NewEntity(typeof(Position));

            em.AddRelationship<OccupiedBy>(a, cell);
            em.AddRelationship<OccupiedBy>(b, cell);   // 解绑 a

            // a 的关系列应已移除（不再是同一 Archetype 也不含 OccupiedBy）
            var info = em.GetEntityInfoRef(a.Id);
            Assert.False(info.Archetype.Has(typeof(OccupiedBy)));
        }

        [Fact]
        public void NonExclusiveRelation_AllowsManySources()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var a = em.NewEntity(typeof(Position));
            var b = em.NewEntity(typeof(Position));
            var parent = em.NewEntity(typeof(Position));

            em.AddRelationship<ChildOf>(a, parent);
            em.AddRelationship<ChildOf>(b, parent);   // 非独占：不禁用

            Assert.Equal(2, em.GetRelationsOf<ChildOf>(parent).Length);
        }

        [Fact]
        public void Exclusive_ComponentType_Flagged()
        {
            var ct = ComponentTypeManager.GetComponentType(typeof(OccupiedBy));
            Assert.True(ct.IsExclusiveRelation);
            Assert.False(ComponentTypeManager.GetComponentType(typeof(ChildOf)).IsExclusiveRelation);
        }
    }
}
