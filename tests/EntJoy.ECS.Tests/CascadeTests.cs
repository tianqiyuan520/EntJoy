using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 级联删除测试：DestroyEntityCascade 销毁整棵关系子树（递归 + 防环）。
    /// </summary>
    public class CascadeTests
    {
        private static World NewWorld() => new World("Cascade" + Guid.NewGuid().ToString("N"));

        private static bool Alive(World world, Entity e)
            => world.EntityManager.GetEntityInfoRef(e.Id).Archetype != null;

        // ======================== 基础：单层级联 ========================

        [Fact]
        public void DestroyCascade_DestroysDirectChildren()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parent = em.NewEntity(typeof(Position));
            var child1 = em.NewEntity(typeof(Position));
            var child2 = em.NewEntity(typeof(Position));
            var unrelated = em.NewEntity(typeof(Position));  // 无关实体

            em.AddRelationship<ChildOf>(child1, parent);
            em.AddRelationship<ChildOf>(child2, parent);

            em.DestroyEntityCascade(parent);

            Assert.False(Alive(world, parent));
            Assert.False(Alive(world, child1));
            Assert.False(Alive(world, child2));
            Assert.True(Alive(world, unrelated));  // 无关实体保留
        }

        // ======================== 递归：孙实体也销毁 ========================

        [Fact]
        public void DestroyCascade_Recursive_GrandChildrenDestroyed()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var root = em.NewEntity(typeof(Position));
            var child = em.NewEntity(typeof(Position));
            var grandchild = em.NewEntity(typeof(Position));
            var leaf = em.NewEntity(typeof(Position));

            em.AddRelationship<ChildOf>(child, root);
            em.AddRelationship<ChildOf>(grandchild, child);
            em.AddRelationship<ChildOf>(leaf, grandchild);

            em.DestroyEntityCascade(root);

            Assert.False(Alive(world, root));
            Assert.False(Alive(world, child));
            Assert.False(Alive(world, grandchild));
            Assert.False(Alive(world, leaf));
        }

        // ======================== 防环：环状关系不无限递归 ========================

        [Fact]
        public void DestroyCascade_CyclicRelation_Terminates()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var a = em.NewEntity(typeof(Position));
            var b = em.NewEntity(typeof(Position));
            var c = em.NewEntity(typeof(Position));

            em.AddRelationship<ChildOf>(a, b);
            em.AddRelationship<ChildOf>(b, c);
            em.AddRelationship<ChildOf>(c, a);  // 环：a→b→c→a

            em.DestroyEntityCascade(a);  // 不应死循环

            Assert.False(Alive(world, a));
            Assert.False(Alive(world, b));
            Assert.False(Alive(world, c));
        }

        // ======================== 索引一致性 ========================

        [Fact]
        public void DestroyCascade_CleansIndexEntries()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parent = em.NewEntity(typeof(Position));
            var child = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(child, parent);

            em.DestroyEntityCascade(parent);

            // 索引应无残留（销毁 child 后其作为 source 的条目也清理）
            // 通过重建同 Id 实体验证不误伤新实体
            var recycled = em.NewEntity(typeof(Position));
            Assert.True(Alive(world, recycled));
            // 新实体无关系
            Assert.False(em.HasRelationship<ChildOf>(recycled));
        }

        // ======================== 非级联销毁保持旧语义 ========================

        [Fact]
        public void DestroyEntity_DoesNotCascade_ByDefault()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parent = em.NewEntity(typeof(Position));
            var child = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(child, parent);

            em.DestroyEntity(parent);  // 普通销毁不级联

            Assert.False(Alive(world, parent));
            Assert.True(Alive(world, child));  // 子实体保留
            // 子实体的关系失效（target 已销毁）
            Assert.False(em.HasRelationship<ChildOf>(child));
            Assert.Equal(default, em.GetRelationship<ChildOf>(child));
        }

        // ======================== 已销毁实体级联 ========================

        [Fact]
        public void DestroyCascade_AlreadyDestroyed_NoOp()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var e = em.NewEntity(typeof(Position));
            em.DestroyEntity(e);
            em.DestroyEntityCascade(e);  // 不应抛异常
        }

        // ======================== World 入口 ========================

        [Fact]
        public void DestroyCascade_WorldEntry_Works()
        {
            using var world = NewWorld();
            var parent = world.EntityManager.NewEntity(typeof(Position));
            var child = world.EntityManager.NewEntity(typeof(Position));
            world.AddRelationship<ChildOf>(child, parent);

            world.DestroyEntityCascade(parent);

            Assert.False(Alive(world, parent));
            Assert.False(Alive(world, child));
        }
    }
}
