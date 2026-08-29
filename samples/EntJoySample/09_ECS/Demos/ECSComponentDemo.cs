using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// [ECSComponent] 示例：验证标记组件（不写 : IComponentData）可走通
    /// Set/GetComponent/Query/DCB.AddComponent 全链路（Phase 8 S26 源生成器）。
    /// 若生成器未补齐 IComponentData，下面 Set/GetComponent/DCB.AddComponent 无法通过编译。
    /// </summary>
    public static class ECSComponentDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== ECSComponent Demo (auto IComponentData) ===\n");

            using var world = new World("ECSComponentDemo");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 3 个 [GenPosition, GenVelocity] 实体
            var entities = new Entity[3];
            for (int i = 0; i < 3; i++)
            {
                var e = em.NewEntity(typeof(GenPosition), typeof(GenVelocity));
                em.Set(e, new GenPosition { X = i * 10, Y = 0 });
                em.Set(e, new GenVelocity { X = 1, Y = 0 });
                entities[i] = e;
            }

            // Query 遍历（生成器补齐接口后，双组件强类型遍历）
            Console.WriteLine("--- Query<GenPosition, GenVelocity>() ---");
            int count = 0;
            long sumX = 0;
            foreach (var r in world.Query<GenPosition, GenVelocity>())
            {
                sumX += (long)r.Comp0.X;
                r.Comp0.X += r.Comp1.X;
                count++;
            }
            Console.WriteLine($"  matched {count} entities (expect 3), sumX={sumX} (expect 0+10+20=30)");
            Console.WriteLine($"  {(count == 3 && sumX == 30 ? "OK" : "BAD")}\n");

            // GetComponent 单实体读
            Console.WriteLine("--- GetComponent<GenPosition>() ---");
            ref var p = ref em.GetComponent<GenPosition>(entities[0]);
            p.X += 5;
            Console.WriteLine($"  entity0.GenPosition.X = {p.X} (expect 5)");
            Console.WriteLine($"  {(p.X == 5 ? "OK" : "BAD")}\n");

            // DCB.AddComponent 延迟加组件
            Console.WriteLine("--- DeferredCommandBuffer.AddComponent<GenPosition>() ---");
            var e2 = em.NewEntity(typeof(GenVelocity));
            var ecb = new DeferredCommandBuffer();
            ecb.AddComponent(e2, new GenPosition { X = 42, Y = 0 });
            ecb.Playback(em);
            ecb.Dispose();
            ref var p2 = ref em.GetComponent<GenPosition>(e2);
            Console.WriteLine($"  entity.GenPosition.X = {p2.X} (expect 42)");
            Console.WriteLine($"  {(p2.X == 42 ? "OK" : "BAD")}\n");

            Console.WriteLine("=== End ECSComponent Demo ===\n");
        }
    }
}
