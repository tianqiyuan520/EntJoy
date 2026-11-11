using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

public struct DirectChunkQueries
{
    private EntityQuery two, three;
    private ComponentTypeHandle<BenchA> a;
    private ComponentTypeHandle<BenchB> b;
    private ComponentTypeHandle<BenchC> c;

    public void Initialize(ref SystemState state)
    {
        two = state.GetEntityQuery(ComponentType.ReadWrite<BenchA>(), ComponentType.ReadOnly<BenchB>());
        three = state.GetEntityQuery(ComponentType.ReadWrite<BenchA>(), ComponentType.ReadOnly<BenchB>(), ComponentType.ReadOnly<BenchC>());
        a = state.GetComponentTypeHandle<BenchA>(false);
        b = state.GetComponentTypeHandle<BenchB>(true);
        c = state.GetComponentTypeHandle<BenchC>(true);
    }

    public void Execute(ref SystemState state, int components)
    {
        a.Update(ref state);
        b.Update(ref state);
        if (components == 3) c.Update(ref state);
        var query = components == 2 ? two : three;
        using (var chunks = query.ToArchetypeChunkArray(Allocator.TempJob))
        {
            var mask = default(v128);
            if (components == 2)
            {
                var body = new ManagedChunkTwo
                {
                    A = a,
                    B = b
                };
                for (int i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    body.Execute(in chunk, i, false, in mask);
                }
            }
            else
            {
                var body = new ManagedChunkThree
                {
                    A = a,
                    B = b,
                    C = c
                };
                for (int i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    body.Execute(in chunk, i, false, in mask);
                }
            }
        }
    }
}

[DisableAutoCreation]
public partial struct ManagedDirectChunkTraversal : ISystem
{
    private DirectChunkQueries queries;
    public void OnCreate(ref SystemState state) => queries.Initialize(ref state);
    public void OnUpdate(ref SystemState state)
        => queries.Execute(ref state, SystemAPI.GetSingleton<TraversalSettings>().Components);
}

[DisableAutoCreation]
[BurstCompile(CompileSynchronously = true)]
public partial struct BurstDirectChunkTraversal : ISystem
{
    private DirectChunkQueries queries;
    public void OnCreate(ref SystemState state) => queries.Initialize(ref state);

    [BurstCompile(CompileSynchronously = true)]
    public void OnUpdate(ref SystemState state)
        => queries.Execute(ref state, SystemAPI.GetSingleton<TraversalSettings>().Components);
}
