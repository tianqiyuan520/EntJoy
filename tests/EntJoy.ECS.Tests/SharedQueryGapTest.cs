using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// Shared Component 查询测试：
    /// - EntityQuery 面过滤（原缺口判据，修复后 PASS）
    /// - QuerySelection.WithShared 枚举面过滤（单组件 + 双组件链式）
    /// - Change Tracking 联动（SetSharedComponent 标记变更）
    /// </summary>
    public class SharedQueryTests
    {
        private static World NewWorld() => new World("SharedQ" + Guid.NewGuid().ToString("N"));

        private static Entity NewWithMaterial(EntityManager em, int matId)
            => em.NewEntity(new ComponentType[] { typeof(Position), typeof(Material) },
                (typeof(Material), (object)new Material(matId)));

        // ======================== EntityQuery 面 ========================

        [Fact]
        public void EntityQuery_WithShared_Filters()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            for (int i = 0; i < 5; i++) NewWithMaterial(em, 2);
            for (int i = 0; i < 3; i++) NewWithMaterial(em, 3);

            var query = world.GetOrCreateEntityQuery(
                new QueryBuilder().WithAll<Position, Material>().WithShared(new Material(2)));
            Assert.Equal(5, query.CalculateEntityCount());

            var query3 = world.GetOrCreateEntityQuery(
                new QueryBuilder().WithAll<Position, Material>().WithShared(new Material(3)));
            Assert.Equal(3, query3.CalculateEntityCount());
        }

        [Fact]
        public void EntityQuery_WithShared_AfterSetShared_Updates()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var e = NewWithMaterial(em, 2);
            NewWithMaterial(em, 2);

            var query2 = world.GetOrCreateEntityQuery(
                new QueryBuilder().WithAll<Position, Material>().WithShared(new Material(2)));
            Assert.Equal(2, query2.CalculateEntityCount());

            // 移动 e 到 Material=3（多实体 chunk → 换 chunk）
            em.SetSharedComponent(e, new Material(3));
            Assert.Equal(1, query2.CalculateEntityCount());
        }

        // ======================== 枚举面（QuerySelection.WithShared） ========================

        [Fact]
        public void QuerySelection_WithShared_Filters()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            for (int i = 0; i < 5; i++) NewWithMaterial(em, 2);
            for (int i = 0; i < 3; i++) NewWithMaterial(em, 3);

            int count = 0;
            foreach (var r in world.Query<Position>().WithShared(new Material(2)))
                count++;
            Assert.Equal(5, count);
        }

        [Fact]
        public void Query_TwoComponents_WithShared_Filters()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            for (int i = 0; i < 4; i++)
            {
                var e = em.NewEntity(new ComponentType[] { typeof(Position), typeof(Velocity), typeof(Material) },
                    (typeof(Material), (object)new Material(7)));
            }
            for (int i = 0; i < 2; i++)
            {
                em.NewEntity(new ComponentType[] { typeof(Position), typeof(Velocity), typeof(Material) },
                    (typeof(Material), (object)new Material(8)));
            }

            int count = 0;
            foreach (var r in world.Query<Position, Velocity>().WithShared(new Material(7)))
                count++;
            Assert.Equal(4, count);
        }

        [Fact]
        public void QuerySelection_WithShared_ChainWithEnabled()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            for (int i = 0; i < 3; i++)
            {
                var e = em.NewEntity(new ComponentType[] { typeof(Position), typeof(Material), typeof(RelActiveComponent) },
                    (typeof(Material), (object)new Material(5)));
            }

            // 组合过滤（WithShared + WithEnabled）：EntityQuery 面（QuerySelection<T0> 单组件链式以 QueryEnumerable 终结）
            var query = world.GetOrCreateEntityQuery(
                new QueryBuilder().WithAll<Position>().WithShared(new Material(5)).WithEnabled<RelActiveComponent>());
            Assert.Equal(3, query.CalculateEntityCount());
        }

        [Fact]
        public void WithShared_IgnoresArchetypesWithoutSharedColumn()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            // 无 Material 列的 archetype（Position+Velocity）与含 Material 的 archetype 混存：
            // 三个收集路径（枚举/EntityQuery/Job）都必须跳过前者，而不是 GetComponentTypeIndex 字典 miss。
            for (int i = 0; i < 3; i++)
                em.NewEntity(new ComponentType[] { typeof(Position), typeof(Velocity) });
            for (int i = 0; i < 4; i++)
                NewWithMaterial(em, 2);

            int count = 0;
            foreach (var _ in world.Query<Position>().WithShared(new Material(2)))
                count++;
            Assert.Equal(4, count);

            var query = world.GetOrCreateEntityQuery(
                new QueryBuilder().WithAll<Position>().WithShared(new Material(2)));
            Assert.Equal(4, query.CalculateEntityCount());
        }

        // ======================== Change Tracking 联动 ========================

        [Fact]
        public void SetSharedComponent_MarksEntityChanged()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var e = NewWithMaterial(em, 1);
            var query = world.GetOrCreateEntityQuery(
                new QueryBuilder().WithAll<Position, Material>().WithChanged<Position>());

            // 创建即变更（既有语义）→ 先清帧末标记
            em.ClearAllChangedBitMasks();
            Assert.Equal(0, query.CalculateEntityCount());

            // SetSharedComponent（单实体 chunk 就地改值）→ 标记变更
            em.SetSharedComponent(e, new Material(2));
            Assert.Equal(1, query.CalculateEntityCount());

            // ClearAllChangedBitMasks（帧末）→ 归零
            em.ClearAllChangedBitMasks();
            Assert.Equal(0, query.CalculateEntityCount());
        }

        [Fact]
        public void SetSharedComponent_MultiEntityMove_MarksChanged()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var e1 = NewWithMaterial(em, 1);
            NewWithMaterial(em, 1);  // 同 chunk 第二实体

            var query = world.GetOrCreateEntityQuery(
                new QueryBuilder().WithAll<Position, Material>().WithChanged<Position>());

            em.ClearAllChangedBitMasks();
            Assert.Equal(0, query.CalculateEntityCount());

            // 多实体 chunk → 换 chunk 移动 → 新位置标记变更
            em.SetSharedComponent(e1, new Material(9));
            Assert.Equal(1, query.CalculateEntityCount());
        }
    }
}
