using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>声明级联关系：target 销毁时自动级联销毁指向它的 sources（Flecs OnDeleteTarget）。</summary>
    [OnTargetDeleted(Cascade = true)]
    public struct CascadeChildOf : IRelationComponent { public RelationSlot Target; }

    /// <summary>声明级联的多值关系。</summary>
    [MultiRelation]
    [OnTargetDeleted(Cascade = true)]
    public struct CascadeDependsOn : IRelationComponent { public RelationSlot Target; }

    /// <summary>未声明级联的关系（对照：默认不级联）。</summary>
    public struct NonCascadeRef : IRelationComponent { public RelationSlot Target; }

    public class DeclaredCascadeTests
    {
        private static World NewWorld() => new World("RelCascade" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void ComponentType_Flagged_Cascade()
        {
            Assert.True(ComponentTypeManager.GetComponentType(typeof(CascadeChildOf)).CascadeOnTargetDeleted);
            Assert.False(ComponentTypeManager.GetComponentType(typeof(NonCascadeRef)).CascadeOnTargetDeleted);
            Assert.False(ComponentTypeManager.GetComponentType(typeof(ChildOf)).CascadeOnTargetDeleted);
        }

        [Fact]
        public void DestroyEntity_CascadesDeclaredRelation()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parent = em.NewEntity(typeof(Position));
            var child = em.NewEntity(typeof(Position));
            em.AddRelationship<CascadeChildOf>(child, parent);

            em.DestroyEntity(parent);   // 普通销毁，但声明级联 → child 也被销毁

            Assert.False(em.HasRelationship<CascadeChildOf>(child));
            var childInfo = em.GetEntityInfoRef(child.Id);
            Assert.Null(childInfo.Archetype);   // child 已销毁
        }

        [Fact]
        public void DestroyEntity_NoCascade_WithoutAttribute()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parent = em.NewEntity(typeof(Position));
            var child = em.NewEntity(typeof(Position));
            em.AddRelationship<NonCascadeRef>(child, parent);

            em.DestroyEntity(parent);   // 无声明 → 不级联（S24 默认语义）

            var childInfo = em.GetEntityInfoRef(child.Id);
            Assert.NotNull(childInfo.Archetype);   // child 存活
            Assert.False(em.HasRelationship<NonCascadeRef>(child));   // 关系自动失效
        }

        [Fact]
        public void DestroyEntity_Cascade_Recursive()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var root = em.NewEntity(typeof(Position));
            var child = em.NewEntity(typeof(Position));
            var grandchild = em.NewEntity(typeof(Position));
            em.AddRelationship<CascadeChildOf>(child, root);
            em.AddRelationship<CascadeChildOf>(grandchild, child);

            em.DestroyEntity(root);   // 递归级联：root → child → grandchild

            Assert.Null(em.GetEntityInfoRef(child.Id).Archetype);
            Assert.Null(em.GetEntityInfoRef(grandchild.Id).Archetype);
        }

        [Fact]
        public void DestroyEntity_Cascade_MultiRelation()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var hub = em.NewEntity(typeof(Position));
            var a = em.NewEntity(typeof(Position));
            var b = em.NewEntity(typeof(Position));
            em.AddRelationship<CascadeDependsOn>(a, hub);
            em.AddRelationship<CascadeDependsOn>(b, hub);

            em.DestroyEntity(hub);

            Assert.Null(em.GetEntityInfoRef(a.Id).Archetype);
            Assert.Null(em.GetEntityInfoRef(b.Id).Archetype);
            Assert.Empty(em.GetRelationsOf<CascadeDependsOn>(hub));
        }

        [Fact]
        public void DestroyEntity_Cascade_Cyclic_Terminates()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var a = em.NewEntity(typeof(Position));
            var b = em.NewEntity(typeof(Position));
            em.AddRelationship<CascadeChildOf>(a, b);
            em.AddRelationship<CascadeChildOf>(b, a);   // 环

            em.DestroyEntity(a);   // 防环不死循环

            Assert.Null(em.GetEntityInfoRef(b.Id).Archetype);   // b 也级联销毁（a→b 关系）
        }

        [Fact]
        public void DestroyEntityCascade_Explicit_StillWorks()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var parent = em.NewEntity(typeof(Position));
            var child = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(child, parent);   // 无声明

            em.DestroyEntityCascade(parent);   // 显式级联仍可用

            Assert.Null(em.GetEntityInfoRef(child.Id).Archetype);
        }
    }
}
