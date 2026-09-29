using System;
using EntJoy.Collections;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 两条"静默错值"回归（审计 R10b / B19）。
    ///
    /// **R10b**：`EntityQuery.RefreshIncremental` 只用 **Archetype 数量**判断能否复用缓存的匹配集合。
    /// `World.Restore()` 会清空并整体重建 Archetype，数量往往与之前相同 ⇒ 查询继续引用**已释放**的
    /// Archetype，静默返回 0 个实体（而不是抛错）。修复：改比"Archetype 集合身份版本"。
    ///
    /// **B19**：`EntityManager.EntityCount` 实际是**已发放的 id 计数**（`newEntity.Id = entityCount++`，
    /// Destroy 不递减），而 `MemoryReport.TotalEntityCount` 直接填了它 ⇒ 销毁实体后内存报告仍显示历史峰值。
    /// 修复：报告改用存活数（按各 Archetype 计数求和）；`EntityCount` 保留原语义并补文档。
    /// </summary>
    public class QueryRestoreAndMemoryReportTests
    {
        [Fact]
        public void Query_AfterRestore_MustNotReuseStaleMatchingArchetypes()
        {
            TestBackend.EnsureInitialized();

            using var world = new World("RestoreStale" + Guid.NewGuid().ToString("N"));
            World.DefaultWorld = world;
            var em = world.EntityManager;

            for (int i = 0; i < 3; i++) em.NewEntity(typeof(Position), typeof(Velocity));

            var q = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position, Velocity>());
            Assert.Equal(3, q.CalculateEntityCount());

            // 快照 → Restore：内部 archetypeCount 先归零再重建，**数量恰好相同**
            var snap = world.TakeSnapshot();
            world.Restore(snap);

            // 修复前：数量相同 ⇒ 复用已释放的 Archetype ⇒ 静默变成 0
            Assert.Equal(3, q.CalculateEntityCount());
        }

        [Fact]
        public void MemoryReport_TotalEntityCount_MustBeLiveCount_NotIdHighWaterMark()
        {
            TestBackend.EnsureInitialized();

            using var world = new World("LiveCount" + Guid.NewGuid().ToString("N"));
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var ids = new Entity[10];
            for (int i = 0; i < ids.Length; i++) ids[i] = em.NewEntity(typeof(Position));
            for (int i = 0; i < 7; i++) em.DestroyEntity(ids[i]);

            var report = em.GetMemoryReport();
            Assert.Equal(3, report.TotalEntityCount);

            // EntityCount 保留"已发放 id 计数"语义（= 10），也必须被文档说清；
            // 这里断言两者**刻意不同**，防止日后有人再把报告改回去。
            Assert.Equal(10, em.EntityCount);
        }
    }
}
