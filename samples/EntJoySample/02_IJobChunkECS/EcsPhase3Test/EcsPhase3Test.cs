using System;
using System.Diagnostics;
using System.Threading;
using EntJoy;
using EntJoy.JobSystem;

namespace EntJoySample.EcsPhase3Test
{
    public sealed class EcsPhase3Test : IDisposable
    {
        private World _world;
        public void Dispose() { _world?.Dispose(); }

        public void Run()
        {
            Console.WriteLine("=== ECS Phase 3 基线性能测试 ===\n");
            Test1_MultiArchetypeParallel();
            Test2_StructuralChangeWait();
            Test3_BatchCreation();
            Test4_SelectiveWaitPotential();
            Test5_BottleneckDiagnosis();
            Test6_WrittenComponentsFilter();
            Test8_ECB();
        }

        private void Test1_MultiArchetypeParallel()
        {
            Console.WriteLine("── Test 1: 多 Archetype 并行 Job ──\n");
            _world = new World("T1"); var em = _world.EntityManager; World.DefaultWorld = _world;
            var aPV = new ComponentType[] { typeof(Position), typeof(Velocity) };
            var aHA = new ComponentType[] { typeof(Health), typeof(Armor) };
            var aPA = new ComponentType[] { typeof(Position), typeof(Armor) };
            for (int i = 0; i < 100_000; i++) { em.NewEntity(aPV); em.NewEntity(aHA); em.NewEntity(aPA); }
            var qPV = new QueryBuilder().WithAll<Position, Velocity>();
            var qHA = new QueryBuilder().WithAll<Health, Armor>();
            var qPA = new QueryBuilder().WithAll<Position, Armor>();
            for (int i = 0; i < 5; i++) { new MoveJob { DeltaTime = 0.016f }.Schedule(qPV).Complete(); new DamageJob { DamageAmount = 1, Iterations = 10 }.Schedule(qHA).Complete(); new ArmorJob { Bonus = 0.1f }.Schedule(qPA).Complete(); }
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) { new MoveJob { DeltaTime = 0.016f }.Schedule(qPV).Complete(); new DamageJob { DamageAmount = 1, Iterations = 10 }.Schedule(qHA).Complete(); new ArmorJob { Bonus = 0.1f }.Schedule(qPA).Complete(); }
            sw.Stop(); Console.WriteLine($"  串行: {sw.Elapsed.TotalMilliseconds / 100:F3} ms/frame");
            sw.Restart();
            for (int i = 0; i < 100; i++) { var h1 = new MoveJob { DeltaTime = 0.016f }.Schedule(qPV); var h2 = new DamageJob { DamageAmount = 1, Iterations = 10 }.Schedule(qHA); var h3 = new ArmorJob { Bonus = 0.1f }.Schedule(qPA); h1.Complete(); h2.Complete(); h3.Complete(); }
            sw.Stop(); Console.WriteLine($"  并行: {sw.Elapsed.TotalMilliseconds / 100:F3} ms/frame\n");
            _world.Dispose(); _world = null;
        }

        private void Test2_StructuralChangeWait()
        {
            Console.WriteLine("── Test 2: 结构变更等待时间 ──\n");
            _world = new World("T2"); var em = _world.EntityManager; World.DefaultWorld = _world;
            var types = new ComponentType[] { typeof(Position), typeof(Velocity), typeof(Health) };
            var firstEntity = default(Entity);
            for (int i = 0; i < 100_000; i++) { var e = em.NewEntity(types); em.Set(e, new Position { X = i, Y = i }); em.Set(e, new Velocity { X = 0.1f, Y = 0.2f }); em.Set(e, new Health { Current = 100, Max = 100 }); if (i == 0) firstEntity = e; }
            var q = new QueryBuilder().WithAll<Position, Velocity>();
            var sw = Stopwatch.StartNew(); new DamageJob { DamageAmount = 1, Iterations = 1000 }.Schedule(q).Complete(); sw.Stop();
            double heavy = sw.Elapsed.TotalMilliseconds; Console.WriteLine($"  重计算 Job: {heavy:F3} ms");
            sw.Restart(); var h = new DamageJob { DamageAmount = 1, Iterations = 1000 }.Schedule(q); em.AddComponent(firstEntity, new Armor { Value = 5 }); sw.Stop();
            Console.WriteLine($"  AddComponent 等待: {sw.Elapsed.TotalMilliseconds:F3} ms (应 ≈ {heavy:F3})");
            em.RemoveComponent<Armor>(firstEntity);
            sw.Restart(); h = new DamageJob { DamageAmount = 1, Iterations = 1000 }.Schedule(q); em.Set(firstEntity, new Position { X = 999, Y = 999 }); sw.Stop();
            Console.WriteLine($"  Set 等待: {sw.Elapsed.TotalMilliseconds:F3} ms (应 ≈ {heavy:F3})\n");
            _world.Dispose(); _world = null;
        }

