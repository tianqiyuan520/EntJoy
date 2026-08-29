using System;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace EntJoySample.ECS
{
    /// <summary>blittable Shared Component：值内联存于 chunk 内存块，NativeTranspiler 可读。</summary>
    public struct Material : ISharedComponentData
    {
        public int Id;
        public Material(int id) { Id = id; }
    }

    /// <summary>managed Shared Component：值存 EntityManager 扁平数组，chunk 槽位存索引。NativeTranspiler 不处理。</summary>
    public sealed class MeshAsset : ISharedComponentData
    {
        public string Name;
        public MeshAsset(string name) { Name = name; }
    }

    /// <summary>
    /// Shared Component per-chunk 存储测试案例。
    ///
    /// 演示：
    /// 1. blittable shared 分组——同值进入同一 chunk
    /// 2. managed shared 分组——同值索引相同
    /// 3. SetSharedComponent——单实体就地改值，多实体移动 chunk
    /// 4. EntityBuilder.WithShared 链式 API
    /// 5. QueryBuilder.WithShared 查询过滤（EntityQuery 统一路径）
    /// 6. NativeTranspile IJobChunk 使用 chunk.GetSharedComponent
    /// 7. QuerySelection 流式 API：world.Query&lt;T&gt;().WithShared(...)
    /// 8. SetSharedComponent 变更追踪联动（WithChanged 过滤）
    /// </summary>
    public static class SharedComponentDemo
    {
        private static World _world = null!;

        public static void Run()
        {
            Console.WriteLine("=== Shared Component Demo ===\n");

            _world = new World("SharedDemo");
            var em = _world.EntityManager;

            TestBlittableGrouping(em);
            TestManagedGrouping(em);
            TestSetSharedComponent(em);
            TestEntityBuilderWithShared(em);
            TestQueryFilter(em);
            TestNativeTranspileWithShared(em);
            TestQuerySelectionWithShared(em);
            TestChangeTrackingWithShared(em);

            _world.Dispose();
            Console.WriteLine("\n=== Shared Component Demo Complete ===\n");
        }

        /// <summary>测试 1：blittable shared 分组——同值进入同一 chunk。</summary>
        private static void TestBlittableGrouping(EntityManager em)
        {
            Console.WriteLine("--- Test 1: Blittable Shared Grouping ---");

            var types = new ComponentType[] { typeof(Material), typeof(Position) };

            var e1 = em.NewEntity(types, (typeof(Material), (object)new Material(1)));
            var e2 = em.NewEntity(types, (typeof(Material), (object)new Material(1)));
            var e3 = em.NewEntity(types, (typeof(Material), (object)new Material(2)));

            int chunk1 = em.GetEntityInfoRef(e1.Id).ChunkIndex;
            int chunk2 = em.GetEntityInfoRef(e2.Id).ChunkIndex;
            int chunk3 = em.GetEntityInfoRef(e3.Id).ChunkIndex;

            Console.WriteLine($"  e1 (Material=1): chunk={chunk1}");
            Console.WriteLine($"  e2 (Material=1): chunk={chunk2}");
            Console.WriteLine($"  e3 (Material=2): chunk={chunk3}");

            bool sameChunk = chunk1 == chunk2;
            bool diffChunk = chunk1 != chunk3;
            Console.WriteLine($"  e1/e2 same chunk (same value): {sameChunk}");
            Console.WriteLine($"  e1/e3 diff chunk (diff value): {diffChunk}");
            Console.WriteLine();
        }

        /// <summary>测试 2：managed shared 分组——同值索引相同。</summary>
        private static void TestManagedGrouping(EntityManager em)
        {
            Console.WriteLine("--- Test 2: Managed Shared Grouping ---");

            var types = new ComponentType[] { typeof(MeshAsset), typeof(Position) };

            var e1 = em.NewEntity(types, (typeof(MeshAsset), (object)new MeshAsset("hero.fbx")));
            var e2 = em.NewEntity(types, (typeof(MeshAsset), (object)new MeshAsset("hero.fbx")));
            var e3 = em.NewEntity(types, (typeof(MeshAsset), (object)new MeshAsset("enemy.fbx")));

            Console.WriteLine($"  e1 (hero):     value={em.GetSharedComponent<MeshAsset>(e1).Name}");
            Console.WriteLine($"  e2 (hero):     value={em.GetSharedComponent<MeshAsset>(e2).Name}");
            Console.WriteLine($"  e3 (enemy):    value={em.GetSharedComponent<MeshAsset>(e3).Name}");

            bool sameChunk = em.GetEntityInfoRef(e1.Id).ChunkIndex == em.GetEntityInfoRef(e2.Id).ChunkIndex;
            bool diffChunk = em.GetEntityInfoRef(e1.Id).ChunkIndex != em.GetEntityInfoRef(e3.Id).ChunkIndex;
            Console.WriteLine($"  e1/e2 same chunk: {sameChunk}");
            Console.WriteLine($"  e1/e3 diff chunk: {diffChunk}");
            Console.WriteLine();
        }

        /// <summary>测试 3：SetSharedComponent——单实体就地改值，多实体移动 chunk。</summary>
        private static void TestSetSharedComponent(EntityManager em)
        {
            Console.WriteLine("--- Test 3: SetSharedComponent ---");

            var types = new ComponentType[] { typeof(Material), typeof(Position) };

            // 单实体 chunk：就地改值
            var solo = em.NewEntity(types, (typeof(Material), (object)new Material(10)));
            int chunkBefore = em.GetEntityInfoRef(solo.Id).ChunkIndex;
            em.SetSharedComponent(solo, new Material(20));
            int chunkAfter = em.GetEntityInfoRef(solo.Id).ChunkIndex;
            Console.WriteLine($"  Solo entity: Material 10->20, chunk {chunkBefore}->{chunkAfter} (same={chunkBefore == chunkAfter})");

            // 多实体 chunk：移动到新值 chunk
            var a = em.NewEntity(types, (typeof(Material), (object)new Material(30)));
            var b = em.NewEntity(types, (typeof(Material), (object)new Material(30)));
            int chunkA = em.GetEntityInfoRef(a.Id).ChunkIndex;
            Console.WriteLine($"  a,b both Material=30: chunk={chunkA}");

            em.SetSharedComponent(a, new Material(40));
            int chunkAMoved = em.GetEntityInfoRef(a.Id).ChunkIndex;
            Console.WriteLine($"  After Set(a, 40): a chunk={chunkAMoved} (moved={chunkA != chunkAMoved})");
            Console.WriteLine($"  a new value: {em.GetSharedComponent<Material>(a)}");
            Console.WriteLine($"  b still: {em.GetSharedComponent<Material>(b)}");
            Console.WriteLine();
        }

        /// <summary>测试 4：EntityBuilder.WithShared 链式 API。</summary>
        private static void TestEntityBuilderWithShared(EntityManager em)
        {
            Console.WriteLine("--- Test 4: EntityBuilder.WithShared ---");

            var e = _world.Spawn()
                .With(new Position { X = 100, Y = 200 })
                .With(new Velocity { X = 1, Y = 2 })
                .WithShared(new Material(99))
                .Build();

            var pos = em.GetComponent<Position>(e);
            var vel = em.GetComponent<Velocity>(e);
            var mat = em.GetSharedComponent<Material>(e);

            Console.WriteLine($"  Entity #{e.Id}:");
            Console.WriteLine($"    Position = ({pos.X}, {pos.Y})");
            Console.WriteLine($"    Velocity = ({vel.X}, {vel.Y})");
            Console.WriteLine($"    Material = {mat}");
            Console.WriteLine();
        }

        /// <summary>测试 5：QueryBuilder.WithShared 过滤（走 EntityQuery 统一路径）。</summary>
        private static void TestQueryFilter(EntityManager em)
        {
            Console.WriteLine("--- Test 5: QueryBuilder.WithShared Filter (EntityQuery path) ---");

            // 创建混合值
            var types = new ComponentType[] { typeof(Material), typeof(Position) };
            for (int i = 0; i < 10; i++)
            {
                int matId = i < 5 ? 100 : 200;  // 前 5 个 Material=100，后 5 个 Material=200
                em.NewEntity(types, (typeof(Material), (object)new Material(matId)));
            }

            // WithShared 过滤由 EntityQuery.Refresh 按 chunk 统一判定（与 Job 收集共用 MatchesSharedFilter）
            var query100 = _world.GetOrCreateEntityQuery(
                new QueryBuilder().WithAll<Position, Material>().WithShared(new Material(100)));
            var query200 = _world.GetOrCreateEntityQuery(
                new QueryBuilder().WithAll<Position, Material>().WithShared(new Material(200)));

            int count100 = query100.CalculateEntityCount();
            int count200 = query200.CalculateEntityCount();
            Console.WriteLine($"  Created 10 entities (5xMaterial=100, 5xMaterial=200)");
            Console.WriteLine($"  WithShared(Material=100) matched: {count100} entities");
            Console.WriteLine($"  WithShared(Material=200) matched: {count200} entities");
            bool ok = count100 == 5 && count200 == 5;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }

        // ======================== NativeTranspile IJobChunk 测试 ========================

        /// <summary>
        /// [NativeTranspile] IJobChunk：在 Execute 中调用 chunk.GetSharedComponent&lt;Material&gt;()。
        /// 验证 CppChunkStatementTranslator 能正确翻译为 C++ 单值指针解引用。
        ///
        /// 语义：每个实体的 Position.X += Material.Id * DeltaTime。
        /// </summary>
        [NativeTranspile(Target = BackendTarget.Cpp)]
        public struct SharedScaleJob : IJobChunk
        {
            public float DeltaTime;

            public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
            {
                // chunk.GetSharedComponent<T>() 将被翻译为：
                //   reinterpret_cast<Material*>(__chunkData->sharedValuePtrs[0])
                var material = chunk.GetSharedComponent<Material>();

                EntJoy.Collections.NativeArray<Position> positions = chunk.GetComponentDataNativeArray<Position>();
                for (int i = 0; i < positions.Length; i++)
                {
                    var p = positions[i];
                    p.X += material.Id * DeltaTime;
                    positions[i] = p;
                }
            }
        }

        /// <summary>测试 6：NativeTranspile IJobChunk 使用 chunk.GetSharedComponent。</summary>
        private static void TestNativeTranspileWithShared(EntityManager em)
        {
            Console.WriteLine("--- Test 6: NativeTranspile IJobChunk with SharedComponent ---");

            // 创建两种 Material 值的实体
            var types = new ComponentType[] { typeof(Material), typeof(Position) };
            var entities = new Entity[10];
            for (int i = 0; i < 10; i++)
            {
                int matId = i < 5 ? 2 : 3;  // 前 5 个 Material=2，后 5 个 Material=3
                entities[i] = em.NewEntity(types, (typeof(Material), (object)new Material(matId)));
                em.Set(entities[i], new Position { X = 0, Y = 0 });
            }

            var query = new QueryBuilder().WithAll<Position, Material>();
            var job = new SharedScaleJob { DeltaTime = 10f };

            // Run（ImmediateNative 直执路径）
            job.Run(query);

            // 验证：Material=2 的实体 X 应为 20，Material=3 的应为 30
            int mat2Count = 0, mat3Count = 0;
            long sumX = 0;
            foreach (var e in entities)
            {
                var mat = em.GetSharedComponent<Material>(e);
                var pos = em.GetComponent<Position>(e);
                sumX += (long)pos.X;
                if (mat.Id == 2) mat2Count++;
                else if (mat.Id == 3) mat3Count++;
            }
            Console.WriteLine($"  Material=2 entities: {mat2Count}, Material=3 entities: {mat3Count}");
            Console.WriteLine($"  SumX = {sumX} (expected: {mat2Count}*20 + {mat3Count}*30 = {mat2Count * 20 + mat3Count * 30})");
            bool ok = sumX == mat2Count * 20 + mat3Count * 30;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 7：QuerySelection 流式 API——world.Query&lt;T&gt;().WithShared(...)。
        /// 单组件链以 QueryEnumerable 终结；双组件链（QuerySelection&lt;T0,T1&gt;）返回自身可继续链式。
        /// 本世界含无 Material 列的 archetype（Test 2 的 [MeshAsset, Position]）——
        /// 顺带验证 IsMatch 共享列跳过逻辑：不含共享列的 archetype 必须跳过而非字典 miss。
        /// </summary>
        private static void TestQuerySelectionWithShared(EntityManager em)
        {
            Console.WriteLine("--- Test 7: QuerySelection.WithShared (fluent) ---");

            var types = new ComponentType[] { typeof(Material), typeof(Position), typeof(Velocity) };
            for (int i = 0; i < 4; i++)
                em.NewEntity(types, (typeof(Material), (object)new Material(7)));
            for (int i = 0; i < 2; i++)
                em.NewEntity(types, (typeof(Material), (object)new Material(8)));

            // 单组件链：world.Query<T0>().WithShared<TShared>() → QueryEnumerable<T0, TShared>
            int count7 = 0;
            foreach (var _ in _world.Query<Position>().WithShared(new Material(7)))
                count7++;

            // 双组件链：world.Query<T0, T1>().WithShared<TShared>() → 返回自身（可继续 WithEnabled 等）
            int count8 = 0;
            foreach (var _ in _world.Query<Position, Velocity>().WithShared(new Material(8)))
                count8++;

            Console.WriteLine($"  Query<Position>().WithShared(Material=7): {count7} entities (expected 4)");
            Console.WriteLine($"  Query<Position, Velocity>().WithShared(Material=8): {count8} entities (expected 2)");
            bool ok = count7 == 4 && count8 == 2;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 8：SetSharedComponent 与 Change Tracking 联动：
        /// 就地改值 / 移动 chunk 后实体被标记变更，WithChanged 查询可见；帧末 ClearAllChangedBitMasks 归零。
        /// 注意：WithChanged 是 chunk 级过滤——chunk 内任一实体被标记即计入整个 chunk。
        /// 用独立 Material 值保证目标 chunk 实体数可预测（就地=1，移动=新建 1 实体 chunk）。
        /// </summary>
        private static void TestChangeTrackingWithShared(EntityManager em)
        {
            Console.WriteLine("--- Test 8: SetSharedComponent + WithChanged ---");

            var types = new ComponentType[] { typeof(Material), typeof(Position) };

            // 用未出现过的值创建 → 单实体 chunk → 走就地改值路径
            var solo = em.NewEntity(types, (typeof(Material), (object)new Material(101)));

            var query = _world.GetOrCreateEntityQuery(
                new QueryBuilder().WithAll<Position, Material>().WithChanged<Position>());

            // 创建不标记变更；帧末清零后应为 0
            em.ClearAllChangedBitMasks();
            int afterClear0 = query.CalculateEntityCount();
            Console.WriteLine($"  After Clear: {afterClear0} (expected 0)");

            // 单实体 chunk 就地改值 → MarkEntityChanged → 查询可见（该 chunk 仅 1 实体 → 计数 1）
            em.SetSharedComponent(solo, new Material(102));
            int afterSet = query.CalculateEntityCount();
            Console.WriteLine($"  After SetSharedComponent (in-place): {afterSet} (expected 1)");

            // 帧末清零 → 归零
            em.ClearAllChangedBitMasks();
            int afterClear1 = query.CalculateEntityCount();
            Console.WriteLine($"  After Clear again: {afterClear1} (expected 0)");

            // 多实体 chunk → SetSharedComponent 移动实体到新值 chunk → 新位置标记变更
            var a = em.NewEntity(types, (typeof(Material), (object)new Material(103)));
            em.NewEntity(types, (typeof(Material), (object)new Material(103)));  // 同 chunk 第二实体
            em.ClearAllChangedBitMasks();
            em.SetSharedComponent(a, new Material(104));  // 目标 chunk 为新建（仅 a）→ 计数 1
            int afterMove = query.CalculateEntityCount();
            Console.WriteLine($"  After SetSharedComponent (move): {afterMove} (expected 1)");

            bool ok = afterClear0 == 0 && afterSet == 1 && afterClear1 == 0 && afterMove == 1;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }
    }
}
