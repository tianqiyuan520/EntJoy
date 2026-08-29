using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // [Reactive] 处理器：Added + Set 事件 → Execute 自动被 Observer 派发（静态计数器验证）
    [Reactive(ObserverEvents.Added | ObserverEvents.Set)]
    public struct AutoAddedHandler
    {
        public static int Calls;
        public static int LastValue;
        public static void Execute(in ReadOnlySpan<Entity> entities, in ReadOnlySpan<Health> values)
        {
            Calls += entities.Length;
            if (values.Length > 0) LastValue = (int)values[0].Current;
        }
    }

    // 多事件组合：Set + Removed
    [Reactive(ObserverEvents.Set | ObserverEvents.Removed)]
    public struct AutoSetRemovedHandler
    {
        public static int SetCalls;
        public static int RemovedCalls;
        public static void Execute(in ReadOnlySpan<Entity> entities, in ReadOnlySpan<Health> values)
        {
            // 无法区分 Set/Removed（同桶回调），只验证回调发生
            SetCalls++;
        }
    }

    /// <summary>
    /// S32 Reactive System 生成：验证 [Reactive] 处理器经 ReactiveSystemRegistry.RegisterAll
    /// 自动注册 Observer，组件事件触发时 Execute 被自动派发。
    /// </summary>
    public class ReactiveSystemGeneratorTests
    {
        [Fact]
        public void RegisterAll_AddedEvent_DispatchesExecute()
        {
            AutoAddedHandler.Calls = 0;
            AutoAddedHandler.LastValue = 0;
            using var world = new World("S32Added");
            World.DefaultWorld = world;

            ReactiveSystemRegistry.RegisterAll(world);

            // 触发 Health Added：NewEntity 带组件（2 次）
            world.EntityManager.NewEntity(typeof(Health));
            world.EntityManager.NewEntity(typeof(Position), typeof(Health));
            Assert.Equal(2, AutoAddedHandler.Calls);

            // 触发 Health Set：值写入 → Execute 收到新值
            var e = world.EntityManager.NewEntity(typeof(Health));   // 第 3 次 Added
            world.EntityManager.Set(e, new Health { Current = 77 }); // Set → Execute
            Assert.Equal(4, AutoAddedHandler.Calls);  // 3 × Added + 1 × Set
            Assert.Equal(77, AutoAddedHandler.LastValue);
        }

        [Fact]
        public void RegisterAll_SetRemovedEvent_DispatchesExecute()
        {
            AutoSetRemovedHandler.SetCalls = 0;
            using var world = new World("S32SetRemoved");
            World.DefaultWorld = world;

            ReactiveSystemRegistry.RegisterAll(world);

            var e = world.EntityManager.NewEntity(typeof(Health));
            world.EntityManager.Set(e, new Health { Current = 1 });   // Set → Execute
            world.EntityManager.RemoveComponent<Health>(e);                       // Removed → Execute

            Assert.Equal(2, AutoSetRemovedHandler.SetCalls);
        }
    }
}
