using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 关系组件（IRelationComponent 标记，列值 = RelationSlot，8B）。
    /// 必须含 RelationSlot Target 字段：使 Unsafe.SizeOf&lt;TRel&gt; == 8B == 列宽，
    /// 保证 GetComponentDataSpan&lt;TRel&gt;()/IJobEntity 访问步长一致。
    /// </summary>
    public struct ChildOf : IRelationComponent { public RelationSlot Target; }
    public struct InState : IRelationComponent { public RelationSlot Target; }

    public class RelationTests
    {
        private static World NewWorld() => new World("Rel" + Guid.NewGuid().ToString("N"));

        // ======================== 验收 1：不拆 Archetype ========================

        [Fact]
        public void Relation_DoesNotSplitArchetype_ByTarget()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            // 创建带 ChildOf 列的 Archetype（父实体）
            var parentA = em.NewEntity(typeof(Position));
            var parentB = em.NewEntity(typeof(Position));

            var childA = em.NewEntity(typeof(Position));
            var childB = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(childA, parentA);
            em.AddRelationship<ChildOf>(childB, parentB);

            // 两个 target 不同的实体必须同属一个 Archetype（关系值不参与签名）
            Assert.Same(
                em.GetEntityInfoRef(childA.Id).Archetype,
                em.GetEntityInfoRef(childB.Id).Archetype);
            // 无关系实体的 Archetype 也与有关系的不同（列存在性决定签名，值不决定）
            var noRel = em.NewEntity(typeof(Position));
            Assert.NotSame(
                em.GetEntityInfoRef(childA.Id).Archetype,
                em.GetEntityInfoRef(noRel.Id).Archetype);
        }

        // ======================== 验收 4：零结构变更覆盖更新 ========================

        [Fact]
        public void AddRelationship_AlreadyHas_NoStructuralChange()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var child = em.NewEntity(typeof(Position));
            var parentA = em.NewEntity(typeof(Position));
            var parentB = em.NewEntity(typeof(Position));

            em.AddRelationship<ChildOf>(child, parentA);
            var archAfterFirst = em.GetEntityInfoRef(child.Id).Archetype;
            int versionAfterFirst = em.StructuralVersion;

            em.AddRelationship<ChildOf>(child, parentB);  // 覆盖更新
            Assert.Equal(versionAfterFirst, em.StructuralVersion);  // 无结构变更
            Assert.Same(archAfterFirst, em.GetEntityInfoRef(child.Id).Archetype);  // 未迁移

            // 值已更新
            var rel = em.GetRelationship<ChildOf>(child);
            Assert.Equal(parentB.Id, rel.Id);
            Assert.Equal(parentB.Version, rel.Version);
        }

        // ======================== 验收 3：Version 防 ID 回收 ========================

        [Fact]
        public void Relationship_TargetDestroyed_BecomesInvalid()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var child = em.NewEntity(typeof(Position));
            var parent = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(child, parent);

            Assert.True(em.HasRelationship<ChildOf>(child));
            Assert.Equal(parent.Id, em.GetRelationship<ChildOf>(child).Id);

            // 销毁 target（Id 进回收队列，Version 将在复用后 +1）
            em.DestroyEntity(parent);

            Assert.False(em.HasRelationship<ChildOf>(child));   // 目标已销毁
            Assert.Equal(default, em.GetRelationship<ChildOf>(child));
        }

        [Fact]
        public void Relationship_TargetIdRecycled_VersionMismatch_Invalid()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var child = em.NewEntity(typeof(Position));
            var parent = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(child, parent);

            // 销毁 parent 后立即复用其 Id（重建实体，version+1）
            em.DestroyEntity(parent);
            var recycled = em.NewEntity(typeof(Position));
            Assert.Equal(parent.Id, recycled.Id);      // Id 复用
            Assert.NotEqual(parent.Version, recycled.Version);  // 版本递增

            // 关系列仍存旧 version → 不匹配新实体
            Assert.False(em.HasRelationship<ChildOf>(child));
            Assert.Equal(default, em.GetRelationship<ChildOf>(child));
        }

        // ======================== 验收 5：查询过滤正确性 ========================

        [Fact]
        public void WithRelationship_Query_ReturnsOnlyMatchingTarget()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parentA = em.NewEntity(typeof(Position));
            var parentB = em.NewEntity(typeof(Position));

            // 10 个 A 子实体 + 5 个 B 子实体
            var aChildren = new Entity[10];
            for (int i = 0; i < 10; i++)
            {
                aChildren[i] = em.NewEntity(typeof(Position));
                em.AddRelationship<ChildOf>(aChildren[i], parentA);
            }
            for (int i = 0; i < 5; i++)
            {
                var c = em.NewEntity(typeof(Position));
                em.AddRelationship<ChildOf>(c, parentB);
            }

            // WithRelationship<ChildOf>(parentA) 只返回 A 的 10 个子实体
            int count = 0;
            foreach (var r in world.Query<Position>().WithRelationship<ChildOf>(parentA))
            {
                count++;
            }
            Assert.Equal(10, count);
        }

        [Fact]
        public void WithRelationship_Query_AfterTargetDestroy_Empty()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parent = em.NewEntity(typeof(Position));
            var child = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(child, parent);

            int singleCount = 0;
            foreach (var r in world.Query<Position>().WithRelationship<ChildOf>(parent)) singleCount++;
            Assert.Equal(1, singleCount);

            em.DestroyEntity(parent);
            // target 已销毁（version 失效）→ 过滤不命中
            int emptyCount = 0;
            foreach (var r in world.Query<Position>().WithRelationship<ChildOf>(parent)) emptyCount++;
            Assert.Equal(0, emptyCount);
        }

        // ======================== 基础 API ========================

        [Fact]
        public void GetRelationship_NoComponent_ReturnsDefault()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var e = em.NewEntity(typeof(Position));
            Assert.Equal(default, em.GetRelationship<ChildOf>(e));
            Assert.False(em.HasRelationship<ChildOf>(e));
        }

        [Fact]
        public void RemoveRelationship_NoComponent_NoOp()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var e = em.NewEntity(typeof(Position));
            em.RemoveRelationship<ChildOf>(e);  // 不应抛异常
            Assert.False(em.HasRelationship<ChildOf>(e));
        }

        [Fact]
        public void RemoveRelationship_RemovesColumn()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var child = em.NewEntity(typeof(Position));
            var parent = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(child, parent);
            Assert.True(em.HasRelationship<ChildOf>(child));

            em.RemoveRelationship<ChildOf>(child);
            Assert.False(em.HasRelationship<ChildOf>(child));
            Assert.Equal(default, em.GetRelationship<ChildOf>(child));
            // 列已移除：Archetype 回退到无 ChildOf
            Assert.False(em.GetEntityInfoRef(child.Id).Archetype.Has(typeof(ChildOf)));
        }

        [Fact]
        public void MultipleRelationTypes_IndependentColumns()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var e = em.NewEntity(typeof(Position));
            var parent = em.NewEntity(typeof(Position));
            var state = em.NewEntity(typeof(Position));

            em.AddRelationship<ChildOf>(e, parent);
            em.AddRelationship<InState>(e, state);

            Assert.Equal(parent.Id, em.GetRelationship<ChildOf>(e).Id);
            Assert.Equal(state.Id, em.GetRelationship<InState>(e).Id);
        }

        [Fact]
        public void World_EntryPoints_Work()
        {
            using var world = NewWorld();
            var child = world.EntityManager.NewEntity(typeof(Position));
            var parent = world.EntityManager.NewEntity(typeof(Position));

            world.AddRelationship<ChildOf>(child, parent);
            Assert.True(world.HasRelationship<ChildOf>(child));
            Assert.Equal(parent.Id, world.GetRelationship<ChildOf>(child).Id);

            world.RemoveRelationship<ChildOf>(child);
            Assert.False(world.HasRelationship<ChildOf>(child));
        }
    }
}
