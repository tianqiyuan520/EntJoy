using System;
using EntJoy.Collections;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 查询可见性/数组长度两处静默错值（审计 R9 / R10a）：
    ///
    /// **R9**：`SetSharedComponent` 在「单实体 chunk」上就地改值。就地路径不推进 `StructuralVersion`，
    /// 而 `EntityQuery` / 调度缓存**只在结构版本变化时**刷新匹配集合 ⇒ `WithShared(旧值)` 的查询会继续
    /// 把这个 chunk 当匹配项返回（拿到的是已经不匹配的实体）。
    ///
    /// **R10a**：`ToComponentDataArray&lt;T&gt;` 用 `CalculateEntityCount()`（全部匹配实体）开数组，
    /// 但拷贝时对「缺少 T 的 chunk」`continue` ⇒ 尾部留下一段未初始化数据，`Length` 也大于实际有效项。
    /// </summary>
    public class QueryVisibilityAndArrayTests
    {
        [Fact]
        public void InPlaceSharedChange_MustInvalidateWithSharedFilteredQuery()
        {
            using var world = new World("InPlace" + Guid.NewGuid().ToString("N"));
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var matPos = new ComponentType[] { typeof(Position), typeof(Material) };
            var e = em.NewEntity(matPos, (typeof(Material), (object)new Material(1)));

            var q1 = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position, Material>().WithShared(new Material(1)));
            var q2 = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position, Material>().WithShared(new Material(2)));

            Assert.Equal(1, q1.CalculateEntityCount());
            Assert.Equal(0, q2.CalculateEntityCount());

            em.SetSharedComponent(e, new Material(2));   // 单实体 chunk → 就地改值

            Assert.Equal(0, q1.CalculateEntityCount());  // 修复前：陈旧匹配集合仍返回 1
            Assert.Equal(1, q2.CalculateEntityCount());
        }

        [Fact]
        public void ToComponentDataArray_MustNotLeaveUninitializedTail()
        {
            using var world = new World("ToArray" + Guid.NewGuid().ToString("N"));
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // archetype A：只有 Position（2 个实体，没有 Velocity）
            for (int i = 0; i < 2; i++) em.NewEntity(typeof(Position));
            // archetype B：Position + Velocity（3 个实体）
            for (int i = 0; i < 3; i++)
            {
                var e = em.NewEntity(typeof(Position), typeof(Velocity));
                em.GetComponent<Velocity>(e).X = 7f;
            }

            var query = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>());
            using var arr = query.ToComponentDataArray<Velocity>();

            // 只有 archetype B 提供 Velocity ⇒ 数组必须正好 3 项（修复前是 5 项 + 2 项未初始化尾巴）
            Assert.Equal(3, arr.Length);
            for (int i = 0; i < arr.Length; i++)
                Assert.Equal(7f, arr[i].X);
        }
    }
}
