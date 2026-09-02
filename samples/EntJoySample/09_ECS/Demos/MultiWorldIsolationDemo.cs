using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// 多 World 隔离验证：两个 World 各自跑 SystemRunner，各自注册同一个 System 类型。
    /// System.OnUpdate 内部通过 World.DefaultWorld 访问实体——修复前两个 World 的 System
    /// 都会访问第一个 World（串扰）；修复后各自访问自己所属 World 的实体。
    /// </summary>
    public static class MultiWorldIsolationDemo
    {
        public struct CountSystem : ISystem
        {
            public static string LastWorldName = "";
            public static int LastEntityCount = 0;

            public void OnUpdate()
            {
                LastWorldName = World.DefaultWorld.Name;
                LastEntityCount = 0;
                foreach (var _ in World.DefaultWorld.Query<Position, Velocity>())
                    LastEntityCount++;
            }
        }

        public static void Run()
        {
            Console.WriteLine("=== Multi World Isolation Demo ===\n");

            // World A：3 个 Position 实体 + 自己的 SystemRunner
            var worldA = new World("WorldA");
            var runnerA = new SystemRunner(worldA);
            runnerA.RegisterSystem<CountSystem>();
            worldA.CreateEntities(3, typeof(Position), typeof(Velocity));

            // World B：5 个 Position 实体 + 自己的 SystemRunner
            var worldB = new World("WorldB");
            var runnerB = new SystemRunner(worldB);
            runnerB.RegisterSystem<CountSystem>();
            worldB.CreateEntities(5, typeof(Position), typeof(Velocity));

            // A 的 System 应看到 worldA 的 3 个实体
            runnerA.Update();
            string nameA = CountSystem.LastWorldName;
            int countA = CountSystem.LastEntityCount;

            // B 的 System 应看到 worldB 的 5 个实体
            runnerB.Update();
            string nameB = CountSystem.LastWorldName;
            int countB = CountSystem.LastEntityCount;

            Console.WriteLine($"WorldA 的 System 看到: world={nameA}, entities={countA} (期望 WorldA / 3)");
            Console.WriteLine($"WorldB 的 System 看到: world={nameB}, entities={countB} (期望 WorldB / 5)");

            bool pass = nameA == "WorldA" && countA == 3 && nameB == "WorldB" && countB == 5;
            Console.WriteLine(pass ? "[PASS] 多 World 隔离正确（互不串扰）" : "[FAIL] 串扰：System 访问了错误的 World");

            worldA.Dispose();
            worldB.Dispose();
            Console.WriteLine("\n=== Multi World Isolation Demo Complete ===\n");
        }
    }
}
