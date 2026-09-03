using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    // ─── Reactive System 生成示例 ───
    // [Reactive] 处理器：声明式订阅组件生命周期事件，由 ReactiveSystemRegistry.RegisterAll
    // 自动注册 Observer，无需手写 world.AddObserver<T>(...)。组件类型从 Execute 签名推导。

    [Reactive(ObserverEvents.Added | ObserverEvents.Set)]
    public struct HealthReactiveHandler
    {
        public static int Calls;
        public static int LastValue;
        public static void Execute(in ReadOnlySpan<Entity> entities, in ReadOnlySpan<Health> values)
        {
            Calls += entities.Length;
            if (values.Length > 0) LastValue = (int)values[0].Current;
        }
    }

    [Reactive(ObserverEvents.Removed)]
    public struct HealthRemovedHandler
    {
        public static int Calls;
        public static void Execute(in ReadOnlySpan<Entity> entities, in ReadOnlySpan<Health> values)
        {
            Calls += entities.Length;
        }
    }

    public static class ReactiveDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== Reactive Demo (auto Observer) ===\n");

            HealthReactiveHandler.Calls = 0;
            HealthReactiveHandler.LastValue = 0;
            HealthRemovedHandler.Calls = 0;

            using var world = new World("ReactiveDemo");
            World.DefaultWorld = world;

            // 一行注册本程序集所有 [Reactive] 处理器（替代逐个 AddObserver）
            ReactiveSystemRegistry.RegisterAll(world);

            var e = world.EntityManager.NewEntity(typeof(Health));              // Added
            world.EntityManager.Set(e, new Health { Current = 88 });            // Set
            world.EntityManager.RemoveComponent<Health>(e);                     // Removed

            Console.WriteLine($"  HealthReactiveHandler.Calls = {HealthReactiveHandler.Calls} (expect 2: Added+Set)");
            Console.WriteLine($"  LastValue                   = {HealthReactiveHandler.LastValue} (expect 88)");
            Console.WriteLine($"  HealthRemovedHandler.Calls  = {HealthRemovedHandler.Calls} (expect 1)");
            bool ok = HealthReactiveHandler.Calls == 2 &&
                      HealthReactiveHandler.LastValue == 88 &&
                      HealthRemovedHandler.Calls == 1;
            Console.WriteLine($"  {(ok ? "OK" : "BAD")}\n");

            Console.WriteLine("=== End Reactive Demo ===\n");
        }
    }
}
