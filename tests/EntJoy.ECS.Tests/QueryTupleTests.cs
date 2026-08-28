using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// N 元组查询生成器：验证 world.Query&lt;T0, T1, T2&gt;() 生成的重载可用且正确。
    /// 触发 QueryTupleSourceGenerator 生成 QueryEnumerable/Enumerator/Result 三组件版本。
    /// </summary>
    public class QueryTupleTests
    {
        [Fact]
        public void Query_Triple_IteratesMatchingEntities()
        {
            using var world = new World("QueryTuple1");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 3 个匹配实体（P+V+H）
            for (int i = 0; i < 3; i++)
            {
                var e = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health));
                em.Set(e, new Position { X = i, Y = 0 });
                em.Set(e, new Velocity { X = 1, Y = 1 });
                em.Set(e, new Health { Current = 100 });
            }
            // 1 个不匹配实体（只有 P+V）
            em.NewEntity(typeof(Position), typeof(Velocity));

            int count = 0;
            long sumX = 0;
            foreach (var r in world.Query<Position, Velocity, Health>())
            {
                sumX += (long)r.Comp0.X;
                Assert.Equal(1, (int)r.Comp1.X);
                Assert.Equal(100, (int)r.Comp2.Current);
                count++;
            }

            Assert.Equal(3, count);
            Assert.Equal(0 + 1 + 2, sumX); // X = 0,1,2
        }

        [Fact]
        public void Query_Triple_EmptyWorld_NoIteration()
        {
            using var world = new World("QueryTuple2");
            World.DefaultWorld = world;

            int count = 0;
            foreach (var r in world.Query<Position, Velocity, Health>())
            {
                count++;
            }
            Assert.Equal(0, count);
        }
    }
}
