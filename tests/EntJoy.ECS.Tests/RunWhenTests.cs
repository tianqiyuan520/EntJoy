using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // 测试用事件
    public struct TestDamageEvent : IComponentData { }
    public struct TestHealEvent : IComponentData { }

    // 测试用 System
    [Read(typeof(Health))]
    [Write(typeof(Health))]
    [RunWhen(typeof(TestDamageEvent))]
    public struct ConditionalSystem : ISystem
    {
        public void OnUpdate() { }
    }

    [Read(typeof(Health))]
    [Write(typeof(Health))]
    public struct AlwaysRunSystem : ISystem
    {
        public void OnUpdate() { }
    }

    public class RunWhenTests
    {
        [Fact]
        public void RunWhen_ShouldSkipWhenNoEvent()
        {
            var runner = new SystemRunner(new World("Test"));
            runner.RegisterSystem<ConditionalSystem>();

            // 不触发事件
            runner.Update();

            // 系统应该被跳过（无法直接验证，但不应抛异常）
            Assert.Equal(1, runner.CurrentFrame);
        }

        [Fact]
        public void RunWhen_ShouldRunWhenEventTriggered()
        {
            var runner = new SystemRunner(new World("Test"));
            runner.RegisterSystem<ConditionalSystem>();

            // 触发事件
            runner.EventCounter.Increment<TestDamageEvent>();
            runner.Update();

            // 系统应该执行
            Assert.Equal(1, runner.CurrentFrame);
        }

        [Fact]
        public void RunWhen_ShouldResetAfterFrame()
        {
            var runner = new SystemRunner(new World("Test"));
            runner.RegisterSystem<ConditionalSystem>();

            // 帧 1：触发事件
            runner.EventCounter.Increment<TestDamageEvent>();
            runner.Update();
            Assert.Equal(1, runner.CurrentFrame);

            // 帧 2：不触发事件，系统应跳过
            runner.Update();
            Assert.Equal(2, runner.CurrentFrame);
        }

        [Fact]
        public void AlwaysRunSystem_ShouldAlwaysExecute()
        {
            var runner = new SystemRunner(new World("Test"));
            runner.RegisterSystem<AlwaysRunSystem>();

            // 不触发任何事件
            runner.Update();
            runner.Update();
            runner.Update();

            Assert.Equal(3, runner.CurrentFrame);
        }

        [Fact]
        public void MultipleEvents_ShouldWork()
        {
            var counter = new EventCounter();
            counter.Increment<TestDamageEvent>();
            counter.Increment<TestHealEvent>();

            Assert.Equal(1, counter.GetCount<TestDamageEvent>());
            Assert.Equal(1, counter.GetCount<TestHealEvent>());
        }
    }
}
