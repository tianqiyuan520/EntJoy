using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // [ECSComponent] 标记的组件：不写 : IComponentData，由源生成器自动补齐接口。
    [ECSComponent]
    public partial struct AutoPos { public float X, Y; }

    [ECSComponent]
    public partial struct AutoVel { public float X, Y; }

    /// <summary>
    /// S26 组件存取生成：验证 [ECSComponent] partial struct（无 IComponentData 标记）可走通
    /// Set/GetComponent/Query/AddComponent/RemoveComponent/DCB.AddComponent 全链路。
    /// 若生成器未补齐接口，下面这些要求 IComponentData 约束的调用将无法通过编译。
    /// </summary>
    public class ECSComponentGeneratorTests
    {
        [Fact]
        public void Set_GetComponent_WorksWithoutIComponentDataMarker()
        {
            using var world = new World("S26Set");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var e = em.NewEntity(typeof(AutoPos));
            em.Set(e, new AutoPos { X = 3.5f, Y = -1f });

            ref var p = ref em.GetComponent<AutoPos>(e);
            Assert.Equal(3.5f, p.X);
            Assert.Equal(-1f, p.Y);
        }

        [Fact]
        public void Query_Iterates_WithoutIComponentDataMarker()
        {
            using var world = new World("S26Query");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            for (int i = 0; i < 3; i++)
            {
                var e = em.NewEntity(typeof(AutoPos), typeof(AutoVel));
                em.Set(e, new AutoPos { X = i, Y = 0 });
                em.Set(e, new AutoVel { X = 1, Y = 1 });
            }

            int count = 0;
            long sumX = 0;
            foreach (var r in world.Query<AutoPos, AutoVel>())
            {
                sumX += (long)r.Comp0.X;
                Assert.Equal(1, (int)r.Comp1.X);
                count++;
            }

            Assert.Equal(3, count);
            Assert.Equal(0 + 1 + 2, sumX);
        }

        [Fact]
        public void AddComponent_RemoveComponent_WorksWithoutIComponentDataMarker()
        {
            using var world = new World("S26Add");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var e = em.NewEntity(typeof(AutoPos)); // 含 AutoPos，无 AutoVel
            em.AddComponent(e, new AutoVel { X = 7, Y = 8 });

            int count = 0;
            foreach (var r in world.Query<AutoPos, AutoVel>())
            {
                Assert.Equal(7, (int)r.Comp1.X);
                count++;
            }
            Assert.Equal(1, count);

            em.RemoveComponent<AutoVel>(e);

            count = 0;
            foreach (var _ in world.Query<AutoPos, AutoVel>())
                count++;
            Assert.Equal(0, count);
        }

        [Fact]
        public void DeferredCommandBuffer_AddComponent_WorksWithoutIComponentDataMarker()
        {
            using var world = new World("S26Ecb");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var e = em.NewEntity(typeof(AutoVel));

            var ecb = new DeferredCommandBuffer();
            ecb.AddComponent(e, new AutoPos { X = 9, Y = 10 });
            ecb.Playback(em);
            ecb.Dispose();

            ref var p = ref em.GetComponent<AutoPos>(e);
            Assert.Equal(9, (int)p.X);
            Assert.Equal(10, (int)p.Y);
        }
    }
}
