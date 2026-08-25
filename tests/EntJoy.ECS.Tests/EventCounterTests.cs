using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // 测试用事件
    public struct TestEvent : IComponentData { }

    public class EventCounterTests
    {
        [Fact]
        public void Increment_ShouldIncreaseCount()
        {
            var counter = new EventCounter();
            counter.Increment<TestEvent>();

            Assert.Equal(1, counter.GetCount<TestEvent>());
        }

        [Fact]
        public void IncrementTwice_ShouldIncreaseTwice()
        {
            var counter = new EventCounter();
            counter.Increment<TestEvent>();
            counter.Increment<TestEvent>();

            Assert.Equal(2, counter.GetCount<TestEvent>());
        }

        [Fact]
        public void Decrement_ShouldDecreaseCount()
        {
            var counter = new EventCounter();
            counter.Increment<TestEvent>();
            counter.Increment<TestEvent>();
            counter.Decrement<TestEvent>();

            Assert.Equal(1, counter.GetCount<TestEvent>());
        }

        [Fact]
        public void DecrementToZero_ShouldStayZero()
        {
            var counter = new EventCounter();
            counter.Increment<TestEvent>();
            counter.Decrement<TestEvent>();
            counter.Decrement<TestEvent>(); // 不应该变成负数

            Assert.Equal(0, counter.GetCount<TestEvent>());
        }

        [Fact]
        public void Reset_ShouldClearAll()
        {
            var counter = new EventCounter();
            counter.Increment<TestEvent>();
            counter.Increment<TestEvent>();
            counter.Reset();

            Assert.Equal(0, counter.GetCount<TestEvent>());
        }

        [Fact]
        public void GetCount_WithoutIncrement_ShouldReturnZero()
        {
            var counter = new EventCounter();
            Assert.Equal(0, counter.GetCount<TestEvent>());
        }

        [Fact]
        public void GetCount_ByType_ShouldWork()
        {
            var counter = new EventCounter();
            counter.Increment<TestEvent>();

            Assert.Equal(1, counter.GetCount(typeof(TestEvent)));
        }

        [Fact]
        public void MultipleEventTypes_ShouldBeIndependent()
        {
            var counter = new EventCounter();
            counter.Increment<TestEvent>();

            var otherEvent = new EventCounter();
            otherEvent.Increment<TestEvent>();

            Assert.Equal(1, counter.GetCount<TestEvent>());
            Assert.Equal(1, otherEvent.GetCount<TestEvent>());
        }
    }
}