        private void Test3_BatchCreation()
        {
            Console.WriteLine("── Test 3: 批量创建性能 ──\n");
            var types = new ComponentType[] { typeof(Position), typeof(Velocity) };
            _world = new World("T3"); var em = _world.EntityManager; World.DefaultWorld = _world;
            var sw = Stopwatch.StartNew(); em.CreateEntities(100_000, types); sw.Stop();
            Console.WriteLine($"  10 万实体: {sw.Elapsed.TotalMilliseconds:F3} ms");
            _world.Dispose(); _world = null;
            _world = new World("T3b"); em = _world.EntityManager; World.DefaultWorld = _world;
            sw.Restart(); em.CreateEntities(1_000_000, types); sw.Stop();
            Console.WriteLine($"  100 万实体: {sw.Elapsed.TotalMilliseconds:F3} ms ({sw.Elapsed.TotalMilliseconds / 1_000_000 * 1000:F2} μs/entity)\n");
            _world.Dispose(); _world = null;
        }

        private void Test4_SelectiveWaitPotential()
        {
            Console.WriteLine("── Test 4: Selective Wait 潜力分析 ──\n");
            _world = new World("T4"); var em = _world.EntityManager; World.DefaultWorld = _world;
            var aPV = new ComponentType[] { typeof(Position), typeof(Velocity) };
            var aHA = new ComponentType[] { typeof(Health), typeof(Armor) };
            var aPH = new ComponentType[] { typeof(Position), typeof(Health) };
            for (int i = 0; i < 50_000; i++) { var e = em.NewEntity(aPV); em.Set(e, new Position { X = 1, Y = 1 }); em.Set(e, new Velocity { X = 0.1f, Y = 0.1f }); }
            for (int i = 0; i < 50_000; i++) { var e = em.NewEntity(aHA); em.Set(e, new Health { Current = 100, Max = 100 }); em.Set(e, new Armor { Value = 10 }); }
            for (int i = 0; i < 50_000; i++) { var e = em.NewEntity(aPH); em.Set(e, new Position { X = 1, Y = 1 }); em.Set(e, new Health { Current = 50, Max = 100 }); }
            var q0 = new QueryBuilder().WithAll<Position, Velocity>(); var q1 = new QueryBuilder().WithAll<Health, Armor>(); var q2 = new QueryBuilder().WithAll<Position, Health>();
            var sw = Stopwatch.StartNew(); for (int i = 0; i < 100; i++) { new MoveJob { DeltaTime = 0.016f }.Schedule(q0).Complete(); new DamageJob { DamageAmount = 1, Iterations = 10 }.Schedule(q1).Complete(); new ArmorJob { Bonus = 0.1f }.Schedule(q1).Complete(); }
            sw.Stop();
            double serial = sw.Elapsed.TotalMilliseconds / 100;
            sw.Restart(); for (int i = 0; i < 100; i++) { var h1 = new MoveJob { DeltaTime = 0.016f }.Schedule(q0); var h2 = new DamageJob { DamageAmount = 1, Iterations = 10 }.Schedule(q1); var h3 = new ArmorJob { Bonus = 0.1f }.Schedule(q1); h1.Complete(); h2.Complete(); h3.Complete(); }
            sw.Stop();
            double parallel = sw.Elapsed.TotalMilliseconds / 100;
            Console.WriteLine($"  串行: {serial:F3} ms  并行: {parallel:F3} ms  潜力: {serial / parallel:F2}x\n");
            _world.Dispose(); _world = null;
        }

