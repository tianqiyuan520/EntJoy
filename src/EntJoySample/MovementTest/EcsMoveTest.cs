using EntJoy.Collections;
using EntJoy.Mathematics;
using System.Diagnostics;

namespace EntJoy.MovementTest
{
    /// <summary>
    /// ECS 版本的 100w 实体位移测试
    /// 使用 World → EntityManager 创建实体，通过 IJobChunk 遍历更新
    /// 使用与 MoveEntitiesTest 完全相同的初始数据（seed=42）
    /// 基准测试后验证结果与标量 reference 一致
    /// </summary>
    public class EcsMoveTest : IDisposable
    {
        private const int ENTITY_COUNT = 1_000_000;
        private const float VIEWPORT_WIDTH = 1920f;
        private const float VIEWPORT_HEIGHT = 1080f;
        private const float DT = 0.016f;

        private World _world;
        private bool _disposed;

        // 标量 reference（用于正确性验证）
        private NativeArray<float2> _refPositions;
        private NativeArray<float2> _refVelocities;

        public EcsMoveTest()
        {
            // 先生成与 MoveEntitiesTest 完全一致的初始数据
            _refPositions = new NativeArray<float2>(ENTITY_COUNT, Allocator.Persistent);
            _refVelocities = new NativeArray<float2>(ENTITY_COUNT, Allocator.Persistent);
            MoveEntitiesTest.GenerateInitialData(_refPositions, _refVelocities, ENTITY_COUNT, seed: 42);

            // 创建 World
            _world = new World("MoveTestWorld");
            var em = _world.EntityManager;

            for (int i = 0; i < ENTITY_COUNT; i++)
            {
                var entity = em.NewEntity(typeof(EcsPosition), typeof(EcsVelocity));
                em.Set(entity, new EcsPosition { pos = _refPositions[i] });
                em.Set(entity, new EcsVelocity { vel = _refVelocities[i] });
            }

            Console.WriteLine($"已创建 {ENTITY_COUNT:N0} 个 ECS 实体（初始数据与 MoveEntitiesTest 相同）");
        }

        /// <summary>
        /// 使用 IJobChunk 并行调度
        /// </summary>
        public static void RunJobChunk()
        {
            var query = new QueryBuilder().WithAll<EcsPosition, EcsVelocity>();
            var job = new EcsMoveJobChunk
            {
                Dt = DT,
                ViewportWidth = VIEWPORT_WIDTH,
                ViewportHeight = VIEWPORT_HEIGHT
            };
            JobHandle handle = job.Schedule(query);
            handle.Complete();
        }

