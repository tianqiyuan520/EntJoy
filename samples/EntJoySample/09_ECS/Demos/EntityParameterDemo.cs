using System;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace EntJoySample.ECS
{
    /// <summary>
    /// DOTS 式 Entity 参数示例：IJobEntity 的 Execute 可按值声明 <c>Entity e</c> 参数，
    /// 从 <c>e.Id</c> 拿到全局实体序号（== 创建序 0..N-1，可对齐镜像 SoA 槽位）。
    /// 三后端（托管 / C++ / ISPC）等价；本 demo 把 e.Id 写进 Position.X，回读求和校验。
    /// </summary>

    // 托管（无 [NativeTranspile]）：由 IJobEntitySourceGenerator 生成 IJobChunk 适配器，注入 __chunk.GetEntitySpan()[__idx]
    public struct WriteEntityIdManaged : IJobEntity
    {
        public void Execute(ref Position position, Entity entity) { position.X = entity.Id; }
    }

    // C++ 原生内核：注入局部 __EntJoyEntity entity = ((__EntJoyEntity*)__chunkData->entityArray)[__entity_index]
    [NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct WriteEntityIdCpp : IJobEntity
    {
        public void Execute(ref Position position, Entity entity) { position.X = entity.Id; }
    }

    // ISPC 原生内核：注入 __EntJoyEntity entity = entity_ptr[__entity_index]（impl 内局部 struct __EntJoyEntity）
    [NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct WriteEntityIdIspc : IJobEntity
    {
        public void Execute(ref Position position, Entity entity) { position.X = entity.Id; }
    }

    public static class EntityParameterDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== Entity Parameter Demo (DOTS-style Execute(..., Entity e)) ===\n");
            const int N = 10000;
            long expected = (long)N * (N - 1) / 2;   // 0+1+...+(N-1)

            JobScheduler.Initialize();

            VerifyBackend("managed (C#)", N, expected, q => new WriteEntityIdManaged().Run(q));
            VerifyBackend("C++   (native)", N, expected, q => new WriteEntityIdCpp().Schedule(q).Complete());
            VerifyBackend("ISPC  (native)", N, expected, q => new WriteEntityIdIspc().Schedule(q).Complete());

            Console.WriteLine("\n=== End Entity Parameter Demo ===\n");
        }

        private static void VerifyBackend(string label, int n, long expected, Action<QueryBuilder> schedule)
        {
            using var world = new World($"EntityParameterDemo-{label}");
            World.DefaultWorld = world;
            var em = world.EntityManager;
            for (int i = 0; i < n; i++)
                em.NewEntity(new ComponentType[] { typeof(Position), typeof(Velocity) });

            var query = new QueryBuilder().WithAll<Position, Velocity>();
            schedule(query);

            // 回读校验：每个实体 e.Id 应唯一且覆盖 0..n-1 → X 之和 == 0+1+...+(n-1)
            long sum = 0;
            foreach (var r in world.Query<Position, Velocity>())
                sum += (long)r.Comp0.X;

            bool ok = sum == expected;
            Console.WriteLine($"  {label,-16} sum={sum} (expect {expected}) => {(ok ? "PASS" : "FAIL")}");
        }
    }
}
