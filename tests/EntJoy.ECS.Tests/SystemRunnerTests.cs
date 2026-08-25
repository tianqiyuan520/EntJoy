using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // 测试用 System
    [Read(typeof(Position))]
    [Write(typeof(Position))]
    [Order(0)]
    public struct PositionSystem : ISystem
    {
        public void OnUpdate() { }
    }

    [Read(typeof(Health))]
    [Write(typeof(Health))]
    [Order(1)]
    public struct HealthSystem : ISystem
    {
        public void OnUpdate() { }
    }

    public class SystemRunnerTests
    {
        [Fact]
        public void RegisterSystem_ShouldAddToGraph()
        {
            var world = new World("Test");
            var runner = new SystemRunner(world);

            runner.RegisterSystem<PositionSystem>();
            runner.RegisterSystem<HealthSystem>();

            // 应该在同一层（无冲突）
            runner.PrintSchedule();
        }

        [Fact]
        public void Update_ShouldIncrementFrame()
        {
            var world = new World("Test");
            var runner = new SystemRunner(world);
            runner.RegisterSystem<PositionSystem>();

            Assert.Equal(0, runner.CurrentFrame);
            runner.Update();
            Assert.Equal(1, runner.CurrentFrame);
            runner.Update();
            Assert.Equal(2, runner.CurrentFrame);
        }

        [Fact]
        public void Update_ShouldSetWorldCurrentFrame()
        {
            var world = new World("Test");
            World.DefaultWorld = world;
            var runner = new SystemRunner(world);

            runner.Update();
            Assert.Equal(1, world.CurrentFrame);

            runner.Update();
            Assert.Equal(2, world.CurrentFrame);
        }

        [Fact]
        public void Update_ShouldResetEventCounter()
        {
            var world = new World("Test");
            var runner = new SystemRunner(world);
            runner.RegisterSystem<ConditionalSystem>();

            // 触发事件
            runner.EventCounter.Increment<TestDamageEvent>();
            runner.Update();

            // 事件计数应重置
            Assert.Equal(0, runner.EventCounter.GetCount<TestDamageEvent>());
        }

        [Fact]
        public void EventCounter_ShouldWork()
        {
            var world = new World("Test");
            var runner = new SystemRunner(world);

            runner.EventCounter.Increment<TestDamageEvent>();
            Assert.Equal(1, runner.EventCounter.GetCount<TestDamageEvent>());

            runner.EventCounter.Decrement<TestDamageEvent>();
            Assert.Equal(0, runner.EventCounter.GetCount<TestDamageEvent>());
        }
    }
}
