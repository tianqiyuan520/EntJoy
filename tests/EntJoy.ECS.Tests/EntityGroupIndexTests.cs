using System;
using System.Linq;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// Group 反向索引：Entity → 匹配的 EntityQuery 集合。
    /// </summary>
    public class EntityGroupIndexTests
    {
        [Fact]
        public void GetGroupsOf_Entity_ReturnsMatchingQueries()
        {
            using var world = new World("GroupIndex1");
            var qPosition = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>());
            var qPV = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position, Velocity>());

            var e = world.EntityManager.NewEntity(typeof(Position), typeof(Velocity));
            var e2 = world.EntityManager.NewEntity(typeof(Position));

            var groupsE = world.GetGroupsOf(e);
            var groupsE2 = world.GetGroupsOf(e2);

            // e 有 Position+Velocity → 匹配两个查询
            Assert.Contains(qPosition, groupsE);
            Assert.Contains(qPV, groupsE);
            // e2 只有 Position → 只匹配 qPosition
            Assert.Contains(qPosition, groupsE2);
            Assert.DoesNotContain(qPV, groupsE2);
        }

        [Fact]
        public void GetGroupsOf_AfterComponentAdd_ReflectsNewMatch()
        {
            using var world = new World("GroupIndex2");
            var qPV = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position, Velocity>());

            var e = world.EntityManager.NewEntity(typeof(Position));
            Assert.Empty(world.GetGroupsOf(e)); // 尚无匹配

            world.EntityManager.AddComponent<Velocity>(e, new Velocity { X = 1, Y = 1 });
            Assert.Contains(qPV, world.GetGroupsOf(e)); // 添加组件后匹配
        }

        [Fact]
        public void GetGroupsOf_AfterComponentRemove_NoLongerMatches()
        {
            using var world = new World("GroupIndex3");
            var qPV = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position, Velocity>());

            var e = world.EntityManager.NewEntity(typeof(Position), typeof(Velocity));
            Assert.Contains(qPV, world.GetGroupsOf(e));

            world.EntityManager.RemoveComponent<Velocity>(e);
            Assert.DoesNotContain(qPV, world.GetGroupsOf(e));
        }

        [Fact]
        public void GetGroupsOf_DestroyedEntity_ReturnsEmpty()
        {
            using var world = new World("GroupIndex4");
            var qPosition = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>());

            var e = world.EntityManager.NewEntity(typeof(Position));
            Assert.Contains(qPosition, world.GetGroupsOf(e));

            world.EntityManager.DestroyEntity(e);
            Assert.Empty(world.GetGroupsOf(e));
        }

        [Fact]
        public void GetGroupsOf_NewQueryAfterEntities_ReflectsAll()
        {
            using var world = new World("GroupIndex5");
            var e1 = world.EntityManager.NewEntity(typeof(Position), typeof(Velocity));
            var e2 = world.EntityManager.NewEntity(typeof(Position), typeof(Velocity));

            // 查询在实体之后注册，反向索引应正确构建
            var qPV = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position, Velocity>());

            Assert.Contains(qPV, world.GetGroupsOf(e1));
            Assert.Contains(qPV, world.GetGroupsOf(e2));
        }

        [Fact]
        public void GetGroupsOf_InvalidEntity_ReturnsEmpty()
        {
            using var world = new World("GroupIndex6");
            var qPosition = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>());

            var invalid = new Entity { Id = 9999, Version = 1 };
            Assert.Empty(world.GetGroupsOf(invalid));
        }
    }
}
