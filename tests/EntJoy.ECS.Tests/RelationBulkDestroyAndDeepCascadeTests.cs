using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 关系与批量销毁的两条不变式（审计 B17）：
    ///
    /// **① 反向索引必须随"批量销毁"一起清理**：`DestroyAllInArchetype` 的快路径只在
    /// 「被销毁的 archetype 自身含关系列」时才退回逐实体路径；而一个**没有关系列**的实体
    /// 完全可能是别人关系的 **target**。快路径若只清两表 + Id 回池，反向索引就会为死 id 留下条目，
    /// 且 Id 回收后这些条目会以「幽灵 source」形式出现在 `GetRelationsOf` 里。
    ///
    /// **② 级联删除必须迭代**：`CollectCascade` / `CollectDeclaredCascade` 原为递归 DFS，
    /// 深链（长所有权链/场景图）会 <c>StackOverflowException</c> —— 该异常不可捕获、直接终止进程。
    /// </summary>
    public class RelationBulkDestroyAndDeepCascadeTests
    {
        private static World NewWorld(string tag) => new World("Rel" + tag + Guid.NewGuid().ToString("N"));

        [Fact]
        public void DestroyAllInArchetype_ClearsReverseIndexEntriesForTargets()
        {
            using var world = NewWorld("BulkIdx");
            var em = world.EntityManager;

            var target = em.NewEntity(typeof(Position));   // 无关系列
            var source = em.NewEntity(typeof(Position));
            em.AddRelationship<ChildOf>(source, target);    // source 迁到「Position + ChildOf」archetype

            Assert.Single(em.GetRelationsOf<ChildOf>(target));

            // 找到「只有 Position」的 archetype（target 所在），批量销毁它
            Archetype? posOnly = null;
            foreach (var a in em.GetAllArchetypes())
            {
                if (a.Types.Length == 1 && a.Types[0].Type == typeof(Position)) { posOnly = a; break; }
            }
            Assert.NotNull(posOnly);
            em.DestroyAllInArchetype(posOnly!);

            // target 已销毁
            Assert.Throws<InvalidOperationException>(() => em.GetComponent<Position>(target));

            // ① 反向索引不得留下死 id 条目
            Assert.Empty(em.GetRelationsOf<ChildOf>(target));

            // ② Id 被回收复用后，不得以"幽灵 source"形式重新出现
            var reused = em.NewEntity(typeof(Position));
            Assert.Equal(target.Id, reused.Id); // 复用同一 id 是本用例的前提
            Assert.Empty(em.GetRelationsOf<ChildOf>(reused));
        }

        [Fact]
        public void DestroyEntityCascade_DeepChain_MustNotOverflowStack()
        {
            using var world = NewWorld("DeepCascade");
            var em = world.EntityManager;

            const int Depth = 200_000;
            var first = em.NewEntity(typeof(Position));
            var prev = first;
            for (int i = 1; i < Depth; i++)
            {
                var next = em.NewEntity(typeof(Position));
                em.AddRelationship<CascadeChildOf>(next, prev);  // next 指向 prev；销毁 prev 应级联销毁 next
                prev = next;
            }

            // 递归版会在此处 StackOverflow（进程终止）；迭代版必须正常返回
            em.DestroyEntityCascade(first);

            long live = 0;
            foreach (var a in em.GetAllArchetypes()) live += a.EntityCount;
            Assert.Equal(0, live);
        }
    }
}
