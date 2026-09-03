using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// 数据导航工具示例：DumpEntity（实体字段值）/ DumpArchetype（Archetype 所有实体）/ DumpWorld（概览），
    /// 均用组件元数据非反射打印。
    /// </summary>
    public static class DataNavigationDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== 数据导航工具 Demo ===\n");
            var world = new World("NavDemo");

            var e1 = world.EntityManager.NewEntity(typeof(Position), typeof(Velocity));
            world.EntityManager.Set(e1, new Position { X = 1, Y = 2 });
            world.EntityManager.Set(e1, new Velocity { X = 3, Y = 4 });

            var e2 = world.EntityManager.NewEntity(typeof(Position));
            world.EntityManager.Set(e2, new Position { X = 5, Y = 6 });

            Console.WriteLine("--- DumpEntity ---");
            Console.WriteLine(world.DumpEntity(e1));

            Console.WriteLine("--- DumpArchetype(typeof(Position)) ---");
            Console.WriteLine(world.DumpArchetype(typeof(Position)));

            Console.WriteLine("--- DumpWorld ---");
            Console.WriteLine(world.DumpWorld());

            world.Dispose();
            Console.WriteLine("=== 数据导航工具 Demo Complete ===\n");
        }
    }
}
