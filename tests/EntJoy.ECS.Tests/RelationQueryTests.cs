using System;
using System.Linq;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>本地 enableable 组件（链式过滤测试用）。</summary>
    public struct RelActiveComponent : IComponentData, IEnableableComponent { }

    /// <summary>
    /// 关系查询进阶测试：
    /// - GetRelationsOf&lt;TRel&gt;(target)：反向索引 O(1) 查询（target → sources）
    /// - GetRelationsOfAll(target)：跨类型反向查询
    /// - Query&lt;T0,T1&gt;().WithRelationship&lt;TRel&gt;(target)：双组件链式关系过滤
    /// </summary>
    public class RelationQueryTests
    {
        private static World NewWorld() => new World("RelQuery" + Guid.NewGuid().ToString("N"));

        // ======================== GetRelationsOf ========================

        [Fact]
        public void GetRelationsOf_ReturnsSourcesByType()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parent = em.NewEntity(typeof(Position));
            var childA = em.NewEntity(typeof(Position));
            var childB = em.NewEntity(typeof(Position));
            var state = em.NewEntity(typeof(Position));

            em.AddRelationship<ChildOf>(childA, parent);
            em.AddRelationship<ChildOf>(childB, parent);
            em.AddRelationship<InState>(childA, state);  // 不同类型关系

            // ChildOf 关系源 = {childA, childB}
            var childOfSources = em.GetRelationsOf<ChildOf>(parent);
            Assert.Equal(2, childOfSources.Length);
            Assert.Contains(childA, childOfSources);
            Assert.Contains(childB, childOfSources);

            // InState 关系源（state 的）不含 childB
            var stateSources = em.GetRelationsOf<InState>(state);
            Assert.Single(stateSources);
            Assert.Equal(childA, stateSources[0]);

            // 无关系的 target → 空
            var orphan = em.NewEntity(typeof(Position));
            Assert.Empty(em.GetRelationsOf<ChildOf>(orphan));
        }

        [Fact]
        public void GetRelationsOfAll_ReturnsAllTypes()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var hub = em.NewEntity(typeof(Position));
            var a = em.NewEntity(typeof(Position));
            var b = em.NewEntity(typeof(Position));

            em.AddRelationship<ChildOf>(a, hub);
            em.AddRelationship<InState>(b, hub);

            var all = em.GetRelationsOfAll(hub);
            Assert.Equal(2, all.Length);
            Assert.Contains(a, all);
            Assert.Contains(b, all);
        }

        [Fact]
        public void GetRelationsOf_AfterRemove_Empty()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parent = em.NewEntity(typeof(Position));
            var child = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(child, parent);

            em.RemoveRelationship<ChildOf>(child);
            Assert.Empty(em.GetRelationsOf<ChildOf>(parent));
        }

        [Fact]
        public void GetRelationsOf_AfterCascadeDestroy_Empty()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parent = em.NewEntity(typeof(Position));
            var child = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(child, parent);

            em.DestroyEntityCascade(parent);
            Assert.Empty(em.GetRelationsOf<ChildOf>(parent));  // 索引已清理
        }

        [Fact]
        public void GetRelationsOf_WorldEntry_Works()
        {
            using var world = NewWorld();
            var parent = world.EntityManager.NewEntity(typeof(Position));
            var child = world.EntityManager.NewEntity(typeof(Position));
            world.AddRelationship<ChildOf>(child, parent);

            var sources = world.GetRelationsOf<ChildOf>(parent);
            Assert.Single(sources);
            Assert.Equal(child, sources[0]);
        }

        // ======================== 双组件链式 WithRelationship ========================

        [Fact]
        public void Query_TwoComponents_WithRelationship_Filters()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parentA = em.NewEntity(typeof(Position));
            var parentB = em.NewEntity(typeof(Position));

            // A 的孩子带 Position+Velocity，B 的孩子带 Position+Velocity
            for (int i = 0; i < 5; i++)
            {
                var c = em.NewEntity(typeof(Position), typeof(Velocity));
                em.AddRelationship<ChildOf>(c, parentA);
                em.Set(c, new Position { X = i, Y = 0 });
            }
            for (int i = 0; i < 3; i++)
            {
                var c = em.NewEntity(typeof(Position), typeof(Velocity));
                em.AddRelationship<ChildOf>(c, parentB);
            }

            // 双组件 + 关系过滤：只返回 A 的 5 个孩子（含 Position+Velocity）
            int count = 0;
            float sumX = 0;
            foreach (var r in world.Query<Position, Velocity>().WithRelationship<ChildOf>(parentA))
            {
                count++;
                sumX += r.Comp0.X;
            }
            Assert.Equal(5, count);
            Assert.Equal(0 + 1 + 2 + 3 + 4, (int)sumX);  // X = 0..4 精确
        }

        [Fact]
        public void Query_TwoComponents_WithRelationship_ChainWithEnabled()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parent = em.NewEntity(typeof(Position));
            for (int i = 0; i < 4; i++)
            {
                var c = em.NewEntity(typeof(Position), typeof(Velocity), typeof(RelActiveComponent));
                em.AddRelationship<ChildOf>(c, parent);
            }

            // 双组件 + 关系过滤 + enabled 过滤（都启用）→ 4 个
            int count = 0;
            foreach (var r in world.Query<Position, Velocity>()
                .WithRelationship<ChildOf>(parent)
                .WithEnabled<RelActiveComponent>())
            {
                count++;
            }
            Assert.Equal(4, count);
        }
    }
}
