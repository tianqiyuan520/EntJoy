using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    [Read(typeof(Position))]
    [Read(typeof(Velocity))]
    [Write(typeof(Position))]
    [Order(0)]
    public struct MovementSystem : ISystem
    {
        public void OnUpdate()
        {
            foreach (var result in SystemAPI.Query<Position, Velocity>())
            {
                result.Comp0.X += result.Comp1.X * 0.016f;
                result.Comp0.Y += result.Comp1.Y * 0.016f;
            }
        }
    }

    [Read(typeof(Health))]
    [Write(typeof(Health))]
    [Order(1)]
    public struct DamageSystem : ISystem
    {
        public void OnUpdate()
        {
            foreach (var result in SystemAPI.Query<Position, Health>())
            {
                result.Comp1.Current -= 0.1f;
                if (result.Comp1.Current < 0)
                    result.Comp1.Current = 0;
            }
        }
    }

    [Read(typeof(Health))]
    [Write(typeof(Health))]
    [RunWhen(typeof(DamageEvent))]
    [OrderAfter(typeof(DamageSystem))]
    [Order(2)]
    public struct RegenSystem : ISystem
    {
        public void OnUpdate()
        {
            Console.WriteLine("  [RegenSystem] Regen triggered by DamageEvent");
            foreach (var result in SystemAPI.Query<Position, Health>())
            {
                result.Comp1.Current += 5f;
                if (result.Comp1.Current > 100f)
                    result.Comp1.Current = 100f;
            }
        }
    }

    public static class ScheduleGraphDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== Schedule Graph Demo ===\n");
            var world = new World("ScheduleGraphDemo");
            World.DefaultWorld = world;

            var types = new ComponentType[] { typeof(Position), typeof(Velocity), typeof(Health) };
            world.CreateEntities(1000, types);

            var runner = new SystemRunner(world);
            runner.RegisterSystem<MovementSystem>();
            runner.RegisterSystem<DamageSystem>();
            runner.RegisterSystem<RegenSystem>();

            runner.PrintSchedule();

            for (int frame = 0; frame < 5; frame++)
            {
                Console.WriteLine($"--- Frame {runner.CurrentFrame + 1} ---");
                if (frame == 2)
                {
                    Console.WriteLine("  [Event] DamageEvent fired");
                    runner.EventCounter.Increment<DamageEvent>();
                }
                runner.Update();
                Console.WriteLine($"  Frame {runner.CurrentFrame} done\n");
            }

            world.Dispose();
            Console.WriteLine("=== Demo End ===\n");
        }
    }
}