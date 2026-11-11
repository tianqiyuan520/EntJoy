using System.Reflection;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture;

public struct ChunkLoopValue : IComponentData
{
    public int Id, Limit, Count;
    public float Value;
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct ChunkLoopJob : IJobChunk
{
    public int Mode, Iterations;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var values = chunk.GetComponentDataNativeArray<ChunkLoopValue>();
        for (int index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (Mode == 0)
            {
                var delta = value.Limit > 2 ? 0.5f : 0.25f;
                for (int k = 0; k < 3; k++) value.Value += delta;
            }
            else if (Mode == 1)
            {
                for (int k = 0; k < Iterations; k += 2)
                {
                    if ((k & 3) == 0) continue;
                    value.Count += k;
                    if (k > value.Id % 7) break;
                }
            }
            else if (Mode == 2)
            {
                var end = value.Limit;
                for (int k = 0; k < end; k++)
                {
                    if ((k & 1) == 0) continue;
                    value.Value += k + 0.5f;
                    end--;
                    if (k > value.Id % 4) break;
                }
            }
            else if (Mode == 3)
            {
                for (int y = 0; y < 2; y++)
                    for (int x = 1; x <= value.Limit; x += 2)
                    {
                        if (x == 3) continue;
                        value.Count += x + y;
                        if (x > 7) break;
                    }
                { var x = 0.5f; value.Value += x; }
            }
            else if (Mode == 4)
            {
                for (int k = value.Limit; k >= 0; k -= 2)
                {
                    value.Count += k;
                    if (k == 2) break;
                }
            }
            else
            {
                for (int k = 0; k < chunk.Count; k++)
                {
                    if (k == value.Limit) break;
                    value.Count++;
                }
            }
            if (Mode == 2)
            {
                if ((value.Id & 1) == 0) values[index] = value;
            }
            else values[index] = value;
        }
    }
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct ChunkLoopReturnJob : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var values = chunk.GetComponentDataNativeArray<ChunkLoopValue>();
        for (int index = 0; index < values.Length; index++)
        {
            var value = values[index];
            for (int k = 0; k < 3; k++)
            {
                if (value.Id % 11 == 5) return;
                value.Count++;
            }
            values[index] = value;
        }
    }
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct ChunkLoopSnapshotJob : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var values = chunk.GetComponentDataNativeArray<ChunkLoopValue>();
        for (int index = 0; index < values.Length; index++)
        {
            var value = values[index];
            value.Value += 10;
            var snapshot = values[index].Value;
            for (int k = 0; k < 3; k++) value.Count++;
            value.Value = snapshot;
            values[index] = value;
        }
    }
}

public static unsafe class ChunkLoopBindingsFixture
{
    // 取「只收 Chunk」的那个内部构造：ArchetypeChunk 另有一个带 QueryReadState 的重载。
    private static readonly ConstructorInfo ViewConstructor = typeof(ArchetypeChunk)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(c => c.GetParameters().Length == 1);