        /// <summary>
        /// 手动遍历所有 archetype/chunk（不使用 IJobChunk，作为对照）
        /// </summary>
        public static void RunManualQuery()
        {
            var world = World.DefaultWorld;
            if (world == null) throw new InvalidOperationException("No world!");
            var em = world.EntityManager;

            for (int a = 0; a < em.ArchetypeCount; a++)
            {
                var arch = em.Archetypes[a];
                if (arch == null) continue;
                if (!arch.IsMatch(new QueryBuilder().WithAll<EcsPosition, EcsVelocity>()))
                    continue;

                int posIdx = arch.GetComponentTypeIndex<EcsPosition>();
                int velIdx = arch.GetComponentTypeIndex<EcsVelocity>();
                var chunks = arch.GetChunks();

                for (int c = 0; c < chunks.Count; c++)
                {
                    var chunk = chunks[c];
                    int count = chunk.EntityCount;
                    if (count == 0) continue;

                    unsafe
                    {
                        EcsPosition* posPtr = (EcsPosition*)chunk.GetComponentArrayPointer(posIdx).ToPointer();
                        EcsVelocity* velPtr = (EcsVelocity*)chunk.GetComponentArrayPointer(velIdx).ToPointer();

                        for (int i = 0; i < count; i++)
                        {
                            float2 p = posPtr[i].pos;
                            float2 v = velPtr[i].vel;

                            p.x += v.x * DT;
                            p.y += v.y * DT;

                            if (p.x < 0f || p.x > VIEWPORT_WIDTH) v.x = -v.x;
                            if (p.y < 0f || p.y > VIEWPORT_HEIGHT) v.y = -v.y;

                            posPtr[i] = new EcsPosition { pos = p };
                            velPtr[i] = new EcsVelocity { vel = v };
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 从 ECS World 中读出当前所有 Position 数据（按 chunk 遍历顺序）
        /// 注意：由于实体通过 AddEntity 按创建顺序填入 chunk，
        /// chunk 遍历顺序 = 实体创建顺序 = 标量数组索引顺序，可以直接按索引对比。
        /// </summary>
        public unsafe NativeArray<float2> ReadPositionsFromECS()
        {
            var result = new NativeArray<float2>(ENTITY_COUNT, Allocator.Temp);
            int idx = 0;
            var world = World.DefaultWorld;
            if (world == null) return result;
            var em = world.EntityManager;

            for (int a = 0; a < em.ArchetypeCount; a++)
            {
                var arch = em.Archetypes[a];
                if (arch == null) continue;
                if (!arch.IsMatch(new QueryBuilder().WithAll<EcsPosition, EcsVelocity>()))
                    continue;

                int posIdx = arch.GetComponentTypeIndex<EcsPosition>();
                var chunks = arch.GetChunks();

                for (int c = 0; c < chunks.Count; c++)
                {
                    var chunk = chunks[c];
                    int count = chunk.EntityCount;
                    if (count == 0) continue;

                    EcsPosition* posPtr = (EcsPosition*)chunk.GetComponentArrayPointer(posIdx).ToPointer();
                    for (int i = 0; i < count; i++)
                    {
                        result[idx++] = posPtr[i].pos;
                    }
                }
            }

            return result;
        }

        public unsafe void ResetWorld()
        {
            _world?.Dispose();
            _world = new World("MoveTestWorld");
            var em = _world.EntityManager;
            for (int i = 0; i < ENTITY_COUNT; i++)
            {
                var entity = em.NewEntity(typeof(EcsPosition), typeof(EcsVelocity));
                em.Set(entity, new EcsPosition { pos = _refPositions[i] });
                em.Set(entity, new EcsVelocity { vel = _refVelocities[i] });
            }
        }

        public void RunAll()
        {
            const int WARMUP = 3;
            const int ITERATIONS = 1000;

            Console.WriteLine($"\n=== ECS 实体位移性能测试 ===");
            Console.WriteLine($"实体数: {ENTITY_COUNT:N0}, 迭代: {ITERATIONS}, 预热: {WARMUP}");
            Console.WriteLine();

            // 预热
            Console.WriteLine("预热中...");
            for (int i = 0; i < WARMUP; i++)
            {
                RunManualQuery();
                RunJobChunk();
            }

            Console.WriteLine("开始基准测试...\n");

            // ----- IJobChunk 基准（先跑，因为它的结果用于验证） -----
            ResetWorld();
            double totalChunk = 0;
            for (int i = 0; i < ITERATIONS; i++)
            {
                var sw = Stopwatch.StartNew();
                RunJobChunk();
                sw.Stop();
                totalChunk += sw.Elapsed.TotalMilliseconds;
                if ((i + 1) % 100 == 0) Console.Write(".");
            }
            Console.WriteLine();
            double avgChunk = totalChunk / ITERATIONS;

            // 读取 IJobChunk 后的 ECS 数据作为 "ECS 结果"
            var ecsPositions = ReadPositionsFromECS();

            // ----- Manual Query 基准（重置 ECS 到初始数据） -----
            ResetWorld();
            double totalManual = 0;
            for (int i = 0; i < ITERATIONS; i++)
            {
                var sw = Stopwatch.StartNew();
                RunManualQuery();
                sw.Stop();
                totalManual += sw.Elapsed.TotalMilliseconds;
                if ((i + 1) % 100 == 0) Console.Write(".");
            }
            Console.WriteLine();
            double avgManual = totalManual / ITERATIONS;

            // 读取 Manual Query 后的 ECS 数据
            var manualPositions = ReadPositionsFromECS();

            // ----- 结果 -----
            Console.WriteLine($"\n--- ECS 结果 ---");
            Console.WriteLine($"ECS 手动遍历:        {avgManual,8:F3} ms");
            Console.WriteLine($"ECS IJobChunk:       {avgChunk,8:F3} ms (加速比 {avgManual / avgChunk:F2}x)");

            // ==========================================================
            // 正确性验证 1: ECS (IJobChunk) vs 标量 reference
            // ==========================================================
            // 由于实体通过 AddEntity 按创建 ID 顺序填入 chunk，不存在碎片化问题。
            // chunk 遍历顺序 ≡ 创建顺序 ≡ Entity.Id 递增顺序 ≡ 标量数组索引顺序。
            // 因此可以直接按索引逐元素比较！
            Console.WriteLine($"\n--- 正确性验证 1: ECS(IJobChunk) vs 标量 reference ---");
            Console.WriteLine($"对 {ENTITY_COUNT} 个实体按索引做逐元素比较（chunk 遍历顺序 = 创建顺序）");
            Console.WriteLine($"注意：ECS 已跑完 {ITERATIONS} 次迭代，标量也跑相同的 {ITERATIONS} 次才能对比");

            // 用标量跑同样次数（ITERATIONS 次）—— ECS 基准跑完 1000 次后读取的最终状态
            var scalarPositions = new NativeArray<float2>(ENTITY_COUNT, Allocator.Temp);
            var scalarVelocities = new NativeArray<float2>(ENTITY_COUNT, Allocator.Temp);
            for (int i = 0; i < ENTITY_COUNT; i++)
            {
                scalarPositions[i] = _refPositions[i];
                scalarVelocities[i] = _refVelocities[i];
            }
            // 跑 ITERATIONS 次，与 ECS 基准一致
            for (int i = 0; i < ITERATIONS; i++)
            {
                MoveEntitiesTest.RunScalar(scalarPositions, scalarVelocities, ENTITY_COUNT);
            }

            // ECS 已跑了 ITERATIONS 次 IJobChunk，ecsPositions 中有位移后的位置
            MoveEntitiesTest.VerifyResults(scalarPositions, ecsPositions, "ECS IJobChunk vs 标量");
            scalarPositions.Dispose();
            scalarVelocities.Dispose();

            // ==========================================================
            // 正确性验证 2: IJobChunk vs Manual Query（一致性）
            // ==========================================================
            Console.WriteLine($"\n--- 正确性验证 2: ECS Manual vs IJobChunk ---");
            Console.WriteLine($"两者遍历相同的 chunk，元素顺序一致，共 {ENTITY_COUNT} 个实体");
            MoveEntitiesTest.VerifyResults(manualPositions, ecsPositions, "ECS Manual vs IJobChunk");

            // 清理临时数组
            manualPositions.Dispose();
            ecsPositions.Dispose();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _world?.Dispose();
                if (_refPositions.IsCreated) _refPositions.Dispose();
                if (_refVelocities.IsCreated) _refVelocities.Dispose();
            }
        }
    }

    // ======================== ECS 组件 ========================

    public struct EcsPosition : IComponentData
    {
        public float2 pos;
    }

    public struct EcsVelocity : IComponentData
    {
        public float2 vel;
    }

    // ======================== IJobChunk 实现 ========================

    /// <summary>
    /// IJobChunk 实体位移 Job
    /// 对应 Godot SpritesRandomMove 的 MoveSystem
    /// </summary>
    public struct EcsMoveJobChunk : IJobChunk
    {
        public float Dt;
        public float ViewportWidth;
        public float ViewportHeight;

        public unsafe void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            Span<EcsPosition> positions = chunk.GetComponentDataSpan<EcsPosition>();
            Span<EcsVelocity> velocities = chunk.GetComponentDataSpan<EcsVelocity>();

            for (int i = 0; i < positions.Length; i++)
            {
                float2 p = positions[i].pos;
                float2 v = velocities[i].vel;

                p.x += v.x * Dt;
                p.y += v.y * Dt;

                // 边界反弹
                if (p.x < 0f || p.x > ViewportWidth) v.x = -v.x;
                if (p.y < 0f || p.y > ViewportHeight) v.y = -v.y;

                positions[i] = new EcsPosition { pos = p };
                velocities[i] = new EcsVelocity { vel = v };
            }
        }
    }
}
