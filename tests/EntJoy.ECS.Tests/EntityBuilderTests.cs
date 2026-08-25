using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class EntityBuilderTests
    {
        [Fact]
        public void Spawn_WithOneComponent_ShouldCreateEntity()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;

            var entity = world.Spawn()
                .With(new Position { X = 1, Y = 2 })
                .Build();

            // 验证实体已创建（第一个实体 Id = 0 是正常的）
            Assert.Equal(1, world.EntityManager.EntityCount);
        }

        [Fact]
        public void Spawn_WithMultipleComponents_ShouldCreateEntity()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;

            var entity = world.Spawn()
                .With(new Position { X = 1, Y = 2 })
                .With(new Velocity { X = 0.1f, Y = 0.2f })
                .With(new Health { Current = 100 })
                .Build();

            // 验证实体已创建
            Assert.Equal(1, world.EntityManager.EntityCount);
        }

        [Fact]
        public void Spawn_ShouldSetComponentValues()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;

            var entity = world.Spawn()
                .With(new Position { X = 1, Y = 2 })
                .Build();

            // 验证组件值已设置
            var pos = world.EntityManager.GetComponent<Position>(entity);
            Assert.Equal(1, pos.X);
            Assert.Equal(2, pos.Y);
        }

        [Fact]
        public void Spawn_MultipleEntities_ShouldWork()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;

            for (int i = 0; i < 100; i++)
            {
                world.Spawn()
                    .With(new Position { X = i, Y = i })
                    .With(new Velocity { X = 0.1f, Y = 0.1f })
                    .Build();
            }

            Assert.Equal(100, world.EntityManager.EntityCount);
        }
    }
}
