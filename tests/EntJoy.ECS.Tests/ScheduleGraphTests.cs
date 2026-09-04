using System;
using System.Collections.Generic;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // 测试用组件
    public struct Position : IComponentData { public float X, Y; }
    public struct Velocity : IComponentData { public float X, Y; }
    public struct Health : IComponentData { public float Current; }
    public struct Armor : IComponentData { public float Value; }

    // 测试用 System
    [Read(typeof(Position))]
    [Read(typeof(Velocity))]
    [Write(typeof(Position))]
    [Order(0)]
    public struct MovementSystem : ISystem
    {
        public void OnUpdate() { }
    }

    [Read(typeof(Health))]
    [Write(typeof(Health))]
    [Order(1)]
    public struct DamageSystem : ISystem
    {
        public void OnUpdate() { }
    }

    [Read(typeof(Armor))]
    [Write(typeof(Armor))]
    [Order(2)]
    public struct ArmorSystem : ISystem
    {
        public void OnUpdate() { }
    }

    // 冲突的 System
    [Read(typeof(Position))]
    [Write(typeof(Position))]
    public struct ReadPositionSystem : ISystem
    {
        public void OnUpdate() { }
    }

    [Write(typeof(Position))]
    public struct WritePositionSystem : ISystem
    {
        public void OnUpdate() { }
    }

    // 测试 OrderBefore/OrderAfter 的 System
    [Write(typeof(Health))]
    [Order(0)]
    public struct HealSystem : ISystem
    {
        public void OnUpdate() { }
    }

    [Read(typeof(Health))]
    [Write(typeof(Armor))]
    [OrderAfter(typeof(HealSystem))]  // 必须在 HealSystem 之后
    [Order(1)]
    public struct BuffSystem : ISystem
    {
        public void OnUpdate() { }
    }

    [Read(typeof(Position))]
    [Order(2)]
    [OrderBefore(typeof(HealSystem))]  // 必须在 HealSystem 之前
    public struct CheckSystem : ISystem
    {
        public void OnUpdate() { }
    }

    public class ScheduleGraphTests
    {
        [Fact]
        public void RegisterSystem_ShouldAddToGraph()
        {
            var graph = new ScheduleGraph();
            graph.RegisterSystem<MovementSystem>();
            graph.RegisterSystem<DamageSystem>();

            var layers = graph.GetLayers();
            // 无读写冲突，应该在同一层（1层）
            Assert.Single(layers);
            Assert.Equal(2, layers[0].Count);
        }

        [Fact]
        public void NoConflict_ShouldBeParallel()
        {
            var graph = new ScheduleGraph();
            graph.RegisterSystem<MovementSystem>(); // 读 Pos/Vel，写 Pos
            graph.RegisterSystem<DamageSystem>();   // 读 Health，写 Health

            var layers = graph.GetLayers();
            // 无读写冲突，应该在同一层（并行）
            Assert.Equal(1, layers.Count);
            Assert.Equal(2, layers[0].Count);
        }

        [Fact]
        public void WriteWriteConflict_ShouldBeSequential()
        {
            var graph = new ScheduleGraph();
            graph.RegisterSystem<ReadPositionSystem>();  // 读 Position
            graph.RegisterSystem<WritePositionSystem>(); // 写 Position

            var layers = graph.GetLayers();
            // 有写-写冲突，应该在不同层（串行）
            Assert.Equal(2, layers.Count);
        }

        [Fact]
        public void OrderPriority_ShouldDetermineOrder()
        {
            var graph = new ScheduleGraph();
            graph.RegisterSystem<DamageSystem>();   // Order 1
            graph.RegisterSystem<MovementSystem>(); // Order 0

            var layers = graph.GetLayers();
            // MovementSystem (Order 0) 应该在 DamageSystem (Order 1) 之前
            // 但由于无冲突，它们在同一层，只是顺序不同
            Assert.Single(layers);
            Assert.Equal(2, layers[0].Count);
        }

        [Fact]
        public void PrintSchedule_ShouldNotThrow()
        {
            var graph = new ScheduleGraph();
            graph.RegisterSystem<MovementSystem>();
            graph.RegisterSystem<DamageSystem>();

            // 应该不抛异常
            graph.PrintSchedule();
        }

        [Fact]
        public void OrderAfter_ShouldCreateDependency()
        {
            var graph = new ScheduleGraph();
            graph.RegisterSystem<HealSystem>();   // 写 Health
            graph.RegisterSystem<BuffSystem>();   // [OrderAfter(HealSystem)]

            var layers = graph.GetLayers();
            // BuffSystem 必须在 HealSystem 之后 → 2 层
            Assert.Equal(2, layers.Count);
            Assert.Contains("HealSystem", layers[0][0].Name);
            Assert.Contains("BuffSystem", layers[1][0].Name);
        }

        [Fact]
        public void OrderBefore_ShouldCreateDependency()
        {
            var graph = new ScheduleGraph();
            graph.RegisterSystem<HealSystem>();   // 写 Health
            graph.RegisterSystem<CheckSystem>();  // [OrderBefore(HealSystem)]

            var layers = graph.GetLayers();
            // CheckSystem 必须在 HealSystem 之前 → 2 层
            Assert.Equal(2, layers.Count);
            Assert.Contains("CheckSystem", layers[0][0].Name);
            Assert.Contains("HealSystem", layers[1][0].Name);
        }

        [Fact]
        public void ManualOrder_ShouldOverrideAutomaticConflict()
        {
            var graph = new ScheduleGraph();
            // HealSystem 写 Health，BuffSystem 读 Health → 自动冲突
            // 但 BuffSystem 有 [OrderAfter(HealSystem)]，手动指定顺序
            graph.RegisterSystem<BuffSystem>();   // [OrderAfter(HealSystem)]
            graph.RegisterSystem<HealSystem>();   // 写 Health

            var layers = graph.GetLayers();
            // 手动顺序优先：HealSystem → BuffSystem
            Assert.Equal(2, layers.Count);
            Assert.Contains("HealSystem", layers[0][0].Name);
            Assert.Contains("BuffSystem", layers[1][0].Name);
        }

        // 测试多组件语法
        [Read(typeof(Position), typeof(Velocity))]
        [Write(typeof(Health), typeof(Armor))]
        public struct MultiComponentSystem : ISystem
        {
            public void OnUpdate() { }
        }

        // 测试 params 语法（超过4个组件）
        [Read(typeof(Position), typeof(Velocity), typeof(Health), typeof(Armor), typeof(Position))]
        [Write(typeof(Health))]
        public struct ManyComponentsSystem : ISystem
        {
            public void OnUpdate() { }
        }

        [Fact]
        public void MultiComponentSyntax_ShouldWork()
        {
            var graph = new ScheduleGraph();
            graph.RegisterSystem<MultiComponentSystem>();

            var layers = graph.GetLayers();
            Assert.Single(layers);
            Assert.Single(layers[0]);
            
            var slot = layers[0][0];
            Assert.Equal(2, slot.ReadComponents.Count);  // Position, Velocity
            Assert.Equal(2, slot.WriteComponents.Count); // Health, Armor
            Assert.Contains(typeof(Position), slot.ReadComponents);
            Assert.Contains(typeof(Velocity), slot.ReadComponents);
            Assert.Contains(typeof(Health), slot.WriteComponents);
            Assert.Contains(typeof(Armor), slot.WriteComponents);
        }

        [Fact]
        public void ManyComponentsSyntax_ShouldWork()
        {
            var graph = new ScheduleGraph();
            graph.RegisterSystem<ManyComponentsSystem>();

            var layers = graph.GetLayers();
            Assert.Single(layers);
            
            var slot = layers[0][0];
            Assert.Equal(4, slot.ReadComponents.Count);  // Position, Velocity, Health, Armor (去重)
            Assert.Equal(1, slot.WriteComponents.Count); // Health
        }
    }
}
