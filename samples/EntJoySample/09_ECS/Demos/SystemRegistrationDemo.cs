using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    // ─── S27 System 注册生成示例 ───
    // 以下 System 无需逐个 runner.RegisterSystem<T>()，由 SystemRegistrationSourceGenerator
    // 自动收集到 SystemRegistry.RegisterAll（一行注册本程序集所有 struct : ISystem）。
    // OnUpdate 用静态计数器验证执行（不打印、不改数据，避免干扰其他 demo）。

    [Read(typeof(Position))]
    [Write(typeof(Position))]
    [Order(0)]
    public struct AutoMoveSystem : ISystem
    {
        public static int Executions;
        public void OnUpdate() => Executions++;
    }

    [Read(typeof(Health))]
    [Write(typeof(Health))]
    [Order(1)]
    public struct AutoDamageSystem : ISystem
    {
        public static int Executions;
        public void OnUpdate() => Executions++;
    }

    [Read(typeof(Health))]
    [RunWhen(typeof(DamageEvent))]
    [Order(2)]
    public struct AutoRegenSystem : ISystem
    {
        public static int Executions;
        public void OnUpdate() => Executions++;
    }

    public static class SystemRegistrationDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== SystemRegistration Demo (auto RegisterAll) ===\n");

            AutoMoveSystem.Executions = 0;
            AutoDamageSystem.Executions = 0;
            AutoRegenSystem.Executions = 0;

            using var world = new World("SystemRegistrationDemo");
            World.DefaultWorld = world;

            var runner = new SystemRunner(world);
            // 一行注册本程序集所有 ISystem（替代逐个 RegisterSystem<T>()）
            SystemRegistry.RegisterAll(runner);

            runner.PrintSchedule();

            // 触发一次 DamageEvent：让带 [RunWhen] 的 AutoRegenSystem 只执行第 1 帧
            runner.EventCounter.Increment<DamageEvent>();

            runner.Update();
            runner.Update();

            Console.WriteLine($"  AutoMoveSystem.Executions   = {AutoMoveSystem.Executions} (expect 2)");
            Console.WriteLine($"  AutoDamageSystem.Executions = {AutoDamageSystem.Executions} (expect 2)");
            Console.WriteLine($"  AutoRegenSystem.Executions  = {AutoRegenSystem.Executions} (expect 1, RunWhen 条件)");
            bool ok = AutoMoveSystem.Executions == 2 &&
                      AutoDamageSystem.Executions == 2 &&
                      AutoRegenSystem.Executions == 1;
            Console.WriteLine($"  {(ok ? "OK" : "BAD")}\n");

            Console.WriteLine("=== End SystemRegistration Demo ===\n");
        }
    }
}