    public static void Run()
    {
        int comparisons = 0;
        foreach (int length in new[] { 1, 7, 8, 9, 17, 129, 257 })
        {
            using var world = new World("ChunkLoopBindings");
            var types = new ComponentType[] { typeof(ChunkLoopValue) };
            for (int i = 0; i < length; i++) world.EntityManager.NewEntity(types);
            var query = new QueryBuilder().WithAll<ChunkLoopValue>();
            int[] required = { ComponentTypeManager.GetComponentType(typeof(ChunkLoopValue)).Id };
            for (int scenario = 0; scenario < 8; scenario++)
            {
                Fill(world);
                foreach (var archetype in world.EntityManager.GetAllArchetypes())
                foreach (var chunk in archetype.GetChunks())
                {
                    // 真实 Chunk 的托管视图直接调用原始 C# Execute，绕过原生绑定。
                    var view = (ArchetypeChunk)ViewConstructor.Invoke(new object[] { chunk });
                    if (scenario < 6) new ChunkLoopJob { Mode = scenario, Iterations = 12 }.Execute(view, default);
                    else if (scenario == 6) new ChunkLoopReturnJob().Execute(view, default);
                    else new ChunkLoopSnapshotJob().Execute(view, default);
                }
                var expected = Read(world);
                for (int path = 0; path < 4; path++)
                {
                    Fill(world);
                    if (scenario < 6)
                    {
                        var job = new ChunkLoopJob { Mode = scenario, Iterations = 12 };
                        if (path == 0) NativeTranspiler.Bindings.NativeExports.RunImmediate_ChunkLoopJob(ref job, query, world);
                        else if (path == 1) NativeTranspiler.Bindings.NativeExports.Schedule_ChunkLoopJob(ref job, query, default, world).Complete();
                        else RunRaw(ref job, path, world, query, required);
                    }
                    else if (scenario == 6)
                    {
                        var job = new ChunkLoopReturnJob();
                        if (path == 0) NativeTranspiler.Bindings.NativeExports.RunImmediate_ChunkLoopReturnJob(ref job, query, world);
                        else if (path == 1) NativeTranspiler.Bindings.NativeExports.Schedule_ChunkLoopReturnJob(ref job, query, default, world).Complete();
                        else RunRaw(ref job, path, world, query, required);
                    }
                    else
                    {
                        var job = new ChunkLoopSnapshotJob();
                        if (path == 0) NativeTranspiler.Bindings.NativeExports.RunImmediate_ChunkLoopSnapshotJob(ref job, query, world);
                        else if (path == 1) NativeTranspiler.Bindings.NativeExports.Schedule_ChunkLoopSnapshotJob(ref job, query, default, world).Complete();
                        else RunRaw(ref job, path, world, query, required);
                    }
                    var actual = Read(world);
                    for (int i = 0; i < length; i++)
                    {
                        if (actual[i].Count != expected[i].Count || actual[i].Value != expected[i].Value)
                            throw new InvalidOperationException($"Chunk loop N={length}, scenario={scenario}, path={path}, index={i}: {actual[i].Count}/{expected[i].Count}, {actual[i].Value}/{expected[i].Value}");
                        comparisons++;
                    }
                }
            }
        }
        Console.WriteLine($"PASS: Chunk loop bindings: {comparisons} native/managed-source comparisons");
    }

    private static void RunRaw<T>(ref T job, int path, World world, QueryBuilder query, int[] required) where T : struct, IJobChunk
    {
        string entry = $"Get_SharpNative_Job_NativeTranspilerFixture_{typeof(T).Name}_Execute";
        var handle = path == 2
            ? ChunkJobScheduler.ScheduleChunkRawWithWorkerCap(ref job, world.EntityManager, query,
                NativeJobScheduler.GetNativeExportPtr(entry + "_AdapterPtr"), required, 1, world: world)
            : ChunkJobScheduler.ScheduleChunkRangeRaw(ref job, world.EntityManager, query,
                NativeJobScheduler.GetNativeExportPtr(entry + "_RangeAdapterPtr"), required, world: world);
        NativeJobScheduler.Complete(ref handle);
    }

    private static void Fill(World world)
    {
        int id = 0;
        foreach (var archetype in world.EntityManager.GetAllArchetypes())
        foreach (var chunk in archetype.GetChunks())
        {
            var values = (ChunkLoopValue*)chunk.GetComponentArrayPointer(archetype.GetComponentTypeIndex<ChunkLoopValue>());
            for (int i = 0; i < chunk.EntityCount; i++, id++)
                values[i] = new ChunkLoopValue { Id = id, Limit = id % 9, Value = id % 13 * 0.25f };
        }
    }

    private static ChunkLoopValue[] Read(World world)
    {
        var result = new List<ChunkLoopValue>();
        foreach (var archetype in world.EntityManager.GetAllArchetypes())
        foreach (var chunk in archetype.GetChunks())
        {
            var values = (ChunkLoopValue*)chunk.GetComponentArrayPointer(archetype.GetComponentTypeIndex<ChunkLoopValue>());
            for (int i = 0; i < chunk.EntityCount; i++) result.Add(values[i]);
        }
        return result.ToArray();
    }
}
