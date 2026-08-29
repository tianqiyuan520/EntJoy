using System;
using System.Linq;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 关系遍历 API 测试：GetAncestors / GetDescendants / GetSiblings。
    /// 树形层级 root→child→grandchild + 防环 + 防御（已销毁/无关系）。
    /// </summary>
    public class RelationTraversalTests
    {
        private static World NewWorld() => new World("Trav" + Guid.NewGuid().ToString("N"));

        /// <summary>构建 3 层树：root → 2 children → 每 child 2 grandchildren（共 7 实体）。</summary>
        private static (World world, Entity root, Entity[] children, Entity[] grandchildren) BuildTree()
        {
            var world = NewWorld();
            var em = world.EntityManager;

            var root = em.NewEntity(typeof(Position));
            var children = new Entity[2];
            for (int i = 0; i < 2; i++)
            {
                children[i] = em.NewEntity(typeof(Position));
                em.AddRelationship<ChildOf>(children[i], root);
            }
            var grandchildren = new Entity[4];
            for (int i = 0; i < 4; i++)
            {
                grandchildren[i] = em.NewEntity(typeof(Position));
                em.AddRelationship<ChildOf>(grandchildren[i], children[i / 2]);  // 每 child 2 个
            }
            return (world, root, children, grandchildren);
        }

        // ======================== GetAncestors ========================

        [Fact]
        public void GetAncestors_Chain_UpToRoot()
        {
            var (world, root, children, grandchildren) = BuildTree();
            var em = world.EntityManager;

            var ancestors = em.GetAncestors<ChildOf>(grandchildren[0]);
            // 孙 → 直接子 → root（最近祖先在前）
            Assert.Equal(2, ancestors.Length);
            Assert.Equal(children[0].Id, ancestors[0].Id);
            Assert.Equal(root.Id, ancestors[1].Id);
        }

        [Fact]
        public void GetAncestors_DirectChild_OneLevel()
        {
            var (world, root, children, _) = BuildTree();
            var em = world.EntityManager;

            var ancestors = em.GetAncestors<ChildOf>(children[0]);
            Assert.Single(ancestors);
            Assert.Equal(root.Id, ancestors[0].Id);
        }

        [Fact]
        public void GetAncestors_NoRelation_Empty()
        {
            var (world, root, _, _) = BuildTree();
            var em = world.EntityManager;

            Assert.Empty(em.GetAncestors<ChildOf>(root));  // root 无父
        }

        [Fact]
        public void GetAncestors_Cycle_Terminates()
        {
            var world = NewWorld();
            var em = world.EntityManager;

            var a = em.NewEntity(typeof(Position));
            var b = em.NewEntity(typeof(Position));
            var c = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(a, b);
            em.AddRelationship<ChildOf>(b, c);
            em.AddRelationship<ChildOf>(c, a);  // 环

            var ancestors = em.GetAncestors<ChildOf>(a);
            // a→b→c→a（环，a 已访问终止）→ [b, c]
            Assert.Equal(2, ancestors.Length);
            Assert.Equal(b.Id, ancestors[0].Id);
            Assert.Equal(c.Id, ancestors[1].Id);
        }

        // ======================== GetDescendants ========================

        [Fact]
        public void GetDescendants_BFS_AllLevels()
        {
            var (world, root, children, grandchildren) = BuildTree();
            var em = world.EntityManager;

            var descendants = em.GetDescendants<ChildOf>(root);
            Assert.Equal(6, descendants.Length);  // 2 children + 4 grandchildren

            var ids = descendants.Select(e => e.Id).ToHashSet();
            Assert.Contains(children[0].Id, ids);
            Assert.Contains(children[1].Id, ids);
            foreach (var g in grandchildren)
                Assert.Contains(g.Id, ids);
            // root 自身不在结果中
            Assert.DoesNotContain(root.Id, ids);
        }

        [Fact]
        public void GetDescendants_Leaf_Empty()
        {
            var (world, _, _, grandchildren) = BuildTree();
            var em = world.EntityManager;

            Assert.Empty(em.GetDescendants<ChildOf>(grandchildren[0]));  // 叶子无后代
        }

        [Fact]
        public void GetDescendants_Cycle_Terminates()
        {
            var world = NewWorld();
            var em = world.EntityManager;

            var a = em.NewEntity(typeof(Position));
            var b = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(a, b);
            em.AddRelationship<ChildOf>(b, a);  // 2 环

            var descendants = em.GetDescendants<ChildOf>(a);
            // a 的"子"= b（a→b），b 的"子"= a（已访问）→ [b]
            Assert.Single(descendants);
            Assert.Equal(b.Id, descendants[0].Id);
        }

        // ======================== GetSiblings ========================

        [Fact]
        public void GetSiblings_SameParent_ExcludesSelf()
        {
            var (world, root, children, _) = BuildTree();
            var em = world.EntityManager;

            var siblings = em.GetSiblings<ChildOf>(children[0]);
            Assert.Single(siblings);
            Assert.Equal(children[1].Id, siblings[0].Id);
            Assert.DoesNotContain(children[0].Id, siblings.Select(e => e.Id));
        }

        [Fact]
        public void GetSiblings_Grandchildren_OfSameChild()
        {
            var (world, _, children, grandchildren) = BuildTree();
            var em = world.EntityManager;

            // grandchildren[0]/[1] 同父 children[0]
            var siblings = em.GetSiblings<ChildOf>(grandchildren[0]);
            Assert.Single(siblings);
            Assert.Equal(grandchildren[1].Id, siblings[0].Id);
        }

        [Fact]
        public void GetSiblings_NoRelation_Empty()
        {
            var (world, root, _, _) = BuildTree();
            var em = world.EntityManager;

            Assert.Empty(em.GetSiblings<ChildOf>(root));  // root 无父 → 无兄弟
        }

        // ======================== World 入口 + 防御 ========================

        [Fact]
        public void World_EntryPoints_Work()
        {
            var (world, root, children, grandchildren) = BuildTree();

            Assert.Equal(2, world.GetAncestors<ChildOf>(grandchildren[0]).Length);
            Assert.Equal(6, world.GetDescendants<ChildOf>(root).Length);
            Assert.Single(world.GetSiblings<ChildOf>(children[0]));
        }

        [Fact]
        public void Traversal_AfterDestroy_SkipsDead()
        {
            var (world, root, children, grandchildren) = BuildTree();
            var em = world.EntityManager;

            // 销毁 children[1]（其 2 个孙实体被级联销毁）
            em.DestroyEntityCascade(children[1]);

            // root 的后代 = children[0] + 其 2 个孙 = 3
            var descendants = em.GetDescendants<ChildOf>(root);
            Assert.Equal(3, descendants.Length);

            // children[0] 的兄弟 = 无（children[1] 已死）
            Assert.Empty(em.GetSiblings<ChildOf>(children[0]));
        }
    }
}
