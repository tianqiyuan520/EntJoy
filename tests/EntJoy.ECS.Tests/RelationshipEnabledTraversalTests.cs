using System;
using System.Collections.Generic;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct ComboPos : IComponentData { public int X; }
    public struct ComboVel : IComponentData { public int X; }
    public struct ComboFlag : IEnableableComponent { public int V; }
    public struct ComboRel : IRelationComponent, IComponentData { public RelationSlot Target; }

    /// <summary>启用位与关系同时过滤：段扫描要跨 64 位字边界，且被停用位或目标不符处截断。</summary>
    public class RelationshipEnabledTraversalTests
    {
        [Fact]
        public void EnabledAndRelationship_FiltersBothConditionsExactly()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("RelEnabled");
            var em = world.EntityManager;
            var target = em.NewEntity(typeof(ComboPos));
            var other = em.NewEntity(typeof(ComboPos));

            const int matches = 150;
            var expected = new HashSet<int>();
            var disabled = new List<Entity>();
            for (int i = 0; i < matches; i++)
            {
                var e = em.NewEntity(typeof(ComboPos), typeof(ComboVel), typeof(ComboFlag), typeof(ComboRel));
                em.Set(e, new ComboPos { X = i });
                em.AddRelationship<ComboRel>(e, target);
                if (i % 3 == 0)
                {
                    // 停用位交错分布，使连续命中段被切断。
                    em.SetComponentEnabled<ComboFlag>(e, false);
                    disabled.Add(e);
                }
                else expected.Add(i);
            }
            for (int i = 0; i < 40; i++)
            {
                var e = em.NewEntity(typeof(ComboPos), typeof(ComboVel), typeof(ComboFlag), typeof(ComboRel));
                em.Set(e, new ComboPos { X = 1000 + i });
                em.AddRelationship<ComboRel>(e, other);
            }

            var seen = new List<int>();
            foreach (var result in world.Query<ComboPos, ComboVel>().WithEnabled<ComboFlag>().WithRelationship<ComboRel>(target))
                seen.Add(result.ReadOnlyComp0.X);

            Assert.Equal(expected.Count, seen.Count);
            foreach (int value in seen) Assert.Contains(value, expected);
            Assert.Equal(matches - disabled.Count, seen.Count);
        }

        [Fact]
        public void EnabledAndRelationship_MatchesPlainForeachWhenEveryEntityIsEnabled()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("RelEnabledAll");
            var em = world.EntityManager;
            var target = em.NewEntity(typeof(ComboPos));
            for (int i = 0; i < 200; i++)
            {
                var e = em.NewEntity(typeof(ComboPos), typeof(ComboVel), typeof(ComboFlag), typeof(ComboRel));
                em.Set(e, new ComboPos { X = i });
                em.AddRelationship<ComboRel>(e, target);
            }

            int filtered = 0;
            foreach (var result in world.Query<ComboPos, ComboVel>().WithEnabled<ComboFlag>().WithRelationship<ComboRel>(target))
                filtered++;
            int plain = 0;
            foreach (var result in world.Query<ComboPos, ComboVel>()) plain++;

            Assert.Equal(200, plain);
            Assert.Equal(plain, filtered);
        }
    }
}
