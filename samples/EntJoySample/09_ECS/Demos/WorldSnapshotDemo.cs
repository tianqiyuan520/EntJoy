using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// World 快照示例：TakeSnapshot 序列化 → 修改 World → Restore 恢复到快照状态，
    /// 验证实体/组件值完整恢复（零拷贝序列化）。
    /// </summary>
    public static class WorldSnapshotDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== World 快照 Demo ===\n");
            var world = new World("SnapshotDemo");

            var e1 = world.EntityManager.NewEntity(typeof(Position), typeof(Velocity));
            world.EntityManager.Set(e1, new Position { X = 1, Y = 2 });
            world.EntityManager.Set(e1, new Velocity { X = 3, Y = 4 });

            var e2 = world.EntityManager.NewEntity(typeof(Position));
            world.EntityManager.Set(e2, new Position { X = 5, Y = 6 });

            var snapshot = world.TakeSnapshot();
            Console.WriteLine($"--- 快照后 ---");
            Console.WriteLine(world.DumpWorld());

            // 修改：改 e1、删 e2、加 e3
            world.EntityManager.Set(e1, new Position { X = 99, Y = 99 });
            world.EntityManager.DestroyEntity(e2);
            var e3 = world.EntityManager.NewEntity(typeof(Position));
            world.EntityManager.Set(e3, new Position { X = 7, Y = 8 });
            Console.WriteLine($"--- 修改后 ---");
            Console.WriteLine(world.DumpWorld());

            // 恢复
            world.Restore(snapshot);
            Console.WriteLine($"--- 恢复后 ---");
            Console.WriteLine(world.DumpWorld());

            // 验证：e1 的 Position 恢复到 X=1，e2 存在且 X=5，e3 消失
            bool restored = world.EntityManager.EntityCount == 2
                && world.EntityManager.GetComponent<Position>(e1).X == 1
                && world.EntityManager.GetComponent<Position>(e2).X == 5;
            Console.WriteLine($"恢复正确: {restored}");

            world.Dispose();
            Console.WriteLine("\n=== World 快照 Demo Complete ===\n");
        }
    }
}
