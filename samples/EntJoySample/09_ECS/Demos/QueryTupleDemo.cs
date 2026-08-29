using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// N 元组查询示例：验证 SourceGenerator 生成的 <c>world.Query&lt;T0, T1, T2&gt;()</c>
    /// 强类型三组件遍历（运行时零反射，按需生成）。
    /// </summary>
    public static class QueryTupleDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== QueryTuple Demo (N-ary query) ===\n");

            using var world = new World("QueryTupleDemo");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 创建 5 个 [Position, Velocity, Health] 实体（记录句柄，供各 demo 段重置数据）
            var entities = new Entity[5];
            for (int i = 0; i < 5; i++)
            {
                var e = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health));
                em.Set(e, new Position { X = i * 10, Y = 0 });
                em.Set(e, new Velocity { X = 1, Y = 0 });
                em.Set(e, new Health { Current = 100 });
                entities[i] = e;
            }
            // 1 个 [Position, Velocity] 实体（不应被三组件查询匹配）
            em.NewEntity(typeof(Position), typeof(Velocity));

            // 每个 demo 段独立断言：段前重置 X = i*10，避免前段修改污染后段
            void ResetPositions()
            {
                for (int i = 0; i < entities.Length; i++)
                    em.Set(entities[i], new Position { X = i * 10, Y = 0 });
            }

            // Chunk 级三组件强类型遍历（生成器生成）
            Console.WriteLine("--- QueryChunks<Position, Velocity, Health>() ---");
            ResetPositions();
            long chunkSumX = 0;
            int chunkEntityCount = 0;
            foreach (var chunk in world.QueryChunks<Position, Velocity, Health>())
            {
                chunkEntityCount += chunk.Length;
                var posSpan = chunk.GetSpan0();
                var velSpan = chunk.GetSpan1();
                var hpSpan = chunk.GetSpan2();
                for (int i = 0; i < posSpan.Length; i++)
                {
                    chunkSumX += (long)posSpan[i].X;
                    posSpan[i].X += velSpan[i].X;
                    hpSpan[i].Current -= 0.1f;
                }
            }
            Console.WriteLine($"  matched {chunkEntityCount} entities (expect 5), sumX={chunkSumX} (expect 0+10+20+30+40=100)");
            Console.WriteLine($"  {(chunkEntityCount == 5 && chunkSumX == 100 ? "OK" : "BAD")}\n");

            // 三组件强类型遍历（生成器生成）
            Console.WriteLine("--- Query<Position, Velocity, Health>() ---");
            ResetPositions();
            int count = 0;
            long sumX = 0;
            foreach (var r in world.Query<Position, Velocity, Health>())
            {
                sumX += (long)r.Comp0.X;
                r.Comp0.X += r.Comp1.X;          // 用 Comp0/Comp1/Comp2 强类型访问
                r.Comp2.Current -= 0.1f;
                count++;
            }
            Console.WriteLine($"  matched {count} entities (expect 5), sumX={sumX} (expect 0+10+20+30+40=100)");
            Console.WriteLine($"  {(count == 5 && sumX == 100 ? "OK" : "BAD")}\n");

            // N 元组 QueryBuilder 版本（复用共享查询，WithAll<T0,T1,T2> 由生成器生成）
            Console.WriteLine("--- QueryBuilder variant (shared query) ---");
            var query = world.GetOrCreateEntityQuery(
                new QueryBuilder().WithAll<Position, Velocity, Health>());
            Console.WriteLine($"  shared query matches {query.CalculateEntityCount()} entities (expect 5)");
            Console.WriteLine($"  {(query.CalculateEntityCount() == 5 ? "OK" : "BAD")}\n");

            Console.WriteLine("=== End QueryTuple Demo ===\n");
        }
    }
}