        private unsafe void Test5_BottleneckDiagnosis()
        {
            Console.WriteLine("── Test 5: 瓶颈诊断 ──\n");
            const int N = 1_000_000, W = 10, M = 200;
            _world = new World("T5"); var em = _world.EntityManager; World.DefaultWorld = _world;
            var types = new ComponentType[] { typeof(Position), typeof(Velocity) };
            var entities = em.CreateEntities(N, types);
            foreach (var e in entities) { em.Set(e, new Position { X = 1, Y = 1 }); em.Set(e, new Velocity { X = 0.001f, Y = 0.001f }); }
            var query = new QueryBuilder().WithAll<Position, Velocity>();
            double aMs = 0, bMs = 0, cMs = 0; int workers = Environment.ProcessorCount;

            // A: 单线程纯内存读写
            {
                var arch = em.Archetypes[0]; int pi = arch.GetComponentTypeIndex<Position>(), vi = arch.GetComponentTypeIndex<Velocity>();
                for (int i = 0; i < W; i++) foreach (var c in arch.ChunkSpan) { var p = (Position*)c.GetComponentArrayPointer(pi); var v = (Velocity*)c.GetComponentArrayPointer(vi); for (int j = 0; j < c.EntityCount; j++) p[j].X += v[j].X * 0.001f; }
                var sw = Stopwatch.StartNew(); for (int i = 0; i < M; i++) foreach (var c in arch.ChunkSpan) { var p = (Position*)c.GetComponentArrayPointer(pi); var v = (Velocity*)c.GetComponentArrayPointer(vi); for (int j = 0; j < c.EntityCount; j++) p[j].X += v[j].X * 0.001f; }
                sw.Stop();
                aMs = sw.Elapsed.TotalMilliseconds / M; Console.WriteLine($"  A: 单线程纯内存读写  {aMs:F4} ms  ({aMs / N * 1e6:F1} ns/entity)");
            }

            // B: 单线程带分支
            {
                var arch = em.Archetypes[0]; int pi = arch.GetComponentTypeIndex<Position>(), vi = arch.GetComponentTypeIndex<Velocity>();
                for (int i = 0; i < W; i++) foreach (var c in arch.ChunkSpan) { var p = (Position*)c.GetComponentArrayPointer(pi); var v = (Velocity*)c.GetComponentArrayPointer(vi); for (int j = 0; j < c.EntityCount; j++) { p[j].X += v[j].X * 0.001f; if (p[j].X > 10f) p[j].X = -10f; } }
                var sw = Stopwatch.StartNew(); for (int i = 0; i < M; i++) foreach (var c in arch.ChunkSpan) { var p = (Position*)c.GetComponentArrayPointer(pi); var v = (Velocity*)c.GetComponentArrayPointer(vi); for (int j = 0; j < c.EntityCount; j++) { p[j].X += v[j].X * 0.001f; if (p[j].X > 10f) p[j].X = -10f; } }
                sw.Stop();
                bMs = sw.Elapsed.TotalMilliseconds / M; Console.WriteLine($"  B: 单线程带分支      {bMs:F4} ms  ({bMs / N * 1e6:F1} ns/entity)");
            }

            // C: 多线程并行
            {
                for (int i = 0; i < W; i++) new MoveJob { DeltaTime = 0.001f }.Schedule(query).Complete();
                var sw = Stopwatch.StartNew(); for (int i = 0; i < M; i++) new MoveJob { DeltaTime = 0.001f }.Schedule(query).Complete(); sw.Stop();
                cMs = sw.Elapsed.TotalMilliseconds / M; Console.WriteLine($"  C: 多线程并行 ({workers} workers) {cMs:F4} ms  ({cMs / N * 1e6:F1} ns/entity)");
            }

            Console.WriteLine($"\n  ── 分析 ──");
            Console.WriteLine($"  A (纯内存下限):     {aMs:F4} ms");
            Console.WriteLine($"  B (带分支):         {bMs:F4} ms  分支开销: {bMs - aMs:F4} ms ({bMs / aMs:F2}x)");
            Console.WriteLine($"  C (调度器并行):      {cMs:F4} ms  加速比: {aMs / cMs:F2}x vs 单线程");
            Console.WriteLine($"  理论最优 ({workers}x):  {aMs / workers:F4} ms");
            Console.WriteLine($"  调度器开销:          {cMs - aMs / workers:F4} ms\n");
            _world.Dispose(); _world = null;
        }

