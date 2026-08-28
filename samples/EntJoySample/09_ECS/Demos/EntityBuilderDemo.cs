using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    public static class EntityBuilderDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== Entity Builder Demo ===\n");
            Console.WriteLine("Entity Builder now uses SetRaw (no reflection), performance is close to manual way.\n");
            
            var world = new World("EntityBuilderDemo");
            World.DefaultWorld = world;

            Console.WriteLine("Manual way:");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                var entity = world.EntityManager.NewEntity(new ComponentType[] { typeof(Position), typeof(Velocity), typeof(Health) });
                world.EntityManager.Set(entity, new Position { X = i, Y = i });
                world.EntityManager.Set(entity, new Velocity { X = 0.1f, Y = 0.1f });
                world.EntityManager.Set(entity, new Health { Current = 100, Max = 100 });
            }
            sw.Stop();
            Console.WriteLine($"  1000 entities: {sw.Elapsed.TotalMilliseconds:F3} ms");

            Console.WriteLine("\nEntity Builder way:");
            sw.Restart();
            for (int i = 0; i < 1000; i++)
            {
                world.Spawn()
                    .With(new Position { X = i, Y = i })
                    .With(new Velocity { X = 0.1f, Y = 0.1f })
                    .With(new Health { Current = 100, Max = 100 })
                    .Build();
            }
            sw.Stop();
            Console.WriteLine($"  1000 entities: {sw.Elapsed.TotalMilliseconds:F3} ms");

            world.Dispose();
            Console.WriteLine("\n=== Demo End ===\n");
        }
    }
}