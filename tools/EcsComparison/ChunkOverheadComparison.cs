using System;
using BenchmarkDotNet.Attributes;
using EntJoy.ECS;

[MemoryDiagnoser]
public class ChunkOverheadComparison
{
    [Params(1280, 12800, 100000)] public int EntityCount { get; set; }
    private World world;
    private QueryBuilder query;

    [GlobalSetup]
    public void Setup()
    {
        world = new World("chunk-overhead");
        query = new QueryBuilder().WithAll<A, B>();
        for (int i = 0; i < EntityCount; i++)
        {
            var entity = world.EntityManager.NewEntity(typeof(A), typeof(B));
            world.EntityManager.Set(entity, new A { Value = 7 });
        }
        int chunks = 0;
        foreach (var chunk in world.QueryChunks<A, B>()) chunks++;
        Console.WriteLine($"Chunk overhead: {EntityCount} entities, {chunks} chunks");
        Run();
        int count = 0;
        foreach (var item in world.Query<A, B>())
        {
            if (item.Comp0.Value != 7) throw new InvalidOperationException("Empty job changed a component");
            count++;
        }
        if (count != EntityCount) throw new InvalidOperationException("Entity count mismatch");
    }

    [Benchmark]
    public void Run() => new EntEmptyTwoCpp().Run(query, world);

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();
}