        // ── Test 6: writtenComponents 过滤 ──
        private void Test6_WrittenComponentsFilter()
        {
            Console.WriteLine("── Test 6: writtenComponents Selective Wait ──\n");
            _world = new World("T6"); var em = _world.EntityManager; World.DefaultWorld = _world;
            var aPV = new ComponentType[]{typeof(Position),typeof(Velocity)};
            var aHV = new ComponentType[]{typeof(Health),typeof(Velocity)};
            for (int i = 0; i < 100_000; i++) { em.NewEntity(aPV); em.NewEntity(aHV); }
            var qPV = new QueryBuilder().WithAll<Position,Velocity>();
            var qHV = new QueryBuilder().WithAll<Health,Velocity>();

            // 场景: Job A 写 Velocity, Job B 写 Health
            // Set<Position> 应该只等 Job A (写了 Position 的), 不等 Job B
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)
            {
                var h1 = ChunkJobExtensions.Schedule(new MoveJob{DeltaTime=0.016f}, qPV, writtenComponents: new ComponentType[]{typeof(Position)});
                var h2 = ChunkJobExtensions.Schedule(new DamageJob{DamageAmount=1,Iterations=10}, qHV, writtenComponents: new ComponentType[]{typeof(Health)});
                h1.Complete(); h2.Complete();
            }
            sw.Stop();
            Console.WriteLine($"  两个 Job (写 Position + 写 Health): {sw.Elapsed.TotalMilliseconds/100:F3} ms/frame");

            // 对比: 不声明 writtenComponents (保守等待)
            sw.Restart();
            for (int i = 0; i < 100; i++)
            {
                var h1 = new MoveJob{DeltaTime=0.016f}.Schedule(qPV);
                var h2 = new DamageJob{DamageAmount=1,Iterations=10}.Schedule(qHV);
                h1.Complete(); h2.Complete();
            }
            sw.Stop();
            Console.WriteLine($"  对比 (无 writtenComponents):       {sw.Elapsed.TotalMilliseconds/100:F3} ms/frame\n");
            _world.Dispose(); _world = null;
        }

        // ── Test 8: ECB 手动延迟命令 ──
        private void Test8_ECB()
        {
            Console.WriteLine("── Test 8: ECB 手动延迟命令 ──\n");
            _world = new World("T8"); var em = _world.EntityManager; World.DefaultWorld = _world;

            // 创建一些实体
            var types = new ComponentType[]{typeof(Position),typeof(Velocity)};
            var entities = em.CreateEntities(10_000, types);

            // 用 ECB 记录命令
            var ecb = new DeferredCommandBuffer();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 10_000; i++)
            {
                ecb.CreateEntity(typeof(Position), typeof(Velocity));
            }
            sw.Stop();
            Console.WriteLine($"  记录 10000 CreateEntity: {sw.Elapsed.TotalMilliseconds:F3} ms");

            // Playback
            sw.Restart();
            ecb.Playback(em);
            sw.Stop();
            Console.WriteLine($"  Playback 10000 CreateEntity: {sw.Elapsed.TotalMilliseconds:F3} ms");
            Console.WriteLine($"  总实体数: {em.EntityCount:N0}");

            // AddComponent + RemoveComponent
            ecb = new DeferredCommandBuffer();
            sw.Restart();
            for (int i = 0; i < 10_000; i++)
            {
                ecb.AddComponent(entities[i % entities.Length], new Health{Current=100,Max=100});
            }
            sw.Stop();
            Console.WriteLine($"  记录 10000 AddComponent: {sw.Elapsed.TotalMilliseconds:F3} ms");

            sw.Restart();
            ecb.Playback(em);
            sw.Stop();
            Console.WriteLine($"  Playback 10000 AddComponent: {sw.Elapsed.TotalMilliseconds:F3} ms\n");

            ecb.Dispose();
            _world.Dispose(); _world = null;
        }
    }
}
